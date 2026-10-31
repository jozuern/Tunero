using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>
/// The build stages over fakes: every DISM, download, ISO and folder step is recorded, nothing runs dism, attaches an ISO
/// or touches a drive root. The fakes write their files into a temp folder that stands in for the output drive.
/// </summary>
public sealed class MediaBuildTests : IDisposable
{
    private readonly string _root = TestFolders.Create("mediabuild");
    private readonly FakeMediaSteps _steps;

    public MediaBuildTests() => _steps = new FakeMediaSteps(_root);

    public void Dispose() => TestFolders.Delete(_root);

    private string OutputIso => Path.Combine(_root, "Win11.iso");

    private static readonly EsdFile Esd = new(
        "26300.9711.260410-1914.br_release_CLIENTCONSUMER_RET_x64FRE_en-us.esd", "en-us", "English (United States)", "Professional", "x64",
        5L << 30, new string('a', 64), new Uri("http://dl.delivery.mp.microsoft.com/filestreamingservice/files/x.esd"));

    private static AnswerChoices Plain => new("amd64", "en-US", "0409:00000409", "en-US", "en-US", null, false, false, false);

    private static FirstSignInFile First => new(CatalogData.Current.Profiles.Default.Id, "en", "System");

    private MediaRequest Download(string edition = "Professional", IReadOnlyList<string>? apps = null, AnswerChoices? answers = null) =>
        new(new MediaSource(Esd, null, null), edition, null, apps ?? [], answers ?? Plain, null, null, OutputIso);

    private MediaRequest OwnIso(string edition = "Professional", int? index = 2)
    {
        var iso = Path.Combine(_root, "source.iso");
        File.WriteAllBytes(iso, new byte[4096]);
        return new(new MediaSource(null, iso, null), edition, index, [], Plain, null, null, OutputIso);
    }

    private MediaRequest WithTunero(MediaRequest r)
    {
        var exe = Path.Combine(_root, "Tunero-running.exe");
        File.WriteAllBytes(exe, Encoding.ASCII.GetBytes("MZ fake Tunero"));
        return r with { Answers = r.Answers with { FirstSignInTunero = true }, TuneroExe = exe, FirstSignIn = First };
    }

    private MediaBuild Build(TimeSpan? dismWait = null) =>
        new(_steps) { RetryDelay = TimeSpan.Zero, DismPollDelay = TimeSpan.Zero, DismWaitLimit = dismWait ?? TimeSpan.FromMinutes(15) };

    private Task<MediaBuildResult> Run(MediaRequest r, CancellationToken ct = default, IProgress<MediaProgress>? progress = null) =>
        Build().RunAsync(r, progress ?? new Collect(_steps.Progress), ct);

    private async Task<MediaBuildException> Fails(MediaRequest r) => await Assert.ThrowsAsync<MediaBuildException>(() => Run(r));

    private async Task<MediaBuildException> FailsAt(MediaRequest r, MediaStage stage, string? key)
    {
        var ex = await Fails(r);
        Assert.Equal(stage, ex.Stage);
        Assert.Equal(key, ex.Key);
        return ex;
    }

    private int Count(string call) => _steps.Calls.Count(c => c == call);

    private void AssertCleanedUp()
    {
        Assert.Contains("DeleteWorkFolder", _steps.Calls);
        Assert.False(Directory.Exists(_steps.WorkFolder), "work folder left behind");
        Assert.False(File.Exists(OutputIso), "ISO at the output path");
        Assert.Empty(_steps.Dism.Mounted);
        Assert.Empty(_steps.LoadedHives);
        Assert.Empty(_steps.LoadedWritableHives);
    }

    // ---- stage order and the download path ----

