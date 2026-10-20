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

    private Task<string> Run(MediaRequest r, CancellationToken ct = default, IProgress<MediaProgress>? progress = null) =>
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
    }

    // ---- stage order and the download path ----

    [Fact]
    public async Task DownloadSourceRunsTheStagesInOrder()
    {
        Assert.Equal(OutputIso, await Run(Download()));
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
    public async Task EditionIsFoundByEditionId()
    {
        await Run(Download("CoreN"));
        Assert.Contains("Export image.esd 5 install.wim", _steps.Calls);
        _steps.Calls.Clear();
        await Run(Download("Professional"));
        Assert.Contains("Export image.esd 6 install.wim", _steps.Calls);
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
        Assert.Equal(OutputIso, await Run(Download()));
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
        Assert.Equal(upper, await Run(Download() with { OutputIso = upper }));
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
            "Mount install.wim 1", "Provisioned", "Remove Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe", "CheckHealth", "Unmount commit",
        ], apps);
        Assert.Empty(_steps.Dism.Mounted);
    }

    [Fact]
    public async Task NothingToRemoveLeavesTheImageAsItWas()
    {
        _steps.Dism.Provisioned = ["Microsoft.WindowsStore_22510.1401.5.0_neutral_~_8wekyb3d8bbwe"];
        await Run(Download(apps: ["Microsoft.BingNews"]));
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Remove", StringComparison.Ordinal));
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
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
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Apps, "Media_Unhealthy");
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
        Assert.Equal(["Mount boot.wim 2", "LoadHive", "UnloadHive", "Unmount discard", "UnloadLeftoverHives", "Unmount discard", "DeleteWorkFolder"],
            _steps.Calls.SkipWhile(c => c != "Mount boot.wim 2"));
        AssertCleanedUp();
    }

    [Fact]
    public async Task BootImageWithoutASystemHiveFailsClosed()
    {
        _steps.OwnBootImages![1] = _steps.OwnBootImages[1] with { Files = new Dictionary<string, byte[]>() };
        await FailsAt(OwnIso(), MediaStage.Files, "Media_ImageCheckFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task BootWimThatNoLongerBootsImage2AfterTheCommitFails()
    {
        _steps.Dism.CommitBreaksHeader = true;
        await FailsAt(Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" }), MediaStage.Files, "Media_ImageCheckFailed");
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertCleanedUp();
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
        AssertCleanedUp();
    }

    [Fact]
    public async Task StopWhileBootWimIsMountedDiscardsIt()
    {
        using var stop = new CancellationTokenSource();
        _steps.Dism.StopOnMountOf = "boot.wim";
        _steps.Dism.StopOnMount = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(answers: Plain with { SkipSetup = true, AccountName = "Anna" }), stop.Token));
        Assert.Equal(["Mount boot.wim 2", "UnloadLeftoverHives", "Unmount discard", "DeleteWorkFolder"], _steps.Calls.SkipWhile(c => c != "Mount boot.wim 2"));
        AssertCleanedUp();
    }

    [Fact]
    public async Task FailingBootWimMountIsDiscarded()
    {
        _steps.Dism.MountFailsFor = "boot.wim";
        var ex = await FailsAt(Download(), MediaStage.Files, null);
        Assert.Equal(5, ex.Code);
        Assert.Contains("Unmount discard", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task AnswerFileOnTheDownloadedMediaFailsTheFilesCheck()
    {
        _steps.SetupFilesCarryAnswerFile = true;
        await FailsAt(Download(), MediaStage.Files, "Media_FilesCheckFailed");
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task LeftoverBootWimMountIsDiscardedAfterItsHive()
    {
        _steps.Dism.Mounted.Add(@"X:\TuneroMedia.ab12cd34\bootmount");
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedDirs", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedDirs", "DeleteWorkFolder"], _steps.Calls);
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
        Assert.Equal(3, Count("Unmount discard"));
        Assert.Contains("CleanupMountpoints", _steps.Calls);
        // Deleting the folder would delete files inside the mounted image; the next start cleans it up.
        Assert.DoesNotContain("DeleteWorkFolder", _steps.Calls);
        Assert.Contains(_steps.LogLines, l => l.Contains("still mounted", StringComparison.Ordinal));
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
        // The partial ISO was written inside the work folder, which is gone.
        Assert.StartsWith(_steps.WorkFolder!, _steps.IsoWriter.Target!, StringComparison.OrdinalIgnoreCase);
        AssertCleanedUp();
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
        Assert.Equal(OutputIso, await Run(OwnIso()));
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

    // ---- ISO, Verify and the output path ----

    [Fact]
    public async Task VerifyMismatchNeverReachesTheOutput()
    {
        File.WriteAllText(OutputIso, "older ISO");
        _steps.IsoReader.Mismatch = [@"sources\install.wim"];
        await FailsAt(Download(), MediaStage.Verify, "Media_VerifyFailed");
        Assert.Equal("older ISO", File.ReadAllText(OutputIso));
        Assert.False(Directory.Exists(_steps.WorkFolder));
    }

    [Fact]
    public async Task VerifyMismatchLeavesNoIso()
    {
        _steps.IsoReader.Mismatch = [@"sources\install.wim"];
        await FailsAt(Download(), MediaStage.Verify, "Media_VerifyFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task BadBootCatalogLeavesNoIso()
    {
        _steps.IsoReader.BootCatalogOk = false;
        await FailsAt(Download(), MediaStage.Verify, "Media_VerifyFailed");
        AssertCleanedUp();
    }

    [Fact]
    public async Task VerifyFailureAfterACommitDoesNotDiscardAgain()
    {
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        _steps.IsoReader.Mismatch = [@"sources\boot.wim"];
        await FailsAt(Download(apps: ["Microsoft.BingNews"]), MediaStage.Verify, "Media_VerifyFailed");
        Assert.Equal(1, Count("Unmount commit"));
        Assert.DoesNotContain(_steps.Calls.SkipWhile(c => c != "WriteIso"), c => c.StartsWith("Unmount", StringComparison.Ordinal));
        AssertCleanedUp();
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
        Assert.False(Directory.Exists(_steps.WorkFolder));
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
            Assert.Equal(OutputIso, await Run(Download()));
        }
    }

    [Fact]
    public async Task WorkFolderThatCannotBeDeletedKeepsTheVerifiedIso()
    {
        _steps.DeleteFailures = int.MaxValue;
        Assert.Equal(OutputIso, await Run(Download()));
        Assert.True(File.Exists(OutputIso));
        Assert.Equal(3, Count("DeleteWorkFolder"));

        _steps.Reset();
        _steps.DeleteThrowsArgument = true;
        Assert.Equal(OutputIso, await Run(Download()));
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
        var dismCalls = _steps.Calls.Count(c => c.Split(' ')[0] is "WimInfo" or "Apply" or "Export" or "Mount" or "Provisioned" or "Remove" or "CheckHealth" or "Unmount");
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

    // ---- leftovers ----

    [Fact]
    public async Task LeftoversAreDiscardedThenDeleted()
    {
        _steps.Dism.Mounted.AddRange([@"X:\TuneroMedia.ab12cd34\mount", @"C:\Other"]);
        await Build().CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedDirs", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedDirs", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34\mount"], _steps.Dism.Unmounted);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34"], _steps.DeletedFolders);
        Assert.Equal([@"C:\Other"], _steps.Dism.Mounted);
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
        Assert.Equal(["MountedDirs"], _steps.Calls);
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
        Assert.Equal(["DISM ended", "MountedDirs", "UnloadLeftoverHives", "Unmount discard", "CleanupMountpoints", "MountedDirs", "DeleteWorkFolder"], _steps.Calls);
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
        _steps.Dism.MountedDirsThrows = true;
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff"];
        await Build().CleanLeftoversAsync(CancellationToken.None);
        // Without the list of mounted images no folder may be deleted: one could hold a mounted image.
        Assert.Empty(_steps.DeletedFolders);

        _steps.Dism.MountedDirsThrows = false;
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
        Progress.Clear();
        DeletedFolders.Clear();
        IsoContents = [];
        Dism = new FakeDism(this);
        IsoWriter = new FakeIsoWriter(this);
        IsoReader = new FakeIsoReader(this);
        Free = long.MaxValue;
        DeleteFailures = 0;
        DeleteThrowsArgument = false;
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

    public void PutImages(string path, IEnumerable<Img> images)
    {
        var token = Guid.NewGuid().ToString("N");
        _images[token] = images.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteImageFile(path, token);
    }

    /// <summary>Header as in a real WIM: "MSWIM", header size 208, image count at 44, boot index at 120.</summary>
    private void WriteImageFile(string path, string token)
    {
        var images = _images[token];
        var header = new byte[HeaderSize];
        "MSWIM\0\0\0"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(8), (uint)HeaderSize);
        BitConverter.TryWriteBytes(header.AsSpan(44), (uint)images.Count);
        BitConverter.TryWriteBytes(header.AsSpan(120), (uint)(images.FindLastIndex(i => i.Bootable) + 1));
        File.WriteAllBytes(path, [.. header, .. Encoding.UTF8.GetBytes(token)]);
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
        PutImages(target, EsdImages);
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
        if (DeleteThrowsArgument) throw new ArgumentException("refused");
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

    /// <summary>The hive's own unload gives up (a handle stays open) and leaves it loaded; the leftover unload then works.</summary>
    public bool HiveUnloadGivesUp { get; set; }

    public void UnloadLeftoverHives()
    {
        Calls.Add("UnloadLeftoverHives");
        LoadedHives.Clear();
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

internal sealed class FakeDism(FakeMediaSteps steps) : IDism
{
    public List<string> Provisioned { get; set; } = [];
    public bool ProvisionedThrows { get; set; }
    public bool MountThrows { get; set; }
    public bool MountedDirsThrows { get; set; }
    public bool Healthy { get; set; } = true;
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

    public Task<IReadOnlyList<string>> MountedDirsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("MountedDirs");
        if (MountedDirsThrows) throw new DismException(5, "Error: 5\n\nAccess is denied.");
        return Task.FromResult<IReadOnlyList<string>>(Mounted.ToList());
    }

    public Task CleanupMountpointsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        steps.Calls.Add("CleanupMountpoints");
        return Task.CompletedTask;
    }
}

internal sealed class FakeIsoWriter(FakeMediaSteps steps) : IIsoWriter
{
    public CancellationTokenSource? StopWith { get; set; }
    public Action? BeforeWrite { get; set; }
    public string? Target { get; private set; }

    public Task WriteAsync(string mediaFolder, string isoPath, string volumeName, IProgress<double>? p, CancellationToken ct)
    {
        steps.Calls.Add("WriteIso");
        BeforeWrite?.Invoke();
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
