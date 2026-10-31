using Optimizer.Core.Platform;

namespace Optimizer.Core.Tools;

/// <summary>
/// The icon cache files of one user, deleted while Explorer is not running so that it builds them anew. Windows 10 and 11
/// keep the caches Explorer uses per icon size in %LocalAppData%\Microsoft\Windows\Explorer (iconcache_16.db,
/// iconcache_32.db, iconcache_idx.db and so on); %LocalAppData%\IconCache.db is the older single cache, still deleted
/// where it exists. The thumbnail caches next to them (thumbcache_*.db) stay.
/// </summary>
public static class IconCache
{
    /// <summary>
    /// Deletes the icon cache files below the user's Local folder. Each goes through <see cref="SafeDelete.DeleteFile"/>
    /// (the user's own folder, from the elevated app: a link swapped in for the file or a folder above it is refused). A
    /// file another program still holds open is left in place and counted, so it does not stop the others.
    /// </summary>
    /// <param name="localAppData">The user's %LocalAppData% folder (AppData\Local of their profile).</param>
    public static (int Deleted, int Skipped) Delete(string localAppData)
    {
        int deleted = 0, skipped = 0;
        foreach (var file in Files(localAppData))
        {
            try
            {
                SafeDelete.DeleteFile(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logging.Log.Warn("iconcache", $"{Path.GetFileName(file)} left in place: {ex.Message}");
                skipped++;
            }
        }
        return (deleted, skipped);
    }

    /// <summary>The icon cache files that exist now: the old IconCache.db and every iconcache_*.db of Explorer's folder.</summary>
    public static IReadOnlyList<string> Files(string localAppData)
    {
        var files = new List<string>();
        var legacy = Path.Combine(localAppData, "IconCache.db");
        if (File.Exists(legacy)) files.Add(legacy);
        var explorer = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
        try
        {
            if (Directory.Exists(explorer))
                files.AddRange(Directory.EnumerateFiles(explorer, "iconcache_*.db")
                    .Where(f => Path.GetFileName(f) is var n && n.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("iconcache", $"cannot list {explorer}: {ex.Message}");
        }
        return files;
    }
}
