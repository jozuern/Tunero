using System.Buffers.Binary;
using System.Text;
using Optimizer.Core.Storage.Mft;

namespace Optimizer.Core.Tests;

/// <summary>
/// The NTFS file record and run list parser on records built in memory (no volume is opened). The parser reads bytes
/// from a possibly damaged volume, so the malformed cases must come back as null, never as an exception.
/// </summary>
public class MftParserTests
{
    private static readonly DateTime T = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private const ulong RootRef = 0x0005_0000_0000_0005; // record 5, sequence 5

    private static MftRecord? Parse(byte[] bytes, uint number = 40, int bytesPerSector = 512) =>
        MftRecordParser.Parse(bytes, bytesPerSector, number);

    [Fact]
    public void ParsesAFileWithResidentData()
    {
        var bytes = new RecordBuilder()
            .StandardInformation(T, 0x20)
            .FileName(RootRef, "readme.txt", 3)
            .Resident(0x80, Encoding.ASCII.GetBytes("hello, world!"))
            .Build();

        var r = Parse(bytes);

        Assert.NotNull(r);
        Assert.Equal(40u, r.Number);
        Assert.Equal(1, r.Sequence);
        Assert.Equal(0ul, r.BaseReference);
        Assert.False(r.IsFolder);
        Assert.Equal(new[] { new MftName(RootRef, "readme.txt", 3) }, r.Names);
        Assert.Equal(13, r.DataSize);
        Assert.Equal(0, r.DataAllocated);
        Assert.Equal(T, r.LastWriteUtc);
        Assert.Equal(DateTimeKind.Utc, r.LastWriteUtc.Kind);
        Assert.Equal(0x20u, r.FileAttributes);
        Assert.Empty(r.ExtensionReferences);
    }

    [Fact]
    public void ParsesNonResidentSizes()
    {
        var plain = Parse(new RecordBuilder()
            .StandardInformation(T, 0x20)
            .FileName(RootRef, "big.bin", 1)
            .NonResident(0x80, allocated: 8192, size: 5000)
            .Build());
        Assert.NotNull(plain);
        Assert.Equal(5000, plain.DataSize);
        Assert.Equal(8192, plain.DataAllocated);

        // Compressed: the clusters really in use (total allocated) count, not the allocated length.
        var compressed = Parse(new RecordBuilder()
            .StandardInformation(T, 0x800)
            .FileName(RootRef, "packed.log", 1)
            .NonResident(0x80, allocated: 65536, size: 60000, flags: 0x0001, totalAllocated: 12288)
            .Build());
        Assert.NotNull(compressed);
        Assert.Equal(60000, compressed.DataSize);
        Assert.Equal(12288, compressed.DataAllocated);

        var sparse = Parse(new RecordBuilder()
            .StandardInformation(T, 0x200)
            .FileName(RootRef, "holes.vhdx", 1)
            .NonResident(0x80, allocated: 1 << 30, size: 1 << 30, flags: 0x8000, totalAllocated: 4096)
            .Build());
        Assert.NotNull(sparse);
        Assert.Equal(4096, sparse.DataAllocated);
    }

    [Fact]
    public void IgnoresNamedDataStreams()
    {
        var r = Parse(new RecordBuilder()
            .StandardInformation(T, 0x20)
            .FileName(RootRef, "photo.jpg", 1)
            .Resident(0x80, new byte[99], name: "Zone.Identifier")
            .NonResident(0x80, allocated: 4096, size: 1234)
            .Build());

        Assert.NotNull(r);
        Assert.Equal(1234, r.DataSize);
        Assert.Equal(4096, r.DataAllocated);
    }

    [Fact]
    public void ReadsFolderFlagAndHasNoDataSize()
    {
        var r = Parse(new RecordBuilder { Flags = 0x03 }
            .StandardInformation(T, 0x10)
            .FileName(RootRef, "Documents", 3)
            .Build());

        Assert.NotNull(r);
        Assert.True(r.IsFolder);
        Assert.Null(r.DataSize);
        Assert.Null(r.DataAllocated);
    }

