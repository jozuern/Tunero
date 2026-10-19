using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Optimizer.Core.Interop;

namespace Optimizer.Core.Media;

/// <summary>Writes a media folder as a bootable ISO.</summary>
public interface IIsoWriter
{
    /// <summary>
    /// Writes <paramref name="mediaFolder"/> to <paramref name="isoPath"/>. The ISO is written as "isoPath.part" and gets its
    /// name only when it is complete, so a stop or a failure never leaves a half-written ISO (an older file at
    /// <paramref name="isoPath"/> stays until then). <paramref name="p"/> gets 0 to 1.
    /// </summary>
    Task WriteAsync(string mediaFolder, string isoPath, string volumeName, IProgress<double>? p, CancellationToken ct);
}

/// <summary>
/// The El Torito boot entries of the ISO and how to read them back. The boot images are the media's own files, unchanged:
/// boot\etfsboot.com for BIOS and efi\microsoft\boot\efisys.bin (a FAT image holding the UEFI loader) for UEFI, the same
/// layout as Microsoft's ISOs. The 26H2 media has no 2023-signed efisys_EX.bin; the loader inside its efisys.bin carries
/// one embedded signature, by "Microsoft Windows Production PCA 2011" (checked 2026-10-19). So the ISO boots wherever
/// the media's own files boot: not on PCs that put that certificate in the Secure Boot DBX (KB5025885) or that lack it in the DB.
/// </summary>
public static class IsoBoot
{
    public const int PlatformBios = 0x00, PlatformUefi = 0xEF;
    public const string BiosImage = @"boot\etfsboot.com", UefiImage = @"efi\microsoft\boot\efisys.bin", UefiLoader = @"efi\boot\bootx64.efi";

    private const int Sector = 2048;
    private const int MaxVolumeName = 32;

    /// <summary>The boot entries in catalog order: BIOS first when the media has etfsboot.com, then UEFI when it has efisys.bin.</summary>
    public static IReadOnlyList<(string File, int Platform)> Entries(string mediaFolder)
    {
        var entries = new List<(string, int)>();
        if (File.Exists(Path.Combine(mediaFolder, BiosImage))) entries.Add((BiosImage, PlatformBios));
        if (File.Exists(Path.Combine(mediaFolder, UefiImage))) entries.Add((UefiImage, PlatformUefi));
        return entries;
    }

    /// <summary>
    /// Whether the media can boot a UEFI PC: a non-empty efi\microsoft\boot\efisys.bin (the El Torito image) and the
    /// efi\boot\bootx64.efi loader. The build refuses media without them before writing.
    /// </summary>
    public static bool HasUefiBoot(string mediaFolder)
    {
        var image = new FileInfo(Path.Combine(mediaFolder, UefiImage));
        return image.Exists && image.Length > 0 && File.Exists(Path.Combine(mediaFolder, UefiLoader));
    }

