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

    /// <summary>A task that runs when Windows starts but not at sign-in: the page names its trigger as Windows start.</summary>
    public bool AtBootOnly { get; init; }

    /// <summary>
    /// Runs PowerShell, cmd, mshta or a similar script host: Microsoft-signed, but the command decides what runs, so
    /// such entries are never hidden as "Microsoft". Variables are expanded with the machine's values only: this app
    /// inherits the user's own variables (HKCU\Environment), which a program running as the user can point at
    /// System32 for this check and somewhere else at the next sign-in.
    /// </summary>
    public bool RunsScriptHost => CommandLine.RunsUnverifiedScript(Command, Platform.PathExpander.Machine.Expand);
}

/// <summary>Reads every autostart location the app knows (Autoruns-style), through the swappable registry and task interfaces.</summary>
public sealed partial class StartupScanner(IRegistryRoots registry, ITaskScheduler tasks, string? profilePath)
{
    /// <summary>Entries that run when the user signs in are expanded with that user's folders and variables, not the app's.</summary>
    private readonly Func<string, string> _userExpand = Platform.PathExpander.ForUser(profilePath, registry).Expand;

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
                // Unexpanded, as the Run keys: this app's own variables are not the ones the user's sign-in expands it with.
                var command = key!.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
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

    /// <summary>The Startup folder for all users; null = the one Windows uses (tests point it at a temp folder).</summary>
    public string? CommonStartupFolder { get; init; }

