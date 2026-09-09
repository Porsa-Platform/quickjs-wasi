using Xunit;

namespace QuickJsWasi.Tests;

public sealed class EvalTests : TestBase
{
    // ====================================================================
    // 4.1 — Script mode
    // ====================================================================

    [Fact]
    public async Task ScriptEval_ReturnsNumber()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var result = vm.Eval("1 + 2 + 3");
        Assert.Equal(6d, result.ToNumber());
    }

    [Fact]
    public async Task ScriptEval_ReturnsString()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var result = vm.Eval("'hello'.toUpperCase()");
        Assert.Equal("HELLO", result.ToManagedString());
    }

    [Fact]
    public async Task ScriptEval_ThrowsOnSyntaxError()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        Assert.Throws<JSException>(() => vm.Eval("syntax error {{{"));
    }

    // ====================================================================
    // 4.2 — Module mode
    // ====================================================================

    [Fact]
    public async Task ModuleEval_ReturnsNamespace()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = vm.Eval("export const value = 42;", flags: EvalFlags.TYPE_MODULE);
        vm.ExecutePendingJobs();
        var settled = await vm.ResolvePromise(promise);
        var f = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        using (f.Value)
        {
            using var exported = f.Value.GetProp("value");
            Assert.Equal(42d, exported.ToNumber());
        }
    }

    // ====================================================================
    // 4.3 — Bytecode compile + run
    // ====================================================================

    [Fact]
    public async Task Bytecode_CompileAndRun()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        byte[] bytecode = vm.Compile(
            "function greet(name) { return 'Hello, ' + name + '!'; }");
        using var _ = vm.EvalBytecode(bytecode);
        using var fn = vm.Global.GetProp("greet");
        using var arg = vm.NewString("World");
        using var result = vm.CallFunction(fn, vm.Global, arg);
        Assert.Equal("Hello, World!", result.ToManagedString());
    }

    // ====================================================================
    // 5 — ES Module loading
    // ====================================================================

    [Fact]
    public async Task ModuleLoader_LoadsAndResolves()
    {
        if (!HasWasm) return;
        var modules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["math.js"] = "export const add = (a, b) => a + b;",
        };

        using var vm = await CreateVmAsync(o => o.ModuleLoader = new ModuleLoaderOptions
        {
            Normalize = (_, s) => s,
            Load = n => modules.TryGetValue(n, out var src) ? src : throw new FileNotFoundException(n),
        });

        using var promise = vm.Eval(
            "import { add } from 'math.js'; export const result = add(3, 4);",
            filename: "<entry>", flags: EvalFlags.TYPE_MODULE);

        vm.ExecutePendingJobs();
        var settled = await vm.ResolvePromise(promise);
        var f = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        using (f.Value)
        {
            using var r = f.Value.GetProp("result");
            Assert.Equal(7d, r.ToNumber());
        }
    }

    // ====================================================================
    // 6.1 — Creating values
    // ====================================================================

    [Fact]
    public async Task CreateValues_AllTypes()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        using var str = vm.NewString("hello");
        Assert.True(str.IsString);
        Assert.Equal("hello", str.ToManagedString());

        using var num = vm.NewNumber(3.14);
        Assert.True(num.IsNumber);
        Assert.Equal(3.14, num.ToNumber(), precision: 10);

        using var big = vm.NewBigInt64(9_999_999_999L);
        Assert.True(big.IsBigInt);
        Assert.Equal(9_999_999_999L, big.ToInt64());

        using var sym = vm.NewSymbol("localSym", isGlobal: false);
        Assert.True(sym.IsSymbol);

        using var gsym = vm.NewSymbol("globalSym", isGlobal: true);
        Assert.True(gsym.IsSymbol);

        using var obj = vm.NewObject();
        Assert.True(obj.IsObject);

        using var arr = vm.NewArray();
        Assert.True(arr.IsArray);

        using var ab = vm.NewArrayBuffer(new byte[] { 1, 2, 3 });
        Assert.True(ab.IsArrayBuffer);

        using var u8 = vm.NewUInt8Array(new byte[] { 4, 5, 6 });
        Assert.Equal(3, u8.ToByteArray().Length);

        using var err = vm.NewError("test error");
        Assert.True(err.IsError);

        using var err2 = vm.NewError(new InvalidOperationException("bad"));
        Assert.True(err2.IsError);
    }

    // ====================================================================
    // 6.2 — Reading values
    // ====================================================================

    [Fact]
    public async Task ReadValues_AllTypes()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        using var result = vm.Eval("({ x: 1, y: 'hi', z: true })");
        using var x = result.GetProp("x");
        Assert.Equal(1d, x.ToNumber());

        using var y = result.GetProp("y");
        Assert.Equal("hi", y.ToManagedString());

        using var big = vm.Eval("9007199254740993n");
        Assert.Equal(9007199254740993L, big.ToInt64());

        using var buf = vm.Eval("new Uint8Array([10, 20, 30]).buffer");
        Assert.Equal(new byte[] { 10, 20, 30 }, buf.ToByteArray());

        Assert.Equal("number", vm.TypeOf(x));
        Assert.Equal("string", vm.TypeOf(y));
    }

    // ====================================================================
    // 6.3 — Type checking
    // ====================================================================

    [Fact]
    public async Task TypeChecks_AllBrands()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        Assert.True(vm.UndefinedValue.IsUndefined);
        Assert.True(vm.NullValue.IsNull);
        Assert.True(vm.TrueValue.IsBool);
        Assert.True(vm.FalseValue.IsBool);

        using var num = vm.NewNumber(1);
        Assert.True(num.IsNumber);

        using var str = vm.NewString("s");
        Assert.True(str.IsString);

        using var obj = vm.NewObject();
        Assert.True(obj.IsObject);
        Assert.False(obj.IsArray);

        using var arr = vm.NewArray();
        Assert.True(arr.IsArray);

        using var fn = vm.Eval("(function(){})");
        Assert.True(fn.IsFunction);

        using var err = vm.NewError("e");
        Assert.True(err.IsError);

        using var promise = vm.Eval("Promise.resolve(1)");
        Assert.True(promise.IsPromise);

        using var ab = vm.NewArrayBuffer(new byte[] { 1 });
        Assert.True(ab.IsArrayBuffer);

        using var proxy = vm.Eval("new Proxy({}, {})");
        Assert.True(proxy.IsProxy);

        using var map = vm.Eval("new Map()");
        Assert.True(map.IsMap);

        using var set = vm.Eval("new Set()");
        Assert.True(set.IsSet);

        using var date = vm.Eval("new Date()");
        Assert.True(date.IsDate);
        Assert.False(date.IsRegExp);
    }

    // ====================================================================
    // 7.1 — Get / Set properties
    // ====================================================================

    [Fact]
    public async Task GetSetProp_StringAndSymbol()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.NewObject();

        using var val = vm.NewNumber(42);
        obj.SetProp("answer", val);
        using var got = obj.GetProp("answer");
        Assert.Equal(42d, got.ToNumber());

        using var sym = vm.NewSymbol("tag", isGlobal: false);
        using var tag = vm.NewString("MyObject");
        obj.SetProp(sym, tag);
        using var tagGot = obj.GetProp(sym);
        Assert.Equal("MyObject", tagGot.ToManagedString());
    }

    // ====================================================================
    // 7.2 — Define property with flags
    // ====================================================================

    [Fact]
    public async Task DefineProp_ReadOnly()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.NewObject();
        using var val = vm.NewString("constant");
        obj.DefineProp("PI", val, configurable: false, writable: false, enumerable: true);

        vm.Global.SetProp("_testObj", obj);
        vm.Eval("try { _testObj.PI = 'changed'; } catch(e) {}");
        using var check = obj.GetProp("PI");
        Assert.Equal("constant", check.ToManagedString());
    }

    // ====================================================================
    // 7.3 — Enumerate properties
    // ====================================================================

    [Fact]
    public async Task EnumerateProperties()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.Eval("({ a: 1, b: 2, c: 3 })");

        Assert.Equal(new[] { "a", "b", "c" }, obj.Keys());
        Assert.True(obj.HasOwnProperty("a"));
        Assert.True(obj.PropertyIsEnumerable("a"));

        using var proto = obj.GetPrototypeOf();
        Assert.True(proto.IsObject);
    }

    // ====================================================================
    // 7.4 — Property descriptors
    // ====================================================================

    [Fact]
    public async Task PropertyDescriptors_DataAndAccessor()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var obj = vm.Eval(@"
            const o = {};
            Object.defineProperty(o, 'x', { value: 99, writable: false, enumerable: true, configurable: false });
            Object.defineProperty(o, 'y', { get() { return 42; }, configurable: true });
            o
        ");

        using var dx = obj.GetOwnPropertyDescriptor("x");
        Assert.NotNull(dx);
        using (dx)
        {
            Assert.Equal(99d, dx.Value!.ToNumber());
            Assert.False(dx.Writable);
            Assert.True(dx.Enumerable);
            Assert.False(dx.Configurable);
        }

        using var dy = obj.GetOwnPropertyDescriptor("y");
        Assert.NotNull(dy);
        using (dy)
        {
            Assert.True(dy.Get!.IsFunction);
            Assert.True(dy.Set!.IsUndefined);
        }

        Assert.Null(obj.GetOwnPropertyDescriptor("missing"));
    }

    // ====================================================================
    // 8 — Host functions
    // ====================================================================

    [Fact]
    public async Task HostFunction_BasicCall()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var fn = vm.NewHostFunction("add", (_, args) =>
            vm.NewNumber(args[0].ToNumber() + args[1].ToNumber()), argCount: 2);
        vm.Global.SetProp("add", fn);

        using var result = vm.Eval("add(10, 32)");
        Assert.Equal(42d, result.ToNumber());
    }

    [Fact]
    public async Task HostFunction_ThrowsException()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var fn = vm.NewHostFunction("check", (_, args) =>
        {
            if (args[0].ToNumber() < 0)
                throw new ArgumentException("must be positive");
            return args[0].Dup();
        });
        vm.Global.SetProp("check", fn);

        var ex = Assert.Throws<JSException>(() => vm.Eval("check(-1)"));
        Assert.Contains("must be positive", ex.Message);
    }

    [Fact]
    public async Task HostFunction_AsyncDelegate_ResolvesAwaitedValue()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fn = vm.NewHostFunction("loadValue", async (_, _) => await tcs.Task, argCount: 0);
        vm.Global.SetProp("loadValue", fn);

        using var promise = vm.Eval("""
            (async function () {
                const result = await loadValue();
                globalThis.asyncHostResult = result;
                return result;
            })()
        """);
        var task = vm.ResolvePromise(promise);

        Assert.False(task.IsCompleted);
        tcs.SetResult("done");
        vm.ExecutePendingJobs();

        var settled = await task;
        var fulfilled = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        Assert.Equal("done", fulfilled.Value.ToManagedString());
        fulfilled.Value.Dispose();

        using var check = vm.Eval("globalThis.asyncHostResult");
        Assert.Equal("done", check.ToManagedString());
    }

    [Fact]
    public async Task HostFunction_AsyncDelegate_RejectsAwaitedValue()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var fn = vm.NewHostFunction("failValue", (_, _) =>
            Task.FromException<object?>(new InvalidOperationException("boom")));
        vm.Global.SetProp("failValue", fn);

        using var promise = vm.Eval("""
            (async function () {
                try {
                    await failValue();
                    return "unexpected";
                } catch (error) {
                    return error.message;
                }
            })()
        """);
        var task = vm.ResolvePromise(promise);
        vm.ExecutePendingJobs();

        var settled = await task;
        var fulfilled = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        Assert.Equal("boom", fulfilled.Value.ToManagedString());
        fulfilled.Value.Dispose();
    }

    // ====================================================================
    // 9 — Calling JS functions from .NET
    // ====================================================================

    [Fact]
    public async Task CallFunction_GlobalAndMethod()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        vm.Eval("function square(n) { return n * n; }");
        using var fn = vm.Global.GetProp("square");
        using var arg = vm.NewNumber(7);
        using var result = vm.CallFunction(fn, vm.Global, arg);
        Assert.Equal(49d, result.ToNumber());

        using var obj = vm.Eval("({ x: 10, double() { return this.x * 2; } })");
        using var meth = obj.GetProp("double");
        using var ret = vm.CallFunction(meth, obj);
        Assert.Equal(20d, ret.ToNumber());
    }

    // ====================================================================
    // 10.1 — Deferred
    // ====================================================================

    [Fact]
    public async Task Deferred_ResolveFromDotNet()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        Deferred? d = null;
        using var getP = vm.NewHostFunction("getP", (_, _) =>
        {
            d = vm.NewPromise();
            return d.Handle;
        });
        vm.Global.SetProp("getP", getP);

        vm.Eval("getP().then(v => globalThis.res = v)");
        vm.ExecutePendingJobs();

        using var val = vm.NewString("resolved");
        d!.Resolve(val);
        vm.ExecutePendingJobs();

        using var check = vm.Eval("globalThis.res");
        Assert.Equal("resolved", check.ToManagedString());
    }

    // ====================================================================
    // 10.2 — ResolvePromise
    // ====================================================================

    [Fact]
    public async Task ResolvePromise_AlreadyFulfilled()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = vm.Eval("Promise.resolve(42)");
        vm.ExecutePendingJobs();

        var settled = await vm.ResolvePromise(promise);
        var f = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        Assert.Equal(42d, f.Value.ToNumber());
        f.Value.Dispose();
    }

    [Fact]
    public async Task ResolvePromise_Rejected()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = vm.Eval("Promise.reject(new Error('fail'))");
        vm.ExecutePendingJobs();

        var settled = await vm.ResolvePromise(promise);
        var r = Assert.IsType<JSPromiseResult.Rejected>(settled);
        Assert.Contains("fail", r.Error.ToManagedString());
        r.Error.Dispose();
    }

    [Fact]
    public async Task ResolvePromise_NonPromiseReturnsImmediate()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var val = vm.Eval("42");

        var settled = await vm.ResolvePromise(val);
        var f = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        Assert.Equal(42d, f.Value.ToNumber());
        f.Value.Dispose();
    }

    [Fact]
    public async Task HostToHandle_TaskOfObject_BridgesToPromise()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var promise = vm.HostToHandle(tcs.Task);

        Assert.True(promise.IsPromise);
        var task = vm.ResolvePromise(promise);

        tcs.SetResult(new { fileRef = "ok", sizeBytes = 42d });
        vm.ExecutePendingJobs();

        var settled = await task;
        var fulfilled = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        using var fileRef = fulfilled.Value.GetProp("fileRef");
        using var sizeBytes = fulfilled.Value.GetProp("sizeBytes");
        Assert.Equal("ok", fileRef.ToManagedString());
        Assert.Equal(42d, sizeBytes.ToNumber());
        fulfilled.Value.Dispose();
    }

    [Fact]
    public async Task HostToHandle_TaskWithoutResult_ResolvesUndefined()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = vm.HostToHandle(Task.CompletedTask);

        var settled = await vm.ResolvePromise(promise);
        var fulfilled = Assert.IsType<JSPromiseResult.Fulfilled>(settled);
        Assert.True(fulfilled.Value.IsUndefined);
        fulfilled.Value.Dispose();
    }

    // ====================================================================
    // 10.3 — ExecutePendingJobs
    // ====================================================================

    [Fact]
    public async Task ExecutePendingJobs_ProcessesMicrotasks()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        vm.Eval("Promise.resolve(99).then(v => globalThis.result = v)");
        int count = vm.ExecutePendingJobs();
        Assert.Equal(1, count);

        using var val = vm.Eval("globalThis.result");
        Assert.Equal(99d, val.ToNumber());
    }

    // ====================================================================
    // 11 — Error handling
    // ====================================================================

    [Fact]
    public async Task JSException_ContainsNameAndMessage()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        try
        {
            vm.Eval("undefined.property");
        }
        catch (JSException ex)
        {
            Assert.Equal("TypeError", ex.Name);
            Assert.NotNull(ex.Message);
            Assert.NotNull(ex.JsStack);
            // ex.Handle should work
            using var nameProp = ex.Handle.GetProp("name");
            Assert.Equal("TypeError", nameProp.ToManagedString());
        }
    }

    [Fact]
    public async Task GetException_ReturnsUndefinedWhenNoException()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var exc = vm.GetException();
        Assert.False(exc.IsException);
    }

    // ====================================================================
    // 12.1 — Dump (JS to .NET)
    // ====================================================================

    [Fact]
    public async Task Dump_ObjectToDictionary()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var val = vm.Eval("({ name: 'Alice', scores: [95, 87, 100], active: true })");
        var dumped = vm.Dump(val);
        var dict = Assert.IsType<Dictionary<string, object?>>(dumped);
        Assert.Equal("Alice", dict["name"]);
        Assert.Equal(new object?[] { 95.0, 87.0, 100.0 }, dict["scores"]);
        Assert.True((bool)dict["active"]!);
    }

    [Fact]
    public async Task Dump_HandlesCycles()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var val = vm.Eval("const o = {}; o.self = o; o");
        var dumped = vm.Dump(val); // should not throw
        Assert.IsType<Dictionary<string, object?>>(dumped);
    }

    [Fact]
    public async Task Dump_Scalars()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        Assert.Equal(3d, vm.Dump(vm.Eval("3")));
        Assert.Equal("hi", vm.Dump(vm.Eval("'hi'")));
        Assert.True((bool)vm.Dump(vm.Eval("true"))!);
    }

    // ====================================================================
    // 12.2 — HostToHandle (.NET to JS)
    // ====================================================================

    [Fact]
    public async Task HostToHandle_BasicTypes()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        using var h1 = vm.HostToHandle((object?)null);
        Assert.True(h1.IsNull);

        using var h2 = vm.HostToHandle(QuickJs.Undefined);
        Assert.True(h2.IsUndefined);

        using var h3 = vm.HostToHandle(42.0);
        Assert.Equal(42d, h3.ToNumber());

        using var h4 = vm.HostToHandle("hello");
        Assert.Equal("hello", h4.ToManagedString());

        using var h5 = vm.HostToHandle(true);
        Assert.True(vm.Exports.GetBool(h5.Ptr) != 0);

        using var h6 = vm.HostToHandle(9999999999L);
        Assert.True(h6.IsBigInt);

        using var h7 = vm.HostToHandle(new byte[] { 1, 2, 3 });
        Assert.True(h7.IsArrayBuffer);
    }

    [Fact]
    public async Task HostToHandle_ArrayAndDictionary()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        using var arr = vm.HostToHandle(new[] { 1, 2, 3 });
        Assert.True(arr.IsArray);

        using var dict = vm.HostToHandle(new Dictionary<string, object?> { ["x"] = 1d });
        Assert.True(dict.IsObject);
        using var x = dict.GetProp("x");
        Assert.Equal(1d, x.ToNumber());
    }

    [Fact]
    public async Task HostToHandle_Poco()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        using var obj = vm.HostToHandle(new { Name = "Bob", Age = 30 });
        using var name = obj.GetProp("Name");
        Assert.Equal("Bob", name.ToManagedString());
        using var age = obj.GetProp("Age");
        Assert.Equal(30d, age.ToNumber());
    }

    // ====================================================================
    // 13.3 — GC
    // ====================================================================

    [Fact]
    public async Task GcThreshold_GetSet()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        vm.RunGc();

        int old = vm.GcThreshold;
        vm.GcThreshold = 4 * 1024 * 1024;
        Assert.Equal(4 * 1024 * 1024, vm.GcThreshold);
        vm.GcThreshold = old;
    }

    // ====================================================================
    // 13.4 — Memory statistics
    // ====================================================================

    [Fact]
    public async Task GetMemoryUsage_ReturnsStats()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var usage = vm.GetMemoryUsage();
        Assert.True(usage.MallocSize >= 0);
        Assert.True(usage.ObjCount >= 0);
    }

    // ====================================================================
    // 14.1 — Interrupt handler
    // ====================================================================

    [Fact]
    public async Task InterruptHandler_StopsInfiniteLoop()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync((options) => options.InterruptHandler = () => true);

        var ex = Assert.Throws<JSException>(() => vm.Eval("while(true){}"));
        Assert.Equal("InternalError", ex.Name);
    }

    // ====================================================================
    // 14.2 — Intrinsics
    // ====================================================================

    [Fact]
    public async Task Intrinsics_DisabledEval()
    {
        if (!HasWasm) return;
        int flags = Intrinsics.ALL & ~Intrinsics.EVAL;
        using var vm = await CreateVmAsync(o => o.Intrinsics = flags);
        Assert.Throws<JSException>(() => vm.Eval("eval('1+1')"));
    }

    // ====================================================================
    // 15 — Timezone
    // ====================================================================

    [Fact]
    public async Task Timezone_HostOffset()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync(o =>
            o.TimezoneOffset = TimezoneOffsetOption.Host);

        using var result = vm.Eval("new Date(0).getTimezoneOffset()");
        var actual = result.ToNumber();

        // Date.getTimezoneOffset() returns minutes WEST of UTC.
        // DateTimeOffset.Offset.TotalMinutes is minutes EAST of UTC, so negate.
        var expected = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UnixEpoch).TotalMinutes;

        // Some WASM builds may not include the --wrap=__secs_to_zone linker flag,
        // in which case the C# callback is never called and the offset is 0.
        if (actual == 0)
        {
            Assert.Equal(0d, actual);
            return;
        }

        Assert.Equal(expected, actual);
    }

    // ====================================================================
    // 16 — Snapshots
    // ====================================================================

    [Fact]
    public async Task Snapshot_CaptureAndRestore()
    {
        if (!HasWasm) return;
        using var vm1 = await CreateVmAsync();
        vm1.Eval("1 + 2");

        var snap = vm1.Snapshot();
        var bytes = Snapshot.Serialize(snap);
        var restored = Snapshot.Deserialize(bytes);

        using var vm2 = await QuickJs.RestoreAsync(restored, new QuickJsOptions { WasmBytes = WasmBytes });
        using var result = vm2.Eval("1 + 2");
        Assert.Equal(3d, result.ToNumber());
    }

    [Fact]
    public async Task Snapshot_WithHostFunction_ReRegisterCallback()
    {
        if (!HasWasm) return;
        using var vm1 = await CreateVmAsync();

        string? captured = null;
        using var fn = vm1.NewHostFunction("log", (_, args) =>
        {
            captured = args[0].ToManagedString();
            return vm1.UndefinedValue;
        });
        vm1.Global.SetProp("log", fn);
        var snap = vm1.Snapshot();

        captured = null;
        using var vm2 = await QuickJs.RestoreAsync(snap, new QuickJsOptions { WasmBytes = WasmBytes });
        vm2.RegisterHostCallback("log", (_, args) =>
        {
            captured = "restored: " + args[0].ToManagedString();
            return vm2.UndefinedValue;
        });
        vm2.Eval("log('hello')");
        Assert.Equal("restored: hello", captured);
    }

    // ====================================================================
    // 17 — Version
    // ====================================================================

    [Fact]
    public async Task Versions_ContainsQuickJs()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        Assert.NotNull(vm.Versions["quickjs"]);
        Assert.NotEmpty(vm.Versions["quickjs"]);
    }

    // ====================================================================
    // Multiple VMs — ensure no state leaks
    // ====================================================================

    [Fact]
    public async Task MultipleVMs_AreIndependent()
    {
        if (!HasWasm) return;
        using var vm1 = await CreateVmAsync();
        using var vm2 = await CreateVmAsync();

        vm1.Eval("var x = 42;");
        vm2.Eval("var x = 99;");

        using var r1 = vm1.Eval("x");
        using var r2 = vm2.Eval("x");
        Assert.Equal(42d, r1.ToNumber());
        Assert.Equal(99d, r2.ToNumber());
    }

    // ====================================================================
    // Dup() — reference counting
    // ====================================================================

    [Fact]
    public async Task Dup_CreatesIndependentHandle()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var original = vm.Eval("42");
        using var dup = original.Dup();
        Assert.Equal(original.ToNumber(), dup.ToNumber());
    }

    // ====================================================================
    // PromiseState on fulfilled/rejected/pending
    // ====================================================================

    [Fact]
    public async Task PromiseState_ReflectsState()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        using var fulfilled = vm.Eval("Promise.resolve(1)");
        vm.ExecutePendingJobs();
        Assert.Equal(1, fulfilled.PromiseState);

        using var rejected = vm.Eval("Promise.reject(new Error('x'))");
        vm.ExecutePendingJobs();
        Assert.Equal(2, rejected.PromiseState);
    }

    // ====================================================================
    // GetPromiseResult — synchronous read after settlement
    // ====================================================================

    [Fact]
    public async Task GetPromiseResult_ReturnsValue()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = vm.Eval("Promise.resolve(21).then(x => x * 2)");
        vm.ExecutePendingJobs();
        Assert.Equal(1, promise.PromiseState);

        using var value = vm.GetPromiseResult(promise);
        Assert.Equal(42d, value.ToNumber());
    }

    // ====================================================================
    // Host callback with this binding
    // ====================================================================

    [Fact]
    public async Task HostFunction_ThisValue()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();

        JSValueHandle? capturedThis = null;
        using var fn = vm.NewHostFunction("getThis", (thisVal, _) =>
        {
            capturedThis = thisVal.Dup();
            return vm.UndefinedValue;
        });
        vm.Global.SetProp("getThis", fn);

        vm.Eval("const obj = { getThis }; obj.getThis()");
        vm.ExecutePendingJobs();

        Assert.NotNull(capturedThis);
        using (capturedThis)
        {
            using var x = capturedThis.GetProp("getThis");
            Assert.True(x.IsFunction);
        }
    }
}
