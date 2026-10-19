using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Catalog;
using Optimizer.Core.Debloat;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

/// <summary>Runs one build (<see cref="MediaBuild.RunAsync"/> in the app; a fake in tests and the demo).</summary>
public delegate Task<string> MediaBuilder(MediaRequest request, IProgress<MediaProgress> progress, CancellationToken ct);

/// <summary>What the install media page needs to know about this PC and this copy of Tunero. Tests pass their own.</summary>
public sealed record InstallMediaSystem
{
    /// <summary>This PC's EditionID (like "Professional"), the default edition when the media offers it.</summary>
    public string? PcEdition { get; init; }

    /// <summary>This PC is ARM64: the page says it builds x64 media only.</summary>
    public bool Arm64 { get; init; }

    /// <summary>DISM and the locked work folder need administrator rights.</summary>
    public bool Elevated { get; init; }

    /// <summary>The running exe, copied onto the media for the first sign-in.</summary>
    public string? ExePath { get; init; }

    /// <summary>The running exe is the published single file (a Debug or Release build has Tunero.dll beside it).</summary>
    public bool IsPublishedExe { get; init; }

    /// <summary>Where the ISO goes by default: the signed-in user's Downloads.</summary>
    public string OutputFolder { get; init; } = "";

    public Func<string> ProfileId { get; init; } = () => CatalogData.Current.Profiles.Default.Id;

    /// <summary>The app's theme setting (System, Dark or Light), which the copy on the media starts with.</summary>
    public Func<string> Theme { get; init; } = () => "System";

    /// <summary>The signed-in user's name, the default name of the new PC's account.</summary>
    public string? UserName { get; init; }

    /// <summary>This PC's region format and keyboard (input profile), the defaults for the new PC.</summary>
    public string? Region { get; init; }
    public string? Keyboard { get; init; }

    /// <summary>Free bytes on the drive of a path, or null when that cannot be read.</summary>
    public Func<string, long?> FreeBytes { get; init; } = _ => null;

    /// <summary>Shows a file selected in Explorer (as the signed-in user).</summary>
    public Action<string> ShowInExplorer { get; init; } = _ => { };

    /// <summary>The real system: this PC's facts, Downloads, the drive's free space.</summary>
    public static InstallMediaSystem ForThisPc(AppServices services, Func<string> profileId, Func<string> theme, Action<string> showInExplorer, bool isPublishedExe)
    {
        var os = BuildInfo.Read();
        return new InstallMediaSystem
        {
            PcEdition = os.Edition,
            Arm64 = os.NativeArchitecture == CpuArchitecture.Arm64,
            Elevated = DataPaths.ProcessIsElevated,
            ExePath = Environment.ProcessPath,
            IsPublishedExe = isPublishedExe,
            OutputFolder = services.SessionDownloadsFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ProfileId = profileId,
            Theme = theme,
            UserName = services.SessionUserName(),
            Region = MediaLocales.ThisPcRegion(),
            Keyboard = MediaLocales.ThisPcKeyboard(),
            FreeBytes = FreeBytesOf,
            ShowInExplorer = showInExplorer,
        };
    }

    /// <summary>
    /// The real cleanup after earlier builds (<see cref="MediaBuild.CleanLeftoversAsync"/>). Without a work folder at a
    /// drive root no build left anything (its mounted image lies inside one), so a start without leftovers runs no DISM.
    /// </summary>
    public static async Task CleanLeftoversAsync(CancellationToken ct)
    {
        var steps = new MediaSteps();
        if (!await Task.Run(() => steps.LocalDrives().Any(d => steps.LeftoverWorkFolders(d).Any()), ct)) return;
        await new MediaBuild(steps).CleanLeftoversAsync(ct);
    }

    private static long? FreeBytesOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>An app of the reviewed list (appx.json) with its tick for the media.</summary>
public sealed partial class MediaAppRow(AppxEntry entry, string lang, bool isChecked) : ObservableObject
{
    public AppxEntry Entry { get; } = entry;
    public string Title { get; } = entry.Label(lang);
    public string Text { get; } = entry.Text(lang);

