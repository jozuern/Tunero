using Optimizer.Core.Backup;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;

namespace Optimizer.Core.Tweaks;

/// <summary>What to do when Windows cannot create the restore point a change asks for.</summary>
public enum RestorePointChoice { Enable, Continue, Cancel }

/// <summary>A set applied together: every result, and the counts the result line reports.</summary>
public sealed record BatchOutcome(IReadOnlyList<BatchItemResult> Results)
{
    /// <summary>No restore point and the change did not go on: nothing was changed.</summary>
    public bool StoppedForRestorePoint => Results.Any(r => r.Result.Outcome == ApplyOutcome.NeedsRestorePointDecision);

    public int Applied => Results.Count(r => r.Result.Outcome is ApplyOutcome.Applied or ApplyOutcome.AppliedIneffective);
    public int Failed => Results.Count(r => r.Result.Outcome == ApplyOutcome.Failed);
    public int Skipped => Results.Count - Applied - Failed;
    public bool BackupsNotUpdated => Results.Any(r => r.Result.BackupNotUpdated);

    /// <summary>Stop was pressed: the changes after the last result were not started.</summary>
    public bool Stopped { get; init; }

    public static readonly BatchOutcome Cancelled = new([]);
}

/// <summary>Undo of every change: each tweak with its result, and the counts.</summary>
public sealed record UndoAllOutcome(IReadOnlyList<(TweakDefinition Tweak, RevertResult Result)> Results)
{
    public int Undone => Results.Count(r => r.Result.Success);
    public int Failed => Results.Count - Undone;
}

/// <summary>
/// A change of <see cref="ChangeSession"/> was started outside <see cref="ChangeSession.ExclusiveAsync"/> while another
/// Tunero held the change lock: nothing was changed.
/// </summary>
public sealed class ChangeBusyException() : InvalidOperationException("Another Tunero is changing this PC, so nothing was changed.");

/// <summary>
/// The PC as a scan sees it: the facts that decide what fits (without the ignored results), the state of each tweak, and
/// every result, ignored ones too (<see cref="Recommendations.ForProfile"/> removes those).
/// </summary>
public sealed record PcSnapshot(Facts Facts, IReadOnlyList<TweakStatus> States, IReadOnlySet<string> Applied, IReadOnlyList<Finding> Findings)
{
    /// <summary>The hardware the scan read (the guards of the app removal list need it).</summary>
    public Hardware.HardwareProfile? Profile { get; init; }

    /// <summary>The keys of the results the user ignores (<see cref="IgnoredResults.Key"/>), left out of <see cref="Facts"/>.</summary>
    public IReadOnlyCollection<string> Ignored { get; init; } = [];
}

/// <summary>
/// The steps every change takes, for the window and the command line alike: apply one tweak or a set, and when Windows
/// cannot make the restore point, ask <c>decide</c> (a dialog in the window, a switch on the command line), turn System
/// Protection on if chosen and try again; undo one tweak or all. What differs between the two is only how a decision is
/// made and how a result is shown.
/// <para>
/// The session owns the cross-process change lock (<see cref="Platform.IChangeLock"/>), so two copies of Tunero never
/// change the PC at the same time and no entry point can skip it: every change of the session holds it. A caller that
/// asks or reads before the change (the confirmation, the scan of a command line run) wraps all of it in
/// <see cref="ExclusiveAsync"/>, and the changes inside use that hold. A change started outside one takes the lock for
/// itself and throws <see cref="ChangeBusyException"/> when another Tunero holds it.
/// </para>
/// </summary>
public sealed class ChangeSession(TweakEngine engine, IRestorePoints restorePoints, Platform.IChangeLock changeLock)
{
    // True in the flow that holds the lock and in the tasks it starts (AsyncLocal flows into awaits and Task.Run, never
    // back out or into a flow that started elsewhere): a change inside it uses the hold instead of taking the lock again,
    // which the lock file would refuse even in the same process.
    private readonly AsyncLocal<bool> _holding = new();

