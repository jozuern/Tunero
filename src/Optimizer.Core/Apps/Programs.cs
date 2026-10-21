using System.Globalization;
using Optimizer.Core.Actions;
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;

namespace Optimizer.Core.Apps;

/// <summary>
/// A desktop program from the Windows uninstall list (what Settings > Apps > Installed apps shows). The quiet command,
/// install folder and icon come from the same key and are only read for a quiet uninstall and the leftover scan.
/// </summary>
public sealed record DesktopProgram(
    string Name,
    string? Publisher,
    string? Version,
    long? SizeKb,
    DateTime? InstallDate,
    string? UninstallString,
    string? ProductCode,
    bool PerUser,
    string RegistryKey,
    string? QuietUninstallString = null,
    string? InstallLocation = null,
    string? DisplayIcon = null,
    bool SystemComponent = false)
{
    public bool CanUninstall => ProductCode is not null || !string.IsNullOrWhiteSpace(UninstallString);
}

/// <summary>How an uninstall runs: Windows Installer (msiexec from System32), elevated, or as the signed-in user.</summary>
public enum UninstallMode { WindowsInstaller, Elevated, AsUser }

public sealed record UninstallCommand(UninstallMode Mode, string File, string Arguments);

/// <summary>
/// Reads the Uninstall keys (64-bit, 32-bit and the session user's) and builds the uninstall command. The program's
/// own uninstaller runs elevated only when it is in a folder only administrators can change; a per-user program or an
/// uninstaller in a user-writable folder is started as the signed-in user, and asks for administrator rights itself if
/// it needs them.
/// </summary>
public static class Programs
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <param name="systemComponents">
    /// Also the entries Settings hides as parts of Windows or of another product (SystemComponent 1), marked as such: the
    /// app catalog needs them to see installed runtimes (WebView2 registers that way). The uninstall list leaves them out.
    /// </param>
    public static IReadOnlyList<DesktopProgram> Read(IRegistryRoots registry, bool systemComponents = false)
    {
        var list = new List<DesktopProgram>();
        foreach (var (hive, path) in new[] { (Hive.Machine, Uninstall), (Hive.Machine, Uninstall32), (Hive.User, Uninstall) })
        {
            Microsoft.Win32.RegistryKey? key;
            try
            {
                key = registry.Open(hive, path, writable: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue; // no user hive
            }
            using (key)
            {
                foreach (var sub in key?.GetSubKeyNames() ?? [])
                {
                    using var app = key!.OpenSubKey(sub);
                    if (app is null || Read(app, sub, hive, path) is not { } p || (p.SystemComponent && !systemComponents)) continue;
                    list.Add(p);
                }
            }
        }
        // The same program can be listed by both views; keep the entry that can be uninstalled.
        return list.GroupBy(p => (p.Name, p.Version), new NameVersionComparer())
            .Select(g => g.OrderByDescending(p => p.CanUninstall).First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private sealed class NameVersionComparer : IEqualityComparer<(string, string?)>
    {
        public bool Equals((string, string?) a, (string, string?) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string?) v) => StringComparer.OrdinalIgnoreCase.GetHashCode(v.Item1);
    }

    private static DesktopProgram? Read(Microsoft.Win32.RegistryKey app, string sub, Hive hive, string path)
    {
        if (app.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) return null;
        // Updates and parts of other products are not listed in Settings either; Windows components are marked.
        var component = app.GetValue("SystemComponent") is int sc && sc == 1;
        if (app.GetValue("ParentKeyName") is string { Length: > 0 }) return null;
        if (app.GetValue("ReleaseType") is string release && release is "Update" or "Hotfix" or "Security Update") return null;
        var uninstall = app.GetValue("UninstallString", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        string? productCode = null;
        if (app.GetValue("WindowsInstaller") is int wi && wi == 1 && Guid.TryParse(sub, out var code)) productCode = code.ToString("B").ToUpperInvariant();
        long? size = app.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null;
        DateTime? date = app.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        return new DesktopProgram(name.Trim(), app.GetValue("Publisher") as string, app.GetValue("DisplayVersion") as string, size, date,
            uninstall, productCode, hive == Hive.User, $@"{(hive == Hive.Machine ? "HKLM" : "HKCU")}\{path}\{sub}",
            app.GetValue("QuietUninstallString", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            app.GetValue("InstallLocation", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            app.GetValue("DisplayIcon", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            component);
    }

    /// <summary>True while the program's uninstall key still exists (the uninstaller has not finished or was cancelled).</summary>
    public static bool StillListed(IRegistryRoots registry, DesktopProgram p)
    {
        var (hive, path) = p.RegistryKey.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ? (Hive.Machine, p.RegistryKey[5..])
            : p.RegistryKey.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? (Hive.User, p.RegistryKey[5..])
            : (Hive.Machine, "");
        if (path.Length == 0) return false;
        try
        {
            using var key = registry.Open(hive, path, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every folder the uninstall entries (also the components and updates the list hides) and the services point into:
    /// a folder that holds one of them still belongs to something installed (<see cref="ProgramLeftovers"/>).
    /// </summary>
    /// <param name="user">Expands the entries of the signed-in user's own uninstall list (<see cref="Platform.PathExpander.ForUser(string?, IRegistryRoots?)"/>); null leaves their user folder variables as they are.</param>
    public static IReadOnlyList<string> ReferencedPaths(IRegistryRoots registry, PathExpander? user = null)
    {
        var paths = new List<string>();
        void Add(string? value, bool command, PathExpander expander)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var path = command ? CommandLine.ImagePath(value, expander.Expand) : ProgramLeftovers.CleanPath(value, expander);
            if (path is { Length: > 0 }) paths.Add(path);
        }
        // Each entry on its own: one only the system can read leaves out that entry, not the rest of the key.
        foreach (var (hive, path) in new[] { (Hive.Machine, Uninstall), (Hive.Machine, Uninstall32), (Hive.User, Uninstall) })
        {
            var expander = hive == Hive.User ? user ?? PathExpander.UnknownUser : PathExpander.Machine;
            ProgramOrphans.ReadEach(registry, hive, path, (key, sub) =>
            {
                using var app = key.OpenSubKey(sub);
                if (app is null) return;
                foreach (var (value, command) in new[] { ("InstallLocation", false), ("UninstallString", true), ("QuietUninstallString", true), ("DisplayIcon", false) })
                    Add(app.GetValue(value, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string, command, expander);
            });
        }
        ProgramOrphans.ReadEach(registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Services", (key, sub) =>
        {
            using var service = key.OpenSubKey(sub);
            Add(service?.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string, command: true, PathExpander.Machine);
        });
        return paths;
    }

    /// <summary>
    /// The uninstall without questions, or null when the program has none: Windows Installer gets /qn /norestart (a
    /// needed restart is left to the user), other programs only their own QuietUninstallString, under the same rules for
    /// running elevated as <see cref="Command"/>.
    /// </summary>
    public static UninstallCommand? QuietCommand(DesktopProgram p, Func<string, bool>? isAdminOnly = null, Func<string, bool>? exists = null, PathExpander? user = null)
    {
        if (Command(p, isAdminOnly, exists, user) is { } normal && IsWindowsInstaller(normal)) return Quiet(normal);
        if (string.IsNullOrWhiteSpace(p.QuietUninstallString)) return null;
        var quiet = Command(p with { UninstallString = p.QuietUninstallString, ProductCode = null }, isAdminOnly, exists, user);
        return quiet is not null && IsWindowsInstaller(quiet) ? Quiet(quiet) : quiet;
    }

    private static bool IsWindowsInstaller(UninstallCommand c) =>
        string.Equals(c.File, ProcessHardening.ResolveSystemTool("msiexec.exe"), StringComparison.OrdinalIgnoreCase);

    private static UninstallCommand Quiet(UninstallCommand msi) => msi with { Arguments = msi.Arguments + " /qn /norestart" };

    /// <summary>Exit codes of a finished uninstall: 3010 and 1641 are Windows Installer's "done, restart needed".</summary>
    public static bool Succeeded(int code) => code is 0 or 3010 or 1641;

    /// <summary>
    /// Null when the program has no usable uninstall command. Windows Installer entries always use System32's msiexec
    /// with /x; a per-user entry (HKCU, which any program of the user can write) runs it as the signed-in user, so a
    /// planted entry cannot make an elevated msiexec remove a program installed for the whole PC. Other uninstallers run
    /// elevated only when the program and, for rundll32 lines, its DLL are in folders only administrators can change.
    /// </summary>
    /// <param name="user">
    /// The signed-in user's expander (<see cref="PathExpander.ForUser(string?, IRegistryRoots?)"/>) for a per-user entry,
    /// which runs as that user; null leaves its user folder variables as they are.
    /// </param>
    public static UninstallCommand? Command(DesktopProgram p, Func<string, bool>? isAdminOnly = null, Func<string, bool>? exists = null, PathExpander? user = null)
    {
        isAdminOnly ??= TrustedPath.IsAdminOnlyWritable;
        var msiMode = p.PerUser ? UninstallMode.AsUser : UninstallMode.WindowsInstaller;
        var msiexec = ProcessHardening.ResolveSystemTool("msiexec.exe");
        if (p.ProductCode is { } productCode) return new UninstallCommand(msiMode, msiexec, $"/x {productCode}");
        if (string.IsNullOrWhiteSpace(p.UninstallString)) return null;
        // A machine-wide entry is expanded with the machine's variables, not with this process's (the signed-in user can
        // set %ProgramFiles% or %LOCALAPPDATA% for their account in HKCU\Environment). A per-user entry runs as the
        // signed-in user, so it is expanded as that user sees it, not as the elevated account would.
        var expanded = (p.PerUser ? user ?? PathExpander.UnknownUser : PathExpander.Machine).Expand(p.UninstallString.Trim());
        if (CommandLine.Program(expanded, s => s, exists) is not var (file, args)) return null;
        // "MsiExec.exe /I{GUID}" or "/X{GUID}" without the WindowsInstaller flag: always the remove action of System32's msiexec.
        if (Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var start = expanded.IndexOf('{');
            var end = start >= 0 ? expanded.IndexOf('}', start) : -1;
            return start >= 0 && end > start && Guid.TryParse(expanded[start..(end + 1)], out var g)
                ? new UninstallCommand(msiMode, msiexec, $"/x {g.ToString("B").ToUpperInvariant()}")
                : null;
        }
        // For a rundll32 line the DLL is the code that runs: it must be admin-only too. A script host or launcher (cmd /c
        // uninstall.cmd, wscript x.vbs) runs the files it is given: each of them must be admin-only, and inline code runs
        // as the user, or a script a vendor left writable would run elevated after any program of the user changed it.
        var image = CommandLine.ImagePath(expanded, s => s, exists);
        var elevated = !p.PerUser && isAdminOnly(file)
            && (image is null || string.Equals(image, file, StringComparison.OrdinalIgnoreCase) || isAdminOnly(image))
            && !CommandLine.RunsUntrustedCode(expanded, isAdminOnly, s => s, exists, interpreters: true)
            // cmd.exe would read operators in a batch file's arguments as more commands.
            && (!ProcessHardening.IsBatchFile(file) || ProcessHardening.SafeForCmd(args));
        return new UninstallCommand(elevated ? UninstallMode.Elevated : UninstallMode.AsUser, file, args);
    }
}

/// <summary>Runs an uninstall command with the uninstaller's own window, so the user answers its questions.</summary>
public static class ProgramUninstaller
{
    /// <summary>Exit code for elevated runs; null when the uninstaller was started as the signed-in user (not waited for).</summary>
    public static async Task<int?> RunAsync(UninstallCommand command, ElevationInfo? elevation, CancellationToken ct = default)
    {
        Logging.Log.Info("uninstall", $"{command.Mode}: {command.File} {command.Arguments}");
        if (command.Mode == UninstallMode.AsUser)
        {
            if (DeElevatedLauncher.Open(command.File, command.Arguments, elevation) == DeElevatedLauncher.Path.Failed)
                throw new InvalidOperationException("The uninstaller could not be started as the signed-in user.");
            return null;
        }
        using var p = ProcessLauncher.Start(ProcessLauncher.Info(command.File, command.Arguments, hidden: false));
        await p.WaitForExitAsync(ct);
        Logging.Log.Info("uninstall", $"exit {p.ExitCode}");
        return p.ExitCode;
    }
}
