# AGENTS.md — Developer guide for the QuickJsWasi .NET host

This document is written for an AI agent (or human developer) who needs to navigate, understand, extend, or maintain the .NET host library in `src/dotnet/`. It covers project structure, build mechanics, key design patterns, and the rules that keep the codebase consistent.

---

## Repository layout

```
src/dotnet/
├── QuickJsWasi/                      # Library project (NuGet package)
│   ├── Interop/
│   │   ├── HostCallDispatcher.cs     # Routes WASM→host callbacks
│   │   ├── NativeExports.cs          # Typed wrappers for every qjs_* WASM export
│   │   └── WasmMemoryAccessor.cs     # Raw WASM memory helpers + WTF-8 codec
│   ├── JSValueHandle.cs              # Ref-counted wrapper for a JS value
│   ├── QuickJs.cs                    # Main VM class — the entire public API
│   ├── QuickJsOptions.cs             # VM creation / restoration options
│   ├── Snapshot.cs                   # Snapshot type + binary serialisation
│   ├── Deferred.cs                   # Promise deferred (resolve/reject pair)
│   ├── WasiShim.cs                   # WASI fd_write / clock shim (I/O override)
│   ├── EvalFlags.cs                  # JS_EVAL_TYPE_* enum
│   ├── CompileFlags.cs               # Bytecode compile flags
│   ├── Intrinsics.cs                 # Selectively disable JS built-ins
│   └── Resources/
│       └── quickjs.wasm              # Embedded WASM binary (git-ignored, built by make)
│
├── QuickJsWasi.Tests/                # xUnit test suite (one file per concern)
│   ├── TestBase.cs                   # Shared VM factory + WASM discovery
│   ├── EvalTests.cs                  # Eval, module, bytecode
│   ├── HostFunctionTests.cs          # NewHostFunction, callbacks
│   ├── HandleLifetimeTests.cs        # Disposed, WithScope, NewEphemeralFunction,
│   │                                 #   UnregisterHostCallback, ResolvePromise
│   ├── IntrospectionTests.cs         # Construct, Identity, ToBoolean, ClassId
│   ├── SnapshotRestoreTests.cs       # Capture, serialize, restore
│   ├── ErrorHandlingTests.cs         # JSException, error propagation
│   ├── InterruptHandlerTests.cs      # Interrupt / timeout
│   ├── LosslessStringTests.cs        # WTF-8 guest↔host transport
│   └── PortableHandleTests.cs        # ExportHandle / ImportHandle
│
├── QuickJsWasi.Benchmarks/           # BenchmarkDotNet suite (requires quickjs.wasm)
│   ├── QuickJsBenchmarks.cs          # All benchmarks (cold start, hot VM, strings, ...)
│   └── Program.cs
│
├── Sample.cs                         # Runnable end-to-end example of every API feature
├── README.md                         # End-user API documentation
└── QuickJsWasi.slnx                  # Solution file
```

The C WASM interface lives outside this folder at `c/interface.c`. The compiled output (`quickjs.wasm`) is embedded as a managed resource and loaded at runtime.

---

## Build and test

### Prerequisites

| Tool | Purpose |
|---|---|
| .NET 10 SDK | Build, test, pack |
| wasi-sdk | Compile `quickjs.wasm` from C source |
| `make` | Top-level build orchestration |

### Commands

```sh
# From repo root — builds WASM + .NET
make

# Build only the WASM binary (requires wasi-sdk installed via `make setup`)
make quickjs.wasm
cp quickjs.wasm src/dotnet/QuickJsWasi/Resources/quickjs.wasm

# .NET only (WASM must already exist)
cd src/dotnet
dotnet build
dotnet test QuickJsWasi.Tests
dotnet run --configuration Release --project QuickJsWasi.Benchmarks

# Pack NuGet
dotnet pack QuickJsWasi/QuickJsWasi.csproj -c Release -o nupkg/
```

`quickjs.wasm` is `.gitignore`d. Tests guard every VM-dependent path with `if (!HasWasm) return;` so the test suite still runs (and skips gracefully) in environments without the binary.

---

## Key classes and their responsibilities

### `QuickJs` (QuickJs.cs)

The only class you interact with directly. It owns the Wasmtime `Engine`, `Module`, `Store`, and `Linker`, and exposes the entire JS API through typed methods.

**Lifecycle:**
1. `CreateAsync(options?)` — instantiates the WASM module, calls the WASI `_initialize` export, and returns the ready-to-use VM.
2. `RestoreAsync(snapshot, options?)` — same as above, then writes the snapshot's memory and stack pointer directly into the WASM linear memory, bypassing re-execution of init code.

