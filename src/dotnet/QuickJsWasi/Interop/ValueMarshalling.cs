using System.Collections;
using System.Numerics;
using System.Reflection;

namespace QuickJsWasi.Interop;

public sealed record JsSymbol(string Description, bool IsGlobal);

internal static class ValueMarshalling
{
    public static object? Dump(QuickJs vm, JSValueHandle handle)
        => Dump(vm, handle, new Dictionary<int, object?>());

    private static object? Dump(QuickJs vm, JSValueHandle handle, Dictionary<int, object?> visited)
    {
        var e = vm.Exports;
        if (handle.IsUndefined) return QuickJs.Undefined;
        if (handle.IsNull) return null;
        if (handle.IsBool) return e.GetBool(handle.Ptr) != 0;
        if (handle.IsNumber) return e.GetFloat64(handle.Ptr);
        if (handle.IsString) return handle.ToManagedString();
        if (handle.IsBigInt) return handle.ToInt64();
        if (handle.IsFunction) return QuickJs.Undefined;
        if (handle.IsArrayBuffer) return handle.ToByteArray();
        if (handle.IsSymbol)
        {
            var outPtr = e.WasmMalloc(4);
            try
            {
                var kind = e.GetSymbolDescription(handle.Ptr, outPtr);
                var descPtr = WasmMemoryAccessor.ReadInt32(e.Memory, outPtr);
                using var descHandle = descPtr == 0 ? null : new JSValueHandle(vm, descPtr);
                var description = descHandle?.ToManagedString() ?? string.Empty;
                return kind == 1 ? new JsSymbol(description, true) : QuickJs.Undefined;
            }
            finally
            {
                e.WasmFree(outPtr);
            }
        }

        if (TryDumpTypedArray(vm, handle, out var typedArray))
        {
            return typedArray;
        }

        if (handle.IsError)
        {
            using var name = handle.GetProp("name");
            using var message = handle.GetProp("message");
            using var stack = handle.GetProp("stack");
            var exception = new Exception(message.IsUndefined ? handle.ToManagedString() : message.ToManagedString())
            {
                Source = name.IsUndefined ? null : name.ToManagedString()
            };
            if (!stack.IsUndefined)
            {
                exception.Data["stack"] = stack.ToManagedString();
            }

            return exception;
        }

        if (handle.IsObject)
        {
            var valuePtr = e.GetValuePtr(handle.Ptr);
            if (valuePtr != 0 && visited.TryGetValue(valuePtr, out var existing))
            {
                return existing;
            }
        }

        if (handle.IsArray)
        {
            using var lengthHandle = handle.GetProp("length");
            var length = Convert.ToInt32(lengthHandle.ToNumber());
            var array = new object?[length];
            var valuePtr = e.GetValuePtr(handle.Ptr);
            if (valuePtr != 0)
            {
                visited[valuePtr] = array;
            }

            for (var i = 0; i < length; i++)
            {
                using var item = new JSValueHandle(vm, e.GetPropUInt32(handle.Ptr, (uint)i));
                array[i] = Dump(vm, item, visited);
            }

            return array;
        }

        if (handle.IsObject)
        {
            using var keysHandle = new JSValueHandle(vm, e.GetOwnPropertyNames(handle.Ptr));
            using var lengthHandle = keysHandle.GetProp("length");
            var length = Convert.ToInt32(lengthHandle.ToNumber());
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            var valuePtr = e.GetValuePtr(handle.Ptr);
            if (valuePtr != 0)
            {
                visited[valuePtr] = dict;
            }

            for (var i = 0; i < length; i++)
            {
                using var keyHandle = new JSValueHandle(vm, e.GetPropUInt32(keysHandle.Ptr, (uint)i));
                var key = keyHandle.ToManagedString();
                using var value = handle.GetProp(key);
                dict[key] = Dump(vm, value, visited);
            }

            return dict;
        }

        return QuickJs.Undefined;
    }

    private static bool TryDumpTypedArray(QuickJs vm, JSValueHandle handle, out object? value)
    {
        value = null;
        var e = vm.Exports;
        if (!handle.IsObject)
        {
            return false;
        }

        var byteOffsetPtr = e.WasmMalloc(4);
        var byteLengthPtr = e.WasmMalloc(4);
        var bytesPerElemPtr = e.WasmMalloc(4);
        try
        {
            var bufferPtr = e.GetTypedArrayBuffer(handle.Ptr, byteOffsetPtr, byteLengthPtr, bytesPerElemPtr);
            if (bufferPtr == 0)
            {
                return false;
            }

            using var bufferHandle = new JSValueHandle(vm, bufferPtr);
            if (bufferHandle.IsException)
            {
                return false;
            }

            var byteOffset = WasmMemoryAccessor.ReadInt32(e.Memory, byteOffsetPtr);
            var byteLength = WasmMemoryAccessor.ReadInt32(e.Memory, byteLengthPtr);
            var rawLenPtr = e.WasmMalloc(4);
            try
            {
                var rawPtr = e.GetArrayBuffer(bufferHandle.Ptr, rawLenPtr);
                if (rawPtr == 0)
                {
                    return false;
                }

                value = WasmMemoryAccessor.ReadBytes(e.Memory, rawPtr + byteOffset, byteLength);
                return true;
            }
            finally
            {
                e.WasmFree(rawLenPtr);
            }
        }
        finally
        {
            e.WasmFree(byteOffsetPtr);
            e.WasmFree(byteLengthPtr);
            e.WasmFree(bytesPerElemPtr);
        }
    }

    public static JSValueHandle HostToHandle(QuickJs vm, object? value)
    {
        if (ReferenceEquals(value, QuickJs.Undefined)) return vm.UndefinedValue;
        if (value is null) return vm.NullValue;
        if (value is bool b) return b ? vm.TrueValue : vm.FalseValue;
        if (value is string s) return vm.NewString(s);
        if (value is byte[] bytes) return vm.NewArrayBuffer(bytes);
        if (value is char c) return vm.NewString(c.ToString());
        if (value is Enum) return vm.NewString(value.ToString()!);
        if (value is BigInteger big) return vm.NewBigInt64(checked((long)big));
        if (value is long l) return vm.NewBigInt64(l);
        if (value is int or short or byte or sbyte or ushort or uint or ulong or float or double or decimal)
        {
            return vm.NewNumber(Convert.ToDouble(value));
        }

        if (value is IList list)
        {
            var result = vm.NewArray();
            for (var i = 0; i < list.Count; i++)
            {
                using var item = HostToHandle(vm, list[i]);
                vm.Exports.SetPropUInt32(result.Ptr, (uint)i, item.Ptr);
            }

            return result;
        }

        if (value is IDictionary dictionary)
        {
            var result = vm.NewObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                using var item = HostToHandle(vm, entry.Value);
                result.SetProp(Convert.ToString(entry.Key)!, item);
            }

            return result;
        }

        if (value is JsSymbol symbol && symbol.IsGlobal)
        {
            return vm.NewSymbol(symbol.Description, true);
        }

        var objectResult = vm.NewObject();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            using var propValue = HostToHandle(vm, property.GetValue(value));
            objectResult.SetProp(property.Name, propValue);
        }

        return objectResult;
    }
}
