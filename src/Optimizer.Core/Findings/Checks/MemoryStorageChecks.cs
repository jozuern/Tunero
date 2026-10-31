using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;

namespace Optimizer.Core.Findings.Checks;

public static class RamSpeed
{
    /// <summary>Rated speed decoded from the part number (catalog decoders). Null = unknown part number -> no advice.</summary>
    public static int? DecodeRated(string partNumber, CatalogData c)
    {
        var part = partNumber.Trim().ToUpperInvariant();
        foreach (var d in c.Ram.Decoders)
        {
            var m = RegexCache.Get(d.Regex).Match(part);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v))
                return c.Ram.RatedAliases.TryGetValue((v * d.Multiplier).ToString(System.Globalization.CultureInfo.InvariantCulture), out var real) ? real : v * d.Multiplier;
        }
        return null;
    }

    /// <summary>Lowest JEDEC speed of the type in MT/s (DDR4-2133, DDR5-4800).</summary>
    private static int? JedecMinimum(string type) => type switch { "DDR4" => 2133, "DDR5" => 4800, _ => null };

    /// <summary>
    /// The configured speed in MT/s. Some firmware reports ConfiguredClockSpeed in MHz (half the MT/s rate, DDR4-3200 as
    /// "1600"), but low MT/s values are real too: four DDR5 modules on AM5 run at 3600, four dual-rank DDR4 modules on
    /// Ryzen 1000 at 1866. The module's own speed (Win32_PhysicalMemory.Speed) decides the unit: a value is MHz when the
    /// module speed is below the JEDEC minimum too (the firmware uses MHz for both), or when twice the value still fits
    /// the module speed (a real 3600 MT/s on a DDR5-4800 module does not). Without the module speed, only values no real
    /// configuration uses are doubled, and the range where both readings are plausible stays unknown. Values below the
    /// lowest real speed are doubled in every case (XMP on: DDR4-3200 as 1600 next to a module speed of 2133).
    /// </summary>
    public static int? NormalizeConfigured(int? value, string type, int? moduleSpeed)
    {
        if (value is null or <= 0) return null;
        if (JedecMinimum(type) is not { } min) return value;
        // Below the lowest speed any real configuration runs at, the value is MHz whatever the module reports. With XMP
        // or EXPO on, the module speed is the JEDEC rate, so twice an MHz value (DDR4-3200 as 1600) is above it.
        var lowestReal = type == "DDR5" ? 3600 : 1866;
        if (value < lowestReal) return value * 2;
        if (moduleSpeed is { } speed and > 0)
            return value < min && (speed < min * 0.95 || value * 2 <= speed * 1.05) ? value * 2 : value;
        return value < min ? null : value;
    }

    public static int? NormalizeConfigured(MemoryModule m) => NormalizeConfigured(m.ConfiguredMts, m.Type, m.SpeedMts);

    /// <summary>
    /// The memory channel of a module from its locator. Intel platforms since 11th gen name one channel A per memory
    /// controller ("Controller0-ChannelA-DIMM0", "Controller1-ChannelA-DIMM0"): every group of the matching pattern
    /// counts, so those are channels "0A" and "1A", not one channel A twice.
    /// </summary>
    public static string? Channel(MemoryModule m, CatalogData c)
    {
        var text = $"{m.DeviceLocator} {m.BankLabel}";
        foreach (var pattern in c.Ram.ChannelPatterns)
        {
            var match = RegexCache.Get(pattern).Match(text);
            if (match.Success) return string.Concat(match.Groups.Values.Skip(1).Select(g => g.Value.ToUpperInvariant()));
        }
        return null;
    }

    public static string Platform(CpuInfo? cpu, string ramType) => cpu?.Vendor switch
    {
        Vendor.Intel => "intel",
        Vendor.Amd => ramType == "DDR5" ? "amd-am5" : "amd-am4",
        _ => "*",
    };
}

/// <summary>
/// Advisor: RAM running below its rated speed (XMP / EXPO / D.O.C.P off). Four DDR5 modules on a Ryzen are expected to
/// run slower: AMD specifies DDR5-3600 for four modules (e.g. "4x1R DDR5-3600 4x2R DDR5-3600"), so that is information
/// (variant "fourDimms"), not a missing setting. Intel 8th to 10th generation processors on a board without a Z chipset
/// cannot run memory above the processor's specified speed (<see cref="IntelLockedMemory"/>): memory at exactly that
/// speed is information too (variant "intelLocked"), since XMP cannot change it there.
/// </summary>
public sealed class XmpCheck : IPhysicalHardwareCheck
{
    public const string Id = "A.xmp";

