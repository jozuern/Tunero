using System.Diagnostics;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;

namespace Optimizer.Core.Tests;

/// <summary>Settings > Uninstall: the cleanup after the app has ended, and the hand-over to a package manager.</summary>
public class SelfUninstallTests
{
    [Fact]
    public void CleanupScriptQuotesEveryPath()
    {
        var script = SelfUninstall.CleanupScript("Tunero (1)", @"C:\ProgramData\Tunero", @"C:\Users\O'Brien\Downloads\Tunero (1).exe");
        Assert.Contains("$name = 'Tunero (1)'", script);
        Assert.Contains("Wait-Process -Name $name -Timeout 60", script);
        Assert.Contains(@"$root = 'C:\ProgramData\Tunero'", script);
        Assert.Contains(@"$files = @('C:\Users\O''Brien\Downloads\Tunero (1).exe', 'C:\Users\O''Brien\Downloads\Tunero (1).exe.old')", script);
        Assert.Contains($"& '{Path.Combine(Environment.SystemDirectory, "cmd.exe")}' /d /c rd /s /q $root", script);
    }

    /// <summary>An installed copy's folder in Program Files goes too, once the exe is gone, and only when it is empty then.</summary>
    [Fact]
    public void TheCleanupRemovesTheEmptyInstallFolder()
    {
        var script = SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\Program Files\Tunero\Tunero.exe", installFolder: @"C:\Program Files\Tunero");
        Assert.Contains(@"$installFolder = 'C:\Program Files\Tunero'", script);
        Assert.Contains(@"'C:\Program Files\Tunero\Tunero.exe.new'", script);
        Assert.Contains($"& '{Path.Combine(Environment.SystemDirectory, "cmd.exe")}' /d /c rd /q $installFolder", script);
        Assert.DoesNotContain("rd /s /q $installFolder", script);
        Assert.True(script.IndexOf("rd /q $installFolder", StringComparison.Ordinal) > script.IndexOf("Remove-Item -LiteralPath $f", StringComparison.Ordinal));
        Assert.DoesNotContain("$installFolder", SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\Users\A\Downloads\Tunero.exe"));
    }

    /// <summary>The backups go only when no copy of the app runs: not when it did not end, not when it was started again.</summary>
    [Fact]
    public void NothingIsDeletedWhileTheAppRuns()
    {
        var script = SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\Apps\Tunero.exe");
        const string running = "if (Get-Process -Name $name) { exit 1 }";
        var wait = script.IndexOf("Wait-Process -Name $name -Timeout 60", StringComparison.Ordinal);
        var first = script.IndexOf(running, StringComparison.Ordinal);
        var delete = script.IndexOf("rd /s /q", StringComparison.Ordinal);
        var beforeDelete = script.LastIndexOf(running, delete, StringComparison.Ordinal);
        Assert.True(wait >= 0 && first > wait && beforeDelete > first && delete > beforeDelete, script);
    }

    /// <summary>
    /// The app's own tasks go with it, once the app is gone and before its files: a re-apply task left behind would start
    /// a missing exe (or a file put at its path later) with administrator rights at every sign-in.
    /// </summary>
    [Fact]
    public void TheOwnTasksAreRemoved()
    {
        foreach (var script in new[]
                 {
                     SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\Program Files\Tunero\Tunero.exe"),
                     SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\Program Files\WinGet\Tunero.exe", removedByPackageManager: true),
                 })
        {
            var tasks = script.IndexOf("$ts.GetFolder('\\Tunero')", StringComparison.Ordinal);
            Assert.True(tasks > script.LastIndexOf("exit 1", tasks, StringComparison.Ordinal) && tasks < script.IndexOf("rd /s /q", StringComparison.Ordinal), script);
            Assert.Contains("DeleteFolder('Tunero', 0)", script);
        }
    }

    /// <summary>
    /// A package manager removes the exe; the data folder (with the backups) goes only after it did. When the package
    /// manager fails, the app stays installed and keeps its backups.
    /// </summary>
    [Fact]
    public void PackageCopyWaitsUntilThePackageManagerRemovedTheExe()
    {
        var script = SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe", removedByPackageManager: true);
        Assert.Contains("$files = @()", script);
        var gone = script.IndexOf("for ($i = 0; $i -lt 900 -and (Test-Path -LiteralPath $exe); $i++) {", StringComparison.Ordinal);
        var stays = script.IndexOf("if (Test-Path -LiteralPath $exe) { exit 1 }", StringComparison.Ordinal);
        var delete = script.IndexOf("rd /s /q", StringComparison.Ordinal);
        Assert.True(gone >= 0 && stays > gone && delete > stays, script);
        Assert.DoesNotContain("Wait-Process -Id", script);
    }

    /// <summary>The elevated Chocolatey window writes its cache into the app's TEMP inside the data folder: deleted after it.</summary>
    [Fact]
    public void ChocolateyCopyIsCleanedUpAfterChocolateyHasEnded()
    {
        var script = SelfUninstall.CleanupScript("Tunero", @"C:\ProgramData\Tunero", @"C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe", true, 4711);
        var app = script.IndexOf("Wait-Process -Name $name -Timeout 60", StringComparison.Ordinal);
        var removed = script.IndexOf("if (Test-Path -LiteralPath $exe) { exit 1 }", StringComparison.Ordinal);
        var choco = script.IndexOf("Wait-Process -Id 4711 -Timeout 900", StringComparison.Ordinal);
        var delete = script.IndexOf("rd /s /q", StringComparison.Ordinal);
        Assert.True(app >= 0 && removed > app && choco > removed && delete > choco, script);
    }

    [Fact]
    public void WingetUninstallWaitsForTheAppAndRemovesThisPackage()
    {
        var args = WingetPackage.UninstallArguments("jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe", "Tunero", "Removing", "Failed \"here\"");
        Assert.StartsWith("-NoProfile -Command \"", args);
        Assert.EndsWith("\"", args);
        Assert.Equal(2, args.Count(c => c == '"')); // only the quotes around the command
        var wait = args.IndexOf("Wait-Process -Name 'Tunero'", StringComparison.Ordinal);
        var remove = args.IndexOf("winget uninstall --product-code 'jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe' ", StringComparison.Ordinal);
        Assert.True(wait > 0 && remove > wait);
        Assert.DoesNotContain("Start-Process", args);
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe", "jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.PCOptimizer__DefaultSource\Tunero.exe", "jozuern.PCOptimizer__DefaultSource")]
    [InlineData(@"C:\Users\a\Downloads\Tunero.exe", null)]
    public void ProductCodeIsThePackageFolder(string exe, string? expected) => Assert.Equal(expected, WingetPackage.PackageFolder(exe));

    /// <summary>
    /// Runs the real script on temp folders only (listed in <see cref="ArchitectureTests"/>): the data folder and the exe
    /// go, and a junction inside the data folder is removed without deleting what it points to.
    /// </summary>
    [Fact]
    public void CleanupDeletesDataFolderAndExeButNotWhatALinkPointsTo()
    {
        var folder = TestFolders.Create("uninstall");
        try
        {
            var data = Path.Combine(folder, "data");
            var outside = Path.Combine(folder, "outside");
            Directory.CreateDirectory(Path.Combine(data, "logs"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(data, "logs", "app.log"), "log");
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            TestFolders.Junction(Path.Combine(data, "link"), outside);
            var exe = Path.Combine(folder, "Tunero.exe");
            File.WriteAllBytes(exe, []);
            File.WriteAllBytes(exe + ".old", []);

            RunCleanup(SelfUninstall.CleanupScript("TuneroTestNoSuchProcess", data, exe, tasksFolder: NoSuchTasks));

            Assert.False(Directory.Exists(data));
            Assert.False(File.Exists(exe));
            Assert.False(File.Exists(exe + ".old"));
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>
    /// Runs the real script on temp folders only: a package manager that did not remove the exe (it failed) leaves the
    /// data folder with the backups in place.
    /// </summary>
    [Fact]
    public void FailedPackageManagerKeepsTheBackups()
    {
        var folder = TestFolders.Create("uninstall");
        try
        {
            var data = Path.Combine(folder, "data");
            Directory.CreateDirectory(Path.Combine(data, "backups"));
            var exe = Path.Combine(folder, "Tunero.exe");
            File.WriteAllBytes(exe, []);

            RunCleanup(SelfUninstall.CleanupScript("TuneroTestNoSuchProcess", data, exe, removedByPackageManager: true, packageManagerSeconds: 2, tasksFolder: NoSuchTasks));

            Assert.True(Directory.Exists(Path.Combine(data, "backups")));
            Assert.True(File.Exists(exe));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Runs the real script on temp folders only: while a process with the app's name runs, nothing is deleted.</summary>
    [Fact]
    public void RunningAppKeepsTheBackups()
    {
        var folder = TestFolders.Create("uninstall");
        try
        {
            var data = Path.Combine(folder, "data");
            Directory.CreateDirectory(Path.Combine(data, "backups"));
            var exe = Path.Combine(folder, "Tunero.exe");
            File.WriteAllBytes(exe, []);
            // This test's own process stands in for a copy of the app that was started again.
            var running = Process.GetCurrentProcess().ProcessName;

            RunCleanup(SelfUninstall.CleanupScript(running, data, exe, tasksFolder: NoSuchTasks).Replace("-Timeout 60", "-Timeout 1", StringComparison.Ordinal));

            Assert.True(Directory.Exists(Path.Combine(data, "backups")));
            Assert.True(File.Exists(exe));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>
    /// The Task Scheduler folder the scripts these tests run may delete: one that does not exist, never the app's own
    /// (this PC's re-apply task lives there, and an elevated test run could delete it).
    /// </summary>
    private static readonly string NoSuchTasks = $@"\TuneroTest-{Guid.NewGuid():N}";

    private static void RunCleanup(string script)
    {
        Assert.Contains($"GetFolder('{NoSuchTasks}')", script);
        var psi = new ProcessStartInfo(ProcessHardening.ResolveSystemTool("powershell.exe"), SelfUninstall.PowerShellArguments(script))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ProcessHardening.Apply(psi); // as the app starts it
        using var p = Process.Start(psi)!;
        // The first powershell.exe on a cold CI runner took over a minute just to start (2026-10-13, twice); the script
        // itself runs in seconds.
        var finished = p.WaitForExit(180_000);
        if (!finished) ProcessHardening.KillTree(p);
        Assert.True(finished, "the cleanup script did not finish");
    }
}
