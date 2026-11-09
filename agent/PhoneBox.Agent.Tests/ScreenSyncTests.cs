using PhoneBox.Agent;
using Xunit;

public sealed class ScreenSyncTests
{
    [Fact]
    public async Task FailedDeviceDoesNotBlockOthersAndIsRetried()
    {
        var calls = new List<string>(); var fail = true;
        var sync = new ScreenSynchronizer(() => true, (device, _, _) =>
        {
            calls.Add(device);
            if (device == "first" && fail) throw new TimeoutException();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<AggregateException>(() => sync.ReconcileAsync(["first", "second"], default));
        Assert.Equal(["first", "second"], calls);
        fail = false;
        await sync.ReconcileAsync(["first", "second"], default);
        Assert.Equal(["first", "second", "first"], calls);
    }

    [Fact]
    public async Task SetupAndPollingAreSerializedAndRecheckTheLatestLock()
    {
        var locked = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<bool>();
        var sync = new ScreenSynchronizer(() => locked, async (_, state, ct) =>
        {
            states.Add(state);
            if (states.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setup = sync.ReconcileAsync(["phone"], deadline.Token, force: true);
        await entered.Task.WaitAsync(deadline.Token);
        locked = true;
        var poll = sync.ReconcileAsync(["phone"], deadline.Token);
        release.SetResult();
        await Task.WhenAll(setup, poll).WaitAsync(deadline.Token);
        Assert.Equal([false, true], states);
        await sync.ReconcileAsync(["phone"], deadline.Token);
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task UnknownSessionNeverWakesThePhoneAndResetReappliesState()
    {
        bool? locked = null; var calls = 0;
        var sync = new ScreenSynchronizer(() => locked, (_, _, _) => { calls++; return Task.CompletedTask; });
        Assert.False(await sync.ReconcileAsync(["phone"], default));
        Assert.Equal(0, calls);
        locked = true;
        Assert.True(await sync.ReconcileAsync(["phone"], default));
        await sync.ResetAsync(default);
        await sync.ReconcileAsync(["phone"], default);
        Assert.Equal(2, calls);
    }
}
