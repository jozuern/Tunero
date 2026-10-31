using System.Globalization;
using System.IO.Enumeration;
using Optimizer.Core.Actions;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Cleanup;

/// <summary>What a cleanup category deletes and how careful it has to be.</summary>
public sealed record CleanupCategory(
    string Id,
    IReadOnlyList<string> Roots,
    string Pattern = "*",
    bool Recursive = true,
    TimeSpan? MinAge = null,
    bool DefaultSelected = true,
    string? WarningKey = null,
    bool RemoveRoots = false);

public sealed record CleanupScan(CleanupCategory Category, long Bytes, int Files, IReadOnlyList<string> ExistingRoots);

public sealed record CleanupResult(string CategoryId, long FreedBytes, int Deleted, int Skipped);

/// <summary>
/// Disk cleanup. Every category is scanned first (sizes shown), and only the selected ones are deleted.
/// Never follows reparse points (junctions, symbolic links, mount points), skips files in use and, for temp folders,
/// files younger than a day (installers may still need them). Paths are resolved for the signed-in user, not the
/// elevated account.
/// </summary>
public static class CleanupEngine
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);

    /// <summary>Categories for this PC. <paramref name="profilePath"/> is the session user's profile folder.</summary>
    public static IReadOnlyList<CleanupCategory> Categories(string? profilePath, string? userSid, string? windowsDir = null, DateTime? now = null)
    {
        var win = windowsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = profilePath is null ? null : Path.Combine(profilePath, @"AppData\Local");
        var list = new List<CleanupCategory>
        {
            new("cleanup.windowsTemp", [Path.Combine(win, "Temp")], MinAge: TempMinAge, WarningKey: "cleanup.warn.temp"),
            new("cleanup.updateDownloads", [Path.Combine(win, @"SoftwareDistribution\Download")], MinAge: TimeSpan.FromDays(3), WarningKey: "cleanup.warn.updateDownloads"),
            new("cleanup.errorReports", [Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue")]),
            new("cleanup.memoryDumps", [Path.Combine(win, "Minidump")], DefaultSelected: false),
            new("cleanup.memoryDumpFile", [win], Pattern: "MEMORY.DMP", Recursive: false, DefaultSelected: false),
            // The folders Windows Update repair renamed instead of deleting (Tools); Windows built new ones. Removed whole.
            new("cleanup.updateRepairLeftovers", UpdateRepairLeftovers(win, (now ?? DateTime.Now).AddDays(-RepairLeftoverDays)),
                DefaultSelected: false, WarningKey: "cleanup.warn.updateRepairLeftovers", RemoveRoots: true),
        };
        if (local is not null)
        {
            list.Add(new("cleanup.userTemp", [Path.Combine(local, "Temp")], MinAge: TempMinAge, WarningKey: "cleanup.warn.temp"));
            list.Add(new("cleanup.thumbnails", [Path.Combine(local, @"Microsoft\Windows\Explorer")], Pattern: "thumbcache_*.db", Recursive: false));
            // A dump of a crash a moment ago may still be needed to report it.
            list.Add(new("cleanup.crashDumps", [Path.Combine(local, "CrashDumps")], MinAge: TimeSpan.FromDays(7), WarningKey: "cleanup.warn.crashDumps"));
            list.Add(new("cleanup.browserCache",
            [
                Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache"),
                Path.Combine(local, @"Microsoft\Edge\User Data\Default\Code Cache"),
                Path.Combine(local, @"Google\Chrome\User Data\Default\Cache"),
                Path.Combine(local, @"Google\Chrome\User Data\Default\Code Cache"),
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"),
                .. FirefoxCaches(local),
            ], DefaultSelected: false, WarningKey: "cleanup.warn.browser"));
            // Shader caches: deleting them makes games recompile shaders (stutter on the next start). Never preselected.
            list.Add(new("cleanup.shaderCaches",
            [
                Path.Combine(local, "D3DSCache"),
                Path.Combine(local, @"NVIDIA\DXCache"),
                Path.Combine(local, @"NVIDIA\GLCache"),
                Path.Combine(local, @"AMD\DxCache"),
                Path.Combine(local, @"AMD\DxcCache"),
                Path.Combine(local, @"AMD\VkCache"),
                Path.Combine(local, @"Intel\ShaderCache"),
            ], DefaultSelected: false, WarningKey: "cleanup.warn.shaders"));
        }
        if (userSid is not null)
            list.Add(new("cleanup.recycleBin", DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", userSid)).ToList(), DefaultSelected: false, WarningKey: "cleanup.warn.recycleBin"));
        return list;
    }

    /// <summary>How long the folders of an update repair are kept, in case the repair has to be checked or undone by hand.</summary>
    public const int RepairLeftoverDays = 7;

    /// <summary>
    /// SoftwareDistribution.old-yyyyMMdd-HHmmss and System32\catroot2.old-yyyyMMdd-HHmmss (the names UpdateRepair gives
    /// them) from before <paramref name="olderThan"/>, read from the date in the name.
    /// </summary>
    internal static IReadOnlyList<string> UpdateRepairLeftovers(string win, DateTime olderThan)
    {
        var list = new List<string>();
        foreach (var (folder, name) in new[] { (win, "SoftwareDistribution"), (Path.Combine(win, "System32"), "catroot2") })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var dir in Directory.EnumerateDirectories(folder, name + ".old-*"))
                {
                    var stamp = Path.GetFileName(dir)[(name.Length + 5)..];
                    if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) && when < olderThan)
                        list.Add(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("cleanup", $"{folder} not readable: {ex.Message}");
            }
        }
        return list;
    }

    private static IEnumerable<string> FirefoxCaches(string local)
    {
        var profiles = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) return [];
        try
        {
            return Directory.GetDirectories(profiles).Select(p => Path.Combine(p, "cache2"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Roots that exist and are real folders all the way up: a user can replace %LOCALAPPDATA%\Temp (or a parent such as
    /// %LOCALAPPDATA%\NVIDIA) with a junction to a folder they could not delete from, and this process runs elevated.
    /// </summary>
    private static IEnumerable<string> SafeRoots(CleanupCategory c)
    {
        foreach (var root in c.Roots.Where(Directory.Exists))
        {
            if (SafeDelete.HasNoLinks(root)) yield return root;
            else Log.Warn("cleanup", $"{c.Id}: skipped {root}, it is or lies under a junction or symbolic link");
        }
    }

    public static CleanupScan Scan(CleanupCategory c, DateTime? now = null)
    {
        long bytes = 0;
        var files = 0;
        var roots = SafeRoots(c).ToList();
        foreach (var f in roots.SelectMany(r => Files(r, c, now ?? DateTime.UtcNow)))
        {
            bytes += f.Length;
            files++;
        }
        return new CleanupScan(c, bytes, files, roots);
    }

    public static CleanupResult Clean(CleanupCategory c, DateTime? now = null)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        var utcNow = now ?? DateTime.UtcNow;
        foreach (var root in SafeRoots(c))
        {
            foreach (var f in Files(root, c, utcNow).ToList())
            {
                try
                {
                    var length = f.Length;
                    // Through a handle, after checking it still resolves to this path (a folder may have been swapped
                    // for a junction since the scan).
                    SafeDelete.DeleteFile(f.FullName);
                    freed += length;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++; // in use or protected: leave it
                }
            }
            if (c.Recursive) RemoveEmptyDirectories(root, c.MinAge, utcNow);
            if (c.RemoveRoots)
                try
                {
                    SafeDelete.DeleteEmptyDirectory(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("cleanup", $"{root} not removed: {ex.Message}");
                }
        }
        Log.Info("cleanup", c.Id, new { freed, deleted, skipped });
        return new CleanupResult(c.Id, freed, deleted, skipped);
    }

    /// <summary>Files of a category. Directories that are reparse points are never entered, file links are never touched.</summary>
    public static IEnumerable<FileInfo> Files(string root, CleanupCategory c, DateTime utcNow)
    {
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo sub)
                {
                    if (c.Recursive) stack.Push(sub);
                    continue;
                }
                if (e is not FileInfo file || !FileSystemName.MatchesSimpleExpression(c.Pattern, file.Name)) continue;
                // desktop.ini gives folders like the Recycle Bin their look; Windows needs it there.
                if (file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.MinAge is { } age && utcNow - Newest(file) < age) continue;
                yield return file;
            }
        }
    }

    private static DateTime Newest(FileInfo f) => new[] { f.LastWriteTimeUtc, f.CreationTimeUtc }.Max();

    /// <summary>
    /// Removes the folders a cleanup left empty below the root; the root itself stays. With <paramref name="minAge"/> a
    /// folder created more recently stays as its files do: a running installer or program may have just made it and not
    /// written to it yet. Its creation time counts, since the deletes above have just changed its last write time.
    /// </summary>
    private static void RemoveEmptyDirectories(string root, TimeSpan? minAge, DateTime utcNow)
    {
        var dirs = new List<DirectoryInfo>();
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            dirs.Add(d);
            try
            {
                foreach (var sub in d.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var d in Enumerable.Reverse(dirs))
        {
            if (string.Equals(d.FullName.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (minAge is { } age && utcNow - d.CreationTimeUtc < age) continue;
                if (!d.EnumerateFileSystemInfos().Any()) SafeDelete.DeleteEmptyDirectory(d.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Component store cleanup (superseded Windows components) through DISM; can take several minutes.</summary>
    public static (int ExitCode, string Output) ComponentStoreCleanup(IProcessRunner processes) =>
        processes.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /NoRestart", TimeSpan.FromMinutes(60));

    /// <summary>Delivery Optimization cache through its documented cmdlet.</summary>
    public static (int ExitCode, string Output) DeliveryOptimizationCleanup(IProcessRunner processes) =>
        processes.Run(PowerShellScript.Exe, PowerShellScript.Command("Delete-DeliveryOptimizationCache -Force"), TimeSpan.FromMinutes(5));
}
