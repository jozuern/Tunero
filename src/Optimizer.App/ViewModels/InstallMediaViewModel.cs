using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core;
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

/// <summary>
/// What the page reads about this PC when it opens (off the UI thread, never at app start): its edition, whether it is
/// ARM64, the signed-in user's name and region format, and their Downloads folder.
/// </summary>
public sealed record ThisPcFacts(string? Edition, bool Arm64, string? UserName, string? Region, string DownloadsFolder);

/// <summary>What the install media page needs to know about this PC and this copy of Tunero. Tests pass their own.</summary>
public sealed record InstallMediaSystem
{
    /// <summary>DISM, attaching an ISO and the locked work folder need administrator rights.</summary>
    public bool Elevated { get; init; }

    /// <summary>The running exe, copied onto the media for the first sign-in.</summary>
    public string? ExePath { get; init; }

    /// <summary>The running exe is the published single file (a Debug or Release build has Tunero.dll beside it).</summary>
    public bool IsPublishedExe { get; init; }

    public Func<string> ProfileId { get; init; } = () => CatalogData.Current.Profiles.Default.Id;

    /// <summary>The app's theme setting (System, Dark or Light), which the copy on the media starts with.</summary>
    public Func<string> Theme { get; init; } = () => "System";

    /// <summary>Reads <see cref="ThisPcFacts"/>; called on a background thread when the page opens.</summary>
    public Func<ThisPcFacts> ReadPc { get; init; } = () => new ThisPcFacts(null, false, null, null, "");

    /// <summary>The keyboard this PC uses now (an input profile), read on the UI thread: the layout is per thread.</summary>
    public Func<string?> Keyboard { get; init; } = () => null;

    /// <summary>The keyboards to offer (Windows' own list, read from the registry on a background thread).</summary>
    public Func<IReadOnlyList<InputProfile>> Keyboards { get; init; } = () => MediaLocales.Keyboards;

    /// <summary>Reads what an own ISO holds (attaches it read-only); throws <see cref="IsoAttachException"/>.</summary>
    public Func<string, CancellationToken, Task<IsoInfo>> ReadIso { get; init; } = (_, _) => throw new NotSupportedException();

    /// <summary>The apps one image of an own ISO holds, as full package names (attaches it read-only and lists the image with DISM).</summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<string>>> ReadIsoApps { get; init; } = (_, _, _) => throw new NotSupportedException();

    /// <summary>Free bytes on the drive of a path, or null when that cannot be read.</summary>
    public Func<string, long?> FreeBytes { get; init; } = _ => null;

    /// <summary>Shows a file selected in Explorer (as the signed-in user).</summary>
    public Action<string> ShowInExplorer { get; init; } = _ => { };

    /// <summary>The real system. Reads nothing now: this runs while the window is built.</summary>
    public static InstallMediaSystem ForThisPc(AppServices services, Func<string> profileId, Func<string> theme, Action<string> showInExplorer, bool isPublishedExe) => new()
    {
        Elevated = DataPaths.ProcessIsElevated,
        ExePath = Environment.ProcessPath,
        IsPublishedExe = isPublishedExe,
        ProfileId = profileId,
        Theme = theme,
        ReadPc = () =>
        {
            var os = BuildInfo.Read();
            // The signed-in user's folders, not the elevated account's (over-the-shoulder elevation).
            return new ThisPcFacts(os.Edition, os.NativeArchitecture == CpuArchitecture.Arm64, services.SessionUserName(), MediaLocales.ThisPcRegion(),
                services.SessionDownloadsFolder() ?? services.SessionDocumentsFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        },
        Keyboard = MediaLocales.ThisPcKeyboard,
        ReadIso = new IsoReader().ReadInfoAsync,
        ReadIsoApps = new IsoReader().ReadAppsAsync,
        FreeBytes = FreeBytesOf,
        ShowInExplorer = showInExplorer,
    };

    /// <summary>Microsoft's catalog as the page reads it: work folder on the Windows drive, expand.exe through the app's runner.</summary>
    public static IMediaCatalogSource CatalogSource(AppServices services) =>
        new MediaCatalogSource(new MediaSteps(), services.Context.Processes, Path.GetPathRoot(Environment.SystemDirectory)!);

    /// <summary>
    /// The real build (<see cref="MediaBuild.RunAsync"/>), with its own steps and log for each build. It runs on the thread
    /// pool: Core's awaits would otherwise come back to the window's thread for every block of a 6 GB download and run its
    /// file copies and deletes there. The page's progress still reports on the window's thread.
    /// </summary>
    public static Task<string> BuildAsync(MediaRequest request, IProgress<MediaProgress> progress, CancellationToken ct) =>
        Task.Run(() => new MediaBuild(new MediaSteps()).RunAsync(request, progress, ct), ct);

    /// <summary>
    /// The real cleanup after earlier builds (<see cref="MediaBuild.CleanLeftoversAsync"/>). Without a work folder at a
    /// drive root no build left anything (its mounted image lies inside one), so a start without leftovers runs no DISM.
    /// It runs on the thread pool, never on the window's thread: it may poll DISM through WMI for minutes (waiting for a
    /// DISM a killed build left running) and deletes gigabytes, and Core's awaits would come back to the dispatcher.
    /// </summary>
    public static Task CleanLeftoversAsync(CancellationToken ct) => CleanLeftoversAsync(new MediaSteps(), ct);

    internal static Task CleanLeftoversAsync(IMediaSteps steps, CancellationToken ct) => Task.Run(async () =>
    {
        if (!steps.LocalDrives().Any(d => steps.LeftoverWorkFolders(d).Any())) return;
        await new MediaBuild(steps).CleanLeftoversAsync(ct);
    }, ct);

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

/// <summary>A row with a tick on the page: an app to remove, an app nobody reviewed, or an app to install.</summary>
public abstract partial class MediaPickRow(string key, string title, string text, string? note, bool isChecked) : ObservableObject
{
    /// <summary>The package name or the app catalog id.</summary>
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Text { get; } = text;
    public string? Note { get; } = note;

    [ObservableProperty] private bool _isChecked = isChecked;

    // What screen readers announce for this item in a list.
    public override string ToString() => Title;
}

/// <summary>An app of the reviewed list (appx.json) with its tick for the media.</summary>
public sealed class MediaAppRow(AppxEntry entry, string lang, bool isChecked)
    // Whether the Microsoft Store still offers the app (as on Debloat).
    : MediaPickRow(entry.Name, entry.Label(lang), entry.Text(lang), Loc.Instance[entry.CanReinstall ? "Media_AppStore" : "Media_AppNoStore"], isChecked)
{
    public AppxEntry Entry { get; } = entry;
}

/// <summary>An app of the own ISO's image that the reviewed list does not cover (Expert): only its package name is known.</summary>
public sealed class MediaUnreviewedRow(string name, bool isChecked) : MediaPickRow(name, name, Loc.Instance["Media_UnreviewedRow"], null, isChecked);

/// <summary>A machine-wide app of the app catalog that the first sign-in installs.</summary>
public sealed class MediaInstallRow(Optimizer.Core.Apps.AppEntry app, string lang, bool isChecked) : MediaPickRow(app.Id, app.Name, app.Text(lang), app.Id, isChecked)
{
    public Optimizer.Core.Apps.AppEntry App { get; } = app;
}

/// <summary>One group of a list of rows (the reviewed list as on Debloat, the app catalog by category).</summary>
public sealed partial class MediaAppGroup : ObservableObject
{
    public MediaAppGroup(string key, string title, IReadOnlyList<MediaPickRow> rows)
    {
        Key = key;
        Title = title;
        Rows = rows;
        _isExpanded = key == "consumer";
        foreach (var row in rows)
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(AllChecked));
            };
    }

    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<MediaPickRow> Rows { get; }

