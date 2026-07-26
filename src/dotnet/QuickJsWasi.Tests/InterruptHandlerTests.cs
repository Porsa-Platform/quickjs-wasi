using Xunit;

namespace QuickJsWasi.Tests;

public sealed class InterruptHandlerTests : TestBase
{
    [Fact]
    public async Task InterruptHandlerStopsExecution()
    {
        if (!HasWasm) return;
        var ticks = 0;
        using var vm = await CreateVmAsync(options => options.InterruptHandler = () => ++ticks > 10_000);

        Assert.Throws<JSException>(() =>
        {
            using var _ = vm.Eval("while (true) {} ");
        });
    }
}
