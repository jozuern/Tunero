using System.Security.Principal;

namespace Optimizer.Core.Platform;

/// <summary>
/// The app's data folder (backups, logs, settings, tools). Elevated, as every Release start is:
/// %ProgramData%\Tunero, locked to Administrators and SYSTEM. Not elevated (Debug builds, screenshots):
/// %LocalAppData%\Tunero, because the locked folder cannot even be listed then, and a process without admin
/// rights cannot apply anything anyway.
/// </summary>
public static class DataPaths
{
    public static bool ProcessIsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public const string FolderName = "Tunero";

    /// <summary>The folder's name up to 0.7.3, when the app was called PCOptimizer.</summary>
    public const string LegacyFolderName = "PCOptimizer";

    /// <summary>The old folder when this start moved it to <see cref="Root"/> (logged once the log is open), else null.</summary>
    public static string? MovedFrom { get; private set; }

    private static string? _root;

    /// <summary>
    /// The data folder. Reading it changes nothing (tests and tools read it too): a folder from before the rename is
    /// used where it is until <see cref="MigrateLegacy"/>, called once at the start of the app, has moved it.
    /// </summary>
    public static string Root => _root ??= Locate(BaseFolder);

    private static string BaseFolder =>
        Environment.GetFolderPath(ProcessIsElevated ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Points the data folder somewhere else for this process. Only the tests call it (a temp folder), so no test can
    /// write into the real data folder of the PC it runs on, even by accident.
    /// </summary>
    public static void UseRoot(string folder) => _root = folder;

    /// <summary>Moves a data folder from before the rename to the new name (<see cref="ResolveRoot"/>). Only the app's start calls this.</summary>
    public static void MigrateLegacy() => _root = ResolveRoot(BaseFolder);

    /// <summary>The data folder under <paramref name="baseFolder"/> as it is now, without moving anything.</summary>
    public static string Locate(string baseFolder)
    {
        var root = Path.Combine(baseFolder, FolderName);
        var legacy = Path.Combine(baseFolder, LegacyFolderName);
        if (Directory.Exists(root) || !Directory.Exists(legacy)) return root;
        try
        {
            return (File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0 ? root : legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return root;
        }
    }

    /// <summary>
    /// The data folder under <paramref name="baseFolder"/>. A folder from before the rename is moved to the new name once,
    /// with its backups, so every change made by an older version can still be undone. The move is a rename on the same
    /// drive and keeps the folder's permissions; the start secures the folder afterwards as always. A link put in place of
    /// the old folder is not followed. When the move fails (an older version still runs and holds files), the old folder is
    /// used until a later start can move it.
    /// </summary>
    public static string ResolveRoot(string baseFolder)
    {
        var root = Path.Combine(baseFolder, FolderName);
        var legacy = Path.Combine(baseFolder, LegacyFolderName);
        if (Directory.Exists(root) || !Directory.Exists(legacy)) return root;
        try
        {
            if ((File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0) return root;
            Directory.Move(legacy, root);
            MovedFrom = legacy;
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }

    // Every place inside the data folder, named once (backups live in BackupStore, below Root).
    public static string Logs => Path.Combine(Root, "logs");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Tools => Path.Combine(Root, "tools");
    public static string Captures => Path.Combine(Tools, "captures");
    public static string Updates => Path.Combine(Root, "updates");
    public static string Runtime => Path.Combine(Root, "runtime");

    /// <summary>TEMP and TMP of the tools the app starts (admin-only when elevated; the user's %TEMP% is not).</summary>
    public static string Temp => Path.Combine(Root, "temp");
    public static string StartupSnapshot => Path.Combine(Root, "startup-snapshot.json");
    public static string TcpIpResetLog => Path.Combine(Root, "tcpip-reset.log");
}

/// <summary>The running app's version (the version the pre-commit hook raises).</summary>
public static class AppInfo
{
    public static Version Version { get; } = Normalize(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version);

    /// <summary>"0.4.5": three parts, as release tags use.</summary>
    public static string Text => Version.ToString(3);

    private static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}

/// <summary>Sizes as people read them.</summary>
public static class ByteSize
{
    /// <summary>
    /// "1.5 GB", "512 MB", "12 KB". Less than 1 KB that is not nothing reads as 1 KB, as in File Explorer, so a file is
    /// never "0 KB"; a size that would round to 1024 of a unit reads in the next one ("1 MB", not "1024 KB").
    /// </summary>
    public static string Format(long bytes)
    {
        const double kb = 1024, mb = 1L << 20, gb = 1L << 30;
        if (bytes >= gb || Math.Round(bytes / mb) >= 1024) return $"{bytes / gb:0.#} GB";
        if (bytes >= mb || Math.Round(bytes / kb) >= 1024) return $"{bytes / mb:0} MB";
        return bytes is > 0 and < 1024 ? "1 KB" : $"{bytes / kb:0} KB";
    }
}
