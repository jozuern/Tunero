using Microsoft.Win32;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;

namespace Optimizer.Core.Tests;

/// <summary>Registry sandbox under HKCU\Software\TuneroTest\&lt;guid&gt; with HKLM and HKU subtrees. Deleted on dispose.</summary>
internal sealed class SandboxRegistry : IRegistryRoots, IDisposable
{
    private readonly string _root = $@"Software\TuneroTest\{Guid.NewGuid():N}";

    private int _holds;

    /// <summary>How often the Default profile was "loaded" (a hold while none was open).</summary>
    public int DefaultProfileLoads { get; private set; }

    public RegistryKey? Open(Hive hive, string path, bool writable, bool create = false)
    {
        // As on a real PC: the Default profile is there only inside a hold.
        if (hive == Hive.DefaultUser && _holds == 0) throw new InvalidOperationException("The Default profile is opened outside a hold.");
        var full = $@"{_root}\{hive switch { Hive.Machine => "HKLM", Hive.User => "HKU", _ => "HKDEF" }}\{path}";
        return create ? Registry.CurrentUser.CreateSubKey(full, writable) : Registry.CurrentUser.OpenSubKey(full, writable);
    }

    public string DisplayRoot(Hive hive) => hive switch { Hive.Machine => "HKLM", Hive.User => "HKU\\TEST", _ => @"Default\NTUSER.DAT" };

    /// <summary>Set to make loading the Default profile fail with this message (regedit has it loaded, the file is missing).</summary>
    public string? LoadFails { get; set; }

    public IDisposable? Hold(Hive hive)
    {
        if (hive != Hive.DefaultUser) return null;
        if (_holds == 0 && LoadFails is { } error) throw new InvalidOperationException(error);
        if (_holds++ == 0) DefaultProfileLoads++;
        return new Release(this);
    }

    private sealed class Release(SandboxRegistry owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._holds--;
        }
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
}

internal sealed class FakeServices : IServiceManager
{
    public Dictionary<string, ServiceStart> Start { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailOnWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServiceStart? GetStartType(string name) => Start.TryGetValue(name, out var s) ? s : null;

    public Dictionary<string, bool> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool? IsRunning(string name) => Running.TryGetValue(name, out var r) ? r : null;

    public void SetStartType(string name, ServiceStart start)
    {
        if (FailOnWrite.Contains(name)) throw new InvalidOperationException($"access denied: {name}");
        Start[name] = start;
    }
}

internal sealed class FakePower : IPowerManager
{
    public static readonly Guid Balanced = PowerAliases.Resolve("balanced");
    public static readonly Guid High = PowerAliases.Resolve("highPerformance");

    public Guid Active { get; set; } = Balanced;
    public Dictionary<Guid, string> SchemeNames { get; } = new() { [Balanced] = "Balanced", [High] = "High performance" };
    public Dictionary<(Guid, Guid, Guid), uint> Ac { get; } = [];
    public Dictionary<(Guid, Guid, Guid), uint> Dc { get; } = [];
    public List<string> Exports { get; } = [];

    public Guid ActiveScheme() => Active;
    public uint? ReadAc(Guid s, Guid g, Guid k) => SchemeNames.ContainsKey(s) ? Ac.GetValueOrDefault((s, g, k), 50u) : null;
    public uint? ReadDc(Guid s, Guid g, Guid k) => SchemeNames.ContainsKey(s) ? Dc.GetValueOrDefault((s, g, k), 50u) : null;
    public void WriteAc(Guid s, Guid g, Guid k, uint v) => Ac[(s, g, k)] = v;
    public void WriteDc(Guid s, Guid g, Guid k, uint v) => Dc[(s, g, k)] = v;
    public void SetActive(Guid s) => Active = s;
    public bool SchemeExists(Guid s) => SchemeNames.ContainsKey(s);

    public Guid Duplicate(Guid source, string name)
    {
        var id = Guid.NewGuid();
        SchemeNames[id] = name;
        return id;
    }

    public void Delete(Guid s) => SchemeNames.Remove(s);
    public IReadOnlyList<(Guid Id, string Name)> Schemes() => SchemeNames.Select(kv => (kv.Key, kv.Value)).ToList();
    public void Export(Guid scheme, string file) => Exports.Add(file);

