using Optimizer.Core.Actions;
using Optimizer.Core.Hardware;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>
/// Every catalog tweak made of registry values and accessibility flags is applied and undone in the registry sandbox:
/// afterwards it reads as applied, and the undo leaves nothing behind that was not there before.
/// </summary>
public class CatalogRoundTripTests(ITestOutputHelper output)
{
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private static bool IsSandboxable(TweakAction a) =>
        a is RegistryAction r && !r.Path.Contains("{nic}", StringComparison.Ordinal) || a is RegistryBitsAction || a is AccessibilityShortcutAction;

    /// <summary>Facts under which the tweak is offered: Enterprise edition covers both the Pro and the Enterprise-only policies.</summary>
    private static Facts FactsFor(TweakDefinition t) => new Facts()
        .Set("os.build", 26300).Set("elevated", true).Set("os.edition", "Enterprise").Set("os.insider", false)
        .Set("system.laptop", t.AppliesTo.FormFactor == "laptop")
        .Set("browser.chrome", true).Set("browser.brave", true).Set("ucpd.running", false)
        .Set("anticheat.strict", false).Set("anticheat.any", false).Set("device.managed", false);

    private static readonly HashSet<string> BlockedOnPurpose = ["gpu.hags"];

    public static TheoryData<string> RegistryTweaks()
    {
        var data = new TheoryData<string>();
        foreach (var t in TweakCatalog.Current.Tweaks.Where(t => !t.Hidden && t.Actions.Count > 0 && t.Actions.All(IsSandboxable)))
            data.Add(t.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(RegistryTweaks))]
    public async Task AppliesAndUndoesInTheSandbox(string id)
    {
        using var fx = new EngineFixture();
        var t = TweakCatalog.Current.Tweaks.Single(x => x.Id == id);
        var facts = FactsFor(t);
        var before = t.Actions.Select(a => a.Read(fx.Context)).ToList();

        var blocks = fx.Engine.Preflight(t, facts, Options);
        if (blocks.Count > 0)
        {
            // Only tweaks whose guard needs something the sandbox facts do not have (covered by their own tests) may be
            // blocked here; a new block anywhere else would otherwise turn this test into a silent pass.
            output.WriteLine($"{id}: blocked ({string.Join(", ", blocks.Select(b => b.ToString()))})");
            Assert.True(BlockedOnPurpose.Contains(id), $"{id} is blocked in the sandbox: {string.Join(", ", blocks.Select(b => b.ReasonKey))}");
            return;
        }

        if (fx.Engine.DetectState(t, facts) == TweakState.Applied)
        {
            // A missing value already means the Windows default this tweak sets (Game Mode, TRIM): nothing to write.
            Assert.Equal(ApplyOutcome.NothingToDo, (await fx.Engine.ApplyAsync(t, facts, Options)).Outcome);
            return;
        }

        var result = await fx.Engine.ApplyAsync(t, facts, Options);
        Assert.True(result.Outcome is ApplyOutcome.Applied, $"{id}: {result.Outcome} {result.Error}");
        Assert.True(fx.Engine.DetectState(t, facts) is TweakState.Applied or TweakState.PendingRestart, $"{id}: not detected as applied");

        Assert.True(fx.Engine.Revert(t).Success, $"{id}: undo failed");
        var after = t.Actions.Select(a => a.Read(fx.Context)).ToList();
        for (var i = 0; i < before.Count; i++)
            Assert.True(before[i]!.SameAs(after[i]!), $"{id}: {t.Actions[i].Describe(fx.Context)} is {after[i]?.Display} after undo, was {before[i]?.Display}");
        Assert.Null(fx.Store.Get(id));
    }

    [Fact]
    public void MostPolicyTweaksAreCovered()
    {
        var count = TweakCatalog.Current.Tweaks.Count(t => !t.Hidden && t.Actions.Count > 0 && t.Actions.All(IsSandboxable));
        Assert.True(count >= 120, $"only {count} tweaks are checked");
    }

