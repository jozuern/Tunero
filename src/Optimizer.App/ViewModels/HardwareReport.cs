using System.Globalization;
using System.Text;
using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

/// <summary>
/// Turns the HardwareProfile into the System info page and the --report file: a few summary tiles, then one section per
/// area with the rows a user reads, plus technical rows (IDs, raw values) shown only on request. Row labels and words
/// inside values come from labels.json ("hw.*" and "value.*" keys, English and German); names read from the hardware
/// (models, adapters, volumes) are shown as they are.
/// </summary>
public static class HardwareReport
{
    public static List<HwSection> Build(HardwareProfile p, Loc l)
    {
        var f = new Fmt(l.Language);
        var sections = new List<HwSection>();

        sections.Add(new HwSection(l["Hw_Os"],
        [
            f.I("version", $"Windows 11 {p.Os.DisplayVersion}"), f.I("edition", p.Os.Edition), f.I("build", p.Os.BuildString),
            f.I("architecture", ArchitectureName(p.Os.NativeArchitecture)), f.I("formFactor", f.T(p.IsLaptop ? "laptop" : "desktop")),
        ])
        {
            Icon = SymbolRegular.Window24,
            Technical =
            [
                f.I("elevated", p.Elevation?.IsElevated), f.I("managed", p.Managed?.IsManaged),
                f.I("insider", p.Os.FlightingActive), f.I("processUser", p.Elevation?.ProcessUser), f.I("sessionUser", p.Elevation?.SessionUser),
                f.I("adminProtection", p.Elevation?.AdministratorProtection), f.I("hypervisor", p.System?.HypervisorPresent),
            ],
        });

        if (p.Cpu is { } c)
        {
            sections.Add(new HwSection(l["Hw_Cpu"],
            [
                f.I("name", c.Name), f.I("vendor", VendorName(c.Vendor)), f.I("coresThreads", f.F("coresThreadsValue", c.Cores, c.Threads)),
                f.I("socket", c.Socket),
                f.I("l3", c.L3Domains.Count == 0 ? null : string.Join(" + ", c.L3Domains.Select(d => $"{d.SizeBytes >> 20} MB"))),
                // Performance cores are the ones in the highest efficiency class, every other class counts as efficiency cores.
                .. c.IsHybrid ? (SummaryItem[])[f.I("coreTypes", f.F("coreTypesValue", c.PerformanceCores, c.CoresByEfficiencyClass.Values.Sum() - c.PerformanceCores))] : [],
            ])
            {
                Icon = SymbolRegular.DeveloperBoard24,
                Technical =
                [
                    f.I("maxClock", c.MaxClockMhz > 0 ? $"{c.MaxClockMhz} MHz" : null),
                    f.I("familyModel", $"{c.Family} / {c.Model} (0x{c.Model:X}) / {c.Stepping}"),
                    f.I("microcodeRunning", c.MicrocodeCurrent is { } m1 ? $"0x{m1:X}" : null),
                    f.I("microcodeBios", c.MicrocodeBios is { } m2 ? $"0x{m2:X} ({c.MicrocodeSource})" : null),
                    f.I("hybrid", c.IsHybrid ? (object)string.Join(", ", c.CoresByEfficiencyClass.Select(kv => f.F("efficiencyClass", kv.Key, kv.Value))) : false),
                ],
            });
        }

        foreach (var g in p.Gpus ?? [])
        {
            var items = new List<SummaryItem>
            {
                f.I("kind", f.V(g.Kind switch
                {
                    GpuKind.Discrete => "value.gpuDiscrete", GpuKind.Integrated => "value.gpuIntegrated", GpuKind.Basic => "value.gpuBasic",
                    GpuKind.Virtual => "value.gpuVirtual", _ => "value.unknown",
                })),
                f.I("vendor", VendorName(g.Vendor)),
                f.I("driver", $"{g.DriverProvider} {g.NvidiaDriverVersion ?? g.DriverVersion}".Trim()) with { Detail = g.DriverDate?.ToString("yyyy-MM-dd") },
                f.I("videoMemory", f.Gb(g.VramBytes)), f.I("largestBar", f.Gb(g.LargestBarBytes)),
                f.I("pcieCard", g.CardLink is { } cl ? $"Gen {cl.CurrentGen} x{cl.CurrentWidth} / Gen {cl.MaxGen} x{cl.MaxWidth}" : null),
                f.I("hags", g.HagsEnabled),
            };
            var technical = new List<SummaryItem>
            {
                f.I("pcieSlot", g.PlatformPortLink is { } pl ? $"Gen {pl.MaxGen} x{pl.MaxWidth}" : null), f.I("switchHops", g.SwitchHopsSkipped),
            };
            // Interrupt mode of the card (the MSI tweak): what the device supports and what the registry sets.
            if (p.Extras?.MsiDevices.FirstOrDefault(m => string.Equals(m.InstanceId, g.PnpDeviceId, StringComparison.OrdinalIgnoreCase)) is { } msi)
                technical.Add(f.I("msi", f.F("msiValue", msi.MessageMaximum?.ToString() ?? "?", msi.MsiSupportedValue is { } v ? v.ToString() : f.T("notSet"))));
            if (g.Vendor == Vendor.Nvidia && p.Extras?.Nvidia is { } nv)
            {
                technical.Add(f.I("batteryBoost", nv.BatteryBoostFps is > 0 ? $"{nv.BatteryBoostFps} FPS" : f.T("notSet")));
                technical.Add(f.I("gsyncGlobal", nv.GsyncGlobalMode?.ToString() ?? f.T("notSet")));
            }
            technical.Add(f.I("pnp", g.PnpDeviceId));
            sections.Add(new HwSection($"{l["Hw_Gpu"]}: {g.Name}", items) { Icon = SymbolRegular.DesktopPulse24, Technical = technical });
        }

        if (p.Memory is { } mem)
        {
            var items = new List<SummaryItem> { f.I("total", f.Gb(mem.TotalBytes)) };
            items.AddRange(mem.Modules.Select(m => new SummaryItem(m.DeviceLocator, f.F("moduleValue", f.Gb(m.CapacityBytes), m.Type, m.ConfiguredMts?.ToString() ?? "?"))
            {
                Detail = string.Join(", ", new[] { $"{m.Manufacturer} {m.PartNumber.Trim()}".Trim(), m.SpeedMts is { } spd ? f.F("moduleRated", spd) : "", m.BankLabel }
                    .Where(s => s.Length > 0)),
            }));
            sections.Add(new HwSection(l["Hw_Memory"], items) { Icon = SymbolRegular.Ram20 });
        }

        var displays = p.Displays ?? [];
        foreach (var d in displays)
        {
            var items = new List<SummaryItem>
            {
                f.I("mode", $"{d.Width}×{d.Height} @ {d.CurrentRefresh.Hz:0} Hz"),
                f.I("maxOffered", $"{d.MaxOfferedRefreshAtCurrentResolution} Hz"),
                f.I("hdrState", f.V(d.HdrEnabled ? "value.on" : d.HdrSupported ? "value.hdrSupportedOff" : "value.hdrNotSupported")),
            };
            // G-SYNC in one word; the four driver flags behind it are technical rows.
            var vrrState = p.Extras?.Nvidia?.Vrr.TryGetValue(d.GdiName, out var vrr) == true ? vrr : null;
            if (vrrState is { } vs)
                items.Add(f.I("gsyncState", f.V(!vs.Possible ? "value.notPossible" : vs.Enabled ? "value.on" : "value.off")));
            items.Add(f.I("connectedTo", d.AdapterName));
            items.Add(f.I("internal", d.IsInternal));
            sections.Add(new HwSection(d.HasOwnName ? $"{l["Hw_Display"]}: {d.FriendlyName}" : DisplayName(d, displays, l), items)
            {
                Icon = SymbolRegular.Desktop24,
                Technical =
                [
                    .. vrrState is { } v2 ? (SummaryItem[])[f.I("gsync", f.F("gsyncValue", f.Value(v2.Possible), f.Value(v2.Enabled), f.Value(v2.Requested), f.Value(v2.InVrrMode)))] : [],
                    f.I("refreshExact", $"{d.CurrentRefresh.Hz:0.###} Hz ({d.CurrentRefresh.Numerator}/{d.CurrentRefresh.Denominator})"),
                    f.I("edid", d.Edid is { } e ? f.F("edidValue", $"{e.ManufacturerId} {e.ProductCode:X4}", e.MinVHz, e.MaxVHz, $"{e.PreferredWidth}×{e.PreferredHeight}", e.PreferredRefreshHz) : null),
                    f.I("gdi", d.GdiName),
                ],
            });
        }

        if (p.Firmware is { } fw)
        {
            sections.Add(new HwSection(l["Hw_Firmware"],
            [
                f.I("board", $"{fw.BoardManufacturer} {fw.BoardProduct}".Trim()),
                f.I("bios", $"{fw.BiosVendor} {fw.BiosVersion}".Trim()) with { Detail = fw.BiosDate?.ToString("yyyy-MM-dd") },
                f.I("uefi", fw.IsUefi), f.I("secureBoot", fw.SecureBoot), f.I("tpm", fw.TpmPresent == TriState.Yes ? fw.TpmSpecVersion : f.Value(fw.TpmPresent)),
                f.I("tpmReady", fw.TpmReady),
                f.I("vbs", f.V(fw.VbsStatus switch { 2 => "value.running", 1 => "value.configured", 0 => "value.off", _ => "value.unknown" })),
                f.I("hvci", fw.HvciRunning),
            ])
            {
                Icon = SymbolRegular.ShieldCheckmark24,
                Technical = [f.I("credentialGuard", fw.CredentialGuardRunning), f.I("mbec", fw.MbecAvailable), f.I("dma", fw.DmaProtectionAvailable), f.I("systemDisk", fw.SystemDiskPartitionStyle)],
            });
        }

        if (p.Power is { } pw)
        {
            var personality = f.V(pw.Personality switch
            {
                PowerPersonality.PowerSaver => "value.personalityPowerSaver", PowerPersonality.Balanced => "value.personalityBalanced",
                PowerPersonality.HighPerformance => "value.personalityHighPerformance", _ => "value.personalityUnknown",
            });
            sections.Add(new HwSection(l["Hw_Power"],
            [
                f.I("plan", pw.ActiveSchemeName) with { Detail = personality },
                f.I("processorState", $"{pw.MaxProcessorStateAc} % / {pw.MinProcessorStateAc} %"),
                f.I("boostMode", pw.BoostModeAc switch { null => null, 0 => f.V("value.off"), <= 6 and var boost => f.V($"value.boost{boost}"), var boost => boost.ToString() }),
                f.I("coreParking", pw.CoreParkingMinCoresAc is { } cp ? $"{cp} %" : null),
                f.I("onAc", pw.OnAc), f.I("energySaver", pw.EnergySaverOn), f.I("modernStandby", pw.ModernStandby),
                // A desktop has no battery: those rows would only say "Unknown".
                .. p.Battery is null && pw.BatteryPercent is null ? (SummaryItem[])[] :
                [
                    f.I("battery", pw.BatteryPercent is { } b ? $"{b} %" : null),
                    f.I("batteryCapacity", p.Battery is { } bh ? $"{bh.FullChargedMwh / 1000.0:0.0} / {bh.DesignedMwh / 1000.0:0.0} Wh ({Math.Round(bh.Health * 100)} %)" : null),
                ],
            ])
            {
                Icon = SymbolRegular.Flash24,
                Technical = [f.I("schemeGuid", pw.ActiveScheme)],
            });
        }

        if (p.Storage is { } st)
        {
            var items = st.Disks.OrderBy(d => d.Number).Select(d => new SummaryItem(d.FriendlyName, $"{d.MediaType}, {d.BusType}, {f.Gb(d.SizeBytes)}")
            {
                Detail = string.Join(", ", new[] { f.F("disk", d.Number), f.V($"value.health{d.Health}", d.Health), d.IsSmrSuspect ? f.T("smr") : "" }.Where(s => s.Length > 0)),
            }).ToList();
            items.AddRange(st.Volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase).Select(v => new SummaryItem(
                string.IsNullOrWhiteSpace(v.Label) ? v.Root : $"{v.Root} ({v.Label.Trim()})",
                f.F("freeOf", f.Gb(v.FreeBytes), f.Gb(v.SizeBytes)))
            {
                Detail = v.DiskNumber is { } n ? f.F("disk", n) : null,
                Fill = v.SizeBytes > 0 ? Math.Clamp(1 - v.FreeFraction, 0, 1) : null,
            }));
            sections.Add(new HwSection(l["Hw_Storage"], items) { Icon = SymbolRegular.Storage24 });
        }

