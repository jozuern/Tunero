using Optimizer.Core.Catalog;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Findings;

/// <summary>Runs all read-only checks. A failing check yields an Unknown result instead of breaking the scan.</summary>
/// <param name="bcd">Reads the BCD elements of {current} (bcdedit needs admin rights); null or a null result = unknown.</param>
/// <param name="today">Today's date for the age checks (BIOS, graphics driver); null: the PC's clock. Tests fix it.</param>
public sealed class FindingEngine(CatalogData catalog, Actions.IRegistryRoots? registry = null, Func<IReadOnlyDictionary<string, string>?>? bcd = null,
    Func<DateTime>? today = null)
{
    /// <summary>All checks; <paramref name="registry"/> gives access to the session user's hive (F4).</summary>
    /// <param name="today">Today's date for the BIOS and graphics driver age; null: the PC's clock.</param>
    public static IReadOnlyList<IFindingCheck> CreateChecks(Actions.IRegistryRoots? registry, Func<IReadOnlyDictionary<string, string>?>? bcd = null,
        Func<DateTime>? today = null) =>
    [
        new RefreshRateCheck(),
        new EdidRefreshCheck(),
        new VrrCheck(),
        new NvidiaGlobalCheck(),
        new PowerModeCheck(),
        new MemoryIntegrityCheck(),
        new OverlaysCheck(),
        new AmdChipsetCheck(),
        new SecureBootCertsCheck(),
        new EthernetSpeedCheck(),
        new WifiBandCheck(),
        new NvmeLinkCheck(),
        new LaptopPanelCheck(),
        new ApoCheck(),
        new AmdFtpmCheck(),
        new RyzenMemoryCheck(),
        new BiosAgeCheck(today),
        new IgpuUnusedCheck(),
        new SystemOnHddCheck(),
        new AmdAdrenalinCheck(),
        new StartupCountCheck(),
        new ThrottleCheck(),
        new DiskHealthCheck(),
        new BackgroundCpuCheck(),
        new GpuPreferenceCheck(registry),
        new LeftoverCheck(bcd),
        new MonitorOnIgpuCheck(),
        new BasicDisplayAdapterCheck(),
        new TurboDisabledCheck(),
        new X3dCheck(),
        new PowerSaverCheck(),
        new EnergySaverCheck(),
        new LowRamCheck(),
        new GamesOnHddCheck(),
        new GpuDriverAgeCheck(today),
        new LowDiskSpaceCheck(),
        new TrimCheck(),
        new XmpCheck(),
        new DualChannelCheck(),
        new MixedMemoryCheck(),
        new VirtualizationCheck(),
        new UnexpectedRestartCheck(),
        new RestartPendingCheck(registry),
        new RebarCheck(),
        new PcieLinkCheck(),
        new MicrocodeCheck(),
        new OnBatteryCheck(),
        new BatteryWearCheck(),
        new GameAccessCheck(),
    ];

    public IReadOnlyList<Finding> Evaluate(HardwareProfile profile) => Evaluate(profile, CreateChecks(registry, bcd, today), catalog);

    /// <summary>
    /// Runs <paramref name="checks"/>. A check that throws contributes exactly one Unknown of its own kind, never the
    /// results it yielded before the exception.
    /// </summary>
    public static IReadOnlyList<Finding> Evaluate(HardwareProfile profile, IEnumerable<IFindingCheck> checks, CatalogData catalog)
    {
        var results = new List<Finding>();
        var virtualMachine = profile.System?.IsVirtualMachine == true;
        foreach (var check in checks)
        {
            if (virtualMachine && check is IPhysicalHardwareCheck) continue;
            try
            {
                results.AddRange(check.Evaluate(profile, catalog).ToList());
            }
            catch (Exception ex)
            {
                Log.Error("findings", $"{check.GetType().Name} failed", ex);
                results.Add(new Finding { Id = check.DocIds.FirstOrDefault() ?? check.GetType().Name, Kind = check.Kind, Status = FindingStatus.Unknown });
            }
        }
        return Sort(results);
    }

    /// <summary>Problems first (critical first, then by ⚡), then Unknown, Info, Unsupported, OK.</summary>
    public static IReadOnlyList<Finding> Sort(IEnumerable<Finding> findings) =>
        findings
            .OrderBy(f => f.Status switch
            {
                FindingStatus.Problem => 0,
                FindingStatus.Unknown => 1,
                FindingStatus.Info => 2,
                FindingStatus.Unsupported => 3,
                _ => 4,
            })
            .ThenByDescending(f => f.Critical)
            .ThenByDescending(f => f.Impact ?? -1)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Gaming Readiness score: computed only from findings, weighted by ⚡. Tweaks never add points, and game access and
/// security prerequisites (findings whose only effect is a prerequisite for anti-cheats) never count.
/// </summary>
public static class ReadinessScore
{
    public static int Weight(int impact) => impact switch { >= 5 => 20, 4 => 12, 3 => 7, 2 => 3, 1 => 1, _ => 0 };

    public static int Compute(IEnumerable<Finding> findings)
    {
        var penalty = findings
            .Where(f => f.Kind != FindingKind.GameAccess && f.Status == FindingStatus.Problem)
            .Where(f => !(f.Effects.Count > 0 && f.Effects.All(e => e == Effect.Prerequisite)))
            .Sum(f => f.Critical ? 20 : Weight(f.Impact ?? 0));
        return Math.Clamp(100 - penalty, 0, 100);
    }
}
