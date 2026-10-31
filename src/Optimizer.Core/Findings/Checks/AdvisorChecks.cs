using System.Globalization;
using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;
using Optimizer.Core.Interop;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Findings.Checks;

/// <summary>Shared helpers for the advisor checks.</summary>
public static class DisplayLink
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY (wingdi.h). Embedded DisplayPort (11) and embedded UDI (13) are built-in panels.
    public static string Connection(uint outputTechnology) => outputTechnology switch
    {
        _ when Hardware.Probes.DisplayProbe.IsInternalOutput(outputTechnology) => "internal",
        0 => "vga",
        4 => "dvi",
        5 => "hdmi",
        10 or 18 => "dp",
        _ => "other",
    };

    public static string ConnectionLabel(uint outputTechnology) => Connection(outputTechnology) switch
    {
        "vga" => "VGA",
        "dvi" => "DVI",
        "hdmi" => "HDMI",
        "dp" => "DisplayPort",
        "internal" => "@internalPanel",
        _ => "@unknown",
    };
}

/// <summary>
/// F2: the monitor accepts a clearly higher refresh rate (EDID range limits) than any mode Windows offers. Typical causes:
/// an HDMI 1.4 or DVI single-link connection, an old cable, or the monitor's own menu limiting the input.
/// </summary>
public sealed class EdidRefreshCheck : IFindingCheck
{
    public const string Id = "F2.edidRefresh";
    public IReadOnlyList<string> DocIds => [Id];

    /// <summary>
    /// Problem only from 75 Hz on, with a 20 % gap, and only when the range goes above 76 Hz or the monitor's preferred mode itself is
    /// above what Windows offers: a 60 Hz monitor whose range limits end at 75 or 76 Hz does not trigger it, a 75 Hz
    /// monitor with a 75 Hz preferred mode held at 60 Hz does.
    /// </summary>
    public static bool IsLimited(int edidMaxHz, int maxOfferedHz, double? preferredHz = null) =>
        maxOfferedHz > 0 && edidMaxHz >= 75 && edidMaxHz >= maxOfferedHz * 1.2 && (edidMaxHz > 76 || preferredHz >= maxOfferedHz * 1.2);

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Displays is null) yield break;
        foreach (var d in p.Displays)
        {
            var maxOffered = d.Modes.Select(m => m.RefreshHz).DefaultIfEmpty(0).Max();
            var edidMax = d.Edid?.MaxVHz;
            var status = d.Edid is null || maxOffered == 0 ? FindingStatus.Unknown
                : edidMax is not { } e ? FindingStatus.Unsupported // no range descriptor: nothing to compare with
                : IsLimited(e, maxOffered, d.Edid.PreferredRefreshHz) ? FindingStatus.Problem
                : FindingStatus.Ok;
            yield return new Finding
            {
                Id = Id,
                InstanceKey = d.GdiName,
                Subject = d.FriendlyName,
                Kind = FindingKind.Finding,
                Status = status,
                Variant = DisplayLink.Connection(d.OutputTechnology),
                Impact = 4,
                Effects = [Effect.Fps, Effect.Latency],
                Facts =
                [
                    new("fact.display", d.FriendlyName),
                    new("fact.edidMaxRefresh", edidMax is { } m ? $"{m} Hz" : "@unknown"),
                    new("fact.maxOfferedAnyResolution", maxOffered > 0 ? $"{maxOffered} Hz" : "@unknown"),
                    new("fact.connection", DisplayLink.ConnectionLabel(d.OutputTechnology)),
                    new("fact.connectedTo", d.AdapterName ?? d.AdapterVendor.ToString()),
                ],
                Params = new Dictionary<string, string>
                {
                    ["display"] = d.FriendlyName,
                    ["edidMax"] = edidMax?.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["offered"] = maxOffered.ToString(CultureInfo.InvariantCulture),
                },
            };
        }
    }
}

