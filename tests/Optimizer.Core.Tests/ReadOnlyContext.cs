using System.Text;
using Microsoft.Win32;
using Optimizer.Core.Actions;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>
/// The real system adapters for the read-only checks of this PC (Category=Hardware), with every write refused: registry
/// keys open read-only, and the write methods of services, power plans, BCD, tasks, displays, devices, DNS, NVIDIA and
/// accessibility throw. Tools still run, since reads need them (bcdedit /enum, dism /Get-, PowerShell Get-), but a
/// command that looks like a change is refused (<see cref="ReadOnlyProcesses.LooksLikeAWrite"/>). The only way a test
/// may reach the real adapters (<see cref="ArchitectureTests"/>): a test that applies a tweak with it fails instead of
/// changing the developer's PC.
/// </summary>
internal static class ReadOnlyContext
{
    public static ActionContext Create(string? userSid)
    {
        var real = SystemNotify.CreateContext(userSid, Path.GetTempPath());
        return new ActionContext
        {
            Registry = new Registry(real.Registry),
            Services = new Services(real.Services),
            Power = new Power(real.Power),
            Bcd = new Bcd(real.Bcd),
            Tasks = new Tasks(real.Tasks),
            Displays = new Displays(real.Displays),
            Processes = new ReadOnlyProcesses(real.Processes),
            PowerMode = new PowerMode(real.PowerMode),
            Devices = new Devices(),
            Network = new Network(real.Network),
            MemoryAgent = real.MemoryAgent,
            Nvidia = new Nvidia(real.Nvidia),
            Accessibility = new Accessibility(real.Accessibility),
            ExportFolder = Path.GetTempPath(),
            Notify = _ => throw Refused("notify"),
        };
    }

    private static InvalidOperationException Refused(string what) => new($"read-only test context: {what} would change this PC");

    private sealed class Registry(IRegistryRoots real) : IRegistryRoots
    {
        public RegistryKey? Open(Hive hive, string path, bool writable, bool create = false) =>
            writable || create ? throw Refused($"registry write {path}") : real.Open(hive, path, false);

        public string DisplayRoot(Hive hive) => real.DisplayRoot(hive);
    }

    private sealed class Services(IServiceManager real) : IServiceManager
    {
        public ServiceStart? GetStartType(string name) => real.GetStartType(name);
        public void SetStartType(string name, ServiceStart start) => throw Refused($"service {name}");
        public bool? IsRunning(string name) => real.IsRunning(name);
    }

    private sealed class Power(IPowerManager real) : IPowerManager
    {
        public Guid ActiveScheme() => real.ActiveScheme();
        public uint? ReadAc(Guid scheme, Guid subgroup, Guid setting) => real.ReadAc(scheme, subgroup, setting);
        public uint? ReadDc(Guid scheme, Guid subgroup, Guid setting) => real.ReadDc(scheme, subgroup, setting);
        public void WriteAc(Guid scheme, Guid subgroup, Guid setting, uint value) => throw Refused("power setting");
        public void WriteDc(Guid scheme, Guid subgroup, Guid setting, uint value) => throw Refused("power setting");
        public void SetActive(Guid scheme) => throw Refused("active power plan");
        public bool SchemeExists(Guid scheme) => real.SchemeExists(scheme);
        public Guid Duplicate(Guid source, string friendlyName) => throw Refused("new power plan");
        public void Delete(Guid scheme) => throw Refused("power plan removal");
        public IReadOnlyList<(Guid Id, string Name)> Schemes() => real.Schemes();
        public void Export(Guid scheme, string file) => throw Refused("power plan export");
        public bool? HibernationSupported() => real.HibernationSupported();
    }

    private sealed class Bcd(IBcdStore real) : IBcdStore
    {
        public IReadOnlyDictionary<string, string> CurrentValues() => real.CurrentValues();
        public IReadOnlySet<string> CurrentElements() => real.CurrentElements();
        public void Set(string element, string value) => throw Refused($"BCD {element}");
        public void Delete(string element) => throw Refused($"BCD {element}");
        public void Export(string file) => throw Refused("BCD export");
    }

    private sealed class Tasks(ITaskScheduler real) : ITaskScheduler
    {
        public bool? IsEnabled(string path) => real.IsEnabled(path);
        public void SetEnabled(string path, bool enabled) => throw Refused($"task {path}");
        public IReadOnlyList<ScheduledTaskInfo> List() => real.List();
    }