    /// <summary>Upper case, every character outside [A-Z0-9_] becomes '_', at most 32 characters.</summary>
    public static string VolumeName(string raw)
    {
        var name = new StringBuilder(MaxVolumeName);
        foreach (var c in raw.ToUpperInvariant())
        {
            if (name.Length == MaxVolumeName) break;
            name.Append(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
        }
        return name.ToString();
    }

    /// <summary>The platform id of every bootable catalog entry in order (0x00 BIOS, 0xEF UEFI); empty without a valid El Torito catalog.</summary>
    public static IReadOnlyList<int> ReadPlatforms(Stream iso) => ReadCatalog(iso).Select(e => e.Platform).ToList();

    /// <summary>One bootable entry of the boot catalog: the image starts at sector <paramref name="Rba"/> and is <paramref name="Sectors"/> * 512 bytes long.</summary>
    public sealed record BootEntry(int Platform, uint Rba, int Sectors);

    /// <summary>
    /// Reads the El Torito boot catalog: the boot record at sector 17 ("EL TORITO SPECIFICATION") points to it at 0x47.
    /// The validation entry names the platform of the initial entry; each section header (0x90, the last one 0x91) names
    /// the platform of the entries after it. Anything that is not a valid catalog gives an empty list.
    /// </summary>
    public static IReadOnlyList<BootEntry> ReadCatalog(Stream iso)
    {
        var entries = new List<BootEntry>();
        var record = ReadSector(iso, 17);
        if (record is null || record[0] != 0 || Encoding.ASCII.GetString(record, 1, 5) != "CD001" ||
            Encoding.ASCII.GetString(record, 7, 23) != "EL TORITO SPECIFICATION")
            return entries;
        var catalog = ReadSector(iso, BitConverter.ToUInt32(record, 0x47));
        if (catalog is null || catalog[0] != 0x01 || catalog[30] != 0x55 || catalog[31] != 0xAA) return entries;
        var sum = 0;
        for (var i = 0; i < 32; i += 2) sum += BitConverter.ToUInt16(catalog, i);
        if ((sum & 0xFFFF) != 0) return entries;

        void Add(int platform, int offset)
        {
            if (catalog[offset] == 0x88)
                entries.Add(new BootEntry(platform, BitConverter.ToUInt32(catalog, offset + 8), BitConverter.ToUInt16(catalog, offset + 6)));
        }

        Add(catalog[1], 32);
        var at = 64;
        while (at + 32 <= Sector && catalog[at] is 0x90 or 0x91)
        {
            var last = catalog[at] == 0x91;
            var platform = catalog[at + 1];
            var count = BitConverter.ToUInt16(catalog, at + 2);
            at += 32;
            for (var i = 0; i < count && at + 32 <= Sector; i++, at += 32)
            {
                Add(platform, at);
                // Selection criteria extensions (0x44) belong to the entry before them.
                while (at + 64 <= Sector && catalog[at + 32] == 0x44) at += 32;
            }
            if (last) break;
        }
        return entries;
    }

    /// <summary>
    /// True when the catalog has exactly the entries <see cref="Entries"/> chooses for <paramref name="mediaFolder"/>, in the
    /// same order, every boot image on the ISO is byte for byte the media's file and fully covered by its sector count,
    /// and one of them is the UEFI entry with the media's efisys.bin. An ISO that cannot boot a UEFI PC never passes,
    /// even though the writer itself writes whatever entries the media has.
    /// </summary>
    public static bool CatalogMatches(Stream iso, string mediaFolder)
    {
        var expected = Entries(mediaFolder);
        if (!expected.Any(e => e.Platform == PlatformUefi)) return false;
        var actual = ReadCatalog(iso);
        if (expected.Count != actual.Count) return false;
        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i].Platform != actual[i].Platform) return false;
            using var file = new FileStream(Path.Combine(mediaFolder, expected[i].File), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (actual[i].Sectors * 512L < Math.Min(file.Length, ushort.MaxValue * 512L)) return false;
            if (!SameBytes(iso, (long)actual[i].Rba * Sector, file)) return false;
        }
        return true;
    }

    private static bool SameBytes(Stream iso, long offset, Stream file)
    {
        if (offset + file.Length > iso.Length) return false;
        iso.Position = offset;
        var a = new byte[1 << 16];
        var b = new byte[a.Length];
        int n;
        while ((n = file.Read(a, 0, a.Length)) > 0)
        {
            iso.ReadExactly(b, 0, n);
            if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n))) return false;
        }
        return true;
    }

    private static byte[]? ReadSector(Stream iso, long sector)
    {
        if ((sector + 1) * Sector > iso.Length) return null;
        var data = new byte[Sector];
        iso.Position = sector * Sector;
        iso.ReadExactly(data);
        return data;
    }
}

/// <summary>
/// Builds the ISO with IMAPI2 (part of Windows): a UDF 1.02 file system like Microsoft's ISOs, with the El Torito boot
/// entries of <see cref="IsoBoot.Entries"/> in no-emulation mode. IMAPI2 needs a single-threaded apartment, so the work
/// runs on its own STA thread; the COM objects and file streams are released in a finally on that thread.
/// </summary>
public sealed class IsoWriter : IIsoWriter
{
    private const int FsiFileSystemUdf = 4;
    private const int UdfRevision102 = 0x102;
    private const int ChunkSize = 1 << 20;

