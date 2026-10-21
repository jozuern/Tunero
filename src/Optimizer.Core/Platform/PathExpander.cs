using System.Text.RegularExpressions;
using Optimizer.Core.Actions;

namespace Optimizer.Core.Platform;

/// <summary>
/// The one way this app expands %variables% in paths and command lines it reads from the system.
/// <para>
/// <see cref="Machine"/> expands as a service or driver sees it: Windows' known folders and the machine variables
/// (HKLM\...\Session Manager\Environment), without any user's variables. <see cref="ForUser(string?, IRegistryRoots?)"/>
/// expands as a program started for the target user sees it: the signed-in user whose profile a change affects, who is not
/// the account this app runs as when an administrator elevated it with a separate account. On top of the machine
/// variables come that user's profile folders and that user's own variables (HKU\&lt;SID&gt;\Environment).
/// </para>
/// <para>
/// This process's own environment is never used: it belongs to the account the app runs as, and it inherits that
/// account's HKCU\Environment, which any program of that user can change. A name the expander does not know stays as it
/// is, so a path never silently points into the wrong account's folders.
/// </para>
/// </summary>
public sealed partial class PathExpander
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> MachineVariables = new(Read);
    private static readonly Lazy<PathExpander> MachineOnly = new(() => new PathExpander(MachineVariables.Value, null));
    private static readonly Lazy<PathExpander> NoUser = new(() => ForUser(null, new Dictionary<string, string>(), MachineVariables.Value));

    private readonly IReadOnlyDictionary<string, string> _variables;

    private PathExpander(IReadOnlyDictionary<string, string> variables, string? profilePath)
    {
        _variables = variables;
        ProfilePath = profilePath;
    }

    /// <summary>The machine's variables only: for services, drivers and anything else Windows starts outside a user's sign-in.</summary>
    public static PathExpander Machine => MachineOnly.Value;

    /// <summary>
    /// For a per-user string when the target user is not known: the machine variables without TEMP and TMP (those are the
    /// user's), and every user folder variable left as it is.
    /// </summary>
    public static PathExpander UnknownUser => NoUser.Value;

    /// <summary>The target user's profile folder; null for <see cref="Machine"/> and when it is not known.</summary>
    public string? ProfilePath { get; }

    /// <summary>%name% replaced by its value; names without one stay as they are.</summary>
    public string Expand(string text) => Expand(text, _variables);

    /// <summary>This expander for a string from the user's hive, <see cref="Machine"/> for one from the machine's.</summary>
    public PathExpander For(Hive hive) => hive == Hive.User ? this : Machine;

    /// <summary>
    /// The target user's expander. <paramref name="profilePath"/> is that user's profile folder (from the profile list by
    /// their SID); <paramref name="registry"/> gives that user's own variables under <see cref="Hive.User"/>\Environment.
    /// Without a profile path the user's folder variables stay unexpanded (<see cref="UnknownUser"/> plus the user's own
    /// variables).
    /// </summary>
    public static PathExpander ForUser(string? profilePath, IRegistryRoots? registry = null) =>
        ForUser(profilePath, registry is null ? new Dictionary<string, string>() : ReadUserVariables(registry), MachineVariables.Value);

    /// <summary>
    /// The variables in the order Windows builds a user's environment: the machine's, the profile folder, the user's own
    /// variables (which may set TEMP and TMP), then the per-session folders (APPDATA, LOCALAPPDATA, HOMEDRIVE, HOMEPATH).
    /// </summary>
    internal static PathExpander ForUser(string? profilePath, IReadOnlyDictionary<string, string> userVariables, IReadOnlyDictionary<string, string> machine)
    {
        var vars = new Dictionary<string, string>(machine, StringComparer.OrdinalIgnoreCase);
        // The machine's TEMP is the system's (C:\Windows\TEMP): a user's programs use the user's own.
        vars.Remove("TEMP");
        vars.Remove("TMP");
        var profile = profilePath is { Length: > 0 } p && Path.IsPathFullyQualified(p) ? p.TrimEnd('\\') : null;
        if (profile is not null) vars["USERPROFILE"] = profile;
        foreach (var (name, value) in userVariables)
        {
            // Path is joined with the machine's, never a folder of its own.
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase) || value.Length == 0) continue;
            vars[name] = Expand(value, vars);
        }
        if (profile is not null)
        {
            var drive = Path.GetPathRoot(profile)!.TrimEnd('\\');
            vars["HOMEDRIVE"] = drive;
            vars["HOMEPATH"] = profile[drive.Length..];
            vars["APPDATA"] = Path.Combine(profile, @"AppData\Roaming");
            vars["LOCALAPPDATA"] = Path.Combine(profile, @"AppData\Local");
            vars.TryAdd("TEMP", Path.Combine(profile, @"AppData\Local\Temp"));
            vars.TryAdd("TMP", Path.Combine(profile, @"AppData\Local\Temp"));
        }
        return new PathExpander(vars, profile);
    }

    /// <summary>The user's own variables as stored (HKU\&lt;SID&gt;\Environment), their %names% not yet expanded.</summary>
    private static Dictionary<string, string> ReadUserVariables(IRegistryRoots registry)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = registry.Open(Hive.User, "Environment", writable: false);
            foreach (var name in key?.GetValueNames() ?? [])
                if (name.Length > 0 && key!.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                    vars[name] = value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No user hive (a service account), or one this process may not read: the profile folders still apply.
        }
        return vars;
    }

    internal static string Expand(string text, IReadOnlyDictionary<string, string> variables) =>
        VariableRegex().Replace(text, m => variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private static IReadOnlyDictionary<string, string> Read()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["ProgramFiles"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramW6432"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Folder(Environment.SpecialFolder.ProgramFilesX86),
            ["CommonProgramFiles"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramW6432"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = Folder(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramData"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["ALLUSERSPROFILE"] = Folder(Environment.SpecialFolder.CommonApplicationData),
        };
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            foreach (var name in key?.GetValueNames() ?? [])
                if (!vars.ContainsKey(name) && key!.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                    vars[name] = value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Logging.Log.Warn("environment", $"machine variables not readable: {ex.Message}");
        }
        // Machine values refer to each other (TEMP is %SystemRoot%\TEMP): one pass over the known folders resolves them.
        foreach (var name in vars.Keys.ToList()) vars[name] = Expand(vars[name], vars);
        return vars;
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex VariableRegex();
}
