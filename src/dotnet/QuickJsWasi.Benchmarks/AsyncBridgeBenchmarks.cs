using BenchmarkDotNet.Attributes;

namespace QuickJsWasi.Benchmarks;

/// <summary>
/// Scaling benchmarks for the async host bridge (host <see cref="Task"/> → QuickJS promise),
/// parameterised by the number of host tasks in flight at once.
///
///   - Concurrent async host calls drained with <see cref="QuickJs.ExecutePendingJobsAsync"/>
///   - Guest job throughput while host tasks are still pending (per-job bridge bookkeeping)
///
/// Run only these with:
///   dotnet run -c Release --project QuickJsWasi.Benchmarks -- --filter *AsyncBridge*
/// </summary>
[MemoryDiagnoser]
public class AsyncBridgeBenchmarks
{
    private QuickJs _vm = null!;
    private TaskCompletionSource<object?>[] _never = [];

    /// <summary>Number of host tasks in flight at once.</summary>
    [Params(1, 100, 1000)]
    public int N;

    [GlobalSetup(Target = nameof(ConcurrentHostCalls))]
    public void SetupConcurrentHostCalls()
    {
        _vm = CreateVm();

        // Completes on a thread-pool continuation, like real async I/O.
        using var yielding = _vm.NewHostFunction("yielding", async (_, _) =>
        {
            await Task.Yield();
            return (object?)1d;
        });
        _vm.Global.SetProp("yielding", yielding);
    }

    [GlobalSetup(Target = nameof(JobsWhileHostTasksInFlight))]
    public void SetupJobsWhileHostTasksInFlight()
    {
        _vm = CreateVm();

        // N host tasks that stay pending for the whole run (each benchmark runs in its own process).
        _never = new TaskCompletionSource<object?>[N];
        for (var i = 0; i < N; i++)
        {
            _never[i] = new TaskCompletionSource<object?>();
            _vm.HostToHandle(_never[i].Task).Dispose();
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var tcs in _never) tcs.TrySetResult(null);
        _vm.Dispose();
    }

    private static QuickJs CreateVm() =>
        QuickJs.CreateAsync(new QuickJsOptions { WasmBytes = QuickJsBenchmarks.LoadWasmBytes() }).GetAwaiter().GetResult();

    [Benchmark(Description = "Async bridge — N concurrent host calls, ExecutePendingJobsAsync")]
    public async Task<int> ConcurrentHostCalls()
    {
        using var p = _vm.Eval($"Promise.all(Array.from({{ length: {N} }}, () => yielding()))");
        return await _vm.ExecutePendingJobsAsync();
    }

    [Benchmark(Description = "Async bridge — 1 000 guest jobs with N host tasks in flight")]
    public int JobsWhileHostTasksInFlight()
    {
        using var p = _vm.Eval("(async () => { for (let i = 0; i < 1000; i++) await null; })()");
        return _vm.ExecutePendingJobs();
    }
}
