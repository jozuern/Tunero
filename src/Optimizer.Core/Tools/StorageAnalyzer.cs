using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Optimizer.Core.Logging;
using Optimizer.Core.Storage;

namespace Optimizer.Core.Tools;

public sealed record DuplicateGroup(long BytesEach, IReadOnlyList<string> Paths)
{
    /// <summary>Space freed by keeping one copy.</summary>
    public long Reclaimable => BytesEach * (Paths.Count - 1);
}

/// <summary>
/// What the Storage page does with a scanned tree (<see cref="DiskTree"/>): duplicate files, protection, the Recycle Bin.
/// Windows, program and game folders are scanned for sizes but are never offered for deletion, and duplicates there are
/// ignored (games ship identical files on purpose). Deleting goes to the Recycle Bin.
/// </summary>
public static class StorageAnalyzer
{
    public const long DuplicateMinBytes = 1L << 20;

    public static IReadOnlyList<string> ProtectedRoots(IEnumerable<string>? gameLibraries)
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            list.Add(Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"));
            list.Add(Path.Combine(d.RootDirectory.FullName, "System Volume Information"));
            list.Add(Path.Combine(d.RootDirectory.FullName, "Recovery"));
        }
        list.AddRange(gameLibraries ?? []);
        return list.Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('\\') + "\\").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Files Windows keeps in the root of a drive (page file, swap file, hibernation file, boot dump log).</summary>
    private static readonly HashSet<string> RootSystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log", "DumpStack.log.tmp",
    };

    /// <param name="appData">Also protect every AppData folder; off only in tests, whose temp folder lies there.</param>
    public static bool IsProtected(string path, IReadOnlyList<string> protectedRoots, bool appData = true)
    {
        var trimmed = path.TrimEnd('\\');
        if (RootSystemFiles.Contains(Path.GetFileName(trimmed))
            && string.Equals(Path.GetDirectoryName(trimmed), Path.GetPathRoot(trimmed), StringComparison.OrdinalIgnoreCase))
            return true;
        var p = trimmed + "\\";
        // AppData holds program state (settings, caches, saves): never offered for deletion.
        if (appData && p.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) return true;
        return protectedRoots.Any(r => p.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Files of the tree that may be duplicates, grouped by size: at least <see cref="DuplicateMinBytes"/>, not system,
    /// not cloud-only (reading one for the hash would download it) and not protected (games ship identical files on
    /// purpose). Only groups with two or more files; their paths are built only for those.
    /// </summary>
    /// <param name="appData">Also leave out every AppData folder; off only in tests, whose temp folder lies there.</param>
    /// <param name="entries">Filled with the tree index of every candidate path, so a list row knows its entry.</param>
    public static IReadOnlyList<List<string>> DuplicateCandidates(DiskTree tree, IReadOnlyList<string> protectedRoots, bool appData = true,
        IDictionary<string, int>? entries = null, CancellationToken ct = default)
    {
        var bySize = new Dictionary<long, List<int>>();
        var stack = new Stack<int>([tree.Root]);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = stack.Pop();
            for (var k = 0; k < tree.ChildCount(folder); k++)
            {
                var c = tree.Child(folder, k);
                if (tree.IsFolder(c))
                {
                    stack.Push(c);
                    continue;
                }
                if (tree.Size(c) < DuplicateMinBytes || (tree.Flags(c) & (EntryFlags.System | EntryFlags.CloudOnly)) != 0) continue;
                if (!bySize.TryGetValue(tree.Size(c), out var same)) bySize[tree.Size(c)] = same = [];
                same.Add(c);
            }
        }
        var result = new List<List<string>>();
        foreach (var same in bySize.Values.Where(v => v.Count > 1))
        {
            ct.ThrowIfCancellationRequested();
            var paths = same.Select(i => (Entry: i, Path: tree.FullPath(i))).Where(e => !IsProtected(e.Path, protectedRoots, appData)).ToList();
            if (paths.Count < 2) continue;
            if (entries is not null) foreach (var (entry, path) in paths) entries[path] = entry;
            result.Add(paths.Select(e => e.Path).ToList());
        }
        return result;
    }

    /// <summary>
    /// Candidates of equal size are compared by the hash of the first 64 KB, then by the full SHA-256. Hard links to one
    /// file count once: recycling one of the paths would free nothing. A file whose live attributes say its data is not
    /// on this PC (<see cref="NotLocal"/>) is left out before it is opened: the tree's cloud flag comes from the scan, and
    /// opening such a file would download it.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> FindDuplicates(IEnumerable<List<string>> sameSize, CancellationToken ct)
    {
        var result = new List<DuplicateGroup>();
        foreach (var candidates in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            var group = candidates.Where(p => !NotLocal(p)).GroupBy(p => Startup.SignatureVerifier.FileId(p) ?? p, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (group.Count < 2) continue;
            foreach (var quick in group.GroupBy(p => Hash(p, 64 * 1024, ct)).Where(g => g.Key is not null && g.Count() > 1))
            {
                foreach (var full in quick.GroupBy(p => Hash(p, long.MaxValue, ct)).Where(g => g.Key is not null && g.Count() > 1))
                {
                    var paths = full.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                    // A file can be deleted while the others are hashed: the size of one that is still there.
                    if (paths.Select(Length).FirstOrDefault(l => l is not null) is { } size) result.Add(new DuplicateGroup(size, paths));
                }
            }
        }
        return result.OrderByDescending(g => g.Reclaimable).ToList();
    }

    private static long? Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const FileAttributes Offline = (FileAttributes)0x1000, RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>
    /// True when the file's attributes, read now, say its data is elsewhere (online only in OneDrive, or moved off by a
    /// storage manager): opening or reading it would bring it back. Reading the attributes does not. True as well when
    /// they cannot be read.
    /// </summary>
    internal static bool NotLocal(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// SHA-256 of the first <paramref name="maxBytes"/> bytes, read in 1 MB parts so Stop ends hashing a large file. Null
    /// for a file that is not on this PC (checked again right before the open: it may have been freed up since).
    /// </summary>
    private static string? Hash(string path, long maxBytes, CancellationToken ct)
    {
        if (NotLocal(path)) return null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long total = 0;
            while (total < maxBytes)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - total));
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                total += read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves files and folders to the Recycle Bin. A path that does not name itself as written is refused first
    /// (<see cref="RecycleRules.ResolvesAsWritten"/>); then each item is checked again with <see cref="RecycleRules.Check"/>
    /// (protected, kept, system, Windows setup and hidden system locations are refused, a folder also when it holds a
    /// protected or kept one); a folder is moved by renaming
    /// it into the Recycle Bin after its folders are pinned and it held no link when checked. An item whose path holds a
    /// reparse point (the item itself or a folder above it: a link, a OneDrive or other cloud item, a compressed file) is
    /// refused from its live attributes by <see cref="Platform.SafeDelete"/>; the page names that reason beforehand
    /// (<see cref="RecycleRules.CheckEntry"/>). The folders of the path stay held while the shell moves the item
    /// (<see cref="Platform.SafeDelete.Pin"/>), so none of them can be swapped for a junction in between; a folder emptied
    /// and turned into a mount point in that moment is reported afterwards. An item that does not fit in the Recycle Bin
    /// would be deleted for good without a question: Windows asks first (FOF_WANTNUKEWARNING), and a "No" counts as a
    /// failure. Returns the failures.
    /// </summary>
    /// <param name="appData">Also refuse every AppData folder; off only in tests, whose temp folder lies there.</param>
    /// <param name="progress">The number of items done before each item.</param>
    public static IReadOnlyList<string> RecycleItems(IReadOnlyList<(string Path, bool Folder)> items, IReadOnlyList<string> protectedRoots,
        IReadOnlyCollection<string> keptFolders, bool appData = true, IProgress<int>? progress = null)
    {
        var failed = new List<string>();
        var done = 0;
        var roots = RecycleRules.NormalizeAll(protectedRoots);
        var kept = RecycleRules.NormalizeAll(keptFolders);
        foreach (var (raw, folder) in items)
        {
            progress?.Report(done++);
            string path;
            try
            {
                path = RecycleRules.Normalize(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failed.Add(raw);
                continue;
            }
            // The normalized path is what is checked and what is moved, so it must name the item that was confirmed: a
            // name ending in a dot or space ("data.") normalizes to another item ("data"), and so does a ".." segment.
            if (!RecycleRules.ResolvesAsWritten(raw, path))
            {
                failed.Add(raw);
                continue;
            }
            var exists = folder ? Directory.Exists(path) : File.Exists(path);
            var system = !folder && exists && (File.GetAttributes(path) & FileAttributes.System) != 0;
            if (!exists || RecycleRules.CheckNormalized(path, folder, system, roots, kept, appData) is not null || (folder && HoldsFolderLink(path)) || !MoveToRecycleBin(path, folder))
                failed.Add(raw);
        }
        return failed;
    }

    /// <summary>
    /// True when a folder below <paramref name="folder"/> is a junction, symbolic link or mount point (or the walk fails):
    /// such a folder is not recycled as a whole, because what it points to may not be what the user saw. Links are not entered.
    /// </summary>
    internal static bool HoldsFolderLink(string folder)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 };
        var stack = new Stack<string>([folder]);
        try
        {
            while (stack.Count > 0)
            {
                foreach (var sub in new DirectoryInfo(stack.Pop()).EnumerateDirectories("*", options))
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                    stack.Push(sub.FullName);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Moves a leftover of an uninstalled program (a folder as a whole, or a shortcut) to the Recycle Bin, with the
    /// same link checks and held folders as <see cref="RecycleItems"/>. Which paths may be offered is decided by
    /// <see cref="Apps.ProgramLeftovers.MayOffer"/>, so the protected roots are not checked here. A folder is moved by
    /// renaming it into the Recycle Bin, which takes a link inside it along as a link and never follows it.
    /// </summary>
    public static bool RecycleLeftover(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (Directory.Exists(full)) return Platform.SafeDelete.HasNoLinks(full) && MoveToRecycleBin(full, folder: true);
        return File.Exists(full) && (File.GetAttributes(full) & FileAttributes.System) == 0 && MoveToRecycleBin(full, folder: false);
    }

    private static bool MoveToRecycleBin(string p, bool folder)
    {
        // The folder it is in may be a drive root (D:\Old backups): a root is held too, it cannot be renamed or replaced.
        using var pin = Platform.SafeDelete.Pin(Path.GetDirectoryName(Path.GetFullPath(p))!, allowRoot: true);
        if (pin is null || !(folder ? Platform.SafeDelete.HasNoLinks(p) : Platform.SafeDelete.ResolvesToItself(p))) return false;
        var (result, aborted) = ShellRecycle(p);
        if (!pin.StillReal())
        {
            Log.Error("storage", $"a folder of {p} became a link while it was moved to the Recycle Bin: check the Recycle Bin");
            return false;
        }
        if (result != 0 || aborted || Path.Exists(p)) return false;
        Log.Info("storage", $"recycled {p}");
        return true;
    }

    /// <summary>
    /// The shell call that moves one checked item to the Recycle Bin (SHFileOperation with undo, on an STA thread): its
    /// result and whether the operation was aborted. The tests replace it in a module initializer with one that fails the
    /// test, so no test reaches the real Recycle Bin even when a refusal it relies on breaks; every check before it stays real.
    /// </summary>
    internal static Func<string, (int Result, bool Aborted)> ShellRecycle = path =>
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
        };
        var result = OnStaThread(() => SHFileOperation(ref op));
        return (result, op.fAnyOperationsAborted);
    };

    /// <summary>
    /// Duplicate groups of which every copy is in <paramref name="selected"/> (the first copy can also be picked in the
    /// list of the largest files). Recycling those would keep no copy at all.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> WholeGroupsSelected(IReadOnlyCollection<string> selected, IEnumerable<DuplicateGroup> groups)
    {
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return groups.Where(g => g.Paths.Count > 0 && g.Paths.All(set.Contains)).ToList();
    }

    /// <summary>
    /// The shell's file operations expect a single-threaded apartment: its question before deleting a file for good (one
    /// that does not fit in the Recycle Bin) belongs to such a thread, not to a thread-pool thread.
    /// </summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }) { IsBackground = true, Name = "recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Recycle Bin operation failed", error);
        return result;
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