        if (p.Network is { } net)
        {
            sections.Add(new HwSection(l["Hw_Network"], net.Select(n => new SummaryItem(n.Name, n.IsUp ? $"{n.SpeedBps / 1_000_000} Mbit/s" : f.V("value.disconnected"))
            {
                Detail = $"{n.Description}, {n.Type}",
            }).ToList()) { Icon = SymbolRegular.NetworkCheck24 });
        }

        if (p.Software is { } sw)
        {
            sections.Add(new HwSection(l["Hw_Software"],
            [
                f.I("antiCheats", sw.AntiCheats.Count == 0 ? f.V("value.none") : string.Join("\n", sw.AntiCheats.Select(a => a.DisplayName))),
                f.I("launchers", sw.Launchers.Count == 0 ? f.V("value.none") : string.Join(", ", sw.Launchers)),
                f.I("gameLibraries", sw.GameLibraryPaths.Count == 0 ? f.V("value.none") : string.Join("\n", sw.GameLibraryPaths)),
            ])
            {
                Icon = SymbolRegular.Games24,
                Technical = sw.AntiCheats.Count == 0 ? [] : [f.I("antiCheatServices", string.Join("\n", sw.AntiCheats.Select(a => $"{a.DisplayName}: {string.Join(", ", a.FoundServices)}")))],
            });
        }

