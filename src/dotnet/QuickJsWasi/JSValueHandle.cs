using QuickJsWasi.Interop;

namespace QuickJsWasi;

public sealed class JSValueHandle : IDisposable
{
    private readonly bool _ownsValue;
    private readonly bool _isSingleton;
    private bool _disposed;

    /// <summary>Whether this handle wraps a value owned by the C trampoline (host-callback args).</summary>
    internal bool _isBorrowed;

    /// <summary>
    /// Extra cleanup to run when this handle is disposed — used by
    /// <see cref="QuickJs.NewEphemeralFunction"/> to unregister its host callback.
    /// </summary>
    internal Action? _onDispose;

    internal JSValueHandle(QuickJs vm, int ptr, bool ownsValue = true, bool isSingleton = false, bool isBorrowed = false)
    {
        Vm = vm;
        Ptr = ptr;
        _ownsValue = ownsValue;
        _isSingleton = isSingleton;
        _isBorrowed = isBorrowed;
        // Singletons and borrowed handles are not scope-tracked:
        // singletons outlive any scope, and borrowed handles are owned by the C caller.
        if (!isSingleton && !isBorrowed) vm._activeScope?.Add(this);
    }

    public QuickJs Vm { get; }

    public int Ptr { get; }

    /// <summary>
    /// Whether <see cref="Dispose"/> has been called on this handle.
    ///
    /// Note that handle methods do not guard against use-after-disposal — reading
    /// from a disposed handle reads freed memory. Check this when a handle's
    /// lifetime is managed elsewhere (e.g. by <see cref="QuickJs.WithScope{T}"/>).
    /// </summary>
    public bool Disposed => _disposed;

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

    /// <summary>
    /// The <c>length</c> property of this value (number of elements for arrays,
    /// number of UTF-16 code units for strings, etc.).
    /// </summary>
    public int Length
    {
        get
        {
            using var lenHandle = GetProp("length");
            return (int)lenHandle.ToNumber();
        }
    }

