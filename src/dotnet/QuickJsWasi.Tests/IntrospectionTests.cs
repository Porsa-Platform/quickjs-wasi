using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// Tests covering value introspection APIs: <see cref="QuickJs.Construct"/>,
/// <see cref="JSValueHandle.Identity"/>, and <see cref="JSValueHandle.ToBoolean"/>.
/// </summary>
public sealed class IntrospectionTests : TestBase
{
    // ====================================================================
    // Construct
    // ====================================================================

    [Fact]
    public async Task Construct_CreatesInstance()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var dateCtor = vm.Eval("Date");
        using var isoStr = vm.NewString("2024-01-15T00:00:00.000Z");
        using var instance = vm.Construct(dateCtor, isoStr);
        Assert.True(instance.IsDate);
    }

    [Fact]
    public async Task Construct_ThrowsWhenNotAConstructor()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var fn = vm.Eval("() => {}");
        Assert.Throws<JSException>(() => vm.Construct(fn));
    }

    [Fact]
    public async Task Construct_ThrowsWhenConstructorThrows()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var badCtor = vm.Eval("class Bad { constructor() { throw new Error('oops'); } }; Bad");
        Assert.Throws<JSException>(() => vm.Construct(badCtor));
    }

    // ====================================================================
    // JSValueHandle.Identity
    // ====================================================================

    [Fact]
    public async Task Identity_SameObjectSameIdentity()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.NewObject();
        using var dup = obj.Dup();
        Assert.Equal(obj.Identity, dup.Identity);
    }

    [Fact]
    public async Task Identity_DifferentObjectsDifferentIdentity()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var a = vm.NewObject();
        using var b = vm.NewObject();
        Assert.NotEqual(a.Identity, b.Identity);
    }

    [Fact]
    public async Task Identity_NonHeapValuesReturnZero()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        Assert.Equal(0, vm.UndefinedValue.Identity);
        Assert.Equal(0, vm.NullValue.Identity);
        Assert.Equal(0, vm.TrueValue.Identity);
        Assert.Equal(0, vm.FalseValue.Identity);
    }

    // ====================================================================
    // JSValueHandle.ToBoolean
    // ====================================================================

    [Fact]
    public async Task ToBoolean_TruthyValues()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var one = vm.NewNumber(1);
        using var str = vm.NewString("hello");
        using var obj = vm.NewObject();
        Assert.True(one.ToBoolean());
        Assert.True(str.ToBoolean());
        Assert.True(obj.ToBoolean());
        Assert.True(vm.TrueValue.ToBoolean());
    }

    [Fact]
    public async Task ToBoolean_FalsyValues()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var zero = vm.NewNumber(0);
        using var emptyStr = vm.NewString("");
        Assert.False(zero.ToBoolean());
        Assert.False(emptyStr.ToBoolean());
        Assert.False(vm.FalseValue.ToBoolean());
        Assert.False(vm.NullValue.ToBoolean());
        Assert.False(vm.UndefinedValue.ToBoolean());
    }

    [Fact]
    public async Task ToBool_DelegatesToToBoolean()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var val = vm.NewNumber(42);
        Assert.Equal(val.ToBool(), val.ToBoolean());
    }
}
