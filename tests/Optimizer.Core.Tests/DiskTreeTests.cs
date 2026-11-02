using Optimizer.Core.Storage;

namespace Optimizer.Core.Tests;

/// <summary>The storage analyzer result model: bottom-up totals, ordering, removal and file type groups.</summary>
public class DiskTreeTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DiskTree Sample(out int a, out int small, out int big, out int b)
    {
        var bl = new DiskTree.Builder(@"C:\");
        a = bl.AddFolder(bl.Root, "a", T, EntryFlags.Folder);
        small = bl.AddFile(a, "small.bin", 10, 4096, T, EntryFlags.None);
        big = bl.AddFile(a, "big.bin", 30, 4096, T, EntryFlags.None);
        b = bl.AddFile(bl.Root, "b.bin", 25, 4096, T, EntryFlags.None);
        return bl.Build(ScanReader.Folders, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TotalsAreSummedBottomUpAndChildrenSortBySize()
    {
        var t = Sample(out var a, out _, out _, out _);

        Assert.Equal(65, t.Size(t.Root));
        Assert.Equal(3, t.Files(t.Root));
        Assert.Equal(new[] { "a", "b.bin" }, t.Children(t.Root).Select(t.Name).ToArray());
        Assert.Equal(new long[] { 30, 10 }, t.Children(a).Select(t.Size).ToArray());
    }

    [Theory]
    [InlineData(1)]    // grows from one slot
    [InlineData(5)]    // exactly the five entries: the builder's arrays become the tree's
    [InlineData(6)]    // one slot spare, within an eighth: kept as they are
    [InlineData(4096)] // far too large: the used part is copied
    public void APresizedBuilderGivesTheSameTree(int capacity)
    {
        var bl = new DiskTree.Builder(@"C:\", capacity);
        var a = bl.AddFolder(bl.Root, "a", T, EntryFlags.Folder);
        bl.AddFile(a, "small.bin", 10, 4096, T, EntryFlags.None);
        bl.AddFile(a, "big.bin", 30, 4096, T, EntryFlags.None);
        bl.AddFile(bl.Root, "b.bin", 25, 4096, T, EntryFlags.None);
        var t = bl.Build(ScanReader.Folders, TimeSpan.Zero);
        var expected = Sample(out _, out _, out _, out _);

        Assert.Equal(expected.Count, t.Count);
        for (var i = 0; i < t.Count; i++)
        {
            Assert.Equal(expected.FullPath(i), t.FullPath(i));
            Assert.Equal(expected.Size(i), t.Size(i));
            Assert.Equal(expected.Allocated(i), t.Allocated(i));
            Assert.Equal(expected.Files(i), t.Files(i));
            Assert.Equal(expected.Children(i), t.Children(i));
        }
        Assert.Equal(new[] { "a", "b.bin" }, t.Children(t.Root).Select(t.Name).ToArray());
    }

    [Fact]
    public void AStopwatchIsReadWhenTheTreeIsDone()
    {
        // The reader's time includes summing and sorting: it is read after them, not before.
        var bl = new DiskTree.Builder(@"C:\");
        for (var i = 0; i < 20_000; i++) bl.AddFile(bl.Root, $"f{i}.bin", i, -1, T, EntryFlags.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var started = watch.Elapsed;
        var t = bl.Build(ScanReader.Folders, watch);
        Assert.True(t.Elapsed > started);
        Assert.True(t.Elapsed <= watch.Elapsed);
    }

    [Fact]
    public void AllocatedIsUnknownWhenAnyChildIsUnknown()
    {
        var bl = new DiskTree.Builder(@"C:\");
        var f = bl.AddFolder(bl.Root, "f", T, EntryFlags.Folder);
        bl.AddFile(f, "x", 1, 4096, T, EntryFlags.None);
        bl.AddFile(f, "y", 1, -1, T, EntryFlags.None);
        var g = bl.AddFolder(bl.Root, "g", T, EntryFlags.Folder);
        bl.AddFile(g, "x", 1, 4096, T, EntryFlags.None);
        bl.AddFile(g, "y", 1, 8192, T, EntryFlags.None);
        var t = bl.Build(ScanReader.FileTable, TimeSpan.Zero);

        Assert.Equal(-1, t.Allocated(f));
        Assert.Equal(12288, t.Allocated(g));
        Assert.Equal(-1, t.Allocated(t.Root));
    }

    [Fact]
    public void RemoveSubtractsFromEveryAncestor()
    {
        var t = Sample(out var a, out var small, out var big, out var b);

        t.Remove(big);

        Assert.Equal(10, t.Size(a));
        Assert.Equal(35, t.Size(t.Root));
        Assert.Equal(2, t.Files(t.Root));
        Assert.Equal(4096, t.Allocated(a));
        Assert.DoesNotContain(big, t.Children(a));
        Assert.DoesNotContain(big, t.LargestFiles(10));
        // The type lists skip removed files: 65 bytes in 3 .bin files before, 35 in 2 after.
        Assert.Equal(new[] { new TypeTotal("bin", FileTypes.GroupOf("bin"), 35, 2) }, t.TypeTotals().ToArray());
        Assert.Equal(new[] { b, small }, t.LargestFilesOfType("bin", 10).ToArray());
    }

    [Fact]
    public void RemoveKeepsEveryListLargestFirst()
    {
        // The treemap stops at the first child too small to draw: a folder that shrank must not stay in front.
        var bl = new DiskTree.Builder(@"C:\");
        var a = bl.AddFolder(bl.Root, "a", T, EntryFlags.Folder);
        var deep = bl.AddFolder(a, "deep", T, EntryFlags.Folder);
        var huge = bl.AddFile(deep, "huge.bin", 100, -1, T, EntryFlags.None);
        var other = bl.AddFile(a, "other.bin", 20, -1, T, EntryFlags.None);
        var b = bl.AddFolder(bl.Root, "b", T, EntryFlags.Folder);
        bl.AddFile(b, "x.bin", 50, -1, T, EntryFlags.None);
        var c = bl.AddFile(bl.Root, "c.bin", 50, -1, T, EntryFlags.None);
        var t = bl.Build(ScanReader.Folders, TimeSpan.Zero);
        Assert.Equal(new[] { a, b, c }, t.Children(t.Root));
        Assert.Equal(new[] { deep, other }, t.Children(a));

        t.Remove(huge);

        // b and c are as large as each other and keep the order they were added in.
        Assert.Equal(new[] { b, c, a }, t.Children(t.Root));
        Assert.Equal(new[] { other, deep }, t.Children(a));
    }

    [Fact]
    public void ChildAccessorsMatchChildrenAndFollowRemove()
    {
        var t = Sample(out var a, out var small, out var big, out _);

        Assert.Equal(t.Children(t.Root).ToArray(), Enumerable.Range(0, t.ChildCount(t.Root)).Select(k => t.Child(t.Root, k)).ToArray());
        Assert.Equal(new[] { big, small }, Enumerable.Range(0, t.ChildCount(a)).Select(k => t.Child(a, k)).ToArray());
        Assert.Equal(0, t.ChildCount(big));
        Assert.False(t.IsRemoved(big));
        t.Remove(big);
        Assert.True(t.IsRemoved(big));
        Assert.False(t.IsRemoved(small));
        Assert.Equal(1, t.ChildCount(a));
        Assert.Equal(small, t.Child(a, 0));
    }

    [Fact]
    public void GroupDoesNotAllocate()
    {
        // The treemap asks for the group of every file it draws, each time its layout is built.
        var bl = new DiskTree.Builder(@"C:\");
        var mp4 = bl.AddFile(bl.Root, "Clip.MP4", 1, -1, T, EntryFlags.None);
        var none = bl.AddFile(bl.Root, "README", 1, -1, T, EntryFlags.None);
        var odd = bl.AddFile(bl.Root, "x.unknownext", 1, -1, T, EntryFlags.None);
        var t = bl.Build(ScanReader.Folders, TimeSpan.Zero);
        Assert.Equal(FileGroup.Video, t.Group(mp4));
        Assert.Equal(FileGroup.Other, t.Group(none));
        Assert.Equal(FileGroup.Other, t.Group(odd));
        Assert.Equal(FileGroup.Other, t.Group(t.Root));

        // The lowest of three rounds after a warm-up: a one-time allocation of the runtime on this thread (tiered
        // compilation swapping the loop's code during a parallel test run) lands in one round, one of Group in every round.
        long Round()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var k = 0; k < 1000; k++) { t.Group(mp4); t.Group(none); t.Group(odd); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Round();
        Assert.Equal(0, Enumerable.Range(0, 3).Min(_ => Round()));
    }

    [Fact]
    public void FullPathJoinsNames()
    {
        var bl = new DiskTree.Builder(@"C:\");
        var users = bl.AddFolder(bl.Root, "Users", T, EntryFlags.Folder);
        var x = bl.AddFile(users, "x.txt", 1, 1, T, EntryFlags.None);
        var t = bl.Build(ScanReader.Folders, TimeSpan.Zero);

        Assert.Equal(@"C:\Users\x.txt", t.FullPath(x));
        Assert.Equal(@"C:\", t.FullPath(t.Root));
        Assert.Equal(-1, t.Parent(t.Root));
        Assert.Equal(users, t.Parent(x));
    }

    [Fact]
    public void TypeTotalsGroupByExtension()
    {
        var bl = new DiskTree.Builder(@"C:\");
        var a = bl.AddFile(bl.Root, "a.MP4", 100, -1, T, EntryFlags.None);
        bl.AddFile(bl.Root, "b.mp4", 50, -1, T, EntryFlags.None);
        bl.AddFile(bl.Root, "c.zip", 70, -1, T, EntryFlags.None);
        var t = bl.Build(ScanReader.Folders, TimeSpan.Zero);

        Assert.Equal(
            new[] { new TypeTotal("mp4", FileGroup.Video, 150, 2), new TypeTotal("zip", FileGroup.Archives, 70, 1) },
            t.TypeTotals().ToArray());
        Assert.Equal(new[] { a }, t.LargestFilesOfType("mp4", 1).ToArray());
        Assert.Equal(new[] { a }, t.LargestFilesOfType(".MP4", 1).ToArray());
        Assert.Empty(t.LargestFilesOfType("mp", 1));
    }

    [Theory]
    [InlineData("mp4", FileGroup.Video)]
    [InlineData(".mkv", FileGroup.Video)]
    [InlineData("FLAC", FileGroup.Audio)]
    [InlineData("heic", FileGroup.Images)]
    [InlineData("iso", FileGroup.Archives)]
    [InlineData("vhdx", FileGroup.Archives)]
    [InlineData("msi", FileGroup.Programs)]
    [InlineData("dll", FileGroup.Programs)]
    [InlineData("pdf", FileGroup.Documents)]
    [InlineData("sys", FileGroup.System)]
    [InlineData("etl", FileGroup.System)]
    [InlineData("xyz", FileGroup.Other)]
    [InlineData("", FileGroup.Other)]
    public void GroupOfKnownExtensions(string extension, FileGroup expected) =>
        Assert.Equal(expected, FileTypes.GroupOf(extension));
}
