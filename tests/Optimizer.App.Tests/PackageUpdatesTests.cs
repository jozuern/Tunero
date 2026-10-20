using System.IO;
using Optimizer.App.Services;
using Optimizer.Core.Apps;
using Optimizer.Core.Platform;

namespace Optimizer.App.Tests;

/// <summary>
/// "Update all apps" with every package manager it runs, and Chocolatey's update list, without starting anything: the
/// runs (elevated, output read) and the windows opened as the signed-in user are recorded, with the environment the
/// elevated Chocolatey would get.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class PackageUpdatesTests
{
    private const string WingetExe = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";
    private const string WingetUser = @"C:\Users\a\AppData\Local\Microsoft\WindowsApps\winget.exe";
    private const string ChocoRoot = @"C:\ProgramData\chocolatey";
    private const string Scoop = @"C:\Users\a\scoop\shims\scoop.ps1";

    private readonly List<(string File, string Arguments, IDictionary<string, string?> Env, System.Text.Encoding? Encoding)> _runs = [];
    private readonly List<(string File, string? Arguments)> _opened = [];

    /// <summary>Exit codes per program file name; lines each run prints.</summary>
    private readonly Dictionary<string, int> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _printed = new(StringComparer.OrdinalIgnoreCase);
    private bool _openFails;

    private PackageUpdates Updates() => new(new Launch
    {
        Start = _ => throw new InvalidOperationException("nothing starts in this test"),
        OpenAsUser = (file, arguments, _) =>
        {
            _opened.Add((file, arguments));
            return !_openFails;
        },
        RunStreaming = (file, arguments, lines, encoding, environment) =>
        {
            var env = new Dictionary<string, string?> { ["ChocolateyInstall"] = @"C:\Users\a\evil", ["ChocolateyToolsLocation"] = @"C:\Users\a\tools" };
            environment?.Invoke(env);
            _runs.Add((file, arguments, env, encoding));
            foreach (var line in _printed.GetValueOrDefault(Path.GetFileName(file)) ?? []) lines?.Report(line);
            return Task.FromResult(_codes.GetValueOrDefault(Path.GetFileName(file)));
        },
    }, null);

    private static PackageManagerInfo Winget(string? user = WingetUser) => new(ManagerKind.Winget, "winget", WingetExe, ManagerRun.Elevated) { UserPath = user };
    private static PackageManagerInfo Choco(ManagerRun run = ManagerRun.Elevated) => new(ManagerKind.Chocolatey, "Chocolatey", Path.Combine(ChocoRoot, "choco.exe"), run) { Root = ChocoRoot };
    private static PackageManagerInfo ScoopManager => new(ManagerKind.Scoop, "Scoop", Scoop, ManagerRun.AsUser);
    private static PackageManagerInfo Store => new(ManagerKind.Store, "Microsoft Store", PackageManagers.StoreUpdatesLink, ManagerRun.StorePage);
    private static PackageManagerInfo Npm => new(ManagerKind.Npm, "npm", @"C:\Program Files\nodejs\npm.cmd", ManagerRun.Copy) { Commands = ["npm update -g"] };

    private sealed class Lines : IProgress<string>
    {
        public List<string> All { get; } = [];
        public void Report(string value) => All.Add(value);
    }

    [Fact]
    public async Task UpdateAllRunsEachManagerItsOwnWay()
    {
        var results = await Updates().RunAllAsync([Winget(), Choco(), ScoopManager, Store], new Lines());

        // Elevated with the output read: winget for the whole PC, then Chocolatey from its checked folder.
        Assert.Equal([WingetExe, Path.Combine(ChocoRoot, "choco.exe")], _runs.Select(r => r.File));
        Assert.Equal(Optimizer.Core.Apps.Winget.UpgradeAllArguments("machine"), _runs[0].Arguments);
        Assert.Equal(Chocolatey.UpgradeAllArguments, _runs[1].Arguments);
        Assert.Equal(ChocoRoot, _runs[1].Env["ChocolateyInstall"]);
        Assert.False(_runs[1].Env.ContainsKey("ChocolateyToolsLocation"));
        Assert.Same(StreamingProcess.Oem, _runs[1].Encoding);

        // As the signed-in user: winget's per-user packages, Scoop in its window, the Store's updates page.
        Assert.Equal(3, _opened.Count);
        Assert.Equal((WingetUser, Optimizer.Core.Apps.Winget.UpgradeAllArguments("user")), _opened[0]);
        Assert.Equal(WingetPackageExe, _opened[1].File);
        Assert.Contains($"& '{Scoop}' update *", _opened[1].Arguments);
        Assert.Equal((PackageManagers.StoreUpdatesLink, null), _opened[2]);

        Assert.Equal([Loc.Instance["Apps_UpdateAllDone"], Loc.Instance["Apps_UpdateAllUser"], Loc.Instance["Apps_ChocoDone"],
            Loc.Instance["Apps_ScoopStarted"], Loc.Instance["Apps_StoreOpened"]], results);
    }

    private static string WingetPackageExe => Optimizer.Core.Updates.WingetPackage.PowerShell;

    [Fact]
    public async Task FailuresAreReportedPerManager()
    {
        _codes["winget.exe"] = 1;
        _codes["choco.exe"] = 1;
        _openFails = true;
        var results = await Updates().RunAllAsync([Winget(user: null), Choco(), ScoopManager, Store], new Lines());
        Assert.Equal([Loc.Instance.Format("Apps_UpdateFailed", "0x00000001"), Loc.Instance.Format("Apps_ChocoUpdateFailed", 1),
            Loc.Instance["Apps_ScoopNotStarted"], Loc.Instance["Apps_StoreNotOpened"]], results);
    }

    /// <summary>A reboot request (3010) and "nothing to do" (2, enhanced exit codes) are no failures.</summary>
    [Theory]
    [InlineData(3010)]
    [InlineData(2)]
    public async Task ChocolateysOtherSuccessCodes(int code)
    {
        _codes["choco.exe"] = code;
        Assert.Equal([Loc.Instance["Apps_ChocoDone"]], await Updates().RunAllAsync([Choco()], new Lines()));
    }

    /// <summary>Only what the plan may run: an untrusted Chocolatey and the developer tools never start.</summary>
    [Fact]
    public async Task NothingOutsideThePlanRuns()
    {
        var results = await Updates().RunAllAsync([Choco(ManagerRun.NotRun), Npm], new Lines());
        Assert.Empty(_runs);
        Assert.Empty(_opened);
        Assert.Empty(results);
    }

    /// <summary>winget found only as the user's alias: its window for the user's packages, nothing elevated.</summary>
    [Fact]
    public async Task WingetForTheUserOnly()
    {
        var results = await Updates().RunAllAsync([new PackageManagerInfo(ManagerKind.Winget, "winget", WingetUser, ManagerRun.AsUser)], new Lines());
        Assert.Empty(_runs);
        Assert.Equal([(WingetUser, Optimizer.Core.Apps.Winget.UpgradeAllArguments("user"))], _opened);
        Assert.Equal([Loc.Instance["Apps_UpdateAllUser"]], results);
    }

    [Fact]
    public void TheQuestionNamesWhatRuns()
    {
        var all = PackageUpdates.ConfirmText([Winget(), Choco(), ScoopManager, Store]);
        Assert.StartsWith(Loc.Instance.Format("Apps_UpdateAllRuns", "winget, Chocolatey, Scoop, Microsoft Store"), all);
        foreach (var key in new[] { "Apps_UpdateAllWinget", "Apps_UpdateAllChoco", "Apps_UpdateAllScoop", "Apps_UpdateAllStore", "Apps_UpdateAllRestart" })
            Assert.Contains(Loc.Instance[key], all);

        var wingetOnly = PackageUpdates.ConfirmText([Winget()]);
        Assert.StartsWith(Loc.Instance.Format("Apps_UpdateAllRuns", "winget"), wingetOnly);
        Assert.DoesNotContain(Loc.Instance["Apps_UpdateAllChoco"], wingetOnly);
        Assert.DoesNotContain(Loc.Instance["Apps_UpdateAllScoop"], wingetOnly);
        Assert.DoesNotContain(Loc.Instance["Apps_UpdateAllStore"], wingetOnly);
    }

    [Fact]
    public async Task ChocolateysUpdateListComesFromItsOutdatedCommand()
    {
        _printed["choco.exe"] = ["Chocolatey v2.4.1", "git|2.47.0|2.47.1|false", "jq|1.7|1.7.1|false"];
        _codes["choco.exe"] = 2; // enhanced exit code: outdated packages found
        var list = await Updates().ChocolateyOutdatedAsync(Choco());
        Assert.Equal(["git", "jq"], list!.Select(p => p.Id));
        Assert.Equal(Chocolatey.OutdatedArguments, Assert.Single(_runs).Arguments);
        Assert.Equal(ChocoRoot, _runs[0].Env["ChocolateyInstall"]);

        _codes["choco.exe"] = 1;
        Assert.Null(await Updates().ChocolateyOutdatedAsync(Choco()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updates().ChocolateyOutdatedAsync(Choco(ManagerRun.NotRun)));
    }

    [Fact]
    public async Task OnePackageUpdatesFromTheCheckedFolder()
    {
        var code = await Updates().ChocolateyUpgradeAsync(Choco(), "git", new Lines());
        Assert.Equal(0, code);
        var run = Assert.Single(_runs);
        Assert.Equal(Path.Combine(ChocoRoot, "choco.exe"), run.File);
        Assert.Equal("upgrade git --yes --no-progress", run.Arguments);
        Assert.Equal(ChocoRoot, run.Env["ChocolateyInstall"]);
        await Assert.ThrowsAsync<ArgumentException>(() => Updates().ChocolateyUpgradeAsync(Choco(), "git & calc", new Lines()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updates().ChocolateyUpgradeAsync(Choco(ManagerRun.NotRun), "git", new Lines()));
    }
}
