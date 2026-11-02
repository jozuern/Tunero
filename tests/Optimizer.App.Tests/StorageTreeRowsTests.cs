using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Storage;

namespace Optimizer.App.Tests;

/// <summary>
/// The folder list of the Storage page: only expanded folders become rows, children come right after their folder,
/// sorted by size, and a drive-sized tree stays a few thousand rows. Trees are built in memory; nothing is read.
/// </summary>
public sealed class StorageTreeRowsTests
{
    private static readonly DateTime When = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// root: docs (a.txt 100, b.txt 300), video.mp4 1000, tools (sub (deep.bin 50)), empty.
    /// Sizes: video.mp4 1000, docs 400, tools 50, empty 0.
    /// </summary>
    private static (DiskTree Tree, int Docs, int Video, int Tools, int Sub, int Deep, int B) Sample()
    {
        var b = new DiskTree.Builder(@"C:\root");
        var docs = b.AddFolder(b.Root, "docs", When, EntryFlags.None);
        b.AddFile(docs, "a.txt", 100, -1, When, EntryFlags.None);
        var bt = b.AddFile(docs, "b.txt", 300, -1, When, EntryFlags.None);
        var video = b.AddFile(b.Root, "video.mp4", 1000, -1, When, EntryFlags.None);
        var tools = b.AddFolder(b.Root, "tools", When, EntryFlags.None);
        var sub = b.AddFolder(tools, "sub", When, EntryFlags.None);
        var deep = b.AddFile(sub, "deep.bin", 50, -1, When, EntryFlags.None);
        b.AddFolder(b.Root, "empty", When, EntryFlags.None);
        return (b.Build(ScanReader.Folders, TimeSpan.Zero), docs, video, tools, sub, deep, bt);
    }

    [Fact]
    public void ResetShowsOnlyTheRootsChildren()
    {
        var (tree, docs, video, tools, _, _, _) = Sample();
        var rows = new StorageTreeRows();
        rows.Reset(tree);
        Assert.Equal(["video.mp4", "docs", "tools", "empty"], rows.Rows.Select(r => r.Name));
        Assert.All(rows.Rows, r => Assert.Equal(0, r.Depth));
        Assert.All(rows.Rows, r => Assert.False(r.IsExpanded));
        Assert.Equal(video, rows.Rows[0].Entry);
        Assert.False(rows.Rows[0].IsFolder);
        Assert.True(rows.Rows[1].IsFolder);
        Assert.Equal(docs, rows.Rows[1].Entry);
        Assert.Equal(tools, rows.Rows[2].Entry);
        // Share of the parent: 1000 of 1450.
        Assert.Equal(1000.0 / 1450, rows.Rows[0].Share, 6);
        // An empty folder has nothing to expand.
        Assert.True(rows.Rows[1].CanExpand);
        Assert.False(rows.Rows[3].CanExpand);
        // A second Reset starts over.
        rows.Toggle(rows.Rows[1]);
        rows.Reset(tree);
        Assert.Equal(4, rows.Rows.Count);
    }

    [Fact]
    public void ToggleInsertsChildrenAfterTheRowAndRemovesThemAgain()
    {
        var (tree, docs, _, _, _, _, bt) = Sample();
        var rows = new StorageTreeRows();
        rows.Reset(tree);
        var docsRow = rows.Rows[1];
        rows.Toggle(docsRow);
        Assert.True(docsRow.IsExpanded);
        Assert.Equal(["video.mp4", "docs", "b.txt", "a.txt", "tools", "empty"], rows.Rows.Select(r => r.Name));
        Assert.Equal(1, rows.Rows[2].Depth);
        Assert.Equal(1, rows.Rows[3].Depth);
        Assert.Equal(bt, rows.Rows[2].Entry);
        Assert.Equal(0.75, rows.Rows[2].Share, 6);

        // Collapsing removes everything below, also rows of folders expanded inside it.
        var toolsRow = rows.Rows[4];
        rows.Toggle(toolsRow);
        rows.Toggle(rows.Rows[5]); // sub
        Assert.Equal(["video.mp4", "docs", "b.txt", "a.txt", "tools", "sub", "deep.bin", "empty"], rows.Rows.Select(r => r.Name));
        Assert.Equal(2, rows.Rows[6].Depth);
        rows.Toggle(toolsRow);
        Assert.False(toolsRow.IsExpanded);
        Assert.Equal(["video.mp4", "docs", "b.txt", "a.txt", "tools", "empty"], rows.Rows.Select(r => r.Name));
        rows.Toggle(docsRow);
        Assert.Equal(["video.mp4", "docs", "tools", "empty"], rows.Rows.Select(r => r.Name));

        // A file has nothing to toggle.
        rows.Toggle(rows.Rows[0]);
        Assert.Equal(4, rows.Rows.Count);
        Assert.Equal(docs, rows.Rows[1].Entry);
    }