    /// <summary>AMD's specified speed for four DDR5 modules (product pages, "4x1R DDR5-3600").</summary>
    public const int FourDimmDdr5Mts = 3600;

    /// <summary>
    /// Gaming impact of memory below its rating, by processor: TechSpot (2024, 13 games) measured 11 to 12 % on average for
    /// a Ryzen 7 7700X and a Core i7-12700K, but 4 % for a Ryzen 7 7800X3D (the large cache hides memory speed) and a Core
    /// i7-14700K. X3D and Intel 13th and 14th gen rate 1, other processors 3.
    /// </summary>
    public static int Impact(CpuInfo? cpu, CatalogData c)
    {
        if (cpu is null) return 3;
        if (X3d.Classify(cpu, c) != X3dLayout.None) return 1;
        return cpu.Vendor == Vendor.Intel && RegexCache.Get(@"\bi[3579]-1[34]\d{3}").IsMatch(cpu.Name) ? 1 : 3;
    }

    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Memory is null)
        {
            if (p.ProbeFailed("Memory")) yield return Finding.NotRead(Id);
            yield break;
        }
        if (p.Memory.Modules.Count == 0) yield break;
        var first = p.Memory.Modules[0];
        var rated = p.Memory.Modules.Select(m => RamSpeed.DecodeRated(m.PartNumber, c)).ToList();
        var configured = p.Memory.Modules.Select(RamSpeed.NormalizeConfigured).ToList();
        var minRated = rated.All(r => r is not null) ? rated.Min() : null;
        var minConfigured = configured.All(v => v is not null) ? configured.Min() : null;

        var status = minRated is null || minConfigured is null ? FindingStatus.Unknown
            : minConfigured < minRated * (1 - c.Ram.SpeedToleranceFraction) ? FindingStatus.Problem
            : FindingStatus.Ok;

        var vendor = c.Bios.NormalizeVendor(p.Firmware?.BoardManufacturer);
        var menu = c.Bios.Find(vendor, RamSpeed.Platform(p.Cpu, first.Type), "xmp");
        var laptop = p.IsLaptop;
        // Four DDR5 modules: the speed AMD (Ryzen) or Intel (datasheet, single-rank) specifies for two modules per channel.
        var fourDimmLimit = laptop || first.Type != "DDR5" || p.Memory.Modules.Count < 4 ? null
            : p.Cpu?.Vendor == Vendor.Amd ? FourDimmDdr5Mts
            : p.Cpu?.Vendor == Vendor.Intel ? c.Ram.FourDimmIntel.FirstOrDefault(r => RegexCache.Get(r.CpuPattern).IsMatch(p.Cpu.Name))?.Mts
            : (int?)null;
        var fourDimms = fourDimmLimit is { } limit && minRated > limit;
        var intelLocked = !laptop && !fourDimms && status == FindingStatus.Problem && IntelLocked(p, minConfigured, c);

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            // Laptops rarely offer XMP; four DDR5 modules on AM5 run below the kit rating by AMD's specification.
            // A non-Z Intel board of 8th to 10th generation runs memory at the processor's specified speed at most.
            Status = (laptop || fourDimms || intelLocked) && status == FindingStatus.Problem ? FindingStatus.Info : status,
            Variant = laptop ? "laptop" : fourDimms && status == FindingStatus.Problem ? (p.Cpu?.Vendor == Vendor.Intel ? "fourDimmsIntel" : "fourDimms") : intelLocked ? "intelLocked" : null,
            Impact = Impact(p.Cpu, c),
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                .. p.Memory.Modules.Select((m, i) => new Fact("fact.ramModule",
                    $"{m.DeviceLocator}: {m.Manufacturer} {m.PartNumber.Trim()}, {m.CapacityBytes >> 30} GB {m.Type}")),
                new("fact.ramRated", minRated is null ? "@unknown" : $"{minRated} MT/s"),
                new("fact.ramConfigured", minConfigured is null ? "@unknown" : $"{minConfigured} MT/s"),
            ],
            Params = new Dictionary<string, string>
            {
                ["rated"] = minRated?.ToString() ?? "?",
                ["configured"] = minConfigured?.ToString() ?? "?",
                ["profileName"] = menu?.ProfileName ?? (first.Type == "DDR5" && p.Cpu?.Vendor == Vendor.Amd ? "EXPO" : "XMP"),
                ["menuPath"] = menu?.Path ?? "",
                ["menuUnverified"] = menu is { Verified: false } ? "yes" : "",
                ["board"] = $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim(),
                ["fourDimmMts"] = fourDimmLimit?.ToString() ?? "",
            },
        };
    }

    /// <summary>
    /// An Intel 8th to 10th generation processor, a board not named as a Z370, Z390, Z490 or Z590 model (OEM boards often carry
    /// only a part code), and memory at one of the speeds Intel specifies for these processors.
    /// </summary>
    private static bool IntelLocked(HardwareProfile p, int? configured, CatalogData c)
    {
        if (c.Ram.IntelLocked is not { } rule || p.Cpu is not { Vendor: Vendor.Intel } cpu || configured is not { } speed) return false;
        if (!rule.CpuPatterns.Any(pattern => RegexCache.Get(pattern).IsMatch(cpu.Name))) return false;
        if (rule.UnlockedBoardPattern.Length > 0 && RegexCache.Get(rule.UnlockedBoardPattern).IsMatch(p.Firmware?.BoardProduct ?? "")) return false;
        return rule.SpecSpeeds.Any(s => Math.Abs(speed - s) <= s * c.Ram.SpeedToleranceFraction);
    }
}

