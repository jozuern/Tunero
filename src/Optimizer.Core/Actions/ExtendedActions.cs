using System.Globalization;
using System.Management;
using Optimizer.Core.Hardware;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Actions;

/// <summary>
/// One NVIDIA driver (DRS) DWORD setting on the global profile or a game's profile. Value null = remove the profile's own
/// value so the driver default applies. Only documented setting IDs (NvApiDriverSettings.h) are used.
/// </summary>
public sealed class NvidiaDrsAction : TweakAction
{
    /// <summary>"global" or the full path of a game executable.</summary>
    public string Profile { get; init; } = "global";

    public uint SettingId { get; init; }
    public uint? Value { get; init; }

    public override string TargetKey => $"drs:{Profile}:{SettingId:X8}".ToLowerInvariant();

    public override TargetText TargetLabel(ActionContext c) => Profile == "global"
        ? TargetText.Of("target.nvidiaGlobal", $"0x{SettingId:X8}")
        : TargetText.Of("target.nvidiaProfile", Path.GetFileName(Profile), $"0x{SettingId:X8}");

    public override StoredValue Desired(ActionContext c) => Value is { } v ? Stored(v) : StoredValue.Missing;

    public override EarlyRead EarlyRead => EarlyRead.AfterScan;

    public override StoredValue? Read(ActionContext c)
    {
        if (!c.Nvidia.Available) return null;
        return c.Nvidia.ReadOwn(Profile, SettingId) is { } v ? Stored(v) : StoredValue.Missing;
    }

    public override void Apply(ActionContext c) => c.Nvidia.Write(Profile, SettingId, Value);

