using System.Text;
using QuickJsWasi.Interop;
using Wasmtime;

namespace QuickJsWasi;

public delegate JSValueHandle HostFunction(JSValueHandle thisValue, IReadOnlyList<JSValueHandle> arguments);

public sealed class QuickJs : IDisposable
{
    private sealed class UndefinedSentinelType
    {
        public override string ToString() => "undefined";
    }

    private readonly Engine _engine;
    private readonly Wasmtime.Module _module;
    private readonly Linker _linker;
    private readonly HostCallDispatcher _hostDispatcher;
    private readonly Dictionary<string, HostFunction> _hostCallbacks = new(StringComparer.Ordinal);
    private readonly WasiShim _wasi;
    private bool _disposed;

    private QuickJs(Engine engine, Wasmtime.Module module, Store store, Linker linker, WasiShim wasi)
    {
        _engine = engine;
        _module = module;
        Store = store;
        _linker = linker;
        _wasi = wasi;
        _hostDispatcher = new HostCallDispatcher(this);
    }

    public static readonly object Undefined = new UndefinedSentinelType();

    internal Store Store { get; }

    internal NativeExports Exports { get; private set; } = null!;

    private Instance? Instance { get; set; }

    public bool IsDisposed => _disposed;

    public JSValueHandle Global { get; private set; } = null!;
    public JSValueHandle UndefinedValue { get; private set; } = null!;
    public JSValueHandle NullValue { get; private set; } = null!;
    public JSValueHandle TrueValue { get; private set; } = null!;
    public JSValueHandle FalseValue { get; private set; } = null!;

