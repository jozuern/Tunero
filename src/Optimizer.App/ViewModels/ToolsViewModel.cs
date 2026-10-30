using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core;
using Optimizer.Core.Apps;
using Optimizer.Core.Backup;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;

namespace Optimizer.App.ViewModels;

public sealed partial class FeatureRow : SwitchRow
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Title;

    private readonly AppServices _services;
    private readonly ChangeRunner _runner;

    public FeatureRow(FeatureEntry entry, bool enabled, string lang, AppServices services, ChangeRunner runner) : base(enabled)
    {
        _services = services;
        _runner = runner;
        Entry = entry;
        Title = entry.Label(lang);
        Text = entry.Text(lang);
        Note = entry.Recommend == "off" && enabled ? Loc.Instance["Feat_RecommendOff"] : entry.Hypervisor ? Loc.Instance["Feat_Hypervisor"] : null;
    }

    public FeatureEntry Entry { get; }
    public string Title { get; }
    public string Text { get; }
    public string? Note { get; }

    protected override Task<bool> ToggleAsync(bool on) => Switching.SetAsync(_services, _runner,
        enabled => Entry.Capability ? OptionalCapabilityAction.Tweak(Entry, enabled) : OptionalFeatureAction.Tweak(Entry, enabled, _services.Store.Exists), on);
}

public sealed record StepRow(string Text, bool Ok, string? Detail)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Text;
}

public sealed partial class ToolsViewModel(IPageHost owner, AppServices services, ChangeRunner runner, IDialogs dialogs) : PageViewModel(owner)
{
    public ObservableCollection<FeatureRow> Features { get; } = [];
    public ObservableCollection<StepRow> RepairSteps { get; } = [];

    [ObservableProperty] private string? _featuresNote;

    public ObservableCollection<PriorityRow> PriorityRules { get; } = [];
    [ObservableProperty] private IReadOnlyList<FilterOption> _priorityChoices = BuildPriorityChoices();

    private static IReadOnlyList<FilterOption> BuildPriorityChoices() =>
    [
        new(nameof(CpuPriority.AboveNormal), Loc.Instance["Prio_AboveNormal"]),
        new(nameof(CpuPriority.High), Loc.Instance["Prio_High"]),
        new(nameof(CpuPriority.BelowNormal), Loc.Instance["Prio_BelowNormal"]),
        new(nameof(CpuPriority.Low), Loc.Instance["Prio_Low"]),
    ];

    /// <summary>After a language switch the page loads again: lists with texts are built again in the new language.</summary>
    private void RebuildTexts()
    {
        var key = NewPriority?.Key;
        PriorityChoices = BuildPriorityChoices();
        NewPriority = PriorityChoices.FirstOrDefault(o => o.Key == key) ?? PriorityChoices[0];
        QuickFixRows = QuickFixes.All.Select(f => new QuickFixRow(f)).ToList();
    }

    [ObservableProperty] private string _newPriorityExe = "";

    /// <summary>From Live readings: the rule form with this program filled in (nothing is applied).</summary>
    public void PrefillPriorityRule(string exe) => NewPriorityExe = exe;
    [ObservableProperty] private FilterOption? _newPriority;
    [ObservableProperty] private bool _newPriorityLowIo;
    [ObservableProperty] private bool _newPriorityLowMemory;
    [ObservableProperty] private string? _priorityNote;

    /// <summary>Reads the rules in the background: Image File Execution Options has a key per program.</summary>
    private async Task LoadPriorityRulesAsync()
    {
        NewPriority ??= PriorityChoices[0];
        var rules = await Task.Run(() => ProgramPriority.Read(services.Context.Registry));
        PriorityRules.Clear();
        foreach (var r in rules)
            PriorityRules.Add(new PriorityRow(r, r.Cpu is { } c ? PriorityChoices.First(o => o.Key == c.ToString()).Text
                    : r.OtherCpu is { } o ? Loc.Instance.Format("Prio_Other", o) : "",
                string.Join(", ", new[] { r.LowIo ? Loc.Instance["Prio_LowIo"] : null, r.LowMemory ? Loc.Instance["Prio_LowMemory"] : null }.OfType<string>()) is { Length: > 0 } low ? low : null));
    }

    [RelayCommand]
    private void BrowsePriorityExe()
    {
        // Only the file name goes into the rule (Windows matches by name); the file itself is neither read nor started.
        if (dialogs.PickOpenFile(Loc.Instance["Prio_ProgramFiles"], ".exe") is { } path) NewPriorityExe = System.IO.Path.GetFileName(path);
    }

    /// <summary>Adds a start priority rule through the engine (confirmation, backup, Changes page).</summary>
    [RelayCommand]
    private async Task AddPriorityRuleAsync()
    {
        var exe = NewPriorityExe.Trim();
        if (!ProgramPriority.IsValidExe(exe))
        {
            PriorityNote = Loc.Instance["Prio_InvalidName"];
            return;
        }
        var cpu = Enum.Parse<CpuPriority>((NewPriority ?? PriorityChoices[0]).Key);
        PriorityNote = null;
        if (await runner.ApplyAsync(ProgramPriority.Tweak(exe, cpu, NewPriorityLowIo, NewPriorityLowMemory))) NewPriorityExe = "";
        await LoadPriorityRulesAsync();
    }

