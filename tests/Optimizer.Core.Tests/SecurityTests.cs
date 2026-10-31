using System.Security.Cryptography;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;

namespace Optimizer.Core.Tests;

/// <summary>The app runs as administrator: links, downloads and files in user-writable places must not be trusted blindly.</summary>
[Collection(GlobalStateCollection.Name)]
public class SecurityTests
{
    private const string Download = "https://github.com/jozuern/Tunero/releases/download/v1.0.0/";

    [Theory]
    [InlineData(Download + "Tunero.exe", true)]
    [InlineData("https://github.com/jozuern/PCOptimizer/releases/download/v1.0.0/PCOptimizer.exe", true)]
    [InlineData("https://github.com/jozuern/PCOptimizerX/releases/download/v1.0.0/Tunero.exe", false)]
    [InlineData("https://github.com/someone/Tunero/releases/download/v1.0.0/Tunero.exe", false)]
    [InlineData(Download + "../../../../evil/repo/releases/download/v1.0.0/Tunero.exe", false)]
    [InlineData(Download + "%2e%2e/%2e%2e/Tunero.exe", false)]
    [InlineData(Download + "Tunero.exe?x=1", false)]
    [InlineData(Download + "Tunero.exe#x", false)]
    [InlineData("http://github.com/jozuern/Tunero/releases/download/v1.0.0/Tunero.exe", false)]
    [InlineData("https://github.com/jozuern/Tunero/releases/download/v1.0.0\\..\\Tunero.exe", false)]
    public void ReleaseDownloadsMustStayInThisRepository(string url, bool expected) =>
        Assert.Equal(expected, ReleaseCheck.IsReleaseDownload(url));

    [Fact]
    public void ReleaseAssetsWithDotSegmentsAreIgnored()
    {
        var json = $$"""
            {"tag_name":"v1.0.0","assets":[
              {"name":"Tunero.exe","browser_download_url":"{{Download}}../../../../evil/x/releases/download/v1/Tunero.exe"},
              {"name":"Tunero.exe.sha256","browser_download_url":"{{Download}}Tunero.exe.sha256"}]}
            """;
        Assert.Null(ReleaseCheck.Evaluate(new Version(0, 3, 0), json).Assets);
    }

