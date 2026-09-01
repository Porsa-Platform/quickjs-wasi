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
            // Throw inside the guest, as the docs promise: NewEphemeralFunction says
            // "calling it after the handle is disposed throws, because the callback
            // is gone" and UnregisterHostCallback says "any QuickJS function still
            // referencing the name will throw when called".
            // Silently returning undefined masked real bugs — a snapshot-restored VM
            // calling a host function never re-registered would return undefined
            // instead of failing loud.
            using var err = _vm.NewError(
                $"Host callback \"{name}\" is not registered: it was unregistered, " +
                "its ephemeral function handle was disposed, or it was never " +
                "re-registered after a snapshot restore.");
            _vm.Exports.Throw(err.Ptr);
            return 0;
        }

        using var thisHandle = new JSValueHandle(_vm, thisPtr, ownsValue: false, isBorrowed: true);
        var args = new JSValueHandle[argc];
        try
        {
            for (var i = 0; i < argc; i++)
            {
                var ptr = WasmMemoryAccessor.ReadInt32(memory, argvPtr + (i * 4));
                args[i] = new JSValueHandle(_vm, ptr, ownsValue: false, isBorrowed: true);
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
