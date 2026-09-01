# QuickJsWasi — .NET host

[![NuGet](https://img.shields.io/nuget/v/QuickJsWasi.svg)](https://www.nuget.org/packages/QuickJsWasi)

A .NET 10 library that embeds the [QuickJS-NG](https://github.com/nicowillis/quickjs-ng) JavaScript engine
compiled to a WASI reactor WebAssembly binary and exposes it through a clean C# API. The key features are:

> 📌 **A complete, runnable example covering every API feature is available in [`Sample.cs`](Sample.cs).**

- **Snapshotable** — freeze the full VM state (including pending Promises) to bytes and restore it instantly in a fresh instance.
- **ES Module loading** — supply a synchronous `ModuleLoader` to handle `import` statements.
- **Host functions** — expose .NET methods as first-class JavaScript functions.
- **Promise bridging** — drive the JS microtask queue and `await` QuickJS promises from .NET `Task`.
- **Sandboxed** — memory limits, stack limits, interrupt handlers, and selective intrinsic removal.
- **Zero native deps** — the entire QuickJS engine ships as an embedded WASM binary inside the NuGet package.

---

## Table of contents

1. [Installation](#1-installation)
2. [WASM provisioning](#2-wasm-provisioning)
3. [Creating a VM](#3-creating-a-vm)
4. [Evaluating JavaScript](#4-evaluating-javascript)
   - [Script mode](#41-script-mode)
   - [Module mode](#42-module-mode)
   - [Bytecode — compile + run](#43-bytecode--compile--run)
5. [ES Module loading](#5-es-module-loading)
6. [Value types](#6-value-types)
   - [Creating values](#61-creating-values)
   - [Reading values](#62-reading-values)
   - [Type checking](#63-type-checking)
7. [Property operations](#7-property-operations)
   - [Get / Set](#71-get--set)
   - [Define (with descriptor flags)](#72-define-with-descriptor-flags)
   - [Enumerate properties](#73-enumerate-properties)
   - [Property descriptors](#74-property-descriptors)
8. [Host functions](#8-host-functions)
9. [Calling JS functions from .NET](#9-calling-js-functions-from-net)
10. [Promises and async](#10-promises-and-async)
    - [Deferred — create a promise](#101-deferred--create-a-promise)
    - [ResolvePromise — await settlement](#102-resolvepromise--await-settlement)
    - [Executing the microtask queue](#103-executing-the-microtask-queue)
11. [Error handling](#11-error-handling)
12. [Value marshalling](#12-value-marshalling)
    - [Dump — JS → .NET](#121-dump--js--net)
    - [HostToHandle — .NET → JS](#122-hosttohandle--net--js)
13. [Memory and performance](#13-memory-and-performance)
    - [Memory limit](#131-memory-limit)
    - [Stack size](#132-stack-size)
    - [Garbage collection](#133-garbage-collection)
    - [Memory usage statistics](#134-memory-usage-statistics)
14. [Execution control](#14-execution-control)
    - [Interrupt handler](#141-interrupt-handler)
    - [Intrinsics — selectively disable builtins](#142-intrinsics--selectively-disable-builtins)
15. [Timezone configuration](#15-timezone-configuration)
16. [Snapshots](#16-snapshots)
    - [Capture and restore](#161-capture-and-restore)
    - [Binary serialisation](#162-binary-serialisation)
    - [Re-registering host callbacks](#163-re-registering-host-callbacks)
17. [Version information](#17-version-information)
18. [WasiShim — customising I/O](#18-wasishim--customising-io)
19. [Full API reference](#19-full-api-reference)
20. [Publish pipeline](#20-publish-pipeline)
21. [Build and test locally](#21-build-and-test-locally)

---

## 1. Installation

```xml
<PackageReference Include="QuickJsWasi" Version="*" />
```

or

```sh
dotnet add package QuickJsWasi
```

The `quickjs.wasm` binary is embedded inside the NuGet package — no extra files needed.

---

## 2. WASM provisioning

When you install the NuGet package the WASM binary is embedded as a managed resource; `QuickJs.CreateAsync()` finds it automatically and no extra configuration is required.

If you want to supply your own WASM build (e.g. a custom instrumented build or a newer binary from the repo), set `WasmBytes`:

```csharp
var wasmBytes = await File.ReadAllBytesAsync("quickjs.wasm");
using var vm = await QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasmBytes });
```

### Building from source

```sh
# Clone with submodules (quickjs-ng)
git clone --recurse-submodules https://github.com/Porsa-Platform/quickjs-wasi.git
cd quickjs-wasi

# Install wasi-sdk (the WASM C toolchain)
make setup

# Build just the core WASM reactor
make quickjs.wasm

# Copy into the library's Resources folder so it is embedded at build time
cp quickjs.wasm src/dotnet/QuickJsWasi/Resources/quickjs.wasm
```

---

## 3. Creating a VM

```csharp
using QuickJsWasi;

// Minimal — uses the embedded WASM binary
using var vm = await QuickJs.CreateAsync();

// With options
using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    MemoryLimit   = 32 * 1024 * 1024,  // 32 MB JS heap
    MaxStackSize  = 4 * 1024 * 1024,   // 4 MB call stack
    TimezoneOffset = TimezoneOffsetOption.Host,
});
```

`QuickJs` implements `IDisposable`. Always dispose the VM (or use `using`) to release the WASM instance.

---

## 4. Evaluating JavaScript

### 4.1 Script mode

```csharp
// Returns a JSValueHandle — must be disposed
using var result = vm.Eval("1 + 2");
Console.WriteLine(result.ToNumber()); // 3

// Second argument is the filename shown in stack traces
using var r2 = vm.Eval("throw new Error('oops')", filename: "myfile.js");
```

`Eval` throws `JSException` if the JavaScript throws.

### 4.2 Module mode

Module evaluation is async inside QuickJS. The returned handle is a Promise.

```csharp
// EvalFlags.TYPE_MODULE tells QuickJS to parse as an ES module
using var promise = vm.Eval(
    "const x = 42; export default x;",
    filename: "entry.mjs",
    flags: EvalFlags.TYPE_MODULE);

// Drive the microtask queue so the module body runs
vm.ExecutePendingJobs();

// Await the settlement
var result = await vm.ResolvePromise(promise);
if (result is JSPromiseResult.Fulfilled f)
{
    Console.WriteLine(vm.Dump(f.Value)); // module namespace object
    f.Value.Dispose();
}
```

`EvalFlags.ASYNC` works similarly for top-level `await` in script mode.

### 4.3 Bytecode — compile + run

Compile once, run many times (e.g., across multiple snapshot-restored VMs):

```csharp
// Compile — does not execute
byte[] bytecode = vm.Compile(
    "function greet(name) { return 'Hello, ' + name + '!'; }",
    filename: "greet.js",
    evalFlags: EvalFlags.TYPE_GLOBAL,
    compileFlags: CompileFlags.STRIP_DEBUG  // smaller output
);

// Execute the bytecode (defines the function in global scope)
using var evalResult = vm.EvalBytecode(bytecode);

// Call the function
using var greetFn = vm.Global.GetProp("greet");
using var arg     = vm.NewString("World");
using var retVal  = vm.CallFunction(greetFn, vm.Global, arg);
Console.WriteLine(retVal.ToManagedString()); // Hello, World!
```

---

## 5. ES Module loading

Provide a `ModuleLoader` to enable `import` statements inside the VM:

```csharp
// Pre-load module sources into a dictionary (loading must be synchronous)
var modules = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["math.js"]  = "export const add = (a, b) => a + b;",
    ["utils.js"] = "export function square(n) { return n * n; }",
};

using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    ModuleLoader = new ModuleLoaderOptions
    {
        // (optional) Resolve import specifiers.
        // Called with (importingModule, rawSpecifier) — return the canonical name.
        Normalize = (baseName, specifier) =>
        {
            // Simple passthrough; real apps may resolve relative paths here
            return specifier;
        },

        // Load the source for a resolved module name.
        Load = name =>
        {
            if (modules.TryGetValue(name, out var src)) return src;
            throw new FileNotFoundException($"Module not found: {name}");
        },
    },
});

using var promise = vm.Eval(
    "import { add } from 'math.js'; export const result = add(3, 4);",
    filename: "<entry>",
    flags: EvalFlags.TYPE_MODULE);

vm.ExecutePendingJobs();

var settled = await vm.ResolvePromise(promise);
if (settled is JSPromiseResult.Fulfilled f)
{
    using (f.Value) // module namespace
    {
        using var r = f.Value.GetProp("result");
        Console.WriteLine(r.ToNumber()); // 7
    }
}
```

> **Important:** Both `Normalize` and `Load` are called synchronously from inside the WASM execution stack. They must return immediately — returning a `Task` or throwing from an `async` method will not work. Pre-fetch all module sources before calling `Eval`.

---

## 6. Value types

### 6.1 Creating values

```csharp
// Primitives
using var str  = vm.NewString("hello");
using var num  = vm.NewNumber(3.14);
using var big  = vm.NewBigInt64(9_999_999_999L);
using var sym  = vm.NewSymbol("mySymbol", isGlobal: false);
using var gsym = vm.NewSymbol("shared",   isGlobal: true); // Symbol.for("shared")

// Collections
using var obj  = vm.NewObject();
using var arr  = vm.NewArray();

// Binary data
byte[] raw  = new byte[] { 1, 2, 3 };
using var ab = vm.NewArrayBuffer(raw);    // JS ArrayBuffer
using var u8 = vm.NewUInt8Array(raw);     // JS Uint8Array

// Errors
using var err = vm.NewError("something went wrong");
using var err2 = vm.NewError(new InvalidOperationException("bad state"));

// Cached singletons (do NOT dispose these)
JSValueHandle undef = vm.UndefinedValue;
JSValueHandle nul   = vm.NullValue;
JSValueHandle t     = vm.TrueValue;
JSValueHandle f     = vm.FalseValue;
JSValueHandle glob  = vm.Global;
```

### 6.2 Reading values

```csharp
using var result = vm.Eval("({ x: 1, y: 'hi', z: true })");

using var x = result.GetProp("x");
Console.WriteLine(x.ToNumber());          // 1.0

using var y = result.GetProp("y");
Console.WriteLine(y.ToManagedString());   // hi

using var z = result.GetProp("z");
Console.WriteLine(vm.Exports.GetBool(z.Ptr) != 0); // True

// BigInt
using var bigVal = vm.Eval("9007199254740993n");
Console.WriteLine(bigVal.ToInt64());      // 9007199254740993

// ArrayBuffer / Uint8Array
using var buf = vm.Eval("new Uint8Array([10, 20, 30]).buffer");
byte[] bytes  = buf.ToByteArray();        // [10, 20, 30]

// Get the typeof as a string
Console.WriteLine(vm.TypeOf(x));          // "number"
```

### 6.3 Type checking

Every `JSValueHandle` exposes Boolean properties for fast, trap-free type testing:

```csharp
handle.IsUndefined   // typeof === 'undefined'
handle.IsNull        // value === null
handle.IsBool        // typeof === 'boolean'
handle.IsNumber      // typeof === 'number'
handle.IsString      // typeof === 'string'
handle.IsSymbol      // typeof === 'symbol'
handle.IsBigInt      // typeof === 'bigint'
handle.IsObject      // typeof === 'object' && value !== null
handle.IsArray       // Array.isArray()
handle.IsFunction    // typeof === 'function'
handle.IsError       // value instanceof Error
handle.IsPromise     // value instanceof Promise
handle.IsArrayBuffer // value instanceof ArrayBuffer
handle.IsProxy       // engine-level Proxy brand check (trap-free)
handle.IsMap         // engine-level Map brand check
handle.IsSet         // engine-level Set brand check
handle.IsDate        // engine-level Date brand check
handle.IsRegExp      // engine-level RegExp brand check
handle.IsWeakRef     // engine-level WeakRef brand check
handle.IsWeakMap     // engine-level WeakMap brand check
handle.IsWeakSet     // engine-level WeakSet brand check
handle.IsDataView    // engine-level DataView brand check
handle.IsException   // value is an internal JS exception sentinel
handle.ClassId       // raw QuickJS class ID (advanced use)
handle.PromiseState  // 0 = pending, 1 = fulfilled, 2 = rejected
```

Example — safe value extraction:

```csharp
using var val = vm.Eval("Math.random()");
if (val.IsNumber)
{
    double n = val.ToNumber();
    Console.WriteLine($"Got a number: {n}");
}
```

---

## 7. Property operations

### 7.1 Get / Set

```csharp
using var obj = vm.NewObject();
using var num = vm.NewNumber(42);
obj.SetProp("answer", num);

using var got = obj.GetProp("answer");
Console.WriteLine(got.ToNumber()); // 42

// Symbol key
using var sym = vm.NewSymbol("tag", isGlobal: false);
using var tag = vm.NewString("MyObject");
obj.SetProp(sym, tag);

using var tagGot = obj.GetProp(sym);
Console.WriteLine(tagGot.ToManagedString()); // MyObject
```

### 7.2 Define (with descriptor flags)

`DefineProp` mirrors `Object.defineProperty()` — all flags default to `false`:

```csharp
using var obj = vm.NewObject();
using var val = vm.NewString("constant");

// Read-only, non-configurable, enumerable
obj.DefineProp("PI", val, configurable: false, writable: false, enumerable: true);

// Verify it cannot be reassigned
vm.Global.SetProp("obj", obj);
vm.Eval("try { obj.PI = 'changed'; } catch(e) {}");
using var check = obj.GetProp("PI");
Console.WriteLine(check.ToManagedString()); // constant
```

### 7.3 Enumerate properties

```csharp
using var obj = vm.Eval("({ a: 1, b: 2, c: 3 })");

// Enumerable string keys only (Object.keys)
string[] keys = obj.Keys();                    // ["a", "b", "c"]

// All own string keys including non-enumerable (Object.getOwnPropertyNames)
string[] allKeys = obj.GetOwnPropertyNames();

// All own keys including symbols (Reflect.ownKeys)
IReadOnlyList<object> ownKeys = obj.GetOwnPropertyKeys();
foreach (var k in ownKeys)
{
    if (k is string s) Console.WriteLine($"string key: {s}");
    else if (k is JSValueHandle sh)
    {
        Console.WriteLine($"symbol key");
        sh.Dispose(); // caller owns symbol handles
    }
}

// Check for a specific own property
bool has = obj.HasOwnProperty("a"); // true
bool en  = obj.PropertyIsEnumerable("a"); // true

// Walk the prototype chain
using var proto = obj.GetPrototypeOf();
```

### 7.4 Property descriptors

`GetOwnPropertyDescriptor` retrieves the raw descriptor without invoking getters:

```csharp
using var obj = vm.Eval(@"
    const o = {};
    Object.defineProperty(o, 'x', { value: 99, writable: false, enumerable: true, configurable: false });
    Object.defineProperty(o, 'y', { get() { return 42; }, configurable: true });
    o
");

// Data property
using var dx = obj.GetOwnPropertyDescriptor("x");
if (dx is not null)
{
    using (dx)
    {
        Console.WriteLine(dx.Value!.ToNumber());   // 99
        Console.WriteLine(dx.Writable);             // false
        Console.WriteLine(dx.Enumerable);           // true
        Console.WriteLine(dx.Configurable);         // false
    }
}

// Accessor property
using var dy = obj.GetOwnPropertyDescriptor("y");
if (dy is not null)
{
    using (dy)
    {
        Console.WriteLine(dy.Get!.IsFunction); // true — the getter function
        Console.WriteLine(dy.Set!.IsUndefined); // true — no setter
    }
}

// Non-existent property
var noDesc = obj.GetOwnPropertyDescriptor("missing"); // null
```

---

## 8. Host functions

Expose .NET delegates as JS functions:

```csharp
// Register a host function (must have a unique name per VM instance)
using var addFn = vm.NewHostFunction("add", (thisVal, args) =>
{
    double a = args[0].ToNumber();
    double b = args[1].ToNumber();
    return vm.NewNumber(a + b);
}, argCount: 2);

vm.Global.SetProp("add", addFn);
using var result = vm.Eval("add(10, 32)");
Console.WriteLine(result.ToNumber()); // 42
```

Throw a JS exception from a host function by throwing a `JSException` or any other exception:

```csharp
using var fn = vm.NewHostFunction("mustBePositive", (_, args) =>
{
    if (args[0].ToNumber() < 0)
        throw new ArgumentException("Value must be positive");
    return args[0].Dup(); // return a duplicate of the argument
});
vm.Global.SetProp("mustBePositive", fn);

try
{
    vm.Eval("mustBePositive(-1)");
}
catch (JSException ex)
{
    Console.WriteLine(ex.Message); // Value must be positive
}
```

---

## 9. Calling JS functions from .NET

```csharp
// Call a function stored in a variable
vm.Eval("function square(n) { return n * n; }");
using var squareFn = vm.Global.GetProp("square");
using var arg      = vm.NewNumber(7);
using var result   = vm.CallFunction(squareFn, vm.Global, arg);
Console.WriteLine(result.ToNumber()); // 49

// Call a method on an object (this = the object)
using var obj  = vm.Eval("({ x: 10, double() { return this.x * 2; } })");
using var meth = obj.GetProp("double");
using var ret  = vm.CallFunction(meth, obj);  // this = obj
Console.WriteLine(ret.ToNumber()); // 20
```

---

## 10. Promises and async

### 10.1 Deferred — create a promise

`Deferred` lets .NET code control a JS promise from the outside:

```csharp
// Expose a host function that returns a promise resolved later by .NET
Deferred? pendingDeferred = null;

using var getPromise = vm.NewHostFunction("getPromise", (_, _) =>
{
    pendingDeferred = vm.NewPromise();
    return pendingDeferred.Handle; // caller owns a dup; Deferred.Handle is separate
});
vm.Global.SetProp("getPromise", getPromise);

vm.Eval("getPromise().then(v => console.log('Resolved:', v))");
vm.ExecutePendingJobs();

// ... later, resolve from .NET:
using var resolvedValue = vm.NewString("done!");
pendingDeferred!.Resolve(resolvedValue);
vm.ExecutePendingJobs(); // runs the .then callback → prints "Resolved: done!"
```

To reject instead of resolve use `pendingDeferred.Reject(errorHandle)`.

### 10.2 ResolvePromise — await settlement

`ResolvePromise` bridges a QuickJS promise to a .NET `Task<JSPromiseResult>`:

```csharp
// Module eval returns a promise
using var promise = vm.Eval(
    "new Promise(resolve => setTimeout(() => resolve(123), 0))",
    flags: EvalFlags.ASYNC);

// Start the task before pumping — it attaches .then/.catch internally
var task = vm.ResolvePromise(promise);

// Pump microtasks until the promise settles
vm.ExecutePendingJobs();

// task completes synchronously on the current thread once jobs are pumped
var settled = await task;

switch (settled)
{
    case JSPromiseResult.Fulfilled f:
        Console.WriteLine(f.Value.ToNumber()); // 123
        f.Value.Dispose();
        break;
    case JSPromiseResult.Rejected r:
        Console.WriteLine(r.Error.ToManagedString());
        r.Error.Dispose();
        break;
}
```

If the handle passed to `ResolvePromise` is not a promise, it is treated as an immediately fulfilled value.

### 10.3 Executing the microtask queue

QuickJS's job queue does **not** run automatically. You must call `ExecutePendingJobs()` after any code that enqueues microtasks (Promise reactions, `queueMicrotask`, etc.):

```csharp
vm.Eval("Promise.resolve(42).then(v => console.log(v))");
int jobsRun = vm.ExecutePendingJobs(); // prints "42", returns 1
```

---

## 11. Error handling

JavaScript exceptions surface as `JSException` (which extends `Exception`):

```csharp
try
{
    vm.Eval("undefined.property"); // TypeError
}
catch (JSException ex)
{
    Console.WriteLine(ex.Name);     // TypeError
    Console.WriteLine(ex.Message);  // Cannot read properties of undefined
    Console.WriteLine(ex.JsStack);  // JS stack trace (may be null)

    // ex.Handle is a live JSValueHandle to the QuickJS error object
    using var handle = ex.Handle;
    using var code = handle.GetProp("code"); // custom properties
}
```

Create error values manually:

```csharp
// From a message string
using var err1 = vm.NewError("something failed");

// From a .NET exception (copies message, name, stack)
using var err2 = vm.NewError(new InvalidOperationException("bad state"));

// Get the pending exception without throwing
using var exc = vm.GetException();
```

---

## 12. Value marshalling

### 12.1 Dump — JS → .NET

`Dump` converts a `JSValueHandle` into a native .NET value:

| JS type                     | .NET type                              |
| --------------------------- | -------------------------------------- |
| `undefined`                 | `QuickJs.Undefined` (singleton object) |
| `null`                      | `null`                                 |
| `boolean`                   | `bool`                                 |
| `number`                    | `double`                               |
| `string`                    | `string`                               |
| `bigint`                    | `long`                                 |
| `symbol` (global)           | `JsSymbol` record                      |
| `symbol` (local)            | `QuickJs.Undefined`                    |
| `ArrayBuffer` / typed array | `byte[]`                               |
| `Error`                     | `Exception`                            |
| `function`                  | `QuickJs.Undefined`                    |
| `Array`                     | `object?[]`                            |
| plain `object`              | `Dictionary<string, object?>`          |

```csharp
using var val = vm.Eval("({ name: 'Alice', scores: [95, 87, 100], active: true })");
var dumped = vm.Dump(val);
// dumped is Dictionary<string, object?> {
//   "name"   → "Alice",
//   "scores" → object?[] { 95.0, 87.0, 100.0 },
//   "active" → true
// }
```

Circular references in objects/arrays are handled gracefully — the already-visited object is returned instead of recursing infinitely.

### 12.2 HostToHandle — .NET → JS

`HostToHandle` converts common .NET values into `JSValueHandle`:

```csharp
using var h1 = vm.HostToHandle(null);              // JS null
using var h2 = vm.HostToHandle(QuickJs.Undefined); // JS undefined
using var h3 = vm.HostToHandle(42.0);              // JS number
using var h4 = vm.HostToHandle("hello");           // JS string
using var h5 = vm.HostToHandle(true);              // JS boolean
using var h6 = vm.HostToHandle(9999999999L);       // JS BigInt
using var h7 = vm.HostToHandle(new byte[]{1,2,3}); // JS ArrayBuffer

// Arrays and dictionaries become JS arrays/objects
using var h8  = vm.HostToHandle(new[] { 1, 2, 3 });
using var h9  = vm.HostToHandle(new Dictionary<string, object?> { ["x"] = 1 });

// POCOs become plain JS objects (public readable properties)
using var h10 = vm.HostToHandle(new { Name = "Bob", Age = 30 });
```

---

## 13. Memory and performance

### 13.1 Memory limit

Cap the JS heap to prevent run-away allocations:

```csharp
using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    MemoryLimit = 16 * 1024 * 1024, // 16 MB
});

try
{
    vm.Eval("new Array(10_000_000).fill(0)"); // may throw
}
catch (JSException ex) when (ex.Name == "InternalError")
{
    Console.WriteLine("OOM: " + ex.Message);
}
```

### 13.2 Stack size

Limit the native call stack depth (useful in sandboxes). The shipped WASM binary has a
1 MiB linker-defined stack; the maximum safe value is 512 KiB (`512 * 1024`), which
leaves headroom for native frames and stack-overflow exception handling:

```csharp
using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    MaxStackSize = 256 * 1024, // 256 KB
});
```

Set to `0` to disable the QuickJS stack guard entirely.

### 13.3 Garbage collection

QuickJS runs GC automatically via a configurable threshold. You can also trigger it manually:

```csharp
// Trigger GC now
vm.RunGc();

// Read or change the auto-GC threshold (in bytes allocated since last GC)
long threshold = vm.GcThreshold;
vm.GcThreshold = 4 * 1024 * 1024; // trigger GC every 4 MB

// Disable automatic GC (GC only runs when you call RunGc())
vm.GcThreshold = 0;
```

### 13.4 Memory usage statistics

Get a detailed breakdown of the JS runtime's memory use:

```csharp
MemoryUsage usage = vm.GetMemoryUsage();

Console.WriteLine($"Malloc size:        {usage.MallocSize:N0} bytes");
Console.WriteLine($"Malloc limit:       {usage.MallocLimit:N0} bytes");
Console.WriteLine($"Memory used:        {usage.MemoryUsedSize:N0} bytes");
Console.WriteLine($"Objects:            {usage.ObjCount}");
Console.WriteLine($"Strings:            {usage.StrCount}");
Console.WriteLine($"JS functions:       {usage.JsFuncCount}");
Console.WriteLine($"Atoms:              {usage.AtomCount}");
Console.WriteLine($"Array count:        {usage.ArrayCount}");
Console.WriteLine($"Binary objects:     {usage.BinaryObjectCount}");
```

All 26 fields mirror the QuickJS `JSMemoryUsage` struct.

---

## 14. Execution control

### 14.1 Interrupt handler

Use an interrupt handler to implement timeouts or step limits. The handler is called approximately once per bytecode instruction:

```csharp
var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    InterruptHandler = () => DateTimeOffset.UtcNow > deadline,
});

try
{
    vm.Eval("while(true){}");
}
catch (JSException ex) when (ex.Name == "InternalError")
{
    Console.WriteLine("Interrupted: " + ex.Message);
}
```

### 14.2 Intrinsics — selectively disable builtins

By default all built-in JavaScript features are available. Pass a bitmask to `Intrinsics` to create a minimal, hardened sandbox:

```csharp
// Disable eval() and Function() — the two most dangerous builtins
int intrinsics = Intrinsics.ALL & ~Intrinsics.EVAL;

using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    Intrinsics = intrinsics,
});

try
{
    vm.Eval("eval('1+1')"); // throws ReferenceError — eval is not defined
}
catch (JSException) { }
```

Available flags:

| Constant                   | Description                               |
| -------------------------- | ----------------------------------------- |
| `Intrinsics.DATE`          | `Date`                                    |
| `Intrinsics.EVAL`          | `eval()` / `Function()`                   |
| `Intrinsics.REGEXP`        | `RegExp`                                  |
| `Intrinsics.JSON`          | `JSON`                                    |
| `Intrinsics.PROXY`         | `Proxy` / `Reflect`                       |
| `Intrinsics.MAP_SET`       | `Map` / `Set` / `WeakMap` / `WeakSet`     |
| `Intrinsics.TYPED_ARRAYS`  | `ArrayBuffer` / typed arrays / `DataView` |
| `Intrinsics.PROMISE`       | `Promise` / `async`/`await`               |
| `Intrinsics.BIG_INT`       | `BigInt`                                  |
| `Intrinsics.WEAK_REF`      | `WeakRef` / `FinalizationRegistry`        |
| `Intrinsics.PERFORMANCE`   | `performance.now()`                       |
| `Intrinsics.DOM_EXCEPTION` | `DOMException`                            |
| `Intrinsics.ATOB_BTOA`     | `atob()` / `btoa()`                       |
| `Intrinsics.ALL`           | All intrinsics (default)                  |

---

## 15. Timezone configuration

Control the timezone offset used by `Date` inside the sandbox:

```csharp
// Mirror the host machine's local timezone (default)
TimezoneOffset = TimezoneOffsetOption.Host

// Fixed UTC offset (minutes west of UTC, same sign convention as Date.getTimezoneOffset())
TimezoneOffset = TimezoneOffsetOption.Fixed(-60)  // UTC+1

// Dynamic callback — return minutes west of UTC for a given epoch-second
TimezoneOffset = TimezoneOffsetOption.Callback(epochSecs =>
{
    // DST-aware custom logic, e.g. via NodaTime
    return (int)TimeZoneInfo.Local.GetUtcOffset(
        DateTimeOffset.FromUnixTimeSeconds(epochSecs)).TotalMinutes * -1;
})
```

---

## 16. Snapshots

A snapshot captures the **entire VM state** — heap, stack, pending promises, all global variables — as a plain byte array. You can restore it in a new VM instance instantly.

### 16.1 Capture and restore

```csharp
// Create and populate a VM
using var vm1 = await QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasmBytes });
vm1.Eval("var counter = 0; function inc() { return ++counter; }");
vm1.Eval("inc(); inc();"); // counter is now 2

// Capture
Snapshot snap = vm1.Snapshot();

// Restore into a fresh VM
using var vm2 = await QuickJs.RestoreAsync(snap, new QuickJsOptions { WasmBytes = wasmBytes });

// State is preserved
using var r = vm2.Eval("inc()"); // counter continues at 3
Console.WriteLine(r.ToNumber()); // 3
```

### 16.2 Binary serialisation

Serialise a snapshot to bytes (for storage or transport) and deserialise it back:

```csharp
Snapshot snap   = vm.Snapshot();
byte[]   bytes  = Snapshot.Serialize(snap);

// Persist
await File.WriteAllBytesAsync("vm.snapshot", bytes);

// Load later
byte[]   loaded  = await File.ReadAllBytesAsync("vm.snapshot");
Snapshot restored = Snapshot.Deserialize(loaded);
using var vm2 = await QuickJs.RestoreAsync(restored, options);
```

The binary format is identical to the TypeScript `serializeSnapshot` / `deserializeSnapshot` format, so snapshots are portable between the two runtimes (for snapshots without native extensions).

### 16.3 Re-registering host callbacks

Host functions (registered with `NewHostFunction`) are stored by name inside the snapshot, but the .NET delegates are **not**. After `RestoreAsync` you must re-bind the delegates using `RegisterHostCallback` — this does not create a new WASM function object:

> **Important:** If you call a guest function whose host callback has not been re-registered
> after restore (or was removed with `UnregisterHostCallback`, or was from an ephemeral
> handle that was disposed), the call **throws a `JSException`** rather than silently
> returning `undefined`. This ensures snapshot-restore bugs fail loudly.

```csharp
// Before snapshot
using var fn = vm1.NewHostFunction("log", (_, args) =>
{
    Console.WriteLine(args[0].ToManagedString());
    return vm1.UndefinedValue;
});
vm1.Global.SetProp("log", fn);
var snap = vm1.Snapshot();

// After restore
using var vm2 = await QuickJs.RestoreAsync(Snapshot.Deserialize(Snapshot.Serialize(snap)), options);

// Re-register the delegate — the JS function object already exists in the restored heap
vm2.RegisterHostCallback("log", (_, args) =>
{
    Console.WriteLine("Restored: " + args[0].ToManagedString());
    return vm2.UndefinedValue;
});

vm2.Eval("log('hello from restored VM')"); // prints "Restored: hello from restored VM"
```

---

## 17. Version information

```csharp
IReadOnlyDictionary<string, string> versions = vm.Versions;
Console.WriteLine(versions["quickjs"]); // e.g. "0.8.0"
```

---

## 18. WasiShim — customising I/O

By default `console.log` inside QuickJS writes to `stdout` and `console.error` to `stderr`. To intercept or redirect this output, subclass `WasiShim` and override `FdWrite`:

```csharp
public sealed class CapturingWasiShim : WasiShim
{
    private readonly List<string> _output = new();
    public IReadOnlyList<string> Output => _output;

    public override int FdWrite(
        Wasmtime.Memory memory, Wasmtime.Caller caller,
        int fd, int iovsPtr, int iovsLen, int nwrittenPtr)
    {
        // Capture stdout only; delegate stderr to base
        if (fd == 1)
        {
            // Read the iovec structs and accumulate text
            var span = memory.GetSpan<byte>(0);
            int total = 0;
            for (int i = 0; i < iovsLen; i++)
            {
                int offset = iovsPtr + i * 8;
                int ptr = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
                _output.Add(System.Text.Encoding.UTF8.GetString(span.Slice(ptr, len)));
                total += len;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(nwrittenPtr, 4), total);
            return 0; // ERRNO_SUCCESS
        }
        return base.FdWrite(memory, caller, fd, iovsPtr, iovsLen, nwrittenPtr);
    }
}
```

Pass a custom shim via `QuickJsOptions.WasiShim` — **wait**, the public `CreateAsync` factory does not expose this yet; `WasiShim` is currently an internal detail passed to `new WasiShim()`. To override it you can subclass `WasiShim` and override the virtual methods; the library instantiates `new WasiShim()` internally. To fully control it you would need to fork or extend the `QuickJs` class. Subclassing `WasiShim` and overriding virtual methods is the current extension point.

---

## 19. Full API reference

### `QuickJs` (implements `IDisposable`)

| Member                                                | Description                                                |
| ----------------------------------------------------- | ---------------------------------------------------------- |
| `CreateAsync(options?)`                               | Factory — create a fresh VM                                |
| `RestoreAsync(snapshot, options?)`                    | Factory — restore from a snapshot                          |
| `Eval(code, filename?, flags?)`                       | Evaluate JS source, returns `JSValueHandle`                |
| `Compile(code, filename?, evalFlags?, compileFlags?)` | Compile to `byte[]` bytecode                               |
| `EvalBytecode(bytecode)`                              | Execute compiled bytecode                                  |
| `ExecutePendingJobs()`                                | Run the microtask queue; returns job count                 |
| `ResolvePromise(promise)`                             | Await a QuickJS promise as `Task<JSPromiseResult>`         |
| `CallFunction(func, thisVal, args...)`                | Call a JS function; throws on exception                    |
| `NewString(value)`                                    | Create a JS string                                         |
| `NewNumber(value)`                                    | Create a JS number                                         |
| `NewBigInt64(value)`                                  | Create a JS BigInt from a `long`                           |
| `NewSymbol(description, isGlobal)`                    | Create a JS symbol                                         |
| `NewObject()`                                         | Create an empty JS object                                  |
| `NewArray()`                                          | Create an empty JS array                                   |
| `NewArrayBuffer(data)`                                | Create a JS `ArrayBuffer` (copies bytes)                   |
| `NewUInt8Array(data)`                                 | Create a JS `Uint8Array` (copies bytes)                    |
| `NewError(message)`                                   | Create a JS `Error` from a string                          |
| `NewError(exception)`                                 | Create a JS `Error` from a .NET exception                  |
| `NewHostFunction(name, callback, argCount?)`          | Register a host function callable from JS                  |
| `RegisterHostCallback(name, callback)`                | Re-bind a host callback after restore (no duplicate check) |
| `NewPromise()`                                        | Create a `Deferred` (handle + resolve + reject)            |
| `GetException()`                                      | Retrieve the pending JS exception                          |
| `GetPromiseResult(promise)`                           | Get the settled value of a promise                         |
| `Dump(handle)`                                        | Convert JS value → .NET object                             |
| `HostToHandle(value)`                                 | Convert .NET object → `JSValueHandle`                      |
| `Snapshot()`                                          | Capture VM state as `Snapshot`                             |
| `RunGc()`                                             | Trigger GC now                                             |
| `GetMemoryUsage()`                                    | Detailed memory statistics (`MemoryUsage`)                 |
| `TypeOf(handle)`                                      | Returns the typeof string                                  |
| `Global`                                              | The global object (singleton, do not dispose)              |
| `UndefinedValue / NullValue / TrueValue / FalseValue` | Cached singletons (do not dispose)                         |
| `GcThreshold`                                         | Get/set the auto-GC threshold in bytes                     |
| `Versions`                                            | `{ "quickjs": "x.y.z" }`                                   |
| `IsDisposed`                                          | Whether the VM has been disposed                           |
| `Dispose()`                                           | Release all WASM resources                                 |

### `JSValueHandle` (implements `IDisposable`)

| Member                                                    | Description                                          |
| --------------------------------------------------------- | ---------------------------------------------------- |
| `GetProp(name)`                                           | Get property by string name                          |
| `GetProp(key)`                                            | Get property by symbol key                           |
| `SetProp(name, value)`                                    | Set property by string name                          |
| `SetProp(key, value)`                                     | Set property by symbol key                           |
| `DefineProp(name, value, …)`                              | Define property with explicit descriptor flags       |
| `Keys()`                                                  | Enumerable own string property names                 |
| `GetOwnPropertyNames()`                                   | All own string property names (incl. non-enumerable) |
| `GetOwnPropertyKeys()`                                    | All own keys including symbols (`Reflect.ownKeys()`) |
| `GetOwnPropertyDescriptor(key)`                           | Property descriptor without invoking getters         |
| `HasOwnProperty(name)`                                    | `Object.prototype.hasOwnProperty`                    |
| `PropertyIsEnumerable(name)`                              | `Object.prototype.propertyIsEnumerable`              |
| `GetPrototypeOf()`                                        | Get the prototype                                    |
| `GetProxyTarget()`                                        | Get `[[ProxyTarget]]` (trap-free)                    |
| `GetProxyHandler()`                                       | Get `[[ProxyHandler]]` (trap-free)                   |
| `Dup()`                                                   | Duplicate (increment refcount)                       |
| `ToNumber()`                                              | Extract as `double`                                  |
| `ToInt64()`                                               | Extract BigInt as `long`                             |
| `ToManagedString()`                                       | Extract as `string`                                  |
| `ToByteArray()`                                           | Extract `ArrayBuffer` / typed array as `byte[]`      |
| `IsUndefined / IsNull / IsBool / IsNumber / IsString / …` | Type checks                                          |
| `PromiseState`                                            | 0 = pending, 1 = fulfilled, 2 = rejected             |
| `ClassId`                                                 | Raw QuickJS class ID                                 |
| `Dispose()`                                               | Free the JS value                                    |

### `QuickJsOptions`

| Property               | Type                    | Default          | Description                      |
| ---------------------- | ----------------------- | ---------------- | -------------------------------- |
| `WasmBytes`            | `byte[]?`               | embedded binary  | Raw WASM bytes                   |
| `MemoryLimit`          | `long?`                 | unlimited        | Max JS heap in bytes             |
| `MaxStackSize`         | `long?`                 | engine default   | Max call stack in bytes          |
| `InterruptHandler`     | `Func<bool>?`           | none             | Return `true` to interrupt       |
| `OnUnhandledRejection` | `Action<…>?`            | none             | Unhandled promise rejection hook |
| `ModuleLoader`         | `ModuleLoaderOptions?`  | none             | ES module loader                 |
| `TimezoneOffset`       | `TimezoneOffsetOption?` | `Host`           | Timezone for `Date`              |
| `Intrinsics`           | `int?`                  | `Intrinsics.ALL` | Bitmask of enabled intrinsics    |

### Constant classes

| Class          | Purpose                                                     |
| -------------- | ----------------------------------------------------------- |
| `Intrinsics`   | Bitmask flags for `QuickJsOptions.Intrinsics`               |
| `EvalFlags`    | Flags for `QuickJs.Eval` (e.g., `TYPE_MODULE`, `ASYNC`)     |
| `CompileFlags` | Flags for `QuickJs.Compile` (`STRIP_SOURCE`, `STRIP_DEBUG`) |

---

## 20. Publish pipeline

The repository ships a GitHub Actions workflow at `.github/workflows/dotnet-publish.yml` that:

1. **On every push to `main` and every pull request:**
   - Checks out the repo with submodules (needed for `quickjs-ng`).
   - Installs **wasi-sdk** via `make setup`.
   - Builds **`quickjs.wasm`** with `make quickjs.wasm`.
   - Stages the WASM file at `src/dotnet/QuickJsWasi/Resources/quickjs.wasm` so it is embedded as a managed resource.
   - Builds and tests the .NET solution.
   - Packs a `.nupkg` and uploads it as a workflow artifact.

2. **On a tag of the form `dotnet/v1.2.3`** (additionally):
   - Downloads the artifact packed in step 1.
   - Pushes the `.nupkg` and `.snupkg` (symbols) to NuGet.org.

### Releasing a new version

```sh
# 1. Update the VersionPrefix in the .csproj, commit, push to main
# 2. Tag the release
git tag dotnet/v1.2.3
git push origin dotnet/v1.2.3
# → GitHub Actions automatically publishes to NuGet
```

### Required secret

Add `NUGET_API_KEY` in **Settings → Secrets → Actions** of the repository. This is a NuGet.org API key with the `Push` scope for the `QuickJsWasi` package.

---

## 21. Build and test locally

```sh
# Prerequisites: .NET 10 SDK, wasi-sdk (installed via make setup)

# 1. Build the WASM
make setup          # downloads wasi-sdk if not present
make quickjs.wasm   # builds the WASI reactor

# 2. Stage for embedding
mkdir -p src/dotnet/QuickJsWasi/Resources
cp quickjs.wasm src/dotnet/QuickJsWasi/Resources/quickjs.wasm

# 3. Build the .NET solution
dotnet build src/dotnet/QuickJsWasi.slnx

# 4. Run tests
dotnet test src/dotnet/QuickJsWasi.slnx

# 5. Pack the NuGet package
dotnet pack src/dotnet/QuickJsWasi/QuickJsWasi.csproj -c Release -o nupkg/
```

Tests skip gracefully when `quickjs.wasm` is absent (the binary is git-ignored), so the CI signal is reliable without a local WASM build.

---

## API deviations from the TypeScript implementation

| TypeScript                                       | .NET                                         | Notes                                                                                                                 |
| ------------------------------------------------ | -------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `undefined` host value                           | `QuickJs.Undefined` sentinel                 | JS `undefined` → C# `QuickJs.Undefined`, not C# `null`. C# `null` → JS `null`.                                        |
| `Symbol`                                         | `JsSymbol` record                            | Global symbols marshal as `new JsSymbol(description, isGlobal: true)`; local symbols → `QuickJs.Undefined`.           |
| `bigint`                                         | `long` / `BigInteger`                        | `Dump` returns `long`; `HostToHandle` accepts `long` or `BigInteger`.                                                 |
| `async create()`                                 | `Task<QuickJs> CreateAsync()`                | Returns a completed task; async signature kept for compatibility.                                                     |
| `resolvePromise()` → `Promise<{value}\|{error}>` | `ResolvePromise()` → `Task<JSPromiseResult>` | `JSPromiseResult` is a discriminated union.                                                                           |
| `versions` includes npm package version          | `Versions` includes engine version only      | No equivalent of the npm package version in .NET.                                                                     |
| `hostToHandle(Promise)` wraps host promises      | Not supported                                | .NET `Task` cannot be synchronously awaited inside the WASM call stack; use `NewPromise()` + `Deferred`.              |
| Extensions                                       | Not supported                                | Dynamic WASM module composition via Wasmtime .NET SDK is not available. Snapshots with extensions cannot be restored. |

## Packages used

- `Wasmtime` 44.0.0 (pinned, not floating)
- `xunit` 2.6.6 (tests only)
- `Microsoft.NET.Test.Sdk` 17.8.0 (tests only)

# Credits

This project is a port and rewrite from [vercelabs/quickjs-wasi](https://github.com/vercel-labs/quickjs-wasi)