    private const string UserShellFolders = @"Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>
    /// The Startup folders Explorer runs at sign-in: the machine's ("Common Startup") and the user's ("Startup") as the
    /// User Shell Folders values name them, which folder redirection or a program can point elsewhere (the user's value
    /// needs no administrator rights). Their default places when a value is missing or names no full path. A folder
    /// outside its default place and not on a network share (where folder redirection puts it) is marked for a look:
    /// Windows never moves it there itself.
    /// </summary>
    internal IReadOnlyList<(string Dir, Hive Hive, bool Moved)> StartupFolderPaths()
    {
        var folders = new List<(string, Hive, bool)>();
        if (CommonStartupFolder is { } given) folders.Add((given, Hive.Machine, false));
        else
        {
            var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\StartUp");
            var common = ShellFolder(Hive.Machine, @"SOFTWARE\" + UserShellFolders, "Common Startup", Platform.PathExpander.Machine.Expand) ?? usual;
            folders.Add((common, Hive.Machine, Moved(common, usual)));
        }
        var userUsual = profilePath is null ? null : Path.Combine(profilePath, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
        if ((ShellFolder(Hive.User, @"Software\" + UserShellFolders, "Startup", _userExpand) ?? userUsual) is { } user) folders.Add((user, Hive.User, Moved(user, userUsual)));
        return folders;

        static bool Moved(string dir, string? usual) =>
            !string.Equals(Path.TrimEndingDirectorySeparator(dir), usual is null ? null : Path.TrimEndingDirectorySeparator(usual), StringComparison.OrdinalIgnoreCase)
            && !dir.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>A User Shell Folders value, expanded; null when it is missing or does not name a full path.</summary>
    private string? ShellFolder(Hive hive, string path, string name, Func<string, string> expand)
    {
        using var key = Open(hive, path);
        if (key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is not { Length: > 0 } value) return null;
        var dir = expand(value.Trim().Trim('"'));
        return !dir.Contains('%') && !dir.Contains("..", StringComparison.Ordinal) && Path.IsPathFullyQualified(dir) ? dir : null;
    }

    public IEnumerable<StartupEntry> StartupFolders()
    {
        foreach (var (dir, hive, moved) in StartupFolderPaths())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                // Quoted: Explorer opens the file itself, so "...\Start Menu\..." must not be read the way CreateProcess
                // reads an unquoted command line, where a planted ...\Windows\Start.exe would be checked instead.
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : $"\"{file}\"";
                // A shortcut with arguments gives "\"target\" args": the image is only the target.
                yield return new StartupEntry(StartupKind.StartupFolder, Path.GetFileNameWithoutExtension(file), target, CommandLine.ImagePath(target, _userExpand), dir, hive,
                    ApprovedState(hive, "StartupFolder", name), $"folder:{hive}:{name}")
                {
                    Target = $@"{Approved}\StartupFolder|{name}",
                    Suspicious = moved,
                };
            }
        }
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var (target, args) = Platform.ShellLinkFile.Read(lnk);
            // The target is a path, not a command line: quoted, also without arguments.
            if (string.IsNullOrWhiteSpace(target)) return null;
            var quoted = $"\"{target.Trim().Trim('"')}\"";
            return string.IsNullOrEmpty(args) ? quoted : $"{quoted} {args}";
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
    /// Shell service objects Windows itself lists under the machine's ShellServiceObjectDelayLoad, by CLSID: WebCheck
    /// (the old Internet Explorer offline pages component) stays there on Windows 11 with no COM server registered.
    /// </summary>
    private static readonly HashSet<string> WindowsShellServiceObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "{E6FB5E20-DE35-11CF-9C87-00AA005127ED}",
    };

    /// <summary>
    /// One of Windows' own shell service objects (<see cref="WindowsShellServiceObjects"/>) in the machine's
    /// ShellServiceObjectDelayLoad with no COM server behind it: nothing loads from it. Matched by place and CLSID, not by
    /// the value's name, which any program could choose.
    /// </summary>
    public static bool IsWindowsShellServiceObjectWithoutServer(StartupEntry e) =>
        e.Kind == StartupKind.ExplorerAddOn && e.Hive == Hive.Machine && e.ImagePath is null
        && e.Location.EndsWith(@"\ShellServiceObjectDelayLoad", StringComparison.OrdinalIgnoreCase)
        && e.Command is { } clsid && WindowsShellServiceObjects.Contains(clsid);

    /// <summary>
    /// One entry per action a task runs (<see cref="TaskActions"/>): a second action is checked and shown on its own, so
    /// it cannot hide behind a signed first one.
    /// </summary>
    private IEnumerable<StartupEntry> TaskEntries(Func<ScheduledTaskInfo, bool> include)
    {
        foreach (var t in tasks.List().Where(include))
        {
            var actions = TaskActions(t);
            for (var i = 0; i < actions.Count; i++)
            {
                var (command, image, keySuffix) = actions[i];
                var name = t.Path.TrimStart('\\') + (i == 0 ? "" : $" ({i + 1})");
                yield return new StartupEntry(StartupKind.LogonTask, name, command, image, t.Path, Hive.Machine, t.Enabled, $"task:{t.Path}{keySuffix}")
                {
                    Target = t.Path,
                    StartsWithWindows = t.AtLogon || t.AtBoot,
                    AtBootOnly = t.AtBoot && !t.AtLogon,
                };
            }
        }
    }

    /// <summary>One action of a task: what the row shows as its command, the file it runs and the end of its entry's key.</summary>
    public sealed record TaskAction(string? Command, string? Image, string KeySuffix);

    /// <summary>
    /// Everything a task runs, as the Startup page and Services &amp; tasks check it: each program (expanded for the
    /// signed-in user), then each COM handler with the DLL registered for it, so Windows' own handler tasks are recognized
    /// as Microsoft's and a handler next to a program is not left out. A task without a program starts with its first
    /// handler; a task with neither gets one action without a file. Reads the registry for a COM handler: not for every
    /// row on each search. The machine's registration only: such a task often runs as another account, for which the
    /// signed-in user's classes do not count.
    /// </summary>
    public IReadOnlyList<TaskAction> TaskActions(ScheduledTaskInfo t)
    {
        var list = new List<TaskAction>();
        var handlers = new[] { t.ComHandler }.Concat(t.OtherComHandlers ?? []).OfType<string>().Where(c => c.Length > 0).ToList();
        if (TaskCommand(t.Command, t.Arguments) is { } first) list.Add(new TaskAction(first, CommandLine.ImagePath(first, _userExpand), ""));
        else if (handlers.Count > 0)
        {
            var dll = ComServer(handlers[0]);
            list.Add(new TaskAction(dll, dll, ""));
            handlers.RemoveAt(0);
        }
        else list.Add(new TaskAction(null, null, ""));
        var others = t.OtherActions ?? [];
        for (var i = 0; i < others.Count; i++)
        {
            var command = TaskCommand(others[i].Command, others[i].Arguments);
            list.Add(new TaskAction(command, CommandLine.ImagePath(command, _userExpand), $"#{i + 2}"));
        }
        for (var i = 0; i < handlers.Count; i++)
        {
            // Next to a program: the CLSID when no DLL is registered for it, so the row still says what it runs.
            var dll = ComServer(handlers[i]);
            list.Add(new TaskAction(dll ?? handlers[i], dll, i == 0 ? "#com" : $"#com{i + 1}"));
        }
        return list;
    }

    /// <summary>
    /// A task's program with its arguments, as both pages show it. The program path is often stored with quotes already:
    /// quoted only once. Null for a task without a program (a COM handler).
    /// </summary>
    public static string? TaskCommand(string? program, string? arguments) =>
        string.IsNullOrWhiteSpace(program) ? null : $"\"{program.Trim().Trim('"')}\" {arguments}".Trim();

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
        return CommandLine.FilePath(dll, Platform.PathExpander.Machine.Expand);
    }

    /// <summary>
    /// The DLL a program of the signed-in user (Explorer, a media player) loads for a COM class, as it is registered and
    /// as a file. The account's own registration (HKCU\Software\Classes, without administrator rights) wins over the
    /// machine's, as in HKEY_CLASSES_ROOT: a class registered there for a CLSID the machine also has replaces the
    /// machine's DLL for that user, which <paramref name="overridesMachine"/> says so the row is marked for a look.
    /// </summary>
    private (string? Registered, string? File) UserComServer(string id, bool wow64, out bool overridesMachine)
    {
        overridesMachine = false;
        using var own = Open(Hive.User, $@"Software\Classes\{(wow64 ? @"WOW6432Node\" : "")}CLSID\{id}\InprocServer32");
        if (own?.GetValue("", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is { } dll && !string.IsNullOrWhiteSpace(dll))
        {
            var file = CommandLine.FilePath(dll, _userExpand);
            overridesMachine = ComServer(id, wow64) is { } machine && !string.Equals(machine, file, StringComparison.OrdinalIgnoreCase);
            return (dll, file);
        }
        using var server = Open(Hive.Machine, $@"SOFTWARE\Classes\{(wow64 ? @"WOW6432Node\" : "")}CLSID\{id}\InprocServer32");
        var registered = server?.GetValue("", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        return string.IsNullOrWhiteSpace(registered) ? (null, ComServer(id, wow64)) : (registered, CommandLine.FilePath(registered, Platform.PathExpander.Machine.Expand));
    }

    /// <summary>A COM class's name as the signed-in user's programs see it: the account's own registration first; null when it has none.</summary>
    private string? ClassName(string id)
    {
        foreach (var (hive, classes) in new[] { (Hive.User, @"Software\Classes\CLSID"), (Hive.Machine, @"SOFTWARE\Classes\CLSID") })
        {
            using var cls = Open(hive, $@"{classes}\{id}");
            if (cls?.GetValue("")?.ToString() is { Length: > 0 } name) return name;
        }
        return null;
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
        string? file = CommandLine.ImagePath(image, Platform.PathExpander.Machine.Expand);
        if (isService && file is not null && Path.GetFileName(file).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            using var p = key.OpenSubKey("Parameters");
            if (p?.GetValue("ServiceDll", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is { } dll) file = CommandLine.FilePath(dll, Platform.PathExpander.Machine.Expand);
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
                // Explorer loads the class as the signed-in user sees it: that account's own registration wins.
                var (dll, file) = UserComServer(id, wow64: false, out var overridden);
                var name = ClassName(id) ?? handler;
                var blockedForUserOnly = !blockedSet.Contains(id) && userBlockedSet.Contains(id);
                yield return new StartupEntry(StartupKind.ShellExtension, name, dll, file, $@"HKCR\{handlerRoot}\{handler}",
                    blockedForUserOnly ? Hive.User : Hive.Machine, !blockedSet.Contains(id) && !blockedForUserOnly, $"shellext:{id}")
                {
                    Target = id,
                    BlockedForUserToo = blockedSet.Contains(id) && userBlockedSet.Contains(id),
                    Suspicious = overridden,
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
            yield return new StartupEntry(StartupKind.Winlogon, name, value, CommandLine.ImagePath(value.Split(',')[0], Platform.PathExpander.Machine.Expand), $@"HKLM\{Winlogon}", Hive.Machine, null, $"winlogon:{name}")
            {
                Suspicious = !normal,
            };
        }

        // Values Windows does not set: a program started with the desktop (Taskman), at sign-in (AppSetup), an old
        // sign-in DLL (GinaDLL) or extra programs started with the session (System).
        foreach (var name in new[] { "Taskman", "AppSetup", "GinaDLL", "System" })
            if (key.GetValue(name)?.ToString() is { Length: > 0 } extra)
                yield return new StartupEntry(StartupKind.Winlogon, name, extra, CommandLine.ImagePath(extra.Split(',')[0], Platform.PathExpander.Machine.Expand), $@"HKLM\{Winlogon}",
                    Hive.Machine, null, $"winlogon:{name}")
                {
                    Suspicious = true,
                };

        // A Shell value in the user's own Winlogon key replaces Explorer for that user and needs no administrator
        // rights to set; Windows does not create it.
        using var user = Open(Hive.User, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon");
        if (user?.GetValue("Shell", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is { Length: > 0 } shell)
            yield return new StartupEntry(StartupKind.Winlogon, "Shell", shell, CommandLine.ImagePath(shell.Split(',')[0], _userExpand), @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
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
                yield return new StartupEntry(StartupKind.AppInit, Path.GetFileName(dll), dll, CommandLine.ImagePath(dll, Platform.PathExpander.Machine.Expand), $@"HKLM\{path}\AppInit_DLLs", Hive.Machine, null, $"appinit:{dll}")
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

    /// <summary>
    /// "@%SystemRoot%\system32\x.dll,-101" display names resolved through SHLoadIndirectString. A driver package writes
    /// "@oem19.inf,%ServiceDisplayName%;Intel(R) ... Service", which that function cannot read: the text after the ';'
    /// is the name the service manager shows.
    /// </summary>
    public static string? ResolveIndirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('@')) return value;
        var sb = new StringBuilder(512);
        if (SHLoadIndirectString(value, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.ToString() is { Length: > 0 } resolved && !resolved.StartsWith('@')) return resolved;
        return InfFallback(value) ?? value;
    }

    /// <summary>The fallback text of "@file.inf,%key%;text"; null for any other form.</summary>
    internal static string? InfFallback(string value)
    {
        var inf = value.IndexOf(".inf,", StringComparison.OrdinalIgnoreCase);
        if (inf < 0) return null;
        // The key is between two '%': the ';' that follows it starts the text (which may hold ';' itself).
        var keyStart = value.IndexOf('%', inf);
        var keyEnd = keyStart < 0 ? -1 : value.IndexOf('%', keyStart + 1);
        var semicolon = keyEnd < 0 ? -1 : value.IndexOf(';', keyEnd);
        return semicolon < 0 || value[(semicolon + 1)..].Trim() is not { Length: > 0 } text ? null : text;
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
        // A service to Manual is the change Services & tasks makes with the same rule (ServiceManager.Change): moderate
        // where the service catalog warns about it. Removing a program redirect stays Expert.
        var risk = e.Kind switch
        {
            StartupKind.ImageHijack => Risk.Expert,
            StartupKind.Service when Catalog.CatalogData.Current.Services.Find(Services.ServiceManager.BaseName(e.Target!))?.Mode == "warn" => Risk.Moderate,
            _ => Risk.Safe,
        };
        return new TweakDefinition
        {
            Id = $"startup.{(enabled ? "on" : "off")}.{e.Kind.ToString().ToLowerInvariant()}.{TweakIds.Slug(e.Key)}",
            Docs = $"startup.{e.Kind.ToString().ToLowerInvariant()}",
            Subject = e.Name,
            Category = "Startup",
            Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Risk = risk,
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
            Sources =
            [
                "https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns",
                "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd",
                "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-logon",
            ],
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

    /// <summary>
    /// Third-party entries enabled at logon: what F16 counts. Tunero's own "Apply changes again" task is not one: it runs
    /// two minutes after sign-in, only when the user turned it on, and turning it off belongs in Settings.
    /// </summary>
    public static bool CountsForF16(StartupEntry e) => e.Enabled == true && e.Kind is StartupKind.RunKey or StartupKind.StartupFolder or StartupKind.LogonTask
        && !(e.Kind == StartupKind.LogonTask && Tweaks.ReapplyTask.IsOwnTask(e.Name));
}
