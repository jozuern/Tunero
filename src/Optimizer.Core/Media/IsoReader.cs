using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Media;

/// <summary>
/// What <see cref="IIsoReader.VerifyAsync"/> found: whether the boot catalog has exactly the entries and images the media
/// folder asks for, and which listed files are missing on the ISO or differ from the media folder's copy.
/// </summary>
public sealed record IsoCheck(bool BootCatalogOk, IReadOnlyList<string> Mismatched);

/// <summary>
/// The ISO could not be opened or attached. <see cref="InUse"/> is true for a sharing or lock violation: another program
/// holds the file, for example Explorer with the ISO mounted. The message is for the log; the page shows its own text.
/// </summary>
public sealed class IsoAttachException(int win32Error, string message) : IOException(message)
{
    private const int ErrorSharingViolation = 32, ErrorLockViolation = 33;

    public int Win32Error { get; } = win32Error;

    public bool InUse => Win32Error is ErrorSharingViolation or ErrorLockViolation;

    /// <param name="step">"opened" or "attached".</param>
    internal static IsoAttachException For(int win32Error, string step)
    {
        var message = $"The ISO could not be {step}: error {win32Error} ({new Win32Exception(win32Error).Message}).";
        if (win32Error is ErrorSharingViolation or ErrorLockViolation)
            message += " The file is in use by another program, for example mounted in Explorer; eject it there and try again.";
        return new IsoAttachException(win32Error, message);
    }
}

/// <summary>Reads ISOs without changing them. Attaching needs administrator rights.</summary>
public interface IIsoReader
{
    /// <summary>
    /// Copies every folder (empty ones included) and file of the ISO into <paramref name="targetFolder"/> (created when
    /// missing) and returns it. <paramref name="p"/> gets 0 to 1. A failed or stopped copy leaves a partial target folder:
    /// the caller deletes it. Throws <see cref="IsoAttachException"/> when the ISO cannot be opened or attached.
    /// </summary>
    Task<string> CopyMediaAsync(string isoPath, string targetFolder, IProgress<double>? p, CancellationToken ct);

    /// <summary>
    /// Compares the SHA-256 of each of <paramref name="relativeFiles"/> on the ISO with the same file in
    /// <paramref name="mediaFolder"/> (a file missing on either side counts as mismatched) and checks the boot catalog
    /// with <see cref="IsoBoot.CatalogMatches"/>: BootCatalogOk is false unless the ISO has a UEFI entry with the media's
    /// efisys.bin. Throws <see cref="IsoAttachException"/> when the ISO cannot be opened or attached.
    /// </summary>
    Task<IsoCheck> VerifyAsync(string isoPath, string mediaFolder, IReadOnlyList<string> relativeFiles, IProgress<double>? p, CancellationToken ct);
}

/// <summary>
/// Attaches the ISO through virtdisk read-only and without a drive letter, finds its volume (the one whose device number
/// matches the attached CD-ROM device) and reads the files through the volume's \\?\Volume{GUID}\ path. The attachment
/// lasts only as long as the virtual disk handle: no ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME, the flag that would
/// "decouple the virtual disk lifetime from that of the VirtualDiskHandle" (Microsoft Learn, ATTACH_VIRTUAL_DISK_FLAG).
/// The finally detaches it with DetachVirtualDisk and closes the handle; should the app end in between, closing the
/// handle at process exit detaches it too, so no ISO stays attached.
/// </summary>
public sealed class IsoReader : IIsoReader
{
    /// <summary>How long to wait for the attached ISO's volume to appear and mount.</summary>
    internal static TimeSpan VolumeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private const int ChunkSize = 1 << 20;

    public Task<string> CopyMediaAsync(string isoPath, string targetFolder, IProgress<double>? p, CancellationToken ct) => Task.Run(async () =>
    {
        using var iso = AttachedIso.Attach(isoPath);
        var root = await iso.WaitForVolumeAsync(ct);
        await CopyTreeAsync(root, targetFolder, p, ct);
        return targetFolder;
    }, ct);

