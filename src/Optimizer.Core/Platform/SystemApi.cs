using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Optimizer.Core.Actions;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Platform;

/// <summary>
/// Real registry roots. "User" = HKU\&lt;SID of the signed-in session user&gt;, never Registry.CurrentUser: under
/// over-the-shoulder elevation or Administrator protection the process's HKCU belongs to another account.
/// </summary>
public sealed class SystemRegistryRoots(string? userSid) : IRegistryRoots
{
    public string? UserSid { get; } = userSid;

    public RegistryKey? Open(Hive hive, string path, bool writable, bool create = false)
    {
        RegistryKey root;
        if (hive == Hive.Machine)
        {
            root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }
        else if (hive == Hive.DefaultUser)
        {
            root = DefaultProfileHive.OpenRoot(writable);
        }
        else
        {
            if (UserSid is null) throw new InvalidOperationException("The signed-in user could not be determined; user settings are not changed.");
            root = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64).OpenSubKey(UserSid, writable)
                   ?? throw new InvalidOperationException($"User hive {UserSid} is not loaded.");
        }
        using (root)
        {
            return create ? root.CreateSubKey(path, writable) : root.OpenSubKey(path, writable);
        }
    }

    public string DisplayRoot(Hive hive) => hive switch
    {
        Hive.Machine => "HKLM",
        Hive.DefaultUser => DefaultProfileHive.FilePath() ?? @"Default\NTUSER.DAT",
        _ => $"HKU\\{UserSid}",
    };

    public IDisposable? Hold(Hive hive) => hive == Hive.DefaultUser ? DefaultProfileHive.Hold() : null;
}

public sealed class SystemServiceManager : IServiceManager
{
    public bool? IsRunning(string name)
    {
        var scm = NativeWrite.OpenSCManager(null, null, NativeWrite.ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = NativeWrite.OpenService(scm, name, NativeWrite.ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try
            {
                if (!NativeWrite.QueryServiceStatus(svc, out var status)) return null;
                return status.dwCurrentState switch
                {
                    NativeWrite.ServiceRunning => true,
                    NativeWrite.ServiceStopped => false,
                    _ => null,
                };
            }
            finally
            {
                NativeWrite.CloseServiceHandle(svc);
            }
        }
        finally
        {
            NativeWrite.CloseServiceHandle(scm);
        }
    }

    public ServiceStart? GetStartType(string name)
    {
        var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
        if (Reg.HklmInt(path, "Start") is not { } start) return null;
        return start == 2 && Reg.HklmInt(path, "DelayedAutostart") == 1 ? ServiceStart.AutomaticDelayed : (ServiceStart)start;
    }

    public void SetStartType(string name, ServiceStart start)
    {
        var scm = NativeWrite.OpenSCManager(null, null, NativeWrite.ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var svc = NativeWrite.OpenService(scm, name, NativeWrite.ServiceChangeConfig | NativeWrite.ServiceQueryConfig);
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open service {name}");
            try
            {
                var type = start == ServiceStart.AutomaticDelayed ? 2u : (uint)start;
                // ChangeServiceConfig sets the start type; protected services (e.g. WaaSMedicSvc) return access denied.
                if (!NativeWrite.ChangeServiceConfig(svc, NativeWrite.ServiceNoChange, type, NativeWrite.ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot change start type of {name}");
                // The delayed flag only when it changes: writing "not delayed" where the value never existed would leave a
                // DelayedAutostart=0 behind after an undo.
                var delayedNow = Reg.HklmInt($@"SYSTEM\CurrentControlSet\Services\{name}", "DelayedAutostart") == 1;
                if (type == 2 && delayedNow != (start == ServiceStart.AutomaticDelayed))
                {
                    var info = new NativeWrite.SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = start == ServiceStart.AutomaticDelayed ? 1 : 0 };
                    if (!NativeWrite.ChangeServiceConfig2(svc, NativeWrite.ServiceConfigDelayedAutoStartInfo, ref info))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot change the delayed start of {name}");
                }
            }
            finally
            {
                NativeWrite.CloseServiceHandle(svc);
            }
        }
        finally
        {
            NativeWrite.CloseServiceHandle(scm);
        }
    }
}

public sealed class SystemPowerManager(IProcessRunner processes) : IPowerManager
{
    public Guid ActiveScheme()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return Guid.Empty;
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            Native.LocalFree(ptr);
        }
    }

