using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Tweaks;

/// <summary>State of a tweak on this PC, as detected from the real values.</summary>
public enum TweakState
{
    Applied,
    NotApplied,
    Partial,
    NotApplicable,
    Unsupported,
    AppliedIneffective,
    RevertedByWindows,
    EnforcedByPolicy,
    PendingRestart,

    /// <summary>Undone, but the restored value takes effect only after the next restart (still reads as applied).</summary>
    UndoPendingRestart,
}

/// <summary>A reason that prevents applying, as a label key plus values for the text.</summary>
public sealed record Block(string ReasonKey, string? Detail = null, bool CanOverride = false);

public sealed record TweakStatus(
    TweakDefinition Tweak,
    TweakState State,
    int Impact,
    IReadOnlyList<string> Effects,
    string? ImpactReasonKey,
    bool Recommended,
    IReadOnlyList<Block> Blocks,
    bool HasBackup)
{
    public bool IsOn => State is TweakState.Applied or TweakState.PendingRestart or TweakState.AppliedIneffective or TweakState.Partial;
}

public sealed record ApplyOptions
{
    /// <summary>Expert mode is on (required for Expert and boot-critical tweaks).</summary>
    public bool ExpertMode { get; init; }

    /// <summary>The user confirmed the anti-cheat warning for this tweak.</summary>
    public bool AcknowledgeAntiCheat { get; init; }

    /// <summary>The user chose to continue although no restore point could be created.</summary>
    public bool ContinueWithoutRestorePoint { get; init; }

    /// <summary>
    /// No restore point is tried at all (the first sign-in on a new installation, which has nothing to go back to). A
    /// later change without this option still makes the session's one.
    /// </summary>
    public bool WithoutRestorePoint { get; init; }

    /// <summary>Called right before the restore point is made, which can take a minute (the progress banner says so).</summary>
    public Action? RestorePointStarting { get; init; }
}

public enum ApplyOutcome { Applied, AppliedIneffective, Blocked, NeedsRestorePointDecision, Failed, NothingToDo }

/// <param name="LeftChanged">Failed, and part of the change could not be rolled back: the backup is kept for Undo.</param>
/// <param name="BackupNotUpdated">
/// Applied, but the backup could not be updated with what was written. The originals were saved before the first write,
/// so Undo still restores them; until the next change the app may not know that a restart is pending.
/// </param>
public sealed record ApplyResult(ApplyOutcome Outcome, IReadOnlyList<ChangeLine> Changes, string? Error = null, IReadOnlyList<Block>? Blocks = null, bool LeftChanged = false,
    bool BackupNotUpdated = false);

public sealed record BatchItemResult(TweakDefinition Tweak, ApplyResult Result);

/// <summary>
/// Where a batch or undo all is: <paramref name="Tweak"/> starts after <paramref name="Done"/> of <paramref name="Total"/>,
/// or makes the restore point first (<paramref name="RestorePoint"/>).
/// </summary>
public sealed record BatchStep(int Done, int Total, TweakDefinition? Tweak, bool RestorePoint = false);

public sealed record RevertResult(bool Success, IReadOnlyList<string> AlreadyRevertedByWindows, IReadOnlyList<string> Errors);

/// <summary>
/// A change this app made that is no longer in place. <see cref="AppliedOn"/>: the Windows version it was last known to be
/// in place on (its last apply, or a later scan that still found it, <see cref="TweakBackup.KeptOnVersion"/>).
/// <see cref="WindowsUpdatedSince"/>: the version (build and update revision) differs from that one, so an update is the
/// likely cause.
/// </summary>
public sealed record DriftItem(TweakDefinition Tweak, TweakBackup Backup, string? AppliedOn, string Current)
{
    public bool WindowsUpdatedSince => IsOtherVersion(AppliedOn, Current);

    /// <summary>True when <paramref name="current"/> is another Windows version than <paramref name="on"/> (a build number alone compares by build).</summary>
    public static bool IsOtherVersion(string? on, string current) =>
        on is not null && !string.Equals(on, on.Contains('.') ? current : current.Split('.')[0], StringComparison.Ordinal);
}