    /// <summary>
    /// Copies every folder (empty ones too) and file below <paramref name="root"/> to <paramref name="targetFolder"/>.
    /// Reparse points are skipped, hidden and system entries copied. Each file is written as a new file, not with
    /// File.Copy, so the copy does not inherit the read-only attribute every file on a disc image has.
    /// </summary>
    internal static async Task CopyTreeAsync(string root, string targetFolder, IProgress<double>? p, CancellationToken ct)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var source = new DirectoryInfo(root);
        var folders = source.EnumerateDirectories("*", options).ToList();
        var files = source.EnumerateFiles("*", options).ToList();
        var total = files.Sum(f => f.Length);
        var progress = new FractionProgress(p);
        long done = 0;
        Directory.CreateDirectory(targetFolder);
        foreach (var folder in folders) Directory.CreateDirectory(Path.Combine(targetFolder, Path.GetRelativePath(root, folder.FullName)));
        var buffer = new byte[ChunkSize];
        foreach (var file in files)
        {
            var target = Path.Combine(targetFolder, Path.GetRelativePath(root, file.FullName));
            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var copy = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await copy.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(done, total - 1) / (double)total);
            }
            copy.Flush(flushToDisk: true);
        }
        progress.Report(1.0);
    }

    public Task<IsoCheck> VerifyAsync(string isoPath, string mediaFolder, IReadOnlyList<string> relativeFiles, IProgress<double>? p, CancellationToken ct) => Task.Run(async () =>
    {
        // The boot catalog is read from the ISO file itself, before the attach.
        bool bootOk;
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            bootOk = IsoBoot.CatalogMatches(stream, mediaFolder);

        using var iso = AttachedIso.Attach(isoPath);
        var root = await iso.WaitForVolumeAsync(ct);
        var mismatched = new List<string>();
        var sizes = relativeFiles.Select(f => IsPlainRelative(f) && File.Exists(Path.Combine(mediaFolder, f)) ? new FileInfo(Path.Combine(mediaFolder, f)).Length : 0).ToList();
        var total = 2 * sizes.Sum();
        var progress = new FractionProgress(p);
        long done = 0;
        void Count(long bytes)
        {
            done += bytes;
            if (total > 0) progress.Report(Math.Min(done, total - 1) / (double)total);
        }

        foreach (var relative in relativeFiles)
        {
            var media = Path.Combine(mediaFolder, relative);
            var onIso = Path.Combine(root, relative);
            if (!IsPlainRelative(relative) || !File.Exists(media) || !File.Exists(onIso))
            {
                mismatched.Add(relative);
                continue;
            }
            var a = await HashAsync(media, Count, ct);
            var b = await HashAsync(onIso, Count, ct);
            if (!a.AsSpan().SequenceEqual(b)) mismatched.Add(relative);
        }
        progress.Report(1.0);
        return new IsoCheck(bootOk, mismatched);
    }, ct);

    /// <summary>A path below the folder: not rooted and without "..".</summary>
    private static bool IsPlainRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('\\', '/').Contains("..");

    private static async Task<byte[]> HashAsync(string path, Action<long> count, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[ChunkSize];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            count(n);
        }
        return hash.GetHashAndReset();
    }

    /// <summary>An ISO attached read-only without a drive letter; Dispose detaches it.</summary>
    private sealed class AttachedIso : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private AttachedIso(SafeFileHandle handle) => _handle = handle;

        public static AttachedIso Attach(string isoPath)
        {
            var type = new NativeWrite.VIRTUAL_STORAGE_TYPE { DeviceId = NativeWrite.VirtualStorageTypeDeviceIso, VendorId = NativeWrite.VirtualStorageTypeVendorMicrosoft };
            var error = NativeWrite.OpenVirtualDisk(in type, Path.GetFullPath(isoPath), NativeWrite.VirtualDiskAccessRead, 0, IntPtr.Zero, out var handle);
            if (error != 0)
            {
                handle.Dispose();
                throw IsoAttachException.For((int)error, "opened");
            }
            var parameters = new NativeWrite.ATTACH_VIRTUAL_DISK_PARAMETERS { Version = NativeWrite.AttachVirtualDiskVersion1 };
            error = NativeWrite.AttachVirtualDisk(handle, IntPtr.Zero, NativeWrite.AttachVirtualDiskFlagReadOnly | NativeWrite.AttachVirtualDiskFlagNoDriveLetter,
                0, in parameters, IntPtr.Zero);
            if (error != 0)
            {
                handle.Dispose();
                throw IsoAttachException.For((int)error, "attached");
            }
            return new AttachedIso(handle);
        }

        /// <summary>The volume root (\\?\Volume{GUID}\) once the volume exists and its file system answers.</summary>
        public async Task<string> WaitForVolumeAsync(CancellationToken ct)
        {
            var device = PhysicalPath();
            var deadline = DateTime.UtcNow + VolumeTimeout;
            while (true)
            {
                if (FindVolume(device) is { } root && Directory.Exists(root)) return root;
                if (DateTime.UtcNow > deadline) throw new IOException($"The attached ISO ({device}) has no readable volume.");
                await Task.Delay(250, ct);
            }
        }

        private unsafe string PhysicalPath()
        {
            var buffer = new char[260];
            var size = (uint)(buffer.Length * sizeof(char));
            uint error;
            fixed (char* p = buffer) error = Native.GetVirtualDiskPhysicalPath(_handle, ref size, p);
            if (error != 0) throw new IOException($"The attached ISO has no device path: {new Win32Exception((int)error).Message}");
            var end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end >= 0 ? end : buffer.Length);
        }

        /// <summary>The \\?\Volume{GUID}\ path of the volume on <paramref name="device"/> (\\.\CDROMn), matched by device type and number.</summary>
        private static unsafe string? FindVolume(string device)
        {
            if (DeviceNumber(device) is not { } wanted) return null;
            var buffer = new char[260];
            fixed (char* p = buffer)
            {
                var find = Native.FindFirstVolume(p, (uint)buffer.Length);
                if (find == new IntPtr(-1)) return null;
                try
                {
                    do
                    {
                        var name = new string(p);
                        // CreateFile opens the volume device only without the trailing backslash.
                        if (DeviceNumber(name.TrimEnd('\\')) is { } number &&
                            number.DeviceType == wanted.DeviceType && number.DeviceNumber == wanted.DeviceNumber)
                            return name;
                    }
                    while (Native.FindNextVolume(find, p, (uint)buffer.Length));
                }
                finally
                {
                    Native.FindVolumeClose(find);
                }
            }
            return null;
        }

        private static Native.STORAGE_DEVICE_NUMBER? DeviceNumber(string path)
        {
            using var handle = Native.CreateFile(path, 0, Native.FileShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            return Native.DeviceIoControl(handle, Native.IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out var number,
                (uint)Marshal.SizeOf<Native.STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero) ? number : null;
        }

        public void Dispose()
        {
            // Closing the handle detaches it as well (no permanent lifetime), so a failed detach is only logged.
            var error = NativeWrite.DetachVirtualDisk(_handle, 0, 0);
            if (error != 0) Log.Warn("media", $"ISO detach answered {error} ({new Win32Exception((int)error).Message}); closing its handle detaches it");
            _handle.Dispose();
        }
    }
}
