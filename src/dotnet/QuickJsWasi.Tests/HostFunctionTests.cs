using Xunit;

namespace QuickJsWasi.Tests;

public sealed class HostFunctionTests : TestBase
{
    [Fact]
    public async Task InvokesHostFunction()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var fn = vm.NewHostFunction("add", (_, args) =>
        {
            using var sum = vm.NewNumber(args[0].ToNumber() + args[1].ToNumber());
            return sum.Dup();
        });
        vm.Global.SetProp("add", fn);
        using var result = vm.Eval("add(20, 22)");
        Assert.Equal(42, result.ToNumber());
    }
}