    /// <summary>
    /// Without the NVIDIA driver (uninstalled, another graphics card) there is nothing to restore. With the driver installed
    /// but not answering (a driver update or a reset in progress) the undo fails, so the backup and its original stay.
    /// </summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (!c.Nvidia.Available)
        {
            if (c.Nvidia.DriverInstalled) throw new InvalidOperationException("The NVIDIA driver settings do not answer right now (a driver update or a restart may be pending). Undo again later.");
            return;
        }
        c.Nvidia.Write(Profile, SettingId, original.Existed && uint.TryParse(original.Data, CultureInfo.InvariantCulture, out var v) ? v : null);
    }

    private static StoredValue Stored(uint v) => new(true, "drs", v.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// Windows power mode (Settings > System > Power > Power mode). Windows keeps one mode for mains and one for battery
/// and the API reads and writes the one of the current power source. The engine fills in <see cref="Source"/> when it
/// expands the tweak, so a change made on mains is undone on mains: on battery the undo waits instead of writing the
/// battery mode or taking the other mode for a reset by Windows.
/// </summary>
public sealed class PowerModeAction : TweakAction
{
    /// <summary>Overlay GUID; Guid.Empty = Balanced.</summary>
    public Guid Overlay { get; init; }

    /// <summary>"ac" or "dc" after expansion; null in the catalog and in backups made before the source was recorded.</summary>
    public string? Source { get; init; }

    /// <summary>The catalog's power mode goes to the source the PC runs on now (unchanged when Windows does not say).</summary>
    public override IEnumerable<TweakAction> Expand(ExpansionContext x) => [Source is null && x.PowerSource is { } source ? For(source) : this];

    public PowerModeAction For(string source) => new() { Overlay = Overlay, Source = source };

    public override string TargetKey => Source is null ? "pwr:overlay" : $"pwr:overlay:{Source}";
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of(Source switch
    {
        "ac" => "target.powerModeAc",
        "dc" => "target.powerModeDc",
        _ => "target.powerMode",
    });

    public override StoredValue Desired(ActionContext c) => new(true, "overlay", Overlay.ToString());

    private bool OnOtherSource(ActionContext c) => Source is not null && CurrentSource(c) is { } now && now != Source;

    public static string? CurrentSource(ActionContext c) => c.PowerMode.OnBattery() switch { true => "dc", false => "ac", null => null };

    public override StoredValue? Read(ActionContext c) =>
        !OnOtherSource(c) && c.PowerMode.Read() is { } g ? new StoredValue(true, "overlay", g.ToString()) : null;

    public override void Apply(ActionContext c) => c.PowerMode.Write(Overlay);

    public override bool IsStillApplied(ActionContext c, StoredValue applied) => OnOtherSource(c) || base.IsStillApplied(c, applied);

    public override void Restore(ActionContext c, StoredValue original)
    {
        if (OnOtherSource(c))
            throw new InvalidOperationException(Source == "ac"
                ? "The power mode was changed while plugged in: connect the charger and undo again."
                : "The power mode was changed on battery: unplug the charger and undo again.");
        if (Guid.TryParse(original.Data, out var g)) c.PowerMode.Write(g);
    }
}

/// <summary>
/// Network adapter advanced properties (standardized "*" keywords or vendor keywords). In the catalog this is a
/// template; the engine expands it into one action per physical adapter (<see cref="NicAdapters"/>). Only keywords the
/// adapter's driver declares under Ndi\params, with the wanted value in its enum list, are touched; an adapter without
/// any of them is Unsupported. One adapter restart per action, so the driver reads the new values.
/// </summary>
public sealed class NicPropertyAction : TweakAction
{
    /// <summary>The template stands for every physical adapter of its medium (or the one interface it names).</summary>
    public override IEnumerable<TweakAction> Expand(ExpansionContext x) => Adapter is not null ? [this]
        : x.Adapters
            .Where(a => Media switch { "ethernet" => a.IsEthernet, "wifi" => a.IsWifi, _ => true })
            .Where(a => InterfaceGuid is not { } only || string.Equals(only, a.InterfaceGuid, StringComparison.OrdinalIgnoreCase))
            .Select(a => new NicPropertyAction { Properties = Properties, Dwords = Dwords, Media = Media, InterfaceGuid = InterfaceGuid, Adapter = a });

    // Driver keywords are case-insensitive. The setters keep that for dictionaries read back from a backup, whose JSON
    // reader builds case-sensitive ones: otherwise the stored action of an undo would name a different target.
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _dwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keyword -> value (string, as the driver stores it).</summary>
    public Dictionary<string, string> Properties { get => _properties; init => _properties = new(value, StringComparer.OrdinalIgnoreCase); }

    /// <summary>DWORD values written directly in the class key (e.g. PnPCapabilities), not driver keywords.</summary>
    public Dictionary<string, uint> Dwords { get => _dwords; init => _dwords = new(value, StringComparer.OrdinalIgnoreCase); }

    /// <summary>Only Ethernet / only Wi-Fi / both: "ethernet", "wifi", "any".</summary>
    public string Media { get; init; } = "any";

    /// <summary>Optional: limit the expansion to one adapter (interface GUID), for fixes built for one adapter.</summary>
    public string? InterfaceGuid { get; init; }

    /// <summary>Set by the expansion.</summary>
    public NicAdapter? Adapter { get; init; }

    public override string TargetKey => $"nic:{Adapter?.ClassKey}:{string.Join(",", Keys())}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => Adapter?.Description is { } name
        ? TargetText.Of("target.nicProperties", name, string.Join(", ", Keys()))
        : TargetText.Of("target.nicPropertiesUnknown", string.Join(", ", Keys()));

    private IEnumerable<string> Keys() => Applicable(null).Select(p => p.Key).Concat(Dwords.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    /// <summary>Keywords this adapter's driver supports with the wanted value.</summary>
    private IEnumerable<KeyValuePair<string, string>> Applicable(ActionContext? c) =>
        Adapter is null ? [] : Properties.Where(p => AllowedValues(p.Key) is { } values && values.Contains(p.Value, StringComparer.OrdinalIgnoreCase));

    private IReadOnlyList<string>? AllowedValues(string keyword) =>
        Adapter!.Allowed.TryGetValue(keyword, out var values) ? values
        : Adapter.Allowed.FirstOrDefault(a => a.Key.Equals(keyword, StringComparison.OrdinalIgnoreCase)).Value;

    public override StoredValue Desired(ActionContext c)
    {
        var map = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Applicable(c)) map[p.Key] = p.Value;
        foreach (var d in Dwords) map[d.Key] = d.Value.ToString(CultureInfo.InvariantCulture);
        return new StoredValue(true, "nic", Format(map));
    }

    /// <summary>
    /// The class key (...\0000) still belongs to the adapter this action was made for. Windows can give the number of a
    /// removed adapter to a new one: an undo stored for the old adapter must not write its values there.
    /// </summary>
    private bool IsSameAdapter(ActionContext c) =>
        Adapter is not null && (Adapter.InterfaceGuid.Length == 0 ||
            RegistryValue.Read(c.Registry, Hive.Machine, Adapter.ClassKey, "NetCfgInstanceId") is not { Existed: true, Data: { } id } ||
            string.Equals(id.Trim('{', '}'), Adapter.InterfaceGuid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));

    public override StoredValue? Read(ActionContext c)
    {
        if (Adapter is null || (!Applicable(c).Any() && Dwords.Count == 0) || !IsSameAdapter(c)) return null;
        var map = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Applicable(c))
            map[p.Key] = RegistryValue.Read(c.Registry, Hive.Machine, Adapter.ClassKey, p.Key) is { Existed: true } v ? v.Data : null;
        foreach (var d in Dwords)
            map[d.Key] = RegistryValue.Read(c.Registry, Hive.Machine, Adapter.ClassKey, d.Key) is { Existed: true } v ? v.Data : null;
        return new StoredValue(true, "nic", Format(map));
    }

    public override void Apply(ActionContext c)
    {
        foreach (var p in Applicable(c)) WriteKeyword(c, p.Key, p.Value);
        foreach (var d in Dwords) RegistryValue.Write(c.Registry, Hive.Machine, Adapter!.ClassKey, d.Key, "dword", d.Value.ToString(CultureInfo.InvariantCulture));
        RestartAdapter(c);
    }

    /// <summary>
    /// Driver keywords are REG_SZ by the NDIS convention, but some drivers store them as DWORD: an existing value keeps
    /// its type, so neither the change nor its undo turns a DWORD into a string.
    /// </summary>
    private void WriteKeyword(ActionContext c, string key, string value)
    {
        var existing = RegistryValue.Read(c.Registry, Hive.Machine, Adapter!.ClassKey, key);
        var kind = existing is { Existed: true, Kind: "dword" } && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "dword" : "string";
        RegistryValue.Write(c.Registry, Hive.Machine, Adapter.ClassKey, key, kind, value);
    }

    public override void Restore(ActionContext c, StoredValue original)
    {
        var map = original.Data is null ? null : Parse(original.Data);
        if (map is null) return;
        if (!IsSameAdapter(c)) throw new InvalidOperationException($"{Adapter?.Description}: another network adapter uses its settings key now; nothing was restored");
        foreach (var (key, value) in map)
        {
            if (value is null) RegistryValue.Delete(c.Registry, Hive.Machine, Adapter!.ClassKey, key);
            else if (Dwords.ContainsKey(key)) RegistryValue.Write(c.Registry, Hive.Machine, Adapter!.ClassKey, key, "dword", value);
            else WriteKeyword(c, key, value);
        }
        RestartAdapter(c);
    }

    /// <summary>
    /// Each keyword is compared and restored on its own: a keyword the user changed since in Device Manager keeps the
    /// user's value, the others go back.
    /// </summary>
    public override RestoreOutcome RestoreIfUnchanged(ActionContext c, StoredValue original, StoredValue? applied)
    {
        if (applied is null || Adapter is null) return base.RestoreIfUnchanged(c, original, applied);
        if (original.Data is null || applied.Data is null || Read(c) is not { Data: { } now }) return RestoreOutcome.ChangedSince;
        var before = Parse(original.Data);
        var wrote = Parse(applied.Data);
        var current = Parse(now);
        var restore = before.Where(kv => wrote.TryGetValue(kv.Key, out var w) && current.TryGetValue(kv.Key, out var cur) && cur == w).ToList();
        if (restore.Count == 0) return RestoreOutcome.ChangedSince;
        Restore(c, new StoredValue(true, "nic", Format(restore)));
        return restore.Count == before.Count ? RestoreOutcome.Restored : RestoreOutcome.PartlyRestored;
    }

    /// <summary>"Keyword=value, Keyword2=(not set)": readable in the confirmation dialog, parsed back on undo.</summary>
    public const string NotSet = "(not set)";

    public static string Format(IEnumerable<KeyValuePair<string, string?>> map) =>
        string.Join(", ", map.Select(kv => $"{kv.Key}={kv.Value ?? NotSet}"));

    public static Dictionary<string, string?> Parse(string data)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in data.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            var value = part[(i + 1)..];
            map[part[..i]] = value == NotSet ? null : value;
        }
        return map;
    }

    /// <summary>A failed restart does not undo the change: the driver reads the values at the next start (or reboot).</summary>
    private void RestartAdapter(ActionContext c)
    {
        if (Adapter?.DeviceInstanceId is not { Length: > 0 } id) return;
        try
        {
            c.Devices.Restart(id);
        }
        catch (Exception ex)
        {
            Log.Warn("nic", $"restart of {Adapter.Description} failed, values apply after a reboot: {ex.Message}");
        }
    }
}

