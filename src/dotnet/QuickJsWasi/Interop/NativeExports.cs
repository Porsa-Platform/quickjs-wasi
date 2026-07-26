using Wasmtime;

namespace QuickJsWasi.Interop;

internal sealed class NativeExports
{
    private readonly Dictionary<string, Function> _functions = new(StringComparer.Ordinal);

    public NativeExports(Store store, Instance instance)
    {
        Instance = instance;
        Memory = instance.GetMemory("memory") ?? throw new InvalidOperationException("Missing exported memory.");
        StackPointer = instance.GetGlobal("__stack_pointer") ?? throw new InvalidOperationException("Missing __stack_pointer export.");
    }

    public Instance Instance { get; }

    public Memory Memory { get; }

    public Global StackPointer { get; }

    public int StackPointerValue
    {
        get => Convert.ToInt32(StackPointer.GetValue());
        set => StackPointer.SetValue(value);
    }

    private Function Fn(string name)
    {
        if (!_functions.TryGetValue(name, out var function))
        {
            function = Instance.GetFunction(name) ?? throw new InvalidOperationException($"Missing export '{name}'.");
            _functions.Add(name, function);
        }

        return function;
    }

    private static ValueBox Box(object value) => value switch
    {
        int i => i,
        uint u => unchecked((int)u),
        long l => l,
        float f => f,
        double d => d,
        string s => s,
        Function fn => fn,
        byte[] bytes => (ValueBox)bytes,
        _ => throw new NotSupportedException($"Unsupported WASM argument type: {value.GetType().FullName}")
    };

    private object? Call(string name, params object[] args) => args.Length == 0 ? Fn(name).Invoke() : Fn(name).Invoke(args.Select(Box).ToArray());

