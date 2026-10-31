using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Optimizer.App.Services;
using Optimizer.Core;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

public sealed record StartOption(ServiceStart Start, string Text)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Text;
}

public sealed partial class ServiceRowVm : ObservableObject
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => DisplayName;

    private readonly ServicesViewModel _page;
    private bool _suppress;

    public ServiceRowVm(ServiceRow row, string lang, ServicesViewModel page)
    {
        _page = page;
        Row = row;
        Note = WithoutName(row.Note?.Text(lang), row.DisplayName);
        // A description that only repeats the name ("ChatGPT") adds nothing under it.
        Description = string.Equals(row.Description?.Trim(), row.DisplayName.Trim(), StringComparison.CurrentCultureIgnoreCase) ? null : row.Description;
        StateText = Loc.Instance[row.Running ? "Svc_Running" : "Svc_Stopped"];
        Publisher = row.Signature?.Publisher ?? "";
        MetaText = string.Join(", ", new[] { row.Name, StateText, Publisher }.Where(x => x.Length > 0));
        CanSetManual = row.Edit == ServiceEdit.ManualOnly && row.Start != ServiceStart.Manual;
        // Every row shows its start type in the same combo box, so the column reads the same all the way down: all four
        // start types, the current one and Manual for a Windows service the app explains, or only the current one
        // (disabled, with a lock) when there is nothing to choose. Choosing goes through the same confirmation.
        Options = row.Edit == ServiceEdit.Full
            ? [Opt(ServiceStart.Automatic, lang), Opt(ServiceStart.AutomaticDelayed, lang), Opt(ServiceStart.Manual, lang), Opt(ServiceStart.Disabled, lang)]
            : CanSetManual ? [Opt(ServiceStart.Manual, lang)] : [];
        LockTip = row.Edit == ServiceEdit.ReadOnly ? Loc.Instance["Svc_LockTipReadOnly"] : Loc.Instance["Svc_LockTipManual"];
        if (row.Start is { } current && Options.All(o => o.Start != current)) Options = [Opt(current, lang), .. Options];
        _selected = Options.FirstOrDefault(o => o.Start == row.Start);
        EditText = row.Edit switch
        {
            ServiceEdit.ReadOnly => Loc.Instance["Svc_ReadOnly"],
            // Only when there is something to choose: a service already on Manual has no change to warn about.
            ServiceEdit.ManualOnly => row.Note?.Mode == "warn" && CanSetManual ? Loc.Instance["Svc_Warn"] : null,
            _ => null,
        };
    }

    private static StartOption Opt(ServiceStart s, string lang) => new(s, Labels.Current.Get(lang, $"serviceStart.{s}"));

    /// <summary>
    /// The catalog's note without the service's name in front: the notes start with the English or German name
    /// ("Bluetooth-Unterstützungsdienst: für Bluetooth-Controller"), which the row's title already shows when Windows has
    /// the app's language. The names are compared without case, spaces and hyphens, since the catalog and Windows do not
    /// always write them alike ("Xbox Live-Authentifizierungs-Manager", "Xbox Live Authentifizierungs-Manager"). The rest
    /// starts with a capital letter, as the note's own sentences do.
    /// </summary>
    internal static string? WithoutName(string? note, string displayName)
    {
        var name = Plain(displayName);
        if (note is null || name.Length == 0) return note;
        for (var colon = note.IndexOf(':'); colon > 0; colon = note.IndexOf(':', colon + 1))
        {
            if (Plain(note[..colon]) != name) continue;
            var rest = note[(colon + 1)..].TrimStart();
            return rest.Length == 0 ? note : char.ToUpper(rest[0], System.Globalization.CultureInfo.CurrentCulture) + rest[1..];
        }
        return note;

        static string Plain(string s) =>
            new(s.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '‐' && c != '‑').Select(char.ToLowerInvariant).ToArray());
    }

    public ServiceRow Row { get; }
    public string Name => Row.Name;
    public string DisplayName => Row.DisplayName;
    public string? Description { get; }
    public string? Note { get; }
    public string StateText { get; }
    public string Publisher { get; }
    public string MetaText { get; }
    public string? EditText { get; }
    public IReadOnlyList<StartOption> Options { get; }
    public bool CanEdit => Row.Edit == ServiceEdit.Full;

    /// <summary>A Windows service the app explains: the only change offered is Manual.</summary>
    public bool CanSetManual { get; }

    /// <summary>Nothing to choose: the start type in a disabled combo box with a lock, and why in its tooltip.</summary>
    public bool IsLocked => !CanEdit && !CanSetManual;

    public bool CanChoose => !IsLocked;

    public string LockTip { get; }

    /// <summary>The lock's reason as the tooltip of a locked row; rows with a choice have none.</summary>
    public string? LockTipShown => IsLocked ? LockTip : null;

    /// <summary>"Start type of Print Spooler": what a screen reader says for the combo box.</summary>
    public string StartTypeLabel => Loc.Instance.Format("Svc_StartTypeName", DisplayName);

    [ObservableProperty] private StartOption? _selected;

    partial void OnSelectedChanged(StartOption? oldValue, StartOption? newValue)
    {
        if (_suppress || newValue is null || newValue.Start == Row.Start) return;
        ChangeAsync(oldValue, newValue).Forget("service start type");
    }

    private async Task ChangeAsync(StartOption? oldValue, StartOption newValue)
    {
        // Not inside the combo box's selection change: the confirmation dialog opens from here, and setting the selection
        // back after a cancel would otherwise happen while the combo box is still changing it.
        await Task.Yield();
        var written = false;
        try
        {
            written = await _page.ChangeStartAsync(this, newValue.Start);
        }
        finally
        {
            // Cancelled or failed (also by an exception): show the real start type again, not one that was never written.
            if (!written)
            {
                _suppress = true;
                Selected = oldValue;
                _suppress = false;
            }
        }
    }
}

