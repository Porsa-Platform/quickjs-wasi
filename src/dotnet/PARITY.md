# TypeScript → .NET parity

The TypeScript library (`src/index.ts`, `src/wasi-shim.ts`, `src/extensions.ts`, `test/*.test.ts`) is the
reference implementation. This document tracks how the .NET host (`src/dotnet/QuickJsWasi`) matches it.

- **Reference:** upstream `quickjs-wasi` 3.6.3 (quickjs-ng v0.17.0). **.NET package:** 1.4.0.
- **Status keys:** ✅ ported · ⚠️ ported, behaves differently · ❌ missing · ➕ .NET-only.
- When you port an upstream change, update the matching row and the [upstream sync log](#upstream-sync-log).

## Open gaps (prioritised)

| # | Gap | TS | .NET | Status |
|---|---|---|---|---|
| 1 | `TimezoneOffsetOption.Host` is always UTC | `-new Date(t*1000).getTimezoneOffset()*60` (index.ts ~986) | `DateTimeOffset.FromUnixTimeSeconds(s).Offset` is always zero (QuickJsOptions.cs `HostTimezoneOffsetOption`). Should use `TimeZoneInfo.Local.GetUtcOffset(...)`. `Timezone_HostOffset` has an `actual == 0` escape that hides this. | ⚠️ bug |
| 2 | `ResolvePromise` can run guest code | Subscribes with engine-level `qjs_promise_then`, which ignores patched `then` / `Symbol.species` | Uses `Promise.prototype.then` captured on first use (`GetPromiseThen`). A patch made before the first call is captured, and `Symbol.species` is consulted. The `PromiseThen` wrapper exists but is unused. | ⚠️ |
| 3 | Missing exception checks | `getProxyTarget`/`getProxyHandler` throw `JSException`; `keys()` returns `[]`; `dump` returns `{}` | `GetProxyTarget`/`GetProxyHandler`, `Keys()` and `Dump` of objects use the exception sentinel. The guest TypeError stays pending. | ⚠️ |
| 4 | `HasOwnProperty` / `PropertyIsEnumerable` on error | `=== 1`, so an error reads as `false` | `!= 0`, so an error (-1) reads as `true` | ⚠️ |
| 5 | `Deferred.Settled` timing | Completes when the guest promise settles | Completes when the host calls `Resolve`/`Reject`, even when resolving with a pending thenable | ⚠️ |
| 6 | Missing APIs | `className`, `constructorName`, `typeof` (handle getter), `consume`, `markPromiseHandled`, `getGlobal()`…`getFalse()`, `defineProp` with symbol/handle key, `MAX_STACK_SIZE` and `maxStackSize` RangeError validation | — (`GetClassName` and `PromiseMarkAsHandled` wrappers exist but are unused) | ❌ |
| 7 | Extensions and WASI overrides | `extensions` option, `loadExtension`/`restoreExtensions`; `wasi` option | No extension loading. `RestoreAsync` rejects snapshots that have extensions. `WasiShim` is hard-coded (subclassing isn't reachable from options). | ❌ |
| 8 | `ToByteArray` on typed arrays | Checks `qjs_is_array_buffer` first | Calls `GetArrayBuffer` first; on a typed array this leaves a pending TypeError before falling back | ⚠️ |
| 9 | Disposed-VM guarding | `assertNotDisposed()` on nearly every method | `ObjectDisposedException` only in `Construct`, `WithScope`, `NewEphemeralFunction`, `ExecutePendingJobsAsync` and the task bridge | ⚠️ |
| 10 | `Versions` | `quickjs`, `quickjs-wasi`, plus extension versions; cached | `quickjs` only; not cached | ⚠️ |

**Intentional .NET differences (no action needed):**
- Errors use idiomatic .NET exception types where TS throws a plain `Error`. For example, `Compile` and `ExecutePendingJobs` throw `JSException`, and invalid arguments throw `InvalidOperationException` / `ArgumentOutOfRangeException`.
- `SetProp` and `DefineProp` throw `JSException` on failure, where TS silently ignores the result.
- A host callback that throws a `JSException` rethrows the original guest value. TS re-wraps it as a new Error.
- `NewError(Exception)` sets `name` to the .NET type name.

## Public API

### `QuickJS` → `QuickJs`

| TS | .NET | Status |
|---|---|---|
| `create` / `restore` | `CreateAsync` / `RestoreAsync` | ⚠️ `QuickJsOptions` only; falls back to the embedded wasm. Restore rejects extensions. |
| `serializeSnapshot` / `deserializeSnapshot` | `Snapshot.Serialize` / `Snapshot.Deserialize` | ✅ Same binary format (v1/v2) |
| `versions` | `Versions` | ⚠️ Gap 10 |
| `global` / `undefined` / `null` / `true` / `false` | `Global` / `UndefinedValue` / `NullValue` / `TrueValue` / `FalseValue` | ✅ (created eagerly) |
| `getGlobal()` … `getFalse()` | — | ❌ (use the properties) |
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
| `newPromise` → `Deferred` | `NewPromise` → `Deferred` | ⚠️ Gap 5 |
| `resolvePromise` | `ResolvePromise` → `Task<JSPromiseResult>` | ⚠️ Gap 2 |
| `markPromiseHandled` | — | ❌ |
| `callFunction` / `construct` | `CallFunction` / `Construct` | ✅ |
| `setProp` / `defineProp` / `getProp` (on the VM) | instance methods on `JSValueHandle` | ⚠️ `DefineProp` takes string keys only |
| `getException` | `GetException` (+ `GetPromiseResult` ➕) | ✅ |
| `newError` | `NewError(Exception)` / `NewError(string)` | ⚠️ `name` is the .NET type name |
| `typeof` / `dump` / `hostToHandle` | `TypeOf` / `Dump` / `HostToHandle` (+ `Task` overloads ➕) | ⚠️ See [Marshalling](#marshalling) |
| `snapshot` / `dispose` | `Snapshot()` / `Dispose()` | ✅ (no extensions in snapshot) |

### `JSValueHandle`

| TS | .NET | Status |
|---|---|---|
| 22 `is*` brand getters, `identity`, `toBoolean`, `classId`, `promiseState`, `dup`, `dispose`, `disposed` | same | ✅ (`IsException`, `ToBool` ➕) |
| `className` / `constructorName` / `typeof` / `consume` | — | ❌ |
| `length` | `Length` (`int`) | ✅ |
| `keys()` | `Keys()` | ⚠️ Gap 3 |
| `getOwnPropertyNames` / `getOwnPropertyKeys` / `getOwnPropertyDescriptor` | same | ✅ (`null` instead of `undefined`) |
| `hasOwnProperty` / `propertyIsEnumerable` | same | ⚠️ Gap 4 |
| `getPrototypeOf` | same | ✅ |
| `getProxyTarget` / `getProxyHandler` | same | ⚠️ Gap 3 |
| `getProp` / `setProp` / `defineProp` | same | ✅ / ⚠️ (`DefineProp` has no handle-key overload) |
| `toNumber` / `toBigInt` / `toArrayBuffer` / `toUint8Array` / `toString` | `ToNumber` / `ToInt64` / `ToByteArray` / `ToManagedString` | ✅ / ⚠️ Gap 8 |

### Other types and constants

| TS | .NET | Status |
|---|---|---|
| `JSException` | `JSException` | ✅ (`Handle`, `Name`, `JsStack`; `StackTrace` returns the JS stack) |
| `Deferred` | `Deferred` | ✅ Settle-once and release-both-resolvers (upstream #56); ⚠️ Gap 5 |
| `HandleScope`, `JSOwnPropertyDescriptor`, `MemoryUsage`, `Snapshot`, `SnapshotExtension` | same | ✅ |
| `EvalFlags`, `CompileFlags`, `Intrinsics` | same | ✅ |
| `MAX_STACK_SIZE` | — | ❌ |
| `JSPropertyDescriptor` | bool parameters on `DefineProp` | ⚠️ |
| `ExtensionDescriptor`, `LoadedExtension`, `WasiOptions`, … | — | ❌ Gap 7 |

### `QuickJSOptions` → `QuickJsOptions`

| Option | .NET | Status |
|---|---|---|
| `wasm` | `WasmBytes` | ✅ (`byte[]`; embedded fallback) |
| `memoryLimit` / `interruptHandler` / `onUnhandledRejection` / `moduleLoader` / `intrinsics` | same | ✅ |
| `maxStackSize` | `MaxStackSize` | ⚠️ No range validation |
| `timezoneOffset` | `TimezoneOffset` (`Host` / `Fixed` / `Callback`) | ⚠️ Gap 1 |
| `wasi` / `extensions` | — | ❌ Gap 7 |

## WASM exports

- `c/interface.c` exports 104 `qjs_*` / `wasm_*` functions.
- `Interop/NativeExports.cs` wraps all of them, plus `_initialize`. No wrapper calls an export that doesn't exist.
- Wrappers that nothing calls: `Destroy`, `GetString` and `TypeOf` (TS doesn't use these either), and `GetClassName`, `PromiseThen` and `PromiseMarkAsHandled` (gaps 2 and 6).
- Host imports (`env.host_*`, WASI) are all defined. .NET also defines `proc_exit` (unused).
- A missing export throws `InvalidOperationException` naming the stale `quickjs.wasm`. Rebuild it with `make quickjs.wasm`; on Windows, run `make setup` first.

## WASI shim and timezone

| Function | TS | .NET |
|---|---|---|
| `clock_time_get` | Realtime ms for ids 0/1; other ids `NOSYS` | Monotonic `Stopwatch` for id 1; realtime otherwise ⚠️ |
| `fd_write`, `fd_fdstat_get`, `fd_close`, `fd_seek`, `random_get` | — | ✅ |
| User override (`wasi` option) | ✅ | ❌ |
| Timezone `'host'` | Real local offset | ⚠️ Always UTC (Gap 1) |

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
| memory-growth, promise-rejection, gc, gc-threshold edge cases, timezone fixed/callback, module-loader (13 of 14), intrinsics (17 of 18), base64, wasi | ❌ Not ported; portable without new APIs, except `markPromiseHandled` and the `wasi` option |
| extensions, crypto, encoding, headers, structured-clone, wpt-*, devalue-round-trip | ❌ Need extension support (Gap 7) |
| Async host bridge, `ExecutePendingJobsAsync`, `HostToHandle(Task)`, POCO marshalling | ➕ .NET-only |

## Upstream sync log

| Upstream | Change | .NET |
|---|---|---|
| #51 `066475d` | MIT license notice in the package | `LICENSE` packed into the nupkg |
| #50 `93a7b0b` | Settled module evaluation exposes the namespace without running jobs | Via the rebuilt `quickjs.wasm`; `ModuleCompletionTests` |
| #53 `1f38685` | quickjs-ng v0.17.0 | Via the rebuilt `quickjs.wasm` |
| #56 `2d87476` | `Deferred` releases both resolvers; settle-once | `Deferred.Settle`; `PromiseRetentionTests` |
