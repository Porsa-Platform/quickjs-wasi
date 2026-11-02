using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// Port of the <c>module completion (source|bytecode)</c> suite in <c>test/eval.test.ts</c>:
/// settled module evaluation exposes its namespace (or rejection) without running
/// unrelated guest jobs, while pending evaluation and <c>then</c> exports stay deferred.
/// </summary>
public sealed class ModuleCompletionTests : TestBase
{
    public static TheoryData<string> Modes => new() { "source", "bytecode" };

    public static TheoryData<string, string> ThenExports
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var mode in new[] { "source", "bytecode" })
            {
                data.Add(mode, "export function then(resolve) { resolve(7); }");
                data.Add(mode, "export let then; Promise.resolve().then(() => { then = resolve => resolve(7); });");
                data.Add(mode, "export const then = 7;");
            }

            return data;
        }
    }

    private static JSValueHandle Evaluate(QuickJs vm, string mode, string source) => mode == "source"
        ? vm.Eval(source, "entry.js", EvalFlags.TYPE_MODULE)
        : vm.EvalBytecode(vm.Compile(source, "entry.js", EvalFlags.TYPE_MODULE));

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ExposesSynchronousExports_WithoutRunningUnrelatedJobs(string mode)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = Evaluate(vm, mode, """
            export let count = 0;
            Promise.resolve().then(() => { count = 1; });
            """);
        Assert.True(promise.IsPromise);
        Assert.Equal(1, promise.PromiseState);

        var resolved = Assert.IsType<JSPromiseResult.Fulfilled>(await vm.ResolvePromise(promise));
        using var ns = resolved.Value;
        using (var count = ns.GetProp("count")) Assert.Equal(0d, count.ToNumber());
        vm.ExecutePendingJobs();
        using (var count = ns.GetProp("count")) Assert.Equal(1d, count.ToNumber());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ExposesSynchronousRejection_WithoutRunningUnrelatedJobs(string mode)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = Evaluate(vm, mode, """
            globalThis.count = 0;
            Promise.resolve().then(() => { globalThis.count = 1; });
            throw new Error('init failed');
            """);
        Assert.Equal(2, promise.PromiseState);

        var resolved = Assert.IsType<JSPromiseResult.Rejected>(await vm.ResolvePromise(promise));
        using var error = resolved.Error;
        Assert.Contains("init failed", error.ToManagedString());
        using var count = vm.Eval("count");
        Assert.Equal(0d, count.ToNumber());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task SettlesWithoutConsultingGuestPromiseHooks(string mode)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = Evaluate(vm, mode, """
            Promise.prototype.then = () => { throw new Error('then intercepted'); };
            Object.defineProperty(Promise, Symbol.species, {
              get() { throw new Error('species intercepted'); }
            });
            globalThis.Promise = () => { throw new Error('constructor intercepted'); };
            export const value = 7;
            """);
        Assert.Equal(1, promise.PromiseState);

        var resolved = Assert.IsType<JSPromiseResult.Fulfilled>(await vm.ResolvePromise(promise));
        using var ns = resolved.Value;
        using var value = ns.GetProp("value");
        Assert.Equal(7d, value.ToNumber());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task KeepsTopLevelAwaitPendingUntilItsJobsRun(string mode)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = Evaluate(vm, mode, "export const value = await Promise.resolve(7);");
        Assert.Equal(0, promise.PromiseState);
        vm.ExecutePendingJobs();

        var resolved = Assert.IsType<JSPromiseResult.Fulfilled>(await vm.ResolvePromise(promise));
        using var ns = resolved.Value;
        using var value = ns.GetProp("value");
        Assert.Equal(7d, value.ToNumber());
    }

    [Theory]
    [MemberData(nameof(ThenExports))]
    public async Task PreservesDeferredResolutionOfThenExport(string mode, string source)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        using var promise = Evaluate(vm, mode, source);
        Assert.Equal(0, promise.PromiseState);
        vm.ExecutePendingJobs();

        var resolved = Assert.IsType<JSPromiseResult.Fulfilled>(await vm.ResolvePromise(promise));
        using var value = resolved.Value;
        if (value.IsObject)
        {
            using var then = value.GetProp("then");
            Assert.Equal(7d, then.ToNumber());
        }
        else
        {
            Assert.Equal(7d, value.ToNumber());
        }
    }
}
