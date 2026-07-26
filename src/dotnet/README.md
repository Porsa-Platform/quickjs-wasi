# QuickJsWasi — .NET host

`src/dotnet/` is a .NET 10 host-side port of `quickjs-wasi`, providing a C# API that drives the
same precompiled `quickjs.wasm` binary used by the TypeScript implementation. All existing source
trees (`src/`, `c/`, `extensions/`, `test/`, `bench/`, the `quickjs-ng` submodule) are untouched.

## Prerequisites

- .NET 10 SDK
- `quickjs.wasm` built from the repo root (see _WASM provisioning_ below)

## WASM Provisioning

The `*.wasm` file is excluded from git. After building the WASM binary from the repo root:

```sh
make          # builds quickjs.wasm in the project root
cp quickjs.wasm src/dotnet/QuickJsWasi/Resources/quickjs.wasm
```

When `Resources/quickjs.wasm` is present it is compiled in as an `EmbeddedResource` in the
library DLL, so applications do not need to ship the file separately.

Alternatively, pass raw bytes at runtime via `QuickJsOptions.WasmBytes`:

```csharp
var options = new QuickJsOptions { WasmBytes = await File.ReadAllBytesAsync("quickjs.wasm") };
using var vm = await QuickJs.CreateAsync(options);
```

## Build

```sh
dotnet build src/dotnet/QuickJsWasi.sln
```

## Test

```sh
dotnet test src/dotnet/QuickJsWasi.sln
```

Tests skip gracefully when `quickjs.wasm` is absent (the repo-wide `.gitignore` excludes `*.wasm`),
so the CI green/red signal is reliable without the binary.

## Quick start

```csharp
using QuickJsWasi;

// Create a VM
using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    WasmBytes = File.ReadAllBytes("quickjs.wasm"),
    MemoryLimit = 32 * 1024 * 1024, // 32 MB
});

// Evaluate JavaScript
using var result = vm.Eval("1 + 2");
Console.WriteLine(result.ToNumber()); // 3

// Register a host callback
using var fn = vm.NewHostFunction("add", (thisVal, args) =>
{
    var a = args[0].ToNumber();
    var b = args[1].ToNumber();
    return vm.NewNumber(a + b);
});
vm.Global.SetProp("add", fn);

using var sum = vm.Eval("add(10, 32)");
Console.WriteLine(sum.ToNumber()); // 42

// Execute pending microtasks
vm.Eval("Promise.resolve(1).then(x => console.log('Resolved:', x))");
vm.ExecutePendingJobs();

// Snapshot and restore
var snapshot = vm.Snapshot();
var bytes = Snapshot.Serialize(snapshot);
var loaded = Snapshot.Deserialize(bytes);
using var vm2 = await QuickJs.RestoreAsync(loaded, new QuickJsOptions
{
    WasmBytes = File.ReadAllBytes("quickjs.wasm"),
});
```

## ES Module loading

Provide a `ModuleLoader` in `QuickJsOptions` to enable ES module `import` statements:

```csharp
var modules = new Dictionary<string, string>
{
    ["./math.js"] = "export const add = (a, b) => a + b;",
};

using var vm = await QuickJs.CreateAsync(new QuickJsOptions
{
    WasmBytes = File.ReadAllBytes("quickjs.wasm"),
    ModuleLoader = new ModuleLoaderOptions
    {
        Load = name => modules[name],
        // Optional: resolve specifiers relative to the importing module
        Normalize = (baseName, specifier) => specifier,
    },
});

using var promise = vm.Eval("import('./math.js').then(m => m.add(1, 2))", flags: EvalFlags.TYPE_MODULE);
vm.ExecutePendingJobs();
var result = await vm.ResolvePromise(promise);
// result is JSPromiseResult.Fulfilled with value 3
```

Both `Load` and `Normalize` are **synchronous** — they must return their result immediately.
Pre-fetch all module sources before evaluating and serve them from a cache.

## API overview

### `QuickJs` (implements `IDisposable`)

