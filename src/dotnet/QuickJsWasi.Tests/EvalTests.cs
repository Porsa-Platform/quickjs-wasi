using Xunit;

namespace QuickJsWasi.Tests;

public sealed class EvalTests : TestBase
{
    [Fact]
    public async Task EvaluatesBasicExpression()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var result = vm.Eval("1 + 2 + 3");
        Assert.Equal(6d, result.ToNumber());
    }

    [Fact]
    public async Task ExecutesPromiseJobs()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = vm.Eval("Promise.resolve(21).then(x => x * 2)");
        vm.ExecutePendingJobs();
        Assert.Equal(1, promise.PromiseState);
        using var value = vm.GetPromiseResult(promise);
        Assert.Equal(42d, value.ToNumber());
    }
}
