using Xunit;

namespace QuickJsWasi.Tests;

public sealed class MemoryLimitTests : TestBase
{
    [Fact]
    public async Task EnforcesConfiguredMemoryLimit()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync(options => options.MemoryLimit = 4_000_000L);

        Assert.Throws<JSException>(() =>
        {
            using var _ = vm.Eval("new Uint8Array(16 * 1024 * 1024)");
        });
    }
}
