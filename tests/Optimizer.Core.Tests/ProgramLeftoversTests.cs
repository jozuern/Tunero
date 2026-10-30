using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Tools;

namespace Optimizer.Core.Tests;

/// <summary>Quiet uninstalls and what an uninstalled program left behind (folders and shortcuts, moved to the Recycle Bin after review).</summary>
public class ProgramLeftoversTests : IDisposable
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Code = "{11111111-2222-3333-4444-555555555555}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pco-test-" + Guid.NewGuid().ToString("N"));

    public ProgramLeftoversTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFolders.Delete(_root);

    private static DesktopProgram Program(string? uninstall, string? quiet = null, string? productCode = null, bool perUser = false, string? location = null, string? icon = null) =>
        new("App", null, null, null, null, uninstall, productCode, perUser, "k", quiet, location, icon);

    [Fact]
    public void WindowsInstallerProductsWithoutALocationGetTheFolderOfTheirFiles()
    {
        // 7-Zip 25.01's MSI: its packed code as Windows Installer writes it (read in the VM).
        const string sevenZip = "{23170F69-40C1-2702-2501-000001000000}";
        Assert.Equal("96F071321C0420725210000010000000", ProgramLeftovers.PackedGuid(sevenZip));
        Assert.Null(ProgramLeftovers.PackedGuid("not a guid"));

        using var registry = new SandboxRegistry();
        const string components = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components";
        void Component(string name, string value, string path) =>
            RegistryValue.Write(registry, Hive.Machine, $@"{components}\{name}", value, "string", path);
        var packed = ProgramLeftovers.PackedGuid(sevenZip)!;
        Component("A1", packed, @"C:\Program Files\7-Zip\7z.exe");
        Component("A2", packed, @"C:\Program Files\7-Zip\Lang\en.ttt");
        Component("A3", packed, @"C:\Program Files\7-Zip\");
        Component("A4", packed, @"21:\Software\7-Zip\Path"); // a registry component: not a folder
        Component("A5", ProgramLeftovers.PackedGuid(Code)!, @"C:\Program Files\Other\other.exe");
        Component("A6", ProgramLeftovers.PackedGuid(Code)!, @"C:\Windows\System32\other.dll"); // no common folder below the drive
        var found = ProgramLeftovers.MsiFolders(registry, [sevenZip, Code]);
        Assert.Equal(@"C:\Program Files\7-Zip", found[sevenZip]);
        Assert.False(found.ContainsKey(Code));

        Assert.Equal(@"C:\Program Files\App", ProgramLeftovers.CommonFolder([@"C:\Program Files\App\bin", @"c:\program files\app"]));
        Assert.Null(ProgramLeftovers.CommonFolder([]));
    }

    /// <summary>
    /// After A is uninstalled, its folder may hold B, a Windows Installer product still installed that records no
    /// location: B's files keep the folder from being offered.
    /// </summary>
    [Fact]
    public void AFolderWithFilesOfAnInstalledWindowsInstallerProductIsNotOffered()
    {
        using var registry = new SandboxRegistry();
        RegistryValue.Write(registry, Hive.Machine, $@"{Uninstall}\{Code}", "DisplayName", "string", "B");
        RegistryValue.Write(registry, Hive.Machine, $@"{Uninstall}\{Code}", "UninstallString", "string", $"MsiExec.exe /X{Code}");
        RegistryValue.Write(registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components\B1",
            ProgramLeftovers.PackedGuid(Code)!, "string", @"C:\Program Files\Vendor\Tools\b.exe");
        var roots = new LeftoverRoots([@"C:\Program Files"], [], []);

        Assert.True(ProgramLeftovers.MayOffer(@"C:\Program Files\Vendor", roots, Programs.ReferencedPaths(registry)));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Program Files\Vendor", roots, ProgramOrphans.ReadEvidence(registry, []).Paths));
    }

    /// <summary>
    /// Installers write uninstall strings with 8.3 short names (C:\PROGRA~2\...): such a reference still keeps its long
    /// named folder from being offered. Skipped on a volume without short names.
    /// </summary>
    [Fact]
    public void AShortNameReferenceKeepsItsFolder()
    {
        var vendor = Path.Combine(_root, "Long Vendor Name");
        var uninstaller = Path.Combine(vendor, "Product B", "uninstall.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(uninstaller)!);
        File.WriteAllBytes(uninstaller, []);
        var buffer = new char[1024];
        var length = GetShortPathNameW(uninstaller, buffer, (uint)buffer.Length);
        var shortName = new string(buffer, 0, (int)length);
        if (length == 0 || !shortName.Contains('~')) return;

        using var registry = new SandboxRegistry();
        RegistryValue.Write(registry, Hive.Machine, $@"{Uninstall}\B", "DisplayName", "string", "B");
        RegistryValue.Write(registry, Hive.Machine, $@"{Uninstall}\B", "UninstallString", "string", $"\"{shortName}\" /S");
        var roots = new LeftoverRoots([_root], [], []);

        Assert.False(ProgramLeftovers.MayOffer(vendor, roots, Programs.ReferencedPaths(registry)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint length);

    [Fact]
    public void FootprintCountsTheFilesThatAreStillThere()
    {
        var app = Path.Combine(_root, "App");
        Directory.CreateDirectory(Path.Combine(app, "sub"));
        File.WriteAllText(Path.Combine(app, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(app, "sub", "b.txt"), "123");
        Assert.Equal((2, 8L), ProgramLeftovers.Footprint([app, Path.Combine(_root, "missing")]));
    }

    [Fact]
    public void QuietUninstallUsesWindowsInstallerOrTheProgramsOwnQuietCommand()
    {
        var msi = Program($"MsiExec.exe /I{Code}", productCode: Code);
        var cmd = Programs.QuietCommand(msi, _ => false)!;
        Assert.Equal(UninstallMode.WindowsInstaller, cmd.Mode);
        Assert.Equal($"/x {Code} /qn /norestart", cmd.Arguments);
        // The flag missing but msiexec named in the line: still quiet, and a per-user entry still runs as the user.
        Assert.Equal($"/x {Code} /qn /norestart", Programs.QuietCommand(msi with { ProductCode = null }, _ => false)!.Arguments);
        Assert.Equal(UninstallMode.AsUser, Programs.QuietCommand(msi with { PerUser = true }, _ => true)!.Mode);

        var inno = Program(@"""C:\Program Files\App\unins000.exe""", @"""C:\Program Files\App\unins000.exe"" /SILENT");
        var quiet = Programs.QuietCommand(inno, _ => true)!;
        Assert.Equal(UninstallMode.Elevated, quiet.Mode);
        Assert.Equal(@"C:\Program Files\App\unins000.exe", quiet.File);
        Assert.Equal("/SILENT", quiet.Arguments);
        // The quiet line follows the same rules: a user-writable uninstaller runs as the user.
        Assert.Equal(UninstallMode.AsUser, Programs.QuietCommand(inno, _ => false)!.Mode);
        // No quiet command: null, the bulk run falls back to the uninstaller's window.
        Assert.Null(Programs.QuietCommand(inno with { QuietUninstallString = null }, _ => true));
        // A quiet line that names msiexec gets the remove action of System32's msiexec, quiet.
        Assert.Equal($"/x {Code} /qn /norestart", Programs.QuietCommand(inno with { QuietUninstallString = $"MsiExec.exe /X{Code}" }, _ => true)!.Arguments);

        Assert.True(Programs.Succeeded(0) && Programs.Succeeded(3010) && Programs.Succeeded(1641));
        Assert.False(Programs.Succeeded(1602));
    }

    [Fact]
    public void ReadsTheQuietCommandFolderAndIconAndFindsTheKeyAgain()
    {
        using var r = new SandboxRegistry();
        void Value(Hive hive, string key, string name, string kind, string value) => RegistryValue.Write(r, hive, $@"{Uninstall}\{key}", name, kind, value);
        Value(Hive.Machine, "App", "DisplayName", "string", "App");
        Value(Hive.Machine, "App", "UninstallString", "string", @"""C:\Program Files\App\unins000.exe""");
        Value(Hive.Machine, "App", "QuietUninstallString", "string", @"""C:\Program Files\App\unins000.exe"" /SILENT");
        Value(Hive.Machine, "App", "InstallLocation", "string", @"C:\Program Files\App\");
        Value(Hive.Machine, "App", "DisplayIcon", "string", @"C:\Program Files\App\app.exe,0");
        // A hidden component and a service: not in the list, but their folders are still in use.
        Value(Hive.Machine, "Part", "DisplayName", "string", "Hidden part");
        Value(Hive.Machine, "Part", "SystemComponent", "dword", "1");
        Value(Hive.Machine, "Part", "InstallLocation", "string", @"C:\Program Files\Vendor\Part");
        RegistryValue.Write(r, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\VendorSvc", "ImagePath", "string", @"""C:\Program Files\Vendor\Service\svc.exe"" -run");

        var app = Assert.Single(Programs.Read(r));
        Assert.Equal(@"""C:\Program Files\App\unins000.exe"" /SILENT", app.QuietUninstallString);
        Assert.Equal(@"C:\Program Files\App\", app.InstallLocation);
        Assert.Equal(@"C:\Program Files\App\app.exe,0", app.DisplayIcon);
        Assert.True(Programs.StillListed(r, app));
        Assert.False(Programs.StillListed(r, app with { RegistryKey = $@"HKLM\{Uninstall}\Gone" }));

        var referenced = Programs.ReferencedPaths(r);
        Assert.Contains(@"C:\Program Files\Vendor\Part", referenced);
        Assert.Contains(@"C:\Program Files\Vendor\Service\svc.exe", referenced);
        Assert.Contains(@"C:\Program Files\App\unins000.exe", referenced);
    }

    [Fact]
    public void ProgramFoldersComeFromLocationUninstallerAndIcon()
    {
        var p = Program(@"""C:\Program Files\App\Uninstall\unins000.exe""", location: @"""C:\Program Files\App\""", icon: @"""C:\Program Files\App\bin\app.exe"",0");
        Assert.Equal([@"C:\Program Files\App", @"C:\Program Files\App\Uninstall", @"C:\Program Files\App\bin"], ProgramLeftovers.Folders(p, _ => false));
        // msiexec and rundll32 live in Windows: no folder from them, and an icon in the Installer cache says nothing either.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Empty(ProgramLeftovers.Folders(Program($"MsiExec.exe /X{Code}", productCode: Code, icon: $@"{windows}\Installer\{Code}\icon.ico"), _ => false));
        Assert.Empty(ProgramLeftovers.Folders(Program(@"RunDll32 C:\PROGRA~2\COMMON~1\INSTAL~1\Ctor.dll,LaunchSetup"), _ => false));
    }

    [Fact]
    public void OnlyFoldersOfThisProgramBelowAProgramFolderAreOffered()
    {
        var roots = new LeftoverRoots([@"C:\Program Files", @"C:\Users\me\AppData\Local"], [], [@"C:\Tools\Tunero"]);
        Assert.True(ProgramLeftovers.MayOffer(@"C:\Program Files\App", roots, []));
        Assert.True(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData\Local\Programs\App", roots, []));
        Assert.True(ProgramLeftovers.MayOffer(@"C:\Program Files\Common Files\Vendor", roots, []));
        // A program folder itself, a folder outside them and the first level of a shared folder: never.
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Program Files", roots, []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData", roots, []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Games\App", roots, []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Program Files\Common Files", roots, []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData\Local\Programs", roots, []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData\Local\Microsoft", roots, []));
        // Shared folders deeper down (Store app aliases, winget's links): only what is inside them.
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps", roots, []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links", roots, []));
        Assert.True(ProgramLeftovers.MayOffer(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Tool_abc", roots, []));
        // Something still installed inside it, or a still installed program whose folder contains it.
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Program Files\Vendor", roots, [@"C:\Program Files\Vendor\Service\svc.exe"]));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Program Files\Vendor\Tool", roots, [@"C:\Program Files\Vendor"]));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Program Files\App", roots, [@"C:\Program Files\App"]));
        // An entry that names Program Files itself (some installers write that) does not block every folder in it.
        Assert.True(ProgramLeftovers.MayOffer(@"C:\Program Files\App", roots, [@"C:\Program Files", @"C:\"]));
        // This app's own folder, and anything around it.
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Tools\Tunero", new LeftoverRoots([@"C:\Tools"], [], [@"C:\Tools\Tunero"]), []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Tools\Tunero\sub", new LeftoverRoots([@"C:\Tools"], [], [@"C:\Tools\Tunero"]), []));
        Assert.False(ProgramLeftovers.MayOffer(@"C:\Tools\Group", new LeftoverRoots([@"C:\Tools"], [], [@"C:\Tools\Group\Tunero"]), []));
    }

    [Fact]
    public void FindsTheLeftFolderAndShortcutsIntoIt()
    {
        var programs = Path.Combine(_root, "Program Files");
        var app = Path.Combine(programs, "App");
        Directory.CreateDirectory(Path.Combine(app, "bin"));
        File.WriteAllBytes(Path.Combine(app, "bin", "app.dll"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(app, "settings.ini"), new byte[24]);
        var start = Path.Combine(_root, "Start Menu");
        Directory.CreateDirectory(Path.Combine(start, "App"));
        var appLink = Path.Combine(start, "App", "App.lnk");
        var otherLink = Path.Combine(start, "Other.lnk");
        File.WriteAllText(appLink, "");
        File.WriteAllText(otherLink, "");
        var targets = new Dictionary<string, string> { [appLink] = Path.Combine(app, "bin", "app.exe"), [otherLink] = Path.Combine(programs, "Other", "other.exe") };

        var roots = new LeftoverRoots([programs], [start], []);
        var removed = Program($@"""{app}\bin\unins000.exe""", location: app);
        var found = ProgramLeftovers.Find(removed, roots, [], l => targets.GetValueOrDefault(l));
        // The bin folder is inside the program folder: moved with it, not offered on its own.
        Assert.Equal([(LeftoverKind.Folder, app, 1024L), (LeftoverKind.Shortcut, appLink, 0L)], found.Select(l => (l.Kind, l.Path, l.Bytes)));

        // Gone already: only the shortcut is left. Still used by another entry: nothing.
        Assert.Equal([LeftoverKind.Shortcut], ProgramLeftovers.Find(removed with { InstallLocation = Path.Combine(programs, "Gone"), UninstallString = null },
            roots, [], l => l == appLink ? Path.Combine(programs, "Gone", "app.exe") : null).Select(l => l.Kind));
        Assert.Empty(ProgramLeftovers.Find(removed, roots, [Path.Combine(app, "bin", "helper.exe")], l => targets.GetValueOrDefault(l)));
    }

    /// <summary>A leftover folder swapped for a junction after the scan: refused, nothing reaches the Recycle Bin.</summary>
    [Fact]
    public void RecyclingRefusesALeftoverThatBecameALink()
    {
        var victim = Path.Combine(_root, "protected");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "x");
        var leftover = Path.Combine(_root, "App");
        TestFolders.Junction(leftover, victim);
        Assert.False(StorageAnalyzer.RecycleLeftover(leftover));
        Assert.False(StorageAnalyzer.RecycleLeftover(Path.Combine(leftover, "keep.txt")));
        Assert.False(StorageAnalyzer.RecycleLeftover(Path.Combine(_root, "missing")));
        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")));
    }
}
