using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Optimizer.Core.Storage.Mft;

/// <summary>
/// One $FILE_NAME of a record. <paramref name="ParentReference"/> is the full file reference (record number in the low
/// 48 bits, sequence number in the high 16). Namespace: 0 POSIX, 1 Win32, 2 DOS, 3 Win32 and DOS.
/// </summary>
public sealed record MftName(ulong ParentReference, string Name, byte Namespace);

/// <summary>
/// What the storage scan needs from one in-use MFT file record. <paramref name="Sequence"/> is the record's sequence
/// number, which a file reference to it (a parent or base reference) must carry in its high 16 bits to be current.
/// <paramref name="BaseReference"/> is 0 for a base record
/// and the base record's file reference for an extension record. Data sizes are null when the record holds no unnamed
/// $DATA with sizes (a folder, or the stream lives in an extension record); resident data occupies no clusters, so its
/// allocated size is 0. <paramref name="LastWriteUtc"/> is default when the record has no valid
/// $STANDARD_INFORMATION time. <paramref name="ReparseTag"/> is the tag of a resident $REPARSE_POINT in this record, 0
/// when there is none or it is non-resident.
/// </summary>
public sealed record MftRecord(uint Number, ushort Sequence, ulong BaseReference, bool IsFolder, IReadOnlyList<MftName> Names, long? DataSize,
    long? DataAllocated, DateTime LastWriteUtc, uint FileAttributes, uint ReparseTag);

/// <summary>One segment of a non-resident unnamed $DATA: the VCNs it maps and their runs on the volume.</summary>
internal sealed record MftDataSegment(long LowestVcn, long HighestVcn, IReadOnlyList<(long Lcn, long Clusters)> Runs);

/// <summary>
/// What locating $MFT on the volume needs from one of its records: its sequence and base reference, the $DATA segments
/// it holds, and <paramref name="Listed"/>, the lowest VCN and record of every unnamed $DATA segment its resident
/// $ATTRIBUTE_LIST names (empty without one). <paramref name="ListNonResident"/>: the record has an attribute list that
/// was not read because it is not resident.
/// </summary>
internal sealed record MftDataSegments(ushort Sequence, ulong BaseReference, IReadOnlyList<MftDataSegment> Segments,
    IReadOnlyList<(long LowestVcn, ulong Reference)> Listed, bool ListNonResident);

/// <summary>
/// Parses one raw NTFS 3.x file record segment (FILE_RECORD_SEGMENT_HEADER and its attributes). The bytes come from a
/// volume that may be damaged, so every offset and length is checked against the record before use and anything
/// malformed returns null instead of throwing. No LINQ and no allocation per attribute: this runs for every record of a
/// drive.
/// </summary>
public static class MftRecordParser
{
    /// <summary>NTFS protects multisector structures in strides of 512 bytes whatever the sector size (MULTI_SECTOR_HEADER).</summary>
    private const int Stride = 512;
    private const uint StandardInformation = 0x10, AttributeList = 0x20, FileName = 0x30, Data = 0x80, ReparsePoint = 0xC0,
        End = 0xFFFF_FFFF;
    private const ushort InUse = 0x0001, Directory = 0x0002;
    private const ushort CompressionMask = 0x00FF, Sparse = 0x8000;
    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// Parses <paramref name="record"/> (exactly one file record) and applies the update sequence fixups in place. Null
    /// when the record has no FILE signature, is not in use, fails the fixups or is malformed. The fixup stride is 512
    /// bytes; <paramref name="bytesPerSector"/> is accepted as the stride only when the record's update sequence count
    /// fits it and not 512.
    /// </summary>
    public static MftRecord? Parse(Span<byte> record, int bytesPerSector, uint number)
    {
        var f = new Fields { Collect = true };
        if (!Walk(record, bytesPerSector, number, ref f)) return null;
        return new MftRecord(number, f.Sequence, f.BaseReference, f.IsFolder, (IReadOnlyList<MftName>?)f.Names ?? [],
            f.HasData ? f.DataSize : null, f.HasData ? f.DataAllocated : null, MftTable.Time(f.LastWriteTicks), f.Attributes,
            f.ReparseTag);
    }