    public bool? Hibernation { get; set; } = true;
    public bool? HibernationSupported() => Hibernation;
}

internal sealed class FakeBcd : IBcdStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Exports { get; } = [];
    public IReadOnlyDictionary<string, string> CurrentValues() => Values;
    public void Set(string element, string value) => Values[element] = value;
    public void Delete(string element) => Values.Remove(element);
    public void Export(string file) => Exports.Add(file);
}

internal sealed class FakeTasks : ITaskScheduler
{
    public Dictionary<string, bool> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool? IsEnabled(string path) => Enabled.TryGetValue(path, out var e) ? e : null;
    public void SetEnabled(string path, bool enabled) => Enabled[path] = enabled;

    public List<ScheduledTaskInfo> Listed { get; } = [];
    public IReadOnlyList<ScheduledTaskInfo> List() => Listed.Select(t => t with { Enabled = Enabled.GetValueOrDefault(t.Path, t.Enabled) }).ToList();

    /// <summary>Registered tasks by path, as their exported XML.</summary>
    public Dictionary<string, string> Xml { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ExportXml(string path) => Xml.GetValueOrDefault(path);

    public void Delete(string path)
    {
        if (!Xml.Remove(path)) throw new InvalidOperationException($"no task {path}");
        Enabled.Remove(path);
    }

    public void Register(string path, string xml) => Xml[path] = xml;
}

internal sealed class FakeDisplays : IDisplayManager
{
    public Dictionary<string, int> Refresh { get; } = [];
    public Dictionary<string, (int Width, int Height)> Size { get; } = [];
    public int? CurrentRefresh(string gdi) => Refresh.TryGetValue(gdi, out var r) ? r : null;

    public void SetMode(string gdi, int w, int h, int hz)
    {
        Size[gdi] = (w, h);
        Refresh[gdi] = hz;
    }

    public void SetRefresh(string gdi, int hz) => Refresh[gdi] = hz;
}

internal sealed class FakeProcesses : IProcessRunner
{
    public List<string> Calls { get; } = [];
    public bool MemoryCompression { get; set; } = true;

    /// <summary>Like real Windows: Enable/Disable-MMAgent only change the setting for the next start (see <see cref="Restart"/>).</summary>
    public bool MemoryCompressionAfterRestart { get; set; }

    private bool? _pendingMemoryCompression;

    /// <summary>Optional responder for other commands (dism, net, powershell scripts).</summary>
    public Func<string, string, (int, string)?>? Handler { get; set; }

    public (int ExitCode, string Output) Run(string file, string arguments, TimeSpan? timeout = null)
    {
        Calls.Add($"{file} {arguments}");
        if (Handler?.Invoke(file, arguments) is { } handled) return handled;
        if (arguments.Contains("(Get-MMAgent)")) return (0, MemoryCompression ? "True" : "False");
        if (arguments.Contains("Disable-MMAgent")) SetMemoryCompression(false);
        if (arguments.Contains("Enable-MMAgent")) SetMemoryCompression(true);
        return (0, "");
    }

    private void SetMemoryCompression(bool on)
    {
        if (MemoryCompressionAfterRestart) _pendingMemoryCompression = on;
        else MemoryCompression = on;
    }

    /// <summary>Simulated restart: pending settings take effect.</summary>
    public void Restart()
    {
        if (_pendingMemoryCompression is { } on) MemoryCompression = on;
        _pendingMemoryCompression = null;
    }
}

internal sealed class FakePowerMode : IPowerModeManager
{
    public Guid? Current { get; set; } = Guid.Empty;
    public Guid? Read() => Current;
    public void Write(Guid overlay) => Current = overlay;
    public bool? Battery { get; set; } = false;
    public bool? OnBattery() => Battery;
}

internal sealed class FakeDevices : IDeviceManager
{
    public List<string> Restarts { get; } = [];
    public bool Fail { get; set; }

