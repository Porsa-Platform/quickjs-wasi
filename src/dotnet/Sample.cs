#:project ./QuickJsWasi/QuickJsWasi.csproj

using System.Collections;
using QuickJsWasi;

// ====================================================================
// 3 — Creating a VM
// ====================================================================

// Minimal — uses the embedded WASM binary
using var vm = await QuickJs.CreateAsync();

// With options
// using var vm = await QuickJs.CreateAsync(new QuickJsOptions
// {
//     MemoryLimit  = 32 * 1024 * 1024,  // 32 MB JS heap
//     MaxStackSize = 4 * 1024 * 1024,   // 4 MB call stack
// });

Console.WriteLine($"QuickJS version: {vm.Versions["quickjs"]}");

// ====================================================================
// 4.1 — Script mode
// ====================================================================

{
    using var result = vm.Eval("1 + 2");
    Console.WriteLine($"4.1 Script eval: {result.ToNumber()}"); // 3
}

// ====================================================================
// 4.2 — Module mode
// ====================================================================

{
    // Module eval returns a promise that resolves to the module's namespace (exports)
    using var promise = vm.Eval(
        "export const value = 42;",
        filename: "mod.mjs",
        flags: EvalFlags.TYPE_MODULE);

    vm.ExecutePendingJobs();

    var settled = await vm.ResolvePromise(promise);
    if (settled is JSPromiseResult.Fulfilled f)
    {
        using (f.Value)
        {
            using var exported = f.Value.GetProp("value");
            Console.WriteLine($"4.2 Module eval: {exported.ToNumber()}"); // 42
        }
    }
}

// ====================================================================
// 4.3 — Bytecode compile + run
// ====================================================================

{
    byte[] bytecode = vm.Compile(
        "function greet(name) { return 'Hello, ' + name + '!'; }",
        filename: "greet.js",
        evalFlags: EvalFlags.TYPE_GLOBAL);

    using var evalResult = vm.EvalBytecode(bytecode);

    using var greetFn = vm.Global.GetProp("greet");
    using var arg = vm.NewString("World");
    using var retVal = vm.CallFunction(greetFn, vm.Global, arg);
    Console.WriteLine($"4.3 Bytecode: {retVal.ToManagedString()}"); // Hello, World!
}

// ====================================================================
// 5 — ES Module loading
// ====================================================================

{
    var modules = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["math.js"] = "export const add = (a, b) => a + b;",
    };

    // We need a separate VM for module loader to keep scope clean
    // (module loader is set at creation time)
    using var modVm = await QuickJs.CreateAsync(new QuickJsOptions
    {
        ModuleLoader = new ModuleLoaderOptions
        {
            Normalize = (baseName, specifier) => specifier,
            Load = name =>
            {
                if (modules.TryGetValue(name, out var src)) return src;
                throw new FileNotFoundException($"Module not found: {name}");
            },
        },
    });

    using var promise = modVm.Eval(
        "import { add } from 'math.js'; export const result = add(3, 4);",
        filename: "<entry>",
        flags: EvalFlags.TYPE_MODULE);

    modVm.ExecutePendingJobs();

    var settled = await modVm.ResolvePromise(promise);
    if (settled is JSPromiseResult.Fulfilled f2)
    {
        using (f2.Value)
        {
            using var r = f2.Value.GetProp("result");
            Console.WriteLine($"5 Module loader: {r.ToNumber()}"); // 7
        }
    }
}

// ====================================================================
// 6.1 — Creating values
// ====================================================================

{
    using var str = vm.NewString("hello");
    using var num = vm.NewNumber(3.14);
    using var big = vm.NewBigInt64(9_999_999_999L);
    using var sym = vm.NewSymbol("mySymbol", isGlobal: false);
    using var gsym = vm.NewSymbol("shared", isGlobal: true);
    using var obj = vm.NewObject();
    using var arr = vm.NewArray();
    byte[] raw = [1, 2, 3];
    using var ab = vm.NewArrayBuffer(raw);
    using var u8 = vm.NewUInt8Array(raw);
    using var err = vm.NewError("something went wrong");
    using var err2 = vm.NewError(new InvalidOperationException("bad state"));

    Console.WriteLine($"6.1 Values created: str={str.ToManagedString()}, num={num.ToNumber()}, big={big.ToInt64()}");
    Console.WriteLine($"   obj.IsObject={obj.IsObject}, arr.IsArray={arr.IsArray}");
    Console.WriteLine($"   ab.IsArrayBuffer={ab.IsArrayBuffer}, u8.Length={u8.ToByteArray().Length}");
    Console.WriteLine($"   err.IsError={err.IsError}, err2.IsError={err2.IsError}");

    // Singletons (do NOT dispose these)
    Console.WriteLine($"   undefined.Value.IsUndefined: {vm.UndefinedValue.IsUndefined}");
    Console.WriteLine($"   null: {vm.NullValue.IsNull}");
    Console.WriteLine($"   true: {vm.TrueValue.IsBool}");
    Console.WriteLine($"   false: {vm.FalseValue.IsBool}");
}

