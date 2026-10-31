using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;

namespace Optimizer.Core.Tweaks;

/// <summary>
/// Builds the fact set for tweak conditions from the hardware profile, the findings and a few registry values
/// (user values come from the session user's hive). Facts that cannot be determined are left out (= unknown).
/// </summary>
public static class FactsBuilder
{
    /// <param name="ignored">
    /// The results the user ignores (<see cref="IgnoredResults.Key"/>): they are not in the facts, so nothing is
    /// recommended for them, in the window or by --apply-recommended.
    /// </param>
    public static Facts Build(HardwareProfile p, IReadOnlyList<Finding> findings, CatalogData catalog, IRegistryRoots? registry,
        IReadOnlyCollection<string>? ignored = null)
    {
        findings = IgnoredResults.Without(findings, ignored ?? []);
        var f = new Facts();
        f.Set("os.build", p.Os.Build);
        f.Set("os.insider", p.Os.FlightingActive);
        f.Set("os.edition", p.Os.Edition.Length > 0 ? p.Os.Edition : null);
        f.Set("elevated", p.Elevation?.IsElevated);
        f.Set("device.managed", p.Managed?.IsManaged);
        f.Set("system.laptop", p.System is null ? null : p.IsLaptop);

        if (p.Cpu is { } cpu)
        {
            f.Set("cpu.vendor", cpu.Vendor.ToString().ToLowerInvariant());
            f.Set("cpu.hybrid", cpu.IsHybrid);
            f.Set("cpu.x3dMultiCcd", X3d.Classify(cpu, catalog) == X3dLayout.MultiCcdAsymmetric);
            f.Set("cpu.cores", cpu.Cores);
        }

        if (p.Gpus is { } gpus)
        {
            var discrete = gpus.Where(g => g.Kind == GpuKind.Discrete).ToList();
            f.Set("gpu.hasDiscrete", discrete.Count > 0);
            f.Set("gpu.hasIntegrated", gpus.Any(g => g.Kind == GpuKind.Integrated));
            f.Set("gpu.discreteVendor", discrete.FirstOrDefault()?.Vendor.ToString().ToLowerInvariant());
            f.Set("gpu.hasNvidia", gpus.Any(g => g.Vendor == Vendor.Nvidia && g.Kind == GpuKind.Discrete));
            f.Set("gpu.hasAmd", gpus.Any(g => g.Vendor == Vendor.Amd && g.Kind == GpuKind.Discrete));
            // appliesTo.gpuVendor "intel" reads this fact (Intel Arc); before, the name had no fact and never matched.
            f.Set("gpu.hasIntel", gpus.Any(g => g.Vendor == Vendor.Intel && g.Kind == GpuKind.Discrete));
            // HAGS needs WDDM 2.7+ and a supporting GPU/driver; every current discrete GPU qualifies.
            f.Set("gpu.supportsHags", discrete.Count > 0);
            // DLSS Frame Generation (RTX 40/50) requires HAGS.
            f.Set("gpu.supportsFrameGeneration", discrete.Any(g => RegexCache.Get(@"RTX\s*(40|50)\d{2}").IsMatch(g.Name)));
            f.Set("gpu.hagsEnabled", gpus.FirstOrDefault()?.HagsEnabled switch { TriState.Yes => true, TriState.No => false, _ => null });
        }

        // VbsStatus is -1 when Win32_DeviceGuard could not be read: both facts stay unknown then.
        if (p.Firmware is { VbsStatus: >= 0 } fw)
        {
            f.Set("cpu.mbec", fw.MbecAvailable);
            f.Set("vbs.running", fw.VbsRunning);
        }
        if (p.Memory is { } mem) f.Set("memory.totalGb", Math.Round(mem.TotalBytes / (double)(1L << 30)));
        if (p.Power is { } pw)
        {
            f.Set("power.modernStandby", pw.ModernStandby);
            f.Set("power.personality", pw.Personality.ToString().ToLowerInvariant());
            if (pw.OnAc is { } onAc) f.Set("power.onAc", onAc); // ACLineStatus 255: the fact stays unknown
        }
        if (p.Storage is { } st)
        {
            f.Set("storage.hasSsd", st.Disks.Any(d => d.MediaType == "SSD"));
            f.Set("storage.hasHdd", st.Disks.Any(d => d.MediaType == "HDD"));
            f.Set("storage.ssdOnly", st.Disks.Count > 0 && st.Disks.All(d => d.MediaType == "SSD" || d.BusType == "USB"));
        }
        if (p.Software is { } sw)
        {
            var strict = sw.AntiCheats.Where(a => catalog.AntiCheat.AntiCheats.FirstOrDefault(s => s.Id == a.Id)?.BlocksVbsOff == true).ToList();
            f.Set("anticheat.strict", strict.Count > 0);
            f.Set("anticheat.strictNames", string.Join(", ", strict.Select(a => a.DisplayName)));
            f.Set("anticheat.any", sw.AntiCheats.Count > 0);
        }
        if (p.Network is { } net) f.Set("network.wifiOnly", net.Any(n => n.IsUp) && net.Where(n => n.IsUp).All(n => n.Type == "Wi-Fi"));

        foreach (var group in findings.GroupBy(x => x.Id))
        {
            f.Set($"finding.{group.Key}.problem", group.Any(x => x.Status == FindingStatus.Problem));
            f.Set($"finding.{group.Key}.status", group.First().Status.ToString().ToLowerInvariant());
        }

        if (registry is not null) AddRegistryFacts(f, registry);
        return f;
    }