    /// <summary>
    /// Whether <paramref name="record"/> is an extension record (it names a base record). The base reference lies outside
    /// the stride ends, so it reads the same before the fixups.
    /// </summary>
    internal static bool IsExtension(ReadOnlySpan<byte> record) => record.Length >= 0x28 && U64(record, 0x20) != 0;

    /// <summary>
    /// What a drive scan does with every base record: as <see cref="Parse"/>, but straight into entry
    /// <paramref name="entry"/> of <paramref name="table"/> (reserved for it with <see cref="MftTable.Reserve"/>) with only
    /// its best name decoded, so no object is made per record. Writes nothing else, so records of different entries can be
    /// parsed at the same time. An extension record (rare: files with many names or fragments) goes through
    /// <see cref="Parse"/> instead. False where <see cref="Parse"/> returns null; the entry is then left untouched.
    /// </summary>
    internal static bool ParseInto(Span<byte> record, int bytesPerSector, uint number, MftTable table, int entry)
    {
        var f = new Fields();
        if (!Walk(record, bytesPerSector, number, ref f)) return false;
        var name = f.NameRank == 0 ? null : Encoding.Unicode.GetString(record.Slice(f.NameAt, f.NameChars * 2));
        table.Set(entry, number, f.Sequence, f.IsFolder, name, f.NameParent, f.NameRank, f.HasData, f.DataSize, f.DataAllocated,
            f.LastWriteTicks, f.Attributes, f.ReparseTag);
        return true;
    }

    /// <summary>
    /// The rank of a name namespace when a file is listed under one name: Win32 (or Win32 and DOS) first, then POSIX (how
    /// Windows stores extra hard links), then a DOS short name.
    /// </summary>
    internal static byte NameRank(byte nameSpace) => nameSpace switch { PosixNamespace => 2, DosNamespace => 1, _ => 3 };

    private const byte PosixNamespace = 0, DosNamespace = 2;

    /// <summary>One record's fields as the attribute walk finds them. The names are collected only for <see cref="Parse"/>.</summary>
    private struct Fields
    {
        public bool Collect;
        public ushort Sequence;
        public ulong BaseReference;
        public bool IsFolder, HasData;
        public long DataSize, DataAllocated, LastWriteTicks;
        public uint Attributes, ReparseTag;
        public List<MftName>? Names;
        /// <summary>The best name so far (first of the highest rank): where its characters start in the record.</summary>
        public int NameAt, NameChars;
        public byte NameRank;
        public ulong NameParent;
    }

    /// <summary>
    /// A name NTFS itself would write: not empty, no NUL, \, / or :, and not "." or ".." (only the root's own record, 5,
    /// is named ".").
    /// </summary>
    private static bool IsOneSegment(ReadOnlySpan<byte> name, uint number)
    {
        var chars = name.Length / 2;
        if (chars == 0) return false;
        var dots = 0;
        for (var i = 0; i < chars; i++)
        {
            var c = (char)(name[2 * i] | name[2 * i + 1] << 8);
            if (c is '\0' or '\\' or '/' or ':') return false;
            if (c == '.') dots++;
        }
        return dots != chars || chars > 2 || (chars == 1 && number == 5);
    }

