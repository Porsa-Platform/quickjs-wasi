namespace QuickJsWasi;

/// <summary>
/// Represents the settled result of a QuickJS promise, returned by
/// <see cref="QuickJs.ResolvePromise"/>.
/// </summary>
public abstract record JSPromiseResult
{
    private JSPromiseResult() { }

    /// <summary>The promise fulfilled with the given value. The caller owns the handle.</summary>
    public sealed record Fulfilled(JSValueHandle Value) : JSPromiseResult;

    /// <summary>The promise rejected with the given reason. The caller owns the handle.</summary>
    public sealed record Rejected(JSValueHandle Error) : JSPromiseResult;
}
