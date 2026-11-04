using QuickJsWasi;
using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// The QuickJS module is compiled once per distinct binary and shared by every VM: compiling it per VM made each
/// create/restore cost ~150 ms, and serialized them. A VM owns only its store, so disposing it leaves the shared module
/// to the others.
/// </summary>
public sealed class CompiledModuleTests : TestBase
{
    [Fact]
    public async Task Vms_of_the_same_binary_share_one_compiled_module()
    {
        if (!HasWasm) return;

        using (await CreateVmAsync())
        {
        }

        var compiled = CompiledModules.Count;
        var copy = (byte[])WasmBytes!.Clone();
        using var first = await CreateVmAsync();
        using var second = await QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = copy });

        Assert.Equal(compiled, CompiledModules.Count);
        Assert.Same(CompiledModules.For(WasmBytes), CompiledModules.For(copy));
    }

    [Fact]
    public async Task Disposing_a_vm_leaves_the_others_and_the_next_ones_working()
    {
        if (!HasWasm) return;

        using var kept = await CreateVmAsync();
        kept.Eval("globalThis.kept = 41").Dispose();
        (await CreateVmAsync()).Dispose();

        using var next = await CreateVmAsync();
        using var fromKept = kept.Eval("kept + 1");
        using var fromNext = next.Eval("6 * 7");

        Assert.Equal(42.0, kept.Dump(fromKept));
        Assert.Equal(42.0, next.Dump(fromNext));
    }

    [Fact]
    public async Task Restores_side_by_side_each_get_their_own_vm()
    {
        if (!HasWasm) return;

        Snapshot snapshot;
        using (var source = await CreateVmAsync())
        {
            source.Eval("globalThis.count = 0").Dispose();
            snapshot = source.Snapshot();
        }

        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var vm = await QuickJs.RestoreAsync(snapshot, new QuickJsOptions { WasmBytes = WasmBytes });
            using var result = vm.Eval("++count");
            return vm.Dump(result);
        })));

        Assert.All(counts, count => Assert.Equal(1.0, count));
    }
}