    [Fact]
    public void PrefersRecordingAllNamesWithNamespaces()
    {
        var r = Parse(new RecordBuilder()
            .StandardInformation(T, 0x20)
            .FileName(RootRef, "Long File Name.txt", 1)
            .FileName(RootRef, "LONGFI~1.TXT", 2)
            .FileName(0x0001_0000_0000_0123, "hard link.txt", 0)
            .Resident(0x80, new byte[3])
            .Build());

        Assert.NotNull(r);
        Assert.Equal(new[]
        {
            new MftName(RootRef, "Long File Name.txt", 1),
            new MftName(RootRef, "LONGFI~1.TXT", 2),
            new MftName(0x0001_0000_0000_0123, "hard link.txt", 0),
        }, r.Names);
    }

    [Fact]
    public void ReadsTheSequenceNumber()
    {
        // FILE_RECORD_SEGMENT_HEADER.SequenceNumber at 0x10: what a file reference to this record must carry.
        var r = Parse(new RecordBuilder { Sequence = 0x1234 }.StandardInformation(T, 0x20).FileName(RootRef, "a", 1).Build());
        Assert.NotNull(r);
        Assert.Equal(0x1234, r.Sequence);
    }

    [Fact]
    public void ReadsTheReparseTag()
    {
        // REPARSE_DATA_BUFFER: tag, data length, reserved, then the tag's data (here an empty mount point).
        var value = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(value, 0xA000_0003);
        var r = Parse(new RecordBuilder { Flags = 0x03 }
            .StandardInformation(T, 0x410)
            .FileName(RootRef, "junction", 1)
            .Resident(0xC0, value)
            .Build());

        Assert.NotNull(r);
        Assert.Equal(0xA000_0003u, r.ReparseTag);
        Assert.Equal(0u, Parse(Sample())!.ReparseTag);
        Assert.Null(Parse(new RecordBuilder().StandardInformation(T, 0x400).FileName(RootRef, "short", 1)
            .Resident(0xC0, new byte[3]).Build()));
    }

