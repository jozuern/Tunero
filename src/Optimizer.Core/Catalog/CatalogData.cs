using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Optimizer.Core.Catalog;

/// <summary>Typed access to the embedded catalog JSON files (Catalog/Data/*.json).</summary>
public sealed class CatalogData
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<CatalogData> Lazy = new(Load);
    public static CatalogData Current => Lazy.Value;

    public required GpuCatalog Gpu { get; init; }
    public required CpuCatalog Cpu { get; init; }
    public required RamCatalog Ram { get; init; }
    public required AntiCheatCatalog AntiCheat { get; init; }
    public required BiosCatalog Bios { get; init; }
    public required StorageCatalog Storage { get; init; }
    public required ExtrasCatalog Extras { get; init; }
    public required Debloat.AppxCatalog Appx { get; init; }
    public required Services.ServiceCatalog Services { get; init; }
    public required Apps.AppCatalog Apps { get; init; }
    public required Tools.FeatureCatalog Features { get; init; }
    public required Profiles.ProfileCatalog Profiles { get; init; }
    public required NotIncludedCatalog NotIncluded { get; init; }
    public required Media.MediaImageCatalog MediaImages { get; init; }
    public required IReadOnlyList<Media.MediaPart> MediaParts { get; init; }

    public static CatalogData Load() => new()
    {
        Appx = Read<Debloat.AppxCatalog>("appx.json"),
        Services = Read<Services.ServiceCatalog>("services.json"),
        Apps = Read<Apps.AppCatalog>("apps.json"),
        Features = Read<Tools.FeatureCatalog>("features.json"),
        Profiles = Read<Profiles.ProfileCatalog>("profiles.json"),
        Gpu = Read<GpuCatalog>("gpu.json"),
        Cpu = Read<CpuCatalog>("cpu.json"),
        Ram = Read<RamCatalog>("ram.json"),
        AntiCheat = Read<AntiCheatCatalog>("anticheat.json"),
        Bios = Read<BiosCatalog>("bios.json"),
        Storage = Read<StorageCatalog>("storage.json"),
        Extras = Read<ExtrasCatalog>("extras.json"),
        NotIncluded = Read<NotIncludedCatalog>("notincluded.json"),
        MediaImages = Read<Media.MediaImageCatalog>("mediaimages.json"),
        MediaParts = Read<Media.MediaPartCatalog>("mediaparts.json").Parts,
    };

    public static IEnumerable<string> ResourceNames(string prefix) =>
        typeof(CatalogData).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal));

    public static string ReadResourceText(string name)
    {
        using var stream = typeof(CatalogData).Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static T Read<T>(string file) =>
        JsonSerializer.Deserialize<T>(ReadResourceText("Catalog.Data." + file), Options)
        ?? throw new InvalidDataException($"Catalog file {file} is empty");
}

public sealed class GpuCatalog
{
    public Dictionary<string, string> PciVendors { get; init; } = [];
    public Dictionary<string, string> DiscretePatterns { get; init; } = [];

    /// <summary>Per vendor: graphics the vendor no longer releases drivers for (F15 variant "legacy").</summary>
    public Dictionary<string, string> LegacyPatterns { get; init; } = [];
    public List<string> VirtualPatterns { get; init; } = [];
    public List<string> BasicPatterns { get; init; } = [];
    public List<string> OnCardSwitchVendors { get; init; } = [];
    public List<RebarRule> Rebar { get; init; } = [];
    public List<string> Sources { get; init; } = [];
}

public sealed class RebarRule
{
    public string Vendor { get; init; } = "";
    public string Regex { get; init; } = "";
    public bool Supported { get; init; }
    public bool Critical { get; init; }
}

public sealed class CpuCatalog
{
    public List<MicrocodeRule> MicrocodeRules { get; init; } = [];
    public List<string> X3dMultiCcdModels { get; init; } = [];
    public List<string> X3dSymmetricDualModels { get; init; } = [];
    public List<string> X3dSingleCcdModels { get; init; } = [];
    public double X3dAsymmetryFactor { get; init; } = 2.0;
}

public sealed class MicrocodeRule
{
    public string Id { get; init; } = "";
    public string Vendor { get; init; } = "";
    public int Family { get; init; }
    public List<int> Models { get; init; } = [];
    public string NameRegex { get; init; } = "";
    public string MinRevision { get; init; } = "0x0";
    public bool Critical { get; init; }
    public List<string> Sources { get; init; } = [];

    public uint MinRevisionValue => Convert.ToUInt32(MinRevision, 16);
}

public sealed class RamCatalog
{
    public List<RamDecoder> Decoders { get; init; } = [];
    public List<string> ChannelPatterns { get; init; } = [];
    public double SpeedToleranceFraction { get; init; } = 0.03;

