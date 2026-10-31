using System.IO;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Hardware;
using Optimizer.Core.Platform;
using Optimizer.Core.Storage;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The Storage page without the window: opening it reads only the drive list, an analysis fills the open tab, and a
/// stopped analysis keeps the result before it. Only a temp folder is read; nothing is moved or deleted: every view
/// model gets a recorder as its Recycle Bin (<see cref="_recycled"/>), never the real one.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class StorageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunero-storage-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakePageHost _host = new();
    private readonly FakeDialogs _dialogs = new();

    /// <summary>What the view model handed to its Recycle Bin, one call per item; nothing is moved.</summary>
    private readonly List<(string Path, bool Folder)> _recycled = [];

    /// <summary>Paths the recorder reports as not moved.</summary>
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    public StorageViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(string name, int bytes)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// The page with the recorder as its Recycle Bin. AppData is not refused in the selection check (the temp folder lies
    /// there); the real move would refuse it anyway.
    /// </summary>
    private StorageViewModel Storage(Func<string, IProgress<ScanProgress>?, CancellationToken, Task<DiskTree>>? scanner = null) =>
        new(_host, null!, _dialogs, scanner ?? ((root, progress, ct) => new EnumerationScanner().ScanAsync(root, progress, ct)), Record, appData: false);

    private IReadOnlyList<string> Record(IReadOnlyList<(string Path, bool Folder)> items)
    {
        lock (_recycled) _recycled.AddRange(items);
        return items.Select(i => i.Path).Where(_failing.Contains).ToList();
    }

    private static int Entry(DiskTree tree, string name) => Enumerable.Range(0, tree.Count).Single(i => !tree.IsRemoved(i) && tree.Name(i) == name);

    [Fact]
    public async Task OpeningThePageReadsOnlyDrives()
    {
        var scans = 0;
        var storage = Storage((_, _, _) =>
        {
            scans++;
            throw new InvalidOperationException("opening the page must not scan");
        });
        await storage.EnsureLoadedAsync();
        Assert.Null(storage.Tree);
        Assert.NotEmpty(storage.Drives);
        Assert.Equal(0, scans);
        Assert.Empty(_host.Results);
    }

    [Fact]
    public async Task AnalyzeFolderFillsLargestFiles()
    {
        var big = File("big.bin", 3000);
        File("middle.bin", 2000);
        var small = File("small.txt", 1000);
        var storage = Storage();
        await storage.EnsureLoadedAsync();
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);
        Assert.Equal(3, tree.Files(tree.Root));
        Assert.NotNull(storage.ResultText);
        // Only the open tab builds its rows.
        Assert.Empty(storage.LargestFiles);
        storage.Tab = 2;
        await storage.RowsBuilt;
        Assert.Equal(3, storage.LargestFiles.Count);
        Assert.Equal(big, storage.LargestFiles[0].Path);
        Assert.Equal(small, storage.LargestFiles[^1].Path);
        Assert.Equal(3000, storage.LargestFiles[0].Bytes);
        Assert.False(_host.Runner.Work.IsActive);
    }

    [Fact]
    public async Task TypesListGroupsAndFiles()
    {
        var a = File("a.mp4", 300);
        var b = File("b.mp4", 100);
        File("c.zip", 200);
        var storage = Storage();
        await storage.EnsureLoadedAsync();
        await storage.AnalyzeFolderAsync(_root);
        // Only the open tab builds its rows.
        Assert.Empty(storage.TypeGroups);
        storage.Tab = StorageViewModel.TabTypes;
        Assert.Equal([FileGroup.Video, FileGroup.Archives], storage.TypeGroups.Select(g => g.Group));
        Assert.Equal([400L, 200L], storage.TypeGroups.Select(g => g.Bytes));
        Assert.Equal([2, 1], storage.TypeGroups.Select(g => g.Count));
        Assert.Equal(400 / 600.0, storage.TypeGroups[0].Share, 6);
        // Extension rows exist only for an expanded group.
        Assert.All(storage.TypeGroups, g => Assert.Empty(g.Extensions));
        storage.TypeGroups[0].IsExpanded = true;
        Assert.Equal([".mp4"], storage.TypeGroups[0].Extensions.Select(e => e.Name));
        Assert.Empty(storage.TypeGroups[1].Extensions);
        Assert.Empty(storage.TypeFiles);
        // The group's selection list drives the selection, and only the group that owns it shows it.
        storage.TypeGroups[0].Selected = storage.TypeGroups[0].Extensions[0];
        await storage.RowsBuilt;
        Assert.Same(storage.TypeGroups[0].Extensions[0], storage.SelectedType);
        Assert.True(storage.SelectedType!.IsSelected);
        Assert.Equal([a, b], storage.TypeFiles.Select(f => f.Path));
        // Closing the group drops the pick and its files.
        storage.TypeGroups[0].IsExpanded = false;
        Assert.Null(storage.SelectedType);
        Assert.Empty(storage.TypeFiles);
        storage.TypeGroups[0].IsExpanded = true;
        storage.SelectedType = storage.TypeGroups[0].Extensions[0];
        // The checks were made for the first list: the rows come at once.
        Assert.True(storage.RowsBuilt.IsCompleted);
        Assert.Same(storage.SelectedType, storage.TypeGroups[0].Selected);
        Assert.Equal([a, b], storage.TypeFiles.Select(f => f.Path));
        // Closing the result drops the lists.
        storage.CloseResultCommand.Execute(null);
        Assert.Empty(storage.TypeGroups);
        Assert.Empty(storage.TypeFiles);
        Assert.Null(storage.SelectedType);
    }

    [Fact]
    public async Task PickingAnExtensionAgainBeforeItsFillEndsListsEachFileOnce()
    {
        var a = File("a.mp4", 300);
        var b = File("b.mp4", 100);
        File("c.zip", 200);
        var storage = Storage();
        await storage.EnsureLoadedAsync();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = StorageViewModel.TabTypes;
        foreach (var g in storage.TypeGroups) g.IsExpanded = true;
        var mp4 = storage.TypeGroups.Single(g => g.Group == FileGroup.Video).Extensions[0];
        var zip = storage.TypeGroups.Single(g => g.Group == FileGroup.Archives).Extensions[0];
        // Down and Up again in the group's list while the first fill still checks its paths: the same row is picked twice.
        storage.SelectedType = mp4;
        storage.SelectedType = zip;
        storage.SelectedType = mp4;
        await storage.RowsBuilt;
        Assert.Equal([a, b], storage.TypeFiles.Select(f => f.Path));
    }

    [Fact]
    public async Task AGroupShowsAtMost200ExtensionsAndCountsTheRest()
    {
        for (var i = 0; i < 250; i++) File($"f.e{i:D3}", 10 + i);
        var storage = Storage();
        await storage.EnsureLoadedAsync();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = StorageViewModel.TabTypes;
        var group = Assert.Single(storage.TypeGroups);
        Assert.Null(group.MoreText);
        group.IsExpanded = true;
        Assert.Equal(200, group.Extensions.Count);
        Assert.Equal(".e249", group.Extensions[0].Name);
        Assert.Equal(Loc.Instance.Format("Storage_MoreTypes", 50), group.MoreText);
        group.IsExpanded = false;
        Assert.Empty(group.Extensions);
        Assert.Null(group.MoreText);
    }

    [Fact]
    public async Task CancelledAnalyzeKeepsThePreviousResult()
    {
        File("a.bin", 1000);
        var calls = 0;
        var storage = Storage((root, progress, ct) =>
        {
            // The second analysis is stopped: the reader gives up without a tree.
            if (++calls == 2) throw new OperationCanceledException();
            return new EnumerationScanner().ScanAsync(root, progress, ct);
        });
        await storage.AnalyzeFolderAsync(_root);
        var first = storage.Tree;
        var text = storage.ResultText;
        Assert.NotNull(first);
        var changes = 0;
        storage.TreeChanged += (_, _) => changes++;

        await storage.AnalyzeFolderAsync(_root);
        Assert.Same(first, storage.Tree);
        Assert.Equal(text, storage.ResultText);
        Assert.Equal(0, changes);
        Assert.False(_host.Runner.Work.IsActive);
        Assert.Equal(Loc.Instance["Storage_Stopped"], _host.Results[^1].Text);
    }

    [Fact]
    public async Task ReloadKeepsTheResultAndAnotherDriveDropsIt()
    {
        File("a.bin", 1000);
        var storage = Storage();
        await storage.EnsureLoadedAsync();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = 2;
        await storage.RowsBuilt;
        var tree = storage.Tree;
        // F5 reads the drive list again: the same drive stays picked, the result and its rows stay as they are.
        await storage.ReloadAsync();
        Assert.Same(tree, storage.Tree);
        Assert.Single(storage.LargestFiles);
        storage.SelectedDrive = new DriveRow(@"Z:\", 1, 1);
        Assert.Null(storage.Tree);
    }

    /// <summary>
    /// While the page's own work runs (a recycle), nothing may read or replace the tree: Find duplicates and Analyze are
    /// off and say so through CanExecute. IsWorking is set directly; nothing is recycled.
    /// </summary>
    [Fact]
    public async Task NothingStartsWhileThePageIsWorking()
    {
        File("a.bin", 1000);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        Assert.True(storage.FindDuplicatesCommand.CanExecute(null));
        Assert.True(storage.AnalyzeCommand.CanExecute(null));
        var raised = 0;
        storage.FindDuplicatesCommand.CanExecuteChanged += (_, _) => raised++;

        storage.IsWorking = true;
        Assert.False(storage.FindDuplicatesCommand.CanExecute(null));
        Assert.False(storage.AnalyzeCommand.CanExecute(null));
        Assert.False(storage.RecycleCommand.CanExecute(null));
        Assert.True(raised > 0);
        // Called anyway (a key binding, a test): it does nothing.
        await storage.FindDuplicatesCommand.ExecuteAsync(null);
        Assert.False(storage.DuplicatesSearched);
        var tree = storage.Tree;
        await storage.AnalyzeFolderAsync(_root);
        Assert.Same(tree, storage.Tree);

        storage.IsWorking = false;
        Assert.True(storage.FindDuplicatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnalyzeWaitsForThePcScan()
    {
        _host.Profile = null;
        var storage = Storage((_, _, _) => throw new InvalidOperationException("no analysis before the PC scan"));
        await storage.AnalyzeFolderAsync(_root);
        Assert.Null(storage.Tree);
        Assert.Equal(Loc.Instance["Storage_WaitForScan"], _host.Results[^1].Text);
    }

    [Fact]
    public async Task CloseResultDropsTheTree()
    {
        File("a.bin", 1000);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = 2;
        var changes = 0;
        storage.TreeChanged += (_, _) => changes++;
        storage.CloseResultCommand.Execute(null);
        Assert.Null(storage.Tree);
        Assert.Null(storage.ResultText);
        Assert.Empty(storage.LargestFiles);
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// The Folders tab is the default: an analysis lists the root's children, a folder shown on the map gets its path
    /// as the breadcrumb, an entry picked on the map expands its folder in the list, and Close result empties it all.
    /// </summary>
    [Fact]
    public async Task FoldersTabFollowsTheMapAndTheList()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "deep"));
        File(Path.Combine("docs", "deep", "x.txt"), 500);
        File(Path.Combine("docs", "y.txt"), 100);
        File("top.bin", 2000);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);
        Assert.Equal(StorageViewModel.TabFolders, storage.Tab);
        Assert.Equal(["top.bin", "docs"], storage.Folders.Rows.Select(r => r.Name));
        Assert.Equal(tree.Root, storage.Folder);
        Assert.Equal([_root], storage.Crumbs.Select(c => c.Name));

        // Enter (or a double-click) on a folder shows it on the map; on a file, the folder it is in.
        var docs = storage.Folders.Rows[1].Entry;
        var deep = tree.Children(docs).Single(tree.IsFolder);
        var x = tree.Children(deep).Single();
        storage.OpenFolder(x);
        Assert.Equal(deep, storage.Folder);
        Assert.Equal([_root, "docs", "deep"], storage.Crumbs.Select(c => c.Name));
        storage.OpenFolder(docs);
        Assert.Equal(docs, storage.Folder);

        // A click on the map selects the row, with its folders expanded; the row selects the entry back.
        storage.SelectedEntry = x;
        Assert.Equal(x, storage.SelectedRow?.Entry);
        Assert.Equal(["top.bin", "docs", "deep", "x.txt", "y.txt"], storage.Folders.Rows.Select(r => r.Name));
        storage.SelectedRow = storage.Folders.Rows[0];
        Assert.Equal(storage.Folders.Rows[0].Entry, storage.SelectedEntry);

        // Left on a closed entry selects its folder, Left again closes it; Right opens it and then moves into it.
        var deepRow = storage.Folders.Rows[2];
        storage.CollapseOrSelectParent(storage.Folders.Rows[3]);
        Assert.Same(deepRow, storage.SelectedRow);
        storage.CollapseOrSelectParent(deepRow);
        Assert.False(deepRow.IsExpanded);
        storage.ExpandOrSelectChild(deepRow);
        Assert.True(deepRow.IsExpanded);
        storage.ExpandOrSelectChild(deepRow);
        Assert.Equal(x, storage.SelectedEntry);

        storage.CloseResultCommand.Execute(null);
        Assert.Empty(storage.Folders.Rows);
        Assert.Empty(storage.Crumbs);
        Assert.Equal(-1, storage.SelectedEntry);
    }

    [Fact]
    public async Task FindDuplicatesKeepsTheFirstCopyWithoutATick()
    {
        // Duplicates in the temp folder (under AppData) are protected, so this checks the list, not the Recycle Bin.
        var bytes = (int)Optimizer.Core.Tools.StorageAnalyzer.DuplicateMinBytes;
        File("a.bin", bytes);
        File("b.bin", bytes);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = 3;
        Assert.Empty(storage.Duplicates);
        await storage.FindDuplicatesCommand.ExecuteAsync(null);
        // Protected (AppData): the analysis never offers these, so no group is listed.
        Assert.Empty(storage.Duplicates);
        Assert.True(storage.DuplicatesSearched);
        Assert.False(_host.Runner.Work.IsActive);
    }

    /// <summary>
    /// A game library inside the analyzed folder is protected: the library, a file in it and the folder that holds it are
    /// refused with their reason, and Recycle stays off; a plain file is offered. Nothing reaches the recorder.
    /// </summary>
    [Fact]
    public async Task SelectionOfAProtectedFolderShowsTheReason()
    {
        var library = Path.Combine(_root, "games", "library");
        Directory.CreateDirectory(library);
        File(Path.Combine("games", "library", "game.bin"), 500);
        File("plain.bin", 100);
        _host.Profile = _host.Profile! with { Software = new SoftwareInfo([], [], [library]) };
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);
        Assert.Null(storage.SelectionBlock);
        Assert.False(storage.RecycleCommand.CanExecute(null));

        storage.SelectedEntry = Entry(tree, "games");
        Assert.Equal([Entry(tree, "games")], storage.SelectedEntries);
        Assert.Equal(Loc.Instance["Storage_BlockContainsProtected"], storage.SelectionBlock);
        Assert.False(storage.RecycleCommand.CanExecute(null));
        Assert.True(storage.OpenInExplorerCommand.CanExecute(null));
        Assert.True(storage.CopyPathCommand.CanExecute(null));

        storage.SelectedEntry = Entry(tree, "library");
        Assert.Equal(Loc.Instance["Storage_BlockProtected"], storage.SelectionBlock);
        storage.SelectedEntry = Entry(tree, "plain.bin");
        Assert.Null(storage.SelectionBlock);
        Assert.True(storage.RecycleCommand.CanExecute(null));

        // A list that mixes an offered file with a protected one: the reason of the protected one, and Recycle is off.
        storage.Tab = StorageViewModel.TabLargest;
        await storage.RowsBuilt;
        Assert.Empty(storage.SelectedEntries);
        storage.LargestFiles.Single(f => f.Path.EndsWith("plain.bin", StringComparison.Ordinal)).Selected = true;
        Assert.True(storage.RecycleCommand.CanExecute(null));
        storage.LargestFiles.Single(f => f.Path.EndsWith("game.bin", StringComparison.Ordinal)).Selected = true;
        Assert.Equal(2, storage.SelectedEntries.Count);
        Assert.Equal(Loc.Instance["Storage_BlockProtected"], storage.SelectionBlock);
        Assert.False(storage.RecycleCommand.CanExecute(null));
        await storage.RecycleCommand.ExecuteAsync(null);
        Assert.Empty(_recycled);
        Assert.Empty(_dialogs.Asked);
    }

    /// <summary>
    /// A folder picked in the tree goes to the recorder (which reports success and moves nothing) after the confirmation;
    /// it leaves the tree, the root shrinks by its size, and the list no longer shows it.
    /// </summary>
    [Fact]
    public async Task RecycleRemovesTheEntryFromTheTree()
    {
        var old = Path.Combine(_root, "old");
        Directory.CreateDirectory(old);
        File(Path.Combine("old", "a.bin"), 500);
        File(Path.Combine("old", "b.bin"), 300);
        File("keep.bin", 100);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);
        var before = tree.Size(tree.Root);
        var entry = Entry(tree, "old");
        var changes = 0;
        storage.TreeChanged += (_, _) => changes++;

        storage.SelectedEntry = entry;
        await storage.RecycleCommand.ExecuteAsync(null);

        Assert.Equal([(old, true)], _recycled);
        Assert.Single(_dialogs.Asked);
        Assert.True(tree.IsRemoved(entry));
        Assert.Equal(before - 800, tree.Size(tree.Root));
        Assert.DoesNotContain(storage.Folders.Rows, r => r.Name == "old");
        Assert.Equal(-1, storage.SelectedEntry);
        Assert.Empty(storage.SelectedEntries);
        Assert.True(changes > 0);
        Assert.Equal((Loc.Instance.Format("Storage_Recycled", 1, 0), InfoBarSeverity.Success), _host.Results[^1]);
        Assert.False(_host.Runner.Work.IsActive);
        // The folder is still there: the recorder moved nothing.
        Assert.True(Directory.Exists(old));
    }

    /// <summary>A file the Recycle Bin did not take stays in the tree and the list, and the result bar names it.</summary>
    [Fact]
    public async Task FailedRecycleKeepsTheEntry()
    {
        var a = File("a.bin", 500);
        File("b.bin", 300);
        _failing.Add(a);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);
        var before = tree.Size(tree.Root);
        storage.Tab = StorageViewModel.TabLargest;
        await storage.RowsBuilt;
        storage.LargestFiles.Single(f => f.Path == a).Selected = true;

        await storage.RecycleCommand.ExecuteAsync(null);

        Assert.Equal([(a, false)], _recycled);
        Assert.False(tree.IsRemoved(Entry(tree, "a.bin")));
        Assert.Equal(before, tree.Size(tree.Root));
        Assert.Contains(storage.LargestFiles, f => f.Path == a);
        var (text, severity) = _host.Results[^1];
        Assert.StartsWith(Loc.Instance.Format("Storage_Recycled", 0, 1), text);
        Assert.Contains(a, text);
        Assert.Equal(InfoBarSeverity.Warning, severity);
    }

    /// <summary>
    /// Whatever is, or lies in, a cloud folder or another reparse point is refused with its reason, in the folder tab and in
    /// the file lists: the move would refuse it (any reparse point on the path). The tree comes from the scanner seam; none
    /// of its paths exist, and nothing reaches the recorder.
    /// </summary>
    [Fact]
    public async Task WhatLiesInACloudFolderShowsTheReason()
    {
        var b = new DiskTree.Builder(_root);
        var oneDrive = b.AddFolder(b.Root, "OneDrive", default, EntryFlags.NonLinkReparse | EntryFlags.CloudSync);
        var docs = b.AddFolder(oneDrive, "Docs", default, EntryFlags.None);
        b.AddFile(docs, "report.docx", 900, -1, default, EntryFlags.None);
        b.AddFile(b.Root, "compact.bin", 800, -1, default, EntryFlags.NonLinkReparse);
        b.AddFile(b.Root, "file link.txt", 700, -1, default, EntryFlags.ReparsePoint);
        b.AddFile(b.Root, "plain.bin", 100, -1, default, EntryFlags.None);
        var built = b.Build(ScanReader.Folders, TimeSpan.Zero);
        var storage = Storage((_, _, _) => Task.FromResult(built));
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);

        storage.SelectedEntry = Entry(tree, "report.docx");
        Assert.Equal(Loc.Instance["Storage_BlockCloud"], storage.SelectionBlock);
        Assert.False(storage.RecycleCommand.CanExecute(null));
        storage.SelectedEntry = Entry(tree, "Docs");
        Assert.Equal(Loc.Instance["Storage_BlockCloud"], storage.SelectionBlock);
        storage.SelectedEntry = Entry(tree, "compact.bin");
        Assert.Equal(Loc.Instance["Storage_BlockReparse"], storage.SelectionBlock);
        storage.SelectedEntry = Entry(tree, "file link.txt");
        Assert.Equal(Loc.Instance["Storage_BlockLink"], storage.SelectionBlock);
        storage.SelectedEntry = Entry(tree, "plain.bin");
        Assert.Null(storage.SelectionBlock);

        storage.Tab = StorageViewModel.TabLargest;
        await storage.RowsBuilt;
        Assert.Equal(Loc.Instance["Storage_BlockCloud"], storage.LargestFiles.Single(f => f.Path.EndsWith("report.docx", StringComparison.Ordinal)).BlockText);
        Assert.Null(storage.LargestFiles.Single(f => f.Path.EndsWith("plain.bin", StringComparison.Ordinal)).BlockText);
        storage.LargestFiles.Single(f => f.Path.EndsWith("report.docx", StringComparison.Ordinal)).Selected = true;
        Assert.Equal(Loc.Instance["Storage_BlockCloud"], storage.SelectionBlock);
        await storage.RecycleCommand.ExecuteAsync(null);
        Assert.Empty(_recycled);
        Assert.Empty(_dialogs.Asked);
    }

    /// <summary>Entries the reader could not see are counted in the result line, one in the singular.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task LostEntriesAreCountedInTheResultLine(int lost)
    {
        var b = new DiskTree.Builder(_root);
        b.AddFile(b.Root, "a.bin", 100, -1, default, EntryFlags.None);
        var built = b.Build(ScanReader.FileTable, TimeSpan.FromSeconds(1), lost);
        var storage = Storage((_, _, _) => Task.FromResult(built));
        await storage.AnalyzeFolderAsync(_root);

        var text = Assert.IsType<string>(storage.ResultText);
        var plain = Loc.Instance.Format("Storage_ResultOneFile", CleanupViewModel.Size(100), 1, Loc.Instance["Storage_ReaderFileTable"], 1.0);
        Assert.Equal(lost switch
        {
            0 => plain,
            1 => Loc.Instance.Format("Storage_ResultLostOne", plain),
            _ => Loc.Instance.Format("Storage_ResultLost", plain, lost),
        }, text);
        if (lost > 0) Assert.NotEqual(plain, text);
    }

    /// <summary>
    /// A junction in the tree is not offered: the move would refuse it (it holds no folder of its own), so the bar says
    /// so first. Only the link is created and removed; its target stays.
    /// </summary>
    [Fact]
    public async Task ALinkIsNotOffered()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        File(Path.Combine("target", "t.bin"), 100);
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
               { CreateNoWindow = true, UseShellExecute = false })!)
            mk.WaitForExit();
        try
        {
            Assert.True(Directory.Exists(link));
            var storage = Storage();
            await storage.AnalyzeFolderAsync(_root);
            var tree = Assert.IsType<DiskTree>(storage.Tree);
            storage.SelectedEntry = Entry(tree, "link");
            Assert.Equal(Loc.Instance["Storage_BlockLink"], storage.SelectionBlock);
            Assert.False(storage.RecycleCommand.CanExecute(null));
            storage.SelectedEntry = Entry(tree, "target");
            Assert.Null(storage.SelectionBlock);
        }
        finally
        {
            // The link alone, not what it points to.
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    /// <summary>
    /// A name ending in a dot or space (made through \\?\) would be checked and moved as its trimmed twin: the entry, a
    /// file below it and such a file in a list are refused with that reason, and the twin is offered.
    /// </summary>
    [Fact]
    public async Task ANameEndingInADotOrSpaceIsNotOffered()
    {
        var odd = Path.Combine(_root, "odd.");
        var inside = Path.Combine(odd, "inside.bin");
        var spaced = Path.Combine(_root, "report ");
        Directory.CreateDirectory(@"\\?\" + odd);
        System.IO.File.WriteAllBytes(@"\\?\" + inside, new byte[300]);
        System.IO.File.WriteAllBytes(@"\\?\" + spaced, new byte[200]);
        Directory.CreateDirectory(Path.Combine(_root, "odd"));
        File("report", 100);
        try
        {
            var storage = Storage();
            await storage.AnalyzeFolderAsync(_root);
            var tree = Assert.IsType<DiskTree>(storage.Tree);
            var name = Loc.Instance["Storage_BlockName"];
            storage.SelectedEntry = Entry(tree, "odd.");
            Assert.Equal(name, storage.SelectionBlock);
            Assert.False(storage.RecycleCommand.CanExecute(null));
            storage.SelectedEntry = Entry(tree, "inside.bin");
            Assert.Equal(name, storage.SelectionBlock);
            storage.SelectedEntry = Entry(tree, "odd");
            Assert.Null(storage.SelectionBlock);

            storage.Tab = StorageViewModel.TabLargest;
            await storage.RowsBuilt;
            Assert.Equal(name, storage.LargestFiles.Single(f => f.Path == spaced).BlockText);
            Assert.Null(storage.LargestFiles.Single(f => f.Path == Path.Combine(_root, "report")).BlockText);
            await storage.RecycleCommand.ExecuteAsync(null);
            Assert.Empty(_recycled);
        }
        finally
        {
            // The trimmed names are reachable only through \\?\.
            foreach (var f in new[] { inside, spaced }) if (System.IO.File.Exists(@"\\?\" + f)) System.IO.File.Delete(@"\\?\" + f);
            if (Directory.Exists(@"\\?\" + odd)) Directory.Delete(@"\\?\" + odd);
        }
    }

    /// <summary>
    /// The file lists add their rows only after the path checks, which read the disk, ran on a worker thread; a list
    /// dropped meanwhile (Close result) gets none, and a list built again from checks already made gets them at once.
    /// </summary>
    [Fact]
    public async Task FileRowsComeAfterTheirChecksAndNotForADroppedList()
    {
        File("a.bin", 300);
        File("b.bin", 200);
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = StorageViewModel.TabLargest;
        storage.CloseResultCommand.Execute(null);
        await storage.RowsBuilt;
        Assert.Empty(storage.LargestFiles);

        await storage.AnalyzeFolderAsync(_root);
        await storage.RowsBuilt;
        Assert.Equal(2, storage.LargestFiles.Count);
        // A removal builds the list again from the checks already made: the rows are there when the recycle returns.
        storage.LargestFiles.Single(f => f.Path.EndsWith("b.bin", StringComparison.Ordinal)).Selected = true;
        await storage.RecycleCommand.ExecuteAsync(null);
        Assert.True(storage.RowsBuilt.IsCompleted);
        Assert.Equal([Path.Combine(_root, "a.bin")], storage.LargestFiles.Select(f => f.Path));
    }

    /// <summary>
    /// The drive list read again during an analysis (a language switch) can leave another drive picked when the drive
    /// is gone: the result of the drive analyzed is then dropped, not shown under the other one.
    /// </summary>
    [Fact]
    public async Task AnAnalysisOfADriveThePickerNoLongerShowsIsDropped()
    {
        var release = new TaskCompletionSource();
        var b = new DiskTree.Builder(@"Q:");
        b.AddFile(b.Root, "a.bin", 100, -1, default, EntryFlags.None);
        var built = b.Build(ScanReader.Folders, TimeSpan.Zero);
        var storage = Storage(async (_, _, _) =>
        {
            await release.Task;
            return built;
        });
        await storage.EnsureLoadedAsync();
        storage.Drives.Add(new DriveRow(@"Q:", 1000, 500));
        storage.SelectedDrive = storage.Drives[^1];
        var analysis = storage.AnalyzeCommand.ExecuteAsync(null);
        // As LoadAsync does when the drive is no longer listed: the first drive is picked.
        storage.SelectedDrive = storage.Drives[0];
        release.SetResult();
        await analysis;
        Assert.Null(storage.Tree);
        Assert.Null(storage.ResultText);
        Assert.False(storage.IsAnalyzing);
    }

    /// <summary>
    /// An extension picked in a group stays picked when the lists are built again (a removal, a language switch) even
    /// when it now ranks past the 200 rows the group shows: it is added below them.
    /// </summary>
    [Fact]
    public async Task APickedExtensionPastTheShownRowsSurvivesARebuild()
    {
        for (var i = 0; i < 202; i++) File($"f.e{i:D3}", 1000 + i);
        var small = File("tiny.zzz", 10);
        var storage = Storage();
        await storage.EnsureLoadedAsync();
        await storage.AnalyzeFolderAsync(_root);
        storage.Tab = StorageViewModel.TabTypes;
        var group = Assert.Single(storage.TypeGroups);
        group.IsExpanded = true;
        Assert.DoesNotContain(group.Extensions, t => t.Extension == "zzz");
        // Picked as the page would after a rebuild of a list where it ranked higher.
        var row = group.Show("zzz");
        Assert.NotNull(row);
        storage.SelectedType = row;
        await storage.RowsBuilt;
        Assert.Equal([small], storage.TypeFiles.Select(f => f.Path));

        // A removal elsewhere builds the File types rows again when the tab opens.
        storage.Tab = StorageViewModel.TabLargest;
        await storage.RowsBuilt;
        storage.LargestFiles[0].Selected = true;
        await storage.RecycleCommand.ExecuteAsync(null);
        storage.Tab = StorageViewModel.TabTypes;
        await storage.RowsBuilt;
        Assert.Equal("zzz", storage.SelectedType?.Extension);
        Assert.Equal([small], storage.TypeFiles.Select(f => f.Path));
        Assert.Contains(storage.TypeGroups.Single().Extensions, t => t.Extension == "zzz");
        Assert.Null(storage.TypeGroups.Single().Show("none"));
    }

    /// <summary>"No" in the confirmation: nothing reaches the recorder and the tree stays as it was.</summary>
    [Fact]
    public async Task RecycleAsksFirst()
    {
        File("a.bin", 500);
        _dialogs.Answer = false;
        var storage = Storage();
        await storage.AnalyzeFolderAsync(_root);
        var tree = Assert.IsType<DiskTree>(storage.Tree);
        storage.SelectedEntry = Entry(tree, "a.bin");
        await storage.RecycleCommand.ExecuteAsync(null);
        Assert.Single(_dialogs.Asked);
        Assert.Empty(_recycled);
        Assert.False(tree.IsRemoved(Entry(tree, "a.bin")));
    }
}