    public uint? ReadAc(Guid scheme, Guid sub, Guid setting) => Native.PowerReadACValueIndex(IntPtr.Zero, scheme, sub, setting, out var v) == 0 ? v : null;
    public uint? ReadDc(Guid scheme, Guid sub, Guid setting) => Native.PowerReadDCValueIndex(IntPtr.Zero, scheme, sub, setting, out var v) == 0 ? v : null;

    public void WriteAc(Guid scheme, Guid sub, Guid setting, uint value) => Check(NativeWrite.PowerWriteACValueIndex(IntPtr.Zero, scheme, sub, setting, value), "PowerWriteACValueIndex");
    public void WriteDc(Guid scheme, Guid sub, Guid setting, uint value) => Check(NativeWrite.PowerWriteDCValueIndex(IntPtr.Zero, scheme, sub, setting, value), "PowerWriteDCValueIndex");
    public void SetActive(Guid scheme) => Check(NativeWrite.PowerSetActiveScheme(IntPtr.Zero, scheme), "PowerSetActiveScheme");

    public bool SchemeExists(Guid scheme) => Schemes().Any(s => s.Id == scheme);

    public Guid Duplicate(Guid source, string friendlyName)
    {
        var dest = IntPtr.Zero;
        Check(NativeWrite.PowerDuplicateScheme(IntPtr.Zero, source, ref dest), "PowerDuplicateScheme");
        try
        {
            var id = Marshal.PtrToStructure<Guid>(dest);
            var name = Encoding.Unicode.GetBytes(friendlyName + "\0");
            Check(NativeWrite.PowerWriteFriendlyName(IntPtr.Zero, id, IntPtr.Zero, IntPtr.Zero, name, (uint)name.Length), "PowerWriteFriendlyName");
            return id;
        }
        finally
        {
            Native.LocalFree(dest);
        }
    }

    public void Delete(Guid scheme) => Check(NativeWrite.PowerDeleteScheme(IntPtr.Zero, scheme), "PowerDeleteScheme");

    public IReadOnlyList<(Guid Id, string Name)> Schemes()
    {
        var list = new List<(Guid, string)>();
        for (uint i = 0; ; i++)
        {
            var buffer = new byte[16];
            uint size = 16;
            if (NativeWrite.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeWrite.AccessScheme, i, buffer, ref size) != 0) break;
            var id = new Guid(buffer);
            list.Add((id, FriendlyName(id)));
        }
        return list;
    }

    /// <summary>Throws when powercfg fails, so the backup never lists an export file that does not exist.</summary>
    public void Export(Guid scheme, string file)
    {
        var (code, output) = processes.Run("powercfg.exe", $"/export \"{file}\" {scheme}");
        if (code != 0 || !File.Exists(file)) throw new InvalidOperationException($"powercfg /export failed ({code}): {output}");
    }

    public bool? HibernationSupported() => Hardware.Probes.PowerProbe.HibernationSupported();

    private static string FriendlyName(Guid scheme)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return "";
        var buffer = new byte[size];
        return Native.PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : "";
    }

    private static void Check(uint result, string api)
    {
        if (result != 0) throw new Win32Exception((int)result, $"{api} failed");
    }
}

/// <summary>bcdedit wrapper. Element identifiers are read from "/enum {current} /v" (identifiers are not localized).</summary>
public sealed class SystemBcdStore(IProcessRunner processes) : IBcdStore
{
    private const string CacheKey = "bcd:current";

