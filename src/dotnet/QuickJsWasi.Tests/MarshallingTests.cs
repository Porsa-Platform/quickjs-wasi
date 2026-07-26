using Xunit;

namespace QuickJsWasi.Tests;

public sealed class MarshallingTests : TestBase
{
    [Fact]
    public async Task MarshalsHostObjectsAndArrays()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var handle = vm.HostToHandle(new Dictionary<string, object?>
        {
            ["answer"] = 42,
            ["items"] = new object?[] { "a", true, null }
        });

        var dumped = Assert.IsType<Dictionary<string, object?>>(vm.Dump(handle));
        Assert.Equal(42d, dumped["answer"]);
        var items = Assert.IsType<object?[]>(dumped["items"]);
        Assert.Equal("a", items[0]);
        Assert.Equal(true, items[1]);
    }
}
