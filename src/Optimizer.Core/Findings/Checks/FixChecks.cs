using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;
using Optimizer.Core.Interop;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Findings.Checks;

/// <summary>Fixes that depend on this PC's exact values (display mode, game paths) are built at scan time.</summary>
public static class RuntimeFixes
{
    public const string RefreshRateDoc = "fix.refreshRate";
    public const string GpuPreferenceDoc = "fix.gpuPreference";

    // The two ids below are built by hand, from before TweakIds.Slug: they stay, because backups of earlier versions
    // carry them, and a display name or an interface GUID is already unique and plain.
    public static TweakDefinition RefreshRate(DisplayInfo d) => new()
    {
        Id = $"fix.refreshRate.{d.GdiName.TrimStart('\\', '.')}",
        Docs = RefreshRateDoc,
        Category = "Fixes",
        Impact = new ImpactInfo { Gaming = 5, Basis = "situational", Effect = ["fps", "latency"] },
        Risk = Risk.Safe,
        Hidden = true,
        Actions =
        [
            new DisplayModeAction { GdiName = d.GdiName, Width = d.Width, Height = d.Height, RefreshHz = d.MaxOfferedRefreshAtCurrentResolution },
        ],
        Fixes = [RefreshRateCheck.Id],
        Sources = ["https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw"],
    };

    public const string NvidiaGlobalDoc = "fix.nvidiaGlobal";
    public const string NvidiaFrameCapDoc = "fix.nvidiaFrameCap";
    public const string PowerModeDoc = "fix.powerMode";
    public const string EthernetAutoDoc = "fix.ethernetAuto";

    public static IReadOnlyList<string> DocIds => [RefreshRateDoc, GpuPreferenceDoc, NvidiaGlobalDoc, NvidiaFrameCapDoc, PowerModeDoc, EthernetAutoDoc];

    /// <summary>Removes the global profile's own values for the given settings, so the driver defaults apply again.</summary>
    public static TweakDefinition NvidiaGlobalReset(IEnumerable<uint> settings) => new()
    {
        Id = "fix.nvidiaGlobal",
        Docs = NvidiaGlobalDoc,
        Category = "Fixes",
        Impact = new ImpactInfo { Gaming = 4, Basis = "situational", Effect = ["fps", "latency"] },
        Risk = Risk.Safe,
        Preview = true,
        Hidden = true,
        Actions = settings.Select(id => (TweakAction)new NvidiaDrsAction { Profile = "global", SettingId = id, Value = null }).ToList(),
        Fixes = [NvidiaGlobalCheck.Id],
        Sources =
        [
            "https://docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html",
            "https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h",
        ],
    };

    /// <summary>
    /// Global NVIDIA frame rate limit (FRL_FPS_ID, documented in NvApiDriverSettings.h) 3 FPS below the G-SYNC display's
    /// refresh rate. Preview: that the NVIDIA App's "Max Frame Rate" is this setting is not yet confirmed on real hardware.
    /// </summary>
    public static TweakDefinition NvidiaFrameCap(int fps) => new()
    {
        Id = "fix.nvidiaFrameCap",
        Docs = NvidiaFrameCapDoc,
        Category = "Fixes",
        Impact = new ImpactInfo { Gaming = 2, Basis = "situational", Effect = ["latency"] },
        Risk = Risk.Safe,
        Preview = true,
        Hidden = true,
        Actions = [new NvidiaDrsAction { Profile = "global", SettingId = Nvapi.SettingFrameRateLimiter, Value = (uint)fps }],
        Fixes = [NvidiaGlobalCheck.Id],
        Sources =
        [
            "https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h",
            "https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/",
        ],
    };

    /// <summary>Power mode "Best performance" while plugged in.</summary>
    public static TweakDefinition PowerModeBestPerformance() => new()
    {
        Id = "fix.powerMode",
        Docs = PowerModeDoc,
        Category = "Fixes",
        Impact = new ImpactInfo { Gaming = 3, Basis = "situational", Effect = ["fps"] },
        Risk = Risk.Safe,
        Hidden = true,
        Actions = [new PowerModeAction { Overlay = FirmwareExtras.OverlayBestPerformance }],
        Fixes = [PowerModeCheck.Id],
        // Same Windows setting as the Quiet profile's power mode: one would silently overwrite the other's backup.
        ConflictsWith = ["quiet.powerModeEfficiency"],
        Sources = ["https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider"],
    };

