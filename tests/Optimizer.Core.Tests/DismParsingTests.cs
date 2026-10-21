using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>DISM argument strings and output parsing. The fixtures are real dism.exe output (26H2 ESD, German machine, /English).</summary>
public class DismParsingTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", name));

    // IProcessRunner.RunAsync hands over trimmed non-empty lines joined by newlines; the parsers must cope with that form too.
    private static string Streamed(string text) =>
        string.Join("\n", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WimInfoListGivesIndexAndName(bool streamed)
    {
        var text = Fixture("dism-list.txt");
        var images = DismOutput.ParseWimInfo(streamed ? Streamed(text) : text);
        Assert.Equal(10, images.Count);
        Assert.Equal(1, images[0].Index);
        Assert.Equal("Windows Setup Media", images[0].Name);
        Assert.Equal(6, images[5].Index);
        Assert.Equal("Windows 11 Home Single Language", images[5].Name);
        Assert.Equal("Windows 11 Pro", images[8].Name);
        Assert.All(images, i => Assert.Null(i.Edition));
        Assert.All(images, i => Assert.Empty(i.Languages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WimInfoDetailGivesEdition(bool streamed)
    {
        var text = Fixture("dism-detail.txt");
        var image = Assert.Single(DismOutput.ParseWimInfo(streamed ? Streamed(text) : text));
        Assert.Equal(6, image.Index);
        Assert.Equal("Windows 11 Home Single Language", image.Name);
        Assert.Equal("CoreSingleLanguage", image.Edition);
        Assert.Equal("x64", image.Architecture);
        Assert.Equal("10.0.26300.9711", image.Version);
        Assert.Equal(new[] { "en-US" }, image.Languages);
        Assert.Equal("en-US", image.DefaultLanguage);
    }

    /// <summary>The language DISM marks "(Default)" is kept, wherever it stands in the list.</summary>
    [Fact]
    public void WimInfoKeepsTheDefaultLanguage()
    {
        var image = Assert.Single(DismOutput.ParseWimInfo("Index : 1\nName : X\nLanguages :\n\ten-GB\n\tde-DE (Default)\n"));
        Assert.Equal(new[] { "en-GB", "de-DE" }, image.Languages);
        Assert.Equal("de-DE", image.DefaultLanguage);
    }

    [Fact]
    public void WimInfoDetailWithoutServicePackKeepsVersion()
    {
        var image = Assert.Single(DismOutput.ParseWimInfo("Index : 1\nName : X\nArchitecture : arm64\nVersion : 10.0.26100\nEdition : Professional\nLanguages :\n\tde-DE (Default)\n\ten-US\n"));
        Assert.Equal("10.0.26100", image.Version);
        Assert.Equal("arm64", image.Architecture);
        Assert.Equal(new[] { "de-DE", "en-US" }, image.Languages);
    }

    [Fact]
    public void WimInfoOfNothingIsEmpty()
    {
        Assert.Empty(DismOutput.ParseWimInfo(""));
        Assert.Empty(DismOutput.ParseWimInfo(Fixture("dism-err.txt")));
    }

    [Theory]
    [InlineData("[==========================42.0%                          ]", 42.0)]
    [InlineData("[===                       5.5%                           ]", 5.5)]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("Deployment Image Servicing", null)]
    [InlineData("Version: 10.0.26100.9549", null)]
    [InlineData("", null)]
    public void PercentFromProgressLine(string line, double? expected) => Assert.Equal(expected, DismOutput.Percent(line));

    [Fact]
    public void ProvisionedNames()
    {
        var names = DismOutput.ParseProvisioned(Fixture("dism-prov.txt"));
        Assert.Equal(new[]
        {
            "Clipchamp.Clipchamp_4.7.10020.0_neutral_~_yxz26nhyzhsrt",
            "Microsoft.ApplicationCompatibilityEnhancements_1.2511.9.0_neutral_~_8wekyb3d8bbwe",
            "Microsoft.AV1VideoExtension_2.0.35.0_neutral_~_8wekyb3d8bbwe",
            "Microsoft.AVCEncoderVideoExtension_1.1.47.0_neutral_~_8wekyb3d8bbwe",
        }, names);
    }

    /// <summary>
    /// The package folders directly under WindowsApps, as /List-Image prints them (lines from the 26300 Pro image): no
    /// files, subfolders, "Deleted" or "Merged", nothing outside WindowsApps.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageAppFoldersFromListImage(bool streamed)
    {
        var text = Fixture("dism-list-image.txt");
        var folders = (streamed ? Streamed(text) : text).Split('\n').Select(DismOutput.ImageAppFolder).OfType<string>().ToList();
        Assert.Equal(new[]
        {
            "Clipchamp.Clipchamp_4.5.10920.0_neutral_split.scale-100_yxz26nhyzhsrt",
            "Clipchamp.Clipchamp_4.5.10920.0_neutral_~_yxz26nhyzhsrt",
            "Clipchamp.Clipchamp_4.5.10920.0_x64__yxz26nhyzhsrt",
            "Microsoft.BingNews_4.11.6001.0_neutral_~_8wekyb3d8bbwe",
            "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe",
        }, folders);
        Assert.Equal("/English /List-Image /ImageFile:\"E:\\sources\\install.wim\" /Index:6", DismArgs.ListImage(@"E:\sources\install.wim", 6));
    }

    /// <summary>The page's list: Microsoft's groups for the download, exactly the offered apps an own ISO's image holds.</summary>
    [Fact]
    public void PageAppsFollowTheImage()
    {
        var appx = Catalog.CatalogData.Current.Appx;
        var download = MediaChoices.PageApps(appx, null).ToList();
        Assert.NotEmpty(download);
        Assert.All(download, a => Assert.Contains(a.Group, MediaChoices.MicrosoftImageGroups));
        Assert.DoesNotContain(download, a => appx.IsProtected(a.Name));

        var image = MediaChoices.PageApps(appx, ["Clipchamp.Clipchamp_4.5.10920.0_x64__yxz26nhyzhsrt", "Microsoft.WindowsStore_22510.1401.6.0_x64__8wekyb3d8bbwe",
            "king.com.CandyCrushSaga_1.0.0.0_x86__kgqvnymyfvs32"]).Select(a => a.Name).Order();
        Assert.Equal(["Clipchamp.Clipchamp", "king.com.CandyCrushSaga"], image);
    }

    [Fact]
    public void ErrorCodeHexAndDecimal()
    {
        Assert.Equal(unchecked((int)0x800f081f), DismOutput.ErrorCode("Error: 0x800f081f\n\nThe source files could not be found."));
        Assert.Equal(87, DismOutput.ErrorCode("Error: 87\nThe parameter is incorrect."));
        Assert.Equal(2, DismOutput.ErrorCode(Fixture("dism-err.txt")));
        Assert.Null(DismOutput.ErrorCode("The operation completed successfully."));
    }

    [Fact]
    public void HealthyOnlyWhenNoCorruptionIsReported()
    {
        Assert.True(DismOutput.Healthy(Fixture("dism-checkhealth.txt")));
        Assert.False(DismOutput.Healthy("Image Version: 10.0.26300.9711\n\nThe component store is repairable.\nThe operation completed successfully."));
        Assert.False(DismOutput.Healthy("The component store is corrupted."));
        Assert.False(DismOutput.Healthy(Fixture("dism-err.txt")));
        Assert.False(DismOutput.Healthy(""));
    }

    /// <summary>
    /// The 26H2 ESD's setup image (index 1) has no edition; the boot images (2 and 3) are WindowsPE for x64. Captured in
    /// PCO-Test 2026-10-20; the build's Check stage relies on exactly this, not on the localized image names.
    /// </summary>
    [Fact]
    public void EsdSetupAndBootImagesAreRecognizable()
    {
        var setup = Assert.Single(DismOutput.ParseWimInfo(Fixture("dism-esd-index1.txt")));
        Assert.Equal("<undefined>", setup.Edition);
        Assert.Equal("<undefined>", setup.Architecture);
        var boot = Assert.Single(DismOutput.ParseWimInfo(Fixture("dism-esd-index3.txt")));
        Assert.Equal("WindowsPE", boot.Edition);
        Assert.Equal("x64", boot.Architecture);
        Assert.Equal("10.0.26100.9711", boot.Version);
    }

    private static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", name));

    /// <summary>
    /// Real headers (first 208 bytes) of the 26H2 media's boot.wim and install.wim. DISM's "WIM Bootable" line says No
    /// for both boot images even after /Export-Image /Bootable (checked in PCO-Test 2026-10-20); the header's boot index
    /// is what marks Setup as the image that boots.
    /// </summary>
    [Fact]
    public void WimHeaderGivesImageCountAndBootIndex()
    {
        Assert.Equal(new WimHeader.Info(2, 2), WimHeader.Read(new MemoryStream(Bytes("wim-header-boot.bin"))));
        Assert.Equal(new WimHeader.Info(1, 0), WimHeader.Read(new MemoryStream(Bytes("wim-header-install.bin"))));
    }

    [Fact]
    public void WimHeaderRejectsOtherFiles()
    {
        Assert.Null(WimHeader.Read(new MemoryStream(Bytes("wim-header-boot.bin")[..100])));
        var wrongMagic = Bytes("wim-header-boot.bin");
        wrongMagic[0] = (byte)'X';
        Assert.Null(WimHeader.Read(new MemoryStream(wrongMagic)));
        var wrongSize = Bytes("wim-header-boot.bin");
        wrongSize[8] = 0;
        Assert.Null(WimHeader.Read(new MemoryStream(wrongSize)));
        Assert.Null(WimHeader.Read(new MemoryStream([])));
    }

    [Fact]
    public void MountedImagesFromMountedImageInfo()
    {
        // The first entry as DISM printed it in PCO-Test (2026-10-20); the next two have the other states Microsoft lists
        // (Take Inventory of an Image or Component Using DISM), the last one has no Status line at all.
        const string text = """
            Deployment Image Servicing and Management tool
            Version: 10.0.26100.9549

            Mounted images:

            Mount Dir : C:\w\mount
            Image File : C:\w\sources\install.wim
            Image Index : 1
            Mounted Read/Write : No
            Status : Ok

            Mount Dir : D:\other dir\m
            Image File : D:\x.wim
            Image Index : 2
            Mounted Read/Write : Yes
            Status : Needs Remount

            Mount Dir : E:\bad
            Image File : E:\y.wim
            Image Index : 1
            Mounted Read/Write : Yes
            Status : Invalid

            Mount Dir : F:\no status
            Image File : F:\z.wim

            The operation completed successfully.
            """;
        var images = DismOutput.ParseMountedImages(text);
        Assert.Equal(
        [
            new MountedImage(@"C:\w\mount", "Ok"), new MountedImage(@"D:\other dir\m", "Needs Remount"),
            new MountedImage(@"E:\bad", "Invalid"), new MountedImage(@"F:\no status", null),
        ], images);
        Assert.Equal([false, true, true, false], images.Select(i => i.NeedsRecovery));
        Assert.Empty(DismOutput.ParseMountedImages("No mounted images found.\nThe operation completed successfully."));
    }

    [Fact] public void ExportArgs() => Assert.Equal("/English /Export-Image /SourceImageFile:\"C:\\w\\a.esd\" /SourceIndex:6 /DestinationImageFile:\"C:\\w\\m\\sources\\install.wim\" /Compress:max", DismArgs.Export(@"C:\w\a.esd", 6, @"C:\w\m\sources\install.wim", bootable: false));
    [Fact] public void ExportBootableArgs() => Assert.Equal("/English /Export-Image /SourceImageFile:\"C:\\a.wim\" /SourceIndex:2 /DestinationImageFile:\"C:\\b.wim\" /Compress:max /Bootable", DismArgs.Export(@"C:\a.wim", 2, @"C:\b.wim", bootable: true));

    [Fact]
    public void WimInfoArgs()
    {
        Assert.Equal("/English /Get-WimInfo /WimFile:\"C:\\a.esd\"", DismArgs.WimInfo(@"C:\a.esd", null));
        Assert.Equal("/English /Get-WimInfo /WimFile:\"C:\\a.esd\" /Index:6", DismArgs.WimInfo(@"C:\a.esd", 6));
    }

    [Fact] public void ApplyArgs() => Assert.Equal("/English /Apply-Image /ImageFile:\"C:\\a.esd\" /Index:6 /ApplyDir:\"C:\\t\"", DismArgs.Apply(@"C:\a.esd", 6, @"C:\t"));
    [Fact] public void MountArgs() => Assert.Equal("/English /Mount-Image /ImageFile:\"C:\\a.wim\" /Index:1 /MountDir:\"C:\\m\"", DismArgs.Mount(@"C:\a.wim", 1, @"C:\m"));
    [Fact] public void ReadOnlyMountArgs() => Assert.Equal("/English /Mount-Image /ImageFile:\"C:\\a.wim\" /Index:2 /MountDir:\"C:\\m\" /ReadOnly", DismArgs.Mount(@"C:\a.wim", 2, @"C:\m", readOnly: true));
    [Fact] public void ProvisionedArgs() => Assert.Equal("/English /Image:\"C:\\m\" /Get-ProvisionedAppxPackages", DismArgs.Provisioned(@"C:\m"));
    [Fact] public void RemoveProvisionedArgs() => Assert.Equal("/English /Image:\"C:\\m\" /Remove-ProvisionedAppxPackage /PackageName:Microsoft.BingNews_1.0_neutral_~_8wekyb3d8bbwe", DismArgs.RemoveProvisioned(@"C:\m", "Microsoft.BingNews_1.0_neutral_~_8wekyb3d8bbwe"));
    [Fact] public void CheckHealthArgs() => Assert.Equal("/English /Image:\"C:\\m\" /Cleanup-Image /CheckHealth", DismArgs.CheckHealth(@"C:\m"));
    [Fact] public void UnmountDiscardArgs() => Assert.Equal("/English /Unmount-Image /MountDir:\"C:\\m\" /Discard", DismArgs.Unmount(@"C:\m", commit: false));
    [Fact] public void UnmountCommitArgs() => Assert.Equal("/English /Unmount-Image /MountDir:\"C:\\m\" /Commit", DismArgs.Unmount(@"C:\m", commit: true));

    [Fact]
    public void MountedAndCleanupArgs()
    {
        Assert.Equal("/English /Get-MountedImageInfo", DismArgs.MountedImageInfo());
        Assert.Equal("/English /Cleanup-Mountpoints", DismArgs.CleanupMountpoints());
        Assert.Equal("/English /Remount-Image /MountDir:\"C:\\m\"", DismArgs.Remount(@"C:\m"));
    }

    [Fact] public void TrailingBackslashDoesNotEscapeTheClosingQuote() => Assert.Equal("/English /Image:\"D:\\\\\" /Cleanup-Image /CheckHealth", DismArgs.CheckHealth(@"D:\"));

    [Fact]
    public void PathsWithQuotesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => DismArgs.Mount("C:\\a\"b.wim", 1, @"C:\m"));
        Assert.Throws<ArgumentException>(() => DismArgs.Unmount("C:\\m\" /Commit", commit: false));
        Assert.Throws<ArgumentException>(() => DismArgs.WimInfo("C:\\a\".esd", null));
        Assert.Throws<ArgumentException>(() => DismArgs.CheckHealth("C:\\m\""));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("x /Remove-Image")]
    [InlineData("")]
    public void PackageNamesWithSpacesAreRefused(string name) => Assert.Throws<ArgumentException>(() => DismArgs.RemoveProvisioned(@"C:\m", name));

    [Fact]
    public void ExceptionMessageHasTheErrorCode()
    {
        var ex = new DismException(2, Fixture("dism-err.txt"));
        Assert.Equal(2, ex.ExitCode);
        Assert.Contains("0x00000002", ex.Message);
        Assert.Contains("The system cannot find the file specified.", ex.Message);
    }
}
