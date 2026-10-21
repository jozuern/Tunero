using System.IO;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
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

    private readonly FakeToolRunner _tools = new();
    private List<FakeToolRunner.ToolRun> _runs => _tools.Runs;
    private readonly List<(string File, string? Arguments)> _opened = [];

    /// <summary>Exit codes per program file name; lines each run prints.</summary>
    private readonly Dictionary<string, int> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _printed = new(StringComparer.OrdinalIgnoreCase);
    private bool _openFails;

    public PackageUpdatesTests()
    {
        // What a user could have set: the elevated Chocolatey must not take its root or tools from there.
        _tools.Environment = () => new(StringComparer.OrdinalIgnoreCase) { ["ChocolateyInstall"] = @"C:\Users\a\evil", ["ChocolateyToolsLocation"] = @"C:\Users\a\tools" };
        _tools.Respond = (file, _) => (_codes.GetValueOrDefault(Path.GetFileName(file)), _printed.GetValueOrDefault(Path.GetFileName(file)) ?? []);
    }

    private PackageUpdates Updates(TimeSpan? limit = null) => new(new Launch
    {
        Start = _ => throw new InvalidOperationException("nothing starts in this test"),
        OpenAsUser = (file, arguments, _) =>
        {
            _opened.Add((file, arguments));
            return !_openFails;
        },
        Processes = _tools,
    }, null) { OutdatedTimeout = limit ?? TimeSpan.FromMinutes(2) };

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
        Assert.EndsWith("--ignore-http-cache", _runs[1].Arguments);
        Assert.Equal(ChocoRoot, _runs[1].Environment["ChocolateyInstall"]);
        Assert.False(_runs[1].Environment.ContainsKey("ChocolateyToolsLocation"));
        Assert.Same(ToolEncoding.Oem, _runs[1].Options?.Encoding);
        // Updates have no time limit: an installer ended halfway can leave a program half updated.
        Assert.All(_runs, r => Assert.Null(r.Timeout));

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

        // winget's sentence says what runs: the whole PC and the user's window, the whole PC alone, or the user's window alone.
        Assert.DoesNotContain(Loc.Instance["Apps_UpdateAllWingetMachine"], all);
        var machineOnly = PackageUpdates.ConfirmText([Winget(user: null)]);
        Assert.Contains(Loc.Instance["Apps_UpdateAllWingetMachine"], machineOnly);
        Assert.DoesNotContain(Loc.Instance["Apps_UpdateAllWinget"], machineOnly);
        var userOnly = PackageUpdates.ConfirmText([new PackageManagerInfo(ManagerKind.Winget, "winget", WingetUser, ManagerRun.AsUser)]);
        Assert.Contains(Loc.Instance["Apps_UpdateAllWingetUser"], userOnly);
        Assert.DoesNotContain(Loc.Instance["Apps_UpdateAllWinget"], userOnly);

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
        Assert.Equal(ChocoRoot, _runs[0].Environment["ChocolateyInstall"]);

        _codes["choco.exe"] = 1;
        Assert.Null(await Updates().ChocolateyOutdatedAsync(Choco()));

        // Without a connection to its source choco exits with 0 and lists the installed versions as the newest: could not check.
        _printed["choco.exe"] = ["Unable to load the service index for source https://community.chocolatey.org/api/v2/ ... Unable to connect to source",
            "chocolatey|2.7.4|2.7.4|false", "jq|1.8.2|1.8.2|false"];
        _codes["choco.exe"] = 0;
        Assert.Null(await Updates().ChocolateyOutdatedAsync(Choco()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updates().ChocolateyOutdatedAsync(Choco(ManagerRun.NotRun)));
    }

    /// <summary>A Chocolatey that does not answer (no connection to a source) is ended after the time limit.</summary>
    [Fact]
    public async Task TheOutdatedCheckHasATimeLimit()
    {
        _tools.Hangs = true;
        await Assert.ThrowsAsync<TimeoutException>(() => Updates(TimeSpan.FromMilliseconds(50)).ChocolateyOutdatedAsync(Choco()));
        Assert.Equal(TimeSpan.FromMilliseconds(50), Assert.Single(_runs).Timeout);
        Assert.Equal(1, _tools.Ended);
        Assert.Equal(TimeSpan.FromMinutes(2), new PackageUpdates(new Launch(), null).OutdatedTimeout);
    }

    // ---------------- the page ----------------

    private AppsViewModel Page(params PackageManagerInfo[] managers)
    {
        var page = new AppsViewModel(new FakePageHost(), null!, new FakeDialogs(), Updates());
        page.SetManagers(managers);
        return page;
    }

    private static PackageManagerInfo Pip(bool commandPrompt = false) =>
        new(ManagerKind.Pip, "pip", @"C:\Program Files\Python313\python.exe", ManagerRun.Copy) { Version = "3.13.1", Commands = ["py -m pip list --outdated"], CommandPrompt = commandPrompt };

    /// <summary>The check never clears the list while one of its packages updates.</summary>
    [Fact]
    public async Task NoCheckWhileAPackageUpdates()
    {
        var page = Page(Choco());
        page.ChocolateyUpdates.Add(new ChocolateyUpdateRow(new ChocolateyOutdated("git", "1", "2")) { IsUpdating = true });
        await page.CheckChocolateyCommand.ExecuteAsync(null);
        Assert.Empty(_runs);
        Assert.Single(page.ChocolateyUpdates);
    }

    /// <summary>Another change, scan or update runs: the check waits for it like every other page operation.</summary>
    [Fact]
    public async Task NoCheckWhileOtherWorkRuns()
    {
        var page = Page(Choco());
        ChangeGate.Instance.Busy = true;
        try
        {
            await page.CheckChocolateyCommand.ExecuteAsync(null);
        }
        finally
        {
            ChangeGate.Instance.Busy = false;
        }
        Assert.Empty(_runs);
        Assert.Equal(Loc.Instance["Change_Busy"], page.ChocolateyNote);
    }

    /// <summary>A language switch keeps a running update's row (its Update button stays off) and relabels it.</summary>
    [Fact]
    public void ALanguageSwitchKeepsTheUpdatingRow()
    {
        var language = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage("en");
            var page = Page(Choco());
            var row = new ChocolateyUpdateRow(new ChocolateyOutdated("git", "1", "2")) { IsUpdating = true };
            page.ChocolateyUpdates.Add(row);
            Loc.Instance.SetLanguage("de");
            page.SetManagers([Choco()]);
            Assert.Same(row, Assert.Single(page.ChocolateyUpdates));
            Assert.True(row.IsUpdating);
            Assert.Equal(Loc.Instance.Format("Apps_ChocoMeta", "1", "2"), row.Meta);
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
        }
    }

    [Fact]
    public void AnOldChocolateySaysWhy()
    {
        var page = Page(Choco(ManagerRun.NotRun) with { TooOld = true }, Choco(ManagerRun.NotRun) with { Name = "Chocolatey2" });
        Assert.Equal(Loc.Instance["Apps_HowChocoTooOld"], page.Managers[0].Meta);
        Assert.Equal(Loc.Instance["Apps_HowChocoUntrusted"], page.Managers[1].Meta);
        Assert.False(page.CanCheckChocolatey);
    }

    [Fact]
    public void RowsSayWhatEachOneDoes()
    {
        var page = Page(Winget(), Winget(user: null) with { Name = "winget2" }, Store, Pip(), Pip(commandPrompt: true) with { Name = "pip2" });
        Assert.Equal(Loc.Instance["Apps_HowWinget"], page.Managers[0].Meta);
        Assert.Equal(Loc.Instance["Apps_HowWingetMachine"], page.Managers[1].Meta);
        // The Store only opens its page: no check mark, its own status.
        var store = page.Managers[2];
        Assert.False(store.Included);
        Assert.Equal(Loc.Instance["Apps_ManagerOpensPage"], store.StatusText);
        // pip shows the Python version it belongs to; a quoted python.exe path is for Command Prompt.
        Assert.Equal(Loc.Instance.Format("Apps_PythonVersion", "3.13.1"), page.DeveloperTools[0].VersionText);
        Assert.DoesNotContain(Loc.Instance["Apps_DevInCommandPrompt"], page.DeveloperTools[0].Meta);
        Assert.Contains(Loc.Instance["Apps_DevInCommandPrompt"], page.DeveloperTools[1].Meta);
    }

    /// <summary>Update all writes its output and each manager's result to the card: it unfolds, so a failure is seen.</summary>
    [Fact]
    public async Task UpdateAllUnfoldsTheCardWithItsOutput()
    {
        _printed["winget.exe"] = ["(1/1) Found Git"];
        _codes["winget.exe"] = 1;
        var page = Page(Winget());
        Assert.False(page.ManagersExpanded);
        await page.UpdateAllCommand.ExecuteAsync(null);
        Assert.Single(_runs);
        Assert.True(page.ManagersExpanded);
        Assert.Contains("Found Git", page.ManagerOutput);
    }

    /// <summary>The card is folded by default with a one-line summary, and shows for developer tools alone too.</summary>
    [Fact]
    public void TheCardIsFoldedWithASummary()
    {
        var page = Page(Winget(), Choco(), Store, Pip(), Npm);
        Assert.False(page.ManagersExpanded);
        Assert.True(page.HasManagerCard);
        Assert.Equal(Loc.Instance.Format("Apps_ManagersSummary", "winget, Chocolatey, Microsoft Store", 2), page.ManagersSummary);

        // Screen readers hear the summary with the card's name while it is folded.
        Assert.Equal(Loc.Instance["Apps_ManagersTitle"] + ", " + page.ManagersSummary, page.ManagersAutomationName);
        // The hint names what Update all does with the Store only when the Store is there.
        Assert.Equal(Loc.Instance["Apps_ManagersHint"], page.ManagersHint);
        Assert.Equal(Loc.Instance["Apps_ManagersHintNoStore"], Page(Winget()).ManagersHint);

        var toolsOnly = Page(Npm);
        Assert.True(toolsOnly.HasManagerCard);
        Assert.Equal(Loc.Instance.Format("Apps_DevToolsSummary", 1), toolsOnly.ManagersSummary);
        Assert.False(Page().HasManagerCard);
    }

    [Fact]
    public async Task OnePackageUpdatesFromTheCheckedFolder()
    {
        var code = await Updates().ChocolateyUpgradeAsync(Choco(), "git", new Lines());
        Assert.Equal(0, code);
        var run = Assert.Single(_runs);
        Assert.Equal(Path.Combine(ChocoRoot, "choco.exe"), run.File);
        Assert.Equal("upgrade git --yes --no-progress --ignore-http-cache", run.Arguments);
        Assert.Equal(ChocoRoot, run.Environment["ChocolateyInstall"]);
        await Assert.ThrowsAsync<ArgumentException>(() => Updates().ChocolateyUpgradeAsync(Choco(), "git & calc", new Lines()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updates().ChocolateyUpgradeAsync(Choco(ManagerRun.NotRun), "git", new Lines()));
    }
}
