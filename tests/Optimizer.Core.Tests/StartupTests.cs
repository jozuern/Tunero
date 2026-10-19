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
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

public class StartupTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true);
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    [Fact]
    public async Task DisablingARunEntryLikeTaskManagerAndUndo()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Discord", "string", "\"C:\\Users\\x\\Discord\\Update.exe\" --processStart Discord.exe");
        var scanner = new StartupScanner(fx.Registry, fx.Tasks, null);
        var entry = Assert.Single(scanner.RunKeys(), e => e.Name == "Discord");
        Assert.True(entry.Enabled); // no StartupApproved value = enabled

        var tweak = StartupTweaks.Set(entry, enabled: false)!;
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(tweak, Facts, new HashSet<string>(), Options)).Outcome);
        var raw = RegistryValue.Read(fx.Registry, Hive.User, $@"{Approved}\Run", "Discord");
        Assert.StartsWith("03000000", raw.Data);
        Assert.Equal(24, raw.Data!.Length); // 12 bytes
        Assert.False(Assert.Single(scanner.RunKeys(), e => e.Name == "Discord").Enabled);
        // The Run value itself is untouched.
        Assert.True(RegistryValue.Read(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Discord").Existed);

        Assert.True(fx.Engine.Revert(tweak).Success);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.User, $@"{Approved}\Run", "Discord").Existed);
    }

    [Fact]
    public void TaskManagerDisabledValuesAreRecognized()
    {
        Assert.False(StartupApprovedAction.IsEnabled(new StoredValue(true, "binary", "030000005A1B3C4D5E6F7081")));
        Assert.False(StartupApprovedAction.IsEnabled(new StoredValue(true, "binary", "010000000000000000000000")));
        Assert.True(StartupApprovedAction.IsEnabled(new StoredValue(true, "binary", "020000000000000000000000")));
        Assert.True(StartupApprovedAction.IsEnabled(new StoredValue(true, "binary", "060000000000000000000000")));
        Assert.True(StartupApprovedAction.IsEnabled(StoredValue.Missing));
        // Values a program wrote in another form must not throw (that ended the scan of the Run keys).
        Assert.True(StartupApprovedAction.IsEnabled(new StoredValue(true, "string", "zz")));
        Assert.True(StartupApprovedAction.IsEnabled(new StoredValue(true, "binary", "zz00")));
        Assert.True(StartupApprovedAction.IsEnabled(new StoredValue(true, "dword", "3")));
    }

    [Fact]
    public async Task ShellExtensionIsBlockedByClsidAndUndone()
    {
        using var fx = new EngineFixture();
        const string clsid = "{23170F69-40C1-278A-1000-000100020000}";
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\7-Zip", "", "string", clsid);
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"SOFTWARE\Classes\CLSID\{clsid}", "", "string", "7-Zip Shell Extension");
        var entry = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions());
        Assert.Equal("7-Zip Shell Extension", entry.Name);
        var tweak = StartupTweaks.Set(entry, enabled: false)!;
        await fx.Engine.ApplyAsync(tweak, Facts, new HashSet<string>(), Options);
        Assert.True(RegistryValue.Read(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", clsid).Existed);
        Assert.False(Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions()).Enabled);
        Assert.True(fx.Engine.Revert(tweak).Success);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", clsid).Existed);
    }

    /// <summary>Blocked in the machine's list and in the user's own: one switch clears both, or Explorer keeps it off.</summary>
    [Fact]
    public async Task ShellExtensionBlockedInBothListsIsClearedFromBoth()
    {
        using var fx = new EngineFixture();
        const string clsid = "{23170F69-40C1-278A-1000-000100020000}";
        const string blocked = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\7-Zip", "", "string", clsid);
        RegistryValue.Write(fx.Registry, Hive.Machine, blocked, clsid, "string", "");
        RegistryValue.Write(fx.Registry, Hive.User, blocked, clsid, "string", "");
        var entry = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions());
        Assert.False(entry.Enabled);

        await fx.Engine.ApplyAsync(StartupTweaks.Set(entry, enabled: true)!, Facts, new HashSet<string>(), Options);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, blocked, clsid).Existed);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.User, blocked, clsid).Existed);
        Assert.True(Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions()).Enabled);
    }

    /// <summary>A codec for 32-bit players: the filter and its class are both registered under WOW6432Node.</summary>
    [Fact]
    public void ThirtyTwoBitDirectShowFiltersAreListedWithTheirFile()
    {
        using var fx = new EngineFixture();
        const string clsid = "{11111111-2222-3333-4444-666666666666}";
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"SOFTWARE\Classes\WOW6432Node\CLSID\{{083863F1-70DE-11D0-BD40-00A0C911CE86}}\Instance\{clsid}", "FriendlyName", "string", "Old Splitter");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"SOFTWARE\Classes\WOW6432Node\CLSID\{clsid}\InprocServer32", "", "string", @"C:\Codecs\splitter32.ax");
        var filter = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).DirectShowFilters());
        Assert.Equal("Old Splitter", filter.Name);
        Assert.Equal(@"C:\Codecs\splitter32.ax", filter.ImagePath);
    }

    [Fact]
    public void WinlogonDefaultsAndIfeoDebuggers()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.Machine, StartupScanner.Winlogon, "Shell", "string", "explorer.exe");
        RegistryValue.Write(fx.Registry, Hive.Machine, StartupScanner.Winlogon, "Userinit", "string", @"C:\Windows\system32\userinit.exe,C:\evil.exe");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{StartupScanner.Ifeo}\taskmgr.exe", "Debugger", "string", @"C:\Tools\procexp64.exe");
        var scanner = new StartupScanner(fx.Registry, fx.Tasks, null);
        var winlogon = scanner.WinlogonEntries().ToList();
        Assert.False(winlogon.Single(e => e.Name == "Shell").Suspicious);
        Assert.True(winlogon.Single(e => e.Name == "Userinit").Suspicious);
        var ifeo = Assert.Single(scanner.ImageHijacks());
        Assert.Equal(Risk.Expert, StartupTweaks.Set(ifeo, enabled: false)!.EffectiveRisk);
        Assert.Null(StartupTweaks.Set(winlogon[0], enabled: false)); // read-only
    }

    [Fact]
    public void ListedOnlyLocationsAreReadWithTheirFiles()
    {
        using var fx = new EngineFixture();
        var sys = Environment.SystemDirectory;
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "Cleanup", "string", @"C:\Tools\cleanup.exe /once");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Active Setup\Installed Components\{AAAA}", "", "string", "Contoso Setup");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Active Setup\Installed Components\{AAAA}", "StubPath", "string", @"C:\Contoso\setup.exe /user");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Active Setup\Installed Components\{BBBB}", "StubPath", "string", @"C:\Old\old.exe");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Active Setup\Installed Components\{BBBB}", "IsInstalled", "dword", "0");
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows NT\CurrentVersion\Windows", "Load", "string", @"C:\Users\x\evil.exe");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute", "multiString", "autocheck autochk *\nC:\\evil\\native.exe");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs", "kernel32", "string", "kernel32.dll");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs", "DllDirectory", "string", "%SystemRoot%\\system32");
        // PackedCatalogItem: the DLL path as a zero-terminated ANSI string, then binary data.
        var packed = Convert.ToHexString([.. System.Text.Encoding.ASCII.GetBytes(@"%SystemRoot%\system32\mswsock.dll"), 0, 0x41, 0x42]);
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries\000000000001", "PackedCatalogItem", "binary", packed);
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Local Port", "Driver", "string", "localspl.dll");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Lsa", "Authentication Packages", "multiString", "msv1_0");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order", "ProviderOrder", "string", "RDPNP,LanmanWorkstation");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\NetworkProvider", "ProviderPath", "string", @"%SystemRoot%\System32\ntlanman.dll");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\NetworkProvider", "Name", "string", "Microsoft Windows Network");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32", "msacm.imaadpcm", "string", "imaadp32.acm");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32", "MidisrvTransferComplete", "dword", "1");

        var all = new StartupScanner(fx.Registry, fx.Tasks, null) { CommonStartupFolder = Path.Combine(fx.BackupRoot, "no-startup") }.ScanAll(includeServicesAndDrivers: false, includeWmi: false);
        StartupEntry One(StartupKind kind) => Assert.Single(all, e => e.Kind == kind);
        Assert.Equal(@"C:\Tools\cleanup.exe", One(StartupKind.RunOnce).ImagePath);
        Assert.Equal("Contoso Setup", One(StartupKind.ActiveSetup).Name); // {BBBB} is no longer installed
        Assert.True(One(StartupKind.LoadValue).Suspicious);
        var boot = all.Where(e => e.Kind == StartupKind.BootExecute).ToList();
        Assert.False(boot.Single(e => e.Command == StartupScanner.BootExecuteDefault).Suspicious);
        Assert.Equal(Path.Combine(sys, "autochk.exe"), boot.Single(e => e.Command == StartupScanner.BootExecuteDefault).ImagePath);
        Assert.True(boot.Single(e => e.Command!.Contains("native")).Suspicious);
        Assert.Equal(Path.Combine(sys, "kernel32.dll"), One(StartupKind.KnownDll).ImagePath);
        // Catalog_Entries is the 32-bit catalog: its System32 path is loaded from SysWOW64 (WOW64 file redirection).
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "mswsock.dll"), One(StartupKind.WinsockProvider).ImagePath, ignoreCase: true);
        Assert.Equal(Path.Combine(sys, "localspl.dll"), One(StartupKind.PrintMonitor).ImagePath);
        Assert.Equal(Path.Combine(sys, "msv1_0.dll"), One(StartupKind.LsaPackage).ImagePath);
        var providers = all.Where(e => e.Kind == StartupKind.NetworkProvider).ToList();
        Assert.Equal(["RDPNP", "Microsoft Windows Network"], providers.Select(p => p.Name));
        Assert.Equal(Path.Combine(sys, "imaadp32.acm"), One(StartupKind.Codec).ImagePath);
        // Listed only: none of them can be switched here.
        Assert.All(all.Where(e => e.Kind >= StartupKind.RunOnce), e => Assert.Null(StartupTweaks.Set(e, enabled: false)));
        Assert.Null(StartupScanner.PackedPath([0x01, 0x02, 0x00]));
    }

    [Fact]
    public void SnapshotShowsWhatAnInstallerAddedChangedAndRemoved()
    {
        StartupEntry Run(string name, string command) =>
            new(StartupKind.RunKey, name, command, null, @"HKCU\Run", Hive.User, true, $"run:User:Run:{name}");
        var before = new[] { Run("Discord", "discord.exe"), Run("Steam", "steam.exe -silent"), Run("Old", "old.exe") };
        var snapshot = StartupSnapshot.Take(before, DateTimeOffset.Parse("2026-10-01T10:00:00Z"));

        var folder = TestFolders.Create("snapshot");
        try
        {
            var file = Path.Combine(folder, "startup-snapshot.json");
            StartupSnapshot.Save(file, snapshot);
            var loaded = StartupSnapshot.Load(file)!;
            Assert.Equal(3, loaded.Entries.Count);

            var after = new[] { Run("Discord", "discord.exe"), Run("Steam", "steam.exe -silent -nofriendsui"), Run("Updater", "contoso-update.exe") };
            var diff = StartupSnapshot.Compare(loaded, after);
            Assert.Equal(["run:User:Run:Steam", "run:User:Run:Updater"], diff.NewKeys.Order());
            Assert.Equal(["Old", "Steam"], diff.Removed.Select(r => r.Name).Order());
            Assert.Equal(snapshot.TakenAt, diff.TakenAt);

            File.WriteAllText(file, "{ broken");
            Assert.Null(StartupSnapshot.Load(file));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [Fact]
    public void LogonTasksComeFromTheScheduler()
    {
        using var fx = new EngineFixture();
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Vendor\Updater", true, "Vendor", @"C:\Vendor\up.exe", "/silent", true, false, null));
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Vendor\Daily", true, "Vendor", @"C:\Vendor\up.exe", null, false, false, null));
        var task = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).LogonTasks());
        Assert.Equal(@"\Vendor\Updater", task.Target);
        Assert.IsType<ScheduledTaskAction>(StartupTweaks.Set(task, false)!.Actions.Single());

        // A program path stored with quotes is quoted only once, so the image (signature, script host) is found.
        fx.Tasks.Listed.Clear();
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Vendor\Ps", true, "Vendor", "\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\"", "-w hidden -enc SQBFAFgA", true, false, null));
        var ps = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).LogonTasks());
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ps.ImagePath, ignoreCase: true);
        Assert.True(ps.RunsScriptHost);
    }

    /// <summary>
    /// Every program of a task is its own entry (a second one cannot hide behind a signed first one); a COM handler task
    /// shows the DLL registered for it; Windows' own tasks are listed but switched only on Services &amp; tasks; tasks of
    /// other programs are listed whatever starts them, as in Autoruns.
    /// </summary>
    [Fact]
    public void TasksShowEveryProgramTheirHandlerAndWhoMaySwitchThem()
    {
        using var fx = new EngineFixture();
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Vendor\Two", true, "Vendor", @"C:\Windows\System32\cmd.exe", "/c exit", true, false, null,
            [(@"C:\Users\Public\x.exe", null)]));
        const string clsid = "{12345678-1234-1234-1234-123456789ABC}";
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32", "", "string", @"C:\Windows\System32\fixture.dll");
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Microsoft\Windows\Fixture\Handler", true, "Microsoft", null, null, true, false, null, null, clsid));
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Vendor\Daily", true, "Vendor", @"C:\Vendor\up.exe", null, false, false, null));
        fx.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Microsoft\Windows\Fixture\Daily", true, "Microsoft", @"C:\Windows\System32\x.exe", null, false, false, null));

        var scanner = new StartupScanner(fx.Registry, fx.Tasks, null);
        var all = scanner.ScheduledTasks().ToList();
        Assert.Equal([@"C:\Windows\System32\cmd.exe", @"C:\Users\Public\x.exe"], all.Where(e => e.Target == @"\Vendor\Two").Select(e => e.ImagePath!));
        Assert.Equal(2, all.Where(e => e.Target == @"\Vendor\Two").Select(e => e.Key).Distinct().Count());
        var handler = Assert.Single(all, e => e.Target == @"\Microsoft\Windows\Fixture\Handler");
        Assert.Equal(@"C:\Windows\System32\fixture.dll", handler.ImagePath, ignoreCase: true);
        Assert.Null(StartupTweaks.Set(handler, false));
        Assert.Contains(all, e => e.Target == @"\Vendor\Daily");
        Assert.DoesNotContain(all, e => e.Target == @"\Microsoft\Windows\Fixture\Daily");
        // The startup check counts only what starts with Windows.
        Assert.DoesNotContain(scanner.LogonTasks(), e => e.Target == @"\Vendor\Daily");
    }

    /// <summary>
    /// The Autoruns locations added in 0.9.30: the cmd.exe AutoRun, a changed open command of programs (the default is
    /// not listed), SilentProcessExit monitors, AppCertDlls, Explorer add-ons, sign-in providers, the account's own context
    /// menu handlers, the 32-bit IFEO key and Winlogon values Windows does not set. All are listed only, and the ones
    /// Windows never sets are marked for a look.
    /// </summary>
    [Fact]
    public void MoreAutorunsLocationsAreListed()
    {
        using var fx = new EngineFixture();
        var r = fx.Registry;
        RegistryValue.Write(r, Hive.User, @"Software\Microsoft\Command Processor", "AutoRun", "string", @"C:\Users\Public\x.cmd");
        RegistryValue.Write(r, Hive.Machine, @"SOFTWARE\Classes\exefile\shell\open\command", "", "string", "\"%1\" %*");
        RegistryValue.Write(r, Hive.Machine, @"SOFTWARE\Classes\batfile\shell\open\command", "", "string", @"C:\evil.exe ""%1"" %*");
        RegistryValue.Write(r, Hive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\notepad.exe", "MonitorProcess", "string", @"C:\evil.exe");
        RegistryValue.Write(r, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls", "x", "string", @"C:\evil.dll");
        const string clsid = "{11111111-2222-3333-4444-555555555555}";
        RegistryValue.Write(r, Hive.Machine, $@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32", "", "string", @"C:\Program Files\Vendor\overlay.dll");
        RegistryValue.Write(r, Hive.Machine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\ VendorSync", "", "string", clsid);
        RegistryValue.Write(r, Hive.Machine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{clsid}", "", "string", "Vendor sign-in");
        RegistryValue.Write(r, Hive.User, $@"Software\Classes\*\shellex\ContextMenuHandlers\VendorMenu", "", "string", clsid);
        RegistryValue.Write(r, Hive.Machine, $@"{StartupScanner.Ifeo32}\game.exe", "Debugger", "string", @"C:\evil.exe");
        RegistryValue.Write(r, Hive.Machine, StartupScanner.Winlogon, "Taskman", "string", @"C:\evil.exe");

        var all = new StartupScanner(r, fx.Tasks, null).ScanAll(includeServicesAndDrivers: false, includeWmi: false);
        StartupEntry One(StartupKind kind, Func<StartupEntry, bool>? where = null) => Assert.Single(all, e => e.Kind == kind && (where?.Invoke(e) ?? true));

        Assert.True(One(StartupKind.CommandAutoRun).Suspicious);
        var hijack = One(StartupKind.FileTypeHijack);
        Assert.Equal("batfile open", hijack.Name);
        Assert.Equal(@"C:\evil.exe", One(StartupKind.SilentProcessExit).ImagePath);
        Assert.True(One(StartupKind.AppCertDll).Suspicious);
        Assert.Equal(@"C:\Program Files\Vendor\overlay.dll", One(StartupKind.ExplorerAddOn).ImagePath);
        Assert.Equal("Vendor sign-in", One(StartupKind.CredentialProvider).Name);
        Assert.Null(One(StartupKind.ShellExtension, e => e.Hive == Hive.User).Enabled);
        Assert.Equal(@"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\game.exe", One(StartupKind.ImageHijack).Target);
        Assert.True(One(StartupKind.Winlogon, e => e.Name == "Taskman").Suspicious);
        Assert.All(all.Where(e => e.Kind is StartupKind.CommandAutoRun or StartupKind.FileTypeHijack or StartupKind.SilentProcessExit or StartupKind.AppCertDll
            or StartupKind.ExplorerAddOn or StartupKind.CredentialProvider), e => Assert.Null(e.Enabled));
    }

    /// <summary>
    /// The exported list quotes cells with separators or quotes, keeps one line per entry, and never lets a spreadsheet
    /// read a command line as a formula.
    /// </summary>
    [Fact]
    public void ExportedListIsSafeCsv()
    {
        var rows = new[]
        {
            new StartupExportRow("Vendor; Updater", "Task", "On", "Vendor \"Inc\"", "Signed", "=cmd /c calc", @"C:\x.exe", "line1\nline2"),
        };
        var csv = StartupExport.ToCsv(rows, ["Name", "Kind", "State", "Publisher", "Signature", "Command", "File", "Location"], ';');
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("\"Vendor; Updater\";Task;On;\"Vendor \"\"Inc\"\"\";Signed;'=cmd /c calc;C:\\x.exe;line1 line2", lines[1]);
        Assert.Equal(';', StartupExport.SeparatorFor(new System.Globalization.CultureInfo("de-DE")));
        Assert.Equal(',', StartupExport.SeparatorFor(new System.Globalization.CultureInfo("en-US")));
    }

    /// <summary>A variable of the user's own (inherited by this app) cannot make a script look like one from System32.</summary>
    [Fact]
    public void UserVariablesDoNotMakeAScriptTrusted()
    {
        const string name = "TUNERO_TEST_SYS32";
        Environment.SetEnvironmentVariable(name, Environment.SystemDirectory);
        try
        {
            var entry = new StartupEntry(StartupKind.RunKey, "x", $@"C:\Windows\System32\wscript.exe %{name}%\a.vbs", @"C:\Windows\System32\wscript.exe",
                @"HKCU\Run", Hive.User, true, "run:x");
            Assert.True(entry.RunsScriptHost);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void StartupFolderShortcutWithArgumentsPointsAtItsTarget()
    {
        using var fx = new EngineFixture();
        var profile = TestFolders.Create("profile");
        try
        {
            var folder = Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
            Directory.CreateDirectory(folder);
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var lnk = shell.CreateShortcut(Path.Combine(folder, "PcoTest.lnk"));
            lnk.TargetPath = cmd;
            lnk.Arguments = @"/c echo C:\Test\file.txt";
            lnk.Save();

            var entry = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, profile) { CommonStartupFolder = Path.Combine(profile, "no-startup") }.StartupFolders(), e => e.Name == "PcoTest");
            Assert.Equal(cmd, entry.ImagePath, ignoreCase: true); // was the whole command line, so the signature check said "File not found"
        }
        finally
        {
            TestFolders.Delete(profile);
        }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" -min", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe -min", @"C:\Program Files\App\app.exe")]
    [InlineData(@"rundll32.exe C:\Tools\helper.dll,Start", @"C:\Tools\helper.dll")]
    [InlineData(@"\??\C:\Drivers\x.sys", @"C:\Drivers\x.sys")]
    [InlineData(@"rundll32.exe /d C:\Tools\helper.dll,Start", @"C:\Tools\helper.dll")] // Windows' Autochk task passes /d first
    [InlineData("\"C:\\Program Files\\App\\app.exe\" C:\\Test\\file.txt", @"C:\Program Files\App\app.exe")] // Startup folder shortcut with arguments
    public void CommandLineImagePaths(string command, string expected)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\App\app.exe", @"C:\Tools\helper.dll", @"C:\Drivers\x.sys" };
        Assert.Equal(expected, CommandLine.ImagePath(command, s => s, files.Contains));
    }

    /// <summary>
    /// An unquoted path with spaces runs the shortest prefix that exists, as CreateProcess and the service manager do: a
    /// planted C:\Tools\My.exe runs instead of C:\Tools\My App\app.exe, and the scanner must show that file.
    /// </summary>
    [Fact]
    public void UnquotedPathsResolveToTheFileWindowsRuns()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools\My App\app.exe", @"C:\Tools\My.exe" };
        Assert.Equal(@"C:\Tools\My.exe", CommandLine.ImagePath(@"C:\Tools\My App\app.exe -min", s => s, files.Contains));
        files.Remove(@"C:\Tools\My.exe");
        Assert.Equal(@"C:\Tools\My App\app.exe", CommandLine.ImagePath(@"C:\Tools\My App\app.exe -min", s => s, files.Contains));
    }

    [Fact]
    public void BarePowerShellResolvesToItsSystemFolder()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var ps = Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        Assert.Equal(ps, CommandLine.ImagePath("powershell -w hidden -enc SQBFAFgA", s => s, f => f.Equals(ps, StringComparison.OrdinalIgnoreCase)), ignoreCase: true);
    }

    [Theory]
    [InlineData("Microsoft Windows", true)]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("Microsoft Windows Publisher", true)]
    [InlineData("Microsoft Windows Hardware Compatibility Publisher", false)] // signs other vendors' drivers (WHQL, attestation)
    [InlineData("Microsoft Windows Early Launch Anti-malware Publisher", false)]
    [InlineData("Microsoft Corporation Fake Ltd", false)]
    public void OnlyMicrosoftsOwnSignersCountAsMicrosoft(string publisher, bool microsoft) =>
        Assert.Equal(microsoft, new SignatureInfo(SignatureStatus.Signed, publisher, false).IsMicrosoft);

    /// <summary>Paths of services and drivers expand with the machine's variables, never with a variable the user set.</summary>
    [Fact]
    public void SystemEntriesExpandWithTheMachineEnvironment()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, @"System32\svchost.exe"), MachineEnvironment.Expand(@"%SystemRoot%\System32\svchost.exe"));
        Assert.Equal(Path.Combine(windows, @"System32\x.dll"), MachineEnvironment.Expand(@"%windir%\System32\x.dll"));
        Assert.Equal(@"%PCO_NOT_A_MACHINE_VAR%\x.exe", MachineEnvironment.Expand(@"%PCO_NOT_A_MACHINE_VAR%\x.exe"));
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"] = @"C:\Windows" };
        Assert.Equal(@"C:\Windows\TEMP", MachineEnvironment.Expand(@"%systemroot%\TEMP", vars));
    }

    /// <summary>A certificate named "Microsoft Corporation" under a root someone added is not Microsoft's.</summary>
    [Fact]
    public void TheMicrosoftNameNeedsAMicrosoftRoot()
    {
        Assert.False(new SignatureInfo(SignatureStatus.Signed, "Microsoft Corporation", false, MicrosoftRoot: false).IsMicrosoft);
        Assert.True(new SignatureInfo(SignatureStatus.Signed, "Microsoft Corporation", false, MicrosoftRoot: true).IsMicrosoft);
    }

    [Fact]
    public void VirusTotalParsingAndThresholds()
    {
        static string Json(int m, int s) =>
            "{\"data\":{\"attributes\":{\"last_analysis_stats\":{\"malicious\":" + m + ",\"suspicious\":" + s + ",\"undetected\":60,\"harmless\":0}}}}";
        Assert.Equal(VirusTotalVerdict.Clean, VirusTotalClient.Parse("x", Json(0, 0)).Verdict);
        Assert.Equal(VirusTotalVerdict.Suspicious, VirusTotalClient.Parse("x", Json(1, 0)).Verdict); // one engine: often a false positive
        var bad = VirusTotalClient.Parse("x", Json(12, 1));
        Assert.Equal((VirusTotalVerdict.Malicious, 73), (bad.Verdict, bad.Total));
        // A proxy's or captive portal's page, and JSON of another shape, are errors, not exceptions.
        Assert.Equal(VirusTotalVerdict.Error, VirusTotalClient.Parse("x", "<html>Sign in to the Wi-Fi</html>").Verdict);
        Assert.Equal(VirusTotalVerdict.Error, VirusTotalClient.Parse("x", "{\"data\":[1,2]}").Verdict);
        Assert.Equal(VirusTotalVerdict.Error, VirusTotalClient.Parse("x", "[]").Verdict);
    }

    [Fact]
    public void DpapiRoundTrip()
    {
        var protectedKey = Dpapi.Protect("secret-api-key");
        Assert.DoesNotContain("secret", protectedKey);
        Assert.Equal("secret-api-key", Dpapi.Unprotect(protectedKey));
        Assert.Null(Dpapi.Unprotect("not base64!"));
    }
}
