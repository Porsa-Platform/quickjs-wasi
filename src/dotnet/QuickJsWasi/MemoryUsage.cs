namespace QuickJsWasi;

/// <summary>Memory usage statistics from the QuickJS runtime.</summary>
public sealed record MemoryUsage
{
    /// <summary>Total bytes allocated via malloc.</summary>
    public long MallocSize { get; init; }

    /// <summary>Current malloc limit (0 for unlimited).</summary>
    public long MallocLimit { get; init; }

    /// <summary>Total memory used (including overhead).</summary>
    public long MemoryUsedSize { get; init; }

    /// <summary>Number of malloc calls.</summary>
    public long MallocCount { get; init; }

    /// <summary>Number of memory-using objects.</summary>
    public long MemoryUsedCount { get; init; }

    /// <summary>Number of atoms.</summary>
    public long AtomCount { get; init; }

    /// <summary>Atom memory size.</summary>
    public long AtomSize { get; init; }

    /// <summary>Number of strings.</summary>
    public long StrCount { get; init; }

    /// <summary>String memory size.</summary>
    public long StrSize { get; init; }

    /// <summary>Number of objects.</summary>
    public long ObjCount { get; init; }

    /// <summary>Object memory size.</summary>
    public long ObjSize { get; init; }

    /// <summary>Number of properties.</summary>
    public long PropCount { get; init; }

    /// <summary>Property memory size.</summary>
    public long PropSize { get; init; }

    /// <summary>Number of shapes.</summary>
    public long ShapeCount { get; init; }

    /// <summary>Shape memory size.</summary>
    public long ShapeSize { get; init; }

    /// <summary>Number of JS functions.</summary>
    public long JsFuncCount { get; init; }

    /// <summary>JS function memory size.</summary>
    public long JsFuncSize { get; init; }

    /// <summary>JS function code size.</summary>
    public long JsFuncCodeSize { get; init; }

    /// <summary>Number of PC-to-line mappings.</summary>
    public long JsFuncPc2LineCount { get; init; }

    /// <summary>PC-to-line mapping memory size.</summary>
    public long JsFuncPc2LineSize { get; init; }

    /// <summary>Number of C functions.</summary>
    public long CFuncCount { get; init; }

    /// <summary>Number of arrays.</summary>
    public long ArrayCount { get; init; }

    /// <summary>Number of fast arrays.</summary>
    public long FastArrayCount { get; init; }

    /// <summary>Number of fast array elements.</summary>
    public long FastArrayElements { get; init; }

    /// <summary>Number of binary objects (ArrayBuffer, etc.).</summary>
    public long BinaryObjectCount { get; init; }

    /// <summary>Binary object memory size.</summary>
    public long BinaryObjectSize { get; init; }
}