    /// <summary>Whether the Microsoft Store still offers the app (as on Debloat).</summary>
    public string Note { get; } = Loc.Instance[entry.CanReinstall ? "Media_AppStore" : "Media_AppNoStore"];

    [ObservableProperty] private bool _isChecked = isChecked;

    // What screen readers announce for this item in a list.
    public override string ToString() => Title;
}

/// <summary>One group of the reviewed list, as on Debloat.</summary>
public sealed partial class MediaAppGroup : ObservableObject
{
    public MediaAppGroup(string key, string title, IReadOnlyList<MediaAppRow> rows)
    {
        Key = key;
        Title = title;
        Rows = rows;
        _isExpanded = key == "consumer";
        foreach (var row in rows) row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<MediaAppRow> Rows { get; }

    [ObservableProperty] private bool _isExpanded;

    /// <summary>"3 of 18 chosen".</summary>
    public string Summary => Loc.Instance.Format("Media_AppsChosen", Rows.Count(r => r.IsChecked), Rows.Count);

    public override string ToString() => Title;
}

/// <summary>
/// The Windows install media page: where the image comes from, the edition, the changes and the output file, then the
/// build with the banner (stage, percent, Stop). Every choice is picked from a list or checked as typed, and Build stays
/// off until the choices make a whole request; the engine checks everything again (MediaBuild).
/// </summary>
public sealed partial class InstallMediaViewModel : PageViewModel
{
    private const string Architecture = "amd64";
    private const string CatalogArchitecture = "x64";
    private static readonly string[] GroupOrder = ["consumer", "microsoft", "xbox", "media", "legacy", "thirdParty", "oem"];

    private readonly IDialogs _dialogs;
    private IMediaCatalogSource _catalog;
    private MediaBuilder _build;
    private InstallMediaSystem _system;
    private readonly Func<CancellationToken, Task>? _cleanLeftovers;
    private IReadOnlyList<EsdFile>? _files;
    private bool _outputPicked;
    private bool _demo;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private Task? _cleanup;

    public InstallMediaViewModel(IPageHost owner, IDialogs dialogs, IMediaCatalogSource catalog, MediaBuilder build, InstallMediaSystem system,
        Func<CancellationToken, Task>? cleanLeftovers = null) : base(owner)
    {
        _dialogs = dialogs;
        _catalog = catalog;
        _build = build;
        _system = system;
        _cleanLeftovers = cleanLeftovers;
        _canApplyProfile = system.IsPublishedExe && system.ExePath is not null;
        _applyProfile = _canApplyProfile;
        // A name Windows would refuse is not offered; the field stays empty then.
        _accountNameText = system.UserName is { } user && AccountName.Problem(user) is null ? user : "";
    }

    // ---- source ----

    /// <summary>Download from Microsoft (true) or the user's own ISO (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseIso))]
    private bool _useDownload = true;

    public bool UseIso
    {
        get => !UseDownload;
        set => UseDownload = !value;
    }

    partial void OnUseDownloadChanged(bool value)
    {
        ShowEditions();
        Changed();
    }

    /// <summary>On an ARM64 PC: Tunero builds x64 media only.</summary>
    public string? ArchitectureNote => _system.Arm64 ? Loc.Instance["Media_X64Only"] : null;

    [ObservableProperty] private bool _isCheckingCatalog;
    [ObservableProperty] private string? _catalogStep;
    [ObservableProperty] private string? _catalogError;

    /// <summary>"Windows 11, build 26300.9711, 6.2 GB" for the chosen language and edition.</summary>
    [ObservableProperty] private string? _catalogText;

    public bool HasCatalog => _files is not null;

    [ObservableProperty] private IReadOnlyList<FilterOption> _languages = [];
    [ObservableProperty] private FilterOption? _language;

    partial void OnLanguageChanged(FilterOption? value)
    {
        ShowEditions();
        Changed();
    }

    [ObservableProperty] private string? _isoPath;

    partial void OnIsoPathChanged(string? value) => Changed();

    // ---- edition ----

    [ObservableProperty] private IReadOnlyList<FilterOption> _editions = [];
    [ObservableProperty] private FilterOption? _edition;