    [Fact]
    public async Task AccessibilityShortcutsChangeOnlyTheShortcutBit()
    {
        using var fx = new EngineFixture();
        var t = TweakCatalog.Current.Tweaks.Single(x => x.Id == "input.accessibilityShortcutsOff");
        var facts = FactsFor(t);
        fx.Accessibility.Flags[AccessibilityFeature.StickyKeys] = 511; // Sticky Keys itself on: stays on

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, facts, Options)).Outcome);
        Assert.Equal(507u, fx.Accessibility.Flags[AccessibilityFeature.StickyKeys]);
        Assert.Equal(122u, fx.Accessibility.Flags[AccessibilityFeature.FilterKeys]);
        Assert.Equal(58u, fx.Accessibility.Flags[AccessibilityFeature.ToggleKeys]);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(511u, fx.Accessibility.Flags[AccessibilityFeature.StickyKeys]);
        Assert.Equal(126u, fx.Accessibility.Flags[AccessibilityFeature.FilterKeys]);
        Assert.Equal(62u, fx.Accessibility.Flags[AccessibilityFeature.ToggleKeys]);
    }

    [Fact]
    public void AccessibilityShortcutsAreUnsupportedForAnotherAccount()
    {
        using var fx = new EngineFixture();
        fx.Accessibility.Unavailable = true;
        var t = TweakCatalog.Current.Tweaks.Single(x => x.Id == "input.accessibilityShortcutsOff");
        Assert.Equal(TweakState.Unsupported, fx.Engine.DetectState(t, FactsFor(t)));
    }

    [Fact]
    public void BrowserFactsComeFromAppPaths()
    {
        using var fx = new EngineFixture();
        var profile = new HardwareProfile { Os = new BuildInfo(26300, 0, "26H2", "Professional", "Windows 11 Pro", CpuArchitecture.X64, false, null) };
        var none = FactsBuilder.Build(profile, [], Catalog.CatalogData.Current, fx.Registry);
        Assert.Equal(false, none.Get("browser.chrome"));
        Assert.Equal(false, none.Get("browser.brave"));

        using (fx.Registry.Open(Hive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", writable: true, create: true)) { }
        using (fx.Registry.Open(Hive.User, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe", writable: true, create: true)) { }
        var both = FactsBuilder.Build(profile, [], Catalog.CatalogData.Current, fx.Registry);
        Assert.Equal(true, both.Get("browser.chrome"));
        Assert.Equal(true, both.Get("browser.brave"));
    }

    [Fact]
    public void WidgetsPolicyOnlyWithoutTheUserChoiceProtectionDriver()
    {
        using var fx = new EngineFixture();
        var profile = new HardwareProfile { Os = new BuildInfo(26300, 0, "26H2", "Core", "Windows 11 Home", CpuArchitecture.X64, false, null) };
        var widgets = TweakCatalog.Current.Tweaks.Single(x => x.Id == "background.widgetsOff");
        Assert.Equal(false, FactsBuilder.Build(profile, [], Catalog.CatalogData.Current, fx.Registry).Get("ucpd.running"));

        const string service = @"SYSTEM\CurrentControlSet\Services\UCPD";
        using (var k = fx.Registry.Open(Hive.Machine, service, writable: true, create: true)!) k.SetValue("Start", 1, Microsoft.Win32.RegistryValueKind.DWord);
        var running = FactsBuilder.Build(profile, [], Catalog.CatalogData.Current, fx.Registry);
        Assert.Equal(true, running.Get("ucpd.running"));
        Assert.Equal(TweakState.NotApplicable, fx.Engine.DetectState(widgets, running));

        using (var k = fx.Registry.Open(Hive.Machine, service, writable: true)!) k.SetValue("Start", 4, Microsoft.Win32.RegistryValueKind.DWord);
        Assert.Equal(false, FactsBuilder.Build(profile, [], Catalog.CatalogData.Current, fx.Registry).Get("ucpd.running"));
    }
}
