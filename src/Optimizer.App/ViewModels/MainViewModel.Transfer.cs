using System.IO;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Backup;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

/// <summary>
/// Export and import of the changes (Changes page): the catalog tweaks this app changed and the start priority rules,
/// as a file for another PC or a reinstall. The import applies through the same runner as Apply recommended.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Opt-in sections of the export (Changes page), remembered for the next export.</summary>
    public bool ExportApps
    {
        get => _settings.ExportApps;
        set { _settings.ExportApps = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool ExportServices
    {
        get => _settings.ExportServices;
        set { _settings.ExportServices = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool ExportStartup
    {
        get => _settings.ExportStartup;
        set { _settings.ExportStartup = value; _settings.Save(); OnPropertyChanged(); }
    }

    [RelayCommand]
    private async Task ExportChangesAsync()
    {
        try
        {
            var facts = _facts;
            var (apps, serviceTypes, startup) = (ExportApps, ExportServices, ExportStartup);
            var (file, pcSpecific) = await Task.Run(() => ChangeTransfer.Create(
                _services.Store.All().Select(b => b.TweakId), AppliedIds(), _services.Catalog,
                ProgramPriority.Read(_services.Context.Registry), AppInfo.Text, _services.Os.BuildString,
                apps || serviceTypes || startup ? TransferData.Sections(_services, facts, apps, serviceTypes, startup) : null));
            if (file.Count == 0)
            {
                ShowResult(Loc.Instance["Transfer_NothingToExport"]);
                return;
            }
            var name = $"Tunero-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}{ChangeTransfer.Extension}";
            if (_dialogs.PickSaveFile(name, Loc.Instance["Transfer_FileType"], ChangeTransfer.Extension) is not { } path) return;
            var text = ChangeTransfer.Serialize(file);
            await Task.Run(() => SafeDelete.WriteReplacing(path, text));
            Log.Info("transfer", "exported", new { tweaks = file.Tweaks.Count, priorities = file.Priorities.Count, apps = file.Apps?.Count, services = file.Services?.Count, startup = file.Startup?.Count, pcSpecific });
            ShowResult(Loc.Instance.Format("Transfer_Exported", file.Count, Path.GetFileName(path), pcSpecific), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("transfer", $"export failed: {ex.Message}");
            ShowResult(ex is LinkedFolderException linked ? Loc.Instance.Format("Export_LinkedFolder", linked.Folder) : Loc.Instance.Format("Result_Error", ex.Message),
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ImportChangesAsync()
    {
        if (_dialogs.PickOpenFile(Loc.Instance["Transfer_FileType"], ChangeTransfer.ImportPattern) is not { } path) return;
        TransferFile? file;
        try
        {
            file = await ChangeTransfer.ReadFileAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowResult(Loc.Instance.Format("Result_Error", ex.Message), InfoBarSeverity.Error);
            return;
        }
        if (file is null)
        {
            ShowResult(Loc.Instance["Transfer_Invalid"], InfoBarSeverity.Warning);
            return;
        }
        var profile = Profile;
        var targets = await Task.Run(() => TransferData.Targets(_services, file, profile));
        var plan = ChangeTransfer.Plan(file, _services.Catalog, _facts, AppliedIds(), targets);
        Log.Info("transfer", "import", new { together = plan.Together.Count, oneByOne = plan.OneByOne.Count, apps = plan.Apps.Count, plan.AlreadyOn, plan.NotForThisPc, plan.Unknown });
        var skipped = Loc.Instance.Format("Transfer_Skipped", plan.AlreadyOn, plan.NotForThisPc, plan.Unknown);
        if (plan.IsEmpty)
        {
            ShowResult(Loc.Instance["Transfer_Nothing"] + " " + skipped);
            return;
        }
        if (plan.Together.Count > 0)
        {
            var summary = Loc.Instance["Transfer_Intro"];
            if (plan.OneByOne.Count > 0) summary += " " + Loc.Instance.Format("Transfer_LaterOneByOne", plan.OneByOne.Count);
            var results = await Runner.ApplyBatchAsync(plan.Together, Loc.Instance["Transfer_Title"], summary);
            // Cancelled, or stopped in the banner (fewer results than changes): the rest of the import does not follow.
            if (results.Count < plan.Together.Count) return;
        }
        if (plan.OneByOne.Count > 0 &&
            _dialogs.Ask(Loc.Instance["Transfer_OneByOneTitle"], Loc.Instance.Format("Transfer_OneByOneText", plan.OneByOne.Count), Loc.Instance["Transfer_OneByOneGo"]))
        {
            // Each one with its own confirmation (preview, Expert and anti-cheat warnings); cancelling skips it.
            foreach (var t in plan.OneByOne) await Runner.ApplyAsync(t);
        }
        // One result line at the end: the app removal's and the skipped counts together, so neither hides the other.
        var removal = plan.Apps.Count > 0 ? await RemoveImportedAppsAsync(plan.Apps) : null;
        if (removal is { Stopped: true }) return; // the change gate's own message (busy, error) stays
        var hasSkipped = plan.AlreadyOn + plan.NotForThisPc + plan.Unknown > 0;
        if (removal is { Text: { } text, Severity: var severity }) ShowResult(hasSkipped ? text + " " + skipped : text, severity);
        else if (hasSkipped) ShowResult(skipped);
    }

    /// <summary>The removal's result line; <see cref="Stopped"/> when it did not run (the change gate showed why).</summary>
    private sealed record AppRemoval(string? Text, InfoBarSeverity Severity, bool Stopped = false);

    /// <summary>
    /// "Copy to new accounts": the per-user tweaks that are on, written to the Default profile as one change with its own
    /// confirmation (every value is listed) and undo on this page.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task CopyToNewAccountsAsync()
    {
        // Only what Tunero turned on (it has a backup): a tweak that reads as on because Windows' default matches stays out.
        var on = _tweakStates.Where(s => s.State is TweakState.Applied or TweakState.PendingRestart && HasBackup(s.Tweak.Id)).Select(s => s.Tweak).ToList();
        var engine = _services.Engine;
        var tweak = await Task.Run(() => NewUserDefaults.Build(on.Select(t => (t, engine.Expand(t)))));
        if (tweak is null)
        {
            ShowResult(Loc.Instance["NewUsers_Nothing"]);
            return;
        }
        await Runner.ApplyAsync(tweak);
    }

    /// <summary>
    /// The file's removed apps that this PC has: removing cannot be undone (the Store link stays for apps the Store
    /// offers), so they are asked for on their own, after the changes, with every name listed.
    /// </summary>
    private async Task<AppRemoval> RemoveImportedAppsAsync(IReadOnlyList<Optimizer.Core.Debloat.DebloatItem> apps)
    {
        var lang = Loc.Instance.Language;
        var names = string.Join(", ", apps.Select(a => a.Entry.Label(lang)));
        if (!_dialogs.Ask(Loc.Instance["Transfer_AppsTitle"], Loc.Instance.Format("Transfer_AppsText", apps.Count, names), Loc.Instance["Debloat_Remove"]))
            return new AppRemoval(null, InfoBarSeverity.Informational);
        var removed = 0;
        var errors = new List<string>();
        var ran = await Runner.RunExclusiveAsync("import remove apps", async () =>
        {
            var service = new Optimizer.Core.Debloat.DebloatService(_services.Context.Processes, AppServices.DataFolder);
            foreach (var app in apps)
            {
                var error = await Task.Run(() => service.Remove(app.Installed, app.Entry));
                if (error is null) removed++;
                else errors.Add($"{app.Entry.Label(lang)}: {error}");
            }
        });
        if (!ran) return new AppRemoval(null, InfoBarSeverity.Informational, Stopped: true);
        await Debloat.ReloadAsync();
        return new AppRemoval(Loc.Instance.Format("Transfer_AppsRemoved", removed) + (errors.Count > 0 ? " " + string.Join("; ", errors) : ""),
            errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }
}