/// <summary>
/// Detect -> preflight -> restore point -> backup (first-original) -> apply with per-tweak rollback -> verify -> log
///. Undo restores originals unless Windows already changed the value (feature-update-aware).
/// </summary>
/// <param name="windowsVersion">Full Windows version ("26300.9550"); defaults to the build number.</param>
/// <param name="catalog">The tweak catalog (runtime tweaks are those it does not contain); null = the embedded catalog.</param>
public sealed class TweakEngine(ActionContext ctx, BackupStore store, IRestorePoints restorePoints, string appVersion, int windowsBuild, string? windowsVersion = null,
    TweakCatalog? catalog = null)
{
    private readonly TweakCatalog _catalog = catalog ?? TweakCatalog.Current;

    public string WindowsVersion { get; } = windowsVersion ?? windowsBuild.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public const string RestorePointFrequencyTweak = "system.restorePointFrequency";

    private bool _restorePointDone;

    public ActionContext Context => ctx;
    public BackupStore Store => store;

    // ---------------- detection ----------------

    public IReadOnlyList<TweakStatus> DetectAll(IEnumerable<TweakDefinition> tweaks, Facts facts)
    {
        var list = tweaks.ToList();
        // One read of every backup file for the whole detection instead of several per tweak. It is passed along, not kept
        // in a field, so two detections at once (a scan and a page) never see each other's read.
        var read = store.ReadAll();
        var view = new BackupView(read.Own.ToDictionary(b => b.TweakId, StringComparer.Ordinal), read.OtherAccounts);
        var first = list.Select(t => (t, state: DetectState(t, facts, view))).ToList();
        return first.Select(x => BuildStatus(x.t, x.state, facts, view)).ToList();
    }

    /// <summary>The backups as one detection read them; null outside a detection (each lookup reads the file).</summary>
    private sealed record BackupView(IReadOnlyDictionary<string, TweakBackup> Own, IReadOnlySet<string> OtherAccounts);

    private TweakBackup? BackupOf(string id, BackupView? view) => view is null ? PeekBackup(id) : view.Own.GetValueOrDefault(id);

    private bool HeldByAnotherAccount(string id, BackupView? view) => view?.OtherAccounts.Contains(id) ?? store.HeldByAnotherAccount(id);

    /// <summary>For display and checks only: a backup that is locked right now counts as absent (apply and undo fail instead).</summary>
    private TweakBackup? PeekBackup(string id)
    {
        try
        {
            return store.Get(id);
        }
        catch (BackupUnreadableException ex)
        {
            Log.Warn("engine", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Starts the slow reads of <paramref name="when"/> side by side in the background (a tool takes up to seconds, the
    /// NVIDIA driver settings a quarter of a second to open), so that the tweak detection after the hardware scan finds
    /// them done instead of waiting for each in turn. Only for the app's context (its runner keeps results in
    /// <see cref="ReadCache"/>; test fakes do not). A read that fails is left for the detection to report.
    /// </summary>
    public Task PrefetchReads(IEnumerable<TweakDefinition> tweaks, EarlyRead when)
    {
        if (!ReadCache.IsEnabled(ctx.Processes)) return Task.CompletedTask;
        var actions = tweaks.SelectMany(t => t.Actions).Where(a => a.EarlyRead == when).ToList();
        return Task.WhenAll(actions.Select(a => Task.Run(() =>
        {
            try
            {
                a.Read(ctx);
            }
            catch (Exception ex)
            {
                Log.Debug("engine", $"early read of {a.TargetKey} failed: {ex.Message}");
            }
        })));
    }

    public TweakStatus Detect(TweakDefinition t, Facts facts) => BuildStatus(t, DetectState(t, facts), facts, null);

    private TweakStatus BuildStatus(TweakDefinition t, TweakState state, Facts facts, BackupView? view)
    {
        var (impact, effects, reason) = ImpactFor(t, facts);
        // Expert mode is a UI filter, not a reason to show a tweak as blocked; the anti-cheat warning is kept.
        var blocks = Preflight(t, facts, new ApplyOptions { ExpertMode = true }, view);
        var recommended = state is TweakState.NotApplied or TweakState.Partial or TweakState.RevertedByWindows
                          && t.RecommendWhen?.Evaluate(facts) == true && blocks.Count == 0;
        return new TweakStatus(t, state, impact, effects, reason, recommended, blocks, BackupOf(t.Id, view) is not null);
    }

    public TweakState DetectState(TweakDefinition t, Facts facts) => DetectState(t, facts, null);

    private TweakState DetectState(TweakDefinition t, Facts facts, BackupView? view)
    {
        if (!AppliesTo(t, facts)) return TweakState.NotApplicable;
        using var hold = HoldHives(t);
        var actions = Expand(t);
        var states = actions.Select(a => SafeState(a)).ToList();
        var supported = states.Where(s => s != ActionState.Unsupported).ToList();
        if (supported.Count == 0) return TweakState.Unsupported;
        if (ActivePendingUndo(t, actions) is not null) return TweakState.UndoPendingRestart;

        var appliedCount = supported.Count(s => s == ActionState.Applied);
        var state = appliedCount == supported.Count ? TweakState.Applied : appliedCount == 0 ? TweakState.NotApplied : TweakState.Partial;

        var backup = BackupOf(t.Id, view);
        if (backup is not null)
        {
            // Pending restart: everything reads as applied, except changes that only show their value after the restart.
            if (IsRestartPending(backup) && actions.Select((a, i) => (a, s: states[i])).All(x => x.s != ActionState.NotApplied || x.a.TakesEffectAfterRestart))
                return TweakState.PendingRestart;
            // We applied it, now it is gone: a feature update or another tool reset it (drift). Only
            // targets we changed count: a newly added adapter or a target outside the backup is simply not applied.
            // A target that still holds what the app wrote is not drift either: a newer catalog version asks for
            // another value there, and the tweak simply reads as not (fully) applied.
            if (actions.Select((a, i) => (a, s: states[i])).Any(x => x.s == ActionState.NotApplied && backup.Entry(x.a.TargetKey) is { } entry
                    && !StillHoldsWhatWasWritten(x.a, entry)))
                return TweakState.RevertedByWindows;
        }
        else if (state != TweakState.Applied && facts.Get("device.managed") is true && actions.OfType<RegistryAction>().Any(a => IsPolicyPath(a.Path) && SafeRead(a) is { Existed: true }))
        {
            return TweakState.EnforcedByPolicy;
        }
        return state;
    }

    private bool StillHoldsWhatWasWritten(TweakAction a, BackupEntry entry)
    {
        if (entry.Applied is not { } applied) return false;
        try
        {
            return a.IsStillApplied(ctx, applied);
        }
        catch (Exception ex)
        {
            Log.Warn("engine", $"{a.TargetKey}: could not compare with the written value: {ex.Message}");
            return false;
        }
    }

    private ActionState SafeState(TweakAction a)
    {
        try
        {
            return a.State(ctx);
        }
        catch (Exception ex)
        {
            Log.Warn("engine", $"state of {a.TargetKey} unreadable: {ex.Message}");
            return ActionState.Unsupported;
        }
    }

    public static bool AppliesTo(TweakDefinition t, Facts facts)
    {
        var a = t.AppliesTo;
        var build = facts.Get("os.build") as int? ?? 0;
        if (build != 0 && (build < a.MinBuild || (a.MaxBuild is { } max && build > max))) return false;
        if (a.CpuVendor is { Count: > 0 } cpus && facts.Get("cpu.vendor") is string cpu && !cpus.Contains(cpu, StringComparer.OrdinalIgnoreCase)) return false;
        if (a.GpuVendor is { Count: > 0 } gpus && !gpus.Any(g => facts.Get($"gpu.has{char.ToUpperInvariant(g[0])}{g[1..]}") is true)) return false;
        if (a.FormFactor == "desktop" && facts.Get("system.laptop") is true) return false;
        if (a.FormFactor == "laptop" && facts.Get("system.laptop") is false) return false;
        return a.When?.Evaluate(facts) ?? true;
    }

    public static (int Impact, IReadOnlyList<string> Effects, string? ReasonKey) ImpactFor(TweakDefinition t, Facts facts)
    {
        foreach (var o in t.ImpactOverrides)
            if (o.When.Evaluate(facts)) return (o.Gaming, o.Effect ?? t.Impact.Effect, o.ReasonKey);
        return (t.Impact.Gaming, t.Impact.Effect, null);
    }

    private static bool IsPolicyPath(string path) => path.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase);

    // ---------------- preflight / guard rules ----------------

    public List<Block> Preflight(TweakDefinition t, Facts facts, ApplyOptions options) => Preflight(t, facts, options, null);

    private List<Block> Preflight(TweakDefinition t, Facts facts, ApplyOptions options, BackupView? view)
    {
        var blocks = new List<Block>();
        if (!AppliesTo(t, facts)) blocks.Add(new Block("block.notApplicable"));
        foreach (var c in t.BlockedWhen.Where(c => c.Evaluate(facts)))
            blocks.Add(new Block(c.ReasonKey ?? "block.guardRule"));
        // A guard whose facts could not be read (a probe failed) fails closed: the danger it guards against may be there.
        if (t.BlockedWhen.Any(c => !c.CanEvaluate(facts)) && blocks.Count == 0) blocks.Add(new Block("block.cannotCheck"));
        if (t.AntiCheatSensitive && facts.Get("anticheat.strict") is true && !options.AcknowledgeAntiCheat)
            blocks.Add(new Block("block.antiCheat", facts.Get("anticheat.strictNames") as string, CanOverride: true));
        if (t.EffectiveRisk == Risk.Expert && !options.ExpertMode) blocks.Add(new Block("block.expertMode", CanOverride: true));
        // Conflicts: only changes this app made and has not undone. A value that merely matches the other tweak (the
        // Windows default Balanced plan matches power.balancedPlan) is not a change to protect; this tweak's own backup
        // keeps it as the original, so undo brings it back.
        foreach (var other in t.ConflictsWith.Where(o => BackupOf(o, view) is not null)) blocks.Add(new Block("block.conflict", other));
        // A backup file that cannot be read still holds the true originals: applying again would record the changed
        // values as originals and overwrite it.
        if (store.IsDamaged(t.Id)) blocks.Add(new Block("block.backupDamaged", store.DamagedFile(t.Id)));
        if (HeldByAnotherAccount(t.Id, view)) blocks.Add(new Block("block.otherAccount"));
        if (facts.Get("elevated") is false) blocks.Add(new Block("block.notElevated"));
        return blocks;
    }

    // ---------------- preview ----------------

    public IReadOnlyList<ChangeLine> Preview(TweakDefinition t)
    {
        using var hold = HoldHives(t);
        return Expand(t).Select(a =>
        {
            try
            {
                return a.Change(ctx);
            }
            catch (Exception ex)
            {
                return new ChangeLine(a.TargetKey, "(unreadable)", ex.Message);
            }
        }).ToList();
    }

    // ---------------- apply ----------------

    /// <summary>
    /// "Apply recommended": applies each batch-safe tweak in order with the normal pipeline (one restore point for the
    /// whole batch, per-tweak rollback). Stops early when the restore point decision is needed, and before the next
    /// change when <paramref name="stop"/> is set (a change is never left half done).
    /// </summary>
    public async Task<IReadOnlyList<BatchItemResult>> ApplyBatchAsync(IEnumerable<TweakDefinition> tweaks, Facts facts,
        ApplyOptions options, IProgress<BatchStep>? progress = null, CancellationToken stop = default)
    {
        var results = new List<BatchItemResult>();
        var list = tweaks.ToList();
        foreach (var t in list)
        {
            if (stop.IsCancellationRequested) break;
            if (!t.IsBatchSafe)
            {
                results.Add(new BatchItemResult(t, new ApplyResult(ApplyOutcome.Blocked, [], Blocks: [new Block("block.notBatchSafe")])));
                continue;
            }
            var done = results.Count;
            progress?.Report(new BatchStep(done, list.Count, t));
            var r = await ApplyAsync(t, facts,
                progress is null ? options : options with { RestorePointStarting = () => progress.Report(new BatchStep(done, list.Count, t, RestorePoint: true)) });
            results.Add(new BatchItemResult(t, r));
            if (r.Outcome == ApplyOutcome.NeedsRestorePointDecision) break;
        }
        return results;
    }

    public async Task<ApplyResult> ApplyAsync(TweakDefinition t, Facts facts, ApplyOptions options)
    {
        var blocks = Preflight(t, facts, options);
        if (blocks.Count > 0) return new ApplyResult(ApplyOutcome.Blocked, [], Blocks: blocks);

        var actions = Expand(t);
        IReadOnlyList<ChangeLine> changes;
        PendingUndo? pendingUndo;
        using (HoldHives(t, out var holdError))
        {
            // Without the hive every value reads as unsupported, which is not "already in place".
            if (holdError is not null) return Fail(t, [], holdError);
            changes = Preview(t);
            // After an undo that waits for a restart the running value still matches, but the setting is already reverted.
            pendingUndo = ActivePendingUndo(t, actions);
            if (pendingUndo is null)
            {
                var states = actions.Select(SafeState).ToList();
                // Nothing to change only when something reads as applied: a tweak whose targets are all missing or unreadable
                // here (no network adapter, a tool that failed) is not "already in place".
                if (!states.Contains(ActionState.NotApplied))
                    return states.Contains(ActionState.Applied) ? new ApplyResult(ApplyOutcome.NothingToDo, changes)
                        : new ApplyResult(ApplyOutcome.Blocked, changes, Blocks: [new Block("block.unsupported")]);
            }
        }

        // Safety net: one restore point per session before the first change (secondary to the JSON backup).
        if (!_restorePointDone && !options.WithoutRestorePoint && t.Id != RestorePointFrequencyTweak)
        {
            options.RestorePointStarting?.Invoke();
            var ok = await EnsureRestorePointAsync(facts);
            if (!ok && !options.ContinueWithoutRestorePoint) return new ApplyResult(ApplyOutcome.NeedsRestorePointDecision, changes);
            _restorePointDone = true;
        }
        // Only now: the restore point can take a minute, and the Default profile should not stay loaded meanwhile.
        using var hold = HoldHives(t);

        TweakBackup? existing;
        try
        {
            existing = store.Get(t.Id);
        }
        catch (BackupUnreadableException ex)
        {
            return new ApplyResult(ApplyOutcome.Failed, changes, ex.Message);
        }
        // Get found the file damaged only now (after the preflight) and moved it aside: it still holds the originals.
        if (existing is null && store.IsDamaged(t.Id))
            return new ApplyResult(ApplyOutcome.Blocked, changes, Blocks: [new Block("block.backupDamaged", store.DamagedFile(t.Id))]);
        // Another account's backup holds the originals: a new one would record that account's changed values instead.
        if (existing is null && store.HeldByAnotherAccount(t.Id))
            return new ApplyResult(ApplyOutcome.Blocked, changes, Blocks: [new Block("block.otherAccount")]);
        var backup = existing ?? new TweakBackup
        {
            TweakId = t.Id,
            WindowsBuild = windowsBuild,
            AppVersion = appVersion,
            CatalogVersion = TweakCatalog.Version,
        };

        // Exports before boot-critical or power-plan changes.
        var exportsBefore = backup.Exports.Count;
        try
        {
            if (actions.Any(a => a.IsBootCritical))
            {
                var file = Path.Combine(ctx.ExportFolder, $"bcd-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bcd");
                ctx.Bcd.Export(file);
                backup.Exports.Add(file);
            }
            if (actions.Any(a => a is PowerSettingAction or PowerSchemeAction))
            {
                var scheme = ctx.Power.ActiveScheme();
                var file = Path.Combine(ctx.ExportFolder, $"power-{scheme}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pow");
                ctx.Power.Export(scheme, file);
                backup.Exports.Add(file);
            }
        }
        catch (Exception ex)
        {
            DeleteExports(backup.Exports.Skip(exportsBefore).ToList());
            return Fail(t, changes, $"Export before change failed: {ex.Message}");
        }

        // First-original rule: an entry is written once and never overwritten by a later apply.
        var before = new Dictionary<string, StoredValue>();
        var added = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var a in actions)
            {
                StoredValue? current;
                try
                {
                    current = a.Read(ctx);
                }
                catch (Exception ex)
                {
                    // A read that fails is not a target that is absent: skipping it would leave part of the confirmed change
                    // unwritten and still report it as applied. Nothing is written yet.
                    DeleteExports(backup.Exports.Skip(exportsBefore).ToList());
                    return Fail(t, changes, $"{a.TargetKey} could not be read ({ex.Message}); nothing was changed.");
                }
                if (current is null) continue; // the target does not exist on this PC
                // Undone and not restarted yet: the setting is the restored original, not the running value.
                if (a.TakesEffectAfterRestart && pendingUndo?.Entries.FirstOrDefault(e => e.TargetKey == a.TargetKey) is { } restored) current = restored.Original;
                before[a.TargetKey] = current;
                if (backup.Entry(a.TargetKey) is not { } entry)
                {
                    var label = a.TargetLabel(ctx);
                    backup.Entries.Add(entry = new BackupEntry { TargetKey = a.TargetKey, Description = Docs.Labels.Current.Render("en", label), Label = label, Original = current });
                    added.Add(a.TargetKey);
                }
                // A startup entry its program put back is deleted again: undo restores this content (the program's
                // current command), not the first one, which may point to a file the program has removed since.
                else if (Startup.StartupTweaks.IsDelete(t) && current.Existed && !current.SameAs(entry.Original))
                {
                    var renewed = new BackupEntry { TargetKey = entry.TargetKey, Description = entry.Description, Label = entry.Label, Original = current, Action = entry.Action };
                    backup.Entries[backup.Entries.IndexOf(entry)] = renewed;
                    entry = renewed;
                }
                // The action that wrote the change first restores it (Revert): a later catalog version can write less under
                // the same target (one side of a power setting), and its action would leave the rest changed for good.
                entry.Action ??= JsonSerializer.Serialize(a, TweakCatalog.JsonOptions);
            }
            // Nothing could be read (a tool timed out after the state check): no originals, so nothing may be written.
            if (before.Count == 0)
            {
                // Nothing is changed, so the exports made for this run are not needed.
                DeleteExports(backup.Exports.Skip(exportsBefore).ToList());
                return Fail(t, changes, "The current values could not be read; nothing was changed.");
            }
            // Runtime tweaks keep one id while their content can change (a service's start type, a game list): the latest
            // definition is what detection compares against; each entry restores itself through its own action.
            if (_catalog.Get(t.Id) is null) backup.Definition = JsonSerializer.Serialize(t, TweakCatalog.JsonOptions);
            store.Save(backup); // persisted before the first write, so a crash mid-way still has the originals
        }
        catch (Exception ex)
        {
            // Describe or the first save failed: nothing was written, and the exports made for this run are not needed.
            DeleteExports(backup.Exports.Skip(exportsBefore).ToList());
            return Fail(t, changes, ex.Message);
        }

        // Apply in order; on failure roll back what this run changed (per-tweak transaction).
        var done = new List<TweakAction>();
        foreach (var a in actions.Where(a => before.ContainsKey(a.TargetKey)))
        {
            try
            {
                a.Apply(ctx);
                done.Add(a);
            }
            catch (Exception ex)
            {
                Log.Error("engine", $"{t.Id}: {a.TargetKey} failed, rolling back {done.Count} action(s) and the failed one", ex);
                // The failing action may have written part of its change before it threw (one adapter keyword, the
                // mains side of a power setting): it is rolled back too. A target counts as stuck only when it no
                // longer reads as before, so a write that Windows refused outright is not reported as half-changed.
                var stuck = new List<TweakAction>();
                foreach (var undo in Enumerable.Reverse(done).Prepend(a))
                {
                    var restored = true;
                    try
                    {
                        undo.Restore(ctx, before[undo.TargetKey]);
                    }
                    catch (Exception rex)
                    {
                        restored = false;
                        Log.Error("engine", $"rollback of {undo.TargetKey} failed", rex);
                    }
                    var now = SafeRead(undo);
                    // A change that takes effect after a restart still reads as before: only the restore itself tells.
                    if (undo.TakesEffectAfterRestart ? !restored : now is not null ? !now.SameAs(before[undo.TargetKey]) : !restored) stuck.Add(undo);
                }
                if (stuck.Count > 0)
                {
                    // Part of the change is still on the system: keep the backup so Undo can restore it later. Entries this
                    // run added for targets that were rolled back cleanly are dropped; they would later read as "reset
                    // by Windows" and raise a drift notice.
                    backup.Entries.RemoveAll(e => added.Contains(e.TargetKey) && !stuck.Any(s => s.TargetKey == e.TargetKey));
                    foreach (var s in stuck)
                        if (backup.Entry(s.TargetKey) is { } e) e.Applied = SafeApplied(s);
                    backup.LastApplied = DateTimeOffset.Now;
                    backup.AppliedOnVersion = WindowsVersion;
                    backup.KeptOnVersion = null;
                    var kept = SaveAfterChange(() => store.Save(backup), t.Id);
                    return Fail(t, changes, $"{ex.Message} (rollback failed for {string.Join(", ", stuck.Select(s => s.TargetKey))}; " +
                        (kept ? "the backup is kept, use Undo)" : "the backup could not be updated; it still holds the original values, use Undo)")) with { LeftChanged = true };
                }
                // Everything was rolled back: the entries this run added go (with an earlier backup still applied they would
                // read as "reset by Windows"), and the backup goes to history when nothing of this tweak remains applied.
                backup.Entries.RemoveAll(e => added.Contains(e.TargetKey));
                // Nothing of this run is left, so neither are its exports (each failed attempt would leave a .pow file).
                var runExports = backup.Exports.Skip(exportsBefore).ToList();
                DeleteExports(runExports);
                backup.Exports.RemoveRange(exportsBefore, runExports.Count);
                if (backup.Entries.All(e => before.TryGetValue(e.TargetKey, out var b) && b.SameAs(e.Original)))
                    SaveAfterChange(() => store.Archive(t.Id), t.Id);
                else
                    SaveAfterChange(() => store.Save(backup), t.Id);
                return Fail(t, changes, ex.Message);
            }
        }

        // A change that takes effect after a restart still reads the old value: record what was written instead, or undo
        // would later take the new running value for a change by Windows and skip it.
        foreach (var a in done)
            if (backup.Entry(a.TargetKey) is { } e) e.Applied = a.MergeApplied(e.Applied, SafeApplied(a));
        backup.LastApplied = DateTimeOffset.Now;
        backup.AppliedOnVersion = WindowsVersion;
        backup.KeptOnVersion = null;
        if (t.Restart || t.Verify == "afterRestart" || done.Any(a => a.TakesEffectAfterRestart)) backup.PendingRestartSince = DateTimeOffset.Now;
        // The originals were saved before the first write; this save adds what was written and when. When it fails, the
        // change is in place and Undo still has the originals, but the result says so.
        var saved = SaveAfterChange(() => store.Save(backup), t.Id);
        if (pendingUndo is not null) store.ClearPendingUndo(t.Id);

        // The data lets the Changes page show the line in the UI language; the message stays English for the log file.
        foreach (var line in changes)
            Log.Info("change", $"{t.Id}: {line.Target}", new { tweak = t.Id, kind = "apply", target = line.Target, label = line.Label, before = line.Before, after = line.After });

        // Verify: re-read every action. "afterRestart" tweaks are confirmed on the next scan after a reboot.
        var ineffective = t.Verify != "afterRestart" && done.Any(a => !a.TakesEffectAfterRestart && SafeState(a) != ActionState.Applied);
        return new ApplyResult(ineffective ? ApplyOutcome.AppliedIneffective : ApplyOutcome.Applied, changes, BackupNotUpdated: !saved);
    }

    private ApplyResult Fail(TweakDefinition t, IReadOnlyList<ChangeLine> changes, string error)
    {
        Log.Error("engine", $"{t.Id}: apply failed: {error}");
        return new ApplyResult(ApplyOutcome.Failed, changes, error);
    }

    private async Task<bool> EnsureRestorePointAsync(Facts facts)
    {
        if (restorePoints.IsEnabled() != true) return false;
        // Lift the 24 h limit through a normal, backed-up tweak, so undo puts the original value back.
        var freq = _catalog.Get(RestorePointFrequencyTweak);
        var lifted = freq is not null && PeekBackup(freq.Id) is null && !store.IsDamaged(freq.Id) &&
                     (await ApplyAsync(freq, facts, new ApplyOptions { ExpertMode = true, ContinueWithoutRestorePoint = true })).Outcome
                     is ApplyOutcome.Applied or ApplyOutcome.AppliedIneffective;
        var created = await restorePoints.CreateAsync($"Tunero {DateTime.Now:yyyy-MM-dd HH:mm}");
        // No restore point came of it (the user may now cancel the change): the limit goes back as it was.
        if (!created && lifted) Revert(freq!);
        return created;
    }

    // ---------------- undo ----------------

    public RevertResult Revert(TweakDefinition t)
    {
        using var hold = HoldHives(t);
        TweakBackup? backup;
        try
        {
            backup = store.Get(t.Id);
        }
        catch (BackupUnreadableException ex)
        {
            return new RevertResult(false, [], [ex.Message]);
        }
        if (backup is null)
            return store.IsDamaged(t.Id)
                ? new RevertResult(false, [], [$"The backup file {store.DamagedFile(t.Id)} is damaged; the original values cannot be read from it."])
                : new RevertResult(true, [], []);
        var skipped = new List<string>();
        var errors = new List<string>();
        var kept = new List<BackupEntry>();
        var restartBound = new List<BackupEntry>();
        var pending = IsRestartPending(backup);
        var current = new Dictionary<string, TweakAction>();
        foreach (var a in Expand(t)) current.TryAdd(a.TargetKey, a);
        // Every entry of the backup is restored, also targets the tweak no longer expands to (through the stored action).
        foreach (var entry in Enumerable.Reverse(backup.Entries))
        {
            // The action that wrote the change first: a later catalog version can change an action under the same target
            // (for example write only one side of a power setting), and undo must restore what was written then.
            var a = StoredAction(entry) ?? current.GetValueOrDefault(entry.TargetKey);
            if (a is null)
            {
                kept.Add(entry);
                errors.Add($"{entry.Description}: cannot be restored automatically (not part of this change any more)");
                continue;
            }
            try
            {
                // Feature-update-aware: if the value is no longer what we applied, Windows (or the user) changed it: leave it.
                // Not for a change that takes effect after a restart while the restart is pending (the running value is
                // still the old one), nor for backups of version 0.4.0 and older, which stored that old value as applied.
                var waitsForRestart = a.TakesEffectAfterRestart && (pending || entry.Applied?.SameAs(entry.Original) == true);
                var outcome = a.RestoreIfUnchanged(ctx, entry.Original, waitsForRestart ? null : entry.Applied);
                if (outcome == RestoreOutcome.ChangedSince)
                {
                    skipped.Add(entry.Description);
                    continue;
                }
                // Parts changed since (one side of a power setting, one adapter keyword) keep the value set since.
                if (outcome == RestoreOutcome.PartlyRestored) skipped.Add(entry.Description);
                if (a.TakesEffectAfterRestart) restartBound.Add(entry);
                Log.Info("change", $"{t.Id}: undo {entry.Description}", new { tweak = t.Id, kind = "undo", target = entry.Description, label = entry.Label, restored = entry.Original.Display });
            }
            catch (Exception ex)
            {
                kept.Add(entry);
                errors.Add($"{entry.Description}: {ex.Message}");
                Log.Error("engine", $"undo {t.Id} {a.TargetKey} failed", ex);
            }
        }
        if (kept.Count == 0)
        {
            DeleteExports(backup.Exports);
            if (!SaveAfterChange(() => store.Archive(t.Id), t.Id)) errors.Add("The values were restored, but the backup file could not be moved to the history.");
        }
        else
        {
            // Only what could not be restored stays: a retry does not report restored targets as "reset by Windows".
            backup.Entries.RemoveAll(e => !kept.Contains(e));
            if (!SaveAfterChange(() => store.Save(backup), t.Id)) errors.Add("The backup file could not be updated.");
        }
        // Restored values that take effect after a restart: shown as "off after restart" until then.
        if (restartBound.Count > 0) SaveAfterChange(() => store.SavePendingUndo(new PendingUndo { TweakId = t.Id, Entries = restartBound }), t.Id);
        return new RevertResult(errors.Count == 0, skipped, errors);
    }

    /// <summary>
    /// The BCD and power plan exports made before the change: kept while the change is in place (a manual way back),
    /// removed with its complete undo, so the exports folder does not grow with every apply.
    /// </summary>
    private void DeleteExports(IEnumerable<string> exports)
    {
        foreach (var file in exports)
        {
            try
            {
                var folder = Path.GetFullPath(ctx.ExportFolder).TrimEnd('\\');
                // Only in the exports folder. A backup from before the rename to Tunero names the old data folder, whose
                // files moved here with it: the same file name in the exports folder is that export.
                var path = Path.GetFullPath(file);
                if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)) continue;
                    path = Path.Combine(folder, Path.GetFileName(path));
                }
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn("engine", $"export {file} not removed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A backup write after the system was changed: one retry (antivirus briefly holding the file), then logged. It never
    /// throws, so the caller still reports what was changed. False when it did not succeed.
    /// </summary>
    private static bool SaveAfterChange(Action save, string tweakId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                save();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    Thread.Sleep(250);
                    continue;
                }
                Log.Error("backup", $"{tweakId}: the backup could not be saved after the change", ex);
                return false;
            }
        }
    }

    private static TweakAction? StoredAction(BackupEntry entry)
    {
        if (entry.Action is not { } json) return null;
        try
        {
            var a = JsonSerializer.Deserialize<TweakAction>(json, TweakCatalog.JsonOptions);
            return a?.TargetKey == entry.TargetKey ? a : null;
        }
        // Not only JSON errors: TargetKey can throw too (a power setting alias a later version no longer knows), and one
        // such entry must not stop the undo of the others (or Undo all).
        catch (Exception ex)
        {
            Log.Warn("engine", $"stored action of {entry.TargetKey} unreadable: {ex.Message}");
            return null;
        }
    }

    private StoredValue? SafeApplied(TweakAction a)
    {
        try
        {
            return a.AppliedValue(ctx);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private StoredValue? SafeRead(TweakAction a)
    {
        try
        {
            return a.Read(ctx);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes every backed-up change: catalog tweaks and runtime tweaks (from their stored definition). Also the settings
    /// of "permanent" tweaks: what they deleted (Recall snapshots) does not come back, but their values do, as with their
    /// own Undo. A backup nothing describes any more is reported as failed with the reason, so "Undo all" never claims more
    /// than it did.
    /// </summary>
    public IReadOnlyList<(TweakDefinition Tweak, RevertResult Result)> RevertAll(IProgress<BatchStep>? progress = null)
    {
        var results = new List<(TweakDefinition, RevertResult)>();
        var read = store.ReadAll();
        var total = read.Own.Count + read.Unreadable.Count;
        foreach (var b in read.Own)
        {
            var t = Resolve(b);
            progress?.Report(new BatchStep(results.Count, total, t ?? Placeholder(b.TweakId)));
            if (t is null)
                results.Add((Placeholder(b.TweakId), new RevertResult(false, [], ["This change is not described by this version of the app; undo it on its explanation page or by hand."])));
            else
                results.Add((t, Revert(t)));
        }
        // A backup file that cannot be read (damaged, locked, from a newer version) still stands for a change: it is
        // reported as not undone instead of being passed over.
        foreach (var u in read.Unreadable)
            results.Add((Placeholder(Path.GetFileName(u.File)), new RevertResult(false, [], [$"The backup file {u.File} is {u.Reason}; the change it records was not undone."])));
        return results;
    }

    private static TweakDefinition Placeholder(string id) => new()
    {
        Id = id, Category = "Retired", Docs = "retired", Subject = id, Hidden = true,
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] }, Actions = [],
    };

    /// <summary>
    /// Every change this app made (catalog and runtime tweaks) whose values are no longer in place. Run after each scan;
    /// a Windows update between apply and now is reported as the likely cause.
    /// </summary>
    /// <param name="known">States the scan just read with the same facts (by tweak id): those tweaks are not read again.</param>
    /// <param name="keptAfterUpdate">
    /// Collects the changes still in place on another Windows version than the one they were last known on: recorded with
    /// <see cref="ChangeSession.NoteKept"/>, a later reset on this version is not blamed on the update.
    /// </param>
    public IReadOnlyList<DriftItem> CheckDrift(Facts facts, IReadOnlyDictionary<string, TweakState>? known = null, ICollection<string>? keptAfterUpdate = null)
    {
        var list = new List<DriftItem>();
        foreach (var backup in store.All())
        {
            // From the backup just read: by id, a runtime tweak's backup would be read from its file once more.
            if (Resolve(backup) is not { } t) continue;
            // A deleted startup entry that is back was put back by its program, not reset by Windows (StartupTweaks.IsDelete).
            if (Startup.StartupTweaks.IsDelete(t) || WritesDefaultProfile(t)) continue;
            var state = known is not null && known.TryGetValue(t.Id, out var read) ? read : DetectState(t, facts);
            if (state is TweakState.Applied or TweakState.PendingRestart)
            {
                if (DriftItem.IsOtherVersion(KnownInPlaceOn(backup), WindowsVersion)) keptAfterUpdate?.Add(t.Id);
                continue;
            }
            if (state != TweakState.RevertedByWindows) continue;
            list.Add(new DriftItem(t, backup, KnownInPlaceOn(backup), WindowsVersion));
        }
        return list;
    }

    /// <summary>
    /// The Windows version a change was last known to be in place on: where a scan last found it after an update, else its
    /// last apply. Older backups only know the build number of their first apply (compared by build only).
    /// </summary>
    private static string? KnownInPlaceOn(TweakBackup backup) =>
        backup.KeptOnVersion ?? backup.AppliedOnVersion
        ?? (backup.WindowsBuild > 0 ? backup.WindowsBuild.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);

    /// <summary>
    /// Records that these changes were found in place on this Windows version (<see cref="TweakBackup.KeptOnVersion"/>), so
    /// a later reset on this version reads as the user's or a program's doing, not an update's. Only under the change lock
    /// (<see cref="ChangeSession.NoteKept"/>): each backup is read again and saved, so no change made meanwhile is lost.
    /// </summary>
    internal void NoteKept(IEnumerable<string> tweakIds)
    {
        foreach (var id in tweakIds)
        {
            try
            {
                if (store.Get(id) is not { } backup || !DriftItem.IsOtherVersion(KnownInPlaceOn(backup), WindowsVersion)) continue;
                backup.KeptOnVersion = WindowsVersion;
                store.Save(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a hint for the drift notice: the next scan tries again.
                Log.Warn("drift", $"could not record that {id} is still in place: {ex.Message}");
            }
        }
    }

    /// <summary>The definition of a tweak id: the catalog entry, or the one stored with the backup of a runtime tweak.</summary>
    public TweakDefinition? Resolve(string id)
    {
        if (_catalog.Get(id) is { } t) return t;
        return PeekBackup(id) is { } backup ? Resolve(backup) : null;
    }

    /// <summary>Like <see cref="Resolve(string)"/>, from a backup already read (no second read of its file).</summary>
    public TweakDefinition? Resolve(TweakBackup backup)
    {
        var id = backup.TweakId;
        if (_catalog.Get(id) is { } t) return t;
        if (backup.Definition is { } json)
        {
            try
            {
                return JsonSerializer.Deserialize<TweakDefinition>(json, TweakCatalog.JsonOptions);
            }
            catch (JsonException ex)
            {
                Log.Warn("engine", $"stored definition of {id} unreadable: {ex.Message}");
            }
        }
        return Retired(backup);
    }

    /// <summary>
    /// A catalog tweak that a later version removed: it is no longer offered, but a change made with it must still be
    /// undoable. Undo restores every entry through the action stored with it, so the definition needs no actions.
    /// </summary>
    private static TweakDefinition? Retired(TweakBackup backup) =>
        backup.Entries.Any(e => e.Action is not null)
            ? new TweakDefinition
            {
                Id = backup.TweakId,
                Category = "Retired",
                Docs = "retired",
                Subject = backup.TweakId,
                Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
                Hidden = true,
                Actions = [],
            }
            : null;

    // ---------------- helpers ----------------

    /// <summary>
    /// Keeps the Default profile loaded for a whole call on a tweak that writes it: one load instead of one per value
    /// (each action holds it too). A hive that cannot be loaded is left to the actions, which report it per target.
    /// </summary>
    private IDisposable? HoldHives(TweakDefinition t) => HoldHives(t, out _);

    /// <param name="error">Why the Default profile could not be loaded; null when it is loaded or not needed.</param>
    private IDisposable? HoldHives(TweakDefinition t, out string? error)
    {
        error = null;
        if (!WritesDefaultProfile(t)) return null;
        try
        {
            return ctx.Registry.Hold(Hive.DefaultUser);
        }
        catch (Exception ex)
        {
            Log.Warn("engine", $"{t.Id}: Default profile not loaded: {ex.Message}");
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// A change of the Default profile (Copy to new accounts). Its hive is loaded only for the change, so scans neither
    /// check it for drift nor read its state: the file must be free when Windows creates an account.
    /// </summary>
    public static bool WritesDefaultProfile(TweakDefinition t) => t.Actions.OfType<RegistryAction>().Any(a => a.Hive == Hive.DefaultUser);

    /// <summary>The tweak's actions for this PC: templates expand to their targets (<see cref="TweakAction.Expand"/>).</summary>
    public IReadOnlyList<TweakAction> Expand(TweakDefinition t)
    {
        var x = new ExpansionContext(ctx);
        return t.Actions.SelectMany(a => a.Expand(x)).ToList();
    }

    private static DateTimeOffset LastBoot() => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static bool IsRestartPending(TweakBackup backup) => backup.PendingRestartSince is { } since && LastBoot() < since;

    /// <summary>
    /// The pending undo of this tweak while it still waits: no boot since the undo, and a restored value is not in
    /// effect yet (it still reads as applied). Otherwise the record is done and removed.
    /// </summary>
    private PendingUndo? ActivePendingUndo(TweakDefinition t, IReadOnlyList<TweakAction> actions)
    {
        if (store.GetPendingUndo(t.Id) is not { } p) return null;
        // A value that cannot be read now (a WMI error) is no proof that the restart happened: the record stays.
        if (LastBoot() < p.Since && actions.Where(a => a.TakesEffectAfterRestart && p.Entries.Any(e => e.TargetKey == a.TargetKey))
                .Any(a => SafeState(a) is ActionState.Applied or ActionState.Unsupported))
            return p;
        try
        {
            store.ClearPendingUndo(t.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("engine", $"pending undo of {t.Id} not removed: {ex.Message}");
        }
        return null;
    }
}
