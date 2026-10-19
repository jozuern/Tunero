using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Logging;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Startup;

public enum StartupKind
{
    RunKey, StartupFolder, LogonTask, Service, Driver, ShellExtension, Winlogon, ImageHijack, AppInit, PolicyRun,

    // Listed only (Autoruns-style locations): changed by the program that owns them or by hand.
    RunOnce, ActiveSetup, LoadValue, BootExecute, KnownDll, WinsockProvider, PrintMonitor, LsaPackage, NetworkProvider, Codec, WmiConsumer,
    CommandAutoRun, ExplorerAddOn, FileTypeHijack, SilentProcessExit, CredentialProvider, AppCertDll,
}

/// <summary>
/// One autostart entry. <see cref="Enabled"/> is null for entries that cannot be switched here (drivers, Winlogon,
/// AppInit, policy Run entries): those are listed so you can see them, and the page explains how to handle them.
/// </summary>
public sealed record StartupEntry(
    StartupKind Kind,
    string Name,
    string? Command,
    string? ImagePath,
    string Location,
    Hive Hive,
    bool? Enabled,
    string Key)
{
    /// <summary>Registry or file details the toggle needs (approved subkey, task path, CLSID, service name).</summary>
    public string? Target { get; init; }

    /// <summary>A shell extension blocked in the machine's list and in the user's own: switching it on clears both.</summary>
    public bool BlockedForUserToo { get; init; }

    /// <summary>Non-default Winlogon values and IFEO debuggers deserve attention (often malware or leftovers).</summary>
    public bool Suspicious { get; init; }

    /// <summary>A task that runs at sign-in or at boot; false for a task on another trigger (a daily updater).</summary>
    public bool StartsWithWindows { get; init; } = true;

    /// <summary>
    /// Runs PowerShell, cmd, mshta or a similar script host: Microsoft-signed, but the command decides what runs, so
    /// such entries are never hidden as "Microsoft". Variables are expanded with the machine's values only: this app
    /// inherits the user's own variables (HKCU\Environment), which a program running as the user can point at
    /// System32 for this check and somewhere else at the next sign-in.
    /// </summary>
    public bool RunsScriptHost => CommandLine.RunsUnverifiedScript(Command, Platform.MachineEnvironment.Expand);
}

/// <summary>Reads every autostart location the app knows (Autoruns-style), through the swappable registry and task interfaces.</summary>
public sealed partial class StartupScanner(IRegistryRoots registry, ITaskScheduler tasks, string? profilePath)
{
    /// <summary>Entries that run when the user signs in are expanded with that user's profile folders, not the app's.</summary>
    private readonly Func<string, string> _userExpand = Platform.MachineEnvironment.ForUser(profilePath);

