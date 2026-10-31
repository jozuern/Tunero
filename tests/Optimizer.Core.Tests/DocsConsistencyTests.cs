using System.Text;
using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Cleanup;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// Numbers and lists in the documents come from the catalog, so they cannot drift. The VM test plan's tweak tables are
/// generated: run the tests with PCO_UPDATE_DOCS=1 to rewrite them after a catalog change.
/// </summary>
public class DocsConsistencyTests
{
    private const string Begin = "<!-- Generated from the tweak catalog by DocsConsistencyTests. Do not edit by hand. -->";
    private const string End = "<!-- End of generated tables. -->";

    public static int VisibleTweaks => TweakCatalog.Current.Tweaks.Count(t => !t.Hidden);
    public static int Checks => FindingEngine.CreateChecks(null).SelectMany(c => c.DocIds).Distinct(StringComparer.Ordinal).Count();
    public static int InboxApps => CatalogData.Current.Appx.Apps.Count;
    public static int CleanupCategories => CleanupEngine.Categories(@"C:\Users\Example", "S-1-5-21-0-0-0-1001").Count;

    [Fact]
    public void ReadmeCountsMatchTheCatalog()
    {
        var readme = File.ReadAllText(Path.Combine(RepoPaths.Root, "README.md"));
        var missing = new[] { $"{VisibleTweaks} catalog tweaks", $"{Checks} read-only checks", $"{InboxApps} removable apps", $"{CleanupCategories} cleanup categories" }
            .Where(phrase => !readme.Contains(phrase, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "README.md must say: " + string.Join("; ", missing));
    }

    [Fact]
    public void VmTestPlanIsGeneratedFromTheCatalog()
    {
        var path = Path.Combine(RepoPaths.Root, "docs", "vm-test-plan.md");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "vm-test-plan.md needs the generated-table markers");
        var expected = text[..start] + Begin + "\n\n" + GenerateTables() + End + text[(end + End.Length)..];
        if (Environment.GetEnvironmentVariable("PCO_UPDATE_DOCS") == "1" && expected != text) File.WriteAllText(path, expected);
        Assert.True(expected == text, "docs/vm-test-plan.md is out of date: run the tests once with PCO_UPDATE_DOCS=1");
        Assert.Contains($"Each of the {CleanupCategories} categories", text);
    }