/// <summary>A physical network adapter's class key with the values its driver allows per keyword.</summary>
public sealed record NicAdapter(string ClassKey, string InterfaceGuid, string Description, string DeviceInstanceId, bool IsWifi,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Allowed, bool IsEthernet = false);

/// <summary>Physical adapters from the network class key, read through <see cref="IRegistryRoots"/> (sandbox-testable).</summary>
public static class NicAdapters
{
    public const string ClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const int NcfPhysical = 0x4;
    private const int IfTypeWifi = 71; // IF_TYPE_IEEE80211
    private const int IfTypeEthernet = 6; // IF_TYPE_ETHERNET_CSMACD; mobile broadband and others have their own types

    /// <param name="includeSynthetic">
    /// Also the synthetic adapter of a Hyper-V guest (VMBUS\): it is the guest's only uplink, so DNS servers and TCP
    /// values belong on it. Never for hardware properties (NicPropertyAction). The host's virtual switch adapters
    /// (ROOT\VMS_MP) stay excluded either way.
    /// </param>
    /// <summary>
    /// The one rule for "a real network card" (findings, fixes, DNS presets and per-adapter values all use it): the
    /// driver marks it physical (NCF_PHYSICAL) and the device is on PCI or USB, or, with <paramref name="includeSynthetic"/>,
    /// the synthetic adapter of a Hyper-V guest (VMBUS\). Virtual switches, VPN and WAN miniports never count.
    /// </summary>
    public static bool IsPhysical(int characteristics, string? deviceInstanceId, bool includeSynthetic = false) =>
        (characteristics & NcfPhysical) != 0 && deviceInstanceId is { } instance &&
        (instance.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
         (includeSynthetic && instance.StartsWith(@"VMBUS\", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Interface ids (GUIDs) of the connected adapters that <see cref="IsPhysical"/> accepts, synthetic guest adapters included.</summary>
    public static IReadOnlyList<string> ConnectedPhysicalInterfaceIds(IRegistryRoots registry)
    {
        var physical = Enumerate(registry, includeSynthetic: true).Select(a => a.InterfaceGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && physical.Contains(n.Id) &&
                        n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                            or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet)
            .Select(n => n.Id)
            .ToList();
    }

    public static IReadOnlyList<NicAdapter> Enumerate(IRegistryRoots registry, bool includeSynthetic = false)
    {
        var list = new List<NicAdapter>();
        using var cls = registry.Open(Hive.Machine, ClassPath, writable: false);
        if (cls is null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsAsciiDigit)))
        {
            using var key = cls.OpenSubKey(sub);
            if (key is null) continue;
            var characteristics = key.GetValue("Characteristics") is int ch ? ch : 0;
            var instance = key.GetValue("DeviceInstanceID") as string ?? "";
            var guid = key.GetValue("NetCfgInstanceId") as string;
            // Physical hardware only: virtual switches, VPN and Hyper-V adapters are never changed.
            if (guid is null || !IsPhysical(characteristics, instance, includeSynthetic)) continue;
            var allowed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            using (var ndi = key.OpenSubKey(@"Ndi\params"))
            {
                foreach (var param in ndi?.GetSubKeyNames() ?? [])
                {
                    using var en = ndi!.OpenSubKey($@"{param}\enum");
                    if (en is not null) allowed[param] = en.GetValueNames();
                }
            }
            var ifType = key.GetValue("*IfType") switch { int t => t, string text when int.TryParse(text, out var t) => t, _ => 0 };
            list.Add(new NicAdapter($@"{ClassPath}\{sub}", guid, key.GetValue("DriverDesc") as string ?? guid, instance, ifType == IfTypeWifi, allowed, ifType == IfTypeEthernet));
        }
        return list;
    }
}

/// <summary>
/// IPv4 DNS servers of one adapter (by interface GUID). Stored value: "dhcp" or a comma-separated list.
/// Read from the TCP/IP interface key (NameServer is empty when the servers come from DHCP).
/// </summary>
public sealed class DnsAction : TweakAction
{
    public string InterfaceGuid { get; init; } = "";