        if (p.ProbeErrors.Count > 0)
            sections.Add(new HwSection(l["Hw_Errors"], p.ProbeErrors.Select(e => new SummaryItem(e.Key, e.Value)).ToList()) { Icon = SymbolRegular.ErrorCircle24 });
        return sections;
    }

    /// <summary>As Windows writes it (System > About): "x64", "Arm64"; the enum's own names start with a capital X.</summary>
    private static string ArchitectureName(Optimizer.Core.Platform.CpuArchitecture a) => a switch
    {
        Optimizer.Core.Platform.CpuArchitecture.X64 => "x64",
        Optimizer.Core.Platform.CpuArchitecture.Arm64 => "Arm64",
        _ => a.ToString(),
    };

    /// <summary>
    /// A display's name: the monitor's own one, else "Built-in display" for the laptop panel and "Display 2" for others
    /// (numbered in the order Windows lists them), instead of a generic "Display" that tells the displays apart from nothing.
    /// </summary>
    private static string DisplayName(DisplayInfo d, IReadOnlyList<DisplayInfo> all, Loc l)
    {
        if (d.HasOwnName) return d.FriendlyName;
        if (d.IsInternal) return l["Hw_BuiltInDisplay"];
        var index = 0;
        while (index < all.Count && !ReferenceEquals(all[index], d)) index++;
        return l.Format("Hw_DisplayNumber", index + 1);
    }

    /// <summary>The tiles at the top of the System info page: what a user looks up most, one line each plus a detail line.</summary>
    public static List<HwTile> Tiles(HardwareProfile p, Loc l)
    {
        var f = new Fmt(l.Language);
        var tiles = new List<HwTile>();
        if (p.Cpu is { } c)
            tiles.Add(new HwTile(l["Hw_Cpu"], c.ShortName, f.F("coresThreadsValue", c.Cores, c.Threads), SymbolRegular.DeveloperBoard24));
        if (p.Gpus?.Where(g => g.Kind is not GpuKind.Virtual).ToList() is { Count: > 0 } gpus)
        {
            var main = gpus.FirstOrDefault(g => g.Kind == GpuKind.Discrete) ?? gpus[0];
            var detail = string.Join(", ", new[] { main.VramBytes is { } v ? f.Gb(v) : "", (main.NvidiaDriverVersion ?? main.DriverVersion) is { } dv ? f.F("driverValue", dv) : "" }.Where(s => s.Length > 0));
            tiles.Add(new HwTile(l["Hw_Gpu"], string.Join("\n", gpus.Select(g => g.Name)), detail, SymbolRegular.DesktopPulse24));
        }
        if (p.Memory is { } m)
        {
            var first = m.Modules.FirstOrDefault();
            // Without a reported speed (virtual machines, some firmware) the line leaves it out instead of showing " MT/s".
            var detail = first is null ? "" : RamSpeed.NormalizeConfigured(first) is { } speed
                ? f.F("memoryValue", first.Type, speed, m.Modules.Count)
                : f.F("memoryNoSpeedValue", first.Type, m.Modules.Count);
            tiles.Add(new HwTile(l["Hw_Memory"], f.Gb(m.TotalBytes), detail, SymbolRegular.Ram20));
        }
        if (p.Displays is { Count: > 0 } d)
            tiles.Add(new HwTile(d.Count == 1 ? l["Hw_Display"] : l["Hw_Displays"], string.Join("\n", d.Select(x => DisplayName(x, d, l))),
                string.Join("\n", d.Select(x => $"{x.Width}×{x.Height}, {x.CurrentRefresh.Hz:0} Hz")), SymbolRegular.Desktop24));
        if (p.Firmware is { } fw)
            tiles.Add(new HwTile(l["Hw_Board"], $"{fw.BoardManufacturer} {fw.BoardProduct}".Trim(), $"BIOS {fw.BiosVersion}".Trim(), SymbolRegular.DeveloperBoardLightning20));
        tiles.Add(new HwTile(l["Hw_Os"], $"Windows 11 {p.Os.DisplayVersion}", $"{p.Os.Edition}, Build {p.Os.BuildString}", SymbolRegular.Window24));
        return tiles;
    }

    /// <summary>Plain text of all sections (technical rows included) for the clipboard and the --report file.</summary>
    public static string ToText(IEnumerable<HwSection> sections, string bullet = "")
    {
        var sb = new StringBuilder();
        foreach (var s in sections)
        {
            sb.AppendLine(bullet.Length > 0 ? $"### {s.Title}" : s.Title);
            foreach (var i in s.Items.Concat(s.Technical))
            {
                var value = i.Value.Replace("\n", "; ");
                if (i.Detail is { Length: > 0 } detail) value += $" ({detail.Replace("\n", "; ")})";
                sb.AppendLine($"{bullet}{i.Label}: {value}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string VendorName(Vendor v) => v switch
    {
        Vendor.Nvidia => "NVIDIA", Vendor.Amd => "AMD", Vendor.Intel => "Intel", Vendor.Microsoft => "Microsoft", Vendor.Qualcomm => "Qualcomm",
        _ => v.ToString(),
    };

    /// <summary>Label and value formatting in one language.</summary>
    private sealed class Fmt(string lang)
    {
        public string T(string key) => Labels.Current.Get(lang, $"hw.{key}");

        public string V(string key, string? fallback = null) =>
            Labels.Current.Has(lang, key) ? Labels.Current.Get(lang, key) : fallback ?? Labels.Current.Get(lang, "value.unknown");

        public string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

        public SummaryItem I(string key, object? v) => new(T(key), Value(v));

        public string Value(object? v) => v switch
        {
            null => V("value.unknown"),
            bool b => V(b ? "value.yes" : "value.no"),
            TriState t => V(t switch { TriState.Yes => "value.yes", TriState.No => "value.no", _ => "value.unknown" }),
            string { Length: 0 } => V("value.unknown"),
            _ => v.ToString() ?? V("value.unknown"),
        };

        public string Gb(long? b) => b is { } x ? RebarCheck.FormatBytes(x) : V("value.unknown");
    }
}
