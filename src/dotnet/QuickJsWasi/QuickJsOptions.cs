namespace QuickJsWasi;

public sealed class QuickJsOptions
{
    public byte[]? WasmBytes { get; set; }

    public long? MemoryLimit { get; set; }

    public Func<bool>? InterruptHandler { get; set; }

    public Action<JSValueHandle, JSValueHandle, bool>? OnUnhandledRejection { get; set; }

    public TimezoneOffsetOption? TimezoneOffset { get; set; }

    public int? Intrinsics { get; set; }
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