    /// <summary>
    /// "{nic}" sets the servers of every active network interface. "dhcp" leaves out an interface with a fixed IP address
    /// (EnableDHCP 0): no DHCP server hands it DNS servers, so an empty list would leave it without any.
    /// </summary>
    public override IEnumerable<TweakAction> Expand(ExpansionContext x) =>
        InterfaceGuid != "{nic}" ? [this]
        : x.NetworkInterfaces.Where(id => Servers != "dhcp" || !HasFixedAddress(x.Context, id)).Select(id => new DnsAction { InterfaceGuid = id, Servers = Servers });

    private static bool HasFixedAddress(ActionContext c, string interfaceGuid) =>
        RegistryValue.Read(c.Registry, Hive.Machine, $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceGuid}", "EnableDHCP").Data == "0";

    /// <summary>"dhcp" or comma-separated IPv4 addresses.</summary>
    public string Servers { get; init; } = "dhcp";

    public override string TargetKey => $"dns:{InterfaceGuid}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c)
    {
        // The adapter's name, so the confirmation does not show only a GUID; the GUID stays for adapters that look alike.
        var name = NicAdapters.Enumerate(c.Registry, includeSynthetic: true)
            .FirstOrDefault(a => a.InterfaceGuid.Equals(InterfaceGuid, StringComparison.OrdinalIgnoreCase))?.Description;
        return name is null || name.Equals(InterfaceGuid, StringComparison.OrdinalIgnoreCase)
            ? TargetText.Of("target.dnsAdapter", InterfaceGuid)
            : TargetText.Of("target.dnsNamed", name, InterfaceGuid);
    }
    public override StoredValue Desired(ActionContext c) => new(true, "dns", Servers);

