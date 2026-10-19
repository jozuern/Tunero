using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Apps;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

public sealed partial class AppRow(AppEntry app, string lang, bool installed) : ObservableObject
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;

    public AppEntry App { get; } = app;
    public string Name => App.Name;
    public string Text { get; } = app.Text(lang);
    public string Category { get; } = Labels.Current.Get(lang, $"appCategory.{app.Category}");
    public bool PerUser => App.Scope == "user";

    /// <summary>Category heading above the first app of each category (the catalog lists them grouped).</summary>
    public string? GroupHeader { get; init; }

    public Wpf.Ui.Controls.SymbolRegular Symbol => App.Category switch
    {
        "launchers" => Wpf.Ui.Controls.SymbolRegular.Games24,
        "streaming" => Wpf.Ui.Controls.SymbolRegular.Video24,
        "peripherals" => Wpf.Ui.Controls.SymbolRegular.Keyboard24,
        "communication" => Wpf.Ui.Controls.SymbolRegular.Chat24,
        "monitoring" => Wpf.Ui.Controls.SymbolRegular.Gauge24,
        "media" => Wpf.Ui.Controls.SymbolRegular.Play24,
        "browsers" => Wpf.Ui.Controls.SymbolRegular.Globe24,
        "runtimes" => Wpf.Ui.Controls.SymbolRegular.Box24,
        _ => Wpf.Ui.Controls.SymbolRegular.Toolbox24,
    };

    [ObservableProperty] private bool _installed = installed;

    /// <summary>"Install Discord": what a screen reader says for the button (each row has one).</summary>
    public string InstallLabel => Loc.Instance.Format("Apps_InstallName", Name);

    public string UpdateLabel => Loc.Instance.Format("Apps_UpdateName", Name);

    /// <summary>Installed for the whole PC: winget can update it from here (per-user apps update in their own installer).</summary>
    public bool CanUpdate => Installed && !PerUser;

    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(CanUpdate));
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _stateText = installed ? Loc.Instance["Apps_Installed"] : "";
}

/// <summary>A package from a winget search: not on the reviewed list, installed only after its own confirmation.</summary>
public sealed partial class WingetResultRow(WingetSearchResult package) : ObservableObject
{
    // What screen readers announce for this item in a list.
    public override string ToString() => Name;

    public WingetSearchResult Package { get; } = package;
    public string Name => Package.Name;
    public string Meta { get; } = Loc.Instance.Format("Apps_SearchMeta", package.Id, package.Version);
    public string InstallLabel => Loc.Instance.Format("Apps_InstallName", Name);

    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _stateText = "";
}

public sealed record DriverItem(string Device, string ClassText, string Provider, string Version, string DateText, string? UpdateUrl, bool Old)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Device;

    /// <summary>Class, provider and version; a driver without provider or version shows no empty part.</summary>
    public string MetaText => string.Join(", ", new[] { ClassText, $"{Provider} {Version}".Trim() }.Where(x => x.Length > 0));

    /// <summary>Heading above the first driver of an update source ("Updates from Intel"), with one link for the group.</summary>
    public string? GroupHeader { get; init; }

    public string? GroupUrl { get; init; }

    /// <summary>"10.08.2018, about 8 years old": the date in the app language and a rounded age. Placeholder dates say so.</summary>
    public static string Describe(DateTime? date, int? ageDays)
    {
        if (date is not { } d) return "";
        if (ageDays is not { } a) return Loc.Instance["Drv_NoRealDate"];
        var shown = d.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        // A date after today (some inbox drivers carry one) has no meaningful age.
        if (a < 0) return shown;
        var age = a < 60 ? Loc.Instance["Drv_AgeNew"]
            : a < 730 ? Loc.Instance.Format("Drv_AgeMonths", (int)Math.Round(a / 30.44))
            : Loc.Instance.Format("Drv_AgeYears", (int)Math.Round(a / 365.25));
        return $"{shown}, {age}";
    }
}

/// <summary>A desktop program in the uninstall list.</summary>
public sealed partial class ProgramRow(DesktopProgram program) : ObservableObject
{
    public DesktopProgram Program { get; } = program;
    public string Name => Program.Name;

