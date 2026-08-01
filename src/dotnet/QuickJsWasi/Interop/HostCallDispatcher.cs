using Wasmtime;

namespace QuickJsWasi.Interop;

internal sealed class HostCallDispatcher
{
    private readonly QuickJs _vm;

    public HostCallDispatcher(QuickJs vm)
    {
        _vm = vm;
    }

    public int Invoke(Caller caller, int namePtr, int nameLen, int thisPtr, int argc, int argvPtr)
    {
        var memory = caller.GetMemory("memory") ?? _vm.Exports.Memory;
        var name = WasmMemoryAccessor.ReadUtf8(memory, namePtr, nameLen);
        if (!_vm.TryGetHostCallback(name, out var callback))
        {
            using var err = _vm.NewError(new InvalidOperationException($"Host callback '{name}' is not registered."));
            _vm.Exports.Throw(err.Ptr);
            return 0;
        }

        using var thisHandle = new JSValueHandle(_vm, thisPtr, ownsValue: false);
        var args = new JSValueHandle[argc];
        try
        {
            for (var i = 0; i < argc; i++)
            {
                var ptr = WasmMemoryAccessor.ReadInt32(memory, argvPtr + (i * 4));
                args[i] = new JSValueHandle(_vm, ptr, ownsValue: false);
            }

            var result = callback(thisHandle, args);
            return _vm.Exports.DupValue((result ?? _vm.UndefinedValue).Ptr);
        }
        catch (JSException jsEx)
        {
            _vm.Exports.Throw(jsEx.Handle.Ptr);
            return 0;
        }
        catch (Exception ex)
        {
            using var err = _vm.NewError(ex);
            _vm.Exports.Throw(err.Ptr);
            return 0;
        }
        finally
        {
            foreach (var arg in args)
            {
                arg?.Dispose();
            }
        }
    }
}