/// <summary>
/// F7: NVIDIA global profile settings that cost performance in every game: a global frame rate limit far below the
/// refresh rate, or the power mode forced to minimum. Forced V-Sync is information only (with G-SYNC it is the usual setup).
/// </summary>
public sealed class NvidiaGlobalCheck : IFindingCheck
{
    public const string Id = "F7.nvidiaGlobal";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Gpus?.Any(g => g.Vendor == Vendor.Nvidia && g.Kind == GpuKind.Discrete) != true) yield break;
        var nv = p.Extras?.Nvidia;
        if (nv is null)
        {
            yield return new Finding { Id = Id, Kind = FindingKind.Finding, Status = FindingStatus.Unknown, Impact = 4, Effects = [Effect.Fps, Effect.Latency] };
            yield break;
        }

        var nvidiaDisplays = p.Displays?.Where(d => d.AdapterVendor == Vendor.Nvidia).ToList() ?? [];
        var refresh = (nvidiaDisplays.Count > 0 ? nvidiaDisplays : p.Displays ?? []).Select(d => d.CurrentRefresh.Hz).DefaultIfEmpty(0).Max();
        var frl = nv.FrameRateLimit is > 0 ? nv.FrameRateLimit : null;
        var vrrOn = nv.Vrr.Values.Any(v => v.Enabled);
        // The G-SYNC cap follows the fastest G-SYNC display, not a faster fixed refresh display next to it.
        var gsyncRefresh = nvidiaDisplays.Where(d => nv.Vrr.TryGetValue(d.GdiName, out var v) && v.Enabled)
            .Select(d => d.CurrentRefresh.Hz).DefaultIfEmpty(refresh).Max();
        // A cap a few FPS below the refresh rate is the recommended G-SYNC setup; only a cap well below it is a problem.
        // Reflex caps lower on very high refresh displays (refresh - refresh² / 3600: 416 FPS at 480 Hz), so that cap counts as fine too.
        // A cap that fits the G-SYNC display is fine even when a faster fixed refresh display sits on the same card.
        static double Lowest(double hz) => hz > 0 ? Math.Min(hz * 0.9, hz - hz * hz / 3600) - 2 : 0;
        var lowCap = frl is { } cap && refresh > 0 && cap < Lowest(refresh) && !(vrrOn && gsyncRefresh > 0 && cap >= Lowest(gsyncRefresh));
        var powerMin = nv.PowerMode == Nvapi.PStatePreferMin;
        var vsyncForced = nv.VSyncMode == Nvapi.VSyncForceOn && !vrrOn;
        // G-SYNC with V-Sync forced on and no cap: once a game runs faster than the refresh rate, G-SYNC falls back to
        // V-Sync and buffers 2 to 6 frames (Blur Busters); a cap 3 FPS below the refresh rate avoids that. A limit at or
        // above the G-SYNC display's refresh rate does not keep games below it either.
        var capTarget = gsyncRefresh >= 60 ? (int)Math.Floor(gsyncRefresh) - 3 : 0;
        var gsyncNoCap = vrrOn && nv.VSyncMode == Nvapi.VSyncForceOn && capTarget > 0 && (frl is null || frl >= Math.Floor(gsyncRefresh));

        var reset = new List<uint>();
        if (lowCap) reset.Add(Nvapi.SettingFrameRateLimiter);
        if (powerMin) reset.Add(Nvapi.SettingPreferredPState);
        // A low cap with G-SYNC and forced V-Sync: removing it would leave the setup without the cap it needs, and the next
        // scan would offer the frame cap fix for the same setting. The reset sets the cap below the refresh rate instead.
        var capInReset = lowCap && vrrOn && nv.VSyncMode == Nvapi.VSyncForceOn && capTarget > 0;

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = reset.Count > 0 ? FindingStatus.Problem : vsyncForced || gsyncNoCap ? FindingStatus.Info : FindingStatus.Ok,
            Impact = reset.Count == 0 && gsyncNoCap ? 2 : 4,
            Effects = reset.Count == 0 && gsyncNoCap ? [Effect.Latency] : [Effect.Fps, Effect.Latency],
            Facts =
            [
                new("fact.nvidiaDriver", nv.DriverVersion ?? "@unknown"),
                new("fact.nvFrameRateLimit", frl is { } f ? $"{f} FPS" : "@off"),
                new("fact.nvPowerMode", nv.PowerMode switch
                {
                    null or Nvapi.PStateOptimalPower => "@nvPowerNormal",
                    Nvapi.PStatePreferMax => "@nvPowerMax",
                    Nvapi.PStatePreferMin => "@nvPowerMin",
                    0 => "@nvPowerAdaptive",
                    var v => $"0x{v:X}",
                }),
                new("fact.nvVSync", nv.VSyncMode switch
                {
                    null or Nvapi.VSyncPassive => "@nvVSyncApp",
                    Nvapi.VSyncForceOn => "@on",
                    Nvapi.VSyncForceOff => "@off",
                    var v => $"0x{v:X}",
                }),
                new("fact.gsyncEnabled", nv.Vrr.Count == 0 ? "@unknown" : vrrOn ? "@yes" : "@no"),
                new("fact.currentRefresh", refresh > 0 ? $"{refresh:0.##} Hz" : "@unknown"),
            ],
            Fix = reset.Count > 0 ? RuntimeFixes.NvidiaGlobalReset(reset, capInReset ? (uint)capTarget : null)
                : gsyncNoCap ? RuntimeFixes.NvidiaFrameCap(capTarget) : null,
            Params = new Dictionary<string, string>
            {
                ["lowCap"] = lowCap ? "yes" : "",
                ["gsyncNoCap"] = gsyncNoCap ? "yes" : "",
                ["capInReset"] = capInReset ? "yes" : "",
                ["capTarget"] = capTarget > 0 ? capTarget.ToString(CultureInfo.InvariantCulture) : "",
                ["powerMin"] = powerMin ? "yes" : "",
                ["vsyncForced"] = vsyncForced ? "yes" : "",
                ["cap"] = frl?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["refresh"] = refresh > 0 ? refresh.ToString("0", CultureInfo.InvariantCulture) : "",
                ["gsyncRefresh"] = gsyncRefresh > 0 ? gsyncRefresh.ToString("0", CultureInfo.InvariantCulture) : "",
            },
        };
    }
}

/// <summary>F8: Windows power mode "Best power efficiency" while plugged in (Balanced plan only; other plans hide the slider).</summary>
public sealed class PowerModeCheck : IFindingCheck
{
    public const string Id = "F8.powerMode";
    public IReadOnlyList<string> DocIds => [Id];

    public static string Label(Guid? overlay) =>
        overlay is not { } g ? "@unknown"
        : g == FirmwareExtras.OverlayBetterBattery ? "@powerModeEfficiency"
        : g == FirmwareExtras.OverlayBestPerformance ? "@powerModeBest"
        : g == Guid.Empty || g == FirmwareExtras.OverlayBetterPerformance ? "@powerModeBalanced"
        : g.ToString();

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Power is null || p.Extras?.PowerOverlay is not { } overlay) yield break; // API unavailable: nothing to judge
        if (p.Power.OnAc != true || p.Power.Personality != PowerPersonality.Balanced) yield break;
        var problem = overlay == FirmwareExtras.OverlayBetterBattery;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = problem ? FindingStatus.Problem : FindingStatus.Ok,
            // Windows has its own values for the modes only on PCs with a battery; on a desktop they come from the PC
            // maker, if at all, so the mode may change little there (customize-power-slider, "AC only devices").
            Impact = p.IsLaptop ? 3 : 1,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.powerMode", Label(overlay)),
                new("fact.powerPlan", p.Power.ActiveSchemeName),
                new("fact.acPower", "@yes"),
            ],
            Fix = problem ? RuntimeFixes.PowerModeBestPerformance() : null,
        };
    }
}

/// <summary>F14: overlay and capture programs running (information; each adds a hook into the game's frame).</summary>
public sealed class OverlaysCheck : IFindingCheck
{
    public const string Id = "F14.overlays";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Extras is null)
        {
            if (p.ProbeFailed("Extras")) yield return Finding.NotRead(Id);
            yield break;
        }
        var running = p.Extras.RunningOverlays;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = running.Count > 0 ? FindingStatus.Info : FindingStatus.Ok,
            Impact = 1,
            Effects = [Effect.Lows, Effect.Stutter],
            Facts = running.Count > 0 ? running.Select(o => new Fact("fact.overlayRunning", o)).ToList() : [new Fact("fact.overlayRunning", "@none")],
            Params = new Dictionary<string, string> { ["overlays"] = string.Join(", ", running) },
        };
    }
}

/// <summary>
/// F15 (chipset part): AMD desktop without the AMD Chipset Software package. Needed for the 3D V-Cache driver on
/// dual-CCD X3D processors (Problem there), otherwise information (Windows Update ships most of the drivers).
/// </summary>
public sealed class AmdChipsetCheck : IFindingCheck
{
    public const string Id = "F15.amdChipset";
    public IReadOnlyList<string> DocIds => [Id];

