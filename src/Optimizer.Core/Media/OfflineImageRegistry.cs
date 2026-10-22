using System.ComponentModel;
using Optimizer.Core.Interop;
using Optimizer.Core.Platform;
using AppLog = Optimizer.Core.Logging.Log;

namespace Optimizer.Core.Media;

/// <summary>
/// A registry hive file of the mounted install.wim (SYSTEM or SOFTWARE), loaded until disposed and open for DWORD values.
/// Disposing unloads it.
/// </summary>
public interface IWritableHive : IDisposable
{
    /// <summary>Sets a DWORD value (the key is created when it is missing) and reads it back; throws IOException when it does not read back as written.</summary>
    void SetDword(string key, string name, int value);

    /// <summary>A DWORD value, or null when the key or the value is not there. Throws IOException for a value of another type.</summary>
    int? GetDword(string key, string name);

    /// <summary>
    /// "ControlSet001" and the like: the control set the image boots from, named by <c>Select\Current</c> of a SYSTEM hive
    /// (an offline SYSTEM hive has no CurrentControlSet). Throws IOException when the hive names none or the key is missing.
    /// </summary>
    string ControlSet { get; }

    /// <summary>
    /// Unloads the hive now. Throws IOException when it gives up (a handle stays open): the hive is then still loaded, and
    /// <see cref="IDisposable.Dispose"/> (which never throws) and the leftover unload try again.
    /// </summary>
    void Unload();
}

/// <summary>
/// A hive of a mounted install.wim loaded under HKLM (<see cref="HiveFile"/>) and written with
/// <c>REG_OPTION_BACKUP_RESTORE</c> while SeBackupPrivilege and SeRestorePrivilege are enabled: that skips the access check,
/// so keys that only TrustedInstaller may write are set without changing their ACL. It follows <see cref="OfflineSystemHive"/>
/// (the process id in the name, the held set, <see cref="OfflineSystemHive.UnloadLeftovers"/> for a hive a killed Tunero left
/// loaded, which holds its file of the mount open and so blocks the discard). Every value is read back after it is written.
/// Not covered by unit tests (it loads a hive into the registry, which no test may do); the VM test drives it.
/// </summary>
public sealed class OfflineImageRegistry : IWritableHive
{
    internal const string MountPrefix = "TuneroMediaImage-";
    private static int _count;

    private readonly string _name;
    private int _unloaded;

    private OfflineImageRegistry(string name) => _name = name;

    /// <summary>Loads the hive file. Throws IOException when it cannot be loaded, UnauthorizedAccessException without administrator rights.</summary>
    public static OfflineImageRegistry Load(string file)
    {
        var name = $"{MountPrefix}{Environment.ProcessId}-{Interlocked.Increment(ref _count)}";
        OfflineSystemHive.Hold(name);
        var rc = HiveFile.Load(HiveFile.LocalMachine, name, file);
        if (rc != 0)
        {
            OfflineSystemHive.Release(name);
            if (rc is null) throw new UnauthorizedAccessException("Loading a registry hive needs administrator rights.");
            throw new IOException($"{file} could not be loaded as a registry hive: {new Win32Exception(rc.Value).Message}");
        }
        AppLog.Info("media", "offline image hive loaded", new { file, name });
        return new OfflineImageRegistry(name);
    }

    public string ControlSet
    {
        get
        {
            var current = GetDword("Select", "Current") ?? throw new IOException(@"The SYSTEM hive of the image names no control set (Select\Current).");
            if (current is < 1 or > 999) throw new IOException($@"Select\Current of the image is {current}, not a control set number.");
            var controlSet = $"ControlSet{current:D3}";
            // A control set that is not there must not be created by the write that follows.
            if (!KeyExists(controlSet)) throw new IOException($"The image has no {controlSet}, which Select\\Current names.");
            return controlSet;
        }
    }

