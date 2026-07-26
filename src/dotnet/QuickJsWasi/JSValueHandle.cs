using QuickJsWasi.Interop;

namespace QuickJsWasi;

public sealed class JSValueHandle : IDisposable
{
    private readonly bool _ownsValue;
    private readonly bool _isSingleton;
    private bool _disposed;

    internal JSValueHandle(QuickJs vm, int ptr, bool ownsValue = true, bool isSingleton = false)
    {
        Vm = vm;
        Ptr = ptr;
        _ownsValue = ownsValue;
        _isSingleton = isSingleton;
    }

    public QuickJs Vm { get; }

    public int Ptr { get; }

    public bool IsException => Vm.Exports.IsException(Ptr) != 0;
    public bool IsUndefined => Vm.Exports.IsUndefined(Ptr) != 0;
    public bool IsNull => Vm.Exports.IsNull(Ptr) != 0;
    public bool IsBool => Vm.Exports.IsBool(Ptr) != 0;
    public bool IsNumber => Vm.Exports.IsNumber(Ptr) != 0;
    public bool IsString => Vm.Exports.IsString(Ptr) != 0;
    public bool IsObject => Vm.Exports.IsObject(Ptr) != 0;
    public bool IsArray => Vm.Exports.IsArray(Ptr) != 0;
    public bool IsFunction => Vm.Exports.IsFunction(Ptr) != 0;
    public bool IsError => Vm.Exports.IsError(Ptr) != 0;
    public bool IsPromise => Vm.Exports.IsPromise(Ptr) != 0;
    public bool IsSymbol => Vm.Exports.IsSymbol(Ptr) != 0;
    public bool IsBigInt => Vm.Exports.IsBigInt(Ptr) != 0;
    public bool IsArrayBuffer => Vm.Exports.IsArrayBuffer(Ptr) != 0;
    public bool IsProxy => Vm.Exports.IsProxy(Ptr) != 0;
    public bool IsMap => Vm.Exports.IsMap(Ptr) != 0;
    public bool IsSet => Vm.Exports.IsSet(Ptr) != 0;
    public bool IsDate => Vm.Exports.IsDate(Ptr) != 0;
    public bool IsRegExp => Vm.Exports.IsRegExp(Ptr) != 0;
    public bool IsWeakRef => Vm.Exports.IsWeakRef(Ptr) != 0;
    public bool IsWeakMap => Vm.Exports.IsWeakMap(Ptr) != 0;
    public bool IsWeakSet => Vm.Exports.IsWeakSet(Ptr) != 0;
    public bool IsDataView => Vm.Exports.IsDataView(Ptr) != 0;
    public int ClassId => Vm.Exports.GetClassId(Ptr);
    public int PromiseState => Vm.Exports.PromiseState(Ptr);

    public JSValueHandle Dup() => new(Vm, Vm.Exports.DupValue(Ptr));

    public JSValueHandle GetProp(string name)
    {
        var written = Vm.WriteString(name);
        try
        {
            return new JSValueHandle(Vm, Vm.Exports.GetPropString(Ptr, written.Ptr));
        }
        finally
        {
            Vm.Exports.WasmFree(written.Ptr);
        }
    }

    public JSValueHandle GetProp(JSValueHandle key) => new(Vm, Vm.Exports.GetPropValue(Ptr, key.Ptr));

    public void SetProp(string name, JSValueHandle value)
    {
        var written = Vm.WriteString(name);
        try
        {
            Vm.ThrowOnNegative(Vm.Exports.SetPropString(Ptr, written.Ptr, value.Ptr));
        }
        finally
        {
            Vm.Exports.WasmFree(written.Ptr);
        }
    }

    public void SetProp(JSValueHandle key, JSValueHandle value)
        => Vm.ThrowOnNegative(Vm.Exports.SetPropValue(Ptr, key.Ptr, value.Ptr));

    public void DefineProp(string name, JSValueHandle value, bool configurable = false, bool writable = false, bool enumerable = false)
    {
        var flags = (configurable ? 1 : 0) | (writable ? 2 : 0) | (enumerable ? 4 : 0);
        var written = Vm.WriteString(name);
        try
        {
            Vm.ThrowOnNegative(Vm.Exports.DefinePropString(Ptr, written.Ptr, value.Ptr, flags));
        }
        finally
        {
            Vm.Exports.WasmFree(written.Ptr);
        }
    }

    public string[] Keys()
    {
        using var names = new JSValueHandle(Vm, Vm.Exports.GetOwnPropertyNames(Ptr));
        using var lengthHandle = names.GetProp("length");
        var length = Convert.ToInt32(lengthHandle.ToNumber());
        var result = new string[length];
        for (var i = 0; i < length; i++)
        {
            using var key = new JSValueHandle(Vm, Vm.Exports.GetPropUInt32(names.Ptr, (uint)i));
            result[i] = key.ToManagedString();
        }

        return result;
    }

