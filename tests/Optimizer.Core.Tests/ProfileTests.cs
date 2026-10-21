using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;
using Optimizer.Core.Hardware.Probes;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

public class ProfileTests
{
    private static ProfileCatalog Profiles => CatalogData.Current.Profiles;

    private static TweakStatus S(string id, TweakState state = TweakState.NotApplied, bool recommended = false)
    {
        var t = TweakCatalog.Current.Get(id) ?? throw new InvalidOperationException(id);
        return new TweakStatus(t, state, t.Impact.Gaming, t.Impact.Effect, null, recommended, [], state is TweakState.Applied);
    }

    private static Finding F(string id, FindingStatus status = FindingStatus.Problem, int? impact = 3, bool critical = false) =>
        new() { Id = id, Kind = id.StartsWith("A.", StringComparison.Ordinal) ? FindingKind.Advisor : id == "G.access" ? FindingKind.GameAccess : FindingKind.Finding,
                Status = status, Impact = impact, Critical = critical };

    [Fact]
    public void ProfileDataIsConsistent()
    {
        var ids = Profiles.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(UsageProfile.GamingId, Profiles.Default.Id);
        Assert.Equal(["gaming", "laptopGaming", "battery", "office", "quiet", "lowEnd"], ids);
        var findingIds = DocLint.RequiredDocIds().ToHashSet();
        foreach (var p in Profiles.Profiles)
        {
            Assert.Contains(p.Goal, new[] { "gaming", "battery", "everyday", "quiet" });
            foreach (var lang in DocStore.Languages) Assert.True(Labels.Current.Has(lang, $"effect.{p.Goal}"), $"{lang} effect.{p.Goal}");
            Assert.Equal(p.Tweaks.Count, p.Tweaks.Select(t => t.Id).Distinct().Count());
            foreach (var t in p.Tweaks)
            {
                Assert.True(TweakCatalog.Current.Get(t.Id) is not null, $"{p.Id}: unknown tweak {t.Id}");
                Assert.True(t.Impact is null or (>= -5 and <= 5), $"{p.Id}/{t.Id} impact");
                // Never recommend something that works against the profile.
                Assert.False(t.RecommendWhen is not null && t.Impact is < 1, $"{p.Id}/{t.Id}: recommended with impact {t.Impact}");
                if (t.Impact < 0) Assert.False(t.ReasonKey is null, $"{p.Id}/{t.Id}: works against without a reason");
                if (t.ReasonKey is { } k)
                    foreach (var lang in DocStore.Languages) Assert.True(Labels.Current.Has(lang, k), $"{lang} label {k}");
            }
            foreach (var (id, w) in p.Findings)
            {
                Assert.True(findingIds.Contains(id), $"{p.Id}: unknown finding {id}");
                Assert.InRange(w, -1, 5);
            }
            // Profiles that do not use the catalog must not list a category they rely on being excluded.
            if (!p.UsesCatalog) Assert.Empty(p.ExcludeCategories);
        }
    }

    [Fact]
    public void GamingProfileKeepsTheCatalogBehavior()
    {
        var gaming = Profiles.Default;
        var p = ProfileView.For(gaming, S("gpu.gameMode", recommended: true), new Facts());
        Assert.True(p.Relevant);
        Assert.True(p.Recommended);
        Assert.Equal(1, p.Impact);
        // Tweaks made for other profiles are not part of the gaming view, except the ones that work against it.
        Assert.False(ProfileView.For(gaming, S("battery.boostOffDc"), new Facts()).Relevant);
        var quiet = ProfileView.For(gaming, S("quiet.boostOff", TweakState.Applied), new Facts());
        Assert.True(quiet.Relevant && quiet.WorksAgainst);
        Assert.False(quiet.Recommended);
        // Findings keep their gaming impact; nothing is hidden.
        var findings = ProfileView.Findings(gaming, [F("F1.refresh", impact: 5), F("F28.diskHealth", impact: 4)]);
        Assert.Equal([5, 4], findings.Select(f => f.Impact!.Value));
    }