    public void Restart(string id)
    {
        if (Fail) throw new InvalidOperationException("device busy");
        Restarts.Add(id);
    }
}

internal sealed class FakeNetwork(SandboxRegistry registry) : INetworkManager
{
    public void SetDns(string interfaceGuid, string[]? servers) =>
        RegistryValue.Write(registry, Hive.Machine, $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceGuid}", "NameServer", "string",
            servers is null ? "" : string.Join(",", servers));

    /// <summary>The DoH servers Windows knows; null = the list cannot be read.</summary>
    public Dictionary<string, bool>? Doh { get; set; } = [];

    /// <summary>How often the DoH list was read (the scan shares one read through <see cref="ReadCache"/>).</summary>
    public int DohReads { get; private set; }

    public IReadOnlyDictionary<string, bool>? DohServers()
    {
        DohReads++;
        return Doh is null ? null : new Dictionary<string, bool>(Doh);
    }
}

/// <summary>Reads the memory compression state the fake MMAgent cmdlets change (<see cref="FakeProcesses"/>).</summary>
internal sealed class FakeMemoryAgent(FakeProcesses processes) : IMemoryAgent
{
    public bool? MemoryCompression() => processes.MemoryCompression;
}

internal sealed class FakeNvidia : INvidiaSettings
{
    public bool Available { get; set; } = true;
    public Dictionary<(string, uint), uint> Values { get; } = [];
    public uint? ReadOwn(string profile, uint id) => Values.TryGetValue((profile, id), out var v) ? v : null;

    public void Write(string profile, uint id, uint? value)
    {
        if (value is { } v) Values[(profile, id)] = v;
        else Values.Remove((profile, id));
    }
}

internal sealed class FakeAccessibility : IAccessibilitySettings
{
    /// <summary>Windows defaults: Sticky Keys 510, Filter Keys 126, Toggle Keys 62 (shortcut bit 0x4 set in all three).</summary>
    public Dictionary<AccessibilityFeature, uint> Flags { get; } = new()
    {
        [AccessibilityFeature.StickyKeys] = 510, [AccessibilityFeature.FilterKeys] = 126, [AccessibilityFeature.ToggleKeys] = 62,
    };

    /// <summary>The app runs as another account than the session user: nothing can be read.</summary>
    public bool Unavailable { get; set; }

    public uint? GetFlags(AccessibilityFeature feature) => Unavailable ? null : Flags[feature];
    public void SetFlags(AccessibilityFeature feature, uint flags) => Flags[feature] = flags;
}

internal sealed class FakeSystemParameters : ISystemParameters
{
    /// <summary>Windows defaults: both 400 ms.</summary>
    public Dictionary<SystemParameter, uint> Values { get; } = new() { [SystemParameter.MenuShowDelay] = 400, [SystemParameter.MouseHoverTime] = 400 };

    /// <summary>The app runs as another account than the session user: nothing can be read.</summary>
    public bool Unavailable { get; set; }

    public uint? Get(SystemParameter parameter) => Unavailable ? null : Values[parameter];
    public void Set(SystemParameter parameter, uint value) => Values[parameter] = value;
}

internal sealed class FakeRestorePoints : IRestorePoints
{
    public bool? Enabled { get; set; } = true;
    public int Created { get; private set; }
    public bool? IsEnabled() => Enabled;
    public void Enable(int maxPercent = 5) => Enabled = true;

    /// <summary>System Restore is on, but creating the point fails (disk full, the service refuses).</summary>
    public bool CreateFails { get; set; }

    public Task<bool> CreateAsync(string description)
    {
        Created++;
        return Task.FromResult(Enabled == true && !CreateFails);
    }
}

