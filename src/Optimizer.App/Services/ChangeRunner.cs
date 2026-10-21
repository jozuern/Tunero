using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Services;

/// <summary>A result line and how it is shown (success, caution, error).</summary>
public sealed record ResultMessage(string Text, InfoBarSeverity Severity);

/// <summary>
/// One path for every change in the app (tweaks, fixes, startup entries, services, features, device tweaks): exact
/// changes in the confirmation dialog, the restore point decision, apply with backup, undo. Pages only build the
/// TweakDefinition; this class does the rest and reports a result line.
/// <para>
/// One change at a time (<see cref="ChangeGate"/>): the gate is taken before the preview, so a second click cannot
/// stack a second confirmation built from the same, soon stale state. Detection, preview, apply and undo run off the UI
/// thread (DISM, bcdedit, powercfg and PowerShell can take minutes); the dialogs stay on it. Every failure is caught,
/// logged and reported with a text that says what is left.
/// </para>
/// </summary>
/// <param name="session">The change session whose lock the runner holds; null: the app's (<see cref="AppServices.Session"/>). Tests pass one with a fake lock.</param>
public sealed class ChangeRunner(AppServices services, IDialogs dialogs, Func<Facts> facts, Func<bool> expertMode,
    ChangeSession? session = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ChangeSession Session => session ?? services.Session;

    /// <summary>The scan that runs now, or a completed task: a change waits for it (see <see cref="ExclusiveAsync"/>).</summary>
    public Func<Task> ScanInProgress { get; init; } = () => Task.CompletedTask;

    public event EventHandler<ResultMessage>? Status;

    /// <summary>The banner over every page while a change runs (after its confirmation).</summary>
    public WorkProgress Work { get; } = new();

    /// <summary>Raised after any change was written or undone (also when it failed half-way), so pages refresh their state.</summary>
    public event EventHandler<TweakDefinition?>? Changed;

    /// <summary>
    /// Raises <see cref="Changed"/> for changes made inside <see cref="RunExclusiveAsync(string, string, Func{WorkProgress.Scope, Task}, CancellationTokenSource?)"/>
    /// through the change session (the first sign-in); null: several at once, everything is read again.
    /// </summary>
    public void NotifyChanged(TweakDefinition? t) => Changed?.Invoke(this, t);

    public string Title(TweakDefinition t) => TitleOf(t, Loc.Instance.Language);

    // HKU\<SID of this process>: the hive that this process sees as HKCU.
    private static readonly string? OwnHive = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is { } sid ? $@"HKU\{sid}" : null;

    /// <summary>
    /// A registry path of the signed-in user as people know it ("HKCU\Software\...") instead of "HKU\S-1-5-21-...\Software\...".
    /// Only when this app runs as that user: with a separate administrator account the SID stays, since HKCU would be
    /// that account's hive. Other targets ("Service Spooler (start type)") in the UI language, line by line; a target only
    /// known as English text (a backup or log of 1.0.0 or older) is translated by its pattern.
    /// </summary>
    public static string ShortTarget(string target) =>
        string.Join("\n", target.Split('\n').Select(line => Labels.Current.Target(Loc.Instance.Language, ShortHive(line))));

    /// <summary>A change line's target in the UI language. A title line in front of the target (Apply recommended) stays.</summary>
    public static string ShortTarget(ChangeLine line) => line.Label is { } label ? ShortTarget(line.Target, label) : ShortTarget(line.Target);

    /// <summary>The last line of <paramref name="target"/> rendered from its label; the lines before it stay as they are.</summary>
    public static string ShortTarget(string target, TargetText label)
    {
        var rendered = Labels.Current.Render(Loc.Instance.Language, label with { Args = label.Args.Select(ShortHive).ToList() });
        var cut = target.LastIndexOf('\n');
        return cut < 0 ? rendered : target[..(cut + 1)] + rendered;
    }

    private static string ShortHive(string target) =>
        OwnHive is { } own && target.StartsWith(own, StringComparison.OrdinalIgnoreCase) && (target.Length == own.Length || target[own.Length] == '\\')
            ? "HKCU" + target[own.Length..]
            : target;

    public static IReadOnlyList<ChangeLine> ShortTargets(IReadOnlyList<ChangeLine> lines) => lines.Select(c => c with { Target = ShortTarget(c), Label = null }).ToList();

    /// <summary>The explanation page's title, with the subject for tweaks that share a page (DNS presets, services).</summary>
    public static string TitleOf(TweakDefinition t, string lang)
    {
        var title = DocStore.GetOrEnglish(t.DocId, lang)?.Title ?? t.Id;
        return t.Subject is { Length: > 0 } s ? $"{title}: {s}" : title;
    }

    /// <summary>
    /// A block reason as text. A conflict names the other tweak by id; the user sees its title instead.
    /// </summary>
    public static string BlockText(Block b, string lang)
    {
        var detail = b.Detail ?? "";
        if (b.ReasonKey is "block.conflict" && TweakCatalog.Current.Get(detail) is { } other) detail = TitleOf(other, lang);
        return Labels.Current.Get(lang, b.ReasonKey) + detail;
    }

    public string Summary(TweakDefinition t)
    {
        var lang = Loc.Instance.Language;
        var page = DocStore.GetOrEnglish(t.DocId, lang);
        return page?.Section(DocHeadings.Summary(page.Language))?.Body ?? "";
    }

    /// <summary>
    /// Runs <paramref name="work"/> as the only change. Returns <paramref name="whenNotRun"/> when another change, a
    /// scan or an update is running, and after an unexpected error.
    /// </summary>
    private async Task<T> ExclusiveAsync<T>(string what, Func<Task<T>> work, T whenNotRun)
    {
        // Every change starts a rescan; a change that follows at once (the next item of an import, a DNS choice after an
        // apply) waits for it instead of being refused as busy.
        if (ChangeGate.Instance is { Scanning: true, Busy: false, Updating: false }) await ScanInProgress();
        if (!ChangeGate.Instance.CanChange || !_gate.Wait(0))
        {
            Report(Loc.Instance["Change_Busy"], InfoBarSeverity.Warning);
            return whenNotRun;
        }
        try
        {
            // Another Tunero (a second window, a command line run, another account) may be changing this PC right now:
            // the session takes the cross-process lock for the confirmation and the change together.
            var result = whenNotRun;
            if (await Session.ExclusiveAsync(async () =>
                {
                    Busy(true);
                    result = await work();
                }))
                return result;
            Report(Loc.Instance["Change_OtherInstance"], InfoBarSeverity.Warning);
            return whenNotRun;
        }
        catch (ChangeBusyException)
        {
            Report(Loc.Instance["Change_OtherInstance"], InfoBarSeverity.Warning);
            return whenNotRun;
        }
        catch (Exception ex)
        {
            Log.Error("ui", $"{what} failed", ex);
            Report(Loc.Instance.Format("Result_Error", ex.Message), InfoBarSeverity.Error);
            return whenNotRun;
        }
        finally
        {
            Busy(false);
            _gate.Release();
        }
    }

    /// <summary>
    /// A page operation that changes the system outside the tweak engine (remove an app, clean up, uninstall, a quick
    /// fix): runs as the only change, like an apply, and an unexpected error is logged and shown. False when it did not
    /// run or failed.
    /// </summary>
    public Task<bool> RunExclusiveAsync(string what, Func<Task> work) => RunExclusiveAsync(what, Loc.Instance["Busy"], _ => work());

    /// <summary>
    /// The same with the banner titled <paramref name="title"/>; <paramref name="work"/> reports its steps to the scope.
    /// <paramref name="stop"/> adds Stop to the banner (only for work that can end between two safe points).
    /// </summary>
    public Task<bool> RunExclusiveAsync(string what, string title, Func<WorkProgress.Scope, Task> work, CancellationTokenSource? stop = null) =>
        ExclusiveAsync(what, async () =>
        {
            using var scope = Work.Begin(title, stop);
            await work(scope);
            return true;
        }, false);

    /// <summary>The restore point can take a minute: the banner says so (the engine calls this on its own thread).</summary>
    private static Action RestorePointNote(WorkProgress.Scope scope)
    {
        IProgress<string> onUi = new Progress<string>(scope.Indeterminate);
        return () => onUi.Report(Loc.Instance["Work_RestorePoint"]);
    }

    /// <summary>Confirms and applies one tweak. Returns true when something was written.</summary>
    public Task<bool> ApplyAsync(TweakDefinition t) => ExclusiveAsync($"apply {t.Id}", async () =>
    {
        ChangeLogText.Remember(t);
        var currentFacts = facts();
        var status = await Task.Run(() => services.Engine.Detect(t, currentFacts));
        var request = ConfirmRequestFor(t, status, expertMode(), Title(t), Summary(t), await Task.Run(() => services.Engine.Preview(t)));
        var confirm = dialogs.ConfirmApply(request);
        if (confirm is null) return false;

        using var work = Work.Begin(Loc.Instance["Work_ApplyOne"]);
        work.Indeterminate(request.Title);
        var options = new ApplyOptions { ExpertMode = expertMode(), AcknowledgeAntiCheat = confirm.AcknowledgedAntiCheat, RestorePointStarting = RestorePointNote(work) };
        if (await Session.ApplyAsync(t, currentFacts, options, dialogs.AskRestorePoint) is not { } result) return false;
        Report(ResultText(t, request.Title, result), SeverityOf(result));
        var written = result.Outcome is ApplyOutcome.Applied or ApplyOutcome.AppliedIneffective;
        // A failed apply can leave a backup behind (a rollback that did not work): the pages show it as a change.
        if (written || result.Outcome == ApplyOutcome.Failed) Changed?.Invoke(this, t);
        return written;
    }, false);

    /// <summary>The confirmation dialog of one tweak (also used by the --confirm-shot developer switch).</summary>
    public static ConfirmRequest ConfirmRequestFor(TweakDefinition t, TweakStatus status, bool expert, string title, string summary, IReadOnlyList<ChangeLine> preview)
    {
        var lang = Loc.Instance.Language;
        var labels = Labels.Current;
        var badges = new List<string> { labels.Get(lang, $"risk.{t.EffectiveRisk}"), labels.Get(lang, $"reversibility.{t.Reversibility}") };
        if (t.Restart) badges.Add(labels.Get(lang, "badge.restart"));
        if (t.SignOut) badges.Add(labels.Get(lang, "badge.signOut"));
        if (t.Preview) badges.Insert(0, labels.Get(lang, "badge.preview"));
        if (t.Undocumented) badges.Add(labels.Get(lang, "badge.undocumented"));
        var warnings = new List<string>();
        if (t.Preview) warnings.Add(labels.Get(lang, "preview.warning"));
        if (t.IsBootCritical) warnings.Add(labels.Get(lang, "undo.bootCritical"));
        // What stops the change goes first and keeps Apply off; notes about the change stay warnings below it.
        var blocks = status.Blocks.Where(b => b.ReasonKey is not ("block.antiCheat" or "block.expertMode")).Select(b => BlockText(b, lang)).ToList();
        if (t.EffectiveRisk == Risk.Expert && !expert) blocks.Add(labels.Get(lang, "block.expertMode"));
        var antiCheat = status.Blocks.FirstOrDefault(b => b.ReasonKey == "block.antiCheat") is { } ac ? labels.Get(lang, ac.ReasonKey) + ac.Detail : null;
        return new ConfirmRequest(title, summary, preview, badges, warnings, antiCheat, false) { Blocks = blocks };
    }

    /// <summary>Confirms and undoes one tweak from its backup.</summary>
    public Task<bool> UndoAsync(TweakDefinition t) => ExclusiveAsync($"undo {t.Id}", async () =>
    {
        ChangeLogText.Remember(t);
        var lang = Loc.Instance.Language;
        TweakBackup? backup;
        try
        {
            backup = services.Store.Get(t.Id);
        }
        catch (BackupUnreadableException ex)
        {
            Report(Loc.Instance.Format("Result_Error", ex.Message), InfoBarSeverity.Error);
            return false;
        }
        if (backup is null)
        {
            Report(services.Store.IsDamaged(t.Id)
                ? Labels.Current.Get(lang, "block.backupDamaged") + services.Store.DamagedFile(t.Id)
                : Loc.Instance["Tweak_NoBackup"], InfoBarSeverity.Warning);
            return false;
        }
        var lines = backup.Entries.Select(e => new ChangeLine(e.Description, e.Applied?.Display ?? "?", e.Original.Display, e.Label)).ToList();
        var warnings = t.IsBootCritical ? new List<string> { Labels.Current.Get(lang, "undo.bootCritical") } : [];
        var title = Title(t);
        if (dialogs.ConfirmApply(new ConfirmRequest(title, Labels.Current.Get(lang, "undo.reversible"), lines, [], warnings, null, true)) is null) return false;
        using var work = Work.Begin(Loc.Instance["Work_UndoOne"]);
        work.Indeterminate(title);
        try
        {
            var result = await Session.UndoAsync(t);
            if (!result.Success) Report(Loc.Instance.Format("Result_UndoIncomplete", title, string.Join("; ", result.Errors)), InfoBarSeverity.Error);
            else if (result.AlreadyRevertedByWindows.Count > 0) Report(Loc.Instance.Format("Result_UndoneSkipped", title, result.AlreadyRevertedByWindows.Count));
            else Report(Loc.Instance.Format("Result_Undone", title), InfoBarSeverity.Success);
            return result.Success;
        }
        finally
        {
            // Also after a failure: part of it may be undone, and the Changes page must show what is left.
            Changed?.Invoke(this, t);
        }
    }, false);

    /// <summary>
    /// Undoes every change this app made (one confirmation, unless the caller already asked: the uninstall).
    /// <paramref name="incompleteNote"/> is added to the message when not everything was undone; it is part of the
    /// runner's own message because the rescan after the change shows that message again.
    /// </summary>
    public Task<bool> UndoAllAsync(bool confirm = true, string? incompleteNote = null) => ExclusiveAsync("undo all", async () =>
    {
        var count = await Task.Run(() =>
        {
            var backups = services.Store.All();
            // The undo deletes the backups that hold run-time tweaks: keep their titles for the change log.
            foreach (var b in backups)
                if (services.Engine.Resolve(b) is { } t) ChangeLogText.Remember(t);
            return backups.Count;
        });
        if (count == 0 || (confirm && !dialogs.ConfirmUndoAll(count))) return false;
        // No Stop: the uninstall undoes everything before it removes the app, and must not end up with half of it.
        using var work = Work.Begin(Loc.Instance["Work_UndoAll"]);
        try
        {
            var outcome = await Session.UndoAllAsync(new Progress<BatchStep>(s => work.Step(s.Done, s.Total, s.Tweak is { } t ? Title(t) : null)));
            if (outcome.Failed == 0) Report(Loc.Instance.Format("Result_UndoAll", outcome.Results.Count), InfoBarSeverity.Success);
            else Report((Loc.Instance.Format("Result_UndoAllIncomplete", outcome.Undone, outcome.Failed) + " " + incompleteNote).TrimEnd(), InfoBarSeverity.Error);
            return outcome.Failed == 0;
        }
        finally
        {
            Changed?.Invoke(this, null);
        }
    }, false);

    /// <summary>
    /// Undoes several changes of this app after one confirmation listing every value that goes back (the Tweaks page's
    /// selection). Returns the tweaks it tried; one without a readable backup is left out and stays selected.
    /// </summary>
    public Task<IReadOnlyList<TweakDefinition>> UndoBatchAsync(IReadOnlyList<TweakDefinition> tweaks) =>
        tweaks.Count == 0 ? Task.FromResult<IReadOnlyList<TweakDefinition>>([]) : ExclusiveAsync<IReadOnlyList<TweakDefinition>>("undo batch", async () =>
    {
        foreach (var t in tweaks) ChangeLogText.Remember(t);
        var lang = Loc.Instance.Language;
        var backups = await Task.Run(() => tweaks.Select(t =>
        {
            try
            {
                return (Tweak: t, Backup: services.Store.Get(t.Id));
            }
            catch (BackupUnreadableException)
            {
                return (Tweak: t, Backup: (TweakBackup?)null);
            }
        }).Where(x => x.Backup is not null).ToList());
        if (backups.Count == 0)
        {
            Report(Loc.Instance["Tweak_NoBackup"], InfoBarSeverity.Warning);
            return [];
        }
        var lines = backups.SelectMany(x => x.Backup!.Entries.Select(e =>
            new ChangeLine($"{Title(x.Tweak)}\n{e.Description}", e.Applied?.Display ?? "?", e.Original.Display, e.Label))).ToList();
        var warnings = backups.Any(x => x.Tweak.IsBootCritical) ? new List<string> { Labels.Current.Get(lang, "undo.bootCritical") } : [];
        var badges = new List<string> { Loc.Instance.Format("Rec_Count", backups.Count) };
        if (dialogs.ConfirmApply(new ConfirmRequest(Loc.Instance["Tweaks_PickedUndoTitle"], Labels.Current.Get(lang, "undo.reversible"), lines, badges, warnings, null, true)) is null)
            return [];

        using var work = Work.Begin(Loc.Instance["Work_UndoPicked"]);
        int undone = 0, failed = 0;
        try
        {
            for (var i = 0; i < backups.Count; i++)
            {
                var t = backups[i].Tweak;
                work.Step(i, backups.Count, Title(t));
                var result = await Session.UndoAsync(t);
                if (result.Success) undone++;
                else
                {
                    failed++;
                    Log.Warn("change", $"undo of {t.Id} incomplete: {string.Join("; ", result.Errors)}");
                }
            }
            if (failed == 0) Report(Loc.Instance.Format("Result_UndoAll", undone), InfoBarSeverity.Success);
            else Report(Loc.Instance.Format("Result_UndoAllIncomplete", undone, failed), InfoBarSeverity.Error);
        }
        finally
        {
            // Also after a failure: part of it may be undone, and the Changes page must show what is left.
            Changed?.Invoke(this, null);
        }
        return backups.Select(x => x.Tweak).ToList();
    }, []);

    /// <summary>Applies the given set after one confirmation listing every change (Apply recommended).</summary>
    public Task<IReadOnlyList<BatchItemResult>> ApplyBatchAsync(IReadOnlyList<TweakDefinition> tweaks, string title, string summary) =>
        tweaks.Count == 0 ? Task.FromResult<IReadOnlyList<BatchItemResult>>([]) : ExclusiveAsync<IReadOnlyList<BatchItemResult>>("apply batch", async () =>
    {
        foreach (var t in tweaks) ChangeLogText.Remember(t);
        var lang = Loc.Instance.Language;
        var preview = await Task.Run(() => tweaks.SelectMany(t => services.Engine.Preview(t).Select(c => c with { Target = $"{Title(t)}\n{c.Target}" })).ToList());
        var badges = new List<string> { Loc.Instance.Format("Rec_Count", tweaks.Count) };
        if (tweaks.Any(t => t.Restart)) badges.Add(Labels.Current.Get(lang, "badge.restart"));
        if (dialogs.ConfirmApply(new ConfirmRequest(title, summary, preview, badges, [], null, false)) is null) return [];

        // Stop ends the batch after the change that runs: every change is applied whole or not at all.
        using var stop = new CancellationTokenSource();
        using var work = Work.Begin(Loc.Instance["Work_Apply"], stop);
        var steps = new Progress<BatchStep>(s =>
        {
            if (s.RestorePoint) work.Indeterminate(Loc.Instance["Work_RestorePoint"]);
            else work.Step(s.Done, s.Total, s.Tweak is { } t ? Title(t) : null);
        });
        var outcome = await Session.ApplyBatchAsync(tweaks, facts(), new ApplyOptions { ExpertMode = expertMode() }, dialogs.AskRestorePoint,
            steps, stop.Token);
        // Cancelled at the restore point question: nothing was changed, like a cancelled confirmation (Import then stops too).
        if (outcome.Results.Count == 0 && !outcome.Stopped) return [];
        if (outcome.StoppedForRestorePoint)
        {
            // Enabling System Protection did not lead to a restore point: the batch stopped before changing anything.
            Report(Loc.Instance["Result_NoRestorePoint"], InfoBarSeverity.Warning);
            return [];
        }
        var text = outcome.Stopped
            ? Loc.Instance.Format("Result_BatchStopped", outcome.Applied, outcome.Failed, outcome.Skipped, tweaks.Count - outcome.Results.Count)
            : Loc.Instance.Format("Result_Batch", outcome.Applied, outcome.Failed, outcome.Skipped);
        Report(outcome.BackupsNotUpdated ? $"{text} {Loc.Instance["Result_BackupsNotUpdated"]}" : text,
            outcome.Failed > 0 || outcome.BackupsNotUpdated || outcome.Stopped ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        // Several changes at once: null makes the app read everything again (a single page-level item would not).
        if (outcome.Applied > 0 || outcome.Failed > 0) Changed?.Invoke(this, tweaks.Count == 1 ? tweaks[0] : null);
        return outcome.Results;
    }, []);

    public static string ResultText(TweakDefinition t, string title, ApplyResult result)
    {
        var text = OutcomeText(t, title, result);
        return result.BackupNotUpdated ? $"{text} {Loc.Instance["Result_BackupNotUpdated"]}" : text;
    }

    private static string OutcomeText(TweakDefinition t, string title, ApplyResult result)
    {
        var lang = Loc.Instance.Language;
        return result.Outcome switch
        {
            ApplyOutcome.Applied => Loc.Instance.Format(t.Restart ? "Result_AppliedRestart" : t.SignOut ? "Result_AppliedSignOut" : "Result_Applied", title),
            ApplyOutcome.AppliedIneffective => Loc.Instance.Format("Result_Ineffective", title),
            ApplyOutcome.NothingToDo => Loc.Instance["Result_NothingToDo"],
            ApplyOutcome.Blocked => string.Join(" ", (result.Blocks ?? []).Select(b => BlockText(b, lang))),
            // Only reached when a restore point was requested but could not be created: nothing was changed.
            ApplyOutcome.NeedsRestorePointDecision => Loc.Instance["Result_NoRestorePoint"],
            // The engine keeps the backup when part of the change could not be rolled back: then say so.
            _ when result.LeftChanged => Loc.Instance.Format("Result_FailedPartly", title, result.Error),
            _ => Loc.Instance.Format("Result_Failed", result.Error ?? "?"),
        };
    }

    public void Report(string text, InfoBarSeverity severity = InfoBarSeverity.Informational) => Status?.Invoke(this, new ResultMessage(text, severity));

    private static InfoBarSeverity SeverityOf(ApplyResult result) => result.Outcome switch
    {
        ApplyOutcome.Applied when result.BackupNotUpdated => InfoBarSeverity.Warning,
        ApplyOutcome.Applied => InfoBarSeverity.Success,
        ApplyOutcome.AppliedIneffective or ApplyOutcome.Blocked or ApplyOutcome.NeedsRestorePointDecision => InfoBarSeverity.Warning,
        ApplyOutcome.Failed => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    private static void Busy(bool busy) => ChangeGate.Instance.Busy = busy;
}
