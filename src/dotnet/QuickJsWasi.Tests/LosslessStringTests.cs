#pragma warning disable xUnit1026
using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// Tests for lossless string transport across the WASM boundary.
///
/// Guest→host previously read strings through NUL-terminated <c>JS_ToCString</c>:
/// embedded U+0000 truncated the string, and lone surrogates were replaced with U+FFFD.
/// Host→guest previously encoded with standard UTF-8, which replaces lone surrogates
/// with U+FFFD before the guest ever sees them.
///
/// Now: guest→host reads length-aware WTF-8 (<c>qjs_get_string_len</c>), and
/// host→guest writes WTF-8 (<c>EncodeWtf8</c>), so every JS string round-trips exactly.
/// Keys with NULs or lone surrogates are routed through length-aware guest string values.
/// </summary>
public sealed class LosslessStringTests : TestBase
{
    // Every code unit is escaped as \uXXXX so the guest receives the exact code units
    // regardless of how source code is transported (avoids the "both-sides-corrupted" trap).
    private static string GuestLiteral(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        for (var i = 0; i < s.Length; i++)
            sb.Append($"\\u{(int)s[i]:x4}");
        sb.Append('"');
        return sb.ToString();
    }

    public static IEnumerable<object[]> Cases => new[]
    {
        new object[] { "embedded NUL",                 "ab\u0000cd" },
        new object[] { "leading NUL",                  "\u0000x" },
        new object[] { "trailing NUL",                 "x\u0000" },
        new object[] { "bare lone high surrogate",     "\ud800" },
        new object[] { "bare lone low surrogate",      "\udfff" },
        new object[] { "length-canceling surrogate + NUL", "\ud800\u0000a" },
        new object[] { "legit replacement char",       "already\ufffdhere" },
        new object[] { "well-formed pair (emoji)",     "emoji \U0001F600 pair" },
        new object[] { "mixed",                        "a\u0000\ud800\U0001F600\udc00z" },
    };

    // ====================================================================
    // guest→host: ToManagedString / Length are lossless
    // ====================================================================

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GuestToHost_ToString_IsLossless(string _name, string value)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var handle = vm.Eval(GuestLiteral(value));
        Assert.Equal(value, handle.ToManagedString());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GuestToHost_Length_MatchesUtf16CodeUnitCount(string _name, string value)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var handle = vm.Eval(GuestLiteral(value));
        Assert.Equal(value.Length, handle.Length);
    }

    // ====================================================================
    // host→guest: NewString / EvalCode sources are lossless
    // ====================================================================

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HostToGuest_NewString_RoundTrips(string _name, string value)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var handle = vm.NewString(value);
        // Set as global and compare with a literal the guest constructs itself.
        vm.Global.SetProp("probe", handle);
        using var match = vm.Eval($"probe === {GuestLiteral(value)}");
        Assert.True(match.ToBoolean());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HostToGuest_NewString_ReadBackIsLossless(string _name, string value)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var handle = vm.NewString(value);
        Assert.Equal(value, handle.ToManagedString());
    }

    // ====================================================================
    // Property keys with NULs / lone surrogates
    // ====================================================================

    private static string KeyObj =>
        $"({{ {GuestLiteral("a\u0000b")}: 1, {GuestLiteral("\ud800k")}: 2, {GuestLiteral("mix\ud800\u0000ed")}: 3, plain: 4 }})";

    [Fact]
    public async Task PropertyKeys_Enumeration_ReturnsExactKeys()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.Eval(KeyObj);

        var keys = obj.Keys();
        Assert.Equal(new[] { "a\u0000b", "\ud800k", "mix\ud800\u0000ed", "plain" }, keys);

        var allNames = obj.GetOwnPropertyNames();
        Assert.Equal(new[] { "a\u0000b", "\ud800k", "mix\ud800\u0000ed", "plain" }, allNames);
    }

    [Fact]
    public async Task PropertyKeys_GetPropSetProp_RoundTrip()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.Eval(KeyObj);

        using var v1 = obj.GetProp("a\u0000b");
        Assert.Equal(1, (int)v1.ToNumber());

        using var v2 = obj.GetProp("\ud800k");
        Assert.Equal(2, (int)v2.ToNumber());

        using var v3 = obj.GetProp("mix\ud800\u0000ed");
        Assert.Equal(3, (int)v3.ToNumber());

        // Set a new mangled key and read it back.
        using var nine = vm.NewNumber(9);
        obj.SetProp("new\u0000\ud800key", nine);
        using var read = obj.GetProp("new\u0000\ud800key");
        Assert.Equal(9, (int)read.ToNumber());
    }

    [Fact]
    public async Task PropertyKeys_HasOwnPropertyAndEnumerable()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.Eval(KeyObj);

        Assert.True(obj.HasOwnProperty("a\u0000b"));
        Assert.True(obj.HasOwnProperty("\ud800k"));
        Assert.False(obj.HasOwnProperty("a")); // NOT the NUL-truncated form
        Assert.True(obj.PropertyIsEnumerable("\ud800k"));
    }

    [Fact]
    public async Task PropertyKeys_DefineProp_MangledKey()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var target = vm.NewObject();
        using var v = vm.NewNumber(5);
        target.DefineProp("def\u0000\ud800", v, enumerable: true);

        using var read = target.GetProp("def\u0000\ud800");
        Assert.Equal(5, (int)read.ToNumber());
        Assert.Equal(new[] { "def\u0000\ud800" }, target.Keys());
    }

    [Fact]
    public async Task PropertyKeys_GetOwnPropertyDescriptor_MangledKey()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.Eval(KeyObj);
        var desc = obj.GetOwnPropertyDescriptor("mix\ud800\u0000ed");
        Assert.NotNull(desc);
        Assert.NotNull(desc!.Value);
        Assert.Equal(3, (int)desc.Value!.ToNumber());
        desc.Value?.Dispose();
        desc.Get?.Dispose();
        desc.Set?.Dispose();
    }
}
