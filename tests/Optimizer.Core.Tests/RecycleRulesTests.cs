using Optimizer.Core.Storage;
using Optimizer.Core.Tools;

namespace Optimizer.Core.Tests;

/// <summary>What the storage page may move to the Recycle Bin. Only refusals are tested: the bin is the real user's.</summary>
public sealed class RecycleRulesTests : IDisposable
{
    private readonly string _root = TestFolders.Create("recycle-rules");

    public void Dispose() => TestFolders.Delete(_root);

    [Fact]
    public void SystemFileIsRefused()
    {
        Assert.Equal(RecycleBlock.SystemFile, RecycleRules.Check(@"C:\pagefile.sys", folder: false, systemFile: true, [], [], appData: false));
    }

    /// <summary>Reads this PC's profile list and drives (read only): the CI runner has both, so CI runs it.</summary>
    [Fact]
    public void DriveRootIsKept()
    {
        var kept = RecycleRules.KeptFolders();
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(RecycleBlock.KeptFolder, RecycleRules.Check(system, folder: true, systemFile: false, [], kept, appData: false));
    }

    /// <summary>Reads this PC's profile list and drives (read only): the CI runner has both, so CI runs it.</summary>
    [Fact]
    public void ProfileRootIsKeptButItsFilesAreNot()
    {
        var kept = RecycleRules.KeptFolders();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(RecycleBlock.KeptFolder, RecycleRules.Check(profile, folder: true, systemFile: false, [], kept, appData: false));
        Assert.Equal(RecycleBlock.KeptFolder, RecycleRules.Check(Path.Combine(profile, "Downloads"), folder: true, systemFile: false, [], kept, appData: false));
        Assert.Null(RecycleRules.Check(Path.Combine(profile, "Downloads", "setup.exe"), folder: false, systemFile: false, [], kept, appData: false));
        Assert.Null(RecycleRules.Check(Path.Combine(profile, "Downloads", "old"), folder: true, systemFile: false, [], kept, appData: false));
    }

    /// <summary>
    /// The signed-in user's Documents moved to another drive, which the elevated account's own known folders do not name:
    /// passed in, it stays as a whole, and so does a folder that holds it. Reads this PC's drives and profile list (read only).
    /// </summary>
    [Fact]
    public void TheSessionUsersMovedFoldersAreKept()
    {
        var documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(documents);
        var kept = RecycleRules.KeptFolders([documents]);
        Assert.Equal(RecycleBlock.KeptFolder, RecycleRules.Check(documents, folder: true, systemFile: false, [], kept, appData: false));
        Assert.Equal(RecycleBlock.ContainsProtected, RecycleRules.Check(_root, folder: true, systemFile: false, [], kept, appData: false));
        Assert.Null(RecycleRules.Check(Path.Combine(documents, "old"), folder: true, systemFile: false, [], kept, appData: false));
    }

    [Fact]
    public void ProtectedPathIsRefusedBeforeContainment()
    {
        var games = Path.Combine(_root, "games") + "\\";
        Assert.Equal(RecycleBlock.Protected, RecycleRules.Check(Path.Combine(games, "g.pak"), folder: false, systemFile: false, [games], [], appData: false));
    }

    [Fact]
    public void FolderThatContainsAProtectedRootIsRefused()
    {
        var x = Path.Combine(_root, "x");
        var protectedRoots = new[] { Path.Combine(x, "games") + "\\" };
        Assert.Equal(RecycleBlock.ContainsProtected, RecycleRules.Check(x, folder: true, systemFile: false, protectedRoots, [], appData: false));
        Assert.Null(RecycleRules.Check(Path.Combine(x, "other"), folder: true, systemFile: false, protectedRoots, [], appData: false));
    }

    [Fact]
    public void FolderThatContainsAKeptFolderIsRefused()
    {
        var x = Path.Combine(_root, "x");
        var kept = new[] { Path.Combine(x, "profile") + "\\" };
        Assert.Equal(RecycleBlock.ContainsProtected, RecycleRules.Check(x, folder: true, systemFile: false, [], kept, appData: false));
        Assert.Null(RecycleRules.Check(Path.Combine(x, "other"), folder: true, systemFile: false, [], kept, appData: false));
    }