    /// <summary>
    /// Runs <paramref name="work"/> as the only change on this PC: takes the lock, waiting up to <paramref name="wait"/>
    /// for another Tunero (a window, a command line run, another account), and releases it when the work ends. False,
    /// without running the work, when the other one did not finish in time. Inside a hold the work simply runs.
    /// </summary>
    public async Task<bool> ExclusiveAsync(Func<Task> work, TimeSpan wait = default)
    {
        if (_holding.Value)
        {
            await work();
            return true;
        }
        using var held = wait > TimeSpan.Zero ? await Task.Run(() => changeLock.TryAcquire(wait)) : changeLock.TryAcquire(wait);
        if (held is null) return false;
        _holding.Value = true;
        try
        {
            await work();
            return true;
        }
        finally
        {
            _holding.Value = false;
        }
    }

    /// <summary>
    /// Records that a scan found these changes still in place after a Windows update (<see cref="TweakEngine.CheckDrift"/>
    /// collects them), so a later reset is not blamed on that update. Writes backups, so only under the lock: inside a hold,
    /// or when no other Tunero holds it right now; else false and the next scan records them.
    /// </summary>
    public bool NoteKept(IReadOnlyCollection<string> tweakIds)
    {
        if (tweakIds.Count == 0) return true;
        if (_holding.Value)
        {
            engine.NoteKept(tweakIds);
            return true;
        }
        using var held = changeLock.TryAcquire(TimeSpan.Zero);
        if (held is null) return false;
        engine.NoteKept(tweakIds);
        return true;
    }

    /// <summary>One change of the session under the lock: the caller's hold, or the lock taken for this change alone.</summary>
    private async Task<T> Locked<T>(Func<Task<T>> change)
    {
        T result = default!;
        if (!await ExclusiveAsync(async () => result = await change())) throw new ChangeBusyException();
        return result;
    }

    /// <summary>Applies one tweak. Null when <paramref name="decide"/> cancelled at the restore point question.</summary>
    public Task<ApplyResult?> ApplyAsync(TweakDefinition t, Facts facts, ApplyOptions options, Func<RestorePointChoice> decide) => Locked(async () =>
    {
        var result = await Task.Run(() => engine.ApplyAsync(t, facts, options));
        if (result.Outcome != ApplyOutcome.NeedsRestorePointDecision) return result;
        return await AfterDecision(options, decide) is { } again ? await Task.Run(() => engine.ApplyAsync(t, facts, again)) : null;
    });

    /// <summary>
    /// Applies a set with one restore point. <see cref="BatchOutcome.Cancelled"/> when <paramref name="decide"/> cancelled;
    /// <see cref="BatchOutcome.StoppedForRestorePoint"/> when even after the decision no restore point came of it.
    /// <paramref name="stop"/> ends it before the next change (<see cref="BatchOutcome.Stopped"/>).
    /// </summary>
    public Task<BatchOutcome> ApplyBatchAsync(IReadOnlyList<TweakDefinition> tweaks, Facts facts, ApplyOptions options,
        Func<RestorePointChoice> decide, IProgress<BatchStep>? progress = null, CancellationToken stop = default)
    {
        if (tweaks.Count == 0) return Task.FromResult(BatchOutcome.Cancelled);
        return Locked(async () =>
        {
            var results = await Task.Run(() => engine.ApplyBatchAsync(tweaks, facts, options, progress, stop));
            if (!results.Any(r => r.Result.Outcome == ApplyOutcome.NeedsRestorePointDecision)) return Outcome(results);
            // Nothing was changed yet: a cancel ends here like a cancelled confirmation.
            if (await AfterDecision(options, decide) is not { } again) return BatchOutcome.Cancelled;
            return Outcome(await Task.Run(() => engine.ApplyBatchAsync(tweaks, facts, again, progress, stop)));
        });

        BatchOutcome Outcome(IReadOnlyList<BatchItemResult> results) => new(results) { Stopped = results.Count < tweaks.Count && stop.IsCancellationRequested };
    }