    /// <summary>
    /// Version information for the runtime. Always includes <c>"quickjs"</c> (the QuickJS engine version).
    /// </summary>
    public IReadOnlyDictionary<string, string> Versions
    {
        get
        {
            var qjsVersion = ReadCString(Exports.GetQuickJsVersion());
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["quickjs"] = qjsVersion,
            };
        }
    }

    public static Task<QuickJs> CreateAsync(QuickJsOptions? options = null)
    {
        options ??= new QuickJsOptions();
        var wasm = options.WasmBytes ?? LoadDefaultWasm() ?? throw new InvalidOperationException("quickjs.wasm not found. Set QuickJsOptions.WasmBytes or place quickjs.wasm in Resources/.");
        var engine = new Engine();
        var module = Module.FromBytes(engine, "quickjs", wasm);
        var store = new Store(engine);
        var linker = new Linker(engine);
        var vm = new QuickJs(engine, module, store, linker, new WasiShim());
        vm.DefineImports(options);
        vm.Instance = linker.Instantiate(store, module);
        vm.Exports = new NativeExports(store, vm.Instance);
        vm.Exports.Initialize();
        var initResult = options.Intrinsics.HasValue ? vm.Exports.Init2(options.Intrinsics.Value) : vm.Exports.Init();
        if (initResult != 0)
        {
            vm.Dispose();
            throw new InvalidOperationException("Failed to initialize QuickJS runtime.");
        }

        vm.InitializeSingletons();
        vm.ApplyOptions(options);
        return Task.FromResult(vm);
    }

    public static async Task<QuickJs> RestoreAsync(Snapshot snapshot, QuickJsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new QuickJsOptions();
        if (snapshot.Extensions is { Count: > 0 })
        {
            throw new NotSupportedException("Snapshot restore with extensions is not yet supported by the .NET host.");
        }

        var vm = await CreateUninitializedAsync(options).ConfigureAwait(false);
        var currentLength = vm.Exports.Memory.GetSpan<byte>(0).Length;
        if (snapshot.Memory.Length > currentLength)
        {
            var deltaPages = (int)Math.Ceiling((snapshot.Memory.Length - currentLength) / 65536d);
            if (deltaPages > 0)
            {
                vm.Exports.Memory.Grow(deltaPages);
            }
        }

        WasmMemoryAccessor.WriteBytes(vm.Exports.Memory, 0, snapshot.Memory);
        vm.Exports.SetRuntimeAndContext(snapshot.RuntimePtr, snapshot.ContextPtr);
        vm.Exports.StackPointerValue = snapshot.StackPointer;
        vm.InitializeSingletons();
        vm.ApplyOptions(options);
        return vm;
    }

    private static Task<QuickJs> CreateUninitializedAsync(QuickJsOptions options)
    {
        var wasm = options.WasmBytes ?? LoadDefaultWasm() ?? throw new InvalidOperationException("quickjs.wasm not found. Set QuickJsOptions.WasmBytes or place quickjs.wasm in Resources/.");
        var engine = new Engine();
        var module = Module.FromBytes(engine, "quickjs", wasm);
        var store = new Store(engine);
        var linker = new Linker(engine);
        var vm = new QuickJs(engine, module, store, linker, new WasiShim());
        vm.DefineImports(options);
        vm.Instance = linker.Instantiate(store, module);
        vm.Exports = new NativeExports(store, vm.Instance);
        return Task.FromResult(vm);
    }

    private static byte[]? LoadDefaultWasm()
    {
        var assembly = typeof(QuickJs).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("quickjs.wasm", StringComparison.OrdinalIgnoreCase) || x.EndsWith("quickjs_wasm", StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        var assemblyDir = Path.GetDirectoryName(assembly.Location);
        if (assemblyDir is not null)
        {
            var file = Path.Combine(assemblyDir, "Resources", "quickjs.wasm");
            if (File.Exists(file))
            {
                return File.ReadAllBytes(file);
            }
        }

        return null;
    }

    private void DefineImports(QuickJsOptions options)
    {
        Memory? memoryAccessor() => Exports?.Memory;

        _linker.Define("env", "host_call",
            Function.FromCallback(Store, (Caller caller, int namePtr, int nameLen, int thisPtr, int argc, int argvPtr)
                => _hostDispatcher.Invoke(caller, namePtr, nameLen, thisPtr, argc, argvPtr)));

        _linker.Define("env", "host_interrupt",
            Function.FromCallback(Store, () => options.InterruptHandler?.Invoke() == true ? 1 : 0));

        _linker.Define("env", "host_promise_rejection",
            Function.FromCallback(Store, (int promisePtr, int reasonPtr, int isHandled) =>
            {
                var handler = options.OnUnhandledRejection;
                if (handler is null)
                {
                    Exports.FreeValue(promisePtr);
                    Exports.FreeValue(reasonPtr);
                    return;
                }

                using var promise = new JSValueHandle(this, promisePtr);
                using var reason = new JSValueHandle(this, reasonPtr);
                handler(promise, reason, isHandled != 0);
            }));

        _linker.Define("env", "host_module_normalize",
            Function.FromCallback(Store, (Caller caller, int baseNamePtr, int namePtr) =>
            {
                var memory = caller.GetMemory("memory") ?? memoryAccessor() ?? throw new InvalidOperationException("WASM memory is not available.");
                var normalize = options.ModuleLoader?.Normalize;
                if (normalize is null)
                {
                    // No normalize handler — return a copy of the specifier unchanged
                    var name = WasmMemoryAccessor.ReadCString(memory, caller, namePtr);
                    return WriteString(name).Ptr;
                }

                var baseName = WasmMemoryAccessor.ReadCString(memory, caller, baseNamePtr);
                var specifier = WasmMemoryAccessor.ReadCString(memory, caller, namePtr);
                try
                {
                    var normalized = normalize(baseName, specifier);
                    return WriteString(normalized).Ptr;
                }
                catch (Exception ex)
                {
                    using var err = NewError(ex);
                    Exports.Throw(err.Ptr);
                    return 0;
                }
            }));

        _linker.Define("env", "host_module_load",
            Function.FromCallback(Store, (Caller caller, int namePtr, int outLenPtr) =>
            {
                var load = options.ModuleLoader?.Load;
                if (load is null) return 0;

                var memory = caller.GetMemory("memory") ?? memoryAccessor() ?? throw new InvalidOperationException("WASM memory is not available.");
                var name = WasmMemoryAccessor.ReadCString(memory, caller, namePtr);
                try
                {
                    var source = load(name);
                    var written = WriteString(source);
                    WasmMemoryAccessor.WriteInt32(Exports.Memory, outLenPtr, written.Length);
                    return written.Ptr;
                }
                catch (Exception ex)
                {
                    using var err = NewError(ex);
                    Exports.Throw(err.Ptr);
                    return 0;
                }
            }));

        _linker.Define("env", "host_get_timezone_offset",
            Function.FromCallback(Store, (int hi, int lo) => ResolveTimezoneOffset(options.TimezoneOffset, ((long)hi << 32) | (uint)lo)));

        _wasi.Define(_linker, Store, memoryAccessor);
    }

    private static int ResolveTimezoneOffset(TimezoneOffsetOption? option, long epochSeconds)
        => (option ?? TimezoneOffsetOption.Host).ResolveSeconds(epochSeconds);

    private void InitializeSingletons()
    {
        Global = new JSValueHandle(this, Exports.GetGlobal(), ownsValue: true, isSingleton: true);
        UndefinedValue = new JSValueHandle(this, Exports.GetUndefined(), ownsValue: true, isSingleton: true);
        NullValue = new JSValueHandle(this, Exports.GetNull(), ownsValue: true, isSingleton: true);
        TrueValue = new JSValueHandle(this, Exports.GetTrue(), ownsValue: true, isSingleton: true);
        FalseValue = new JSValueHandle(this, Exports.GetFalse(), ownsValue: true, isSingleton: true);
    }

    private void ApplyOptions(QuickJsOptions options)
    {
        if (options.MemoryLimit is long limit)
        {
            Exports.SetMemoryLimit(checked((int)limit));
        }

        if (options.MaxStackSize is long stackSize)
        {
            Exports.SetMaxStackSize(checked((int)stackSize));
        }

        Exports.SetInterruptHandler(options.InterruptHandler is null ? 0 : 1);
        Exports.SetPromiseRejectionHandler(options.OnUnhandledRejection is null ? 0 : 1);
        Exports.SetModuleLoader(options.ModuleLoader is null ? 0 : 1);
    }

    internal bool TryGetHostCallback(string name, out HostFunction callback)
        => _hostCallbacks.TryGetValue(name, out callback!);

    /// <summary>
    /// Re-register a host callback after restoring from a snapshot.
    /// The name must match the name passed to <see cref="NewHostFunction"/> before the snapshot.
    /// Unlike <see cref="NewHostFunction"/>, this method does not create a new WASM function object
    /// and does not throw if a callback with the same name is already registered.
    /// </summary>
    public void RegisterHostCallback(string name, HostFunction callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);
        _hostCallbacks[name] = callback;
    }

    internal (int Ptr, int Length) WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Exports.WasmMalloc(bytes.Length + 1);
        WasmMemoryAccessor.WriteBytes(Exports.Memory, ptr, bytes);
        WasmMemoryAccessor.WriteBytes(Exports.Memory, ptr + bytes.Length, new byte[] { 0 });
        return (ptr, bytes.Length);
    }

    internal string ReadCString(int ptr) => WasmMemoryAccessor.ReadCString(Exports.Memory, ptr);

    internal void ThrowOnNegative(int result)
    {
        if (result < 0)
        {
            throw new JSException(GetException());
        }
    }

    internal JSValueHandle CallFunctionRaw(JSValueHandle func, JSValueHandle thisValue, params JSValueHandle[] args)
    {
        var argvPtr = 0;
        if (args.Length > 0)
        {
            argvPtr = Exports.WasmMalloc(args.Length * 4);
            for (var i = 0; i < args.Length; i++)
            {
                WasmMemoryAccessor.WriteInt32(Exports.Memory, argvPtr + (i * 4), args[i].Ptr);
            }
        }

        try
        {
            return new JSValueHandle(this, Exports.Call(func.Ptr, thisValue.Ptr, args.Length, argvPtr));
        }
        finally
        {
            if (argvPtr != 0)
            {
                Exports.WasmFree(argvPtr);
            }
        }
    }

    private JSValueHandle ThrowIfException(JSValueHandle handle)
        => handle.IsException ? throw new JSException(GetException()) : handle;

    public JSValueHandle Eval(string code, string filename = "<eval>", int flags = 0)
    {
        var codePtr = WriteString(code);
        var filePtr = WriteString(filename);
        try
        {
            return ThrowIfException(new JSValueHandle(this, Exports.Eval(codePtr.Ptr, codePtr.Length, filePtr.Ptr, flags)));
        }
        finally
        {
            Exports.WasmFree(codePtr.Ptr);
            Exports.WasmFree(filePtr.Ptr);
        }
    }

    public byte[] Compile(string code, string filename = "<compile>", int evalFlags = 0, int writeFlags = 0)
    {
        var codePtr = WriteString(code);
        var filePtr = WriteString(filename);
        var outLenPtr = Exports.WasmMalloc(4);
        try
        {
            var bufferPtr = Exports.Compile(codePtr.Ptr, codePtr.Length, filePtr.Ptr, evalFlags, writeFlags, outLenPtr);
            if (bufferPtr == 0)
            {
                throw new JSException(GetException());
            }

            try
            {
                var length = WasmMemoryAccessor.ReadInt32(Exports.Memory, outLenPtr);
                return WasmMemoryAccessor.ReadBytes(Exports.Memory, bufferPtr, length);
            }
            finally
            {
                Exports.WasmFree(bufferPtr);
            }
        }
        finally
        {
            Exports.WasmFree(codePtr.Ptr);
            Exports.WasmFree(filePtr.Ptr);
            Exports.WasmFree(outLenPtr);
        }
    }

    public JSValueHandle EvalBytecode(byte[] bytecode)
    {
        ArgumentNullException.ThrowIfNull(bytecode);
        var ptr = Exports.WasmMalloc(bytecode.Length);
        try
        {
            WasmMemoryAccessor.WriteBytes(Exports.Memory, ptr, bytecode);
            return ThrowIfException(new JSValueHandle(this, Exports.EvalBytecode(ptr, bytecode.Length)));
        }
        finally
        {
            Exports.WasmFree(ptr);
        }
    }

    public int ExecutePendingJobs()
    {
        var count = 0;
        while (Exports.IsJobPending() != 0)
        {
            var result = Exports.ExecutePendingJob();
            if (result < 0)
            {
                throw new JSException(GetException());
            }

            count++;
        }

        return count;
    }

    public JSValueHandle GetException() => new(this, Exports.GetException());

    public JSValueHandle GetPromiseResult(JSValueHandle promise) => new(this, Exports.PromiseResult(promise.Ptr));

    /// <summary>
    /// Resolve a promise handle. Returns a host-side <see cref="Task{T}"/> that resolves
    /// when the QuickJS promise settles.
    ///
    /// <para>
    /// If the handle is not a promise, it is treated as an already-fulfilled value.
    /// </para>
    /// <para>
    /// The returned task resolves to a <see cref="JSPromiseResult.Fulfilled"/> or
    /// <see cref="JSPromiseResult.Rejected"/>. The caller owns the handle inside the result
    /// and must dispose it.
    /// </para>
    /// <para>
    /// <b>Important:</b> You must call <see cref="ExecutePendingJobs"/> after setting up
    /// any promises to pump the microtask queue. The task will complete only after the
    /// promise has settled.
    /// </para>
    /// </summary>
    public Task<JSPromiseResult> ResolvePromise(JSValueHandle promise)
    {
        ArgumentNullException.ThrowIfNull(promise);

        if (!promise.IsPromise)
        {
            return Task.FromResult<JSPromiseResult>(new JSPromiseResult.Fulfilled(promise.Dup()));
        }

        // Check if already settled
        var state = Exports.PromiseState(promise.Ptr);
        if (state == 1) // fulfilled
        {
            return Task.FromResult<JSPromiseResult>(new JSPromiseResult.Fulfilled(new JSValueHandle(this, Exports.PromiseResult(promise.Ptr))));
        }
        else if (state == 2) // rejected
        {
            return Task.FromResult<JSPromiseResult>(new JSPromiseResult.Rejected(new JSValueHandle(this, Exports.PromiseResult(promise.Ptr))));
        }

        // Pending — attach internal .then/.catch callbacks to get notified
        var tcs = new TaskCompletionSource<JSPromiseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid().ToString("N");
        var fulfilledName = $"__onFulfilled:{id}";
        var rejectedName = $"__onRejected:{id}";

        HostFunction onFulfilled = (_, args) =>
        {
            var val = args.Count > 0 ? args[0].Dup() : UndefinedValue;
            _hostCallbacks.Remove(fulfilledName);
            _hostCallbacks.Remove(rejectedName);
            tcs.TrySetResult(new JSPromiseResult.Fulfilled(val));
            return UndefinedValue;
        };
        HostFunction onRejected = (_, args) =>
        {
            var val = args.Count > 0 ? args[0].Dup() : UndefinedValue;
            _hostCallbacks.Remove(fulfilledName);
            _hostCallbacks.Remove(rejectedName);
            tcs.TrySetResult(new JSPromiseResult.Rejected(val));
            return UndefinedValue;
        };

        _hostCallbacks[fulfilledName] = onFulfilled;
        _hostCallbacks[rejectedName] = onRejected;

        var fulfilledWritten = WriteString(fulfilledName);
        var onFulfilledHandle = new JSValueHandle(this, Exports.NewHostFunction(fulfilledWritten.Ptr, fulfilledWritten.Length, 1));
        Exports.WasmFree(fulfilledWritten.Ptr);

        var rejectedWritten = WriteString(rejectedName);
        var onRejectedHandle = new JSValueHandle(this, Exports.NewHostFunction(rejectedWritten.Ptr, rejectedWritten.Length, 1));
        Exports.WasmFree(rejectedWritten.Ptr);

        using var thenFn = promise.GetProp("then");
        CallFunctionRaw(thenFn, promise, onFulfilledHandle, onRejectedHandle).Dispose();
        onFulfilledHandle.Dispose();
        onRejectedHandle.Dispose();

        return tcs.Task;
    }

    public JSValueHandle NewString(string value)
    {
        var written = WriteString(value);
        try
        {
            return new JSValueHandle(this, Exports.NewString(written.Ptr, written.Length));
        }
        finally
        {
            Exports.WasmFree(written.Ptr);
        }
    }

    public JSValueHandle NewNumber(double value) => new(this, Exports.NewNumber(value));
    public JSValueHandle NewObject() => new(this, Exports.NewObject());
    public JSValueHandle NewArray() => new(this, Exports.NewArray());
    public JSValueHandle NewBigInt64(long value) => new(this, Exports.NewBigInt64(unchecked((int)value), unchecked((int)(value >> 32))));

    public JSValueHandle NewSymbol(string description, bool isGlobal)
    {
        var written = WriteString(description);
        try
        {
            return new JSValueHandle(this, Exports.NewSymbol(written.Ptr, written.Length, isGlobal ? 1 : 0));
        }
        finally
        {
            Exports.WasmFree(written.Ptr);
        }
    }

    public JSValueHandle NewArrayBuffer(ReadOnlySpan<byte> data)
    {
        var ptr = Exports.WasmMalloc(data.Length);
        try
        {
            WasmMemoryAccessor.WriteBytes(Exports.Memory, ptr, data);
            return new JSValueHandle(this, Exports.NewArrayBuffer(ptr, data.Length));
        }
        finally
        {
            Exports.WasmFree(ptr);
        }
    }

    public JSValueHandle NewUInt8Array(ReadOnlySpan<byte> data)
    {
        var ptr = Exports.WasmMalloc(data.Length);
        try
        {
            WasmMemoryAccessor.WriteBytes(Exports.Memory, ptr, data);
            return new JSValueHandle(this, Exports.NewUInt8Array(ptr, data.Length));
        }
        finally
        {
            Exports.WasmFree(ptr);
        }
    }

    public JSValueHandle NewHostFunction(string name, HostFunction callback, int argCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);
        if (_hostCallbacks.ContainsKey(name))
        {
            throw new InvalidOperationException($"A host callback named '{name}' is already registered.");
        }

        _hostCallbacks[name] = callback;
        var written = WriteString(name);
        try
        {
            return new JSValueHandle(this, Exports.NewHostFunction(written.Ptr, written.Length, argCount));
        }
        finally
        {
            Exports.WasmFree(written.Ptr);
        }
    }

    public Deferred NewPromise()
    {
        var resolveOutPtr = Exports.WasmMalloc(4);
        var rejectOutPtr = Exports.WasmMalloc(4);
        try
        {
            var promisePtr = Exports.NewPromise(resolveOutPtr, rejectOutPtr);
            var resolvePtr = WasmMemoryAccessor.ReadInt32(Exports.Memory, resolveOutPtr);
            var rejectPtr = WasmMemoryAccessor.ReadInt32(Exports.Memory, rejectOutPtr);
            return new Deferred(this, new JSValueHandle(this, promisePtr), new JSValueHandle(this, resolvePtr), new JSValueHandle(this, rejectPtr));
        }
        finally
        {
            Exports.WasmFree(resolveOutPtr);
            Exports.WasmFree(rejectOutPtr);
        }
    }

    public JSValueHandle NewError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var handle = new JSValueHandle(this, Exports.NewError());
        using var message = NewString(error.Message);
        handle.SetProp("message", message);
        if (!string.IsNullOrEmpty(error.GetType().Name))
        {
            using var name = NewString(error.GetType().Name);
            handle.SetProp("name", name);
        }
        if (!string.IsNullOrEmpty(error.StackTrace))
        {
            using var stack = NewString(error.StackTrace);
            handle.SetProp("stack", stack);
        }
        return handle;
    }

    public JSValueHandle NewError(string message) => NewError(new Exception(message));

    public JSValueHandle CallFunction(JSValueHandle func, JSValueHandle thisValue, params JSValueHandle[] args)
        => ThrowIfException(CallFunctionRaw(func, thisValue, args));

    public object? Dump(JSValueHandle handle) => ValueMarshalling.Dump(this, handle);

    public JSValueHandle HostToHandle(object? value) => ValueMarshalling.HostToHandle(this, value);

    public Snapshot Snapshot()
        => new(Exports.Memory.GetSpan<byte>(0).ToArray(), Exports.StackPointerValue, Exports.GetRuntimePtr(), Exports.GetContextPtr(), Array.Empty<SnapshotExtension>());

    public void RunGc() => Exports.RunGc();

    public int GcThreshold
    {
        get => Exports.GetGcThreshold();
        set => Exports.SetGcThreshold(value);
    }

    /// <summary>
    /// Get detailed memory usage statistics from the QuickJS runtime.
    /// Returns counts and sizes for atoms, strings, objects, functions, etc.
    /// </summary>
    public MemoryUsage GetMemoryUsage()
    {
        // Allocate a buffer for 26 int64 fields (26 * 8 = 208 bytes)
        const int fieldCount = 26;
        var bufPtr = Exports.WasmMalloc(fieldCount * 8);
        try
        {
            Exports.ComputeMemoryUsage(bufPtr);
            var bytes = WasmMemoryAccessor.ReadBytes(Exports.Memory, bufPtr, fieldCount * 8);
            long ReadI64(int idx) => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(idx * 8, 8));
            return new MemoryUsage
            {
                MallocSize = ReadI64(0),
                MallocLimit = ReadI64(1),
                MemoryUsedSize = ReadI64(2),
                MallocCount = ReadI64(3),
                MemoryUsedCount = ReadI64(4),
                AtomCount = ReadI64(5),
                AtomSize = ReadI64(6),
                StrCount = ReadI64(7),
                StrSize = ReadI64(8),
                ObjCount = ReadI64(9),
                ObjSize = ReadI64(10),
                PropCount = ReadI64(11),
                PropSize = ReadI64(12),
                ShapeCount = ReadI64(13),
                ShapeSize = ReadI64(14),
                JsFuncCount = ReadI64(15),
                JsFuncSize = ReadI64(16),
                JsFuncCodeSize = ReadI64(17),
                JsFuncPc2LineCount = ReadI64(18),
                JsFuncPc2LineSize = ReadI64(19),
                CFuncCount = ReadI64(20),
                ArrayCount = ReadI64(21),
                FastArrayCount = ReadI64(22),
                FastArrayElements = ReadI64(23),
                BinaryObjectCount = ReadI64(24),
                BinaryObjectSize = ReadI64(25),
            };
        }
        finally
        {
            Exports.WasmFree(bufPtr);
        }
    }

    public string TypeOf(JSValueHandle handle)
    {
        if (handle.IsUndefined) return "undefined";
        if (handle.IsNull) return "object";
        if (handle.IsBool) return "boolean";
        if (handle.IsNumber) return "number";
        if (handle.IsBigInt) return "bigint";
        if (handle.IsString) return "string";
        if (handle.IsSymbol) return "symbol";
        if (handle.IsFunction) return "function";
        if (handle.IsObject) return "object";
        return "unknown";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // NB: Do NOT call Exports.Destroy() here. The C-side qjs_destroy calls
        // JS_FreeContext which leaves the runtime with a non-empty gc_obj_list.
        // When the Store is disposed the WASM instance tears down, triggering
        // JS_FreeRuntime which asserts on the non-empty list and calls abort().
        // Skipping the explicit JS_FreeContext avoids the assertion entirely —
        // the WASM linear memory is reclaimed by Wasmtime regardless.

        try
        {
            Store.Dispose();
        }
        catch
        {
        }

        _linker.Dispose();
        _module.Dispose();
        _engine.Dispose();
    }
}