    public static bool IsChipsetPackage(string name) =>
        name.StartsWith("AMD Chipset Software", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("AMD Ryzen Chipset", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("AMD Chipset Drivers", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Cpu?.Vendor != Vendor.Amd || p.IsLaptop) yield break;
        if (p.Extras is null)
        {
            if (p.ProbeFailed("Extras")) yield return Finding.NotRead(Id);
            yield break;
        }
        // The package as Settings lists it; its hidden parts do not count.
        var programs = p.Extras.Programs.Where(x => !x.SystemComponent).ToList();
        var package = programs.FirstOrDefault(x => IsChipsetPackage(x.Name));
        var x3d = X3d.Classify(p.Cpu, c) == X3dLayout.MultiCcdAsymmetric;
        var status = programs.Count == 0 ? FindingStatus.Unknown
            : package is not null ? FindingStatus.Ok
            : x3d ? FindingStatus.Problem : FindingStatus.Info;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = status,
            Variant = x3d ? "x3d" : null,
            Impact = x3d ? 4 : 1,
            Effects = x3d ? [Effect.Fps, Effect.Lows] : [Effect.Stability],
            Facts =
            [
                new("fact.cpu", p.Cpu.Name),
                new("fact.chipsetPackage", package is null ? "@notFound" : $"{package.Name} {package.Version}".Trim()),
                new("fact.board", $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim()),
            ],
            Params = new Dictionary<string, string> { ["cpu"] = p.Cpu.Name },
        };
    }
}

/// <summary>
/// F20: variable refresh rate (G-SYNC / G-SYNC Compatible / FreeSync). NVIDIA: read per display through NVAPI.
/// Other vendors: no public read API, so a VRR-looking monitor gives information with the setup steps (Unknown never
/// carries advice, and nothing could ever resolve it).
/// </summary>
public sealed class VrrCheck : IFindingCheck
{
    public const string Id = "F20.vrr";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Displays is null) yield break;
        foreach (var d in p.Displays)
        {
            var looksVrr = d.Edid?.LooksVrrCapable == true;
            NvidiaVrr? nv = null;
            if (d.AdapterVendor == Vendor.Nvidia) p.Extras?.Nvidia?.Vrr.TryGetValue(d.GdiName, out nv);

            FindingStatus status;
            string variant;
            if (nv is not null)
            {
                if (nv.Possible && nv.Enabled) { status = FindingStatus.Ok; variant = "on"; }
                else if (nv.Possible) { status = FindingStatus.Problem; variant = "off"; }
                else if (looksVrr) { status = FindingStatus.Info; variant = "notPossible"; }
                else { status = FindingStatus.Unsupported; variant = "fixed"; }
            }
            else if (looksVrr)
            {
                status = FindingStatus.Info;
                variant = d.AdapterVendor == Vendor.Nvidia ? "nvidiaUnknown" : "otherVendor";
            }
            else
            {
                continue; // fixed-refresh monitor on a GPU we cannot query: nothing to say
            }

            yield return new Finding
            {
                Id = Id,
                InstanceKey = d.GdiName,
                Subject = d.FriendlyName,
                Kind = FindingKind.Finding,
                Status = status,
                Variant = variant,
                Impact = 3,
                Effects = [Effect.Stutter, Effect.Latency],
                Facts =
                [
                    new("fact.display", d.FriendlyName),
                    new("fact.edidMinRefresh", d.Edid?.MinVHz is { } lo ? $"{lo} Hz" : "@unknown"),
                    new("fact.edidMaxRefresh", d.Edid?.MaxVHz is { } hi ? $"{hi} Hz" : "@unknown"),
                    new("fact.connection", DisplayLink.ConnectionLabel(d.OutputTechnology)),
                    new("fact.connectedTo", d.AdapterName ?? d.AdapterVendor.ToString()),
                    .. NvidiaFacts(nv),
                ],
                Params = new Dictionary<string, string>
                {
                    ["display"] = d.FriendlyName,
                    ["hdmi"] = DisplayLink.Connection(d.OutputTechnology) == "hdmi" ? "yes" : "",
                    ["amd"] = d.AdapterVendor == Vendor.Amd ? "yes" : "",
                    ["intel"] = d.AdapterVendor == Vendor.Intel ? "yes" : "",
                },
            };
        }
    }

    private static Fact[] NvidiaFacts(NvidiaVrr? nv) => nv is null
        ? []
        : [new Fact("fact.vrrPossible", nv.Possible ? "@yes" : "@no"), new Fact("fact.gsyncEnabled", nv.Enabled ? "@yes" : "@no")];
}

/// <summary>
/// F22: Secure Boot certificates. Microsoft's 2011 certificates expire in 2026; PCs need the 2023 CAs in KEK and db to
/// keep receiving boot manager and Secure Boot updates. Read-only (the app never writes UEFI variables).
/// </summary>
public sealed class SecureBootCertsCheck : IFindingCheck
{
    public const string Id = "F22.secureBootCerts";
    public IReadOnlyList<string> DocIds => [Id];
    public FindingKind Kind => FindingKind.Advisor;

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Firmware?.SecureBoot != TriState.Yes) yield break; // without Secure Boot the certificates are not used
        var certs = p.Extras?.SecureBootCerts;
        static string B(bool? v) => v switch { true => "@present", false => "@missing", null => "@unknown" };
        var status = certs?.Kek2023 is null || certs.WindowsUefiCa2023 is null ? FindingStatus.Unknown
            : certs.Kek2023 == true && certs.WindowsUefiCa2023 == true ? FindingStatus.Ok
            : FindingStatus.Info;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Variant = p.Elevation?.IsElevated == false ? "notElevated" : null,
            Impact = 0,
            Effects = [Effect.None],
            Facts =
            [
                new("fact.kek2023", B(certs?.Kek2023)),
                new("fact.windowsUefiCa2023", B(certs?.WindowsUefiCa2023)),
                new("fact.microsoftUefiCa2023", B(certs?.MicrosoftUefiCa2023)),
                new("fact.optionRomCa2023", B(certs?.OptionRomCa2023)),
                new("fact.servicingStatus", certs?.ServicingStatus ?? "@unknown"),
                new("fact.bios", $"{p.Firmware.BiosVersion} ({p.Firmware.BiosDate:yyyy-MM-dd})"),
            ],
            Params = new Dictionary<string, string>
            {
                ["board"] = $"{p.Firmware.BoardManufacturer} {p.Firmware.BoardProduct}".Trim(),
            },
        };
    }
}