// ====================================================================
// 6.2 — Reading values
// ====================================================================

{
    using var result2 = vm.Eval("({ x: 1, y: 'hi', z: true })");

    using var x = result2.GetProp("x");
    Console.WriteLine($"6.2 ToNumber: {x.ToNumber()}"); // 1

    using var y = result2.GetProp("y");
    Console.WriteLine($"   ToManagedString: {y.ToManagedString()}"); // hi

    using var z = result2.GetProp("z");
    var boolVal = z.ToBool();
    Console.WriteLine($"   GetBool: {boolVal}"); // True

    using var bigVal = vm.Eval("9007199254740993n");
    Console.WriteLine($"   ToInt64: {bigVal.ToInt64()}"); // 9007199254740993

    using var buf = vm.Eval("new Uint8Array([10, 20, 30]).buffer");
    byte[] bytes = buf.ToByteArray();
    Console.WriteLine($"   ToByteArray: [{string.Join(", ", bytes)}]"); // [10, 20, 30]

    Console.WriteLine($"   TypeOf: {vm.TypeOf(x)}"); // number
}

// ====================================================================
// 6.3 — Type checking
// ====================================================================

{
    using var val = vm.Eval("Math.random()");
    if (val.IsNumber)
    {
        double n = val.ToNumber();
        Console.WriteLine($"6.3 Type check: Got a number: {n}");
    }

    using var strVal = vm.NewString("test");
    Console.WriteLine($"   IsString={strVal.IsString}, IsNumber={strVal.IsNumber}");

    using var promise = vm.Eval("Promise.resolve(1)");
    Console.WriteLine($"   IsPromise={promise.IsPromise}");
}

// ====================================================================
// 7.1 — Get / Set properties
// ====================================================================

{
    using var obj2 = vm.NewObject();
    using var num2 = vm.NewNumber(42);
    obj2.SetProp("answer", num2);

    using var got = obj2.GetProp("answer");
    Console.WriteLine($"7.1 Get/Set string: {got.ToNumber()}"); // 42

    using var symKey = vm.NewSymbol("tag", isGlobal: false);
    using var tagVal = vm.NewString("MyObject");
    obj2.SetProp(symKey, tagVal);

    using var tagGot = obj2.GetProp(symKey);
    Console.WriteLine($"   Get/Set symbol: {tagGot.ToManagedString()}"); // MyObject
}

// ====================================================================
// 7.2 — Define property with descriptor flags
// ====================================================================

{
    using var obj3 = vm.NewObject();
    using var constVal = vm.NewString("constant");
    obj3.DefineProp("PI", constVal, configurable: false, writable: false, enumerable: true);

    vm.Global.SetProp("readonlyObj", obj3);
    vm.Eval("try { readonlyObj.PI = 'changed'; } catch(e) {}");
    using var check = obj3.GetProp("PI");
    Console.WriteLine($"7.2 DefineProp: {check.ToManagedString()}"); // constant
}

// ====================================================================
// 7.3 — Enumerate properties
// ====================================================================

{
    using var obj4 = vm.Eval("({ a: 1, b: 2, c: 3 })");

    string[] keys = obj4.Keys();
    Console.WriteLine($"7.3 Keys: [{string.Join(", ", keys)}]"); // [a, b, c]

    string[] allKeys = obj4.GetOwnPropertyNames();
    Console.WriteLine($"   GetOwnPropertyNames: [{string.Join(", ", allKeys)}]");

    IReadOnlyList<object> ownKeys = obj4.GetOwnPropertyKeys();
    foreach (var k in ownKeys)
    {
        if (k is string s)
            Console.WriteLine($"   string key: {s}");
        else if (k is JSValueHandle sh)
            sh.Dispose();
    }

    Console.WriteLine($"   HasOwnProperty('a'): {obj4.HasOwnProperty("a")}");
    Console.WriteLine($"   PropertyIsEnumerable('a'): {obj4.PropertyIsEnumerable("a")}");

    using var proto = obj4.GetPrototypeOf();
    Console.WriteLine($"   GetPrototypeOf: {proto.IsObject}");
}

