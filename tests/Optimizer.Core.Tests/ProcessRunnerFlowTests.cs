using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Platform;
using Optimizer.Core.Tools;

namespace Optimizer.Core.Tests;

/// <summary>
/// The long tool runs (sfc and dism, winget, PresentMon) through the injected <see cref="IProcessRunner"/>, with
/// <see cref="FakeProcesses"/>: what each one starts, the lines it hands on, its time limit and a Stop. Nothing starts.
/// </summary>
public class ProcessRunnerFlowTests
{
    private sealed class Lines : IProgress<string>
    {
        public List<string> All { get; } = [];

        public void Report(string value)
        {
            lock (All) All.Add(value);
        }
    }

    // ---------------- sfc and dism ----------------

    public static TheoryData<string, string, string, int> Repairs => new()
    {
        { "sfc", "sfc.exe", "/scannow", ToolEncoding.Utf16.CodePage },
        { "scan", "dism.exe", "/Online /Cleanup-Image /ScanHealth", ToolEncoding.Oem.CodePage },
        { "restore", "dism.exe", "/Online /Cleanup-Image /RestoreHealth", ToolEncoding.Oem.CodePage },
    };

    private static Task<int> Repair(string which, IProcessRunner processes, IProgress<string>? lines, CancellationToken ct) => which switch
    {
        "sfc" => SystemRepair.ScanNowAsync(processes, lines, ct),
        "scan" => SystemRepair.ScanHealthAsync(processes, lines, ct),
        _ => SystemRepair.RestoreHealthAsync(processes, lines, ct),
    };

