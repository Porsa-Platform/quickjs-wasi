# TypeScript → .NET parity

The TypeScript library (`src/index.ts`, `src/wasi-shim.ts`, `src/extensions.ts`, `test/*.test.ts`) is the
reference implementation. This document tracks how the .NET host (`src/dotnet/QuickJsWasi`) matches it.

- **Reference:** upstream `quickjs-wasi` 3.6.3 (quickjs-ng v0.17.0). **.NET package:** 1.5.0.
- **Status keys:** ✅ ported · ⚠️ ported, behaves differently · ❌ missing · ➕ .NET-only.
- When you port an upstream change, update the matching row and the [upstream sync log](#upstream-sync-log).

## Open gaps

| # | Gap | TS | .NET | Status |
|---|---|---|---|---|
| 7 | Extensions and WASI overrides | `extensions` option, `loadExtension`/`restoreExtensions`; `wasi` option | No extension loading (dylink parsing, memory/table bases, `qjs_ext_*_init`). `RestoreAsync` rejects snapshots that have extensions. `WasiShim` is hard-coded (subclassing isn't reachable from options). Needs its own port. | ❌ |
| 11 | `getGlobal()` … `getFalse()` methods | Methods alongside the cached getters | Only the `Global` / `UndefinedValue` / … properties | ❌ by design: the properties cover it |

Gap numbers are kept stable so commits and issues can refer to them.

### Resolved in 1.4.0

Tests are in `QuickJsWasi.Tests/ParityTests.cs` unless noted.

| # | Gap | Fix |
|---|---|---|
| 1 | `TimezoneOffsetOption.Host` was always UTC | Uses `TimeZoneInfo.Local.GetUtcOffset(...)`, so it's DST-aware. **Behavior change:** the default VM now uses the host timezone, as in TS. Removed the `actual == 0` escape from `Timezone_HostOffset`. |
| 2 | `ResolvePromise` subscribed via guest-visible `Promise.prototype.then` | Subscribes via `qjs_promise_then` (`PromiseThenRaw`), so patched `then` / `Symbol.species` are never consulted. Ported test: "cannot be intercepted by patching". |
| 3 | Missing exception checks | `GetProxyTarget` / `GetProxyHandler` throw `JSException`; `Keys()` returns `[]`; `Dump` of an object whose enumeration throws returns an empty dictionary. |
| 4 | `HasOwnProperty` / `PropertyIsEnumerable` read errors as `true` | Compare `== 1`, so an error reads as `false`. |
| 5 | `Deferred.Settled` completed when the host called `Resolve`/`Reject` | Lazily subscribes to the guest promise (trap-free) and completes when it actually settles. |
| 6 | Missing APIs | Added `ClassName`, `ConstructorName`, `TypeOf` (handle property), `Consume`, `MarkPromiseHandled`, `DefineProp(JSValueHandle key, …)`, `QuickJs.MAX_STACK_SIZE` with `MaxStackSize` validation (`ArgumentOutOfRangeException`). |
| 8 | `ToByteArray` left a pending TypeError on typed arrays | Checks `IsArrayBuffer` first; an empty `ArrayBuffer` returns `[]`. |
| 9 | Inconsistent disposed-VM guarding | Public `QuickJs` members throw `ObjectDisposedException` after `Dispose()`. |
| 10 | `Versions` had no package key and wasn't cached | Adds `quickjs-wasi` (the .NET package version) and caches the dictionary. |

**Intentional .NET differences (no action needed):**
- Errors use idiomatic .NET exception types where TS throws a plain `Error`. For example, `Compile` and `ExecutePendingJobs` throw `JSException`, and invalid arguments throw `InvalidOperationException` / `ArgumentOutOfRangeException`.
- `SetProp` and `DefineProp` throw `JSException` on failure, where TS silently ignores the result.
- A host callback that throws a `JSException` rethrows the original guest value. TS re-wraps it as a new Error.
- `NewError(Exception)` sets `name` to the .NET type name.
- The compiled module and its `Engine` are shared by every VM of the process (1.5.0, `CompiledModules`). TS does the same only when the caller passes a `WebAssembly.Module`. Isolation is unchanged: each VM still has its own `Store`, linear memory, imports and limits. Compiled modules are never evicted.

## Public API

### `QuickJS` → `QuickJs`

| TS | .NET | Status |
|---|---|---|
| `create` / `restore` | `CreateAsync` / `RestoreAsync` | ⚠️ `QuickJsOptions` only; falls back to the embedded wasm. Restore rejects extensions. Reuses the process-wide compiled module (see `wasm`). |
| `serializeSnapshot` / `deserializeSnapshot` | `Snapshot.Serialize` / `Snapshot.Deserialize` | ✅ Same binary format (v1/v2) |
| `versions` | `Versions` | ✅ `quickjs-wasi` + `quickjs`, cached (no extension versions, Gap 7) |
| `global` / `undefined` / `null` / `true` / `false` | `Global` / `UndefinedValue` / `NullValue` / `TrueValue` / `FalseValue` | ✅ (created eagerly) |
| `getGlobal()` … `getFalse()` | — | ❌ by design (Gap 11): use the properties |
| `evalCode` / `compile` / `evalBytecode` | `Eval` / `Compile` / `EvalBytecode` | ✅ |
| `executePendingJobs` | `ExecutePendingJobs` | ⚠️ Also delivers completed async host tasks |
| — | `ExecutePendingJobsAsync(CancellationToken)` | ➕ Awaits in-flight async host tasks until drained |
| `runGC` / `gcThreshold` / `getMemoryUsage` | `RunGc` / `GcThreshold` / `GetMemoryUsage` | ✅ (no disposed check) |
| `newString` / `newNumber` / `newObject` / `newArray` | same | ✅ |
| `newBigInt(bigint)` | `NewBigInt64(long)` | ✅ |
| `newSymbolFor` | `NewSymbol(desc, isGlobal)` | ✅ ➕ (local symbols too) |
| `newArrayBuffer` / `newUint8Array` | `NewArrayBuffer` / `NewUInt8Array` | ✅ |
| `newFunction` | `NewHostFunction` (+ async overloads ➕) | ✅ |
| `newEphemeralFunction` / `registerHostCallback` / `unregisterHostCallback` | same (+ async overloads ➕) | ✅ |
| `withScope` | `WithScope<T>` | ✅ |
| `exportHandle` / `importHandle` | same | ✅ |
| `newPromise` → `Deferred` | `NewPromise` → `Deferred` | ✅ |
| `resolvePromise` | `ResolvePromise` → `Task<JSPromiseResult>` | ✅ Trap-free (`qjs_promise_then`) |
| `markPromiseHandled` | `MarkPromiseHandled` | ✅ |
| `callFunction` / `construct` | `CallFunction` / `Construct` | ✅ |
| `setProp` / `defineProp` / `getProp` (on the VM) | instance methods on `JSValueHandle` | ✅ (string or handle keys) |
| `getException` | `GetException` (+ `GetPromiseResult` ➕) | ✅ |
| `newError` | `NewError(Exception)` / `NewError(string)` | ⚠️ `name` is the .NET type name |
| `typeof` / `dump` / `hostToHandle` | `TypeOf` / `Dump` / `HostToHandle` (+ `Task` overloads ➕) | ⚠️ See [Marshalling](#marshalling) |
| `snapshot` / `dispose` | `Snapshot()` / `Dispose()` | ✅ (no extensions in snapshot) |

### `JSValueHandle`

| TS | .NET | Status |
|---|---|---|
| 22 `is*` brand getters, `identity`, `toBoolean`, `classId`, `promiseState`, `dup`, `dispose`, `disposed` | same | ✅ (`IsException`, `ToBool` ➕) |
| `className` / `constructorName` / `typeof` / `consume` | `ClassName` / `ConstructorName` / `TypeOf` / `Consume` | ✅ (`null` instead of `undefined`) |
| `length` | `Length` (`int`) | ✅ |
| `keys()` | `Keys()` | ✅ |
| `getOwnPropertyNames` / `getOwnPropertyKeys` / `getOwnPropertyDescriptor` | same | ✅ (`null` instead of `undefined`) |
| `hasOwnProperty` / `propertyIsEnumerable` | same | ✅ |
| `getPrototypeOf` | same | ✅ |
| `getProxyTarget` / `getProxyHandler` | same | ✅ |
| `getProp` / `setProp` / `defineProp` | same | ✅ |
| `toNumber` / `toBigInt` / `toArrayBuffer` / `toUint8Array` / `toString` | `ToNumber` / `ToInt64` / `ToByteArray` / `ToManagedString` | ✅ |

### Other types and constants

| TS | .NET | Status |
|---|---|---|
| `JSException` | `JSException` | ✅ (`Handle`, `Name`, `JsStack`; `StackTrace` returns the JS stack) |
| `Deferred` | `Deferred` | ✅ Settle-once, release-both-resolvers (upstream #56); `Settled` tracks the guest promise |
| `HandleScope`, `JSOwnPropertyDescriptor`, `MemoryUsage`, `Snapshot`, `SnapshotExtension` | same | ✅ |
| `EvalFlags`, `CompileFlags`, `Intrinsics` | same | ✅ |
| `MAX_STACK_SIZE` | `QuickJs.MAX_STACK_SIZE` | ✅ |
| `JSPropertyDescriptor` | bool parameters on `DefineProp` | ⚠️ |
| `ExtensionDescriptor`, `LoadedExtension`, `WasiOptions`, … | — | ❌ Gap 7 |

### `QuickJSOptions` → `QuickJsOptions`

| Option | .NET | Status |
|---|---|---|
| `wasm` | `WasmBytes` | ⚠️ `byte[]` only; embedded fallback. Since 1.5.0 compiled once per distinct binary (SHA-256 of the content, plus a by-reference lookup per array) and shared by every VM; TS recompiles bytes per `create()` unless given a `WebAssembly.Module`. No compiled-module option: a Wasmtime `Module` is bound to its `Engine`, which is internal (`CompiledModules`). |
| `memoryLimit` / `interruptHandler` / `onUnhandledRejection` / `moduleLoader` / `intrinsics` | same | ✅ |
| `maxStackSize` | `MaxStackSize` | ✅ Range-checked (`ArgumentOutOfRangeException` instead of `RangeError`) |
| `timezoneOffset` | `TimezoneOffset` (`Host` / `Fixed` / `Callback`) | ✅ |
| `wasi` / `extensions` | — | ❌ Gap 7 |

## WASM exports

- `c/interface.c` exports 104 `qjs_*` / `wasm_*` functions.
- `Interop/NativeExports.cs` wraps all of them, plus `_initialize`. No wrapper calls an export that doesn't exist.
- Wrappers that nothing calls: `Destroy`, `GetString` and `TypeOf`. TS doesn't use these either.
- Host imports (`env.host_*`, WASI) are all defined. .NET also defines `proc_exit` (unused).
- A missing export throws `InvalidOperationException` naming the stale `quickjs.wasm`. Rebuild it with `make quickjs.wasm`; on Windows, run `make setup` first.

## WASI shim and timezone

| Function | TS | .NET |
|---|---|---|
| `clock_time_get` | Realtime ms for ids 0/1; other ids `NOSYS` | Monotonic `Stopwatch` for id 1; realtime otherwise ⚠️ |
| `fd_write`, `fd_fdstat_get`, `fd_close`, `fd_seek`, `random_get` | — | ✅ |
| User override (`wasi` option) | ✅ | ❌ |
| Timezone `'host'` | Real local offset | ✅ Real local offset, DST-aware |

## Marshalling

| Case | TS `dump` / `hostToHandle` | .NET `Dump` / `HostToHandle` |
|---|---|---|
| `undefined` | `undefined` | `QuickJs.Undefined` sentinel |
| Global / local symbol | `Symbol.for` / `undefined` | `JsSymbol` / `Undefined` |
| Typed arrays | Matching typed array | `byte[]` |
| BigInt | `bigint` | `long` (and a host `long` becomes a JS BigInt) |
| Error | `Error` | `System.Exception` (`Source` = name, `Data["stack"]`). A host `Exception` passed to `HostToHandle` is reflected as a plain object. |
| Promise / Task | Auto-runs jobs on settle | Bridged; delivered by `ExecutePendingJobs[Async]` |
| Objects | Plain objects | `IDictionary` and POCOs (reflection) |
| Cycles in `HostToHandle` | Not handled | Not handled |

## Tests

Mapping of `test/*.test.ts` to `src/dotnet/QuickJsWasi.Tests`:

| Area | .NET coverage |
|---|---|
| lossless-strings, portable-handles, promise-retention, eval "module completion" | ✅ full (`LosslessStringTests`, `PortableHandleTests`, `PromiseRetentionTests`, `ModuleCompletionTests`) |
| eval, functions, handle-lifetime, introspection, values, promises, snapshot, limits, symbols, bytecode, binary | Partial (core paths only) |
| Parity fixes (promises interception, `markPromiseHandled`, proxy introspection, className, timezone host/fixed/callback, `maxStackSize` range, versions) | ✅ `ParityTests` |
| memory-growth, promise-rejection (`onUnhandledRejection` cases), gc, gc-threshold edge cases, module-loader (13 of 14), intrinsics (17 of 18), base64, wasi | ❌ Not ported; portable without new APIs, except the `wasi` option |
| extensions, crypto, encoding, headers, structured-clone, wpt-*, devalue-round-trip | ❌ Need extension support (Gap 7) |
| Async host bridge, `ExecutePendingJobsAsync`, `HostToHandle(Task)`, POCO marshalling | ➕ .NET-only |

## Upstream sync log

| Upstream | Change | .NET |
|---|---|---|
| #51 `066475d` | MIT license notice in the package | `LICENSE` packed into the nupkg |
| #50 `93a7b0b` | Settled module evaluation exposes the namespace without running jobs | Via the rebuilt `quickjs.wasm`; `ModuleCompletionTests` |
| #53 `1f38685` | quickjs-ng v0.17.0 | Via the rebuilt `quickjs.wasm` |
| #56 `2d87476` | `Deferred` releases both resolvers; settle-once | `Deferred.Settle`; `PromiseRetentionTests` |