    public string Meta { get; } = string.Join(", ", new[]
    {
        program.Publisher, program.Version,
        program.SizeKb is { } kb ? CleanupViewModel.Size(kb * 1024) : null,
        program.InstallDate?.ToString("d"),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [ObservableProperty] private string? _stateText;
    [ObservableProperty] private bool _isRemoving;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Tells the page that the number of selected programs changed.</summary>
    internal Action? SelectionChanged { get; init; }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    /// <summary>"Uninstall Steam": what a screen reader says for the button (each row has one).</summary>
    public string UninstallLabel => Loc.Instance.Format("Programs_UninstallName", Name);

    public string SelectLabel => Loc.Instance.Format("Programs_SelectName", Name);
}

/// <summary>A folder or shortcut an uninstalled program left behind, offered for the Recycle Bin.</summary>
public sealed partial class LeftoverRow(Leftover leftover, string program) : ObservableObject
{
    public Leftover Leftover { get; } = leftover;
    public string ProgramName { get; } = program;
    public string Path => Leftover.Path;

    /// <summary>"Folder, 120 MB, left by App", in the language of the moment: a language switch builds new rows.</summary>
    public string Meta { get; } = leftover.LastChangeUtc is { } changed
        ? Loc.Instance.Format("Leftovers_OrphanMeta", CleanupViewModel.Size(leftover.Bytes), changed.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))
        : leftover.Kind == LeftoverKind.Folder
        ? Loc.Instance.Format("Leftovers_FolderMeta", CleanupViewModel.Size(leftover.Bytes), program)
        : Loc.Instance.Format("Leftovers_ShortcutMeta", program);

    [ObservableProperty] private bool _selected = true;
    [ObservableProperty] private string? _stateText;
}

public sealed partial class AppsViewModel(IPageHost owner, AppServices services, IDialogs dialogs) : PageViewModel(owner)
{
    /// <summary>The installed state of the catalog apps comes from the scan result (installed programs).</summary>
    protected override bool UsesProfile => true;
    private List<ProgramRow> _allPrograms = [];
    private bool _restorePointReady;

    public ObservableCollection<ProgramRow> ProgramRows { get; } = [];

    /// <summary>What uninstalled programs left behind, for the user to review (see <see cref="ProgramLeftovers"/>).</summary>
    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];

    [ObservableProperty] private string _programSearch = "";
    [ObservableProperty] private string _programsText = "";
    [ObservableProperty] private bool _isUpdatingAll;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _quietUninstall = true;
    [ObservableProperty] private bool _isQueueRunning;
    [ObservableProperty] private bool _isWaitingForUninstaller;
    [ObservableProperty] private string _queueText = "";
    [ObservableProperty] private bool _isRecycling;

    private CancellationTokenSource? _queueStop;
    private TaskCompletionSource? _skipWait;

