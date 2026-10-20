using System.ComponentModel;
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
/// The SYSTEM hive of a mounted image, loaded under HKLM (<see cref="HiveFile"/>, as <see cref="DefaultProfileHive"/>
/// loads the Default profile) and only read. The name carries the process id: a hive that a killed Tunero left loaded
/// holds its file open, so its image cannot be unmounted until <see cref="UnloadLeftovers"/> unloads it, and another
/// running Tunero's hive is never touched. Not covered by unit tests beyond <see cref="LeftoversToUnload"/> (it loads a
/// hive into the registry); the VM test drives it.
/// </summary>
public sealed class OfflineSystemHive : IOfflineHive
{
    internal const string MountPrefix = "TuneroMediaSystem-";
    private static int _count;

    /// <summary>This process's hives that a build holds now; any other one of this process is a leftover.</summary>
    private static readonly HashSet<string> Held = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _name;
    private int _unloaded;

    private OfflineSystemHive(string name) => _name = name;

    /// <summary>Loads the hive file. Throws IOException when it cannot be loaded, UnauthorizedAccessException without administrator rights.</summary>
    public static OfflineSystemHive Load(string file)
    {
        var name = $"{MountPrefix}{Environment.ProcessId}-{Interlocked.Increment(ref _count)}";
        // Held before it exists: UnloadLeftovers running meanwhile never takes it for a leftover.
        lock (Held) Held.Add(name);
        var rc = HiveFile.Load(HiveFile.LocalMachine, name, file);
        if (rc != 0)
        {
            lock (Held) Held.Remove(name);
            if (rc is null) throw new UnauthorizedAccessException("Loading a registry hive needs administrator rights.");
            throw new IOException($"{file} could not be loaded as a registry hive: {new Win32Exception(rc.Value).Message}");
        }
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

    /// <summary>
    /// Unloads the hive (tried again for a moment while a handle stays open). Never throws: a hive left loaded is logged,
    /// and as no build holds it any more, the next <see cref="UnloadLeftovers"/> (also in this process) tries again.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _unloaded, 1) == 1) return;
        try
        {
            Unload(_name);
        }
        finally
        {
            lock (Held) Held.Remove(_name);
        }
    }

    /// <summary>Unloads the hives that no build holds: those of Tunero processes no longer running and this process's given-up ones. Never throws.</summary>
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
        string[] held;
        lock (Held) held = [.. Held];
        foreach (var name in LeftoversToUnload(names, Environment.ProcessId, held, DefaultProfileHive.IsTuneroRunning)) Unload(name);
    }

    /// <summary>
    /// Of the loaded hive <paramref name="names"/>, this class's ones that no build holds: this process's that are not
    /// <paramref name="held"/>, and those of processes that are no running Tunero (or whose name has no readable id).
    /// </summary>
    internal static List<string> LeftoversToUnload(IEnumerable<string> names, int ownPid, IReadOnlyCollection<string> held, Func<int, bool> isTuneroRunning)
    {
        var leftovers = new List<string>();
        foreach (var name in names.Where(n => n.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var pid = name.AsSpan(MountPrefix.Length);
            if (pid.IndexOf('-') is var dash and > 0) pid = pid[..dash];
            if (int.TryParse(pid, out var id) && (id == ownPid ? held.Contains(name, StringComparer.OrdinalIgnoreCase) : isTuneroRunning(id))) continue;
            leftovers.Add(name);
        }
        return leftovers;
    }

    private static void Unload(string name) => HiveFile.Retry(lastTry =>
    {
        var rc = HiveFile.TryUnload(HiveFile.LocalMachine, name);
        if (rc is 0 or 2 /* ERROR_FILE_NOT_FOUND: not loaded */)
        {
            if (rc == 0) AppLog.Info("media", "offline SYSTEM hive unloaded", new { name });
            return true;
        }
        if (!lastTry) return false;
        AppLog.Warn("media", $"offline SYSTEM hive {name} not unloaded: {new Win32Exception(rc).Message}");
        return true;
    });
}
