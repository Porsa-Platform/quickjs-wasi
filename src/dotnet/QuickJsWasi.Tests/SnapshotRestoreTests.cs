using Xunit;

namespace QuickJsWasi.Tests;

public sealed class SnapshotRestoreTests : TestBase
{
    [Fact]
    public async Task RestoresSnapshotState()
    {
        if (!HasWasm) return;
        Snapshot snapshot;
        using (var vm = await CreateVmAsync())
        {
            vm.Eval("globalThis.counter = 41").Dispose();
            snapshot = vm.Snapshot();
        }

        using var restored = await QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = WasmBytes });
        using var temp = restored.Eval("globalThis.counter = 0");

        using var vm2 = await QuickJs.RestoreAsync(snapshot, new QuickJsOptions { WasmBytes = WasmBytes });
        using var result = vm2.Eval("globalThis.counter + 1");
        Assert.Equal(42, result.ToNumber());
    }
}