    [ObservableProperty] private bool _isExpanded;

    /// <summary>"3 of 18 chosen".</summary>
    public string Summary => Loc.Instance.Format("Media_AppsChosen", Rows.Count(r => r.IsChecked), Rows.Count);

    /// <summary>The group's check box: all ticked, none, or some (null); a click ticks all, the next one none.</summary>
    public bool? AllChecked
    {
        get => Rows.All(r => r.IsChecked) ? true : Rows.Any(r => r.IsChecked) ? null : false;
        set
        {
            foreach (var row in Rows) row.IsChecked = value == true;
        }
    }

    public string AllCheckedLabel => Loc.Instance.Format("Media_AppsGroupAll", Title);

    public override string ToString() => Title;
}

/// <summary>
/// The Windows install media page: where the image comes from, the edition, the changes and the output file, then the
/// build with the banner (stage, percent, elapsed time, Stop). Every choice is picked from a list or checked as typed, an
/// own ISO is read before it can be built, and Build stays off until the choices make a whole request; the engine checks
/// everything again (MediaBuild).
/// </summary>
public sealed partial class InstallMediaViewModel : PageViewModel
{
    private const string Architecture = MediaChoices.Architecture;
    private const string CatalogArchitecture = MediaChoices.CatalogArchitecture;
    private const int FirstWindows11Build = 22000;
    private static readonly string[] GroupOrder = ["consumer", "microsoft", "xbox", "media", "legacy", "thirdParty", "oem"];

    private readonly IDialogs _dialogs;
    private IMediaCatalogSource _catalog;
    private MediaBuilder _build;
    private InstallMediaSystem _system;
    private readonly Func<CancellationToken, Task>? _cleanLeftovers;
    private ThisPcFacts _pc = new(null, false, null, null, "");
    private string? _pcKeyboard;
    private IReadOnlyList<CultureInfo>? _regionCultures;
    private IReadOnlyList<InputProfile> _keyboardProfiles = [];
    private IReadOnlyList<EsdFile>? _files;
    private IsoInfo? _isoInfo;
    private string? _isoProblem;
    private CancellationTokenSource? _isoRead;

    /// <summary>The apps of each own ISO image read so far, by path and index: package names, or null when the read failed.</summary>
    private readonly Dictionary<(string Iso, int Index), IReadOnlyList<string>?> _imageApps = [];
    private (string Iso, int Index)? _appsReading;
    private CancellationTokenSource? _appsRead;