public sealed partial class ServicesViewModel(IPageHost owner, AppServices services, ChangeRunner runner) : PageViewModel(owner)
{
    protected override IReadOnlyList<Page> ShownOn => [Page.Services];

    private List<ServiceRowVm> _all = [];

    public ObservableCollection<ServiceRowVm> Rows { get; } = [];
    public ObservableCollection<StartupRow> Tasks { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showWindowsServices;
    [ObservableProperty] private bool _showMicrosoftTasks;

    /// <summary>Scheduled tasks tab: words in the task's path or command.</summary>
    [ObservableProperty] private string _taskSearch = "";

    /// <summary>The services or tasks were read but the search and filters leave none: the page says so.</summary>
    public bool RowsNoMatch => _all.Count > 0 && Rows.Count == 0;

    public bool TasksNoMatch => _taskList.Count > 0 && Tasks.Count == 0;

    partial void OnTaskSearchChanged(string value) => FillTasks();

    partial void OnSearchChanged(string value) => Filter();
    partial void OnShowWindowsServicesChanged(bool value) => Filter();
    // The task list is kept: switching the filter shows or hides rows without reading every service and task again.
    partial void OnShowMicrosoftTasksChanged(bool value) => FillTasks();

    private IReadOnlyList<ScheduledTaskInfo> _taskList = [];

    private IReadOnlyList<ServiceRow>? _serviceRows;

    protected override async Task LoadAsync()
    {
        _serviceRows = await Task.Run(() =>
        {
            var list = ServiceManager.List(services.Context.Services, CatalogData.Current.Services);
            // Signature decides "Microsoft" for services the catalog does not explain.
            return list.AsParallel().WithDegreeOfParallelism(4).Select(r => r.Note is null ? r with { Signature = SignatureVerifier.Verify(r.File) } : r).ToList();
        });
        ShowServices(_serviceRows);

        _taskList = await Task.Run(() => services.Context.Tasks.List());
        _taskChecks = null;
        services.Search.UpdateRuntime(Optimizer.Core.Search.SearchKind.ScheduledTask,
            _taskList.Select(t => ("Services", t.Path, t.Path.TrimStart('\\'), t.Command ?? "")));
        FillTasks();
        _taskChecks = await Task.Run(() => CheckTasks(_taskList));
        FillTasks();
    }

    /// <summary>What a task runs and who signed it, read once per load in the background (the verifier caches the files).</summary>
    private sealed record TaskCheck(string? Image, SignatureInfo Signature, bool RunsScriptHost);

    /// <summary>
    /// Until the signatures are read, Windows' own tasks are told by their folder (\Microsoft\); after that by the rule
    /// of the Startup page (<see cref="StartupRow.IsPlainMicrosoft"/>), so both pages hide the same tasks: a Windows task
    /// in the root folder too, and not a task another program put below \Microsoft\.
    /// </summary>
    private IReadOnlyDictionary<string, TaskCheck>? _taskChecks;

    private Dictionary<string, TaskCheck> CheckTasks(IReadOnlyList<ScheduledTaskInfo> tasks)
    {
        var scanner = new StartupScanner(services.Context.Registry, services.Context.Tasks, services.ProfilePath);
        return tasks.AsParallel().WithDegreeOfParallelism(4).Select(t =>
            {
                var image = scanner.TaskImage(t);
                var command = StartupScanner.TaskCommand(t.Command, t.Arguments);
                return (t.Path, Check: new TaskCheck(image, SignatureVerifier.Verify(image), CommandLine.RunsUnverifiedScript(command, PathExpander.Machine.Expand)));
            })
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Check, StringComparer.OrdinalIgnoreCase);
    }

