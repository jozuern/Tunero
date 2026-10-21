using Microsoft.Win32;

namespace Optimizer.Core.Actions;

/// <summary>
/// Which registry root an action targets. <see cref="User"/> = the signed-in session user's hive; <see cref="DefaultUser"/> =
/// the Default profile (C:\Users\Default\NTUSER.DAT), the template Windows copies for every account created later;
/// <see cref="SignInScreen"/> = HKU\.DEFAULT, the settings Windows uses before anyone signs in (the sign-in screen).
/// </summary>
public enum Hive { Machine, User, DefaultUser, SignInScreen }

/// <summary>Registry roots. Real: HKLM (64-bit view) and HKU\&lt;session SID&gt;. Tests: a sandbox under HKCU.</summary>
public interface IRegistryRoots
{
    /// <summary>Opens <paramref name="path"/> under the hive; returns null if missing and <paramref name="create"/> is false.</summary>
    RegistryKey? Open(Hive hive, string path, bool writable, bool create = false);

    /// <summary>Human-readable prefix for change lists ("HKLM", "HKU\S-1-5-...").</summary>
    string DisplayRoot(Hive hive);

    /// <summary>
    /// Keeps a hive that is not always loaded (<see cref="Hive.DefaultUser"/>) loaded until the result is disposed; holds
    /// nest, and the hive is unloaded when the last one ends. Open that hive only inside a hold. Null for other hives.
    /// </summary>
    IDisposable? Hold(Hive hive) => null;
}

public enum ServiceStart { Boot = 0, System = 1, Automatic = 2, Manual = 3, Disabled = 4, AutomaticDelayed = 102 }

public interface IServiceManager
{
    /// <summary>Null when the service does not exist.</summary>
    ServiceStart? GetStartType(string name);

    void SetStartType(string name, ServiceStart start);

    /// <summary>True when running, false when stopped, null when the service does not exist or is starting or stopping.</summary>
    bool? IsRunning(string name) => null;
}

public interface IPowerManager
{
    Guid ActiveScheme();
    uint? ReadAc(Guid scheme, Guid subgroup, Guid setting);
    uint? ReadDc(Guid scheme, Guid subgroup, Guid setting);
    void WriteAc(Guid scheme, Guid subgroup, Guid setting, uint value);
    void WriteDc(Guid scheme, Guid subgroup, Guid setting, uint value);
    void SetActive(Guid scheme);
    bool SchemeExists(Guid scheme);
    Guid Duplicate(Guid source, string friendlyName);
    void Delete(Guid scheme);
    IReadOnlyList<(Guid Id, string Name)> Schemes();
    void Export(Guid scheme, string file);

    /// <summary>The firmware supports hibernation (S4), also while it is turned off; null when unknown.</summary>
    bool? HibernationSupported() => null;
}

public interface IBcdStore
{
    /// <summary>Elements of {current} with their values as bcdedit prints them (element identifiers are not localized).</summary>
    IReadOnlyDictionary<string, string> CurrentValues();

    /// <summary>Elements present on {current}.</summary>
    IReadOnlySet<string> CurrentElements() => CurrentValues().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    void Set(string element, string value);
    void Delete(string element);
    void Export(string file);
}

public interface ITaskScheduler
{
    /// <summary>Null when the task does not exist.</summary>
    bool? IsEnabled(string path);
    void SetEnabled(string path, bool enabled);

    /// <summary>All registered tasks (read-only listing for the startup and task pages).</summary>
    IReadOnlyList<ScheduledTaskInfo> List();

    /// <summary>The task's registration XML, as Task Scheduler exports it; null when the task does not exist.</summary>
    string? ExportXml(string path) => null;

    /// <summary>Deletes the task.</summary>
    void Delete(string path) => throw new NotSupportedException("deleting tasks");

    /// <summary>Registers the task from its exported XML again, with the account the XML names (no stored password).</summary>
    void Register(string path, string xml) => throw new NotSupportedException("registering tasks");
}

