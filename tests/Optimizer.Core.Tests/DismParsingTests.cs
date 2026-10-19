using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>DISM argument strings and output parsing. The fixtures are real dism.exe output (26H2 ESD, German machine, /English).</summary>
public class DismParsingTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", name));

    // StreamingProcess hands over trimmed non-empty lines joined by newlines; the parsers must cope with that form too.
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

    [Fact]
    public void MountedDirsFromMountedImageInfo()
    {
        const string text = """
            Deployment Image Servicing and Management tool
            Version: 10.0.26100.9549

            Mounted images:
            Mount Dir : C:\w\mount
            Image File : C:\w\sources\install.wim
            Image Index : 1
            Mounted Read/Write : Yes
            Status : Ok

            Mount Dir : D:\other dir\m
            Image File : D:\x.wim
            Image Index : 2
            Mounted Read/Write : No
            Status : Remount

            The operation completed successfully.
            """;
        Assert.Equal(new[] { @"C:\w\mount", @"D:\other dir\m" }, DismOutput.ParseMountedDirs(text));
        Assert.Empty(DismOutput.ParseMountedDirs("No mounted images found.\nThe operation completed successfully."));
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
