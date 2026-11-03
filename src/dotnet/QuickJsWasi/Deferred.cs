namespace QuickJsWasi;

public sealed class Deferred
{
    private readonly QuickJs _vm;
    private readonly JSValueHandle _resolve;
    private readonly JSValueHandle _reject;
    private TaskCompletionSource? _settled;
    private int _completed;

    internal Deferred(QuickJs vm, JSValueHandle handle, JSValueHandle resolve, JSValueHandle reject)
    {
        _vm = vm;
        Handle = handle;
        _resolve = resolve;
        _reject = reject;
    }

    public JSValueHandle Handle { get; }

    /// <summary>
    /// A task that completes when the QuickJS promise settles (fulfilled or rejected),
    /// which can be later than the <see cref="Resolve"/>/<see cref="Reject"/> call, e.g.
    /// when resolving with a pending promise or thenable. The subscription is attached on
    /// first access without running guest code, and its callback runs as a guest job, so
    /// call <see cref="QuickJs.ExecutePendingJobs"/> to let it complete. Read this while
    /// <see cref="Handle"/> is still alive.
    /// </summary>
    public Task Settled
    {
        get
        {
            if (_settled is null)
            {
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _settled = settled;
                var name = $"__settle:{_vm.NextInternalId()}";
                using var onSettle = _vm.NewInternalFunction(name, (_, _) =>
                {
                    settled.TrySetResult();
                    _vm.UnregisterInternalCallback(name);
                    return _vm.UndefinedValue;
                });
                _vm.PromiseThenRaw(Handle, onSettle, onSettle).Dispose();
            }

            return _settled.Task;
        }
    }

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
        }
    }
}
