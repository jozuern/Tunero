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
    /// The first sign-in on a PC installed from Tunero's install media (<see cref="FirstSignInStart.ApplySwitch"/>, once its
    /// pending marker was taken), after the first scan: the profile's recommended changes exactly as --apply-recommended
    /// picks them (<see cref="CommandLineApply.RecommendedPlanAsync"/>), applied as the only change with the banner showing
    /// each one, without the confirmation (the owner chose them on the media) and without a restore point (a new
    /// installation has nothing to go back to). Changes needing their own confirmation stay on the Overview. Whenever a plan
    /// was made, the pages read the PC again and Changes opens with the result (also after an error, with what was applied
    /// listed there), and each change can be undone there. Without a scan, with a profile this PC does not offer (a laptop
    /// profile on a desktop) or while another change runs, nothing is applied and the message says so.
    /// After the profile, in the same run, the <paramref name="apps"/> the media named are installed with winget one after
    /// the other (<see cref="InstallFirstSignInAppsAsync"/>); the result names those that were not.
    /// </summary>
    public async Task FirstSignInApplyAsync(string profileId, IReadOnlyList<string>? apps = null)
    {
        apps ??= [];
        if (ScanFailed || Profile is null)
        {
            Log.Warn("firstsignin", "the first scan failed: nothing applied");
            ShowResult(WithApps(Loc.Instance["Media_FirstSignInScanFailed"], [], apps), InfoBarSeverity.Error);
            return;
        }
        var known = _catalog.Profiles.Profiles.FirstOrDefault(p => p.Id == profileId);
        var name = known is null ? profileId : ProfileName(known);
        RecommendedSelection? plan = null;
        BatchOutcome? outcome = null;
        Exception? error = null;
        IReadOnlyList<string> installed = [];
        var ran = await Runner.RunExclusiveAsync("first sign-in", Loc.Instance.Format("Media_FirstSignInTitle", name), async work =>
        {
            try
            {
                work.Indeterminate(Loc.Instance["Scan_Running"]);
                (var pc, plan) = await CommandLineApply.RecommendedPlanAsync(_services, profileId, null, IgnoredKeys(), IgnoredTweakIds());
                if (plan is null) return;
                foreach (var t in plan.Together) ChangeLogText.Remember(t);
                // No Stop: each change applies whole; the banner counts them.
                var steps = new Progress<BatchStep>(s => work.Step(s.Done, s.Total, s.Tweak is { } t ? Runner.Title(t) : null));
                outcome = await _services.Session.ApplyBatchAsync(plan.Together, pc.Facts, pc.Applied, new ApplyOptions { WithoutRestorePoint = true },
                    () => RestorePointChoice.Continue, steps);
            }
            catch (Exception ex)
            {
                // Caught here, not by the runner: what was applied before the error still has to be read and shown.
                Log.Error("firstsignin", "the first sign-in stopped with an error", ex);
                error = ex;
            }
            // The apps do not depend on the profile: they are installed also when it was not offered or stopped.
            if (apps.Count > 0) installed = await InstallFirstSignInAppsAsync(apps, work);
        });
        if (!ran)
        {
            // Another change or an update ran (the runner said so): nothing was applied.
            Log.Warn("firstsignin", "another change was running: nothing applied", new { profileId });
            ShowResult(WithApps(Loc.Instance["Media_FirstSignInBusy"], [], apps), InfoBarSeverity.Warning);
            return;
        }
        var appsMissing = installed.Count < apps.Count;
        if (plan is null)
        {
            Log.Warn("firstsignin", error is null ? "the profile is not offered on this PC: nothing applied" : "the scan failed: nothing applied", new { profileId });
            ShowResult(WithApps(error is null ? Loc.Instance.Format("Media_FirstSignInNoProfile", name) : Loc.Instance["Media_FirstSignInScanFailed"], installed, apps),
                error is null ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            if (installed.Count > 0) Runner.NotifyChanged(null);
            return;
        }
        string text;
        InfoBarSeverity severity;
        if (error is not null)
        {
            text = Loc.Instance.Format("Media_FirstSignInError", ProfileName(plan.Profile), error.Message);
            severity = InfoBarSeverity.Error;
        }
        else
        {
            var done = outcome ?? BatchOutcome.Cancelled;
            Log.Info("firstsignin", "profile applied", new { profileId, done.Applied, done.Failed, done.Skipped, inApp = plan.InApp.Count });
            text = Loc.Instance.Format("Media_FirstSignInDone", ProfileName(plan.Profile), done.Applied, done.Failed, done.Skipped + plan.InApp.Count);
            if (done.BackupsNotUpdated) text = $"{text} {Loc.Instance["Result_BackupsNotUpdated"]}";
            severity = done.Failed > 0 || done.BackupsNotUpdated || appsMissing ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        }
        text = WithApps(text, installed, apps);
        // Changes shows what was applied; the result stays open (also when the rescan after the change shows it again), and
        // the change event lets every page read the PC again, as after Apply recommended.
        CurrentPage = Page.Changes;
        _stickyResult = text;
        ShowResult(text, severity);
        Runner.NotifyChanged(null);
    }

    /// <summary>
    /// Installs the media's apps with the trusted winget (<see cref="Optimizer.Core.Apps.Winget.FindTrusted()"/>), machine-wide and one after the
    /// other, with the banner naming each. Returns the ids winget installed; the others are logged with winget's code. Needs
    /// the internet: offline every one fails, and the result says where to install them later.
    /// </summary>
    private async Task<IReadOnlyList<string>> InstallFirstSignInAppsAsync(IReadOnlyList<string> apps, WorkProgress.Scope work)
    {
        var done = new List<string>();
        var winget = await Task.Run(Optimizer.Core.Apps.Winget.FindTrusted);
        if (winget is null)
        {
            Log.Warn("firstsignin", "winget was not found: no app installed", new { apps });
            return done;
        }
        var offered = Optimizer.Core.Media.MediaChoices.InstallableApps(_catalog.Apps).ToList();
        for (var i = 0; i < apps.Count; i++)
        {
            if (offered.FirstOrDefault(a => a.Id == apps[i]) is not { } app) continue;
            work.Step(i, apps.Count, Loc.Instance.Format("Media_FirstSignInInstalling", app.Name));
            try
            {
                var code = await Optimizer.Core.Apps.Winget.InstallAsync(winget, app, null, CancellationToken.None);
                if (Optimizer.Core.Apps.Winget.IsSuccess(code)) done.Add(app.Id);
                else Log.Warn("firstsignin", $"{app.Id} not installed: winget 0x{code:X8}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException)
            {
                Log.Warn("firstsignin", $"{app.Id} not installed: {ex.Message}");
            }
        }
        Log.Info("firstsignin", "apps installed", new { installed = done, asked = apps });
        return done;
    }

    /// <summary>The first sign-in's result with what became of the media's apps: those installed, and those that were not.</summary>
    private string WithApps(string text, IReadOnlyList<string> installed, IReadOnlyList<string> apps)
    {
        if (apps.Count == 0) return text;
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => _catalog.Apps.Apps.FirstOrDefault(a => a.Id == id)?.Name ?? id));
        if (installed.Count > 0) text = $"{text} {Loc.Instance.Format("Media_FirstSignInAppsDone", Names(installed))}";
        var missing = apps.Where(a => !installed.Contains(a)).ToList();
        if (missing.Count > 0) text = $"{text} {Loc.Instance.Format("Media_FirstSignInAppsMissing", Names(missing))}";
        return text;
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