/// <summary>
/// F24: Ethernet link slower than the adapter can do. Forced speed in the driver = fixable here; auto-negotiated at
/// 100 Mbit/s or less on a gigabit adapter usually means a damaged cable or port (two of four wire pairs).
/// A 2.5G adapter on a 1G router is normal and not reported. On battery the driver's power saving can lower the link on
/// purpose: information then (variant "negotiatedBattery"), to be checked again on mains power.
/// </summary>
public sealed class EthernetSpeedCheck : IFindingCheck
{
    public const string Id = "F24.ethernetSpeed";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        foreach (var n in p.Extras?.Nics.Where(n => n.Type == "Ethernet" && n.IsUp && n.IsPhysical) ?? [])
        {
            if (n.MaxSpeedMbps is not { } max || n.SpeedBps <= 0) continue;
            var link = (int)(n.SpeedBps / 1_000_000);
            var forcedValue = n.Keywords.TryGetValue("*SpeedDuplex", out var sd) ? sd : null;
            var forced = forcedValue is not null && forcedValue != "0";
            var forcedMbps = forced ? Hardware.Probes.ExtrasProbe.SpeedDuplexMbps(forcedValue!) : null;
            string? variant = null;
            if (forced && forcedMbps is { } f && f < max) variant = "forced";
            else if (!forced && link <= 100 && max >= 1000) variant = p.Power?.OnAc == false ? "negotiatedBattery" : "negotiated";
            yield return new Finding
            {
                Id = Id,
                InstanceKey = n.Id,
                Subject = n.Description,
                Kind = FindingKind.Finding,
                // A link at 100 Mbit/s the adapter negotiated itself is often the other end (a router port, a powerline
                // adapter, an older switch), which the scan cannot see: worth a look, not a problem. A speed forced lower
                // than the adapter supports is one.
                Status = variant is null ? FindingStatus.Ok : variant == "forced" ? FindingStatus.Problem : FindingStatus.Info,
                Variant = variant,
                Impact = 1,
                Effects = [Effect.Latency],
                Facts =
                [
                    new("fact.adapter", n.Description),
                    new("fact.linkSpeed", FormatMbps(link)),
                    new("fact.adapterMaxSpeed", FormatMbps(max)),
                    new("fact.speedDuplex", forced ? (forcedMbps is { } fm ? FormatMbps(fm) : forcedValue!) : "@autoNegotiation"),
                ],
                Fix = variant == "forced" ? RuntimeFixes.EthernetAuto(n) : null,
                Params = new Dictionary<string, string>
                {
                    ["adapter"] = n.Description,
                    ["link"] = FormatMbps(link),
                    ["max"] = FormatMbps(max),
                },
            };
        }
    }

    public static string FormatMbps(int mbps) => mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gbit/s" : $"{mbps} Mbit/s";
}

/// <summary>
/// F25: Wi-Fi connected on 2.4 GHz. A Problem only when the same network is also visible on 5 or 6 GHz; otherwise
/// information. Skipped while a physical Ethernet adapter is connected (Windows then routes through the cable); virtual
/// adapters (Hyper-V, WSL, VPN) do not count. When Windows withholds the access point details because the app may not
/// use the location, the result is Unknown (variant "noLocation").
/// </summary>
public sealed class WifiBandCheck : IFindingCheck
{
    public const string Id = "F25.wifiBand";
    public IReadOnlyList<string> DocIds => [Id];

    /// <summary>Sanity range for center frequencies (kHz); outside means the struct was misread.</summary>
    public static bool Plausible(uint khz) => khz is >= 2_400_000 and <= 7_200_000;

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        var extras = p.Extras;
        if (extras is null || extras.Nics.Any(n => n.Type == "Ethernet" && n.IsUp && n.SpeedBps > 0 && n.IsPhysical)) yield break;
        foreach (var w in extras.Wifi)
        {
            if (w.LocationDenied && w.CenterFrequencyKhz is null)
            {
                yield return new Finding
                {
                    Id = Id,
                    InstanceKey = w.Interface,
                    Subject = string.IsNullOrEmpty(w.Ssid) ? w.Interface : w.Ssid,
                    Kind = FindingKind.Finding,
                    Status = FindingStatus.Unknown,
                    Variant = "noLocation",
                    Impact = 2,
                    Effects = [Effect.Latency, Effect.Stutter],
                    Facts = [new("fact.adapter", w.Interface), new("fact.wifiBand", "@unknown")],
                    Params = new Dictionary<string, string> { ["ssid"] = w.Ssid },
                };
                continue;
            }
            var freq = w.CenterFrequencyKhz is { } f && Plausible(f) ? f : (uint?)null;
            var band = freq is { } k ? Wlan.Band(k) : null;
            var otherBands = w.SameSsidFrequenciesKhz.Where(Plausible).Select(Wlan.Band).Distinct().ToList();
            var fasterVisible = otherBands.Any(b => b != "2.4 GHz");
            var status = band is null ? FindingStatus.Unknown
                : band != "2.4 GHz" ? FindingStatus.Ok
                : fasterVisible ? FindingStatus.Problem : FindingStatus.Info;
            yield return new Finding
            {
                Id = Id,
                InstanceKey = w.Interface,
                Subject = w.Ssid,
                Kind = FindingKind.Finding,
                Status = status,
                Variant = status == FindingStatus.Info ? "only24" : null,
                Impact = 2,
                Effects = [Effect.Latency, Effect.Stutter],
                Facts =
                [
                    new("fact.adapter", w.Interface),
                    new("fact.wifiNetwork", w.Ssid),
                    new("fact.wifiBand", band ?? "@unknown"),
                    new("fact.wifiBandsVisible", otherBands.Count > 0 ? string.Join(", ", otherBands.Order(StringComparer.Ordinal)) : "@unknown"),
                ],
                Params = new Dictionary<string, string> { ["ssid"] = w.Ssid },
            };
        }
    }
}

