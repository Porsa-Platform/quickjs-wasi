using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// <remarks>
/// A binary is identified by the SHA-256 of its content, so two arrays holding the same bytes share one module.
/// Hashing the ~750 KB binary costs ~0.4 ms, a large share of a create, so each array is also remembered by
/// reference: a host that keeps passing the same <c>byte[]</c> pays the hash once. The array must therefore not be
/// mutated after it was first passed — the module compiled from its first content would keep being used.
/// </remarks>
internal static class CompiledModules
{
    private static readonly Lazy<byte[]?> DefaultWasm = new(QuickJs.LoadDefaultWasm);
    private static readonly ConcurrentDictionary<string, Lazy<Module>> Modules = new(StringComparer.Ordinal);

    // Weak keys: an array the host drops is collected along with its entry; the module stays in Modules.
    private static readonly ConditionalWeakTable<byte[], Module> ModulesByArray = new();

    /// <summary>The engine every VM's store, linker and module belong to.</summary>
    public static Engine Engine { get; } = new();

    /// <summary>The compiled module for <paramref name="wasmBytes"/> — the embedded binary when null — compiled the
    /// first time it's asked for, by one caller, the others waiting for it.</summary>
    public static Module For(byte[]? wasmBytes)
    {
        if (wasmBytes is null)
        {
            var bytes = DefaultWasm.Value ?? throw new InvalidOperationException("quickjs.wasm not found. Set QuickJsOptions.WasmBytes or place quickjs.wasm in Resources/.");
            return Compile("default", bytes);
        }

        if (ModulesByArray.TryGetValue(wasmBytes, out var known))
        {
            return known;
        }

        var module = Compile(Convert.ToHexString(SHA256.HashData(wasmBytes)), wasmBytes);
        ModulesByArray.TryAdd(wasmBytes, module);
        return module;
    }

    /// <summary>Whether <paramref name="wasmBytes"/> is remembered by reference, so the next VM skips the hash.</summary>
    internal static bool IsKnown(byte[] wasmBytes) => ModulesByArray.TryGetValue(wasmBytes, out _);

    /// <summary>How many distinct binaries have been compiled.</summary>
    internal static int Count => Modules.Count;

    private static Module Compile(string key, byte[] bytes) =>
        Modules.GetOrAdd(key, _ => new Lazy<Module>(() => Module.FromBytes(Engine, "quickjs", bytes))).Value;
}
