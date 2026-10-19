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

    private MediaRequest Download(string edition = "Professional", IReadOnlyList<string>? apps = null, AnswerChoices? answers = null) =>
        new(new MediaSource(Esd, null, null), edition, null, apps ?? [], answers ?? Plain, null, null, OutputIso);

    private MediaRequest OwnIso(string edition = "Professional", int? index = 2)
    {
        var iso = Path.Combine(_root, "source.iso");
        File.WriteAllBytes(iso, new byte[4096]);
        return new(new MediaSource(null, iso, null), edition, index, [], Plain, null, null, OutputIso);
    }

    private Task<string> Run(MediaRequest r, CancellationToken ct = default) =>
        new MediaBuild(_steps).RunAsync(r, new Collect(_steps.Progress), ct);

    private async Task<MediaBuildException> Fails(MediaRequest r) => await Assert.ThrowsAsync<MediaBuildException>(() => Run(r));

    private void AssertCleanedUp()
    {
        Assert.Contains("DeleteWorkFolder", _steps.Calls);
        Assert.False(Directory.Exists(_steps.WorkFolder), "work folder left behind");
        Assert.False(File.Exists(OutputIso), "ISO left behind");
        Assert.False(File.Exists(OutputIso + ".part"), "partial ISO left behind");
        Assert.Empty(_steps.Dism.Mounted);
    }

    [Fact]
    public async Task DownloadSourceRunsTheStagesInOrder()
    {
        Assert.Equal(OutputIso, await Run(Download()));
        Assert.Equal(
        [
            "NewWorkFolder", "Download",
            "WimInfo image.esd",
            "Apply image.esd 1", "Export image.esd 2 boot.wim", "Export image.esd 3 boot.wim bootable",
            "WimInfo image.esd 4", "WimInfo image.esd 5", "WimInfo image.esd 6", "WimInfo image.esd 7",
            "Export image.esd 6 install.wim",
            "WimInfo install.wim", "WimInfo install.wim 1", "WimInfo boot.wim", "WimInfo boot.wim 2",
            "WriteIso", "VerifyIso", "DeleteWorkFolder",
        ], _steps.Calls);
        Assert.True(File.Exists(OutputIso));
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
        var ex = await Fails(Download("Education"));
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_EditionMissing", ex.Key);
        Assert.DoesNotContain(_steps.Calls, c => c.EndsWith("install.wim", StringComparison.Ordinal) && c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task TwoImagesWithTheEditionFailAtUnpack()
    {
        _steps.EsdImages.Add(new Img("Windows 11 Pro copy", "Professional"));
        var ex = await Fails(Download());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_ImageCheckFailed", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task AppsAreRemovedOnlyWhenProvisioned()
    {
        _steps.Dism.Provisioned =
        [
            "Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe",
            "Microsoft.WindowsStore_22510.1401.5.0_neutral_~_8wekyb3d8bbwe",
        ];
        await Run(Download(apps: ["Microsoft.BingNews", "Microsoft.ZuneMusic"]));
        var apps = _steps.Calls.SkipWhile(c => !c.StartsWith("Mount", StringComparison.Ordinal)).TakeWhile(c => c != "WriteIso").ToList();
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
        // Nothing removed: the image is left as it was.
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
    }

    [Fact]
    public async Task AppOutsideTheAllowlistIsRefused()
    {
        var ex = await Fails(Download(apps: ["Microsoft.WindowsStore"]));
        Assert.Equal(MediaStage.Apps, ex.Stage);
        AssertCleanedUp();
    }

    [Fact]
    public async Task NoAppsMeansNoMount()
    {
        await Run(Download());
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Mount", StringComparison.Ordinal) || c.StartsWith("Unmount", StringComparison.Ordinal));
        Assert.DoesNotContain(_steps.Progress, p => p.Stage == MediaStage.Apps);
    }

    [Fact]
    public async Task FilesStageWritesAnswerFileAndTunero()
    {
        var exe = Path.Combine(_root, "Tunero-running.exe");
        File.WriteAllBytes(exe, Encoding.ASCII.GetBytes("MZ fake Tunero"));
        var answers = Plain with { SkipSetup = true, AccountName = "Anna", FirstSignInTunero = true };
        var first = new FirstSignInFile(CatalogData.Current.Profiles.Default.Id, "de", "Dark");
        await Run(Download(answers: answers) with { TuneroExe = exe, FirstSignIn = first });

        Assert.Equal(AnswerFile.Build(answers), Encoding.UTF8.GetString(_steps.IsoContents["autounattend.xml"]));
        Assert.False(_steps.IsoContents["autounattend.xml"].AsSpan().StartsWith(Encoding.UTF8.Preamble), "no byte order mark");
        Assert.Equal("MZ fake Tunero", Encoding.ASCII.GetString(_steps.IsoContents[@"sources\$OEM$\$$\Setup\Tunero\Tunero.exe"]));
        Assert.Equal(FirstSignIn.Serialize(first), Encoding.UTF8.GetString(_steps.IsoContents[@"sources\$OEM$\$$\Setup\Tunero\first-sign-in.json"]));
    }

    [Fact]
    public async Task NoChoicesMeansNoAnswerFile()
    {
        await Run(Download());
        Assert.DoesNotContain("autounattend.xml", _steps.IsoContents.Keys);
        Assert.DoesNotContain(_steps.IsoContents.Keys, k => k.Contains("$OEM$", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownProfileForTheFirstSignInIsRefusedBeforeAnything()
    {
        var exe = Path.Combine(_root, "Tunero.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var request = Download(answers: Plain with { FirstSignInTunero = true }) with { TuneroExe = exe, FirstSignIn = new FirstSignInFile("turbo", "en", "System") };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(request));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task TuneroWithoutItsAnswerFileEntryIsRefused()
    {
        var exe = Path.Combine(_root, "Tunero.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var request = Download() with { TuneroExe = exe, FirstSignIn = new FirstSignInFile(CatalogData.Current.Profiles.Default.Id, "en", "System") };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(request));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task OnlyX64IsBuilt()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download(answers: Plain with { Architecture = "arm64" })));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Download() with { Source = new MediaSource(Esd with { Architecture = "ARM64" }, null, null) }));
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task FailureWhileMountedDiscardsAndCleansUp()
    {
        _steps.Dism.ProvisionedThrows = true;
        var ex = await Fails(Download(apps: ["Microsoft.BingNews"]));
        Assert.Equal(MediaStage.Apps, ex.Stage);
        Assert.Equal(87, ex.Code);
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        Assert.True(_steps.Calls.IndexOf("Unmount discard") < _steps.Calls.IndexOf("DeleteWorkFolder"));
        AssertCleanedUp();
    }

    [Fact]
    public async Task FolderWithAnImageThatStaysMountedIsKept()
    {
        _steps.Dism.ProvisionedThrows = true;
        _steps.Dism.UnmountThrows = true;
        var ex = await Fails(Download(apps: ["Microsoft.BingNews"]));
        Assert.Equal(MediaStage.Apps, ex.Stage);
        Assert.Contains("CleanupMountpoints", _steps.Calls);
        // Deleting the folder would delete files inside the mounted image; the next start cleans it up.
        Assert.DoesNotContain("DeleteWorkFolder", _steps.Calls);
        Assert.Contains(_steps.LogLines, l => l.Contains("still mounted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopDuringIsoDeletesPartialIso()
    {
        using var stop = new CancellationTokenSource();
        _steps.IsoWriter.StopWith = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Download(), stop.Token));
        Assert.Contains("WriteIso", _steps.Calls);
        Assert.DoesNotContain("VerifyIso", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task StopKeepsAnOlderIsoThatWasNotReplaced()
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
    public async Task NotEnoughSpaceFailsBeforeAnything()
    {
        _steps.Free = MediaBuild.NeededBytes(new MediaSource(Esd, null, null), Esd.Size) - 1;
        var ex = await Fails(Download());
        Assert.Equal(MediaStage.Download, ex.Stage);
        Assert.Equal("Media_NotEnoughSpace", ex.Key);
        Assert.Empty(_steps.Calls);
    }

    [Fact]
    public async Task OutputOverTheSourceIsoIsRefused()
    {
        var request = OwnIso();
        var ex = await Fails(request with { OutputIso = request.Source.IsoPath! });
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_OutputInvalid", ex.Key);
        Assert.Empty(_steps.Calls);
    }

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
            "WriteIso", "VerifyIso", "DeleteWorkFolder",
        ], _steps.Calls);
        Assert.Contains(@"sources\install.wim", _steps.IsoContents.Keys);
        Assert.DoesNotContain(@"sources\install.esd", _steps.IsoContents.Keys);
        Assert.Equal("Professional", _steps.ImagesOf(_steps.IsoContents[@"sources\install.wim"]).Single().Edition);
    }

    [Fact]
    public async Task OwnIsoInstallWimIsReplacedByTheExport()
    {
        _steps.OwnImageFile = "install.wim";
        await Run(OwnIso(index: 1, edition: "Core"));
        var images = _steps.ImagesOf(_steps.IsoContents[@"sources\install.wim"]);
        Assert.Equal("Core", Assert.Single(images).Edition);
    }

    [Fact]
    public async Task OwnIsoEditionIsFoundWithoutAnIndex()
    {
        _steps.OwnImageFile = "install.wim";
        await Run(OwnIso(index: null, edition: "Windows 11 Pro"));
        Assert.Contains("Export install.wim 2 install.wim", _steps.Calls);
    }

    [Fact]
    public async Task OwnIsoIndexWithAnotherEditionIsRefused()
    {
        _steps.OwnImageFile = "install.wim";
        var ex = await Fails(OwnIso(index: 1, edition: "Professional"));
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_EditionMissing", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoWithoutInstallImageIsRefused()
    {
        _steps.OwnImageFile = null;
        var ex = await Fails(OwnIso());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_NotWindows11", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoBelowBuild22000IsRefused()
    {
        _steps.OwnImageFile = "install.wim";
        _steps.OwnVersion = "10.0.19045";
        var ex = await Fails(OwnIso());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_NotWindows11", ex.Key);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoForArmIsRefused()
    {
        _steps.OwnImageFile = "install.wim";
        _steps.OwnArchitecture = "ARM64";
        var ex = await Fails(OwnIso());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_NotX64", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task MediaWithoutUefiBootIsRefused()
    {
        _steps.OwnImageFile = "install.wim";
        _steps.WithoutUefi = true;
        var ex = await Fails(OwnIso());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_NoUefiBoot", ex.Key);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Export", StringComparison.Ordinal));
        AssertCleanedUp();
    }

    [Fact]
    public async Task OwnIsoInUseKeepsTheReason()
    {
        _steps.IsoReader.AttachError = 32;
        var ex = await Fails(OwnIso());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal(32, ex.Code);
        Assert.True(ex.InUse);
        Assert.Equal("Media_IsoInUse", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task WrongEditionAfterExportFails()
    {
        _steps.Dism.ExportTamper = (target, img) => target.EndsWith("install.wim", StringComparison.OrdinalIgnoreCase) ? img with { Edition = "Core" } : img;
        var ex = await Fails(Download("Professional"));
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_ImageCheckFailed", ex.Key);
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task BootImageThatIsNotBootableFails()
    {
        _steps.Dism.ExportTamper = (target, img) => target.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase) ? img with { Bootable = false } : img;
        var ex = await Fails(Download());
        Assert.Equal(MediaStage.Unpack, ex.Stage);
        Assert.Equal("Media_ImageCheckFailed", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task UnhealthyImageIsNotCommitted()
    {
        _steps.Dism.Provisioned = ["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"];
        _steps.Dism.Healthy = false;
        var ex = await Fails(Download(apps: ["Microsoft.BingNews"]));
        Assert.Equal(MediaStage.Apps, ex.Stage);
        Assert.Equal("Media_Unhealthy", ex.Key);
        Assert.Contains("Unmount discard", _steps.Calls);
        Assert.DoesNotContain("Unmount commit", _steps.Calls);
        Assert.DoesNotContain("WriteIso", _steps.Calls);
        AssertCleanedUp();
    }

    [Fact]
    public async Task VerifyMismatchDeletesTheIso()
    {
        _steps.IsoReader.Mismatch = [@"sources\install.wim"];
        var ex = await Fails(Download());
        Assert.Equal(MediaStage.Verify, ex.Stage);
        Assert.Equal("Media_VerifyFailed", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task BadBootCatalogDeletesTheIso()
    {
        _steps.IsoReader.BootCatalogOk = false;
        var ex = await Fails(Download());
        Assert.Equal(MediaStage.Verify, ex.Stage);
        Assert.Equal("Media_VerifyFailed", ex.Key);
        AssertCleanedUp();
    }

    [Fact]
    public async Task VerifyRunsAfterIso()
    {
        var exe = Path.Combine(_root, "Tunero.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var answers = Plain with { FirstSignInTunero = true };
        await Run(Download(answers: answers) with { TuneroExe = exe, FirstSignIn = new FirstSignInFile(CatalogData.Current.Profiles.Default.Id, "en", "System") });
        var write = _steps.Calls.IndexOf("WriteIso");
        var verify = _steps.Calls.IndexOf("VerifyIso");
        Assert.True(write >= 0 && verify == write + 1 && _steps.Calls.IndexOf("DeleteWorkFolder") == verify + 1);
        Assert.Equal(
        [
            @"sources\install.wim", @"sources\boot.wim", "autounattend.xml",
            @"boot\etfsboot.com", @"efi\microsoft\boot\efisys.bin", @"efi\boot\bootx64.efi",
            @"sources\$OEM$\$$\Setup\Tunero\Tunero.exe", @"sources\$OEM$\$$\Setup\Tunero\first-sign-in.json",
        ], _steps.IsoReader.Verified);
    }

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

    [Fact]
    public async Task LeftoversAreDiscardedThenDeleted()
    {
        _steps.Dism.Mounted.AddRange([@"X:\TuneroMedia.ab12cd34\mount", @"C:\Other"]);
        await new MediaBuild(_steps).CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedDirs", "Unmount discard", "CleanupMountpoints", "MountedDirs", "DeleteWorkFolder"], _steps.Calls);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34\mount"], _steps.Dism.Unmounted);
        Assert.Equal([@"X:\TuneroMedia.ab12cd34"], _steps.DeletedFolders);
        Assert.Equal([@"C:\Other"], _steps.Dism.Mounted);
    }

    [Fact]
    public async Task LeftoverFoldersWithoutMountsAreDeleted()
    {
        _steps.Drives.Add(@"D:\");
        _steps.Leftovers[@"D:\"] = [@"D:\TuneroMedia.0000ffff", @"D:\TuneroMediaOther"];
        await new MediaBuild(_steps).CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal([@"D:\TuneroMedia.0000ffff"], _steps.DeletedFolders);
        Assert.DoesNotContain(_steps.Calls, c => c.StartsWith("Unmount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeftoverStillMountedIsKept()
    {
        _steps.Dism.Mounted.Add(@"X:\TuneroMedia.ab12cd34\mount");
        _steps.Dism.UnmountThrows = true;
        await new MediaBuild(_steps).CleanLeftoversAsync(CancellationToken.None);
        Assert.Empty(_steps.DeletedFolders);
    }

    [Fact]
    public async Task NothingLeftOverMeansNoDismChange()
    {
        _steps.Dism.Mounted.Add(@"C:\Other");
        await new MediaBuild(_steps).CleanLeftoversAsync(CancellationToken.None);
        Assert.Equal(["MountedDirs"], _steps.Calls);
        Assert.Empty(_steps.LogLines);
    }

    [Fact]
    public void NeededBytesForAFiveGigabyteEsd()
    {
        const long gb = 1L << 30;
        Assert.Equal((5 + 11 + 1) * gb, MediaBuild.NeededBytes(new MediaSource(Esd, null, null), 5 * gb));
        Assert.Equal((10 + 1) * gb, MediaBuild.NeededBytes(new MediaSource(null, @"C:\x.iso", null), 4 * gb));
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

    [Fact]
    public void RealStepsRefuseAShare()
    {
        var steps = new MediaSteps();
        var ex = Assert.Throws<MediaBuildException>(() => steps.NewWorkFolder(@"\\server\share\Win11.iso"));
        Assert.Equal("Media_OutputNotLocal", ex.Key);
    }

    [Fact]
    public void RealStepsListAndDeleteOnlyWorkFolders()
    {
        var drive = Path.Combine(_root, "drive");
        var work = Path.Combine(drive, "TuneroMedia.0a1b2c3d");
        var other = Path.Combine(drive, "TuneroMedia.notours");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(work, "media", "sources"));
        File.WriteAllText(Path.Combine(work, "media", "sources", "install.wim"), "x");
        File.SetAttributes(Path.Combine(work, "media", "sources", "install.wim"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        TestFolders.Junction(Path.Combine(work, "link"), outside);

        var steps = new MediaSteps();
        Assert.Equal([work], steps.LeftoverWorkFolders(drive));
        steps.DeleteWorkFolder(work);
        Assert.False(Directory.Exists(work));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")), "the link target was touched");
        Assert.Throws<ArgumentException>(() => steps.DeleteWorkFolder(other));
        Assert.True(Directory.Exists(other));
    }

    private sealed class Collect(List<MediaProgress> list) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => list.Add(value);
    }
}

/// <summary>An image inside a fake WIM or ESD.</summary>
internal sealed record Img(string Name, string? Edition, string Architecture = "x64", string Version = "10.0.26300", bool Bootable = false);

/// <summary>
/// Records every step. Fake image files hold a token that names their images, so exports, moves and deletes of the
/// files behave as on disk; GetWimInfo answers in DISM's text form.
/// </summary>
internal sealed class FakeMediaSteps : IMediaSteps
{
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

    /// <summary>The image file on the own ISO: "install.wim", "install.esd" or null for none.</summary>
    public string? OwnImageFile { get; set; } = "install.wim";
    public string OwnVersion { get; set; } = "10.0.26300";
    public string OwnArchitecture { get; set; } = "x64";
    public bool WithoutUefi { get; set; }

    public string LogPath => Path.Combine(_root, "media-test.log");

    public void Reset()
    {
        Calls.Clear();
        LogLines.Clear();
        Progress.Clear();
        DeletedFolders.Clear();
        IsoContents = [];
        Dism = new FakeDism(this);
        IsoWriter = new FakeIsoWriter(this);
        IsoReader = new FakeIsoReader(this);
        Free = long.MaxValue;
        EsdImages =
        [
            new("Windows Setup Media", null),
            new("Microsoft Windows PE (amd64)", "WindowsPE"),
            new("Microsoft Windows Setup (amd64)", "WindowsPE"),
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
        File.WriteAllText(path, token);
    }

    public List<Img> ImagesAt(string path) =>
        File.Exists(path) && _images.TryGetValue(File.ReadAllText(path), out var list) ? list : throw new DismException(2, "Error: 2\n\nThe system cannot find the file specified.");

    public List<Img> ImagesOf(byte[] content) => _images[Encoding.UTF8.GetString(content)];

    public static void Put(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
    }

    // ---- IMediaSteps ----

    public Task DownloadAsync(EsdFile file, string target, IProgress<(long Done, long Total)>? p, CancellationToken ct)
    {
        Calls.Add("Download");
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

    public string NewWorkFolder(string outputIso)
    {
        Calls.Add("NewWorkFolder");
        WorkFolder = Path.Combine(_root, "TuneroMedia.0123abcd");
        Directory.CreateDirectory(WorkFolder);
        return WorkFolder;
    }

    public void DeleteWorkFolder(string folder)
    {
        Calls.Add("DeleteWorkFolder");
        DeletedFolders.Add(folder);
        if (folder.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) TestFolders.Delete(folder);
    }

    public long FreeBytes(string path) => Free;

    public IEnumerable<string> LocalDrives() => Drives;

    public IEnumerable<string> LeftoverWorkFolders(string drive) => Leftovers.TryGetValue(drive, out var list) ? list : [];

    public void Log(string line) => LogLines.Add(line);
}

internal sealed class FakeDism(FakeMediaSteps steps) : IDism
{
    public List<string> Provisioned { get; set; } = [];
    public bool ProvisionedThrows { get; set; }
    public bool Healthy { get; set; } = true;
    public bool UnmountThrows { get; set; }
    public List<string> Mounted { get; } = [];
    public List<string> Unmounted { get; } = [];
    public Func<string, Img, Img>? ExportTamper { get; set; }

    private static string Name(string path) => Path.GetFileName(path);

    private static void Absolute(string path) => Assert.True(Path.IsPathFullyQualified(path), $"{path} is not absolute");

    public Task<string> GetWimInfoAsync(string image, int? index, CancellationToken ct)
    {
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
            text.Append($"Index : {index}\nName : {img.Name}\nDescription : {img.Name}\nSize : 1.000 bytes\nWIM Bootable : {(img.Bootable ? "Yes" : "No")}\n");
            text.Append($"Architecture : {img.Architecture}\nHal : <undefined>\nVersion : {string.Join('.', version.Take(3))}\n");
            text.Append($"ServicePack Build : {(version.Length > 3 ? version[3] : "1")}\nServicePack Level : 0\n");
            if (img.Edition is not null) text.Append($"Edition : {img.Edition}\n");
            text.Append("Installation : Client\nLanguages :\n\ten-US (Default)\n");
        }
        text.Append("\nThe operation completed successfully.\n");
        return Task.FromResult(text.ToString());
    }

    public Task ApplyAsync(string image, int index, string target, IProgress<double>? p, CancellationToken ct)
    {
        Absolute(image);
        Absolute(target);
        steps.Calls.Add($"Apply {Name(image)} {index}");
        steps.WriteSetupFiles(target);
        p?.Report(50);
        return Task.CompletedTask;
    }

    public Task ExportAsync(string source, int index, string target, bool bootable, IProgress<double>? p, CancellationToken ct)
    {
        Absolute(source);
        Absolute(target);
        steps.Calls.Add($"Export {Name(source)} {index} {Name(target)}" + (bootable ? " bootable" : ""));
        var img = steps.ImagesAt(source)[index - 1] with { Bootable = bootable };
        if (ExportTamper is not null) img = ExportTamper(target, img);
        // DISM appends to an existing destination file.
        if (File.Exists(target)) steps.ImagesAt(target).Add(img);
        else steps.PutImages(target, [img]);
        p?.Report(50);
        return Task.CompletedTask;
    }

    public Task MountAsync(string image, int index, string mountDir, IProgress<double>? p, CancellationToken ct)
    {
        Absolute(image);
        Assert.True(Directory.Exists(mountDir), "the mount folder must exist");
        steps.Calls.Add($"Mount {Name(image)} {index}");
        Mounted.Add(mountDir);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ProvisionedAsync(string mountDir, CancellationToken ct)
    {
        steps.Calls.Add("Provisioned");
        if (ProvisionedThrows) throw new DismException(87, "Error: 87\n\nThe parameter is incorrect.");
        return Task.FromResult<IReadOnlyList<string>>(Provisioned);
    }

    public Task RemoveProvisionedAsync(string mountDir, string packageName, CancellationToken ct)
    {
        steps.Calls.Add($"Remove {packageName}");
        return Task.CompletedTask;
    }

    public Task<string> CheckHealthAsync(string mountDir, CancellationToken ct)
    {
        steps.Calls.Add("CheckHealth");
        return Task.FromResult(Healthy
            ? "Image Version: 10.0.26300.9711\n\nNo component store corruption detected.\nThe operation completed successfully.\n"
            : "Image Version: 10.0.26300.9711\n\nThe component store is repairable.\nThe operation completed successfully.\n");
    }

    public Task UnmountAsync(string mountDir, bool commit, IProgress<double>? p, CancellationToken ct)
    {
        steps.Calls.Add($"Unmount {(commit ? "commit" : "discard")}");
        if (UnmountThrows) throw new DismException(5, "Error: 5\n\nAccess is denied.");
        Unmounted.Add(mountDir);
        Mounted.RemoveAll(m => string.Equals(m, mountDir, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> MountedDirsAsync(CancellationToken ct)
    {
        steps.Calls.Add("MountedDirs");
        return Task.FromResult<IReadOnlyList<string>>(Mounted.ToList());
    }

    public Task CleanupMountpointsAsync(CancellationToken ct)
    {
        steps.Calls.Add("CleanupMountpoints");
        return Task.CompletedTask;
    }
}

internal sealed class FakeIsoWriter(FakeMediaSteps steps) : IIsoWriter
{
    public CancellationTokenSource? StopWith { get; set; }

    public Task WriteAsync(string mediaFolder, string isoPath, string volumeName, IProgress<double>? p, CancellationToken ct)
    {
        steps.Calls.Add("WriteIso");
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
    public List<string> Mismatch { get; set; } = [];
    public List<string> Verified { get; } = [];

    public Task<string> CopyMediaAsync(string isoPath, string targetFolder, IProgress<double>? p, CancellationToken ct)
    {
        steps.Calls.Add("CopyMedia");
        if (AttachError is { } error) throw new IsoAttachException(error, $"The ISO could not be opened: error {error}.");
        steps.WriteSetupFiles(targetFolder);
        steps.PutImages(Path.Combine(targetFolder, "sources", "boot.wim"),
        [
            new("Microsoft Windows PE (amd64)", "WindowsPE"),
            new("Microsoft Windows Setup (amd64)", "WindowsPE", Bootable: true),
        ]);
        if (steps.OwnImageFile is { } name)
        {
            steps.PutImages(Path.Combine(targetFolder, "sources", name),
            [
                new("Windows 11 Home", "Core", steps.OwnArchitecture, steps.OwnVersion),
                new("Windows 11 Pro", "Professional", steps.OwnArchitecture, steps.OwnVersion),
            ]);
        }
        p?.Report(1.0);
        return Task.FromResult(targetFolder);
    }

    public Task<IsoCheck> VerifyAsync(string isoPath, string mediaFolder, IReadOnlyList<string> relativeFiles, IProgress<double>? p, CancellationToken ct)
    {
        steps.Calls.Add("VerifyIso");
        Assert.True(File.Exists(isoPath));
        Verified.AddRange(relativeFiles);
        var mismatched = relativeFiles.Where(f => !File.Exists(Path.Combine(mediaFolder, f))).Concat(Mismatch).ToList();
        return Task.FromResult(new IsoCheck(BootCatalogOk, mismatched));
    }
}