/// <summary>A scheduled task: first action's program, author, and whether it starts at logon or at boot.</summary>
/// <param name="Command">The first program the task runs, null when it runs none (a COM handler).</param>
/// <param name="OtherActions">Further programs it runs after the first, as (program, arguments).</param>
/// <param name="ComHandler">The CLSID of a COM handler the task runs instead of a program (many of Windows' own tasks).</param>
public sealed record ScheduledTaskInfo(string Path, bool Enabled, string? Author, string? Command, string? Arguments, bool AtLogon, bool AtBoot, DateTime? LastRun,
    IReadOnlyList<(string Command, string? Arguments)>? OtherActions = null, string? ComHandler = null);

public interface IDisplayManager
{
    /// <summary>Current whole-Hz refresh of a display; null if the display is gone.</summary>
    int? CurrentRefresh(string gdiName);
    void SetMode(string gdiName, int width, int height, int refreshHz);
    /// <summary>Only the refresh rate, at the resolution the display has now.</summary>
    void SetRefresh(string gdiName, int refreshHz);
}

/// <summary>Keyboard accessibility features with a keyboard shortcut (SystemParametersInfo STICKYKEYS, FILTERKEYS, TOGGLEKEYS).</summary>
public enum AccessibilityFeature { StickyKeys, FilterKeys, ToggleKeys }

public interface IAccessibilitySettings
{
    /// <summary>The feature's dwFlags for the signed-in session user; null when they cannot be read or written for that user.</summary>
    uint? GetFlags(AccessibilityFeature feature);

    /// <summary>Writes dwFlags for the session user and saves them to the user profile.</summary>
    void SetFlags(AccessibilityFeature feature, uint flags);
}

/// <summary>Per-user timing settings of SystemParametersInfo that take milliseconds.</summary>
public enum SystemParameter { MenuShowDelay, MouseHoverTime }

public interface ISystemParameters
{
    /// <summary>The value for the signed-in session user; null when it cannot be read or written for that user.</summary>
    uint? Get(SystemParameter parameter);

    /// <summary>Sets the value for the session user, saves it to the user profile and tells running programs.</summary>
    void Set(SystemParameter parameter, uint value);
}

/// <summary>
/// Starts the tools the app runs (System32 tools by plain name, or a full path the caller checked: the trusted winget,
/// the hash-checked PresentMon, Chocolatey from its admin-only folder), always through the hardened
/// <see cref="Platform.ProcessLauncher"/>. One timeout policy for both: when the time is up the tool and everything it
/// started are ended and a <see cref="TimeoutException"/> follows. <see cref="Run"/> waits <see cref="ToolTimeouts.Default"/>
/// when no time is given; <see cref="RunAsync"/> has no limit then (a repair or an installer ended halfway can leave
/// things broken), so reads that wait on the network pass one (<see cref="ToolTimeouts.PackageList"/>).
/// </summary>
public interface IProcessRunner
{
    (int ExitCode, string Output) Run(string file, string arguments, TimeSpan? timeout = null);

    /// <summary>For a tool that writes UTF-8 (winget), not the OEM code page of Windows' console tools.</summary>
    (int ExitCode, string Output) RunUtf8(string file, string arguments, TimeSpan? timeout = null) => Run(file, arguments, timeout);

    /// <summary>
    /// Runs a long tool without a window and reports each output line to <paramref name="lines"/> while it runs (trimmed,
    /// empty lines left out, the last segment of a line rewritten with carriage returns); returns the exit code.
    /// Cancelling <paramref name="ct"/> (a Stop button) ends the process tree and throws
    /// <see cref="OperationCanceledException"/> once the tool has exited; after <paramref name="timeout"/> (null: no
    /// limit) the same happens with a <see cref="TimeoutException"/>. The output is read as UTF-8 unless
    /// <paramref name="options"/> names another encoding.
    /// </summary>
    Task<int> RunAsync(string file, string arguments, TimeSpan? timeout, CancellationToken ct, IProgress<string>? lines, RunOptions? options = null);
}

