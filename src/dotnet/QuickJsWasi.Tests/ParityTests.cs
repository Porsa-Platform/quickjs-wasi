using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// Behaviour the TypeScript host defines and the .NET host previously diverged from
/// (see <c>src/dotnet/PARITY.md</c>). Ported from <c>test/*.test.ts</c> where a TS test exists.
/// </summary>
public sealed class ParityTests : TestBase
{
    // ── Promises (test/promises.test.ts, test/promise-rejection.test.ts) ──────────────

    [Fact]
    public async Task ResolvePromise_CannotBeInterceptedByPatchingThenOrSpecies()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        vm.Eval("""
            globalThis.thenCalls = 0;
            const realThen = Promise.prototype.then;
            Promise.prototype.then = function (...args) {
              globalThis.thenCalls++;
              return realThen.apply(this, args);
            };
            Object.defineProperty(Promise, Symbol.species, {
              get() { globalThis.thenCalls += 1000; return Promise; }
            });
            """).Dispose();

        using var pending = vm.Eval("""
            globalThis.trigger = null;
            new Promise((resolve) => { globalThis.trigger = resolve; })
            """);
        var resultTask = vm.ResolvePromise(pending);
        vm.Eval("trigger('untouched')").Dispose();
        vm.ExecutePendingJobs();