/// <summary>
/// F26: NVMe SSD link. Fewer lanes than the drive supports (trained down) is a problem (seating, slot sharing, riser);
/// a slower slot is information (e.g. a PCIe 4.0 drive in a 3.0 slot works at 3.0 speed).
/// </summary>
public sealed class NvmeLinkCheck : IPhysicalHardwareCheck
{
    public const string Id = "F26.nvmeLink";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        foreach (var n in p.Extras?.NvmeLinks ?? [])
        {
            var l = n.Link;
            FindingStatus status;
            string? variant = null;
            if (l?.CurrentWidth is not { } cw || cw == 0 || l.MaxWidth is not { } mw) status = FindingStatus.Unknown;
            else if (cw < mw && n.Port?.MaxWidth is { } pw && pw < mw && cw >= pw) { status = FindingStatus.Info; variant = "slotWidth"; }
            else if (cw < mw) { status = FindingStatus.Problem; variant = "trainedDown"; }
            else if (l.CurrentGen is { } cg && l.MaxGen is { } mg && cg < mg)
            {
                status = FindingStatus.Info;
                variant = n.Port?.MaxGen is { } pg && pg < mg ? "slotGen" : "genLower";
            }
            else status = FindingStatus.Ok;

            yield return new Finding
            {
                Id = Id,
                InstanceKey = n.PnpId,
                Subject = n.Disk,
                Kind = FindingKind.Finding,
                Status = status,
                Variant = variant,
                Impact = 1,
                Effects = [Effect.Stutter],
                Facts =
                [
                    new("fact.disk", n.Disk),
                    new("fact.ssdMaxLink", Link(l?.MaxGen, l?.MaxWidth)),
                    new("fact.currentLink", Link(l?.CurrentGen, l?.CurrentWidth)),
                    new("fact.slotMaxLink", Link(n.Port?.MaxGen, n.Port?.MaxWidth)),
                ],
                Params = new Dictionary<string, string> { ["disk"] = n.Disk },
            };
        }
    }

    private static string Link(int? gen, int? width) =>
        gen is null && width is null ? "@unknown" : $"{(gen is null ? "Gen ?" : $"Gen {gen}")} x{width?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
}

/// <summary>
/// F27: laptop panel driven by the integrated GPU while a discrete GPU exists (hybrid graphics without a MUX). Frames
/// are copied from the dGPU to the iGPU, which costs some FPS and adds latency. Information: it is a hardware design.
/// </summary>
public sealed class LaptopPanelCheck : IFindingCheck
{
    public const string Id = "F27.laptopPanel";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (!p.IsLaptop || p.Displays is null || p.Gpus is null) yield break;
        var discrete = p.Gpus.FirstOrDefault(g => g.Kind == GpuKind.Discrete);
        var panel = p.Displays.FirstOrDefault(d => d.IsInternal);
        if (discrete is null || panel is null) yield break;
        var panelGpu = p.Gpus.FirstOrDefault(g => string.Equals(g.Name, panel.AdapterName, StringComparison.Ordinal));
        var onIgpu = panelGpu?.Kind == GpuKind.Integrated;
        var externalOnDgpu = p.Displays.Where(d => !d.IsInternal && string.Equals(d.AdapterName, discrete.Name, StringComparison.Ordinal)).ToList();
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = panelGpu is null ? FindingStatus.Unknown : onIgpu ? FindingStatus.Info : FindingStatus.Ok,
            Impact = 2,
            Effects = [Effect.Fps, Effect.Latency],
            Facts =
            [
                new("fact.internalPanel", panel.FriendlyName),
                new("fact.displayAdapter", $"{panel.FriendlyName}: {panel.AdapterName ?? "?"}"),
                new("fact.discreteGpu", discrete.Name),
                .. externalOnDgpu.Select(d => new Fact("fact.externalOnDgpu", d.FriendlyName)),
            ],
            Params = new Dictionary<string, string>
            {
                ["dgpu"] = discrete.Name,
                ["externalOnDgpu"] = string.Join(", ", externalOnDgpu.Select(d => d.FriendlyName)),
            },
        };
    }
}

/// <summary>Advisor: Intel Application Optimization (APO) on supported K processors. Information with the setup steps.</summary>
public sealed class ApoCheck : IFindingCheck
{
    public const string Id = "A.apo";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Cpu?.Vendor != Vendor.Intel || !RegexCache.Get(c.Extras.ApoCpus.Regex).IsMatch(p.Cpu.Name)) yield break;
        var dtt = c.Extras.ServiceGroups.TryGetValue("intelDtt", out var g) && g.Services.Any(s => p.Extras?.ServicesPresent.Contains(s) == true);
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = FindingStatus.Info,
            Variant = dtt ? "dttFound" : "dttNotFound",
            Impact = 2,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.cpu", p.Cpu.Name),
                new("fact.dttDriver", dtt ? "@found" : "@notFound"),
                new("fact.board", $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim()),
            ],
            Params = new Dictionary<string, string>
            {
                ["cpu"] = p.Cpu.Name,
                ["board"] = $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim(),
                ["laptop"] = p.IsLaptop ? "yes" : "", // the DTT driver comes from the laptop maker there
            },
        };
    }
}

/// <summary>
/// Advisor: AMD fTPM stutter on AM4 (AMD PA-410). Fixed by AGESA 1.2.0.7 (BIOS releases from May 2022). Shown only when
/// the TPM in use is the firmware TPM (manufacturer "AMD").
/// </summary>
public sealed class AmdFtpmCheck : IFindingCheck
{
    public const string Id = "A.amdFtpm";
    public static readonly Version FixedAgesa = new(1, 2, 0, 7);
    public static readonly DateTime FixedBiosDate = new(2022, 5, 1);
    public IReadOnlyList<string> DocIds => [Id];

    /// <summary>A mobile Ryzen (6800H, 7840HS, 5800U, Ryzen AI 9 HX 370), also when it sits in a mini PC without a battery.</summary>
    public static bool IsMobileRyzen(string name) => RegexCache.Get(@"\b\d{4}(U|H|HS|HX)\b|\bRyzen AI\b").IsMatch(name);

    /// <summary>
    /// AM4 desktop: socket name, else Zen/Zen+/Zen 2 (family 17h) or Zen 3 up to Cezanne (family 19h, model &lt; 60h).
    /// Mobile processors (U, H, HS, HX suffix) are excluded, also in mini PCs without a battery.
    /// </summary>
    public static bool IsAm4(CpuInfo cpu, bool laptop)
    {
        if (cpu.Vendor != Vendor.Amd || laptop) return false;
        if (IsMobileRyzen(cpu.Name)) return false;
        if (cpu.Socket.Contains("AM4", StringComparison.OrdinalIgnoreCase)) return true;
        if (cpu.Socket.Contains("AM5", StringComparison.OrdinalIgnoreCase) || cpu.Socket.Contains("TR", StringComparison.OrdinalIgnoreCase)) return false;
        if (cpu.Name.Contains("Threadripper", StringComparison.OrdinalIgnoreCase) || cpu.Name.Contains("EPYC", StringComparison.OrdinalIgnoreCase)) return false;
        return cpu.Family == 0x17 || (cpu.Family == 0x19 && cpu.Model < 0x60);
    }