/// <summary>Everything an engine test needs, disposed together.</summary>
internal sealed class EngineFixture : IDisposable
{
    public SandboxRegistry Registry { get; } = new();
    public FakeServices Services { get; } = new();
    public FakePower Power { get; } = new();
    public FakeBcd Bcd { get; } = new();
    public FakeTasks Tasks { get; } = new();
    public FakeDisplays Displays { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakeRestorePoints RestorePoints { get; } = new();
    public FakePowerMode PowerMode { get; } = new();
    public FakeDevices Devices { get; } = new();
    public FakeNvidia Nvidia { get; } = new();
    public FakeAccessibility Accessibility { get; } = new();
    public FakeSystemParameters SystemParameters { get; } = new();
    public FakeNetwork Network { get; }
    public string BackupRoot { get; } = Path.Combine(Path.GetTempPath(), "pco-test-" + Guid.NewGuid().ToString("N"));
    public ActionContext Context { get; }
    public BackupStore Store { get; }
    public Tweaks.TweakEngine Engine { get; }

    public EngineFixture(params string[] nics)
    {
        Network = new FakeNetwork(Registry);
        Context = new ActionContext
        {
            Registry = Registry,
            Services = Services,
            Power = Power,
            Bcd = Bcd,
            Tasks = Tasks,
            Displays = Displays,
            Processes = Processes,
            PowerMode = PowerMode,
            Devices = Devices,
            Network = Network,
            MemoryAgent = new FakeMemoryAgent(Processes),
            Nvidia = Nvidia,
            Accessibility = Accessibility,
            SystemParameters = SystemParameters,
            ExportFolder = BackupRoot,
            NetworkInterfaceIds = nics,
        };
        Store = new BackupStore(BackupRoot, secure: false);
        Engine = new Tweaks.TweakEngine(Context, Store, RestorePoints, "test", 26300);
    }

    public void Dispose()
    {
        Registry.Dispose();
        TestFolders.Delete(BackupRoot);
    }
}

/// <summary>The command-line tools the actions call (DISM, powercfg, the DoH cmdlet), as the real ones answer.</summary>
internal sealed class FakeSystemTools(EngineFixture fx)
{
    private bool _reserved = true;
    private readonly Dictionary<string, string> _features = new(StringComparer.OrdinalIgnoreCase) { ["Microsoft-Hyper-V-All"] = "Disabled" };
    private readonly Dictionary<string, string> _capabilities = new(StringComparer.OrdinalIgnoreCase) { ["Media.WindowsMediaPlayer~~~~0.0.12.0"] = "Installed" };

    /// <summary>Reserved storage as DISM reports it (on, as on a new installation).</summary>
    public bool ReservedStorage { get => _reserved; set => _reserved = value; }

    public (int, string)? Run(string file, string args)
    {
        string? Arg(string name) => args.Split(' ').FirstOrDefault(a => a.StartsWith(name, StringComparison.OrdinalIgnoreCase))?[name.Length..];
        if (file == "dism.exe" && args.Contains("/Get-ReservedStorageState")) return (0, $"Reserved storage is {(_reserved ? "enabled" : "disabled")}.");
        if (file == "dism.exe" && args.Contains("/Set-ReservedStorageState"))
        {
            _reserved = args.Contains("/State:Enabled");
            return (0, "");
        }
        if (file == "dism.exe" && Arg("/FeatureName:") is { } feature)
        {
            if (args.Contains("/Get-FeatureInfo")) return _features.TryGetValue(feature, out var s) ? (0, $"Feature Name : {feature}\nState : {s}\n") : (1, "");
            _features[feature] = args.Contains("/Enable-Feature") ? "Enabled" : "Disabled";
            return (3010, "");
        }
        if (file == "dism.exe" && Arg("/CapabilityName:") is { } capability)
        {
            if (args.Contains("/Get-CapabilityInfo")) return _capabilities.TryGetValue(capability, out var s) ? (0, $"Capability Identity : {capability}\nState : {s}\n") : (1, "");
            _capabilities[capability] = args.Contains("/Add-Capability") ? "Installed" : "Not Present";
            return (0, "");
        }
        if (file == "powercfg.exe" && args.StartsWith("/hibernate", StringComparison.Ordinal))
        {
            RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", "dword", args.EndsWith("on", StringComparison.Ordinal) ? "1" : "0");
            return (0, "");
        }
        if (args.Contains("Set-DnsClientDohServerAddress"))
        {
            var server = System.Text.RegularExpressions.Regex.Match(args, "-ServerAddress '([^']+)'").Groups[1].Value;
            fx.Network.Doh![server] = args.Contains("-AutoUpgrade $true");
            return (0, "");
        }
        return null;
    }
}
