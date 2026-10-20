using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The row's action button: the same action its label names (<see cref="TweakItemViewModel.Action"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task RunActionAsync(InspectorItem? item)
    {
        item ??= SelectedItem;
        switch (item)
        {
            case TweakItemViewModel { Action: RowAction.Undo } t:
                await Runner.UndoAsync(t.Tweak);
                break;
            case TweakItemViewModel { Action: RowAction.Apply or RowAction.ApplyAgain } t:
                await Runner.ApplyAsync(t.Tweak);
                break;
            case FindingItemViewModel { Fix: { } fix }:
                await Runner.ApplyAsync(fix);
                break;
        }
    }

    /// <summary>Switch on a tweak row: on = apply (confirmation first), off = undo. The switch shows the real state.</summary>
    public async Task ToggleAsync(TweakItemViewModel item, bool on)
    {
        // Called from the switch's binding setter: the confirmation dialog must not open inside the click and the
        // switch's property update (undo reaches ShowDialog before its first await), as SwitchRow.RunAsync does.
        await Task.Yield();
        var done = false;
        try
        {
            if (!CanChange) ShowResult(Loc.Instance["Change_Busy"]);
            else if (on && !item.IsOn) done = await Runner.ApplyAsync(item.Tweak);
            else if (!on && item.HasBackup) done = await Runner.UndoAsync(item.Tweak);
            else if (!on) ShowResult(Loc.Instance["Tweak_NoBackup"]);
        }
        finally
        {
            // A switch that was just turned on and confirmed stays on until the rescan rebuilds the row (flipping back for a
            // few seconds reads like a failure); cancelled or failed, it shows the real state again.
            item.ResetSwitch(done ? on : null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ApplyRecommendedAsync()
    {
        if (_plan.IsEmpty)
        {
            ShowResult(Loc.Instance["Rec_None"]);
            return;
        }
        // Only the ticked lines of the plan on the overview (all of them unless the user unticked some).
        var chosen = Recommendations.Where(r => r.IsSelected && r.Tweak is not null).Select(r => r.Tweak!).ToList();
        if (chosen.Count == 0)
        {
            ShowResult(Loc.Instance["Rec_NoneSelected"]);
            return;
        }
        await Runner.ApplyBatchAsync(chosen, Loc.Instance["Rec_Title"], Loc.Instance["Rec_Intro"]);
    }

    /// <summary>
    /// The first sign-in on a PC installed from Tunero's install media (<see cref="FirstSignInStart.ApplySwitch"/>), after
    /// the first scan: the profile's recommended changes exactly as --apply-recommended picks them
    /// (<see cref="CommandLineApply.RecommendedPlanAsync"/>), applied as the only change with the banner showing each one,
    /// without the confirmation (the owner chose them on the media) and without a restore point (a new installation has
    /// nothing to go back to). Changes needing their own confirmation stay on the Overview. Every change is recorded, so
    /// Changes opens with the result and each can be undone there. Without a scan or with a profile this PC does not offer
    /// (a laptop profile on a desktop), nothing is applied and the message says so.
    /// </summary>
    public async Task FirstSignInApplyAsync(string profileId)
    {
        if (ScanFailed || Profile is null)
        {
            Log.Warn("firstsignin", "the first scan failed: nothing applied");
            ShowResult(Loc.Instance["Media_FirstSignInScanFailed"], InfoBarSeverity.Error);
            return;
        }
        var known = _catalog.Profiles.Profiles.FirstOrDefault(p => p.Id == profileId);
        var name = known is null ? profileId : ProfileName(known);
        RecommendedSelection? plan = null;
        BatchOutcome? outcome = null;
        var ran = await Runner.RunExclusiveAsync("first sign-in", Loc.Instance.Format("Media_FirstSignInTitle", name), async work =>
        {
            work.Indeterminate(Loc.Instance["Scan_Running"]);
            (var pc, plan) = await CommandLineApply.RecommendedPlanAsync(_services, profileId, null, IgnoredKeys());
            if (plan is null) return;
            foreach (var t in plan.Together) ChangeLogText.Remember(t);
            // No Stop: the run is short and each change applies whole; the banner counts them.
            var steps = new Progress<BatchStep>(s => work.Step(s.Done, s.Total, s.Tweak is { } t ? Runner.Title(t) : null));
            outcome = await _services.Session.ApplyBatchAsync(plan.Together, pc.Facts, pc.Applied, new ApplyOptions { WithoutRestorePoint = true },
                () => RestorePointChoice.Continue, steps);
        });
        if (!ran) return;
        if (plan is null)
        {
            Log.Warn("firstsignin", "the profile is not offered on this PC: nothing applied", new { profileId });
            ShowResult(Loc.Instance.Format("Media_FirstSignInNoProfile", name), InfoBarSeverity.Warning);
            return;
        }
        var done = outcome ?? BatchOutcome.Cancelled;
        Log.Info("firstsignin", "profile applied", new { profileId, done.Applied, done.Failed, done.Skipped, inApp = plan.InApp.Count });
        // The pages read the PC again (as after any change), then Changes shows what was applied, with the result kept open.
        _reuseSlowParts = true;
        await ScanAsync();
        RefreshReapply();
        CurrentPage = Page.Changes;
        ShowResult(Loc.Instance.Format("Media_FirstSignInDone", ProfileName(plan.Profile), done.Applied, done.Failed, done.Skipped + plan.InApp.Count),
            done.Failed > 0 || done.BackupsNotUpdated ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        _resultTimer?.Stop();
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task UndoRecordAsync(string tweakId)
    {
        if (ResolveTweak(tweakId) is { } t) await Runner.UndoAsync(t);
        else ShowResult(Loc.Instance.Format("Result_Error", tweakId), Wpf.Ui.Controls.InfoBarSeverity.Error);
    }

    /// <summary>Catalog entry, runtime fix of this scan, or the definition stored with the backup.</summary>
    public TweakDefinition? ResolveTweak(string id) =>
        _services.Catalog.Get(id) ?? _findings.Select(f => f.Fix).FirstOrDefault(f => f?.Id == id) ?? _services.Engine.Resolve(id);

    /// <summary>The runner refreshes the pages after it (also when some changes could not be undone).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task UndoAllAsync() => Runner.UndoAllAsync();

    /// <summary>Opens Windows' System Restore wizard (rstrui.exe from System32) to go back to a restore point.</summary>
    [RelayCommand]
    private void OpenSystemRestore()
    {
        try
        {
            Optimizer.Core.Platform.ProcessLauncher.StartDetached(Optimizer.Core.Platform.ProcessLauncher.Info("rstrui.exe", hidden: false));
        }
        catch (Exception ex)
        {
            Log.Error("ui", "starting System Restore failed", ex);
            ShowResult(Loc.Instance.Format("Result_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task EnableRestorePointsAsync()
    {
        // The same gate as every change: one at a time, errors logged and shown.
        await Runner.RunExclusiveAsync("enable System Restore", () => Task.Run(() => _services.RestorePoints.Enable()));
        await RefreshChangesStateAsync();
    }

    /// <summary>
    /// Tweaks that change findings need a rescan; page-level changes (startup, services, features) refresh their page.
    /// Null: several changes at once (Undo all).
    /// </summary>
    private async Task OnChangedAsync(TweakDefinition? t)
    {
        try
        {
            if (t?.Category is "Startup" or "Services" or "Features" or "Priorities")
            {
                await RefreshChangesStateAsync();
                return;
            }
            await AfterChangeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui", "refresh after a change failed", ex);
        }
    }

    private bool _reuseSlowParts;

    private async Task AfterChangeAsync()
    {
        var result = ResultText;
        var severity = ResultSeverity;
        _reuseSlowParts = true;
        await ScanAsync();
        if (result is not null)
        {
            ShowResult(result, severity);
            ResultShowsChanges = true;
        }
    }

    // The Changes page reads every backup file and asks WMI whether System Restore is on: done in the background after a
    // scan or a change, not on every rebuild (filter, language, theme, profile).
    private IReadOnlyList<Optimizer.Core.Backup.TweakBackup> _backups = [];
    private bool? _restoreEnabled;

    // The definitions of the backups, resolved with them in the background (a definition stored with a backup would
    // otherwise be read from its file again on the UI thread at every rebuild).
    private IReadOnlyDictionary<string, TweakDefinition?> _backupTweaks = new Dictionary<string, TweakDefinition?>();

    // States of changes the tweak lists do not cover (startup entries, services, tasks, the restore point frequency),
    // so their Changes rows show On or Off like the others.
    private IReadOnlyDictionary<string, TweakState> _otherChangeStates = new Dictionary<string, TweakState>();

    private async Task RefreshChangesStateAsync()
    {
        var store = _services.Store;
        var engine = _services.Engine;
        var facts = _facts;
        var known = _tweakStates.Concat(_deviceStates).Select(s => s.Tweak.Id).ToHashSet(StringComparer.Ordinal);
        (_backups, _restoreEnabled, _otherChangeStates, _backupTweaks) = await Task.Run(() =>
        {
            var backups = store.All();
            var tweaks = backups.ToDictionary(b => b.TweakId, b => engine.Resolve(b), StringComparer.Ordinal);
            return (backups, SafeRestoreEnabled(), OtherChangeStates(tweaks, engine, backups, known, facts), (IReadOnlyDictionary<string, TweakDefinition?>)tweaks);
        });
        BuildChanges();
    }

    private static IReadOnlyDictionary<string, TweakState> OtherChangeStates(IReadOnlyDictionary<string, TweakDefinition?> tweaks,
        TweakEngine engine, IReadOnlyList<Optimizer.Core.Backup.TweakBackup> backups, HashSet<string> known, Facts facts)
    {
        var states = new Dictionary<string, TweakState>(StringComparer.Ordinal);
        foreach (var b in backups.Where(b => !known.Contains(b.TweakId)))
        {
            // A retired tweak has no actions: its state cannot be read, only undone. The Default profile is loaded only
            // for a change (TweakEngine.WritesDefaultProfile).
            if (tweaks.GetValueOrDefault(b.TweakId) is not { Actions.Count: > 0 } t || TweakEngine.WritesDefaultProfile(t)) continue;
            try
            {
                states[b.TweakId] = engine.DetectState(t, facts);
            }
            catch (Exception ex)
            {
                Log.Warn("changes", $"state of {b.TweakId} unreadable: {ex.Message}");
            }
        }
        return states;
    }

    public HashSet<string> AppliedIds() =>
        _tweakStates.Concat(_deviceStates).Where(s => s.State is TweakState.Applied or TweakState.PendingRestart).Select(s => s.Tweak.Id).ToHashSet();
}
