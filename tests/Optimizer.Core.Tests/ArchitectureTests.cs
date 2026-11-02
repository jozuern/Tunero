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
        foreach (var file in new[] { "Optimizer.Core.Tests", "Optimizer.App.Tests" }.SelectMany(t => Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", t), "*.cs", SearchOption.AllDirectories)))
        {
            if (IsBuildOutput(file)) continue;
            var name = Path.GetFileName(file);
            if (name is "ReadOnlyContext.cs" or "ArchitectureTests.cs") continue;
            var text = File.ReadAllText(file);
            if (text.Contains("SystemNotify.CreateContext(", StringComparison.Ordinal)) problems.Add($"{name}: SystemNotify.CreateContext (use ReadOnlyContext.Create)");
            // The app's own services build the real adapters (and unload Default profile hives when elevated); the window needs
            // them. Tests use the constructor that takes the fakes (AppFixture in Optimizer.App.Tests).
            if (RealAppServices().Match(text) is { Success: true } services) problems.Add($"{name}: {services.Value} (use AppFixture, the services on fakes)");
            if (text.Contains("new MainViewModel(", StringComparison.Ordinal)) problems.Add($"{name}: new MainViewModel (test ScanState with a fake scan source)");
            foreach (Match m in RealAdapter().Matches(text)) problems.Add($"{name}: {m.Value} (use ReadOnlyContext.Create)");
            if (LauncherStart().Match(text) is { Success: true } start && !AllowedToolRuns.ContainsKey(name))
                problems.Add($"{name}: {start.Value.TrimEnd('(')} (review it and list it in AllowedToolRuns)");
            if (text.Contains("Process.Start(", StringComparison.Ordinal) && !AllowedToolRuns.ContainsKey(name))
                problems.Add($"{name}: Process.Start (review it and list it in AllowedToolRuns)");
        }
        Assert.Empty(problems);
    }

    /// <summary>The guard above stops the services with the real adapters, not the constructor that takes the fakes.</summary>
    [Theory]
    [InlineData("var services = new AppServices();", true)]
    [InlineData("private readonly AppServices _services = new ( );", true)]
    [InlineData("AppServices services = new();", true)]
    [InlineData("Services = new AppServices(Engine.Context, Engine.Store, elevation, Engine.RestorePoints, Engine.ChangeLock, os);", false)]
    public void TheServicesGuardStopsOnlyTheRealConstructor(string code, bool real) => Assert.Equal(real, RealAppServices().IsMatch(code));

    /// <summary>
    /// The window's scan and plan state is tested without the window (ScanStateTests): it names no WPF type, no UI text and
    /// no process-wide gate, and it never leaves its thread (ConfigureAwait(false) would bring a result back on another one).
    /// </summary>
    [Fact]
    public void TheWindowsScanStateNeedsNoWpf()
    {
        var problems = new List<string>();
        foreach (var name in new[] { "ScanState.cs", "IScanSource.cs" })
        {
            var text = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Optimizer.App", "ViewModels", name));
            foreach (Match m in WindowOnly().Matches(text)) problems.Add($"{name}: {m.Value}");
        }
        Assert.Empty(problems);
    }

    [GeneratedRegex(@"System\.Windows|Wpf\.Ui|\bLoc\.|\bChangeGate\b|\bDispatcher\b|ConfigureAwait\(false\)")]
    private static partial Regex WindowOnly();

    /// <summary>App code that builds a start info itself, each reviewed: everything else goes through ProcessLauncher.</summary>
    private static readonly Dictionary<string, string> OwnStartInfo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProcessLauncher.cs"] = "the one place that builds hardened start infos",
        ["Program.cs"] = "the bundle starting itself again with the protected runtime folder",
        ["InstallService.cs"] = "a Tunero exe started with the start's own environment rules (the installed copy, the self-update, the first sign-in)",
        ["DeElevatedLauncher.cs"] = "the shell opening a file or link as the signed-in user",
    };

    /// <summary>
    /// Programs start through ProcessLauncher (System32 tools, no shell, hardened environment) and PowerShell flags come only
    /// from PowerShellScript (-NoProfile always): a ProcessStartInfo, a Process.Start or a PowerShell flag anywhere else
    /// fails here.
    /// </summary>
    [Fact]
    public void ProcessesStartThroughTheLauncher()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (text.Contains("new ProcessStartInfo(", StringComparison.Ordinal) || text.Contains("new System.Diagnostics.ProcessStartInfo(", StringComparison.Ordinal))
                if (!OwnStartInfo.ContainsKey(name)) problems.Add($"{name}: builds a ProcessStartInfo (use ProcessLauncher.Info)");
            if (ProcessStart().IsMatch(text) && !OwnStartInfo.ContainsKey(name)) problems.Add($"{name}: Process.Start (use ProcessLauncher.Start)");
            if (PowerShellFlag().IsMatch(text) && name != "PowerShellScript.cs")
                problems.Add($"{name}: writes PowerShell flags (use PowerShellScript)");
        }
        Assert.Empty(problems);
    }

    /// <summary>The launcher guard finds a PowerShell flag without -NoProfile and a direct Process.Start, not a script's own parameters.</summary>
    [Theory]
    [InlineData("ProcessLauncher.Info(PowerShellScript.Exe, \"-ExecutionPolicy Bypass -Command \\\"Get-X\\\"\")", true, false)]
    [InlineData("var args = \"-NonInteractive -EncodedCommand \" + encoded;", true, false)]
    [InlineData("Process.Start(\"netsh.exe\", args);", false, true)]
    [InlineData("System.Diagnostics.Process.Start(start);", false, true)]
    [InlineData("PowerShellScript.Command(\"Get-ChildItem -File C:\\\\x | Select-Object -First 1\")", false, false)]
    [InlineData("_launch.Start(InstallService.ExeStart(path, [SingleInstance.AfterUpdate]));", false, false)]
    public void TheLauncherGuardFindsFlagsAndDirectStarts(string code, bool flag, bool start)
    {
        Assert.Equal(flag, PowerShellFlag().IsMatch(code));
        Assert.Equal(start, ProcessStart().IsMatch(code));
    }

    /// <summary>A PowerShell flag in a string literal, at its start or after a space.</summary>
    [GeneratedRegex(@"""[^""\r\n]*(?<![\w-])-(NoProfile|NonInteractive|EncodedCommand|ExecutionPolicy|Command)\b")]
    private static partial Regex PowerShellFlag();

    [GeneratedRegex(@"\bProcess\.Start\(")]
    private static partial Regex ProcessStart();

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// App files that write with <see cref="Platform.DurableFile"/>, which follows a link as File.WriteAll* does, and why
    /// the path is one of the app's own data files that nobody else chose.
    /// </summary>
    private static readonly Dictionary<string, string> DurableWrites = new(StringComparer.Ordinal)
    {
        ["AppSettings.cs"] = "settings.json in the app's data folder",
        ["FirstSignInStart.cs"] = "the first sign-in marker in the app's data folder",
        ["InstallMediaViewModel.cs"] = "the remembered media choices in the app's admin-only data folder",
    };

    /// <summary>
    /// The window's code writes a file someone chose (an export, a developer switch's output) through
    /// <see cref="Platform.SafeDelete.WriteReplacing"/> or the new-file-only output of a switch, never with File.WriteAll*,
    /// which follows a link a program put in place of the file or a folder above it after the dialog closed. The app's own
    /// data files go through <see cref="Platform.DurableFile"/>, only in the files <see cref="DurableWrites"/> lists.
    /// </summary>
    [Fact]
    public void TheWindowWritesChosenFilesWithoutFollowingLinks()
    {
        var problems = new List<string>();
        var durable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", "Optimizer.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (FileWriteAll().IsMatch(text))
                problems.Add($"{name}: writes with File.WriteAll* (use SafeDelete.WriteReplacing, or DurableFile for the app's own data files)");
            if (!DurableWrite().IsMatch(text)) continue;
            durable.Add(name);
            if (!DurableWrites.ContainsKey(name))
                problems.Add($"{name}: writes with DurableFile, which follows links (SafeDelete.WriteReplacing for a chosen file; an own data file goes in DurableWrites)");
        }
        problems.AddRange(DurableWrites.Keys.Where(k => !durable.Contains(k)).Select(k => $"{k}: listed in DurableWrites but writes nothing with DurableFile"));
        Assert.Empty(problems);
    }

    [GeneratedRegex(@"\bDurableFile\.\w*(Write|Append)\w*\(")]
    private static partial Regex DurableWrite();

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
            if (IsBuildOutput(file)) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (name is not ("ChangeLock.cs" or "ChangeSession.cs") && LockTaken().IsMatch(text)) problems.Add($"{name}: takes the change lock (use ChangeSession.ExclusiveAsync)");
            if (name is not ("ChangeSession.cs" or "TweakEngine.cs") && EngineChange().Match(text) is { Success: true } m) problems.Add($"{name}: {m.Value} (use ChangeSession)");
            if (text.Contains("ExpandEnvironmentVariables", StringComparison.Ordinal)) problems.Add($"{name}: ExpandEnvironmentVariables (use PathExpander)");
        }
        Assert.Empty(problems);
    }

    /// <summary>The change lock taken through any name that ends in ChangeLock (a field "_changeLock" too), or its data folder lock.</summary>
    [GeneratedRegex(@"\b\w*[Cc]hangeLock(\.DataFolder)?\.TryAcquire\(")]
    private static partial Regex LockTaken();

    /// <summary>The engine's apply or undo through any name that ends in engine ("_engine", "tweakEngine").</summary>
    [GeneratedRegex(@"\b\w*[Ee]ngine\.(ApplyAsync|ApplyBatchAsync|Revert|RevertAll)\(")]
    private static partial Regex EngineChange();

    [Theory]
    [InlineData("_engine.Revert(t)", false, true)]
    [InlineData("await tweakEngine.ApplyAsync(t, facts, options)", false, true)]
    [InlineData("Engine.RevertAll(progress)", false, true)]
    [InlineData("_changeLock.TryAcquire(wait)", true, false)]
    [InlineData("IChangeLock.TryAcquire(wait)", true, false)]
    [InlineData("ChangeLock.DataFolder.TryAcquire(TimeSpan.Zero)", true, false)]
    [InlineData("SingleInstance.TryAcquire()", false, false)]
    [InlineData("Runner.ApplyAsync(t)", false, false)]
    [InlineData("steps.Dism.ApplyAsync(esd, 1, m, p, ct)", false, false)]
    public void TheSessionGuardFindsTheLockAndTheEngineByAnyName(string code, bool lockTaken, bool engineChange)
    {
        Assert.Equal(lockTaken, LockTaken().IsMatch(code));
        Assert.Equal(engineChange, EngineChange().IsMatch(code));
    }

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
    [InlineData("taskkill.exe", "/f /im explorer.exe /fi \"SESSION eq 1\"", true)]
    [InlineData("shutdown.exe", "/r /o /t 0", true)]
    [InlineData("chkdsk.exe", "C: /scan", true)]
    public void TheReadOnlyRunnerRefusesChanges(string file, string arguments, bool refused) =>
        Assert.Equal(refused, ReadOnlyProcesses.LooksLikeAWrite(file, arguments));

    /// <summary>Every step of every quick fix changes something, so the read-only runner refuses each of them.</summary>
    [Fact]
    public void TheReadOnlyRunnerRefusesEveryQuickFix()
    {
        var passed = Optimizer.Core.Tools.QuickFixes.All
            .SelectMany(fix => fix.Steps.Select(step => (fix.Id, step.File, step.Arguments)))
            .Where(s => !ReadOnlyProcesses.LooksLikeAWrite(s.File, s.Arguments))
            .Select(s => $"{s.Id}: {s.File} {s.Arguments}")
            .ToList();
        Assert.Empty(passed);
    }

    [Fact]
    public void AnEncodedScriptIsCheckedToo()
    {
        static string Encoded(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Assert.False(ReadOnlyProcesses.LooksLikeAWrite("powershell.exe", $"-NoProfile -EncodedCommand {Encoded("Get-AppxPackage -AllUsers | ConvertTo-Json")}"));
        Assert.True(ReadOnlyProcesses.LooksLikeAWrite("powershell.exe", $"-NoProfile -EncodedCommand {Encoded("Remove-AppxPackage x")}"));
    }

    /// <summary>The ProcessLauncher methods that start a real process.</summary>
    [GeneratedRegex(@"ProcessLauncher\.(RunStreamingAsync|Run|Start|StartDetached)\(")]
    private static partial Regex LauncherStart();

    /// <summary>The app's services with the real adapters: "new AppServices()" or "AppServices services = new()".</summary>
    [GeneratedRegex(@"new\s+AppServices\s*\(\s*\)|\bAppServices\s+\w+\s*=\s*new\s*\(\s*\)")]
    private static partial Regex RealAppServices();

    /// <summary>The real adapter classes (SystemPowerManager, SystemBcdStore, SystemRegistryRoots and the others).</summary>
    [GeneratedRegex(@"new System[A-Z][A-Za-z]*(Manager|Store|Scheduler|Roots|Runner|Settings)\(")]
    private static partial Regex RealAdapter();
}