/// <summary>How <see cref="IProcessRunner.RunAsync"/> starts a tool, beyond its command line.</summary>
public sealed record RunOptions
{
    /// <summary>The encoding the tool writes; null reads UTF-8 (winget, PresentMon).</summary>
    public System.Text.Encoding? Encoding { get; init; }

    /// <summary>Called right after the process has started (PresentMon releases the file it held open since the hash check).</summary>
    public Action? Started { get; init; }

    /// <summary>Changes the hardened environment before the start (Chocolatey's root, <see cref="Apps.Chocolatey.HardenEnvironment"/>).</summary>
    public Action<IDictionary<string, string?>>? Environment { get; init; }

    /// <summary>A console tool that writes the OEM code page (dism, choco).</summary>
    public static RunOptions Oem { get; } = new() { Encoding = Platform.ToolEncoding.Oem };

    /// <summary>sfc, which writes UTF-16 to a redirected pipe.</summary>
    public static RunOptions Utf16 { get; } = new() { Encoding = Platform.ToolEncoding.Utf16 };
}

/// <summary>The time limits of <see cref="IProcessRunner"/>, in one place.</summary>
public static class ToolTimeouts
{
    /// <summary>What <see cref="IProcessRunner.Run"/> waits when no time is given: a quick read or switch of a console tool.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

    /// <summary>A package list or search that reads a catalog from the network (winget search, winget upgrade without installing).</summary>
    public static readonly TimeSpan PackageList = TimeSpan.FromMinutes(5);

    /// <summary>How long PresentMon may run past its own capture time before it counts as hung.</summary>
    public static readonly TimeSpan CaptureGrace = TimeSpan.FromSeconds(60);
}

/// <summary>Everything an action needs to read and write the system. Swappable for tests.</summary>
public sealed class ActionContext
{
    public required IRegistryRoots Registry { get; init; }
    public required IServiceManager Services { get; init; }
    public required IPowerManager Power { get; init; }
    public required IBcdStore Bcd { get; init; }
    public required ITaskScheduler Tasks { get; init; }
    public required IDisplayManager Displays { get; init; }
    public required IProcessRunner Processes { get; init; }
    public required IPowerModeManager PowerMode { get; init; }
    public required IDeviceManager Devices { get; init; }
    public required INetworkManager Network { get; init; }
    public required IMemoryAgent MemoryAgent { get; init; }
    public required INvidiaSettings Nvidia { get; init; }
    public required IAccessibilitySettings Accessibility { get; init; }

    /// <summary>Menu and hover timing; null makes the actions that need it unsupported (contexts that do not set it).</summary>
    public ISystemParameters? SystemParameters { get; init; }

    /// <summary>Called after user-scope changes that need a live refresh (mouse, Explorer settings).</summary>
    public Action<string>? Notify { get; init; }

    /// <summary>Folder for BCD/power exports belonging to the backup store (admin-only; never the user's %TEMP%).</summary>
    public required string ExportFolder { get; init; }

    /// <summary>
    /// Whether a program may be started elevated without asking (a task of this app runs it at sign-in): only one in a
    /// folder users cannot change. Tests that register tasks with made-up paths pass their own.
    /// </summary>
    public Func<string, bool> MayStartElevated { get; init; } = Platform.TrustedPath.IsAdminOnlyBelowSharedFolders;

    /// <summary>Interface GUIDs ("{...}") of active Ethernet/Wi-Fi adapters, for per-interface TCP values ("{nic}" in paths).</summary>
    public IReadOnlyList<string> NetworkInterfaceIds { get; init; } = [];

    /// <summary>
    /// Reads the adapters again (the app: an adapter connected after the start counts too). Null: the fixed
    /// <see cref="NetworkInterfaceIds"/> (tests).
    /// </summary>
    public Func<IReadOnlyList<string>>? NetworkInterfaceIdsSource { get; init; }

    public IReadOnlyList<string> CurrentNetworkInterfaceIds() => NetworkInterfaceIdsSource?.Invoke() ?? NetworkInterfaceIds;
}
