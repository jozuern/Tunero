using Optimizer.Core.Apps;

namespace Optimizer.Core.Tests;

/// <summary>
/// The package managers on Install and update: detection from files in a temp folder (standing in for Program Files,
/// ProgramData, Windows and the user's profile) with the registry, the folder trust check and the file versions faked;
/// Chocolatey's outdated list and the command lines. Nothing runs.
/// </summary>
public sealed class PackageManagersTests : IDisposable
{
    private readonly string _root = TestFolders.Create("pm");

    public void Dispose() => TestFolders.Delete(_root);

    private string ProgramFiles => Path.Combine(_root, "Program Files");
    private string ProgramData => Path.Combine(_root, "ProgramData");
    private string Windows => Path.Combine(_root, "Windows");
    private string Profile => Path.Combine(_root, @"Users\a");

    private string File(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private ManagerSystem Pc(Func<string, bool>? adminOnly = null, Dictionary<string, string>? machine = null, Dictionary<string, string>? user = null,
        string? profile = "", string? wingetTrusted = null, string? wingetUser = null, string? store = "22608.1401.7.0",
        Func<string, bool>? adminOnlyFolder = null) => new()
    {
        ProgramFiles = ProgramFiles,
        ProgramData = ProgramData,
        Windows = Windows,
        ProfilePath = profile == "" ? Profile : profile,
        WingetTrusted = wingetTrusted,
        WingetForUser = wingetUser,
        StoreVersion = () => store,
        MachineVariable = name => machine?.GetValueOrDefault(name),
        UserVariable = name => user?.GetValueOrDefault(name),
        IsAdminOnly = adminOnly ?? (_ => true),
        IsAdminOnlyFolder = adminOnlyFolder ?? (_ => true),
        FileVersion = path => Path.GetFileName(path) switch
        {
            "choco.exe" => "2.4.1",
            "dotnet.exe" => "10.0.0",
            "python.exe" => "3.13.1",
            _ => null,
        },
    };

    private static PackageManagerInfo Get(IReadOnlyList<PackageManagerInfo> found, ManagerKind kind) => Assert.Single(found, m => m.Kind == kind);

    // ---------------- detection ----------------

    [Fact]
    public void NothingInstalledFindsOnlyTheStore()
    {
        var found = PackageManagers.Detect(Pc());
        var store = Assert.Single(found);
        Assert.Equal(ManagerKind.Store, store.Kind);
        Assert.Equal("22608.1401.7.0", store.Version);
        Assert.Equal(ManagerRun.StorePage, store.Run);
        Assert.Empty(PackageManagers.Detect(Pc(store: null)));
    }

    [Fact]
    public void WingetComesFromTheFinders()
    {
        var trusted = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";
        var user = @"C:\Users\a\AppData\Local\Microsoft\WindowsApps\winget.exe";
        var winget = Get(PackageManagers.Detect(Pc(wingetTrusted: trusted, wingetUser: user)), ManagerKind.Winget);
        Assert.Equal(ManagerRun.Elevated, winget.Run);
        Assert.Equal(trusted, winget.Path);
        Assert.Equal(user, winget.UserPath);
        Assert.Equal("1.29.380.0", winget.Version);
        Assert.True(winget.InUpdateAll);

        // Only the user's alias: winget runs in the user's window alone.
        var userOnly = Get(PackageManagers.Detect(Pc(wingetUser: user)), ManagerKind.Winget);
        Assert.Equal(ManagerRun.AsUser, userOnly.Run);
        Assert.Equal(user, userOnly.Path);
        Assert.Null(userOnly.Version);
    }

    [Fact]
    public void ChocolateyInTheDefaultFolderRunsElevatedOnlyWhenAdminOnly()
    {
        var choco = File(@"ProgramData\chocolatey\choco.exe");
        var found = Get(PackageManagers.Detect(Pc()), ManagerKind.Chocolatey);
        Assert.Equal(ManagerRun.Elevated, found.Run);
        Assert.Equal(choco, found.Path);
        Assert.Equal(Path.GetDirectoryName(choco), found.Root);
        Assert.Equal("2.4.1", found.Version);

        var checkedPaths = new List<string>();
        var weak = Get(PackageManagers.Detect(Pc(adminOnly: p =>
        {
            checkedPaths.Add(p);
            return false;
        })), ManagerKind.Chocolatey);
        Assert.Equal(ManagerRun.NotRun, weak.Run);
        Assert.False(weak.InUpdateAll);
        Assert.Equal([choco], checkedPaths);
    }

    /// <summary>
    /// An elevated choco loads helpers, extensions, hooks, its configuration and the packages' scripts from its root: the
    /// root and each of those folders must be admin-only too, not only choco.exe.
    /// </summary>
    [Fact]
    public void ChocolateyRunsElevatedOnlyWhenTheFoldersItLoadsFromAreAdminOnly()
    {
        var choco = File(@"ProgramData\chocolatey\choco.exe");
        var root = Path.GetDirectoryName(choco)!;
        foreach (var folder in new[] { "helpers", "extensions", "hooks", "config", "lib", "lib-bkp" }) Directory.CreateDirectory(Path.Combine(root, folder));
        var checkedFolders = new List<string>();
        var all = Get(PackageManagers.Detect(Pc(adminOnlyFolder: f =>
        {
            checkedFolders.Add(f);
            return true;
        })), ManagerKind.Chocolatey);
        Assert.Equal(ManagerRun.Elevated, all.Run);
        Assert.Equal(new[] { root }.Concat(new[] { "helpers", "extensions", "hooks", "config", "lib" }.Select(f => Path.Combine(root, f))), checkedFolders);

        foreach (var weak in new[] { "helpers", "extensions", "hooks", "config", "lib" })
        {
            var found = Get(PackageManagers.Detect(Pc(adminOnlyFolder: f => !f.EndsWith(weak, StringComparison.Ordinal))), ManagerKind.Chocolatey);
            Assert.Equal(ManagerRun.NotRun, found.Run);
        }
        Assert.Equal(ManagerRun.NotRun, Get(PackageManagers.Detect(Pc(adminOnlyFolder: f => f != root)), ManagerKind.Chocolatey).Run);
    }

    [Fact]
    public void ChocolateyInstallFromTheMachineThenTheUserVariable()
    {
        var machineChoco = File(@"Tools\choco\choco.exe");
        var userChoco = File(@"Users\a\AppData\Local\UniGetUI\Chocolatey\choco.exe");
        var machine = new Dictionary<string, string> { ["ChocolateyInstall"] = Path.GetDirectoryName(machineChoco)! };
        var user = new Dictionary<string, string> { ["ChocolateyInstall"] = Path.GetDirectoryName(userChoco)! };
        Assert.Equal(machineChoco, Get(PackageManagers.Detect(Pc(machine: machine, user: user)), ManagerKind.Chocolatey).Path);
        // A Chocolatey in the user's folder is found, and the trust check decides (here: the user can change it).
        var fromUser = Get(PackageManagers.Detect(Pc(user: user, adminOnly: p => !p.Contains("UniGetUI", StringComparison.Ordinal))), ManagerKind.Chocolatey);
        Assert.Equal(userChoco, fromUser.Path);
        Assert.Equal(ManagerRun.NotRun, fromUser.Run);
        // Without the user's profile, the user's variable is not read (it would be expanded for the elevated account).
        Assert.DoesNotContain(PackageManagers.Detect(Pc(user: user, profile: null)), m => m.Kind == ManagerKind.Chocolatey);
    }

    /// <summary>A user's ChocolateyInstall cannot hide a trusted install in ProgramData: the first trusted root wins.</summary>
    [Fact]
    public void AUserChocolateyDoesNotHideATrustedOne()
    {
        var userChoco = File(@"Users\a\AppData\Local\UniGetUI\Chocolatey\choco.exe");
        var defaultChoco = File(@"ProgramData\chocolatey\choco.exe");
        var user = new Dictionary<string, string> { ["ChocolateyInstall"] = Path.GetDirectoryName(userChoco)! };
        static bool NotUniGetUi(string p) => !p.Contains("UniGetUI", StringComparison.Ordinal);
        var found = Get(PackageManagers.Detect(Pc(user: user, adminOnly: NotUniGetUi, adminOnlyFolder: NotUniGetUi)), ManagerKind.Chocolatey);
        Assert.Equal(defaultChoco, found.Path);
        Assert.Equal(ManagerRun.Elevated, found.Run);
        Assert.Equal(Path.GetDirectoryName(defaultChoco), found.Root);
    }

    [Fact]
    public void ScoopInTheProfileOrTheUsersScoopFolderRunsAsTheUser()
    {
        var shim = File(@"Users\a\scoop\shims\scoop.ps1");
        File(@"Users\a\scoop\apps\scoop\current\CHANGELOG.md", "# Changelog\n\n## [Unreleased]\n\n## [v0.5.3](https://github.com/ScoopInstaller/Scoop/compare/v0.5.2...v0.5.3) - 2025-08-11\n");
        var scoop = Get(PackageManagers.Detect(Pc()), ManagerKind.Scoop);
        Assert.Equal(ManagerRun.AsUser, scoop.Run);
        Assert.Equal(shim, scoop.Path);
        Assert.Equal("0.5.3", scoop.Version);

        var moved = File(@"D\Scoop\shims\scoop.ps1");
        var user = new Dictionary<string, string> { ["SCOOP"] = Path.Combine(_root, @"D\Scoop") };
        Assert.Equal(moved, Get(PackageManagers.Detect(Pc(user: user)), ManagerKind.Scoop).Path);
        Assert.Null(Get(PackageManagers.Detect(Pc(user: user)), ManagerKind.Scoop).Version);
        // Without the user's profile, a SCOOP value is not used either.
        Assert.DoesNotContain(PackageManagers.Detect(Pc(user: user, profile: null)), m => m.Kind == ManagerKind.Scoop);
    }

    [Fact]
    public void WithoutAProfileNoScoopOrCargo()
    {
        File(@"Users\a\scoop\shims\scoop.ps1");
        File(@"Users\a\.cargo\bin\cargo.exe");
        File(@"Users\a\.cargo\bin\cargo-install-update.exe");
        var found = PackageManagers.Detect(Pc(profile: null));
        Assert.DoesNotContain(found, m => m.Kind is ManagerKind.Scoop or ManagerKind.Cargo);
    }

    [Fact]
    public void DeveloperToolsAreListedWithCommandsAndNeverRun()
    {
        File(@"Program Files\nodejs\npm.cmd");
        File(@"Program Files\nodejs\node_modules\npm\package.json", "{\"name\":\"npm\",\"version\":\"10.9.2\"}");
        File(@"Windows\py.exe");
        File(@"Program Files\Python39\python.exe");
        File(@"Program Files\Python313\python.exe");
        File(@"Program Files\dotnet\dotnet.exe");
        Directory.CreateDirectory(Path.Combine(Profile, @".dotnet\tools\.store\dotnet-ef"));
        Directory.CreateDirectory(Path.Combine(Profile, @".dotnet\tools\.store\csharpier"));
        Directory.CreateDirectory(Path.Combine(Profile, @".dotnet\tools\.store\bad name&calc"));
        File(@"Users\a\.cargo\bin\cargo.exe");
        File(@"Users\a\.cargo\.crates2.json", """
            {"installs":{
              "ripgrep 14.1.1 (registry+https://github.com/rust-lang/crates.io-index)":{},
              "bat 0.25.0 (sparse+https://index.crates.io/)":{},
              "mytool 0.1.0 (git+https://example.com/mytool#abc)":{},
              "inhouse 1.0.0 (registry+https://example.com/index)":{},
              "inhouse2 1.0.0 (sparse+https://cargo.example.com/)":{},
              "local 0.1.0 (path+file:///C:/src/local)":{}
            }}
            """);

        var found = PackageManagers.Detect(Pc());
        Assert.All(found.Where(m => m.IsDeveloperTool), m =>
        {
            Assert.Equal(ManagerRun.Copy, m.Run);
            Assert.False(m.InUpdateAll);
        });

        var npm = Get(found, ManagerKind.Npm);
        Assert.Equal("10.9.2", npm.Version);
        Assert.Equal(["npm update -g"], npm.Commands);

        var pip = Get(found, ManagerKind.Pip);
        Assert.Equal("3.13.1", pip.Version);
        Assert.Equal(["py -m pip list --outdated"], pip.Commands);
        Assert.False(pip.CommandPrompt);

        var dotnet = Get(found, ManagerKind.DotnetTools);
        Assert.Equal("10.0.0", dotnet.Version);
        Assert.Equal(["dotnet tool update -g csharpier", "dotnet tool update -g dotnet-ef"], dotnet.Commands);

        var cargo = Get(found, ManagerKind.Cargo);
        Assert.Equal(["cargo install bat", "cargo install ripgrep"], cargo.Commands);
    }

    [Fact]
    public void CargoUpdateReplacesTheCommandPerCrate()
    {
        File(@"Users\a\.cargo\bin\cargo.exe");
        File(@"Users\a\.cargo\bin\cargo-install-update.exe");
        File(@"Users\a\.cargo\.crates2.json", "{\"installs\":{\"ripgrep 14.1.1 (registry+https://github.com/rust-lang/crates.io-index)\":{}}}");
        Assert.Equal(["cargo install-update -a"], Get(PackageManagers.Detect(Pc()), ManagerKind.Cargo).Commands);
    }

    /// <summary>Python without the py launcher: the command names the python.exe it found, in quotes.</summary>
    [Fact]
    public void PipWithoutTheLauncherNamesPython()
    {
        var python = File(@"Users\a\AppData\Local\Programs\Python\Python312\python.exe");
        var pip = Get(PackageManagers.Detect(Pc()), ManagerKind.Pip);
        Assert.Equal([$"\"{python}\" -m pip list --outdated"], pip.Commands);
        // A quoted path is text in PowerShell: the page says to run it in Command Prompt.
        Assert.True(pip.CommandPrompt);
    }

    [Fact]
    public void ToolsWithNothingToUpdateAreLeftOut()
    {
        File(@"Program Files\dotnet\dotnet.exe");
        File(@"Users\a\.cargo\bin\cargo.exe");
        var found = PackageManagers.Detect(Pc());
        Assert.DoesNotContain(found, m => m.Kind is ManagerKind.DotnetTools or ManagerKind.Cargo);
    }

    /// <summary>Detection only looks at files: the source starts no program (CLAUDE.md, the app runs elevated).</summary>
    [Fact]
    public void DetectionStartsNoProgram()
    {
        var source = System.IO.File.ReadAllText(Path.Combine(RepoPaths.Root, @"src\Optimizer.Core\Apps\PackageManagers.cs"));
        Assert.DoesNotContain("Process", source);
        Assert.DoesNotContain("DeElevatedLauncher", source);
    }

    [Fact]
    public void UpdateAllRunsTheManagersInOrder()
    {
        File(@"ProgramData\chocolatey\choco.exe");
        File(@"Users\a\scoop\shims\scoop.ps1");
        File(@"Program Files\nodejs\npm.cmd");
        var found = PackageManagers.Detect(Pc(wingetUser: @"C:\w\winget.exe"));
        Assert.Equal([ManagerKind.Winget, ManagerKind.Chocolatey, ManagerKind.Scoop, ManagerKind.Store],
            PackageManagers.UpdateAllPlan(found).Select(m => m.Kind));
        // An untrusted Chocolatey stays out.
        var weak = PackageManagers.Detect(Pc(adminOnly: _ => false));
        Assert.DoesNotContain(PackageManagers.UpdateAllPlan(weak), m => m.Kind == ManagerKind.Chocolatey);
    }

    [Fact]
    public void TheDemoReadsNothing()
    {
        var demo = PackageManagers.Demo();
        Assert.Contains(demo, m => m.Kind == ManagerKind.Chocolatey && m.InUpdateAll);
        Assert.Contains(demo, m => m.Kind == ManagerKind.Scoop && m.InUpdateAll);
        Assert.Contains(demo, m => m.IsDeveloperTool);
    }

    /// <summary>dotnet.exe's product version carries the commit ("10.0.13 @Commit: 4099f83..."): only the version is shown.</summary>
    [Theory]
    [InlineData("10.0.13 @Commit: 4099f83977f4d29604a9aeb971fb0a7bc1d5f699", "10.0.13")]
    [InlineData("6.0.0+abcdef", "6.0.0")]
    [InlineData(" 2.4.1 ", "2.4.1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VersionsLoseBuildMetadata(string? raw, string? shown) => Assert.Equal(shown, ManagerSystem.CleanVersion(raw));

    // ---------------- Chocolatey ----------------

    [Fact]
    public void ParsesChocolateysOutdatedList()
    {
        const string output = """
            Chocolatey v2.4.1
            git|2.47.0|2.47.1|false
            7zip.install|24.08.0|24.09.0|false
            tunero|1.3.0|1.4.0|false
            bad id|1|2|false
            pinned|1.0|2.0|true
            nonsense line
            jq|1.7|1.7.1|false
            """;
        var list = Chocolatey.ParseOutdated(output);
        Assert.Equal(["git", "7zip.install", "jq"], list.Select(p => p.Id));
        Assert.Equal(("2.47.0", "2.47.1"), (list[0].Current, list[0].Available));
    }

    [Theory]
    [InlineData("jq", true)]
    [InlineData("7zip.install", true)]
    [InlineData("dotnet-sdk", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("x&calc", false)]
    [InlineData("-force", false)]
    [InlineData("\"x\"", false)]
    public void ChocolateyIdsAreChecked(string id, bool safe) => Assert.Equal(safe, Chocolatey.IsSafeId(id));

    [Fact]
    public void ChocolateyCommandLines()
    {
        Assert.Equal("outdated --limit-output --ignore-pinned", Chocolatey.OutdatedArguments);
        // Tunero itself updates through Update now (a running exe cannot be replaced).
        Assert.Equal("upgrade all --yes --no-progress --except=tunero", Chocolatey.UpgradeAllArguments);
        Assert.Equal("upgrade git --yes --no-progress", Chocolatey.UpgradeArguments("git"));
        Assert.Throws<ArgumentException>(() => Chocolatey.UpgradeArguments("git & calc"));
        Assert.Throws<ArgumentException>(() => Chocolatey.UpgradeArguments("tunero"));
        Assert.True(Chocolatey.IsSuccess(0));
        Assert.True(Chocolatey.IsSuccess(3010));
        Assert.True(Chocolatey.IsSuccess(1641));
        Assert.True(Chocolatey.IsSuccess(2));
        Assert.False(Chocolatey.IsSuccess(1));
        Assert.False(Chocolatey.IsSuccess(-1));
    }

    [Fact]
    public void TheChocolateyEnvironmentPointsToTheCheckedFolder()
    {
        var env = new Dictionary<string, string?> { ["ChocolateyInstall"] = @"C:\Users\a\evil", ["ChocolateyToolsLocation"] = @"C:\x", ["PATH"] = "p" };
        Chocolatey.HardenEnvironment(env, @"C:\ProgramData\chocolatey");
        Assert.Equal(@"C:\ProgramData\chocolatey", env["ChocolateyInstall"]);
        Assert.False(env.ContainsKey("ChocolateyToolsLocation"));
        Assert.Equal("p", env["PATH"]);
    }

    // ---------------- Scoop and the Store ----------------

    [Fact]
    public void ScoopUpdatesItselfThenEveryAppAndKeepsTheWindowOpen()
    {
        var args = PackageManagers.ScoopUpdateArguments(@"C:\Users\a\scoop\shims\scoop.ps1", "Done. Press Enter.");
        Assert.StartsWith("-NoProfile -ExecutionPolicy Bypass -Command \"", args);
        Assert.Equal(2, args.Count(c => c == '"'));
        var self = args.IndexOf(@"& 'C:\Users\a\scoop\shims\scoop.ps1' update;", StringComparison.Ordinal);
        var all = args.IndexOf(@"& 'C:\Users\a\scoop\shims\scoop.ps1' update *;", StringComparison.Ordinal);
        var wait = args.IndexOf("Read-Host 'Done. Press Enter.'", StringComparison.Ordinal);
        Assert.True(self > 0 && all > self && wait > all, args);
    }

    [Fact]
    public void TheStoreOpensItsUpdatesPage()
    {
        Assert.Equal("ms-windows-store://downloadsandupdates", PackageManagers.StoreUpdatesLink);
        Assert.True(Platform.DeElevatedLauncher.IsLink(PackageManagers.StoreUpdatesLink));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_x64__8wekyb3d8bbwe", "22608.1401.7.0")]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_neutral_~_8wekyb3d8bbwe", null)]
    [InlineData("Microsoft.WindowsStore_22608.1401.7.0_arm64__8wekyb3d8bbwe", "22608.1401.7.0")]
    [InlineData("Microsoft.WindowsStoreX_1.0.0.0_x64__8wekyb3d8bbwe", null)]
    [InlineData("Microsoft.WindowsStore_1.0.0.0_x64__other", null)]
    public void StorePackageVersion(string package, string? version) => Assert.Equal(version, PackageManagers.StorePackageVersion(package)?.ToString());
}