    // A language switch builds the rows from the services and tasks already read.
    protected override Task RelabelAsync()
    {
        if (_serviceRows is null) return LoadAsync();
        ShowServices(_serviceRows);
        FillTasks();
        return Task.CompletedTask;
    }

    private void ShowServices(IReadOnlyList<ServiceRow> rows)
    {
        var lang = Lang;
        _all = rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(r => new ServiceRowVm(r, lang, this)).ToList();
        services.Search.UpdateRuntime(Optimizer.Core.Search.SearchKind.Service, _all.Select(r => ("Services", r.Name, r.DisplayName, r.Description ?? r.Name)));
        Filter();
    }

    /// <summary>A task switched on this page: the list the rows are built from follows, so a search or filter shows the new state.</summary>
    private void TaskSwitched(string path, bool enabled) =>
        _taskList = _taskList.Select(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) ? t with { Enabled = enabled } : t).ToList();

    private void FillTasks()
    {
        var lang = Lang;
        var showMs = ShowMicrosoftTasks;
        var q = TaskSearch.Trim();
        // A task's program as the signed-in user's sign-in would start it (as the Startup page reads it).
        var userPaths = services.UserPaths;
        var checks = _taskChecks;
        Tasks.Clear();
        foreach (var t in _taskList.Where(t => q.Length == 0 || t.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Command?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            var check = checks?.GetValueOrDefault(t.Path);
            if (!showMs && check is null && t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            // Full command with arguments: rundll32 resolves to its DLL, and script hosts are judged by what they run.
            var command = StartupScanner.TaskCommand(t.Command, t.Arguments);
            var entry = new StartupEntry(StartupKind.LogonTask, t.Path.TrimStart('\\'), command, check is null ? CommandLine.ImagePath(command, userPaths.Expand) : check.Image,
                t.AtLogon ? Loc.Instance["Task_AtLogon"] : t.AtBoot ? Loc.Instance["Task_AtBoot"] : Loc.Instance["Task_Other"], Hive.Machine, t.Enabled, $"task:{t.Path}")
            {
                Target = t.Path,
            };
            var path = t.Path;
            var row = new StartupRow(entry, lang, services, runner, on => TaskSwitched(path, on));
            if (check is not null)
            {
                row.SetSignature(check.Signature, check.RunsScriptHost, lang);
                if (!showMs && row.IsPlainMicrosoft) continue;
            }
            Tasks.Add(row);
        }
        OnPropertyChanged(nameof(TasksNoMatch));
    }

    private void Filter()
    {
        var q = Search.Trim();
        Rows.Clear();
        foreach (var r in _all.Where(r => (ShowWindowsServices || r.Row.Edit != ServiceEdit.ReadOnly)
                                          && (q.Length == 0 || r.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))))
            Rows.Add(r);
        OnPropertyChanged(nameof(RowsNoMatch));
    }

    public async Task<bool> ChangeStartAsync(ServiceRowVm row, ServiceStart start)
    {
        // Back to the original start type of a change this app made: undo that change instead.
        var id = ServiceManager.ChangeId(ServiceManager.StartTypeOwner(row.Name));
        TweakBackup? previousBackup;
        try
        {
            previousBackup = services.Store.Get(id);
        }
        catch (BackupUnreadableException ex)
        {
            // Locked by another process right now: deciding without it could record a changed value as the original.
            Owner.ShowResult(Loc.Instance.Format("Result_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
            return false;
        }
        if (previousBackup is { } previous && previous.Entries.FirstOrDefault()?.Original.Data == start.ToString()
            && Owner.ResolveTweak(id) is { } t)
            return await runner.UndoAsync(t) && await ReloadThenTrue();
        if (ServiceManager.Change(row.Row, start) is not { } tweak) return false;
        var written = await runner.ApplyAsync(tweak);
        if (written) await ReloadAsync();
        return written;
    }

    private async Task<bool> ReloadThenTrue()
    {
        await ReloadAsync();
        return true;
    }
}