| Method / Property | Description |
|---|---|
| `CreateAsync(options?)` | Factory — create a fresh VM |
| `RestoreAsync(snapshot, options?)` | Factory — restore a VM from a snapshot |
| `Eval(code, filename?, flags?)` | Evaluate JS, returns `JSValueHandle` |
| `Compile(code, ...)` | Compile to bytecode (`byte[]`) |
| `EvalBytecode(bytecode)` | Execute compiled bytecode |
| `ExecutePendingJobs()` | Run all pending microtasks |
| `ResolvePromise(promise)` | Await a QuickJS promise as a host `Task<JSPromiseResult>` |
| `CallFunction(func, this, args...)` | Call a JS function |
| `NewString / NewNumber / NewObject / NewArray / NewBigInt64 / ...` | Value factories |
| `NewHostFunction(name, callback)` | Register a host function callable from JS |
| `RegisterHostCallback(name, fn)` | Re-register a callback after snapshot restore |
| `NewPromise()` | Create a `Deferred` |
| `Dump(handle)` | Convert JS value to a .NET object |
| `HostToHandle(value)` | Convert .NET object to a JS value handle |
| `GetMemoryUsage()` | Get detailed runtime memory statistics |
| `Snapshot()` | Capture VM state |
| `RunGc()` | Trigger garbage collection |
| `Versions` | Dictionary with `"quickjs"` engine version |
| `Global / UndefinedValue / NullValue / TrueValue / FalseValue` | Cached singleton handles (do not dispose) |
| `GcThreshold` | Get/set the GC threshold in bytes |
| `Dispose()` | Release all WASM resources |

### `JSValueHandle` (implements `IDisposable`)

Wraps a `JSValue*` pointer inside the WASM linear memory. Disposing frees the heap-allocated value.

**Important:** The cached properties `vm.Global`, `vm.UndefinedValue`, `vm.NullValue`, `vm.TrueValue`,
and `vm.FalseValue` are singletons — `Dispose()` is a no-op on them. Do not dispose them manually.

| Method / Property | Description |
|---|---|
| `GetProp(name)` / `GetProp(key)` | Get a property by string name or symbol key |
| `SetProp(name, value)` / `SetProp(key, value)` | Set a property |
| `DefineProp(name, value, ...)` | Define a property with explicit descriptor flags |
| `Keys()` | Enumerable own string property names (like `Object.keys()`) |
| `GetOwnPropertyNames()` | All own string property names including non-enumerable |
| `GetOwnPropertyKeys()` | All own keys including symbols (`Reflect.ownKeys()`) |
| `GetOwnPropertyDescriptor(key)` | Property descriptor without invoking getters |
| `HasOwnProperty(name)` | Check for own property |
| `PropertyIsEnumerable(name)` | Check if property is enumerable |
| `GetPrototypeOf()` | Get the prototype |
| `GetProxyTarget()` / `GetProxyHandler()` | Inspect a Proxy without firing traps |
| `ToNumber()` / `ToInt64()` / `ToManagedString()` / `ToByteArray()` | Extract typed values |
| `Dup()` | Duplicate (increment refcount) |
| `IsXxx` properties | Type checks: `IsUndefined`, `IsNull`, `IsBool`, `IsNumber`, ... |
| `PromiseState` | Promise state: 0 = pending, 1 = fulfilled, 2 = rejected |
| `Dispose()` | Free the heap-allocated value |

### `QuickJsOptions`

| Property | Type | Description |
|---|---|---|
| `WasmBytes` | `byte[]?` | Raw WASM binary (required unless embedded resource present) |
| `MemoryLimit` | `long?` | Max JS heap in bytes |
| `MaxStackSize` | `long?` | Max JS call stack size in bytes |
| `InterruptHandler` | `Func<bool>?` | Called during execution; return `true` to interrupt |
| `OnUnhandledRejection` | `Action<JSValueHandle, JSValueHandle, bool>?` | Promise rejection hook |
| `ModuleLoader` | `ModuleLoaderOptions?` | ES module loader (normalize + load callbacks) |
| `TimezoneOffset` | `TimezoneOffsetOption?` | `TimezoneOffsetOption.Host`, `.Fixed(minutes)`, `.Callback(fn)` |
| `Intrinsics` | `int?` | Bitmask of `Intrinsics.*` constants (default: all) |

