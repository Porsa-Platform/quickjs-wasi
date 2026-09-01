using BenchmarkDotNet.Attributes;

namespace QuickJsWasi.Benchmarks;

/// <summary>
/// Benchmarks covering every major use case of QuickJsWasi:
///
///   - VM creation (cold start)
///   - Eval / script execution
///   - Bytecode compile + run
///   - Host function dispatch
///   - String transport (ASCII and WTF-8 with lone surrogates)
///   - Property operations (get / set / enumerate)
///   - Value marshalling (Dump / HostToHandle)
///   - Snapshot capture and restore
///   - ExportHandle / ImportHandle
///   - Promise creation and resolution
///   - GC
///
/// Run with:
///   dotnet run --configuration Release --project QuickJsWasi.Benchmarks
/// </summary>
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

    // ── VM creation (cold start) ──────────────────────────────────────────

    [Benchmark(Description = "Cold start — create VM")]
    public void ColdStart_CreateVm()
    {
        using var vm = CreateVm();
    }

    [Benchmark(Description = "Cold start — create VM + eval fib(20)")]
    public void ColdStart_Fib20()
    {
        using var vm = CreateVm();
        using var r = vm.Eval("(function f(n){return n<=1?n:f(n-1)+f(n-2)})(20)");
        _ = r.ToNumber();
    }

    [Benchmark(Description = "Cold start — create VM + 1 000 objects")]
    public void ColdStart_1000Objects()
    {
        using var vm = CreateVm();
        using var r = vm.Eval(@"
            var a=[];
            for(var i=0;i<1000;i++)
                a.push({id:i,name:'u'+i,tags:['a','b','c'],
                    meta:{score:i*1.1,flags:{active:i%2===0}}});
            a.length");
        _ = r.ToNumber();
    }

    // ── Hot VM — shared state (pre-warmed, reused across iterations) ──────

    public class HotVm : IDisposable
    {
        public QuickJs Vm { get; }
        public HotVm(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            using var _ = Vm.Eval("1+1"); // warm JIT
        }
        public void Dispose() => Vm.Dispose();
    }

    public IEnumerable<HotVm> HotVmSource() { yield return new HotVm(_wasm); }

    [Benchmark(Description = "Hot VM — fib(20)")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void HotVm_Fib20(HotVm h)
    {
        using var r = h.Vm.Eval("(function f(n){return n<=1?n:f(n-1)+f(n-2)})(20)");
        _ = r.ToNumber();
    }

    [Benchmark(Description = "Hot VM — 10 K array filter+map+reduce")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void HotVm_ArrayChain(HotVm h)
    {
        using var r = h.Vm.Eval(
            "var a=[];for(var i=0;i<10000;i++)a.push(i);" +
            "a.filter(x=>x%2===0).map(x=>x*3).reduce((a,b)=>a+b,0)");
        _ = r.ToNumber();
    }

    // ── Bytecode compile + run ────────────────────────────────────────────

    public class BytecodeState : IDisposable
    {
        public QuickJs Vm { get; }
        public byte[] Bytecode { get; }
        public BytecodeState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            Bytecode = Vm.Compile("function add(a,b){return a+b;}", "add.js");
        }
        public void Dispose() => Vm.Dispose();
    }

    public IEnumerable<BytecodeState> BytecodeSource() { yield return new BytecodeState(_wasm); }

    [Benchmark(Description = "Bytecode — compile fib")]
    [ArgumentsSource(nameof(HotVmSource))]
    public byte[] Bytecode_Compile(HotVm h)
        => h.Vm.Compile("(function f(n){return n<=1?n:f(n-1)+f(n-2)})(20)", "fib.js");

    [Benchmark(Description = "Bytecode — eval pre-compiled")]
    [ArgumentsSource(nameof(BytecodeSource))]
    public void Bytecode_Eval(BytecodeState s)
    {
        using var r = s.Vm.EvalBytecode(s.Bytecode);
    }

    // ── Host function dispatch ────────────────────────────────────────────

    public class HostFnState : IDisposable
    {
        public QuickJs Vm { get; }
        private int _counter;
        public HostFnState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            using var fn = Vm.NewHostFunction("inc", (_, args) =>
            {
                _counter += (int)args[0].ToNumber();
                return Vm.UndefinedValue;
            }, 1);
            Vm.Global.SetProp("inc", fn);
            using var _ = Vm.Eval("1+1"); // warm
        }
        public void Dispose() => Vm.Dispose();
    }

    public IEnumerable<HostFnState> HostFnSource() { yield return new HostFnState(_wasm); }

    [Benchmark(Description = "Host fn — 100 calls from JS")]
    [ArgumentsSource(nameof(HostFnSource))]
    public void HostFn_100Calls(HostFnState s)
    {
        using var _ = s.Vm.Eval("for(var i=0;i<100;i++) inc(i);");
    }

    [Benchmark(Description = "Host fn — CallFunction from .NET")]
    [ArgumentsSource(nameof(HostFnSource))]
    public void HostFn_CallFromDotNet(HostFnState s)
    {
        using var fn = s.Vm.Global.GetProp("inc");
        using var arg = s.Vm.NewNumber(1);
        using var _ = s.Vm.CallFunction(fn, s.Vm.Global, arg);
    }

    // ── String transport ──────────────────────────────────────────────────

    [Benchmark(Description = "String — NewString ASCII 100 chars")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void String_NewAscii(HotVm h)
    {
        using var s = h.Vm.NewString("Hello, QuickJS! This is a plain ASCII string of about a hundred chars total.");
    }

    [Benchmark(Description = "String — NewString with lone surrogate (WTF-8)")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void String_NewWtf8(HotVm h)
    {
        using var s = h.Vm.NewString("prefix\ud800middle\udfff\u0000suffix");
    }

    [Benchmark(Description = "String — guest→host ToManagedString ASCII")]
    [ArgumentsSource(nameof(HotVmSource))]
    public string String_ToManagedAscii(HotVm h)
    {
        using var s = h.Vm.Eval("'Hello, QuickJS! This is a plain ASCII string of about a hundred chars total.'");
        return s.ToManagedString();
    }

    [Benchmark(Description = "String — guest→host ToManagedString WTF-8 (surrogate)")]
    [ArgumentsSource(nameof(HotVmSource))]
    public string String_ToManagedWtf8(HotVm h)
    {
        using var s = h.Vm.Eval("\"\\ud800\\u0000\\udfff\"");
        return s.ToManagedString();
    }

    [Benchmark(Description = "String — Length property")]
    [ArgumentsSource(nameof(HotVmSource))]
    public int String_Length(HotVm h)
    {
        using var s = h.Vm.NewString("Hello, World!");
        return s.Length;
    }

    // ── Property operations ───────────────────────────────────────────────

    public class PropState : IDisposable
    {
        public QuickJs Vm { get; }
        public JSValueHandle Obj { get; }
        public PropState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            Obj = Vm.Eval("({a:1,b:2,c:3,d:4,e:5})");
        }
        public void Dispose() { Obj.Dispose(); Vm.Dispose(); }
    }

    public IEnumerable<PropState> PropSource() { yield return new PropState(_wasm); }

    [Benchmark(Description = "Property — GetProp by name")]
    [ArgumentsSource(nameof(PropSource))]
    public double Prop_GetByName(PropState s)
    {
        using var v = s.Obj.GetProp("c");
        return v.ToNumber();
    }

    [Benchmark(Description = "Property — SetProp by name")]
    [ArgumentsSource(nameof(PropSource))]
    public void Prop_SetByName(PropState s)
    {
        using var v = s.Vm.NewNumber(99);
        s.Obj.SetProp("c", v);
    }

    [Benchmark(Description = "Property — Keys() enumeration (5 props)")]
    [ArgumentsSource(nameof(PropSource))]
    public string[] Prop_Keys(PropState s) => s.Obj.Keys();

    [Benchmark(Description = "Property — HasOwnProperty")]
    [ArgumentsSource(nameof(PropSource))]
    public bool Prop_HasOwn(PropState s) => s.Obj.HasOwnProperty("c");

    // ── Value marshalling ─────────────────────────────────────────────────

    public class MarshalState : IDisposable
    {
        public QuickJs Vm { get; }
        public JSValueHandle Obj { get; }
        public MarshalState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            Obj = Vm.Eval("({x:1,y:'hello',z:[1,2,3],w:{a:true}})");
        }
        public void Dispose() { Obj.Dispose(); Vm.Dispose(); }
    }

    public IEnumerable<MarshalState> MarshalSource() { yield return new MarshalState(_wasm); }

    [Benchmark(Description = "Marshal — Dump nested object")]
    [ArgumentsSource(nameof(MarshalSource))]
    public object? Marshal_DumpObject(MarshalState s) => s.Vm.Dump(s.Obj);

    [Benchmark(Description = "Marshal — HostToHandle Dictionary")]
    [ArgumentsSource(nameof(MarshalSource))]
    public void Marshal_HostToHandle(MarshalState s)
    {
        using var h = s.Vm.HostToHandle(new Dictionary<string, object>
        {
            ["x"] = 1, ["y"] = "hello", ["z"] = new[] { 1, 2, 3 }
        });
    }

    // ── Snapshot capture and restore ──────────────────────────────────────

    public class SnapshotState : IDisposable
    {
        public QuickJs Vm { get; }
        public Snapshot Snap { get; }
        public byte[]? Wasm { get; }
        public SnapshotState(byte[]? wasm)
        {
            Wasm = wasm;
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            using var _ = Vm.Eval("var x = 42; var arr = []; for(var i=0;i<100;i++) arr.push(i);");
            Snap = Vm.Snapshot();
        }
        public void Dispose() => Vm.Dispose();
    }

    public IEnumerable<SnapshotState> SnapshotSource() { yield return new SnapshotState(_wasm); }

    [Benchmark(Description = "Snapshot — capture")]
    [ArgumentsSource(nameof(SnapshotSource))]
    public Snapshot Snapshot_Capture(SnapshotState s) => s.Vm.Snapshot();

    [Benchmark(Description = "Snapshot — serialize to bytes")]
    [ArgumentsSource(nameof(SnapshotSource))]
    public byte[] Snapshot_Serialize(SnapshotState s) => Snapshot.Serialize(s.Snap);

    [Benchmark(Description = "Snapshot — restore VM")]
    [ArgumentsSource(nameof(SnapshotSource))]
    public void Snapshot_Restore(SnapshotState s)
    {
        using var vm = QuickJs.RestoreAsync(s.Snap, new QuickJsOptions { WasmBytes = s.Wasm }).GetAwaiter().GetResult();
    }

    // ── ExportHandle / ImportHandle ───────────────────────────────────────

    public class HandlePortState : IDisposable
    {
        public QuickJs Vm { get; }
        public int Token { get; }
        private readonly JSValueHandle _handle;
        public HandlePortState(byte[]? wasm)
        {
            Vm = QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = wasm }).GetAwaiter().GetResult();
            _handle = Vm.Eval("({ tag: 'portable' })");
            Token = Vm.ExportHandle(_handle);
        }
        public void Dispose() { _handle.Dispose(); Vm.Dispose(); }
    }

    public IEnumerable<HandlePortState> HandlePortSource() { yield return new HandlePortState(_wasm); }

    [Benchmark(Description = "ExportHandle")]
    [ArgumentsSource(nameof(HandlePortSource))]
    public int ExportHandle_Benchmark(HandlePortState s)
    {
        using var h = s.Vm.Eval("({n:1})");
        return s.Vm.ExportHandle(h);
    }

    [Benchmark(Description = "ImportHandle")]
    [ArgumentsSource(nameof(HandlePortSource))]
    public void ImportHandle_Benchmark(HandlePortState s)
    {
        using var h = s.Vm.ImportHandle(s.Token);
    }

    // ── Promise ───────────────────────────────────────────────────────────

    [Benchmark(Description = "Promise — create Deferred")]
    [ArgumentsSource(nameof(HotVmSource))]
    public void Promise_Create(HotVm h)
    {
        var d = h.Vm.NewPromise();
        d.Handle.Dispose();
    }

    [Benchmark(Description = "Promise — resolve + await")]
    [ArgumentsSource(nameof(HotVmSource))]
    public async Task Promise_ResolveAwait(HotVm h)
    {
        using var promise = h.Vm.Eval("new Promise(r => r(42))");
        h.Vm.ExecutePendingJobs();
        var result = await h.Vm.ResolvePromise(promise);
        if (result is JSPromiseResult.Fulfilled f)
            f.Value.Dispose();
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

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return File.ReadAllBytes(c);
        }

        return null;
    }
}