    public bool HasOwnProperty(string name)
    {
        var written = Vm.WriteString(name);
        try
        {
            return Vm.Exports.HasOwnProperty(Ptr, written.Ptr) != 0;
        }
        finally
        {
            Vm.Exports.WasmFree(written.Ptr);
        }
    }

    public bool PropertyIsEnumerable(string name)
    {
        var written = Vm.WriteString(name);
        try
        {
            return Vm.Exports.PropertyIsEnumerable(Ptr, written.Ptr) != 0;
        }
        finally
        {
            Vm.Exports.WasmFree(written.Ptr);
        }
    }

    public JSValueHandle GetPrototypeOf() => new(Vm, Vm.Exports.GetPrototypeOf(Ptr));
    public JSValueHandle GetProxyTarget() => new(Vm, Vm.Exports.GetProxyTarget(Ptr));
    public JSValueHandle GetProxyHandler() => new(Vm, Vm.Exports.GetProxyHandler(Ptr));
    public double ToNumber() => Vm.Exports.GetFloat64(Ptr);

    public long ToInt64()
    {
        var loPtr = Vm.Exports.WasmMalloc(4);
        var hiPtr = Vm.Exports.WasmMalloc(4);
        try
        {
            if (Vm.Exports.GetBigInt64(Ptr, loPtr, hiPtr) != 0)
            {
                throw new InvalidOperationException("Value is not convertible to a 64-bit BigInt.");
            }

            var lo = WasmMemoryAccessor.ReadUInt32(Vm.Exports.Memory, loPtr);
            var hi = WasmMemoryAccessor.ReadInt32(Vm.Exports.Memory, hiPtr);
            return ((long)hi << 32) | lo;
        }
        finally
        {
            Vm.Exports.WasmFree(loPtr);
            Vm.Exports.WasmFree(hiPtr);
        }
    }

    public byte[] ToByteArray()
    {
        var lengthPtr = Vm.Exports.WasmMalloc(4);
        try
        {
            var dataPtr = Vm.Exports.GetArrayBuffer(Ptr, lengthPtr);
            if (dataPtr != 0)
            {
                var length = WasmMemoryAccessor.ReadInt32(Vm.Exports.Memory, lengthPtr);
                return WasmMemoryAccessor.ReadBytes(Vm.Exports.Memory, dataPtr, length);
            }
        }
        finally
        {
            Vm.Exports.WasmFree(lengthPtr);
        }

        var byteOffsetPtr = Vm.Exports.WasmMalloc(4);
        var byteLengthPtr = Vm.Exports.WasmMalloc(4);
        var bytesPerElementPtr = Vm.Exports.WasmMalloc(4);
        try
        {
            var bufferPtr = Vm.Exports.GetTypedArrayBuffer(Ptr, byteOffsetPtr, byteLengthPtr, bytesPerElementPtr);
            using var bufferHandle = new JSValueHandle(Vm, bufferPtr);
            if (bufferHandle.IsException)
            {
                throw new InvalidOperationException("Value is not an ArrayBuffer or typed array.");
            }

            var byteOffset = WasmMemoryAccessor.ReadInt32(Vm.Exports.Memory, byteOffsetPtr);
            var byteLength = WasmMemoryAccessor.ReadInt32(Vm.Exports.Memory, byteLengthPtr);
            var rawLenPtr = Vm.Exports.WasmMalloc(4);
            try
            {
                var rawPtr = Vm.Exports.GetArrayBuffer(bufferHandle.Ptr, rawLenPtr);
                if (rawPtr == 0)
                {
                    throw new InvalidOperationException("Unable to read typed-array backing store.");
                }

                return WasmMemoryAccessor.ReadBytes(Vm.Exports.Memory, rawPtr + byteOffset, byteLength);
            }
            finally
            {
                Vm.Exports.WasmFree(rawLenPtr);
            }
        }
        finally
        {
            Vm.Exports.WasmFree(byteOffsetPtr);
            Vm.Exports.WasmFree(byteLengthPtr);
            Vm.Exports.WasmFree(bytesPerElementPtr);
        }
    }

    public string ToManagedString()
    {
        var ptr = Vm.Exports.GetString(Ptr);
        if (ptr == 0)
        {
            return string.Empty;
        }

        try
        {
            return Vm.ReadCString(ptr);
        }
        finally
        {
            Vm.Exports.FreeCString(ptr);
        }
    }

    public override string ToString() => ToManagedString();

    public void Dispose()
    {
        if (_disposed || _isSingleton || !_ownsValue)
        {
            return;
        }

        _disposed = true;
        if (!Vm.IsDisposed && Ptr != 0)
        {
            Vm.Exports.FreeValue(Ptr);
        }
    }
}