    /// <summary>Shared through <see cref="ReadCache"/>: the findings and every BCD tweak of a scan read the same list.</summary>
    public IReadOnlyDictionary<string, string> CurrentValues() => ReadCache.Get(processes, CacheKey, () =>
    {
        var (code, output) = processes.Run("bcdedit.exe", "/enum {current} /v");
        // bcdedit needs admin rights; failing loudly makes BCD tweaks "unknown" instead of wrongly "applied".
        if (code != 0) throw new InvalidOperationException($"bcdedit /enum failed ({code})");
        return Parse(output);
    });

    /// <summary>"element    value" lines; the localized header lines start with a capital letter and are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || !char.IsAsciiLetterLower(line[0])) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            map.TryAdd(parts[0], parts.Length > 1 ? parts[1].Trim() : "");
        }
        return map;
    }

    public void Set(string element, string value) => Run($"/set {{current}} {element} {value}");
    public void Delete(string element) => Run($"/deletevalue {{current}} {element}");
    public void Export(string file) => Run($"/export \"{file}\"");

    private void Run(string args) => ReadCache.Writing(processes, CacheKey, () =>
    {
        var (code, output) = processes.Run("bcdedit.exe", args);
        if (code != 0) throw new InvalidOperationException($"bcdedit {args} failed ({code}): {output}");
    });
}

