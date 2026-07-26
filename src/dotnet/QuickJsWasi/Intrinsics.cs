namespace QuickJsWasi;

/// <summary>
/// Bitmask flags for <see cref="QuickJsOptions.Intrinsics"/> controlling which built-in
/// JavaScript features are available in the VM.
///
/// By default all intrinsics are enabled. Pass a bitmask to create a minimal context —
/// for example, omit <see cref="EVAL"/> to prevent <c>eval()</c> usage.
///
/// <c>BaseObjects</c> (Object, Array, Number, String, Boolean, Error, etc.) is always
/// included and cannot be disabled.
/// </summary>
public static class Intrinsics
{
    /// <summary><c>Date</c> constructor and prototype methods.</summary>
    public const int DATE = 1 << 0;

    /// <summary><c>eval()</c> and <c>Function()</c> constructor.</summary>
    public const int EVAL = 1 << 1;

    /// <summary><c>RegExp</c> constructor, prototype methods, and regex literals.</summary>
    public const int REGEXP = 1 << 2;

    /// <summary><c>JSON.parse()</c> and <c>JSON.stringify()</c>.</summary>
    public const int JSON = 1 << 3;

    /// <summary><c>Proxy</c> and <c>Reflect</c>.</summary>
    public const int PROXY = 1 << 4;

    /// <summary><c>Map</c>, <c>Set</c>, <c>WeakMap</c>, <c>WeakSet</c>.</summary>
    public const int MAP_SET = 1 << 5;

    /// <summary><c>ArrayBuffer</c>, <c>TypedArray</c> variants, <c>DataView</c>.</summary>
    public const int TYPED_ARRAYS = 1 << 6;

    /// <summary><c>Promise</c>, <c>async</c>/<c>await</c>.</summary>
    public const int PROMISE = 1 << 7;

    /// <summary>
    /// <c>BigInt</c>. Note: BigInt is part of BaseObjects in quickjs-ng and cannot be fully removed.
    /// </summary>
    public const int BIG_INT = 1 << 8;

    /// <summary><c>WeakRef</c> and <c>FinalizationRegistry</c>.</summary>
    public const int WEAK_REF = 1 << 9;

    /// <summary><c>performance.now()</c>.</summary>
    public const int PERFORMANCE = 1 << 10;

    /// <summary><c>DOMException</c> class.</summary>
    public const int DOM_EXCEPTION = 1 << 11;

    /// <summary>
    /// <c>atob()</c> and <c>btoa()</c> global functions. Also pulls in <see cref="DOM_EXCEPTION"/>
    /// as a dependency (errors thrown by these functions are <c>DOMException</c>s).
    /// </summary>
    public const int ATOB_BTOA = 1 << 12;

    /// <summary>All intrinsics enabled (default).</summary>
    public const int ALL = unchecked((int)0xFFFFFFFF);
}