    /// <summary>A name with a separator, a drive or ".." would lead the joined path off the scanned drive: the record is damaged.</summary>
    [Theory]
    [InlineData(@"C:\Users\x\Documents\a.docx", 40u, false)]
    [InlineData(@"..\x", 40u, false)]
    [InlineData("a/b", 40u, false)]
    [InlineData("..", 40u, false)]
    [InlineData(".", 40u, false)]
    [InlineData(".", 5u, true)]
    [InlineData("...", 40u, true)]
    [InlineData(".gitignore", 40u, true)]
    public void NamesMustBeOneSegment(string name, uint number, bool valid) =>
        Assert.Equal(valid, Parse(new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, name, 1).Build(), number) is not null);

    [Fact]
    public void RejectsBrokenFixups()
    {
        var bytes = Sample();
        Assert.NotNull(Parse((byte[])bytes.Clone()));

        // A torn write: sector 1 ends with something other than the update sequence number.
        bytes[1022] ^= 0xFF;
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void AppliesFixupsInPlace()
    {
        // The builder puts a marker in the last two bytes of each sector; the USA saves them while on disk.
        var bytes = new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, "a", 1).Build();
        Assert.Equal(RecordBuilder.Usn, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(510)));

        Assert.NotNull(Parse(bytes));

        Assert.Equal(RecordBuilder.SectorEndMarker(0), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(510)));
        Assert.Equal(RecordBuilder.SectorEndMarker(1), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1022)));
    }

    [Fact]
    public void FixupsUseThe512ByteStrideOn4KSectorDrives()
    {
        // NTFS protects every 512 bytes whatever the sector size: a 4 KB record has 8 sector ends to check.
        var bytes = new RecordBuilder { Size = 4096 }
            .StandardInformation(T, 0x20)
            .FileName(RootRef, "4k.txt", 1)
            .Resident(0x80, new byte[7])
            .Build();
        var r = Parse((byte[])bytes.Clone(), bytesPerSector: 4096);
        Assert.NotNull(r);
        Assert.Equal(7, r.DataSize);

        bytes[2046] ^= 0xFF; // end of the fourth 512-byte stride
        Assert.Null(Parse(bytes, bytesPerSector: 4096));
    }

    [Fact]
    public void RejectsUnusedRecord()
    {
        Assert.Null(Parse(new RecordBuilder { Flags = 0x00 }.StandardInformation(T, 0x20).FileName(RootRef, "gone.txt", 1).Build()));
        Assert.Null(Parse(new RecordBuilder { Flags = 0x02 }.StandardInformation(T, 0x10).FileName(RootRef, "gone", 1).Build()));
    }

    [Fact]
    public void RejectsMissingSignature()
    {
        var bytes = Sample();
        Encoding.ASCII.GetBytes("BAAD").CopyTo(bytes, 0); // what NTFS writes over a record it found torn
        Assert.Null(Parse(bytes));
        Assert.Null(Parse(new byte[1024]));
    }

    [Fact]
    public void ReadsAttributeListReferences()
    {
        const ulong ext41 = 0x0002_0000_0000_0029, ext42 = 0x0003_0000_0000_002A, ext43 = 0x0001_0000_0000_002B;
        const ulong self = 0x0001_0000_0000_0028;
        var list = new AttributeListBuilder()
            .Entry(0x10, self)
            .Entry(0x30, ext41)
            .Entry(0x80, ext42)
            .Entry(0x80, ext41, lowestVcn: 4096) // a later part of the same stream: listed once
            .Entry(0x80, ext43, name: "ads")     // named stream: not needed for the size
            .Entry(0x90, ext43)                   // index root: not needed
            .Build();

        var r = Parse(new RecordBuilder()
            .StandardInformation(T, 0x20)
            .Resident(0x20, list)
            .FileName(RootRef, "many links.txt", 1)
            .Build(), number: 40);

        Assert.NotNull(r);
        Assert.Equal(new[] { ext41, ext42 }, r.ExtensionReferences);
        Assert.Null(r.DataSize); // the data sits in an extension record
    }

    [Fact]
    public void ReadsTheBaseReferenceOfAnExtensionRecord()
    {
        const ulong baseRef = 0x0004_0000_0000_0028;
        var r = Parse(new RecordBuilder { BaseReference = baseRef }
            .FileName(RootRef, "many links (2).txt", 1)
            .NonResident(0x80, allocated: 4096, size: 100)
            .Build(), number: 41);

        Assert.NotNull(r);
        Assert.Equal(41u, r.Number);
        Assert.Equal(baseRef, r.BaseReference);
        Assert.Equal(1, r.Sequence);
        Assert.Equal(default, r.LastWriteUtc); // no $STANDARD_INFORMATION in an extension record
        Assert.Equal(100, r.DataSize);
    }

    [Fact]
    public void SkipsSizesOfALaterDataSegment()
    {
        // Sizes are valid only in the segment that starts at VCN 0 (ATTRIBUTE_RECORD_HEADER).
        var r = Parse(new RecordBuilder { BaseReference = 0x0001_0000_0000_0028 }
            .NonResident(0x80, allocated: 999_999, size: 999_999, lowestVcn: 64)
            .Build());

        Assert.NotNull(r);
        Assert.Null(r.DataSize);
        Assert.Null(r.DataAllocated);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    public void InvalidTimesBecomeUnknown(long fileTime)
    {
        var r = Parse(new RecordBuilder().StandardInformation(fileTime, 0x20).FileName(RootRef, "t", 1).Build());

        Assert.NotNull(r);
        Assert.Equal(default, r.LastWriteUtc);
    }

    // Damaged volumes: every one of these must come back as null without throwing or hanging.

    [Fact]
    public void RejectsATruncatedRecord()
    {
        var bytes = Sample();
        Assert.Null(Parse(bytes.AsSpan(0, 1000).ToArray()));
        Assert.Null(Parse(bytes.AsSpan(0, 600).ToArray()));
        Assert.Null(Parse(bytes.AsSpan(0, 30).ToArray()));
        Assert.Null(Parse(bytes.AsSpan(0, 4).ToArray()));
        Assert.Null(Parse([]));
    }

    [Fact]
    public void RejectsAnAttributeLengthOfZero()
    {
        var b = new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, "x.txt", 1);
        var bytes = b.Build();
        PatchUInt32(bytes, b.AttributeOffsets[1] + 4, 0);
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void RejectsAnAttributeLengthPastTheEnd()
    {
        var b = new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, "x.txt", 1);
        var bytes = b.Build();
        PatchUInt32(bytes, b.AttributeOffsets[1] + 4, 0x7FFF_FFF0);
        Assert.Null(Parse((byte[])bytes.Clone()));
        PatchUInt32(bytes, b.AttributeOffsets[1] + 4, 0xFFFF_FFF8); // would wrap a 32-bit sum
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void RejectsARecordWithoutTheEndMarker()
    {
        var b = new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, "x.txt", 1);
        var bytes = b.Build();
        // Make the last attribute run to the very end of the record: no room left for 0xFFFFFFFF.
        PatchUInt32(bytes, b.AttributeOffsets[1] + 4, (uint)(1024 - b.AttributeOffsets[1]));
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void RejectsANameLengthPastTheEnd()
    {
        var b = new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, "x.txt", 1);
        var bytes = b.Build();
        var value = b.AttributeOffsets[1] + 24;
        bytes[value + 64] = 200; // 200 characters do not fit the value
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void RejectsAResidentValuePastTheAttribute()
    {
        var b = new RecordBuilder().StandardInformation(T, 0x20).FileName(RootRef, "x.txt", 1).Resident(0x80, new byte[8]);
        var bytes = b.Build();
        PatchUInt32(bytes, b.AttributeOffsets[2] + 16, 5000);
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void RejectsAnAttributeListEntryOfLengthZero()
    {
        var list = new AttributeListBuilder().Entry(0x30, 0x0001_0000_0000_0029).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(list.AsSpan(4), 0);
        Assert.Null(Parse(new RecordBuilder().StandardInformation(T, 0x20).Resident(0x20, list).Build()));
    }

    [Theory]
    [InlineData(0x30, 0)]   // not even the sequence number
    [InlineData(0x30, 1)]   // the sequence number but no sector ends
    [InlineData(0x30, 2)]   // one sector end for a record of two strides
    [InlineData(0x30, 600)] // array far past the first sector
    [InlineData(0x31, 3)]   // odd offset
    [InlineData(506, 3)]    // array runs into the last word of the first sector
    [InlineData(0xFFFF, 3)] // array past the record
    public void RejectsABadUpdateSequenceHeader(int offset, int count)
    {
        var bytes = Sample();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)count);
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void RejectsAFirstAttributeOffsetPastTheEnd()
    {
        var bytes = Sample();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1022);
        Assert.Null(Parse((byte[])bytes.Clone()));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 0xFFFF);
        Assert.Null(Parse(bytes));
    }

    [Fact]
    public void SurvivesRandomDamage()
    {
        // Flip bytes after the fixups (so the walk sees them): never throws, whatever it returns.
        var rng = new Random(1234);
        var good = Sample();
        for (var i = 0; i < 5000; i++)
        {
            var bytes = (byte[])good.Clone();
            for (var n = rng.Next(1, 6); n > 0; n--)
            {
                var at = rng.Next(0, bytes.Length);
                if (at % 512 is 510 or 511) continue; // keep the sector ends, or every record fails the fixups
                bytes[at] = (byte)rng.Next(256);
            }
            MftRecordParser.Parse(bytes, 512, 40);
        }
    }

    // Run lists.

    public static TheoryData<string, (long, long)[]> Runs => new()
    {
        { "21 18 34 56 00", [(0x5634, 0x18)] },
        // Second offset 0xF000 is -4096: the run lies before the first one.
        { "21 18 34 56 21 10 00 F0 00", [(0x5634, 0x18), (0x4634, 0x10)] },
        // A sparse run (no offset bytes) does not move the current LCN.
        { "21 18 34 56 01 08 11 04 10 00", [(0x5634, 0x18), (-1, 8), (0x5644, 4)] },
        // Three-byte length, four-byte offset.
        { "43 00 00 01 00 00 00 10 00", [(0x1000_0000, 0x1_0000)] },
        // No terminator, but the span ends cleanly after the last run.
        { "11 05 20", [(0x20, 5)] },
        { "00", [] },
        { "", [] },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public void RunListParses(string hex, (long, long)[] expected)
    {
        Assert.Equal(expected, RunList.Parse(Hex(hex)));
    }

    [Theory]
    [InlineData("21 18 34")]              // header claims two offset bytes, one is left
    [InlineData("21 18")]                 // no offset bytes at all
    [InlineData("31 18 00")]              // header claims three offset bytes after the length, none left
    [InlineData("02 18")]                 // two length bytes claimed, one left
    [InlineData("10 34 00")]              // length size 0 is not a run
    [InlineData("11 00 34 00")]           // run of 0 clusters
    [InlineData("91 01 00 00 00 00 00 00 00 00 01 00")] // 9 offset bytes
    [InlineData("19 01 00 00 00 00 00 00 00 00 01 00")] // 9 length bytes
    [InlineData("11 05 80 00")]           // first offset negative: LCN below 0
    [InlineData("21 18 34 56 11 04 00 00")] // offset 0: the run would start where the last one did
    [InlineData("18 FF FF FF FF FF FF FF FF 01 00")]    // length that does not fit a positive long
    [InlineData("81 01 FF FF FF FF FF FF FF 7F 81 01 FF FF FF FF FF FF FF 7F 00")] // LCN overflow
    public void RunListRejectsMalformedRuns(string hex)
    {
        Assert.Empty(RunList.Parse(Hex(hex)));
    }

    private static byte[] Sample() => new RecordBuilder()
        .StandardInformation(T, 0x20)
        .FileName(RootRef, "readme.txt", 3)
        .Resident(0x80, new byte[13])
        .Build();

    private static void PatchUInt32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);

    private static byte[] Hex(string hex) =>
        hex.Length == 0 ? [] : hex.Split(' ').Select(h => Convert.ToByte(h, 16)).ToArray();

    /// <summary>
    /// Builds an NTFS 3.1 file record the way Windows lays it out (FILE_RECORD_SEGMENT_HEADER, update sequence array at
    /// 0x30, attributes from the next quadword) and protects it as on disk: the last two bytes of every 512-byte
    /// stride move into the array and the update sequence number takes their place.
    /// </summary>
    private sealed class RecordBuilder
    {
        public const ushort Usn = 0x0007;
        public int Size { get; init; } = 1024;
        public ushort Flags { get; init; } = 0x01;
        public ulong BaseReference { get; init; }
        public ushort Sequence { get; init; } = 1;
        public List<int> AttributeOffsets { get; } = [];
        private readonly List<byte[]> _attributes = [];

        public static ushort SectorEndMarker(int sector) => (ushort)(0xA000 + sector);

        public RecordBuilder StandardInformation(DateTime lastWrite, uint attributes) =>
            StandardInformation(lastWrite.ToFileTimeUtc(), attributes);

        public RecordBuilder StandardInformation(long lastWrite, uint attributes)
        {
            var v = new byte[0x48];
            BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), lastWrite);
            BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), lastWrite);
            BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(16), lastWrite);
            BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(24), lastWrite);
            BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(32), attributes);
            return Resident(0x10, v);
        }

        public RecordBuilder FileName(ulong parent, string name, byte ns)
        {
            var v = new byte[66 + name.Length * 2];
            BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(0), parent);
            v[64] = (byte)name.Length;
            v[65] = ns;
            Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
            return Resident(0x30, v);
        }

        public RecordBuilder Resident(uint type, byte[] value, string? name = null)
        {
            var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
            var valueOffset = Align8(24 + nameBytes.Length);
            var a = new byte[Align8(valueOffset + value.Length)];
            Header(a, type, form: 0, nameBytes, nameOffset: 24, flags: 0);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), (uint)value.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(20), (ushort)valueOffset);
            value.CopyTo(a, valueOffset);
            _attributes.Add(a);
            return this;
        }

        public RecordBuilder NonResident(uint type, long allocated, long size, ushort flags = 0, long? totalAllocated = null,
            long lowestVcn = 0, string? name = null)
        {
            var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
            var headerLength = totalAllocated is null ? 0x40 : 0x48;
            var runsOffset = Align8(headerLength + nameBytes.Length);
            byte[] runs = [0x11, 0x01, 0x20, 0x00];
            var a = new byte[Align8(runsOffset + runs.Length)];
            Header(a, type, form: 1, nameBytes, nameOffset: headerLength, flags);
            BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(16), lowestVcn);
            BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(24), lowestVcn);
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(32), (ushort)runsOffset);
            if (totalAllocated is not null) a[34] = 4; // compression unit
            BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(40), allocated);
            BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(48), size);
            BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(56), size);
            if (totalAllocated is long t) BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(64), t);
            runs.CopyTo(a, runsOffset);
            _attributes.Add(a);
            return this;
        }

        private static void Header(byte[] a, uint type, byte form, byte[] name, int nameOffset, ushort flags)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0), type);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)a.Length);
            a[8] = form;
            a[9] = (byte)(name.Length / 2);
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), (ushort)nameOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12), flags);
            name.CopyTo(a, nameOffset);
        }

        public byte[] Build()
        {
            const int usaOffset = 0x30;
            var strides = Size / 512;
            var r = new byte[Size];
            Encoding.ASCII.GetBytes("FILE").CopyTo(r, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), usaOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), (ushort)(strides + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(16), Sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(18), 1);  // link count
            var pos = Align8(usaOffset + 2 * (strides + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(20), (ushort)pos);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(22), Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), (uint)Size);
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), BaseReference);
            AttributeOffsets.Clear();
            foreach (var a in _attributes)
            {
                AttributeOffsets.Add(pos);
                a.CopyTo(r, pos);
                pos += a.Length;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(pos), 0xFFFF_FFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), (uint)(pos + 8)); // bytes in use

            // Protect: save each stride's last word into the array, then write the update sequence number there.
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(usaOffset), Usn);
            for (var s = 0; s < strides; s++)
            {
                var end = (s + 1) * 512 - 2;
                if (BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(end)) == 0)
                    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), SectorEndMarker(s));
                r.AsSpan(end, 2).CopyTo(r.AsSpan(usaOffset + 2 + 2 * s));
                BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), Usn);
            }
            return r;
        }

        private static int Align8(int n) => (n + 7) & ~7;
    }

    /// <summary>Resident $ATTRIBUTE_LIST value: ATTRIBUTE_LIST_ENTRY records, quadword aligned.</summary>
    private sealed class AttributeListBuilder
    {
        private readonly List<byte> _bytes = [];

        public AttributeListBuilder Entry(uint type, ulong reference, long lowestVcn = 0, string? name = null)
        {
            var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
            var e = new byte[(0x1A + nameBytes.Length + 7) & ~7];
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0), type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), (ushort)e.Length);
            e[6] = (byte)(nameBytes.Length / 2);
            e[7] = 0x1A;
            BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(8), lowestVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(16), reference);
            nameBytes.CopyTo(e, 0x1A);
            _bytes.AddRange(e);
            return this;
        }

        public byte[] Build() => [.. _bytes];
    }
}
