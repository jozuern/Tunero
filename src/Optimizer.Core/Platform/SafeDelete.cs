using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optimizer.Core.Platform;

/// <summary>
/// Deletes files and empty folders in places a standard user can write to, from the elevated process, without
/// following links. The path is opened without following a final reparse point, the handle's real path is compared
/// with the expected one (a folder swapped for a junction after the scan resolves elsewhere and is refused), and the
/// delete goes through the handle, so it hits exactly the object that was checked. New files there are created the same
/// way (<see cref="CreateNew"/>), and folders can be held so that a path handed to another API keeps its meaning
/// (<see cref="Pin"/>).
/// </summary>
public static class SafeDelete
{
    /// <summary>True when the path and every existing parent folder are real folders (no junction, symbolic link or mount point).</summary>
    public static bool HasNoLinks(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            var root = Path.GetPathRoot(full);
            for (var p = full; !string.IsNullOrEmpty(p) && !string.Equals(p.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
            {
                if (!Path.Exists(p)) continue;
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the file exists, no part of its path is a link, and its handle's real path is the path itself. For
    /// callers that must hand the path to another API (the Recycle Bin), checked right before that call.
    /// </summary>
    public static bool ResolvesToItself(string path)
    {
        if (!HasNoLinks(path)) return false;
        try
        {
            var expected = LongPath(Path.GetFullPath(path)).TrimEnd('\\');
            using var handle = CreateFileW(@"\\?\" + expected, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            return !handle.IsInvalid && string.Equals(FinalPath(handle), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Holds the folder and every parent below the drive root open without delete sharing, after checking that each one
    /// is a real folder (no junction, symbolic link or mount point) at its own path. While the pin is not disposed none of
    /// them can be renamed, moved or replaced, so a path handed to another API (the Recycle Bin) still leads to the folder
    /// that was checked. An emptied folder can still be turned into a mount point: <see cref="FolderPin.StillReal"/>
    /// tells afterwards. Null when a folder is missing, is a link, or cannot be held.
    /// </summary>
    public static FolderPin? Pin(string folder) => Pin(folder, allowRoot: false);

    /// <param name="allowRoot">
    /// A drive root (C:\) itself is held too when <paramref name="folder"/> is one; it cannot be renamed or replaced, but its
    /// handle (<see cref="FolderPin.Leaf"/>) is what a caller works through.
    /// </param>
    /// <param name="readPermissions">The pinned folder's own handle may also read its permissions (READ_CONTROL); only that one.</param>
    internal static FolderPin? Pin(string folder, bool allowRoot, bool readPermissions = false)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var full = LongPath(Path.GetFullPath(folder)).TrimEnd('\\');
            var root = Path.GetPathRoot(full)?.TrimEnd('\\');
            var chain = new List<string>();
            for (var p = full; !string.IsNullOrEmpty(p) && !string.Equals(p.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
                chain.Add(p);
            chain.Reverse();
            if (chain.Count == 0 && allowRoot && root is { Length: 2 } && root[1] == ':')
            {
                var rootHandle = CreateFileW(@"\\?\" + root + @"\", FileListDirectory | FileReadAttributes | (readPermissions ? ReadControl : 0), FileShareRead | FileShareWrite, IntPtr.Zero,
                    OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
                handles.Add(rootHandle);
                if (rootHandle.IsInvalid || !IsRealFolder(rootHandle) || !string.Equals(FinalPath(rootHandle), root + @"\", StringComparison.OrdinalIgnoreCase))
                    return Release(handles);
                return new FolderPin(handles);
            }
            foreach (var p in chain)
            {
                // Top down: each parent is held before its child is opened, so the child's path cannot change in between. The
                // handle needs a data access (list): one with attribute access only takes no part in sharing checks, and the
                // folder could still be renamed.
                // READ_CONTROL only on the pinned folder itself and only when asked: the folders above, and every folder of
                // a plain pin, keep the list access they always had.
                var access = FileListDirectory | FileReadAttributes | (readPermissions && ReferenceEquals(p, chain[^1]) ? ReadControl : 0);
                var handle = CreateFileW(@"\\?\" + p, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return Release(handles);
                }
                handles.Add(handle);
                if (!IsRealFolder(handle) || !string.Equals(FinalPath(handle), p, StringComparison.OrdinalIgnoreCase)) return Release(handles);
            }
            return handles.Count == 0 ? null : new FolderPin(handles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Release(handles);
        }
    }

    private static FolderPin? Release(List<SafeFileHandle> handles)
    {
        foreach (var h in handles) h.Dispose();
        return null;
    }

    /// <summary>Folders held by <see cref="Pin"/>; disposing releases them.</summary>
    public sealed class FolderPin : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;

        internal FolderPin(List<SafeFileHandle> handles) => _handles = handles;

        /// <summary>False when one of the held folders has become a link (an emptied folder turned into a mount point).</summary>
        public bool StillReal() => _handles.All(IsRealFolder);

        /// <summary>The handle of the pinned folder itself (list, read attributes; read permissions when asked), valid until disposed.</summary>
        internal SafeFileHandle Leaf => _handles[^1];

        public void Dispose()
        {
            foreach (var h in _handles) h.Dispose();
        }
    }

    /// <summary>
    /// The attributes and reparse tag (0 without the reparse point attribute) of the file or folder itself, a final link
    /// not followed; reading them recalls nothing from the cloud. Null when it cannot be opened.
    /// </summary>
    public static (uint Attributes, uint Tag)? AttributesAndTag(string path)
    {
        try
        {
            using var handle = CreateFileW(@"\\?\" + LongPath(Path.GetFullPath(path)).TrimEnd('\\'), FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, Marshal.SizeOf<FileAttributeTagInfo>())) return null;
            return (info.FileAttributes, (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? info.ReparseTag : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsRealFolder(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, Marshal.SizeOf<FileAttributeTagInfo>())
        && (info.FileAttributes & (uint)FileAttributes.Directory) != 0
        && (info.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0;

    /// <summary>
    /// Creates a new file in a folder a standard user can write to and returns it open for writing. A name that already
    /// exists (also a planted link) is refused, and the new file's real path is compared with the expected one before
    /// anything is written: a file that came out somewhere else (a folder swapped in between) is deleted through its
    /// handle and IOException is thrown.
    /// </summary>
    public static FileStream CreateNew(string path)
    {
        var expected = CreatedPath(path);
        var handle = CreateFileW(@"\\?\" + expected, GenericWrite | Delete_ | FileReadAttributes, FileShareRead, IntPtr.Zero, CreateNew_,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"cannot create {expected}", new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            var final = FinalPath(handle);
            if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
            {
                var info = new FileDispositionInfoEx { Flags = DispositionDelete | DispositionPosixSemantics };
                SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref info, Marshal.SizeOf<FileDispositionInfoEx>());
                throw new IOException($"{expected} was created as {final}: removed again");
            }
            return new FileStream(handle, FileAccess.Write);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The path <see cref="CreateNew"/> creates and expects the handle to have: the folder in its long form, then the name.
    /// Join, not Combine: in a drive root the folder is "E:", and Combine would make the drive-relative "E:name".
    /// </summary>
    internal static string CreatedPath(string path) =>
        Path.Join(LongPath(Path.GetDirectoryName(Path.GetFullPath(path))!).TrimEnd('\\'), Path.GetFileName(path));

    /// <summary>
    /// The bytes of a real file at this very path, read through the one handle whose path was checked, so a link swapped
    /// in after a check cannot make the elevated app read another file. Null when the path or a folder above it is a
    /// link, the handle leads elsewhere, or the file is larger than <paramref name="maxBytes"/>.
    /// </summary>
    public static byte[]? ReadFile(string path, long maxBytes)
    {
        if (!HasNoLinks(path)) return null;
        var expected = LongPath(Path.GetFullPath(path)).TrimEnd('\\');
        using var handle = CreateFileW(@"\\?\" + expected, GenericRead | FileReadAttributes, FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"cannot open {expected}", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!string.Equals(FinalPath(handle), expected, StringComparison.OrdinalIgnoreCase)) return null;
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > maxBytes) return null;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Deletes the file (read-only included). Throws IOException when the path does not resolve to itself.</summary>
    public static void DeleteFile(string path) => Delete(path, directory: false);

    /// <summary>Deletes the folder if it is empty (the call fails for a non-empty folder).</summary>
    public static void DeleteEmptyDirectory(string path) => Delete(path, directory: true);

    private static void Delete(string path, bool directory)
    {
        // Long names: the final path never contains 8.3 short names (C:\Users\EXAMPL~1), the input may.
        var expected = LongPath(Path.GetFullPath(path)).TrimEnd('\\');
        var flags = FileFlagOpenReparsePoint | (directory ? FileFlagBackupSemantics : 0);
        using var handle = CreateFileW(@"\\?\" + expected, Delete_ | FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"cannot open {expected}", new Win32Exception(Marshal.GetLastWin32Error()));

        var final = FinalPath(handle);
        if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{expected} resolves to {final}: not deleted");

        // POSIX semantics (the name goes away at once) and ignore the read-only attribute (Windows 10 1809 and newer).
        var info = new FileDispositionInfoEx { Flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly };
        if (!SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref info, Marshal.SizeOf<FileDispositionInfoEx>()))
            throw new IOException($"cannot delete {expected}", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    // Both APIs return the needed size (with the terminating null) when the buffer is too small; paths can be up to
    // 32767 characters with the \\?\ prefix.
    internal static string LongPath(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathNameW(@"\\?\" + path, buffer, (uint)buffer.Length);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetLongPathNameW(@"\\?\" + path, buffer, (uint)buffer.Length);
        }
        if (length == 0 || length >= buffer.Length) return path;
        var s = new string(buffer, 0, (int)length);
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length) throw new IOException("cannot read the final path", new Win32Exception(Marshal.GetLastWin32Error()));
        var s = new string(buffer, 0, (int)length);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    private const uint ReadControl = 0x00020000;
    private const uint Delete_ = 0x00010000, FileReadAttributes = 0x80, FileListDirectory = 0x1, GenericWrite = 0x40000000, GenericRead = 0x80000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4, OpenExisting = 3, CreateNew_ = 1;
    private const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;
    private const int FileDispositionInfoExClass = 21, FileAttributeTagInfoClass = 9;
    private const uint DispositionDelete = 0x1, DispositionPosixSemantics = 0x2, DispositionIgnoreReadOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo info, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] buffer, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDispositionInfoEx info, int size);
}