    /// <summary>Undoes a rule this app set; a rule from elsewhere is removed with its own backup.</summary>
    [RelayCommand]
    private async Task RemovePriorityRuleAsync(PriorityRow? row)
    {
        if (row is null) return;
        var ours = ProgramPriority.Tweak(row.Rule.Exe, row.Rule.Cpu ?? CpuPriority.AboveNormal, row.Rule.LowIo, row.Rule.LowMemory);
        if (services.Store.Exists(ours.Id)) await runner.UndoAsync(ours);
        else await runner.ApplyAsync(ProgramPriority.RemoveTweak(row.Rule.Exe));
        await LoadPriorityRulesAsync();
    }

    /// <summary>Repairs that run documented Windows commands (restart a service or device, renew network state, rebuild a cache).</summary>
    [ObservableProperty] private IReadOnlyList<QuickFixRow> _quickFixRows = QuickFixes.All.Select(f => new QuickFixRow(f)).ToList();

    // The page shows the quick fixes in three groups: network, restart a part of Windows, repair Windows.
    private static readonly string[] NetworkFixes = ["Dns", "Winsock", "IpRenew", "TcpIpReset"];
    private static readonly string[] RestartFixes = ["AudioRestart", "BluetoothRestart", "SearchRestart", "GraphicsRestart"];

    public IEnumerable<QuickFixRow> QuickNetworkRows => QuickFixRows.Where(r => NetworkFixes.Contains(r.Fix.Id));
    public IEnumerable<QuickFixRow> QuickRestartRows => QuickFixRows.Where(r => RestartFixes.Contains(r.Fix.Id));
    public IEnumerable<QuickFixRow> QuickRepairRows => QuickFixRows.Where(r => !NetworkFixes.Contains(r.Fix.Id) && !RestartFixes.Contains(r.Fix.Id));

    partial void OnQuickFixRowsChanged(IReadOnlyList<QuickFixRow> value)
    {
        OnPropertyChanged(nameof(QuickNetworkRows));
        OnPropertyChanged(nameof(QuickRestartRows));
        OnPropertyChanged(nameof(QuickRepairRows));
    }