    [Fact]
    public void BatteryProfileRecommendsBatterySettingsAndFlagsDrains()
    {
        var battery = Profiles.Get("battery");
        var facts = new Facts().Set("system.laptop", true).Set("power.personality", "balanced");
        var boost = ProfileView.For(battery, S("battery.boostOffDc"), facts);
        Assert.True(boost.Recommended);
        Assert.Equal(3, boost.Impact);
        Assert.Equal("rec.battery.boost", boost.ReasonKey);
        // Already applied: not recommended again.
        Assert.False(ProfileView.For(battery, S("battery.boostOffDc", TweakState.Applied), facts).Recommended);
        // Gaming tweaks the profile does not list are not part of it.
        Assert.False(ProfileView.For(battery, S("gpu.gameMode", recommended: true), facts).Relevant);
        Assert.False(ProfileView.For(battery, S("gpu.gameMode", recommended: true), facts).Recommended);
        // Global power throttling off costs battery: flagged, with the reason.
        var throttling = ProfileView.For(battery, S("power.throttlingOff", TweakState.Applied), facts);
        Assert.True(throttling.WorksAgainst);
        Assert.Equal("avoid.ecoQos", throttling.ReasonKey);
        // The Balanced plan is only recommended over High performance.
        Assert.False(ProfileView.For(battery, S("power.balancedPlan"), facts).Recommended);
        Assert.True(ProfileView.For(battery, S("power.balancedPlan"), new Facts().Set("power.personality", "highperformance")).Recommended);
    }