    public override StoredValue? Read(ActionContext c)
    {
        var path = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{InterfaceGuid}";
        using (var key = c.Registry.Open(Hive.Machine, path, writable: false))
            if (key is null) return null;
        var v = RegistryValue.Read(c.Registry, Hive.Machine, path, "NameServer");
        var servers = string.Join(",", (v.Data ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new StoredValue(true, "dns", servers.Length == 0 ? "dhcp" : servers);
    }

    public override void Apply(ActionContext c) => c.Network.SetDns(InterfaceGuid, Parse(Servers));

    /// <summary>An adapter that was removed since has nothing to restore.</summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (Read(c) is null) return;
        c.Network.SetDns(InterfaceGuid, Parse(original.Data));
    }

    private static string[]? Parse(string? servers) => servers is null or "dhcp" ? null : servers.Split(',');
}

// ---------------- system interfaces (faked in tests) ----------------

public interface IPowerModeManager
{
    /// <summary>Effective power mode overlay; null when the API is unavailable.</summary>
    Guid? Read();

    void Write(Guid overlay);

    /// <summary>True on battery, false on mains, null when Windows does not know.</summary>
    bool? OnBattery();
}

public interface IDeviceManager
{
    void Restart(string deviceInstanceId);
}

public interface INetworkManager
{
    /// <summary>Null = obtain DNS servers automatically (DHCP).</summary>
    void SetDns(string interfaceGuid, string[]? servers);

