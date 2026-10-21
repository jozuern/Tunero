using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// The steps the window and the command line share (<see cref="ChangeSession"/>): the restore point decision for one
/// tweak and for a set, and the counts of a batch and of undo all.
/// </summary>
public class ChangeSessionTests
{
    private static Facts Facts() => new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false)
        .Set("gpu.hasDiscrete", true).Set("gpu.supportsHags", true).Set("memory.totalGb", 32.0).Set("storage.ssdOnly", true)
        .Set("power.modernStandby", false).Set("cpu.x3dMultiCcd", false).Set("os.insider", false);

    private static TweakDefinition T(string id) => TweakCatalog.Current.Get(id) ?? throw new InvalidOperationException(id);

    private static ChangeSession Session(EngineFixture fx) => new(fx.Engine, fx.RestorePoints, fx.ChangeLock);

    [Fact]
    public async Task WithRestorePointsOnNobodyIsAsked()
    {
        using var fx = new EngineFixture();
        var asked = 0;
        var result = await Session(fx).ApplyAsync(T("gpu.hags"), Facts(), new ApplyOptions(), () => { asked++; return RestorePointChoice.Cancel; });
        Assert.Equal(ApplyOutcome.Applied, result!.Outcome);
        Assert.Equal(0, asked);
        Assert.Equal(1, fx.RestorePoints.Created);
    }

    [Fact]
    public async Task CancelAtTheRestorePointQuestionChangesNothing()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var t = T("gpu.hags");
        Assert.Null(await Session(fx).ApplyAsync(t, Facts(), new ApplyOptions(), () => RestorePointChoice.Cancel));
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task ContinueAppliesWithoutARestorePoint()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var result = await Session(fx).ApplyAsync(T("gpu.hags"), Facts(), new ApplyOptions(), () => RestorePointChoice.Continue);
        Assert.Equal(ApplyOutcome.Applied, result!.Outcome);
        Assert.False(fx.RestorePoints.Enabled);
    }

    [Fact]
    public async Task EnableTurnsOnSystemProtectionAndMakesTheRestorePoint()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var result = await Session(fx).ApplyAsync(T("gpu.hags"), Facts(), new ApplyOptions(), () => RestorePointChoice.Enable);
        Assert.Equal(ApplyOutcome.Applied, result!.Outcome);
        Assert.True(fx.RestorePoints.Enabled);
        Assert.True(fx.RestorePoints.Created > 0);
    }

    [Fact]
    public async Task BatchCountsAndUndoAll()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = Session(fx);
        var tweaks = new[] { T("gpu.hags"), T("explorer.fileExtensions") };
        var cancelled = await session.ApplyBatchAsync(tweaks, Facts(), new ApplyOptions(), () => RestorePointChoice.Cancel);
        Assert.Same(BatchOutcome.Cancelled, cancelled);
        Assert.Empty(fx.Store.All());

        var outcome = await session.ApplyBatchAsync(tweaks, Facts(), new ApplyOptions(), () => RestorePointChoice.Continue);
        Assert.False(outcome.StoppedForRestorePoint);
        Assert.Equal((2, 0, 0), (outcome.Applied, outcome.Failed, outcome.Skipped));

        var undone = await session.UndoAllAsync();
        Assert.Equal((2, 0), (undone.Undone, undone.Failed));
        Assert.Empty(fx.Store.All());
    }

    /// <summary>
    /// The session takes the cross-process lock for every change itself, so an entry point that calls it directly
    /// cannot skip it: held during the change, released after, and nothing changes while another Tunero holds it.
    /// </summary>
    [Fact]
    public async Task EveryChangeHoldsTheLockAndNothingChangesWhileAnotherTuneroHoldsIt()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = Session(fx);
        var t = T("gpu.hags");
        var heldWhileDeciding = false;
        var result = await session.ApplyAsync(t, Facts(), new ApplyOptions(), () =>
        {
            heldWhileDeciding = fx.ChangeLock.IsHeld;
            return RestorePointChoice.Continue;
        });
        Assert.Equal(ApplyOutcome.Applied, result!.Outcome);
        Assert.True(heldWhileDeciding);
        Assert.False(fx.ChangeLock.IsHeld);
        Assert.Equal(1, fx.ChangeLock.Taken);

        fx.ChangeLock.HeldByOther = true;
        await Assert.ThrowsAsync<ChangeBusyException>(() => session.UndoAsync(t));
        await Assert.ThrowsAsync<ChangeBusyException>(() => session.UndoAllAsync());
        await Assert.ThrowsAsync<ChangeBusyException>(() => session.ApplyBatchAsync([T("explorer.fileExtensions")], Facts(), new ApplyOptions(),
            () => RestorePointChoice.Continue));
        Assert.NotNull(fx.Store.Get(t.Id));
        Assert.Null(fx.Store.Get("explorer.fileExtensions"));

        fx.ChangeLock.HeldByOther = false;
        Assert.True((await session.UndoAsync(t)).Success);
        Assert.Null(fx.Store.Get(t.Id));
        Assert.Equal(2, fx.ChangeLock.Taken);
    }

    /// <summary>
    /// A caller that confirms before it changes (the window) or scans first (the command line) holds the lock across all
    /// of it with ExclusiveAsync: the changes inside use that hold instead of taking the lock again, and a second flow
    /// in the same process is refused while it lasts.
    /// </summary>
    [Fact]
    public async Task ExclusiveHoldsTheLockAcrossTheWholeWorkAndRefusesASecondFlow()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = Session(fx);
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        BatchOutcome? outcome = null;
        var first = session.ExclusiveAsync(async () =>
        {
            Assert.True(fx.ChangeLock.IsHeld);
            outcome = await session.ApplyBatchAsync([T("gpu.hags"), T("explorer.fileExtensions")], Facts(), new ApplyOptions(),
                () => RestorePointChoice.Continue);
            Assert.True(fx.ChangeLock.IsHeld);
            inside.SetResult();
            await release.Task;
        });
        await inside.Task;

        // Another flow (not started inside the hold) gets neither the lock nor a change in.
        var second = await Task.Run(() => session.ExclusiveAsync(() => throw new InvalidOperationException("must not run")));
        Assert.False(second);
        await Task.Run(() => Assert.ThrowsAsync<ChangeBusyException>(() => session.UndoAllAsync()));

        release.SetResult();
        Assert.True(await first);
        Assert.Equal(2, outcome!.Applied);
        Assert.Equal(1, fx.ChangeLock.Taken);
        Assert.False(fx.ChangeLock.IsHeld);
        Assert.Equal(2, fx.Store.All().Count);
    }

    /// <summary>A failed change still lets go of the lock: the next change, in this window or another Tunero, can run.</summary>
    [Fact]
    public async Task TheLockIsReleasedAfterAnError()
    {
        using var fx = new EngineFixture();
        var session = Session(fx);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExclusiveAsync(() => throw new InvalidOperationException("boom")));
        Assert.False(fx.ChangeLock.IsHeld);
        Assert.True(await session.ExclusiveAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task EnableThatYieldsNoRestorePointStopsTheBatch()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        fx.RestorePoints.CreateFails = true;
        var outcome = await Session(fx).ApplyBatchAsync([T("gpu.hags")], Facts(), new ApplyOptions(), () => RestorePointChoice.Enable);
        Assert.True(outcome.StoppedForRestorePoint);
        Assert.Equal(0, outcome.Applied);
        Assert.Empty(fx.Store.All());
    }
}