    [Fact]
    public async Task DownloadSourceRunsTheStagesInOrder()
    {
        Assert.Equal(OutputIso, (await Run(Download())).Iso);
        Assert.Equal(
        [
            "NewWorkFolder", "Download",
            "WimInfo image.esd", "WimInfo image.esd 1", "WimInfo image.esd 2", "WimInfo image.esd 3",
            "Apply image.esd 1", "Export image.esd 2 boot.wim", "Export image.esd 3 boot.wim bootable",
            "WimInfo image.esd 4", "WimInfo image.esd 5", "WimInfo image.esd 6", "WimInfo image.esd 7",
            "Export image.esd 6 install.wim",
            "WimInfo install.wim", "WimInfo install.wim 1", "WimInfo boot.wim", "WimInfo boot.wim 2",
            "Mount boot.wim 2", "LoadHive", "UnloadHive", "Unmount discard",
            "WriteIso", "VerifyIso", "DeleteWorkFolder",
        ], _steps.Calls);
        Assert.Equal("ISO WIN11_26300_X64", File.ReadAllText(OutputIso));
        Assert.False(Directory.Exists(_steps.WorkFolder));
        var stages = _steps.Progress.Where(p => p.Percent is null).Select(p => p.Stage).Distinct();
        Assert.Equal([MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Files, MediaStage.Iso, MediaStage.Verify, MediaStage.Cleanup], stages);
        Assert.Contains(_steps.Progress, p => p.Stage == MediaStage.Unpack && p.Percent == 50);
        // The ESD is deleted once the media is unpacked; the ISO holds the media only.
        Assert.DoesNotContain(_steps.IsoContents.Keys, k => k.EndsWith(".esd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"sources\install.wim", _steps.IsoContents.Keys);
        Assert.Contains(@"sources\boot.wim", _steps.IsoContents.Keys);
    }

    [Fact]
    public async Task ResultCarriesSizeAndHashOfThePublishedIso()
    {
        var result = await Run(Download());
        var bytes = File.ReadAllBytes(OutputIso);
        Assert.Equal(OutputIso, result.Iso);
        Assert.Equal(bytes.Length, result.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(_steps.LogPath, result.LogPath);
    }

    [Fact]
    public async Task HashingTheIsoReportsProgressInTheVerifyStage()
    {
        await Run(Download());
        var verify = _steps.Progress.Where(p => p.Stage == MediaStage.Verify && p.Percent is not null).Select(p => p.Percent!.Value).ToList();
        Assert.Contains(verify, v => v >= 100);
        Assert.True(verify.All(v => v is >= 0 and <= 100));
    }

    /// <summary>The page lists these stages before the build; the engine must begin exactly them, in this order (not Catalog, not Cleanup).</summary>
    [Theory]
    [InlineData("download")]
    [InlineData("download apps")]
    [InlineData("download apps tunero")]
    [InlineData("iso")]
    [InlineData("iso apps")]
    [InlineData("iso unreviewed")]
    [InlineData("download smaller")]
    [InlineData("iso apps smaller")]
    [InlineData("download settings")]
    [InlineData("iso apps settings")]
    [InlineData("download settings smaller")]
    [InlineData("download parts")]
    [InlineData("iso apps parts")]
    [InlineData("iso parts settings smaller")]
    public async Task PlannedStagesAreExactlyTheStagesARunBegins(string kind)
    {
        var apps = kind.Contains("apps", StringComparison.Ordinal) ? new[] { "Microsoft.BingNews" } : [];
        var r = kind.StartsWith("download", StringComparison.Ordinal) ? Download(apps: apps) : OwnIso() with { RemoveApps = apps };
        if (kind.Contains("unreviewed", StringComparison.Ordinal)) r = r with { RemoveUnreviewed = ["Contoso.Notes"] };
        if (kind.Contains("tunero", StringComparison.Ordinal)) r = WithTunero(r);
        if (kind.Contains("smaller", StringComparison.Ordinal)) r = Smaller(r);
        if (kind.Contains("settings", StringComparison.Ordinal)) r = r with { Settings = new(true, true, false) };
        if (kind.Contains("parts", StringComparison.Ordinal)) r = r with { RemoveParts = ["wordpad"] };
        await Run(r);
        var begun = _steps.Progress.Where(p => p.Percent is null && p.Stage != MediaStage.Cleanup).Select(p => p.Stage).Distinct();
        Assert.Equal(MediaBuild.PlannedStages(r), begun);
        Assert.DoesNotContain(MediaStage.Catalog, MediaBuild.PlannedStages(r));
    }

    // ---- optional Windows parts ----

    [Fact]
    public async Task ChosenPartsAreRemovedAndMissingOnesAreLogged()
    {
        _steps.Dism.Capabilities = new() { ["Microsoft.Windows.WordPad~~~~0.0.1.0"] = "Installed", ["Language.Speech~~~en-US~0.0.1.0"] = "Installed" };
        _steps.Dism.Features = new() { ["Recall"] = "Enabled" };
        await Run(Download() with { RemoveParts = ["wordpad", "speech", "recall", "xps"] });
        Assert.Contains("RemoveCapability Microsoft.Windows.WordPad~~~~0.0.1.0", _steps.Calls);
        Assert.Contains("RemoveCapability Language.Speech~~~en-US~0.0.1.0", _steps.Calls);
        Assert.Contains("DisableFeature Recall", _steps.Calls);
        Assert.Contains(_steps.LogLines, l => l.Contains("XPS Viewer") && l.Contains("not present"));
        Assert.Equal(1, Count("Mount install.wim 1"));
        Assert.Equal(1, Count("Unmount commit"));
    }

    [Fact]
    public async Task APartThatIsNotInstalledOrEnabledIsLeftAlone()
    {
        _steps.Dism.Capabilities = new() { ["Microsoft.Windows.WordPad~~~~0.0.1.0"] = "Not Present", ["App.StepsRecorder~~~~0.0.1.0"] = "Installed" };
        _steps.Dism.Features = new() { ["Recall"] = "Disabled" };
        await Run(Download() with { RemoveParts = ["wordpad", "stepsRecorder", "recall"] });
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("RemoveCapability Microsoft.Windows.WordPad", StringComparison.Ordinal));
        Assert.Contains("RemoveCapability App.StepsRecorder~~~~0.0.1.0", _steps.Calls);
        Assert.DoesNotContain("DisableFeature Recall", _steps.Calls);
    }

    [Fact]
    public async Task APrefixMatchesEveryLanguageAndTextToSpeechTakesSpeechRecognitionFirst()
    {
        _steps.Dism.Capabilities = new()
        {
            ["Language.TextToSpeech~~~en-US~0.0.1.0"] = "Installed", ["Language.Speech~~~en-US~0.0.1.0"] = "Installed",
            ["Language.TextToSpeech~~~de-DE~0.0.1.0"] = "Installed", ["Language.Speech~~~de-DE~0.0.1.0"] = "Installed",
            ["Language.Basic~~~en-US~0.0.1.0"] = "Installed",
        };
        await Run(Download() with { RemoveParts = ["textToSpeech", "speech"] });
        var removed = _steps.Calls.Where(c => c.StartsWith("RemoveCapability ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, removed.Count);
        // Speech recognition depends on text to speech: every Speech goes before any TextToSpeech, none twice.
        Assert.True(removed.FindLastIndex(c => c.Contains("Language.Speech~~~")) < removed.FindIndex(c => c.Contains("Language.TextToSpeech~~~")));
        Assert.DoesNotContain(removed, c => c.Contains("Language.Basic"));
        Assert.Equal(removed.Count, removed.Distinct().Count());
    }

    [Fact]
    public async Task OnlyPartsInTheRequestReadTheImage()
    {
        await Run(Download() with { RemoveParts = ["recall"] });
        Assert.DoesNotContain("Capabilities", _steps.Calls);
        Assert.Contains("Features", _steps.Calls);
    }

    [Fact]
    public async Task APartFailureStopsTheBuildAtTheParts()
    {
        _steps.Dism.Capabilities = new() { ["App.StepsRecorder~~~~0.0.1.0"] = "Installed" };
        _steps.Dism.RemoveCapabilityFails = true;
        var ex = await Fails(Download() with { RemoveParts = ["stepsRecorder"] });
        Assert.Equal(MediaStage.Parts, ex.Stage);
        AssertCleanedUp();
    }

    [Fact]
    public void UnknownPartIsRefused() =>
        Assert.Throws<ArgumentException>(() => MediaBuild.Validate(Download() with { RemoveParts = ["defender"] }));

    [Fact]
    public void ACatalogPartIsAccepted() =>
        MediaBuild.Validate(Download() with { RemoveParts = ["wordpad"] });

    [Fact]
    public async Task EditionIsFoundByEditionId()
    {
        await Run(Download("CoreN"));
        Assert.Contains("Export image.esd 5 install.wim", _steps.Calls);
        _steps.Calls.Clear();
        await Run(Download("Professional"));
        Assert.Contains("Export image.esd 6 install.wim", _steps.Calls);
    }

    [Fact]
    public async Task ImageChangesShareOneMountAndOneCommit()
    {
        await Run(Download(apps: ["Microsoft.BingNews"]));
        Assert.Equal(1, Count("Mount install.wim 1"));
        Assert.Equal(1, Count("Unmount commit")); // Plain answers: boot.wim is not committed
        var i = _steps.Calls.IndexOf("ComponentCleanup");
        Assert.True(i > _steps.Calls.FindLastIndex(c => c.StartsWith("Remove", StringComparison.Ordinal)) && i < _steps.Calls.IndexOf("CheckHealth"));
    }

    [Fact]
    public async Task PendingOperationsSkipTheCleanupAndTheBuildGoesOn()
    {
        _steps.Dism.ComponentCleanupFails = MediaBuild.PendingOperations;
        Assert.Equal(OutputIso, (await Run(Download(apps: ["Microsoft.BingNews"]))).Iso);
        Assert.Contains(_steps.LogLines, l => l.Contains("0x800F0806", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherCleanupErrorsFailAtShrink()
    {
        _steps.Dism.ComponentCleanupFails = 2;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Shrink, null);
        AssertCleanedUp();
    }

    [Fact]
    public async Task MissingEditionFailsAtUnpack()
    {
        await FailsAt(Download("Education"), MediaStage.Unpack, "Media_EditionMissing");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal) && c.EndsWith("install.wim", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task TwoImagesWithTheEditionFailAtUnpack()
    {
        _steps.EsdImages.Add(new Img("Windows 11 Pro copy", "Professional"));
        await FailsAt(Download(), MediaStage.Unpack, "Media_ImageCheckFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task LocalizedEsdIsAccepted()
    {
        _steps.EsdImages[0] = _steps.EsdImages[0] with { Name = "Windows-Setupmedien" };
        _steps.EsdImages[5] = _steps.EsdImages[5] with { Name = "Windows 11 Pro (deutsch)" };
        Assert.Equal(OutputIso, (await Run(Download())).Iso);
    }

    [Fact]
    public async Task EsdWithoutItsBootImagesFailsAtCheck()
    {
        _steps.EsdImages[2] = new Img("Windows 11 Home", "Core");
        await FailsAt(Download(), MediaStage.Check, "Media_NotWindows11");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Apply", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task EsdWhoseFirstImageHasAnEditionFailsAtCheck()
    {
        _steps.EsdImages[0] = new Img("Windows 11 Home", "Core");
        await FailsAt(Download(), MediaStage.Check, "Media_NotWindows11");
        AssertCleanedUp();
    }

    [Fact]
    public async Task EsdWithTooFewImagesFailsAtCheck()
    {
        _steps.EsdImages.RemoveRange(3, _steps.EsdImages.Count - 3);
        await FailsAt(Download(), MediaStage.Check, "Media_NotWindows11");
        AssertCleanedUp();
    }

    [Fact]
    public async Task SetupFilesThatAlreadyHoldAnImageFail()
    {
        _steps.ApplyPlantsImage = true;
        await FailsAt(Download(), MediaStage.Unpack, "Media_ImageCheckFailed");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    // ---- request and output checks before anything runs ----

    [Fact]
    public async Task UnknownProfileForTheFirstSignInIsRefusedBeforeAnything()
    {
        var request = WithTunero(Download()) with { FirstSignIn = new FirstSignInFile("turbo", "en", "System") };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(request));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task TuneroWithoutItsAnswerFileEntryIsRefused()
    {
        var request = WithTunero(Download());
        await Assert.ThrowsAsync<ArgumentException>(() => Run(request with { Answers = Plain }));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task OnlyX64IsBuilt()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download(answers: Plain with { Architecture = "arm64" })));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download() with { Source = new MediaSource(Esd with { Architecture = "ARM64" }, null, null) }));
        Assert.Empty(_steps.Calls);
    }

    /// <summary>Keyboards and locales the answer file names must be ones Windows knows: anything else is refused before any step.</summary>
    [Theory]
    [InlineData("0407", null, null)]
    [InlineData("0407:0407", null, null)]
    [InlineData("German", null, null)]
    [InlineData("0407:00000407;", null, null)]
    [InlineData(null, "xx-YY", null)]
    [InlineData(null, "", null)]
    [InlineData(null, null, "not a locale")]
    public async Task UnknownKeyboardsAndLocalesAreRefusedBeforeAnything(string? keyboard, string? userLocale, string? uiLanguage)
    {
        var answers = Plain with
        {
            InputLocale = keyboard ?? Plain.InputLocale,
            UserLocale = userLocale ?? Plain.UserLocale,
            UiLanguage = uiLanguage ?? Plain.UiLanguage,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download(answers: answers)));
        Assert.Empty(_steps.Calls);
    }

    [Theory]
    [InlineData("0C07:00000407")]
    [InlineData("0407:00000407;0409:00000409")]
    [InlineData("0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}")]
    public async Task WindowsKeyboardsAreAccepted(string keyboard)
    {
        await Run(Download(answers: Plain with { InputLocale = keyboard }));
        Assert.True(File.Exists(OutputIso));
    }

    [Fact]
    public async Task AppOutsideTheAllowlistIsRefusedBeforeAnything()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download(apps: ["Microsoft.BingNews", "Microsoft.WindowsStore"])));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task NotEnoughSpaceFailsBeforeAnything()
    {
        _steps.Free = MediaBuild.NeededBytes(new MediaSource(Esd, null, null), Esd.Size) - 1;
        await FailsAt(Download(), MediaStage.Download, "Media_NotEnoughSpace");
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task OutputMustBeAnIsoFile()
    {
        await FailsAt(Download() with { OutputIso = Path.Combine(_root, "Win11.img") }, MediaStage.Download, "Media_OutputInvalid");
        Assert.Empty(_steps.Calls);
        var upper = Path.Combine(_root, "Win11.ISO");
        Assert.Equal(upper, (await Run(Download() with { OutputIso = upper })).Iso);
    }

    [Fact]
    public async Task OutputOverTheSourceIsoIsRefused()
    {
        var request = OwnIso();
        await FailsAt(request with { OutputIso = request.Source.IsoPath! }, MediaStage.Unpack, "Media_OutputInvalid");
        Assert.Empty(_steps.Calls);
    }

    // ---- apps ----

    [Fact]
    public async Task AppsAreRemovedOnlyWhenProvisioned()
    {
        _steps.Dism.Provisioned =
        [
            "Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe",
            "Microsoft.WindowsStore_22510.1401.5.0_neutral_~_8wekyb3d8bbwe",
        ];
        await Run(Download(apps: ["Microsoft.BingNews", "Microsoft.ZuneMusic"]));
        var apps = _steps.Calls.SkipWhile(c => !c.StartsWith("Mount", StringComparison.Ordinal)).TakeWhile(c => c != "Mount boot.wim 2").ToList();
        Assert.Equal(
        [
            "Mount install.wim 1", "Provisioned", "Remove Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe", "ComponentCleanup", "CheckHealth", "Unmount commit",
        ], apps);
        Assert.Empty(_steps.Dism.Mounted);
    }

    [Fact]
    public async Task NothingToRemoveStillCommitsTheCleanup()
    {
        _steps.Dism.Provisioned = ["Microsoft.WindowsStore_22510.1401.5.0_neutral_~_8wekyb3d8bbwe"];
        await Run(Download(apps: ["Microsoft.BingNews"]));
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Remove", StringComparison.Ordinal));
        Assert.Contains("ComponentCleanup", _steps.Calls);
        Assert.Contains("Unmount commit", _steps.Calls);
    }

    [Fact]
    public async Task UnreviewedAppsAreRemovedByTheirExactName()
    {
        _steps.Dism.Provisioned =
        [
            "Contoso.Notes_1.2.0.0_x64__abcdefghijklm",
            "Contoso.NotesHelper_1.0.0.0_x64__abcdefghijklm",
            "Microsoft.WindowsStore_22510.1401.5.0_neutral_~_8wekyb3d8bbwe",
        ];
        await Run(Download() with { RemoveUnreviewed = ["Contoso.Notes"] });
        Assert.Equal(["Remove Contoso.Notes_1.2.0.0_x64__abcdefghijklm"], _steps.Calls.Where(c => c.StartsWith("Remove", StringComparison.Ordinal)));
        Assert.Contains("Unmount commit", _steps.Calls);
    }

    [Theory]
    [InlineData("Microsoft.BingNews")]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("x'; Remove-Item C:\\")]
    public async Task UnreviewedAppsFollowTheRulesOfRemoveApps(string name)
    {
        // The reviewed list, protected apps, parts of Windows and anything but a package name are refused.
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download() with { RemoveUnreviewed = [name] }));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task AppsToInstallReachTheFirstSignInFile()
    {
        var id = MediaChoices.InstallableApps(CatalogData.Current.Apps).First().Id;
        var request = WithTunero(Download()) with { FirstSignIn = First with { Apps = [id] } };
        await Run(request);
        var written = Encoding.UTF8.GetString(_steps.IsoContents[@"sources\$OEM$\$$\Setup\Tunero\first-sign-in.json"]);
        Assert.Contains(id, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAppsMeansNoInstallWimMount()
    {
        await Run(Download());
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Mount install.wim", StringComparison.Ordinal));
        Assert.DoesNotContain(_steps.Progress, p => p.Stage == MediaStage.Apps);
    }

    [Fact]
    public async Task UnhealthyImageIsNotCommitted()
    {
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        _steps.Dism.Healthy = false;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Shrink, "Media_Unhealthy");
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertCleanedUp();
    }

    // ---- files ----

    [Fact]
    public async Task FilesStageWritesAnswerFileAndTunero()
    {
        var answers = Plain with { SkipSetup = true, AccountName = "Anna" };
        var request = WithTunero(Download(answers: answers)) with { FirstSignIn = new FirstSignInFile(CatalogData.Current.Profiles.Default.Id, "de", "Dark") };
        await Run(request);

        Assert.Equal(AnswerFile.Build(request.Answers), Encoding.UTF8.GetString(BootFiles()["Autounattend.xml"]));
        Assert.False(BootFiles()["Autounattend.xml"].AsSpan().StartsWith(Encoding.UTF8.Preamble), "no byte order mark");
        Assert.Equal("MZ fake Tunero", Encoding.ASCII.GetString(_steps.IsoContents[@"sources\$OEM$\$$\Setup\Tunero\Tunero.exe"]));
        Assert.Equal(FirstSignIn.Serialize(request.FirstSignIn!), Encoding.UTF8.GetString(_steps.IsoContents[@"sources\$OEM$\$$\Setup\Tunero\first-sign-in.json"]));
    }

    /// <summary>
    /// The page cannot know the language of an own ISO: Setup's language in the answer file is the one the chosen image
    /// has, never one it lacks.
    /// </summary>
    [Fact]
    public async Task AnswerFileTakesTheLanguageOfTheImage()
    {
        _steps.OwnLanguage = "de-DE";
        var request = OwnIso() with { Answers = Plain with { SkipSetup = true, AccountName = "Anna" } };
        await Run(request);
        Assert.Equal(AnswerFile.Build(request.Answers with { UiLanguage = "de-DE" }), Encoding.UTF8.GetString(BootFiles()["Autounattend.xml"]));
        Assert.Contains(_steps.LogLines, l => l.Contains("de-DE", StringComparison.Ordinal));
    }

    /// <summary>An image with several languages: the one DISM marks as the default, not the first listed.</summary>
    [Fact]
    public async Task AnswerFileTakesTheDefaultLanguageOfAMultiLanguageImage()
    {
        _steps.OwnLanguage = "de-DE";
        _steps.OwnOtherLanguage = "en-GB";
        var request = OwnIso() with { Answers = Plain with { UiLanguage = "fr-FR", SkipSetup = true, AccountName = "Anna" } };
        await Run(request);
        Assert.Equal(AnswerFile.Build(request.Answers with { UiLanguage = "de-DE" }), Encoding.UTF8.GetString(BootFiles()["Autounattend.xml"]));
    }

    [Fact]
    public async Task NoChoicesMeansNoAnswerFile()
    {
        await Run(Download());
        Assert.DoesNotContain("autounattend.xml", _steps.IsoContents.Keys);
        Assert.DoesNotContain("Autounattend.xml", BootFiles().Keys);
        Assert.DoesNotContain(_steps.IsoContents.Keys, k => k.Contains("$OEM$", StringComparison.Ordinal));
    }

    // ---- boot.wim: the answer file's only place ----

    private IReadOnlyDictionary<string, byte[]> BootFiles() =>
        _steps.ImagesOf(_steps.IsoContents[@"sources\boot.wim"])[1].Files ?? FakeMediaSteps.PeFiles();

    private static bool IsAnswerFileName(string path) =>
        Path.GetFileName(path).Equals("autounattend.xml", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("unattend.xml", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"Windows\Panther\Unattend\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every place in Windows PE where Setup would also find an answer file (names in mixed case).</summary>
    private static readonly string[] BootAnswerFiles =
    [
        "autounattend.xml", "UNATTEND.XML", @"sources\AutoUnattend.xml", @"sources\unattend.xml",
        @"Windows\Panther\Unattend.xml", @"Windows\Panther\autounattend.xml", @"Windows\Panther\Unattend\custom.xml",
        @"Windows\Panther\Unattend\Unattend.XML", @"Windows\System32\Sysprep\Unattend.xml",
    ];

    private void PlantBootAnswerFiles() =>
        _steps.OwnBootImages![1] = _steps.OwnBootImages[1] with
        {
            Files = FakeMediaSteps.PeFiles("", [.. BootAnswerFiles.Select(f => (f, "<old/>")), (@"Windows\Panther\notes.txt", "keep")]),
        };

    /// <summary>
    /// setup.exe started inside Windows finds an answer file at the media root or in sources and then offers only a clean
    /// install (VM 1e). Inside image 2 of boot.wim it is X:\, which Setup searches only when the PC starts from the media.
    /// </summary>
    [Fact]
    public async Task AnswerFileGoesIntoBootWimImage2Only()
    {
        var request = Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" });
        await Run(request);
        var files = _steps.Calls.SkipWhile(c => c != "WimInfo boot.wim 2").Skip(1).TakeWhile(c => c != "WriteIso");
        Assert.Equal(["Mount boot.wim 2", "LoadHive", "UnloadHive", "Unmount commit", "Mount boot.wim 2 readonly", "Unmount discard"], files);
        var boot = BootFiles();
        Assert.Equal(AnswerFile.Build(request.Answers), Encoding.UTF8.GetString(boot["Autounattend.xml"]));
        Assert.Contains("Autounattend.xml", boot.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(_steps.IsoContents.Keys, IsAnswerFileName);
        // Image 1 is untouched, and the header still boots image 2.
        var images = _steps.ImagesOf(_steps.IsoContents[@"sources\boot.wim"]);
        Assert.Null(images[0].Files);
        Assert.Equal(new WimHeader.Info(2, 2), WimHeader.Read(new MemoryStream(_steps.IsoContents[@"sources\boot.wim"])));
        Assert.Contains(_steps.Progress, p => p.Stage == MediaStage.Files && p.Detail == "boot.wim");
        Assert.All(_steps.Dism.Unmounted, d => Assert.StartsWith(_steps.WorkFolder!, d, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OwnIsoEndsUpWithoutAnyAnswerFile()
    {
        _steps.PlantOldFiles = true;
        PlantBootAnswerFiles();
        await Run(OwnIso());
        Assert.DoesNotContain(_steps.IsoContents.Keys, IsAnswerFileName);
        Assert.DoesNotContain(BootFiles().Keys, IsAnswerFileName);
        Assert.Contains(@"Windows\Panther\notes.txt", BootFiles().Keys);
        // Removing them is a change: committed and checked in the committed image.
        Assert.Contains("Unmount commit", _steps.Calls);
        Assert.Contains("Mount boot.wim 2 readonly", _steps.Calls);
        foreach (var file in BootAnswerFiles)
            Assert.Contains(_steps.LogLines, l => l.Contains(file, StringComparison.Ordinal) && l.Contains("boot.wim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildLogShowsTheBootWimAnswerFileAndRegistryChecks()
    {
        await Run(OwnIso());
        Assert.Contains(_steps.LogLines, l => l.Contains(@"Setup\UnattendFile", StringComparison.Ordinal) && l.Contains("not set", StringComparison.Ordinal));
        Assert.Contains(_steps.LogLines, l => l.Contains("No answer files found in image 2 of boot.wim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildLogNamesTheBootWimAnswerFilesItFound()
    {
        PlantBootAnswerFiles();
        await Run(OwnIso());
        Assert.Contains(_steps.LogLines, l => l.StartsWith("Answer files found in image 2 of boot.wim: ", StringComparison.Ordinal) && BootAnswerFiles.All(f => l.Contains(f, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CleanIsoWithoutAnswerFileLeavesBootWimUnchanged()
    {
        await Run(OwnIso());
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("Mount boot.wim 2 readonly", _steps.Calls);
        Assert.Null(_steps.ImagesOf(_steps.IsoContents[@"sources\boot.wim"])[1].Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootImagePointingToAnAnswerFileFailsClosed(bool download)
    {
        var pointer = FakeMediaSteps.PeFiles(@"X:\Windows\setup.xml");
        if (download) _steps.EsdImages[2] = _steps.EsdImages[2] with { Files = pointer };
        else _steps.OwnBootImages![1] = _steps.OwnBootImages[1] with { Files = pointer };
        await FailsAt(download ? Download() : OwnIso(), MediaStage.Files, "Media_BootImageAnswerFile");
        // The hive is unloaded before the image is discarded.
        Assert.Equal(["Mount boot.wim 2", "LoadHive", "UnloadHive", "UnloadLeftoverHives", "Unmount discard", "DeleteWorkFolder"], _steps.Calls.SkipWhile(c => c != "Mount boot.wim 2"));
        AssertCleanedUp();
    }

    /// <summary>A hive whose own unload gave up holds a file of the mounted image: the cleanup unloads it before the discard.</summary>
    [Fact]
    public async Task HiveLeftLoadedByTheBuildIsUnloadedBeforeTheDiscard()
    {
        _steps.HiveUnloadGivesUp = true;
        await FailsAt(Download(), MediaStage.Files, null);
        Assert.Equal(["Mount boot.wim 2", "LoadHive", "UnloadHive", "Unmount discard", "UnloadLeftoverHives", "Unmount discard"],
            _steps.Calls.SkipWhile(c => c != "Mount boot.wim 2"));
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task BootImageWithoutASystemHiveFailsClosed()
    {
        _steps.OwnBootImages![1] = _steps.OwnBootImages[1] with { Files = new Dictionary<string, byte[]>() };
        await FailsAt(OwnIso(), MediaStage.Files, "Media_ImageCheckFailed");
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task BootWimThatNoLongerBootsImage2AfterTheCommitFails()
    {
        _steps.Dism.CommitBreaksHeader = true;
        await FailsAt(Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" }), MediaStage.Files, "Media_ImageCheckFailed");
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    /// <summary>The committed image is mounted read-only and checked: what Setup will read, not what was written before the commit.</summary>
    [Theory]
    [InlineData("lost")]
    [InlineData("changed")]
    [InlineData("extra")]
    public async Task CommittedBootImageIsCheckedAgain(string tamper)
    {
        _steps.Dism.CommitTamper = files =>
        {
            if (tamper == "lost") files.Remove("Autounattend.xml");
            if (tamper == "changed") files["Autounattend.xml"] = Encoding.UTF8.GetBytes("<unattend/>");
            if (tamper == "extra") files[@"Windows\Panther\unattend.xml"] = Encoding.UTF8.GetBytes("<old/>");
        };
        await FailsAt(Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" }), MediaStage.Files, "Media_FilesCheckFailed");
        Assert.Contains("Mount boot.wim 2 readonly", _steps.Calls);
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task StopWhileBootWimIsMountedDiscardsIt()
    {
        using var stop = new CancellationTokenSource();
        _steps.Dism.StopOnMountOf = "boot.wim";
        _steps.Dism.StopOnMount = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" }), stop.Token));
        Assert.Equal(["Mount boot.wim 2", "UnloadLeftoverHives", "Unmount discard"], _steps.Calls.SkipWhile(c => c != "Mount boot.wim 2"));
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task FailingBootWimMountIsDiscarded()
    {
        _steps.Dism.MountFailsFor = "boot.wim";
        var ex = await FailsAt(Download(), MediaStage.Files, null);
        Assert.Equal(5, ex.Code);
        Assert.Contains("Unmount discard", _steps.Calls);
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task AnswerFileOnTheDownloadedMediaFailsTheFilesCheck()
    {
        _steps.SetupFilesCarryAnswerFile = true;
        await FailsAt(Download(), MediaStage.Files, "Media_FilesCheckFailed");
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task LeftoverBootWimMountIsDiscardedAfterItsHive()
    {
        _steps.Dism.Mounted.Add(@"X:\TuneroMedia.ab12cd34\bootmount");
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34"], _steps.DeletedFolders);
    }

    // ---- failure, Stop and cleanup ----

    [Fact]
    public async Task FailureWhileMountedDiscardsAndCleansUp()
    {
        _steps.Dism.ProvisionedThrows = true;
        var ex = await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        Assert.Equal(87, ex.Code);
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        Assert.True(_steps.Calls.IndexOf("Unmount discard") < _steps.Calls.IndexOf("DeleteWorkFolder"));
        AssertCleanedUp();
    }

    [Fact]
    public async Task FailingMountIsDiscarded()
    {
        _steps.Dism.MountThrows = true;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        Assert.Contains("Unmount discard", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task StopWhileRemovingAppsDiscardsTheImage()
    {
        using var stop = new CancellationTokenSource();
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        _steps.Dism.StopOnRemove = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(apps: ["Microsoft.BingNews"]), stop.Token));
        // The fake honors the token: the discard and the delete ran because the cleanup does not use the stopped one.
        Assert.Contains("Unmount discard", _steps.Calls);
        AssertCleanedUp();
        // A stop is logged as a stop, not as a failure of the stage.
        Assert.Single(_steps.LogLines, l => l.StartsWith("Stopped in stage Apps after ", StringComparison.Ordinal));
        Assert.DoesNotContain(_steps.LogLines, l => l.Contains(" failed after ", StringComparison.Ordinal));
    }

    /// <summary>A canceled task that is no Stop (the token was not used) stays a failure in the log.</summary>
    [Fact]
    public async Task CancellationWithoutStopIsLoggedAsAFailure()
    {
        _steps.IsoWriter.BeforeWrite = () => throw new TaskCanceledException("timed out");
        await FailsAt(Download(), MediaStage.Iso, null);
        Assert.Contains(_steps.LogLines, l => l.StartsWith("Iso failed after ", StringComparison.Ordinal));
        Assert.DoesNotContain(_steps.LogLines, l => l.StartsWith("Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscardIsRetried()
    {
        _steps.Dism.ProvisionedThrows = true;
        _steps.Dism.UnmountFailures = 2;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        Assert.Equal(3, Count("Unmount discard"));
        AssertCleanedUp();
    }

    [Fact]
    public async Task FolderWithAnImageThatStaysMountedIsKept()
    {
        _steps.Dism.ProvisionedThrows = true;
        _steps.Dism.UnmountFailures = int.MaxValue;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        // Three discards, Microsoft's recovery order (remount, three more discards, cleanup), then the list.
        Assert.Equal(["Unmount discard", "Unmount discard", "Unmount discard", "Remount", "Unmount discard", "Unmount discard", "Unmount discard", "CleanupMountpoints", "MountedImages"],
            _steps.Calls.SkipWhile(c => c != "Unmount discard").ToList());
        Assert.Contains(_steps.LogLines, l => l.StartsWith("Mounted image ", StringComparison.Ordinal) && l.EndsWith(@"\mount: status Ok", StringComparison.Ordinal));
        // Deleting the folder would delete files inside the mounted image; the next start cleans it up.
        Assert.DoesNotContain("DeleteWorkFolder", _steps.Calls);
        Assert.Contains(_steps.LogLines, l => l.Contains("still mounted", StringComparison.Ordinal));
    }

    /// <summary>A failed build whose discard keeps failing remounts the image and discards it again (the same steps as at the next start).</summary>
    [Fact]
    public async Task DiscardThatKeepsFailingIsRecoveredByARemount()
    {
        _steps.Dism.ProvisionedThrows = true;
        _steps.Dism.UnmountFailures = 3;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        Assert.Equal(["Unmount discard", "Unmount discard", "Unmount discard", "Remount", "Unmount discard", "DeleteWorkFolder"],
            _steps.Calls.SkipWhile(c => c != "Unmount discard").ToList());
        AssertCleanedUp();
    }

    [Fact]
    public async Task FolderDeleteIsRetried()
    {
        _steps.Dism.ProvisionedThrows = true;
        _steps.DeleteFailures = 2;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        Assert.Equal(3, Count("DeleteWorkFolder"));
        AssertCleanedUp();
    }

    /// <summary>The failure path names the deleted work folder in the media log, as the start cleanup does.</summary>
    [Fact]
    public async Task CleanupAfterAFailureLogsTheDeletedFolder()
    {
        _steps.Dism.ProvisionedThrows = true;
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, null);
        AssertCleanedUp();
        Assert.Contains("Deleted " + _steps.WorkFolder, _steps.LogLines);
    }

    [Fact]
    public async Task ProgressSinkThatThrowsDoesNotStopTheCleanup()
    {
        _steps.Dism.ProvisionedThrows = true;
        var sink = new Throwing(MediaStage.Cleanup);
        var ex = await Assert.ThrowsAsync<MediaBuildException>(() => Run(Download(apps: ["Microsoft.BingNews"]), progress: sink));
        Assert.Equal(MediaStage.Apps, ex.Stage);
        Assert.True(sink.Thrown);
        Assert.Contains("Unmount discard", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task StopDuringIsoDeletesPartialIso()
    {
        using var stop = new CancellationTokenSource();
        _steps.IsoWriter.StopWith = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(), stop.Token));
        Assert.Contains("WriteIso", _steps.Calls);
        Assert.DoesNotContain("VerifyIso", _steps.Calls);
        // The partial ISO was written inside the work folder, which stays for Resume without it.
        Assert.StartsWith(_steps.WorkFolder!, _steps.IsoWriter.Target!, StringComparison.OrdinalIgnoreCase);
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task StopKeepsAnOlderIso()
    {
        File.WriteAllText(OutputIso, "older ISO");
        using var stop = new CancellationTokenSource();
        _steps.IsoWriter.StopWith = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(), stop.Token));
        Assert.Equal("older ISO", File.ReadAllText(OutputIso));
        Assert.False(File.Exists(OutputIso + ".part"));
    }

    [Fact]
    public async Task StopDuringDownloadCleansUp()
    {
        using var stop = new CancellationTokenSource();
        _steps.StopDownloadWith = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(), stop.Token));
        AssertCleanedUp();
    }

    [Fact]
    public async Task WorkFolderIsLockedWhileTheBuildRuns()
    {
        await Run(Download());
        Assert.True(_steps.LockHeldDuringDownload, "the lock file was not held open");
    }

    // ---- own ISO ----

    [Fact]
    public async Task OwnIsoCopiesAndExportsTheIndex()
    {
        _steps.OwnImageFile = "install.esd";
        Assert.Equal(OutputIso, (await Run(OwnIso())).Iso);
        Assert.Equal(
        [
            "NewWorkFolder", "CopyMedia",
            "WimInfo install.esd", "WimInfo install.esd 2", "Export install.esd 2 install.wim",
            "WimInfo install.wim", "WimInfo install.wim 1", "WimInfo boot.wim", "WimInfo boot.wim 2",
            "Mount boot.wim 2", "LoadHive", "UnloadHive", "Unmount discard",
            "WriteIso", "VerifyIso", "DeleteWorkFolder",
        ], _steps.Calls);
        Assert.Contains(@"sources\install.wim", _steps.IsoContents.Keys);
        Assert.DoesNotContain(@"sources\install.esd", _steps.IsoContents.Keys);
        Assert.Equal("Professional", _steps.ImagesOf(_steps.IsoContents[@"sources\install.wim"]).Single().Edition);
    }

    [Fact]
    public async Task OwnIsoInstallWimIsReplacedByTheExport()
    {
        await Run(OwnIso(index: 1, edition: "Core"));
        Assert.Equal("Core", Assert.Single(_steps.ImagesOf(_steps.IsoContents[@"sources\install.wim"])).Edition);
    }

    [Fact]
    public async Task OwnIsoEditionIsFoundWithoutAnIndex()
    {
        await Run(OwnIso(index: null, edition: "Windows 11 Pro"));
        Assert.Contains("Export install.wim 2 install.wim", _steps.Calls);
    }

    [Fact]
    public async Task OwnIsoIndexWithAnotherEditionIsRefused()
    {
        await FailsAt(OwnIso(index: 1, edition: "Professional"), MediaStage.Unpack, "Media_EditionMissing");
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoWithoutInstallImageIsRefused()
    {
        _steps.OwnImageFile = null;
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NotWindows11");
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoWithBothInstallImagesIsRefused()
    {
        _steps.OwnBothImages = true;
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NotWindows11");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoWithoutBootWimIsRefused()
    {
        _steps.OwnBootImages = null;
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NotWindows11");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoBelowBuild22000IsRefused()
    {
        _steps.OwnVersion = "10.0.19045";
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NotWindows11");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoForArmIsRefused()
    {
        _steps.OwnArchitecture = "ARM64";
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NotX64");
        AssertCleanedUp();
    }

    [Fact]
    public async Task MediaWithoutUefiBootIsRefused()
    {
        _steps.WithoutUefi = true;
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NoUefiBoot");
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoInUseKeepsTheReason()
    {
        _steps.IsoReader.AttachError = 32;
        var ex = await FailsAt(OwnIso(), MediaStage.Unpack, "Media_IsoInUse");
        Assert.Equal(32, ex.Code);
        Assert.True(ex.InUse);
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoLosesAnOldAnswerFileAndTuneroCopy()
    {
        _steps.PlantOldFiles = true;
        await Run(OwnIso());
        Assert.DoesNotContain("autounattend.xml", _steps.IsoContents.Keys);
        Assert.DoesNotContain(@"sources\autounattend.xml", _steps.IsoContents.Keys);
        Assert.DoesNotContain(_steps.IsoContents.Keys, k => k.StartsWith(@"sources\$OEM$\$$\Setup\Tunero", StringComparison.OrdinalIgnoreCase));
        // Answer files Setup would pick up from $OEM$ go too (any case); the ISO's other $OEM$ content stays.
        Assert.DoesNotContain(_steps.IsoContents.Keys, k => k.EndsWith("unattend.xml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"sources\$OEM$\$$\Setup\Scripts\keep.cmd", _steps.IsoContents.Keys);
        Assert.Contains(@"sources\$OEM$\$$\Panther\notes.txt", _steps.IsoContents.Keys);
        Assert.Contains(@"sources\$OEM$\$1\keep.txt", _steps.IsoContents.Keys);
        // Every file the ISO carries under $OEM$ is named in the log once.
        Assert.Single(_steps.LogLines, l => l.Contains(@"sources\$OEM$\$$\Setup\Scripts\keep.cmd", StringComparison.Ordinal));
        Assert.Contains(_steps.LogLines, l => l.Contains(@"sources\$OEM$\$$\System32\Sysprep\unattend.xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnIsoOldAnswerFileIsReplacedByTheNewOne()
    {
        _steps.PlantOldFiles = true;
        PlantBootAnswerFiles();
        var request = WithTunero(OwnIso());
        await Run(request);
        Assert.Equal(AnswerFile.Build(request.Answers), Encoding.UTF8.GetString(BootFiles()["Autounattend.xml"]));
        // Ours is the only answer file left, in the boot image and on the media.
        Assert.Equal(["Autounattend.xml"], BootFiles().Keys.Where(IsAnswerFileName));
        Assert.DoesNotContain(_steps.IsoContents.Keys, IsAnswerFileName);
        Assert.Equal("MZ fake Tunero", Encoding.ASCII.GetString(_steps.IsoContents[@"sources\$OEM$\$$\Setup\Tunero\Tunero.exe"]));
        Assert.DoesNotContain(@"sources\$OEM$\$$\Setup\Tunero\old.txt", _steps.IsoContents.Keys);
    }

    // ---- safety checks after unpacking ----

    [Fact]
    public async Task WrongEditionAfterExportFails()
    {
        _steps.Dism.ExportTamper = (target, img) => target.EndsWith("install.wim", StringComparison.OrdinalIgnoreCase) ? img with { Edition = "Core" } : img;
        await FailsAt(Download("Professional"), MediaStage.Unpack, "Media_ImageCheckFailed");
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task ArmImageAfterExportFails()
    {
        _steps.Dism.ExportTamper = (target, img) => target.EndsWith("install.wim", StringComparison.OrdinalIgnoreCase) ? img with { Architecture = "ARM64" } : img;
        await FailsAt(Download(), MediaStage.Unpack, "Media_NotX64");
        AssertCleanedUp();
    }

    [Fact]
    public async Task OldVersionAfterExportFails()
    {
        _steps.Dism.ExportTamper = (target, img) => target.EndsWith("install.wim", StringComparison.OrdinalIgnoreCase) ? img with { Version = "10.0.19045.1" } : img;
        await FailsAt(Download(), MediaStage.Unpack, "Media_NotWindows11");
        AssertCleanedUp();
    }

    [Fact]
    public async Task InstallWimWithTwoImagesFails()
    {
        _steps.Dism.DoubleExportInto = "install.wim";
        await FailsAt(Download(), MediaStage.Unpack, "Media_ImageCheckFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task BootWimWithoutABootIndexFails()
    {
        _steps.Dism.ExportTamper = (target, img) => target.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase) ? img with { Bootable = false } : img;
        await FailsAt(Download(), MediaStage.Unpack, "Media_ImageCheckFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task BootWimWithThreeImagesFails()
    {
        _steps.OwnBootImages!.Add(new Img("Extra", "WindowsPE"));
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_ImageCheckFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task BootWimForArmFails()
    {
        _steps.OwnBootImages = [new Img("PE", "WindowsPE", "ARM64"), new Img("Setup", "WindowsPE", "ARM64", Bootable: true)];
        await FailsAt(OwnIso(), MediaStage.Unpack, "Media_NotX64");
        AssertCleanedUp();
    }

    // ---- Smaller ISO: install.esd with recovery compression ----

    private MediaRequest Smaller(MediaRequest r) => r with { Compression = MediaCompression.Recovery };

    [Fact]
    public async Task RecoveryLeavesOnlyInstallEsdOnTheMedia()
    {
        await Run(Smaller(Download()));
        Assert.Contains(@"sources\install.esd", _steps.IsoContents.Keys);
        Assert.DoesNotContain(@"sources\install.wim", _steps.IsoContents.Keys);
        Assert.Contains("ExportRecovery install.wim 1 install.esd", _steps.Calls);
        Assert.Contains(MediaStage.Compress, _steps.Progress.Select(p => p.Stage));
        var images = _steps.ImagesOf(_steps.IsoContents[@"sources\install.esd"]);
        Assert.Equal("Professional", Assert.Single(images).Edition);
    }

    [Fact]
    public async Task RecoveryFromAnOwnIsoWithInstallEsdEndsWithOneImage()
    {
        _steps.OwnImageFile = "install.esd";
        await Run(Smaller(OwnIso()));
        Assert.Contains(@"sources\install.esd", _steps.IsoContents.Keys);
        Assert.DoesNotContain(@"sources\install.wim", _steps.IsoContents.Keys);
        Assert.Equal(1, Count("ExportRecovery install.wim 1 install.esd"));
        Assert.Equal("Professional", Assert.Single(_steps.ImagesOf(_steps.IsoContents[@"sources\install.esd"])).Edition);
    }

    [Fact]
    public async Task RecoveryIsTheLastChangeToTheInstallImage()
    {
        await Run(Smaller(Download(apps: ["Microsoft.BingNews"])));
        Assert.True(_steps.Calls.LastIndexOf("Unmount commit") < _steps.Calls.IndexOf("ExportRecovery install.wim 1 install.esd"));
    }

    [Fact]
    public async Task VerifyReadsBackTheInstallImageThatIsOnTheMedia()
    {
        await Run(Smaller(Download()));
        Assert.Contains(@"sources\install.esd", _steps.IsoReader.Verified);
        Assert.DoesNotContain(@"sources\install.wim", _steps.IsoReader.Verified);
    }

    [Fact]
    public async Task VerifyOfTheEsdMismatchFailsTheBuild()
    {
        _steps.IsoReader.Mismatch = [@"sources\install.esd"];
        await FailsAt(Smaller(Download()), MediaStage.Verify, "Media_VerifyFailed");
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task RecoveryExportOfTwoImagesFailsTheBuild()
    {
        _steps.Dism.DoubleRecoveryExport = true;
        await FailsAt(Smaller(Download()), MediaStage.Compress, "Media_ImageCheckFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task WimBuildNeverExportsRecovery()
    {
        await Run(Download());
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("ExportRecovery", StringComparison.Ordinal));
        Assert.DoesNotContain(MediaStage.Compress, _steps.Progress.Select(p => p.Stage));
    }

    [Fact]
    public void RecoveryNeedsRoomForBothImages()
    {
        var download = new MediaSource(Esd, null, null);
        var own = new MediaSource(null, @"C:\a.iso", null);
        Assert.Equal(MediaBuild.NeededBytes(download, 5L << 30) + (5L << 30), MediaBuild.NeededBytes(download, 5L << 30, MediaCompression.Recovery));
        Assert.Equal(MediaBuild.NeededBytes(own, 5L << 30) + (5L << 30), MediaBuild.NeededBytes(own, 5L << 30, MediaCompression.Recovery));
    }

    [Fact]
    public async Task RecoveryChecksTheSpaceForBothImages()
    {
        _steps.Free = MediaBuild.NeededBytes(new MediaSource(Esd, null, null), Esd.Size, MediaCompression.Recovery) - 1;
        await FailsAt(Smaller(Download()), MediaStage.Download, "Media_NotEnoughSpace");
    }

    // ---- ISO, Verify and the output path ----

    [Fact]
    public async Task VerifyMismatchNeverReachesTheOutput()
    {
        File.WriteAllText(OutputIso, "older ISO");
        _steps.IsoReader.Mismatch = [@"sources\install.wim"];
        await FailsAt(Download(), MediaStage.Verify, "Media_VerifyFailed");
        Assert.Equal("older ISO", File.ReadAllText(OutputIso));
        Assert.False(File.Exists(Path.Combine(_steps.WorkFolder!, "output.iso")));
    }

    [Fact]
    public async Task VerifyMismatchLeavesNoIso()
    {
        _steps.IsoReader.Mismatch = [@"sources\install.wim"];
        await FailsAt(Download(), MediaStage.Verify, "Media_VerifyFailed");
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task BadBootCatalogLeavesNoIso()
    {
        _steps.IsoReader.BootCatalogOk = false;
        await FailsAt(Download(), MediaStage.Verify, "Media_VerifyFailed");
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task VerifyFailureAfterACommitDoesNotDiscardAgain()
    {
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        _steps.IsoReader.Mismatch = [@"sources\boot.wim"];
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Verify, "Media_VerifyFailed");
        Assert.Equal(1, Count("Unmount commit"));
        Assert.DoesNotContain(_steps.Calls.SkipWhile(c => c != "WriteIso"), c => c.StartsWith("Unmount", StringComparison.Ordinal));
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task VerifyRunsAfterIso()
    {
        await Run(WithTunero(Download()));
        var write = _steps.Calls.IndexOf("WriteIso");
        var verify = _steps.Calls.IndexOf("VerifyIso");
        Assert.True(write >= 0 && verify == write + 1 && _steps.Calls.IndexOf("DeleteWorkFolder") == verify + 1);
        // The ISO is written and verified inside the work folder; only a verified ISO reaches the output path.
        Assert.StartsWith(_steps.WorkFolder!, _steps.IsoReader.VerifiedIso!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
        [
            @"sources\install.wim", @"sources\boot.wim",
            @"boot\etfsboot.com", @"efi\microsoft\boot\efisys.bin", @"efi\boot\bootx64.efi",
            @"sources\$OEM$\$$\Setup\Tunero\Tunero.exe", @"sources\$OEM$\$$\Setup\Tunero\first-sign-in.json",
        ], _steps.IsoReader.Verified);
    }

    [Fact]
    public async Task VerifiedIsoReplacesAnOlderOneAndTakesTheFolderPermissions()
    {
        File.WriteAllText(OutputIso, "older ISO");
        await Run(Download());
        Assert.Equal("ISO WIN11_26300_X64", File.ReadAllText(OutputIso));
        // Written in the work folder (which only lets this user in here), the ISO must take the output folder's permissions.
        var fresh = Path.Combine(_root, "fresh.txt");
        File.WriteAllText(fresh, "x");
        Assert.Equal(Identities(fresh), Identities(OutputIso));
        Assert.All(new FileInfo(OutputIso).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => Assert.True(r.IsInherited));
    }

    private static string[] Identities(string file) =>
        new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => $"{r.IdentityReference.Value} {r.AccessControlType} {(int)r.FileSystemRights:x}").Order().ToArray();

    [Fact]
    public async Task OutputFolderThatIsALinkIsRefusedBeforeAnything()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "Win11.iso"), "older ISO");
        var link = Path.Combine(_root, "outlink");
        TestFolders.Junction(link, elsewhere);
        await FailsAt(Download() with { OutputIso = Path.Combine(link, "Win11.iso") }, MediaStage.Download, "Media_OutputLink");
        Assert.Empty(_steps.Calls);
        Assert.Equal("older ISO", File.ReadAllText(Path.Combine(elsewhere, "Win11.iso")));
    }

    [Fact]
    public async Task OutputFolderSwappedForALinkDuringTheBuildIsRefused()
    {
        var output = Path.Combine(_root, "out");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(elsewhere);
        _steps.IsoWriter.BeforeWrite = () =>
        {
            Directory.Delete(output);
            TestFolders.Junction(output, elsewhere);
        };
        await FailsAt(Download() with { OutputIso = Path.Combine(output, "Win11.iso") }, MediaStage.Verify, "Media_OutputInvalid");
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.False(File.Exists(Path.Combine(_steps.WorkFolder!, "output.iso")));
    }

    [Fact]
    public async Task ReadOnlyOutputIsRefusedBeforeAnything()
    {
        File.WriteAllText(OutputIso, "older ISO");
        File.SetAttributes(OutputIso, FileAttributes.ReadOnly);
        await FailsAt(Download(), MediaStage.Download, "Media_OutputReadOnly");
        Assert.Empty(_steps.Calls);
        Assert.Equal("older ISO", File.ReadAllText(OutputIso));
    }

    [Fact]
    public async Task OutputOpenElsewhereIsRefusedBeforeAnything()
    {
        File.WriteAllText(OutputIso, "older ISO");
        using (new FileStream(OutputIso, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await FailsAt(Download(), MediaStage.Download, "Media_OutputInUse");
        }
        Assert.Empty(_steps.Calls);
        // Open with delete sharing (as a reader that lets others replace the file) is fine.
        using (new FileStream(OutputIso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(OutputIso, (await Run(Download())).Iso);
        }
    }

    [Fact]
    public async Task WorkFolderThatCannotBeDeletedKeepsTheVerifiedIso()
    {
        _steps.DeleteFailures = int.MaxValue;
        Assert.Equal(OutputIso, (await Run(Download())).Iso);
        Assert.True(File.Exists(OutputIso));
        Assert.Equal(3, Count("DeleteWorkFolder"));

        _steps.Reset();
        _steps.DeleteThrowsArgument = true;
        Assert.Equal(OutputIso, (await Run(Download())).Iso);
        Assert.True(File.Exists(OutputIso));
    }

    // ---- log ----

    [Fact]
    public async Task EveryCommandIsLogged()
    {
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        await Run(Download(apps: ["Microsoft.BingNews"]));
        foreach (var stage in new[] { "Download", "Check", "Unpack", "Apps", "Files", "Iso", "Verify", "Cleanup" })
            Assert.Contains(_steps.LogLines, l => l.StartsWith(stage + " started", StringComparison.Ordinal));
        Assert.Contains(_steps.LogLines, l => l.StartsWith("Verify done in ", StringComparison.Ordinal));
        var dismCalls = _steps.Calls.Count(c => c.Split(' ')[0] is "WimInfo" or "Apply" or "Export" or "Mount" or "Provisioned" or "Remove" or "ComponentCleanup" or "CheckHealth" or "Unmount");
        Assert.Equal(dismCalls, _steps.LogLines.Count(l => l.StartsWith("dism /English ", StringComparison.Ordinal)));
        Assert.Equal(dismCalls, _steps.LogLines.Count(l => l == "dism exit code 0"));
        Assert.Contains(_steps.LogLines, l => l.Contains("/Remove-ProvisionedAppxPackage /PackageName:Microsoft.BingNews_", StringComparison.Ordinal));

        _steps.Reset();
        _steps.Dism.ProvisionedThrows = true;
        var ex = await Fails(Download(apps: ["Microsoft.BingNews"]));
        Assert.Contains(_steps.LogPath, ex.Message);
        Assert.Equal(_steps.LogPath, ex.LogPath);
        Assert.Contains(_steps.LogLines, l => l.StartsWith("dism exit code 87", StringComparison.Ordinal));
    }

    // ---- checkpoints and resume ----

    private Task<MediaBuildResult> Resume(MediaRequest r, string? folder = null, CancellationToken ct = default) =>
        Build().ResumeAsync(folder ?? _steps.WorkFolder!, r, new Collect(_steps.Progress), ct);

    private async Task<MediaBuildException> ResumeRefused(MediaRequest r, string? folder = null)
    {
        var ex = await Assert.ThrowsAsync<MediaBuildException>(() => Resume(r, folder));
        Assert.Equal("Media_ResumeInvalid", ex.Key);
        return ex;
    }

    /// <summary>The download with an ESD whose content the fake writes the same way each time, so its SHA-256 is known before.</summary>
    private MediaRequest CheckedDownload(IReadOnlyList<string>? apps = null)
    {
        _steps.FixedEsd = true;
        return Download(apps: apps) with { Source = new MediaSource(Esd with { Sha256 = _steps.EsdSha256() }, null, null) };
    }

    /// <summary>The folder stays for Resume: no mount, no ISO of the failed run, the lock released, the checkpoint in place.</summary>
    private void AssertKeptForResume(CheckpointKind kind)
    {
        Assert.True(Directory.Exists(_steps.WorkFolder), "work folder deleted");
        Assert.DoesNotContain("DeleteWorkFolder", _steps.Calls);
        Assert.Equal(kind, MediaCheckpoint.Read(_steps.WorkFolder!)!.Kind);
        Assert.True(File.Exists(Path.Combine(_steps.WorkFolder!, MediaCheckpoint.ChoicesFileName)), "choices.json missing");
        Assert.False(File.Exists(Path.Combine(_steps.WorkFolder!, "output.iso")), "the failed run's ISO stayed");
        Assert.False(File.Exists(Path.Combine(_steps.WorkFolder!, "output.iso.part")), "the failed run's partial ISO stayed");
        Assert.False(File.Exists(OutputIso), "ISO at the output path");
        Assert.Empty(_steps.Dism.Mounted);
        Assert.Empty(_steps.LoadedHives);
        Assert.Empty(_steps.LoadedWritableHives);
        using var _ = new FileStream(Path.Combine(_steps.WorkFolder!, MediaBuild.LockFileName), FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task AFailedIsoWriteKeepsTheReadyImage()
    {
        _steps.IsoWriter.Fails = true;
        await FailsAt(Download(), MediaStage.Iso, null);
        AssertKeptForResume(CheckpointKind.ImageReady);
        var checkpoint = MediaCheckpoint.Read(_steps.WorkFolder!)!;
        Assert.Equal(MediaCheckpoint.HashOf(Download()), checkpoint.RequestHash);
        Assert.Equal("26300", checkpoint.Build);
        Assert.Equal("en-US", checkpoint.UiLanguage);
        // The untouched boot.wim, from before Files commits into it.
        var ready = Path.Combine(_steps.WorkFolder!, MediaCheckpoint.BootWimCopy);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ready))), checkpoint.BootSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_steps.WorkFolder!, @"media\sources\install.wim")))), checkpoint.FileSha256);
        Assert.Contains(_steps.LogLines, l => l.Contains("stays for Resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStopInTheIsoStageKeepsTheReadyImage()
    {
        using var stop = new CancellationTokenSource();
        _steps.IsoWriter.StopWith = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(), stop.Token));
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    /// <summary>Files commits into boot.wim: its mount is discarded and the folder kept, with the untouched copy for the resume.</summary>
    [Fact]
    public async Task AFailureInFilesDiscardsTheBootMountAndKeepsTheFolder()
    {
        _steps.Dism.CommitBreaksHeader = true;
        await FailsAt(Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" }), MediaStage.Files, "Media_ImageCheckFailed");
        AssertKeptForResume(CheckpointKind.ImageReady);
    }

    [Fact]
    public async Task ResumeStartsAfterTheCheckpoint()
    {
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        var request = WithTunero(Download(apps: ["Microsoft.BingNews"]));
        _steps.IsoWriter.Fails = true;
        await FailsAt(request, MediaStage.Iso, null);
        Assert.Contains("Mount install.wim 1", _steps.Calls);
        var folder = _steps.WorkFolder!;
        _steps.Calls.Clear();
        _steps.Progress.Clear();
        _steps.IsoWriter.Fails = false;

        var result = await Resume(request with { OutputIso = OutputIso });
        Assert.Equal(OutputIso, result.Iso);
        Assert.Equal("Mount boot.wim 2", _steps.Calls[0]);
        Assert.DoesNotContain("Download", _steps.Calls);
        Assert.DoesNotContain("NewWorkFolder", _steps.Calls);
        Assert.DoesNotContain("Mount install.wim 1", _steps.Calls);
        Assert.Contains("WriteIso", _steps.Calls);
        Assert.Equal("DeleteWorkFolder", _steps.Calls[^1]);
        Assert.False(Directory.Exists(folder));
        Assert.Equal("ISO WIN11_26300_X64", File.ReadAllText(OutputIso));
        Assert.Equal([MediaStage.Files, MediaStage.Iso, MediaStage.Verify, MediaStage.Cleanup], _steps.Progress.Where(p => p.Percent is null).Select(p => p.Stage).Distinct());
        // The Tunero copy of the failed run was replaced by a fresh one.
        Assert.Contains(@"sources\$OEM$\$$\Setup\Tunero\Tunero.exe", _steps.IsoContents.Keys);
    }

    /// <summary>
    /// A crash while Files committed into boot.wim can leave it broken: the resume puts the untouched copy back, and Setup's
    /// language stays the one the image has (read before the checkpoint).
    /// </summary>
    [Fact]
    public async Task ResumePutsTheUntouchedBootWimBackAndKeepsTheImageLanguage()
    {
        _steps.OwnLanguage = "de-DE";
        var request = OwnIso() with { Answers = Plain with { SkipSetup = true, AccountName = "Anna" } };
        _steps.IsoWriter.Fails = true;
        await FailsAt(request, MediaStage.Iso, null);
        var folder = _steps.WorkFolder!;
        // As after a commit that was cut off: the media's boot.wim is not what the checkpoint saw, and a Tunero folder is left.
        File.WriteAllText(Path.Combine(folder, @"media\sources\boot.wim"), "half a commit");
        FakeMediaSteps.Put(Path.Combine(folder, "media"), @"sources\$OEM$\$$\Setup\Tunero\old.txt", "left over");
        _steps.IsoWriter.Fails = false;
        _steps.Calls.Clear();

        await Resume(request);
        Assert.Equal(AnswerFile.Build(request.Answers with { UiLanguage = "de-DE" }), Encoding.UTF8.GetString(BootFiles()["Autounattend.xml"]));
        Assert.DoesNotContain(_steps.IsoContents.Keys, k => k.Contains("$OEM$", StringComparison.Ordinal));
        Assert.Equal("Mount boot.wim 2", _steps.Calls[0]);
    }

    [Fact]
    public async Task ResumeRefusesAChangedRequestOrImage()
    {
        _steps.IsoWriter.Fails = true;
        await FailsAt(Download(), MediaStage.Iso, null);
        var folder = _steps.WorkFolder!;
        _steps.IsoWriter.Fails = false;
        _steps.Calls.Clear();

        // Other choices: refused, nothing runs, nothing is deleted.
        var ex = await ResumeRefused(Download(apps: ["Microsoft.BingNews"]));
        Assert.Equal(MediaStage.Files, ex.Stage);
        Assert.Empty(_steps.Calls);
        Assert.True(Directory.Exists(folder));

        // An install image that is not the one the checkpoint saw.
        var image = Path.Combine(folder, @"media\sources\install.wim");
        var original = File.ReadAllBytes(image);
        File.AppendAllText(image, "changed");
        await ResumeRefused(Download());
        File.WriteAllBytes(image, original);

        // The same for the untouched boot.wim copy.
        var ready = Path.Combine(folder, MediaCheckpoint.BootWimCopy);
        var boot = File.ReadAllBytes(ready);
        File.AppendAllText(ready, "changed");
        await ResumeRefused(Download());
        File.WriteAllBytes(ready, boot);

        Assert.Empty(_steps.Calls);
        Assert.True(Directory.Exists(folder));
        Assert.NotNull(MediaCheckpoint.Read(folder));
        // Everything back as it was: the resume runs.
        await Resume(Download());
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task ResumeRefusesAFolderItCannotTrust()
    {
        _steps.IsoWriter.Fails = true;
        await FailsAt(Download(), MediaStage.Iso, null);
        var folder = _steps.WorkFolder!;
        _steps.IsoWriter.Fails = false;
        _steps.Calls.Clear();

        // Not locked to administrators.
        _steps.Untrusted.Add(folder);
        await ResumeRefused(Download());
        _steps.Untrusted.Clear();

        // Another Tunero holds its lock.
        using (new FileStream(Path.Combine(folder, MediaBuild.LockFileName), FileMode.Open, FileAccess.Write, FileShare.None))
            await ResumeRefused(Download());

        // Not a work folder by its name.
        var renamed = Path.Combine(_root, "Other." + Path.GetFileName(folder));
        await Assert.ThrowsAsync<ArgumentException>(() => Resume(Download(), renamed));

        // Older than 14 days.
        var checkpoint = MediaCheckpoint.Read(folder)!;
        (checkpoint with { Saved = DateTimeOffset.Now.AddDays(-15) }).Save(folder);
        await ResumeRefused(Download());

        // No checkpoint at all.
        File.Delete(Path.Combine(folder, MediaCheckpoint.FileName));
        await ResumeRefused(Download());

        Assert.Empty(_steps.Calls);
        Assert.True(Directory.Exists(folder));
    }

    /// <summary>The work folder is created on the output's drive and the ISO renamed out of it: another drive cannot be resumed into.</summary>
    [Fact]
    public async Task ResumeRefusesAnOutputOnAnotherDrive()
    {
        _steps.IsoWriter.Fails = true;
        await FailsAt(Download(), MediaStage.Iso, null);
        var other = Path.GetPathRoot(_root)!.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? @"D:\Win11.iso" : @"C:\Win11.iso";
        var ex = await Assert.ThrowsAsync<MediaBuildException>(() => Resume(Download() with { OutputIso = other }));
        Assert.True(ex.Key is "Media_ResumeInvalid" or "Media_OutputInvalid", ex.Key);
        Assert.True(Directory.Exists(_steps.WorkFolder));
    }

    [Fact]
    public async Task AFailureAfterTheDownloadKeepsItAndTheResumeRunsFromCheck()
    {
        var request = CheckedDownload();
        // DISM's Apply fails (a retry can fix that), before the ESD is deleted.
        _steps.Dism.ApplyFails = true;
        await FailsAt(request, MediaStage.Unpack, null);
        AssertKeptForResume(CheckpointKind.Downloaded);
        var folder = _steps.WorkFolder!;
        Assert.True(File.Exists(Path.Combine(folder, "image.esd")));
        Assert.Equal(request.Source.Download!.Sha256.ToUpperInvariant(), MediaCheckpoint.Read(folder)!.FileSha256);
        _steps.Dism.ApplyFails = false;
        _steps.Calls.Clear();
        _steps.Progress.Clear();

        await Resume(request);
        Assert.Equal("WimInfo image.esd", _steps.Calls[0]);
        Assert.DoesNotContain("Download", _steps.Calls);
        Assert.Equal(MediaStage.Check, _steps.Progress[0].Stage);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(OutputIso));
    }

    [Fact]
    public async Task ResumeRefusesAChangedDownload()
    {
        var request = CheckedDownload();
        _steps.Dism.ApplyFails = true;
        await FailsAt(request, MediaStage.Unpack, null);
        File.AppendAllText(Path.Combine(_steps.WorkFolder!, "image.esd"), "changed");
        _steps.Calls.Clear();
        var ex = await ResumeRefused(request);
        Assert.Equal(MediaStage.Check, ex.Stage);
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task AFailureBeforeTheFirstCheckpointStillDeletesEverything()
    {
        using var stop = new CancellationTokenSource();
        _steps.StopDownloadWith = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(CheckedDownload(), stop.Token));
        AssertCleanedUp();
    }

    /// <summary>Once Unpack deleted the ESD the download checkpoint is gone: a failure while install.wim is changed leaves nothing.</summary>
    [Fact]
    public async Task AFailureBetweenTheCheckpointsStillDeletesEverything()
    {
        using var stop = new CancellationTokenSource();
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        _steps.Dism.StopOnRemove = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(CheckedDownload(apps: ["Microsoft.BingNews"]), stop.Token));
        AssertCleanedUp();
    }

    /// <summary>A finished build whose folder cannot be deleted leaves no checkpoint: the next start never offers it for Resume.</summary>
    [Fact]
    public async Task AFinishedBuildLeavesNoCheckpoint()
    {
        _steps.DeleteFailures = 3;
        await Run(Download());
        Assert.True(Directory.Exists(_steps.WorkFolder));
        Assert.Null(MediaCheckpoint.Read(_steps.WorkFolder!));
    }

    /// <summary>A leftover folder named like a work folder under the test's stand-in drive, with a checkpoint saved <paramref name="age"/> ago.</summary>
    private string SavedLeftover(string id, TimeSpan age, bool checkpoint = true)
    {
        var drive = Path.Combine(_root, "drive");
        var folder = Path.Combine(drive, "TuneroMedia." + id);
        FakeMediaSteps.Put(folder, @"media\sources\install.wim", "image");
        if (checkpoint)
            new MediaCheckpoint(CheckpointKind.ImageReady, new string('1', 64), new string('2', 64), new string('3', 64), "26300", DateTimeOffset.Now - age) { UiLanguage = "en-US" }.Save(folder);
        if (!_steps.Drives.Contains(drive)) _steps.Drives.Add(drive);
        _steps.Leftovers[drive] = [.. _steps.Leftovers.GetValueOrDefault(drive, []), folder];
        return folder;
    }

    [Fact]
    public async Task CleanLeftoversKeepsCheckpointedFoldersButDiscardsTheirMounts()
    {
        var saved = SavedLeftover("0000000a", TimeSpan.FromHours(1));
        var mounted = SavedLeftover("0000000b", TimeSpan.FromHours(2));
        _steps.Dism.Mounted.Add(Path.Combine(mounted, "bootmount"));
        var old = SavedLeftover("0000000c", TimeSpan.FromDays(15));
        var none = SavedLeftover("0000000d", TimeSpan.Zero, checkpoint: false);
        var untrusted = SavedLeftover("0000000e", TimeSpan.FromHours(1));
        _steps.Untrusted.Add(untrusted);

        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.Empty(_steps.Dism.Mounted);
        Assert.True(Directory.Exists(saved));
        Assert.True(Directory.Exists(mounted));
        Assert.Equal([old, none, untrusted], _steps.DeletedFolders.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains($"{saved} kept: a saved build can resume from it", _steps.LogLines);
    }

    [Fact]
    public void FindResumableListsOnlyTrustedSavedBuilds()
    {
        var newer = SavedLeftover("0000000a", TimeSpan.FromHours(1));
        var older = SavedLeftover("0000000b", TimeSpan.FromHours(5));
        SavedLeftover("0000000c", TimeSpan.FromDays(15));
        SavedLeftover("0000000d", TimeSpan.Zero, checkpoint: false);
        _steps.Untrusted.Add(SavedLeftover("0000000e", TimeSpan.FromHours(1)));
        FakeMediaSteps.Put(SavedLeftover("0000000f", TimeSpan.FromHours(1)), @"bootmount\Windows\x.dll", "still mounted");
        var held = SavedLeftover("00000010", TimeSpan.FromHours(1));
        using var lockFile = new FileStream(Path.Combine(held, MediaBuild.LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var found = Build().FindResumable();
        Assert.Equal([newer, older], found.Select(f => f.WorkFolder));
        Assert.Equal(CheckpointKind.ImageReady, found[0].Checkpoint.Kind);
        Assert.True(found[0].Bytes > 0);
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public void DiscardDeletesTheFolderUnlessABuildHoldsIt()
    {
        var folder = SavedLeftover("0000000a", TimeSpan.FromHours(1));
        using (new FileStream(Path.Combine(folder, MediaBuild.LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            Assert.Throws<IOException>(() => Build().Discard(folder));
        Assert.True(Directory.Exists(folder));
        Assert.Throws<ArgumentException>(() => Build().Discard(Path.Combine(_root, "Downloads")));
        Build().Discard(folder);
        Assert.Equal([folder], _steps.DeletedFolders);
    }

    /// <summary>--build-media has no Resume: its start cleanup deletes saved builds like any leftover.</summary>
    [Fact]
    public async Task CleanLeftoversWithoutKeepingSavedBuildsDeletesThem()
    {
        var saved = SavedLeftover("0000000a", TimeSpan.FromHours(1));
        await Build().CleanLeftoversAsync(CancellationToken.None, keepSaved: false);
        Assert.Equal([saved], _steps.DeletedFolders);
    }

    /// <summary>
    /// A start with only saved builds has nothing to clean: the app skips the cleanup's DISM calls. A leftover beside them, an
    /// image in a mount folder, a running DISM, or --build-media (which deletes saved builds) has something.
    /// </summary>
    [Fact]
    public void HasLeftoversIsFalseWhenOnlySavedBuildsAreKept()
    {
        Assert.False(Build().HasLeftovers());
        var saved = SavedLeftover("0000000a", TimeSpan.FromHours(1));
        Assert.False(Build().HasLeftovers());
        Assert.True(Build().HasLeftovers(keepSaved: false));
        Assert.Empty(_steps.Calls);

        _steps.DismRunning.Add(@"dism.exe /Export-Image /SourceImageFile:" + saved + @"\image.esd");
        Assert.True(Build().HasLeftovers());
        _steps.DismRunning.Clear();

        FakeMediaSteps.Put(saved, @"mount\Windows\x.dll", "still mounted");
        Assert.True(Build().HasLeftovers());
        Directory.Delete(Path.Combine(saved, "mount"), recursive: true);

        SavedLeftover("0000000b", TimeSpan.FromDays(15));
        Assert.True(Build().HasLeftovers());
    }

    /// <summary>Only a failure a retry can fix keeps the saved build; what the saved files themselves fail deletes it.</summary>
    [Theory]
    [InlineData("Media_NotWindows11", MediaStage.Check, false)]
    [InlineData("Media_NotX64", MediaStage.Unpack, false)]
    [InlineData("Media_NoUefiBoot", MediaStage.Unpack, false)]
    [InlineData("Media_EditionMissing", MediaStage.Unpack, false)]
    [InlineData("Media_Unhealthy", MediaStage.Shrink, false)]
    [InlineData("Media_BootImageAnswerFile", MediaStage.Files, false)]
    [InlineData("Media_ImageCheckFailed", MediaStage.Check, false)]
    [InlineData("Media_ImageCheckFailed", MediaStage.Unpack, false)]
    [InlineData("Media_ImageCheckFailed", MediaStage.Files, true)]
    [InlineData(null, MediaStage.Iso, true)]
    [InlineData("Media_NotEnoughSpace", MediaStage.Unpack, true)]
    [InlineData("Media_IsoInUse", MediaStage.Iso, true)]
    [InlineData("Media_OutputInUse", MediaStage.Verify, true)]
    [InlineData("Media_OutputReadOnly", MediaStage.Verify, true)]
    [InlineData("Media_OutputMoveFailed", MediaStage.Verify, true)]
    [InlineData("Media_OutputInvalid", MediaStage.Verify, true)]
    [InlineData("Media_WorkFolderFailed", MediaStage.Check, true)]
    [InlineData("Media_VerifyFailed", MediaStage.Verify, true)]
    [InlineData("Media_FilesCheckFailed", MediaStage.Files, true)]
    public void OnlyAFailureARetryCanFixKeepsTheSavedBuild(string? key, MediaStage stage, bool keeps)
    {
        Assert.Equal(keeps, MediaBuild.KeepsSavedBuild(new MediaBuildException(stage, "x", key: key), stage));
        Assert.True(MediaBuild.KeepsSavedBuild(new OperationCanceledException(), stage));
        Assert.True(MediaBuild.KeepsSavedBuild(new IOException("disk full"), stage));
        Assert.True(MediaBuild.KeepsSavedBuild(new DismException(112, "Error: 112"), stage));
    }

    /// <summary>The ESD fails its check after the download checkpoint: a resume would fail the same way, so nothing stays.</summary>
    [Fact]
    public async Task ADownloadThatFailsItsCheckDeletesTheSavedBuild()
    {
        _steps.EsdImages[0] = new Img("Windows 11 Home", "Core");
        await FailsAt(CheckedDownload(), MediaStage.Check, "Media_NotWindows11");
        AssertCleanedUp();
        Assert.Contains(_steps.LogLines, l => l.Contains("a retry cannot fix that", StringComparison.Ordinal));
    }

    // ---- leftovers ----

    [Fact]
    public async Task LeftoversAreDiscardedThenDeleted()
    {
        _steps.Dism.Mounted.AddRange([@"X:\TuneroMedia.ab12cd34\mount", @"C:\Other"]);
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34\mount"], _steps.Dism.Unmounted);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34"], _steps.DeletedFolders);
        Assert.Equal([@"C:\Other"], _steps.Dism.Mounted);
    }

    /// <summary>
    /// The PC restarted while a build had install.wim mounted: DISM lists the mount as "Needs Remount", and neither a
    /// discard nor Cleanup-Mountpoints removes it before a remount. Remount, discard, cleanup, then the folder goes.
    /// </summary>
    [Fact]
    public async Task LeftoverThatNeedsARemountIsRemountedDiscardedAndDeleted()
    {
        const string mount = @"X:\TuneroMedia.ab12cd34\mount";
        _steps.Dism.Mounted.Add(mount);
        _steps.Dism.Status[mount] = "Needs Remount";
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "Remount", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34"], _steps.DeletedFolders);
        Assert.Contains(@"Mounted image X:\TuneroMedia.ab12cd34\mount: status Needs Remount", _steps.LogLines);
        Assert.Contains(@"dism /English /Remount-Image /MountDir:""X:\TuneroMedia.ab12cd34\mount""", _steps.LogLines);
    }

    [Fact]
    public async Task LeftoverWhoseDiscardFailsIsRemountedAndDiscardedAgain()
    {
        const string mount = @"X:\TuneroMedia.ab12cd34\mount";
        _steps.Dism.Mounted.Add(mount);
        _steps.Dism.UnmountFailures = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "Unmount discard", "Remount", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34"], _steps.DeletedFolders);
        Assert.Contains(@"Mounted image X:\TuneroMedia.ab12cd34\mount: status Ok", _steps.LogLines);
    }

    /// <summary>Still fail closed: a mount that cannot be remounted stays listed, so its folder stays, with the reason in the log.</summary>
    [Fact]
    public async Task LeftoverWhoseRemountFailsIsKept()
    {
        const string mount = @"X:\TuneroMedia.ab12cd34\mount";
        _steps.Dism.Mounted.Add(mount);
        _steps.Dism.Status[mount] = "Invalid";
        _steps.Dism.RemountFails = true;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "Remount", "Unmount discard", "CleanupMountpoints", "MountedImages"], _steps.Calls);
        Assert.Empty(_steps.DeletedFolders);
        Assert.Contains(_steps.LogLines, l => l.StartsWith("dism exit code 5", StringComparison.Ordinal));
        Assert.Contains(@"X:\TuneroMedia.ab12cd34 kept: an image is still mounted in it", _steps.LogLines);
        Assert.Equal(2, _steps.LogLines.Count(l => l == @"Mounted image X:\TuneroMedia.ab12cd34\mount: status Invalid"));
    }

    /// <summary>
    /// A work folder at a drive under the temp folder whose mount folder (or boot.wim mount folder) still holds the image
    /// files, as after a hard restart while install.wim was mounted (VM 4b, 2026-10-20).
    /// </summary>
    private string LostMount(string id, string mountFolder = "mount")
    {
        var drive = Path.Combine(_root, "drive");
        var folder = Path.Combine(drive, "TuneroMedia." + id);
        FakeMediaSteps.Put(folder, $@"{mountFolder}\Windows\bfsvc.exe", "image file");
        FakeMediaSteps.Put(folder, @"media\sources\setup.exe", "setup");
        if (!_steps.Drives.Contains(drive)) _steps.Drives.Add(drive);
        _steps.Leftovers[drive] = [.. _steps.Leftovers.GetValueOrDefault(drive, []), folder];
        return folder;
    }

    private static string NoMountListed(string folder) =>
        $"No mount listed for {folder}, but its mount folder is not empty: cleaning up DISM mount points";

    /// <summary>
    /// After a hard restart DISM lists no mount although the mount folder still holds the image, and deleting the folder is
    /// refused inside it. /Cleanup-Mountpoints unmounts it (VM 4b); then the list is read again and the folder deleted.
    /// </summary>
    [Fact]
    public async Task UnlistedMountThatIsNotEmptyIsCleanedUpBeforeTheDelete()
    {
        var folder = LostMount("a180be9e");
        _steps.Dism.CleanupDoes = () => TestFolders.Delete(Path.Combine(folder, "mount", "Windows"));
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([folder], _steps.DeletedFolders);
        Assert.Contains(NoMountListed(folder), _steps.LogLines);
        Assert.Contains("dism /English /Cleanup-Mountpoints", _steps.LogLines);
        Assert.Contains("dism /English /Get-MountedImageInfo", _steps.LogLines);
        Assert.Contains("Deleted " + folder, _steps.LogLines);
    }

    [Fact]
    public async Task TwoUnlistedMountsNeedOneCleanupOfTheMountPoints()
    {
        var first = LostMount("a180be9e");
        var second = LostMount("a1740bc3", "bootmount");
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(1, Count("CleanupMountpoints"));
        Assert.Equal([first, second], _steps.DeletedFolders);
        Assert.Contains(NoMountListed(first), _steps.LogLines);
        Assert.Contains(NoMountListed(second), _steps.LogLines);
    }

    /// <summary>A mount DISM lists again after its cleanup goes the usual way: discarded, then the folder deleted.</summary>
    [Fact]
    public async Task UnlistedMountThatIsListedAfterTheCleanupIsDiscarded()
    {
        var folder = LostMount("a180be9e");
        var mount = Path.Combine(folder, "mount");
        var listed = false;
        _steps.Dism.CleanupDoes = () =>
        {
            if (listed) return;
            listed = true;
            _steps.Dism.Mounted.Add(mount);
        };
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "CleanupMountpoints", "MountedImages", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"],
            _steps.Calls);
        Assert.Equal([mount], _steps.Dism.Unmounted);
        Assert.Equal([folder], _steps.DeletedFolders);
        Assert.Contains($"Mounted image {mount}: status Ok", _steps.LogLines);
    }

    /// <summary>Never a delete into a mount DISM lists: a folder whose image stays listed is kept.</summary>
    [Fact]
    public async Task UnlistedMountThatStaysListedIsKept()
    {
        var folder = LostMount("a180be9e");
        var mount = Path.Combine(folder, "mount");
        _steps.Dism.CleanupDoes = () =>
        {
            if (!_steps.Dism.Mounted.Contains(mount)) _steps.Dism.Mounted.Add(mount);
        };
        _steps.Dism.UnmountFailures = int.MaxValue;
        _steps.Dism.RemountFails = true;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Empty(_steps.DeletedFolders);
        Assert.DoesNotContain("DeleteWorkFolder", _steps.Calls);
        Assert.Contains($"{folder} kept: an image is still mounted in it", _steps.LogLines);
        Assert.True(File.Exists(Path.Combine(mount, "Windows", "bfsvc.exe")));
    }

    /// <summary>An empty mount folder (DISM leaves it behind after an unmount) needs no DISM step: the folder is deleted as before.</summary>
    [Fact]
    public async Task EmptyMountFolderNeedsNoCleanupOfTheMountPoints()
    {
        var folder = LostMount("a180be9e");
        TestFolders.Delete(Path.Combine(folder, "mount", "Windows"));
        Directory.CreateDirectory(Path.Combine(folder, "bootmount"));
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([folder], _steps.DeletedFolders);
    }

    /// <summary>A work folder whose mount folder is empty at the start and gets content when the delete begins.</summary>
    private string MountFilledAtTheDelete(string id, string mountFolder = "mount")
    {
        var folder = LostMount(id);
        TestFolders.Delete(Path.Combine(folder, "mount", "Windows"));
        var filled = false;
        _steps.Deleting = _ =>
        {
            if (filled) return;
            filled = true;
            FakeMediaSteps.Put(folder, Path.Combine(mountFolder, "Windows", "bfsvc.exe"), "image file");
        };
        return folder;
    }

    private static string RefusedWithMount(string folder) =>
        $"Deleting {folder} was refused while its mount folder is not empty: cleaning up DISM mount points";

    /// <summary>
    /// A delete refused while a mount folder has content, whatever the refusal says: /Cleanup-Mountpoints, the list again,
    /// then one more delete.
    /// </summary>
    [Fact]
    public async Task DeleteRefusedWhileAMountFolderHasContentIsTriedAgainAfterACleanup()
    {
        var folder = MountFilledAtTheDelete("0000ffff");
        _steps.DeleteFailures = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "DeleteWorkFolder", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([folder], _steps.DeletedFolders);
        Assert.Contains(RefusedWithMount(folder), _steps.LogLines);

        // Access denied on a path outside the mount folders: the content of the mount folder decides.
        _steps.Reset();
        _steps.Drives.Clear();
        _steps.Leftovers.Clear();
        folder = MountFilledAtTheDelete("0000eeee", "bootmount");
        _steps.DeniedPath = Path.Combine(folder, "media", "sources", "install.wim");
        _steps.DeleteDenials = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "DeleteWorkFolder", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([folder], _steps.DeletedFolders);
    }

    /// <summary>No retry when the mount folders are empty or the mount points were cleaned up already; never a delete into a listed mount.</summary>
    [Fact]
    public async Task DeleteRefusedWithEmptyMountFoldersOrAfterACleanupIsLeftInPlace()
    {
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff"];
        _steps.DeleteFailures = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "DeleteWorkFolder"], _steps.Calls);
        Assert.Contains(@"D:\TuneroMedia.0000ffff left in place: in use", _steps.LogLines);

        // The mount points were cleaned up already for the lost mount: the refusal is not retried.
        _steps.Reset();
        _steps.Drives.Clear();
        _steps.Leftovers.Clear();
        var folder = LostMount("a180be9e");
        _steps.DeniedPath = Path.Combine(folder, "mount", "Windows", "bfsvc.exe");
        _steps.DeleteDenials = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(1, Count("CleanupMountpoints"));
        Assert.Equal(1, Count("DeleteWorkFolder"));
        Assert.Empty(_steps.DeletedFolders);

        // The mount the cleanup brings back into the list is not deleted into.
        _steps.Reset();
        _steps.Drives.Clear();
        _steps.Leftovers.Clear();
        folder = MountFilledAtTheDelete("0000dddd", "bootmount");
        var boot = Path.Combine(folder, "bootmount");
        _steps.DeleteFailures = 1;
        _steps.Dism.CleanupDoes = () => _steps.Dism.Mounted.Add(boot);
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "DeleteWorkFolder", "CleanupMountpoints", "MountedImages"], _steps.Calls);
        Assert.Empty(_steps.DeletedFolders);
        Assert.Contains($"{folder} kept: an image is still mounted in it", _steps.LogLines);
    }

    /// <summary>The discard of a listed mount ran Cleanup-Mountpoints already: a refused delete does not run it again.</summary>
    [Fact]
    public async Task RefusedDeleteAfterADiscardRunsNoFurtherCleanup()
    {
        var folder = LostMount("a180be9e");
        _steps.Dism.Mounted.Add(Path.Combine(folder, "mount"));
        _steps.DeleteFailures = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Empty(_steps.DeletedFolders);
        Assert.Contains($"{folder} left in place: in use", _steps.LogLines);

        // A lost mount that the first cleanup brings back into the list: its discard cleans up again, the refusal not a third time.
        _steps.Reset();
        _steps.Drives.Clear();
        _steps.Leftovers.Clear();
        folder = LostMount("a1740bc3");
        var mount = Path.Combine(folder, "mount");
        var listed = false;
        _steps.Dism.CleanupDoes = () =>
        {
            if (listed) return;
            listed = true;
            _steps.Dism.Mounted.Add(mount);
        };
        _steps.DeleteFailures = 1;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(2, Count("CleanupMountpoints"));
        Assert.Equal(1, Count("DeleteWorkFolder"));
        Assert.Empty(_steps.DeletedFolders);
    }

    [Fact]
    public async Task LeftoverFoldersWithoutMountsAreDeleted()
    {
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff", @"D:\TuneroMediaOther"];
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal([@"D:\TuneroMedia.0000ffff"], _steps.DeletedFolders);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Unmount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeftoverStillMountedIsKept()
    {
        _steps.Dism.Mounted.Add(@"X:\TuneroMedia.ab12cd34\mount");
        _steps.Dism.UnmountFailures = int.MaxValue;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Empty(_steps.DeletedFolders);
    }

    [Fact]
    public async Task FolderOfARunningBuildIsLeftAlone()
    {
        var drive = Path.Combine(_root, "drive");
        var live = Path.Combine(drive, "TuneroMedia.11112222");
        Directory.CreateDirectory(live);
        using var held = new FileStream(Path.Combine(live, MediaBuild.LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _steps.Drives.Add(drive);
        _steps.Leftovers[drive] = [live];
        _steps.Dism.Mounted.Add(Path.Combine(live, "mount"));
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Empty(_steps.DeletedFolders);
        Assert.Empty(_steps.Dism.Unmounted);
        Assert.True(Directory.Exists(live));
    }

    [Fact]
    public async Task LeftoverWithAReleasedLockIsDeleted()
    {
        var drive = Path.Combine(_root, "drive");
        var dead = Path.Combine(drive, "TuneroMedia.33334444");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, MediaBuild.LockFileName), "");
        _steps.Drives.Add(drive);
        _steps.Leftovers[drive] = [dead];
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal([dead], _steps.DeletedFolders);
    }

    [Fact]
    public async Task LockFileThatIsALinkIsNeverOpened()
    {
        var drive = Path.Combine(_root, "drive");
        var planted = Path.Combine(drive, "TuneroMedia.55556666");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(planted);
        Directory.CreateDirectory(target);
        // A junction cannot be opened as a file: read as "held", it would keep the folder forever. A link is no Tunero
        // build at all; the folder goes to DeleteWorkFolder, which only unlinks or refuses what it does not trust.
        TestFolders.Junction(Path.Combine(planted, MediaBuild.LockFileName), target);
        _steps.Drives.Add(drive);
        _steps.Leftovers[drive] = [planted];
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal([planted], _steps.DeletedFolders);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task NothingLeftOverMeansNoDismChange()
    {
        _steps.Dism.Mounted.Add(@"C:\Other");
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedImages"], _steps.Calls);
        Assert.Empty(_steps.LogLines);
    }

    /// <summary>The mount a killed build started: DISM goes on without Tunero (VM 4, 2026-10-19).</summary>
    private const string MountingDism =
        @"dism.exe /English /Mount-Image /ImageFile:""D:\TuneroMedia.0000ffff\media\sources\install.wim"" /Index:6 /MountDir:""D:\TuneroMedia.0000ffff\mount""";

    /// <summary>
    /// A build killed while DISM mounts: DISM is not ended with it and finishes the mount minutes later. The cleanup waits
    /// for it before it lists the mounted images, so it discards the mount instead of deleting files inside it.
    /// </summary>
    [Fact]
    public async Task LeftoverCleanupWaitsForARunningDism()
    {
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff"];
        _steps.DismRunning.Add(MountingDism);
        _steps.DismEndsAfterLooks = 3;
        _steps.DismEnded = () => _steps.Dism.Mounted.Add(@"D:\TuneroMedia.0000ffff\mount");
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["DISM ended", "MountedImages", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedImages", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"D:\TuneroMedia.0000ffff\mount"], _steps.Dism.Unmounted);
        Assert.Equal([@"D:\TuneroMedia.0000ffff"], _steps.DeletedFolders);
        Assert.Equal(@"Waiting for DISM to finish with D:\TuneroMedia.0000ffff", Assert.Single(_steps.LogLines, l => l.StartsWith("Waiting", StringComparison.Ordinal)));
    }

    /// <summary>DISM for other folders, and DISM of a build that still runs (its lock is held), is not waited for.</summary>
    [Fact]
    public async Task DismForAnotherFolderIsNotWaitedFor()
    {
        var drive = Path.Combine(_root, "drive");
        var live = Path.Combine(drive, "TuneroMedia.11112222");
        Directory.CreateDirectory(live);
        using var held = new FileStream(Path.Combine(live, MediaBuild.LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _steps.Drives.Add(drive);
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[drive] = [live];
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff"];
        _steps.DismRunning.AddRange(
        [
            @"dism.exe /Mount-Image /ImageFile:C:\images\install.wim /Index:1 /MountDir:C:\Other\mount",
            @"dism.exe /Mount-Image /ImageFile:D:\TuneroMedia.0000ffff2\install.wim /Index:1 /MountDir:D:\XTuneroMedia.0000ffff\mount",
            $@"dism.exe /English /Mount-Image /ImageFile:""{live}\media\sources\boot.wim"" /Index:2 /MountDir:""{live}\boot""",
        ]);
        await Build(dismWait: TimeSpan.FromSeconds(30)).CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(1, _steps.DismLooks);
        Assert.DoesNotContain(_steps.LogLines, l => l.StartsWith("Waiting", StringComparison.Ordinal));
        Assert.Equal([@"D:\TuneroMedia.0000ffff"], _steps.DeletedFolders);
        Assert.True(Directory.Exists(live));
    }

    /// <summary>A DISM that does not end within the limit: its folder is left alone (and its mount), the next start tries again.</summary>
    [Fact]
    public async Task ADismThatDoesNotEndKeepsItsFolder()
    {
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff", @"D:\TuneroMedia.0000eeee"];
        _steps.Dism.Mounted.Add(@"D:\TuneroMedia.0000ffff\mount");
        _steps.DismRunning.Add(MountingDism);
        await Build(dismWait: TimeSpan.Zero).CleanLeftoversAsync(CancellationToken.None);
        Assert.Empty(_steps.Dism.Unmounted);
        Assert.Equal([@"D:\TuneroMedia.0000eeee"], _steps.DeletedFolders);
        Assert.Contains(_steps.LogLines, l => l.StartsWith(@"D:\TuneroMedia.0000ffff left alone: DISM still works in it", StringComparison.Ordinal));
        Assert.Equal([@"D:\TuneroMedia.0000ffff\mount"], _steps.Dism.Mounted);
    }

    /// <summary>
    /// A dism.exe runs but its command line cannot be read (WMI fails): it may be mounting into a leftover, so this start
    /// leaves every leftover alone, with one line; the next start tries again.
    /// </summary>
    [Fact]
    public async Task UnreadableDismLeavesEveryLeftoverAlone()
    {
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff", @"D:\TuneroMedia.0000eeee"];
        _steps.Dism.Mounted.Add(@"D:\TuneroMedia.0000ffff\mount");
        _steps.DismUnreadable = true;
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Empty(_steps.Calls);
        Assert.Empty(_steps.DeletedFolders);
        Assert.Empty(_steps.Dism.Unmounted);
        Assert.Equal(@"Left alone: D:\TuneroMedia.0000ffff, D:\TuneroMedia.0000eeee (DISM runs, but its command line cannot be read; the next start tries again)",
            Assert.Single(_steps.LogLines));
    }

    /// <summary>Stop (closing the app) while the cleanup waits for DISM ends it quietly: nothing is discarded or deleted.</summary>
    [Fact]
    public async Task StopWhileWaitingForDismDeletesNothing()
    {
        using var stop = new CancellationTokenSource();
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff"];
        _steps.DismRunning.Add(MountingDism);
        _steps.DismLooked = () =>
        {
            if (_steps.DismLooks == 2) stop.Cancel();
        };
        await Build().CleanLeftoversAsync(stop.Token);
        Assert.Equal(2, _steps.DismLooks);
        Assert.Empty(_steps.Calls);
        Assert.Empty(_steps.DeletedFolders);
    }

    [Fact]
    public async Task LeftoverCleanupNeverThrows()
    {
        _steps.Dism.MountedImagesThrows = true;
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff"];
        await Build().CleanLeftoversAsync(CancellationToken.None);
        // Without the list of mounted images no folder may be deleted: one could hold a mounted image.
        Assert.Empty(_steps.DeletedFolders);

        _steps.Dism.MountedImagesThrows = false;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Build().CleanLeftoversAsync(stop.Token);
        Assert.Empty(_steps.DeletedFolders);
    }

    // ---- pure helpers ----

    [Fact]
    public void NeededBytesForAFiveGigabyteEsd()
    {
        const long gb = 1L << 30;
        Assert.Equal((5 + 11 + 1) * gb, MediaBuild.NeededBytes(new MediaSource(Esd, null, null), 5 * gb));
        Assert.Equal((10 + 1) * gb, MediaBuild.NeededBytes(new MediaSource(null, @"C:\x.iso", null), 4 * gb));
    }

    /// <summary>
    /// Leftover SYSTEM hives: those of Tuneros no longer running, and this process's own that no build holds any more (an
    /// unload that gave up after its retries). Another running Tunero's and this process's live ones stay; other keys too.
    /// </summary>
    [Fact]
    public void LeftoverHivesAreThoseNoBuildHolds()
    {
        string[] names =
        [
            "SOFTWARE", "SYSTEM", "TuneroDefaultProfile-77",
            "TuneroMediaSystem-100-1", "TuneroMediaSystem-100-2",   // this process: 1 is held, 2 was given up
            "TuneroMediaSystem-200-1",                              // another Tunero that runs
            "TuneroMediaSystem-300-4",                              // a Tunero that ended
            "TuneroMediaSystem-x",
        ];
        var leftovers = OfflineSystemHive.LeftoversToUnload(names, 100, ["TuneroMediaSystem-100-1"], pid => pid == 200);
        Assert.Equal(["TuneroMediaSystem-100-2", "TuneroMediaSystem-300-4", "TuneroMediaSystem-x"], leftovers);
    }

    /// <summary>
    /// A copy under another exe name ("Tunero (1).exe", a renamed copy) is told by its product name, so its hives stay
    /// loaded; another program with a reused id is not a Tunero; an exe that cannot be read counts as one (never unload on
    /// a guess).
    /// </summary>
    [Fact]
    public void ARenamedCopyCountsAsARunningTunero()
    {
        const string own = @"C:\Program Files\Tunero\Tunero.exe";
        const string renamed = @"C:\Users\x\Downloads\Tunero (1).exe";
        string? Product(string path) => path switch
        {
            own or renamed => "Tunero",
            @"C:\Windows\notepad.exe" => "Microsoft Windows Operating System",
            @"C:\Tools\NoVersion.exe" => "",
            _ => null,
        };
        bool IsTunero(string name, string? image) => Platform.DefaultProfileHive.IsTuneroImage(name, image, "Tunero", own, Product);
        Assert.True(IsTunero("Tunero", @"D:\Other\Tunero.exe"));
        Assert.True(IsTunero("Tunero (1)", renamed));
        Assert.False(IsTunero("notepad", @"C:\Windows\notepad.exe"));
        Assert.False(IsTunero("NoVersion", @"C:\Tools\NoVersion.exe"));
        Assert.True(IsTunero("Gone", @"C:\Gone\Gone.exe"));
        Assert.True(IsTunero("Protected", null));

        // The renamed copy's build keeps its hives; the hives of the program with a reused id are leftovers.
        Assert.Equal(["TuneroMediaSystem-300-1"], OfflineSystemHive.LeftoversToUnload(
            ["TuneroMediaSystem-200-1", "TuneroMediaSystem-300-1"], 100, [],
            pid => pid == 200 ? IsTunero("Tunero (1)", renamed) : IsTunero("notepad", @"C:\Windows\notepad.exe")));
    }

    // ---- Settings: values written into the registry of the mounted install.wim ----

    private static MediaImageSettings Both => new(true, true, false);

    [Fact]
    public async Task SettingsAreWrittenReadBackAndUnloadedBeforeTheCommit()
    {
        await Run(Download() with { Settings = Both });
        Assert.Equal(1, _steps.Hives["SYSTEM"].Get(@"ControlSet001\Control\BitLocker", "PreventDeviceEncryption"));
        Assert.Equal(0, _steps.Hives["SOFTWARE"].Get(@"Microsoft\Windows\CurrentVersion\ReserveManager", "ShippedWithReserves"));
        Assert.True(_steps.Calls.IndexOf("UnloadWritableHive SOFTWARE") < _steps.Calls.IndexOf("Unmount commit"));
        Assert.True(_steps.Calls.IndexOf("UnloadWritableHive SYSTEM") < _steps.Calls.IndexOf("CheckHealth"));
        Assert.Contains(MediaStage.Settings, _steps.Progress.Select(p => p.Stage));
        Assert.Empty(_steps.LoadedWritableHives);
    }

    /// <summary>Settings alone mount the image, commit it and begin no Apps stage.</summary>
    [Fact]
    public async Task SettingsAloneMountAndCommitTheImage()
    {
        await Run(Download() with { Settings = new(true, false, false) });
        Assert.Contains("Mount install.wim 1", _steps.Calls);
        Assert.Contains("Unmount commit", _steps.Calls);
        Assert.DoesNotContain(MediaStage.Apps, _steps.Progress.Select(p => p.Stage));
        Assert.Equal(["LoadWritableHive SYSTEM", "UnloadWritableHive SYSTEM"], _steps.Calls.Where(c => c.Contains("WritableHive", StringComparison.Ordinal)));
        Assert.False(_steps.Hives.ContainsKey("SOFTWARE"));
    }

    /// <summary>A cleanup that is skipped for pending operations changed nothing: the settings still need the commit.</summary>
    [Fact]
    public async Task SettingsAreCommittedWhenTheComponentCleanupIsSkipped()
    {
        _steps.Dism.ComponentCleanupFails = unchecked((int)0x800F0806);
        await Run(Download() with { Settings = new(false, true, false) });
        Assert.Contains("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("Unmount discard", _steps.Calls.SkipWhile(c => c != "Mount install.wim 1").TakeWhile(c => c != "Mount boot.wim 2"));
    }

    /// <summary>The control set is the one SYSTEM names in Select\Current, never assumed.</summary>
    [Fact]
    public async Task TheControlSetComesFromSelectCurrent()
    {
        _steps.CurrentControlSet = 2;
        await Run(Download() with { Settings = new(true, false, false) });
        Assert.Equal(1, _steps.Hives["SYSTEM"].Get(@"ControlSet002\Control\BitLocker", "PreventDeviceEncryption"));
        Assert.Null(_steps.Hives["SYSTEM"].Get(@"ControlSet001\Control\BitLocker", "PreventDeviceEncryption"));
    }

    [Fact]
    public async Task ASystemHiveWithoutSelectCurrentFailsAtSettings()
    {
        _steps.CurrentControlSet = null;
        await FailsAt(Download() with { Settings = new(true, false, false) }, MediaStage.Settings, "Media_SettingsFailed");
        Assert.Contains("Unmount discard", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task AValueThatDoesNotReadBackFailsAtSettings()
    {
        _steps.HiveDropsWrites = true;
        await FailsAt(Download() with { Settings = new(true, false, false) }, MediaStage.Settings, "Media_SettingsFailed");
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task AHiveThatCannotBeLoadedFailsAtSettings()
    {
        _steps.WritableHiveLoadFails = true;
        await FailsAt(Download() with { Settings = new(false, true, false) }, MediaStage.Settings, "Media_SettingsFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task StopWhileAHiveIsLoadedUnloadsItBeforeTheDiscard()
    {
        using var stop = new CancellationTokenSource();
        _steps.StopOnHiveLoad = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download() with { Settings = Both }, stop.Token));
        var unload = _steps.Calls.IndexOf("UnloadWritableHive SYSTEM");
        Assert.True(unload > 0 && unload < _steps.Calls.IndexOf("Unmount discard"));
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        AssertCleanedUp();
    }

    /// <summary>A hive whose own unload gave up holds a file of the mounted install.wim: the cleanup unloads it before the discard.</summary>
    [Fact]
    public async Task AWritableHiveLeftLoadedIsUnloadedBeforeTheDiscard()
    {
        _steps.HiveUnloadGivesUp = true;
        _steps.HiveDropsWrites = true;
        await FailsAt(Download() with { Settings = new(true, false, false) }, MediaStage.Settings, "Media_SettingsFailed");
        var calls = _steps.Calls.SkipWhile(c => c != "LoadWritableHive SYSTEM").ToList();
        var leftover = calls.IndexOf("UnloadLeftoverHives");
        Assert.True(leftover > 0 && leftover < calls.IndexOf("Unmount discard"));
        AssertCleanedUp();
    }

    /// <summary>An unload that gives up on the success path fails the build at Settings, before the cleanup and the commit.</summary>
    [Fact]
    public async Task AHiveThatStaysLoadedAfterTheWritesFailsAtSettings()
    {
        _steps.HiveUnloadGivesUp = true;
        await FailsAt(Download() with { Settings = new(true, false, false) }, MediaStage.Settings, "Media_SettingsFailed");
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("ComponentCleanup", _steps.Calls);
        var calls = _steps.Calls.SkipWhile(c => c != "LoadWritableHive SYSTEM").ToList();
        var leftover = calls.IndexOf("UnloadLeftoverHives");
        Assert.True(leftover > 0 && leftover < calls.IndexOf("Unmount discard"));
        AssertCleanedUp();
    }

    [Fact]
    public async Task NoSettingsMeansNoHiveIsLoaded()
    {
        await Run(Download());
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("LoadWritableHive", StringComparison.Ordinal));
        Assert.DoesNotContain(MediaStage.Settings, _steps.Progress.Select(p => p.Stage));
    }

    [Fact]
    public async Task AllowUnsupportedUpgradesWritesMoSetup()
    {
        await Run(Download(answers: Plain with { AllowUnsupported = true }) with { Settings = new(false, false, true) });
        Assert.Equal(1, _steps.Hives["SYSTEM"].Get(@"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU"));
        Assert.Null(_steps.Hives["SYSTEM"].Get(@"ControlSet001\Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU"));
        Assert.False(_steps.Hives.ContainsKey("SOFTWARE"));
    }

    [Fact]
    public async Task AllowUnsupportedUpgradesNeedsAllowUnsupported()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download() with { Settings = new(false, false, true) }));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public void TheSettingsHaveOnePlaceForTheirValues()
    {
        Assert.Empty(MediaImageSettings.Values(MediaImageSettings.None, "ControlSet001"));
        Assert.False(MediaImageSettings.None.Any);
        Assert.True(new MediaImageSettings(false, false, true).Any);
        Assert.Equal([("SYSTEM", @"ControlSet003\Control\BitLocker", "PreventDeviceEncryption", 1)],
            MediaImageSettings.Values(new(true, false, false), "ControlSet003"));
        Assert.Equal([("SOFTWARE", @"Microsoft\Windows\CurrentVersion\ReserveManager", "ShippedWithReserves", 0)],
            MediaImageSettings.Values(new(false, true, false), "ControlSet003"));
        Assert.Equal([("SYSTEM", @"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1)],
            MediaImageSettings.Values(new(false, false, true), "ControlSet003"));
    }

    [Fact]
    public void LeftoverUnloadCoversImageHives() =>
        Assert.Equal(["TuneroMediaImage-999-1"], OfflineSystemHive.LeftoversToUnload(["TuneroMediaImage-999-1", "Other"], 1, [], _ => false));

    [Fact]
    public void ImageHivesOfARunningTuneroOrHeldByThisBuildStay() =>
        Assert.Equal(["TuneroMediaImage-300-2"],
            OfflineSystemHive.LeftoversToUnload(["TuneroMediaImage-100-1", "TuneroMediaImage-200-1", "TuneroMediaImage-300-2"], 100, ["TuneroMediaImage-100-1"], pid => pid == 200));

    [Fact]
    public void WorkFolderNamesAreRecognized()
    {
        Assert.Equal(@"X:\TuneroMedia.ab12cd34", MediaBuild.WorkFolderOf(@"X:\TuneroMedia.ab12cd34\mount"));
        Assert.Equal(@"X:\TuneroMedia.AB12CD34", MediaBuild.WorkFolderOf(@"X:\TuneroMedia.AB12CD34"));
        Assert.Null(MediaBuild.WorkFolderOf(@"X:\Data\TuneroMedia.ab12cd34\mount"));
        Assert.Null(MediaBuild.WorkFolderOf(@"X:\TuneroMedia.ab12cd3\mount"));
        Assert.Null(MediaBuild.WorkFolderOf(@"X:\TuneroMedia.ab12cd34x"));
        Assert.Null(MediaBuild.WorkFolderOf(@"\\server\share\TuneroMedia.ab12cd34"));
        Assert.Null(MediaBuild.WorkFolderOf(@"C:\Other"));
    }

    // ---- the real steps, in a temp folder only ----

    [Fact]
    public void RealStepsRefuseAShare()
    {
        var ex = Assert.Throws<MediaBuildException>(() => new MediaSteps().NewWorkFolder(@"\\server\share\Win11.iso"));
        Assert.Equal("Media_OutputNotLocal", ex.Key);
    }

    [Fact]
    public void RealStepsRemoveAWorkFolderThatCouldNotBeLocked()
    {
        var drive = Path.Combine(_root, "drive");
        Directory.CreateDirectory(drive);
        // Created but not locked (as when SecureFolder.Lock fails), and not trusted: DeleteWorkFolder would refuse it.
        var steps = new MediaSteps(_ => false, folder =>
        {
            Directory.CreateDirectory(folder);
            return false;
        });
        var ex = Assert.Throws<MediaBuildException>(() => steps.NewWorkFolder(Path.Combine(drive, "Win11.iso"), drive));
        Assert.Equal("Media_WorkFolderFailed", ex.Key);
        Assert.Empty(Directory.EnumerateFileSystemEntries(drive));
    }

    [Fact]
    public void RealStepsLockTheNewWorkFolderBeforeReturningIt()
    {
        var drive = Path.Combine(_root, "drive");
        Directory.CreateDirectory(drive);
        var steps = new MediaSteps(_ => true, folder =>
        {
            Directory.CreateDirectory(folder);
            return true;
        });
        var lockFile = "";
        using (var work = steps.NewWorkFolder(Path.Combine(drive, "Win11.iso"), drive))
        {
            Assert.True(MediaBuild.IsWorkFolderName(Path.GetFileName(work.Path)));
            Assert.Equal(drive, Path.GetDirectoryName(work.Path));
            lockFile = Path.Combine(work.Path, MediaBuild.LockFileName);
            Assert.Throws<IOException>(() => new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose());
        }
        new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
    }

    [Fact]
    public void RealStepsDeleteOnlyLockedWorkFoldersAndUnlinkLinks()
    {
        var drive = Path.Combine(_root, "drive");
        var work = Path.Combine(drive, "TuneroMedia.0a1b2c3d");
        var link = Path.Combine(drive, "TuneroMedia.ffff0000");
        var other = Path.Combine(drive, "TuneroMedia.notours");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(work, "media", "sources"));
        var wim = Path.Combine(work, "media", "sources", "install.wim");
        File.WriteAllText(wim, "x");
        File.SetAttributes(wim, FileAttributes.ReadOnly);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        TestFolders.Junction(Path.Combine(work, "link"), outside);
        TestFolders.Junction(link, outside);

        var steps = new MediaSteps();
        Assert.Equal([work, link], steps.LeftoverWorkFolders(drive).Order(StringComparer.OrdinalIgnoreCase));
        // Made by this user, not locked to Administrators and SYSTEM: someone else's folder, never deleted elevated.
        Assert.Throws<IOException>(() => steps.DeleteWorkFolder(work));
        Assert.True(File.Exists(wim));
        // A folder that is itself a link is only unlinked.
        steps.DeleteWorkFolder(link);
        Assert.False(Path.Exists(link));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.Throws<ArgumentException>(() => steps.DeleteWorkFolder(other));
        Assert.True(Directory.Exists(other));

        // A trusted (locked) folder goes with everything in it; a link inside is removed without touching its target.
        new MediaSteps(_ => true).DeleteWorkFolder(work);
        Assert.False(Directory.Exists(work));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")), "the link target was touched");
    }

    private sealed class Collect(List<MediaProgress> list) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => list.Add(value);
    }

    private sealed class Throwing(MediaStage stage) : IProgress<MediaProgress>
    {
        public bool Thrown { get; private set; }

        public void Report(MediaProgress value)
        {
            if (value.Stage != stage) return;
            Thrown = true;
            throw new InvalidOperationException("progress sink failed");
        }
    }
}

/// <summary>
/// An image inside a fake WIM or ESD. <paramref name="Files"/> is what a mount of it shows (relative path to bytes); null
/// stands for <see cref="FakeMediaSteps.PeFiles"/> without an answer file pointer.
/// </summary>
internal sealed record Img(string Name, string? Edition, string Architecture = "x64", string Version = "10.0.26300", bool Bootable = false, string Language = "en-US",
    string? OtherLanguage = null, IReadOnlyDictionary<string, byte[]>? Files = null);

/// <summary>
/// Records every step. A fake image file is a 208-byte WIM header (image count, boot index) followed by a token that names
/// its images, so exports, moves and deletes behave as on disk; GetWimInfo answers in DISM's text form.
/// </summary>
internal sealed class FakeMediaSteps : IMediaSteps
{
    private const int HeaderSize = 208;
    private readonly Dictionary<string, List<Img>> _images = [];
    private readonly string _root;

    public FakeMediaSteps(string root)
    {
        _root = root;
        Dism = new FakeDism(this);
        IsoWriter = new FakeIsoWriter(this);
        IsoReader = new FakeIsoReader(this);
        Reset();
    }

    public List<string> Calls { get; } = [];
    public List<string> LogLines { get; } = [];
    public List<MediaProgress> Progress { get; } = [];
    public List<string> DeletedFolders { get; } = [];
    public List<string> Drives { get; } = [];
    public Dictionary<string, List<string>> Leftovers { get; } = [];
    public Dictionary<string, byte[]> IsoContents { get; set; } = [];
    public List<Img> EsdImages { get; private set; } = [];

    public FakeDism Dism { get; private set; }
    public FakeIsoWriter IsoWriter { get; private set; }
    public FakeIsoReader IsoReader { get; private set; }
    IDism IMediaSteps.Dism => Dism;
    IIsoWriter IMediaSteps.IsoWriter => IsoWriter;
    IIsoReader IMediaSteps.IsoReader => IsoReader;

    public long Free { get; set; }
    public string? WorkFolder { get; private set; }
    public CancellationTokenSource? StopDownloadWith { get; set; }
    public bool LockHeldDuringDownload { get; private set; }
    public int DeleteFailures { get; set; }
    public bool DeleteThrowsArgument { get; set; }

    /// <summary>The next deletes refused with access denied on <see cref="DeniedPath"/>.</summary>
    public int DeleteDenials { get; set; }
    public string DeniedPath { get; set; } = "";

    /// <summary>Runs when a delete starts, before any refusal.</summary>
    public Action<string>? Deleting { get; set; }
    public bool ApplyPlantsImage { get; set; }

    /// <summary>The image file on the own ISO: "install.wim", "install.esd" or null for none.</summary>
    public string? OwnImageFile { get; set; }
    public bool OwnBothImages { get; set; }
    public string OwnVersion { get; set; } = "";
    public string OwnArchitecture { get; set; } = "";
    public string OwnLanguage { get; set; } = "en-US";

    /// <summary>A second language of the own ISO's images, listed before the default one; null for none.</summary>
    public string? OwnOtherLanguage { get; set; }
    /// <summary>The boot.wim images of the own ISO, or null for no boot.wim.</summary>
    public List<Img>? OwnBootImages { get; set; }
    public bool WithoutUefi { get; set; }
    public bool PlantOldFiles { get; set; }

    /// <summary>The setup files of the download carry a root Unattend.xml (Microsoft's never do).</summary>
    public bool SetupFilesCarryAnswerFile { get; set; }

    /// <summary>Hive files loaded through <see cref="LoadSystemHive"/> and not unloaded yet.</summary>
    public List<string> LoadedHives { get; } = [];

    public string LogPath => Path.Combine(_root, "media-test.log");

    public void Reset()
    {
        Calls.Clear();
        LogLines.Clear();
        LoadedHives.Clear();
        LoadedWritableHives.Clear();
        Hives.Clear();
        CurrentControlSet = 1;
        HiveDropsWrites = false;
        WritableHiveLoadFails = false;
        StopOnHiveLoad = null;
        Progress.Clear();
        DeletedFolders.Clear();
        IsoContents = [];
        Dism = new FakeDism(this);
        IsoWriter = new FakeIsoWriter(this);
        IsoReader = new FakeIsoReader(this);
        Free = long.MaxValue;
        DeleteFailures = 0;
        DeleteThrowsArgument = false;
        DeleteDenials = 0;
        DeniedPath = "";
        Deleting = null;
        OwnImageFile = "install.wim";
        OwnVersion = "10.0.26300";
        OwnArchitecture = "x64";
        OwnBootImages = [new("Microsoft Windows PE (amd64)", "WindowsPE"), new("Microsoft Windows Setup (amd64)", "WindowsPE", Bootable: true)];
        EsdImages =
        [
            new("Windows Setup Media", null, "<undefined>", "<undefined>"),
            new("Microsoft Windows PE (amd64)", "WindowsPE", Version: "10.0.26100.9711"),
            new("Microsoft Windows Setup (amd64)", "WindowsPE", Version: "10.0.26100.9711"),
            new("Windows 11 Home", "Core"),
            new("Windows 11 Home N", "CoreN"),
            new("Windows 11 Pro", "Professional"),
            new("Windows 11 Pro N", "ProfessionalN"),
        ];
    }

    // ---- fake image files ----

    public void PutImages(string path, IEnumerable<Img> images, string? token = null)
    {
        token ??= Guid.NewGuid().ToString("N");
        _images[token] = images.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteImageFile(path, token);
    }

    private void WriteImageFile(string path, string token) => File.WriteAllBytes(path, ImageBytes(_images[token], token));

    private const string FixedEsdToken = "fixed-esd";

    /// <summary>The download writes the same bytes each time (<see cref="EsdSha256"/>), as a real download of one catalog file does.</summary>
    public bool FixedEsd { get; set; }

    /// <summary>The SHA-256 of the ESD a download with <see cref="FixedEsd"/> writes, as the catalog would name it.</summary>
    public string EsdSha256() => Convert.ToHexString(SHA256.HashData(ImageBytes(EsdImages, FixedEsdToken))).ToLowerInvariant();

    /// <summary>Work folders <see cref="IsTrustedWorkFolder"/> refuses (not locked to administrators, in the real steps).</summary>
    public HashSet<string> Untrusted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsTrustedWorkFolder(string folder) =>
        MediaBuild.IsWorkFolderName(Path.GetFileName(folder.TrimEnd('\\'))) && Directory.Exists(folder) && !Untrusted.Contains(folder.TrimEnd('\\'));

    /// <summary>Header as in a real WIM: "MSWIM", header size 208, image count at 44, boot index at 120.</summary>
    private static byte[] ImageBytes(List<Img> images, string token)
    {
        var header = new byte[HeaderSize];
        "MSWIM\0\0\0"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(8), (uint)HeaderSize);
        BitConverter.TryWriteBytes(header.AsSpan(44), (uint)images.Count);
        BitConverter.TryWriteBytes(header.AsSpan(120), (uint)(images.FindLastIndex(i => i.Bootable) + 1));
        return [.. header, .. Encoding.UTF8.GetBytes(token)];
    }

    private static string TokenOf(byte[] content) => Encoding.UTF8.GetString(content.AsSpan(HeaderSize));

    public List<Img> ImagesAt(string path) =>
        File.Exists(path) && _images.TryGetValue(TokenOf(File.ReadAllBytes(path)), out var list)
            ? list
            : throw new DismException(2, "Error: 2\n\nThe system cannot find the file specified.");

    /// <summary>DISM adds to an existing destination file.</summary>
    public void AddImage(string path, Img image)
    {
        if (!File.Exists(path))
        {
            PutImages(path, [image]);
            return;
        }
        var token = TokenOf(File.ReadAllBytes(path));
        _images[token].Add(image);
        WriteImageFile(path, token);
    }

    public List<Img> ImagesOf(byte[] content) => _images[TokenOf(content)];

    /// <summary>
    /// What Windows PE's image holds of interest here: the SYSTEM hive, whose fake content is the value of Setup\UnattendFile
    /// ("" for none), plus <paramref name="extra"/> files.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> PeFiles(string unattendFile = "", params (string Path, string Content)[] extra)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [@"Windows\System32\config\SYSTEM"] = Encoding.UTF8.GetBytes(unattendFile) };
        foreach (var (path, content) in extra) files[path] = Encoding.UTF8.GetBytes(content);
        return files;
    }

    /// <summary>The files a mount of image <paramref name="index"/> of <paramref name="path"/> shows.</summary>
    public IReadOnlyDictionary<string, byte[]> FilesOf(string path, int index) => ImagesAt(path)[index - 1].Files ?? PeFiles();

    /// <summary>A commit: the image gets <paramref name="files"/> and the file a new token, as DISM rewrites the WIM.</summary>
    public void Commit(string path, int index, IReadOnlyDictionary<string, byte[]> files, bool breakHeader)
    {
        var images = ImagesAt(path).ToList();
        images[index - 1] = images[index - 1] with { Files = files };
        if (breakHeader) images = images.Select(i => i with { Bootable = false }).ToList();
        PutImages(path, images);
    }

    public static void Put(string folder, string relative, string content, bool readOnly = false)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    /// <summary>What index 1 of a Windows ESD unpacks to (and what an ISO holds besides its images).</summary>
    public void WriteSetupFiles(string folder)
    {
        Put(folder, @"boot\etfsboot.com", "bios boot");
        if (!WithoutUefi)
        {
            Put(folder, @"efi\microsoft\boot\efisys.bin", "uefi boot");
            Put(folder, @"efi\boot\bootx64.efi", "uefi loader");
        }
        Put(folder, @"sources\setup.exe", "setup");
        Put(folder, "setup.exe", "setup");
        if (SetupFilesCarryAnswerFile) Put(folder, "Unattend.xml", "<old/>");
    }

    private static bool IsHeldOpen(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // ---- IMediaSteps ----

    public Task DownloadAsync(EsdFile file, string target, IProgress<(long Done, long Total)>? p, CancellationToken ct)
    {
        Calls.Add("Download");
        LockHeldDuringDownload = IsHeldOpen(Path.Combine(WorkFolder!, MediaBuild.LockFileName));
        if (StopDownloadWith is { } stop)
        {
            File.WriteAllText(target + ".part", "half");
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
        }
        p?.Report((file.Size / 2, file.Size));
        PutImages(target, EsdImages, FixedEsd ? FixedEsdToken : null);
        return Task.CompletedTask;
    }

    /// <summary>Like the real one, the folder lets only one account in (here this user, protected from inheritance).</summary>
    public WorkFolder NewWorkFolder(string outputIso)
    {
        Calls.Add("NewWorkFolder");
        WorkFolder = Path.Combine(_root, "TuneroMedia." + System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true));
        var folder = Directory.CreateDirectory(WorkFolder);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
        return Media.WorkFolder.Hold(WorkFolder);
    }

    public void DeleteWorkFolder(string folder)
    {
        Calls.Add("DeleteWorkFolder");
        Deleting?.Invoke(folder);
        if (DeleteThrowsArgument) throw new ArgumentException("refused");
        if (DeleteDenials > 0)
        {
            DeleteDenials--;
            // The message of the refusal in VM 4b: a file of the image that DISM no longer listed.
            throw new UnauthorizedAccessException($"Access to the path '{DeniedPath}' is denied.");
        }
        if (DeleteFailures > 0)
        {
            DeleteFailures--;
            throw new IOException("in use");
        }
        DeletedFolders.Add(folder);
        if (folder.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) TestFolders.Delete(folder);
    }

    public long FreeBytes(string path) => Free;

    public IEnumerable<string> LocalDrives() => Drives;

    public IEnumerable<string> LeftoverWorkFolders(string drive) => Leftovers.TryGetValue(drive, out var list) ? list : [];

    /// <summary>The command lines of the running dism.exe processes.</summary>
    public List<string> DismRunning { get; } = [];

    /// <summary>They end at this look (-1: never); <see cref="DismEnded"/> runs then.</summary>
    public int DismEndsAfterLooks { get; set; } = -1;

    public Action? DismEnded { get; set; }
    public Action? DismLooked { get; set; }
    public int DismLooks { get; private set; }

    /// <summary>A dism.exe runs whose command line cannot be read.</summary>
    public bool DismUnreadable { get; set; }

    public IReadOnlyList<string>? DismCommandLines()
    {
        DismLooks++;
        if (DismUnreadable) return null;
        DismLooked?.Invoke();
        if (DismRunning.Count > 0 && DismEndsAfterLooks >= 0 && DismLooks >= DismEndsAfterLooks)
        {
            DismRunning.Clear();
            Calls.Add("DISM ended");
            DismEnded?.Invoke();
        }
        return DismRunning.ToList();
    }

    public IOfflineHive LoadSystemHive(string file)
    {
        Calls.Add("LoadHive");
        Assert.True(Path.IsPathFullyQualified(file), $"{file} is not absolute");
        if (!File.Exists(file)) throw new IOException($"{file} could not be loaded as a registry hive: The system cannot find the file specified.");
        LoadedHives.Add(file);
        return new FakeHive(this, file, File.ReadAllText(file));
    }

    // ---- writable hives of a mounted install.wim ----

    /// <summary>The hives loaded through <see cref="LoadWritableHive"/> (by name: SYSTEM, SOFTWARE), kept after they are unloaded so a test can read what was written.</summary>
    public Dictionary<string, FakeWritableHive> Hives { get; } = [];

    /// <summary>Hive names loaded and not unloaded yet.</summary>
    public List<string> LoadedWritableHives { get; } = [];

    /// <summary>Select\Current of the fake SYSTEM hive; null: the value is missing.</summary>
    public int? CurrentControlSet { get; set; } = 1;

    /// <summary>A write is dropped, so the read-back differs.</summary>
    public bool HiveDropsWrites { get; set; }
    public bool WritableHiveLoadFails { get; set; }

    /// <summary>Cancelled when a writable hive is loaded.</summary>
    public CancellationTokenSource? StopOnHiveLoad { get; set; }

    public IWritableHive LoadWritableHive(string file)
    {
        Assert.True(Path.IsPathFullyQualified(file), $"{file} is not absolute");
        var name = Path.GetFileName(file);
        Assert.EndsWith(@"\Windows\System32\config\" + name, file);
        Calls.Add("LoadWritableHive " + name);
        if (WritableHiveLoadFails) throw new IOException($"{file} could not be loaded as a registry hive: Access is denied.");
        var hive = new FakeWritableHive(this, name);
        Hives[name] = hive;
        LoadedWritableHives.Add(name);
        StopOnHiveLoad?.Cancel();
        return hive;
    }

    /// <summary>The hive's own unload gives up (a handle stays open) and leaves it loaded; the leftover unload then works.</summary>
    public bool HiveUnloadGivesUp { get; set; }

    public void UnloadLeftoverHives()
    {
        Calls.Add("UnloadLeftoverHives");
        LoadedHives.Clear();
        LoadedWritableHives.Clear();
    }

    public void Log(string line) => LogLines.Add(line);

    private sealed class FakeHive(FakeMediaSteps steps, string file, string unattendFile) : IOfflineHive
    {
        private bool _unloaded;

        public string? SetupValue(string name)
        {
            Assert.False(_unloaded, "the hive was read after it was unloaded");
            return name == "UnattendFile" && unattendFile.Length > 0 ? unattendFile : null;
        }

        public void Dispose()
        {
            if (_unloaded) return;
            _unloaded = true;
            steps.Calls.Add("UnloadHive");
            if (!steps.HiveUnloadGivesUp) steps.LoadedHives.Remove(file);
        }
    }
}

/// <summary>A registry hive of the mounted image, in memory: values by key and name.</summary>
internal sealed class FakeWritableHive(FakeMediaSteps steps, string name) : IWritableHive
{
    private readonly Dictionary<(string Key, string Name), int> _values = new();
    private bool _unloaded;

    public int? Get(string key, string valueName) => _values.TryGetValue((key, valueName), out var v) ? v : null;

    public string ControlSet =>
        steps.CurrentControlSet is { } n ? $"ControlSet{n:D3}" : throw new IOException(@"The Select\Current value of the SYSTEM hive is missing.");

    public void SetDword(string key, string valueName, int value)
    {
        Assert.False(_unloaded, "the hive was written after it was unloaded");
        if (!steps.HiveDropsWrites) _values[(key, valueName)] = value;
    }

    public int? GetDword(string key, string valueName)
    {
        Assert.False(_unloaded, "the hive was read after it was unloaded");
        return Get(key, valueName);
    }

    public void Unload()
    {
        if (_unloaded) return;
        _unloaded = true;
        steps.Calls.Add("UnloadWritableHive " + name);
        if (steps.HiveUnloadGivesUp) throw new IOException($"The image hive {name} could not be unloaded: a handle stays open.");
        steps.LoadedWritableHives.Remove(name);
    }

    public void Dispose()
    {
        try
        {
            Unload();
        }
        catch (IOException)
        {
            // Dispose never throws: the hive stays loaded for the leftover unload.
        }
    }
}

internal sealed class FakeDism(FakeMediaSteps steps) : IDism
{
    public List<string> Provisioned { get; set; } = [];

    /// <summary>/Apply-Image fails with a DISM error that a retry can fix (a full disk).</summary>
    public bool ApplyFails { get; set; }

    /// <summary>The capabilities and features the image holds (name to state), as /Get-Capabilities and /Get-Features list them.</summary>
    public Dictionary<string, string> Capabilities { get; set; } = [];
    public Dictionary<string, string> Features { get; set; } = [];
    public bool RemoveCapabilityFails { get; set; }
    public bool ProvisionedThrows { get; set; }
    public bool MountThrows { get; set; }
    public bool MountedImagesThrows { get; set; }
    public bool Healthy { get; set; } = true;
    /// <summary>/StartComponentCleanup fails with this DISM error code.</summary>
    public int? ComponentCleanupFails { get; set; }
    public int UnmountFailures { get; set; }
    public CancellationTokenSource? StopOnRemove { get; set; }
    public List<string> Mounted { get; } = [];
    public List<string> Unmounted { get; } = [];
    public Func<string, Img, Img>? ExportTamper { get; set; }
    public string? DoubleExportInto { get; set; }

    /// <summary>A mount of this image file (by name) fails, or is stopped by <see cref="StopOnMount"/> once it is mounted.</summary>
    public string? MountFailsFor { get; set; }
    public string? StopOnMountOf { get; set; }
    public CancellationTokenSource? StopOnMount { get; set; }

    /// <summary>A commit writes a header without a boot index, or changes the files on the way.</summary>
    public bool CommitBreaksHeader { get; set; }
    public Action<Dictionary<string, byte[]>>? CommitTamper { get; set; }

    private readonly Dictionary<string, (string Image, int Index, bool ReadOnly)> _mounts = new(StringComparer.OrdinalIgnoreCase);

    private static string Name(string path) => Path.GetFileName(path);

    private static void Absolute(string path) => Assert.True(Path.IsPathFullyQualified(path), $"{path} is not absolute");

    public Task<string> GetWimInfoAsync(string image, int? index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Absolute(image);
        steps.Calls.Add($"WimInfo {Name(image)}" + (index is { } i ? $" {i}" : ""));
        var images = steps.ImagesAt(image);
        var text = new StringBuilder("Deployment Image Servicing and Management tool\nVersion: 10.0.26100.9549\n\nDetails for image : " + image + "\n\n");
        if (index is null)
        {
            for (var n = 0; n < images.Count; n++)
                text.Append($"Index : {n + 1}\nName : {images[n].Name}\nDescription : {images[n].Name}\nSize : 1.000 bytes\n\n");
        }
        else
        {
            if (index < 1 || index > images.Count) throw new DismException(87, "Error: 87\n\nThe specified image does not exist in the WIM.");
            var img = images[index.Value - 1];
            var version = img.Version.Split('.');
            // DISM's "WIM Bootable" line says No even for boot images (real 26H2 output); the header holds the boot index.
            text.Append($"Index : {index}\nName : {img.Name}\nDescription : {img.Name}\nSize : 1.000 bytes\nWIM Bootable : No\n");
            text.Append($"Architecture : {img.Architecture}\nHal : <undefined>\n");
            text.Append(version.Length >= 3
                ? $"Version : {string.Join('.', version.Take(3))}\nServicePack Build : {(version.Length > 3 ? version[3] : "1")}\n"
                : "Version : <undefined>\nServicePack Build : <undefined>\n");
            text.Append($"ServicePack Level : 0\nEdition : {img.Edition ?? "<undefined>"}\n");
            text.Append("Installation : Client\nLanguages :\n");
            if (img.OtherLanguage is { } other) text.Append($"\t{other}\n");
            text.Append($"\t{img.Language} (Default)\n");
        }
        text.Append("\nThe operation completed successfully.\n");
        return Task.FromResult(text.ToString());
    }

    public Task ApplyAsync(string image, int index, string target, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Absolute(image);
        Absolute(target);
        steps.Calls.Add($"Apply {Name(image)} {index}");
        if (ApplyFails) throw new DismException(112, "Error: 112\n\nThere is not enough space on the disk.");
        steps.WriteSetupFiles(target);
        if (steps.ApplyPlantsImage) steps.PutImages(Path.Combine(target, "sources", "install.wim"), [new Img("Windows 11 Pro", "Professional")]);
        p?.Report(50);
        return Task.CompletedTask;
    }

    public Task ExportAsync(string source, int index, string target, bool bootable, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Absolute(source);
        Absolute(target);
        steps.Calls.Add($"Export {Name(source)} {index} {Name(target)}" + (bootable ? " bootable" : ""));
        var img = steps.ImagesAt(source)[index - 1] with { Bootable = bootable };
        if (ExportTamper is not null) img = ExportTamper(target, img);
        steps.AddImage(target, img);
        if (string.Equals(Name(target), DoubleExportInto, StringComparison.OrdinalIgnoreCase)) steps.AddImage(target, img);
        p?.Report(50);
        return Task.CompletedTask;
    }

    public Task ExportRecoveryAsync(string source, int index, string target, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Absolute(source);
        Absolute(target);
        steps.Calls.Add($"ExportRecovery {Name(source)} {index} {Name(target)}");
        steps.AddImage(target, steps.ImagesAt(source)[index - 1]);
        if (DoubleRecoveryExport) steps.AddImage(target, steps.ImagesAt(source)[index - 1]);
        p?.Report(50);
        return Task.CompletedTask;
    }

    /// <summary>The export writes the image twice into install.esd (DISM adds to an existing file).</summary>
    public bool DoubleRecoveryExport { get; set; }

    public Task MountAsync(string image, int index, string mountDir, bool readOnly, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Absolute(image);
        Absolute(mountDir);
        Assert.True(Directory.Exists(mountDir), "the mount folder must exist");
        Assert.Empty(Directory.EnumerateFileSystemEntries(mountDir));
        steps.Calls.Add($"Mount {Name(image)} {index}" + (readOnly ? " readonly" : ""));
        if (MountThrows || string.Equals(Name(image), MountFailsFor, StringComparison.OrdinalIgnoreCase))
            throw new DismException(5, "Error: 5\n\nAccess is denied.");
        Mounted.Add(mountDir);
        _mounts[mountDir] = (image, index, readOnly);
        foreach (var (relative, content) in steps.FilesOf(image, index))
        {
            var path = Path.Combine(mountDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        p?.Report(50);
        if (StopOnMount is { } stop && string.Equals(Name(image), StopOnMountOf, StringComparison.OrdinalIgnoreCase))
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ProvisionedAsync(string mountDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("Provisioned");
        if (ProvisionedThrows) throw new DismException(87, "Error: 87\n\nThe parameter is incorrect.");
        return Task.FromResult<IReadOnlyList<string>>(Provisioned);
    }

    public Task RemoveProvisionedAsync(string mountDir, string packageName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add($"Remove {packageName}");
        if (StopOnRemove is { } stop)
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync(string mountDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("Capabilities");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Capabilities, StringComparer.OrdinalIgnoreCase));
    }

    public Task RemoveCapabilityAsync(string mountDir, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add($"RemoveCapability {name}");
        if (RemoveCapabilityFails) throw new DismException(87, "Error: 87\n\nThe parameter is incorrect.");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> FeaturesAsync(string mountDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("Features");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Features, StringComparer.OrdinalIgnoreCase));
    }

    public Task DisableFeatureAsync(string mountDir, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add($"DisableFeature {name}");
        return Task.CompletedTask;
    }

    public Task ComponentCleanupAsync(string mountDir, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("ComponentCleanup");
        if (ComponentCleanupFails is { } code) throw new DismException(code, $"Error: 0x{code:x8}");
        return Task.CompletedTask;
    }

    public Task<string> CheckHealthAsync(string mountDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("CheckHealth");
        return Task.FromResult(Healthy
            ? "Image Version: 10.0.26300.9711\n\nNo component store corruption detected.\nThe operation completed successfully.\n"
            : "Image Version: 10.0.26300.9711\n\nThe component store is repairable.\nThe operation completed successfully.\n");
    }

    public Task UnmountAsync(string mountDir, bool commit, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add($"Unmount {(commit ? "commit" : "discard")}");
        if (UnmountFailures > 0)
        {
            UnmountFailures--;
            throw new DismException(5, "Error: 5\n\nAccess is denied.");
        }
        // An image that needs a remount (the PC restarted while it was mounted) cannot be unmounted before it.
        if (Status.TryGetValue(mountDir, out var status) && status != "Ok")
            throw new DismException(5, $"Error: 5\n\nThe image is not usable ({status}).");
        // A hive loaded from the image keeps its file open: DISM cannot unmount it.
        if (steps.LoadedHives.Any(h => h.StartsWith(mountDir + @"\", StringComparison.OrdinalIgnoreCase)))
            throw new DismException(5, "Error: 0xc1420117\n\nThe directory could not be completely unmounted.");
        if (_mounts.Remove(mountDir, out var mount))
        {
            if (commit)
            {
                Assert.False(mount.ReadOnly, "a read-only mount was committed");
                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(mountDir, "*", SearchOption.AllDirectories))
                    files[Path.GetRelativePath(mountDir, file)] = File.ReadAllBytes(file);
                CommitTamper?.Invoke(files);
                steps.Commit(mount.Image, mount.Index, files, CommitBreaksHeader);
            }
            // DISM leaves the empty mount folder behind.
            foreach (var entry in new DirectoryInfo(mountDir).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo d) d.Delete(recursive: true);
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
        }
        Unmounted.Add(mountDir);
        Mounted.RemoveAll(m => string.Equals(m, mountDir, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MountedImage>> MountedImagesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("MountedImages");
        if (MountedImagesThrows) throw new DismException(5, "Error: 5\n\nAccess is denied.");
        return Task.FromResult<IReadOnlyList<MountedImage>>(Mounted.Select(m => new MountedImage(m, Status.GetValueOrDefault(m, "Ok"))).ToList());
    }

    /// <summary>The Status that Get-MountedImageInfo shows for a mount folder ("Ok" when not set here).</summary>
    public Dictionary<string, string> Status { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RemountFails { get; set; }

    public Task RemountAsync(string mountDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Absolute(mountDir);
        steps.Calls.Add("Remount");
        if (RemountFails) throw new DismException(5, "Error: 5\n\nAccess is denied.");
        Status.Remove(mountDir);
        return Task.CompletedTask;
    }

    /// <summary>What /Cleanup-Mountpoints does: unmount an image DISM no longer lists, or bring it back into the list.</summary>
    public Action? CleanupDoes { get; set; }

    public Task CleanupMountpointsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("CleanupMountpoints");
        CleanupDoes?.Invoke();
        return Task.CompletedTask;
    }
}

internal sealed class FakeIsoWriter(FakeMediaSteps steps) : IIsoWriter
{
    public CancellationTokenSource? StopWith { get; set; }
    public Action? BeforeWrite { get; set; }
    public string? Target { get; private set; }

    /// <summary>The write fails halfway (IMAPI2 error), leaving its partial file.</summary>
    public bool Fails { get; set; }

    public Task WriteAsync(string mediaFolder, string isoPath, string volumeName, IProgress<double>? p, CancellationToken ct)
    {
        steps.Calls.Add("WriteIso");
        BeforeWrite?.Invoke();
        if (Fails)
        {
            File.WriteAllText(isoPath + ".part", "half an ISO");
            throw new IOException("The ISO could not be written.");
        }
        Target = isoPath;
        if (StopWith is { } stop)
        {
            File.WriteAllText(isoPath + ".part", "half an ISO");
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
        }
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(mediaFolder, "*", SearchOption.AllDirectories))
            contents[Path.GetRelativePath(mediaFolder, file)] = File.ReadAllBytes(file);
        steps.IsoContents = contents;
        File.WriteAllText(isoPath, "ISO " + volumeName);
        p?.Report(1.0);
        return Task.CompletedTask;
    }
}

internal sealed class FakeIsoReader(FakeMediaSteps steps) : IIsoReader
{
    public int? AttachError { get; set; }
    public bool BootCatalogOk { get; set; } = true;

    /// <summary>The build never reads ISO info (the page does, before it).</summary>
    public Task<IsoInfo> ReadInfoAsync(string isoPath, CancellationToken ct) => throw new NotSupportedException();
    public List<string> Mismatch { get; set; } = [];
    public List<string> Verified { get; } = [];
    public string? VerifiedIso { get; private set; }

    public Task<string> CopyMediaAsync(string isoPath, string targetFolder, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("CopyMedia");
        if (AttachError is { } error) throw new IsoAttachException(error, $"The ISO could not be opened: error {error}.");
        steps.WriteSetupFiles(targetFolder);
        if (steps.OwnBootImages is { } boot) steps.PutImages(Path.Combine(targetFolder, "sources", "boot.wim"), boot);
        Img[] editions =
        [
            new("Windows 11 Home", "Core", steps.OwnArchitecture, steps.OwnVersion, Language: steps.OwnLanguage, OtherLanguage: steps.OwnOtherLanguage),
            new("Windows 11 Pro", "Professional", steps.OwnArchitecture, steps.OwnVersion, Language: steps.OwnLanguage, OtherLanguage: steps.OwnOtherLanguage),
        ];
        if (steps.OwnImageFile is { } name) steps.PutImages(Path.Combine(targetFolder, "sources", name), editions);
        if (steps.OwnBothImages)
        {
            steps.PutImages(Path.Combine(targetFolder, "sources", "install.wim"), editions);
            steps.PutImages(Path.Combine(targetFolder, "sources", "install.esd"), editions);
        }
        if (steps.PlantOldFiles)
        {
            FakeMediaSteps.Put(targetFolder, "autounattend.xml", "<old/>", readOnly: true);
            FakeMediaSteps.Put(targetFolder, "Unattend.xml", "<old/>", readOnly: true);
            FakeMediaSteps.Put(targetFolder, @"sources\autounattend.xml", "<old/>", readOnly: true);
            FakeMediaSteps.Put(targetFolder, @"sources\Unattend.XML", "<old/>");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Setup\Tunero\Tunero.exe", "old Tunero", readOnly: true);
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Setup\Tunero\old.txt", "old");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Setup\Scripts\keep.cmd", "rem keep");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Panther\unattend.xml", "<old/>", readOnly: true);
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Panther\AutoUnattend.XML", "<old/>");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Panther\Unattend\Unattend.xml", "<old/>");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Panther\Unattend\AutoUnattend.xml", "<old/>");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\System32\Sysprep\unattend.xml", "<old/>");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$$\Panther\notes.txt", "keep");
            // $1 is copied to the root of the Windows drive, where Setup also looks for Unattend.xml and Autounattend.xml.
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$1\Autounattend.xml", "<old/>");
            FakeMediaSteps.Put(targetFolder, @"sources\$OEM$\$1\keep.txt", "keep");
        }
        p?.Report(1.0);
        return Task.FromResult(targetFolder);
    }

    public Task<IsoCheck> VerifyAsync(string isoPath, string mediaFolder, IReadOnlyList<string> relativeFiles, IProgress<double>? p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("VerifyIso");
        Assert.True(File.Exists(isoPath));
        VerifiedIso = isoPath;
        Verified.AddRange(relativeFiles);
        var mismatched = relativeFiles.Where(f => !File.Exists(Path.Combine(mediaFolder, f))).Concat(Mismatch).ToList();
        return Task.FromResult(new IsoCheck(BootCatalogOk, mismatched));
    }
}