    partial void OnEditionChanged(FilterOption? value) => Changed();

    // ---- changes ----

    [ObservableProperty] private bool _removeApps = true;
    [ObservableProperty] private IReadOnlyList<MediaAppGroup> _appGroups = [];

    public IReadOnlyList<MediaAppRow> AppRows => AppGroups.SelectMany(g => g.Rows).ToList();

    partial void OnRemoveAppsChanged(bool value) => Changed();

    [ObservableProperty] private bool _skipSetup = true;
    [ObservableProperty] private string _accountNameText = "";

    /// <summary>Why the typed account name cannot be used, or null.</summary>
    public string? AccountProblem => SkipSetup && AccountName.Problem(AccountNameText.Trim()) is { } key ? Loc.Instance[key] : null;

    partial void OnSkipSetupChanged(bool value) => Changed();

    partial void OnAccountNameTextChanged(string value) => Changed();

    [ObservableProperty] private IReadOnlyList<FilterOption> _regions = [];
    [ObservableProperty] private FilterOption? _region;
    [ObservableProperty] private IReadOnlyList<FilterOption> _keyboards = [];
    [ObservableProperty] private FilterOption? _keyboard;

    partial void OnRegionChanged(FilterOption? value) => Changed();

    partial void OnKeyboardChanged(FilterOption? value) => Changed();