    public JSValueHandle GetProp(string name)
    {
        if (WasmMemoryAccessor.StringKeyNeedsValuePath(name))
        {
            using var keyHandle = Vm.NewString(name);
            return new JSValueHandle(Vm, Vm.Exports.GetPropValue(Ptr, keyHandle.Ptr));
        }

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
        if (WasmMemoryAccessor.StringKeyNeedsValuePath(name))
        {
            using var keyHandle = Vm.NewString(name);
            Vm.ThrowOnNegative(Vm.Exports.SetPropValue(Ptr, keyHandle.Ptr, value.Ptr));
            return;
        }

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
        if (WasmMemoryAccessor.StringKeyNeedsValuePath(name))
        {
            using var keyHandle = Vm.NewString(name);
            Vm.ThrowOnNegative(Vm.Exports.DefinePropValue(Ptr, keyHandle.Ptr, value.Ptr, flags));
            return;
        }

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

    /// <summary>
    /// Get all own property names including non-enumerable ones
    /// (equivalent to <c>Object.getOwnPropertyNames()</c>).
    /// </summary>
    public string[] GetOwnPropertyNames()
    {
        using var names = new JSValueHandle(Vm, Vm.Exports.GetOwnPropertyNamesAll(Ptr));
        if (names.IsException)
        {
            return Array.Empty<string>();
        }

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

    /// <summary>
    /// Get ALL own property keys — strings and symbols, including non-enumerable
    /// (equivalent to <c>Reflect.ownKeys()</c>).
    ///
    /// String keys are returned as <see cref="string"/>; symbol keys are returned as
    /// <see cref="JSValueHandle"/> instances which the caller must dispose.
    /// </summary>
    public IReadOnlyList<object> GetOwnPropertyKeys()
    {
        using var keys = new JSValueHandle(Vm, Vm.Exports.GetOwnPropertyKeys(Ptr));
        if (keys.IsException)
        {
            return Array.Empty<object>();
        }

        using var lengthHandle = keys.GetProp("length");
        var length = Convert.ToInt32(lengthHandle.ToNumber());
        var result = new List<object>(length);
        for (var i = 0; i < length; i++)
        {
            var keyHandle = new JSValueHandle(Vm, Vm.Exports.GetPropUInt32(keys.Ptr, (uint)i));
            if (keyHandle.IsSymbol)
            {
                result.Add(keyHandle); // caller must dispose
            }
            else
            {
                result.Add(keyHandle.ToManagedString());
                keyHandle.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// Get the own property descriptor for a key without invoking getters
    /// (equivalent to <c>Object.getOwnPropertyDescriptor()</c>).
    ///
    /// Returns <c>null</c> if there is no such own property.
    /// The caller owns and must dispose the returned descriptor's handles.
    /// </summary>
    public JSOwnPropertyDescriptor? GetOwnPropertyDescriptor(string key)
    {
        using var keyHandle = Vm.NewString(key);
        return GetOwnPropertyDescriptor(keyHandle);
    }

    /// <summary>
    /// Get the own property descriptor for a key (string or symbol) without invoking getters
    /// (equivalent to <c>Object.getOwnPropertyDescriptor()</c>).
    ///
    /// Returns <c>null</c> if there is no such own property.
    /// The caller owns and must dispose the returned descriptor's handles.
    /// </summary>
    public JSOwnPropertyDescriptor? GetOwnPropertyDescriptor(JSValueHandle key)
    {
        var descPtr = Vm.Exports.GetOwnPropertyDescriptor(Ptr, key.Ptr);
        if (descPtr == 0) return null;

        using var descHandle = new JSValueHandle(Vm, descPtr);
        if (descHandle.IsException)
        {
            throw new JSException(Vm.GetException());
        }

        using var enumerableHandle = descHandle.GetProp("enumerable");
        using var configurableHandle = descHandle.GetProp("configurable");
        var enumerable = Vm.Exports.GetBool(enumerableHandle.Ptr) != 0;
        var configurable = Vm.Exports.GetBool(configurableHandle.Ptr) != 0;

        if (descHandle.HasOwnProperty("value"))
        {
            using var writableHandle = descHandle.GetProp("writable");
            return new JSOwnPropertyDescriptor
            {
                Value = descHandle.GetProp("value"),
                Writable = Vm.Exports.GetBool(writableHandle.Ptr) != 0,
                Enumerable = enumerable,
                Configurable = configurable,
            };
        }

        return new JSOwnPropertyDescriptor
        {
            Get = descHandle.GetProp("get"),
            Set = descHandle.GetProp("set"),
            Enumerable = enumerable,
            Configurable = configurable,
        };
    }

    public bool HasOwnProperty(string name)
    {
        if (WasmMemoryAccessor.StringKeyNeedsValuePath(name))
        {
            using var keyHandle = Vm.NewString(name);
            return Vm.Exports.HasOwnPropertyValue(Ptr, keyHandle.Ptr) != 0;
        }

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
        if (WasmMemoryAccessor.StringKeyNeedsValuePath(name))
        {
            using var keyHandle = Vm.NewString(name);
            return Vm.Exports.PropertyIsEnumerableValue(Ptr, keyHandle.Ptr) != 0;
        }

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
    public bool ToBool() => ToBoolean();

    /// <summary>
    /// Extract the value as a boolean, applying JavaScript truthiness
    /// (equivalent to <c>!!value</c> inside the VM).
    /// </summary>
    public bool ToBoolean() => Vm.Exports.GetBool(Ptr) != 0;

    /// <summary>
    /// A numeric identity for the underlying heap value, or <c>0</c> for values
    /// that are not heap-allocated (numbers, booleans, <c>null</c>, <c>undefined</c>).
    ///
    /// Two handles to the same underlying object always report the same identity;
    /// two live handles to different objects always report different identities.
    /// This is the value to key a <see cref="Dictionary{TKey,TValue}"/> on when
    /// deduplicating or detecting cycles across handles.
    ///
    /// The identity is only meaningful while the value is alive; it is an address
    /// and may be reused after every handle to the value has been disposed.
    /// </summary>
    public int Identity => Vm.Exports.GetValuePtr(Ptr);

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
        // Use qjs_get_string_len to get WTF-8 bytes and length, preserving lone surrogates and NULs.
        var lenPtr = Vm.Exports.WasmMalloc(4);
        try
        {
            var strPtr = Vm.Exports.GetStringLen(Ptr, lenPtr);
            if (strPtr == 0) return string.Empty;
            try
            {
                var len = WasmMemoryAccessor.ReadInt32(Vm.Exports.Memory, lenPtr);
                if (len <= 0) return string.Empty;
                var bytes = WasmMemoryAccessor.ReadBytes(Vm.Exports.Memory, strPtr, len);
                return WasmMemoryAccessor.DecodeWtf8(bytes);
            }
            finally
            {
                Vm.Exports.FreeCString(strPtr);
            }
        }
        finally
        {
            Vm.Exports.WasmFree(lenPtr);
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
        _onDispose?.Invoke();
        _onDispose = null;
        if (!Vm.IsDisposed && Ptr != 0)
        {
            Vm.Exports.FreeValue(Ptr);
        }
    }
}
