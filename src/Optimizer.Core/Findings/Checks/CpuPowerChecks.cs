using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;

namespace Optimizer.Core.Findings.Checks;

/// <summary>Advisor: BIOS microcode below a catalog threshold (Raptor Lake 0x12F, Arrow Lake 0x114). Uses the BIOS-provided revision.</summary>
public sealed class MicrocodeCheck : IPhysicalHardwareCheck
{
    public IReadOnlyList<string> DocIds => CatalogData.Current.Cpu.MicrocodeRules.Select(r => r.Id).ToList();

    public static MicrocodeRule? MatchRule(CpuInfo cpu, CatalogData c) =>
        c.Cpu.MicrocodeRules.FirstOrDefault(r =>
            string.Equals(r.Vendor, cpu.Vendor.ToString(), StringComparison.OrdinalIgnoreCase) &&
            r.Family == cpu.Family && r.Models.Contains(cpu.Model) &&
            RegexCache.Get(r.NameRegex).IsMatch(cpu.Name));

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Cpu is null) yield break;
        var rule = MatchRule(p.Cpu, c);
        if (rule is null) yield break; // CPU not affected by any rule -> nothing to show
        // The BIOS revision matters: an OS-loaded update does not fix the BIOS voltage behavior. Without it, the running
        // revision still proves a problem (an OS update only raises it, so the BIOS revision is not higher), but not a fix.
        var bios = p.Cpu.MicrocodeBios;
        var status = bios is { } b ? b < rule.MinRevisionValue ? FindingStatus.Problem : FindingStatus.Ok
            : p.Cpu.MicrocodeCurrent is { } current && current < rule.MinRevisionValue ? FindingStatus.Problem
            : FindingStatus.Unknown;
        yield return new Finding
        {
            Id = rule.Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Critical = rule.Critical && status == FindingStatus.Problem,
            Impact = rule.Critical ? null : 2,
            Effects = [Effect.Stability],
            Facts =
            [
                new("fact.cpu", p.Cpu.Name),
                new("fact.microcodeBios", bios is null ? "@unknown" : $"0x{bios:X}"),
                new("fact.microcodeCurrent", p.Cpu.MicrocodeCurrent is { } cur ? $"0x{cur:X}" : "@unknown"),
                new("fact.microcodeRequired", $"≥ {rule.MinRevision}"),
                new("fact.bios", $"{p.Firmware?.BiosVersion} ({p.Firmware?.BiosDate:yyyy-MM-dd})"),
            ],
            Params = new Dictionary<string, string>
            {
                ["min"] = rule.MinRevision,
                ["board"] = $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim(),
            },
        };
    }
}

public enum X3dLayout { None, SingleCcd, MultiCcdAsymmetric, DualVCache }