    [Fact]
    public void UpdatedExeStartsOnlyWithTheCheckedHashAndCannotBeSwappedMeanwhile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pco-start-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "Tunero.exe");
            File.WriteAllText(exe, "new exe");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe)));

            var started = false;
            Exception? swap = null;
            Assert.True(Updater.StartVerified(exe, hash, _ =>
            {
                started = true;
                // While the process starts, another process cannot replace or rename the file.
                swap = Record.Exception(() => File.WriteAllText(exe, "planted"));
                swap ??= Record.Exception(() => File.Move(exe, exe + ".moved"));
            }));
            Assert.True(started);
            Assert.IsType<IOException>(swap);
            Assert.Equal("new exe", File.ReadAllText(exe));

            File.WriteAllText(exe, "planted");
            Assert.False(Updater.StartVerified(exe, hash, _ => throw new InvalidOperationException("must not start")));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/windows/", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ms-windows-store://pdp/?PFN=Microsoft.WindowsCalculator_8wekyb3d8bbwe", true)]
    [InlineData(@"C:\Users\Public\evil.exe", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("not a link", false)]
    public void OnlyWebAndStoreLinksAreOpened(string target, bool expected) =>
        Assert.Equal(expected, DeElevatedLauncher.IsLink(target));

    /// <summary>
    /// A one-time task that is still waiting reports 1999-11-30 and SCHED_S_TASK_HAS_NOT_RUN: it has not run, so its
    /// "result" is not taken for the program's exit code. One that ran after the start counts, with a little clock tolerance.
    /// </summary>
    [Fact]
    public void AWaitingTaskIsNotTakenForAFinishedOne()
    {
        var runAt = new DateTime(2026, 10, 31, 12, 0, 0);
        Assert.False(DeElevatedLauncher.HasRunSince(new DateTime(1999, 11, 30).ToOADate(), 0x00041303, runAt));
        Assert.False(DeElevatedLauncher.HasRunSince(new DateTime(1999, 11, 30).ToOADate(), 0, runAt));
        Assert.False(DeElevatedLauncher.HasRunSince(0, 0x00041303, runAt));
        Assert.False(DeElevatedLauncher.HasRunSince(runAt.AddMinutes(-5).ToOADate(), 0, runAt));
        Assert.True(DeElevatedLauncher.HasRunSince(runAt.AddSeconds(3).ToOADate(), 0, runAt));
        Assert.True(DeElevatedLauncher.HasRunSince(runAt.AddSeconds(-1).ToOADate(), 1, runAt));
    }

    /// <summary>"Show in Explorer" hands Explorer only a quoted full path: selected, or a folder opened when asked.</summary>
    [Theory]
    [InlineData(@"C:\Users\me\file.txt", DeElevatedLauncher.ItemKind.File, false, "/select,\"C:\\Users\\me\\file.txt\"")]
    [InlineData(@"C:\Users\me\file.txt", DeElevatedLauncher.ItemKind.File, true, "/select,\"C:\\Users\\me\\file.txt\"")]
    [InlineData(@"C:\Users\me\Logs", DeElevatedLauncher.ItemKind.Folder, true, "\"C:\\Users\\me\\Logs\"")]
    [InlineData(@"C:\Users\me\Logs", DeElevatedLauncher.ItemKind.Folder, false, "/select,\"C:\\Users\\me\\Logs\"")]
    [InlineData(@"C:\Users\me\gone.txt", DeElevatedLauncher.ItemKind.Missing, false, null)]
    [InlineData(@"file.txt", DeElevatedLauncher.ItemKind.File, false, null)]
    [InlineData(@"\Users\me\file.txt", DeElevatedLauncher.ItemKind.File, false, null)]
    [InlineData("", DeElevatedLauncher.ItemKind.File, false, null)]
    public void ExplorerGetsOnlyAQuotedFullPath(string path, DeElevatedLauncher.ItemKind kind, bool openFolder, string? expected) =>
        Assert.Equal(expected, DeElevatedLauncher.ExplorerArguments(path, kind, openFolder));

    [Fact]
    public void ElevatedToolsStartWithoutUserControlledCodePaths()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ProcessHardening.ResolveSystemTool("powershell.exe"));
        psi.Environment["PSModulePath"] = @"C:\Users\me\Documents\WindowsPowerShell\Modules";
        psi.Environment["COR_ENABLE_PROFILING"] = "1";
        psi.Environment["COR_PROFILER_PATH"] = @"C:\Users\me\evil.dll";
        psi.Environment["DOTNET_STARTUP_HOOKS"] = @"C:\Users\me\hook.dll";
        psi.Environment["PATH_KEEP"] = "x";
        ProcessHardening.Apply(psi);
        Assert.Equal(ProcessHardening.PowerShellModules, psi.Environment["PSModulePath"]);
        Assert.False(psi.Environment.ContainsKey("COR_ENABLE_PROFILING"));
        Assert.False(psi.Environment.ContainsKey("COR_PROFILER_PATH"));
        Assert.False(psi.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.Equal("x", psi.Environment["PATH_KEEP"]);
        Assert.Equal(Environment.SystemDirectory, psi.WorkingDirectory);
        Assert.StartsWith(Environment.SystemDirectory, psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), ProcessHardening.ResolveSystemTool("bcdedit.exe"));
    }

    /// <summary>windir, ComSpec and PATH come from Windows, not from HKCU\Environment; TEMP is the app's admin-only folder.</summary>
    [Fact]
    public void ElevatedToolsGetWindowsPathsAndTheAppsTemp()
    {
        var windows = Path.GetDirectoryName(Environment.SystemDirectory)!;
        var temp = TempFolder("tooltemp");
        var before = ProcessHardening.TempFolder;
        try
        {
            ProcessHardening.TempFolder = temp;
            var psi = new System.Diagnostics.ProcessStartInfo(ProcessHardening.ResolveSystemTool("dism.exe"));
            psi.Environment["windir"] = @"C:\Users\me\fakewin";
            psi.Environment["ComSpec"] = @"C:\Users\me\cmd.exe";
            psi.Environment["PATH"] = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps;" + Environment.SystemDirectory;
            psi.Environment["TEMP"] = @"C:\Users\me\AppData\Local\Temp";
            ProcessHardening.Apply(psi);
            Assert.Equal(windows, psi.Environment["windir"]);
            Assert.Equal(windows, psi.Environment["SystemRoot"]);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), psi.Environment["ComSpec"]);
            var path = psi.Environment["PATH"]!.Split(';');
            Assert.Equal(Environment.SystemDirectory, path[0]);
            Assert.DoesNotContain(path, p => p.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) || p.Contains('%'));
            Assert.Equal(temp, psi.Environment["TEMP"]);
            Assert.Equal(temp, psi.Environment["TMP"]);
        }
        finally
        {
            ProcessHardening.TempFolder = before;
            TestFolders.Delete(temp);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AConfiguredProfilerIsRecognized(string? value, bool expected) =>
        Assert.Equal(expected ? "CORECLR_ENABLE_PROFILING" : null, ProcessHardening.DiagnosticsRequested(n => n == "CORECLR_ENABLE_PROFILING" ? value : null));

    [Theory]
    [InlineData("DOTNET_DiagnosticPorts", @"\\.\pipe\x,connect,suspend")]
    [InlineData("COMPlus_DiagnosticPorts", "x")]
    [InlineData("CORECLR_ENABLE_NOTIFICATION_PROFILERS", "1")]
    [InlineData("DOTNET_ENABLE_PROFILING", "1")]
    [InlineData("DOTNET_EnableEventPipe", "1")]
    [InlineData("DOTNET_DbgEnableMiniDump", "1")]
    public void OtherRuntimeDiagnosticsAreRecognized(string name, string value)
    {
        // Windows environment names are case-insensitive.
        string? Get(string n) => n.Equals(name, StringComparison.OrdinalIgnoreCase) ? value : null;
        Assert.Equal(name, ProcessHardening.DiagnosticsRequested(Get), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void HarmlessDotnetVariablesDoNotBlockTheStart()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_ROOT"] = @"C:\Program Files\dotnet", ["DOTNET_EnableEventPipe"] = "0",
        };
        Assert.Null(ProcessHardening.DiagnosticsRequested(n => set.GetValueOrDefault(n)));
    }

    // ---------------- Data folder ----------------

    private static string TempFolder(string name) => TestFolders.Create(name);

    /// <summary>
    /// A standard user can plant backups or settings in the data folder before the first elevated start. Such files
    /// are owned by the user; when the test runs elevated (files would be owned by Administrators) the owner is set
    /// to the user explicitly.
    /// </summary>
    [Fact]
    public void FilesNotCreatedByAnAdministratorAreRemoved()
    {
        var root = TempFolder("untrusted");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "backups"));
            var planted = Path.Combine(root, "backups", "planted.json");
            var settings = Path.Combine(root, "settings.json");
            File.WriteAllText(planted, "{}");
            File.WriteAllText(settings, "{}");
            if (DataPaths.ProcessIsElevated)
                foreach (var f in new[] { planted, settings }) OwnByUser(f);
            Assert.False(Backup.SecureFolder.IsOwnedByAdmins(settings));

            Backup.SecureFolder.RemoveUntrusted(root);

            Assert.False(File.Exists(planted));
            Assert.False(File.Exists(settings));
            // What is left is trusted: an elevated test run creates the backups folder itself (owned by Administrators).
            Assert.All(new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories),
                e => Assert.True(Backup.SecureFolder.IsOwnedByAdmins(e), e.FullName));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    /// <summary>
    /// The data folder is created locked in one step and never adopts what another account put there: a folder owned by
    /// the user is replaced, a planted file inside a trusted folder is deleted instead of given a new owner. Without
    /// administrator rights the folder cannot get the Administrators owner, and Lock fails closed.
    /// </summary>
    [Fact]
    public void LockCreatesALockedFolderAndNeverAdoptsForeignEntries()
    {
        var parent = TempFolder("lock");
        try
        {
            var root = Path.Combine(parent, "data");
            if (!DataPaths.ProcessIsElevated)
            {
                Assert.False(Backup.SecureFolder.Lock(root));
                return;
            }
            Assert.True(Backup.SecureFolder.Lock(root));
            Assert.True(Backup.SecureFolder.IsLocked(new DirectoryInfo(root)));
            Assert.True(Backup.SecureFolder.IsOwnedByAdmins(new DirectoryInfo(root)));

            var planted = Path.Combine(root, "planted.json");
            File.WriteAllText(planted, "{}");
            OwnByUser(planted);
            Assert.True(Backup.SecureFolder.Lock(root));
            Assert.False(File.Exists(planted));

            // A root created by the user is removed and created again, not taken over with its content.
            var foreign = Path.Combine(parent, "foreign");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "settings.json"), "{}");
            var info = new DirectoryInfo(foreign);
            var security = info.GetAccessControl();
            security.SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            info.SetAccessControl(security);
            Assert.True(Backup.SecureFolder.PrepareRoot(foreign));
            Assert.False(File.Exists(Path.Combine(foreign, "settings.json")));
            Assert.True(Backup.SecureFolder.IsLocked(new DirectoryInfo(foreign)));
        }
        finally
        {
            TestFolders.Delete(parent);
        }
    }

    private static void OwnByUser(string file)
    {
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        info.SetAccessControl(security);
    }

    [Fact]
    public void RemoveLinksDeletesJunctionsButNotTheirTargets()
    {
        var root = TempFolder("links");
        var target = TempFolder("target");
        try
        {
            File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
            TestFolders.Junction(Path.Combine(root, "logs"), target);
            Backup.SecureFolder.RemoveLinks(root);
            Assert.False(Path.Exists(Path.Combine(root, "logs")));
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        }
        finally
        {
            TestFolders.Delete(root);
            TestFolders.Delete(target);
        }
    }

    [Fact]
    public void SafeDeleteHandlesPathsLongerThanTheOldBuffer()
    {
        var root = TempFolder("long");
        try
        {
            var dir = root;
            while (dir.Length < 1200) dir = Path.Combine(dir, new string('d', 60));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "old.tmp");
            File.WriteAllText(file, "x");
            SafeDelete.DeleteFile(file);
            Assert.False(File.Exists(file));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    // ---------------- Native libraries ----------------

    [Fact]
    public void PlainDllNamesResolveToSystem32First()
    {
        var trusted = TempFolder("trusted");
        try
        {
            File.WriteAllText(Path.Combine(trusted, "wpfgfx_cor3.dll"), "");
            File.WriteAllText(Path.Combine(trusted, "powrprof.dll"), "");
            var system32 = Environment.SystemDirectory;
            Assert.Equal(Path.Combine(system32, "powrprof.dll"), NativeLibraryGuard.Locate("powrprof.dll", system32, [trusted]), ignoreCase: true);
            Assert.Equal(Path.Combine(system32, "powrprof.dll"), NativeLibraryGuard.Locate("powrprof", system32, [trusted]), ignoreCase: true);
            Assert.Equal(Path.Combine(trusted, "wpfgfx_cor3.dll"), NativeLibraryGuard.Locate("wpfgfx_cor3", system32, [trusted]));
            Assert.Null(NativeLibraryGuard.Locate(@"C:\Elsewhere\x.dll", system32, [trusted]));
            Assert.Null(NativeLibraryGuard.Locate("not-a-real-library-xyz", system32, [trusted]));
        }
        finally
        {
            TestFolders.Delete(trusted);
        }
    }

    private const string ProtectedRuntime = @"C:\ProgramData\Tunero\runtime";

    [Theory]
    [InlineData(true, null, false, false, RuntimeStart.Restart)]
    [InlineData(true, @"C:\Users\me\AppData\Local\Temp", false, true, RuntimeStart.Restart)]
    [InlineData(true, ProtectedRuntime, false, true, RuntimeStart.Continue)]
    [InlineData(true, ProtectedRuntime + @"\", true, true, RuntimeStart.Continue)]
    [InlineData(false, null, false, true, RuntimeStart.Continue)]
    // The restart marker without a folder was inherited (1.0.0 passed it on to the version its update started): restart.
    [InlineData(true, null, true, true, RuntimeStart.Restart)]
    // The restart marker never skips the check: with another folder (set from outside, or a restart that did not take),
    // an elevated process refuses to start; an unelevated one gains nothing from the folder and continues.
    [InlineData(true, @"C:\Users\me\Planted", true, true, RuntimeStart.Refuse)]
    [InlineData(true, @"C:\Users\me\Planted", true, false, RuntimeStart.Continue)]
    public void SingleFileExeRestartsOnceWithTheProtectedExtractionFolder(bool bundle, string? current, bool restarted, bool elevated, RuntimeStart expected) =>
        Assert.Equal(expected, SingleFileRuntime.Decide(bundle, current, ProtectedRuntime, restarted, elevated));

    [Fact]
    public void AFailedRestartStopsAnElevatedProcess()
    {
        Assert.Equal(RuntimeStart.Refuse, SingleFileRuntime.AfterFailedRestart(elevated: true));
        Assert.Equal(RuntimeStart.Continue, SingleFileRuntime.AfterFailedRestart(elevated: false));
    }

    [Fact]
    public void OnlyExtractionFoldersInsideTheDataFolderAreTrusted()
    {
        var list = SingleFileRuntime.TrustedFolders(
            @"C:\ProgramData\Tunero\runtime\Tunero\abc123\;C:\Users\me\Downloads\;C:\ProgramData\Tunero\runtime-evil\x;relative\path",
            @"C:\ProgramData\Tunero\runtime");
        Assert.Equal([@"C:\ProgramData\Tunero\runtime\Tunero\abc123"], list);
    }
}