        var fulfilled = Assert.IsType<JSPromiseResult.Fulfilled>(await resultTask);
        using (fulfilled.Value) Assert.Equal("untouched", fulfilled.Value.ToManagedString());
        using var calls = vm.Eval("globalThis.thenCalls");
        Assert.Equal(0d, calls.ToNumber());
    }

    [Fact]
    public async Task MarkPromiseHandled_SuppressesUnhandledRejectionCallback()
    {
        if (!HasWasm) return;
        var rejections = new List<string>();
        using var vm = await CreateVmAsync(o => o.OnUnhandledRejection = (_, reason, isHandled) =>
        {
            if (!isHandled) rejections.Add(reason.ToManagedString());
        });

        using var promise = vm.Eval("""
            globalThis.trigger = null;
            new Promise((_, reject) => { globalThis.trigger = reject; })
            """);
        vm.MarkPromiseHandled(promise);
        vm.Eval("trigger('suppressed')").Dispose();
        vm.ExecutePendingJobs();
        Assert.DoesNotContain("suppressed", rejections);

        // An unmarked rejection still fires, proving the tracker is active.
        vm.Eval("Promise.reject('reported')").Dispose();
        vm.ExecutePendingJobs();
        Assert.Contains("reported", rejections);
    }

    [Fact]
    public async Task MarkPromiseHandled_IsNoOpForNonPromises()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var notAPromise = vm.Eval("({})");
        vm.MarkPromiseHandled(notAPromise);
    }

    [Fact]
    public async Task DeferredSettled_CompletesWhenGuestPromiseSettles_NotWhenResolveIsCalled()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var outer = vm.NewPromise();
        var inner = vm.NewPromise();
        var settled = outer.Settled;

        outer.Resolve(inner.Handle); // adopts a still-pending promise
        vm.ExecutePendingJobs();
        Assert.False(settled.IsCompleted);

        inner.Resolve(vm.TrueValue);
        vm.ExecutePendingJobs();
        Assert.True(settled.IsCompleted);

        inner.Handle.Dispose();
        outer.Handle.Dispose();
    }

    // ── Exception checks (test/introspection.test.ts, test/handle-introspection.test.ts) ──

    [Fact]
    public async Task GetProxyTargetAndHandler_ThrowJSExceptionForNonProxy()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var plain = vm.Eval("({})");
        Assert.Throws<JSException>(() => plain.GetProxyTarget());
        Assert.Throws<JSException>(() => plain.GetProxyHandler());

        using var proxy = vm.Eval("new Proxy({ marker: 1 }, { tag: 'handler' })");
        using var target = proxy.GetProxyTarget();
        using var handler = proxy.GetProxyHandler();
        using (var marker = target.GetProp("marker")) Assert.Equal(1d, marker.ToNumber());
        using (var tag = handler.GetProp("tag")) Assert.Equal("handler", tag.ToManagedString());
    }

    [Fact]
    public async Task KeysAndDump_ReturnEmptyWhenOwnKeysTrapThrows()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var proxy = vm.Eval("new Proxy({ a: 1 }, { ownKeys() { throw new Error('nope'); } })");

        Assert.Empty(proxy.Keys());
        vm.GetException().Dispose();

        var dumped = Assert.IsType<Dictionary<string, object?>>(vm.Dump(proxy));
        Assert.Empty(dumped);
        vm.GetException().Dispose();
    }

    [Fact]
    public async Task HasOwnPropertyAndPropertyIsEnumerable_ReturnFalseOnError()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var proxy = vm.Eval("new Proxy({ a: 1 }, { getOwnPropertyDescriptor() { throw new Error('trap'); } })");

        Assert.False(proxy.HasOwnProperty("a"));
        vm.GetException().Dispose();
        Assert.False(proxy.PropertyIsEnumerable("a"));
        vm.GetException().Dispose();
    }

    [Fact]
    public async Task ToByteArray_OnTypedArray_LeavesNoPendingException()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var view = vm.Eval("new Uint8Array([1, 2, 3]).subarray(1)");

        Assert.Equal(new byte[] { 2, 3 }, view.ToByteArray());
        using var pending = vm.GetException();
        Assert.False(pending.IsError);

        using var empty = vm.Eval("new ArrayBuffer(0)");
        Assert.Empty(empty.ToByteArray());
    }

    // ── Ported APIs ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClassName_ReadsEngineBrand_ConstructorNameReadsProperties()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var spoofed = vm.Eval("class Fake {}; const m = new Map(); Object.setPrototypeOf(m, Fake.prototype); m");
        Assert.Equal("Map", spoofed.ClassName);
        Assert.Equal("Fake", spoofed.ConstructorName);

        using var date = vm.Eval("new Date(0)");
        Assert.Equal("Date", date.ClassName);

        using var number = vm.NewNumber(1);
        Assert.Null(number.ClassName);

        using var bare = vm.Eval("Object.create(null)");
        Assert.Null(bare.ConstructorName);
    }

    [Fact]
    public async Task TypeOfProperty_MatchesVmTypeOf()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var fn = vm.Eval("(() => 1)");
        using var str = vm.NewString("s");
        Assert.Equal("function", fn.TypeOf);
        Assert.Equal("string", str.TypeOf);
        Assert.Equal("undefined", vm.UndefinedValue.TypeOf);
    }

    [Fact]
    public async Task Consume_ReturnsResultAndDisposes()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var handle = vm.NewNumber(41);
        var result = handle.Consume(h => h.ToNumber() + 1);
        Assert.Equal(42d, result);
        Assert.True(handle.Disposed);
    }

    [Fact]
    public async Task DefineProp_WithSymbolKey()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.NewObject();
        using var sym = vm.NewSymbol("tag", isGlobal: false);
        using var value = vm.NewString("tagged");
        obj.DefineProp(sym, value, enumerable: true);

        using var read = obj.GetProp(sym);
        Assert.Equal("tagged", read.ToManagedString());
        using var desc = obj.GetOwnPropertyDescriptor(sym);
        Assert.NotNull(desc);
        Assert.True(desc!.Enumerable);
        Assert.False(desc.Configurable);
    }

    // ── Options, versions, lifetime (test/limits.test.ts, test/versions.test.ts) ──

    [Theory]
    [InlineData(-1L)]
    [InlineData(QuickJs.MAX_STACK_SIZE + 1)]
    public async Task MaxStackSize_OutOfRangeThrows(long size)
    {
        if (!HasWasm) return;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateVmAsync(o => o.MaxStackSize = size));
    }

    [Fact]
    public async Task MaxStackSize_AtLimitIsAccepted()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync(o => o.MaxStackSize = QuickJs.MAX_STACK_SIZE);
        using var r = vm.Eval("1 + 1");
        Assert.Equal(2d, r.ToNumber());
    }

    [Fact]
    public async Task Versions_IncludesPackageAndEngine_AndIsCached()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var versions = vm.Versions;
        Assert.False(string.IsNullOrEmpty(versions["quickjs-wasi"]));
        Assert.False(string.IsNullOrEmpty(versions["quickjs"]));
        Assert.Same(versions, vm.Versions);
    }

    [Fact]
    public async Task DisposedVm_ThrowsObjectDisposedException()
    {
        if (!HasWasm) return;
        var vm = await CreateVmAsync();
        vm.Dispose();

        Assert.Throws<ObjectDisposedException>(() => vm.Eval("1"));
        Assert.Throws<ObjectDisposedException>(() => vm.NewNumber(1));
        Assert.Throws<ObjectDisposedException>(() => vm.NewPromise());
        Assert.Throws<ObjectDisposedException>(() => vm.ExecutePendingJobs());
        Assert.Throws<ObjectDisposedException>(() => vm.Versions);
        Assert.Throws<ObjectDisposedException>(() => vm.GcThreshold);
        Assert.Throws<ObjectDisposedException>(() => vm.Snapshot());
    }

    // ── Timezone (test/timezone.test.ts) ──────────────────────────────────────────

    [Theory]
    [InlineData("2024-01-15T12:00:00Z")]
    [InlineData("2024-07-15T12:00:00Z")]
    public async Task HostTimezone_MatchesLocalOffsetIncludingDst(string isoInstant)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync(o => o.TimezoneOffset = TimezoneOffsetOption.Host);
        using var offset = vm.Eval($"new Date('{isoInstant}').getTimezoneOffset()");

        // getTimezoneOffset() is minutes WEST of UTC.
        var expected = -TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Parse(isoInstant)).TotalMinutes;
        Assert.Equal(expected, offset.ToNumber());
    }

    [Fact]
    public async Task FixedTimezone_IsReported()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync(o => o.TimezoneOffset = TimezoneOffsetOption.Fixed(-60));
        using var offset = vm.Eval("new Date(0).getTimezoneOffset()");
        Assert.Equal(-60d, offset.ToNumber());
        using var hours = vm.Eval("new Date('2024-01-01T00:00:00Z').getHours()");
        Assert.Equal(1d, hours.ToNumber());
    }

    [Fact]
    public async Task CallbackTimezone_ReceivesEpochSeconds()
    {
        if (!HasWasm) return;
        var seen = new List<long>();
        using var vm = await CreateVmAsync(o => o.TimezoneOffset = TimezoneOffsetOption.Callback(seconds =>
        {
            seen.Add(seconds);
            return 300; // UTC-5
        }));
        using var offset = vm.Eval("new Date('2024-01-01T00:00:00Z').getTimezoneOffset()");
        Assert.Equal(300d, offset.ToNumber());
        Assert.Contains(seen, s => Math.Abs(s - 1704067200) < 86_400);
    }
}
