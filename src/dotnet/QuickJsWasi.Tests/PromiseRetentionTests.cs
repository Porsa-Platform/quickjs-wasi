using Xunit;

namespace QuickJsWasi.Tests;

/// <summary>
/// Port of <c>test/promise-retention.test.ts</c>: a <see cref="Deferred"/> releases both
/// resolver functions after its first settlement, and later settlement attempts are no-ops.
/// </summary>
public sealed class PromiseRetentionTests : TestBase
{
    public static TheoryData<string> Settlements => new() { "resolve", "reject" };

    private static void Settle(Deferred deferred, string settlement, JSValueHandle value)
    {
        if (settlement == "resolve") deferred.Resolve(value);
        else deferred.Reject(value);
    }

    [Theory]
    [MemberData(nameof(Settlements))]
    public async Task ReleasesBothResolversAfterSettlement(string settlement)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync(options => options.MemoryLimit = 4 * 1024 * 1024);
        vm.Eval("globalThis.total = 0").Dispose();
        for (var i = 0; i < 32; i++)
        {
            var deferred = vm.NewPromise();
            vm.Global.SetProp("pending", deferred.Handle);
            vm.Eval("pending.then(value => { total += value.length }, value => { total += value.length }); delete globalThis.pending").Dispose();
            using (var value = vm.NewString(new string('x', 256 * 1024)))
            {
                Settle(deferred, settlement, value);
            }

            deferred.Handle.Dispose();
            vm.ExecutePendingJobs();
            vm.RunGc();
        }

        using var total = vm.Eval("total");
        Assert.Equal(32d * 256 * 1024, total.ToNumber());
    }

    [Theory]
    [MemberData(nameof(Settlements))]
    public async Task KeepsFirstOutcomeWhenSettledAgain(string settlement)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var deferred = vm.NewPromise();
        using var value = vm.NewString("first");
        Settle(deferred, settlement, value);
        deferred.Resolve(vm.UndefinedValue);
        deferred.Reject(vm.UndefinedValue);

        var result = await vm.ResolvePromise(deferred.Handle);
        using var handle = result switch
        {
            JSPromiseResult.Fulfilled f => f.Value,
            JSPromiseResult.Rejected r => r.Error,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(settlement == "resolve", result is JSPromiseResult.Fulfilled);
        Assert.Equal("first", handle.ToManagedString());
        deferred.Handle.Dispose();
    }

    [Fact]
    public async Task ContinuesAdoptingPendingPromiseAfterReleasingResolvers()
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        var deferred = vm.NewPromise();
        var inner = vm.NewPromise();
        var settled = deferred.Settled;
        deferred.Resolve(inner.Handle);
        deferred.Reject(vm.UndefinedValue);
        using (var value = vm.NewString("adopted"))
        {
            inner.Resolve(value);
        }

        inner.Handle.Dispose();
        vm.ExecutePendingJobs();
        await settled;

        var result = Assert.IsType<JSPromiseResult.Fulfilled>(await vm.ResolvePromise(deferred.Handle));
        using (result.Value)
        {
            Assert.Equal("adopted", result.Value.ToManagedString());
        }

        deferred.Handle.Dispose();
    }

    [Theory]
    [MemberData(nameof(Settlements))]
    public async Task IgnoresReentrantSettlementWhileResolvingWithThenable(string nested)
    {
        if (!HasWasm) return;
        using var vm = await CreateVmAsync();
        Deferred? deferred = null;
        using (var settleNow = vm.NewHostFunction("settleNow", (_, _) =>
        {
            Settle(deferred!, nested, vm.UndefinedValue);
            return vm.UndefinedValue;
        }))
        {
            vm.Global.SetProp("settleNow", settleNow);
        }

        deferred = vm.NewPromise();
        using var thenable = vm.Eval("""
            ({
              marker: 'thenable',
              get then() {
                settleNow();
                globalThis.keep = [];
                for (let i = 0; i < 5000; i++) keep.push({ x: i }, 'ab'.repeat(i % 7 + 1), [i]);
                return undefined;
              },
            })
            """);
        deferred.Resolve(thenable);
        vm.ExecutePendingJobs();
        Assert.Equal(1, deferred.Handle.PromiseState); // fulfilled

        var result = Assert.IsType<JSPromiseResult.Fulfilled>(await vm.ResolvePromise(deferred.Handle));
        using (result.Value)
        using (var marker = result.Value.GetProp("marker"))
        {
            Assert.Equal("thenable", marker.ToManagedString());
        }

        deferred.Handle.Dispose();
    }
}
