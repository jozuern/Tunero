using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IPageHost
{
    private readonly CatalogData _catalog = CatalogData.Current;
    private readonly AppSettings _settings;
    private readonly AppServices _services;
    private readonly IDialogs _dialogs;
    /// <summary>The scan and plan state: the last scan's results, the profile in use and the plan (<see cref="ScanState"/>).</summary>
    private readonly ScanState _state;

    private readonly SetupDecision _setup;

    /// <param name="setupChecks">What the first start's setup decides by; null: this PC's (<see cref="SetupChecks.ForThisPc"/>).</param>
    /// <param name="updateSteps">The self-update's download, swap and restart; null: the real ones (<see cref="UpdateSteps"/>).</param>
    public MainViewModel(AppSettings settings, AppServices services, IDialogs dialogs, SetupChecks? setupChecks = null, UpdateSteps? updateSteps = null)
    {
        _settings = settings;
        _services = services;
        _dialogs = dialogs;
        _state = new ScanState(_catalog, settings, settings.Save, new AppScanSource(services, _catalog), () => ChangeGate.Instance.Busy)
        {
            AfterScan = RefreshChangesStateAsync,
        };
        _state.Updated += OnScanUpdated;
        _state.Probed += probe => ScanStatus = Loc.Instance.Format("Scan_Probe", probe);
        _expertMode = settings.ExpertMode;
        _checkForUpdates = settings.CheckForUpdates;
        Runner = new ChangeRunner(services, dialogs, () => _state.Facts, () => ExpertMode) { ScanInProgress = () => ScanInProgress };
        Install = InstallFacts.ForThisProcess(services);
        Updates = new UpdateService(Install, dialogs, this, steps: updateSteps);
        _setup = new SetupDecision(Install, settings, setupChecks ?? SetupChecks.ForThisPc(MachineRegistry));
        Updates.StateChanged += (_, _) => OnUpdateStateChanged();
        Uninstalls = new UninstallService(Install, dialogs, this, Updates, () => services.Store.All().Count,
            note => Runner.UndoAllAsync(confirm: false, incompleteNote: note), () => StartMenuFolder, DataPaths.Root,
            installed: InstalledCopyOf(Install.ExePath));
        Runner.Status += (_, result) => ShowResult(result.Text, result.Severity);
        Runner.Changed += async (_, t) => await OnChangedAsync(t);
        Runner.Changed += (_, _) => RefreshReapply();
        ChangeGate.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChangeGate.CanChange)) return;
            OnPropertyChanged(nameof(CanChange));
            foreach (var command in new CommunityToolkit.Mvvm.Input.IRelayCommand[]
                     {
                         RunActionCommand, ApplyRecommendedCommand, ApplyTweakGroupCommand, TurnOnPickedCommand, UndoPickedCommand, UndoAllCommand, UndoRecordCommand, ReapplyRecordCommand,
                         ReapplyDriftCommand, EnableRestorePointsCommand, ScanCommand, UpdateNowCommand, ImportChangesCommand, CopyToNewAccountsCommand, UninstallCommand,
                     })
                command.NotifyCanExecuteChanged();
        };

        Network = new NetworkViewModel(this, services);
        Debloat = new DebloatViewModel(this, services, dialogs);
        Cleanup = new CleanupViewModel(this, services, dialogs);
        Storage = new StorageViewModel(this, services, dialogs);
        Startup = new StartupViewModel(this, services, Runner);
        ServicesPage = new ServicesViewModel(this, services, Runner);
        Apps = new AppsViewModel(this, services, dialogs);
        Tools = new ToolsViewModel(this, services, Runner, dialogs);
        InstallMedia = new InstallMediaViewModel(this, dialogs, InstallMediaSystem.CatalogSource(services), InstallMediaSystem.BuildAsync,
            InstallMediaSystem.ForThisPc(services, () => _state.Usage.Id, () => _settings.Theme, path => ShowInExplorerCommand.Execute(path), Install.IsPublishedExe),
            ct => InstallMediaSystem.CleanLeftoversAsync(ct));
        Health = new HealthViewModel(this, dialogs, exe =>
        {
            Tools.PrefillPriorityRule(exe);
            ToolsTab = 2;
            CurrentPage = Page.Tools;
        });
        _virusTotalConfigured = !string.IsNullOrEmpty(settings.VirusTotalKey);
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            Rebuild();
            Network.Rebuild();
            OnPropertyChanged(nameof(ReapplyHint));
            OnPropertyChanged(nameof(StartMenuHint));
            foreach (var page in new PageViewModel[] { Debloat, Cleanup, Storage, Startup, ServicesPage, Apps, Tools, InstallMedia, Health }) page.OnLanguageChanged();
        };
    }

    /// <summary>Every page that loads its own data.</summary>
    public IReadOnlyList<PageViewModel> Pages => [Network, Debloat, Cleanup, Storage, Startup, ServicesPage, Apps, Tools, InstallMedia, Health];

    /// <summary>
    /// After the first scan, the pages that read slow data of their own (startup entries, services, programs and
    /// drivers, AppX packages, folder sizes) load it in the background, one after another and only while the window is
    /// idle, so they open with their rows instead of a loading state. Everything here only reads; a page the user opens
    /// meanwhile starts its own load at once. Tools is not among them: its two DISM lists take about 12 seconds of
    /// processor time when elevated, too much to spend on every start for a page that is opened rarely. Storage is not
    /// among them either: it reads nothing but the drive list until Analyze is pressed.
    /// </summary>
    public async Task PreloadPagesAsync()
    {
        foreach (var page in new PageViewModel[] { Startup, ServicesPage, Apps, Debloat, Cleanup })
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await page.EnsureLoadedAsync();
        }
    }

    public AppServices Services => _services;
    public ChangeRunner Runner { get; }
    public NetworkViewModel Network { get; }
    public DebloatViewModel Debloat { get; }
    public CleanupViewModel Cleanup { get; }
    public StorageViewModel Storage { get; }
    public StartupViewModel Startup { get; }
    public ServicesViewModel ServicesPage { get; }
    public AppsViewModel Apps { get; }
    public ToolsViewModel Tools { get; }
    public InstallMediaViewModel InstallMedia { get; }
    public HealthViewModel Health { get; }

    [ObservableProperty] private Page _currentPage = Page.Overview;
    [ObservableProperty] private bool _isScanning;

    /// <summary>The last scan stopped with an error: the verdict says so instead of "the scan is reading this PC".</summary>
    [ObservableProperty] private bool _scanFailed;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private HardwareProfile? _profile;
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private string _headline = "";

    /// <summary>The Overview's verdict: one sentence on what holds this PC back (the problems listed right under it).</summary>
    [ObservableProperty] private string _verdict = "";

    private int _findingProblems;

    /// <summary>
    /// The verdict: the problems listed under it, or without any, whether recommended changes can still help. The score
    /// counts only problems, so "100 of 100" and two recommended changes are both true; a sentence that said only "nothing
    /// holds this PC back" next to the recommendations read as a contradiction. A setting that works against the profile
    /// (listed on the same page with a warning) comes first: it costs what the profile is for.
    /// </summary>
    private void UpdateVerdict() => Verdict = _findingProblems switch
    {
        0 when AgainstProfile.Count == 1 => Loc.Instance.Format("Dash_VerdictNoneOneAgainst", ProfileName(_state.Usage)),
        0 when AgainstProfile.Count > 1 => Loc.Instance.Format("Dash_VerdictNoneAgainst", ProfileName(_state.Usage), AgainstProfile.Count),
        0 when _state.Plan.Items.Count == 1 => Loc.Instance["Dash_VerdictNoneOneChange"],
        0 when _state.Plan.Items.Count > 1 => Loc.Instance.Format("Dash_VerdictNoneChanges", _state.Plan.Items.Count),
        0 => Loc.Instance["Dash_VerdictNone"],
        1 => Loc.Instance["Dash_VerdictOne"],
        _ => Loc.Instance.Format("Dash_VerdictMany", _findingProblems),
    };

    /// <summary>The readiness score and the passed checks, one quiet line under the verdict.</summary>
    [ObservableProperty] private string _readinessLine = "";

    /// <summary>"To change by hand: 1": the way to Manual checks when the BIOS or the hardware has something to change.</summary>
    [ObservableProperty] private string? _advisorLinkText;
    [ObservableProperty] private bool _showPassed;
    [ObservableProperty] private InspectorItem? _selectedItem;
    [ObservableProperty] private FindingItemViewModel? _gameAccess;
    [ObservableProperty] private bool _onlyRecommended;
    /// <summary>Tweaks page: words in the title or summary (or the tweak id). Not saved.</summary>
    [ObservableProperty] private string _tweakSearch = "";
    [ObservableProperty] private bool _expertMode;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _restorePointText = "";
    [ObservableProperty] private bool _canEnableRestorePoints;

    /// <summary>The safety net card's icon follows the state: a check only when System Protection is known to be on.</summary>
    [ObservableProperty] private SymbolRegular _restorePointIcon = SymbolRegular.Shield24;
    [ObservableProperty] private string _tweaksSummary = "";
    [ObservableProperty] private string? _resultText;

    /// <summary>The result is about a change: the bar offers the way to the Changes page.</summary>
    [ObservableProperty] private bool _resultShowsChanges;

    /// <summary>The result says a new version is out: the bar offers the update (<see cref="ResultUpdateText"/>).</summary>
    [ObservableProperty] private bool _resultShowsUpdate;
    [ObservableProperty] private string? _resultUpdateText;

    /// <summary>Counts in the navigation (null = no badge).</summary>
    [ObservableProperty] private string? _overviewBadge;
    [ObservableProperty] private string? _advisorBadge;

    /// <summary>A new version is available: Settings, where the update is, carries a badge on every page.</summary>
    [ObservableProperty] private string? _settingsBadge;

    [RelayCommand]
    private void OpenChanges()
    {
        ResultOpen = false;
        CurrentPage = Page.Changes;
    }
    [ObservableProperty] private bool _resultOpen;
    [ObservableProperty] private string _recommendedButtonText = "";
    [ObservableProperty] private bool _hasRecommendations;
    [ObservableProperty] private string _recommendationsEmptyText = "";
    [ObservableProperty] private bool _virusTotalConfigured;
    [ObservableProperty] private ProfileOption? _selectedProfile;
    [ObservableProperty] private bool _onlyProfile = true;
    [ObservableProperty] private string _profileDescription = "";
    [ObservableProperty] private string? _suggestedProfileText;
    [ObservableProperty] private string _scoreHint = "";
    [ObservableProperty] private string? _hiddenFindingsText;
    /// <summary>BIOS and hardware page: how many passed checks "Show passed checks" would add (null when shown or none).</summary>
    [ObservableProperty] private string? _advisorPassedText;
    [ObservableProperty] private string _recommendationsIntro = "";
    [ObservableProperty] private bool _hasAgainst;
    [ObservableProperty] private string _againstTitle = "";

    public ObservableCollection<ProfileOption> ProfileOptions { get; } = [];
    public ObservableCollection<AgainstLine> AgainstProfile { get; } = [];

    /// <summary>Developer switch --profile: use this profile for the session without saving it.</summary>
    public string? ProfileOverride
    {
        get => _state.ProfileOverride;
        set => _state.ProfileOverride = value;
    }

    public ObservableCollection<FindingItemViewModel> Findings { get; } = [];
    public ObservableCollection<FindingItemViewModel> AdvisorItems { get; } = [];
    /// <summary>Every tweak that passes the filters, also those in closed groups (selection, details pane).</summary>
    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];

    /// <summary>What the Tweaks page lists: each group's heading, followed by its tweaks when the group is open.</summary>
    public ObservableCollection<object> TweakRows { get; } = [];
    public ObservableCollection<ChangeRecordItem> ChangeRecords { get; } = [];
    public ObservableCollection<LogLine> ChangeLog { get; } = [];
    public ObservableCollection<Banner> Banners { get; } = [];
    public ObservableCollection<HwSection> HardwareSections { get; } = [];
    public ObservableCollection<HwTile> HardwareTiles { get; } = [];

    public ObservableCollection<RecommendationLine> Recommendations { get; } = [];
    public ObservableCollection<RecommendationLine> RecommendationsExcluded { get; } = [];

    public Facts Facts => _state.Facts;
    public IReadOnlyList<TweakStatus> DeviceStates => _state.DeviceStates;

    public bool FindingsEmpty => Profile is not null && Findings.Count == 0;
    /// <summary>The list is empty and no summary card says why (the card names the passed checks and the switch).</summary>
    public bool AdvisorEmpty => Profile is not null && AdvisorItems.Count == 0 && AdvisorSummaryTitle is null;

    /// <summary>Manual checks page: what was checked and how it came out, above the list (which hides the passed ones).</summary>
    [ObservableProperty] private string? _advisorSummaryTitle;
    [ObservableProperty] private string? _advisorSummaryText;
    [ObservableProperty] private bool _advisorAllGood;

    partial void OnAdvisorSummaryTitleChanged(string? value) => OnPropertyChanged(nameof(AdvisorEmpty));

    private void BuildAdvisorSummary()
    {
        // Checks that do not apply to this PC (no such hardware) are not counted as checked.
        var all = _state.ProfiledFindings.Where(f => f.Kind == FindingKind.Advisor && f.Status != FindingStatus.Unsupported).ToList();
        if (Profile is null || all.Count == 0)
        {
            AdvisorSummaryTitle = null;
            return;
        }
        var problems = all.Count(f => f.IsProblem);
        var passed = all.Count(f => f.Status == FindingStatus.Ok);
        var unknown = all.Count(f => f.Status == FindingStatus.Unknown);
        var notes = all.Count(f => f.Status == FindingStatus.Info);
        AdvisorAllGood = problems == 0;
        AdvisorSummaryTitle = problems == 0 ? Loc.Instance["Advisor_SummaryGood"] : Loc.Instance.Format("Advisor_SummaryProblems", problems);
        // "9 checked: 8 passed, 1 could not be checked", parts of zero left out (like the counts on the Tweaks page)
        var parts = new[]
        {
            (Count: passed, Key: "Advisor_CountPassed"),
            (Count: unknown, Key: "Advisor_CountUnknown"),
            (Count: notes, Key: "Advisor_CountNotes"),
        }.Where(x => x.Count > 0).Select(x => Loc.Instance.Format(x.Key, x.Count)).ToList();
        var checkedText = Loc.Instance.Format("Advisor_SummaryCounts", all.Count);
        AdvisorSummaryText = parts.Count == 0 ? checkedText : $"{checkedText}: {string.Join(", ", parts)}";
        // Every check passed and the list hides them: the card says how to see them (a second line under it said the same).
        if (AdvisorItems.Count == 0 && !ShowPassed) AdvisorSummaryText += ". " + Loc.Instance["Advisor_SummaryShowPassed"];
    }

    public bool ChangesEmpty => ChangeRecords.Count == 0;
    public bool ChangeLogEmpty => ChangeLog.Count == 0;
    public bool IsElevated => _services.Elevation.IsElevated;

    public string Version => AppInfo.Text;
    public string AboutText => Loc.Instance.Format("About_Text", Version);
    public string LogFile => Log.CurrentFile ?? Log.Directory;
    public string DataFolder => AppServices.DataFolder;
    public string? VirusTotalKey => Optimizer.Core.Startup.Dpapi.Unprotect(_settings.VirusTotalKey);

    partial void OnShowPassedChanged(bool value)
    {
        // Set from elsewhere (the overview, the search): the Changed view no longer holds.
        if (value) _onlyChanged = false;
        OnPropertyChanged(nameof(TweakViewMode));
        Rebuild();
    }

    partial void OnOnlyRecommendedChanged(bool value)
    {
        // Set from elsewhere (the overview, the search): the Changed view no longer holds.
        if (value) _onlyChanged = false;
        OnPropertyChanged(nameof(TweakViewMode));
        RefillTweaks();
    }

    partial void OnTweakSearchChanged(string value) => RefillTweaks();

    partial void OnOnlyProfileChanged(bool value)
    {
        // Set from elsewhere (the overview, the search): the Changed view no longer holds.
        if (value) _onlyChanged = false;
        OnPropertyChanged(nameof(TweakViewMode));
        RefillTweaks();
    }

    /// <summary>A filter or the search changed: the rows are created again, and the selected tweak stays selected if it is still listed.</summary>
    private void RefillTweaks()
    {
        var selectedKey = SelectedItem?.Key;
        FillTweaks();
        RestoreSelection(selectedKey);
    }

    /// <summary>The user picked another profile: re-rate everything (nothing on the PC changes).</summary>
    partial void OnSelectedProfileChanged(ProfileOption? value)
    {
        if (value is null || value.Id == _state.Usage.Id) return;
        _state.SetProfile(value.Id, save: true);
        Rebuild();
        Network.Rebuild();
    }

    /// <summary>Developer aid (--perf): switches the profile as the picker does, without saving it.</summary>
    public void SetProfileForSession(ProfileOption option)
    {
        if (option.Id == _state.Usage.Id) return;
#pragma warning disable MVVMTK0034
        _selectedProfile = option;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedProfile));
        _state.SetProfile(option.Id, save: false);
        Rebuild();
        Network.Rebuild();
    }

    [RelayCommand]
    private void UseSuggestedProfile()
    {
        if (_state.Suggested is not null && ProfileOptions.FirstOrDefault(o => o.Id == _state.Suggested.Id) is { } option) SelectedProfile = option;
    }

    public static string ProfileName(UsageProfile p) => Loc.Instance[$"Profile_{p.Id}"];

    partial void OnCurrentPageChanged(Page value)
    {
        SelectedItem = null;
        if (value == Page.Changes) BuildChanges();
        Health.PageShown(value == Page.Health);
        (Views.PageRegistry.Of(value).Load?.Invoke(this) ?? Task.CompletedTask).Forget($"loading page {value}");
    }

    /// <summary>Developer switch --expert on: Expert mode for this session only (not saved, no confirmation).</summary>
    public void EnableExpertForSession()
    {
#pragma warning disable MVVMTK0034
        _expertMode = true;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(ExpertMode));
    }

    partial void OnExpertModeChanged(bool value)
    {
        if (value && !_settings.ExpertMode)
        {
            // Asked after the switch's binding has finished (a modal dialog inside a setter re-enters it, and a screen
            // reader's toggle call waits for the dialog); a "no" turns the switch back off.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!ExpertMode || _settings.ExpertMode) return;
                if (_dialogs.ConfirmExpertMode()) SaveExpertMode(true);
                else ExpertMode = false;
            });
            return;
        }
        SaveExpertMode(value);
    }

    private void SaveExpertMode(bool value)
    {
        _settings.ExpertMode = value;
        _settings.Save();
        Rebuild();
        Network.Rebuild();
    }

    /// <summary>Developer aid (--perf): switches Expert mode for this session as the switch does, without the question and without saving.</summary>
    public void SetExpertModeForSession(bool value)
    {
#pragma warning disable MVVMTK0034
        _expertMode = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(ExpertMode));
        Rebuild();
        Network.Rebuild();
    }

    /// <summary>Score and counts only after a finished scan: during a scan the cards keep their layout but show "--".</summary>
    public bool HasScore => Profile is not null && !IsScanning;

    /// <summary>While a scan runs (and before the first): what the scan is doing, not an old or empty verdict.</summary>
    public string VerdictShown => HasScore ? Verdict : ScanFailed && !IsScanning ? Loc.Instance["Dash_VerdictFailed"] : Loc.Instance["Dash_VerdictPending"];
    public string ReadinessShown => HasScore ? ReadinessLine : "";
    public string? AdvisorLinkShown => HasScore ? AdvisorLinkText : null;

    private void NotifyScore()
    {
        foreach (var name in new[] { nameof(HasScore), nameof(VerdictShown), nameof(ReadinessShown), nameof(AdvisorLinkShown) })
            OnPropertyChanged(name);
    }

    partial void OnVerdictChanged(string value) => NotifyScore();
    partial void OnReadinessLineChanged(string value) => NotifyScore();
    partial void OnAdvisorLinkTextChanged(string? value) => NotifyScore();
    partial void OnProfileChanged(HardwareProfile? value) => NotifyScore();
    partial void OnScanFailedChanged(bool value) => NotifyScore();

    partial void OnIsScanningChanged(bool value)
    {
        ChangeGate.Instance.Scanning = value;
        NotifyScore();
    }

    /// <summary>No change, scan or update is running: switches and change buttons are enabled.</summary>
    public bool CanChange => ChangeGate.Instance.CanChange;

    /// <summary>A scan may start while no change or update runs (a running scan is joined, not repeated).</summary>
    private bool CanScan() => !ChangeGate.Instance.Busy && !ChangeGate.Instance.Updating;

    /// <summary>
    /// The simple or the technical view of the lists: off hides paths, registry locations and package names (the details
    /// pane, confirmations and copied reports keep them).
    /// </summary>
    public bool ShowTechnical
    {
        get => _technicalForSession ?? _settings.TechnicalDetails;
        set
        {
            if (value == ShowTechnical) return;
            _technicalForSession = null;
            _settings.TechnicalDetails = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>The view chosen by a developer switch (--technical) for this run; the saved setting stays as it is.</summary>
    private bool? _technicalForSession;

    internal void ShowTechnicalForSession(bool on)
    {
        _technicalForSession = on;
        OnPropertyChanged(nameof(ShowTechnical));
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _settings.Save();
    }

    public InstallFacts Install { get; }
    public UpdateService Updates { get; }
    public UninstallService Uninstalls { get; }

    void IShell.OpenLink(string url) => OpenLink(url);

    void IShell.Shutdown() => System.Windows.Application.Current.Shutdown();

    public string? UpdateStatus => Updates.Checking ? Loc.Instance["Update_Checking"] : Updates.Release?.Status switch
    {
        ReleaseCheckStatus.UpToDate => Loc.Instance.Format("Update_UpToDate", Version),
        ReleaseCheckStatus.NewerAvailable => Loc.Instance.Format("Update_Available", Updates.Release.Latest!.ToString(3)),
        ReleaseCheckStatus.NoRelease => Loc.Instance["Update_NoRelease"],
        ReleaseCheckStatus.Error => Loc.Instance["Update_Error"],
        _ => null,
    };

    public bool UpdateAvailable => Updates.UpdateAvailable;
    public bool CanUpdateInApp => Updates.CanUpdateInApp;
    public bool Updating => Updates.Updating;
    public PackageInstall? Package => Install.Package;
    public bool CanUninstall => Uninstalls.CanUninstall;

    /// <summary>The published single-file exe (a Debug or Release build has Tunero.dll next to its exe).</summary>
    public static bool IsPublishedExe { get; } = InstallFacts.PublishedExe(Environment.ProcessPath);

    private void OnUpdateStateChanged()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(CanUpdateInApp));
        OnPropertyChanged(nameof(Updating));
    }

    /// <summary>At start only when the user turned the check on; "Check now" always asks.</summary>
    public async Task CheckForUpdatesAsync(bool atStart)
    {
        if (atStart && !_settings.CheckForUpdates) return;
        await Updates.CheckAsync();
        BuildBanners();
        SettingsBadge = UpdateAvailable ? "1" : null;
        if (atStart && UpdateAvailable) ShowUpdateNotice();
    }

    /// <summary>Developer aid (--screenshot with --update-shot): as if the check at start had found <paramref name="latest"/>.</summary>
    public void PreviewUpdateFound(Version latest)
    {
        Updates.PreviewFound(latest);
        BuildBanners();
        SettingsBadge = "1";
        ShowUpdateNotice();
    }

    /// <summary>
    /// The check at start found a new version: the result bar says so on whatever page is open, with the button that
    /// updates (or opens the release page when this copy cannot update itself), and stays until it is closed or used.
    /// </summary>
    private void ShowUpdateNotice()
    {
        if (Updates.Release?.Latest is not { } latest) return;
        var version = latest.ToString(3);
        // A package manager offers the version only after its review (winget and Chocolatey can take days): its copy gets
        // the bar once per version, saying so; the Settings badge stays until the update is installed.
        if (Package is { } package)
        {
            if (_settings.UpdateNoticeShownFor == version) return;
            _settings.UpdateNoticeShownFor = version;
            _settings.Save();
            ShowResult(Loc.Instance.Format("Update_AvailablePackage", version, package.DisplayName));
        }
        else
        {
            ShowResult(UpdateStatus!);
        }
        _resultTimer?.Stop();
        ResultUpdateText = Loc.Instance[CanUpdateInApp ? "Update_Install" : "Update_Open"];
        ResultShowsUpdate = true;
    }

    [RelayCommand]
    private Task CheckUpdatesNow() => CheckForUpdatesAsync(atStart: false);

    [RelayCommand]
    private void OpenReleasePage() => OpenLink(ReleaseCheck.LatestReleaseUrl);

    /// <summary>
    /// Opens the GitHub bug form with version and Windows build filled in. Both are part of the link, so GitHub receives
    /// them when the browser opens it (docs/PRIVACY.md); the report itself is sent only when the user submits it there.
    /// </summary>
    [RelayCommand]
    private void ReportProblem() =>
        OpenLink($"https://github.com/{ReleaseCheck.Repository}/issues/new?template=bug_report.yml" +
                 $"&version={Uri.EscapeDataString(Version)}&windows={Uri.EscapeDataString(_services.Os.BuildString)}");

    /// <summary>Copies this session's log for a bug report (the clipboard, not a file in a user-writable folder).</summary>
    [RelayCommand]
    private void CopyLog()
    {
        var entries = Log.Snapshot(400);
        var text = string.Join(Environment.NewLine, entries.Select(e =>
            $"{e.Time:HH:mm:ss} {e.Level} {e.Source}: {e.Message}{(e.Data is null ? "" : " " + e.Data)}"));
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowResult(Loc.Instance.Format("Log_Copied", entries.Count));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowResult(Loc.Instance["Log_CopyFailed"], InfoBarSeverity.Warning);
        }
    }

    /// <summary>Copies the System info page as text (technical rows included), for a forum post or a bug report.</summary>
    [RelayCommand]
    private void CopyHardware()
    {
        try
        {
            System.Windows.Clipboard.SetText(HardwareReport.ToText(HardwareSections));
            ShowResult(Loc.Instance["Hw_Copied"]);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowResult(Loc.Instance["Log_CopyFailed"], InfoBarSeverity.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task UpdateNowAsync() => Updates.UpdateNowAsync();
}
