using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>
/// The catalog tweaks <see cref="CatalogRoundTripTests"/> cannot run in the registry sandbox alone (services, power
/// settings and plans, boot options, tasks, DNS, network adapters, NVIDIA settings, hibernation, memory compression,
/// power mode, DoH, reserved storage, the value lists and bit masks): each one is applied and undone against the fakes,
/// from a starting state where it is not applied yet, and every target has to read as before afterwards.
/// </summary>
public class CatalogSystemRoundTripTests(ITestOutputHelper output)
{
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private const string NicGuid = "{44444444-4444-4444-4444-444444444444}";
    private static readonly string NicKey = $@"{NicAdapters.ClassPath}\0009";

    private static bool InRegistrySandboxTest(TweakAction a) =>
        a is RegistryAction r && !r.Path.Contains("{nic}", StringComparison.Ordinal) || a is RegistryBitsAction || a is AccessibilityShortcutAction;

    /// <summary>A desktop gaming PC on which every tweak here is offered: discrete NVIDIA graphics, 32 GB, SSDs only, no X3D.</summary>
    private static Facts FactsFor(TweakDefinition t) => new Facts()
        .Set("os.build", 26300).Set("elevated", true).Set("os.edition", "Enterprise").Set("os.insider", false)
        .Set("system.laptop", t.AppliesTo.FormFactor == "laptop").Set("power.modernStandby", false).Set("power.personality", "balanced")
        .Set("cpu.x3dMultiCcd", false).Set("gpu.hasDiscrete", true).Set("gpu.hasNvidia", true).Set("memory.totalGb", 32).Set("storage.ssdOnly", true)
        .Set("browser.chrome", true).Set("browser.brave", true).Set("ucpd.running", false)
        .Set("anticheat.strict", false).Set("anticheat.any", false).Set("device.managed", false);

    public static TheoryData<string> SystemTweaks()
    {
        var data = new TheoryData<string>();
        foreach (var t in TweakCatalog.Current.Tweaks.Where(t => !t.Hidden && t.Actions.Count > 0 && !t.Actions.All(InRegistrySandboxTest)))
            data.Add(t.Id);
        return data;
    }

    /// <summary>A starting state in the fakes where every action of the tweak reads something and is not applied yet.</summary>
    private static void Prepare(EngineFixture fx, TweakDefinition t)
    {
        fx.Processes.Handler = new FakeSystemTools(fx).Run;
        foreach (var a in t.Actions)
        {
            switch (a)
            {
                case BcdAction { Delete: true } bcd:
                    fx.Bcd.Values[bcd.Element] = "Yes";
                    break;
                case PowerSchemeAction plan when plan.Activate is not null:
                    // Another plan is active, so switching to this one is a change.
                    fx.Power.Active = PowerAliases.Resolve(plan.Activate) == FakePower.High ? FakePower.Balanced : FakePower.High;
                    break;
                case ServiceAction s:
                    fx.Services.Start[s.Name] = s.StartType == ServiceStart.Automatic ? ServiceStart.Manual : ServiceStart.Automatic;
                    break;
                case ScheduledTaskAction task:
                    fx.Tasks.Enabled[task.Path] = !task.Enabled;
                    break;
                case HibernationAction h:
                    RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", "dword", h.Enabled ? "0" : "1");
                    break;
                case MemoryCompressionAction m:
                    fx.Processes.MemoryCompression = !m.Enabled;
                    break;
                case DohAutoUpgradeAction doh:
                    fx.Network.Doh = doh.Servers.ToDictionary(s => s, _ => !doh.AutoUpgrade, StringComparer.OrdinalIgnoreCase);
                    break;
                case RegistryTokenAction token:
                    RegistryValue.Write(fx.Registry, token.Hive, token.Path, token.Name, "string", $"{token.Token}=x;");
                    break;
                case RegistryBinaryBitsAction bits:
                    // A UserPreferencesMask as Windows writes it: the bits this tweak sets are clear, the ones it clears are set.
                    RegistryValue.Write(fx.Registry, bits.Hive, bits.Path, bits.Name, "binary", "9E1E078012000000");
                    break;
                case NicPropertyAction nic:
                    SetUpNic(fx, nic);
                    break;
                case DnsAction dns:
                    // Servers set by hand, so "automatic" (DHCP) is a change too.
                    fx.Network.SetDns(NicGuid, dns.Servers == "dhcp" ? ["192.0.2.1"] : null);
                    break;
                case RegistryAction r when r.Path.Contains("{nic}", StringComparison.Ordinal):
                    fx.Network.SetDns(NicGuid, null);
                    break;
            }
        }
    }

    /// <summary>One physical Ethernet adapter whose driver declares every keyword of the tweak, set to another value.</summary>
    private static void SetUpNic(EngineFixture fx, NicPropertyAction nic)
    {
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "NetCfgInstanceId", "string", NicGuid);
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "DeviceInstanceID", "string", @"PCI\VEN_8086&DEV_15BC\9");
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "Characteristics", "dword", "132");
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "DriverDesc", "string", "Test Ethernet");
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "*IfType", "dword", "6");
        foreach (var (name, value) in nic.Dwords)
            RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, name, "dword", (value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (keyword, value) in nic.Properties)
        {
            var other = value == "0" ? "1" : "0";
            RegistryValue.Write(fx.Registry, Hive.Machine, $@"{NicKey}\Ndi\params\{keyword}\enum", value, "string", "Wanted");
            RegistryValue.Write(fx.Registry, Hive.Machine, $@"{NicKey}\Ndi\params\{keyword}\enum", other, "string", "Other");
            RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, keyword, "string", other);
        }
    }

    [Theory]
    [MemberData(nameof(SystemTweaks))]
    public async Task AppliesAndUndoesAgainstTheFakes(string id)
    {
        using var fx = new EngineFixture(NicGuid);
        var t = TweakCatalog.Current.Tweaks.Single(x => x.Id == id);
        Prepare(fx, t);
        var facts = FactsFor(t);
        var targets = fx.Engine.Expand(t);
        var before = targets.Select(a => a.Read(fx.Context)).ToList();

        var blocks = fx.Engine.Preflight(t, facts, Options);
        if (blocks.Count > 0)
        {
            // A block here means a fact the fakes do not set: list it, so a new guard is noticed instead of passing silently.
            output.WriteLine($"{id}: blocked ({string.Join(", ", blocks.Select(b => b.ToString()))})");
            Assert.Fail($"{id} is blocked against the fakes: {string.Join(", ", blocks.Select(b => b.ReasonKey))}");
        }

        var result = await fx.Engine.ApplyAsync(t, facts, Options);
        Assert.True(result.Outcome is ApplyOutcome.Applied or ApplyOutcome.AppliedIneffective, $"{id}: {result.Outcome} {result.Error}");
        Assert.True(fx.Engine.DetectState(t, facts) is TweakState.Applied or TweakState.PendingRestart, $"{id}: not detected as applied");

        var undo = fx.Engine.Revert(t);
        Assert.True(undo.Success, $"{id}: undo failed: {string.Join("; ", undo.Errors)}");
        var after = targets.Select(a => a.Read(fx.Context)).ToList();
        for (var i = 0; i < before.Count; i++)
            Assert.True(before[i] is null ? after[i] is null : after[i] is not null && before[i]!.SameAs(after[i]!),
                $"{id}: {targets[i].Describe(fx.Context)} is {after[i]?.Display} after undo, was {before[i]?.Display}");
        Assert.Null(fx.Store.Get(id));
    }
}
