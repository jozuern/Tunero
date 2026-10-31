using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;

namespace Optimizer.Core.Tests;

/// <summary>The uninstall list and which uninstallers may run elevated.</summary>
public class ProgramsTests
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static void App(SandboxRegistry r, Hive hive, string key, params (string Name, string Kind, string Value)[] values)
    {
        foreach (var (name, kind, value) in values) RegistryValue.Write(r, hive, $@"{Uninstall}\{key}", name, kind, value);
    }

    [Fact]
    public void ListsWhatSettingsShowsAndSkipsComponentsAndUpdates()
    {
        using var r = new SandboxRegistry();
        App(r, Hive.Machine, "7-Zip", ("DisplayName", "string", "7-Zip 24.08 (x64)"), ("Publisher", "string", "Igor Pavlov"), ("DisplayVersion", "string", "24.08"),
            ("UninstallString", "string", @"""C:\Program Files\7-Zip\Uninstall.exe"""), ("EstimatedSize", "dword", "5800"), ("InstallDate", "string", "20260901"));
        App(r, Hive.Machine, "{11111111-2222-3333-4444-555555555555}", ("DisplayName", "string", "Contoso Tool"), ("WindowsInstaller", "dword", "1"),
            ("UninstallString", "string", "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}"));
        App(r, Hive.Machine, "Component", ("DisplayName", "string", "Hidden part"), ("SystemComponent", "dword", "1"), ("UninstallString", "string", "x.exe"));
        App(r, Hive.Machine, "KB123", ("DisplayName", "string", "Update for Contoso"), ("ParentKeyName", "string", "Contoso"));
        App(r, Hive.User, "Discord", ("DisplayName", "string", "Discord"), ("UninstallString", "string", @"""C:\Users\me\AppData\Local\Discord\Update.exe"" --uninstall"));

        var list = Programs.Read(r);
        Assert.Equal(["7-Zip 24.08 (x64)", "Contoso Tool", "Discord"], list.Select(p => p.Name));
        var zip = list[0];
        Assert.Equal(5800, zip.SizeKb);
        Assert.Equal(new DateTime(2026, 9, 1), zip.InstallDate);
        Assert.False(zip.PerUser);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", list[1].ProductCode);
        Assert.True(list[2].PerUser);

        // For the app catalog (WebView2 registers as a system component): included, marked.
        var all = Programs.Read(r, systemComponents: true);
        Assert.Equal(["7-Zip 24.08 (x64)", "Contoso Tool", "Discord", "Hidden part"], all.Select(p => p.Name));
        Assert.Equal([false, false, false, true], all.Select(p => p.SystemComponent));
    }

    /// <summary>
    /// The Apps page reads once with the system components and leaves them out for the uninstall list: a visible entry
    /// that a hidden one with the same name and version duplicates stays, and the catalog's list keeps one entry per name.
    /// </summary>
    [Fact]
    public void OneReadServesTheUninstallListAndTheAppCatalog()
    {
        using var r = new SandboxRegistry();
        App(r, Hive.Machine, "Contoso", ("DisplayName", "string", "Contoso"), ("DisplayVersion", "string", "1.0"));
        App(r, Hive.Machine, "ContosoPart", ("DisplayName", "string", "Contoso"), ("DisplayVersion", "string", "1.0"), ("SystemComponent", "dword", "1"),
            ("UninstallString", "string", "x.exe"));
        App(r, Hive.Machine, "WebView", ("DisplayName", "string", "Microsoft Edge WebView2 Runtime"), ("SystemComponent", "dword", "1"));
        App(r, Hive.User, "Discord", ("DisplayName", "string", "Discord"), ("UninstallString", "string", "u.exe"));

        var all = Programs.Read(r, systemComponents: true);
        Assert.Equal(Programs.Read(r).Select(p => p.RegistryKey), all.Where(p => !p.SystemComponent).Select(p => p.RegistryKey));
        var installed = Optimizer.Core.Hardware.Probes.InstalledPrograms.From(all);
        Assert.Equal(["Contoso", "Discord", "Microsoft Edge WebView2 Runtime"], installed.Select(p => p.Name));
        Assert.Equal([false, false, true], installed.Select(p => p.SystemComponent));
        Assert.True(installed.Single(p => p.Name == "Discord").PerUser);
    }

    /// <summary>An entry only the system can read leaves out that entry, not the whole list (and with it the app catalog's detection).</summary>
    [Fact]
    public void AnUnreadableEntryLeavesOutOnlyThatEntry()
    {
        using var r = new SandboxRegistry();
        App(r, Hive.Machine, "7-Zip", ("DisplayName", "string", "7-Zip 24.08 (x64)"), ("UninstallString", "string", @"""C:\Program Files\7-Zip\Uninstall.exe"""));
        App(r, Hive.Machine, "Locked", ("DisplayName", "string", "Locked"), ("UninstallString", "string", "x.exe"));
        App(r, Hive.User, "Discord", ("DisplayName", "string", "Discord"), ("UninstallString", "string", "u.exe"));
        // Only the sandbox key of this test is changed: reading it is denied to this account, then allowed again.
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.RegistryAccessRule(me, System.Security.AccessControl.RegistryRights.QueryValues | System.Security.AccessControl.RegistryRights.EnumerateSubKeys,
            System.Security.AccessControl.AccessControlType.Deny);
        using (var locked = r.Open(Hive.Machine, $@"{Uninstall}\Locked", writable: true)!)
        {
            var security = locked.GetAccessControl();
            security.AddAccessRule(deny);
            locked.SetAccessControl(security);
        }
        try
        {
            Assert.Equal(["7-Zip 24.08 (x64)", "Discord"], Programs.Read(r).Select(p => p.Name));
        }
        finally
        {
            using var parent = r.Open(Hive.Machine, Uninstall, writable: false)!;
            // ReadWriteSubTree: .NET writes an ACL only through a key it opened for writing.
            using var locked = parent.OpenSubKey("Locked", Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
                System.Security.AccessControl.RegistryRights.ReadPermissions | System.Security.AccessControl.RegistryRights.ChangePermissions)!;
            var security = locked.GetAccessControl();
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public void OnlyUninstallersInAdminOnlyFoldersRunElevated()
    {
        var msi = new DesktopProgram("Contoso", null, null, null, null, "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", "{11111111-2222-3333-4444-555555555555}", false, "k");
        var cmd = Programs.Command(msi, _ => false)!;
        Assert.Equal(UninstallMode.WindowsInstaller, cmd.Mode);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), cmd.File, ignoreCase: true);
        Assert.Equal("/x {11111111-2222-3333-4444-555555555555}", cmd.Arguments); // never /I from the registry

        // msiexec named in UninstallString without the WindowsInstaller flag: still System32's msiexec with /x.
        var msiString = msi with { ProductCode = null };
        Assert.Equal("/x {11111111-2222-3333-4444-555555555555}", Programs.Command(msiString, _ => false)!.Arguments);
        Assert.Null(Programs.Command(msi with { ProductCode = null, UninstallString = "MsiExec.exe /I{not-a-guid}" }, _ => true));

        var machine = new DesktopProgram("7-Zip", null, null, null, null, @"""C:\Program Files\7-Zip\Uninstall.exe"" /S", null, false, "k");
        var trusted = Programs.Command(machine, _ => true)!;
        Assert.Equal(UninstallMode.Elevated, trusted.Mode);
        Assert.Equal(@"C:\Program Files\7-Zip\Uninstall.exe", trusted.File);
        Assert.Equal("/S", trusted.Arguments);
        Assert.Equal(UninstallMode.AsUser, Programs.Command(machine, _ => false)!.Mode);

        // A per-user program never runs elevated, even from a protected folder.
        Assert.Equal(UninstallMode.AsUser, Programs.Command(machine with { PerUser = true }, _ => true)!.Mode);
        Assert.Null(Programs.Command(machine with { UninstallString = "" }, _ => true));
    }

    [Theory]
    [InlineData(@"""C:\A B\u.exe"" /x /y", @"C:\A B\u.exe", "/x /y")]
    [InlineData(@"C:\Tools\u.exe --remove", @"C:\Tools\u.exe", "--remove")]
    [InlineData(@"C:\Tools\u.exe", @"C:\Tools\u.exe", "")]
    // Unquoted path with spaces, and a path written without .exe.
    [InlineData(@"C:\Program Files\App X\unins000.exe /SILENT", @"C:\Program Files\App X\unins000.exe", "/SILENT")]
    [InlineData(@"C:\Program Files\App X\unins000 /SILENT", @"C:\Program Files\App X\unins000.exe", "/SILENT")]
    public void ArgumentsFollowThePath(string command, string exe, string expected)
    {
        var program = new DesktopProgram("App", null, null, null, null, command, null, false, "k");
        var cmd = Programs.Command(program, _ => true, f => f.Equals(@"C:\Program Files\App X\unins000.exe", StringComparison.OrdinalIgnoreCase))!;
        Assert.Equal(exe, cmd.File);
        Assert.Equal(expected, cmd.Arguments);
    }

    /// <summary>A planted HKCU entry must not make an elevated msiexec remove a program installed for the whole PC.</summary>
    [Fact]
    public void PerUserWindowsInstallerEntriesRunAsTheUser()
    {
        var msi = new DesktopProgram("Contoso", null, null, null, null, "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "{11111111-2222-3333-4444-555555555555}", true, "k");
        Assert.Equal(UninstallMode.AsUser, Programs.Command(msi, _ => true)!.Mode);
        Assert.Equal(UninstallMode.AsUser, Programs.Command(msi with { ProductCode = null }, _ => true)!.Mode);
    }

    /// <summary>rundll32 lines run rundll32 with the DLL as argument; elevated only when the DLL is admin-only too.</summary>
    [Fact]
    public void RundllUninstallersRunRundllWithTheirArguments()
    {
        var line = @"RunDll32 C:\PROGRA~2\COMMON~1\INSTAL~1\Ctor.dll,LaunchSetup ""C:\Program Files (x86)\App\setup.exe"" -removeonly";
        var program = new DesktopProgram("Old app", null, null, null, null, line, null, false, "k");
        var rundll = Path.Combine(Environment.SystemDirectory, "RunDll32.exe");
        var cmd = Programs.Command(program, _ => true)!;
        Assert.Equal(rundll, cmd.File, ignoreCase: true);
        Assert.Equal(@"C:\PROGRA~2\COMMON~1\INSTAL~1\Ctor.dll,LaunchSetup ""C:\Program Files (x86)\App\setup.exe"" -removeonly", cmd.Arguments);
        Assert.Equal(UninstallMode.Elevated, cmd.Mode);
        // The DLL in a user-writable folder: not elevated, although rundll32 itself is in System32.
        Assert.Equal(UninstallMode.AsUser, Programs.Command(program, f => f.Equals(rundll, StringComparison.OrdinalIgnoreCase))!.Mode);
    }

    /// <summary>
    /// cmd.exe is admin-only, but the script it runs decides what happens: a script a vendor left user-writable, or
    /// inline code, must not run elevated.
    /// </summary>
    [Fact]
    public void ScriptUninstallersRunElevatedOnlyWithAdminOnlyScripts()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var program = new DesktopProgram("Vendor", null, null, null, null, $@"{cmd} /c ""C:\ProgramData\Vendor\uninstall.cmd""", null, false, "k");
        Assert.Equal(UninstallMode.Elevated, Programs.Command(program, _ => true)!.Mode);
        Assert.Equal(UninstallMode.AsUser, Programs.Command(program, f => !f.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase))!.Mode);
        Assert.Equal(UninstallMode.AsUser, Programs.Command(program with { UninstallString = $"{cmd} /c del /q x" }, _ => true)!.Mode);
    }

    /// <summary>An interpreter another program installed runs the script it is given, like a script host.</summary>
    [Fact]
    public void InterpreterUninstallersRunElevatedOnlyWithAdminOnlyScripts()
    {
        static bool AdminOnly(string f) => f.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase);
        var python = new DesktopProgram("Tool", null, null, null, null, @"""C:\Program Files\Python\python.exe"" ""C:\Users\Public\u.py""", null, false, "k");
        Assert.Equal(UninstallMode.AsUser, Programs.Command(python, AdminOnly)!.Mode);
        Assert.Equal(UninstallMode.Elevated, Programs.Command(python with { UninstallString = @"""C:\Program Files\Python\python.exe"" ""C:\Program Files\Tool\u.py""" }, AdminOnly)!.Mode);
        Assert.Equal(UninstallMode.AsUser, Programs.Command(python with { UninstallString = @"""C:\Program Files\Python\python.exe"" -c ""import os""" }, AdminOnly)!.Mode);
    }

    /// <summary>
    /// cmd.exe runs the signed-in user's AutoRun command first (HKCU, writable by any program of the user): every cmd.exe
    /// the elevated app starts gets /D, and a batch file is started through cmd.exe /D explicitly.
    /// </summary>
    [Fact]
    public void ElevatedCmdNeverRunsTheUsersAutoRun()
    {
        var cmd = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), @"/c ""C:\Program Files\Vendor\u.cmd""") { UseShellExecute = false };
        ProcessHardening.Apply(cmd);
        Assert.Equal(@"/D /c ""C:\Program Files\Vendor\u.cmd""", cmd.Arguments);
        ProcessHardening.Apply(cmd);
        Assert.Equal(@"/D /c ""C:\Program Files\Vendor\u.cmd""", cmd.Arguments); // not twice

        var list = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false };
        list.ArgumentList.Add("/c");
        list.ArgumentList.Add("ver");
        ProcessHardening.Apply(list);
        Assert.Equal(["/D", "/c", "ver"], list.ArgumentList);

        var batch = new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\Vendor\uninstall.bat", "/quiet") { UseShellExecute = false };
        ProcessHardening.Apply(batch);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), batch.FileName);
        Assert.Equal(@"/D /S /C """"C:\Program Files\Vendor\uninstall.bat"" /quiet""", batch.Arguments);

        var bare = new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\Vendor\remove.CMD") { UseShellExecute = false };
        ProcessHardening.Apply(bare);
        Assert.Equal(@"/D /S /C """"C:\Program Files\Vendor\remove.CMD""""", bare.Arguments);
    }

    /// <summary>A one-character token (a lone backslash) must not crash the parser: a planted Run value would break the Startup page.</summary>
    [Theory]
    [InlineData(@"cmd.exe /c \")]
    [InlineData(@"explorer.exe \")]
    [InlineData(@"rundll32 x.dll,E \")]
    public void ShortTokensDoNotThrow(string command)
    {
        _ = CommandLine.RunsUnverifiedScript(command);
        _ = CommandLine.RunsUntrustedCode(command, _ => true);
        Assert.False(CommandLine.IsInProtectedSystemFolder(@"\"));
    }

    [Fact]
    public void System32IsTrustedAndTempIsNot()
    {
        Assert.True(TrustedPath.IsAdminOnlyWritable(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.True(TrustedPath.IsAdminOnlyBelowSharedFolders(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        var folder = TestFolders.Create("trusted");
        try
        {
            var file = Path.Combine(folder, "uninstall.exe");
            File.WriteAllText(file, "x");
            Assert.False(TrustedPath.IsAdminOnlyWritable(file));
            Assert.False(TrustedPath.IsAdminOnlyWritable(Path.Combine(folder, "missing.exe")));
            // Folders above may let users add entries, the program's own folder may not.
            Assert.False(TrustedPath.IsAdminOnlyBelowSharedFolders(file));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>On a share the server reports owner and permissions: never trusted, whatever it claims.</summary>
    [Theory]
    [InlineData(@"\\server\share\app\uninstall.exe")]
    [InlineData(@"\\?\UNC\server\share\uninstall.exe")]
    [InlineData(@"\\localhost\C$\Windows\System32\notepad.exe")]
    public void NetworkPathsAreNeverTrusted(string path)
    {
        Assert.False(TrustedPath.IsOnLocalFixedDrive(path));
        Assert.False(TrustedPath.IsAdminOnlyWritable(path));
        Assert.False(TrustedPath.IsAdminOnlyBelowSharedFolders(path));
    }
}