/// <summary>
/// X3D topology: asymmetric L3 sizes (largest ≥ 2 × smallest) = one CCD with V-Cache. Only for processors named X3D:
/// mobile chips with Zen 5 and Zen 5c clusters (Ryzen AI 300: 16 MB and 8 MB of L3) are asymmetric without V-Cache.
/// </summary>
public static class X3d
{
    public static X3dLayout Classify(CpuInfo cpu, CatalogData c)
    {
        if (cpu.Vendor != Vendor.Amd) return X3dLayout.None;
        if (c.Cpu.X3dSymmetricDualModels.Any(m => cpu.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return X3dLayout.DualVCache;
        var sizes = cpu.L3Domains.Select(d => d.SizeBytes).Where(s => s > 0).ToList();
        if (cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase) && sizes.Count >= 2 && sizes.Max() >= c.Cpu.X3dAsymmetryFactor * sizes.Min())
            return X3dLayout.MultiCcdAsymmetric;
        if (c.Cpu.X3dMultiCcdModels.Any(m => cpu.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return X3dLayout.MultiCcdAsymmetric;
        if (c.Cpu.X3dSingleCcdModels.Any(m => cpu.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return X3dLayout.SingleCcd;
        return X3dLayout.None;
    }
}

/// <summary>
/// F10: multi-CCD X3D: the AMD driver parks the cores of the chiplet without V-Cache while a game runs, which needs core
/// parking. Problem when the plan is not Balanced, or Balanced with core parking switched off (CPMINCORES 100 %: "The
/// Core Parking algorithm is disabled if the value of this setting is 100%", variant "parkingOff").
/// </summary>
public sealed class X3dCheck : IFindingCheck
{
    public const string Id = "F10.x3d";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Cpu is null || X3d.Classify(p.Cpu, c) != X3dLayout.MultiCcdAsymmetric) yield break;
        var balanced = p.Power?.Personality == PowerPersonality.Balanced;
        var parkingOff = p.Power?.CoreParkingMinCoresAc == 100;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            // A plan whose type could not be read is not proof of a wrong plan.
            Status = p.Power is null || p.Power.Personality == PowerPersonality.Unknown ? FindingStatus.Unknown
                : balanced && !parkingOff ? FindingStatus.Ok : FindingStatus.Problem,
            Variant = p.Power is not null && balanced && parkingOff ? "parkingOff" : null,
            Impact = 4,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.cpu", p.Cpu.Name),
                new("fact.l3Domains", string.Join(" + ", p.Cpu.L3Domains.Select(d => $"{d.SizeBytes >> 20} MB"))),
                new("fact.powerPlan", p.Power?.ActiveSchemeName ?? "@unknown"),
                new("fact.coreParkingMinCores", p.Power?.CoreParkingMinCoresAc is { } cores ? $"{cores} %" : "@unknown"),
            ],
        };
    }
}

/// <summary>F6: turbo disabled by the power plan (max processor state &lt; 100 % or boost mode off).</summary>
public sealed class TurboDisabledCheck : IFindingCheck
{
    public const string Id = "F6.turbo";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Power is null || p.Power.MaxProcessorStateAc is null)
        {
            yield return new Finding { Id = Id, Kind = FindingKind.Finding, Status = FindingStatus.Unknown, Impact = 5, Effects = [Effect.Fps] };
            yield break;
        }
        var maxState = p.Power.MaxProcessorStateAc.Value;
        var boostOff = p.Power.BoostModeAc == 0;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = maxState < 100 || boostOff ? FindingStatus.Problem : FindingStatus.Ok,
            Impact = 5,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.powerPlan", p.Power.ActiveSchemeName),
                new("fact.maxProcessorState", $"{maxState} %"),
                new("fact.boostMode", p.Power.BoostModeAc switch { 0 => "@off", null => "@unknown", var v => $"@boost{v}" }),
            ],
        };
    }
}

/// <summary>F9: Power saver plan on a desktop.</summary>
public sealed class PowerSaverCheck : IFindingCheck
{
    public const string Id = "F9.powerSaver";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.IsLaptop) yield break;
        if (p.Power is null)
        {
            if (p.ProbeFailed("Power")) yield return Finding.NotRead(Id);
            yield break;
        }
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = p.Power.Personality == PowerPersonality.PowerSaver ? FindingStatus.Problem
                : p.Power.Personality == PowerPersonality.Unknown ? FindingStatus.Unknown : FindingStatus.Ok,
            Impact = 3,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.powerPlan", p.Power.ActiveSchemeName),
                new("fact.powerPersonality", $"@personality{p.Power.Personality}"),
            ],
        };
    }
}

/// <summary>F19: Energy Saver on while plugged in (exists on desktops since 24H2).</summary>
public sealed class EnergySaverCheck : IFindingCheck
{
    public const string Id = "F19.energySaver";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Power is null)
        {
            if (p.ProbeFailed("Power")) yield return Finding.NotRead(Id);
            yield break;
        }
        // On battery, Energy Saver is expected behavior; only report it on laptops known to be on AC power.
        if (p.IsLaptop && p.Power.OnAc != true) yield break;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = p.Power.EnergySaverOn ? FindingStatus.Problem : FindingStatus.Ok,
            Impact = 3,
            Effects = [Effect.Fps],
            Facts =
            [
                new("fact.energySaver", p.Power.EnergySaverOn ? "@on" : "@off"),
                new("fact.acPower", p.Power.OnAc switch { true => "@yes", false => "@no", null => "@unknown" }),
            ],
        };
    }
}