    /// <summary>Every DoH server Windows knows, with its AutoUpgrade state; null when the list cannot be read.</summary>
    IReadOnlyDictionary<string, bool>? DohServers();
}

/// <summary>The memory manager's settings (what Get-MMAgent shows).</summary>
public interface IMemoryAgent
{
    /// <summary>Null when the setting cannot be read (it needs administrator rights).</summary>
    bool? MemoryCompression();
}

public interface INvidiaSettings
{
    bool Available { get; }

    /// <summary>The NVIDIA driver is installed (its settings library is in System32), even when it does not answer right now.</summary>
    bool DriverInstalled => Available;

    /// <summary>Value stored in the profile itself; null = not set there (inherited or driver default).</summary>
    uint? ReadOwn(string profile, uint settingId);

    /// <summary>null removes the profile's own value.</summary>
    void Write(string profile, uint settingId, uint? value);
}

// ---------------- real implementations ----------------

public sealed class SystemPowerModeManager : IPowerModeManager
{
    public Guid? Read() => FirmwareExtras.OverlayApiAvailable() ? FirmwareExtras.EffectiveOverlay() : null;
    public void Write(Guid overlay) => FirmwareExtras.SetOverlay(overlay);
    public bool? OnBattery() => Hardware.Probes.PowerProbe.OnBattery();
}

public sealed class SystemDeviceManager : IDeviceManager
{
    public void Restart(string deviceInstanceId)
    {
        if (Hardware.Probes.PciDevice.Restart(deviceInstanceId) is { } error) throw new InvalidOperationException(error);
    }
}

/// <summary>
/// Get-MMAgent is a thin PowerShell wrapper (CDXML) around the static method PS_MMAgent.Get: called directly it answers
/// in milliseconds, where starting PowerShell for it took about half a second (VM, 2026-10-13).
/// </summary>
public sealed class SystemMemoryAgent : IMemoryAgent
{
    internal static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(20);

    public bool? MemoryCompression()
    {
        try
        {
            // A stuck WMI service must not stall the tweak detection of every scan: connect and call have a time limit.
            var scope = new ManagementScope(@"root\Microsoft\Windows\PS_MMAgent", new ConnectionOptions { Timeout = WmiTimeout });
            using var agent = new ManagementClass(scope, new ManagementPath("PS_MMAgent"), null);
            using var result = agent.InvokeMethod("Get", null, new InvokeMethodOptions { Timeout = WmiTimeout });
            return result?["cmdletOutput"] is ManagementBaseObject settings && settings["MemoryCompression"] is bool on ? on : null;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn("mmagent", $"memory compression not readable: {ex.Message}");
            return null;
        }
    }
}

public sealed class SystemNetworkManager : INetworkManager
{
    /// <summary>
    /// MSFT_DNSClientDohServerAddress, the class behind Get-DnsClientDohServerAddress: read directly instead of starting
    /// PowerShell for it (milliseconds instead of about a second).
    /// </summary>
    public IReadOnlyDictionary<string, bool>? DohServers()
    {
        try
        {
            var servers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Wmi.Query("SELECT ServerAddress, AutoUpgrade FROM MSFT_DNSClientDohServerAddress", @"root\StandardCimv2"))
                if (row.Str("ServerAddress") is { Length: > 0 } address) servers[address] = row.Bool("AutoUpgrade") == true;
            return servers;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn("dns", $"DoH servers not readable: {ex.Message}");
            return null;
        }
    }

    public void SetDns(string interfaceGuid, string[]? servers)
    {
        if (!Guid.TryParse(interfaceGuid, out var g)) throw new ArgumentException($"Invalid interface id {interfaceGuid}");
        // Time limits on connect, query and call: a hung WMI service fails the change instead of blocking it for good.
        var timeout = SystemMemoryAgent.WmiTimeout;
        var scope = new ManagementScope(@"root\cimv2", new ConnectionOptions { Timeout = timeout });
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{g:B}'"),
            new System.Management.EnumerationOptions { Timeout = timeout });
        foreach (var o in searcher.Get())
        {
            using var mo = (ManagementObject)o;
            using var input = mo.GetMethodParameters("SetDNSServerSearchOrder");
            input["DNSServerSearchOrder"] = servers;
            using var output = mo.InvokeMethod("SetDNSServerSearchOrder", input, new InvokeMethodOptions { Timeout = timeout });
            var code = Convert.ToUInt32(output?["ReturnValue"], CultureInfo.InvariantCulture);
            // 0 = done, 1 = done but needs a restart (documented return codes of the method).
            if (code is not (0 or 1)) throw new InvalidOperationException($"SetDNSServerSearchOrder returned {code}");
            Log.Info("dns", $"{g:B}: {(servers is null ? "DHCP" : string.Join(", ", servers))}");
            return;
        }
        throw new InvalidOperationException($"Adapter {interfaceGuid} not found");
    }
}

