namespace QuickJsWasi;

public sealed class QuickJsOptions
{
    public byte[]? WasmBytes { get; set; }

    public long? MemoryLimit { get; set; }

    public long? MaxStackSize { get; set; }

    public Func<bool>? InterruptHandler { get; set; }

    public Action<JSValueHandle, JSValueHandle, bool>? OnUnhandledRejection { get; set; }

    /// <summary>
    /// Module loader for ES module <c>import</c> statements. When provided, the VM
    /// can resolve and load modules.
    ///
    /// Both callbacks are <b>synchronous</b> — they must return their result
    /// immediately. The engine calls them from inside the WASM call stack,
    /// which cannot be suspended. Pre-fetch all module sources before evaluating
    /// and serve them from a cache for modules loaded asynchronously.
    ///
    /// Exceptions thrown by either callback propagate to the guest as the
    /// module resolution error.
    /// </summary>
    public ModuleLoaderOptions? ModuleLoader { get; set; }

    public TimezoneOffsetOption? TimezoneOffset { get; set; }

    public int? Intrinsics { get; set; }
}

/// <summary>
/// Callbacks for loading ES modules inside the QuickJS VM.
/// </summary>
public sealed class ModuleLoaderOptions
{
    /// <summary>
    /// Resolve a module specifier relative to the importing module.
    /// Called when an <c>import</c> statement is encountered.
    ///
    /// <para>
    /// If omitted, specifiers are passed through to <see cref="Load"/> unchanged.
    /// </para>
    /// </summary>
    /// <param name="baseName">The name of the module containing the import statement.</param>
    /// <param name="specifier">The raw specifier string (e.g. <c>./foo.js</c>, <c>lodash</c>).</param>
    /// <returns>The normalized/canonical module name.</returns>
    public Func<string, string, string>? Normalize { get; set; }

    /// <summary>
    /// Load the source code for a module.
    /// </summary>
    /// <param name="moduleName">The normalized module name (from <see cref="Normalize"/>, or the raw specifier).</param>
    /// <returns>The module source code as a string.</returns>
    public required Func<string, string> Load { get; set; }
}

public abstract record TimezoneOffsetOption
{
    public static readonly TimezoneOffsetOption Host = new HostTimezoneOffsetOption();

    public static TimezoneOffsetOption Fixed(int minutesWestOfUtc) => new FixedTimezoneOffsetOption(minutesWestOfUtc);

    public static TimezoneOffsetOption Callback(Func<long, int> callback) => new CallbackTimezoneOffsetOption(callback);

    internal abstract int ResolveSeconds(long epochSeconds);

    private sealed record HostTimezoneOffsetOption : TimezoneOffsetOption
    {
        internal override int ResolveSeconds(long epochSeconds)
            => (int)DateTimeOffset.FromUnixTimeSeconds(epochSeconds).Offset.TotalSeconds;
    }

    private sealed record FixedTimezoneOffsetOption(int MinutesWestOfUtc) : TimezoneOffsetOption
    {
        internal override int ResolveSeconds(long epochSeconds) => -MinutesWestOfUtc * 60;
    }

    private sealed record CallbackTimezoneOffsetOption(Func<long, int> Handler) : TimezoneOffsetOption
    {
        internal override int ResolveSeconds(long epochSeconds) => -Handler(epochSeconds) * 60;
    }
}
