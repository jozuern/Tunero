using System.Runtime.InteropServices;
using Optimizer.Core.Platform;
using Optimizer.Core.Tools;

namespace Optimizer.Core.Storage;

/// <summary>Why the storage page does not offer a file or folder for the Recycle Bin.</summary>
/// <remarks>
/// <see cref="WindowsSetup"/>: Windows.old, $Windows.~BT or $Windows.~WS in a drive root, or inside one.
/// <see cref="SystemFolder"/>: the folder, or one above it, is both hidden and system (Config.Msi, $SysReset).
/// <see cref="NameMismatch"/>: a name in the path ends in a dot or space, or the path does not name itself (see
/// <see cref="RecycleRules.ResolvesAsWritten"/>), so Windows would act on another item.
/// <see cref="Link"/>: the file or folder is a link (junction, symbolic link, mount point). <see cref="CloudFolder"/>: it
/// is, or lies in, a OneDrive or other cloud files item. <see cref="OtherReparse"/>: it is, or lies in, another reparse
/// point (compressed by WOF, deduplicated, or a tag the reader could not see). The move refuses every reparse point on
/// the item or a folder above it (<see cref="SafeDelete"/>), so these say why before it fails.
/// </remarks>
public enum RecycleBlock { Protected, ContainsProtected, KeptFolder, SystemFile, WindowsSetup, SystemFolder, NameMismatch, Link, CloudFolder, OtherReparse }

/// <summary>
/// What the storage page may move to the Recycle Bin. The app runs elevated, so a folder is refused when it is, or holds,
/// a system or program location (protected roots, AppData) or a folder a user expects to stay (drive roots, profiles,
/// Desktop, Documents, Downloads and the like): the user can empty those, but not delete them as a whole.
/// </summary>
public static class RecycleRules
{
    private static readonly string[] ProfileSubfolders = ["Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "Saved Games"];

    /// <summary>Known folder ids of Desktop, Documents, Downloads, Pictures, Music, Videos and Saved Games (they can be redirected).</summary>
    private static readonly Guid[] KnownFolderIds =
    [
        new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), new("374DE290-123F-4565-9164-39C4925E467B"),
        new("33E28130-4E1E-4676-835A-98395C3BC3BB"), new("4BD8D571-6D19-48D3-BE97-422220080E43"), new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"),
    ];

