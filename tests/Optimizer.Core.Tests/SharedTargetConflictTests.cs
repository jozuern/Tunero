using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Interop;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// The conflict check by shared target: two changes that write the same target conflict without a declared
/// <c>conflictsWith</c>, so batch applies, drift notices and "Undo all" cannot be wrong. Sandbox registry and fakes only.
/// </summary>
public class SharedTargetConflictTests
{
    private const string Path = @"SOFTWARE\Tunero\ConflictTest";
    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string BoostSetting = "be337238-0d82-4146-a960-4f3749d470c7";

    private static readonly ApplyOptions Expert = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private static Facts Facts() => new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false)
        .Set("gpu.hasNvidia", true).Set("gpu.hasDiscrete", true);

    private static TweakDefinition Def(string id, TweakAction action, params string[] conflictsWith) => new()
    {
        Id = id,
        Category = "Test",
        Hidden = true,
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Actions = [action],
        ConflictsWith = [.. conflictsWith],
    };

    private static RegistryAction Value(int data, string path = Path) => new() { Hive = Hive.Machine, Path = path, Name = "Mode", Value = JsonSerializer.SerializeToElement(data) };

    private static RegistryBitsAction Bits(uint set) => new() { Hive = Hive.Machine, Path = Path, Name = "Flags", Set = set };

    private static PowerSettingAction Boost(uint? ac, uint? dc) => new() { Subgroup = ProcessorSubgroup, Setting = BoostSetting, Ac = ac, Dc = dc };

    [Fact]
    public async Task TwoTweaksOnOneRegistryValueConflictWithoutADeclaration()
    {
        using var fx = new EngineFixture();
        var first = Def("test.modeOne", Value(1));
        var second = Def("test.modeTwo", Value(2));

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(first, Facts(), Expert)).Outcome);
        var blocked = await fx.Engine.ApplyAsync(second, Facts(), Expert);
        Assert.Equal(ApplyOutcome.Blocked, blocked.Outcome);
        var block = Assert.Single(blocked.Blocks!, b => b.ReasonKey == "block.conflict");
        Assert.Equal(first.Id, block.Detail);
        Assert.Equal(first.Id, block.Other?.Id); // the definition from the backup: a runtime change has no catalog entry
        Assert.Equal("1", RegistryValue.Read(fx.Registry, Hive.Machine, Path, "Mode").Data);

        // The status the pages show carries the same block; after undo the other tweak is free.
        Assert.Contains(fx.Engine.Detect(second, Facts()).Blocks, b => b.ReasonKey == "block.conflict" && b.Detail == first.Id);
        Assert.Contains(fx.Engine.DetectAll([first, second], Facts())[1].Blocks, b => b.ReasonKey == "block.conflict");
        Assert.True(fx.Engine.Revert(first).Success);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(second, Facts(), Expert)).Outcome);
    }

    [Fact]
    public async Task ABatchAppliesOnlyTheFirstOfTwoTweaksOnOneTarget()
    {
        using var fx = new EngineFixture();
        var first = Def("test.modeOne", Value(1));
        var second = Def("test.modeTwo", Value(2));

        var results = await fx.Engine.ApplyBatchAsync([first, second], Facts(), Expert);

        Assert.Equal(ApplyOutcome.Applied, results.Single(r => r.Tweak.Id == first.Id).Result.Outcome);
        Assert.Equal(ApplyOutcome.Blocked, results.Single(r => r.Tweak.Id == second.Id).Result.Outcome);
        Assert.Null(fx.Store.Get(second.Id));
    }

    [Fact]
    public async Task ADeclaredConflictBlocksInBothDirections()
    {
        using var fx = new EngineFixture();
        // Different targets: only the declaration links them, and only one side names the other.
        var declaring = Def("test.declaring", Value(1), "test.named");
        var named = Def("test.named", Bits(0x4));

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(declaring, Facts(), Expert)).Outcome);
        Assert.Contains(fx.Engine.Preflight(named, Facts(), Expert), b => b.ReasonKey == "block.conflict" && b.Detail == declaring.Id);
    }

    [Fact]
    public async Task BitsOfOneValueConflictOnlyWhenTheyAreTheSameBit()
    {
        using var fx = new EngineFixture();
        var low = Def("test.bitLow", Bits(0x1));
        var high = Def("test.bitHigh", Bits(0x8));
        var lowAgain = Def("test.bitLowAgain", Bits(0x3));
        var whole = Def("test.flagsWhole", new RegistryAction { Hive = Hive.Machine, Path = Path, Name = "Flags", Value = JsonSerializer.SerializeToElement(0) });

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(low, Facts(), Expert)).Outcome);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(high, Facts(), Expert)).Outcome);
        Assert.Contains(fx.Engine.Preflight(lowAgain, Facts(), Expert), b => b.ReasonKey == "block.conflict" && b.Detail == low.Id);
        Assert.DoesNotContain(fx.Engine.Preflight(lowAgain, Facts(), Expert), b => b.Detail == high.Id);
        // The whole value overlaps every bit of it.
        var wholeBlocks = fx.Engine.Preflight(whole, Facts(), Expert).Where(b => b.ReasonKey == "block.conflict").Select(b => b.Detail).ToList();
        Assert.Equal([high.Id, low.Id], wholeBlocks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DeletingAKeyConflictsWithAValueBelowIt()
    {
        using var fx = new EngineFixture();
        var value = Def("test.modeOne", Value(1));
        var key = Def("test.deleteKey", new RegistryKeyAction { Hive = Hive.Machine, Path = Path });
        var sibling = Def("test.sibling", Value(1, Path + "Other"));

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(value, Facts(), Expert)).Outcome);
        Assert.Contains(fx.Engine.Preflight(key, Facts(), Expert), b => b.ReasonKey == "block.conflict" && b.Detail == value.Id);
        Assert.DoesNotContain(fx.Engine.Preflight(sibling, Facts(), Expert), b => b.ReasonKey == "block.conflict");
    }

    [Fact]
    public void PowerSettingsConflictOnlyOnTheSideTheyBothWrite()
    {
        var mains = Def("test.boostAc", Boost(0, null));
        var battery = Def("test.boostDc", Boost(null, 0));
        var both = Def("test.boostBoth", Boost(0, 0));

        Assert.False(TargetOverlap.SharesTarget(mains, battery));
        Assert.True(TargetOverlap.SharesTarget(mains, both));
        Assert.True(TargetOverlap.SharesTarget(battery, both));
        // A plan named in the action (expanded for this PC) and the catalog's "active plan" overlap; two plans do not.
        var planA = Def("test.planA", new PowerSettingAction { Subgroup = ProcessorSubgroup, Setting = BoostSetting, Ac = 0, Scheme = FakePower.Balanced });
        var planB = Def("test.planB", new PowerSettingAction { Subgroup = ProcessorSubgroup, Setting = BoostSetting, Ac = 0, Scheme = FakePower.High });
        Assert.False(TargetOverlap.SharesTarget(planA, planB));
        Assert.True(TargetOverlap.SharesTarget(planA, mains));
    }

    [Fact]
    public async Task NvidiaFixesOnOneDriverSettingBlockEachOther()
    {
        using var fx = new EngineFixture();
        fx.Nvidia.Values[("global", Nvapi.SettingFrameRateLimiter)] = 30;
        fx.Nvidia.Values[("global", Nvapi.SettingPreferredPState)] = Nvapi.PStatePreferMin;
        var reset = RuntimeFixes.NvidiaGlobalReset([Nvapi.SettingFrameRateLimiter, Nvapi.SettingPreferredPState]);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(reset, Facts(), Expert)).Outcome);

        // The frame cap fix and the catalog's power mode tweak write settings the reset changed: neither declares it.
        var cap = RuntimeFixes.NvidiaFrameCap(141);
        Assert.Empty(cap.ConflictsWith);
        var block = Assert.Single(fx.Engine.Preflight(cap, Facts(), Expert), b => b.ReasonKey == "block.conflict");
        Assert.Equal(("fix.nvidiaGlobal", "fix.nvidiaGlobal"), (block.Detail, block.Other?.Id));
        var preferMax = TweakCatalog.Current.Get("nvidia.preferMaxPerformance")!;
        Assert.Contains(fx.Engine.Preflight(preferMax, Facts(), Expert), b => b.ReasonKey == "block.conflict" && b.Detail == "fix.nvidiaGlobal");

        // Undo all in any order restores the user's values: the second change never got a backup of its own.
        Assert.True(fx.Engine.Revert(reset).Success);
        Assert.Equal(30u, fx.Nvidia.Values[("global", Nvapi.SettingFrameRateLimiter)]);
        Assert.Equal(Nvapi.PStatePreferMin, fx.Nvidia.Values[("global", Nvapi.SettingPreferredPState)]);
    }

    [Fact]
    public async Task ATargetAnotherChangeWroteSinceIsNotDrift()
    {
        // Two changes on one target from before the check: the second one wrote over the first. The first must not read
        // as reset by Windows (reapplying it would undo the second); the second reads as applied.
        using var fx = new EngineFixture();
        var first = Def("test.modeOne", Value(1));
        var second = Def("test.modeTwo", Value(2));
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(first, Facts(), Expert)).Outcome);
        var firstBackup = fx.Store.Get(first.Id)!;
        fx.Store.Archive(first.Id);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(second, Facts(), Expert)).Outcome);
        fx.Store.Save(firstBackup);

        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(first, Facts()));
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(second, Facts()));
        var all = fx.Engine.DetectAll([first, second], Facts());
        Assert.Equal([TweakState.NotApplied, TweakState.Applied], all.Select(s => s.State));
        Assert.DoesNotContain(fx.Engine.CheckDrift(Facts()), d => d.Tweak.Id == first.Id);

        // Once the value is neither, Windows or another tool did reset it: drift again.
        RegistryValue.Write(fx.Registry, Hive.Machine, Path, "Mode", "dword", "7");
        Assert.Equal(TweakState.RevertedByWindows, fx.Engine.DetectState(first, Facts()));
    }

    [Fact]
    public void CatalogPairsOnOneTargetAreFoundWithoutDeclarations()
    {
        // Every pair the catalog declares because of a shared target is found by the target alone too.
        var catalog = TweakCatalog.Current;
        Assert.True(TargetOverlap.SharesTarget(catalog.Get("power.turboRestore")!, catalog.Get("quiet.boostOff")!));
        Assert.True(TargetOverlap.SharesTarget(catalog.Get("network.dns.google")!, catalog.Get("network.dns.quad9")!));
        Assert.True(TargetOverlap.SharesTarget(catalog.Get("power.gamingPlan")!, catalog.Get("power.ultimatePlan")!));
        // Restore turbo writes the mains side only, the battery tweak the battery side: no conflict.
        Assert.False(TargetOverlap.SharesTarget(catalog.Get("battery.boostOffDc")!, catalog.Get("power.turboRestore")!));
    }
}