**Pattern for WASM calls:**  
All WASM exports are accessed through `Exports` (a `NativeExports` instance). Methods that return a `JSValue*` pointer are wrapped in `new JSValueHandle(this, ptr)`. Always dispose handles when done.

**Memory allocation:**  
Guest strings and argument arrays are written to WASM linear memory using `WasmMalloc` + `WasmFree`. Every allocation must be freed in a `finally` block.

### `JSValueHandle` (JSValueHandle.cs)

A thin wrapper over a `JSValue*` pointer (a 4-byte WASM pointer into the QuickJS heap). Key invariants:

- `ownsValue = true` (default): the host owns the reference; `Dispose()` calls `FreeValue`.
- `ownsValue = false`: the pointer is borrowed from the C caller (e.g., host-callback args); `Dispose()` is a no-op.
- `_isBorrowed = true`: the pointer is owned by the WASM trampoline and must not be exported (see `ExportHandle`).
- `isSingleton = true`: permanent VM-owned values (`UndefinedValue`, `NullValue`, etc.); never freed, never scope-tracked.

**Scope tracking:**  
`vm._activeScope` is a `List<JSValueHandle>` populated inside `WithScope`. Every new owned, non-singleton handle registers itself at construction time. This is how `WithScope<T>` disposes everything created during its callback — no explicit cleanup required.

**Property routing (NUL / lone-surrogate keys):**  
C-string WASM APIs (`qjs_get_prop_string` etc.) are NUL-terminated and cannot express keys with `\u0000` or lone surrogates. `WasmMemoryAccessor.StringKeyNeedsValuePath(key)` detects these and `GetProp`, `SetProp`, `DefineProp`, `HasOwnProperty`, and `PropertyIsEnumerable` automatically route through the `*_value` WASM variants using a temporary `JSValueHandle` key.

**WTF-8 string encoding:**
- `ToManagedString()` — calls `qjs_get_string_len`, reads the byte span, then calls `WasmMemoryAccessor.DecodeWtf8()`. This preserves lone surrogates and embedded NULs.
- `NewString(value)` in `QuickJs.cs` — calls `WasmMemoryAccessor.EncodeWtf8()` before writing to WASM memory.

### `NativeExports` (Interop/NativeExports.cs)

A thin generated-style class that wraps every `qjs_*` WASM export as a typed C# method. **This is the only place raw WASM exports are named.** When the upstream C interface (`c/interface.c`) gains a new export, add one line here before using it anywhere else.

Convention:
```csharp
public int GetFoo(int objPtr) => Convert.ToInt32(Call("qjs_get_foo", objPtr)!);
```

### `WasmMemoryAccessor` (Interop/WasmMemoryAccessor.cs)

Static helpers for raw WASM memory access. Key members:

| Method | Purpose |
|---|---|
| `ReadUtf8(memory, ptr, len)` | Read a UTF-8 span (for C-string values without surrogates) |
| `DecodeWtf8(bytes)` | Decode WTF-8 → .NET `string` (preserves lone surrogates) |
| `EncodeWtf8(s)` | Encode .NET `string` → WTF-8 bytes (preserves lone surrogates) |
| `StringKeyNeedsValuePath(key)` | Returns `true` if key contains `\u0000` or lone surrogates |
| `ReadInt32 / WriteInt32` | Little-endian 32-bit integer in WASM memory |
| `ReadBytes / WriteBytes` | Byte array copy into / out of WASM memory |

### `HostCallDispatcher` (Interop/HostCallDispatcher.cs)

Called by the WASM trampoline whenever a host function is invoked from JS. It:

1. Reads the callback name from WASM memory (UTF-8, length-prefixed).
2. Looks up the delegate in `vm._hostCallbacks`.
3. If not found: throws a guest-side `Error` via `qjs_throw` — **never returns `undefined`**.
4. Creates `JSValueHandle` wrappers for `this` and each argument with `ownsValue: false, isBorrowed: true`.
5. Calls the delegate and wraps any thrown exception as a guest-side error.

The `isBorrowed: true` flag ensures borrowed handles are not registered with the active scope and cannot be exported via `ExportHandle`.

---

## Extending the API

### Adding a new WASM export

1. Add the function to `c/interface.c` (exports a `qjs_*` symbol).
2. Add a typed wrapper in `NativeExports.cs`.
3. Use it from `QuickJs.cs` or `JSValueHandle.cs` — never call `NativeExports` directly from tests.
4. Add tests in the appropriate test file.

### Adding a new public API method