// ====================================================================
// 7.4 — Property descriptors
// ====================================================================

{
    using var obj5 = vm.Eval(@"
        const o = {};
        Object.defineProperty(o, 'x', { value: 99, writable: false, enumerable: true, configurable: false });
        Object.defineProperty(o, 'y', { get() { return 42; }, configurable: true });
        o
    ");

    using var dx = obj5.GetOwnPropertyDescriptor("x");
    if (dx is not null)
    {
        using (dx)
        {
            Console.WriteLine($"7.4 Data descriptor: value={dx.Value!.ToNumber()}, writable={dx.Writable}, enumerable={dx.Enumerable}, configurable={dx.Configurable}");
        }
    }

    using var dy = obj5.GetOwnPropertyDescriptor("y");
    if (dy is not null)
    {
        using (dy)
        {
            Console.WriteLine($"   Accessor descriptor: get.IsFunction={dy.Get!.IsFunction}, set.IsUndefined={dy.Set!.IsUndefined}");
        }
    }

    var noDesc = obj5.GetOwnPropertyDescriptor("missing");
    Console.WriteLine($"   Missing descriptor: {noDesc is null}"); // true
}

// ====================================================================
// 8 — Host functions
// ====================================================================

{
    using var addFn = vm.NewHostFunction("add", (thisVal, args) =>
    {
        double a = args[0].ToNumber();
        double b = args[1].ToNumber();
        return vm.NewNumber(a + b);
    }, argCount: 2);

    vm.Global.SetProp("add", addFn);
    using var hfResult = vm.Eval("add(10, 32)");
    Console.WriteLine($"8 Host function: {hfResult.ToNumber()}"); // 42

    // Host function throwing an exception
    using var fn = vm.NewHostFunction("mustBePositive", (_, args) =>
    {
        if (args[0].ToNumber() < 0)
            throw new ArgumentException("Value must be positive");
        return args[0].Dup();
    });
    vm.Global.SetProp("mustBePositive", fn);

    try
    {
        vm.Eval("mustBePositive(-1)");
    }
    catch (JSException ex)
    {
        Console.WriteLine($"   Exception: {ex.Message}");
    }
}

// ====================================================================
// 9 — Calling JS functions from .NET
// ====================================================================

{
    vm.Eval("function square(n) { return n * n; }");
    using var squareFn = vm.Global.GetProp("square");
    using var sqArg = vm.NewNumber(7);
    using var sqResult = vm.CallFunction(squareFn, vm.Global, sqArg);
    Console.WriteLine($"9 CallFunction: {sqResult.ToNumber()}"); // 49

    using var callObj = vm.Eval("({ x: 10, double() { return this.x * 2; } })");
    using var meth = callObj.GetProp("double");
    using var callRet = vm.CallFunction(meth, callObj);
    Console.WriteLine($"   Method call: {callRet.ToNumber()}"); // 20
}

// ====================================================================
// 10.1 — Deferred (create a promise from .NET)
// ====================================================================

{
    Deferred? pendingDeferred = null;

    using var getPromise = vm.NewHostFunction("getPromise", (_, _) =>
    {
        pendingDeferred = vm.NewPromise();
        return pendingDeferred.Handle;
    });
    vm.Global.SetProp("getPromise", getPromise);

    vm.Eval("getPromise().then(v => globalThis.resolvedVal = v)");
    vm.ExecutePendingJobs();

    using var resolvedValue = vm.NewString("done!");
    pendingDeferred!.Resolve(resolvedValue);
    vm.ExecutePendingJobs();

    using var checkVal = vm.Eval("globalThis.resolvedVal");
    Console.WriteLine($"10.1 Deferred: {checkVal.ToManagedString()}"); // done!
}

// ====================================================================
// 10.2 — Async host callback (Task<T> → Promise bridge)
// ====================================================================

{
    TaskCompletionSource<string>? pendingText = null;

    using var loadAsync = vm.NewHostFunction("loadAsync", async (_, _) =>
    {
        pendingText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (object?)await pendingText.Task;
    });
    vm.Global.SetProp("loadAsync", loadAsync);

    using var promise = vm.Eval("""
        (async function () {
            const value = await loadAsync();
            globalThis.asyncHostValue = value;
            return value;
        })()
    """);

    var pending = vm.ResolvePromise(promise);
    pendingText!.SetResult("done from async host");
    vm.ExecutePendingJobs();

    var settled = await pending;
    if (settled is JSPromiseResult.Fulfilled fulfilled)
    {
        using (fulfilled.Value)
        {
            Console.WriteLine($"10.2 Async host function: {fulfilled.Value.ToManagedString()}"); // done from async host
        }
    }

    using var asyncHostValue = vm.Eval("globalThis.asyncHostValue");
    Console.WriteLine($"   Guest observed: {asyncHostValue.ToManagedString()}"); // done from async host
}

// ====================================================================
// 10.3 — ResolvePromise (await promise settlement)
// ====================================================================

{
    // ResolvePromise on a fulfilled promise
    using var promise2 = vm.Eval("Promise.resolve(123)");
    var settled2 = await vm.ResolvePromise(promise2);
    switch (settled2)
    {
        case JSPromiseResult.Fulfilled f:
            Console.WriteLine($"10.3 ResolvePromise: {f.Value.ToNumber()}"); // 123
            f.Value.Dispose();
            break;
        case JSPromiseResult.Rejected r:
            Console.WriteLine($"10.3 Rejected: {r.Error.ToManagedString()}");
            r.Error.Dispose();
            break;
    }
}

// ====================================================================
// 10.4 — ExecutePendingJobs
// ====================================================================

{
    vm.Eval("Promise.resolve(42).then(v => globalThis.promiseVal = v)");
    int jobsRun = vm.ExecutePendingJobs();
    using var pv = vm.Eval("globalThis.promiseVal");
    Console.WriteLine($"10.4 ExecutePendingJobs: {jobsRun} job(s), value={pv.ToNumber()}"); // 1, 42
}

// ====================================================================
// 11 — Error handling
// ====================================================================

{
    try
    {
        vm.Eval("undefined.property");
    }
    catch (JSException ex)
    {
        Console.WriteLine($"11 Error: Name={ex.Name}, Message={ex.Message}");
        Console.WriteLine($"   Stack={(ex.JsStack ?? "(null)").Split('\n')[0]}");
        // ex.Handle is a live JSValueHandle to the QuickJS error object
        using var handle2 = ex.Handle;
        using var nameProp = handle2.GetProp("name");
        Console.WriteLine($"   Error handle name: {nameProp.ToManagedString()}");
    }

    // NB: GetException may still find the exception on the context even after
    // JSException was thrown and caught, depending on the quickjs-ng build.
    using var exc = vm.GetException();
    Console.WriteLine($"   GetException (after catch): IsUndefined={exc.IsUndefined}");
}

// ====================================================================
// 12.1 — Dump (JS to .NET)
// ====================================================================

{
    using var dumpVal = vm.Eval("({ name: 'Alice', scores: [95, 87, 100], active: true })");
    var dumped = vm.Dump(dumpVal);
    if (dumped is Dictionary<string, object?> dict)
    {
        Console.WriteLine($"12.1 Dump: name={dict["name"]}, scores=[{string.Join(", ", (object?[])dict["scores"]!)}], active={dict["active"]}");

        // Circular references
        using var circular = vm.Eval("var circ = {}; circ.self = circ; circ");
        var circDump = vm.Dump(circular);
        Console.WriteLine($"   Circular: {circDump is Dictionary<string, object?>}");
    }
}

// ====================================================================
// 12.2 — HostToHandle (.NET to JS)
// ====================================================================

{
    using var h1 = vm.HostToHandle(null);
    using var h2 = vm.HostToHandle(QuickJs.Undefined);
    using var h3 = vm.HostToHandle(42.0);
    using var h4 = vm.HostToHandle("hello");
    using var h5 = vm.HostToHandle(true);
    using var h6 = vm.HostToHandle(9999999999L);
    using var h7 = vm.HostToHandle(new byte[] { 1, 2, 3 });
    using var h8 = vm.HostToHandle(new[] { 1, 2, 3 });
    using var h9 = vm.HostToHandle(new Dictionary<string, object?> { ["x"] = 1 });
    using var h10 = vm.HostToHandle(new { Name = "Bob", Age = 30 });

    Console.WriteLine($"12.2 HostToHandle: h3={h3.ToNumber()}, h4={h4.ToManagedString()}, h5={h5.ToBool()}");
    Console.WriteLine($"   h8.IsArray={h8.IsArray}, h9.IsObject={h9.IsObject}");
    using var h10Name = h10.GetProp("Name");
    Console.WriteLine($"   POCO.Name={h10Name.ToManagedString()}");
}

// ====================================================================
// 13.3 — Garbage collection
// ====================================================================

{
    vm.RunGc();
    long threshold = vm.GcThreshold;
    Console.WriteLine($"13.3 GC: threshold={threshold}");
    vm.GcThreshold = 4 * 1024 * 1024;
    Console.WriteLine($"   GC threshold set to {vm.GcThreshold}");
}

// ====================================================================
// 13.4 — Memory usage statistics
// ====================================================================

{
    MemoryUsage usage = vm.GetMemoryUsage();
    Console.WriteLine($"13.4 Memory: {usage.ObjCount} objects, {usage.StrCount} strings, {usage.AtomCount} atoms, {usage.MallocSize} bytes");
}

// ====================================================================
// 14.1 — Interrupt handler (timeout)
// ====================================================================

{
    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(200);

    using var intVm = await QuickJs.CreateAsync(new QuickJsOptions
    {
        InterruptHandler = () => DateTimeOffset.UtcNow > deadline,
    });

    try
    {
        intVm.Eval("while(true){}");
    }
    catch (JSException ex) when (ex.Name == "InternalError")
    {
        Console.WriteLine($"14.1 Interrupt: {ex.Message}");
    }
}

// ====================================================================
// 14.2 — Intrinsics (disable eval)
// ====================================================================

{
    int intrinsics = Intrinsics.ALL & ~Intrinsics.EVAL;

    using var sandboxVm = await QuickJs.CreateAsync(new QuickJsOptions
    {
        Intrinsics = intrinsics,
    });

    try
    {
        sandboxVm.Eval("eval('1+1')");
    }
    catch (JSException)
    {
        Console.WriteLine("14.2 Intrinsics: eval disabled as expected");
    }
}

// ====================================================================
// 15 — Timezone configuration
// ====================================================================

{
    using var tzVm = await QuickJs.CreateAsync(new QuickJsOptions
    {
        TimezoneOffset = TimezoneOffsetOption.Host,
    });

    using var tzResult = tzVm.Eval("new Date(0).getTimezoneOffset()");
    var actualTz = tzResult.ToNumber();
    // getTimezoneOffset() returns minutes WEST of UTC.
    // Our host callback may not be linked (depends on WASM build flags), returning 0.
    var hostMinutesEast = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UnixEpoch).TotalMinutes;
    var expectedWest = -hostMinutesEast;
    Console.WriteLine($"15 Timezone: {actualTz} (expected ~{expectedWest}, host offset={actualTz != 0})");
}