    /// <summary>The System32 tool in the encoding it writes, without a time limit, its lines passed on as they come.</summary>
    [Theory]
    [MemberData(nameof(Repairs))]
    public async Task RepairsStreamTheirOutputWithoutATimeLimit(string which, string exe, string arguments, int codePage)
    {
        var processes = new FakeProcesses { StreamHandler = (_, _) => (2, ["Beginning verification phase of system scan.", "Verification 100% complete."]) };
        var lines = new Lines();
        Assert.Equal(2, await Repair(which, processes, lines, CancellationToken.None));
        var run = Assert.Single(processes.Streams);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, exe), run.File);
        Assert.Equal(arguments, run.Arguments);
        Assert.Null(run.Timeout);
        Assert.Equal(codePage, run.Options?.Encoding?.CodePage);
        Assert.Equal(["Beginning verification phase of system scan.", "Verification 100% complete."], lines.All);
    }

    /// <summary>Stop on a long /RestoreHealth: the run ends with a cancellation, not as a result.</summary>
    [Fact]
    public async Task StopEndsALongRepair()
    {
        var processes = new FakeProcesses { Hangs = true, StreamHandler = (_, _) => (0, ["[====    20.0%      ]"]) };
        using var stop = new CancellationTokenSource();
        var lines = new Lines();
        var run = SystemRepair.RestoreHealthAsync(processes, lines, stop.Token);
        await processes.Hanging.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, processes.Ended);
        Assert.Equal(["[====    20.0%      ]"], lines.All);
    }

    // ---------------- winget ----------------

    private const string Winget = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";

    private static readonly string[] SearchTable =
    [
        "Name                 Id                             Version    Match",
        "---------------------------------------------------------------------------------------",
        "AkelPad              AkelPad.AkelPad                4.9.9      Tag: notepad",
        "Notepad++            Notepad++.Notepad++            8.9.8.1    Tag: notepad",
    ];

    /// <summary>The lines become the result; a search waits on the network, so it has the package list's time limit.</summary>
    [Fact]
    public async Task ASearchReadsTheStreamedTable()
    {
        var processes = new FakeProcesses { StreamHandler = (_, _) => (0, SearchTable) };
        var found = await Apps.Winget.SearchAsync(processes, Winget, "notepad", CancellationToken.None);
        Assert.Equal(["AkelPad.AkelPad", "Notepad++.Notepad++"], found.Select(f => f.Id));
        var run = Assert.Single(processes.Streams);
        Assert.Equal(Winget, run.File);
        Assert.Equal(Apps.Winget.SearchArguments("notepad"), run.Arguments);
        Assert.Equal(ToolTimeouts.PackageList, run.Timeout);
        Assert.Null(run.Options?.Encoding);
    }

    /// <summary>A failed search says winget's own last line; nothing found is no failure.</summary>
    [Fact]
    public async Task AFailedSearchSaysWhy()
    {
        var processes = new FakeProcesses { StreamHandler = (_, _) => (unchecked((int)0x8A15000F), ["Failed when searching source: winget", "Data required by the source is missing"]) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Apps.Winget.SearchAsync(processes, Winget, "notepad", CancellationToken.None));
        Assert.Equal("Data required by the source is missing", error.Message);

        processes.StreamHandler = (_, _) => (Apps.Winget.NoPackagesFound, ["No package found matching input criteria."]);
        Assert.Empty(await Apps.Winget.SearchAsync(processes, Winget, "nothing here", CancellationToken.None));
    }

    /// <summary>A winget that does not answer is ended after the time limit, for the search and the update list alike.</summary>
    [Fact]
    public async Task AHangingWingetTimesOut()
    {
        // The fake ends at the limit the flow passed (shortened here); the real runner kills the process tree then (BenchmarkHealthTests).
        var processes = new FakeProcesses { Hangs = true };
        var limited = new LimitedRunner(processes, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => Apps.Winget.ListUpgradesAsync(limited, Winget, CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() => Apps.Winget.SearchAsync(limited, Winget, "notepad", CancellationToken.None));
        Assert.Equal(2, processes.Ended);
        Assert.Equal([ToolTimeouts.PackageList, ToolTimeouts.PackageList], limited.Asked);
    }

    /// <summary>Shortens the time limit a flow passes, so a test does not wait five minutes.</summary>
    private sealed class LimitedRunner(IProcessRunner inner, TimeSpan limit) : IProcessRunner
    {
        /// <summary>The limits the flows asked for.</summary>
        public List<TimeSpan?> Asked { get; } = [];

        public (int ExitCode, string Output) Run(string file, string arguments, TimeSpan? timeout = null) => inner.Run(file, arguments, timeout);

        public Task<int> RunAsync(string file, string arguments, TimeSpan? timeout, CancellationToken ct, IProgress<string>? lines, RunOptions? options = null)
        {
            Asked.Add(timeout);
            return inner.RunAsync(file, arguments, timeout is null ? null : limit, ct, lines, options);
        }
    }

    [Fact]
    public async Task TheUpdateListComesFromTheStreamedLines()
    {
        var processes = new FakeProcesses
        {
            StreamHandler = (_, _) => (0, ["Name                      Id        Version    Available", "--------------------------------------------------------",
                "7-Zip 23.01 (x64 edition) 7zip.7zip 23.01.00.0 26.04", "1 upgrades available."]),
        };
        var upgrades = await Apps.Winget.ListUpgradesAsync(processes, Winget, CancellationToken.None);
        Assert.Equal(new WingetUpgrade("7-Zip 23.01 (x64 edition)", "7zip.7zip", "23.01.00.0", "26.04"), Assert.Single(upgrades));
        Assert.Equal(Apps.Winget.UpgradesArguments, processes.Streams[0].Arguments);
        Assert.Equal(ToolTimeouts.PackageList, processes.Streams[0].Timeout);
    }

    /// <summary>Installs and updates have no time limit (an installer ended halfway leaves a broken app); a bad id never runs.</summary>
    [Fact]
    public async Task InstallsAndUpdatesRunWithoutATimeLimit()
    {
        var processes = new FakeProcesses { StreamHandler = (_, _) => (0, ["Found 7-Zip [7zip.7zip]", "Successfully installed"]) };
        var app = new AppEntry { Id = "7zip.7zip", Name = "7-Zip", WingetScope = "machine" };
        var lines = new Lines();
        Assert.Equal(0, await Apps.Winget.InstallAsync(processes, Winget, app, lines, CancellationToken.None));
        Assert.Equal(0, await Apps.Winget.UpgradeAsync(processes, Winget, app, null, CancellationToken.None));
        Assert.Equal([Apps.Winget.InstallArguments("7zip.7zip", "machine"), Apps.Winget.UpgradeArguments("7zip.7zip")], processes.Streams.Select(s => s.Arguments));
        Assert.All(processes.Streams, s => Assert.Null(s.Timeout));
        Assert.Equal(["Found 7-Zip [7zip.7zip]", "Successfully installed"], lines.All);

        await Assert.ThrowsAsync<ArgumentException>(() => Apps.Winget.InstallAsync(processes, Winget, new AppEntry { Id = "x & calc", Name = "x" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apps.Winget.InstallAsync(processes, Winget, new AppEntry { Id = "7zip.7zip", Name = "7-Zip", Scope = "user" }, null, CancellationToken.None));
        Assert.Equal(2, processes.Streams.Count);
    }

    // ---------------- PresentMon ----------------

    /// <summary>
    /// The checked PresentMon (extracted to a temp folder, never started) runs with a limit of its capture time plus a
    /// grace; the file held open since the hash check is released once it started, and the CSV it wrote becomes the stats.
    /// </summary>
    [Fact]
    public async Task ACaptureRunsTheCheckedPresentMonAndReadsItsCsv()
    {
        var folder = TestFolders.Create("presentmon");
        try
        {
            var exe = PresentMon.Extract(Path.Combine(folder, "tools"));
            var captures = Path.Combine(folder, "captures");
            var processes = new FakeProcesses
            {
                StreamHandler = (_, arguments) =>
                {
                    // Released after the start: nothing holds the checked file any more.
                    using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                    }
                    var csv = arguments.Split("--output_file \"")[1].Split('"')[0];
                    File.WriteAllLines(csv, ["Application,ProcessID,PresentMode,MsBetweenPresents", .. Enumerable.Repeat("game.exe,1,Hardware: Independent Flip,10", 100)]);
                    return (0, ["Started recording."]);
                },
            };
            var stats = await PresentMon.CaptureAsync(processes, exe, "game.exe", 30, captures, null, CancellationToken.None);
            Assert.Equal(100, stats!.Frames);
            var run = Assert.Single(processes.Streams);
            Assert.Equal(exe, run.File);
            Assert.Equal(TimeSpan.FromSeconds(30) + ToolTimeouts.CaptureGrace, run.Timeout);
            Assert.Empty(Directory.GetFiles(captures));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Stop during a capture: a cancellation, no stats, and the exe is not held open afterwards.</summary>
    [Fact]
    public async Task AStoppedCaptureKeepsNothing()
    {
        var folder = TestFolders.Create("presentmon");
        try
        {
            var exe = PresentMon.Extract(Path.Combine(folder, "tools"));
            var processes = new FakeProcesses { Hangs = true };
            using var stop = new CancellationTokenSource();
            var capture = PresentMon.CaptureAsync(processes, exe, "game.exe", 30, Path.Combine(folder, "captures"), null, stop.Token);
            await processes.Hanging.WaitAsync(TimeSpan.FromSeconds(10));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            Assert.Equal(1, processes.Ended);
            using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A file that is not the pinned build never runs.</summary>
    [Fact]
    public async Task AChangedPresentMonNeverRuns()
    {
        var folder = TestFolders.Create("presentmon");
        try
        {
            var exe = Path.Combine(folder, "PresentMon.exe");
            File.WriteAllText(exe, "not PresentMon");
            var processes = new FakeProcesses();
            await Assert.ThrowsAsync<InvalidDataException>(() => PresentMon.CaptureAsync(processes, exe, "game.exe", 30, folder, null, CancellationToken.None));
            Assert.Empty(processes.Streams);
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }
}