    /// <summary>The tweak tables: Expert and boot-critical, restart or sign-out, everything else, and real hardware only.</summary>
    public static string GenerateTables()
    {
        var tweaks = TweakCatalog.Current.Tweaks.Where(t => !t.Hidden).ToList();
        static bool NeedsHardware(TweakDefinition t) => t.Actions.Any(a => a is NvidiaDrsAction or NicPropertyAction);
        var hardware = tweaks.Where(NeedsHardware).ToList();
        var expert = tweaks.Except(hardware).Where(t => t.EffectiveRisk == Risk.Expert || t.IsBootCritical).ToList();
        var restart = tweaks.Except(hardware).Except(expert).Where(t => t.Restart || t.SignOut).ToList();
        var other = tweaks.Except(hardware).Except(expert).Except(restart).ToList();

        var sb = new StringBuilder();
        sb.Append("The tables below cover all ").Append(tweaks.Count).Append(" catalog tweaks. Preview tweaks are the risky ones; test each of them on its own.\n\n");
        void Table(string heading, string intro, IEnumerable<TweakDefinition> list, bool hardwareColumn)
        {
            sb.Append("## ").Append(heading).Append("\n\n");
            if (intro.Length > 0) sb.Append(intro).Append("\n\n");
            // No Result column: these tables are rewritten from the catalog, so results go into the hand-written table after them.
            sb.Append(hardwareColumn ? "| Tweak | Title | Risk | Needs | Actions |\n|---|---|---|---|---|\n" : "| Tweak | Title | Risk | Then | Actions |\n|---|---|---|---|---|\n");
            foreach (var t in list)
            {
                var third = hardwareColumn
                    ? t.Actions.Any(a => a is NvidiaDrsAction) ? "NVIDIA GPU" : "physical network adapter"
                    : t.Restart ? "restart" : t.SignOut ? "sign out" : "";
                var actions = string.Join(", ", t.Actions.Select(ActionType).Distinct());
                sb.Append("| `").Append(t.Id).Append("` | ").Append(Title(t)).Append(" | ").Append(RiskText(t)).Append(" | ").Append(third)
                  .Append(" | ").Append(actions).Append(" |\n");
            }
            sb.Append('\n');
        }
        Table("2. Expert and boot-critical tweaks",
            "Turn on Expert mode. Take a checkpoint before **each** of these, apply it alone, restart, confirm Windows still starts, then undo and restart again.\n\n" +
            "Preconditions, or there is nothing to change on a clean install: turn on memory integrity (Windows Security > Device security > Core isolation) " +
            "and restart before `security.vbsOff`; run `bcdedit /set {current} useplatformclock true` before `leftover.usePlatformClock`.",
            expert, false);
        Table("3. Tweaks that need a restart or sign-out", "", restart, false);
        Table("4. Other tweaks",
            "`memory.sysmainOff` cannot be tested in Hyper-V: the virtual disk is not reported as an SSD, so the tweak does not apply there.",
            other, false);
        Table("5. Tweaks that need real hardware",
            "A VM has no NVIDIA GPU and only a synthetic network adapter. Test these on a spare real PC with the hardware named, one at a time, with a restore point first.",
            hardware, true);

        // Owner rule: an undocumented value is used only once it is proven to work; until then the tweak is a preview.
        var unproven = tweaks.Where(t => t.Undocumented && t.Proof.Count == 0).ToList();
        sb.Append("### Undocumented values: check the effect\n\n")
          .Append("These tweaks use values Microsoft does not document and stay previews until a test shows that the value does what the page says. ")
          .Append("Check the visible effect after apply and after undo, then add the result to the tweak's `proof` in the catalog.\n\n")
          .Append("| Tweak | Title | Check |\n|---|---|---|\n");
        foreach (var t in unproven)
            sb.Append("| `").Append(t.Id).Append("` | ").Append(Title(t)).Append(" | ").Append(EffectToCheck.GetValueOrDefault(t.Id, "the effect the explanation page describes")).Append(" |\n");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>What to look at for an undocumented value (the visible effect, not only the registry value).</summary>
    private static readonly Dictionary<string, string> EffectToCheck = new()
    {
        ["explorer.classicContextMenu"] = "right-click a file in File Explorer after signing in again: the classic menu opens at once; after undo the new menu is back",
        ["gpu.hags"] = "Settings > System > Display > Graphics shows the switch on after the restart (needs a GPU with HAGS support)",
        ["gpu.gameDvrOff"] = "Settings > Gaming > Captures shows background recording off; Win+Alt+R records nothing",
        ["office.launchToThisPc"] = "a new File Explorer window opens to This PC; Folder Options shows \"Open File Explorer to: This PC\"",
        ["privacy.inkingTypingOff"] = "Settings > Privacy & security > Inking & typing personalization shows both switches off",
        ["privacy.suggestionsOff"] = "the three Settings switches named on the page show off",
        ["privacy.webSearchOff"] = "searching in the Start menu shows no web results or suggestions",
    };

    private static string Title(TweakDefinition t) => (DocStore.Get(t.DocId, "en")?.Title ?? t.Id).Replace("|", "/");

    private static string RiskText(TweakDefinition t)
    {
        var parts = new List<string> { t.EffectiveRisk.ToString().ToLowerInvariant() };
        if (t.IsBootCritical) parts.Add("boot-critical");
        if (t.AntiCheatSensitive) parts.Add("anti-cheat sensitive");
        if (t.Preview) parts.Add("preview");
        return string.Join(", ", parts);
    }

    private static string ActionType(TweakAction a)
    {
        var name = a.GetType().Name.Replace("Action", "");
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
