using System.Text;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// What "Apply recommended" picks on four sample PCs (<see cref="SamplePcs"/>), for every profile each PC offers: the
/// checks, the facts and every tweak's state as the window and --apply-recommended read them (against the registry
/// sandbox and the fakes), then the one plan function both use (<see cref="Recommendations.ForProfile"/>). The result is
/// compared with a checked-in file per PC and profile under Fixtures/plans, so an edit of profiles.json, of a tweak's
/// "when" or "recommendWhen", or of a check shows what it does to the plans as a diff of those files. After a change that
/// should alter them, run the tests once with PCO_UPDATE_DOCS=1 and review the diff.
/// </summary>
public sealed class ExpectedPlanTests
{
    private static string Folder => Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "plans");

    private static bool Update => Environment.GetEnvironmentVariable("PCO_UPDATE_DOCS") == "1";

    public static TheoryData<string> Pcs => new(SamplePcs.All.Keys);

    private sealed record Scan(Facts Facts, IReadOnlyList<TweakStatus> States, IReadOnlyList<Finding> Findings);

    /// <summary>The scan of the window and the command line, on a fresh sandbox: checks, facts, every visible tweak's state.</summary>
    private static Scan Read(HardwareProfile pc)
    {
        using var fx = new EngineFixture();
        SamplePcs.Arrange(fx, pc);
        var findings = new ScanService(CatalogData.Current, fx.Registry, fx.Bcd.CurrentValues, today: () => SamplePcs.Today).Evaluate(pc);
        var facts = FactsBuilder.Build(pc, findings, CatalogData.Current, fx.Registry);
        return new Scan(facts, fx.Engine.DetectAll(TweakCatalog.Current.Visible, facts), findings);
    }

    /// <summary>The expected files rely on this: the same PC reads the same on every run (no clock, no order of a set, no state left behind).</summary>
    [Theory]
    [MemberData(nameof(Pcs))]
    public void TheSamePcReadsTheSameOnEveryRun(string pc)
    {
        static List<string> Lines(Scan s) =>
            [.. s.States.Select(x => $"{x.Tweak.Id} {x.State} {x.Recommended} {x.Impact} {string.Join(",", x.Blocks.Select(b => b.ReasonKey))}"),
             .. s.Findings.Select(f => $"{f.Id} {f.InstanceKey} {f.Status} {f.Impact} {f.Fix?.Id}")];
        var first = Read(SamplePcs.All[pc]);
        var second = Read(SamplePcs.All[pc]);
        Assert.Equal(Lines(first), Lines(second));
        Assert.Equal(first.Facts.All.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"),
            second.Facts.All.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
    }

    [Theory]
    [MemberData(nameof(Pcs))]
    public void ThePlansMatchTheCheckedInFiles(string pc)
    {
        var expected = Expected(pc);
        Assert.NotEmpty(expected);
        var differ = new List<string>();
        foreach (var (file, text) in expected)
        {
            var path = Path.Combine(Folder, file);
            var current = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
            if (current == text) continue;
            if (Update)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, text);
            }
            differ.Add(current is null ? $"{file} (missing)" : file);
        }
        Assert.True(differ.Count == 0,
            $"The plans of {pc} differ from Fixtures/plans: {string.Join(", ", differ)}. If the change is meant, run the tests once with PCO_UPDATE_DOCS=1 and review the diff.");
    }

    /// <summary>A file for a PC or profile that no longer exists (or a profile the PC no longer offers) would never be checked again.</summary>
    [Fact]
    public void EveryPlanFileBelongsToAPcAndAProfile()
    {
        var expected = SamplePcs.All.Keys.SelectMany(pc => Expected(pc).Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = Directory.Exists(Folder) ? Directory.GetFiles(Folder).Select(Path.GetFileName).OfType<string>().Where(f => !expected.Contains(f)).ToList() : [];
        if (Update)
            foreach (var f in stale) File.Delete(Path.Combine(Folder, f));
        Assert.True(stale.Count == 0, $"Fixtures/plans has files no sample PC and profile write: {string.Join(", ", stale)}. Run the tests once with PCO_UPDATE_DOCS=1 to remove them.");
    }

    /// <summary>The samples cover what they are named for: laptop profiles only on the laptop, Home only once, a fix on every PC.</summary>
    [Fact]
    public void TheSamplesCoverWhatTheyAreFor()
    {
        var profiles = CatalogData.Current.Profiles;
        var scans = SamplePcs.All.ToDictionary(p => p.Key, p => Read(p.Value));
        foreach (var (pc, scan) in scans)
        {
            Assert.Equal(pc == "gamingLaptop", profiles.Profiles.Any(p => p.LaptopOnly && p.IsAvailable(scan.Facts)));
            Assert.Equal(pc == "homeEdition", Equals(scan.Facts.Get("os.edition"), "Core"));
            Assert.Contains(profiles.Profiles.Where(p => p.IsAvailable(scan.Facts)),
                p => Recommendations.ForProfile(p, scan.States, scan.Findings, scan.Facts).Plan.Items.Any(i => i.FixesFinding is not null));
        }
    }

    /// <summary>File name to content, for every profile the PC offers.</summary>
    private static Dictionary<string, string> Expected(string pc)
    {
        var scan = Read(SamplePcs.All[pc]);
        var profiles = CatalogData.Current.Profiles;
        var suggested = profiles.Choose(null, scan.Facts).Profile;
        return profiles.Profiles.Where(p => p.IsAvailable(scan.Facts)).ToDictionary(
            p => $"{pc}.{p.Id}.txt",
            p => Render(pc, p.Id, p == suggested, Recommendations.ForProfile(p, scan.States, scan.Findings, scan.Facts).Plan),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One line per change in plan order: "apply" for what applies together, "leave" with the reason for the rest.</summary>
    private static string Render(string pc, string profile, bool suggested, RecommendationPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("# \"Apply recommended\" on the sample PC ").Append(pc).Append(" (SamplePcs.cs) with the profile ").Append(profile).Append(".\n");
        sb.Append("# Written by ExpectedPlanTests with PCO_UPDATE_DOCS=1: review the diff, do not edit by hand.\n");
        if (suggested) sb.Append("# The profile this PC gets when none is saved.\n");
        foreach (var i in plan.Items)
        {
            sb.Append("apply ").Append(i.Tweak.Id);
            if (i.FixesFinding is { } f) sb.Append(" fixes ").Append(f.Id);
            sb.Append('\n');
        }
        foreach (var i in plan.Excluded)
        {
            sb.Append("leave ").Append(i.Tweak.Id).Append(' ').Append(i.Blocked?.ReasonKey ?? OwnConfirmation(i.Tweak));
            if (i.FixesFinding is { } f) sb.Append(" fixes ").Append(f.Id);
            sb.Append('\n');
        }
        if (plan.Items.Count == 0 && plan.Excluded.Count == 0) sb.Append("nothing\n");
        return sb.ToString();
    }

    /// <summary>Why a change needs its own confirmation (<see cref="TweakDefinition.IsBatchSafe"/>).</summary>
    private static string OwnConfirmation(TweakDefinition t) => string.Join("+", new[]
    {
        t.Preview ? "preview" : null,
        t.EffectiveRisk == Risk.Expert ? "expert" : null,
        t.IsBootCritical ? "bootCritical" : null,
        t.AntiCheatSensitive ? "antiCheat" : null,
        t.Reversibility != Reversibility.Reversible ? "notFullyReversible" : null,
    }.OfType<string>());
}