**On `QuickJs`:** the method should allocate guest memory (via `WriteString`/`WasmMalloc`), call one or more `Exports.*` methods, wrap the result in a `JSValueHandle`, and free allocations in `finally` blocks.

**On `JSValueHandle`:** the method typically takes string or handle arguments, possibly routes through `StringKeyNeedsValuePath`, and returns either a plain value or a new `JSValueHandle`.

### Adding tests

Test files mirror the TypeScript split from upstream. Add tests to the file that best matches the concern:

| File | What belongs here |
|---|---|
| `EvalTests.cs` | `Eval`, `Compile`, `EvalBytecode`, module loading |
| `HostFunctionTests.cs` | `NewHostFunction`, argument passing, return values |
| `HandleLifetimeTests.cs` | `Disposed`, `WithScope`, `NewEphemeralFunction`, `UnregisterHostCallback`, `ResolvePromise` |
| `IntrospectionTests.cs` | `Identity`, `Construct`, `ToBoolean`, `TypeOf`, `ClassId` |
| `SnapshotRestoreTests.cs` | Snapshot capture, serialise/deserialise, restore |
| `LosslessStringTests.cs` | WTF-8 string round-trips, mangled property keys |
| `PortableHandleTests.cs` | `ExportHandle`, `ImportHandle`, borrowed-handle rejection |

All tests inherit `TestBase` and guard with `if (!HasWasm) return;`. Do not remove this guard — it allows the test suite to run in CI environments that have not yet built the WASM binary.

---

## Snapshot mechanics

A `Snapshot` is a value type containing:

- A byte-for-byte copy of the WASM linear memory at the moment of capture.
- The stack pointer value (a single 32-bit integer).
- The `JSRuntime*` and `JSContext*` pointers (needed to locate the live QuickJS heap roots).

`RestoreAsync` writes the snapshot memory into a freshly instantiated WASM module (without running QuickJS init code), then resets the stack pointer. Host callback delegates are **not** serialised — re-register them with `RegisterHostCallback` after restore.

The binary serialisation format (`Snapshot.Serialize` / `Snapshot.Deserialize`) is identical to the TypeScript `serializeSnapshot` / `deserializeSnapshot` format, so snapshots are portable between runtimes.

---

## Portable handle tokens

`ExportHandle(handle)` returns the raw WASM pointer (`int`) of the `JSValue*` box. This pointer is stable across snapshot/restore cycles because `RestoreAsync` restores the complete memory image verbatim — the box is at the same address in the restored VM.

`ImportHandle(token)` validates the token (must be in `(0, memorySize)`) and calls `qjs_dup_value(token)` to create a new independently-owned reference.

Valid tokens become invalid only when:
- The JS value they point to is garbage-collected (which cannot happen while the original handle is alive).
- The VM is disposed.

---

## WTF-8 codec notes

QuickJS stores JS strings as sequences of UTF-16 code units. When those strings contain lone surrogates, the C export `qjs_get_string_len` encodes them as 3-byte WTF-8 sequences (`0xED 0xA0–0xBF 0x80–0xBF`). Standard UTF-8 decoders replace these with U+FFFD; `WasmMemoryAccessor.DecodeWtf8` preserves them.

The encoder has a fast path: if a .NET `string` contains no lone surrogates, `EncodeWtf8` delegates to `Encoding.UTF8.GetBytes`, which is JIT-optimised. The slow path builds the byte list code-unit by code-unit, emitting 3-byte WTF-8 sequences for lone surrogates.

---

## Common pitfalls

| Mistake | Consequence | Fix |
|---|---|---|
| Not disposing `JSValueHandle` | JS heap leak | Use `using` or dispose in `finally` |
| Disposing a singleton (`UndefinedValue` etc.) | Silent no-op today, but fragile | Don't dispose singletons |
| Returning `args[i]` from a host callback without `Dup()` | Use-after-free: the trampoline frees args on return | Always `Dup()` argument handles before returning them |
| Calling `ExportHandle` on a borrowed arg | `InvalidOperationException` | `Dup()` the arg first |
| Using `Encoding.UTF8.GetBytes` for JS strings | Lone surrogates → U+FFFD | Use `WasmMemoryAccessor.EncodeWtf8` |
| Reading a C-string with `ReadCString` for JS strings | NUL truncation | Use `GetStringLen` + `DecodeWtf8` |
| Adding a new WASM export without adding it to `NativeExports.cs` | `Call` throws at runtime | Always add to `NativeExports.cs` first |
| Forgetting `WasmFree` after `WasmMalloc` | WASM heap leak | Put `WasmFree` in a `finally` block |