// ====================================================================
// 16 — Snapshots
// ====================================================================

{
    // Set some state in the VM
    vm.Eval("var counter = 42; function add(a, b) { return a + b; }");
    using var before = vm.Eval("counter");
    Console.WriteLine($"16 Before snapshot: counter={before.ToNumber()}"); // 42

    // Snapshot captures the full QuickJS heap
    Snapshot snap = vm.Snapshot();
    byte[] snapBytes = Snapshot.Serialize(snap);
    Console.WriteLine($"   Snapshot: {snapBytes.Length} bytes");

    // Restore into a fresh VM — all state is preserved
    using var vm2 = await QuickJs.RestoreAsync(
        Snapshot.Deserialize(snapBytes),
        new QuickJsOptions { WasmBytes = null });

    using var counter2 = vm2.Eval("counter");
    using var sum = vm2.Eval("add(counter, 58)");
    Console.WriteLine($"   Restored: counter={counter2.ToNumber()}, add(counter, 58)={sum.ToNumber()}"); // 42, 100

    // Snapshot with host function + re-registration on restore
    using var fnVm = await QuickJs.CreateAsync();
    using var logFn = fnVm.NewHostFunction("log", (_, args) =>
    {
        Console.WriteLine($"   Host log: {args[0].ToManagedString()}");
        return fnVm.UndefinedValue;
    });
    fnVm.Global.SetProp("log", logFn);
    fnVm.Eval("log('before snapshot')");
    var fnSnap = fnVm.Snapshot();

    using var restoredVm = await QuickJs.RestoreAsync(fnSnap, new QuickJsOptions { WasmBytes = null });
    restoredVm.RegisterHostCallback("log", (_, args) =>
    {
        Console.WriteLine($"   Restored log: {args[0].ToManagedString()}");
        return restoredVm.UndefinedValue;
    });
    restoredVm.Eval("log('state preserved after restore')");
}

// ====================================================================
// 17 — Version information
// ====================================================================

{
    IReadOnlyDictionary<string, string> versions = vm.Versions;
    Console.WriteLine($"17 Version: quickjs={versions["quickjs"]}");
}

Console.WriteLine("\nAll samples completed successfully.");