### `ModuleLoaderOptions`

| Property | Type | Description |
|---|---|---|
| `Load` | `Func<string, string>` | Load module source by name (required) |
| `Normalize` | `Func<string, string, string>?` | Resolve a specifier relative to the importing module (optional) |

### Constant classes

| Class | Constants | Description |
|---|---|---|
| `Intrinsics` | `DATE`, `EVAL`, `REGEXP`, `JSON`, `PROXY`, `MAP_SET`, `TYPED_ARRAYS`, `PROMISE`, `BIG_INT`, `WEAK_REF`, `PERFORMANCE`, `DOM_EXCEPTION`, `ATOB_BTOA`, `ALL` | Bitmask flags for `QuickJsOptions.Intrinsics` |
| `EvalFlags` | `TYPE_GLOBAL`, `TYPE_MODULE`, `STRICT`, `COMPILE_ONLY`, `BACKTRACE_BARRIER`, `ASYNC` | Flags for `QuickJs.Eval` |
| `CompileFlags` | `STRIP_SOURCE`, `STRIP_DEBUG` | Flags for `QuickJs.Compile` |

### `JSPromiseResult`

Returned by `QuickJs.ResolvePromise()`. A discriminated union:
- `JSPromiseResult.Fulfilled(JSValueHandle Value)` — the promise fulfilled with `Value`
- `JSPromiseResult.Rejected(JSValueHandle Error)` — the promise rejected with `Error`

The caller owns the handle inside the result and must dispose it.

### `Snapshot` / `Snapshot.Serialize` / `Snapshot.Deserialize`

Binary format is compatible with the TypeScript `serializeSnapshot` / `deserializeSnapshot` format
(magic `0x514A5353`, version 2). Extension metadata is included in the header; re-registering host
callbacks after restore is required.

## API deviations from the TypeScript implementation

| TypeScript | .NET | Notes |
|---|---|---|
| `undefined` host value | `QuickJs.Undefined` sentinel object | JS `undefined` → C# `QuickJs.Undefined` (a singleton `object`), not C# `null`. C# `null` maps to JS `null`. |
| `Symbol` | `JsSymbol` record | Global symbols marshal as `new JsSymbol(description, isGlobal: true)`; local symbols marshal as `QuickJs.Undefined`. |
| `bigint` | `long` / `BigInteger` | `Dump` returns `long`. `HostToHandle` accepts `long` or `BigInteger`. |
| `async create()` | `Task<QuickJs> CreateAsync()` | Returns a completed task (no async IO) but keeps the async signature for forward compatibility. |
| `HostFunction` | `delegate JSValueHandle HostFunction(JSValueHandle thisValue, IReadOnlyList<JSValueHandle> args)` | |
| `Deferred.settled` | `Deferred.Settled` (`Task`) | Behaves identically. |
| `resolvePromise()` returns `Promise<{value} \| {error}>` | `ResolvePromise()` returns `Task<JSPromiseResult>` | `JSPromiseResult` is a discriminated union of `Fulfilled` and `Rejected`. |
| `versions` returns `Record<string, string>` with package version | `Versions` returns `IReadOnlyDictionary<string, string>` with engine version only | The npm package version has no .NET equivalent; only `"quickjs"` key is populated. |
| `hostToHandle(Promise)` wraps host promises | Not supported | .NET `Task` cannot be synchronously awaited inside the WASM call stack. Use `NewPromise()` and `Deferred` instead. |
| Extensions | Not supported | Dynamic WASM module composition is not available via the Wasmtime .NET SDK. Snapshots with extensions cannot be restored. |

## Snapshot binary compatibility

The .NET and TypeScript snapshot formats are byte-for-byte identical for snapshots without native
extensions (empty extension list). They are therefore interchangeable between runtimes for the
core VM. Extension metadata compatibility is not guaranteed for cross-runtime restore of snapshots
that include native extensions.

## Packages used

- `Wasmtime` 44.0.0 (pinned, not floating)
- `xunit` 2.6.6 (tests only)
- `Microsoft.NET.Test.Sdk` 17.8.0 (tests only)
