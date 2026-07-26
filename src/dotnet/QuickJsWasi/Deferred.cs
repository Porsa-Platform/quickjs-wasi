namespace QuickJsWasi;

public sealed class Deferred
{
    private readonly QuickJs _vm;
    private readonly JSValueHandle _resolve;
    private readonly JSValueHandle _reject;
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;

    internal Deferred(QuickJs vm, JSValueHandle handle, JSValueHandle resolve, JSValueHandle reject)
    {
        _vm = vm;
        Handle = handle;
        _resolve = resolve;
        _reject = reject;
    }

    public JSValueHandle Handle { get; }

    public Task Settled => _tcs.Task;

    public void Resolve(JSValueHandle value)
    {
        _vm.CallFunctionRaw(_resolve, _vm.UndefinedValue, value).Dispose();
        Complete();
    }

    public void Reject(JSValueHandle value)
    {
        _vm.CallFunctionRaw(_reject, _vm.UndefinedValue, value).Dispose();
        Complete();
    }

    private void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _resolve.Dispose();
            _reject.Dispose();
            _tcs.TrySetResult();
        }
    }
}
