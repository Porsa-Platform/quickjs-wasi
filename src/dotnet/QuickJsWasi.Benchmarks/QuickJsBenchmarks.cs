using BenchmarkDotNet.Attributes;

namespace QuickJsWasi.Benchmarks;

[MemoryDiagnoser]
public class QuickJsBenchmarks
{
    private byte[]? _wasm;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _wasm = LoadWasmBytes();
    }

    private QuickJs CreateVm() =>
        QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = _wasm }).GetAwaiter().GetResult();

    // ── Warm VM (cold start: create VM + first eval) ──────────────────────

    [Benchmark(Description = "Warm VM — init + fib(20)")]
    public void WarmVm_Fibonacci()
    {
        using var vm = CreateVm();
        using var r = vm.Eval(@"
            (function f(n) { return n <= 1 ? n : f(n - 1) + f(n - 2); })(20)
        ");
        _ = r.ToNumber();
    }

    [Benchmark(Description = "Warm VM — init + 1000 objects")]
    public void WarmVm_ObjectConstruction()
    {
        using var vm = CreateVm();
        using var r = vm.Eval(@"
            var users = [];
            for (var i = 0; i < 1000; i++)
                users.push({ id: i, name: 'User ' + i, tags: ['a','b','c'],
                    meta: { created: new Date(2024,0,i%28+1), score: Math.random()*100,
                            flags: { active: i%2===0, verified: i%3===0 } } });
            users.length
        ");
        _ = r.ToNumber();
    }

    // ── Hot VM (reused: eval on pre-warmed VM) ────────────────────────────

    public class HotVmState : IDisposable
    {
        public QuickJs Vm { get; }
        public HotVmState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            for (var i = 0; i < 5; i++)
            {
                using var _ = Vm.Eval("1+1");
            }
        }
        public void Dispose() => Vm.Dispose();
    }

    [Benchmark(Description = "Hot VM — fib(20)")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void HotVm_Fibonacci(HotVmState state)
    {
        using var r = state.Vm.Eval(@"
            (function f(n) { return n <= 1 ? n : f(n - 1) + f(n - 2); })(20)
        ");
        _ = r.ToNumber();
    }

    [Benchmark(Description = "Hot VM — 1000 objects")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void HotVm_ObjectConstruction(HotVmState state)
    {
        using var r = state.Vm.Eval(@"
            var users = [];
            for (var j = 0; j < 1000; j++)
                users.push({ id: j, name: 'User ' + j, tags: ['a','b','c'],
                    meta: { created: new Date(2024,0,j%28+1), score: Math.random()*100,
                            flags: { active: j%2===0, verified: j%3===0 } } });
            users.length
        ");
        _ = r.ToNumber();
    }

    public IEnumerable<HotVmState> HotVmSource()
    {
        yield return new HotVmState(_wasm);
    }

    // ── Hot VM with pre-built data structures ─────────────────────────────

    public class SeededVmState : IDisposable
    {
        public QuickJs Vm { get; }
        public SeededVmState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            for (var i = 0; i < 5; i++)
            {
                using var _ = Vm.Eval("1+1");
            }
            using var _1 = Vm.Eval(@"
                var deep = { val: 0, next: null };
                var cur = deep;
                for (var i = 1; i < 100; i++) { cur.next = { val: i, next: null }; cur = cur.next; }
            ");
            using var _2 = Vm.Eval(@"
                var arr = [];
                for (var i = 0; i < 10000; i++) arr.push(i);
            ");
        }
        public void Dispose() => Vm.Dispose();
    }

    [Benchmark(Description = "Hot VM — linked-list walk")]
    [ArgumentsSource(nameof(SeededVmSource))]
    public void HotVm_NestedAccess(SeededVmState state)
    {
        using var _ = state.Vm.Eval("var cur = deep; var s = 0; while (cur) { s += cur.val; cur = cur.next; } s");
    }

    [Benchmark(Description = "Hot VM — 10K array chain")]
    [ArgumentsSource(nameof(SeededVmSource))]
    public void HotVm_ArrayChain(SeededVmState state)
    {
        using var r = state.Vm.Eval("arr.filter(x => x % 2 === 0).map(x => x * 3).reduce((a, b) => a + b, 0)");
        _ = r.ToNumber();
    }

    public IEnumerable<SeededVmState> SeededVmSource()
    {
        yield return new SeededVmState(_wasm);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static byte[]? LoadWasmBytes()
    {
        var assembly = typeof(QuickJs).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("quickjs.wasm", StringComparison.OrdinalIgnoreCase)
                              || x.EndsWith("quickjs_wasm", StringComparison.OrdinalIgnoreCase));
        if (resource is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        var dir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(dir, "quickjs.wasm"),
            Path.Combine(dir, "Resources", "quickjs.wasm"),
            Path.GetFullPath(Path.Combine(dir, "../../../../QuickJsWasi/Resources/quickjs.wasm")),
            Path.GetFullPath(Path.Combine(dir, "../../quickjs.wasm")),
        };

        return candidates.Select(File.ReadAllBytes).FirstOrDefault();
    }
}
