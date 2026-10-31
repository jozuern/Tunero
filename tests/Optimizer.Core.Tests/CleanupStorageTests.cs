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
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

public class CleanupStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pco-test-" + Guid.NewGuid().ToString("N"));

    public CleanupStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFolders.Delete(_root);

    /// <summary>The Xbox library is a real folder named in .GamingRoot (was "D:\ (Xbox)", which no file path starts with).</summary>
    [Fact]
    public void XboxLibrariesAreRealFolders()
    {
        Assert.Empty(Hardware.Probes.SoftwareProbe.XboxLibraries(_root));
        Directory.CreateDirectory(Path.Combine(_root, "Games", "Xbox"));
        byte[] marker = [.. "RGBX"u8, 1, 0, 0, 0, .. System.Text.Encoding.Unicode.GetBytes("Games\\Xbox\0")];
        System.IO.File.WriteAllBytes(Path.Combine(_root, ".GamingRoot"), marker);
        Assert.Equal([Path.Combine(_root, "Games", "Xbox")], Hardware.Probes.SoftwareProbe.XboxLibraries(_root));

        // A marker this version cannot read: the default folder name.
        System.IO.File.WriteAllBytes(Path.Combine(_root, ".GamingRoot"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_root, "XboxGames"));
        Assert.Equal([Path.Combine(_root, "XboxGames")], Hardware.Probes.SoftwareProbe.XboxLibraries(_root));
    }

    private string File(string relative, int bytes, DateTime? written = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        if (written is { } w)
        {
            System.IO.File.SetLastWriteTimeUtc(path, w);
            System.IO.File.SetCreationTimeUtc(path, w);
        }
        return path;
    }

    [Fact]
    public void CleanupNeverFollowsJunctionsAndRespectsAge()
    {
        var outside = Path.Combine(_root, "outside");
        var keep = File(@"outside\precious.txt", 10, DateTime.UtcNow.AddDays(-30));
        var temp = Path.Combine(_root, "temp");
        var old = File(@"temp\old.tmp", 100, DateTime.UtcNow.AddDays(-3));
        var fresh = File(@"temp\fresh.tmp", 100);
        File(@"temp\sub\old2.tmp", 50, DateTime.UtcNow.AddDays(-3));
        Directory.SetCreationTimeUtc(Path.Combine(temp, "sub"), DateTime.UtcNow.AddDays(-3));
        // An installer's work folder made a moment ago and still empty.
        var justMade = Path.Combine(temp, "setup123");
        Directory.CreateDirectory(justMade);
        var ini = File(@"temp\desktop.ini", 5, DateTime.UtcNow.AddDays(-3));
        // Junction inside the cleaned folder pointing outside (no admin rights needed for junctions).
        var junction = Path.Combine(temp, "link");
        Junction(junction, outside);

        var category = new CleanupCategory("cleanup.test", [temp], MinAge: CleanupEngine.TempMinAge);
        var scan = CleanupEngine.Scan(category);
        Assert.Equal((2, 150L), (scan.Files, scan.Bytes));
        var result = CleanupEngine.Clean(category);
        Assert.Equal(2, result.Deleted);
        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(fresh));
        Assert.True(System.IO.File.Exists(ini));
        Assert.True(System.IO.File.Exists(keep));        // never reached through the junction
        Assert.False(Directory.Exists(Path.Combine(temp, "sub"))); // emptied subfolder removed
        Assert.True(Directory.Exists(justMade));         // younger than MinAge: kept like a young file
        Assert.True(Directory.Exists(temp));             // root kept
    }

    private static void Junction(string link, string target) => TestFolders.Junction(link, target);

    [Fact]
    public void CleanupSkipsARootThatIsOrLiesUnderAJunction()
    {
        // %LOCALAPPDATA%\Temp (or a parent like %LOCALAPPDATA%\NVIDIA) replaced by a junction to a protected folder.
        var keep = File(@"protected\program.dll", 10, DateTime.UtcNow.AddDays(-30));
        var keep2 = File(@"protected\DXCache\shader.bin", 10, DateTime.UtcNow.AddDays(-30));
        var temp = Path.Combine(_root, "Temp");
        Junction(temp, Path.Combine(_root, "protected"));
        var nvidia = Path.Combine(_root, "NVIDIA");
        Junction(nvidia, Path.Combine(_root, "protected"));

        var category = new CleanupCategory("cleanup.test", [temp, Path.Combine(nvidia, "DXCache")]);
        Assert.Equal(0, CleanupEngine.Scan(category).Files);
        Assert.Equal(0, CleanupEngine.Clean(category).Deleted);
        Assert.True(System.IO.File.Exists(keep));
        Assert.True(System.IO.File.Exists(keep2));
    }

    [Fact]
    public void BackupFolderLinksAreRemovedWithoutTouchingTargets()
    {
        // A standard user pre-created the backup root (or a subfolder) as a junction to a folder they control.
        var userFiles = Path.Combine(_root, "user");
        var planted = File(@"user\backups\x.json", 10);
        var root = Path.Combine(_root, "Tunero");
        Junction(root, userFiles);
        Optimizer.Core.Backup.SecureFolder.RemoveLinks(root);
        Assert.False(Directory.Exists(root));
        Assert.True(System.IO.File.Exists(planted)); // the target is left alone

        Directory.CreateDirectory(root);
        Junction(Path.Combine(root, "backups"), Path.Combine(userFiles, "backups"));
        Optimizer.Core.Backup.SecureFolder.RemoveLinks(root);
        Assert.False(Directory.Exists(Path.Combine(root, "backups")));
        Assert.True(System.IO.File.Exists(planted));
    }

    [Fact]
    public void SafeDeleteRefusesAPathThatResolvesElsewhere()
    {
        // The scan saw temp\sub\old.tmp; then sub was swapped for a junction to a protected folder with the same file name.
        var victim = File(@"protected\old.tmp", 10);
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
        var sub = Path.Combine(_root, @"temp\sub");
        Junction(sub, Path.Combine(_root, "protected"));
        Assert.Throws<IOException>(() => Optimizer.Core.Platform.SafeDelete.DeleteFile(Path.Combine(sub, "old.tmp")));
        Assert.True(System.IO.File.Exists(victim));
        Assert.False(Optimizer.Core.Platform.SafeDelete.HasNoLinks(Path.Combine(sub, "old.tmp")));
        Assert.False(Optimizer.Core.Platform.SafeDelete.ResolvesToItself(Path.Combine(sub, "old.tmp")));
        Assert.True(Optimizer.Core.Platform.SafeDelete.ResolvesToItself(victim));
        // The storage analyzer refuses to recycle it as well: AppData (where %TEMP% lies) is not protected here, so the
        // link is the only reason left (ResolvesToItself fails before the shell call), and nothing reaches the Recycle
        // Bin; the junction itself, as a folder, fails HasNoLinks there the same way.
        Assert.Equal([Path.Combine(sub, "old.tmp"), sub],
            StorageAnalyzer.RecycleItems([(Path.Combine(sub, "old.tmp"), false), (sub, true)], [], [], appData: false));
        Assert.True(System.IO.File.Exists(victim));
        Assert.Null(Optimizer.Core.Platform.SafeDelete.Pin(sub));

        // A normal read-only file is deleted.
        var ro = File(@"temp\readonly.tmp", 10);
        System.IO.File.SetAttributes(ro, FileAttributes.ReadOnly);
        Optimizer.Core.Platform.SafeDelete.DeleteFile(ro);
        Assert.False(System.IO.File.Exists(ro));
    }

    /// <summary>The folders an update repair renamed are offered after a week and removed whole; other folders are not touched.</summary>
    [Fact]
    public void UpdateRepairLeftoversAreRemovedWhole()
    {
        var win = Path.Combine(_root, "Windows");
        File(@"Windows\SoftwareDistribution.old-20260901-120000\Download\a.cab", 100);
        File(@"Windows\System32\catroot2.old-20260901-120000\edb.log", 50);
        File(@"Windows\SoftwareDistribution.old-20261010-120000\x.bin", 10); // a day old: kept
        File(@"Windows\SoftwareDistribution\keep.bin", 10);
        var now = new DateTime(2026, 10, 11);
        var found = CleanupEngine.UpdateRepairLeftovers(win, now.AddDays(-CleanupEngine.RepairLeftoverDays));
        Assert.Equal(2, found.Count);
        var category = Assert.Single(CleanupEngine.Categories(null, null, win, now), c => c.Id == "cleanup.updateRepairLeftovers");
        Assert.False(category.DefaultSelected);
        CleanupEngine.Clean(category);
        Assert.False(Directory.Exists(Path.Combine(win, "SoftwareDistribution.old-20260901-120000")));
        Assert.False(Directory.Exists(Path.Combine(win, @"System32\catroot2.old-20260901-120000")));
        Assert.True(Directory.Exists(Path.Combine(win, "SoftwareDistribution.old-20261010-120000")));
        Assert.True(System.IO.File.Exists(Path.Combine(win, @"SoftwareDistribution\keep.bin")));
        Assert.True(Labels.Current.Has("en", category.Id) && Labels.Current.Has("de", category.WarningKey!));
    }

    /// <summary>A selection that covers every copy of a duplicate group (the first copy picked under the largest files) is caught.</summary>
    [Fact]
    public void ASelectionMustKeepOneCopyOfEachDuplicateGroup()
    {
        var group = new DuplicateGroup(10, [@"C:\a\x.bin", @"C:\b\x.bin"]);
        Assert.Empty(StorageAnalyzer.WholeGroupsSelected([@"C:\b\x.bin"], [group]));
        Assert.Single(StorageAnalyzer.WholeGroupsSelected([@"c:\A\x.bin", @"C:\b\x.bin"], [group]));
    }

    /// <summary>
    /// A held folder cannot be renamed or replaced (so a path handed to the shell keeps its meaning), while files in it can
    /// still be created and moved out.
    /// </summary>
    [Fact]
    public void PinnedFoldersCannotBeSwapped()
    {
        var folder = Path.Combine(_root, @"downloads\x");
        var file = File(@"downloads\x\big.bin", 10);
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        using (var pin = Optimizer.Core.Platform.SafeDelete.Pin(folder))
        {
            Assert.NotNull(pin);
            Assert.ThrowsAny<IOException>(() => Directory.Move(folder, folder + "-renamed"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(_root, "downloads"), Path.Combine(_root, "downloads-renamed")));
            System.IO.File.Move(file, Path.Combine(_root, @"elsewhere\big.bin"));
            System.IO.File.WriteAllText(Path.Combine(folder, "new.txt"), "x");
            Assert.True(pin!.StillReal());
        }
        Directory.Move(folder, folder + "-renamed");
    }

    /// <summary>A new file in a user folder is created only at its own path: an existing name or a swapped folder is refused.</summary>
    [Fact]
    public void CreateNewRefusesExistingNamesAndLinkedFolders()
    {
        var target = Path.Combine(_root, @"start\Tunero.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var s = Optimizer.Core.Platform.SafeDelete.CreateNew(target)) s.Write([1, 2, 3]);
        Assert.Equal([1, 2, 3], System.IO.File.ReadAllBytes(target));
        Assert.ThrowsAny<IOException>(() => Optimizer.Core.Platform.SafeDelete.CreateNew(target).Dispose());

        var protectedDir = Path.Combine(_root, "protected");
        Directory.CreateDirectory(protectedDir);
        Junction(Path.Combine(_root, "swapped"), protectedDir);
        Assert.ThrowsAny<IOException>(() => Optimizer.Core.Platform.SafeDelete.CreateNew(Path.Combine(_root, @"swapped\Tunero.lnk")).Dispose());
        Assert.Empty(Directory.GetFiles(protectedDir));
    }

    /// <summary>
    /// An export replaces the file it was saved over, with or without a byte order mark, and is refused when a folder on
    /// its path was swapped for a link after the dialog: nothing lands where the link points.
    /// </summary>
    [Fact]
    public void AnExportReplacesItsFileButNotThroughALinkedFolder()
    {
        var export = Path.Combine(_root, @"exports\changes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        System.IO.File.WriteAllText(export, "an older, longer export");
        Optimizer.Core.Platform.SafeDelete.WriteReplacing(export, "new");
        Assert.Equal("new"u8.ToArray(), System.IO.File.ReadAllBytes(export));
        Optimizer.Core.Platform.SafeDelete.WriteReplacing(export, "csv", byteOrderMark: true);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'c', (byte)'s', (byte)'v'], System.IO.File.ReadAllBytes(export));

        var protectedDir = Path.Combine(_root, "protected");
        Directory.CreateDirectory(protectedDir);
        Junction(Path.Combine(_root, "swapped"), protectedDir);
        Assert.ThrowsAny<IOException>(() => Optimizer.Core.Platform.SafeDelete.WriteReplacing(Path.Combine(_root, @"swapped\changes.json"), "x"));
        Assert.Empty(Directory.GetFiles(protectedDir));
    }

    [Fact]
    public async Task StorageAnalyzerFindsDuplicatesInTheTree()
    {
        var a = File(@"docs\a.bin", 2 << 20);
        var b = File(@"backup\a-copy.bin", 2 << 20);
        var different = Path.Combine(_root, @"docs\different.bin");
        System.IO.File.WriteAllBytes(different, [.. new byte[(2 << 20) - 1], 1]);
        File(@"docs\small.txt", 100);
        File(@"small-copy\small.txt", 100);
        File(@"games\a.bin", 2 << 20);
        var tree = await new Optimizer.Core.Storage.EnumerationScanner().ScanAsync(_root, null, CancellationToken.None);
        Assert.Equal(6, tree.Files(tree.Root));
        // The test folder lies in %TEMP% under AppData, which the analyzer protects; that rule is checked below. The
        // game library is protected, and files under 1 MB are no candidates.
        var candidates = StorageAnalyzer.DuplicateCandidates(tree, StorageAnalyzer.ProtectedRoots([Path.Combine(_root, "games")]), appData: false);
        var same = Assert.Single(candidates);
        Assert.Equal(new[] { a, b, different }.Order(StringComparer.OrdinalIgnoreCase), same.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(StorageAnalyzer.DuplicateCandidates(tree, StorageAnalyzer.ProtectedRoots(null)));
        var dup = Assert.Single(StorageAnalyzer.FindDuplicates(candidates, CancellationToken.None));
        Assert.Equal(new[] { b, a }.Order(StringComparer.OrdinalIgnoreCase), dup.Paths);
        Assert.Equal(2L << 20, dup.Reclaimable);
        Assert.True(StorageAnalyzer.IsProtected(@"C:\Windows\System32\x.dll", StorageAnalyzer.ProtectedRoots(null)));
        Assert.True(StorageAnalyzer.IsProtected(@"C:\Users\x\AppData\Local\y.db", StorageAnalyzer.ProtectedRoots(null)));
        Assert.True(StorageAnalyzer.IsProtected(a, StorageAnalyzer.ProtectedRoots(null)));
        Assert.False(StorageAnalyzer.IsProtected(a, StorageAnalyzer.ProtectedRoots(null), appData: false));
        Assert.True(StorageAnalyzer.IsProtected(a, StorageAnalyzer.ProtectedRoots([Path.Combine(_root, "docs")]), appData: false)); // game library
        Assert.True(StorageAnalyzer.IsProtected(@"C:\pagefile.sys", StorageAnalyzer.ProtectedRoots(null))); // offered in the VM test
        Assert.True(StorageAnalyzer.IsProtected(@"D:\swapfile.sys", [], appData: false));
        Assert.False(StorageAnalyzer.IsProtected(@"D:\Backup\pagefile.sys", [], appData: false)); // only the drive root is Windows'
    }

    /// <summary>Two hard links to one file are one file: recycling one path frees nothing.</summary>
    [Fact]
    public void HardLinksAreNotDuplicates()
    {
        var a = File(@"links\a.bin", 2 << 20);
        var link = Path.Combine(_root, @"links\a-link.bin");
        Assert.True(CreateHardLink(link, a, IntPtr.Zero));
        Assert.Empty(StorageAnalyzer.FindDuplicates([[a, link]], CancellationToken.None));
        var copy = File(@"links\copy.bin", 2 << 20);
        var dup = Assert.Single(StorageAnalyzer.FindDuplicates([[a, link, copy]], CancellationToken.None));
        Assert.Equal(2, dup.Paths.Count);
        Assert.Contains(copy, dup.Paths);
    }

    /// <summary>
    /// A file whose attributes say its data is not on this PC is never opened for a hash: opening an online-only file
    /// would download it. Offline is the one of these attributes a program may set; the data here is local, so a hash
    /// would group the two copies, and only the live check keeps them out.
    /// </summary>
    [Fact]
    public void FilesMarkedNotLocalAreNotHashed()
    {
        var a = File(@"cloud\a.bin", 2 << 20);
        var b = File(@"cloud\b.bin", 2 << 20);
        Assert.Single(StorageAnalyzer.FindDuplicates([[a, b]], CancellationToken.None));
        try
        {
            System.IO.File.SetAttributes(b, FileAttributes.Offline);
            Assert.True(StorageAnalyzer.NotLocal(b));
            Assert.False(StorageAnalyzer.NotLocal(a));
            Assert.Empty(StorageAnalyzer.FindDuplicates([[a, b]], CancellationToken.None));
            System.IO.File.SetAttributes(a, FileAttributes.Offline);
            Assert.Empty(StorageAnalyzer.FindDuplicates([[a, b]], CancellationToken.None));
        }
        finally
        {
            System.IO.File.SetAttributes(a, FileAttributes.Normal);
            System.IO.File.SetAttributes(b, FileAttributes.Normal);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    [Fact]
    public void UpdateRepairRenamesAndRestartsServices()
    {
        var win = Path.Combine(_root, "Windows");
        Directory.CreateDirectory(Path.Combine(win, "SoftwareDistribution"));
        Directory.CreateDirectory(Path.Combine(win, @"System32\catroot2"));
        // The fake service follows net stop and net start; bits was already stopped (net.exe returns 2).
        var services = new FakeServices();
        foreach (var name in UpdateRepair.Services) services.Running[name] = name != "bits";
        var processes = new FakeProcesses
        {
            Handler = (f, a) =>
            {
                var parts = a.Split(' ');
                if (parts[0] == "stop") services.Running[parts[1]] = false;
                if (parts[0] == "start") services.Running[parts[1]] = true;
                return (a.StartsWith("stop bits", StringComparison.Ordinal) ? 2 : 0, "");
            },
        };
        var steps = UpdateRepair.Run(processes, services, windowsDir: win, now: new DateTime(2026, 10, 9, 12, 0, 0), settle: TimeSpan.Zero);
        Assert.All(steps, s => Assert.True(s.Ok, s.Key));
        Assert.True(Directory.Exists(Path.Combine(win, "SoftwareDistribution.old-20261009-120000")));
        Assert.True(Directory.Exists(Path.Combine(win, @"System32\catroot2.old-20261009-120000")));
        Assert.Equal(4, processes.Calls.Count(c => c.Contains(" stop ")));
        Assert.Equal(3, processes.Calls.Count(c => c.Contains(" start ")));
        Assert.All(steps, s => Assert.True(Labels.Current.Has("en", s.Key) && Labels.Current.Has("de", s.Key), s.Key));
    }

    /// <summary>A fake service that follows net stop and net start (and stays stopped for the names in <paramref name="neverStarts"/>).</summary>
    private static (FakeServices Services, Func<string, (int, string)?> Follow) ServicesFollowingNet(params string[] neverStarts)
    {
        var services = new FakeServices();
        (int, string)? Follow(string args)
        {
            var parts = args.Split(' ');
            if (parts.Length > 1 && parts[0] == "stop") services.Running[parts[1]] = false;
            if (parts.Length > 1 && parts[0] == "start" && !neverStarts.Contains(parts[1])) services.Running[parts[1]] = true;
            return null;
        }
        return (services, Follow);
    }

    /// <summary>net.exe returns 2 also for "access denied": a service that does not run afterwards is a failed step.</summary>
    [Fact]
    public void UpdateRepairReportsAServiceThatDidNotStart()
    {
        var win = Path.Combine(_root, "Windows2");
        Directory.CreateDirectory(win);
        var (services, follow) = ServicesFollowingNet("wuauserv");
        var processes = new FakeProcesses
        {
            Handler = (f, a) =>
            {
                follow(a);
                return (a == "start wuauserv" ? 2 : 0, a == "start wuauserv" ? "System error 5 has occurred." : "");
            },
        };
        var steps = UpdateRepair.Run(processes, services, windowsDir: win, settle: TimeSpan.Zero);
        var failed = Assert.Single(steps, s => !s.Ok);
        Assert.Equal("repair.start.wuauserv", failed.Key);
        Assert.Contains("System error 5", failed.Detail);
    }

    [Fact]
    public void UpdateRepairRetriesCatrootWhenCryptographicServicesHoldsIt()
    {
        var win = Path.Combine(_root, "Windows");
        var catroot = Path.Combine(win, @"System32\catroot2");
        Directory.CreateDirectory(catroot);
        // An open file inside keeps the folder from being renamed, like the restarted service does; the second stop releases it.
        var held = new FileStream(Path.Combine(catroot, "catdb"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var stops = 0;
        var (services, follow) = ServicesFollowingNet();
        var processes = new FakeProcesses
        {
            Handler = (f, a) =>
            {
                follow(a);
                if (a.StartsWith("stop cryptsvc", StringComparison.Ordinal) && ++stops == 2) held.Dispose();
                return (0, "");
            },
        };
        try
        {
            var steps = UpdateRepair.Run(processes, services, windowsDir: win, now: new DateTime(2026, 10, 9, 12, 0, 0), settle: TimeSpan.Zero, wait: _ => { });
            Assert.True(steps.Single(s => s.Key == "repair.rename.catroot2").Ok);
            Assert.True(Directory.Exists(catroot + ".old-20261009-120000"));
            Assert.Equal(2, stops); // the first stop, then one retry
        }
        finally
        {
            held.Dispose();
        }

        // Still held after every attempt: the step fails with the reason, the services start again.
        Directory.CreateDirectory(catroot);
        using var stuck = new FileStream(Path.Combine(catroot, "catdb"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var (services2, follow2) = ServicesFollowingNet();
        var calls = new FakeProcesses { Handler = (f, a) => { follow2(a); return (0, ""); } };
        var failed = UpdateRepair.Run(calls, services2, windowsDir: win, now: new DateTime(2026, 10, 9, 13, 0, 0), settle: TimeSpan.Zero, wait: _ => { });
        Assert.False(failed.Single(s => s.Key == "repair.rename.catroot2").Ok);
        Assert.Equal(UpdateRepair.CatrootAttempts, calls.Calls.Count(c => c.Contains("stop cryptsvc")));
        Assert.Equal(3, calls.Calls.Count(c => c.Contains(" start ")));
    }
}