    /// <summary>
    /// Decoded speeds that stand for a JEDEC speed: part numbers round it ("26" or "266" for DDR4-2666, "29" for 2933).
    /// Keys are the decoded value, values the real speed.
    /// </summary>
    public Dictionary<string, int> RatedAliases { get; init; } = [];

    /// <summary>
    /// Intel processors and the DDR5 speed Intel specifies with two modules per channel (four modules), single-rank,
    /// from the processor datasheets (A.xmp "fourDimms" on Intel). Dual-rank modules are specified lower.
    /// </summary>
    public List<FourDimmRule> FourDimmIntel { get; init; } = [];

    /// <summary>Intel platforms whose non-Z chipsets cannot run memory above the processor's specification (A.xmp "intelLocked").</summary>
    public IntelLockedMemory? IntelLocked { get; init; }
}

public sealed class FourDimmRule
{
    public string CpuPattern { get; init; } = "";
    public int Mts { get; init; }
}

/// <summary>
/// Intel 8th to 10th generation desktop processors: the H, B and Q chipsets of the 300 and 400 series do not support
/// memory overclocking (Intel's specification pages: "Supports Memory Overclocking: No"), so memory runs at the
/// processor's specified speed whatever XMP says. Only Z370, Z390 and Z490 boards can use XMP.
/// </summary>
public sealed class IntelLockedMemory
{
    /// <summary>Processor names of these generations (Core i3 to i9, Pentium Gold, Celeron).</summary>
    public List<string> CpuPatterns { get; init; } = [];

    /// <summary>Board names with a chipset that supports memory overclocking (Z370, Z390, Z490).</summary>
    public string UnlockedBoardPattern { get; init; } = "";

    /// <summary>The memory speeds Intel specifies for these processors (DDR4-2400, 2666, 2933).</summary>
    public List<int> SpecSpeeds { get; init; } = [];
}

public sealed class RamDecoder
{
    public string Brand { get; init; } = "";
    public string Regex { get; init; } = "";
    public int Multiplier { get; init; } = 1;
}

public sealed class AntiCheatCatalog
{
    public List<AntiCheatSignature> AntiCheats { get; init; } = [];
}

public sealed class AntiCheatSignature
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<string> Services { get; init; } = [];
    public bool BlocksVbsOff { get; init; }
    public bool Verified { get; init; }

    /// <summary>Platform features the anti-cheat requires: uefi, secureBoot, tpm2, hvci, iommu.</summary>
    public List<string> Required { get; init; } = [];

    /// <summary>Features required only in some modes or rollout waves (see SometimesNoteKey).</summary>
    public List<string> Sometimes { get; init; } = [];

    public string? SometimesNoteKey { get; init; }
    public List<string> Sources { get; init; } = [];
}

public sealed class BiosCatalog
{
    public Dictionary<string, string> VendorAliases { get; init; } = [];
    public List<BiosMenu> Menus { get; init; } = [];

    public string? NormalizeVendor(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        foreach (var (alias, vendor) in VendorAliases)
            if (manufacturer.Contains(alias, StringComparison.OrdinalIgnoreCase)) return vendor;
        return null;
    }

    public BiosMenu? Find(string? vendor, string platform, string setting) =>
        vendor is null ? null :
        Menus.FirstOrDefault(m => m.Vendor == vendor && m.Setting == setting && m.Platform == platform) ??
        Menus.FirstOrDefault(m => m.Vendor == vendor && m.Setting == setting && m.Platform == "*");
}

public sealed class BiosMenu
{
    public string Vendor { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Setting { get; init; } = "";
    public string Path { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public bool Verified { get; init; }
}

public sealed class StorageCatalog
{
    public List<string> SmrModels { get; init; } = [];
    public double LowFreeSpaceFraction { get; init; } = 0.10;
    public List<string> Sources { get; init; } = [];

    public bool IsSmr(string model) => SmrModels.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase));
}

public sealed class ExtrasCatalog
{
    public List<OverlayProcess> OverlayProcesses { get; init; } = [];
    public Dictionary<string, ServiceGroup> ServiceGroups { get; init; } = [];
    public ApoCpus ApoCpus { get; init; } = new();

    public IEnumerable<string> AllServiceNames => ServiceGroups.Values.SelectMany(g => g.Services).Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed class OverlayProcess
{
    public string Process { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class ServiceGroup
{
    public List<string> Services { get; init; } = [];
    public bool Verified { get; init; }
}

public sealed class ApoCpus
{
    public string Regex { get; init; } = "$^";
    public List<string> Sources { get; init; } = [];
}

internal static class RegexCache
{
    private static readonly Dictionary<string, Regex> Cache = [];
    private static readonly Lock Gate = new();

    public static Regex Get(string pattern)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(pattern, out var regex))
                Cache[pattern] = regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            return regex;
        }
    }
}