/// <summary>Task Scheduler 2.0 via its COM API (<see cref="TaskSchedulerCom"/>).</summary>
public sealed class SystemTaskScheduler : ITaskScheduler
{
    /// <summary>
    /// Null only for a task (or folder) that does not exist. Any other error throws: a change that took a Task Scheduler
    /// error for a missing task would skip the task and still report the change as applied.
    /// </summary>
    public bool? IsEnabled(string path)
    {
        try
        {
            return GetTask(path).Enabled;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException || ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    public void SetEnabled(string path, bool enabled)
    {
        GetTask(path).Enabled = enabled;
        lock (ListGate) _list = null;
    }

    // Enumerating every task folder over COM takes about a second; the scan, the Startup page and the Services page
    // all need the list. It is shared for a short time and dropped on every change made through this class.
    private static readonly TimeSpan ListLifetime = TimeSpan.FromSeconds(30);
    private static readonly Lock ListGate = new();
    private static (DateTime Read, IReadOnlyList<ScheduledTaskInfo> Tasks)? _list;

    /// <summary>A full scan reads the task list fresh; pages opened after it reuse that read.</summary>
    public static void ForgetList()
    {
        lock (ListGate) _list = null;
    }

    public IReadOnlyList<ScheduledTaskInfo> List()
    {
        lock (ListGate)
        {
            if (_list is { } cached && DateTime.UtcNow - cached.Read < ListLifetime) return cached.Tasks;
        }
        var tasks = ReadList();
        lock (ListGate) _list = (DateTime.UtcNow, tasks);
        return tasks;
    }

    private static TaskSchedulerCom.ITaskService Connect() => TaskSchedulerCom.Connect();

    /// <summary>
    /// Every task of every folder. The folders are listed first, then read by up to four threads, each with its own
    /// connection (no COM object is shared between threads); from a single-threaded apartment one after another.
    /// Sorted by path, so the order does not depend on which thread finished first.
    /// </summary>
    private static IReadOnlyList<ScheduledTaskInfo> ReadList()
    {
        var service = Connect();
        var folders = new List<string>();
        var stack = new Stack<TaskSchedulerCom.ITaskFolder>();
        stack.Push(service.GetFolder("\\"));
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            folders.Add(folder.Path);
            try
            {
                var subs = folder.GetFolders(0);
                for (var i = 1; i <= subs.Count; i++) stack.Push(subs.Item(i));
            }
            catch (Exception ex)
            {
                Log.Warn("tasks", $"unreadable folder: {ex.Message}");
            }
        }

        var list = new System.Collections.Concurrent.ConcurrentBag<ScheduledTaskInfo>();
        void ReadFolder(TaskSchedulerCom.ITaskService connection, string path)
        {
            try
            {
                var tasks = connection.GetFolder(path).GetTasks(TaskSchedulerCom.EnumHidden);
                for (var i = 1; i <= tasks.Count; i++)
                {
                    try
                    {
                        list.Add(Describe(tasks.Item(i)));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("tasks", $"unreadable task: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("tasks", $"unreadable folder: {ex.Message}");
            }
        }
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            Parallel.ForEach(folders, new ParallelOptions { MaxDegreeOfParallelism = 4 }, () => (TaskSchedulerCom.ITaskService?)null, (path, _, connection) =>
            {
                connection ??= Connect();
                ReadFolder(connection, path);
                return connection;
            }, _ => { });
        else
            foreach (var path in folders) ReadFolder(service, path);
        return list.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly System.Xml.Linq.XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Triggers, the first program action and the author from the task's XML: one COM call instead of about ten per
    /// task. An author that is a resource string ("$(@%SystemRoot%\system32\x.dll,-101)") is read through COM, which
    /// resolves it. A task whose XML cannot be read is described property by property.
    /// </summary>
    private static ScheduledTaskInfo Describe(TaskSchedulerCom.IRegisteredTask task)
    {
        var path = task.Path;
        var enabled = task.Enabled;
        var lastRun = DateTime.FromOADate(task.LastRunTime);
        var lastRunOrNull = lastRun.Year < 2000 ? (DateTime?)null : lastRun;
        System.Xml.Linq.XElement? root;
        try
        {
            root = System.Xml.Linq.XDocument.Parse(task.Xml).Root;
        }
        catch (System.Xml.XmlException)
        {
            root = null;
        }
        if (root is null || root.Name.Namespace != TaskNamespace) return DescribeByProperties(task, path, enabled, lastRunOrNull);

        var ns = TaskNamespace;
        var triggers = root.Element(ns + "Triggers")?.Elements().Select(e => e.Name.LocalName).ToList() ?? [];
        // Every program the task runs counts (a second action can hide behind a signed first one), and a COM handler is
        // named by its CLSID so the scanner can check the DLL behind it.
        var actions = root.Element(ns + "Actions");
        var execs = actions?.Elements(ns + "Exec").Select(e => (Command: e.Element(ns + "Command")?.Value, Arguments: e.Element(ns + "Arguments")?.Value))
            .Where(e => !string.IsNullOrWhiteSpace(e.Command)).Select(e => (e.Command!, e.Arguments)).ToList() ?? [];
        var comHandler = actions?.Element(ns + "ComHandler")?.Element(ns + "ClassId")?.Value;
        var author = root.Element(ns + "RegistrationInfo")?.Element(ns + "Author")?.Value;
        if (author?.StartsWith("$(", StringComparison.Ordinal) == true) author = task.Definition.RegistrationInfo.Author;
        return new ScheduledTaskInfo(path, enabled, author, execs.Count > 0 ? execs[0].Item1 : null, execs.Count > 0 ? execs[0].Item2 : null,
            triggers.Contains("LogonTrigger"), triggers.Contains("BootTrigger"), lastRunOrNull, execs.Skip(1).ToList(), comHandler);
    }

    private static ScheduledTaskInfo DescribeByProperties(TaskSchedulerCom.IRegisteredTask task, string path, bool enabled, DateTime? lastRun)
    {
        var definition = task.Definition;
        bool logon = false, boot = false;
        var triggers = definition.Triggers;
        for (var i = 1; i <= triggers.Count; i++)
        {
            var t = triggers.Item(i).Type;
            if (t == TaskSchedulerCom.TriggerLogon) logon = true;
            if (t == TaskSchedulerCom.TriggerBoot) boot = true;
        }
        var execs = new List<(string Command, string? Arguments)>();
        var actions = definition.Actions;
        for (var i = 1; i <= actions.Count; i++)
            if (actions.Item(i) is TaskSchedulerCom.IExecAction { Type: TaskSchedulerCom.ActionExec } action && !string.IsNullOrWhiteSpace(action.Path))
                execs.Add((action.Path, action.Arguments));
        return new ScheduledTaskInfo(path, enabled, definition.RegistrationInfo.Author, execs.Count > 0 ? execs[0].Command : null,
            execs.Count > 0 ? execs[0].Arguments : null, logon, boot, lastRun, execs.Skip(1).ToList());
    }

    private static TaskSchedulerCom.IRegisteredTask GetTask(string path)
    {
        var service = Connect();
        var i = path.LastIndexOf('\\');
        var folder = service.GetFolder(i <= 0 ? "\\" : path[..i]);
        return folder.GetTask(path[(i + 1)..]);
    }

    /// <summary>
    /// The task's XML; null only when the task or its folder is not there (0x80070002 and 0x80070003, which .NET turns
    /// into FileNotFoundException and DirectoryNotFoundException). Any other error (access denied, the service not
    /// answering) is thrown: read as "missing", it would let a delete or an undo think the task is gone.
    /// </summary>
    public string? ExportXml(string path)
    {
        try
        {
            return GetTask(path).Xml;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                   || ex is COMException { HResult: unchecked((int)0x80070002) or unchecked((int)0x80070003) })
        {
            return null;
        }
    }

    public void Delete(string path)
    {
        var i = path.LastIndexOf('\\');
        Connect().GetFolder(i <= 0 ? "\\" : path[..i]).DeleteTask(path[(i + 1)..], 0);
        ForgetList();
    }

    /// <summary>
    /// Registers the exported XML under its old path, with the account and logon type its principal names. A task that
    /// stores a password cannot come back this way (<see cref="Startup.ScheduledTaskXml.Principal"/> refuses it).
    /// </summary>
    public void Register(string path, string xml)
    {
        if (Startup.ScheduledTaskXml.Principal(xml) is not var (logonType, user))
            throw new InvalidOperationException($"The task {path} runs with a stored password; it cannot be registered again without it.");
        var service = Connect();
        var i = path.LastIndexOf('\\');
        var folderPath = i <= 0 ? "\\" : path[..i];
        TaskSchedulerCom.ITaskFolder folder;
        try
        {
            folder = service.GetFolder(folderPath);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or DirectoryNotFoundException)
        {
            // The folder went with the task (an uninstaller removed it), or was never there (the app's own \Tunero folder):
            // create it. A missing folder comes back as 0x80070002, which .NET turns into FileNotFoundException.
            folder = service.GetFolder("\\").CreateFolder(folderPath.TrimStart('\\'), null);
        }
        folder.RegisterTask(path[(i + 1)..], xml, TaskSchedulerCom.CreateOrUpdate, user, null, logonType, null);
        ForgetList();
    }
}

public sealed class SystemDisplayManager : IDisplayManager
{
    public int? CurrentRefresh(string gdiName)
    {
        var dm = new Native.DEVMODE { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
        return Native.EnumDisplaySettingsEx(gdiName, -1 /* ENUM_CURRENT_SETTINGS */, ref dm, 0) ? (int)dm.dmDisplayFrequency : null;
    }

    public void SetMode(string gdiName, int width, int height, int refreshHz) => Change(gdiName, width, height, refreshHz);

    public void SetRefresh(string gdiName, int refreshHz) => Change(gdiName, null, null, refreshHz);

    private static void Change(string gdiName, int? width, int? height, int refreshHz)
    {
        var dm = new Native.DEVMODE { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
        if (!Native.EnumDisplaySettingsEx(gdiName, -1, ref dm, 0)) throw new InvalidOperationException($"Display {gdiName} not found");
        // Without a width and height the current ones stay (EnumDisplaySettingsEx filled them in).
        if (width is { } w) dm.dmPelsWidth = (uint)w;
        if (height is { } h) dm.dmPelsHeight = (uint)h;
        dm.dmDisplayFrequency = (uint)refreshHz;
        dm.dmFields = NativeWrite.DmPelsWidth | NativeWrite.DmPelsHeight | NativeWrite.DmDisplayFrequency;
        var result = NativeWrite.ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, NativeWrite.CdsUpdateRegistry, IntPtr.Zero);
        if (result is not (NativeWrite.DispChangeSuccessful or NativeWrite.DispChangeRestart))
            throw new InvalidOperationException($"ChangeDisplaySettingsEx failed ({result})");
    }
}

/// <summary>Runs a system tool without a window and logs the command line, exit code and output.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>
    /// Console tools (ipconfig, chkdsk, netsh, net, reagentc, and Windows PowerShell when its output is redirected) write
    /// in the OEM code page of the system, not UTF-8: read as UTF-8, German umlauts come out garbled.
    /// </summary>
    public (int ExitCode, string Output) Run(string file, string arguments, TimeSpan? timeout = null) =>
        ProcessLauncher.Run(ProcessLauncher.Info(file, arguments, capture: ToolEncoding.Oem), timeout ?? ToolTimeouts.Default);

    public (int ExitCode, string Output) RunUtf8(string file, string arguments, TimeSpan? timeout = null) =>
        ProcessLauncher.Run(ProcessLauncher.Info(file, arguments, capture: Encoding.UTF8), timeout ?? ToolTimeouts.Default);

    public Task<int> RunAsync(string file, string arguments, TimeSpan? timeout, CancellationToken ct, IProgress<string>? lines, RunOptions? options = null)
    {
        var psi = ProcessLauncher.Info(file, arguments, capture: options?.Encoding ?? Encoding.UTF8);
        options?.Environment?.Invoke(psi.Environment);
        return ProcessLauncher.RunStreamingAsync(psi, lines, timeout, ct, options?.Started);
    }
}

/// <summary>
/// Keyboard accessibility flags through SystemParametersInfo. The call works on the profile of the account the process
/// runs as, so it is used only when that account is the signed-in session user (the usual UAC prompt); under
/// over-the-shoulder elevation the flags count as unavailable and nothing is changed.
/// </summary>
public sealed class SystemAccessibilitySettings(string? userSid) : IAccessibilitySettings
{
    private bool IsSessionUser()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return userSid is not null && string.Equals(identity.User?.Value, userSid, StringComparison.OrdinalIgnoreCase);
    }

    // Structure size in DWORDs: cbSize and dwFlags, FILTERKEYS adds wait, delay, repeat and bounce times.
    private static (uint Get, uint Set, int Dwords) Spi(AccessibilityFeature f) => f switch
    {
        AccessibilityFeature.StickyKeys => (NativeWrite.SpiGetStickyKeys, NativeWrite.SpiSetStickyKeys, 2),
        AccessibilityFeature.ToggleKeys => (NativeWrite.SpiGetToggleKeys, NativeWrite.SpiSetToggleKeys, 2),
        _ => (NativeWrite.SpiGetFilterKeys, NativeWrite.SpiSetFilterKeys, 6),
    };

    private static uint[]? Read(AccessibilityFeature feature)
    {
        var (get, _, dwords) = Spi(feature);
        var data = new uint[dwords];
        data[0] = (uint)(dwords * 4);
        return NativeWrite.SystemParametersInfoDwords(get, data[0], data, 0) ? data : null;
    }

    public uint? GetFlags(AccessibilityFeature feature) => IsSessionUser() ? Read(feature)?[1] : null;

    public void SetFlags(AccessibilityFeature feature, uint flags)
    {
        if (!IsSessionUser()) throw new InvalidOperationException("Accessibility settings can only be changed for the account the app runs as.");
        var data = Read(feature) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
        data[1] = flags;
        var (_, set, _) = Spi(feature);
        if (!NativeWrite.SystemParametersInfoDwords(set, data[0], data, NativeWrite.SpifUpdateIniFile | NativeWrite.SpifSendChange))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Log.Info("accessibility", $"{feature} flags set", new { flags });
    }
}

/// <summary>
/// Menu and hover timing through SystemParametersInfo. Like the accessibility settings, the call works on the profile of
/// the account the process runs as, so it is used only when that account is the signed-in session user.
/// </summary>
public sealed partial class SystemParametersSettings(string? userSid) : ISystemParameters
{
    private const uint SpiGetMouseHoverTime = 0x0066, SpiSetMouseHoverTime = 0x0067, SpiGetMenuShowDelay = 0x006A, SpiSetMenuShowDelay = 0x006B;
    private const uint SpifUpdateIniFile = 0x1, SpifSendChange = 0x2;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoGet(uint action, uint uiParam, out uint pvParam, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoSet(uint action, uint uiParam, IntPtr pvParam, uint winIni);

    private bool IsSessionUser()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return userSid is not null && string.Equals(identity.User?.Value, userSid, StringComparison.OrdinalIgnoreCase);
    }

    private static (uint Get, uint Set) Spi(SystemParameter p) => p switch
    {
        SystemParameter.MenuShowDelay => (SpiGetMenuShowDelay, SpiSetMenuShowDelay),
        _ => (SpiGetMouseHoverTime, SpiSetMouseHoverTime),
    };

    public uint? Get(SystemParameter parameter) =>
        IsSessionUser() && SystemParametersInfoGet(Spi(parameter).Get, 0, out var value, 0) ? value : null;

    public void Set(SystemParameter parameter, uint value)
    {
        if (!IsSessionUser()) throw new InvalidOperationException("These settings can only be changed for the account the app runs as.");
        // Both take the new value in uiParam.
        if (!SystemParametersInfoSet(Spi(parameter).Set, value, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Log.Info("systemparameters", $"{parameter} set", new { value });
    }
}

public static class SystemNotify
{
    /// <summary>Live refresh after user-scope changes. Mouse values: SPI without SPIF_UPDATEINIFILE (the hive was written directly).</summary>
    public static void Handle(IRegistryRoots registry, string what)
    {
        try
        {
            if (what == "mouse")
            {
                int Get(string name) => int.TryParse(RegistryValue.Read(registry, Hive.User, @"Control Panel\Mouse", name).Data, out var v) ? v : 0;
                NativeWrite.SystemParametersInfo(NativeWrite.SpiSetMouse, 0, [Get("MouseThreshold1"), Get("MouseThreshold2"), Get("MouseSpeed")], 0);
            }
            NativeWrite.SendMessageTimeout((IntPtr)NativeWrite.HwndBroadcast, NativeWrite.WmSettingChange, IntPtr.Zero, "Environment",
                NativeWrite.SmtoAbortIfHung, 1000, out _);
        }
        catch (Exception ex)
        {
            Log.Warn("notify", $"live refresh '{what}' failed: {ex.Message}");
        }
    }

    public static ActionContext CreateContext(string? userSid, string exportFolder, Func<IReadOnlyList<string>>? networkInterfaceIds = null)
    {
        var processes = new SystemProcessRunner();
        // DISM and PowerShell reads of a scan or an apply are shared for a few seconds (dropped when the action writes).
        ReadCache.Enable(processes, TimeSpan.FromSeconds(20));
        var registry = new SystemRegistryRoots(userSid);
        return new ActionContext
        {
            Registry = registry,
            Services = new SystemServiceManager(),
            Power = new SystemPowerManager(processes),
            Bcd = new SystemBcdStore(processes),
            Tasks = new SystemTaskScheduler(),
            Displays = new SystemDisplayManager(),
            Processes = processes,
            PowerMode = new SystemPowerModeManager(),
            Devices = new SystemDeviceManager(),
            Network = new SystemNetworkManager(),
            MemoryAgent = new SystemMemoryAgent(),
            Nvidia = new SystemNvidiaSettings(),
            Accessibility = new SystemAccessibilitySettings(userSid),
            SystemParameters = new SystemParametersSettings(userSid),
            Notify = what => Handle(registry, what),
            ExportFolder = exportFolder,
            NetworkInterfaceIdsSource = networkInterfaceIds,
        };
    }
}