    /// <summary>The options for the second try after the restore point decision; null for Cancel.</summary>
    private async Task<ApplyOptions?> AfterDecision(ApplyOptions options, Func<RestorePointChoice> decide)
    {
        var choice = decide();
        if (choice == RestorePointChoice.Cancel) return null;
        if (choice == RestorePointChoice.Enable) await Task.Run(() => restorePoints.Enable());
        return options with { ContinueWithoutRestorePoint = choice == RestorePointChoice.Continue };
    }

    /// <summary>
    /// The re-apply task's run (<see cref="ReapplyTask"/>): the changes the Overview lists as reset by a Windows update
    /// that "Apply again" offers (no Expert, boot-critical or anti-cheat sensitive change), applied again in one go. A
    /// change reset without an update (the user turned it back in Windows) stays as it is, and so does one whose target
    /// another change of this app also writes (a catalog tweak and the Services page on one service): each would undo the
    /// other at every sign-in. A restore point is made when Windows can; without one the run goes on, since the user
    /// chose these changes before and nobody is there to ask. <see cref="BatchOutcome.Cancelled"/> when nothing was reset.
    /// </summary>
    public Task<BatchOutcome> ReapplyResetAsync(PcSnapshot pc) => Locked(async () =>
    {
        var known = pc.States.ToDictionary(s => s.Tweak.Id, s => s.State, StringComparer.Ordinal);
        var reset = await Task.Run(() =>
        {
            var shared = engine.Store.All().SelectMany(b => b.Entries.Select(e => (b.TweakId, e.TargetKey)))
                .GroupBy(x => x.TargetKey, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.TweakId).Distinct(StringComparer.Ordinal).Skip(1).Any())
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();
            var drift = engine.CheckDrift(pc.Facts, known, kept);
            engine.NoteKept(kept);
            return drift.Where(d => d.WindowsUpdatedSince && d.Tweak.IsBatchSafe && !d.Backup.Entries.Any(e => shared.Contains(e.TargetKey)))
                .Select(d => d.Tweak).ToList();
        });
        if (reset.Count == 0) return BatchOutcome.Cancelled;
        return await ApplyBatchAsync(reset, pc.Facts, new ApplyOptions { ContinueWithoutRestorePoint = true }, () => RestorePointChoice.Continue);
    });

    public Task<RevertResult> UndoAsync(TweakDefinition t) => Locked(() => Task.Run(() => engine.Revert(t)));

    public Task<UndoAllOutcome> UndoAllAsync(IProgress<BatchStep>? progress = null) => Locked(async () => new UndoAllOutcome(await Task.Run(() => engine.RevertAll(progress))));

    /// <summary>The scan the window runs, without the window: findings, facts and the state of every visible tweak.</summary>
    /// <param name="ignored">The results the user ignores (<see cref="IgnoredResults.Key"/>): left out of the facts, as in the window, and kept in <see cref="PcSnapshot.Ignored"/>.</param>
    public static async Task<PcSnapshot> ScanAsync(CatalogData catalog, TweakCatalog tweaks, TweakEngine engine, Actions.IRegistryRoots registry,
        Func<IReadOnlyDictionary<string, string>?> bcd, IReadOnlyCollection<string>? ignored = null)
    {
        var visible = tweaks.Visible.ToList();
        var scan = await new ScanService(catalog, registry, bcd, engine, visible).RunAsync();
        ignored ??= [];
        var facts = FactsBuilder.Build(scan.Profile, scan.Findings, catalog, registry, ignored);
        var states = await Task.Run(() => engine.DetectAll(visible, facts));
        var applied = states.Where(s => s.State is TweakState.Applied or TweakState.PendingRestart).Select(s => s.Tweak.Id).ToHashSet(StringComparer.Ordinal);
        return new PcSnapshot(facts, states, applied, scan.Findings) { Profile = scan.Profile, Ignored = ignored };
    }
}
