# QuickJsWasi — .NET host

`src/dotnet/` is a .NET 8 host-side port of `quickjs-wasi`, providing a C# API that drives the
same precompiled `quickjs.wasm` binary used by the TypeScript implementation. All existing source
trees (`src/`, `c/`, `extensions/`, `test/`, `bench/`, the `quickjs-ng` submodule) are untouched.

## Prerequisites

- .NET 8 SDK
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
| `CallFunction(func, this, args...)` | Call a JS function |
| `NewString / NewNumber / NewObject / NewArray / NewBigInt64 / ...` | Value factories |
| `NewHostFunction(name, callback)` | Register a host function callable from JS |
| `NewPromise()` | Create a `Deferred` |
| `Dump(handle)` | Convert JS value to a .NET object |
| `HostToHandle(value)` | Convert .NET object to a JS value handle |
| `Snapshot()` | Capture VM state |
| `RunGc()` | Trigger garbage collection |
| `RegisterHostCallback(name, fn)` | Re-register a callback after snapshot restore |
| `Global / UndefinedValue / NullValue / TrueValue / FalseValue` | Cached singleton handles (do not dispose) |
| `Dispose()` | Release all WASM resources |

### `JSValueHandle` (implements `IDisposable`)

Wraps a `JSValue*` pointer inside the WASM linear memory. Disposing frees the heap-allocated value.

**Important:** The cached properties `vm.Global`, `vm.UndefinedValue`, `vm.NullValue`, `vm.TrueValue`,
and `vm.FalseValue` are singletons — `Dispose()` is a no-op on them. Do not dispose them manually.

### `QuickJsOptions`

| Property | Type | Description |
|---|---|---|
| `WasmBytes` | `byte[]?` | Raw WASM binary (required unless embedded resource present) |
| `MemoryLimit` | `long?` | Max JS heap in bytes |
| `InterruptHandler` | `Func<bool>?` | Called during execution; return `true` to interrupt |
| `OnUnhandledRejection` | `Action<JSValueHandle, JSValueHandle, bool>?` | Promise rejection hook |
| `TimezoneOffset` | `TimezoneOffsetOption?` | `TimezoneOffsetOption.Host`, `.Fixed(minutes)`, `.Callback(fn)` |
| `Intrinsics` | `int?` | Bitmask of intrinsics (default: all) |

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

## Snapshot binary compatibility

The .NET and TypeScript snapshot formats are byte-for-byte identical for snapshots without native
extensions (empty extension list). They are therefore interchangeable between runtimes for the
core VM. Extension metadata compatibility is not guaranteed for cross-runtime restore of snapshots
that include native extensions.

## Packages used

- `Wasmtime` 19.0.0 (pinned, not floating)
- `xunit` 2.6.6 (tests only)
- `Microsoft.NET.Test.Sdk` 17.8.0 (tests only)