    public bool HasSelection => SelectedCount > 0;
    public string UninstallSelectedText => Loc.Instance.Format("Programs_UninstallSelected", SelectedCount);

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(UninstallSelectedText));
    }

    private void UpdateSelection() => SelectedCount = _allPrograms.Count(r => r.IsSelected);

    partial void OnProgramSearchChanged(string value) => FilterPrograms();

    private void FilterPrograms()
    {
        ProgramRows.Clear();
        var q = ProgramSearch.Trim();
        foreach (var r in _allPrograms.Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                  || (r.Program.Publisher?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            ProgramRows.Add(r);
        ProgramsText = Loc.Instance.Format("Programs_Count", _allPrograms.Count);
    }

    [RelayCommand]
    private async Task LoadProgramsAsync()
    {
        var list = await Task.Run(() => Programs.Read(services.Context.Registry));
        // The selection and the state lines survive a reload for the programs that are still listed.
        var old = _allPrograms.GroupBy(r => r.Program.RegistryKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _allPrograms = list.Select(p => new ProgramRow(p)
        {
            IsSelected = old.TryGetValue(p.RegistryKey, out var o) && o.IsSelected,
            StateText = o?.StateText,
            SelectionChanged = UpdateSelection,
        }).ToList();
        UpdateSelection();
        services.Search.UpdateRuntime(Optimizer.Core.Search.SearchKind.InstalledProgram, _allPrograms.Select(r => ("Debloat", r.Program.RegistryKey, r.Name, r.Meta)));
        FilterPrograms();
    }

    /// <summary>Runs the program's own uninstaller (see <see cref="Programs.Command"/> for when it runs elevated).</summary>
    [RelayCommand]
    private async Task UninstallProgramAsync(ProgramRow? row)
    {
        if (row is null || row.IsRemoving) return;
        if (Programs.Command(row.Program) is not { } command)
        {
            row.StateText = Loc.Instance["Programs_NoUninstaller"];
            return;
        }
        var text = Loc.Instance.Format("Programs_ConfirmText", row.Name) + (command.Mode == UninstallMode.AsUser ? "\n\n" + Loc.Instance["Programs_AsUser"] : "");
        if (!dialogs.Ask(Loc.Instance["Programs_ConfirmTitle"], text, Loc.Instance["Programs_Uninstall"])) return;
        row.IsRemoving = true;
        row.StateText = Loc.Instance["Programs_Running"];
        string? state = null;
        try
        {
            await RunWorkAsync($"uninstall {row.Name}", async () =>
            {
                if (!await EnsureRestorePointAsync())
                {
                    state = "";
                    return;
                }
                var msi = await MsiFoldersAsync([row.Program]);
                var code = await ProgramUninstaller.RunAsync(command, services.Elevation);
                if (code is null)
                {
                    state = Loc.Instance["Programs_StartedAsUser"];
                    return;
                }
                await LoadProgramsAsync();
                // The list was read again: the row of the program (if it is still listed) is a new object.
                var still = _allPrograms.FirstOrDefault(r => r.Program.RegistryKey == row.Program.RegistryKey);
                if (still is not null)
                {
                    // Many uninstallers start a copy of themselves and end at once (exit code 0): it may still be running.
                    still.StateText = code == 0 ? Loc.Instance["Programs_MaybeStillRunning"] : Loc.Instance.Format("Programs_StillThere", code);
                }
                else
                {
                    ProgramsText = Loc.Instance.Format("Programs_RemovedName", row.Name);
                    await FindLeftoversAsync([WithMsiFolder(row.Program, msi)]);
                }
            });
        }
        finally
        {
            row.IsRemoving = false;
            if (state is not null) row.StateText = state;
            else if (row.StateText == Loc.Instance["Programs_Running"]) row.StateText = "";
        }
    }

    /// <summary>
    /// A restore point before the first uninstall of the session. When none can be made (System Protection off, or
    /// Windows' limit of one per day), the user decides whether to go on.
    /// </summary>
    private async Task<bool> EnsureRestorePointAsync()
    {
        if (_restorePointReady) return true;
        var made = await Task.Run(() => services.RestorePoints.IsEnabled()) == true
                   && await services.RestorePoints.CreateAsync(Loc.Instance["Programs_RestorePointName"]);
        if (!made && !dialogs.Ask(Loc.Instance["Programs_NoRestorePointTitle"], Loc.Instance["Programs_NoRestorePointText"], Loc.Instance["Programs_Uninstall"]))
            return false;
        _restorePointReady = true;
        return true;
    }

    /// <summary>
    /// Uninstalls the selected programs one after the other (Bulk Crap Uninstaller's queue): quietly where the program
    /// has a quiet command and the switch is on, else with the uninstaller's window. The next one starts when the
    /// program has left the uninstall list, when the user selects Continue, or after 30 minutes; Stop starts no more.
    /// </summary>
    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (IsQueueRunning) return;
        var quietOn = QuietUninstall;
        var plan = _allPrograms.Where(r => r.IsSelected && !r.IsRemoving).Select(r =>
        {
            var quiet = quietOn ? Programs.QuietCommand(r.Program) : null;
            return (Row: r, Command: quiet ?? Programs.Command(r.Program), Quiet: quiet is not null);
        }).ToList();
        foreach (var p in plan.Where(p => p.Command is null)) p.Row.StateText = Loc.Instance["Programs_NoUninstaller"];
        var queue = plan.Where(p => p.Command is not null).ToList();
        if (queue.Count == 0) return;
        var text = Loc.Instance.Format("Programs_BulkConfirmText", queue.Count);
        if (queue.Where(p => p.Quiet).Select(p => p.Row.Name).ToList() is { Count: > 0 } quietNames)
            text += "\n\n" + Loc.Instance.Format("Programs_BulkQuiet", string.Join(", ", quietNames));
        if (queue.Where(p => !p.Quiet).Select(p => p.Row.Name).ToList() is { Count: > 0 } windowNames)
            text += "\n\n" + Loc.Instance.Format("Programs_BulkWindow", string.Join(", ", windowNames));
        if (!dialogs.Ask(Loc.Instance["Programs_BulkConfirmTitle"], text, Loc.Instance["Programs_Uninstall"])) return;

        IsQueueRunning = true;
        using var stop = new CancellationTokenSource();
        _queueStop = stop;
        // Stop here or in the banner: no wait for the running uninstaller either.
        using var stopWaiting = stop.Token.Register(() => _skipWait?.TrySetResult());
        var removed = new List<DesktopProgram>();
        var started = 0;
        try
        {
            await RunWorkAsync("uninstall selected programs", Loc.Instance["Work_Uninstall"], async work =>
            {
                if (!await EnsureRestorePointAsync()) return;
                var msi = await MsiFoldersAsync(queue.Select(q => q.Row.Program));
                foreach (var (row, command, _) in queue)
                {
                    if (stop.IsCancellationRequested) break;
                    work.Step(started, queue.Count, row.Name);
                    started++;
                    QueueText = Loc.Instance.Format("Programs_QueueProgress", started, queue.Count, row.Name);
                    row.IsRemoving = true;
                    row.StateText = Loc.Instance["Programs_Running"];
                    try
                    {
                        var code = await ProgramUninstaller.RunAsync(command!, services.Elevation);
                        // Started as the user, or ended at once after starting a copy of itself: wait for the entry to go.
                        if (code is null || Programs.Succeeded(code.Value)) await WaitUntilGoneAsync(row.Program, stop.Token);
                        if (!await Task.Run(() => Programs.StillListed(services.Context.Registry, row.Program)))
                        {
                            removed.Add(WithMsiFolder(row.Program, msi));
                            row.StateText = Loc.Instance.Format("Programs_RemovedName", row.Name);
                        }
                        else
                        {
                            row.StateText = code is { } c && !Programs.Succeeded(c) ? Loc.Instance.Format("Programs_StillThere", c) : Loc.Instance["Programs_MaybeStillRunning"];
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        row.StateText = Loc.Instance.Format("Result_Error", ex.Message);
                    }
                    finally
                    {
                        row.IsRemoving = false;
                    }
                }
            }, stop);
        }
        finally
        {
            _queueStop = null;
            IsQueueRunning = false;
            IsWaitingForUninstaller = false;
            QueueText = "";
        }
        if (started == 0) return;
        await LoadProgramsAsync();
        Owner.ShowResult(Loc.Instance.Format("Programs_BulkDone", removed.Count, queue.Count),
            removed.Count == queue.Count ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning);
        if (removed.Count > 0) await FindLeftoversAsync(removed);
    }

    private async Task WaitUntilGoneAsync(DesktopProgram program, CancellationToken stop)
    {
        var skip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _skipWait = skip;
        IsWaitingForUninstaller = true;
        try
        {
            var until = DateTime.UtcNow.AddMinutes(30);
            while (DateTime.UtcNow < until && !stop.IsCancellationRequested && !skip.Task.IsCompleted
                   && await Task.Run(() => Programs.StillListed(services.Context.Registry, program)))
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2), stop), skip.Task);
        }
        finally
        {
            _skipWait = null;
            IsWaitingForUninstaller = false;
        }
    }

    /// <summary>Starts the next uninstaller without waiting for the current one to leave the list.</summary>
    [RelayCommand]
    private void ContinueQueue() => _skipWait?.TrySetResult();

    /// <summary>Starts no further uninstaller; the one running goes on in its own window.</summary>
    [RelayCommand]
    private void StopQueue()
    {
        _queueStop?.Cancel();
        _skipWait?.TrySetResult();
    }

    /// <summary>Install folders of the Windows Installer programs without an install location, read before their uninstall removes the records.</summary>
    private Task<IReadOnlyDictionary<string, string>> MsiFoldersAsync(IEnumerable<DesktopProgram> programs)
    {
        var codes = programs.Where(p => p.ProductCode is not null && string.IsNullOrWhiteSpace(p.InstallLocation)).Select(p => p.ProductCode!).ToList();
        return Task.Run(() => ProgramLeftovers.MsiFolders(services.Context.Registry, codes));
    }

    private static DesktopProgram WithMsiFolder(DesktopProgram p, IReadOnlyDictionary<string, string> msi) =>
        p.ProductCode is { } code && string.IsNullOrWhiteSpace(p.InstallLocation) && msi.TryGetValue(code, out var folder) ? p with { InstallLocation = folder } : p;

    private async Task FindLeftoversAsync(IReadOnlyList<DesktopProgram> removed)
    {
        var roots = LeftoverRoots.ForPc(services.ProfilePath);
        var found = await Task.Run(async () =>
        {
            // Some uninstallers remove their entry first and delete the files afterwards: wait until the folders stop
            // changing (at most a minute), so nothing is offered while it is still being deleted.
            var folders = removed.SelectMany(p => ProgramLeftovers.Folders(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var last = ProgramLeftovers.Footprint(folders);
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                var now = ProgramLeftovers.Footprint(folders);
                if (now == last) break;
                last = now;
            }
            // Everything that still points into program folders, as the unused-folder search reads it: also the files of
            // Windows Installer products still installed (which often record no install location), App Paths and COM servers.
            var referenced = ProgramOrphans.ReadEvidence(services.Context.Registry, []).Paths;
            return ProgramLeftovers.FindAll(removed, roots, referenced);
        });
        foreach (var (program, l) in found)
            if (!Leftovers.Any(x => string.Equals(x.Path, l.Path, StringComparison.OrdinalIgnoreCase))) Leftovers.Add(new LeftoverRow(l, program.Name));
    }

    /// <summary>Moves the ticked leftovers to the Recycle Bin, where they can be restored.</summary>
    [RelayCommand]
    private async Task RecycleLeftoversAsync()
    {
        var picked = Leftovers.Where(r => r.Selected).ToList();
        if (picked.Count == 0 || IsRecycling) return;
        IsRecycling = true;
        try
        {
            // Gone already (the uninstaller finished after the check): nothing left to move.
            var failed = await Task.Run(() => picked.Where(r => Path.Exists(r.Path) && !StorageAnalyzer.RecycleLeftover(r.Path)).ToList());
            foreach (var r in picked.Except(failed)) Leftovers.Remove(r);
            foreach (var r in failed) r.StateText = Loc.Instance["Leftovers_Failed"];
            Owner.ShowResult(Loc.Instance.Format("Leftovers_Recycled", picked.Count - failed.Count),
                failed.Count == 0 ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
        finally
        {
            IsRecycling = false;
        }
    }

    [RelayCommand]
    private void DismissLeftovers() => Leftovers.Clear();

    [ObservableProperty] private bool _isFindingOrphans;

    /// <summary>
    /// Program Files folders that no installed program, service, autostart entry, COM server, running program or shortcut
    /// uses (<see cref="ProgramOrphans"/>). They join the Left behind list unticked: the user decides each one.
    /// </summary>
    [RelayCommand]
    private async Task FindOrphansAsync()
    {
        if (IsFindingOrphans) return;
        IsFindingOrphans = true;
        try
        {
            var roots = LeftoverRoots.ForPc(services.ProfilePath);
            var found = await Task.Run(() =>
            {
                var inUse = new List<string>();
                inUse.AddRange(ProgramOrphans.PinnedTargets(services.ProfilePath));
                // Every autostart location of the Startup page (services, drivers, tasks, extensions among them).
                foreach (var e in new StartupScanner(services.Context.Registry, services.Context.Tasks, services.ProfilePath).ScanAll(includeServicesAndDrivers: true, includeWmi: false))
                    if (e.ImagePath is { Length: > 0 } image) inUse.Add(image);
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            if (p.MainModule?.FileName is { } file) inUse.Add(file);
                        }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            // a protected or ended process
                        }
                    }
                }
                var evidence = ProgramOrphans.ReadEvidence(services.Context.Registry, inUse);
                return ProgramOrphans.Find([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)],
                    roots, evidence, DateTime.UtcNow);
            });
            foreach (var l in found)
                if (!Leftovers.Any(x => string.Equals(x.Path, l.Path, StringComparison.OrdinalIgnoreCase))) Leftovers.Add(new LeftoverRow(l, "") { Selected = false });
            Owner.ShowResult(found.Count == 0 ? Loc.Instance["Orphans_None"] : Loc.Instance.Format("Orphans_Found", found.Count),
                found.Count == 0 ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Informational);
        }
        finally
        {
            IsFindingOrphans = false;
        }
    }

    /// <summary>
    /// Developer aid for screenshots (--preview-uninstall): ticks the first two programs and shows a waiting queue and
    /// sample leftovers. Nothing is started, read from disk or moved.
    /// </summary>
    public void PreviewUninstall()
    {
        foreach (var r in _allPrograms.Take(2)) r.IsSelected = true;
        var name = _allPrograms.FirstOrDefault()?.Name ?? "Contoso";
        IsQueueRunning = true;
        IsWaitingForUninstaller = true;
        QueueText = Loc.Instance.Format("Programs_QueueProgress", 1, 2, name);
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Contoso Tool");
        Leftovers.Add(new LeftoverRow(new Leftover(LeftoverKind.Folder, folder, 48_234_496), "Contoso Tool"));
        Leftovers.Add(new LeftoverRow(new Leftover(LeftoverKind.Shortcut,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Contoso Tool.lnk"), 0), "Contoso Tool"));
    }

    /// <summary>winget upgrade --all: every package winget knows an update for, from the winget source.</summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (_winget is null)
        {
            Output = Loc.Instance["Apps_NoWinget"];
            return;
        }
        if (!dialogs.Ask(Loc.Instance["Apps_UpdateAllTitle"], Loc.Instance["Apps_UpdateAllText"], Loc.Instance["Apps_UpdateAll"])) return;
        IsUpdatingAll = true;
        var lines = new List<string>();
        WorkProgress.Scope? banner = null;
        var progress = new Progress<string>(l =>
        {
            lines.Add(l);
            if (lines.Count > 40) lines.RemoveAt(0);
            Output = string.Join("\n", lines);
            // "(2/5) Found ..." as winget starts each package; without it the bar keeps moving.
            if (ToolProgress.WingetPackage(l) is var (done, total)) banner?.Step(done, total, l[(l.IndexOf(')') + 1)..].Trim());
        });
        try
        {
            var winget = _winget;
            // No Stop: ending winget in the middle of an installer can leave a program half updated.
            await RunWorkAsync("winget upgrade all", Loc.Instance["Work_UpdateApps"], async work =>
            {
                banner = work;
                var code = await StreamingProcess.RunAsync(winget, Winget.UpgradeAllArguments("machine"), progress);
                lines.Add(Winget.IsSuccess(code) ? Loc.Instance["Apps_UpdateAllDone"] : Loc.Instance.Format("Apps_UpdateFailed", $"0x{code:X8}"));
                // Per-user programs update as the signed-in user, in a window of their own.
                if (_wingetForUser is { } forUser && Winget.UpgradeAllForUser(forUser, services.Elevation) != DeElevatedLauncher.Path.Failed)
                    lines.Add(Loc.Instance["Apps_UpdateAllUser"]);
                Output = string.Join("\n", lines);
            });
        }
        finally
        {
            IsUpdatingAll = false;
        }
    }

    // Elevated installs only use winget from the protected package folder; per-user installs run as the user and may use the alias.
    private string? _winget;
    private string? _wingetForUser;

    public ObservableCollection<AppRow> Apps { get; } = [];
    public ObservableCollection<DriverItem> Drivers { get; } = [];

    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _wingetMissing;

    private IReadOnlyList<DriverRow>? _drivers;

    protected override async Task LoadAsync()
    {
        (_winget, _wingetForUser) = await Task.Run(() => (Winget.FindTrusted(), Winget.FindForUser(services.ProfilePath)));
        WingetMissing = _winget is null && _wingetForUser is null;
        ShowApps();
        await LoadProgramsAsync();
        _drivers = await Task.Run(DriverInventory.Read);
        ShowDrivers(_drivers);
    }

    // A language switch builds the rows from the programs and drivers already read.
    protected override Task RelabelAsync()
    {
        if (_drivers is null) return LoadAsync();
        ShowApps();
        FilterPrograms();
        ShowDrivers(_drivers);
        for (var i = 0; i < Leftovers.Count; i++)
            Leftovers[i] = new LeftoverRow(Leftovers[i].Leftover, Leftovers[i].ProgramName) { Selected = Leftovers[i].Selected };
        OnPropertyChanged(nameof(UninstallSelectedText));
        for (var i = 0; i < SearchResults.Count; i++) SearchResults[i] = new WingetResultRow(SearchResults[i].Package);
        if (!IsSearching && SearchResults.Count > 0) SearchNote = Loc.Instance.Format("Apps_SearchCount", SearchResults.Count);
        return Task.CompletedTask;
    }

    private void ShowApps()
    {
        var lang = Lang;
        var programs = Owner.Profile?.Extras?.Programs ?? [];
        Apps.Clear();
        string? last = null;
        foreach (var a in CatalogData.Current.Apps.Apps)
        {
            Apps.Add(new AppRow(a, lang, a.IsInstalled(programs)) { GroupHeader = a.Category == last ? null : Labels.Current.Get(lang, $"appCategory.{a.Category}") });
            last = a.Category;
        }
    }

    private void ShowDrivers(IReadOnlyList<DriverRow> drivers)
    {
        var vendor = CatalogData.Current.Bios.NormalizeVendor(Owner.Profile?.Firmware?.BoardManufacturer);
        var board = BiosAgeCheck.SupportUrl(vendor);
        Drivers.Clear();
        var today = DateTime.Today;
        // One link per update source instead of one per driver: most chipset rows lead to the same Intel page. Sources
        // keep the order of their first driver; drivers without a known source come last.
        var groups = drivers.GroupBy(d => DriverInventory.UpdateUrl(d, board) ?? "").OrderBy(g => g.Key.Length == 0);
        foreach (var g in groups)
        {
            var first = true;
            foreach (var d in g)
            {
                var url = g.Key.Length == 0 ? null : g.Key;
                Drivers.Add(new DriverItem(d.Device, Loc.Instance[$"DrvClass_{d.Class}"] is var c && c != $"DrvClass_{d.Class}" ? c : d.Class,
                    d.Provider ?? "", d.Version ?? "", DriverItem.Describe(d.Date, d.AgeDays(today)), url,
                    d.AgeDays(today) > 2 * 365 && d.Class is "DISPLAY" or "NET" or "MEDIA")
                {
                    GroupHeader = first ? GroupTitle(url) : null,
                    GroupUrl = first ? url : null,
                });
                first = false;
            }
        }
    }

    /// <summary>Who the update link leads to, for the group heading.</summary>
    private static string GroupTitle(string? url) =>
        url is null ? Loc.Instance["Drv_GroupOther"]
        : url.Contains("nvidia.", StringComparison.OrdinalIgnoreCase) ? Loc.Instance.Format("Drv_GroupFrom", "NVIDIA")
        : url.Contains("amd.", StringComparison.OrdinalIgnoreCase) ? Loc.Instance.Format("Drv_GroupFrom", "AMD")
        : url.Contains("intel.", StringComparison.OrdinalIgnoreCase) ? Loc.Instance.Format("Drv_GroupFrom", "Intel")
        : Loc.Instance["Drv_GroupBoard"];

    [RelayCommand]
    private async Task InstallAsync(AppRow? row)
    {
        if (row is null) return;
        if (row.PerUser)
        {
            if (_wingetForUser is null) return;
            var path = Winget.InstallForUser(_wingetForUser, row.App, services.Elevation);
            row.StateText = path == DeElevatedLauncher.Path.Failed ? Loc.Instance.Format("Result_Error", "winget") : Loc.Instance["Apps_StartedForUser"];
            return;
        }
        if (_winget is null)
        {
            row.StateText = Loc.Instance["Apps_NoWinget"];
            return;
        }
        row.IsInstalling = true;
        row.StateText = Loc.Instance["Apps_Installing"];
        var lines = new List<string>();
        var progress = new Progress<string>(l =>
        {
            lines.Add(l);
            if (lines.Count > 40) lines.RemoveAt(0);
            Output = string.Join("\n", lines);
        });
        try
        {
            var winget = _winget;
            if (!await RunWorkAsync($"install {row.App.Id}", async () =>
                {
                    var code = await Winget.InstallAsync(winget, row.App, progress, CancellationToken.None);
                    row.Installed = Winget.IsSuccess(code);
                    row.StateText = row.Installed ? Loc.Instance["Apps_Installed"] : Loc.Instance.Format("Apps_Failed", $"0x{code:X8}");
                }))
                row.StateText = "";
        }
        finally
        {
            row.IsInstalling = false;
        }
    }

    /// <summary>winget upgrade for one app: the newest version from the winget source, or "already up to date".</summary>
    [RelayCommand]
    private async Task UpdateAppAsync(AppRow? row)
    {
        if (row is null || !row.CanUpdate) return;
        if (_winget is null)
        {
            row.StateText = Loc.Instance["Apps_NoWinget"];
            return;
        }
        row.IsInstalling = true;
        row.StateText = Loc.Instance["Apps_Updating"];
        var lines = new List<string>();
        var progress = new Progress<string>(l =>
        {
            lines.Add(l);
            if (lines.Count > 40) lines.RemoveAt(0);
            Output = string.Join("\n", lines);
        });
        try
        {
            var winget = _winget;
            if (!await RunWorkAsync($"update {row.App.Id}", async () =>
                {
                    var code = await Winget.UpgradeAsync(winget, row.App, progress, CancellationToken.None);
                    row.StateText = code == Winget.UpdateNotApplicable ? Loc.Instance["Apps_UpToDate"]
                        : Winget.IsSuccess(code) ? Loc.Instance["Apps_Updated"]
                        : Loc.Instance.Format("Apps_UpdateFailed", $"0x{code:X8}");
                }))
                row.StateText = Loc.Instance["Apps_Installed"];
        }
        finally
        {
            row.IsInstalling = false;
        }
    }

    // ---------------- any package from winget ----------------

    public ObservableCollection<WingetResultRow> SearchResults { get; } = [];
    [ObservableProperty] private string _wingetQuery = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _searchNote;

    /// <summary>What winget printed for an install from the search, under the results (the page's own output box is below the long list).</summary>
    [ObservableProperty] private string _searchOutput = "";

    /// <summary>Searches the winget community repository with the trusted winget (a read: nothing is installed).</summary>
    [RelayCommand]
    private async Task SearchWingetAsync()
    {
        if (IsSearching) return;
        var query = WingetQuery.Trim();
        if (!Winget.IsSafeQuery(query))
        {
            SearchNote = Loc.Instance["Apps_SearchInvalid"];
            return;
        }
        if (_winget is null)
        {
            SearchNote = Loc.Instance["Apps_NoWinget"];
            return;
        }
        IsSearching = true;
        SearchNote = Loc.Instance["Apps_Searching"];
        SearchResults.Clear();
        try
        {
            var found = await Winget.SearchAsync(_winget, query, CancellationToken.None);
            foreach (var p in found) SearchResults.Add(new WingetResultRow(p));
            SearchNote = found.Count == 0 ? Loc.Instance.Format("Apps_SearchNone", query) : Loc.Instance.Format("Apps_SearchCount", found.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            SearchNote = Loc.Instance.Format("Result_Error", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Installs a package from the search after a confirmation that says Tunero did not review it: for the whole PC where
    /// the package offers that, else (only a per-user installer) for the signed-in user in winget's own window.
    /// </summary>
    [RelayCommand]
    private async Task InstallFoundAsync(WingetResultRow? row)
    {
        if (row is null || row.IsInstalling) return;
        if (_winget is null)
        {
            row.StateText = Loc.Instance["Apps_NoWinget"];
            return;
        }
        if (!dialogs.Ask(Loc.Instance.Format("Apps_FoundConfirmTitle", row.Name), Loc.Instance.Format("Apps_FoundConfirmText", row.Name, row.Package.Id),
                Loc.Instance["Apps_Install"])) return;
        var app = new AppEntry { Id = row.Package.Id, Name = row.Name, WingetScope = "machine" };
        row.IsInstalling = true;
        row.StateText = Loc.Instance["Apps_Installing"];
        var lines = new List<string>();
        var progress = new Progress<string>(l =>
        {
            lines.Add(l);
            if (lines.Count > 40) lines.RemoveAt(0);
            SearchOutput = string.Join("\n", lines);
        });
        try
        {
            var winget = _winget;
            if (!await RunWorkAsync($"install {app.Id} (search)", async () =>
                {
                    var code = await Winget.InstallAsync(winget, app, progress, CancellationToken.None);
                    if (code == Winget.NoApplicableInstaller && _wingetForUser is { } forUser)
                    {
                        // Only a per-user installer: it runs as the signed-in user, in winget's own window.
                        var path = Winget.InstallForUser(forUser, new AppEntry { Id = app.Id, Name = app.Name, Scope = "user", WingetScope = "user" }, services.Elevation);
                        row.StateText = path == DeElevatedLauncher.Path.Failed ? Loc.Instance.Format("Result_Error", "winget") : Loc.Instance["Apps_StartedForUser"];
                    }
                    else
                    {
                        row.StateText = Winget.IsSuccess(code) ? Loc.Instance["Apps_Installed"] : Loc.Instance.Format("Apps_Failed", $"0x{code:X8}");
                    }
                }))
                row.StateText = "";
        }
        finally
        {
            row.IsInstalling = false;
        }
    }

    [RelayCommand]
    private void OpenDriverPage(string? url)
    {
        if (url is not null) Owner.OpenLink(url);
    }
}