    /// <summary>The ticks by app name, kept while the list changes with the source and edition (and across a language switch).</summary>
    private readonly Dictionary<string, bool> _ticks = new(StringComparer.OrdinalIgnoreCase);
    private bool _outputPicked;
    private string? _defaultOutput;
    private bool _demo, _demoProgress;
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
        UpdateImageApps();
        Changed();
    }

    /// <summary>On an ARM64 PC: Tunero builds x64 media only.</summary>
    public string? ArchitectureNote => _pc.Arm64 ? Loc.Instance["Media_X64Only"] : null;

    [ObservableProperty] private bool _isCheckingCatalog;
    [ObservableProperty] private string? _catalogStep;
    [ObservableProperty] private string? _catalogError;

    /// <summary>"Windows 11, build 26300.9711, x64, download 5.9 GB" for the chosen language and edition.</summary>
    [ObservableProperty] private string? _catalogText;

    public bool HasCatalog => _files is not null;

    [ObservableProperty] private IReadOnlyList<FilterOption> _languages = [];
    [ObservableProperty] private FilterOption? _language;

    partial void OnLanguageChanged(FilterOption? value)
    {
        ShowEditions();
        // The download's file changes with the language: its measured apps may differ.
        if (!_buildingLists) UpdateImageApps();
        Changed();
    }

    [ObservableProperty] private string? _isoPath;

    /// <summary>The own ISO is being read (attached, its image list read, detached).</summary>
    [ObservableProperty] private bool _isReadingIso;

    /// <summary>"Windows 11, build 26300.9711, de-DE, x64" for the chosen image of the own ISO.</summary>
    [ObservableProperty] private string? _isoText;

    /// <summary>The read of the ISO that runs now, or a finished task.</summary>
    public Task IsoLoading { get; private set; } = Task.CompletedTask;

    partial void OnIsoPathChanged(string? value)
    {
        IsoLoading = ReadIsoAsync(value);
        Changed();
    }

    // ---- edition ----

    [ObservableProperty] private IReadOnlyList<FilterOption> _editions = [];
    [ObservableProperty] private FilterOption? _edition;

    /// <summary>The edition last chosen for the download: a look at the own ISO and back keeps it.</summary>
    private string? _downloadEdition;

    partial void OnEditionChanged(FilterOption? value)
    {
        if (UseDownload && value is not null) _downloadEdition = value.Key;
        // While the lists are built, BuildListsCore builds the app list itself.
        if (!_buildingLists) UpdateImageApps();
        Changed();
    }

    // ---- changes ----

    [ObservableProperty] private bool _removeApps = true;
    [ObservableProperty] private IReadOnlyList<MediaAppGroup> _appGroups = [];

    public IReadOnlyList<MediaAppRow> AppRows => AppGroups.SelectMany(g => g.Rows).OfType<MediaAppRow>().ToList();

    partial void OnRemoveAppsChanged(bool value) => Changed();

    /// <summary>The apps of the own ISO's chosen edition are being read (DISM lists the image: a few seconds).</summary>
    [ObservableProperty] private bool _isReadingApps;

    partial void OnIsReadingAppsChanged(bool value) => Changed();

    /// <summary>The read of the image's apps that runs now, or a finished task.</summary>
    public Task AppsLoading { get; private set; } = Task.CompletedTask;

    /// <summary>Where the list comes from: the apps a Microsoft image can hold (the download), or what the own ISO's edition holds.</summary>
    public string AppsSourceText =>
        UseDownload ? MeasuredDownloadApps() is not null ? Loc.Instance.Format("Media_AppsFromDownloadMeasured", AppRows.Count) : Loc.Instance["Media_AppsFromDownload"]
        : IsReadingIso || IsReadingApps ? Loc.Instance["Media_AppsReading"]
        : ImageAppsRead(out var read) ? read is null ? Loc.Instance["Media_AppsIsoFailed"] : Loc.Instance.Format("Media_AppsFromIso", AppRows.Count)
        : Loc.Instance["Media_AppsIsoNotRead"];

    /// <summary>What the folded apps card holds: "18 apps", or off.</summary>
    public string AppsSummary => RemoveApps ? Loc.Instance.Format("Media_AppsSummary", AppRows.Count(r => r.IsChecked)) : Loc.Instance["Media_Off"];

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

    /// <summary>A region brings the keyboard Windows sets up first for it, until the user picked a keyboard.</summary>
    partial void OnRegionChanged(FilterOption? value)
    {
        if (!_buildingLists && !_keyboardPicked && value is not null && Pick(Keyboards, DefaultKeyboard(value.Key)) is { } keyboard)
            SetKeyboard(keyboard);
        Changed();
    }

    partial void OnKeyboardChanged(FilterOption? value)
    {
        if (!_buildingLists && !_settingKeyboard) _keyboardPicked = true;
        Changed();
    }

    private bool _buildingLists, _settingKeyboard, _keyboardPicked;

    /// <summary>Sets the keyboard for the user (from this PC or the region), so it does not count as picked by hand.</summary>
    private void SetKeyboard(FilterOption? keyboard)
    {
        _settingKeyboard = true;
        try
        {
            Keyboard = keyboard;
        }
        finally
        {
            _settingKeyboard = false;
        }
    }

    /// <summary>What the folded setup card holds: "Account Anna, German (Austria)", or off.</summary>
    public string SetupSummary =>
        !SkipSetup ? Loc.Instance["Media_Off"]
        : AccountNameText.Trim() is { Length: > 0 } name ? Loc.Instance.Format("Media_SetupSummary", name, Region?.Text ?? "")
        : Loc.Instance.Format("Media_SetupSummaryNoName", Region?.Text ?? "");

    [ObservableProperty] private bool _applyProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileNote))]
    private bool _canApplyProfile;

    /// <summary>Why the profile cannot go on the media (a build that is not the published exe).</summary>
    public string? ProfileNote => CanApplyProfile ? null : Loc.Instance["Media_ProfileNeedsRelease"];

    [ObservableProperty] private IReadOnlyList<FilterOption> _profiles = [];
    [ObservableProperty] private FilterOption? _profile;

    partial void OnApplyProfileChanged(bool value) => Changed();

    partial void OnProfileChanged(FilterOption? value) => Changed();

    /// <summary>What the folded profile card holds: the profile's name, or off.</summary>
    public string ProfileSummary => ApplyProfile && CanApplyProfile ? Profile?.Text ?? "" : Loc.Instance["Media_Off"];

    // ---- apps to install ----

    /// <summary>The copied Tunero installs the ticked apps with winget at the first sign-in (off when the page opens).</summary>
    [ObservableProperty] private bool _installApps;

    /// <summary>The app catalog's machine-wide apps by category, none ticked when the page opens.</summary>
    [ObservableProperty] private IReadOnlyList<MediaAppGroup> _installGroups = [];

    public IReadOnlyList<MediaInstallRow> InstallRows => InstallGroups.SelectMany(g => g.Rows).OfType<MediaInstallRow>().ToList();

    private readonly HashSet<string> _installTicks = new(StringComparer.Ordinal);

    partial void OnInstallAppsChanged(bool value) => Changed();

    /// <summary>The apps need the Tunero copy, which goes on the media with the profile.</summary>
    public bool CanInstallApps => ApplyProfile && CanApplyProfile;

    public string? InstallNote => CanInstallApps ? null : Loc.Instance["Media_InstallNeedsProfile"];

    /// <summary>What the folded install card holds: "5 apps", or off.</summary>
    public string InstallSummary => InstallApps && CanInstallApps ? Loc.Instance.Format("Media_InstallSummary", InstallRows.Count(r => r.IsChecked)) : Loc.Instance["Media_Off"];

    private void BuildInstallGroups()
    {
        var lang = Lang;
        var open = InstallGroups.Where(g => g.IsExpanded).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        // The catalog lists its apps grouped, in the order the Install page shows them.
        InstallGroups = MediaChoices.InstallableApps(CatalogData.Current.Apps)
            .GroupBy(a => a.Category)
            .Select(g => new MediaAppGroup(g.Key, Labels.Current.Get(lang, $"appCategory.{g.Key}"),
                g.Select(a => new MediaInstallRow(a, lang, _installTicks.Contains(a.Id))).ToList())
            {
                IsExpanded = open.Contains(g.Key),
            })
            .ToList();
        foreach (var row in InstallRows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MediaPickRow.IsChecked)) return;
                if (row.IsChecked) _installTicks.Add(row.Key);
                else _installTicks.Remove(row.Key);
                OnPropertyChanged(nameof(InstallSummary));
                OnPropertyChanged(nameof(SummaryText));
            };
        OnPropertyChanged(nameof(InstallRows));
    }

    // ---- expert ----

    [ObservableProperty] private bool _allowUnsupported;

    partial void OnAllowUnsupportedChanged(bool value) => Changed();

    /// <summary>Expert: also remove apps of the own ISO's image that the reviewed list does not cover (off, none ticked).</summary>
    [ObservableProperty] private bool _removeUnreviewed;

    partial void OnRemoveUnreviewedChanged(bool value) => Changed();

    [ObservableProperty] private IReadOnlyList<MediaUnreviewedRow> _unreviewedRows = [];

    private readonly HashSet<string> _unreviewedTicks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only an own ISO's image is read before the build: the download's apps are known only while it builds.</summary>
    public string UnreviewedSourceText =>
        UseDownload ? MeasuredDownloadApps() is not null ? Loc.Instance["Media_UnreviewedNoneDownload"] : Loc.Instance["Media_UnreviewedDownload"]
        : IsReadingIso || IsReadingApps ? Loc.Instance["Media_AppsReading"]
        : ImageAppsRead(out var read) ? read is null ? Loc.Instance["Media_AppsIsoFailed"]
            : UnreviewedRows.Count == 0 ? Loc.Instance["Media_UnreviewedNone"] : Loc.Instance.Format("Media_UnreviewedFound", UnreviewedRows.Count)
        : Loc.Instance["Media_AppsIsoNotRead"];

    public string UnreviewedSummary => RemoveUnreviewed && !UseDownload ? Loc.Instance.Format("Media_AppsSummary", UnreviewedRows.Count(r => r.IsChecked)) : Loc.Instance["Media_Off"];

    private void BuildUnreviewedRows()
    {
        var apps = !UseDownload && ImageAppsRead(out var read) && read is not null ? MediaChoices.UnreviewedApps(CatalogData.Current.Appx, read) : [];
        UnreviewedRows = apps.Order(StringComparer.OrdinalIgnoreCase).Select(name => new MediaUnreviewedRow(name, _unreviewedTicks.Contains(name))).ToList();
        foreach (var row in UnreviewedRows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MediaPickRow.IsChecked)) return;
                if (row.IsChecked) _unreviewedTicks.Add(row.Key);
                else _unreviewedTicks.Remove(row.Key);
                OnPropertyChanged(nameof(UnreviewedSummary));
                OnPropertyChanged(nameof(SummaryText));
            };
    }

    /// <summary>The change cards start folded; the demo can unfold them for screenshots.</summary>
    [ObservableProperty] private bool _expandOptions;

    // ---- build ----

    [ObservableProperty] private string? _outputPath;

    /// <summary>A path the page did not choose itself (the save dialog) stays: it is the user's.</summary>
    partial void OnOutputPathChanged(string? value)
    {
        if (value != _defaultOutput) _outputPicked = true;
        Changed();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBuilding;

    partial void OnIsBuildingChanged(bool value) => Changed();

    /// <summary>The choices stay as they are while a build runs: it uses the ones it started with (and may hold the own ISO).</summary>
    public bool CanEdit => !IsBuilding;

    /// <summary>The space the build needs on the output drive and what is free there.</summary>
    public string? NeededText
    {
        get
        {
            if (Needed() is not { } needed) return null;
            return OutputPath is { } output && MediaChoices.IsLocalOutput(output) && _system.FreeBytes(output) is { } free
                ? Loc.Instance.Format("Media_Needed", ByteSize.Format(needed), ByteSize.Format(free))
                : Loc.Instance.Format("Media_NeededOnly", ByteSize.Format(needed));
        }
    }

    /// <summary>Why Build is off (the first choice that is missing or wrong), or null when it can start.</summary>
    public string? BuildNote => Problem() is { } key ? Loc.Instance[key] : IsCleaning ? Loc.Instance["Media_CleanupRunning"] : null;

    /// <summary>
    /// The bar at the bottom of the page: the image and what goes into it, in one line (<see cref="InstallMediaSummary.Bar"/>).
    /// Before there is an image to build from it says what is missing.
    /// </summary>
    public string SummaryText => Choices() is { } choices
        ? InstallMediaSummary.Bar(choices, Edition!.Text, BuildLabel(), Needed() ?? 0)
        : Loc.Instance["Media_NeedsCatalog"];

    /// <summary>The build of the chosen image as the page names it: the download's "26300.9711", or the own ISO's.</summary>
    private string? BuildLabel() =>
        UseDownload ? Download()?.Build : SelectedImage() is { } image ? BuildText(image) : null;

    /// <summary>The start cleanup of earlier builds is still running (a build waits for it).</summary>
    private bool IsCleaning => _cleanup is { IsCompleted: false };

    /// <summary>The page was shown: the free space may have changed since (one drive query).</summary>
    public void PageShown()
    {
        if (WasLoaded) RefreshBuild();
    }

    private void RefreshBuild()
    {
        OnPropertyChanged(nameof(NeededText));
        OnPropertyChanged(nameof(BuildNote));
        OnPropertyChanged(nameof(CanBuild));
        BuildCommand.NotifyCanExecuteChanged();
    }

    public bool CanBuild => Problem() is null;

    // The result of the last build, shown on the Build card (and in the window's result bar on another page).
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private InfoBarSeverity _resultSeverity;
    [ObservableProperty] private string? _resultLogPath;
    [ObservableProperty] private string? _resultIso;

    public bool HasResult => ResultText is not null;

    // ---- stages of the running build ----

    /// <summary>The stages of the last build (<see cref="MediaBuild.PlannedStages"/>) with their state; empty until the first Build.</summary>
    public ObservableCollection<MediaStageRow> Stages { get; } = [];

    [ObservableProperty] private bool _hasStages;

    /// <summary>"Stage 3 of 8, 12:40" while a build runs.</summary>
    [ObservableProperty] private string? _stagesText;

    /// <summary>The list was filled for a new build: the page scrolls it into view.</summary>
    public event EventHandler? StagesShown;

    private MediaStageRow? _currentStage;
    private bool _stagesRunning;
    private readonly Stopwatch _stageClock = new();

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    private void Changed()
    {
        OnPropertyChanged(nameof(AccountProblem));
        OnPropertyChanged(nameof(NeededText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(BuildNote));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(AppsSummary));
        OnPropertyChanged(nameof(AppsSourceText));
        OnPropertyChanged(nameof(SetupSummary));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(CanInstallApps));
        OnPropertyChanged(nameof(InstallNote));
        OnPropertyChanged(nameof(InstallSummary));
        OnPropertyChanged(nameof(UnreviewedSourceText));
        OnPropertyChanged(nameof(UnreviewedSummary));
        UpdateCatalogText();
        UpdateIsoText();
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
        else
        {
            if (IsoPath is not { } iso || (!_demo && !File.Exists(iso))) return "Media_NeedsIso";
            if (IsReadingIso || IsReadingApps) return "Media_ReadingIso";
            if (_isoProblem is { } problem) return problem;
            // An install image without a readable image list: the read failed, nothing says the media is wrong.
            if (_isoInfo is { ImageFile: not null, Images.Count: 0 }) return "Media_IsoReadFailed";
            if (_isoInfo is not { ImageFile: not null, HasBootWim: true } info || info.Editions.Count == 0) return "Media_NotWindows11";
        }
        if (Edition is null) return "Media_NoImage";
        if (!UseDownload && SelectedImage() is { } image)
        {
            if (!string.Equals(image.Architecture, CatalogArchitecture, StringComparison.OrdinalIgnoreCase)) return "Media_NotX64";
            if (image.Build is not >= FirstWindows11Build) return "Media_NotWindows11";
        }
        if (SkipSetup && AccountName.Problem(AccountNameText.Trim()) is { } name) return name;
        if (SkipSetup && (Region is null || Keyboard is null)) return "Media_NeedsRegion";
        if (OutputPath is not { } output) return "Media_OutputInvalid";
        // A network share, a \\?\ path or a removable drive is refused by its spelling and drive type, before anything
        // asks the share whether the folder exists (the build checks it again).
        if (Path.IsPathFullyQualified(output) && !MediaChoices.IsLocalOutput(output)) return "Media_OutputNotLocal";
        if (!ValidOutput(output)) return "Media_OutputInvalid";
        if (!UseDownload && IsoPath is { } source && SamePath(source, output)) return "Media_OutputInvalid";
        // While the start cleanup runs, a leftover may hold the space it is about to free; the build waits for the cleanup and
        // checks the space again (MediaBuild.CheckOutputAndSpace) before it writes anything.
        if (!IsCleaning && Needed() is { } needed && _system.FreeBytes(output) is { } free && free < needed) return "Media_NotEnoughSpace";
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

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private EsdFile? Download() =>
        _files is { } files && Language is { } language && Edition is { } edition
            ? MediaCatalog.Pick(files, language.Key, CatalogArchitecture, edition.Key)
            : null;

    /// <summary>The image of the own ISO the edition list points to (its key is the image index).</summary>
    private WimImageInfo? SelectedImage() =>
        _isoInfo is { } info && Edition is { } edition ? info.Editions.FirstOrDefault(i => i.Index.ToString(CultureInfo.InvariantCulture) == edition.Key) : null;

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

    /// <summary>
    /// Reads this PC's facts, the regions and the keyboards off the UI thread (the current keyboard on it), then builds the
    /// lists. A catalog or ISO read earlier is shown again in the current language.
    /// </summary>
    protected override async Task LoadAsync()
    {
        _pcKeyboard = _system.Keyboard();
        var system = _system;
        (_pc, _regionCultures, _keyboardProfiles) = await Task.Run(() => (system.ReadPc(), MediaLocales.Regions(), system.Keyboards()));
        if (AccountNameText.Length == 0 && _pc.UserName is { } user && AccountName.Problem(user) is null) AccountNameText = user;
        BuildLists();
        if (_files is not null) ShowCatalog();
        OnPropertyChanged(nameof(ArchitectureNote));
        OnPropertyChanged(nameof(ProfileNote));
        if (_demo && _files is null) await CheckCatalogAsync();
        // --media-demo progress: the build starts by itself and stops in the middle of Apps.
        if (_demoProgress && !IsBuilding && Request() is { } request) _running = RunBuildAsync(request);
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

    /// <summary>The lists in the current language; every choice keeps its key.</summary>
    private void BuildLists()
    {
        _buildingLists = true;
        try
        {
            BuildListsCore();
        }
        finally
        {
            _buildingLists = false;
        }
        Changed();
    }

    private void BuildListsCore()
    {
        var lang = Lang;
        var regionKey = Region?.Key ?? _pc.Region ?? "en-US";
        Regions = _regionCultures!.Select(c => new FilterOption(c.Name, c.DisplayName)).OrderBy(o => o.Text, StringComparer.CurrentCulture).ToList();
        Region = Regions.FirstOrDefault(o => o.Key.Equals(regionKey, StringComparison.OrdinalIgnoreCase))
                 ?? Regions.FirstOrDefault(o => o.Key == "en-US");

        Keyboards = _keyboardProfiles.Select(k => new FilterOption(k.Code, $"{k.Keyboard} ({k.LanguageTag})"))
            .OrderBy(o => o.Text, StringComparer.CurrentCulture).ToList();
        var keyboardKey = Keyboard?.Key ?? _pcKeyboard;
        Keyboard = Pick(Keyboards, keyboardKey)
                   ?? Pick(Keyboards, Region is { } r ? DefaultKeyboard(r.Key) : null)
                   ?? Pick(Keyboards, "0409:00000409")
                   ?? Keyboards.FirstOrDefault();

        var profileKey = Profile?.Key ?? _system.ProfileId();
        Profiles = CatalogData.Current.Profiles.Profiles.Select(p => new FilterOption(p.Id, Loc.Instance[$"Profile_{p.Id}"])).ToList();
        Profile = Profiles.FirstOrDefault(o => o.Key == profileKey) ?? Profiles.FirstOrDefault();

        ShowEditions();
        UpdateImageApps();
        BuildInstallGroups();
    }

    /// <summary>
    /// The app list for the source: for the download the apps a Microsoft image can hold (its image is read only in the
    /// build, which skips what it lacks), for an own ISO the apps its chosen edition holds once they are read. The groups
    /// stay open or closed as the user left them.
    /// </summary>
    private void BuildAppGroups()
    {
        var lang = Lang;
        var open = AppGroups.Where(g => g.IsExpanded).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var first = AppGroups.Count == 0;
        var apps = UseDownload ? MeasuredDownloadApps() : ImageAppsRead(out var read) ? read : null;
        AppGroups = MediaChoices.PageApps(CatalogData.Current.Appx, apps)
            .GroupBy(a => a.Group)
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var i and >= 0 ? i : GroupOrder.Length)
            .Select(g => new MediaAppGroup(g.Key, Labels.Current.Get(lang, $"debloatGroup.{g.Key}"),
                g.OrderBy(a => a.Label(lang), StringComparer.CurrentCulture)
                 .Select(a => new MediaAppRow(a, lang, _ticks.TryGetValue(a.Name, out var ticked) ? ticked : MediaChoices.ChosenByDefault(a))).ToList())
            {
                IsExpanded = first ? g.Key == "consumer" : open.Contains(g.Key),
            })
            .ToList();
        foreach (var row in AppRows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MediaAppRow.IsChecked)) return;
                _ticks[row.Entry.Name] = row.IsChecked;
                OnPropertyChanged(nameof(AppsSummary));
                OnPropertyChanged(nameof(SummaryText));
            };
        OnPropertyChanged(nameof(AppRows));
        BuildUnreviewedRows();
        Changed();
    }

    /// <summary>The apps the chosen download holds when its build and edition were measured (mediaimages.json), else null.</summary>
    private IReadOnlyList<string>? MeasuredDownloadApps() =>
        Download() is { } esd ? MediaChoices.MeasuredApps(CatalogData.Current.MediaImages, esd) : null;

    /// <summary>Whether the apps of the own ISO's chosen image were read; <paramref name="apps"/> is null when that read failed.</summary>
    private bool ImageAppsRead(out IReadOnlyList<string>? apps)
    {
        apps = null;
        return IsoPath is { } iso && SelectedImage() is { } image && _imageApps.TryGetValue((iso, image.Index), out apps);
    }

    /// <summary>The source or the edition changed: the list follows, reading the own ISO's image when it was not read yet.</summary>
    private void UpdateImageApps()
    {
        if (!UseDownload && IsoPath is { } iso && SelectedImage() is { } image && !ImageAppsRead(out _))
        {
            if (_appsReading != (iso, image.Index)) AppsLoading = ReadAppsAsync(iso, image.Index);
            return;
        }
        // A read that still runs is no longer wanted; coming back to that image starts a new one.
        _appsRead?.Cancel();
        _appsRead = null;
        _appsReading = null;
        IsReadingApps = false;
        BuildAppGroups();
    }

    /// <summary>
    /// Lists the chosen image of the own ISO with DISM and keeps its apps; a failed read leaves the list of what a
    /// Microsoft image can hold (the build skips what the image lacks). A newer choice replaces a read that still runs.
    /// </summary>
    private async Task ReadAppsAsync(string iso, int index)
    {
        _appsRead?.Cancel();
        if (!_system.Elevated && !_demo) return;
        using var read = new CancellationTokenSource();
        _appsRead = read;
        _appsReading = (iso, index);
        IsReadingApps = true;
        IReadOnlyList<string>? apps = null;
        try
        {
            apps = await _system.ReadIsoApps(iso, index, read.Token);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The list of what a Microsoft image can hold stays; the build skips what this image lacks. The log has the cause.
            Log.Warn("media", $"apps of the own ISO could not be read: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_appsRead == read)
            {
                _appsRead = null;
                _appsReading = null;
                IsReadingApps = false;
            }
        }
        if (read.IsCancellationRequested) return;
        _imageApps[(iso, index)] = apps;
        BuildAppGroups();
    }

    private static FilterOption? Pick(IEnumerable<FilterOption> options, string? key) =>
        key is null ? null : options.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string? DefaultKeyboard(string region)
    {
        try
        {
            return MediaLocales.DefaultKeyboard(CultureInfo.GetCultureInfo(region));
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The editions of the chosen download, or the Windows images the own ISO holds (keyed by image index). Default: this
    /// PC's edition when offered, else Pro.
    /// </summary>
    private void ShowEditions()
    {
        var key = Edition?.Key;
        if (UseDownload)
        {
            var keys = _files is { } files && Language is { } language ? MediaCatalog.Editions(files, language.Key, CatalogArchitecture) : MediaCatalog.OfferedEditions;
            Editions = keys.Select(e => new FilterOption(e, EditionName(e, e))).ToList();
            Edition = Pick(Editions, key) ?? Pick(Editions, _downloadEdition) ?? Pick(Editions, _pc.Edition) ?? Pick(Editions, "Professional") ?? Editions.FirstOrDefault();
            return;
        }
        var images = _isoInfo?.Editions ?? [];
        Editions = images.Select(i => new FilterOption(i.Index.ToString(CultureInfo.InvariantCulture), EditionName(i.Edition, i.Name))).ToList();
        string? IndexOf(string? edition) => images.FirstOrDefault(i => string.Equals(i.Edition, edition, StringComparison.OrdinalIgnoreCase))?.Index.ToString(CultureInfo.InvariantCulture);
        Edition = Pick(Editions, key) ?? Pick(Editions, IndexOf(_pc.Edition)) ?? Pick(Editions, IndexOf("Professional")) ?? Editions.FirstOrDefault();
    }

    /// <summary>"Windows 11 Pro" for an edition the page knows, else the image's own name.</summary>
    private static string EditionName(string? edition, string fallback) =>
        edition is not null && MediaCatalog.OfferedEditions.Contains(edition) ? Loc.Instance[$"Media_Edition_{edition}"] : fallback;

    /// <summary>The app language's Windows language: de-de for German, en-us otherwise.</summary>
    private string DefaultLanguage => MediaChoices.WindowsLanguage(Lang);

    private void ShowCatalog()
    {
        var key = Language?.Key ?? DefaultLanguage;
        Languages = _files!.Where(f => f.IsConsumer && f.Architecture.Equals(CatalogArchitecture, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FilterOption(g.Key, LanguageName(g.Key, g.First().Language)))
            .OrderBy(o => o.Text, StringComparer.CurrentCulture)
            .ToList();
        Language = Pick(Languages, key) ?? Pick(Languages, "en-us") ?? Languages.FirstOrDefault();
        OnPropertyChanged(nameof(HasCatalog));
        ShowEditions();
        // Now the download is known: the list follows what its image holds where that was measured.
        UpdateImageApps();
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

    /// <summary>
    /// "Windows 11, build 26300.9711, de-DE, x64" for the chosen image; an image below Windows 11's first build is only
    /// "Windows, build 19045.1, ..." (Build then names why it is refused).
    /// </summary>
    private void UpdateIsoText() =>
        IsoText = !UseDownload && (SelectedImage() ?? _isoInfo?.Editions.FirstOrDefault()) is { } image
            ? Loc.Instance.Format(image.Build is >= FirstWindows11Build ? "Media_IsoFound" : "Media_IsoFacts",
                BuildText(image), image.DefaultLanguage ?? image.Languages.FirstOrDefault() ?? "?", image.Architecture ?? "?")
            : null;

    /// <summary>"26300.9711" from the version 10.0.26300.9711, as the catalog names a build.</summary>
    private static string BuildText(WimImageInfo image) =>
        image.Version?.Split('.') is { Length: >= 3 } parts ? string.Join('.', parts.Skip(2)) : image.Build?.ToString(CultureInfo.InvariantCulture) ?? "?";

    /// <summary>
    /// Windows11_26300.9711_de-de_Professional_Tunero.iso in Downloads, until the user picks a file. A name that is taken
    /// gets " (2)" and so on: only a file the user picked in the save dialog (which asked) may be replaced.
    /// </summary>
    private void UpdateOutputName()
    {
        if (_outputPicked || _pc.DownloadsFolder.Length == 0) return;
        // An own ISO's edition comes from its image list, which anyone can write: only letters and digits go into the name.
        var edition = UseDownload ? Edition?.Key ?? "Professional" : SelectedImage()?.Edition ?? "Windows";
        edition = string.Concat(edition.Where(char.IsAsciiLetterOrDigit)) is { Length: > 0 } clean ? clean : "Windows";
        var stem = UseDownload && Download() is { } esd
            ? $"Windows11_{esd.Build}_{esd.LanguageCode}_{edition}_Tunero"
            : $"Windows11_{edition}_Tunero";
        var path = FreeName(_pc.DownloadsFolder, stem);
        if (path == OutputPath) return;
        _defaultOutput = path;
        OutputPath = path;
    }

    private static string FreeName(string folder, string stem)
    {
        var path = Path.Combine(folder, stem + ".iso");
        for (var n = 2; File.Exists(path) && n < 100; n++) path = Path.Combine(folder, $"{stem} ({n}).iso");
        return path;
    }

    // ---- own ISO ----

    /// <summary>
    /// Reads what the own ISO holds (attaches it read-only, reads the install image's list, detaches). Without
    /// administrator rights nothing is attached: Build says so anyway. A newer pick replaces a read that still runs.
    /// </summary>
    private async Task ReadIsoAsync(string? path)
    {
        _isoRead?.Cancel();
        _isoInfo = null;
        _isoProblem = null;
        // Picked again (perhaps changed since): its images are read again, their apps too.
        foreach (var key in _imageApps.Keys.Where(k => k.Iso == path).ToList()) _imageApps.Remove(key);
        // A new ISO starts from this PC's edition: an image index means nothing in another ISO.
        Edition = null;
        ShowEditions();
        if (path is null || (!_system.Elevated && !_demo)) return;
        using var read = new CancellationTokenSource();
        _isoRead = read;
        IsReadingIso = true;
        Changed();
        IsoInfo? info = null;
        string? problem = null;
        try
        {
            info = await _system.ReadIso(path, read.Token);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            return;
        }
        catch (IsoAttachException ex)
        {
            Log.Warn("media", $"own ISO could not be read: {ex.Message}");
            problem = ex.InUse ? "Media_IsoInUse" : "Media_IsoReadFailed";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever else went wrong, the read failed: the page says so (never "not Windows 11"), the log has the cause.
            Log.Error("media", "own ISO could not be read", ex);
            problem = "Media_IsoReadFailed";
        }
        finally
        {
            if (_isoRead == read)
            {
                _isoRead = null;
                IsReadingIso = false;
            }
        }
        if (read.IsCancellationRequested) return;
        _isoInfo = info;
        _isoProblem = problem;
        ShowEditions();
        Changed();
    }

    partial void OnIsReadingIsoChanged(bool value) => Changed();

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A timeout's TaskCanceledException included: nothing here is cancelled by the user.
            // The message is for the log: the page names no step it cannot know.
            Log.Warn("media", $"catalog failed: {ex.GetType().Name}: {ex.Message}");
            CatalogError = Loc.Instance.Format("Media_CatalogFailed", Loc.Instance["Media_CatalogStepUnknown"]);
        }
        finally
        {
            CatalogStep = null;
            IsCheckingCatalog = false;
        }
    }

    /// <summary>Picks an ISO and reads it; picking the same file again reads it again (after it was ejected in Explorer).</summary>
    [RelayCommand]
    private void PickIso()
    {
        if (_dialogs.PickOpenFile(Loc.Instance["Media_IsoFiles"], ".iso") is not { } path) return;
        UseDownload = false;
        if (string.Equals(path, IsoPath, StringComparison.OrdinalIgnoreCase))
        {
            IsoLoading = ReadIsoAsync(path);
            Changed();
        }
        else
        {
            IsoPath = path;
        }
    }

    [RelayCommand]
    private void PickOutput()
    {
        var current = OutputPath;
        var folder = current is not null && Path.GetDirectoryName(current) is { } f && Directory.Exists(f) ? f : _pc.DownloadsFolder;
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

    /// <summary>
    /// The request from the page's choices (the mapping --build-media shares, <see cref="MediaChoices.ToRequest"/>); null
    /// when they do not make one (Build is off then).
    /// </summary>
    private MediaRequest? Request() => Problem() is null ? Choices()?.ToRequest() : null;

    /// <summary>The page's choices as they stand, whether or not Build is on; null without a source and an edition.</summary>
    private MediaChoices? Choices()
    {
        if (Source() is not { } source || Edition is null) return null;
        var image = UseDownload ? null : SelectedImage();
        var tunero = ApplyProfile && CanApplyProfile && _system.ExePath is { } exe
            ? new TuneroCopy(exe, Profile?.Key ?? _system.ProfileId(), _system.Theme())
            : null;
        return new MediaChoices(source, image is not null ? image.Edition ?? image.Name : Edition.Key, image?.Index,
            image?.DefaultLanguage ?? image?.Languages.FirstOrDefault(), Lang,
            RemoveApps ? AppRows.Where(r => r.IsChecked).Select(r => r.Entry.Name).ToList() : [],
            SkipSetup, AccountNameText, Region?.Key, Keyboard?.Key, AllowUnsupported, tunero, OutputPath!)
        {
            RemoveUnreviewed = RemoveUnreviewed && !UseDownload ? UnreviewedRows.Where(r => r.IsChecked).Select(r => r.Key).ToList() : [],
            InstallApps = InstallApps && tunero is not null ? InstallRows.Where(r => r.IsChecked).Select(r => r.Key).ToList() : [],
        };
    }

    /// <summary>The question before the build (what goes into the ISO); null when the choices do not make a request. Also rendered by --media-demo review.</summary>
    internal ConfirmRequest? ReviewRequest() =>
        Problem() is null && Choices() is { } choices ? InstallMediaSummary.Review(choices, Edition!.Text, BuildLabel(), Needed() ?? 0) : null;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private Task BuildAsync()
    {
        if (Request() is not { } request || ReviewRequest() is not { } review) return Task.CompletedTask;
        // The review shows every choice once more; cancelling it builds nothing.
        if (_dialogs.ConfirmApply(review) is null) return Task.CompletedTask;
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
        StartStages(MediaBuild.PlannedStages(request));
        string? iso = null;
        Exception? failure = null;
        var stopped = false;
        try
        {
            await RunWorkAsync("install media", Loc.Instance["Media_Building"], async scope =>
            {
                // The banner: the stage, its percentage, and the time the build has taken so far (refreshed every second).
                var clock = Stopwatch.StartNew();
                (double? Percent, string Stage) last = (null, Loc.Instance["Media_Building"]);
                void Show()
                {
                    scope.Report(last.Percent, Loc.Instance.Format("Media_Progress", last.Stage, Elapsed(clock.Elapsed)));
                    ShowStageClock(clock.Elapsed);
                }
                var progress = new WindowProgress(p =>
                {
                    ApplyProgress(p);
                    last = (p.Percent, StageText(p));
                    Show();
                });
                using var ticking = new CancellationTokenSource();
                var ticker = TickAsync(Show, ticking.Token);
                try
                {
                    // A cleanup of earlier builds finishes first: the two never run together. It can wait minutes for a DISM
                    // that a killed build left running, so the banner says it cleans up; Stop ends the build here and the
                    // cleanup goes on by itself.
                    if (_cleanup is { IsCompleted: false } cleanup)
                    {
                        last = (null, Loc.Instance["Media_Stage_Cleanup"]);
                        Show();
                        await cleanup.WaitAsync(stop.Token);
                        last = (null, Loc.Instance["Media_Building"]);
                        clock.Restart();
                    }
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
                finally
                {
                    ticking.Cancel();
                    await ticker;
                }
            }, stop, duration: Durations.HalfHour);
        }
        finally
        {
            _stop = null;
            IsBuilding = false;
        }

        FinishStages(iso is not null, stopped, failure);
        if (iso is not null) ShowSuccess(iso);
        else if (failure is not null) ShowFailure(failure);
        else if (stopped) Show(Loc.Instance["Media_Stopped"], InfoBarSeverity.Informational);
    }

    /// <summary>
    /// <see cref="Progress{T}"/> for the window: reports reach the handler on the window's thread, in the order they were
    /// made. Without a window (a test) the handler runs where the report is made, so nothing depends on a thread pool.
    /// </summary>
    private sealed class WindowProgress(Action<MediaProgress> handler) : IProgress<MediaProgress>
    {
        private readonly SynchronizationContext? _window = SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext ? SynchronizationContext.Current : null;

        public void Report(MediaProgress value)
        {
            if (_window is { } window) window.Post(_ => handler(value), null);
            else handler(value);
        }
    }

    private void StartStages(IReadOnlyList<MediaStage> stages)
    {
        Stages.Clear();
        foreach (var stage in stages) Stages.Add(new MediaStageRow(stage));
        HasStages = true;
        StagesText = null;
        _currentStage = null;
        _stagesRunning = true;
        StagesShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A stage begins (the one before is done) or reports its percent and what it is working on.</summary>
    private void ApplyProgress(MediaProgress p)
    {
        if (!_stagesRunning) return;
        // Cleanup is no row: the engine reports it after a success but also before it rethrows a failure or Stop, so
        // FinishStages, which knows how the build ended, closes the running row.
        if (p.Stage == MediaStage.Cleanup) return;
        var index = Stages.ToList().FindIndex(s => s.Stage == p.Stage);
        if (index < 0) return;
        var row = Stages[index];
        if (_currentStage != row)
        {
            if (_currentStage is not null && Stages.IndexOf(_currentStage) > index) return;
            CloseCurrent(MediaStageState.Done);
            foreach (var earlier in Stages.Take(index).Where(s => s.State == MediaStageState.Waiting)) earlier.State = MediaStageState.Done;
            _currentStage = row;
            _stageClock.Restart();
            row.State = MediaStageState.Running;
        }
        if (p.Percent is null && p.Detail is null)
        {
            row.Detail = null;
        }
        else if (p.Detail is not null)
        {
            row.Detail = p.Detail;
        }
        row.Percent = p.Percent;
    }

    private void CloseCurrent(MediaStageState state)
    {
        if (_currentStage is not { } row) return;
        _currentStage = null;
        row.Duration = _stageClock.Elapsed;
        row.Percent = null;
        if (state == MediaStageState.Done) row.Detail = null;
        row.State = state;
    }

    /// <summary>Once a second: the running stage's time and "Stage 3 of 8, 12:40".</summary>
    private void ShowStageClock(TimeSpan elapsed)
    {
        if (!_stagesRunning || _currentStage is not { } row) return;
        row.Duration = _stageClock.Elapsed;
        StagesText = Loc.Instance.Format("Media_StagesCount", Stages.IndexOf(row) + 1, Stages.Count, Elapsed(elapsed));
    }

    /// <summary>The build ended: the running stage is done, stopped or failed (with the reason), the stages after it did not run.</summary>
    private void FinishStages(bool succeeded, bool stopped, Exception? failure)
    {
        _stagesRunning = false;
        StagesText = null;
        if (succeeded) CloseCurrent(MediaStageState.Done);
        else if (stopped) CloseCurrent(MediaStageState.Stopped);
        else if (failure is not null)
        {
            var row = _currentStage ?? (failure is MediaBuildException m ? Stages.FirstOrDefault(s => s.Stage == m.Stage) : null);
            if (row is not null)
            {
                var detail = FailureDetail(failure);
                if (row == _currentStage) CloseCurrent(MediaStageState.Failed);
                row.State = MediaStageState.Failed;
                row.Detail = detail;
            }
        }
        foreach (var row in Stages.Where(s => s.State == MediaStageState.Waiting)) row.State = MediaStageState.NotRun;
    }

    /// <summary>The reason in words with DISM's or Windows' error code, as the result line says it (without the stage).</summary>
    private static string FailureDetail(Exception ex)
    {
        if (ex is not MediaBuildException m) return Loc.Instance.Format("Media_FailedRequest", ex.Message);
        var text = Reason(m);
        return m.Code is { } code ? text + " " + Loc.Instance.Format("Media_ErrorCode", CodeText(code)) : text;
    }

    private static string Reason(MediaBuildException m) =>
        m.Key is { } key ? Loc.Instance[key] : m.InUse ? Loc.Instance["Media_IsoInUse"] : Loc.Instance["Media_FailedUnknown"];

    private static async Task TickAsync(Action show, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                show();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>"12:05" or "1:02:05".</summary>
    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

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
        var text = Loc.Instance.Format("Media_Failed", Loc.Instance[$"Media_Stage_{m.Stage}"], Reason(m));
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
        var cleanup = _cleanup = Clean(_cleanLeftovers);
        RefreshBuild();
        return Finish(cleanup);

        // Resumes on the window's thread (called from there): the free space and the note are read again.
        async Task Finish(Task cleanup)
        {
            await cleanup;
            RefreshBuild();
        }

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

    /// <summary>The stage list of a finished sample build: all done, or stopped by <paramref name="failure"/> (the stages after it did not run).</summary>
    private void DemoStages(MediaBuildException? failure)
    {
        var minutes = new[] { 21, 1, 12, 3, 2, 1, 4, 1 };
        Stages.Clear();
        var failed = false;
        foreach (var (stage, i) in new[] { MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Apps, MediaStage.Shrink, MediaStage.Files, MediaStage.Iso, MediaStage.Verify }.Select((s, i) => (s, i)))
        {
            var row = new MediaStageRow(stage);
            if (failed) row.State = MediaStageState.NotRun;
            else if (failure is not null && failure.Stage == stage)
            {
                row.State = MediaStageState.Failed;
                row.Detail = FailureDetail(failure);
                row.Duration = TimeSpan.FromMinutes(minutes[i]) + TimeSpan.FromSeconds(7);
                failed = true;
            }
            else
            {
                row.State = MediaStageState.Done;
                row.Duration = TimeSpan.FromMinutes(minutes[i]) + TimeSpan.FromSeconds(7 * (i + 1));
            }
            Stages.Add(row);
        }
        HasStages = true;
    }

    /// <summary>
    /// Developer aid: the sample catalog instead of Microsoft's, a sample own ISO, and a build that only shows its stages,
    /// then a sample failure and the next time a sample success. Nothing goes to the network, attaches or runs.
    /// <paramref name="show"/>, comma-separated: "fail" or "done" shows that result at once, "expand" unfolds the change
    /// cards, "progress" starts a build that stays in the middle of Apps (the stage list mid-build), "review" turns on the Expert option that warns in the review, "iso" chooses the sample own ISO, "umlaut" types an account name Setup cannot create, "cleanup" shows the Build card while the start cleanup
    /// runs (for screenshots).
    /// </summary>
    public void UseDemo(string? show)
    {
        var shows = (show ?? "").Split(',', StringSplitOptions.TrimEntries);
        ExpandOptions = shows.Contains("expand");
        if (ExpandOptions)
        {
            // The install card with two apps ticked, as a filled-in example (the lists are built when the page loads).
            InstallApps = true;
            _installTicks.UnionWith(["Valve.Steam", "Mozilla.Firefox"]);
        }
        // "review": the choices a review has something to warn about (the review itself is shown by --confirm-shot).
        AllowUnsupported = shows.Contains("review");
        _demo = true;
        _catalog = new InstallMediaDemo.Catalog();
        _demoProgress = shows.Contains("progress");
        var demo = new InstallMediaDemo.Build { Freeze = _demoProgress };
        _build = demo.RunAsync;
        _system = _system with
        {
            Elevated = true, ExePath = _system.ExePath ?? "Tunero.exe", IsPublishedExe = true,
            ReadIso = (_, _) => Task.FromResult(InstallMediaDemo.Iso()),
            ReadIsoApps = (_, _, _) => Task.FromResult(InstallMediaDemo.IsoApps()),
        };
        CanApplyProfile = true;
        ApplyProfile = true;
        AccountNameText = shows.Contains("umlaut") ? "Jürgen" : "Anna";
        if (shows.Contains("iso"))
        {
            UseDownload = false;
            IsoPath = Path.Combine(Path.GetTempPath(), "Win11_26H2_German_x64.iso");
        }
        if (shows.Contains("cleanup")) _cleanup = new TaskCompletionSource().Task;   // never ends: the card shows the running cleanup
        if (shows.Contains("fail"))
        {
            ShowFailure(InstallMediaDemo.Failure(), window: false);
            DemoStages(InstallMediaDemo.Failure());
        }
        else if (shows.Contains("done"))
        {
            DemoStages(null);
            ResultIso = Path.Combine(Path.GetTempPath(), "Windows11_Tunero.iso");
            Show(Loc.Instance.Format("Media_Done", ResultIso, ByteSize.Format(5_712_000_000)), InfoBarSeverity.Success, window: false);
        }
    }
}