/// <summary>
/// Reads share one DRS session for a short time: opening a session loads the driver's whole settings database, and a
/// detection pass reads dozens of settings (global and per game). A write always uses its own fresh session and drops
/// the shared one, so a read after a change never sees old values; changes made in the NVIDIA Control Panel show up
/// once the shared session expired.
/// </summary>
public sealed class SystemNvidiaSettings : INvidiaSettings
{
    private static readonly TimeSpan ReadSessionLifetime = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private Nvapi.Session? _readSession;
    private DateTime _readSessionOpened;

    public bool Available => Nvapi.Available;

    public bool DriverInstalled => File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));

    public uint? ReadOwn(string profile, uint settingId)
    {
        lock (_gate)
        {
            if (_readSession is null || DateTime.UtcNow - _readSessionOpened > ReadSessionLifetime)
            {
                DropReadSession();
                _readSession = new Nvapi.Session();
                _readSessionOpened = DateTime.UtcNow;
            }
            var s = _readSession;
            var p = profile == "global" ? s.BaseProfile() : s.ProfileForExe(profile, create: false);
            return p == IntPtr.Zero ? null : s.GetOwn(p, settingId);
        }
    }

    private void DropReadSession()
    {
        _readSession?.Dispose();
        _readSession = null;
    }

    public void Write(string profile, uint settingId, uint? value)
    {
        lock (_gate) DropReadSession();
        using var s = new Nvapi.Session();
        var p = profile == "global" ? s.BaseProfile() : s.ProfileForExe(profile, create: value is not null);
        if (p == IntPtr.Zero) return; // resetting a setting on a profile that does not exist: nothing to do
        if (value is { } v)
        {
            s.Set(p, settingId, v);
        }
        else
        {
            s.Delete(p, settingId);
            if (profile != "global") s.DeleteOwnProfileIfEmpty(profile);
        }
        s.Save();
    }
}

/// <summary>
/// The keyboard shortcut of a keyboard accessibility feature, through the documented SystemParametersInfo flag
/// SKF_/FKF_/TKF_HOTKEYACTIVE (0x4): Sticky Keys (Shift five times), Filter Keys (right Shift for eight seconds),
/// Toggle Keys (Num Lock for eight seconds). Only that bit changes; whether the feature itself is on stays as it was.
/// </summary>
public sealed class AccessibilityShortcutAction : TweakAction
{
    private const uint HotkeyActive = 0x4;

    public AccessibilityFeature Feature { get; init; }

    /// <summary>true = the shortcut works, false = it is turned off.</summary>
    public bool Shortcut { get; init; }

    public override string TargetKey => $"spi:{Feature}:hotkey".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.shortcut", Feature.ToString());
    public override StoredValue Desired(ActionContext c) => State(Shortcut);

    private static StoredValue State(bool on) => new(true, "bool", on ? "On" : "Off");

    public override StoredValue? Read(ActionContext c) =>
        c.Accessibility.GetFlags(Feature) is { } flags ? State((flags & HotkeyActive) != 0) : null;

    public override void Apply(ActionContext c) => Write(c, Shortcut);

    public override void Restore(ActionContext c, StoredValue original) => Write(c, original.Data != "Off");

    private void Write(ActionContext c, bool on)
    {
        var flags = c.Accessibility.GetFlags(Feature) ?? throw new InvalidOperationException($"{Feature} settings are not available for the signed-in user");
        c.Accessibility.SetFlags(Feature, on ? flags | HotkeyActive : flags & ~HotkeyActive);
    }
}