    private static void AddRegistryFacts(Facts f, IRegistryRoots r)
    {
        try
        {
            var dx = RegistryValue.Read(r, Hive.User, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
            var tokens = RegistryTokenAction.Parse(dx.Data);
            f.Set("display.autoHdr", tokens.TryGetValue("AutoHDREnable", out var hdr) && hdr == "1");

            var hist = RegistryValue.Read(r, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled");
            f.Set("gamedvr.backgroundRecording", hist is { Existed: true, Data: "1" });
        }
        catch (Exception)
        {
            // User hive unavailable (no interactive session): the facts stay unknown.
        }

        var paging = RegistryValue.Read(r, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles");
        var (managed, disabled, maxMb) = PageFile.Parse(paging.Existed ? paging.Data : null);
        f.Set("pagefile.systemManaged", managed);
        f.Set("pagefile.disabled", disabled);
        f.Set("pagefile.maxMb", maxMb);

        // The User Choice Protection Driver loads at boot unless its service is disabled (Start 4). While it runs, Windows
        // denies writing values it protects, such as the Widgets policy, also to administrators (VM test on 26H2 Home).
        var ucpd = RegistryValue.Read(r, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\UCPD", "Start");
        f.Set("ucpd.running", ucpd.Existed && ucpd.Data != "4");

        // Browsers whose policies the catalog offers: installed for all users or for the session user (App Paths).
        f.Set("browser.chrome", BrowserInstalled(r, "chrome.exe"));
        f.Set("browser.brave", BrowserInstalled(r, "brave.exe"));
    }

    private static bool BrowserInstalled(IRegistryRoots r, string exe)
    {
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}";
        using (var machine = r.Open(Hive.Machine, path, writable: false)) if (machine is not null) return true;
        try
        {
            using var user = r.Open(Hive.User, path, writable: false);
            return user is not null;
        }
        catch (Exception)
        {
            return false; // no user hive: only the machine-wide install counts
        }
    }
}

/// <summary>
/// Parses Memory Management\PagingFiles (REG_MULTI_SZ). "?:\pagefile.sys" = managed on all drives; "C:\pagefile.sys 0 0" =
/// system-managed size on C:; "C:\pagefile.sys 1024 4096" = fixed initial/maximum size in MB; empty = no page file.
/// </summary>
public static class PageFile
{
    public static (bool? Managed, bool? Disabled, double? MaxMb) Parse(string? multiSz)
    {
        if (multiSz is null) return (null, null, null);
        var entries = multiSz.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return (false, true, null);
        var managed = false;
        double? max = null;
        foreach (var e in entries)
        {
            var parts = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].StartsWith("?:", StringComparison.Ordinal)) managed = true;
            else if (parts.Length >= 3 && double.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m))
            {
                if (m == 0) managed = true; // "0 0" = system-managed size on that drive
                else max = (max ?? 0) + m;
            }
            else if (parts.Length == 1) managed = true; // no sizes given: system-managed
        }
        return (managed, false, managed ? null : max);
    }
}