    /// <summary>Sets one Ethernet adapter's speed back to auto-negotiation.</summary>
    public static TweakDefinition EthernetAuto(NicDetail nic) => new()
    {
        Id = $"fix.ethernetAuto.{nic.Id.Trim('{', '}').ToLowerInvariant()}",
        Docs = EthernetAutoDoc,
        Category = "Fixes",
        Impact = new ImpactInfo { Gaming = 1, Basis = "situational", Effect = ["none"] },
        Risk = Risk.Safe,
        Preview = true,
        Hidden = true,
        Actions = [new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*SpeedDuplex"] = "0" }, InterfaceGuid = nic.Id, Media = "ethernet" }],
        Fixes = [EthernetSpeedCheck.Id],
        Sources = ["https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords"],
    };

    public static TweakDefinition GpuPreference(IEnumerable<InstalledGame> games) => new()
    {
        Id = "fix.gpuPreference",
        Docs = GpuPreferenceDoc,
        Category = "Fixes",
        Impact = new ImpactInfo { Gaming = 2, Basis = "situational", Effect = ["fps"] },
        Risk = Risk.Safe,
        Scope = TweakScope.User,
        Hidden = true,
        Actions = games.Where(g => g.Executable is not null)
            .Select(g => (TweakAction)new RegistryTokenAction
            {
                Hive = Hive.User,
                Path = @"Software\Microsoft\DirectX\UserGpuPreferences",
                Name = g.Executable!,
                Token = "GpuPreference",
                Value = "2",
            })
            .ToList(),
        Fixes = [GpuPreferenceCheck.Id],
        Sources = ["https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/nf-dxgi1_6-idxgifactory6-enumadapterbygpupreference"],
    };
}

/// <summary>
/// F4: games may run on the integrated GPU (laptops with hybrid graphics, desktops with the iGPU enabled).
/// Reads the per-app GPU preference (HKU\&lt;user&gt;\...\UserGpuPreferences) for each detected game. Information, not a
/// problem: the graphics driver's own profiles can route a game to the dedicated GPU without a Windows preference, and
/// the app cannot see that.
/// </summary>
public sealed class GpuPreferenceCheck(IRegistryRoots? registry) : IFindingCheck
{
    public const string Id = "F4.gpuPreference";
    public IReadOnlyList<string> DocIds => [Id];

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        if (p.Gpus is null || p.Software is null) yield break;
        var hybrid = p.Gpus.Any(g => g.Kind == GpuKind.Integrated) && p.Gpus.Any(g => g.Kind == GpuKind.Discrete);
        if (!hybrid) yield break;
        var games = p.Software.Games.Where(g => g.Executable is not null).ToList();
        if (games.Count == 0) yield break;
        if (registry is null)
        {
            yield return new Finding { Id = Id, Kind = FindingKind.Finding, Status = FindingStatus.Unknown, Impact = 2, Effects = [Effect.Fps] };
            yield break;
        }

        var missing = new List<InstalledGame>();
        foreach (var g in games)
        {
            var value = RegistryValue.Read(registry, Hive.User, @"Software\Microsoft\DirectX\UserGpuPreferences", g.Executable!);
            if (!RegistryTokenAction.Parse(value.Data).TryGetValue("GpuPreference", out var pref) || pref != "2") missing.Add(g);
        }

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = missing.Count > 0 ? FindingStatus.Info : FindingStatus.Ok,
            Impact = 2,
            Effects = [Effect.Fps],
            Facts =
            [
                new("fact.gpusDetected", string.Join(", ", p.Gpus.Where(g => g.Kind is GpuKind.Integrated or GpuKind.Discrete).Select(g => $"{g.Name} ({g.Kind})"))),
                .. missing.Take(12).Select(g => new Fact("fact.gameWithoutPreference", g.Name)),
            ],
            Fix = missing.Count > 0 ? RuntimeFixes.GpuPreference(missing) : null,
            Params = new Dictionary<string, string>
            {
                ["count"] = missing.Count.ToString(),
                ["dgpu"] = p.Gpus.First(g => g.Kind == GpuKind.Discrete).Name,
            },
        };
    }
}

/// <summary>F21: boot settings left behind by other tweak tools that cost performance on current hardware.</summary>
public sealed class LeftoverCheck : IFindingCheck
{
    public const string Id = "F21.leftovers";
    public IReadOnlyList<string> DocIds => [Id];

    private readonly Func<IReadOnlyDictionary<string, string>?> _bcd;

    /// <param name="bcd">BCD elements of {current} with their values (the app passes a reader only when elevated); null = unknown.</param>
    public LeftoverCheck(Func<IReadOnlyDictionary<string, string>?>? bcd = null) => _bcd = bcd ?? (() => null);

    public IEnumerable<Finding> Evaluate(HardwareProfile p, CatalogData c)
    {
        var found = new List<Fact>();
        var bcd = SafeBcd(_bcd);
        // Only "Yes" forces the platform timer; "No" is what Windows does anyway (and what the fix of other tools leaves).
        if (bcd is not null && bcd.TryGetValue("useplatformclock", out var clock) && BcdAction.Normalize(clock) == "yes") found.Add(new Fact("fact.leftoverValue", "BCD {current} useplatformclock"));

        yield return new Finding
        {
            Id = Id,
            Kind = FindingKind.Finding,
            Status = found.Count > 0 ? FindingStatus.Problem : bcd is null ? FindingStatus.Unknown : FindingStatus.Ok,
            Impact = 2,
            Effects = [Effect.Stability, Effect.Latency],
            Facts = found.Count > 0 ? found : [new Fact("fact.leftoverValue", "@none")],
        };
    }

    private static IReadOnlyDictionary<string, string>? SafeBcd(Func<IReadOnlyDictionary<string, string>?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