    /// <summary>
    /// AMD's AGESA 1.2.0.7 threshold belongs to the ComboAM4v2PI line. The older ComboAM4PI line is numbered 1.0.0.x
    /// and would always look older than 1.2.0.7, so it is judged by the BIOS date like the mobile lines.
    /// </summary>
    public static bool IsDesktopAm4Agesa(string? smbiosString) =>
        smbiosString is not null && RegexCache.Get(@"ComboAM4v2PI").IsMatch(smbiosString);

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Cpu is null || !IsAm4(p.Cpu, p.IsLaptop)) yield break;
        var tpm = p.Extras?.TpmManufacturer;
        if (!string.Equals(tpm?.Trim(), "AMD", StringComparison.OrdinalIgnoreCase)) yield break; // dTPM or no TPM: not affected
        // Mobile AGESA lines (CezannePI, RenoirPI) use their own numbering: fall back to the BIOS date for them.
        var agesa = IsDesktopAm4Agesa(p.Extras?.AgesaSource) ? p.Extras?.Agesa : null;
        var date = p.Firmware?.BiosDate;
        FindingStatus status;
        string variant;
        if (agesa is not null) { status = agesa < FixedAgesa ? FindingStatus.Problem : FindingStatus.Ok; variant = "agesa"; }
        else if (date is { } d) { status = d < FixedBiosDate ? FindingStatus.Problem : FindingStatus.Ok; variant = "date"; }
        else { status = FindingStatus.Unknown; variant = "date"; }
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Variant = variant,
            Impact = 3,
            Effects = [Effect.Stutter],
            Facts =
            [
                new("fact.cpu", p.Cpu.Name),
                new("fact.tpmManufacturer", "AMD fTPM"),
                new("fact.agesa", agesa?.ToString() ?? "@unknown"),
                new("fact.bios", $"{p.Firmware?.BiosVersion} ({date:yyyy-MM-dd})"),
            ],
            Params = new Dictionary<string, string>
            {
                ["board"] = $"{p.Firmware?.BoardManufacturer} {p.Firmware?.BoardProduct}".Trim(),
                ["supportUrl"] = BiosAgeCheck.SupportUrl(c.Bios.NormalizeVendor(p.Firmware?.BoardManufacturer)),
            },
        };
    }
}

/// <summary>
/// Advisor: Ryzen memory speed relative to the fabric clock. The lower bounds are AMD's official two-module memory
/// specification (DDR4-3200 for Ryzen 5000, DDR5-5200 for Ryzen 7000). The upper bounds are typical 1:1 limits reported
/// by testers (AM4 FCLK, AM5 UCLK), not an AMD specification; they can differ per processor and BIOS. Slower kits or
/// speeds above that range are information, never a problem.
/// </summary>
public sealed class RyzenMemoryCheck : IPhysicalHardwareCheck
{
    public const string Id = "A.ryzenMemory";
    public IReadOnlyList<string> DocIds => [Id];

    public static (int Low, int High)? SweetSpot(string ramType) => ramType switch
    {
        "DDR4" => (3200, 3800),
        "DDR5" => (5200, 6000),
        _ => null,
    };

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        // A mobile Ryzen in a mini PC is no laptop, but its memory limits are the mobile ones (DDR5-4800 for a 6800H).
        if (p.Cpu?.Vendor != Vendor.Amd || p.IsLaptop || AmdFtpmCheck.IsMobileRyzen(p.Cpu.Name)) yield break;
        if (p.Memory is null)
        {
            if (p.ProbeFailed("Memory")) yield return Finding.NotRead(Id);
            yield break;
        }
        if (p.Memory.Modules.Count == 0) yield break;
        var type = p.Memory.Modules[0].Type;
        if (SweetSpot(type) is not { } range) yield break;
        var configured = p.Memory.Modules.Select(RamSpeed.NormalizeConfigured).ToList();
        var rated = p.Memory.Modules.Select(m => RamSpeed.DecodeRated(m.PartNumber, c)).ToList();
        var speed = configured.All(v => v is not null) ? configured.Min() : null;
        var ratedMin = rated.All(v => v is not null) ? rated.Min() : null;

        FindingStatus status;
        string? variant = null;
        if (speed is null) status = FindingStatus.Unknown;
        else if (speed > range.High) { status = FindingStatus.Info; variant = "aboveSync"; }
        // Below the range because XMP/EXPO is off is A.xmp's job; here only kits that are slow by design. Without a
        // rated speed the two cannot be told apart: Unknown. Four DDR5 modules are limited by AMD's specification
        // (DDR5-3600), so a faster kit would not help: no slowKit advice there.
        else if (speed < range.Low)
        {
            if (ratedMin is null) status = FindingStatus.Unknown;
            else if (ratedMin < range.Low && !(type == "DDR5" && p.Memory.Modules.Count >= 4)) { status = FindingStatus.Info; variant = "slowKit"; }
            else status = FindingStatus.Ok;
        }
        else status = FindingStatus.Ok;

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = status,
            Variant = variant,
            Impact = 2,
            Effects = [Effect.Lows, Effect.Fps],
            Facts =
            [
                new("fact.cpu", p.Cpu.Name),
                new("fact.ramConfigured", speed is null ? "@unknown" : $"{speed} MT/s {type}"),
                new("fact.ramRated", ratedMin is null ? "@unknown" : $"{ratedMin} MT/s"),
                new("fact.ramSweetSpotLow", $"{range.Low} MT/s"),
                new("fact.ramSweetSpotHigh", $"{range.High} MT/s"),
            ],
            Params = new Dictionary<string, string>
            {
                ["speed"] = speed?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["low"] = range.Low.ToString(CultureInfo.InvariantCulture),
                ["high"] = range.High.ToString(CultureInfo.InvariantCulture),
                ["am4"] = type == "DDR4" ? "yes" : "",
                ["am5"] = type == "DDR5" ? "yes" : "",
            },
        };
    }
}

