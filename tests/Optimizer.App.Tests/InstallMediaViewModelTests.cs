using System.IO;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The install media page without the window: a fake catalog, a build that only records the request (or fails as told),
/// and a temp folder for the files the page checks. Nothing downloads, runs DISM or writes outside the temp folder.
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

    public InstallMediaViewModelTests() => Directory.CreateDirectory(_root);

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

    private sealed class FakeCatalog : IMediaCatalogSource
    {
        public Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct) => Task.FromResult(Files);
    }

    private string Exe()
    {
        var exe = Path.Combine(_root, "Tunero.exe");
        File.WriteAllText(exe, "MZ");
        return exe;
    }

    private InstallMediaViewModel Media(string? edition = "Professional", bool published = true) => new(_host, _dialogs, new FakeCatalog(),
        (request, progress, ct) =>
        {
            _requests.Add(request);
            progress.Report(new MediaProgress(MediaStage.Unpack, 50, null));
            return _build?.Invoke(request, ct) ?? Task.FromResult(request.OutputIso);
        },
        new InstallMediaSystem
        {
            PcEdition = edition,
            Elevated = true,
            ExePath = published ? Exe() : null,
            IsPublishedExe = published,
            OutputFolder = _root,
            ProfileId = () => "office",
            UserName = "Anna",
            FreeBytes = _ => long.MaxValue,
        });

    private static async Task<InstallMediaViewModel> Loaded(InstallMediaViewModel media)
    {
        await media.EnsureLoadedAsync();
        return media;
    }

    [Fact]
    public async Task DefaultsFollowThisPc()
    {
        Loc.Instance.SetLanguage("de");
        var media = await Loaded(Media("Core"));
        Assert.Equal("Core", media.Edition?.Key);
        Assert.Equal("office", media.Profile?.Key);
        await media.CheckCatalogCommand.ExecuteAsync(null);
        Assert.Equal("de-de", media.Language?.Key);
        Assert.Equal("Core", media.Edition?.Key);

        // An edition the media does not offer (Enterprise): Pro.
        Assert.Equal("Professional", (await Loaded(Media("Enterprise"))).Edition?.Key);
    }

    [Fact]
    public async Task InvalidAccountNameBlocksBuild()
    {
        var media = await Loaded(Media());
        await media.CheckCatalogCommand.ExecuteAsync(null);
        media.SkipSetup = true;
        media.AccountNameText = "Anna";
        Assert.Null(media.AccountProblem);
        Assert.True(media.CanBuild);
        media.AccountNameText = "a/b";
        Assert.Equal(Loc.Instance["Media_NameInvalid"], media.AccountProblem);
        Assert.False(media.CanBuild);
    }

    [Fact]
    public async Task OwnIsoNeedsAFile()
    {
        var media = await Loaded(Media());
        media.UseDownload = false;
        Assert.Null(media.IsoPath);
        Assert.False(media.CanBuild);
        var iso = Path.Combine(_root, "Win11.iso");
        File.WriteAllText(iso, "iso");
        media.IsoPath = iso;
        Assert.True(media.CanBuild);
    }

    [Fact]
    public async Task BuildPassesTheChoices()
    {
        var media = await Loaded(Media());
        await media.CheckCatalogCommand.ExecuteAsync(null);
        media.Language = media.Languages.Single(l => l.Key == "en-us");
        media.RemoveApps = true;
        media.SkipSetup = true;
        media.AccountNameText = "Anna";
        media.AllowUnsupported = true;
        media.ApplyProfile = true;
        media.Profile = media.Profiles.Single(p => p.Key == "gaming");
        await media.BuildCommand.ExecuteAsync(null);

        var r = Assert.Single(_requests);
        var consumer = CatalogData.Current.Appx.Apps.Where(a => a.Group == "consumer").Select(a => a.Name).Order();
        Assert.Equal(consumer, r.RemoveApps.Order());
        Assert.Equal("en-us", r.Source.Download?.LanguageCode);
        Assert.Equal("Professional", r.Edition);
        Assert.Equal("Professional", r.Source.Download?.Edition);
        Assert.True(r.Answers.AllowUnsupported);
        Assert.True(r.Answers.SkipSetup);
        Assert.Equal("Anna", r.Answers.AccountName);
        Assert.Equal("amd64", r.Answers.Architecture);
        Assert.Equal("en-US", r.Answers.UiLanguage);
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
        var media = await Loaded(Media());
        await media.CheckCatalogCommand.ExecuteAsync(null);
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
        var media = await Loaded(Media());
        await media.CheckCatalogCommand.ExecuteAsync(null);
        await media.BuildCommand.ExecuteAsync(null);
        Assert.Contains(Loc.Instance["Media_NotEnoughSpace"], media.ResultText);
    }

    [Fact]
    public async Task AppRowsComeFromTheAllowlist()
    {
        var media = await Loaded(Media());
        var appx = CatalogData.Current.Appx;
        Assert.Equal(appx.Apps.Count, media.AppRows.Count);
        Assert.DoesNotContain(media.AppRows, r => appx.IsProtected(r.Entry.Name));
        Assert.All(media.AppRows, r => Assert.Equal(r.Entry.Group == "consumer", r.IsChecked));
        Assert.Equal(appx.Apps.Select(a => a.Group).Distinct().Count(), media.AppGroups.Count);
    }

    [Fact]
    public async Task ProfileNeedsTheSingleFileExe()
    {
        var media = await Loaded(Media(published: false));
        Assert.False(media.CanApplyProfile);
        Assert.False(media.ApplyProfile);
        Assert.Equal(Loc.Instance["Media_ProfileNeedsRelease"], media.ProfileNote);
    }

    [Fact]
    public async Task StopEndsTheBuildAndWaits()
    {
        var started = new TaskCompletionSource();
        var media = await Loaded(Media());
        _build = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        await media.CheckCatalogCommand.ExecuteAsync(null);
        var build = media.BuildCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(media.IsBuilding);
        await media.StopAndWaitAsync();
        Assert.False(media.IsBuilding);
        await build;
        Assert.Equal(Loc.Instance["Media_Stopped"], media.ResultText);
    }
}