    public Task WriteAsync(string mediaFolder, string isoPath, string volumeName, IProgress<double>? p, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        if (!Directory.Exists(mediaFolder)) return Task.FromException(new DirectoryNotFoundException($"The media folder {mediaFolder} does not exist."));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Write(mediaFolder, isoPath, volumeName, p, ct);
                done.SetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                done.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "IMAPI2 ISO writer",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Write(string mediaFolder, string isoPath, string volumeName, IProgress<double>? p, CancellationToken ct)
    {
        var part = isoPath + ".part";
        var complete = false;
        var com = new List<object>();
        var countPtr = Marshal.AllocHGlobal(sizeof(int));

        // Every COM object and stream is released when the ISO is done, failed or stopped.
        T Keep<T>(T value) where T : class
        {
            com.Add(value);
            return value;
        }

        try
        {
            dynamic fs = Keep(Create("IMAPI2FS.MsftFileSystemImage"));
            fs.FileSystemsToCreate = FsiFileSystemUdf;
            fs.UDFRevision = UdfRevision102;
            fs.FreeMediaBlocks = 0;   // no size limit (not a disc)
            var name = IsoBoot.VolumeName(volumeName);
            if (name.Length > 0) fs.VolumeName = name;

            var entries = IsoBoot.Entries(mediaFolder);
            if (entries.Count > 0)
            {
                var options = new object[entries.Count];
                for (var i = 0; i < entries.Count; i++)
                {
                    Native.SHCreateStreamOnFileEx(Path.Combine(mediaFolder, entries[i].File), Native.StgmReadShareDenyWrite, 0, false, IntPtr.Zero, out var stream);
                    Keep(stream);
                    dynamic boot = Keep(Create("IMAPI2FS.BootOptions"));
                    boot.AssignBootImage(stream);
                    boot.PlatformId = entries[i].Platform;
                    boot.Emulation = 0;   // no emulation
                    options[i] = boot;
                }
                fs.BootImageOptionsArray = options;
            }

            ct.ThrowIfCancellationRequested();
            dynamic root = Keep((object)fs.Root);
            root.AddTree(mediaFolder, false);
            ct.ThrowIfCancellationRequested();
            dynamic result = Keep((object)fs.CreateResultImage());
            var image = (IStream)Keep((object)result.ImageStream);
            var total = (long)result.TotalBlocks * (long)result.BlockSize;

            var progress = new FractionProgress(p);
            var buffer = new byte[ChunkSize];
            long written = 0;
            using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    image.Read(buffer, buffer.Length, countPtr);
                    var n = Marshal.ReadInt32(countPtr);
                    if (n <= 0) break;
                    file.Write(buffer, 0, n);
                    written += n;
                    if (total > 0) progress.Report(Math.Min(written, total - 1) / (double)total);
                }
                if (written != total) throw new IOException($"IMAPI2 gave {written} bytes for an image of {total} bytes.");
                file.Flush(flushToDisk: true);
            }
            File.Move(part, isoPath, overwrite: true);
            complete = true;
            progress.Report(1.0);
        }
        catch (COMException ex)
        {
            throw new IOException("IMAPI2 could not build the ISO: " + ex.Message, ex);
        }
        finally
        {
            if (!complete) TryDelete(part);
            Marshal.FreeHGlobal(countPtr);
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (var i = com.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(com[i]) && released.Add(com[i])) Marshal.FinalReleaseComObject(com[i]);
        }
    }

    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new IOException($"{progId} could not be created.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Passes on progress only when it moved by at least one percent, and always the final 1.</summary>
internal sealed class FractionProgress(IProgress<double>? progress)
{
    private double _last = double.NegativeInfinity;

    public void Report(double value)
    {
        if (progress is null || (value < 1 && value - _last < 0.01)) return;
        _last = value;
        progress.Report(value);
    }
}