    private const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string BlockedShellExt = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    public const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public IReadOnlyList<StartupEntry> ScanAll(bool includeServicesAndDrivers = true, bool includeWmi = true)
    {
        var list = new List<StartupEntry>();
        void Try(string part, Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Log.Warn("startup", $"{part}: {ex.Message}");
            }
        }
        Try("run", () => list.AddRange(RunKeys()));
        Try("folders", () => list.AddRange(StartupFolders()));
        Try("tasks", () => list.AddRange(ScheduledTasks()));
        Try("shellext", () => list.AddRange(ShellExtensions()));
        Try("winlogon", () => list.AddRange(WinlogonEntries()));
        Try("ifeo", () => list.AddRange(ImageHijacks()));
        Try("appinit", () => list.AddRange(AppInit()));
        if (includeServicesAndDrivers) Try("services", () => list.AddRange(ServicesAndDrivers()));
        Try("runonce", () => list.AddRange(RunOnceEntries()));
        Try("activesetup", () => list.AddRange(ActiveSetup()));
        Try("load", () => list.AddRange(LoadValues()));
        Try("bootexecute", () => list.AddRange(BootExecute()));
        Try("knowndlls", () => list.AddRange(KnownDlls()));
        Try("winsock", () => list.AddRange(WinsockProviders()));
        Try("printmonitors", () => list.AddRange(PrintMonitors()));
        Try("lsa", () => list.AddRange(LsaPackages()));
        Try("netproviders", () => list.AddRange(NetworkProviders()));
        Try("codecs", () => list.AddRange(Codecs()));
        Try("cmdautorun", () => list.AddRange(CommandAutoRuns()));
        Try("exploreraddons", () => list.AddRange(ExplorerAddOns()));
        Try("usershellext", () => list.AddRange(UserShellExtensions()));
        Try("filetypes", () => list.AddRange(FileTypeHijacks()));
        Try("silentexit", () => list.AddRange(SilentProcessExits()));
        Try("credproviders", () => list.AddRange(CredentialProviders()));
        Try("appcert", () => list.AddRange(AppCertDlls()));
        Try("directshow", () => list.AddRange(DirectShowFilters()));
        if (includeWmi) Try("wmi", () => list.AddRange(WmiConsumers()));
        return list;
    }

    // ---------------- Run keys ----------------

    public IEnumerable<StartupEntry> RunKeys()
    {
        foreach (var (hive, path, approvedSub, label) in new[]
                 {
                     (Hive.Machine, Run, "Run", @"HKLM\" + Run),
                     (Hive.Machine, Run32, "Run32", @"HKLM\" + Run32),
                     (Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
                 })
        {
            using var key = Open(hive, path);
            if (key is null) continue;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                var command = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                var enabled = ApprovedState(hive, approvedSub, name);
                yield return new StartupEntry(StartupKind.RunKey, name, command, CommandLine.ImagePath(command, _userExpand), label, hive, enabled, $"run:{hive}:{approvedSub}:{name}")
                {
                    Target = $@"{Approved}\{approvedSub}",
                };
            }
        }
        // Policy Run entries (Group Policy "Run these programs at user logon"): listed, changed only through the policy.
        foreach (var hive in new[] { Hive.Machine, Hive.User })
        {
            var path = (hive == Hive.Machine ? "SOFTWARE" : "Software") + @"\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
            using var key = Open(hive, path);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                var command = key!.GetValue(name)?.ToString();
                yield return new StartupEntry(StartupKind.PolicyRun, name, command, CommandLine.ImagePath(command, _userExpand), $@"{(hive == Hive.Machine ? "HKLM" : "HKCU")}\{path}", hive, null, $"policyrun:{hive}:{name}");
            }
        }
    }

    /// <summary>StartupApproved data: missing = enabled; first byte odd (0x03, 0x01, 0x07) = disabled (as Task Manager writes it).</summary>
    public bool ApprovedState(Hive hive, string sub, string name)
    {
        var v = RegistryValue.Read(registry, hive, $@"{Approved}\{sub}", name);
        return StartupApprovedAction.IsEnabled(v);
    }

    // ---------------- Startup folders ----------------

    /// <summary>The Startup folder for all users; null = Windows' own (tests point it at a temp folder).</summary>
    public string? CommonStartupFolder { get; init; }

    public IEnumerable<StartupEntry> StartupFolders()
    {
        var common = CommonStartupFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\StartUp");
        var folders = new List<(string Dir, Hive Hive)> { (common, Hive.Machine) };
        if (profilePath is not null) folders.Add((Path.Combine(profilePath, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"), Hive.User));
        foreach (var (dir, hive) in folders)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
                // A shortcut with arguments gives "\"target\" args": the image is only the target.
                yield return new StartupEntry(StartupKind.StartupFolder, Path.GetFileNameWithoutExtension(file), target, CommandLine.ImagePath(target, _userExpand), dir, hive,
                    ApprovedState(hive, "StartupFolder", name), $"folder:{hive}:{name}")
                {
                    Target = $@"{Approved}\StartupFolder|{name}",
                };
            }
        }
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var (target, args) = Platform.ShellLinkFile.Read(lnk);
            return string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------- Scheduled tasks ----------------

    /// <summary>Tasks that start with Windows or at sign-in (the startup check counts these).</summary>
    public IEnumerable<StartupEntry> LogonTasks() => TaskEntries(t => t.AtLogon || t.AtBoot);

    /// <summary>
    /// What the Startup page lists, as Autoruns does: tasks at start or sign-in, and every other task that is not one of
    /// Windows' own (an updater that runs daily). Windows' own tasks are on Services &amp; tasks.
    /// </summary>
    public IEnumerable<StartupEntry> ScheduledTasks() => TaskEntries(t => t.AtLogon || t.AtBoot || !IsWindowsTask(t.Path));

    /// <summary>Tunero's own task (the re-apply task): turned on and off in Settings, so the Startup page does not switch or delete it.</summary>
    public static bool IsOwnTask(string? path) => path?.StartsWith(Actions.OwnTaskAction.Folder, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Windows' own tasks (\Microsoft\Windows\...): listed, but switched only on Services &amp; tasks, where a catalog explains them.</summary>
    public static bool IsWindowsTask(string? path) => path?.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// One entry per program a task runs: a second action is checked and shown on its own, so it cannot hide behind a
    /// signed first one. A task that runs a COM handler shows the DLL registered for it, so Windows' own handler tasks
    /// are recognized as Microsoft's instead of "no publisher".
    /// </summary>
    private IEnumerable<StartupEntry> TaskEntries(Func<ScheduledTaskInfo, bool> include)
    {
        foreach (var t in tasks.List().Where(include))
        {
            var programs = new List<(string? Command, string? Arguments)> { (t.Command, t.Arguments) };
            programs.AddRange((t.OtherActions ?? []).Select(a => ((string?)a.Command, a.Arguments)));
            for (var i = 0; i < programs.Count; i++)
            {
                var (program, arguments) = programs[i];
                // A task's program path is often stored with quotes already: quote it only once.
                var command = program is null ? null : $"\"{program.Trim().Trim('"')}\" {arguments}".Trim();
                var image = command is not null ? CommandLine.ImagePath(command, _userExpand) : ComServer(t.ComHandler);
                var name = t.Path.TrimStart('\\') + (i == 0 ? "" : $" ({i + 1})");
                yield return new StartupEntry(StartupKind.LogonTask, name, command ?? image, image, t.Path, Hive.Machine, t.Enabled,
                    i == 0 ? $"task:{t.Path}" : $"task:{t.Path}#{i + 1}")
                {
                    Target = t.Path,
                    StartsWithWindows = t.AtLogon || t.AtBoot,
                };
            }
        }
    }

    /// <summary>
    /// The DLL registered for a COM class (InprocServer32), expanded with the machine's variables; null when unknown. A
    /// class a 32-bit program loads (<paramref name="wow64"/>, registered under WOW6432Node) is looked up there first.
    /// </summary>
    private string? ComServer(string? clsid, bool wow64 = false)
    {
        if (clsid is null || !Guid.TryParse(clsid, out var g)) return null;
        var id = g.ToString("B").ToUpperInvariant();
        string? dll = null;
        foreach (var classes in wow64 ? new[] { @"SOFTWARE\Classes\WOW6432Node\CLSID", @"SOFTWARE\Classes\CLSID" } : [@"SOFTWARE\Classes\CLSID"])
        {
            using var server = Open(Hive.Machine, $@"{classes}\{id}\InprocServer32");
            dll = server?.GetValue("", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            if (!string.IsNullOrWhiteSpace(dll)) break;
        }
        // A file path, not a command line: spaces in "C:\Program Files\..." are part of it.
        return string.IsNullOrWhiteSpace(dll) ? null : Platform.MachineEnvironment.Expand(dll.Trim().Trim('"'));
    }

    // ---------------- Services and drivers ----------------

    public IEnumerable<StartupEntry> ServicesAndDrivers()
    {
        using var root = Open(Hive.Machine, ServicesKey);
        foreach (var name in root?.GetSubKeyNames() ?? [])
            if (Service(root!, name, manualToo: false) is { } entry) yield return entry;
    }

    /// <summary>
    /// One service, also when it starts on demand: a service this page set to Manual stays listed (switched off), so it
    /// can be switched back here. Null for a driver, a missing service or one that is neither automatic nor manual.
    /// </summary>
    public StartupEntry? ManualService(string name)
    {
        using var root = Open(Hive.Machine, ServicesKey);
        return root is null ? null : Service(root, name, manualToo: true) is { Kind: StartupKind.Service } entry ? entry : null;
    }

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private static StartupEntry? Service(Microsoft.Win32.RegistryKey root, string name, bool manualToo)
    {
        using var key = root.OpenSubKey(name);
        if (key?.GetValue("Start") is not int start || key.GetValue("Type") is not int type) return null;
        var image = key.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        if (image is null) return null;
        var isDriver = (type & 0x3) != 0;
        var isService = (type & 0x30) != 0;
        if (!isDriver && !isService) return null;
        // Drivers: boot/system/automatic. Services: automatic only (manual services start on demand).
        if (isDriver ? start > 2 : !(start == 2 || (manualToo && start == 3))) return null;
        // Started by the service manager: expanded with the machine's variables, not the signed-in user's.
        string? file = CommandLine.ImagePath(image, Platform.MachineEnvironment.Expand);
        if (isService && file is not null && Path.GetFileName(file).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            using var p = key.OpenSubKey("Parameters");
            if (p?.GetValue("ServiceDll", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is { } dll) file = CommandLine.ImagePath(dll, Platform.MachineEnvironment.Expand);
        }
        var display = ResolveIndirect(key.GetValue("DisplayName")?.ToString()) ?? name;
        return new StartupEntry(isDriver ? StartupKind.Driver : StartupKind.Service, display, image, file, $@"HKLM\{ServicesKey}\{name}", Hive.Machine,
            isDriver ? null : start == 2, $"{(isDriver ? "driver" : "service")}:{name}")
        {
            Target = name,
        };
    }

    // ---------------- Explorer shell extensions ----------------

    private static readonly string[] HandlerRoots =
    [
        @"*\shellex\ContextMenuHandlers", @"Directory\shellex\ContextMenuHandlers", @"Directory\Background\shellex\ContextMenuHandlers",
        @"Folder\shellex\ContextMenuHandlers", @"Drive\shellex\ContextMenuHandlers", @"AllFilesystemObjects\shellex\ContextMenuHandlers",
    ];

    public IEnumerable<StartupEntry> ShellExtensions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var blocked = Open(Hive.Machine, BlockedShellExt);
        var blockedSet = (blocked?.GetValueNames() ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Explorer also honors the user's own list: an extension blocked only there is off, and its switch clears it there.
        using var blockedByUser = Open(Hive.User, BlockedShellExt);
        var userBlockedSet = (blockedByUser?.GetValueNames() ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var handlerRoot in HandlerRoots)
        {
            using var key = Open(Hive.Machine, $@"SOFTWARE\Classes\{handlerRoot}");
            foreach (var handler in key?.GetSubKeyNames() ?? [])
            {
                using var h = key!.OpenSubKey(handler);
                var clsid = h?.GetValue("")?.ToString();
                if (string.IsNullOrEmpty(clsid) && Guid.TryParse(handler, out _)) clsid = handler;
                if (clsid is null || !Guid.TryParse(clsid, out var g)) continue;
                var id = g.ToString("B").ToUpperInvariant();
                if (!seen.Add(id)) continue;
                using var server = Open(Hive.Machine, $@"SOFTWARE\Classes\CLSID\{id}\InprocServer32");
                var dll = server?.GetValue("", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                using var cls = Open(Hive.Machine, $@"SOFTWARE\Classes\CLSID\{id}");
                var name = cls?.GetValue("")?.ToString() is { Length: > 0 } n ? n : handler;
                var blockedForUserOnly = !blockedSet.Contains(id) && userBlockedSet.Contains(id);
                yield return new StartupEntry(StartupKind.ShellExtension, name, dll, CommandLine.ImagePath(dll), $@"HKCR\{handlerRoot}\{handler}",
                    blockedForUserOnly ? Hive.User : Hive.Machine, !blockedSet.Contains(id) && !blockedForUserOnly, $"shellext:{id}")
                {
                    Target = id,
                    BlockedForUserToo = blockedSet.Contains(id) && userBlockedSet.Contains(id),
                };
            }
        }
    }

    // ---------------- Winlogon, IFEO, AppInit ----------------

    public static readonly IReadOnlyDictionary<string, string> WinlogonDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Shell"] = "explorer.exe",
        ["Userinit"] = @"C:\Windows\system32\userinit.exe,",
    };

    public IEnumerable<StartupEntry> WinlogonEntries()
    {
        using var key = Open(Hive.Machine, Winlogon);
        if (key is null) yield break;
        foreach (var (name, expected) in WinlogonDefaults)
        {
            var value = key.GetValue(name)?.ToString();
            if (value is null) continue;
            var normal = string.Equals(value.Trim().TrimEnd(','), expected.TrimEnd(','), StringComparison.OrdinalIgnoreCase)
                         || (name == "Userinit" && string.Equals(value.Trim().TrimEnd(','), Path.Combine(Environment.SystemDirectory, "userinit.exe"), StringComparison.OrdinalIgnoreCase));
            yield return new StartupEntry(StartupKind.Winlogon, name, value, CommandLine.ImagePath(value.Split(',')[0], Platform.MachineEnvironment.Expand), $@"HKLM\{Winlogon}", Hive.Machine, null, $"winlogon:{name}")
            {
                Suspicious = !normal,
            };
        }

        // Values Windows does not set: a program started with the desktop (Taskman), at sign-in (AppSetup), an old
        // sign-in DLL (GinaDLL) or extra programs started with the session (System).
        foreach (var name in new[] { "Taskman", "AppSetup", "GinaDLL", "System" })
            if (key.GetValue(name)?.ToString() is { Length: > 0 } extra)
                yield return new StartupEntry(StartupKind.Winlogon, name, extra, CommandLine.ImagePath(extra.Split(',')[0], Platform.MachineEnvironment.Expand), $@"HKLM\{Winlogon}",
                    Hive.Machine, null, $"winlogon:{name}")
                {
                    Suspicious = true,
                };

        // A Shell value in the user's own Winlogon key replaces Explorer for that user and needs no administrator
        // rights to set; Windows does not create it.
        using var user = Open(Hive.User, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon");
        if (user?.GetValue("Shell")?.ToString() is { Length: > 0 } shell)
            yield return new StartupEntry(StartupKind.Winlogon, "Shell", shell, CommandLine.ImagePath(shell.Split(',')[0]), @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
                Hive.User, null, "winlogon:user:Shell")
            {
                Suspicious = true,
            };
    }

    public const string Ifeo32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>A Debugger value starts another program instead of the one named; 32-bit programs read the WOW6432Node copy.</summary>
    public IEnumerable<StartupEntry> ImageHijacks()
    {
        foreach (var (path, prefix) in new[] { (Ifeo, "ifeo"), (Ifeo32, "ifeo32") })
        {
            using var key = Open(Hive.Machine, path);
            foreach (var exe in key?.GetSubKeyNames() ?? [])
            {
                using var sub = key!.OpenSubKey(exe);
                if (sub?.GetValue("Debugger")?.ToString() is not { Length: > 0 } debugger) continue;
                yield return new StartupEntry(StartupKind.ImageHijack, exe, debugger, CommandLine.ImagePath(debugger), $@"HKLM\{path}\{exe}", Hive.Machine, true, $"{prefix}:{exe}")
                {
                    Target = $@"{path}\{exe}",
                    Suspicious = true,
                };
            }
        }
    }

    public IEnumerable<StartupEntry> AppInit()
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows" })
        {
            using var key = Open(Hive.Machine, path);
            if (key?.GetValue("AppInit_DLLs")?.ToString() is not { Length: > 0 } dlls) continue;
            var load = key.GetValue("LoadAppInit_DLLs") is int l && l != 0;
            foreach (var dll in dlls.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                yield return new StartupEntry(StartupKind.AppInit, Path.GetFileName(dll), dll, CommandLine.ImagePath(dll, Platform.MachineEnvironment.Expand), $@"HKLM\{path}\AppInit_DLLs", Hive.Machine, null, $"appinit:{dll}")
                {
                    Suspicious = load,
                };
        }
    }

    private Microsoft.Win32.RegistryKey? Open(Hive hive, string path)
    {
        try
        {
            return registry.Open(hive, path, writable: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"@%SystemRoot%\system32\x.dll,-101" display names resolved through SHLoadIndirectString.</summary>
    public static string? ResolveIndirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('@')) return value;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(value, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : value;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int outputSize, IntPtr reserved);
}

/// <summary>
/// Enables or disables a Run key or Startup folder entry the way Task Manager does: through the StartupApproved value
/// (12 bytes; first byte 0x02 = enabled, 0x03 = disabled, followed by the time it was disabled). The entry itself is
/// never deleted, so the program's own settings and updates keep working. Undo writes the original bytes back.
/// </summary>
public sealed class StartupApprovedAction : TweakAction
{
    public Hive Hive { get; init; }

    /// <summary>Full path of the StartupApproved subkey (Run, Run32 or StartupFolder).</summary>
    public string Path { get; init; } = "";

    public string Name { get; init; } = "";
    public bool Enabled { get; init; }

    /// <summary>
    /// Disabled when the first byte of the binary value has bit 0 set. Anything else (missing, another type, text that
    /// is not hex) counts as enabled, as Task Manager treats it: the key can be written without administrator rights,
    /// and an exception here would end the scan of the Run keys and hide every later entry.
    /// </summary>
    public static bool IsEnabled(StoredValue raw) =>
        !raw.Existed || raw.Kind != "binary" || raw.Data is not { Length: >= 2 } hex
        || !byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var first) || (first & 0x1) == 0;

    public override string TargetKey => $"startupapproved:{Hive}:{Path}\\{Name}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Verbatim($"{c.Registry.DisplayRoot(Hive)}\\{Path}\\{Name}");

    // Data = "Enabled"/"Disabled" (shown and compared); Kind carries the raw bytes for restore ("raw:" + hex, or "raw:" when missing).
    private static StoredValue Semantic(StoredValue raw) =>
        new(true, "raw:" + (raw.Existed ? raw.Data : ""), IsEnabled(raw) ? "Enabled" : "Disabled");

    public override StoredValue Desired(ActionContext c) => new(true, "startupapproved", Enabled ? "Enabled" : "Disabled");

    public override StoredValue? Read(ActionContext c) => Semantic(RegistryValue.Read(c.Registry, Hive, Path, Name));

    public override void Apply(ActionContext c) => RegistryValue.Write(c.Registry, Hive, Path, Name, "binary", Bytes(Enabled, DateTime.UtcNow));

    public override void Restore(ActionContext c, StoredValue original)
    {
        var raw = original.Kind is { } k && k.StartsWith("raw:", StringComparison.Ordinal) ? k[4..] : null;
        if (string.IsNullOrEmpty(raw)) RegistryValue.Delete(c.Registry, Hive, Path, Name);
        else RegistryValue.Write(c.Registry, Hive, Path, Name, "binary", raw);
    }

    public static string Bytes(bool enabled, DateTime utc)
    {
        var b = new byte[12];
        b[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(b, 4);
        return Convert.ToHexString(b);
    }
}

/// <summary>Builds the engine tweaks behind the startup page's switches (backup, undo and the change log come for free).</summary>
public static class StartupTweaks
{

    /// <summary>Null when the entry cannot be switched here.</summary>
    public static TweakDefinition? Set(StartupEntry e, bool enabled)
    {
        TweakAction? action = e.Kind switch
        {
            StartupKind.RunKey => new StartupApprovedAction { Hive = e.Hive, Path = e.Target!, Name = e.Name, Enabled = enabled },
            StartupKind.StartupFolder => new StartupApprovedAction
            {
                Hive = e.Hive, Path = e.Target!.Split('|')[0], Name = e.Target.Split('|')[1], Enabled = enabled,
            },
            // Windows' own tasks are switched on Services & tasks, where the catalog says which are safe to turn off (turning
            // off TextServicesFramework\MsCtfMonitor, for example, stops typing in Start search).
            StartupKind.LogonTask when StartupScanner.IsWindowsTask(e.Target) || StartupScanner.IsOwnTask(e.Target) => null,
            StartupKind.LogonTask => new ScheduledTaskAction { Path = e.Target!, Enabled = enabled },
            // Blocked shell extensions are listed by CLSID under Shell Extensions\Blocked (read by Explorer at start): the
            // machine's list, or the user's own when only that one blocks the extension (the entry's hive says which).
            StartupKind.ShellExtension => new RegistryAction
            {
                Hive = e.Hive,
                Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
                Name = e.Target!,
                Kind = "string",
                Value = JsonSerializer.SerializeToElement(""),
                Delete = enabled,
            },
            // Services are set to Manual, never Disabled, so anything that needs them can still start them.
            StartupKind.Service => new ServiceAction { Name = e.Target!, StartType = enabled ? ServiceStart.Automatic : ServiceStart.Manual },
            StartupKind.ImageHijack when !enabled => new RegistryAction { Hive = Hive.Machine, Path = e.Target!, Name = "Debugger", Delete = true },
            _ => null,
        };
        if (action is null) return null;
        var expert = e.Kind is StartupKind.ImageHijack or StartupKind.Service;
        return new TweakDefinition
        {
            Id = $"startup.{(enabled ? "on" : "off")}.{e.Kind.ToString().ToLowerInvariant()}.{TweakIds.Slug(e.Key)}",
            Docs = $"startup.{e.Kind.ToString().ToLowerInvariant()}",
            Subject = e.Name,
            Category = "Startup",
            Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Risk = expert ? Risk.Expert : Risk.Safe,
            Hidden = true,
            SignOut = e.Kind is StartupKind.ShellExtension,
            // Explorer honors both lists: an extension blocked in both stays off until it is gone from both.
            Actions = enabled && e.BlockedForUserToo && action is RegistryAction blocked
                ? [action, new RegistryAction { Hive = Hive.User, Path = blocked.Path, Name = blocked.Name, Kind = blocked.Kind, Value = blocked.Value, Delete = true }]
                : [action],
            Sources = ["https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns"],
        };
    }

    /// <summary>
    /// Deletes an autostart entry the way Autoruns does, but through the engine: the entry is backed up first and the
    /// Changes page puts it back. A single value in a Run, RunOnce, policy Run or cmd.exe AutoRun key, a file in a Startup
    /// folder (its bytes are kept) or a scheduled task of another program (its XML is kept). Null for everything else
    /// (RunOnceEx entries, services, Windows' own tasks and locations stay as they are).
    /// </summary>
    public static TweakDefinition? Delete(StartupEntry e)
    {
        var (path, name) = e.Kind switch
        {
            StartupKind.RunKey or StartupKind.PolicyRun => (KeyPath(e.Location), e.Name),
            StartupKind.RunOnce when e.Key.StartsWith("runonce:", StringComparison.Ordinal) => (KeyPath(e.Location), e.Name),
            // The location ends with the value name (...\Command Processor\AutoRun).
            StartupKind.CommandAutoRun => (KeyPath(e.Location[..e.Location.LastIndexOf('\\')]), "AutoRun"),
            _ => (null, null),
        };
        TweakAction? action = path is not null && name is not null ? new RegistryAction { Hive = e.Hive, Path = path, Name = name, Delete = true }
            : e.Kind switch
            {
                // Target is "<StartupApproved key>|<file name>", Location the folder.
                StartupKind.StartupFolder when e.Target?.Split('|') is [_, var file] && StartupFileDeleteAction.IsStartupFile(System.IO.Path.Combine(e.Location, file))
                    => new StartupFileDeleteAction { Path = System.IO.Path.Combine(e.Location, file) },
                StartupKind.LogonTask when e.Target is { Length: > 0 } task && !StartupScanner.IsWindowsTask(task) && !StartupScanner.IsOwnTask(task) => new ScheduledTaskDeleteAction { Path = task },
                _ => null,
            };
        if (action is null) return null;
        return new TweakDefinition
        {
            Id = $"startup.delete.{e.Kind.ToString().ToLowerInvariant()}.{TweakIds.Slug(e.Key)}",
            Docs = "startup.delete",
            Subject = e.Name,
            Category = "Startup",
            Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Risk = Risk.Safe,
            Hidden = true,
            Actions = [action],
            Sources = ["https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns"],
        };
    }

    /// <summary>
    /// A deletion from the Startup page. When the entry is there again, its program put it back (often changed: a new
    /// path after an update): that is not Windows resetting a change, so it is not re-applied on its own, and deleting it
    /// again keeps the new content for undo (<see cref="TweakEngine"/>).
    /// </summary>
    public static bool IsDelete(TweakDefinition t) => t.Docs == "startup.delete";

    /// <summary>"HKLM\SOFTWARE\..." or "HKCU\Software\..." without the root; null for anything else.</summary>
    private static string? KeyPath(string location) =>
        location.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) || location.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? location[5..] : null;

    public static IReadOnlyList<string> DocIds =>
        new[] { StartupKind.RunKey, StartupKind.StartupFolder, StartupKind.LogonTask, StartupKind.ShellExtension, StartupKind.Service, StartupKind.ImageHijack }
            .Select(k => $"startup.{k.ToString().ToLowerInvariant()}").Append("startup.delete").ToList();

    /// <summary>Third-party entries enabled at logon: what F16 counts.</summary>
    public static bool CountsForF16(StartupEntry e) => e.Enabled == true && e.Kind is StartupKind.RunKey or StartupKind.StartupFolder or StartupKind.LogonTask;
}