/// <summary>Advisor: single channel or both DIMMs in the same channel.</summary>
public sealed class DualChannelCheck : IPhysicalHardwareCheck
{
    public const string Id = "A.dualChannel";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Memory is null)
        {
            if (p.ProbeFailed("Memory")) yield return Finding.NotRead(Id);
            yield break;
        }
        if (p.Memory.Modules.Count == 0) yield break;
        var modules = p.Memory.Modules;
        var channels = modules.Select(m => RamSpeed.Channel(m, c)).ToList();
        FindingStatus status;
        string? variant = null;
        if (modules.Count == 1)
        {
            // Only a socketed module (Win32_PhysicalMemory.FormFactor 8 = DIMM, 12 = SODIMM) proves single channel.
            // Anything else may be soldered memory, which can be dual channel internally -> Unknown.
            status = modules[0].FormFactor is 8 or 12 ? FindingStatus.Problem : FindingStatus.Unknown;
            variant = "single";
        }
        else if (channels.Any(ch => ch is null))
        {
            status = FindingStatus.Unknown;
        }
        else if (channels.Distinct().Count() == 1)
        {
            status = FindingStatus.Problem;
            variant = "sameChannel";
        }
        else
        {
            status = FindingStatus.Ok;
        }

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Variant = variant,
            Impact = 4,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.ramModuleCount", modules.Count.ToString()),
                .. modules.Select((m, i) => new Fact("fact.ramSlot", $"{m.DeviceLocator} / {m.BankLabel}: {(channels[i] is { } ch ? $"Channel {ch}" : "?")}")),
            ],
            Params = new Dictionary<string, string> { ["board"] = $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim() },
        };
    }
}

/// <summary>F13: low RAM (≤ 8 GB).</summary>
public sealed class LowRamCheck : IFindingCheck
{
    public const string Id = "F13.lowRam";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Memory is null)
        {
            if (p.ProbeFailed("Memory")) yield return Finding.NotRead(Id);
            yield break;
        }
        var gb = p.Memory.TotalBytes / (double)(1L << 30);
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = gb <= 8.5 ? FindingStatus.Problem : FindingStatus.Ok,
            Impact = 4,
            Effects = [Effect.Stutter, Effect.Lows],
            Facts = [new("fact.ramTotal", $"{gb:0.#} GB")],
        };
    }
}

