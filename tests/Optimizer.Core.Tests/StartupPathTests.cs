using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// How the Startup and Services pages read paths and names: a file path is not parsed as a command line, rundll32 entry
/// points are found in every form, driver package names are readable, and a service that a tweak changes is not also
/// changed on the Services page. Only temp folders and hand-made strings; nothing on this PC is changed.
/// </summary>
public sealed class StartupPathTests
{
    [Fact]
    public void AStartupFolderFileIsCheckedItselfNotAPlantedPrefix()
    {
        using var fx = new EngineFixture();
        var profile = TestFolders.Create("profile");
        try
        {
            // "Start Menu" has a space: read as an unquoted command line, ...\Windows\Start.exe would be the program.
            var windows = Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows");
            var folder = Path.Combine(windows, @"Start Menu\Programs\Startup");
            Directory.CreateDirectory(folder);
            var evil = Path.Combine(folder, "evil.exe");
            File.WriteAllBytes(evil, [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(windows, "Start.exe"), [0x4D, 0x5A]);

            var entry = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, profile) { CommonStartupFolder = Path.Combine(profile, "no-startup") }.StartupFolders());
            Assert.Equal(evil, entry.ImagePath, ignoreCase: true);
            Assert.Equal($"\"{evil}\"", entry.Command);
        }
        finally
        {
            TestFolders.Delete(profile);
        }
    }

    [Fact]
    public void AnOlderSnapshotWithTheUnquotedPathDoesNotCallTheFileChanged()
    {
        const string Tool = @"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\tool.exe";
        var before = StartupSnapshot.Take([Folder(Tool)], DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var diff = StartupSnapshot.Compare(before, [Folder($"\"{Tool}\"")]);
        Assert.Empty(diff.NewKeys);
        Assert.Empty(diff.Removed);
        // A changed command still counts.
        Assert.NotEmpty(StartupSnapshot.Compare(before, [Folder($"\"{Tool}\" -min")]).NewKeys);

        static StartupEntry Folder(string command) =>
            new(StartupKind.StartupFolder, "tool", command, null, @"C:\Startup", Hive.User, true, "folder:User:tool.exe");
    }

    [Theory]
    // An InprocServer32 value or a ServiceDll names one file: spaces are part of it, a planted Foo.exe is not checked.
    [InlineData(@"C:\Users\me\AppData\Local\Foo Bar\evil.dll", @"C:\Users\me\AppData\Local\Foo Bar\evil.dll")]
    [InlineData(@"""C:\Program Files\Vendor App\x.dll""", @"C:\Program Files\Vendor App\x.dll")]
    [InlineData(@"  C:\Program Files\Vendor App\x.dll ", @"C:\Program Files\Vendor App\x.dll")]
    [InlineData(@"\??\C:\Drivers\x.sys", @"C:\Drivers\x.sys")]
    public void AFileValueIsOneFile(string value, string expected)
    {
        // Every prefix exists, as a planted file would.
        Assert.Equal(expected, CommandLine.FilePath(value, s => s, _ => true));
        Assert.Null(CommandLine.FilePath("  ", s => s, _ => true));
    }

    [Fact]
    public void AFileValueExpandsVariablesAndFindsABareName()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, @"System32\x.dll"), CommandLine.FilePath(@"""%SystemRoot%\System32\x.dll""", s => s.Replace("%SystemRoot%", windows, StringComparison.OrdinalIgnoreCase)), ignoreCase: true);
        var system = Path.Combine(windows, "System32");
        Assert.Equal(Path.Combine(system, "x.dll"), CommandLine.FilePath("x.dll", s => s, f => f.Equals(Path.Combine(system, "x.dll"), StringComparison.OrdinalIgnoreCase)), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\shell32.dll,ShellExec_RunDLL x.exe", "ShellExec_RunDLL")]
    // Windows' own syntax: the DLL quoted, the entry point after the closing quote, also after a space.
    [InlineData(@"""C:\Windows\System32\shell32.dll"",ShellExec_RunDLL x.exe", "ShellExec_RunDLL")]
    [InlineData(@"""C:\Program Files\Windows Photo Viewer\PhotoViewer.dll"", ImageView_Fullscreen %1", "ImageView_Fullscreen")]
    [InlineData(@"url.dll, FileProtocolHandler evil.exe", "FileProtocolHandler")]
    [InlineData(@"/d C:\Tools\helper.dll,Start", "Start")]
    [InlineData(@"C:\Tools\helper.dll", null)]
    public void TheRundllEntryPointIsReadInEveryForm(string args, string? expected) =>
        Assert.Equal(expected, CommandLine.RundllEntryPoint(args));

    [Theory]
    [InlineData(@"rundll32.exe ""C:\Windows\System32\shell32.dll"",ShellExec_RunDLL C:\Windows\System32\cmd.exe /c powershell -w hidden -enc SQBFAFgA", true)]
    [InlineData(@"rundll32.exe url.dll, FileProtocolHandler C:\Windows\System32\notepad.exe", true)]
    [InlineData(@"rundll32.exe C:\Windows\System32\x.dll,Entry C:\Windows\System32\cmd.exe", true)]
    [InlineData(@"""C:\Windows\system32\rundll32.exe"" ""C:\Windows\System32\x.dll"", Entry", false)]
    public void ARundllProxyIsFoundWhenTheDllIsQuotedOrASpaceFollowsTheComma(string command, bool flagged)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(flagged, CommandLine.RunsUnverifiedScript(command.Replace(@"C:\Windows", windows, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("@oem19.inf,%ServiceDisplayName%;Intel(R) Innovation Platform Framework Service", "Intel(R) Innovation Platform Framework Service")]
    [InlineData("@oem79.inf,%SvcDesc.IBMPMSVC%;Lenovo PM Service", "Lenovo PM Service")]
    // The text after the key may hold a ';' itself.
    [InlineData("@x.inf,%Name%;Vendor; Helper", "Vendor; Helper")]
    [InlineData("@oem1.inf,%Name%", null)]
    [InlineData(@"@%SystemRoot%\system32\x.dll,-101", null)]
    public void ADriverPackageNameGivesItsText(string value, string? expected) =>
        Assert.Equal(expected, StartupScanner.InfFallback(value));

    [Fact]
    public void ResolvingAnInfNameGivesTheReadableName()
    {
        Assert.Equal("Logitech LampArray Service", StartupScanner.ResolveIndirect("@oem27.inf,%logi_lamparray_service.SvcName%;Logitech LampArray Service"));
        Assert.Equal("Plain name", StartupScanner.ResolveIndirect("Plain name"));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe""", "--x", @"""C:\Program Files\App\app.exe"" --x")]
    [InlineData(@"C:\Program Files\App\app.exe", null, @"""C:\Program Files\App\app.exe""")]
    [InlineData(null, "--x", null)]
    public void ATaskCommandIsQuotedOnce(string? program, string? arguments, string? expected) =>
        Assert.Equal(expected, StartupScanner.TaskCommand(program, arguments));

    /// <summary>
    /// A service a tweak sets to another start type than Manual is not changed on the Services page: each would keep its
    /// own original, and undoing both could leave the service changed (DiagTrack, SysMain). A tweak that sets Manual
    /// can share a service the page only sets to Manual: both write the same value.
    /// </summary>
    [Fact]
    public void AServiceATweakChangesIsNotChangedOnTheServicesPageToo()
    {
        var catalog = CatalogData.Current.Services;
        foreach (var tweak in TweakCatalog.Current.Tweaks)
            foreach (var action in tweak.Actions.OfType<ServiceAction>())
            {
                var note = catalog.Find(action.Name) ?? throw new Xunit.Sdk.XunitException($"{tweak.Id} changes {action.Name}, which services.json does not explain");
                var shared = note.Mode is "manualOk" or "warn" && action.StartType == ServiceStart.Manual;
                Assert.True(note.Mode == "protected" || shared, $"{tweak.Id} sets {action.Name} to {action.StartType}; mark it protected in services.json");
            }
    }
}
