using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Hardware;
using Optimizer.Core.Hardware.Probes;
using Optimizer.Core.Interop;

namespace Optimizer.Core.Tweaks;

/// <summary>
/// Tweaks that target one device or one game on this PC, built at scan time (the catalog cannot know instance ids or
/// game paths). They use shared explanation pages and the normal engine (backup, undo, verify).
/// </summary>
public static class DeviceTweaks
{
    public const string MsiDoc = "device.msiMode";
    public const string AffinityDoc = "device.interruptAffinity";
    public const string NvidiaGameDoc = "nvidia.gameMaxPerformance";

    public static IReadOnlyList<string> DocIds => [MsiDoc, AffinityDoc, NvidiaGameDoc];

    public static IReadOnlyList<TweakDefinition> Build(HardwareProfile p)
    {
        var list = new List<TweakDefinition>();
        foreach (var d in p.Extras?.MsiDevices.Where(d => d.SupportsMsi) ?? [])
        {
            list.Add(MsiMode(d));
            // Graphics cards only: network adapters spread interrupts over several processors with RSS (one MSI-X
            // message per queue), and pinning them to one processor works against that.
            if (d.Kind == "gpu" && p.Cpu is { } cpu && AffinityTarget(cpu) is { } lp) list.Add(InterruptAffinity(d, lp));
        }
        if (p.Gpus?.Any(g => g.Vendor == Vendor.Nvidia && g.Kind == GpuKind.Discrete) == true)
            foreach (var g in p.Software?.Games.Where(g => g.Executable is not null).DistinctBy(g => g.Executable, StringComparer.OrdinalIgnoreCase) ?? [])
                list.Add(NvidiaGameMaxPerformance(g));
        return list;
    }


    /// <summary>
    /// MSI mode via the documented registry override (MSISupported = 1 under Interrupt Management). Applies after a
    /// restart. Expert and boot-critical: a driver that mishandles MSI can fail to start.
    /// </summary>
    public static TweakDefinition MsiMode(MsiDevice d) => new()
    {
        Id = $"device.msi.{d.Kind}.{TweakIds.Slug(d.InstanceId)}",
        Subject = d.Name,
        Docs = MsiDoc,
        Category = "Expert",
        Impact = new ImpactInfo { Gaming = 0, Basis = "disputed", Effect = ["latency"] },
        Risk = Risk.Expert,
        BootCritical = true,
        Preview = true,
        Restart = true,
        Verify = "afterRestart",
        Actions =
        [
            new RegistryAction
            {
                Hive = Hive.Machine,
                Path = $@"SYSTEM\CurrentControlSet\Enum\{d.InstanceId}\{ExtrasProbe.MsiKeySuffix}",
                Name = "MSISupported",
                Kind = "dword",
                Value = JsonSerializer.SerializeToElement(1),
            },
        ],
        Sources = ["https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/enabling-message-signaled-interrupts-in-the-registry"],
    };

    /// <summary>Logical processor for device interrupts: the first thread of core 2 (keeps them off core 0 and 1, where Windows handles most system work).</summary>
    public static int? AffinityTarget(CpuInfo cpu)
    {
        if (cpu.Cores < 4 || cpu.Threads < 4) return null;
        var smt = cpu.Threads > cpu.Cores;
        // Hybrid CPUs without SMT (e.g. Core Ultra 200S) interleave P- and E-cores in the numbering: no safe default.
        if (cpu.IsHybrid && !smt) return null;
        var lp = smt ? 4 : 2; // with SMT, P-cores come first and each has two logical processors
        return lp < cpu.Threads && lp < 64 ? lp : null; // one processor group (KAFFINITY) only
    }

    /// <summary>
    /// Interrupt affinity policy (IrqPolicySpecifiedProcessors = 4) with an AssignmentSetOverride mask, the documented
    /// registry values read by the PnP manager at device start.
    /// </summary>
    public static TweakDefinition InterruptAffinity(MsiDevice d, int logicalProcessor)
    {
        var mask = BitConverter.GetBytes(1UL << logicalProcessor); // KAFFINITY, little endian
        var path = $@"SYSTEM\CurrentControlSet\Enum\{d.InstanceId}\Device Parameters\Interrupt Management\Affinity Policy";
        return new TweakDefinition
        {
            Id = $"device.affinity.{d.Kind}.{TweakIds.Slug(d.InstanceId)}",
            Subject = $"{d.Name} > CPU {logicalProcessor}",
            Docs = AffinityDoc,
            Category = "Expert",
            Impact = new ImpactInfo { Gaming = 0, Basis = "disputed", Effect = ["latency"] },
            Risk = Risk.Expert,
            BootCritical = true,
            Preview = true,
            Restart = true,
            Verify = "afterRestart",
            Actions =
            [
                new RegistryAction { Hive = Hive.Machine, Path = path, Name = "DevicePolicy", Kind = "dword", Value = JsonSerializer.SerializeToElement(4) },
                new RegistryAction
                {
                    Hive = Hive.Machine, Path = path, Name = "AssignmentSetOverride", Kind = "binary",
                    Value = JsonSerializer.SerializeToElement(Convert.ToHexString(mask)),
                },
            ],
            Sources = ["https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority"],
        };
    }

    /// <summary>
    /// NVIDIA "Prefer maximum performance" in the game's own driver profile (created if the driver has none). Impact 0: no
    /// measurement shows an effect on frame times yet, like the global nvidia.preferMaxPerformance.
    /// </summary>
    public static TweakDefinition NvidiaGameMaxPerformance(InstalledGame g) => new()
    {
        Id = $"nvidia.game.{TweakIds.Slug(g.Executable!)}",
        Subject = g.Name,
        Docs = NvidiaGameDoc,
        Category = "Graphics",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Risk = Risk.Safe,
        Preview = true,
        AppliesTo = new AppliesTo { GpuVendor = ["nvidia"] },
        Actions = [new NvidiaDrsAction { Profile = g.Executable!, SettingId = Nvapi.SettingPreferredPState, Value = Nvapi.PStatePreferMax }],
        Sources =
        [
            "https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html",
            "https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm",
        ],
    };
}
