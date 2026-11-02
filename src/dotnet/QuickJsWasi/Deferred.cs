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

    /// <summary>
    /// Resolve the promise. Only the first <see cref="Resolve"/> or <see cref="Reject"/>
    /// call settles it; later calls are no-ops.
    /// </summary>
    public void Resolve(JSValueHandle value) => Settle(_resolve, value);

    /// <summary>
    /// Reject the promise. Only the first <see cref="Resolve"/> or <see cref="Reject"/>
    /// call settles it; later calls are no-ops.
    /// </summary>
    public void Reject(JSValueHandle value) => Settle(_reject, value);

    private void Settle(JSValueHandle resolver, JSValueHandle value)
    {
        // Mark as settled before calling the resolver: resolving with an object
        // synchronously reads its `then` property, which can re-enter host code
        // that calls Resolve()/Reject() again. Those nested calls must be no-ops
        // so they don't free the resolver that is still executing.
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        try
        {
            _vm.CallFunctionRaw(resolver, _vm.UndefinedValue, value).Dispose();
        }
        finally
        {
            // Both functions retain the promise, including its eventual value,
            // so release both once either has been called.
            _resolve.Dispose();
            _reject.Dispose();
            _tcs.TrySetResult();
        }
    }
}