    /// <summary>Folders that stay, as full paths with a trailing backslash (case-insensitive set).</summary>
    public static IReadOnlyCollection<string> KeptFolders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                set.Add(Normalize(path).TrimEnd('\\') + "\\");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed)) Add(d.RootDirectory.FullName);

        var profiles = Reg.HklmUnexpanded(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", "ProfilesDirectory") is { Length: > 0 } configured
            ? PathExpander.Machine.Expand(configured)
            : Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));
        Add(profiles);
        if (!string.IsNullOrEmpty(profiles) && Directory.Exists(profiles))
        {
            try
            {
                foreach (var root in Directory.EnumerateDirectories(profiles))
                {
                    Add(root);
                    foreach (var sub in ProfileSubfolders) Add(Path.Combine(root, sub));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var id in KnownFolderIds) Add(KnownFolder(id));
        return set;
    }

    /// <summary>
    /// The first reason a path is not offered, or null. Order: system file, kept folder, protected, folder holds something
    /// protected. The roots and kept folders are normalized like the path, so a different spelling of the same folder
    /// (8.3 name, <c>..</c>, case) still matches.
    /// </summary>
    /// <param name="appData">Also refuse every AppData folder, and a folder that has one directly inside; off only in tests, whose temp folder lies there.</param>
    public static RecycleBlock? Check(string path, bool folder, bool systemFile, IReadOnlyList<string> protectedRoots,
        IReadOnlyCollection<string> keptFolders, bool appData = true) =>
        CheckNormalized(path, folder, systemFile, NormalizeAll(protectedRoots), NormalizeAll(keptFolders), appData);

    /// <summary>The lists in the form <see cref="CheckNormalized"/> expects: normalized, trailing backslash. Done once per batch.</summary>
    public static string[] NormalizeAll(IEnumerable<string> folders) =>
        folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Normalize(f).TrimEnd('\\') + "\\").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// <see cref="Check"/> for lists that <see cref="NormalizeAll"/> already normalized. A name ending in a dot or space is
    /// refused first: everything after it would be checked on the trimmed name, which is another item. A path that does
    /// not name itself in another way (a <c>..</c> segment) is checked where it leads, and refused at the end if nothing
    /// else refused it.
    /// </summary>
    public static RecycleBlock? CheckNormalized(string path, bool folder, bool systemFile, IReadOnlyList<string> protectedRoots,
        IReadOnlyCollection<string> keptFolders, bool appData)
    {
        if (systemFile) return RecycleBlock.SystemFile;
        if (HasTrimmedName(path)) return RecycleBlock.NameMismatch;
        var normalized = Normalize(path);
        var full = normalized.TrimEnd('\\') + "\\";
        if (folder && keptFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) return RecycleBlock.KeptFolder;
        if (StorageAnalyzer.IsProtected(full, protectedRoots, appData)) return RecycleBlock.Protected;
        if (IsWindowsSetup(full)) return RecycleBlock.WindowsSetup;
        if (InHiddenSystemFolder(normalized, folder)) return RecycleBlock.SystemFolder;
        if (folder)
        {
            bool Under(string other) => other.Length > full.Length && other.StartsWith(full, StringComparison.OrdinalIgnoreCase);
            if (protectedRoots.Any(Under) || keptFolders.Any(Under)) return RecycleBlock.ContainsProtected;
            if (appData && Directory.Exists(Path.Combine(full, "AppData"))) return RecycleBlock.ContainsProtected;
        }
        return ResolvesAsWritten(path, normalized) ? null : RecycleBlock.NameMismatch;
    }

    /// <summary>
    /// What the tree says against moving <paramref name="entry"/>: a link itself, or a reparse point that is not a link on
    /// it or a folder above it (a cloud one first), up to the analyzed root. Null when neither; the path rules
    /// (<see cref="Check"/>) still apply.
    /// </summary>
    public static RecycleBlock? CheckEntry(DiskTree tree, int entry)
    {
        if ((tree.Flags(entry) & EntryFlags.ReparsePoint) != 0) return RecycleBlock.Link;
        RecycleBlock? found = null;
        for (var e = entry; e >= 0 && e != tree.Root; e = tree.Parent(e))
        {
            var flags = tree.Flags(e);
            if ((flags & EntryFlags.CloudSync) != 0) return RecycleBlock.CloudFolder;
            if ((flags & EntryFlags.NonLinkReparse) != 0) found ??= RecycleBlock.OtherReparse;
        }
        return found;
    }

    /// <summary>
    /// What <see cref="CheckEntry"/> cannot see for a tree of a folder rather than a drive: the analyzed folder itself and
    /// every folder above it, below the drive root, read from the disk. A cloud folder or another reparse point (a link
    /// too) there holds every entry of the tree, and the move refuses them all, so the page says so for each. Null for a drive
    /// root and when none is one; a folder that cannot be read is skipped (the move still checks it).
    /// </summary>
    /// <param name="read">Attributes and reparse tag of a folder; tests pass their own. Default: <see cref="SafeDelete.AttributesAndTag"/>.</param>
    public static RecycleBlock? CheckAbove(string root, Func<string, (uint Attributes, uint Tag)?>? read = null)
    {
        read ??= SafeDelete.AttributesAndTag;
        var full = Normalize(root);
        var drive = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
        RecycleBlock? found = null;
        for (var p = full; p is not null && p.TrimEnd('\\').Length > drive.Length; p = Path.GetDirectoryName(p))
        {
            if (read(p) is not { } item) continue;
            // A link above the entries (the folder was analyzed through a junction) is not the entry itself: it counts as
            // a reparse point above it, which is what the page then says.
            var flags = ReparseTags.Flags(item.Attributes, item.Tag);
            if ((flags & EntryFlags.CloudSync) != 0) return RecycleBlock.CloudFolder;
            if ((flags & (EntryFlags.ReparsePoint | EntryFlags.NonLinkReparse)) != 0) found ??= RecycleBlock.OtherReparse;
        }
        return found;
    }

    /// <summary>Folders Windows setup keeps in a drive root: the previous installation and the upgrade's working folders.</summary>
    private static readonly string[] WindowsSetupFolders = ["Windows.old", "$Windows.~BT", "$Windows.~WS"];

    /// <summary>True for a Windows setup folder in a drive root (<see cref="WindowsSetupFolders"/>) and anything inside one.</summary>
    internal static bool IsWindowsSetup(string full)
    {
        var root = Path.GetPathRoot(full);
        if (root is not { Length: 3 } || root[1] != ':') return false;
        var rest = full[root.Length..];
        var first = rest.IndexOf('\\') is var end and >= 0 ? rest[..end] : rest;
        return WindowsSetupFolders.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the folder (for a file: the folder it is in) or one above it, below the drive root, is both hidden and
    /// system: Windows keeps its own data there (Config.Msi, $SysReset). System or read-only alone is common on folders
    /// that desktop.ini customizes, so those stay allowed. A folder that cannot be read counts as neither.
    /// </summary>
    internal static bool InHiddenSystemFolder(string normalized, bool folder)
    {
        const FileAttributes hiddenSystem = FileAttributes.Hidden | FileAttributes.System;
        var root = Path.GetPathRoot(normalized) ?? "";
        for (var p = folder ? normalized : Path.GetDirectoryName(normalized); p is not null && p.TrimEnd('\\').Length > root.TrimEnd('\\').Length; p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & hiddenSystem) == hiddenSystem) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
        return false;
    }

    /// <summary>
    /// True when a name in the path ends in a dot or space (other than "." and ".."). Win32 path handling (GetFullPathName)
    /// trims those, so "C:\x\data." would be checked and moved as "C:\x\data", a different item.
    /// </summary>
    public static bool HasTrimmedName(string path) =>
        path.Split('\\', '/').Any(s => s.Length > 0 && s is not "." and not ".." && s[^1] is '.' or ' ');

    /// <summary>
    /// True when <paramref name="raw"/> names the same item as its <see cref="Normalize"/>d form: a full path without a
    /// "\\?\" or "\\.\" prefix whose names are the same apart from case and 8.3 short names (a name with "~" that
    /// Normalize expanded). A name ending in a dot or space, a ".." or "." segment, or a doubled backslash is a mismatch,
    /// so the item checked and moved is never another one than the item confirmed.
    /// </summary>
    public static bool ResolvesAsWritten(string raw, string normalized)
    {
        if (!Path.IsPathFullyQualified(raw) || raw.StartsWith(@"\\?\", StringComparison.Ordinal) || raw.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        var written = raw.Replace('/', '\\').TrimEnd('\\').Split('\\');
        var resolved = normalized.TrimEnd('\\').Split('\\');
        if (written.Length != resolved.Length) return false;
        for (var k = 0; k < written.Length; k++)
        {
            var name = written[k];
            if (name.Length > 0 && name[^1] is '.' or ' ') return false;
            if (string.Equals(name, resolved[k], StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains('~')) continue;   // an 8.3 short name, expanded to its long name
            return false;
        }
        return true;
    }

    /// <summary>
    /// The full path without a trailing backslash, with <c>..</c> resolved and 8.3 short names expanded: the form that
    /// is checked and that is moved, so a spelling cannot slip past the roots and kept folders.
    /// </summary>
    public static string Normalize(string path)
    {
        var full = SafeDelete.LongPath(Path.GetFullPath(path));
        // A drive root keeps its backslash: "C:" alone means the current folder of that drive.
        return full.Length <= 3 ? full : full.TrimEnd('\\');
    }

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) != 0) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
