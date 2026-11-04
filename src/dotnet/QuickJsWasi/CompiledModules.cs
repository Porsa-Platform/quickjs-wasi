using System.Collections.Concurrent;
using System.Security.Cryptography;
using Wasmtime;

namespace QuickJsWasi;

/// <summary>
/// The QuickJS WebAssembly module, compiled once per distinct binary and shared by every VM of the process.
/// Compiling it takes ~150 ms and doesn't scale across cores, so compiling it per <see cref="QuickJs.CreateAsync"/> /
/// <see cref="QuickJs.RestoreAsync"/> bounded a host at a handful of VMs a second. A Wasmtime <see cref="Module"/> is
/// immutable and thread-safe: every VM instantiates it in a <see cref="Store"/> of its own, created from the one
/// shared <see cref="Engine"/>. Neither is ever disposed — they live as long as the process.
/// </summary>
internal static class CompiledModules
{
    private static readonly Lazy<byte[]?> DefaultWasm = new(QuickJs.LoadDefaultWasm);
    private static readonly ConcurrentDictionary<string, Lazy<Module>> Modules = new(StringComparer.Ordinal);

    /// <summary>The engine every VM's store, linker and module belong to.</summary>
    public static Engine Engine { get; } = new();

    /// <summary>The compiled module for <paramref name="wasmBytes"/> — the embedded binary when null — compiled the
    /// first time it's asked for, by one caller, the others waiting for it.</summary>
    public static Module For(byte[]? wasmBytes)
    {
        var (key, bytes) = wasmBytes is null
            ? ("default", DefaultWasm.Value ?? throw new InvalidOperationException("quickjs.wasm not found. Set QuickJsOptions.WasmBytes or place quickjs.wasm in Resources/."))
            : (Convert.ToHexString(SHA256.HashData(wasmBytes)), wasmBytes);
        return Modules.GetOrAdd(key, _ => new Lazy<Module>(() => Module.FromBytes(Engine, "quickjs", bytes))).Value;
    }

    /// <summary>How many distinct binaries have been compiled.</summary>
    internal static int Count => Modules.Count;
}
