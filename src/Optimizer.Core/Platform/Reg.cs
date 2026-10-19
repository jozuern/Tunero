using Microsoft.Win32;

namespace Optimizer.Core.Platform;

/// <summary>Read-only registry helpers. Always use the 64-bit view (the app is x64 only).</summary>
public static class Reg
{
    public static RegistryKey? OpenHklm(string path) =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path, writable: false);

    public static object? HklmValue(string path, string name)
    {
        try
        {
            using var key = OpenHklm(path);
            return key?.GetValue(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? HklmString(string path, string name) => HklmValue(path, name)?.ToString();

    /// <summary>
    /// A REG_EXPAND_SZ value as stored, with its %variables% left in: .NET would expand them with this process's
    /// environment, which includes the signed-in user's own variables (HKCU\Environment). Expand it with
    /// <see cref="MachineEnvironment.Expand"/> instead.
    /// </summary>
    public static string? HklmUnexpanded(string path, string name)
    {
        try
        {
            using var key = OpenHklm(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static int? HklmInt(string path, string name) => HklmValue(path, name) switch
    {
        int i => i,
        // A QWORD that does not fit is not cut to its low 32 bits (a different number): it reads as unknown.
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, out var p) => p,
        _ => null,
    };

    public static bool HklmKeyExists(string path)
    {
        try
        {
            using var key = OpenHklm(path);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string[] HklmSubKeys(string path)
    {
        try
        {
            using var key = OpenHklm(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
