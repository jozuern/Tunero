using Optimizer.App.Services;
using Optimizer.Core.Tests;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// One change at a time (<see cref="ChangeRunner"/> through <see cref="ChangeRunner.RunExclusiveAsync(string, Func{Task})"/>):
/// a second change while one runs, an update or another Tunero holding the change lock is refused with a reason; a change
/// waits for a running scan instead; a failure is reported and frees the gate. The lock is a fake in memory.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class ChangeRunnerGateTests : IDisposable
{
    private readonly FakeChangeLock _lock = new();
    private readonly List<ResultMessage> _results = [];

    private ChangeRunner Runner(Func<Task>? scan = null)
    {
        var runner = new ChangeRunner(null!, new FakeDialogs(), () => throw new NotSupportedException(), () => false, new ChangeSession(null!, null!, _lock))
        {
            ScanInProgress = scan ?? (() => Task.CompletedTask),
        };
        runner.Status += (_, m) =>
        {
            lock (_results) _results.Add(m);
        };
        return runner;
    }

    public void Dispose()
    {
        ChangeGate.Instance.Busy = false;
        ChangeGate.Instance.Scanning = false;
        ChangeGate.Instance.Updating = false;
    }

    [Fact]
    public async Task ASecondChangeWhileOneRunsIsRefused()
    {
        var runner = Runner();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = runner.RunExclusiveAsync("first", () => hold.Task);
        Assert.True(ChangeGate.Instance.Busy);

        var secondRan = false;
        Assert.False(await runner.RunExclusiveAsync("second", () =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }));
        Assert.False(secondRan);
        Assert.Equal(new ResultMessage(Loc.Instance["Change_Busy"], InfoBarSeverity.Warning), Assert.Single(_results));

        hold.SetResult();
        Assert.True(await first);
        Assert.False(ChangeGate.Instance.Busy);
        Assert.False(_lock.IsHeld);
    }

    [Fact]
    public async Task AChangeWaitsForARunningScan()
    {
        var scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Runner(() => scan.Task);
        ChangeGate.Instance.Scanning = true;
        var ran = false;
        var change = runner.RunExclusiveAsync("after the rescan", () =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        await Task.Delay(50);
        Assert.False(ran);
        Assert.False(change.IsCompleted);

        // The window clears Scanning when its scan ends, then the scan task completes.
        ChangeGate.Instance.Scanning = false;
        scan.SetResult();
        Assert.True(await change.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(ran);
        Assert.Empty(_results);
    }

    [Fact]
    public async Task AnUpdateBlocksChanges()
    {
        var runner = Runner();
        ChangeGate.Instance.Updating = true;
        var ran = false;
        Assert.False(await runner.RunExclusiveAsync("during an update", () =>
        {
            ran = true;
            return Task.CompletedTask;
        }));
        Assert.False(ran);
        Assert.Equal(Loc.Instance["Change_Busy"], Assert.Single(_results).Text);
        Assert.Equal(0, _lock.Taken);
    }

    [Fact]
    public async Task AnotherTuneroHoldingTheLockIsReported()
    {
        var runner = Runner();
        _lock.HeldByOther = true;
        var ran = false;
        Assert.False(await runner.RunExclusiveAsync("while another copy changes the PC", () =>
        {
            ran = true;
            return Task.CompletedTask;
        }));
        Assert.False(ran);
        Assert.Equal(new ResultMessage(Loc.Instance["Change_OtherInstance"], InfoBarSeverity.Warning), Assert.Single(_results));
        Assert.False(ChangeGate.Instance.Busy);

        // Once the other copy is done, the next change runs.
        _lock.HeldByOther = false;
        Assert.True(await runner.RunExclusiveAsync("afterwards", () => Task.CompletedTask));
    }

    [Fact]
    public async Task AFailingChangeIsReportedAndReleasesTheGate()
    {
        var runner = Runner();
        Assert.False(await runner.RunExclusiveAsync("failing", () => throw new InvalidOperationException("disk full")));
        Assert.Equal(new ResultMessage(Loc.Instance.Format("Result_Error", "disk full"), InfoBarSeverity.Error), Assert.Single(_results));
        Assert.False(ChangeGate.Instance.Busy);
        Assert.False(_lock.IsHeld);

        var ran = false;
        Assert.True(await runner.RunExclusiveAsync("next", () =>
        {
            ran = true;
            return Task.CompletedTask;
        }));
        Assert.True(ran);
    }
}