    // Optimized from the first call: a drive is read once per process, so these loops would otherwise run as tier-0
    // code for the whole first scan (in the test VM 1.75 s instead of 0.6 s).
    /// <summary>The checks and the attribute walk <see cref="Parse"/> and <see cref="ParseInto"/> share; false when the record is not usable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Walk(Span<byte> record, int bytesPerSector, uint number, ref Fields f)
    {
        // Header fields up to the base reference (0x20) and the next attribute id (0x28).
        if (record.Length < 0x2A) return false;
        if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E') return false;
        var flags = U16(record, 0x16);
        if ((flags & InUse) == 0) return false;
        if (!ApplyFixups(record, bytesPerSector)) return false;

        // FILE_RECORD_SEGMENT_HEADER: sequence number at 0x10, base file record segment at 0x20.
        f.Sequence = U16(record, 0x10);
        f.BaseReference = U64(record, 0x20);
        f.IsFolder = (flags & Directory) != 0;

        var pos = (int)U16(record, 0x14);
        if (pos < 0x2A) return false; // inside the header
        while (true)
        {
            if (pos > record.Length - 4) return false; // no room left for the end marker
            var type = U32(record, pos);
            if (type == End) break;
            if (pos > record.Length - 16) return false;
            var length = U32(record, pos + 4);
            // A length of 0 would never move on; past the end would read outside the record.
            if (length < 16 || length > (uint)(record.Length - pos)) return false;
            var a = record.Slice(pos, (int)length);
            var nonResident = a[8] != 0;
            var nameLength = a[9];

            if (!nonResident)
            {
                if (a.Length < 24) return false;
                var valueLength = U32(a, 16);
                var valueOffset = U16(a, 20);
                if (valueOffset > a.Length || valueLength > (uint)(a.Length - valueOffset)) return false;
                var value = a.Slice(valueOffset, (int)valueLength);
                switch (type)
                {
                    case StandardInformation:
                        // Last write at 0x08, file attributes at 0x20 (ATTR_STD_INFO).
                        if (value.Length < 0x24) return false;
                        f.LastWriteTicks = FileTimeTicks(I64(value, 0x08));
                        f.Attributes = U32(value, 0x20);
                        break;
                    case AttributeList:
                        if (!IsAttributeList(value)) return false;
                        break;
                    case FileName:
                        // FILE_NAME: parent at 0x00, name length (characters) at 0x40, namespace at 0x41, name at 0x42.
                        if (value.Length < 0x42) return false;
                        var chars = value[0x40];
                        if (0x42 + chars * 2 > value.Length) return false;
                        // A name is one path segment: a separator, a drive colon or ".." would make the joined path point
                        // elsewhere (Recycle would then move that file). Such a record counts as damaged.
                        if (!IsOneSegment(value.Slice(0x42, chars * 2), number)) return false;
                        var nameSpace = value[0x41];
                        if (f.Collect)
                            (f.Names ??= new List<MftName>(2)).Add(new MftName(U64(value, 0),
                                Encoding.Unicode.GetString(value.Slice(0x42, chars * 2)), nameSpace));
                        var rank = NameRank(nameSpace);
                        if (rank > f.NameRank)
                        {
                            f.NameRank = rank;
                            f.NameAt = pos + valueOffset + 0x42;
                            f.NameChars = chars;
                            f.NameParent = U64(value, 0);
                        }
                        break;
                    case ReparsePoint:
                        // REPARSE_DATA_BUFFER: the tag, the data length and a reserved word come first.
                        if (value.Length < 8) return false;
                        f.ReparseTag = U32(value, 0);
                        break;
                    case Data when nameLength == 0 && !f.HasData:
                        f.HasData = true;
                        f.DataSize = valueLength;
                        f.DataAllocated = 0;
                        break;
                }
            }
            else
            {
                if (a.Length < 0x40) return false;
                // Sizes are valid only in the segment that starts at VCN 0; named $DATA is an alternate stream.
                if (type == Data && nameLength == 0 && !f.HasData && I64(a, 0x10) == 0)
                {
                    var allocated = I64(a, 0x28);
                    var size = I64(a, 0x30);
                    if (size < 0 || allocated < 0) return false;
                    // Compressed or sparse: the clusters really in use, when the header carries the field before the runs.
                    var attributeFlags = U16(a, 0x0C);
                    if ((attributeFlags & (CompressionMask | Sparse)) != 0 && a.Length >= 0x48 && U16(a, 0x20) >= 0x48)
                    {
                        var total = I64(a, 0x40);
                        if (total < 0) return false;
                        allocated = total;
                    }
                    f.HasData = true;
                    f.DataSize = size;
                    f.DataAllocated = allocated;
                }
            }
            pos += (int)length;
        }
        return true;
    }

    /// <summary>
    /// For $MFT itself (record 0 and its extension records): the unnamed $DATA segments this record holds, and the unnamed
    /// $DATA entries of its resident $ATTRIBUTE_LIST (where each segment lives). Applies the fixups in place; null when
    /// the record is not in use, fails the fixups or is malformed, including a segment whose runs do not fill its VCNs.
    /// </summary>
    internal static MftDataSegments? ReadDataSegments(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 0x2A) return null;
        if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E') return null;
        if ((U16(record, 0x16) & InUse) == 0) return null;
        if (!ApplyFixups(record, bytesPerSector)) return null;