    [Fact]
    public void RevealExpandsAncestors()
    {
        var (tree, _, _, tools, sub, deep, _) = Sample();
        var rows = new StorageTreeRows();
        rows.Reset(tree);
        var row = rows.Reveal(deep);
        Assert.NotNull(row);
        Assert.Equal(deep, row.Entry);
        Assert.Equal(2, row.Depth);
        Assert.Same(row, rows.Rows.Single(r => r.Entry == deep));
        Assert.True(rows.Rows.Single(r => r.Entry == tools).IsExpanded);
        Assert.True(rows.Rows.Single(r => r.Entry == sub).IsExpanded);
        // Revealing it again changes nothing.
        var count = rows.Rows.Count;
        Assert.Same(row, rows.Reveal(deep));
        Assert.Equal(count, rows.Rows.Count);
        // The root has no row.
        Assert.Null(rows.Reveal(tree.Root));
    }

    [Fact]
    public void RefreshKeepsExpandedFoldersAfterARemoval()
    {
        var (tree, docs, _, _, _, _, bt) = Sample();
        var rows = new StorageTreeRows();
        rows.Reset(tree);
        rows.Toggle(rows.Rows.Single(r => r.Entry == docs));
        tree.Remove(bt);
        rows.Refresh();
        Assert.Equal(["video.mp4", "docs", "a.txt", "tools", "empty"], rows.Rows.Select(r => r.Name));
        Assert.True(rows.Rows[1].IsExpanded);
        Assert.Equal(1.0, rows.Rows[2].Share, 6);
    }

    /// <summary>
    /// A link is not followed, so it would read as an empty folder ("0 KB", "0 files"): it shows no size and says it is a
    /// link. A folder that could not be read keeps what was counted and says so; a plain folder shows its file count.
    /// </summary>
    [Fact]
    public void LinksAndUnreadableFoldersSayWhatTheyAre()
    {
        var b = new DiskTree.Builder(@"C:\root");
        var plain = b.AddFolder(b.Root, "plain", When, EntryFlags.None);
        b.AddFile(plain, "a.txt", 100, -1, When, EntryFlags.None);
        var locked = b.AddFolder(b.Root, "locked", When, EntryFlags.Unreadable);
        b.AddFile(locked, "seen.txt", 50, -1, When, EntryFlags.None);
        b.AddFolder(b.Root, "junction", When, EntryFlags.ReparsePoint);
        b.AddFile(b.Root, "file link.txt", 0, -1, When, EntryFlags.ReparsePoint);
        var rows = new StorageTreeRows();
        rows.Reset(b.Build(ScanReader.Folders, TimeSpan.Zero));

        var byName = rows.Rows.ToDictionary(r => r.Name);
        Assert.False(byName["plain"].IsLink);
        Assert.False(byName["plain"].IsUnreadable);
        Assert.Equal("1", byName["plain"].FilesText);

        Assert.True(byName["locked"].IsUnreadable);
        Assert.False(byName["locked"].IsLink);
        Assert.Equal(CleanupViewModel.Size(50), byName["locked"].SizeText);
        Assert.Equal(Loc.Instance["Storage_UnreadableRow"], byName["locked"].FilesText);

        foreach (var name in new[] { "junction", "file link.txt" })
        {
            Assert.True(byName[name].IsLink);
            Assert.False(byName[name].IsUnreadable);
            Assert.Equal("", byName[name].SizeText);
            Assert.Equal(Loc.Instance["Storage_LinkRow"], byName[name].FilesText);
            Assert.Equal($"{name}, {Loc.Instance["Storage_LinkRow"]}", byName[name].ToString());
        }
        Assert.Equal($"plain, {CleanupViewModel.Size(100)}", byName["plain"].ToString());
    }

    [Fact]
    public void LargeTreeStaysCheap()
    {
        // 200,000 files in 2,000 folders (100 each), as a drive's worth of entries.
        var b = new DiskTree.Builder(@"D:\");
        for (var f = 0; f < 2000; f++)
        {
            var folder = b.AddFolder(b.Root, "folder" + f, When, EntryFlags.None);
            for (var i = 0; i < 100; i++) b.AddFile(folder, $"file{i}.bin", 1 + i + f, -1, When, EntryFlags.None);
        }
        var tree = b.Build(ScanReader.Folders, TimeSpan.Zero);
        var rows = new StorageTreeRows();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        rows.Reset(tree);
        Assert.True(rows.Rows.Count <= 2000);
        var target = rows.Rows[1000];
        rows.Toggle(target);
        Assert.Equal(2100, rows.Rows.Count);
        Assert.All(rows.Rows.Skip(1001).Take(100), r => Assert.Equal(1, r.Depth));
        Assert.Equal(0, rows.Rows[1101].Depth);
        rows.Toggle(target);
        Assert.Equal(2000, rows.Rows.Count);
        // Generous for a slow CI machine; a few milliseconds here.
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }
}
