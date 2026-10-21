using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Optimizer.Core.Storage;
using Optimizer.Core.Storage.Mft;

namespace Optimizer.Core.Tests;

/// <summary>
/// The file table tree on a real NTFS $MFT: Fixtures/mft holds the raw $MFT of a 64 MB volume made in the test VM by
/// make-fixture.ps1, and a manifest of every entry from a folder walk of that volume. No volume is opened here.
/// </summary>
public class MftTreeBuilderTests
{
    private static readonly DateTime T = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly Lazy<Fixture> Data = new(Fixture.Load);

    private sealed record ManifestEntry(string Path, bool Folder, long Size, long OnDisk, string? FileId, uint Attributes);

    private sealed class Fixture
    {
        public required byte[] Mft { get; init; }
        public required IReadOnlyList<MftRecord> Records { get; init; }
        public required IReadOnlyList<ManifestEntry> Entries { get; init; }
        public required IReadOnlyList<string> Unreadable { get; init; }
        public required IReadOnlyList<string> Deleted { get; init; }
        public required DiskTree Tree { get; init; }
        /// <summary>Tree entries by path relative to the volume root.</summary>
        public required IReadOnlyDictionary<string, int> ByPath { get; init; }

        public static Fixture Load()
        {
            var folder = Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "mft");
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "test-volume.json")));
            var made = json.RootElement.GetProperty("made");
            var recordSize = made.GetProperty("bytesPerFileRecord").GetInt32();
            var sectorSize = made.GetProperty("bytesPerSector").GetInt32();

            byte[] mft;
            using (var gz = new GZipStream(File.OpenRead(Path.Combine(folder, "test-volume.mft.gz")), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                mft = ms.ToArray();
            }
            Assert.Equal(made.GetProperty("mftValidDataLength").GetInt64(), mft.Length);

            // Parse applies the fixups in place, so each record is parsed once, from a copy kept apart from Mft.
            var work = (byte[])mft.Clone();
            var records = new List<MftRecord>();
            for (var n = 0; n < work.Length / recordSize; n++)
                if (MftRecordParser.Parse(work.AsSpan(n * recordSize, recordSize), sectorSize, (uint)n) is { } r) records.Add(r);

            var entries = json.RootElement.GetProperty("entries").EnumerateArray().Select(e => new ManifestEntry(
                e.GetProperty("path").GetString()!,
                e.GetProperty("folder").GetBoolean(),
                e.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                e.TryGetProperty("onDisk", out var onDisk) ? onDisk.GetInt64() : 0,
                e.TryGetProperty("fileId", out var id) ? id.GetString() : null,
                (uint)e.GetProperty("attributes").GetInt32())).ToList();

            var tree = MftTreeBuilder.Build(records, @"T:\", TimeSpan.FromSeconds(1));
            var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 1; i < tree.Count; i++) byPath.Add(tree.FullPath(i)[tree.RootPath.Length..], i);

            return new Fixture
            {
                Mft = mft,
                Records = records,
                Entries = entries,
                Unreadable = json.RootElement.GetProperty("unreadable").EnumerateArray().Select(e => e.GetString()!).ToList(),
                Deleted = json.RootElement.GetProperty("deleted").EnumerateArray().Select(e => e.GetString()!).ToList(),
                Tree = tree,
                ByPath = byPath,
            };
        }

        public bool UnderUnreadable(string path) => Unreadable.Any(u => path.StartsWith(u + @"\", StringComparison.Ordinal));

        public int Entry(string path) =>
            ByPath.TryGetValue(path, out var i) ? i : throw new Xunit.Sdk.XunitException($"No tree entry for {path}");
    }

    [Fact]
    public void FixtureTreeMatchesTheManifest()
    {
        var f = Data.Value;
        var t = f.Tree;
        Assert.Equal(ScanReader.FileTable, t.Reader);
        Assert.Equal(0, t.Lost);

        foreach (var folder in f.Entries.Where(e => e.Folder))
            Assert.True(t.IsFolder(f.Entry(folder.Path)), folder.Path);

        // Every file once: a file with several names is one entry, under exactly one of them.
        var files = f.Entries.Where(e => !e.Folder).GroupBy(e => e.FileId).ToList();
        foreach (var file in files)
        {
            var found = file.Where(e => f.ByPath.ContainsKey(e.Path)).ToList();
            Assert.True(found.Count == 1, $"{file.First().Path}: {found.Count} of {file.Count()} names in the tree");
            var i = f.Entry(found[0].Path);
            Assert.False(t.IsFolder(i), found[0].Path);
            Assert.Equal(found[0].Size, t.Size(i));
        }

        // Nothing else: no NTFS metadata files, no DOS names, no deleted file. The folders the walk could not open
        // (System Volume Information) are only in the tree.
        var named = f.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(f.ByPath.Keys, p => !named.Contains(p) && !f.UnderUnreadable(p));

        var hidden = f.Unreadable.Select(f.Entry).ToList();
        Assert.Equal(files.Sum(g => g.First().Size) + hidden.Sum(t.Size), t.Size(t.Root));
        Assert.Equal(files.Count + hidden.Sum(t.Files), t.Files(t.Root));
    }

    [Fact]
    public void HardLinkedFileCountsOnce()
    {
        var f = Data.Value;
        var t = f.Tree;
        // Record order: the base record holds the DOS name, then the Win32 name, then the two links (POSIX names).
        var i = f.Entry(@"links\first\shared-original.dat");
        Assert.Equal(20000, t.Size(i));
        Assert.Equal(20480, t.Allocated(i));
        Assert.False(f.ByPath.ContainsKey(@"links\second\shared-link-two.dat"));
        Assert.False(f.ByPath.ContainsKey(@"links\third\SHARED.DAT"));
        Assert.Equal(0, t.Files(f.Entry(@"links\second")));
        Assert.Equal(0, t.Files(f.Entry(@"links\third")));

        var links = f.Entry("links");
        Assert.Equal(1, t.Files(links));
        Assert.Equal(20000, t.Size(links));
    }

    [Fact]
    public void ManyLinksFileIsFoundThroughTheAttributeList()
    {
        var f = Data.Value;
        var t = f.Tree;
        var names = f.Entries.Where(e => e.Path.StartsWith(@"many\", StringComparison.Ordinal)).ToList();
        Assert.Equal(120, names.Count);
        Assert.Single(names.Select(e => e.FileId).Distinct());

        // The 120 names do not fit one record: the base record's attribute list is non-resident, so it names no
        // extension record, and the data and most names sit in extension records that point back to it.
        var record = (uint)(Convert.ToUInt64(names[0].FileId, 16) & 0x0000_FFFF_FFFF_FFFF);
        var baseRecord = f.Records.Single(r => r.Number == record);
        Assert.Null(baseRecord.DataSize);
        Assert.True(baseRecord.Names.Count < 120);

        var many = f.Entry("many");
        Assert.Equal(1, t.Files(many));
        var file = Assert.Single(t.Children(many));
        Assert.Contains(names, e => e.Path == @"many\" + t.Name(file));
        Assert.Equal(50000, t.Size(file));
        Assert.Equal(53248, t.Allocated(file)); // 13 clusters of 4 KB
    }

    [Fact]
    public void JunctionHasNoChildren()
    {
        var f = Data.Value;
        var t = f.Tree;
        var junction = f.Entry("junction-to-target");
        Assert.True(t.IsFolder(junction));
        Assert.True((t.Flags(junction) & EntryFlags.ReparsePoint) != 0);
        Assert.Equal(0, t.ChildCount(junction));
        Assert.Equal(0, t.Size(junction));

        // The target's content counts once, where it is.
        Assert.Equal(700, t.Size(f.Entry(@"junction-target\reached-through-the-junction.txt")));
        Assert.Equal(EntryFlags.Folder, t.Flags(f.Entry("junction-target")));
    }

    [Fact]
    public void DeletedFileIsAbsent()
    {
        var f = Data.Value;
        var t = f.Tree;
        var deleted = Assert.Single(f.Deleted);

        // Its record is still in the $MFT (marked unused), so leaving it out is the parser and builder's doing.
        var name = Encoding.Unicode.GetBytes(Path.GetFileName(deleted));
        Assert.True(f.Mft.AsSpan().IndexOf(name) >= 0);

        Assert.False(f.ByPath.ContainsKey(deleted));
        Assert.DoesNotContain(f.ByPath.Keys, p => p.EndsWith(Path.GetFileName(deleted), StringComparison.Ordinal));
        Assert.Equal(0, t.ChildCount(f.Entry(Path.GetDirectoryName(deleted)!)));
    }

    [Fact]
    public void AlternateStreamDoesNotChangeTheFileSize()
    {
        var f = Data.Value;
        var i = f.Entry(@"streams\with-alternate-stream.txt");
        Assert.Equal(300, f.Tree.Size(i));
        Assert.Equal(0, f.Tree.Allocated(i)); // resident: no clusters; the 4096-byte stream is not counted
    }

    [Fact]
    public void SparseAndCompressedFilesReportTheSpaceInUse()
    {
        var f = Data.Value;
        foreach (var path in new[] { @"sparse\sparse-ten-megabytes.bin", @"compressed\compressible-one-megabyte.txt" })
        {
            var expected = f.Entries.Single(e => e.Path == path);
            var i = f.Entry(path);
            Assert.Equal(expected.Size, f.Tree.Size(i));
            Assert.Equal(expected.OnDisk, f.Tree.Allocated(i)); // GetCompressedFileSize in the VM
            Assert.True(f.Tree.Allocated(i) < f.Tree.Size(i));
        }
    }

    [Fact]
    public void DeepFoldersKeepTheirLongNames()
    {
        var f = Data.Value;
        var path = string.Join('\\', Enumerable.Range(1, 12).Select(n => $"level-{n:D2}-folder")) + @"\bottom-of-the-tree.txt";
        var i = f.Entry(path);
        Assert.Equal(1500, f.Tree.Size(i));
        Assert.Equal(@"T:\" + path, f.Tree.FullPath(i));
        Assert.Equal(1528, f.Tree.Size(f.Entry("level-01-folder"))); // with note.txt two levels down
    }

    // Built in memory: the cases a healthy volume does not show. Every record has sequence number 1, as Ref expects.

    private static MftRecord Rec(uint number, uint parent, string name, bool folder = false, long? size = 0, long? allocated = 0,
        uint attributes = 0, uint reparseTag = 0, byte ns = 1, ushort sequence = 1) =>
        new(number, sequence, 0, folder, [new MftName(Ref(parent), name, ns)], folder ? null : size,
            folder ? null : allocated, T, attributes, reparseTag);

    private static MftRecord FileRecord(uint number, params MftName[] names) => new(number, 1, 0, false, names, 1, 0, T, 0x20, 0);

    private static ulong Ref(uint number, ushort sequence = 1) => (ulong)sequence << 48 | number;

    private static MftRecord Root() => Rec(5, 5, ".", folder: true, attributes: 0x6);

    [Fact]
    public void OrphansAndLoopsCountAsLost()
    {
        var t = MftTreeBuilder.Build([
            Root(),
            Rec(40, 99, "parent missing.txt", size: 10),
            Rec(41, 42, "loop a", folder: true),
            Rec(42, 41, "loop b", folder: true),
            Rec(43, 44, "under a file.txt", size: 10),
            Rec(44, 5, "a file.txt", size: 5),
            FileRecord(45), // no name at all
        ], @"X:\", TimeSpan.Zero);

        Assert.Equal(5, t.Lost);
        Assert.Equal(new[] { "a file.txt" }, t.Children(t.Root).Select(t.Name).ToArray());
        Assert.Equal(5, t.Size(t.Root));
    }

    [Fact]
    public void AChildOfAReusedFolderRecordIsLost()
    {
        // Read while the volume changes: the file still names record 40 with sequence 1, but that record was freed and
        // reused for another folder (sequence 2). The file must not appear under the unrelated folder.
        var t = MftTreeBuilder.Build([
            Root(),
            Rec(40, 5, "unrelated", folder: true, sequence: 2),
            Rec(41, 40, "phantom.txt", size: 100),
            new MftRecord(42, 1, 0, false, [new MftName(Ref(40, 2), "current.txt", 1)], 7, 0, T, 0x20, 0),
        ], @"X:\", TimeSpan.Zero);

        var folder = Assert.Single(t.Children(t.Root));
        Assert.Equal(new[] { "current.txt" }, t.Children(folder).Select(t.Name).ToArray());
        Assert.Equal(1, t.Lost);
        Assert.Equal(7, t.Size(t.Root));
    }

    [Fact]
    public void AnExtensionRecordOfAnEarlierUseOfItsBaseIsNotFolded()
    {
        // Record 50 now holds an unrelated file (sequence 3); record 30 is a leftover extension of its earlier use.
        var stale = new MftRecord(30, 1, Ref(50, 2), false, [new MftName(Ref(5), "old name.bin", 1)], 9000, 12288, default, 0, 0);
        // Its own name is a POSIX link and its data is missing, so a wrongly folded Win32 name and data would show.
        var current = new MftRecord(50, 3, 0, false, [new MftName(Ref(5), "new.txt", 0)], null, null, T, 0x20, 0);

        var t = MftTreeBuilder.Build([Root(), stale, current], @"X:\", TimeSpan.Zero);

        var file = Assert.Single(t.Children(t.Root));
        Assert.Equal("new.txt", t.Name(file));
        Assert.Equal(0, t.Size(file));
        Assert.Equal(0, t.Lost);
    }

    [Fact]
    public void NtfsMetadataFilesAreLeftOutAndNotLost()
    {
        var t = MftTreeBuilder.Build([
            Rec(0, 5, "$MFT", size: 262144, allocated: 262144, attributes: 0x6, ns: 3),
            Root(),
            Rec(11, 5, "$Extend", folder: true, attributes: 0x6, ns: 3),
            Rec(12, 5, "", size: 0), // reserved, in use, no real name
            Rec(24, 11, "$Quota", size: 0, ns: 0),
            Rec(27, 11, "$RmMetadata", folder: true, ns: 0),
            Rec(28, 27, "$Repair", size: 100, ns: 0),
            Rec(40, 5, "user.txt", size: 7),
        ], @"X:\", TimeSpan.Zero);

        Assert.Equal(0, t.Lost);
        Assert.Equal(new[] { "user.txt" }, t.Children(t.Root).Select(t.Name).ToArray());
        Assert.Equal(7, t.Size(t.Root));
    }

    [Fact]
    public void Win32NameWinsOverTheDosNameAndPosixNamesAreKept()
    {
        var file = FileRecord(40, new MftName(Ref(5), "LONGFI~1.TXT", 2), new MftName(Ref(5), "Long file name.txt", 1));
        var dosOnly = FileRecord(41, new MftName(Ref(5), "ONLY.TXT", 2));
        var posix = Rec(42, 5, "posix:name", ns: 0, size: 4);

        var t = MftTreeBuilder.Build([Root(), file, dosOnly, posix], @"X:\", TimeSpan.Zero);

        Assert.Equal(new[] { "Long file name.txt", "ONLY.TXT", "posix:name" },
            t.Children(t.Root).Select(t.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AHardLinkedFileCountsUnderItsWin32NameEvenWhenAPosixLinkComesFirst()
    {
        // Windows stores extra hard links as POSIX names, and attribute order can put one before the original name.
        var file = FileRecord(40,
            new MftName(Ref(60), "link.txt", 0),
            new MftName(Ref(61), "ORIGIN~1.TXT", 2),
            new MftName(Ref(61), "original name.txt", 1),
            new MftName(Ref(61), "second link.txt", 0));
        var both = FileRecord(41, new MftName(Ref(60), "other link.txt", 0), new MftName(Ref(61), "SHORT.TXT", 3));

        var t = MftTreeBuilder.Build([Root(), file, both, Rec(60, 5, "a", folder: true), Rec(61, 5, "b", folder: true)],
            @"X:\", TimeSpan.Zero);

        var a = t.Children(t.Root).Single(c => t.Name(c) == "a");
        var b = t.Children(t.Root).Single(c => t.Name(c) == "b");
        Assert.Equal(0, t.ChildCount(a));
        Assert.Equal(new[] { "SHORT.TXT", "original name.txt" },
            t.Children(b).Select(t.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, t.Files(t.Root));
    }

    [Fact]
    public void ExtensionRecordsFoldIntoTheirBaseInAnyOrder()
    {
        // The extension record comes first and holds the data and the Win32 name; the base has only a POSIX link.
        var extension = new MftRecord(30, 1, Ref(50), false, [new MftName(Ref(60), "second.bin", 1)], 9000, 12288, default, 0, 0);
        var baseRecord = new MftRecord(50, 1, 0, false, [new MftName(Ref(61), "first.bin", 0)], null, null, T, 0x20, 0);
        var t = MftTreeBuilder.Build([
            extension, Root(), baseRecord, Rec(60, 5, "a", folder: true), Rec(61, 5, "b", folder: true),
        ], @"X:\", TimeSpan.Zero);

        Assert.Equal(1, t.Files(t.Root));
        var a = t.Children(t.Root).Single(c => t.Name(c) == "a");
        var file = Assert.Single(t.Children(a));
        Assert.Equal("second.bin", t.Name(file));
        Assert.Equal(9000, t.Size(file));
        Assert.Equal(12288, t.Allocated(file));
        Assert.Equal(T, t.LastWriteUtc(file));
    }

    [Fact]
    public void AFileWithoutDataTakesNoSpaceAndKeepsTheTotalsKnown()
    {
        var noData = new MftRecord(40, 1, 0, false, [new MftName(Ref(5), "odd.bin", 1)], null, null, T, 0x20, 0);
        var t = MftTreeBuilder.Build([Root(), noData, Rec(41, 5, "normal.bin", size: 100, allocated: 4096)], @"X:\", TimeSpan.Zero);

        var odd = t.Children(t.Root).Single(c => t.Name(c) == "odd.bin");
        Assert.Equal(0, t.Size(odd));
        Assert.Equal(0, t.Allocated(odd));
        Assert.Equal(4096, t.Allocated(t.Root));
    }

    /// <summary>
    /// Cloud folders are entered and flagged as cloud reparse points (the page refuses what lies in them); links are
    /// listed, flagged and not entered, files as well as folders; other reparse points are flagged but not as cloud.
    /// </summary>
    [Fact]
    public void CloudFoldersKeepTheirChildrenAndLinksDoNot()
    {
        const uint reparse = 0x400, cloud = 0x9000_601A, mountPoint = 0xA000_0003, symlink = 0xA000_000C, wof = 0x8000_0017;
        var t = MftTreeBuilder.Build([
            Root(),
            Rec(40, 5, "OneDrive", folder: true, attributes: reparse, reparseTag: cloud),
            Rec(41, 40, "online only.docx", size: 5000, allocated: 0, attributes: 0x20 | reparse | 0x400000, reparseTag: cloud),
            Rec(42, 5, "junction", folder: true, attributes: reparse, reparseTag: mountPoint),
            Rec(43, 5, "dir link", folder: true, attributes: reparse, reparseTag: symlink),
            Rec(44, 5, "file link.txt", attributes: 0x20 | reparse, reparseTag: symlink),
            Rec(45, 42, "impossible child.txt", size: 1),
            Rec(46, 5, "pagefile.sys", size: 64, attributes: 0x26),
            Rec(47, 5, "compact.bin", size: 8000, allocated: 4096, attributes: 0x20 | reparse, reparseTag: wof),
        ], @"X:\", TimeSpan.Zero);

        int Named(string n) => t.Children(t.Root).Single(c => t.Name(c) == n);
        var oneDrive = Named("OneDrive");
        Assert.Equal(EntryFlags.Folder | EntryFlags.NonLinkReparse | EntryFlags.CloudSync, t.Flags(oneDrive));
        var online = Assert.Single(t.Children(oneDrive));
        Assert.Equal(EntryFlags.CloudOnly | EntryFlags.NonLinkReparse | EntryFlags.CloudSync, t.Flags(online));
        Assert.Equal(5000, t.Size(oneDrive));
        Assert.Equal(RecycleBlock.CloudFolder, RecycleRules.CheckEntry(t, online));
        Assert.Equal(EntryFlags.ReparsePoint, t.Flags(Named("file link.txt")));
        Assert.Equal(RecycleBlock.Link, RecycleRules.CheckEntry(t, Named("file link.txt")));
        Assert.Equal(EntryFlags.NonLinkReparse, t.Flags(Named("compact.bin")));

        foreach (var link in new[] { "junction", "dir link" })
        {
            Assert.Equal(EntryFlags.Folder | EntryFlags.ReparsePoint, t.Flags(Named(link)));
            Assert.Equal(0, t.ChildCount(Named(link)));
        }
        // The child under the junction is not placed, and not lost either: skipped on purpose, as the folder walk does.
        Assert.Equal(0, t.Lost);
        Assert.DoesNotContain(Enumerable.Range(0, t.Count), i => t.Name(i) == "impossible child.txt");
        Assert.Equal(EntryFlags.System, t.Flags(Named("pagefile.sys")));
    }

    [Fact]
    public void AReparseFolderWithoutAReadableTagIsEntered()
    {
        // As EnumerationScanner.IsLink: only a name surrogate tag makes a link, so both readers agree.
        var t = MftTreeBuilder.Build([
            Root(),
            Rec(40, 5, "unknown tag", folder: true, attributes: 0x400),
            Rec(41, 40, "inside.txt", size: 30),
        ], @"X:\", TimeSpan.Zero);

        var folder = Assert.Single(t.Children(t.Root));
        Assert.Equal(EntryFlags.Folder | EntryFlags.NonLinkReparse, t.Flags(folder));
        Assert.Equal(new[] { "inside.txt" }, t.Children(folder).Select(t.Name).ToArray());
        // Which reparse point it is cannot be told: the page refuses what lies in it with the general reason.
        Assert.Equal(RecycleBlock.OtherReparse, RecycleRules.CheckEntry(t, t.Children(folder).Single()));
        Assert.Equal(0, t.Lost);
    }
}
