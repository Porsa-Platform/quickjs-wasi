namespace QuickJsWasi;

/// <summary>
/// Flags for <see cref="QuickJs.Eval"/>, matching the QuickJS <c>JS_EVAL_*</c> constants.
/// </summary>
public static class EvalFlags
{
    /// <summary>Global script mode (default).</summary>
    public const int TYPE_GLOBAL = 0;

    /// <summary>
    /// Module mode. <see cref="QuickJs.Eval"/> returns a handle to a Promise that resolves
    /// to the module's namespace object (its exports), or rejects if module evaluation throws.
    /// Use together with <see cref="QuickJs.ExecutePendingJobs"/> and <see cref="QuickJs.ResolvePromise"/>.
    /// </summary>
    public const int TYPE_MODULE = 1 << 0;

    /// <summary>Force strict mode.</summary>
    public const int STRICT = 1 << 3;

    /// <summary>Compile only — do not execute.</summary>
    public const int COMPILE_ONLY = 1 << 5;

    /// <summary>Omit stack frames before this eval from Error backtraces.</summary>
    public const int BACKTRACE_BARRIER = 1 << 6;

    /// <summary>
    /// Allow top-level <c>await</c> in global scripts. When used, <see cref="QuickJs.Eval"/>
    /// returns a handle to a Promise that resolves to the completion value. Use together with
    /// <see cref="QuickJs.ExecutePendingJobs"/> and <see cref="QuickJs.ResolvePromise"/>.
    /// </summary>
    public const int ASYNC = 1 << 7;
}