    protected override async Task LoadAsync()
    {
        RebuildTexts();
        await LoadPriorityRulesAsync();
        _featureStates = await Task.Run(() => OptionalFeatureAction.ReadAll(services.Context.Processes)
            .Concat(OptionalCapabilityAction.ReadAll(services.Context.Processes)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        ShowFeatures(_featureStates);
    }

    private Dictionary<string, string>? _featureStates;

    // A language switch builds the rows again without DISM (two lists that take seconds); the priority rules are a quick read.
    protected override async Task RelabelAsync()
    {
        if (_featureStates is null)
        {
            await LoadAsync();
            return;
        }
        RebuildTexts();
        ShowFeatures(_featureStates);
        await LoadPriorityRulesAsync();
    }

    private void ShowFeatures(Dictionary<string, string> states)
    {
        var lang = Lang;
        Features.Clear();
        foreach (var f in CatalogData.Current.Features.Features)
            if (states.TryGetValue(f.Name, out var s)) Features.Add(new FeatureRow(f, s == "Enabled", lang, services, runner));
        // DISM needs administrator rights to list features.
        FeaturesNote = Features.Count > 0 ? null : services.Elevation.IsElevated ? Loc.Instance["Feat_None"] : Loc.Instance["Feat_NeedsAdmin"];
    }

    [RelayCommand]
    private async Task RepairUpdateAsync()
    {
        if (!dialogs.Ask(Loc.Instance["Repair_Title"], Loc.Instance["Repair_Text"], Loc.Instance["Repair_Run"])) return;
        RepairSteps.Clear();
        var lang = Lang;
        var progress = new Progress<RepairStep>(s => RepairSteps.Add(new StepRow(Labels.Current.Get(lang, s.Key), s.Ok, s.Detail)));
        await RunWorkAsync("windows update repair", async () =>
        {
            var steps = await Task.Run(() => UpdateRepair.Run(services.Context.Processes, services.Context.Services, progress));
            var ok = steps.All(s => s.Ok);
            Owner.ShowResult(ok ? Loc.Instance["Repair_Done"] : Loc.Instance["Repair_Partial"], ok ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning);
        });
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!dialogs.Ask(Loc.Instance["Quick_ExplorerTitle"], Loc.Instance["Quick_ExplorerText"], Loc.Instance["Quick_Explorer"])) return;
        await RunWorkAsync("restart explorer", () => RestartExplorerCoreAsync(null, Loc.Instance["Quick_ExplorerRestarted"]));
    }

    /// <summary>
    /// Rebuilds the icon cache the way Microsoft's troubleshooting article does: Explorer ends, the signed-in user's
    /// IconCache.db is deleted, Explorer starts again and builds a new one.
    /// </summary>
    [RelayCommand]
    private async Task ResetIconCacheAsync()
    {
        if (services.ProfilePath is not { Length: > 0 } profile)
        {
            Owner.ShowResult(Loc.Instance["Quick_IconCacheNoProfile"], Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }
        if (!dialogs.Ask(Loc.Instance["Quick_IconCache"], Loc.Instance["Quick_IconCacheConfirm"], Loc.Instance["Quick_IconCacheAction"])) return;
        var cache = Path.Combine(profile, "AppData", "Local", "IconCache.db");
        await RunWorkAsync("reset icon cache", () => RestartExplorerCoreAsync(() =>
        {
            // The user's own folder: deleted only through a handle that resolves to this very file (no link swapped in).
            if (File.Exists(cache)) SafeDelete.DeleteFile(cache);
        }, Loc.Instance["Quick_IconCacheDone"]));
    }

    /// <param name="whileStopped">Runs after Explorer ended and before it starts again; an exception is shown and Explorer still starts.</param>
    private async Task RestartExplorerCoreAsync(Action? whileStopped, string done)
    {
        // Only Explorer of this session: the elevated taskkill would otherwise end it for every signed-in user, and it is
        // started again only for this one.
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        await Task.Run(() => services.Context.Processes.Run("taskkill.exe", $"/f /im explorer.exe /fi \"SESSION eq {session}\""));
        await Task.Delay(1000);
        string? failure = null;
        if (whileStopped is not null)
        {
            try
            {
                await Task.Run(whileStopped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex.Message;
            }
        }
        // Explorer must run as the signed-in user, never elevated.
        var path = DeElevatedLauncher.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), null, services.Elevation);
        if (path == DeElevatedLauncher.Path.Failed) Owner.ShowResult(Loc.Instance["Quick_ExplorerManual"], Wpf.Ui.Controls.InfoBarSeverity.Warning);
        else if (failure is not null) Owner.ShowResult(Loc.Instance.Format("Result_Error", failure), Wpf.Ui.Controls.InfoBarSeverity.Error);
        else Owner.ShowResult(done, Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task RunQuickFixAsync(QuickFixRow row)
    {
        var fix = row.Fix;
        if (fix.Confirm && !dialogs.Ask(row.Title, Loc.Instance[$"Quick_{fix.Id}Confirm"], row.ActionText)) return;
        row.Output = null;
        await RunWorkAsync($"quick fix {fix.Id}", async () =>
        {
            var (ok, output) = await Task.Run(() => QuickFixes.Run(services.Context.Processes, fix));
            ShowQuickResult(ok, Loc.Instance[$"Quick_{fix.Id}Done"], output);
            // A report worth reading (chkdsk, reagentc) stays in the card that ran it; the result bar closes by itself.
            if (ok && fix.ShowOutput) row.Output = string.Join("\n", output.Trim().Split('\n').Select(l => l.TrimEnd()).TakeLast(8));
        }, slow: fix.TakesMinutes);
    }

    /// <summary>
    /// The outcome of a quick fix goes to the result bar at the bottom of the window: the cards sit high up on a long
    /// page, so a line below the last card would be out of view.
    /// </summary>
    private void ShowQuickResult(bool ok, string done, string output) =>
        Owner.ShowResult(ok ? done : Loc.Instance.Format("Result_Error", output.Trim()),
            ok ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Error);
}

/// <summary>A start priority rule found in Image File Execution Options.</summary>
public sealed record PriorityRow(PriorityRule Rule, string CpuText, string? IoText)
{
    public string Exe => Rule.Exe;
    public string Details => IoText is null ? CpuText : CpuText.Length == 0 ? IoText : $"{CpuText}, {IoText}";

    /// <summary>What a screen reader says for the row's Remove button.</summary>
    public string RemoveLabel => Loc.Instance.Format("Prio_RemoveName", Exe);
}

/// <summary>A quick fix card; title and hint come from the string resources in the current language.</summary>
public sealed partial class QuickFixRow(QuickFix fix) : ObservableObject
{
    public QuickFix Fix { get; } = fix;
    public string Title => Loc.Instance[$"Quick_{Fix.Id}"];
    public string Hint => Loc.Instance[$"Quick_{Fix.Id}Hint"];

    /// <summary>The button names what it does ("Renew", "Restart") instead of a generic "Run".</summary>
    public string ActionText => Loc.Instance[$"Quick_{Fix.Id}Action"];

    public bool NeedsRestart => Fix.NeedsRestart;

    /// <summary>Can take several minutes (chkdsk /scan): the card shows the clock line.</summary>
    public bool TakesMinutes => Fix.TakesMinutes;

    /// <summary>The last lines of the tool's report (chkdsk, reagentc), shown in the card that ran it.</summary>
    [ObservableProperty] private string? _output;

    // What screen readers announce for this item in a list.
    public override string ToString() => Title;
}