    private sealed class Displays(IDisplayManager real) : IDisplayManager
    {
        public int? CurrentRefresh(string gdiName) => real.CurrentRefresh(gdiName);
        public void SetMode(string gdiName, int width, int height, int refreshHz) => throw Refused($"display mode {gdiName}");
        public void SetRefresh(string gdiName, int refreshHz) => throw Refused($"display mode {gdiName}");
    }

    private sealed class PowerMode(IPowerModeManager real) : IPowerModeManager
    {
        public Guid? Read() => real.Read();
        public void Write(Guid overlay) => throw Refused("power mode");
        public bool? OnBattery() => real.OnBattery();
    }

    private sealed class Devices : IDeviceManager
    {
        public void Restart(string deviceInstanceId) => throw Refused($"device restart {deviceInstanceId}");
    }

    private sealed class Network(INetworkManager real) : INetworkManager
    {
        public void SetDns(string interfaceGuid, string[]? servers) => throw Refused($"DNS of {interfaceGuid}");
        public IReadOnlyDictionary<string, bool>? DohServers() => real.DohServers();
    }

    private sealed class Nvidia(INvidiaSettings real) : INvidiaSettings
    {
        public bool Available => real.Available;
        public uint? ReadOwn(string profile, uint settingId) => real.ReadOwn(profile, settingId);
        public void Write(string profile, uint settingId, uint? value) => throw Refused("NVIDIA setting");
    }

    private sealed class Accessibility(IAccessibilitySettings real) : IAccessibilitySettings
    {
        public uint? GetFlags(AccessibilityFeature feature) => real.GetFlags(feature);
        public void SetFlags(AccessibilityFeature feature, uint flags) => throw Refused($"accessibility {feature}");
    }
}

/// <summary>Runs the tools the reads need and refuses a command that looks like a change.</summary>
internal sealed class ReadOnlyProcesses(IProcessRunner real) : IProcessRunner
{
    /// <summary>
    /// Switches and cmdlet verbs of the tools the app runs that change something (bcdedit /set, powercfg /setactive,
    /// dism /Enable-Feature, PowerShell Set-, Remove-, Disable-MMAgent, net stop). Reads use /enum, /query, /Get-, Get-.
    /// </summary>
    private static readonly string[] WriteMarkers =
    [
        "/set", "/delete", "/import", "/export", "/create", "/copy", "/change", "/hibernate", "/duplicatescheme", "/apply",
        "/enable-", "/disable-", "/add-", "/remove-", "/cleanup-image", "/restorehealth", "/scannow",
        "set-", "remove-", "add-", "enable-", "disable-", "delete-", "new-", "stop-", "start-", "restart-", "register-",
        "unregister-", "install-", "uninstall-", "clear-", "reset-", "rename-", "move-", "copy-", "invoke-",
        " stop ", " start ", " config ", " resize ", " add ", " delete ",
        // The tools also take "-" for "/" (powercfg -setactive, bcdedit -set).
        " -set", " -delete", " -import", " -export", " -create", " -copy", " -change", " -hibernate", " -duplicatescheme",
        // Quick fixes and other tools the app runs: ipconfig /release, netsh ... reset, reagentc /enable, w32tm /resync,
        // lodctr /r, winget install and upgrade, fsutil ... set.
        "/release", "/renew", "/flushdns", "/registerdns", " reset", "/enable", "/disable", "/resync", "lodctr",
        " install ", " upgrade ", " uninstall ", " import ", " set ",
    ];

    public static bool LooksLikeAWrite(string file, string arguments)
    {
        var text = $" {Path.GetFileName(file)} {Decoded(arguments)} ".ToLowerInvariant();
        return WriteMarkers.Any(text.Contains);
    }

    /// <summary>The arguments with an -EncodedCommand script decoded (UTF-16, Base64), so its cmdlets are checked too.</summary>
    private static string Decoded(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase) || p.Equals("-enc", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= parts.Length) return arguments;
        try
        {
            return arguments + " " + Encoding.Unicode.GetString(Convert.FromBase64String(parts[i + 1]));
        }
        catch (FormatException)
        {
            return arguments + " invoke-unreadable";
        }
    }

    public (int ExitCode, string Output) Run(string file, string arguments, TimeSpan? timeout = null) =>
        LooksLikeAWrite(file, arguments)
            ? throw new InvalidOperationException($"read-only test context: {file} {arguments} looks like a change to this PC")
            : real.Run(file, arguments, timeout);
}
