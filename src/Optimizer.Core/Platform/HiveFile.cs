using System.Runtime.InteropServices;

namespace Optimizer.Core.Platform;

/// <summary>
/// A registry hive file loaded under HKU or HKLM with RegLoadKey (as `reg load` does) and unloaded again, each call with
/// the backup and restore privileges it needs. Shared by <see cref="DefaultProfileHive"/> and
/// <see cref="Media.OfflineSystemHive"/>, which keep their own names, logs and bookkeeping.
/// </summary>
internal static class HiveFile
{
    internal static readonly IntPtr Users = new(unchecked((int)0x80000003));
    internal static readonly IntPtr LocalMachine = new(unchecked((int)0x80000002));

    /// <summary>A key handle that is still open (antivirus, a search indexer) makes an unload fail for a moment: tried this often, 100 ms apart.</summary>
    internal const int UnloadTries = 20;

    /// <summary>Loads <paramref name="file"/> as <paramref name="root"/>\<paramref name="name"/>: the Win32 result (0 when loaded), or null without the privileges.</summary>
    internal static int? Load(IntPtr root, string name, string file)
    {
        using var restore = FirmwareExtras.EnablePrivilege("SeRestorePrivilege");
        using var backup = FirmwareExtras.EnablePrivilege("SeBackupPrivilege");
        if (restore is null || backup is null) return null;
        return RegLoadKey(root, name, file);
    }

    /// <summary>One unload attempt: 0 unloaded, 2 (ERROR_FILE_NOT_FOUND) not loaded, otherwise the Win32 error.</summary>
    internal static int TryUnload(IntPtr root, string name)
    {
        using var restore = FirmwareExtras.EnablePrivilege("SeRestorePrivilege");
        using var backup = FirmwareExtras.EnablePrivilege("SeBackupPrivilege");
        return RegUnLoadKey(root, name);
    }

    /// <summary>Calls <paramref name="attempt"/> (told whether it is the last try) until it returns true, 100 ms apart, at most <see cref="UnloadTries"/> times.</summary>
    internal static void Retry(Func<bool, bool> attempt)
    {
        for (var i = 1; !attempt(i == UnloadTries); i++) Thread.Sleep(100);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegLoadKey(IntPtr hKey, string subKey, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegUnLoadKey(IntPtr hKey, string subKey);
}