    [Fact]
    public void FolderWithAnAppDataFolderInsideIsRefusedOnlyWhenAppDataIsProtected()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, "AppData"));
        // %TEMP% lies in AppData itself, so with the check on the folder is Protected before the contents are looked at.
        Assert.NotNull(RecycleRules.Check(home, folder: true, systemFile: false, [], [], appData: true));
        Assert.Null(RecycleRules.Check(home, folder: true, systemFile: false, [], [], appData: false));
    }

    [Fact]
    public void PathWithDotDotIsCheckedWhereItLeads()
    {
        // Pure Check test: the roots are fake and nothing is touched. Both spellings lead into games\.
        var games = Path.Combine(_root, "games") + "\\";
        var sneaky = Path.Combine(_root, "x", "..", "games", "g.pak");
        Assert.Equal(RecycleBlock.Protected, RecycleRules.Check(sneaky, folder: false, systemFile: false, [games], [], appData: false));
        Assert.Equal(RecycleBlock.Protected, RecycleRules.Check(Path.Combine(_root, "x", "..", "games"), folder: true, systemFile: false, [games], [], appData: false));
        Assert.EndsWith(@"\games\g.pak", RecycleRules.Normalize(sneaky));
    }

    [Fact]
    public void RootsAndKeptFoldersInAnotherSpellingStillMatch()
    {
        // The same folders spelled with a ".." segment and other case (an 8.3 name cannot be forced here).
        var games = Path.Combine(_root, "x", "..", "GAMES") + "\\";
        var kept = Path.Combine(_root, "y", "..", "KEPT") + "\\";
        Assert.Equal(RecycleBlock.Protected, RecycleRules.Check(Path.Combine(_root, "games", "g.pak"), folder: false, systemFile: false, [games], [], appData: false));
        Assert.Equal(RecycleBlock.KeptFolder, RecycleRules.Check(Path.Combine(_root, "kept"), folder: true, systemFile: false, [], [kept], appData: false));
        Assert.Equal(RecycleBlock.ContainsProtected, RecycleRules.Check(_root, folder: true, systemFile: false, [games], [], appData: false));
    }

    [Fact]
    public void ShortNameOfAKeptFolderIsStillKept()
    {
        // The long name of the temp folder differs from its 8.3 form on most machines; where it does not, the test still holds.
        var kept = Path.Combine(_root, "Long Folder Name For Kept");
        Directory.CreateDirectory(kept);
        var set = new[] { RecycleRules.Normalize(kept) + "\\" };
        Assert.Equal(RecycleBlock.KeptFolder, RecycleRules.Check(kept, folder: true, systemFile: false, [], set, appData: false));
        Assert.Equal(RecycleRules.Normalize(kept), RecycleRules.Normalize(kept + @"\sub\.."));
    }

    [Fact]
    public void HoldsFolderLinkFindsAJunctionAtAnyDepth()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var plain = Path.Combine(_root, "plain", "inner");
        Directory.CreateDirectory(plain);
        var direct = Path.Combine(_root, "direct");
        Directory.CreateDirectory(direct);
        TestFolders.Junction(Path.Combine(direct, "link"), target);
        var deep = Path.Combine(_root, "deep", "a", "b");
        Directory.CreateDirectory(deep);
        TestFolders.Junction(Path.Combine(deep, "link"), target);

        Assert.False(StorageAnalyzer.HoldsFolderLink(Path.Combine(_root, "plain")));
        Assert.True(StorageAnalyzer.HoldsFolderLink(direct));
        Assert.True(StorageAnalyzer.HoldsFolderLink(Path.Combine(_root, "deep")));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void NameEndingInADotOrSpaceIsRefused()
    {
        // Pure Check test: "a." and "b " would be checked as "a" and "b"; a folder above with such a name counts too.
        Assert.Equal(RecycleBlock.NameMismatch, RecycleRules.Check(Path.Combine(_root, "a."), folder: true, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.NameMismatch, RecycleRules.Check(Path.Combine(_root, "b "), folder: false, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.NameMismatch, RecycleRules.Check(Path.Combine(_root, "x.", "file.txt"), folder: false, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.NameMismatch, RecycleRules.Check(Path.Combine(_root, "y ", "z"), folder: true, systemFile: false, [], [], appData: false));
        Assert.False(RecycleRules.ResolvesAsWritten(Path.Combine(_root, "a."), RecycleRules.Normalize(Path.Combine(_root, "a."))));
        Assert.False(RecycleRules.ResolvesAsWritten(@"\\?\" + Path.Combine(_root, "a"), Path.Combine(_root, "a")));
        Assert.False(RecycleRules.ResolvesAsWritten(Path.Combine(_root, "x", "..", "a"), Path.Combine(_root, "a")));
        Assert.True(RecycleRules.ResolvesAsWritten(Path.Combine(_root, "a"), RecycleRules.Normalize(Path.Combine(_root, "A"))));
    }

    [Fact]
    public void WindowsSetupFoldersInADriveRootAreRefused()
    {
        // Pure Check test on paths: nothing is read but attributes.
        Assert.Equal(RecycleBlock.WindowsSetup, RecycleRules.Check(@"C:\Windows.old", folder: true, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.WindowsSetup, RecycleRules.Check(@"C:\Windows.old\Users\x\photo.jpg", folder: false, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.WindowsSetup, RecycleRules.Check(@"D:\$WINDOWS.~BT", folder: true, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.WindowsSetup, RecycleRules.Check(@"D:\$Windows.~WS\Sources\setup.log", folder: false, systemFile: false, [], [], appData: false));
        // Only in a drive root: a folder of that name elsewhere is the user's.
        Assert.False(RecycleRules.IsWindowsSetup(@"D:\backup\Windows.old\"));
    }

    [Fact]
    public void HiddenSystemFolderIsRefusedButSystemOrReadOnlyAloneIsNot()
    {
        var hiddenSystem = Path.Combine(_root, "hs");
        var systemOnly = Path.Combine(_root, "s");
        var readOnly = Path.Combine(_root, "r");
        var hiddenOnly = Path.Combine(_root, "h");
        foreach (var d in new[] { hiddenSystem, systemOnly, readOnly, hiddenOnly }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(hiddenSystem, "rollback.rbf"), "x");
        File.SetAttributes(hiddenSystem, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(systemOnly, FileAttributes.Directory | FileAttributes.System);
        File.SetAttributes(readOnly, FileAttributes.Directory | FileAttributes.ReadOnly);
        File.SetAttributes(hiddenOnly, FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Equal(RecycleBlock.SystemFolder, RecycleRules.Check(hiddenSystem, folder: true, systemFile: false, [], [], appData: false));
        Assert.Equal(RecycleBlock.SystemFolder, RecycleRules.Check(Path.Combine(hiddenSystem, "rollback.rbf"), folder: false, systemFile: false, [], [], appData: false));
        Assert.Null(RecycleRules.Check(systemOnly, folder: true, systemFile: false, [], [], appData: false));
        Assert.Null(RecycleRules.Check(readOnly, folder: true, systemFile: false, [], [], appData: false));
        Assert.Null(RecycleRules.Check(hiddenOnly, folder: true, systemFile: false, [], [], appData: false));
    }

    [Fact]
    public void RecycleItemsRefusesATrailingDotNameAndLeavesItsTwinAlone()
    {
        // "a." and "a" side by side (the dotted one only through \\?\). RecycleItems normalizes "a." to "a"; the
        // ResolvesAsWritten check right after Normalize refuses it before Exists, Check or MoveToRecycleBin run, so nothing
        // here can reach the Recycle Bin. The same holds for the file pair "b." and "b". The hidden system folder is
        // refused by CheckNormalized, which is evaluated before HoldsFolderLink and MoveToRecycleBin.
        var twin = Path.Combine(_root, "a");
        var dotted = twin + ".";
        var file = Path.Combine(_root, "b");
        var dottedFile = file + ".";
        var hiddenSystem = Path.Combine(_root, "Config.Msi");
        Directory.CreateDirectory(twin);
        Directory.CreateDirectory(@"\\?\" + dotted);
        File.WriteAllText(file, "x");
        File.WriteAllText(@"\\?\" + dottedFile, "x");
        Directory.CreateDirectory(hiddenSystem);
        File.SetAttributes(hiddenSystem, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        try
        {
            Assert.True(Directory.Exists(@"\\?\" + dotted));
            // The rules first: if one of them stopped refusing, the test ends here, before RecycleItems could move anything.
            Assert.False(RecycleRules.ResolvesAsWritten(dotted, RecycleRules.Normalize(dotted)));
            Assert.False(RecycleRules.ResolvesAsWritten(dottedFile, RecycleRules.Normalize(dottedFile)));
            Assert.Equal(RecycleBlock.SystemFolder, RecycleRules.Check(hiddenSystem, folder: true, systemFile: false, [], [], appData: false));
            var failed = StorageAnalyzer.RecycleItems([(dotted, true), (dottedFile, false), (hiddenSystem, true)], [], [], appData: false);

            Assert.Equal([dotted, dottedFile, hiddenSystem], failed);
            Assert.True(Directory.Exists(twin));
            Assert.True(Directory.Exists(@"\\?\" + dotted));
            Assert.True(File.Exists(file));
            Assert.True(File.Exists(@"\\?\" + dottedFile));
            Assert.True(Directory.Exists(hiddenSystem));
        }
        finally
        {
            // The trimmed names are reachable only through \\?\.
            if (File.Exists(@"\\?\" + dottedFile)) File.Delete(@"\\?\" + dottedFile);
            if (Directory.Exists(@"\\?\" + dotted)) Directory.Delete(@"\\?\" + dotted);
        }
    }

    [Fact]
    public void RecycleItemsRefusesKeptProtectedAndDotDotPathsBeforeAnyMove()
    {
        // Every item is refused by Check before MoveToRecycleBin can be reached: nothing here may reach the Recycle Bin.
        var kept = Path.Combine(_root, "kept");
        var games = Path.Combine(_root, "games");
        Directory.CreateDirectory(kept);
        Directory.CreateDirectory(games);
        var gameFile = Path.Combine(games, "g.pak");
        File.WriteAllText(gameFile, "x");
        var sneaky = Path.Combine(_root, "kept", "..", "games", "g.pak");
        var roots = new[] { games + "\\" };

        var failed = StorageAnalyzer.RecycleItems([(kept, true), (gameFile, false), (sneaky, false), (games, true)], roots, [kept + "\\"], appData: false);

        Assert.Equal([kept, gameFile, sneaky, games], failed);
        Assert.True(Directory.Exists(kept));
        Assert.True(File.Exists(gameFile));
    }

    /// <summary>
    /// What the tree says: a link itself, and anything that is or lies in a cloud folder or another reparse point, which
    /// the move would refuse. A tree in memory; nothing on disk.
    /// </summary>
    [Fact]
    public void CheckEntryRefusesLinksAndWhatLiesInCloudOrOtherReparseFolders()
    {
        var b = new DiskTree.Builder(@"X:");
        var oneDrive = b.AddFolder(b.Root, "OneDrive", default, EntryFlags.NonLinkReparse | EntryFlags.CloudSync);
        var photos = b.AddFolder(oneDrive, "Photos", default, EntryFlags.None);
        var photo = b.AddFile(photos, "a.jpg", 10, -1, default, EntryFlags.None);
        var projected = b.AddFolder(b.Root, "projected", default, EntryFlags.NonLinkReparse);
        var inside = b.AddFile(projected, "b.bin", 10, -1, default, EntryFlags.None);
        var compressed = b.AddFile(b.Root, "c.bin", 10, -1, default, EntryFlags.NonLinkReparse);
        var linkFile = b.AddFile(b.Root, "link.txt", 0, -1, default, EntryFlags.ReparsePoint);
        var linkFolder = b.AddFolder(b.Root, "junction", default, EntryFlags.ReparsePoint);
        var plain = b.AddFile(b.Root, "plain.bin", 10, -1, default, EntryFlags.None);
        var t = b.Build(ScanReader.Folders, TimeSpan.Zero);

        Assert.Equal(RecycleBlock.CloudFolder, RecycleRules.CheckEntry(t, oneDrive));
        Assert.Equal(RecycleBlock.CloudFolder, RecycleRules.CheckEntry(t, photos));
        Assert.Equal(RecycleBlock.CloudFolder, RecycleRules.CheckEntry(t, photo));
        Assert.Equal(RecycleBlock.OtherReparse, RecycleRules.CheckEntry(t, projected));
        Assert.Equal(RecycleBlock.OtherReparse, RecycleRules.CheckEntry(t, inside));
        Assert.Equal(RecycleBlock.OtherReparse, RecycleRules.CheckEntry(t, compressed));
        Assert.Equal(RecycleBlock.Link, RecycleRules.CheckEntry(t, linkFile));
        Assert.Equal(RecycleBlock.Link, RecycleRules.CheckEntry(t, linkFolder));
        Assert.Null(RecycleRules.CheckEntry(t, plain));
        Assert.Null(RecycleRules.CheckEntry(t, t.Root));
    }

    /// <summary>
    /// The shell call is the test module's stand-in (TestSetup): a test that loses a refusal fails instead of moving
    /// anything. Called with a path that does not exist, so even the real call could move nothing.
    /// </summary>
    [Fact]
    public void TheRecycleBinIsOutOfReachInTests()
    {
        var missing = Path.Combine(_root, "never-created-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<RecycleBinReachedException>(() => StorageAnalyzer.ShellRecycle(missing));
    }

    /// <summary>An analyzed folder in OneDrive or below another reparse point: the tree's own entries cannot say so.</summary>
    [Fact]
    public void CheckAboveReadsTheAnalyzedFolderAndEveryFolderAboveIt()
    {
        const uint dir = 0x10, reparse = 0x400;
        var root = @"C:\Users\x\OneDrive\Projects";
        var asked = new List<string>();
        (uint, uint)? Read(string p, string at, uint tag)
        {
            asked.Add(p);
            return string.Equals(p, at, StringComparison.OrdinalIgnoreCase) ? (dir | reparse, tag) : (dir, 0u);
        }

        Assert.Equal(RecycleBlock.CloudFolder, RecycleRules.CheckAbove(root, p => Read(p, @"C:\Users\x\OneDrive", 0x9000_601A)));
        Assert.Equal(RecycleBlock.CloudFolder, RecycleRules.CheckAbove(root, p => Read(p, root, 0x9000_601A)));
        Assert.Equal(RecycleBlock.OtherReparse, RecycleRules.CheckAbove(root, p => Read(p, @"C:\Users", 0xA000_0003)));
        Assert.Equal(RecycleBlock.OtherReparse, RecycleRules.CheckAbove(root, p => Read(p, @"C:\Users\x", 0x8000_0017)));
        asked.Clear();
        Assert.Null(RecycleRules.CheckAbove(root, p => Read(p, "none", 0)));
        // The drive root is never read: it is not a folder above the entries that the move checks.
        Assert.Equal([root, @"C:\Users\x\OneDrive", @"C:\Users\x", @"C:\Users"], asked);
        Assert.Null(RecycleRules.CheckAbove(@"C:\", _ => throw new InvalidOperationException("a drive root reads nothing")));
        Assert.Null(RecycleRules.CheckAbove(root, _ => null));
    }

    /// <summary>Both readers classify a reparse point by its tag the same way (ReparseTags.Flags).</summary>
    [Theory]
    [InlineData(0x20u, 0u, EntryFlags.None)]
    [InlineData(0x400u, 0xA000_0003u, EntryFlags.ReparsePoint)]          // junction, mount point
    [InlineData(0x400u, 0xA000_000Cu, EntryFlags.ReparsePoint)]          // symbolic link
    [InlineData(0x400u, 0x9000_001Au, EntryFlags.NonLinkReparse | EntryFlags.CloudSync)]
    [InlineData(0x400u, 0x9000_601Au, EntryFlags.NonLinkReparse | EntryFlags.CloudSync)]  // OneDrive's CLOUD_6
    [InlineData(0x400u, 0x8000_0017u, EntryFlags.NonLinkReparse)]        // WOF compression
    [InlineData(0x400u, 0u, EntryFlags.NonLinkReparse)]                  // tag not read
    public void ReparseTagsClassifyLinksCloudAndOthers(uint attributes, uint tag, EntryFlags expected) =>
        Assert.Equal(expected, ReparseTags.Flags(attributes, tag));
}
