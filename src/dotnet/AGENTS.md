# AGENTS.md — .NET host for quickjs-wasi

This folder contains the .NET 10 host library (`QuickJsWasi`) and its test suite.

## Repository layout

```
src/dotnet/
  QuickJsWasi/            # Library project
    Interop/              # Low-level WASM interop (HostCallDispatcher, NativeExports, WasmMemoryAccessor)
    QuickJs.cs            # Main VM class — public API
    JSValueHandle.cs      # Handle wrapper for JS values
    QuickJsOptions.cs     # VM creation options
    ...
  QuickJsWasi.Tests/      # xUnit test suite
    HandleLifetimeTests.cs  # WithScope, NewEphemeralFunction, UnregisterHostCallback, Disposed, ResolvePromise
    IntrospectionTests.cs   # Construct, Identity, ToBoolean
    EvalTests.cs
    HostFunctionTests.cs
    ...
  Sample.cs               # Runnable example covering every API feature
  README.md               # .NET-specific API documentation
```

## Build and test

```sh
# From repo root — builds the WASM binary first (requires wasi-sdk)
make

# .NET only (WASM binary must already exist at src/dotnet/QuickJsWasi/Resources/quickjs.wasm)
cd src/dotnet
dotnet test QuickJsWasi.Tests
```

## Key design decisions

### HostCallDispatcher

When a guest function backed by a host callback is called, `HostCallDispatcher.Invoke`
looks up the callback by name. If the callback is **not found** (unregistered, ephemeral
handle disposed, or never re-registered after snapshot restore) it **throws a guest-side
error** so bugs fail loud rather than silently returning `undefined`.

### NativeExports

All WASM exports are wrapped in `NativeExports.cs`. When the upstream C interface
(`c/interface.c`) gains a new `qjs_*` export, add a corresponding line here before
using it from `QuickJs.cs`.

### New exports (synced from upstream `eee34b81`)

The following exports were added in the upstream sync and are wired in `NativeExports.cs`:

| Export | Purpose |
|---|---|
| `qjs_get_class_name` | Engine-level class name (trap-free, no Symbol.toStringTag) |
| `qjs_get_string_len` | Length-aware string read (survives embedded NUL and lone surrogates) |
| `qjs_has_own_property_value` | `hasOwnProperty` using a `JSValue` key instead of a string |
| `qjs_property_is_enumerable_value` | `propertyIsEnumerable` using a `JSValue` key |
| `qjs_promise_then` | Engine-level `JS_PromiseThen` (bypasses `Promise.prototype.then`) |
| `qjs_promise_mark_as_handled` | Mark a promise as handled to suppress unhandled rejection events |

### Memory limits

Since upstream commit `eee34b81`, the memory limit is enforced by custom
`malloc`/`realloc`/`free` functions with a soft-limit + OOM headroom, rather than the
QuickJS engine-level `JS_SetMemoryLimit`. This ensures the "out of memory" `InternalError`
object can always be allocated. The `MemoryLimit` option in `QuickJsOptions` continues to
work as documented.

### Test file organisation

Test files mirror the TypeScript test split from upstream PR #2:

| C# file | Mirrors TS file |
|---|---|
| `HandleLifetimeTests.cs` | `test/handle-lifetime.test.ts` |
| `IntrospectionTests.cs` | `test/introspection.test.ts` |
