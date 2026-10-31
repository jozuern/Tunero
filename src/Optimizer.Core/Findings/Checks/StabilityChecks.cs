using System.Globalization;
using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;

namespace Optimizer.Core.Findings.Checks;

/// <summary>
/// A.virtualization: hardware virtualization off in the firmware. Memory integrity and virtualization-based security
/// need it, and anti-cheats that can ask for either (catalog: "hvci" or "vbs", required or sometimes) make it a problem.
/// </summary>
public sealed class VirtualizationCheck : IFindingCheck
{
    public const string Id = "A.virtualization";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        var v = p.Extras?.Virtualization;
        var needing = (p.Software?.AntiCheats ?? [])
            .Where(a => c.AntiCheat.AntiCheats.FirstOrDefault(s => s.Id == a.Id) is { } sig &&
                        sig.Required.Concat(sig.Sometimes).Any(part => part is "hvci" or "vbs"))
            .Select(a => a.DisplayName).ToList();
        FindingStatus status;
        string? variant = null;
        if (v is null) status = FindingStatus.Unknown;
        else if (v.Enabled) status = FindingStatus.Ok;
        else if (!v.CpuSupport) status = FindingStatus.Unsupported;
        else if (needing.Count > 0) { status = FindingStatus.Problem; variant = "antiCheat"; }
        else { status = FindingStatus.Info; variant = "off"; }

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Variant = variant,
            Impact = needing.Count > 0 ? 3 : 1,
            Effects = [Effect.Prerequisite],
            Facts = v is null
                ? []
                :
                [
                    new("fact.hypervisor", v.HypervisorPresent ? "@yes" : "@no"),
                    new("fact.virtualizationFirmware", v.Enabled ? "@yes" : v.CpuSupport ? "@no" : "@unsupported"),
                ],
            Params = new Dictionary<string, string>
            {
                ["antiCheats"] = string.Join(", ", needing),
                ["board"] = $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim(),
                ["intel"] = p.Cpu?.Vendor == Vendor.Intel ? "yes" : "",
            },
        };
    }
}

/// <summary>A.mixedRam: memory modules with different part numbers or sizes (not one matched kit).</summary>
public sealed class MixedMemoryCheck : IPhysicalHardwareCheck
{
    public const string Id = "A.mixedRam";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Memory is null)
        {
            if (p.ProbeFailed("Memory")) yield return Finding.NotRead(Id);
            yield break;
        }
        if (p.Memory.Modules.Count < 2) yield break;
        var modules = p.Memory.Modules;
        var parts = modules.Select(m => m.PartNumber.Trim()).ToList();
        var status = parts.Any(string.IsNullOrEmpty) ? FindingStatus.Unknown
            : parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 || modules.Select(m => m.CapacityBytes).Distinct().Count() > 1 ? FindingStatus.Info
            : FindingStatus.Ok;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Impact = 2,
            Effects = [Effect.Stability, Effect.Fps],
            Facts =
            [
                .. modules.Select(m => new Fact("fact.ramModule",
                    $"{m.DeviceLocator}: {m.Manufacturer} {m.PartNumber.Trim()}, {m.CapacityBytes >> 30} GB {m.Type}")),
            ],
        };
    }
}

/// <summary>A.unexpectedRestarts: Kernel-Power event 41 in the last 30 days (crash, power loss or forced power off).</summary>
public sealed class UnexpectedRestartCheck : IFindingCheck
{
    public const string Id = "A.unexpectedRestarts";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        var events = p.Extras?.UnexpectedShutdowns;
        var stop = events?.FirstOrDefault(e => e.BugcheckCode != 0);
        // One shutdown with the power button held down and no Stop error is a user action (a hang ended by hand, a
        // forced power off): information, not a stability problem.
        var oneForcedOff = events is { Count: 1 } && events[0] is { BugcheckCode: 0, PowerButton: true };
        var status = events is null ? FindingStatus.Unknown : events.Count == 0 ? FindingStatus.Ok : oneForcedOff ? FindingStatus.Info : FindingStatus.Problem;
        var last = events?.FirstOrDefault();
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Variant = status is not (FindingStatus.Problem or FindingStatus.Info) ? null : stop is not null ? "stop" : "power",
            Impact = 3,
            Effects = [Effect.Stability],
            Facts = events is null
                ? []
                :
                [
                    new("fact.unexpectedCount", events.Count.ToString(CultureInfo.InvariantCulture)),
                    .. last is null ? Array.Empty<Fact>() : [new Fact("fact.lastUnexpected", last.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))],
                    .. stop is null ? Array.Empty<Fact>() : [new Fact("fact.lastStopCode", StopCode(stop.BugcheckCode))],
                ],
            Params = new Dictionary<string, string>
            {
                ["count"] = events?.Count.ToString(CultureInfo.InvariantCulture) ?? "",
                ["stopCode"] = stop is null ? "" : StopCode(stop.BugcheckCode),
                ["powerButton"] = events?.Any(e => e.PowerButton) == true ? "yes" : "",
            },
        };
    }

    /// <summary>Event 41 stores the bug check code in decimal; documentation lists it as 0x0000009F.</summary>
    public static string StopCode(uint code) => $"0x{code:X8}";
}

/// <summary>
/// A.restartPending: Windows waits for a restart to finish updates or a feature change. Read from the two keys
/// Configuration Manager uses for its "Pending Restart" column (Windows Update's RebootRequired and component based
/// servicing's RebootPending). PendingFileRenameOperations is left out on purpose: programs queue file deletions there
/// all the time, and a restart is not needed for them.
/// </summary>
public sealed class RestartPendingCheck(IRegistryRoots? registry) : IFindingCheck
{
    public const string Id = "A.restartPending";
    public IReadOnlyList<string> DocIds => [Id];

    public const string WindowsUpdateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    public const string ServicingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (registry is null) yield break;
        var updates = Exists(registry, WindowsUpdateKey);
        var servicing = Exists(registry, ServicingKey);
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = updates || servicing ? FindingStatus.Info : FindingStatus.Ok,
            Variant = updates ? "update" : servicing ? "servicing" : null,
            Impact = 1,
            Effects = [Effect.Stability],
            Facts =
            [
                new("fact.restartForUpdates", updates ? "@yes" : "@no"),
                new("fact.restartForServicing", servicing ? "@yes" : "@no"),
            ],
        };
    }

    private static bool Exists(IRegistryRoots registry, string path)
    {
        using var key = registry.Open(Hive.Machine, path, writable: false);
        return key is not null;
    }
}
