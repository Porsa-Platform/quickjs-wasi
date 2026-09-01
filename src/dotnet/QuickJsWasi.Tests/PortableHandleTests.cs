using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// Tests for snapshot-portable handles: <see cref="QuickJs.ExportHandle"/> turns a live
/// handle into an integer token, and <see cref="QuickJs.ImportHandle"/> re-materializes it,
/// on the same VM or on any VM restored from a snapshot taken while the handle was alive.
/// </summary>
public sealed class PortableHandleTests : TestBase
{
    // ====================================================================
    // Basic export / import
    // ====================================================================

    [Fact]
    public async Task ExportImport_RematerializesValueInRestoredVm()
    {
        if (!HasWasm) return;

        var pristineToISOString = default(JSValueHandle);
        using var baseline = await CreateVmAsync();

        // Capture a pristine intrinsic BEFORE "user code" patches it.
        pristineToISOString = baseline.Eval("Date.prototype.toISOString");
        var token = baseline.ExportHandle(pristineToISOString);

        // User code replaces the intrinsic.
        baseline.Eval("Date.prototype.toISOString = function () { return \"patched\"; }").Dispose();

        var snapshot = baseline.Snapshot();
        baseline.Dispose();

        using var restored = await QuickJs.RestoreAsync(snapshot, new QuickJsOptions { WasmBytes = WasmBytes });
        using var imported = restored.ImportHandle(token);

        // The imported handle is the PRISTINE function, not the patch.
        using var date = restored.Eval("new Date(1700000000000)");
        using var iso = restored.CallFunction(imported, date);
        Assert.Equal("2023-11-14T22:13:20.000Z", iso.ToManagedString());

        // The patch is still what guest code observes.
        using var patched = restored.Eval("new Date(0).toISOString()");
        Assert.Equal("patched", patched.ToManagedString());

        pristineToISOString?.Dispose();
    }

    [Fact]
    public async Task ImportHandle_AreIndependentlyOwned()
    {
        if (!HasWasm) return;

        using var baseline = await CreateVmAsync();
        using var obj = baseline.Eval("({ tag: \"kept\" })");
        var token = baseline.ExportHandle(obj);
        var snapshot = baseline.Snapshot();
        baseline.Dispose();

        using var restored = await QuickJs.RestoreAsync(snapshot, new QuickJsOptions { WasmBytes = WasmBytes });
        var first = restored.ImportHandle(token);
        var second = restored.ImportHandle(token);

        using var tag1 = first.GetProp("tag");
        Assert.Equal("kept", tag1.ToManagedString());
        first.Dispose();

        // Disposing one import must not free the value out from under others.
        using var tag2 = second.GetProp("tag");
        Assert.Equal("kept", tag2.ToManagedString());
        second.Dispose();

        // The exported box's own reference keeps the value alive.
        using var third = restored.ImportHandle(token);
        using var tag3 = third.GetProp("tag");
        Assert.Equal("kept", tag3.ToManagedString());
    }

    [Fact]
    public async Task ExportImport_RoundTripOnSameVm()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var original = vm.Eval("({ n: 7 })");
        var token = vm.ExportHandle(original);
        using var imported = vm.ImportHandle(token);

        using var n = imported.GetProp("n");
        Assert.Equal(7, (int)n.ToNumber());

        // Same underlying guest object.
        Assert.Equal(original.Identity, imported.Identity);
    }

    // ====================================================================
    // Rejected inputs
    // ====================================================================

    [Fact]
    public async Task ExportHandle_RejectsBorrowedHandles()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        InvalidOperationException? thrown = null;
        int? dupToken = null;
        // Kept alive outside the lambda so the exported token stays valid
        // until ImportHandle is called below.
        JSValueHandle? ownedForExport = null;

        using var fn = vm.NewEphemeralFunction((_, args) =>
        {
            // Borrowed handle (trampoline arg) should be rejected.
            try
            {
                vm.ExportHandle(args[0]);
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }

            // The documented escape hatch: dup() gives an owned handle.
            // Do NOT use `using` here — the handle must outlive the callback
            // so the token remains valid when ImportHandle is called.
            ownedForExport = args[0].Dup();
            dupToken = vm.ExportHandle(ownedForExport);
            return vm.UndefinedValue;
        });

        vm.Global.SetProp("cb", fn);
        vm.Eval("cb({ n: 3 })").Dispose();

        Assert.NotNull(thrown);
        Assert.Contains("borrowed", thrown!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(dupToken);

        using var imported = vm.ImportHandle(dupToken!.Value);
        using var n = imported.GetProp("n");
        Assert.Equal(3, (int)n.ToNumber());

        ownedForExport?.Dispose();
    }

    [Fact]
    public async Task ImportHandle_RejectsMalformedTokens()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        foreach (var bad in new[] { 0, -1 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => vm.ImportHandle(bad));
        }
    }

    [Fact]
    public async Task ExportHandle_RejectsHandlesFromDifferentVmAndDisposedHandles()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var other = await CreateVmAsync();
        using var foreign = other.Eval("({})");

        var ex = Assert.Throws<InvalidOperationException>(() => vm.ExportHandle(foreign));
        Assert.Contains("different VM", ex.Message, StringComparison.OrdinalIgnoreCase);

        var gone = vm.Eval("({})");
        gone.Dispose();
        var ex2 = Assert.Throws<InvalidOperationException>(() => vm.ExportHandle(gone));
        Assert.Contains("disposed", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // Snapshot serialization preserves tokens
    // ====================================================================

    [Fact]
    public async Task SerializedSnapshot_PreservesTokenValidity()
    {
        if (!HasWasm) return;

        using var baseline = await CreateVmAsync();
        using var value = baseline.Eval("\"portable\\u0000value\"");
        var token = baseline.ExportHandle(value);

        var bytes = Snapshot.Serialize(baseline.Snapshot());
        baseline.Dispose();

        using var restored = await QuickJs.RestoreAsync(
            Snapshot.Deserialize(bytes),
            new QuickJsOptions { WasmBytes = WasmBytes });

        using var imported = restored.ImportHandle(token);
        // 'portable\0value' has 15 UTF-16 code units.
        Assert.Equal("portable\u0000value".Length, imported.Length);
    }
}