    [ObservableProperty] private bool _applyProfile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileNote))]
    private bool _canApplyProfile;

    /// <summary>Why the profile cannot go on the media (a build that is not the published exe).</summary>
    public string? ProfileNote => CanApplyProfile ? null : Loc.Instance["Media_ProfileNeedsRelease"];

    [ObservableProperty] private IReadOnlyList<FilterOption> _profiles = [];
    [ObservableProperty] private FilterOption? _profile;

    partial void OnApplyProfileChanged(bool value) => Changed();

    [ObservableProperty] private bool _allowUnsupported;

    /// <summary>The change cards start folded; the demo can unfold them for screenshots.</summary>
    [ObservableProperty] private bool _expandOptions;

    partial void OnAllowUnsupportedChanged(bool value) => Changed();

    // ---- build ----

    [ObservableProperty] private string? _outputPath;

    partial void OnOutputPathChanged(string? value) => Changed();

    [ObservableProperty] private bool _isBuilding;

    partial void OnIsBuildingChanged(bool value) => Changed();

    /// <summary>The space the build needs on the output drive and what is free there.</summary>
    public string? NeededText
    {
        get
        {
            if (Needed() is not { } needed) return null;
            return OutputPath is { } output && _system.FreeBytes(output) is { } free
                ? Loc.Instance.Format("Media_Needed", ByteSize.Format(needed), ByteSize.Format(free))
                : Loc.Instance.Format("Media_NeededOnly", ByteSize.Format(needed));
        }
    }

    /// <summary>Why Build is off (the first choice that is missing or wrong), or null when it can start.</summary>
    public string? BuildNote => Problem() is { } key ? Loc.Instance[key] : null;

    public bool CanBuild => Problem() is null;

    // The result of the last build, shown on the Build card (and in the window's result bar).
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private InfoBarSeverity _resultSeverity;
    [ObservableProperty] private string? _resultLogPath;
    [ObservableProperty] private string? _resultIso;

    public bool HasResult => ResultText is not null;

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    private void Changed()
    {
        OnPropertyChanged(nameof(AccountProblem));
        OnPropertyChanged(nameof(NeededText));
        OnPropertyChanged(nameof(BuildNote));
        OnPropertyChanged(nameof(CanBuild));
        UpdateCatalogText();
        UpdateOutputName();
        BuildCommand.NotifyCanExecuteChanged();
    }

    protected override void WorkingChanged() => Changed();

    /// <summary>The Strings key of the first thing that keeps Build off, or null.</summary>
    private string? Problem()
    {
        if (IsBuilding || IsWorking) return "Media_Building";
        if (!_system.Elevated && !_demo) return "Media_NeedsAdmin";
        if (UseDownload)
        {
            if (_files is null) return "Media_NeedsCatalog";
            if (Download() is null) return "Media_NoImage";
        }
        else if (IsoPath is not { } iso || !File.Exists(iso))
        {
            return "Media_NeedsIso";
        }
        if (Edition is null) return "Media_NoImage";
        if (SkipSetup && AccountName.Problem(AccountNameText.Trim()) is { } name) return name;
        if (SkipSetup && (Region is null || Keyboard is null)) return "Media_NeedsRegion";
        if (OutputPath is not { } output || !ValidOutput(output)) return "Media_OutputInvalid";
        if (!UseDownload && IsoPath is { } source && string.Equals(Path.GetFullPath(source), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            return "Media_OutputInvalid";
        if (Needed() is { } needed && _system.FreeBytes(output) is { } free && free < needed) return "Media_NotEnoughSpace";
        return null;
    }

    private static bool ValidOutput(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) && !Directory.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private EsdFile? Download() =>
        _files is { } files && Language is { } language && Edition is { } edition
            ? MediaCatalog.Pick(files, language.Key, CatalogArchitecture, edition.Key)
            : null;

    private MediaSource? Source() =>
        UseDownload ? Download() is { } esd ? new MediaSource(esd, null, null) : null
        : IsoPath is { } iso ? new MediaSource(null, iso, null) : null;

    private long? Needed()
    {
        if (Source() is not { } source) return null;
        if (source.Download is { } esd) return MediaBuild.NeededBytes(source, esd.Size);
        try
        {
            var info = new FileInfo(source.IsoPath!);
            return info.Exists ? MediaBuild.NeededBytes(source, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ---- loading and lists ----

    protected override async Task LoadAsync()
    {
        var lists = await Task.Run(() => (Regions: MediaLocales.Regions(), Keyboards: MediaLocales.Keyboards));
        _regionCultures = lists.Regions;
        BuildLists();
        if (_demo && _files is null) await CheckCatalogAsync();
    }

    protected override Task RelabelAsync()
    {
        if (_regionCultures is null) return LoadAsync();
        BuildLists();
        if (_files is not null) ShowCatalog();
        OnPropertyChanged(nameof(ArchitectureNote));
        OnPropertyChanged(nameof(ProfileNote));
        return Task.CompletedTask;
    }

    private IReadOnlyList<System.Globalization.CultureInfo>? _regionCultures;

    /// <summary>The lists in the current language; every choice keeps its key.</summary>
    private void BuildLists()
    {
        var lang = Lang;
        var regionKey = Region?.Key ?? _system.Region ?? "en-US";
        Regions = _regionCultures!.Select(c => new FilterOption(c.Name, c.DisplayName)).OrderBy(o => o.Text, StringComparer.CurrentCulture).ToList();
        Region = Regions.FirstOrDefault(o => o.Key.Equals(regionKey, StringComparison.OrdinalIgnoreCase))
                 ?? Regions.FirstOrDefault(o => o.Key == "en-US");

        var keyboardKey = Keyboard?.Key ?? _system.Keyboard
            ?? (Region is { } r ? MediaLocales.DefaultKeyboard(new CultureInfo(r.Key)) : null) ?? "0409:00000409";
        Keyboards = MediaLocales.Keyboards.Select(k => new FilterOption(k.Code, $"{k.Keyboard} ({k.LanguageTag})"))
            .OrderBy(o => o.Text, StringComparer.CurrentCulture).ToList();
        Keyboard = Keyboards.FirstOrDefault(o => o.Key.Equals(keyboardKey, StringComparison.OrdinalIgnoreCase))
                   ?? Keyboards.FirstOrDefault(o => o.Key == "0409:00000409");

        var profileKey = Profile?.Key ?? _system.ProfileId();
        Profiles = CatalogData.Current.Profiles.Profiles.Select(p => new FilterOption(p.Id, Loc.Instance[$"Profile_{p.Id}"])).ToList();
        Profile = Profiles.FirstOrDefault(o => o.Key == profileKey) ?? Profiles.FirstOrDefault();

        // The ticks survive a language switch.
        var ticked = AppGroups.Count > 0 ? AppRows.Where(r => r.IsChecked).Select(r => r.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var appx = CatalogData.Current.Appx;
        AppGroups = appx.Apps.Where(a => !appx.IsProtected(a.Name))
            .GroupBy(a => a.Group)
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var i and >= 0 ? i : GroupOrder.Length)
            .Select(g => new MediaAppGroup(g.Key, Labels.Current.Get(lang, $"debloatGroup.{g.Key}"),
                g.OrderBy(a => a.Label(lang), StringComparer.CurrentCulture)
                 .Select(a => new MediaAppRow(a, lang, ticked?.Contains(a.Name) ?? a.Group == "consumer")).ToList()))
            .ToList();
        OnPropertyChanged(nameof(AppRows));
        ShowEditions();
        Changed();
    }

    /// <summary>The editions of the chosen download, or every offered one for an own ISO (the build checks it is there).</summary>
    private void ShowEditions()
    {
        var keys = UseDownload && _files is { } files && Language is { } language
            ? MediaCatalog.Editions(files, language.Key, CatalogArchitecture)
            : MediaCatalog.OfferedEditions;
        var key = Edition?.Key;
        Editions = keys.Select(e => new FilterOption(e, Loc.Instance[$"Media_Edition_{e}"])).ToList();
        Edition = Editions.FirstOrDefault(o => o.Key == key)
                  ?? Editions.FirstOrDefault(o => o.Key == _system.PcEdition)
                  ?? Editions.FirstOrDefault(o => o.Key == "Professional")
                  ?? Editions.FirstOrDefault();
    }

    /// <summary>The app language's Windows language: de-de for German, en-us otherwise.</summary>
    private string DefaultLanguage => Lang == "de" ? "de-de" : "en-us";

    private void ShowCatalog()
    {
        var key = Language?.Key ?? DefaultLanguage;
        Languages = _files!.Where(f => f.IsConsumer && f.Architecture.Equals(CatalogArchitecture, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FilterOption(g.Key, LanguageName(g.Key, g.First().Language)))
            .OrderBy(o => o.Text, StringComparer.CurrentCulture)
            .ToList();
        Language = Languages.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                   ?? Languages.FirstOrDefault(o => o.Key == "en-us") ?? Languages.FirstOrDefault();
        OnPropertyChanged(nameof(HasCatalog));
        ShowEditions();
        Changed();
    }

    /// <summary>The language in the app's language where .NET knows it, else the catalog's English name.</summary>
    private static string LanguageName(string code, string fallback)
    {
        try
        {
            return new CultureInfo(code).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return fallback;
        }
    }

    private void UpdateCatalogText() =>
        CatalogText = Download() is { } esd ? Loc.Instance.Format("Media_CatalogFound", esd.Build, ByteSize.Format(esd.Size)) : null;

    /// <summary>Windows11_26300.9711_de-de_Professional_Tunero.iso in Downloads, until the user picks a file.</summary>
    private void UpdateOutputName()
    {
        if (_outputPicked || _system.OutputFolder.Length == 0) return;
        var edition = Edition?.Key ?? "Professional";
        var name = UseDownload && Download() is { } esd
            ? $"Windows11_{esd.Build}_{esd.LanguageCode}_{edition}_Tunero.iso"
            : $"Windows11_{edition}_Tunero.iso";
        var path = Path.Combine(_system.OutputFolder, name);
        if (path != OutputPath) OutputPath = path;
    }

    // ---- commands ----

    /// <summary>Asks Microsoft's catalog for the newest Windows 11 (the page's first network use, started by the user).</summary>
    [RelayCommand]
    public async Task CheckCatalogAsync()
    {
        if (IsCheckingCatalog) return;
        IsCheckingCatalog = true;
        CatalogError = null;
        try
        {
            var progress = new Progress<string>(step => CatalogStep = Loc.Instance[$"Media_CatalogStep_{step}"]);
            _files = await _catalog.LoadAsync(Architecture, progress, CancellationToken.None);
            ShowCatalog();
        }
        catch (MediaDownloadException ex)
        {
            Log.Warn("media", $"catalog: {ex.Step} failed: {ex.Message}");
            CatalogError = Loc.Instance.Format("Media_CatalogFailed", Loc.Instance[$"Media_CatalogStep_{ex.Step}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Log.Warn("media", $"catalog failed: {ex.Message}");
            CatalogError = Loc.Instance.Format("Media_CatalogFailed", ex.Message);
        }
        finally
        {
            CatalogStep = null;
            IsCheckingCatalog = false;
        }
    }

    [RelayCommand]
    private void PickIso()
    {
        if (_dialogs.PickOpenFile(Loc.Instance["Media_IsoFiles"], ".iso") is not { } path) return;
        IsoPath = path;
        UseDownload = false;
    }

    [RelayCommand]
    private void PickOutput()
    {
        var current = OutputPath;
        var folder = current is not null && Path.GetDirectoryName(current) is { } f && Directory.Exists(f) ? f : _system.OutputFolder;
        if (_dialogs.PickSaveFile(Path.GetFileName(current) ?? "Windows11_Tunero.iso", Loc.Instance["Media_IsoFiles"], ".iso", folder) is not { } path) return;
        _outputPicked = true;
        OutputPath = path;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (ResultIso is { } iso) _system.ShowInExplorer(iso);
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (ResultLogPath is { } log) _system.ShowInExplorer(log);
    }

    /// <summary>The request from the page's choices; null when they do not make one (Build is off then).</summary>
    private MediaRequest? Request()
    {
        if (Problem() is not null || Source() is not { } source) return null;
        var region = Region?.Key ?? "en-US";
        // The download's language; for an own ISO the build takes the image's own (the page cannot read it).
        var uiLanguage = new CultureInfo(source.Download?.LanguageCode ?? DefaultLanguage).Name;
        var tunero = ApplyProfile && CanApplyProfile;
        var answers = new AnswerChoices(Architecture, uiLanguage, Keyboard?.Key ?? "0409:00000409", MediaLocales.SystemLocale(region), region,
            SkipSetup ? AccountNameText.Trim() : null, SkipSetup, AllowUnsupported, tunero);
        var apps = RemoveApps ? AppRows.Where(r => r.IsChecked).Select(r => r.Entry.Name).ToList() : [];
        var first = tunero ? new FirstSignInFile(Profile?.Key ?? _system.ProfileId(), Lang, _system.Theme()) : null;
        return new MediaRequest(source, Edition!.Key, null, apps, answers, tunero ? _system.ExePath : null, first, OutputPath!);
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private Task BuildAsync()
    {
        if (Request() is not { } request) return Task.CompletedTask;
        return _running = RunBuildAsync(request);
    }

    private async Task RunBuildAsync(MediaRequest request)
    {
        using var stop = new CancellationTokenSource();
        _stop = stop;
        IsBuilding = true;
        ResultText = null;
        ResultLogPath = null;
        ResultIso = null;
        string? iso = null;
        Exception? failure = null;
        var stopped = false;
        try
        {
            // A cleanup of earlier builds finishes first: the two never run together. Stop meanwhile ends it here.
            if (_cleanup is { } cleanup) await cleanup;
            if (stop.IsCancellationRequested) stopped = true;
            else await RunWorkAsync("install media", Loc.Instance["Media_Building"], async scope =>
            {
                var progress = new Progress<MediaProgress>(p => scope.Report(p.Percent, StageText(p)));
                try
                {
                    iso = await _build(request, progress, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    stopped = true;
                }
                catch (Exception ex) when (ex is MediaBuildException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    failure = ex;
                }
            }, stop);
        }
        finally
        {
            _stop = null;
            IsBuilding = false;
        }

        if (iso is not null) ShowSuccess(iso);
        else if (failure is not null) ShowFailure(failure);
        else if (stopped) Show(Loc.Instance["Media_Stopped"], InfoBarSeverity.Informational);
    }

    private static string StageText(MediaProgress p)
    {
        var stage = Loc.Instance[$"Media_Stage_{p.Stage}"];
        return p.Detail is { Length: > 0 } detail ? $"{stage}: {detail}" : stage;
    }

    private void ShowSuccess(string iso, bool window = true)
    {
        long? size = null;
        try
        {
            if (File.Exists(iso)) size = new FileInfo(iso).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        ResultIso = iso;
        Show(size is { } bytes ? Loc.Instance.Format("Media_Done", iso, ByteSize.Format(bytes)) : Loc.Instance.Format("Media_DoneNoSize", iso),
            InfoBarSeverity.Success, window);
    }

    /// <summary>The stage, the reason in words, DISM's or Windows' error code, and where the log is.</summary>
    private void ShowFailure(Exception ex, bool window = true)
    {
        Log.Warn("media", $"build failed: {ex.Message}");
        if (ex is not MediaBuildException m)
        {
            Show(Loc.Instance.Format("Media_FailedRequest", ex.Message), InfoBarSeverity.Error, window);
            return;
        }
        var reason = m.Key is { } key ? Loc.Instance[key] : m.InUse ? Loc.Instance["Media_IsoInUse"] : Loc.Instance["Media_FailedUnknown"];
        var text = Loc.Instance.Format("Media_Failed", Loc.Instance[$"Media_Stage_{m.Stage}"], reason);
        if (m.Code is { } code) text += " " + Loc.Instance.Format("Media_ErrorCode", CodeText(code));
        if (m.LogPath is { } log) text += " " + Loc.Instance.Format("Media_LogAt", log);
        ResultLogPath = m.LogPath;
        Show(text, InfoBarSeverity.Error, window);
    }

    /// <summary>DISM's HRESULTs as 0x800F081F, Windows' small error numbers as they are (87).</summary>
    private static string CodeText(int code) =>
        code is < 0 or > 0xFFFF ? $"0x{code:X8}" : code.ToString(CultureInfo.InvariantCulture);

    private void Show(string text, InfoBarSeverity severity, bool window = true)
    {
        ResultText = text;
        ResultSeverity = severity;
        // The page shows it on the Build card; on another page (a build goes on there) the window's result bar says it.
        if (window && Owner.CurrentPage != Page.InstallMedia) Owner.ShowResult(text, severity);
    }

    // ---- stop, close and leftovers ----

    /// <summary>Stops a running build and waits until it has cleaned up (closing the window asks for this).</summary>
    public async Task StopAndWaitAsync()
    {
        _stop?.Cancel();
        if (_running is { } running) await running;
    }

    /// <summary>
    /// Removes what killed or crashed builds left (work folders at drive roots, their mounted images), in the background.
    /// Only when no build runs; a build that starts meanwhile waits for it. Never throws.
    /// </summary>
    public Task CleanLeftoversAsync()
    {
        if (IsBuilding || _cleanLeftovers is null) return Task.CompletedTask;
        return _cleanup = Clean(_cleanLeftovers);

        static async Task Clean(Func<CancellationToken, Task> clean)
        {
            try
            {
                await clean(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error("media", "cleanup of earlier builds failed", ex);
            }
        }
    }

    // ---- demo (--media-demo) ----

    /// <summary>
    /// Developer aid: the sample catalog instead of Microsoft's and a build that only shows its stages, then a sample
    /// failure and the next time a sample success. Nothing goes to the network or runs. <paramref name="show"/> "fail" or
    /// "done" shows that result at once, "expand" unfolds the change cards (for screenshots; combined with a comma).
    /// </summary>
    public void UseDemo(string? show)
    {
        var shows = (show ?? "").Split(',', StringSplitOptions.TrimEntries);
        ExpandOptions = shows.Contains("expand");
        _demo = true;
        _catalog = new InstallMediaDemo.Catalog();
        var demo = new InstallMediaDemo.Build();
        _build = demo.RunAsync;
        _system = _system with { Elevated = true, ExePath = _system.ExePath ?? "Tunero.exe", IsPublishedExe = true };
        CanApplyProfile = true;
        ApplyProfile = true;
        AccountNameText = "Anna";
        if (shows.Contains("fail")) ShowFailure(InstallMediaDemo.Failure(), window: false);
        else if (shows.Contains("done"))
        {
            ResultIso = Path.Combine(_system.OutputFolder, "Windows11_Tunero.iso");
            Show(Loc.Instance.Format("Media_Done", ResultIso, ByteSize.Format(5_712_000_000)), InfoBarSeverity.Success, window: false);
        }
    }
}