    public void Initialize() => Call("_initialize");
    public int Init() => Convert.ToInt32(Call("qjs_init")!);
    public int Init2(int intrinsics) => Convert.ToInt32(Call("qjs_init2", intrinsics)!);
    public void Destroy() => Call("qjs_destroy");
    public int Eval(int codePtr, int codeLen, int filenamePtr, int flags) => Convert.ToInt32(Call("qjs_eval", codePtr, codeLen, filenamePtr, flags)!);
    public int Compile(int codePtr, int codeLen, int filenamePtr, int evalFlags, int writeFlags, int outLenPtr) => Convert.ToInt32(Call("qjs_compile", codePtr, codeLen, filenamePtr, evalFlags, writeFlags, outLenPtr)!);
    public int EvalBytecode(int bufPtr, int bufLen) => Convert.ToInt32(Call("qjs_eval_bytecode", bufPtr, bufLen)!);
    public int NewString(int ptr, int len) => Convert.ToInt32(Call("qjs_new_string", ptr, len)!);
    public int NewNumber(double value) => Convert.ToInt32(Call("qjs_new_number", value)!);
    public int NewObject() => Convert.ToInt32(Call("qjs_new_object")!);
    public int NewArray() => Convert.ToInt32(Call("qjs_new_array")!);
    public int GetUndefined() => Convert.ToInt32(Call("qjs_get_undefined")!);
    public int GetNull() => Convert.ToInt32(Call("qjs_get_null")!);
    public int GetTrue() => Convert.ToInt32(Call("qjs_get_true")!);
    public int GetFalse() => Convert.ToInt32(Call("qjs_get_false")!);
    public int NewBigInt64(int lo, int hi) => Convert.ToInt32(Call("qjs_new_big_int64", lo, hi)!);
    public int GetBigInt64(int valuePtr, int loOutPtr, int hiOutPtr) => Convert.ToInt32(Call("qjs_get_big_int64", valuePtr, loOutPtr, hiOutPtr)!);
    public double GetFloat64(int valuePtr) => Convert.ToDouble(Call("qjs_get_float64", valuePtr)!);
    public int GetString(int valuePtr) => Convert.ToInt32(Call("qjs_get_string", valuePtr)!);
    public void FreeCString(int ptr) => Call("qjs_free_cstring", ptr);
    public int TypeOf(int valuePtr) => Convert.ToInt32(Call("qjs_typeof", valuePtr)!);
    public int IsException(int valuePtr) => Convert.ToInt32(Call("qjs_is_exception", valuePtr)!);
    public int IsUndefined(int valuePtr) => Convert.ToInt32(Call("qjs_is_undefined", valuePtr)!);
    public int IsNull(int valuePtr) => Convert.ToInt32(Call("qjs_is_null", valuePtr)!);
    public int IsBool(int valuePtr) => Convert.ToInt32(Call("qjs_is_bool", valuePtr)!);
    public int IsNumber(int valuePtr) => Convert.ToInt32(Call("qjs_is_number", valuePtr)!);
    public int IsString(int valuePtr) => Convert.ToInt32(Call("qjs_is_string", valuePtr)!);
    public int IsObject(int valuePtr) => Convert.ToInt32(Call("qjs_is_object", valuePtr)!);
    public int IsArray(int valuePtr) => Convert.ToInt32(Call("qjs_is_array", valuePtr)!);
    public int IsFunction(int valuePtr) => Convert.ToInt32(Call("qjs_is_function", valuePtr)!);
    public int IsError(int valuePtr) => Convert.ToInt32(Call("qjs_is_error", valuePtr)!);
    public int IsPromise(int valuePtr) => Convert.ToInt32(Call("qjs_is_promise", valuePtr)!);
    public int IsSymbol(int valuePtr) => Convert.ToInt32(Call("qjs_is_symbol", valuePtr)!);
    public int IsBigInt(int valuePtr) => Convert.ToInt32(Call("qjs_is_big_int", valuePtr)!);
    public int IsArrayBuffer(int valuePtr) => Convert.ToInt32(Call("qjs_is_array_buffer", valuePtr)!);
    public int IsProxy(int valuePtr) => Convert.ToInt32(Call("qjs_is_proxy", valuePtr)!);
    public int IsMap(int valuePtr) => Convert.ToInt32(Call("qjs_is_map", valuePtr)!);
    public int IsSet(int valuePtr) => Convert.ToInt32(Call("qjs_is_set", valuePtr)!);
    public int IsDate(int valuePtr) => Convert.ToInt32(Call("qjs_is_date", valuePtr)!);
    public int IsRegExp(int valuePtr) => Convert.ToInt32(Call("qjs_is_regexp", valuePtr)!);
    public int IsWeakRef(int valuePtr) => Convert.ToInt32(Call("qjs_is_weak_ref", valuePtr)!);
    public int IsWeakMap(int valuePtr) => Convert.ToInt32(Call("qjs_is_weak_map", valuePtr)!);
    public int IsWeakSet(int valuePtr) => Convert.ToInt32(Call("qjs_is_weak_set", valuePtr)!);
    public int IsDataView(int valuePtr) => Convert.ToInt32(Call("qjs_is_data_view", valuePtr)!);
    public int GetClassId(int valuePtr) => Convert.ToInt32(Call("qjs_get_class_id", valuePtr)!);
    public int GetProxyTarget(int valuePtr) => Convert.ToInt32(Call("qjs_get_proxy_target", valuePtr)!);
    public int GetProxyHandler(int valuePtr) => Convert.ToInt32(Call("qjs_get_proxy_handler", valuePtr)!);
    public int GetBool(int valuePtr) => Convert.ToInt32(Call("qjs_get_bool", valuePtr)!);
    public int DupValue(int valuePtr) => Convert.ToInt32(Call("qjs_dup_value", valuePtr)!);
    public void FreeValue(int valuePtr) => Call("qjs_free_value", valuePtr);
    public int GetGlobal() => Convert.ToInt32(Call("qjs_get_global")!);
    public int GetPropString(int objPtr, int namePtr) => Convert.ToInt32(Call("qjs_get_prop_string", objPtr, namePtr)!);
    public int SetPropString(int objPtr, int namePtr, int valPtr) => Convert.ToInt32(Call("qjs_set_prop_string", objPtr, namePtr, valPtr)!);
    public int DefinePropString(int objPtr, int namePtr, int valPtr, int flags) => Convert.ToInt32(Call("qjs_define_prop_string", objPtr, namePtr, valPtr, flags)!);
    public int DefinePropValue(int objPtr, int keyPtr, int valPtr, int flags) => Convert.ToInt32(Call("qjs_define_prop_value", objPtr, keyPtr, valPtr, flags)!);
    public int GetPropValue(int objPtr, int keyPtr) => Convert.ToInt32(Call("qjs_get_prop_value", objPtr, keyPtr)!);
    public int SetPropValue(int objPtr, int keyPtr, int valPtr) => Convert.ToInt32(Call("qjs_set_prop_value", objPtr, keyPtr, valPtr)!);
    public int GetPropUInt32(int objPtr, uint index) => Convert.ToInt32(Call("qjs_get_prop_uint32", objPtr, index)!);
    public int SetPropUInt32(int objPtr, uint index, int valPtr) => Convert.ToInt32(Call("qjs_set_prop_uint32", objPtr, index, valPtr)!);
    public int GetOwnPropertyNames(int objPtr) => Convert.ToInt32(Call("qjs_get_own_property_names", objPtr)!);
    public int GetOwnPropertyNamesAll(int objPtr) => Convert.ToInt32(Call("qjs_get_own_property_names_all", objPtr)!);
    public int GetOwnPropertyKeys(int objPtr) => Convert.ToInt32(Call("qjs_get_own_property_keys", objPtr)!);
    public int GetOwnPropertyDescriptor(int objPtr, int keyPtr) => Convert.ToInt32(Call("qjs_get_own_property_descriptor", objPtr, keyPtr)!);
    public int HasOwnProperty(int objPtr, int namePtr) => Convert.ToInt32(Call("qjs_has_own_property", objPtr, namePtr)!);
    public int PropertyIsEnumerable(int objPtr, int namePtr) => Convert.ToInt32(Call("qjs_property_is_enumerable", objPtr, namePtr)!);
    public int GetPrototypeOf(int objPtr) => Convert.ToInt32(Call("qjs_get_prototype_of", objPtr)!);
    public int GetValuePtr(int valuePtr) => Convert.ToInt32(Call("qjs_get_value_ptr", valuePtr)!);
    public int Call(int funcPtr, int thisPtr, int argc, int argvPtr) => Convert.ToInt32(Call("qjs_call", funcPtr, thisPtr, argc, argvPtr)!);
    public int NewHostFunction(int namePtr, int nameLen, int argCount) => Convert.ToInt32(Call("qjs_new_host_function", namePtr, nameLen, argCount)!);
    public int NewPromise(int resolveOutPtr, int rejectOutPtr) => Convert.ToInt32(Call("qjs_new_promise", resolveOutPtr, rejectOutPtr)!);
    public int PromiseState(int promisePtr) => Convert.ToInt32(Call("qjs_promise_state", promisePtr)!);
    public int PromiseResult(int promisePtr) => Convert.ToInt32(Call("qjs_promise_result", promisePtr)!);
    public int IsJobPending() => Convert.ToInt32(Call("qjs_is_job_pending")!);
    public int ExecutePendingJob() => Convert.ToInt32(Call("qjs_execute_pending_job")!);
    public int GetException() => Convert.ToInt32(Call("qjs_get_exception")!);
    public int NewError() => Convert.ToInt32(Call("qjs_new_error")!);
    public int Throw(int valuePtr) => Convert.ToInt32(Call("qjs_throw", valuePtr)!);
    public void SetMemoryLimit(int limit) => Call("qjs_set_memory_limit", limit);
    public void SetMaxStackSize(int size) => Call("qjs_set_max_stack_size", size);
    public void SetInterruptHandler(int enable) => Call("qjs_set_interrupt_handler", enable);
    public void SetPromiseRejectionHandler(int enable) => Call("qjs_set_promise_rejection_handler", enable);
    public void SetModuleLoader(int enable) => Call("qjs_set_module_loader", enable);
    public void RunGc() => Call("qjs_run_gc");
    public void SetGcThreshold(int value) => Call("qjs_set_gc_threshold", value);
    public int GetGcThreshold() => Convert.ToInt32(Call("qjs_get_gc_threshold")!);
    public void ComputeMemoryUsage(int outPtr) => Call("qjs_compute_memory_usage", outPtr);
    public int GetRuntimePtr() => Convert.ToInt32(Call("qjs_get_runtime_ptr")!);
    public int GetContextPtr() => Convert.ToInt32(Call("qjs_get_context_ptr")!);
    public void SetRuntimeAndContext(int rtPtr, int ctxPtr) => Call("qjs_set_runtime_and_context", rtPtr, ctxPtr);
    public int NewSymbol(int descPtr, int descLen, int isGlobal) => Convert.ToInt32(Call("qjs_new_symbol", descPtr, descLen, isGlobal)!);
    public int GetSymbolDescription(int valuePtr, int descOutPtr) => Convert.ToInt32(Call("qjs_get_symbol_description", valuePtr, descOutPtr)!);
    public int NewArrayBuffer(int dataPtr, int length) => Convert.ToInt32(Call("qjs_new_array_buffer", dataPtr, length)!);
    public int GetArrayBuffer(int valuePtr, int lenOutPtr) => Convert.ToInt32(Call("qjs_get_array_buffer", valuePtr, lenOutPtr)!);
    public int NewUInt8Array(int dataPtr, int length) => Convert.ToInt32(Call("qjs_new_uint8_array", dataPtr, length)!);
    public int GetTypedArrayBuffer(int valuePtr, int byteOffsetOutPtr, int byteLengthOutPtr, int bytesPerElementOutPtr) => Convert.ToInt32(Call("qjs_get_typed_array_buffer", valuePtr, byteOffsetOutPtr, byteLengthOutPtr, bytesPerElementOutPtr)!);
    public int GetQuickJsVersion() => Convert.ToInt32(Call("qjs_get_quickjs_version")!);
    public int WasmMalloc(int size) => Convert.ToInt32(Call("wasm_malloc", size)!);
    public void WasmFree(int ptr) => Call("wasm_free", ptr);
}