    [Fact]
    public void FindingsAreSeenThroughTheProfile()
    {
        var battery = Profiles.Get("battery");
        var all = new[]
        {
            F("F1.refresh", impact: 5),            // gaming only: hidden on battery
            F("F29.onBattery", impact: 4),         // expected on battery: hidden
            F("F30.batteryWear", impact: 0),       // battery: impact 3
            F("A.raptorlake", impact: null, critical: true),
            F("G.access", impact: null),
        };
        var seen = ProfileView.Findings(battery, all);
        Assert.Equal(["A.raptorlake", "F30.batteryWear"], seen.Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(3, seen.Single(f => f.Id == "F30.batteryWear").Impact);
        Assert.Equal(["battery"], seen.Single(f => f.Id == "F30.batteryWear").Effects);
        Assert.True(seen.Single(f => f.Id == "A.raptorlake").Critical);
        Assert.Equal(2, ProfileView.HiddenProblems(battery, all)); // F1 and F29; Game access never counts
        // Score: critical 20 + impact 3 (7 points).
        Assert.Equal(100 - 20 - 7, ReadinessScore.Compute(seen));
        // The same findings in the gaming profile: F1 and F29 count, battery wear does not.
        var gaming = ProfileView.Findings(Profiles.Default, all);
        Assert.Equal(100 - 20 - 20 - 12, ReadinessScore.Compute(gaming));
    }

    [Fact]
    public void RecommendationPlanFollowsTheProfile()
    {
        var facts = new Facts().Set("system.laptop", true).Set("power.personality", "balanced");
        var statuses = new[] { S("gpu.gameMode", recommended: true), S("battery.boostOffDc"), S("power.throttlingOff", TweakState.Applied) };
        var turbo = F("F6.turbo", impact: 5);

        var battery = Profiles.Get("battery");
        var plan = Recommendations.Build(ProfileView.For(battery, statuses, facts), ProfileView.Findings(battery, [turbo]));
        Assert.Equal(["battery.boostOffDc"], plan.Items.Select(i => i.Tweak.Id));

        var gaming = Profiles.Default;
        var turboFix = S("power.turboRestore");
        var gamingPlan = Recommendations.Build(ProfileView.For(gaming, [.. statuses, turboFix], facts), ProfileView.Findings(gaming, [turbo]));
        Assert.Equal(["power.turboRestore", "gpu.gameMode"], gamingPlan.Items.Select(i => i.Tweak.Id));

        // Quiet: turbo being off is the goal, so its fix is never recommended there.
        var quiet = Profiles.Get("quiet");
        var quietPlan = Recommendations.Build(ProfileView.For(quiet, [S("quiet.boostOff"), turboFix], facts), ProfileView.Findings(quiet, [turbo]));
        Assert.Equal(["quiet.boostOff"], quietPlan.Items.Select(i => i.Tweak.Id));
    }

    [Fact]
    public void WindowsDefaultsAreNotFlaggedAsWorkingAgainstTheProfile()
    {
        // Processor boost on is the Windows default: in Quiet it only counts as a change against the profile when this app
        // turned it on. A real deviation (global power throttling off) is flagged however it was set.
        var quiet = Profiles.Get("quiet");
        var turbo = TweakCatalog.Current.Get("power.turboRestore")!;
        TweakStatus On(TweakDefinition t, bool backup) => new(t, TweakState.Applied, t.Impact.Gaming, t.Impact.Effect, null, false, [], backup);
        var asDefault = ProfileView.For(quiet, On(turbo, backup: false), new Facts());
        Assert.True(asDefault.WorksAgainst);
        Assert.False(asDefault.Flagged);
        Assert.True(ProfileView.For(quiet, On(turbo, backup: true), new Facts()).Flagged);
        var throttling = TweakCatalog.Current.Get("power.throttlingOff")!;
        Assert.True(ProfileView.For(quiet, On(throttling, backup: false), new Facts()).Flagged);
        // Off: nothing to flag.
        Assert.False(ProfileView.For(quiet, S("power.throttlingOff"), new Facts()).Flagged);
    }

    [Fact]
    public void IgnoredTweakIsNeitherRecommendedNorFlagged()
    {
        // Office recommends clipboard history and flags memory compression off (set outside this app counts too); a user
        // with another clipboard manager and plenty of memory ignores both.
        var office = Profiles.Get("office");
        var compression = TweakCatalog.Current.Get("memory.compressionOff")!;
        var compressionOff = new TweakStatus(compression, TweakState.Applied, 0, ["stutter"], null, false, [], false);
        Assert.True(ProfileView.For(office, S("office.clipboardHistoryOn"), new Facts()).Recommended);
        Assert.True(ProfileView.For(office, compressionOff, new Facts()).Flagged);

        var ignored = new HashSet<string> { "office.clipboardHistoryOn", "memory.compressionOff" };
        var clipboard = ProfileView.For(office, S("office.clipboardHistoryOn"), new Facts(), ignored);
        Assert.True(clipboard.Ignored);
        Assert.False(clipboard.Recommended);
        var against = ProfileView.For(office, compressionOff, new Facts(), ignored);
        Assert.True(against.Ignored);
        Assert.False(against.Flagged);
        // The rating stays what the profile says: only the app's urging is gone.
        Assert.True(against.WorksAgainst);
        Assert.Empty(Recommendations.Build(ProfileView.For(office, [S("office.clipboardHistoryOn")], new Facts(), ignored), []).Items);
        // In every profile, also one that does not rate the tweak.
        Assert.True(ProfileView.For(Profiles.Get("quiet"), S("office.clipboardHistoryOn"), new Facts(), ignored).Ignored);
    }

    [Fact]
    public void IgnoredTweakIsNotThePlansFix()
    {
        // Restore processor turbo fixes F6 in Gaming; ignored, the plan leaves the problem without a one-click fix, while
        // the tweak itself can still say it fixes it (the result's details offer it).
        var gaming = Profiles.Default;
        var ignored = new HashSet<string> { "power.turboRestore" };
        var profiled = ProfileView.For(gaming, [S("power.turboRestore")], new Facts(), ignored);
        Assert.True(profiled.Single().CanFix("F6.turbo"));
        var plan = Recommendations.Build(profiled, ProfileView.Findings(gaming, [F("F6.turbo", impact: 3)]));
        Assert.Empty(plan.Items);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void FixesNeverOverwriteAConflictingChange()
    {
        // Quiet power mode applied, profile Gaming: F8 (power mode) is a problem, but its fix would overwrite the quiet
        // tweak's setting without undoing it. Neither the plan nor the finding row offers it; the quiet tweak is flagged.
        var gaming = Profiles.Default;
        var quietMode = TweakCatalog.Current.Get("quiet.powerModeEfficiency")!;
        var on = new TweakStatus(quietMode, TweakState.Applied, 0, ["none"], null, false, [], true);
        var fix = RuntimeFixes.PowerModeBestPerformance();
        var f8 = F("F8.powerMode", impact: 3) with { Fix = fix };
        var profiled = ProfileView.For(gaming, [on], new Facts());
        Assert.False(ProfileView.RuntimeFixAllowed(fix, profiled));
        Assert.Empty(Recommendations.Build(profiled, ProfileView.Findings(gaming, [f8])).Items);
        Assert.True(profiled.Single().Flagged);

        // Restore processor turbo is blocked by the applied Quiet boost tweak: not offered as F6's fix.
        var turbo = TweakCatalog.Current.Get("power.turboRestore")!;
        var blocked = new TweakStatus(turbo, TweakState.NotApplied, 5, ["fps"], null, false, [new Block("block.conflict", "quiet.boostOff")], false);
        Assert.False(ProfileView.For(gaming, blocked, new Facts()).CanFix("F6.turbo"));
        // Missing admin rights alone do not hide the fix (applying then asks for them).
        var notElevated = blocked with { Blocks = [new Block("block.notElevated")] };
        Assert.True(ProfileView.For(gaming, notElevated, new Facts()).CanFix("F6.turbo"));
        // Such a fix is not in the one-click plan, but it is named as left out, with the reason.
        var plan = Recommendations.Build([ProfileView.For(gaming, notElevated, new Facts())], ProfileView.Findings(gaming, [F("F6.turbo", impact: 3)]));
        Assert.Empty(plan.Items);
        Assert.Equal("block.notElevated", Assert.Single(plan.Excluded).Blocked?.ReasonKey);
    }

    [Fact]
    public async Task ConflictsCountWhileThisAppHoldsTheBackup()
    {
        // The runtime power mode fix is not in the detected list; its backup still blocks the quiet power mode.
        using var fx = new EngineFixture();
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false).Set("power.personality", "balanced");
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true, ExpertMode = true };
        fx.PowerMode.Current = Platform.FirmwareExtras.OverlayBetterBattery;
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(RuntimeFixes.PowerModeBestPerformance(), facts, options)).Outcome);
        var quietMode = TweakCatalog.Current.Get("quiet.powerModeEfficiency")!;
        var r = await fx.Engine.ApplyAsync(quietMode, facts, options);
        Assert.Equal(ApplyOutcome.Blocked, r.Outcome);
        Assert.Contains(r.Blocks!, b => b.ReasonKey == "block.conflict" && b.Detail == "fix.powerMode");
    }

    [Theory]
    [InlineData(true, true, 16, false, "laptopGaming")]
    [InlineData(true, false, 16, false, "office")]
    [InlineData(false, true, 32, false, "gaming")]
    [InlineData(false, true, 8, false, "gaming")]   // a gaming desktop with little RAM stays Gaming (the RAM finding says the rest)
    [InlineData(false, false, 4, false, "lowEnd")]
    [InlineData(false, false, 16, true, "lowEnd")]  // Windows on a hard disk
    [InlineData(true, false, 4, false, "lowEnd")]
    public void SuggestionFitsThePc(bool laptop, bool dgpu, int ramGb, bool systemHdd, string expected)
    {
        var facts = new Facts().Set("system.laptop", laptop).Set("gpu.hasDiscrete", dgpu).Set("memory.totalGb", ramGb)
            .Set("finding.A.systemHdd.problem", systemHdd);
        Assert.Equal(expected, Profiles.Suggest(facts).Id);
    }

    [Fact]
    public void LaptopProfilesAreOnlyOfferedOnLaptops()
    {
        var desktop = new Facts().Set("system.laptop", false);
        Assert.False(Profiles.Get("battery").IsAvailable(desktop));
        Assert.False(Profiles.Get("laptopGaming").IsAvailable(desktop));
        Assert.True(Profiles.Get("quiet").IsAvailable(desktop));
        Assert.True(Profiles.Get("battery").IsAvailable(new Facts().Set("system.laptop", true)));
        Assert.Equal("gaming", Profiles.Get("unknown").Id);
    }

    [Fact]
    public async Task BatteryOnlyAndMainsOnlyChangesToOneSettingAreUndoneIndependently()
    {
        // power.turboRestore writes the mains value of boost mode, battery.boostOffDc the battery value of the same setting.
        using var fx = new EngineFixture();
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", true);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true, ExpertMode = true };
        var sub = PowerAliases.Resolve("SUB_PROCESSOR");
        var boost = PowerAliases.Resolve("PERFBOOSTMODE");
        var max = PowerAliases.Resolve("PROCTHROTTLEMAX");
        var scheme = fx.Power.Active;
        fx.Power.Ac[(scheme, sub, boost)] = 0;
        fx.Power.Dc[(scheme, sub, boost)] = 2;
        fx.Power.Ac[(scheme, sub, max)] = 80;

        var turbo = TweakCatalog.Current.Get("power.turboRestore")!;
        var battery = TweakCatalog.Current.Get("battery.boostOffDc")!;
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(turbo, facts, options)).Outcome);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(battery, facts, options)).Outcome);
        Assert.Equal((2u, 0u), (fx.Power.Ac[(scheme, sub, boost)], fx.Power.Dc[(scheme, sub, boost)]));

        // Undo the mains tweak: the battery value set by the other tweak stays.
        Assert.True(fx.Engine.Revert(turbo).Success);
        Assert.Equal((0u, 0u), (fx.Power.Ac[(scheme, sub, boost)], fx.Power.Dc[(scheme, sub, boost)]));
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(battery, facts));
        // Undo the battery tweak: only the battery value goes back.
        Assert.True(fx.Engine.Revert(battery).Success);
        Assert.Equal((0u, 2u), (fx.Power.Ac[(scheme, sub, boost)], fx.Power.Dc[(scheme, sub, boost)]));
    }

    [Fact]
    public void QuietBoostConflictsWithTurboRestoreBothWays()
    {
        var quiet = TweakCatalog.Current.Get("quiet.boostOff")!;
        var turbo = TweakCatalog.Current.Get("power.turboRestore")!;
        Assert.Contains("power.turboRestore", quiet.ConflictsWith);
        Assert.Contains("quiet.boostOff", turbo.ConflictsWith);
        Assert.Contains("office.clipboardHistoryOn", TweakCatalog.Current.Get("privacy.clipboardHistoryOff")!.ConflictsWith);
        Assert.Contains("privacy.clipboardHistoryOff", TweakCatalog.Current.Get("office.clipboardHistoryOn")!.ConflictsWith);
        Assert.Equal("laptop", TweakCatalog.Current.Get("battery.boostOffDc")!.AppliesTo.FormFactor);
        Assert.Equal("desktop", TweakCatalog.Current.Get("quiet.powerModeEfficiency")!.AppliesTo.FormFactor);
    }

    private static HardwareProfile Laptop(bool onAc, BatteryHealth? battery) => new()
    {
        Os = TestData.Os(),
        System = new SystemInfo("Vendor", "Model", [10], HasBattery: true, LidPresent: true, HypervisorPresent: false),
        Power = new PowerInfo(Guid.Empty, "Balanced", PowerPersonality.Balanced, 100, 5, 2, 0, onAc, false, true, 55),
        Battery = battery,
    };

    [Fact]
    public void OnBatteryCheckReportsGamingOnBattery()
    {
        var c = CatalogData.Current;
        // A gaming laptop (with a graphics card) on battery is a problem; one without is information only.
        var gaming = Laptop(onAc: false, null) with { Gpus = [TestData.Gpu("NVIDIA GeForce RTX 4060 Laptop GPU", Vendor.Nvidia, 8L << 30) with { Kind = GpuKind.Discrete }] };
        Assert.Equal(FindingStatus.Problem, new OnBatteryCheck().Evaluate(gaming, c).Single().Status);
        Assert.Equal(FindingStatus.Problem, new OnBatteryCheck().Evaluate(Laptop(onAc: false, null), c).Single().Status); // GPUs unknown
        Assert.Equal(FindingStatus.Info, new OnBatteryCheck().Evaluate(Laptop(onAc: false, null) with { Gpus = [] }, c).Single().Status);
        Assert.Equal(FindingStatus.Ok, new OnBatteryCheck().Evaluate(Laptop(onAc: true, null), c).Single().Status);
        Assert.Empty(new OnBatteryCheck().Evaluate(Laptop(true, null) with { System = new SystemInfo("V", "M", [3], false, false, false) }, c));
    }

    [Fact]
    public void BatteryWearUsesTheEightyPercentRule()
    {
        var c = CatalogData.Current;
        var worn = new BatteryWearCheck().Evaluate(Laptop(true, BatteryHealth.From(60000, 45000)), c).Single();
        Assert.Equal(FindingStatus.Problem, worn.Status);
        Assert.Equal("75", worn.Params["health"]);
        Assert.Equal(FindingStatus.Ok, new BatteryWearCheck().Evaluate(Laptop(true, BatteryHealth.From(60000, 50000)), c).Single().Status);
        Assert.Equal(FindingStatus.Unsupported, new BatteryWearCheck().Evaluate(Laptop(true, null), c).Single().Status); // no data: no row
        // Implausible driver data is treated as missing.
        Assert.Null(BatteryHealth.From(0, 50000));
        Assert.Null(BatteryHealth.From(50000, 0));
        Assert.Null(BatteryHealth.From(10000, 50000));
        Assert.NotNull(BatteryHealth.From(50000, 52000)); // a new battery slightly above its design capacity
    }
}