    public void SetDword(string key, string name, int value)
    {
        using var restore = Privilege("SeRestorePrivilege");
        using var backup = Privilege("SeBackupPrivilege");
        var rc = NativeWrite.RegCreateKeyEx(HiveFile.LocalMachine, SubKey(key), 0, null, NativeWrite.RegOptionBackupRestore,
            NativeWrite.KeySetValue | NativeWrite.KeyQueryValue, IntPtr.Zero, out var handle, out _);
        if (rc != 0) throw Failure($@"{key} could not be opened for writing", rc);
        try
        {
            rc = NativeWrite.RegSetValueEx(handle, name, 0, NativeWrite.RegDword, ref value, sizeof(int));
            if (rc != 0) throw Failure($@"{key}\{name} could not be set", rc);
        }
        finally
        {
            NativeWrite.RegCloseKey(handle);
        }
        if (GetDword(key, name) != value) throw new IOException($@"{key}\{name} does not read back as {value}.");
    }

    public int? GetDword(string key, string name)
    {
        using var restore = Privilege("SeRestorePrivilege");
        using var backup = Privilege("SeBackupPrivilege");
        // Options 0: RegOpenKeyEx documents only 0 and REG_OPTION_OPEN_LINK, and Administrators can read these keys.
        // REG_OPTION_BACKUP_RESTORE stays on RegCreateKeyEx, where it is documented.
        var rc = NativeWrite.RegOpenKeyEx(HiveFile.LocalMachine, SubKey(key), 0, NativeWrite.KeyQueryValue, out var handle);
        if (rc == 2) return null;   // ERROR_FILE_NOT_FOUND
        if (rc != 0) throw Failure($@"{key} could not be opened", rc);
        try
        {
            var size = (uint)sizeof(int);
            rc = NativeWrite.RegQueryValueEx(handle, name, IntPtr.Zero, out var type, out var data, ref size);
            if (rc == 2) return null;
            // ERROR_MORE_DATA: the value is longer than a DWORD.
            if (rc == 234) throw new IOException($@"{key}\{name} is not a DWORD value.");
            if (rc != 0) throw Failure($@"{key}\{name} could not be read", rc);
            if (type != NativeWrite.RegDword || size != sizeof(int)) throw new IOException($@"{key}\{name} is not a DWORD value.");
            return data;
        }
        finally
        {
            NativeWrite.RegCloseKey(handle);
        }
    }

    private bool KeyExists(string key)
    {
        using var restore = Privilege("SeRestorePrivilege");
        using var backup = Privilege("SeBackupPrivilege");
        var rc = NativeWrite.RegOpenKeyEx(HiveFile.LocalMachine, SubKey(key), 0, NativeWrite.KeyQueryValue, out var handle);
        if (rc == 0) NativeWrite.RegCloseKey(handle);
        return rc == 0;
    }

    public void Unload()
    {
        if (Volatile.Read(ref _unloaded) == 1) return;
        if (!OfflineSystemHive.TryUnload(_name)) throw new IOException($"The image hive {_name} could not be unloaded: a handle stays open.");
        Interlocked.Exchange(ref _unloaded, 1);
        OfflineSystemHive.Release(_name);
    }

    /// <summary>Unloads the hive (tried again for a moment while a handle stays open). Never throws: a hive left loaded is logged and the next <see cref="OfflineSystemHive.UnloadLeftovers"/> tries again.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _unloaded, 1) == 1) return;
        try
        {
            OfflineSystemHive.Unload(_name);
        }
        finally
        {
            OfflineSystemHive.Release(_name);
        }
    }

    private string SubKey(string key) => _name + @"\" + key;

    private static IDisposable Privilege(string name) =>
        FirmwareExtras.EnablePrivilege(name) ?? throw new UnauthorizedAccessException($"{name} is needed to write the image's registry and could not be enabled.");

    private static Exception Failure(string what, int rc) =>
        rc == 5 ? new UnauthorizedAccessException($"{what}: {new Win32Exception(rc).Message}") : new IOException($"{what}: {new Win32Exception(rc).Message}");
}
