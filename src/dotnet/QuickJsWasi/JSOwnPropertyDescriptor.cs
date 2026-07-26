namespace QuickJsWasi;

/// <summary>
/// An own-property descriptor returned by <see cref="JSValueHandle.GetOwnPropertyDescriptor"/>.
/// Mirrors the result of <c>Object.getOwnPropertyDescriptor()</c>: a data property carries
/// <see cref="Value"/> + <see cref="Writable"/>, an accessor property carries
/// <see cref="Get"/> + <see cref="Set"/>.
///
/// The <see cref="Value"/>, <see cref="Get"/>, and <see cref="Set"/> handles are owned by the
/// caller and must be disposed.
/// </summary>
public sealed record JSOwnPropertyDescriptor : IDisposable
{
    /// <summary>Present for data properties. Caller must dispose.</summary>
    public JSValueHandle? Value { get; init; }

    /// <summary>Present for accessor properties (may be an <c>undefined</c> handle). Caller must dispose.</summary>
    public JSValueHandle? Get { get; init; }

    /// <summary>Present for accessor properties (may be an <c>undefined</c> handle). Caller must dispose.</summary>
    public JSValueHandle? Set { get; init; }

    /// <summary>Present for data properties.</summary>
    public bool? Writable { get; init; }

    /// <summary>Whether the property shows up in enumeration.</summary>
    public bool Enumerable { get; init; }

    /// <summary>Whether the property descriptor may be changed or the property deleted.</summary>
    public bool Configurable { get; init; }

    public void Dispose()
    {
        Value?.Dispose();
        Get?.Dispose();
        Set?.Dispose();
    }
}