/// <summary>Advisor: BIOS older than a year (information; newer releases carry microcode, AGESA and stability fixes).</summary>
public sealed class BiosAgeCheck : IPhysicalHardwareCheck
{
    public const string Id = "A.biosAge";
    public const int MaxAgeDays = 365;
    public IReadOnlyList<string> DocIds => [Id];

    private readonly Func<DateTime> _today;

    /// <param name="today">Clock for tests; default: the local date.</param>
    public BiosAgeCheck(Func<DateTime>? today = null) => _today = today ?? (() => DateTime.Today);

    public static string SupportUrl(string? vendor) => vendor switch
    {
        "ASUS" => "https://www.asus.com/support/download-center/",
        "MSI" => "https://www.msi.com/support",
        "Gigabyte" => "https://www.gigabyte.com/Support/Consumer",
        "ASRock" => "https://www.asrock.com/support/index.asp",
        _ => "",
    };

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Firmware?.BiosDate is not { } date) yield break;
        var age = (int)(_today() - date.Date).TotalDays;
        var vendor = c.Bios.NormalizeVendor(p.Firmware.BoardManufacturer);
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = age > MaxAgeDays ? FindingStatus.Info : FindingStatus.Ok,
            Impact = 1,
            Effects = [Effect.Stability],
            Facts =
            [
                new("fact.board", $"{p.Firmware.BoardManufacturer} {p.Firmware.BoardProduct}".Trim()),
                new("fact.bios", p.Firmware.BiosVersion),
                new("fact.biosDate", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("fact.biosAgeDays", age.ToString(CultureInfo.InvariantCulture)),
            ],
            Params = new Dictionary<string, string>
            {
                ["board"] = $"{p.Firmware.BoardManufacturer} {p.Firmware.BoardProduct}".Trim(),
                ["supportUrl"] = SupportUrl(vendor),
                ["laptop"] = p.IsLaptop ? "yes" : "",
            },
        };
    }
}

/// <summary>Advisor: desktop with the integrated GPU enabled but unused (no display on it). Information only.</summary>
public sealed class IgpuUnusedCheck : IFindingCheck
{
    public const string Id = "A.igpuUnused";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.IsLaptop || p.Gpus is null || p.Displays is null) yield break;
        var igpu = p.Gpus.FirstOrDefault(g => g.Kind == GpuKind.Integrated);
        var dgpu = p.Gpus.FirstOrDefault(g => g.Kind == GpuKind.Discrete);
        if (igpu is null || dgpu is null) yield break;
        if (p.Displays.Any(d => string.Equals(d.AdapterName, igpu.Name, StringComparison.Ordinal))) yield break; // F3 covers this
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = FindingStatus.Info,
            Impact = 0,
            Effects = [Effect.None],
            Facts =
            [
                new("fact.integratedGpu", igpu.Name),
                new("fact.discreteGpu", dgpu.Name),
            ],
            Params = new Dictionary<string, string> { ["igpu"] = igpu.Name, ["dgpu"] = dgpu.Name },
        };
    }
}

/// <summary>
/// Advisor: guided AMD Software (Adrenalin) settings for a Radeon card. AMD's settings library (ADLX) is a native library
/// this app does not include, so the app explains instead of changing them. Workstation cards (Radeon Pro, FirePro,
/// Instinct) use AMD Software: PRO Edition or have no display driver UI, so they are skipped.
/// </summary>
public sealed class AmdAdrenalinCheck : IFindingCheck
{
    public const string Id = "A.amdAdrenalin";
    public IReadOnlyList<string> DocIds => [Id];

    public static bool IsWorkstation(string name) => RegexCache.Get(@"Radeon(\(TM\))?\s+Pro\b|FirePro|Instinct").IsMatch(name);

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        var radeon = p.Gpus?.FirstOrDefault(g => g.Vendor == Vendor.Amd && g.Kind == GpuKind.Discrete && !IsWorkstation(g.Name));
        if (radeon is null) yield break;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = FindingStatus.Info,
            Impact = 2,
            Effects = [Effect.Latency, Effect.Fps],
            Facts =
            [
                new("fact.gpu", radeon.Name),
                new("fact.driverVersion", radeon.DriverVersion ?? "@unknown"),
            ],
            Params = new Dictionary<string, string> { ["gpu"] = radeon.Name },
        };
    }
}

/// <summary>
/// F16: many third-party programs start with Windows. Each one costs boot time and some memory; launchers, RGB and
/// update tools also wake up in the background while you play. Information with the list.
/// </summary>
public sealed class StartupCountCheck : IFindingCheck
{
    public const string Id = "F16.startupPrograms";
    public const int Threshold = 8;
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Extras?.StartupPrograms is not { } programs)
        {
            if (p.Extras is null && p.ProbeFailed("Extras")) yield return Finding.NotRead(Id);
            yield break;
        }
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = programs.Count > Threshold ? FindingStatus.Info : FindingStatus.Ok,
            Impact = 1,
            Effects = [Effect.Stutter],
            Facts =
            [
                new("fact.startupCount", programs.Count.ToString(CultureInfo.InvariantCulture)),
                .. programs.Take(12).Select(n => new Fact("fact.startupProgram", n)),
            ],
            Params = new Dictionary<string, string> { ["count"] = programs.Count.ToString(CultureInfo.InvariantCulture) },
        };
    }
}

/// <summary>
/// F23: throttling under load, from the last throttle check on the Health page. CPU: guaranteed performance below 100 %
/// in more than 10 % of busy samples. GPU: thermal limit is a problem; the power limit is normal for graphics cards under
/// full load and only information.
/// </summary>
public sealed class ThrottleCheck : IFindingCheck
{
    public const string Id = "F23.throttling";
    public IReadOnlyList<string> DocIds => [Id];

