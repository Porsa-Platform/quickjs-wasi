namespace QuickJsWasi;

/// <summary>
/// Flags for <see cref="QuickJs.Compile"/> controlling what is included in the bytecode output.
/// These can be combined with bitwise OR.
/// </summary>
public static class CompileFlags
{
    /// <summary>Strip source code from the bytecode (smaller output, no source in errors).</summary>
    public const int STRIP_SOURCE = 1 << 4;

    /// <summary>Strip debug information (line numbers, etc.) from the bytecode.</summary>
    public const int STRIP_DEBUG = 1 << 5;
}
