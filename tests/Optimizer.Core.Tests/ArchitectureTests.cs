using System.Text.RegularExpressions;

namespace Optimizer.Core.Tests;

/// <summary>
/// Rules that keep "tests never change the real PC" (CLAUDE.md) a property of the code instead of a convention: the real
/// system adapters are reached only through <see cref="ReadOnlyContext"/>, and only the listed tests start real tools.
/// </summary>
public partial class ArchitectureTests
{
    /// <summary>Tests that start a real process themselves, each reviewed: what they run changes nothing.</summary>
    private static readonly Dictionary<string, string> AllowedToolRuns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BenchmarkHealthTests.cs"] = "cmd.exe starting ping to 127.0.0.1 (the output pipe, timeout and stop tests; a batch file and its marker in a temp folder)",
        ["SelfUninstallTests.cs"] = "powershell.exe running the uninstall cleanup on temp folders and a Task Scheduler folder that does not exist",
        ["TestFolders.cs"] = "cmd.exe mklink /J creating a junction inside a temp folder",
        ["StorageViewModelTests.cs"] = "cmd.exe mklink /J creating a junction inside the test's temp folder (removed again without touching its target)",
    };

    [Fact]
    public void TestsReachTheRealSystemOnlyReadOnly()
    {
        var problems = new List<string>();
        foreach (var file in new[] { "Optimizer.Core.Tests", "Optimizer.App.Tests" }.SelectMany(t => Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", t), "*.cs")))
        {
            var name = Path.GetFileName(file);
            if (name is "ReadOnlyContext.cs" or "ArchitectureTests.cs") continue;
            var text = File.ReadAllText(file);
            if (text.Contains("SystemNotify.CreateContext(", StringComparison.Ordinal)) problems.Add($"{name}: SystemNotify.CreateContext (use ReadOnlyContext.Create)");
            foreach (Match m in RealAdapter().Matches(text)) problems.Add($"{name}: {m.Value} (use ReadOnlyContext.Create)");
            if (text.Contains("ProcessLauncher.RunStreamingAsync(", StringComparison.Ordinal) && !AllowedToolRuns.ContainsKey(name))
                problems.Add($"{name}: ProcessLauncher.RunStreamingAsync (review it and list it in AllowedToolRuns)");
            if (text.Contains("Process.Start(", StringComparison.Ordinal) && !AllowedToolRuns.ContainsKey(name))
                problems.Add($"{name}: Process.Start (review it and list it in AllowedToolRuns)");
        }
        Assert.Empty(problems);
    }

    /// <summary>App code that builds a start info itself, each reviewed: everything else goes through ProcessLauncher.</summary>
    private static readonly Dictionary<string, string> OwnStartInfo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProcessLauncher.cs"] = "the one place that builds hardened start infos",
        ["Program.cs"] = "the bundle starting itself again with the protected runtime folder",
        ["UpdateService.cs"] = "the updated exe started with the start's own environment rules",
        ["InstallService.cs"] = "the installed exe started like the updated one",
        ["DeElevatedLauncher.cs"] = "the shell opening a file or link as the signed-in user",
    };

    /// <summary>
    /// Programs start through ProcessLauncher (System32 tools, no shell, hardened environment) and PowerShell command lines
    /// come from PowerShellScript (-NoProfile always), so a new call site cannot forget either.
    /// </summary>
    [Fact]
    public void ProcessesStartThroughTheLauncher()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (text.Contains("new ProcessStartInfo(", StringComparison.Ordinal) || text.Contains("new System.Diagnostics.ProcessStartInfo(", StringComparison.Ordinal))
                if (!OwnStartInfo.ContainsKey(name)) problems.Add($"{name}: builds a ProcessStartInfo (use ProcessLauncher.Info)");
            if (text.Contains("\"-NoProfile", StringComparison.Ordinal) && name != "PowerShellScript.cs")
                problems.Add($"{name}: writes PowerShell flags (use PowerShellScript)");
        }
        Assert.Empty(problems);
    }

    /// <summary>App files allowed to write a file with File.WriteAll*, and why the path cannot be one someone else chose.</summary>
    private static readonly Dictionary<string, string> OwnFileWrites = new(StringComparer.Ordinal)
    {
        ["InstallMediaViewModel.cs"] = "the remembered choices, a temp file in the app's admin-only data folder",
    };

    /// <summary>
    /// The window's code writes a file someone chose (an export, a developer switch's output) through
    /// <see cref="Platform.SafeDelete.WriteReplacing"/> or the new-file-only output of a switch, never with File.WriteAll*,
    /// which follows a link a program put in place of the file or a folder above it after the dialog closed.
    /// </summary>
    [Fact]
    public void TheWindowWritesChosenFilesWithoutFollowingLinks()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", "Optimizer.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            if (FileWriteAll().IsMatch(File.ReadAllText(file)) && !OwnFileWrites.ContainsKey(name))
                problems.Add($"{name}: writes with File.WriteAll* (use SafeDelete.WriteReplacing)");
        }
        Assert.Empty(problems);
    }

    [GeneratedRegex(@"\bFile\.(WriteAll|AppendAll)\w*\(")]
    private static partial Regex FileWriteAll();

    /// <summary>
    /// Every change goes through <see cref="Tweaks.ChangeSession"/>, which takes the cross-process change lock itself: no
    /// other code takes the lock or calls the engine's apply and undo, so a new entry point cannot skip the lock. And
    /// %variables% are expanded only by <see cref="Platform.PathExpander"/>, never with this elevated process's own
    /// environment (another account's folders under a separate administrator account).
    /// </summary>
    [Fact]
    public void ChangesGoThroughTheSessionAndPathsThroughTheExpander()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (name is not ("ChangeLock.cs" or "ChangeSession.cs") && LockTaken().IsMatch(text)) problems.Add($"{name}: takes the change lock (use ChangeSession.ExclusiveAsync)");
            if (name is not ("ChangeSession.cs" or "TweakEngine.cs") && EngineChange().Match(text) is { Success: true } m) problems.Add($"{name}: {m.Value} (use ChangeSession)");
            if (text.Contains("ExpandEnvironmentVariables", StringComparison.Ordinal)) problems.Add($"{name}: ExpandEnvironmentVariables (use PathExpander)");
        }
        Assert.Empty(problems);
    }

    [GeneratedRegex(@"\b(ChangeLock|changeLock|IChangeLock)\.TryAcquire\(|ChangeLock\.DataFolder\.TryAcquire\(")]
    private static partial Regex LockTaken();

    [GeneratedRegex(@"\b[Ee]ngine\.(ApplyAsync|ApplyBatchAsync|Revert|RevertAll)\(")]
    private static partial Regex EngineChange();

    [Fact]
    public void PowerShellCommandLines()
    {
        Assert.Equal("-NoProfile -NonInteractive -Command \"Get-Date\"", Optimizer.Core.Platform.PowerShellScript.Command("Get-Date"));
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -Command \"scoop update tunero\"",
            Optimizer.Core.Platform.PowerShellScript.Command("scoop update tunero", interactive: true, bypassPolicy: true));
        // A double quote would end the -Command argument and run the rest as further arguments.
        Assert.Throws<ArgumentException>(() => Optimizer.Core.Platform.PowerShellScript.Command("Write-Host \"x\""));
        var encoded = Optimizer.Core.Platform.PowerShellScript.Encoded("Write-Host \"x\"");
        Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", encoded);
        Assert.Equal("Write-Host \"x\"", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded.Split(' ')[^1])));
    }

    [Theory]
    [InlineData("bcdedit.exe", "/enum {current} /v", false)]
    [InlineData("dism.exe", "/Online /English /Get-ReservedStorageState", false)]
    [InlineData("powershell.exe", "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"", false)]
    [InlineData("bcdedit.exe", "/set {current} useplatformclock true", true)]
    [InlineData("powercfg.exe", "/hibernate off", true)]
    [InlineData("dism.exe", "/Online /English /Set-ReservedStorageState /State:Disabled", true)]
    [InlineData("powershell.exe", "-NoProfile -NonInteractive -Command \"Disable-MMAgent -MemoryCompression\"", true)]
    [InlineData("net.exe", "stop wuauserv /y", true)]
    // Commands the app's quick fixes and tweaks run, and the dash forms the tools accept as well.
    [InlineData("ipconfig.exe", "/release", true)]
    [InlineData("ipconfig.exe", "/flushdns", true)]
    [InlineData("ipconfig.exe", "/all", false)]
    [InlineData("netsh.exe", "int ip reset", true)]
    [InlineData("netsh.exe", "winsock reset", true)]
    [InlineData("netsh.exe", "interface ipv4 show interfaces", false)]
    [InlineData("reagentc.exe", "/enable", true)]
    [InlineData("reagentc.exe", "/info", false)]
    [InlineData("w32tm.exe", "/resync", true)]
    [InlineData("lodctr.exe", "/r", true)]
    [InlineData("powercfg.exe", "-setactive 381b4222-f694-41f0-9685-ff5bb260df2e", true)]
    [InlineData("powercfg.exe", "-list", false)]
    [InlineData("bcdedit.exe", "-set {current} useplatformclock true", true)]
    [InlineData("winget.exe", "install --id x --exact", true)]
    [InlineData("winget.exe", "upgrade --all", true)]
    [InlineData("winget.exe", "list --source winget", false)]
    [InlineData("fsutil.exe", "behavior set disablelastaccess 1", true)]
    [InlineData("fsutil.exe", "behavior query disablelastaccess", false)]
    public void TheReadOnlyRunnerRefusesChanges(string file, string arguments, bool refused) =>
        Assert.Equal(refused, ReadOnlyProcesses.LooksLikeAWrite(file, arguments));

    [Fact]
    public void AnEncodedScriptIsCheckedToo()
    {
        static string Encoded(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Assert.False(ReadOnlyProcesses.LooksLikeAWrite("powershell.exe", $"-NoProfile -EncodedCommand {Encoded("Get-AppxPackage -AllUsers | ConvertTo-Json")}"));
        Assert.True(ReadOnlyProcesses.LooksLikeAWrite("powershell.exe", $"-NoProfile -EncodedCommand {Encoded("Remove-AppxPackage x")}"));
    }

    /// <summary>The real adapter classes (SystemPowerManager, SystemBcdStore, SystemRegistryRoots and the others).</summary>
    [GeneratedRegex(@"new System[A-Z][A-Za-z]*(Manager|Store|Scheduler|Roots|Runner|Settings)\(")]
    private static partial Regex RealAdapter();
}
