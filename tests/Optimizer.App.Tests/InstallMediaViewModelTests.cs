using System.Globalization;
using System.IO;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The install media page without the window: a fake catalog, a fake ISO reader, a build that only records the request
/// (or fails as told), fixed keyboards, and a temp folder for the files the page checks. Nothing downloads, attaches an
/// ISO, runs DISM or writes outside the temp folder.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class InstallMediaViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunero-media-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakePageHost _host = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<MediaRequest> _requests = [];
    private readonly string _language = Loc.Instance.Language;

    /// <summary>What the fake build does with a request; null: records it and returns its output path.</summary>
    private Func<MediaRequest, CancellationToken, Task<string>>? _build;

    /// <summary>The size, hash and log the fake build returns for the ISO it is asked for; null: a sample.</summary>
    private MediaBuildResult? _result;

    private string? _clipboard;
    private readonly List<string> _shown = [];

    /// <summary>What the fake build reports before it ends; null: one report of Unpack at 50 percent.</summary>
    private Action<IProgress<MediaProgress>>? _reports;

    /// <summary>What the fake ISO reader finds in an own ISO.</summary>
    private IsoInfo _iso = Iso(Image(1, "Core", "Windows 11 Home"), Image(2, "Professional", "Windows 11 Pro"));

    public InstallMediaViewModelTests()
    {
        Directory.CreateDirectory(_root);
        // The review before a build is confirmed unless a test cancels it.
        _dialogs.ConfirmApplyResult = new ConfirmResult(false);
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage(_language);
        Directory.Delete(_root, recursive: true);
    }

    private static readonly IReadOnlyList<EsdFile> Files =
    [
        Esd("en-us", "English (United States)", "Professional"), Esd("en-us", "English (United States)", "Core"),
        Esd("de-de", "German (Germany)", "Professional"), Esd("de-de", "German (Germany)", "Core"),
    ];

    private static EsdFile Esd(string code, string language, string edition) =>
        new($"26300.9711.261011-1601.26h2_ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_{code}.esd", code, language, edition, "x64",
            6L << 30, new string('a', 64), new Uri($"http://dl.delivery.mp.microsoft.com/filestreamingservice/files/{code}.esd"));

    private static WimImageInfo Image(int index, string edition, string name, string arch = "x64", int build = 26300, string language = "de-DE") =>
        new(index, name, edition, arch, $"10.0.{build}.1", build, language, [language]);

    private static IsoInfo Iso(params WimImageInfo[] images) => new("install.wim", images, HasBootWim: true);

    private sealed class FakeCatalog : IMediaCatalogSource
    {
        public Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct) => Task.FromResult(Files);
    }

    private string RememberedFile => Path.Combine(_root, "media-choices.json");

    private string Exe()
    {
        var exe = Path.Combine(_root, "Tunero.exe");
        File.WriteAllText(exe, "MZ");
        return exe;
    }

    private static readonly IReadOnlyList<InputProfile> Keyboards =
    [
        new("0407:00000407", "German", "de-DE"), new("0C07:00000407", "German", "de-AT"), new("0409:00000409", "US", "en-US"),
    ];

    private InstallMediaSystem System(string? edition = "Professional", bool published = true) => new()
    {
        Elevated = true,
        ExePath = published ? Exe() : null,
        IsPublishedExe = published,
        ProfileId = () => "office",
        FreeBytes = _ => long.MaxValue,
        ReadPc = () => new ThisPcFacts(edition, false, "Anna", "de-AT", _root),
        Keyboard = () => null,
        Keyboards = () => Keyboards,
        ChoicesFile = () => RememberedFile,
        ReadIso = (_, _) => Task.FromResult(_iso),
        CopyText = text => _clipboard = text,
        ShowInExplorer = path => _shown.Add(path),
    };

    private InstallMediaViewModel Media(string? edition = "Professional", bool published = true, InstallMediaSystem? system = null,
        Func<CancellationToken, Task>? cleanup = null) => new(_host, _dialogs, new FakeCatalog(),
        async (request, progress, ct) =>
        {
            _requests.Add(request);
            if (_reports is { } reports) reports(progress);
            else progress.Report(new MediaProgress(MediaStage.Unpack, 50, null));
            var iso = await (_build?.Invoke(request, ct) ?? Task.FromResult(request.OutputIso));
            return (_result ?? new MediaBuildResult(iso, 1234, new string('A', 64))) with { Iso = iso };
        },
        system ?? System(edition, published), cleanup);

    private static async Task<InstallMediaViewModel> Loaded(InstallMediaViewModel media)
    {
        await media.EnsureLoadedAsync();
        return media;
    }

    private async Task<InstallMediaViewModel> WithCatalog(InstallMediaViewModel? media = null)
    {
        media = await Loaded(media ?? Media());
        await media.CheckCatalogCommand.ExecuteAsync(null);
        return media;
    }

    /// <summary>An own ISO file in the temp folder, read by the fake reader.</summary>
    private async Task<InstallMediaViewModel> WithOwnIso(InstallMediaViewModel? media = null)
    {
        media = await Loaded(media ?? Media());
        var iso = Path.Combine(_root, "Win11.iso");
        File.WriteAllText(iso, "iso");
        media.UseDownload = false;
        media.IsoPath = iso;
        await media.IsoLoading;
        return media;
    }

    [Fact]
    public async Task FinishedCardShowsHashAndOffersCopyFolderAndLog()
    {
        var hash = "5E3A9B1C7D2F4086A1B3C5D7E9F0123456789ABCDEF0123456789ABCDEF01234";
        _result = new MediaBuildResult("", 5_712_000_000, hash) { LogPath = Path.Combine(_root, "build.log") };
        var media = await WithCatalog();
        Assert.False(media.HasFinished);
        await media.BuildCommand.ExecuteAsync(null);

        Assert.True(media.HasFinished);
        Assert.Equal(hash, media.ResultSha256);
        Assert.Equal(ByteSize.Format(5_712_000_000), media.ResultSizeText);
        Assert.Equal(media.OutputPath, media.ResultIso);
        Assert.Equal(InfoBarSeverity.Success, media.ResultSeverity);
        media.CopyHashCommand.Execute(null);
        Assert.Equal(hash, _clipboard);
        media.OpenFolderCommand.Execute(null);
        Assert.Equal([media.OutputPath], _shown);
        Assert.True(media.ShowLogCommand.CanExecute(null));
        media.ShowLogCommand.Execute(null);
        Assert.Equal(_result.LogPath, _shown[^1]);
    }

    [Fact]
    public async Task AFailedOrNewBuildClearsTheEarlierHash()
    {
        var media = await WithCatalog();
        await media.BuildCommand.ExecuteAsync(null);
        Assert.NotNull(media.ResultSha256);

        _build = (_, _) => throw new MediaBuildException(MediaStage.Unpack, "x", 87) { LogPath = Path.Combine(_root, "build.log") };
        await media.BuildCommand.ExecuteAsync(null);
        Assert.False(media.HasFinished);
        Assert.Null(media.ResultSha256);
        Assert.Null(media.ResultIso);
        Assert.True(media.HasFailed);
        Assert.True(media.ShowLogCommand.CanExecute(null));
    }

    [Fact]
    public async Task DefaultsFollowThisPc()
    {
        Loc.Instance.SetLanguage("de");
        var media = await WithCatalog(Media("Core"));
        Assert.Equal("Core", media.Edition?.Key);
        Assert.Equal("office", media.Profile?.Key);
        Assert.Equal("de-de", media.Language?.Key);
        Assert.Equal("de-AT", media.Region?.Key);
        // No keyboard read from this PC: the region's default.
        Assert.Equal(MediaLocales.DefaultKeyboard(new CultureInfo("de-AT")), media.Keyboard?.Key);
        Assert.Equal("Anna", media.AccountNameText);
        Assert.Equal(_root, Path.GetDirectoryName(media.OutputPath));

        // An edition the media does not offer (Enterprise): Pro.
        Assert.Equal("Professional", (await WithCatalog(Media("Enterprise"))).Edition?.Key);
    }

    [Fact]
    public async Task InvalidAccountNameBlocksBuild()
    {
        var media = await WithCatalog();
        media.SkipSetup = true;
        media.AccountNameText = "Anna";
        Assert.Null(media.AccountProblem);
        Assert.True(media.CanBuild);
        media.AccountNameText = "a/b";
        Assert.Equal(Loc.Instance["Media_NameInvalid"], media.AccountProblem);
        Assert.False(media.CanBuild);
        media.AccountNameText = "Jürgen";
        Assert.Equal(Loc.Instance["Media_NameNotAscii"], media.AccountProblem);
        Assert.False(media.CanBuild);
    }

    [Fact]
    public async Task OwnIsoNeedsAFile()
    {
        var media = await Loaded(Media());
        media.UseDownload = false;
        Assert.Null(media.IsoPath);
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_NeedsIso"], media.BuildNote);
        media = await WithOwnIso(media);
        Assert.True(media.CanBuild);
    }

    /// <summary>The own ISO is read before the build: its editions are offered, with its build, language and architecture.</summary>
    [Fact]
    public async Task OwnIsoShowsItsEditionsAndBuildsTheChosenImage()
    {
        var media = await WithOwnIso();
        Assert.Equal(["1", "2"], media.Editions.Select(e => e.Key));
        Assert.Equal([Loc.Instance["Media_Edition_Core"], Loc.Instance["Media_Edition_Professional"]], media.Editions.Select(e => e.Text));
        Assert.Equal("2", media.Edition?.Key);
        Assert.Contains("26300", media.IsoText);
        Assert.Contains("de-DE", media.IsoText);
        Assert.Contains("x64", media.IsoText);

        await media.BuildCommand.ExecuteAsync(null);
        var r = Assert.Single(_requests);
        Assert.Equal(Path.Combine(_root, "Win11.iso"), r.Source.IsoPath);
        Assert.Equal(("Professional", 2), (r.Edition, r.IsoIndex));
        Assert.Equal("de-DE", r.Answers.UiLanguage);
    }

    [Fact]
    public async Task OwnIsoThatIsNotWindows11ForX64IsRefused()
    {
        _iso = Iso(Image(1, "Professional", "Windows 11 Pro", arch: "ARM64"));
        var media = await WithOwnIso();
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_NotX64"], media.BuildNote);

        _iso = Iso(Image(1, "Professional", "Windows 10 Pro", build: 19045));
        media = await WithOwnIso();
        Assert.Equal(Loc.Instance["Media_NotWindows11"], media.BuildNote);

        _iso = new IsoInfo(null, [], HasBootWim: true);
        media = await WithOwnIso();
        Assert.Equal(Loc.Instance["Media_NotWindows11"], media.BuildNote);
        Assert.Empty(media.Editions);
    }

    [Fact]
    public async Task OwnIsoInUseIsSaid()
    {
        var system = System() with { ReadIso = (_, _) => throw new IsoAttachException(32, "in use") };
        var media = await WithOwnIso(Media(system: system));
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_IsoInUse"], media.BuildNote);
    }

    [Fact]
    public async Task BuildAsksForTheReviewAndCancelBuildsNothing()
    {
        var media = await WithCatalog();
        _dialogs.ConfirmApplyResult = null;
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(1, _dialogs.ConfirmApplyCalls);
        Assert.Empty(_requests);
        Assert.False(media.IsBuilding);

        _dialogs.ConfirmApplyResult = new ConfirmResult(false);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(2, _dialogs.ConfirmApplyCalls);
        Assert.Single(_requests);
        Assert.Equal(Loc.Instance["Media_BuildButton"], _dialogs.ConfirmApplyRequests[1].Primary);
    }

    [Fact]
    public async Task SummaryTextFollowsTheChoices()
    {
        var media = await WithCatalog();
        var changed = new List<string?>();
        media.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var before = media.SummaryText;
        Assert.Contains("Windows 11 ", before);
        Assert.Contains("26300.9711", before);
        Assert.Contains("Anna", before);
        var count = media.AppRows.Count(r => r.IsChecked);
        Assert.True(count > 0);
        Assert.Contains(Loc.Instance.Format("Media_Summary_Apps", count), before);

        media.AppGroups.First(g => g.Rows.Any(r => r.IsChecked)).AllChecked = false;
        Assert.Contains(nameof(InstallMediaViewModel.SummaryText), changed);
        Assert.NotEqual(before, media.SummaryText);
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Apps", count), media.SummaryText);

        changed.Clear();
        media.AccountNameText = "Berta";
        Assert.Contains(nameof(InstallMediaViewModel.SummaryText), changed);
        Assert.Contains("Berta", media.SummaryText);
    }

    [Fact]
    public async Task BuildPassesTheChoices()
    {
        var media = await WithCatalog();
        media.Language = media.Languages.Single(l => l.Key == "en-us");
        media.RemoveApps = true;
        media.SkipSetup = true;
        media.AccountNameText = "Anna";
        media.Region = media.Regions.Single(r => r.Key == "hi-IN");
        media.Keyboard = media.Keyboards.Single(k => k.Key == "0407:00000407");
        media.AllowUnsupported = true;
        media.ApplyProfile = true;
        media.Profile = media.Profiles.Single(p => p.Key == "gaming");
        await media.BuildCommand.ExecuteAsync(null);

        var r = Assert.Single(_requests);
        // The consumer apps the measured image of this build and edition holds (mediaimages.json), not the whole group.
        var measured = MediaChoices.MeasuredApps(CatalogData.Current.MediaImages, r.Source.Download!)!;
        var consumer = MediaChoices.PageApps(CatalogData.Current.Appx, measured).Where(a => a.Group == "consumer").Select(a => a.Name).Order();
        Assert.Equal(consumer, r.RemoveApps.Order());
        Assert.DoesNotContain("Microsoft.People", r.RemoveApps);
        Assert.Equal("en-us", r.Source.Download?.LanguageCode);
        Assert.Equal("Professional", r.Edition);
        Assert.Null(r.IsoIndex);
        Assert.Equal("Professional", r.Source.Download?.Edition);
        Assert.True(r.Answers.AllowUnsupported);
        Assert.True(r.Answers.SkipSetup);
        Assert.Equal("Anna", r.Answers.AccountName);
        Assert.Equal("amd64", r.Answers.Architecture);
        Assert.Equal("en-US", r.Answers.UiLanguage);
        Assert.Equal("0407:00000407", r.Answers.InputLocale);
        Assert.Equal("hi-IN", r.Answers.UserLocale);
        // Hindi is Unicode-only: Windows takes no such system locale.
        Assert.Equal("en-US", r.Answers.SystemLocale);
        Assert.True(r.Answers.FirstSignInTunero);
        Assert.Equal("gaming", r.FirstSignIn?.Profile);
        Assert.Equal(media.OutputPath, r.OutputIso);
        Assert.Equal(_root, Path.GetDirectoryName(r.OutputIso));
        Assert.Equal(InfoBarSeverity.Success, media.ResultSeverity);
        Assert.False(media.IsBuilding);
    }

    [Fact]
    public async Task FailureShowsStageAndLog()
    {
        var log = Path.Combine(_root, "media.log");
        _build = (_, _) => throw new MediaBuildException(MediaStage.Unpack, "x", 87) { LogPath = log };
        var media = await WithCatalog();
        await media.BuildCommand.ExecuteAsync(null);

        Assert.Contains(Loc.Instance["Media_Stage_Unpack"], media.ResultText);
        Assert.Contains("87", media.ResultText);
        Assert.Contains(log, media.ResultText);
        Assert.Equal(log, media.ResultLogPath);
        Assert.Equal(InfoBarSeverity.Error, media.ResultSeverity);
        Assert.Contains(_host.Results, r => r.Severity == InfoBarSeverity.Error && r.Text.Contains(Loc.Instance["Media_Stage_Unpack"]));
    }

    [Fact]
    public async Task AFailureWithAKnownReasonShowsItsText()
    {
        _build = (_, _) => throw new MediaBuildException(MediaStage.Download, "x", key: "Media_NotEnoughSpace");
        var media = await WithCatalog();
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Contains(Loc.Instance["Media_NotEnoughSpace"], media.ResultText);
    }

    /// <summary>The download: the allowlist apps Microsoft's image can hold (no manufacturer, third-party or legacy apps).</summary>
    [Fact]
    public async Task AppRowsComeFromTheAllowlist()
    {
        var media = await Loaded(Media());
        var appx = CatalogData.Current.Appx;
        var offered = appx.Apps.Where(a => MediaChoices.MicrosoftImageGroups.Contains(a.Group)).ToList();
        Assert.Equal(offered.Count, media.AppRows.Count);
        Assert.DoesNotContain(media.AppRows, r => appx.IsProtected(r.Entry.Name));
        Assert.DoesNotContain(media.AppRows, r => r.Entry.Group is "oem" or "thirdParty" or "legacy");
        Assert.All(media.AppRows, r => Assert.Equal(r.Entry.Group == "consumer", r.IsChecked));
        Assert.Equal(offered.Select(a => a.Group).Distinct().Count(), media.AppGroups.Count);
        Assert.Equal(Loc.Instance["Media_AppsFromDownload"], media.AppsSourceText);
        var consumer = appx.Apps.Count(a => a.Group == "consumer");
        Assert.Equal(Loc.Instance.Format("Media_AppsSummary", consumer), media.AppsSummary);
        media.AppRows[0].IsChecked = !media.AppRows[0].IsChecked;
        Assert.NotEqual(Loc.Instance.Format("Media_AppsSummary", consumer), media.AppsSummary);
    }

    /// <summary>An own ISO: the list holds exactly the allowlist apps its chosen edition has, read again for another edition.</summary>
    [Fact]
    public async Task OwnIsoListsTheAppsOfItsEdition()
    {
        var reads = new List<int>();
        var system = System() with
        {
            ReadIsoApps = (_, index, _) =>
            {
                reads.Add(index);
                IReadOnlyList<string> apps = index == 2
                    ? ["Microsoft.BingNews_4.1.0.0_x64__8wekyb3d8bbwe", "Microsoft.GamingApp_2.0.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe",
                       "SpotifyAB.SpotifyMusic_1.0.0.0_x64__zpdnekdrzrea0"]
                    : ["Microsoft.BingWeather_4.0.0.0_x64__8wekyb3d8bbwe"];
                return Task.FromResult(apps);
            },
        };
        var media = await WithOwnIso(Media(system: system));
        await media.AppsLoading;
        Assert.Equal([2], reads);
        // The Store is protected: never offered. A third-party app the image holds is listed, unticked.
        Assert.Equal(["Microsoft.BingNews", "Microsoft.GamingApp", "SpotifyAB.SpotifyMusic"], media.AppRows.Select(r => r.Entry.Name).Order());
        Assert.Equal([true, false, false], media.AppRows.OrderBy(r => r.Entry.Name).Select(r => r.IsChecked));
        Assert.Equal(Loc.Instance.Format("Media_AppsFromIso", 3), media.AppsSourceText);

        // News unticked by hand stays unticked when the list comes back.
        media.AppRows.Single(r => r.Entry.Name == "Microsoft.BingNews").IsChecked = false;
        media.Edition = media.Editions.Single(e => e.Key == "1");
        await media.AppsLoading;
        Assert.Equal(["Microsoft.BingWeather"], media.AppRows.Select(r => r.Entry.Name));
        media.Edition = media.Editions.Single(e => e.Key == "2");
        await media.AppsLoading;
        Assert.Equal([2, 1], reads);
        Assert.False(media.AppRows.Single(r => r.Entry.Name == "Microsoft.BingNews").IsChecked);

        await media.BuildCommand.ExecuteAsync(null);
        Assert.Empty(Assert.Single(_requests).RemoveApps);

        // Back to the download: the list of what Microsoft's image can hold again.
        media.UseDownload = true;
        Assert.Equal(Loc.Instance["Media_AppsFromDownload"], media.AppsSourceText);
        Assert.DoesNotContain(media.AppRows, r => r.Entry.Name == "SpotifyAB.SpotifyMusic");
    }

    /// <summary>A failed read of the image's apps leaves the list of what a Microsoft image can hold, and says so.</summary>
    [Fact]
    public async Task OwnIsoWhoseAppsCannotBeReadKeepsTheMicrosoftList()
    {
        var system = System() with { ReadIsoApps = (_, _, _) => Task.FromException<IReadOnlyList<string>>(new DismException(5, "Error: 5")) };
        var media = await WithOwnIso(Media(system: system));
        await media.AppsLoading;
        Assert.Equal(Loc.Instance["Media_AppsIsoFailed"], media.AppsSourceText);
        Assert.Equal(CatalogData.Current.Appx.Apps.Count(a => MediaChoices.MicrosoftImageGroups.Contains(a.Group)), media.AppRows.Count);
        Assert.True(media.CanBuild);
    }

    /// <summary>A group's check box ticks all of the group, then none; with some ticked it shows neither.</summary>
    [Fact]
    public async Task GroupCheckBoxTicksTheWholeGroup()
    {
        var media = await Loaded(Media());
        var group = media.AppGroups.Single(g => g.Key == "microsoft");
        Assert.False(group.AllChecked);
        group.Rows[0].IsChecked = true;
        Assert.Null(group.AllChecked);
        group.AllChecked = true;
        Assert.All(group.Rows, r => Assert.True(r.IsChecked));
        Assert.True(group.AllChecked);
        group.AllChecked = false;
        Assert.All(group.Rows, r => Assert.False(r.IsChecked));
        Assert.Equal(Loc.Instance.Format("Media_AppsChosen", 0, group.Rows.Count), group.Summary);
    }

    [Fact]
    public async Task ProfileNeedsTheSingleFileExe()
    {
        var media = await Loaded(Media(published: false));
        Assert.False(media.CanApplyProfile);
        Assert.False(media.ApplyProfile);
        Assert.Equal(Loc.Instance["Media_ProfileNeedsRelease"], media.ProfileNote);
        // The apps to install need the Tunero copy too.
        Assert.False(media.CanInstallApps);
        Assert.Equal(Loc.Instance["Media_InstallNeedsProfile"], media.InstallNote);
    }

    /// <summary>The app catalog's machine-wide apps, none ticked; the ticked ones go to the first sign-in with the profile.</summary>
    [Fact]
    public async Task TickedAppsGoToTheFirstSignIn()
    {
        var media = await WithCatalog();
        Assert.Equal(MediaChoices.InstallableApps(CatalogData.Current.Apps).Count(), media.InstallRows.Count);
        // Per-user apps too: the first sign-in installs them as the account that signed in.
        Assert.Contains(media.InstallRows, r => r.App.Scope == "user");
        Assert.All(media.InstallRows, r => Assert.False(r.IsChecked));
        Assert.Equal(Loc.Instance["Media_Off"], media.InstallSummary);

        media.InstallRows.Single(r => r.Key == "Valve.Steam").IsChecked = true;
        // Ticked while the card is off: nothing is installed.
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(_requests).FirstSignIn!.Apps);

        media.InstallApps = true;
        Assert.Equal(Loc.Instance.Format("Media_InstallSummary", 1), media.InstallSummary);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(["Valve.Steam"], _requests[^1].FirstSignIn!.Apps);

        // Without the profile there is no Tunero copy to install them.
        media.ApplyProfile = false;
        Assert.False(media.CanInstallApps);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Null(_requests[^1].FirstSignIn);
    }

    /// <summary>Expert: the own ISO's apps beyond the reviewed list, unticked; only ticked ones are removed, never for the download.</summary>
    [Fact]
    public async Task UnreviewedAppsOfTheOwnIsoCanBeRemoved()
    {
        var system = System() with
        {
            ReadIsoApps = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(
            [
                "Microsoft.BingNews_4.1.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsStore_1.0.0.0_x64__8wekyb3d8bbwe",
                "MicrosoftWindows.Client.CBS_1.0.0.0_x64__cw5n1h2txyewy", "Contoso.Notes_1.0.0.0_x64__abcdefghijklm",
            ]),
        };
        var media = await WithOwnIso(Media(system: system));
        await media.AppsLoading;
        // The reviewed, protected and Windows parts are not listed.
        Assert.Equal(["Contoso.Notes"], media.UnreviewedRows.Select(r => r.Key));
        Assert.False(media.UnreviewedRows[0].IsChecked);
        Assert.Equal(Loc.Instance.Format("Media_UnreviewedFound", 1), media.UnreviewedSourceText);

        media.UnreviewedRows[0].IsChecked = true;
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Empty(_requests[^1].RemoveUnreviewed);
        media.RemoveUnreviewed = true;
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(["Contoso.Notes"], _requests[^1].RemoveUnreviewed);

        media.UseDownload = true;
        Assert.Equal(Loc.Instance["Media_UnreviewedDownload"], media.UnreviewedSourceText);
        Assert.Empty(media.UnreviewedRows);
    }

    [Fact]
    public async Task StopEndsTheBuildAndWaits()
    {
        var started = new TaskCompletionSource();
        var media = await WithCatalog();
        _build = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        var build = media.BuildCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(media.IsBuilding);
        await media.StopAndWaitAsync();
        Assert.False(media.IsBuilding);
        await build;
        Assert.Equal(Loc.Instance["Media_Stopped"], media.ResultText);
    }

    private static MediaStageRow Row(InstallMediaViewModel media, MediaStage stage) => media.Stages.Single(s => s.Stage == stage);

    /// <summary>Progress arrives on the window's context after the build reported it: wait for the row to show it.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static void Begin(IProgress<MediaProgress> progress, params MediaStage[] stages)
    {
        foreach (var stage in stages) progress.Report(new MediaProgress(stage, null, null));
    }

    [Fact]
    public async Task TheStageListIsThePlannedStagesOfTheRequest()
    {
        var media = await WithCatalog();
        _build = (_, _) => Task.FromResult("");
        await media.BuildCommand.ExecuteAsync(null);
        var request = Assert.Single(_requests);
        Assert.Equal(MediaBuild.PlannedStages(request), media.Stages.Select(s => s.Stage));
        Assert.All(media.Stages, s => Assert.Equal(Loc.Instance[$"Media_Stage_{s.Stage}"], s.Title));
        Assert.True(media.HasStages);
    }

    [Fact]
    public async Task RowsFollowTheProgressAndTheFailure()
    {
        _reports = p => Begin(p, MediaStage.Download, MediaStage.Check, MediaStage.Unpack);
        _build = (_, _) => throw new MediaBuildException(MediaStage.Unpack, "x", 87, "Media_ImageCheckFailed");
        var media = await WithCatalog();
        await media.BuildCommand.ExecuteAsync(null);

        Assert.Equal(MediaStageState.Done, Row(media, MediaStage.Download).State);
        Assert.Equal(MediaStageState.Done, Row(media, MediaStage.Check).State);
        Assert.NotNull(Row(media, MediaStage.Download).Duration);
        var failed = Row(media, MediaStage.Unpack);
        Assert.Equal(MediaStageState.Failed, failed.State);
        // The reason as the result line says it, and the error code.
        Assert.Contains(Loc.Instance["Media_ImageCheckFailed"], failed.Detail);
        Assert.Contains("87", failed.Detail);
        Assert.Contains(Loc.Instance["Media_ImageCheckFailed"], media.ResultText);
        Assert.All(media.Stages.SkipWhile(s => s.Stage != MediaStage.Unpack).Skip(1), s => Assert.Equal(MediaStageState.NotRun, s.State));
    }

    [Fact]
    public async Task StopMarksTheRunningStageStopped()
    {
        var started = new TaskCompletionSource();
        _reports = p => Begin(p, MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Apps);
        _build = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        var media = await WithCatalog();
        var build = media.BuildCommand.ExecuteAsync(null);
        await started.Task;
        await WaitFor(() => media.Stages.Count > 0 && Row(media, MediaStage.Apps).State == MediaStageState.Running
            && Row(media, MediaStage.Unpack).State == MediaStageState.Done);
        await media.StopAndWaitAsync();
        await build;

        Assert.Equal(MediaStageState.Stopped, Row(media, MediaStage.Apps).State);
        Assert.Equal(MediaStageState.Done, Row(media, MediaStage.Unpack).State);
        Assert.All(media.Stages.SkipWhile(s => s.Stage != MediaStage.Apps).Skip(1), s => Assert.Equal(MediaStageState.NotRun, s.State));
    }

    /// <summary>The engine reports Cleanup before it rethrows a Stop or a failure: the running row still ends Stopped or Failed.</summary>
    [Fact]
    public async Task CleanupReportedBeforeAStopStillMarksTheStageStopped()
    {
        var started = new TaskCompletionSource();
        IProgress<MediaProgress>? engine = null;
        _reports = p =>
        {
            engine = p;
            Begin(p, MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Apps);
        };
        _build = async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Begin(engine!, MediaStage.Cleanup);   // as CleanUpAfterFailureAsync does, before it rethrows
                throw;
            }
            return "";
        };
        var media = await WithCatalog();
        var build = media.BuildCommand.ExecuteAsync(null);
        await started.Task;
        await media.StopAndWaitAsync();
        await build;
        Assert.Equal(MediaStageState.Stopped, Row(media, MediaStage.Apps).State);
        Assert.Equal(MediaStageState.Done, Row(media, MediaStage.Unpack).State);
        Assert.Equal(Loc.Instance["Media_StageStopped"], Row(media, MediaStage.Apps).Note);
    }

    [Fact]
    public async Task CleanupReportedBeforeAFailureStillMarksTheStageFailed()
    {
        _reports = p => Begin(p, MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Cleanup);
        _build = (_, _) => throw new MediaBuildException(MediaStage.Unpack, "x", 87, "Media_ImageCheckFailed");
        var media = await WithCatalog();
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(MediaStageState.Failed, Row(media, MediaStage.Unpack).State);
        Assert.Contains("87", Row(media, MediaStage.Unpack).Detail);
    }

    [Fact]
    public async Task CleanupAfterASuccessLeavesEveryRowDone()
    {
        IProgress<MediaProgress>? engine = null;
        _reports = p => engine = p;
        _build = (request, _) =>
        {
            Begin(engine!, [.. MediaBuild.PlannedStages(request), MediaStage.Cleanup]);
            return Task.FromResult(request.OutputIso);
        };
        var media = await WithCatalog();
        await media.BuildCommand.ExecuteAsync(null);
        Assert.All(media.Stages, s => Assert.Equal(MediaStageState.Done, s.State));
    }

    [Fact]
    public async Task TheRowShowsTheCurrentPackageAndPercent()
    {
        var reported = new TaskCompletionSource();
        var finish = new TaskCompletionSource<string>();
        _reports = p =>
        {
            Begin(p, MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Apps);
            p.Report(new MediaProgress(MediaStage.Apps, 50, "Microsoft.BingNews"));
        };
        _build = (_, _) =>
        {
            reported.SetResult();
            return finish.Task;
        };
        var media = await WithCatalog();
        var build = media.BuildCommand.ExecuteAsync(null);
        await reported.Task;
        await WaitFor(() => Row(media, MediaStage.Apps).Percent is not null);

        var row = Row(media, MediaStage.Apps);
        Assert.Equal(MediaStageState.Running, row.State);
        Assert.Equal("Microsoft.BingNews", row.Detail);
        Assert.Equal(50, row.Percent);
        Assert.StartsWith(Loc.Instance.Format("Media_StagesCount", 4, media.Stages.Count, ""), media.StagesText);

        finish.SetResult(media.OutputPath!);
        await build;
        Assert.All(media.Stages.Take(4), s => Assert.Equal(MediaStageState.Done, s.State));
        Assert.All(media.Stages, s => Assert.Null(s.Percent));
        Assert.Null(Row(media, MediaStage.Apps).Detail);
    }

    /// <summary>A running build uses the choices it started with: the page locks them until it ends.</summary>
    [Fact]
    public async Task ChoicesAreLockedWhileABuildRuns()
    {
        var started = new TaskCompletionSource();
        var media = await WithCatalog();
        _build = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        Assert.True(media.CanEdit);
        var build = media.BuildCommand.ExecuteAsync(null);
        await started.Task;
        Assert.False(media.CanEdit);
        await media.StopAndWaitAsync();
        await build;
        Assert.True(media.CanEdit);
    }

    /// <summary>A look at the own ISO and back keeps the edition chosen for the download.</summary>
    [Fact]
    public async Task TheDownloadEditionSurvivesALookAtTheOwnIso()
    {
        var media = await WithCatalog();
        media.Edition = media.Editions.Single(e => e.Key == "Core");
        media.UseDownload = false;
        media.UseDownload = true;
        Assert.Equal("Core", media.Edition?.Key);
    }

    /// <summary>An own ISO's edition name is written by whoever made the ISO: it cannot move the default file out of Downloads.</summary>
    [Fact]
    public async Task AnOwnIsoEditionCannotStepOutOfDownloads()
    {
        _iso = Iso(Image(1, @"\..\..\Pro:x", "Odd"));
        var media = await WithOwnIso();
        Assert.Equal(_root, Path.GetDirectoryName(media.OutputPath));
        Assert.Equal("Windows11_Prox_Tunero.iso", Path.GetFileName(media.OutputPath));
    }

    // ---- what keeps Build off ----

    [Fact]
    public async Task OutputProblemsBlockBuild()
    {
        var media = await WithCatalog();
        Assert.True(media.CanBuild);
        media.OutputPath = Path.Combine(_root, "Win11.txt");
        Assert.Equal(Loc.Instance["Media_OutputInvalid"], media.BuildNote);
        media.OutputPath = Path.Combine(_root, "missing", "Win11.iso");
        Assert.False(media.CanBuild);
        media.OutputPath = "Win11.iso";
        Assert.False(media.CanBuild);
        media.OutputPath = Path.Combine(_root, "Win11.iso");
        Assert.True(media.CanBuild);
    }

    [Theory]
    [InlineData(@"\\server\share\Win11.iso")]
    [InlineData(@"\\?\C:\Win11.iso")]
    public async Task OutputOffTheLocalDrivesBlocksBuild(string output)
    {
        var media = await WithCatalog();
        media.OutputPath = output;
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_OutputNotLocal"], media.BuildNote);
    }

    [Fact]
    public async Task OutputCannotBeTheSourceIso()
    {
        var media = await WithOwnIso();
        media.OutputPath = media.IsoPath;
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_OutputInvalid"], media.BuildNote);
    }

    [Fact]
    public async Task NotEnoughSpaceBlocksBuild()
    {
        var media = await WithCatalog(Media(system: System() with { FreeBytes = _ => 1L << 30 }));
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_NotEnoughSpace"], media.BuildNote);
    }

    /// <summary>
    /// A leftover work folder holds the space the start cleanup is about to free, and the build checks the space again after
    /// it waited for the cleanup: while the cleanup runs, too little free space does not keep Build off.
    /// </summary>
    [Fact]
    public async Task WhileTheCleanupRunsLittleFreeSpaceDoesNotKeepBuildOff()
    {
        var free = 1L << 30;
        var cleanup = new TaskCompletionSource();
        var media = await WithCatalog(Media(system: System() with { FreeBytes = _ => free }, cleanup: _ => cleanup.Task));
        Assert.Equal(Loc.Instance["Media_NotEnoughSpace"], media.BuildNote);
        var cleaning = media.CleanLeftoversAsync();
        Assert.True(media.CanBuild);
        Assert.True(media.BuildCommand.CanExecute(null));
        Assert.Equal(Loc.Instance["Media_CleanupRunning"], media.BuildNote);
        cleanup.SetResult();
        await cleaning;
    }

    [Fact]
    public async Task WhenTheCleanupEndsWithLittleSpaceBuildTurnsOff()
    {
        var free = 1L << 30;
        var cleanup = new TaskCompletionSource();
        var media = await WithCatalog(Media(system: System() with { FreeBytes = _ => free }, cleanup: _ => cleanup.Task));
        var cleaning = media.CleanLeftoversAsync();
        var changed = new List<string?>();
        var canExecute = 0;
        media.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        media.BuildCommand.CanExecuteChanged += (_, _) => canExecute++;
        cleanup.SetResult();
        await cleaning;
        Assert.Contains(nameof(InstallMediaViewModel.CanBuild), changed);
        Assert.Contains(nameof(InstallMediaViewModel.BuildNote), changed);
        Assert.True(canExecute > 0);
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_NotEnoughSpace"], media.BuildNote);
    }

    [Fact]
    public async Task WhenTheCleanupFreedTheSpaceBuildStaysOnWithoutANote()
    {
        var free = 1L << 30;
        var cleanup = new TaskCompletionSource();
        var media = await WithCatalog(Media(system: System() with { FreeBytes = _ => free }, cleanup: _ => cleanup.Task));
        var cleaning = media.CleanLeftoversAsync();
        free = long.MaxValue;
        cleanup.SetResult();
        await cleaning;
        Assert.True(media.CanBuild);
        Assert.Null(media.BuildNote);
    }

    [Fact]
    public async Task AFailedCleanupAlsoEndsTheNote()
    {
        var media = await WithCatalog(Media(cleanup: _ => Task.FromException(new IOException("x"))));
        await media.CleanLeftoversAsync();
        Assert.Null(media.BuildNote);
    }

    [Fact]
    public async Task ShowingThePageAgainReadsTheFreeSpaceAgain()
    {
        var free = 1L << 30;
        var media = await WithCatalog(Media(system: System() with { FreeBytes = _ => free }));
        Assert.False(media.CanBuild);
        free = long.MaxValue;
        var changed = new List<string?>();
        media.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        media.PageShown();
        Assert.Contains(nameof(InstallMediaViewModel.BuildNote), changed);
        Assert.Contains(nameof(InstallMediaViewModel.CanBuild), changed);
        Assert.Contains(nameof(InstallMediaViewModel.NeededText), changed);
    }

    [Fact]
    public async Task WithoutAdminRightsNothingIsBuilt()
    {
        var media = await WithCatalog(Media(system: System() with { Elevated = false }));
        Assert.False(media.CanBuild);
        Assert.Equal(Loc.Instance["Media_NeedsAdmin"], media.BuildNote);
    }

    [Fact]
    public async Task AMissingRegionBlocksBuild()
    {
        var media = await WithCatalog();
        media.Region = null;
        Assert.Equal(Loc.Instance["Media_NeedsRegion"], media.BuildNote);
        media.SkipSetup = false;
        Assert.True(media.CanBuild);
    }

    /// <summary>The cleanup after earlier builds at start and a build never run together: the build waits for it.</summary>
    [Fact]
    public async Task ABuildWaitsForTheStartupCleanup()
    {
        var cleanup = new TaskCompletionSource();
        var media = await WithCatalog(Media(cleanup: _ => cleanup.Task));
        var cleaning = media.CleanLeftoversAsync();
        var build = media.BuildCommand.ExecuteAsync(null);
        Assert.True(media.IsBuilding);
        Assert.Empty(_requests);
        cleanup.SetResult();
        await cleaning;
        await build;
        Assert.Single(_requests);
    }

    /// <summary>
    /// The real cleanup at the app start polls DISM through WMI for up to 15 minutes and deletes gigabytes: none of it may run
    /// on the window's thread. A context that never runs what is posted to it stands in for a busy dispatcher: the cleanup
    /// must get to DISM without it.
    /// </summary>
    [Fact]
    public async Task TheStartupCleanupRunsOffTheCallingThread()
    {
        var steps = new LeftoverSteps();
        var window = new NeverRuns();
        var previous = SynchronizationContext.Current;
        Task cleaning;
        SynchronizationContext.SetSynchronizationContext(window);
        try
        {
            cleaning = InstallMediaSystem.CleanLeftoversAsync(steps, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        var context = await steps.DismAskedOn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(window, context);
        Assert.NotEqual(Environment.CurrentManagedThreadId, steps.DismThread);
        await cleaning.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class NeverRuns : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }

    /// <summary>One leftover work folder and a DISM whose command line cannot be read: the cleanup then stops there, touching nothing.</summary>
    private sealed class LeftoverSteps : IMediaSteps
    {
        public TaskCompletionSource<SynchronizationContext?> DismAskedOn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DismThread { get; private set; }

        public IReadOnlyList<string>? DismCommandLines()
        {
            DismThread = Environment.CurrentManagedThreadId;
            DismAskedOn.TrySetResult(SynchronizationContext.Current);
            return null;
        }

        public IEnumerable<string> LocalDrives() => [@"X:\"];
        public IEnumerable<string> LeftoverWorkFolders(string drive) => [@"X:\TuneroMedia.0000ffff"];
        public void Log(string line)
        {
        }

        public string LogPath => "";
        public IDism Dism => throw new NotSupportedException();
        public IIsoWriter IsoWriter => throw new NotSupportedException();
        public IIsoReader IsoReader => throw new NotSupportedException();
        public Task DownloadAsync(EsdFile file, string target, IProgress<(long Done, long Total)>? p, CancellationToken ct) => throw new NotSupportedException();
        public WorkFolder NewWorkFolder(string outputIso) => throw new NotSupportedException();
        public void DeleteWorkFolder(string folder) => throw new NotSupportedException();
        public long FreeBytes(string path) => throw new NotSupportedException();
        public IOfflineHive LoadSystemHive(string file) => throw new NotSupportedException();
        public IWritableHive LoadWritableHive(string file) => throw new NotSupportedException();
        public void UnloadLeftoverHives() => throw new NotSupportedException();
    }

    /// <summary>
    /// The cleanup can wait minutes for a DISM a killed build left running: meanwhile the banner says it cleans up, and Stop
    /// ends the build at once (the cleanup goes on by itself).
    /// </summary>
    [Fact]
    public async Task ABuildWaitingForTheCleanupSaysSoAndStops()
    {
        var cleanup = new TaskCompletionSource();
        var media = await WithCatalog(Media(cleanup: _ => cleanup.Task));
        var cleaning = media.CleanLeftoversAsync();
        var build = media.BuildCommand.ExecuteAsync(null);
        Assert.True(media.IsBuilding);
        Assert.Equal(Loc.Instance["Media_Building"], _host.Runner.Work.Title);
        Assert.StartsWith(Loc.Instance["Media_Stage_Cleanup"], _host.Runner.Work.Detail);
        Assert.True(_host.Runner.Work.CanStop);

        await media.StopAndWaitAsync();
        await build;
        Assert.False(media.IsBuilding);
        Assert.Empty(_requests);
        Assert.Equal(Loc.Instance["Media_Stopped"], media.ResultText);
        Assert.False(cleaning.IsCompleted);
        cleanup.SetResult();
        await cleaning;
    }

    /// <summary>A default name that is already taken in Downloads is not replaced: the page picks a free one.</summary>
    [Fact]
    public async Task TheDefaultOutputNameIsAFreeOne()
    {
        var media = await WithCatalog();
        var taken = media.OutputPath!;
        File.WriteAllText(taken, "an older ISO");
        media.AllowUnsupported = true;
        Assert.NotEqual(taken, media.OutputPath);
        Assert.EndsWith(" (2).iso", media.OutputPath);
        Assert.True(File.Exists(taken));
    }

    // ---- languages and summaries ----

    /// <summary>A full reload (after a change elsewhere) keeps the catalog and names its languages in the new language.</summary>
    [Fact]
    public async Task AReloadRelabelsTheCatalog()
    {
        Loc.Instance.SetLanguage("en");
        var media = await WithCatalog();
        Loc.Instance.SetLanguage("de");
        await media.ReloadCommand.ExecuteAsync(null);
        Assert.True(media.HasCatalog);
        Assert.Equal(new CultureInfo("de-DE").DisplayName, media.Languages.Single(l => l.Key == "de-de").Text);
        Assert.NotNull(media.CatalogText);
    }

    [Fact]
    public async Task FoldedCardsSayWhatTheyHold()
    {
        var media = await WithCatalog();
        Assert.Contains("Anna", media.SetupSummary);
        Assert.Contains(media.Region!.Text, media.SetupSummary);
        Assert.Equal(media.Profile!.Text, media.ProfileSummary);
        media.SkipSetup = false;
        media.ApplyProfile = false;
        media.RemoveApps = false;
        Assert.Equal(Loc.Instance["Media_Off"], media.SetupSummary);
        Assert.Equal(Loc.Instance["Media_Off"], media.ProfileSummary);
        Assert.Equal(Loc.Instance["Media_Off"], media.AppsSummary);
    }

    [Fact]
    public async Task ACatalogErrorThatIsNoDownloadErrorKeepsItsMessageOutOfTheText()
    {
        var media = new InstallMediaViewModel(_host, _dialogs, new ThrowingCatalog(), (_, _, _) => Task.FromResult(new MediaBuildResult("", 0, "")), System());
        await Loaded(media);
        await media.CheckCatalogCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Instance.Format("Media_CatalogFailed", Loc.Instance["Media_CatalogStepUnknown"]), media.CatalogError);
        Assert.DoesNotContain("secret detail", media.CatalogError);
    }

    // ---- reading an own ISO again, states and summaries (fix round 2) ----

    private string IsoFile(string name = "Win11.iso")
    {
        var iso = Path.Combine(_root, name);
        File.WriteAllText(iso, "iso");
        return iso;
    }

    /// <summary>A read that failed (the ISO was mounted in Explorer) is tried again when the same file is picked again.</summary>
    [Fact]
    public async Task PickingTheSameIsoAgainReadsItAgain()
    {
        var reads = 0;
        var system = System() with
        {
            ReadIso = (_, _) => ++reads == 1 ? throw new IsoAttachException(32, "in use") : Task.FromResult(_iso),
        };
        var media = await Loaded(Media(system: system));
        _dialogs.OpenFile = IsoFile();
        media.PickIsoCommand.Execute(null);
        await media.IsoLoading;
        Assert.Equal(Loc.Instance["Media_IsoInUse"], media.BuildNote);
        media.PickIsoCommand.Execute(null);
        await media.IsoLoading;
        Assert.Equal(2, reads);
        Assert.True(media.CanBuild);
    }

    /// <summary>A second ISO starts from this PC's edition, not from the image index chosen in the first one.</summary>
    [Fact]
    public async Task AnotherIsoPreselectsByEditionNotByIndex()
    {
        var media = await WithOwnIso();
        media.Edition = media.Editions.Single(e => e.Key == "1");   // Home in the first ISO
        _iso = Iso(Image(1, "Education", "Windows 11 Education"), Image(2, "Professional", "Windows 11 Pro"));
        media.IsoPath = IsoFile("Other.iso");
        await media.IsoLoading;
        Assert.Equal("2", media.Edition?.Key);
    }

    [Fact]
    public async Task ARefusedIsoIsNotCalledWindows11()
    {
        _iso = Iso(Image(1, "Professional", "Windows 10 Pro", build: 19045));
        var media = await WithOwnIso();
        Assert.DoesNotContain("Windows 11", media.IsoText);
        Assert.Contains("19045", media.IsoText);
        Assert.Equal(Loc.Instance["Media_NotWindows11"], media.BuildNote);
    }

    [Fact]
    public async Task AnUnexpectedErrorWhileReadingIsAReadFailure()
    {
        var media = await WithOwnIso(Media(system: System() with { ReadIso = (_, _) => throw new InvalidOperationException("odd") }));
        Assert.Equal(Loc.Instance["Media_IsoReadFailed"], media.BuildNote);
        Assert.True(media.IsoLoading.IsCompletedSuccessfully);
    }

    /// <summary>An install image whose metadata could not be read is a read failure, not "not Windows 11".</summary>
    [Fact]
    public async Task UnreadableMetadataIsAReadFailure()
    {
        _iso = new IsoInfo("install.wim", [], HasBootWim: true);
        var media = await WithOwnIso();
        Assert.Equal(Loc.Instance["Media_IsoReadFailed"], media.BuildNote);
    }

    [Fact]
    public async Task SetupSummaryWithoutANameShowsNoEmptyAccount()
    {
        var media = await WithCatalog();
        media.AccountNameText = "";
        Assert.Equal(Loc.Instance.Format("Media_SetupSummaryNoName", media.Region!.Text), media.SetupSummary);
    }

    /// <summary>A region brings its keyboard along, until a keyboard was picked by hand.</summary>
    [Fact]
    public async Task ARegionPreselectsItsKeyboardUntilOneIsPicked()
    {
        var media = await WithCatalog();
        media.Region = media.Regions.Single(r => r.Key == "de-AT");
        Assert.Equal(MediaLocales.DefaultKeyboard(new CultureInfo("de-AT")), media.Keyboard?.Key);
        media.Region = media.Regions.Single(r => r.Key == "en-US");
        Assert.Equal("0409:00000409", media.Keyboard?.Key);
        media.Keyboard = media.Keyboards.Single(k => k.Key == "0407:00000407");
        media.Region = media.Regions.Single(r => r.Key == "de-AT");
        Assert.Equal("0407:00000407", media.Keyboard?.Key);
    }

    /// <summary>A timeout of the catalog request (HttpClient's TaskCanceledException) shows the catalog failure.</summary>
    [Fact]
    public async Task ACatalogTimeoutShowsTheFailure()
    {
        var media = new InstallMediaViewModel(_host, _dialogs, new TimingOutCatalog(), (_, _, _) => Task.FromResult(new MediaBuildResult("", 0, "")), System());
        await Loaded(media);
        await media.CheckCatalogCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Instance.Format("Media_CatalogFailed", Loc.Instance["Media_CatalogStepUnknown"]), media.CatalogError);
        Assert.False(media.IsCheckingCatalog);
    }

    private sealed class TimingOutCatalog : IMediaCatalogSource
    {
        public Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<EsdFile>>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
    }

    private sealed class ThrowingCatalog : IMediaCatalogSource
    {
        public Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct) =>
            throw new InvalidOperationException("secret detail");
    }

    // ---- remembered, saved, loaded and reset choices ----

    /// <summary>A complete request file for a download; tests change a field or add one.</summary>
    private System.Text.Json.Nodes.JsonObject Saved() => new()
    {
        ["source"] = "download",
        ["language"] = "de-de",
        ["edition"] = "Core",
        ["removeApps"] = new System.Text.Json.Nodes.JsonArray("Microsoft.BingNews"),
        ["skipSetup"] = new System.Text.Json.Nodes.JsonObject { ["accountName"] = "Berta", ["region"] = "de-DE", ["keyboard"] = "0407:00000407" },
        ["profile"] = "gaming",
        ["allowUnsupported"] = true,
        ["output"] = Path.Combine(_root, "Old-name.iso"),
        ["installApps"] = new System.Text.Json.Nodes.JsonArray("Valve.Steam"),
        ["removeUnreviewed"] = new System.Text.Json.Nodes.JsonArray(),
    };

    private string Write(System.Text.Json.Nodes.JsonObject json, string name = "media-choices.json")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    [Fact]
    public async Task PageOpensWithTheLastBuildsChoices()
    {
        Write(Saved());
        var media = await WithCatalog();
        Assert.Null(media.ChoicesNote);
        Assert.True(media.UseDownload);
        Assert.Equal("Core", media.Edition?.Key);
        Assert.Equal("de-de", media.Language?.Key);
        Assert.Equal("Berta", media.AccountNameText);
        Assert.Equal("de-DE", media.Region?.Key);
        Assert.Equal("0407:00000407", media.Keyboard?.Key);
        Assert.Equal("gaming", media.Profile?.Key);
        Assert.True(media.ApplyProfile);
        Assert.True(media.AllowUnsupported);
        Assert.True(media.InstallApps);
        Assert.Equal(["Valve.Steam"], media.InstallRows.Where(r => r.IsChecked).Select(r => r.Key));
        // The folder is remembered, not the file name: a new build gets a new name there.
        Assert.Equal(_root, Path.GetDirectoryName(media.OutputPath));
        Assert.NotEqual("Old-name.iso", Path.GetFileName(media.OutputPath));

        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(["Microsoft.BingNews"], Assert.Single(_requests).RemoveApps);
    }

    [Fact]
    public async Task UnusableRememberedValuesAreDroppedAndNamed()
    {
        var json = Saved();
        json["source"] = Path.Combine(_root, "moved.iso");
        json["profile"] = "old";
        json["removeApps"] = new System.Text.Json.Nodes.JsonArray("Microsoft.BingNews", "Contoso.NotAnApp");
        Write(json);
        var media = await WithCatalog();

        Assert.True(media.UseDownload);
        Assert.Null(media.IsoPath);
        Assert.NotEqual("old", media.Profile?.Key);
        Assert.Equal("Berta", media.AccountNameText);
        Assert.Equal(InfoBarSeverity.Warning, media.ChoicesNoteSeverity);
        Assert.Contains(Loc.Instance["Media_Field_source"], media.ChoicesNote);
        Assert.Contains(Loc.Instance["Media_Field_profile"], media.ChoicesNote);
        Assert.Contains("Contoso.NotAnApp", media.ChoicesNote);
        Assert.DoesNotContain("BingNews", media.ChoicesNote);
        // Nothing unchecked reaches a build.
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(["Microsoft.BingNews"], Assert.Single(_requests).RemoveApps);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    public async Task ARememberedFileThatIsNoRequestIsIgnored(string text)
    {
        File.WriteAllText(RememberedFile, text);
        var media = await WithCatalog();
        Assert.Null(media.ChoicesNote);
        Assert.Equal("Anna", media.AccountNameText);
        Assert.True(media.CanBuild);
    }

    [Fact]
    public async Task ARememberedOwnIsoComesBackWithItsEdition()
    {
        var iso = Path.Combine(_root, "Win11.iso");
        File.WriteAllText(iso, "iso");
        var json = Saved();
        json["source"] = iso;
        json.Remove("language");
        json["edition"] = "Core";
        Write(json);
        var media = await Loaded(Media());
        await media.IsoLoading;
        Assert.False(media.UseDownload);
        Assert.Equal(iso, media.IsoPath);
        Assert.Equal("1", media.Edition?.Key);
        Assert.Null(media.ChoicesNote);

        // The image is gone from the ISO: the page keeps its default edition and says so.
        json["edition"] = "Education";
        Write(json);
        media = await Loaded(Media());
        await media.IsoLoading;
        Assert.Equal("2", media.Edition?.Key);
        Assert.Contains(Loc.Instance["Media_Field_edition"], media.ChoicesNote);
    }

    [Fact]
    public async Task OnlyASuccessfulBuildRemembersItsChoices()
    {
        var media = await WithCatalog();
        media.AccountNameText = "Berta";
        _build = (_, _) => throw new MediaBuildException(MediaStage.Unpack, "x", 87);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.False(File.Exists(RememberedFile));

        _build = null;
        await media.BuildCommand.ExecuteAsync(null);
        var file = MediaRequestFile.Parse(File.ReadAllText(RememberedFile), Keyboards);
        Assert.Equal("Berta", file.SkipSetup?.AccountName);
        Assert.Equal(media.OutputPath, file.Output);
        Assert.True(file.IsDownload);

        // A page opened without administrator rights builds nothing, so it remembers nothing either.
        File.Delete(RememberedFile);
        var guest = await WithCatalog(Media(system: System() with { Elevated = false }));
        Assert.False(guest.CanBuild);
        Assert.False(File.Exists(RememberedFile));
    }

    [Fact]
    public async Task LoadRefusesAFileThatDoesNotParse()
    {
        var media = await WithCatalog();
        media.AccountNameText = "Anna";
        var json = Saved();
        json["edition"] = 5;
        _dialogs.OpenFile = Write(json, "bad.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Equal(InfoBarSeverity.Warning, media.ChoicesNoteSeverity);
        Assert.Contains(Loc.Instance["Media_Field_edition"], media.ChoicesNote);
        Assert.DoesNotContain("must be", media.ChoicesNote);
        Assert.Equal("Anna", media.AccountNameText);
        Assert.NotEqual("Core", media.Edition?.Key);
        Assert.False(media.AllowUnsupported);

        _dialogs.OpenFile = Path.Combine(_root, "missing.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Contains("missing.json", media.ChoicesNote);

        // Not a request file at all: its own sentence. German names the field in German.
        File.WriteAllText(Path.Combine(_root, "junk.json"), "[1]");
        _dialogs.OpenFile = Path.Combine(_root, "junk.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Equal(Loc.Instance["Media_ChoicesNotRequest"], media.ChoicesNote);
        Loc.Instance.SetLanguage("de");
        _dialogs.OpenFile = Path.Combine(_root, "bad.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Contains("Problem bei", media.ChoicesNote);
        json["theme"] = 5;
        json["edition"] = "Core";
        _dialogs.OpenFile = Write(json, "bad3.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Contains("Problem bei: Design.", media.ChoicesNote);
        json.Remove("theme");
        json["skipSetup"]!["region"] = 5;
        json["edition"] = "Core";
        _dialogs.OpenFile = Write(json, "bad2.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Contains("Konto und Einrichtungsfragen", media.ChoicesNote);
        Assert.DoesNotContain("must be", media.ChoicesNote);
    }

    [Fact]
    public void EveryRequestFileFieldHasALabelInBothLanguages()
    {
        foreach (var language in new[] { "en", "de" })
        {
            Loc.Instance.SetLanguage(language);
            foreach (var field in MediaRequestFile.FieldNames)
                Assert.NotEqual($"Media_Field_{field}", Loc.Instance[$"Media_Field_{field}"]);
        }
    }

    [Fact]
    public async Task LoadAppliesAGoodFileAndKeepsItsOutputFile()
    {
        var media = await WithCatalog();
        _dialogs.OpenFile = Write(Saved(), "mine.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.Equal(InfoBarSeverity.Success, media.ChoicesNoteSeverity);
        Assert.Contains("mine.json", media.ChoicesNote);
        Assert.Equal("Berta", media.AccountNameText);
        Assert.Equal("gaming", media.Profile?.Key);
        Assert.Equal(Path.Combine(_root, "Old-name.iso"), media.OutputPath);
        Assert.False(File.Exists(RememberedFile));
    }

    [Fact]
    public async Task TheInstallImageDefaultsToFasterBuildAndTheChoiceReachesTheRequest()
    {
        var media = await WithCatalog();
        Assert.False(media.SmallerIso);
        Assert.True(media.FasterBuild);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(MediaCompression.Wim, Assert.Single(_requests).Compression);

        _requests.Clear();
        media.SmallerIso = true;
        Assert.False(media.FasterBuild);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(MediaCompression.Recovery, Assert.Single(_requests).Compression);
    }

    [Fact]
    public async Task SmallerIsoChangesTheBarTheSpaceAndTheSavedFile()
    {
        var media = await WithCatalog();
        var changed = new List<string?>();
        media.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var bytes = media.NeededText;
        media.SmallerIso = true;
        Assert.Contains(nameof(InstallMediaViewModel.SummaryText), changed);
        Assert.Contains(nameof(InstallMediaViewModel.NeededText), changed);
        Assert.NotEqual(bytes, media.NeededText);
        Assert.Contains(Loc.Instance["Media_CompressionSmaller"], media.SummaryText);
        Assert.Contains(media.ReviewRequest()!.Changes, l => l.Target == Loc.Instance["Media_CompressionTitle"]);

        _dialogs.SaveFile = Path.Combine(_root, "smaller.json");
        media.SaveChoicesCommand.Execute(null);
        Assert.Equal(MediaCompression.Recovery, MediaRequestFile.Parse(File.ReadAllText(_dialogs.SaveFile), Keyboards).Compression);

        media.FasterBuild = true;
        Assert.False(media.SmallerIso);
        Assert.DoesNotContain(Loc.Instance["Media_CompressionSmaller"], media.SummaryText);
    }

    [Fact]
    public async Task ALoadedFileSetsTheInstallImageAndResetGoesBackToFasterBuild()
    {
        var media = await WithCatalog();
        var json = Saved();
        json["compression"] = "recovery";
        _dialogs.OpenFile = Write(json, "smaller.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.True(media.SmallerIso);

        media.ResetChoicesCommand.Execute(null);
        Assert.True(media.FasterBuild);
    }

    [Fact]
    public async Task TheImageSettingsAreOffAndReachTheRequestSummaryReviewAndSavedFile()
    {
        var media = await WithCatalog();
        Assert.False(media.PreventDeviceEncryption);
        Assert.False(media.NoReservedStorage);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(MediaImageSettings.None, Assert.Single(_requests).Settings);

        _requests.Clear();
        var changed = new List<string?>();
        media.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        media.PreventDeviceEncryption = true;
        media.NoReservedStorage = true;
        Assert.Contains(nameof(InstallMediaViewModel.SummaryText), changed);
        Assert.Contains(Loc.Instance["Media_EncryptionTitle"], media.SummaryText);
        Assert.Contains(Loc.Instance["Media_ReservedTitle"], media.SummaryText);
        var review = media.ReviewRequest()!;
        Assert.Contains(review.Changes, l => l.Target == Loc.Instance["Media_EncryptionTitle"]);
        Assert.Contains(review.Changes, l => l.Target == Loc.Instance["Media_ReservedTitle"]);
        Assert.Equal(2, review.Warnings.Count(w => w.Contains(Loc.Instance["Media_EncryptionText"], StringComparison.Ordinal) || w.Contains(Loc.Instance["Media_ReservedText"], StringComparison.Ordinal)));

        _dialogs.SaveFile = Path.Combine(_root, "settings.json");
        media.SaveChoicesCommand.Execute(null);
        var saved = MediaRequestFile.Parse(File.ReadAllText(_dialogs.SaveFile), Keyboards);
        Assert.True(saved.PreventDeviceEncryption);
        Assert.True(saved.NoReservedStorage);

        await media.BuildCommand.ExecuteAsync(null);
        Assert.Equal(new MediaImageSettings(true, true, false), Assert.Single(_requests).Settings);
    }

    [Fact]
    public async Task ALoadedFileSetsTheImageSettingsAndResetTurnsThemOff()
    {
        var media = await WithCatalog();
        var json = Saved();
        json["preventDeviceEncryption"] = true;
        json["noReservedStorage"] = true;
        _dialogs.OpenFile = Write(json, "settings.json");
        media.LoadChoicesCommand.Execute(null);
        Assert.True(media.PreventDeviceEncryption);
        Assert.True(media.NoReservedStorage);

        media.ResetChoicesCommand.Execute(null);
        Assert.False(media.PreventDeviceEncryption);
        Assert.False(media.NoReservedStorage);
    }

    [Fact]
    public async Task TheStageListIncludesSettingsWhenAnImageSettingIsOn()
    {
        var media = await WithCatalog();
        media.RemoveApps = false;
        media.PreventDeviceEncryption = true;
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Contains(MediaStage.Settings, media.Stages.Select(s => s.Stage));
    }

    [Fact]
    public async Task SaveWritesTheChoicesWithACaptionThatNamesWhatTheFileHolds()
    {
        var media = await WithCatalog();
        media.AccountNameText = "Berta";
        media.AllowUnsupported = true;
        _dialogs.SaveFile = Path.Combine(_root, "mine.json");
        media.SaveChoicesCommand.Execute(null);

        var (name, filter, extension, title) = Assert.Single(_dialogs.SaveRequests);
        Assert.Equal(("Windows11_choices.json", ".json"), (name, extension));
        Assert.Equal(Loc.Instance["Media_ChoicesFiles"], filter);
        Assert.Equal(Loc.Instance["Media_SaveChoicesTitle"], title);
        var file = MediaRequestFile.Parse(File.ReadAllText(_dialogs.SaveFile), Keyboards);
        Assert.Equal("Berta", file.SkipSetup?.AccountName);
        Assert.True(file.AllowUnsupported);
        Assert.Equal(media.OutputPath, file.Output);
        Assert.Equal(InfoBarSeverity.Success, media.ChoicesNoteSeverity);

        // Cancelling the dialog writes nothing.
        _dialogs.SaveFile = null;
        media.SaveChoicesCommand.Execute(null);
        Assert.Equal(2, _dialogs.SaveRequests.Count);
    }

    [Fact]
    public async Task SaveNeedsAnImageToSave()
    {
        var media = await Loaded(Media());
        Assert.False(media.SaveChoicesCommand.CanExecute(null));
        await media.CheckCatalogCommand.ExecuteAsync(null);
        Assert.True(media.SaveChoicesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetRestoresTheDefaults()
    {
        Loc.Instance.SetLanguage("en");
        Write(Saved());
        var media = await WithCatalog();
        media.Language = media.Languages.Single(l => l.Key == "en-us");
        media.RemoveApps = false;
        media.AppGroups.First().AllChecked = false;
        media.SkipSetup = false;
        media.AccountNameText = "Berta";
        media.Region = media.Regions.Single(r => r.Key == "hi-IN");
        media.AllowUnsupported = true;
        media.InstallApps = true;
        media.RemoveUnreviewed = true;
        media.OutputPath = Path.Combine(_root, "mine.iso");
        Assert.True(File.Exists(RememberedFile));

        media.ResetChoicesCommand.Execute(null);
        Assert.False(File.Exists(RememberedFile));
        Assert.True(media.UseDownload);
        Assert.True(media.RemoveApps);
        Assert.True(media.SkipSetup);
        Assert.False(media.AllowUnsupported);
        Assert.False(media.InstallApps);
        Assert.False(media.RemoveUnreviewed);
        Assert.DoesNotContain(media.InstallRows, r => r.IsChecked);
        Assert.Equal("Anna", media.AccountNameText);
        Assert.Equal("de-AT", media.Region?.Key);
        Assert.Equal(MediaLocales.DefaultKeyboard(new CultureInfo("de-AT")), media.Keyboard?.Key);
        Assert.Equal("Professional", media.Edition?.Key);
        Assert.Equal("en-us", media.Language?.Key);
        Assert.Equal("office", media.Profile?.Key);
        Assert.Equal(_root, Path.GetDirectoryName(media.OutputPath));
        Assert.StartsWith("Windows11_", Path.GetFileName(media.OutputPath));
        var consumer = media.AppRows.Where(r => r.Entry.Group == "consumer").ToList();
        Assert.NotEmpty(consumer);
        Assert.All(consumer, r => Assert.True(r.IsChecked));
        Assert.All(media.AppRows.Where(r => r.Entry.Group != "consumer"), r => Assert.False(r.IsChecked));
    }
}