/// <summary>
/// F17: low free space (&lt; 10 %). Problem on the system drive and drives that hold a game library; other fixed drives
/// (data or backup disks) are information.
/// </summary>
public sealed class LowDiskSpaceCheck : IFindingCheck
{
    public const string Id = "F17.lowDiskSpace";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Storage is null)
        {
            if (p.ProbeFailed("Storage")) yield return Finding.NotRead(Id);
            yield break;
        }
        var gameRoots = (p.Software?.GameLibraryPaths ?? []).Select(Path.GetPathRoot).Where(r => r is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in p.Storage.Volumes)
        {
            var matters = v.IsSystem || gameRoots.Contains(v.Root);
            var low = v.FreeFraction < c.Storage.LowFreeSpaceFraction;
            yield return new Finding
            {
                Id = Id,
                InstanceKey = v.Root,
                Subject = v.Root,
                Kind = FindingKind.Finding,
                Status = !low ? FindingStatus.Ok : matters ? FindingStatus.Problem : FindingStatus.Info,
                Variant = v.IsSystem ? "system" : gameRoots.Contains(v.Root) ? "games" : "other",
                Impact = v.IsSystem ? 2 : 1,
                Effects = [Effect.Stability, Effect.Stutter],
                Facts =
                [
                    new("fact.volume", $"{v.Root} {v.Label}".Trim()),
                    new("fact.freeSpace", $"{RebarCheck.FormatBytes(v.FreeBytes)} / {RebarCheck.FormatBytes(v.SizeBytes)} ({v.FreeFraction:P0})"),
                ],
                Params = new Dictionary<string, string> { ["volume"] = v.Root },
            };
        }
    }
}

/// <summary>F12: game library on a hard disk (extra note for SMR drives).</summary>
public sealed class GamesOnHddCheck : IFindingCheck
{
    public const string Id = "F12.gamesOnHdd";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Software is null || p.Software.GameLibraryPaths.Count == 0) yield break;
        if (p.Storage is null || p.Storage.DisksUnread)
        {
            if (p.ProbeFailed("Storage") || p.Storage?.DisksUnread == true) yield return Finding.NotRead(Id);
            yield break;
        }
        var storage = p.Storage;
        // One finding per disk: two libraries on the same hard disk are one problem, not two.
        var byDisk = p.Software.GameLibraryPaths
            .Select(library => (Library: library, Disk: storage.DiskOf(storage.Volumes.FirstOrDefault(v => string.Equals(v.Root, Path.GetPathRoot(library), StringComparison.OrdinalIgnoreCase)))))
            .Where(x => x.Disk is not null)
            .GroupBy(x => x.Disk!);
        foreach (var group in byDisk)
        {
            var disk = group.Key;
            var library = string.Join(", ", group.Select(x => x.Library));
            yield return new Finding
            {
                Id = Id,
                InstanceKey = group.First().Library,
                Subject = library,
                Kind = FindingKind.Finding,
                Status = disk.MediaType == "HDD" ? FindingStatus.Problem : disk.MediaType == "Unspecified" ? FindingStatus.Unknown : FindingStatus.Ok,
                Variant = disk.IsSmrSuspect ? "smr" : null,
                // SMR mainly slows sustained writes (installs, updates), not the reads during play: same impact.
                Impact = 3,
                Effects = [Effect.Stutter],
                Facts =
                [
                .. group.Select(x => new Fact("fact.library", x.Library)),
                    new("fact.disk", $"{disk.FriendlyName} ({disk.MediaType}, {disk.BusType})"),
                    new("fact.smr", disk.IsSmrSuspect ? "@yes" : "@no"),
                ],
                Params = new Dictionary<string, string> { ["library"] = library, ["disk"] = disk.FriendlyName },
            };
        }
    }
}

/// <summary>
/// F18: TRIM disabled for NTFS (DisableDeleteNotification = 1). The probe reads the registry (no fsutil call); a missing
/// value means TRIM is on, the NTFS default.
/// </summary>
public sealed class TrimCheck : IFindingCheck
{
    public const string Id = "F18.trim";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Storage is null || p.Storage.DisksUnread)
        {
            if (p.ProbeFailed("Storage") || p.Storage?.DisksUnread == true) yield return Finding.NotRead(Id);
            yield break;
        }
        if (!p.Storage.Disks.Any(d => d.MediaType == "SSD")) yield break;
        var trim = p.Extras?.Trim;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = trim is null ? FindingStatus.Unknown : trim.TrimOn ? FindingStatus.Ok : FindingStatus.Problem,
            Impact = 1,
            Effects = [Effect.Stutter],
            Facts = [new("fact.trim", trim is null ? "@unknown" : trim.TrimOn ? "@on" : "@off")],
        };
    }
}
