using BenchmarkDotNet.Attributes;

namespace QuickJsWasi.Benchmarks;

/// <summary>
/// What a host pays to get a fresh VM, one at a time and many at once — a server restores one per request. Before
/// 1.4.1 each VM compiled the WASM module afresh (~150 ms, not scaling across cores); since, the compiled module is
/// shared and a VM costs its own store and memory only.
///
/// Run with:
///   dotnet run -c Release --project QuickJsWasi.Benchmarks -- --filter *VmCreation*
/// </summary>
[MemoryDiagnoser]
public class VmCreationBenchmarks
{
    private byte[]? _wasm;
    private Snapshot _snapshot = null!;

    /// <summary>How many VMs are restored side by side.</summary>
    [Params(1, 8, 32)]
    public int AtOnce { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _wasm = QuickJsBenchmarks.LoadWasmBytes();
        using var vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = _wasm }).GetAwaiter().GetResult();
        using var seeded = vm.Eval("globalThis.app = { orders: Array.from({ length: 200 }, (_, i) => ({ id: i, total: i * 3 })) }");
        _snapshot = vm.Snapshot();
    }

    [Benchmark(Description = "Create VMs side by side")]
    public Task Create() => Task.WhenAll(Enumerable.Range(0, AtOnce).Select(index => Task.Run(async () =>
    {
        using var vm = await QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = _wasm });
    })));

    [Benchmark(Description = "Restore VMs side by side and run one call")]
    public Task RestoreAndCall() => Task.WhenAll(Enumerable.Range(0, AtOnce).Select(index => Task.Run(async () =>
    {
        using var vm = await QuickJs.RestoreAsync(_snapshot, new QuickJsOptions { WasmBytes = _wasm });
        using var total = vm.Eval("app.orders.reduce((sum, o) => sum + o.total, 0)");
        _ = total.ToNumber();
    })));
}