    /// <summary>After this, the last measurement is shown as out of date and asks for a new one.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Extras?.LastThrottle is not { } t) yield break; // not measured yet: the Health page offers the check
        var gpuThermal = t.GpuThermal;
        // Without enough processor load the run says nothing about the processor: not "no throttling".
        var status = t.CpuThrottled || gpuThermal || t.GpuSlowdown ? FindingStatus.Problem
            : t.GpuPower || !t.CpuJudged ? FindingStatus.Info : FindingStatus.Ok;
        // An old measurement says little about today (dust, a new cooler, another season): shown, but not as a problem.
        var stale = DateTimeOffset.Now - t.Measured > MaxAge;
        if (stale && status == FindingStatus.Problem) status = FindingStatus.Info;
        string Share(string reason) => t.GpuReasonShare.TryGetValue(reason, out var v) ? $"{v * 100:0} %" : "@unknown";
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = status,
            Variant = stale ? "stale" : t.CpuThrottled ? "cpu" : gpuThermal ? "gpuThermal" : t.GpuSlowdown ? "gpuSlowdown"
                : t.GpuPower ? "gpuPower" : !t.CpuJudged ? "noLoad" : null,
            Impact = 4,
            Effects = [Effect.Fps, Effect.Lows],
            Facts =
            [
                new("fact.measuredAt", t.Measured.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                new("fact.samples", t.Samples.ToString(CultureInfo.InvariantCulture)),
                new("fact.cpuBusyShare", $"{t.CpuBusyShare * 100:0} %"),
                new("fact.cpuLimitedShare", $"{t.CpuLimitedShare * 100:0} %"),
                new("fact.cpuLowestLimit", t.CpuLowestLimit is { } l ? $"{l:0} %" : "@unknown"),
                new("fact.gpuThermalShare", Share("thermal")),
                new("fact.gpuPowerShare", Share("powerLimit")),
                new("fact.gpuSlowdownShare", t.GpuReasonShare.Count == 0 ? "@unknown"
                    : $"{t.GpuReasonShare.Where(r => r.Key is "powerBrake" or "hardwareSlowdown").Sum(r => r.Value) * 100:0} %"),
                new("fact.gpuMaxTemp", t.GpuMaxTempC is { } temp ? $"{temp} °C" : "@unknown"),
            ],
            Params = new Dictionary<string, string>
            {
                ["cpu"] = t.CpuThrottled ? "yes" : "",
                ["gpuThermal"] = gpuThermal ? "yes" : "",
                ["laptop"] = p.IsLaptop ? "yes" : "",
            },
        };
    }
}

/// <summary>F28: drive health from Windows Storage Management (S.M.A.R.T. / NVMe health log).</summary>
public sealed class DiskHealthCheck : IPhysicalHardwareCheck
{
    public const string Id = "F28.diskHealth";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Extras is null && p.ProbeFailed("Extras")) yield return Finding.NotRead(Id);
        foreach (var d in p.Extras?.DiskHealth ?? [])
        {
            var status = d.IsProblem ? FindingStatus.Problem : d.Health == "Unknown" ? FindingStatus.Unknown : FindingStatus.Ok;
            yield return new Finding
            {
                Id = Id,
                InstanceKey = d.DeviceId.Length > 0 ? $"disk{d.DeviceId}" : d.Name,
                Subject = d.Name,
                Kind = FindingKind.Finding,
                Status = status,
                Critical = status == FindingStatus.Problem && d.IsCritical,
                Variant = d.MediaType == "HDD" ? "hdd" : "ssd",
                Impact = 2,
                Effects = [Effect.Stability],
                Facts =
                [
                    new("fact.disk", $"{d.Name} ({d.MediaType}, {d.BusType})"),
                    new("fact.diskHealth", $"@health{d.Health}"),
                    new("fact.diskTemperature", d.TemperatureC is { } t ? $"{t} °C" : "@unknown"),
                    new("fact.diskWear", d.WearPercent is { } w ? $"{w} %" : "@unknown"),
                    new("fact.diskUncorrectedErrors", d.ReadErrorsUncorrected is null && d.WriteErrorsUncorrected is null ? "@unknown"
                        : ((d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0)).ToString(CultureInfo.InvariantCulture)),
                    new("fact.diskPowerOnHours", d.PowerOnHours?.ToString(CultureInfo.InvariantCulture) ?? "@unknown"),
                ],
                Params = new Dictionary<string, string> { ["disk"] = d.Name },
            };
        }
    }
}

/// <summary>
/// F11: programs using noticeable processor time in the background (sampled for 3 seconds during the scan, while only
/// this app is in front). Essential Windows processes are not listed.
/// </summary>
public sealed class BackgroundCpuCheck : IFindingCheck
{
    public const string Id = "F11.backgroundCpu";
    public const double Threshold = 0.05;
    public IReadOnlyList<string> DocIds => [Id];

    private static readonly HashSet<string> Essential = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "dwm", "fontdrvhost", "explorer",
        "svchost", "audiodg", "Memory Compression", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ctfmon",
        "WmiPrvSE", "taskhostw", "RuntimeBroker", "sihost", "dllhost", "conhost", "spoolsv", "Tunero", "PCOptimizer",
    };

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Extras?.BackgroundCpu is not { } sample) yield break;
        var hogs = sample.Where(x => x.CpuShare >= Threshold && !Essential.Contains(x.Name)).Take(8).ToList();
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = hogs.Count > 0 ? FindingStatus.Info : FindingStatus.Ok,
            Impact = 2,
            Effects = [Effect.Lows, Effect.Stutter],
            Facts = hogs.Count > 0
                ? hogs.Select(h => new Fact("fact.backgroundProcess", $"{h.Name}: {h.CpuShare * 100:0.#} %")).ToList()
                : [new Fact("fact.backgroundProcess", "@none")],
            Params = new Dictionary<string, string>
            {
                ["names"] = string.Join(", ", hogs.Select(h => h.Name)),
                ["defender"] = hogs.Any(h => h.Name.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase)) ? "yes" : "",
            },
        };
    }
}

/// <summary>Advisor: Windows installed on a hard disk drive.</summary>
public sealed class SystemOnHddCheck : IPhysicalHardwareCheck
{
    public const string Id = "A.systemHdd";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Storage is null ? p.ProbeFailed("Storage") : p.Storage.DisksUnread)
        {
            yield return Finding.NotRead(Id);
            yield break;
        }
        var system = p.Storage?.Volumes.FirstOrDefault(v => v.IsSystem);
        if (system?.DiskNumber is null) yield break;
        var disk = p.Storage!.DiskOf(system);
        if (disk is null) yield break;
        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Advisor,
            Status = disk.MediaType == "HDD" ? FindingStatus.Problem : disk.MediaType == "Unspecified" ? FindingStatus.Unknown : FindingStatus.Ok,
            Impact = 3,
            Effects = [Effect.Stutter],
            Facts =
            [
                new("fact.volume", system.Root),
                new("fact.disk", $"{disk.FriendlyName} ({disk.MediaType}, {disk.BusType})"),
            ],
            Params = new Dictionary<string, string> { ["disk"] = disk.FriendlyName },
        };
    }
}