        var segments = new List<MftDataSegment>(1);
        var listed = new List<(long, ulong)>();
        var listNonResident = false;
        var pos = (int)U16(record, 0x14);
        if (pos < 0x2A) return null;
        while (true)
        {
            if (pos > record.Length - 4) return null;
            var type = U32(record, pos);
            if (type == End) break;
            if (pos > record.Length - 16) return null;
            var length = U32(record, pos + 4);
            if (length < 16 || length > (uint)(record.Length - pos)) return null;
            var a = record.Slice(pos, (int)length);
            var nonResident = a[8] != 0;

            if (type == Data && a[9] == 0 && nonResident)
            {
                if (a.Length < 0x40) return null;
                long lowest = I64(a, 0x10), highest = I64(a, 0x18);
                var runsOffset = U16(a, 0x20);
                if (lowest < 0 || highest < lowest || runsOffset < 0x40 || runsOffset >= a.Length) return null;
                var runs = RunList.Parse(a[runsOffset..]);
                long clusters = 0;
                foreach (var (_, c) in runs)
                {
                    if (c > long.MaxValue - clusters) return null;
                    clusters += c;
                }
                if (runs.Count == 0 || clusters != highest - lowest + 1) return null;
                segments.Add(new MftDataSegment(lowest, highest, runs));
            }
            else if (type == AttributeList && nonResident) listNonResident = true;
            else if (type == AttributeList)
            {
                if (a.Length < 24) return null;
                var valueLength = U32(a, 16);
                var valueOffset = U16(a, 20);
                if (valueOffset > a.Length || valueLength > (uint)(a.Length - valueOffset)) return null;
                var list = a.Slice(valueOffset, (int)valueLength);
                for (var at = 0; at < list.Length;)
                {
                    if (list.Length - at < 0x1A) return null;
                    int entryLength = U16(list, at + 4);
                    if (entryLength < 0x1A || entryLength > list.Length - at) return null;
                    // ATTRIBUTE_LIST_ENTRY: lowest VCN at 0x08, segment reference at 0x10.
                    if (U32(list, at) == Data && list[at + 6] == 0) listed.Add((I64(list, at + 8), U64(list, at + 0x10)));
                    at += entryLength;
                }
            }
            pos += (int)length;
        }
        return new MftDataSegments(U16(record, 0x10), U64(record, 0x20), segments, listed, listNonResident);
    }

    /// <summary>
    /// Checks that every stride ends with the update sequence number, then puts the saved words back. All are checked
    /// before any is replaced, so a torn record is left as read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        int usaOffset = U16(record, 0x04), usaCount = U16(record, 0x06);
        // The array is the sequence number plus one saved word per stride, and ends before the first stride's last word.
        if ((usaOffset & 1) != 0 || usaCount < 2 || usaOffset < 0x08 || usaOffset + 2 * usaCount > Stride - 2) return false;
        var strides = usaCount - 1;
        int stride;
        if (strides * Stride == record.Length) stride = Stride;
        else if (bytesPerSector > Stride && (long)strides * bytesPerSector == record.Length) stride = bytesPerSector;
        else return false;

        var usn = U16(record, usaOffset);
        for (var i = 1; i <= strides; i++)
            if (U16(record, i * stride - 2) != usn) return false;
        for (var i = 1; i <= strides; i++)
            record.Slice(usaOffset + 2 * i, 2).CopyTo(record.Slice(i * stride - 2, 2));
        return true;
    }

    /// <summary>
    /// Whether a resident $ATTRIBUTE_LIST is well formed: ATTRIBUTE_LIST_ENTRY records of at least 0x1A bytes, each with
    /// its length at 0x04, that end exactly at the end of the list. A record with a broken list is malformed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool IsAttributeList(ReadOnlySpan<byte> list)
    {
        var pos = 0;
        while (pos < list.Length)
        {
            if (list.Length - pos < 0x1A) return false;
            int length = U16(list, pos + 4);
            if (length < 0x1A || length > list.Length - pos) return false;
            pos += length;
        }
        return true;
    }

    /// <summary>DateTime ticks (UTC) of a FILETIME, 0 when it is not a valid time.</summary>
    private static long FileTimeTicks(long fileTime) =>
        fileTime > 0 && fileTime <= MaxFileTime ? DateTime.FromFileTimeUtc(fileTime).Ticks : 0;

    private static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(at, 2));
    private static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at, 4));
    private static ulong U64(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(at, 8));
    private static long I64(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt64LittleEndian(s.Slice(at, 8));
}
