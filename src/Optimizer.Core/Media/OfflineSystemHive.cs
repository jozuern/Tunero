using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Optimizer.Core.Platform;
using AppLog = Optimizer.Core.Logging.Log;

namespace Optimizer.Core.Media;

/// <summary>The SYSTEM hive of an offline image, loaded until disposed; disposing unloads it.</summary>
public interface IOfflineHive : IDisposable
{
    /// <summary>A value of the hive's own Setup key (HKLM\SYSTEM\Setup on a running Windows), or null when it is not there.</summary>
    string? SetupValue(string name);
}

/// <summary>
/// The SYSTEM hive of a mounted image, loaded under HKLM with RegLoadKey as <see cref="DefaultProfileHive"/> loads the
/// Default profile (and as `reg load` does) and only read. The name carries the process id: a hive that a killed Tunero
/// left loaded holds its file open, so its image cannot be unmounted until <see cref="UnloadLeftovers"/> unloads it, and
/// another running Tunero's hive is never touched. Not covered by unit tests (it loads a hive into the registry); the
/// VM test drives it.
/// </summary>
public sealed class OfflineSystemHive : IOfflineHive
{
    internal const string MountPrefix = "TuneroMediaSystem-";
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    private static int _count;
    private readonly string _name;
    private int _unloaded;

    private OfflineSystemHive(string name) => _name = name;

    /// <summary>Loads the hive file. Throws IOException when it cannot be loaded, UnauthorizedAccessException without administrator rights.</summary>
    public static OfflineSystemHive Load(string file)
    {
        var name = $"{MountPrefix}{Environment.ProcessId}-{Interlocked.Increment(ref _count)}";
        using var restore = FirmwareExtras.EnablePrivilege("SeRestorePrivilege");
        using var backup = FirmwareExtras.EnablePrivilege("SeBackupPrivilege");
        if (restore is null || backup is null) throw new UnauthorizedAccessException("Loading a registry hive needs administrator rights.");
        var rc = RegLoadKey(HkeyLocalMachine, name, file);
        if (rc != 0) throw new IOException($"{file} could not be loaded as a registry hive: {new Win32Exception(rc).Message}");
        AppLog.Info("media", "offline SYSTEM hive loaded", new { file, name });
        return new OfflineSystemHive(name);
    }

    public string? SetupValue(string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var setup = hklm.OpenSubKey(_name + @"\Setup", writable: false);
        return setup?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            string[] lines => string.Join(" ", lines),
            var value => value.ToString(),
        };
    }

    /// <summary>Unloads the hive (tried again for a moment while a handle stays open). Never throws: a hive left loaded is logged.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _unloaded, 1) == 1) return;
        Unload(_name);
    }

    /// <summary>Unloads hives that Tunero processes no longer running left loaded. Never throws.</summary>
    public static void UnloadLeftovers()
    {
        string[] names;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            names = hklm.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }
        foreach (var name in names.Where(n => n.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            // This process unloads its own hives itself; a running Tunero's hive belongs to its build.
            var pid = name.AsSpan(MountPrefix.Length);
            if (pid.IndexOf('-') is var dash and > 0) pid = pid[..dash];
            if (int.TryParse(pid, out var id) && (id == Environment.ProcessId || DefaultProfileHive.IsTuneroRunning(id))) continue;
            Unload(name);
        }
    }

    // A key handle that is still open (antivirus, a search indexer) makes the unload fail for a moment: try again.
    private const int UnloadTries = 20;

    private static void Unload(string name)
    {
        for (var i = 1; ; i++)
        {
            int rc;
            using (FirmwareExtras.EnablePrivilege("SeRestorePrivilege"))
            using (FirmwareExtras.EnablePrivilege("SeBackupPrivilege"))
                rc = RegUnLoadKey(HkeyLocalMachine, name);
            if (rc is 0 or 2 /* ERROR_FILE_NOT_FOUND: not loaded */)
            {
                if (rc == 0) AppLog.Info("media", "offline SYSTEM hive unloaded", new { name });
                return;
            }
            if (i == UnloadTries)
            {
                AppLog.Warn("media", $"offline SYSTEM hive {name} not unloaded: {new Win32Exception(rc).Message}");
                return;
            }
            Thread.Sleep(100);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegLoadKey(IntPtr hKey, string subKey, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegUnLoadKey(IntPtr hKey, string subKey);
}
