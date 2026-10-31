using Optimizer.Core.Findings;

namespace Optimizer.Core.Tweaks;

/// <summary>One entry of the "Apply recommended" plan, with the reason shown to the user.</summary>
public sealed record RecommendedItem(TweakDefinition Tweak, string? ReasonKey, Finding? FixesFinding, int Impact)
{
    /// <summary>Why a fix is left out of the plan although it fixes a problem (missing admin rights, Expert mode off).</summary>
    public Block? Blocked { get; init; }
}

/// <summary>What "Apply recommended" would do and what it leaves out (and why).</summary>
public sealed record RecommendationPlan(IReadOnlyList<RecommendedItem> Items, IReadOnlyList<RecommendedItem> Excluded)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>A scan seen through a profile (<see cref="Recommendations.ForProfile"/>).</summary>
/// <param name="Tweaks">Every tweak state rated for the profile.</param>
/// <param name="Findings">The results the profile shows, without the ignored ones.</param>
/// <param name="IgnoredFindings">The ignored results the profile would show, with the profile's impact (the window's "Ignored" group).</param>
/// <param name="Plan">What "Apply recommended" applies and what it leaves out.</param>
public sealed record ProfiledPlan(IReadOnlyList<Profiles.ProfiledTweak> Tweaks, IReadOnlyList<Finding> Findings, IReadOnlyList<Finding> IgnoredFindings,
    RecommendationPlan Plan);

/// <summary>
/// Builds the "Apply recommended" plan: catalog tweaks whose recommendation rule matches this PC and that
/// are batch-safe, plus the one-click fixes of problem findings. Expert, boot-critical, anti-cheat sensitive and not fully
/// reversible items are listed as excluded so the user can apply them one by one.
/// </summary>
public static class Recommendations
{
    /// <summary>
    /// The plan for a scan and a profile, the one function the window's Overview and the command line
    /// (--apply-recommended, the first sign-in) both use, so the two cannot pick differently. Ignored results are removed
    /// here, after the profile rated them (the window lists them with the profile's impact); ignored tweaks are neither
    /// recommended nor offered as a fix.
    /// <para>
    /// The plan does not wait for the 3 second background CPU sample: the window builds it before the sample is done.
    /// Its check (F11) is never a problem and no tweak or profile reads it, so the plan is the same with or without it
    /// (CatalogIntegrityTests holds that).
    /// </para>
    /// </summary>
    /// <param name="findings">Every result of the scan, ignored ones too.</param>
    /// <param name="facts">The facts the states were read with, built without the ignored results (<see cref="FactsBuilder.Build"/>).</param>
    /// <param name="ignoredResults">The ignored results' keys (<see cref="IgnoredResults.Key"/>).</param>
    /// <param name="ignoredTweaks">The ids of the tweaks whose recommendation the user ignores.</param>
    public static ProfiledPlan ForProfile(Profiles.UsageProfile profile, IEnumerable<TweakStatus> states, IEnumerable<Finding> findings, Facts facts,
        IReadOnlyCollection<string>? ignoredResults = null, IReadOnlySet<string>? ignoredTweaks = null)
    {
        var tweaks = Profiles.ProfileView.For(profile, states, facts, ignoredTweaks);
        var seen = Profiles.ProfileView.Findings(profile, findings);
        var keys = ignoredResults ?? [];
        var ignored = seen.Where(f => IgnoredResults.IsIgnored(f, keys)).ToList();
        var shown = ignored.Count == 0 ? seen : seen.Where(f => !IgnoredResults.IsIgnored(f, keys)).ToList();
        return new ProfiledPlan(tweaks, shown, ignored, Build(tweaks, shown));
    }

    /// <param name="tweaks">Tweak states seen through the active profile.</param>
    /// <param name="findings">Findings as the active profile sees them (<see cref="Profiles.ProfileView.Findings"/>).</param>
    public static RecommendationPlan Build(IEnumerable<Profiles.ProfiledTweak> tweaks, IEnumerable<Finding> findings)
    {
        var items = new List<RecommendedItem>();
        var excluded = new List<RecommendedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(RecommendedItem item)
        {
            if (!seen.Add(item.Tweak.Id)) return;
            (item.Tweak.IsBatchSafe ? items : excluded).Add(item);
        }

        var list = tweaks.ToList();

        // Fixes first: they address measured problems on this PC that matter for the profile. Runtime fixes are built for
        // this PC; catalog tweaks name the findings they fix (never one that works against the profile).
        foreach (var f in findings.Where(f => f.Status == FindingStatus.Problem && (f.Critical || f.Impact > 0)))
        {
            if (f.Fix is { } fix)
            {
                if (Profiles.ProfileView.RuntimeFixAllowed(fix, list)) Add(new RecommendedItem(fix, null, f, f.Impact ?? 0));
                continue;
            }
            // The plan applies without asking per item, so nothing may block it here (not even missing admin rights). A fix
            // that is blocked is listed as left out, with the block as the reason: the overview must not say "nothing to
            // recommend" while a problem with a fix is shown below it. A tweak the user ignores is not offered here (its
            // result still offers it in its details).
            var catalogFix = list.FirstOrDefault(p => !p.Ignored && p.CanFix(f.Id) && p.Status.Blocks.Count == 0);
            if (catalogFix is not null)
            {
                Add(new RecommendedItem(catalogFix.Tweak, catalogFix.ReasonKey ?? catalogFix.Tweak.RecommendReasonKey, f, f.Impact ?? catalogFix.Impact));
                continue;
            }
            var blockedFix = list.FirstOrDefault(p => !p.Ignored && p.CanFix(f.Id));
            if (blockedFix is not null && seen.Add(blockedFix.Tweak.Id))
                excluded.Add(new RecommendedItem(blockedFix.Tweak, blockedFix.ReasonKey, f, f.Impact ?? blockedFix.Impact) { Blocked = blockedFix.Status.Blocks[0] });
        }

        foreach (var p in list.Where(p => p.Recommended))
            Add(new RecommendedItem(p.Tweak, p.ReasonKey, null, p.Impact));

        static List<RecommendedItem> Order(IEnumerable<RecommendedItem> list) =>
            list.OrderByDescending(i => i.FixesFinding is not null).ThenByDescending(i => i.Impact).ThenBy(i => i.Tweak.Id, StringComparer.Ordinal).ToList();

        return new RecommendationPlan(Order(items), Order(excluded));
    }
}
