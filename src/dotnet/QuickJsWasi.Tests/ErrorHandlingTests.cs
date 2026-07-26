using Xunit;

namespace QuickJsWasi.Tests;

public sealed class ErrorHandlingTests : TestBase
{
    [Fact]
    public async Task ThrowsManagedJsException()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var ex = Assert.Throws<JSException>(() =>
        {
            using var _ = vm.Eval("throw new Error('boom')");
        });
        Assert.Contains("boom", ex.Message);
    }
}
