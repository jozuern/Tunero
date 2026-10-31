using Optimizer.Core.Findings;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Profiles;

/// <summary>Catalog/Data/profiles.json.</summary>
public sealed class ProfileCatalog
{
    public List<UsageProfile> Profiles { get; init; } = [];

    public UsageProfile Default => Profiles.First(p => p.Id == UsageProfile.GamingId);

    public UsageProfile Get(string? id) => Profiles.FirstOrDefault(p => p.Id == id) ?? Default;

    /// <summary>The profile that fits this PC best (highest priority whose condition matches), else Gaming.</summary>
    public UsageProfile Suggest(Facts facts) =>
        Profiles.Where(p => p.SuggestWhen is not null && p.SuggestWhen.Evaluate(facts) && p.IsAvailable(facts))
            .OrderByDescending(p => p.SuggestPriority)
            .FirstOrDefault() ?? Default;
}

/// <summary>
/// What the PC is mainly used for. A profile does not change any setting by itself: it decides what "Apply
/// recommended" includes, how much each tweak and finding matters (impact on the profile's goal), which applied changes
/// work against the goal, and which findings count for the readiness score.
/// </summary>
public sealed class UsageProfile
{
    public const string GamingId = "gaming";

    public required string Id { get; init; }

    /// <summary>WPF-UI SymbolRegular name for the picker.</summary>
    public string Icon { get; init; } = "Games24";

    /// <summary>gaming | battery | everyday | quiet: what impact numbers mean (labels "goal.*", "effect.*").</summary>
    public string Goal { get; init; } = "gaming";

    /// <summary>Battery profiles make no sense without a battery: offered only on laptops.</summary>
    public bool LaptopOnly { get; init; }

    public Condition? SuggestWhen { get; init; }
    public int SuggestPriority { get; init; }

    /// <summary>
    /// Gaming profiles use the catalog's gaming impact and recommendation rules for every tweak (except the excluded
    /// categories), with the entries in <see cref="Tweaks"/> as overrides. Other profiles only rate the listed tweaks
    /// and findings.
    /// </summary>
    public bool UsesCatalog { get; init; }

    /// <summary>Categories made for other profiles (battery, noise, productivity) that a catalog profile leaves out.</summary>
    public List<string> ExcludeCategories { get; init; } = [];

    public List<ProfileTweak> Tweaks { get; init; } = [];

    /// <summary>Finding id -> impact on this profile's goal (0-5). -1 hides the finding in this profile.</summary>
    public Dictionary<string, int> Findings { get; init; } = [];

    public bool IsAvailable(Facts facts) => !LaptopOnly || facts.Get("system.laptop") is true;

    public ProfileTweak? Entry(string tweakId) => Tweaks.FirstOrDefault(t => t.Id == tweakId);
}

/// <summary>How one tweak relates to a profile. Negative impact = works against the profile's goal.</summary>
public sealed class ProfileTweak
{
    public required string Id { get; init; }

    /// <summary>-5..5; null in a catalog profile = the catalog's gaming impact.</summary>
    public int? Impact { get; init; }

    /// <summary>Recommended when this matches ({} = always); null in a catalog profile = the catalog rule.</summary>
    public Condition? RecommendWhen { get; init; }

    /// <summary>Label key: why it is recommended, or why it works against the profile.</summary>
    public string? ReasonKey { get; init; }

    /// <summary>
    /// The tweak's "on" state can simply be the Windows or plan default (processor boost on, for example): only flag it
    /// as working against the profile when this app made the change.
    /// </summary>
    public bool OnlyIfChanged { get; init; }
}

/// <summary>A tweak's status seen through a profile.</summary>
/// <param name="Flagged">On, works against the profile, and is a real change on this PC (shown as a warning).</param>
/// <param name="Ignored">
/// The user ignores what the app says about it (a recommendation that does not fit how this PC is used): it is neither
/// recommended nor flagged, in any profile, and can still be turned on or off by hand.
/// </param>
public sealed record ProfiledTweak(TweakStatus Status, int Impact, bool Relevant, bool Recommended, string? ReasonKey, bool Flagged = false, bool Ignored = false)
{
    public TweakDefinition Tweak => Status.Tweak;

    /// <summary>Lowers the profile's goal (for example costs battery life in the Battery profile).</summary>
    public bool WorksAgainst => Impact < 0;

    /// <summary>
    /// Can be offered as the fix of a finding: fixes it, is not on yet, applies to this PC, does not work against the
    /// profile and nothing but missing administrator rights blocks it (a conflict would make the offer fail).
    /// </summary>
    public bool CanFix(string findingId) =>
        Tweak.Fixes.Contains(findingId) && !Status.IsOn && !WorksAgainst
        && Status.State is not (TweakState.NotApplicable or TweakState.Unsupported)
        && Status.Blocks.All(b => b.CanOverride || b.ReasonKey == "block.notElevated");
}

public static class ProfileView
{
    /// <param name="ignored">Ids of the tweaks the user ignores (<see cref="ProfiledTweak.Ignored"/>).</param>
    public static ProfiledTweak For(UsageProfile profile, TweakStatus s, Facts facts, IReadOnlySet<string>? ignored = null)
    {
        var entry = profile.Entry(s.Tweak.Id);
        var catalog = profile.UsesCatalog && !profile.ExcludeCategories.Contains(s.Tweak.Category);
        var isIgnored = ignored?.Contains(s.Tweak.Id) == true;
        if (entry is null && !catalog) return new ProfiledTweak(s, 0, false, false, null, Ignored: isIgnored);

        var impact = entry?.Impact ?? (catalog ? s.Impact : 0);
        bool recommended;
        if (entry?.RecommendWhen is { } when) recommended = impact > 0 && when.Evaluate(facts) && CanRecommend(s);
        else recommended = entry?.Impact is null && catalog && s.Recommended;
        var reason = entry?.ReasonKey ?? (recommended && catalog ? s.Tweak.RecommendReasonKey : null);
        var flagged = impact < 0 && s.IsOn && (entry?.OnlyIfChanged != true || s.HasBackup);
        return new ProfiledTweak(s, impact, true, recommended && !isIgnored, reason, flagged && !isIgnored, isIgnored);
    }

    public static IReadOnlyList<ProfiledTweak> For(UsageProfile profile, IEnumerable<TweakStatus> statuses, Facts facts, IReadOnlySet<string>? ignored = null) =>
        statuses.Select(s => For(profile, s, facts, ignored)).ToList();

    /// <summary>
    /// A fix built for this PC (runtime) is not offered while a tweak it conflicts with (declared or by a shared target) is
    /// on, or holds a change of this app that the engine would protect by blocking the fix.
    /// </summary>
    public static bool RuntimeFixAllowed(TweakDefinition fix, IEnumerable<ProfiledTweak> tweaks) =>
        !tweaks.Any(p => (p.Status.IsOn || p.Status.HasBackup) && TargetOverlap.Conflict(fix, p.Tweak));

    /// <summary>Same rule as the catalog recommendations: not applied yet and nothing blocks it.</summary>
    private static bool CanRecommend(TweakStatus s) =>
        s.State is TweakState.NotApplied or TweakState.Partial or TweakState.RevertedByWindows && s.Blocks.Count == 0;

    /// <summary>
    /// Impact of a finding on the profile's goal; null = not shown in this profile. Catalog profiles keep the finding's
    /// own (gaming) impact unless listed; other profiles only show what they list.
    /// </summary>
    public static (bool Visible, int? Impact) Finding(UsageProfile profile, Finding f)
    {
        if (profile.Findings.TryGetValue(f.Id, out var w)) return w < 0 ? (false, null) : (true, w);
        return profile.UsesCatalog ? (true, f.Impact) : (false, null);
    }

    /// <summary>
    /// The findings as this profile sees them: hidden ones removed, impact replaced by the profile's, effects replaced by
    /// the profile's goal outside the gaming profiles. Sorted like the engine sorts.
    /// </summary>
    public static IReadOnlyList<Finding> Findings(UsageProfile profile, IEnumerable<Finding> findings)
    {
        var list = new List<Finding>();
        foreach (var f in findings)
        {
            var (visible, impact) = Finding(profile, f);
            if (!visible) continue;
            // Critical findings (unstable microcode) stay critical in every profile that shows them.
            list.Add(f with
            {
                Impact = f.Kind == FindingKind.GameAccess ? null : impact,
                Effects = profile.UsesCatalog ? f.Effects : impact > 0 ? [profile.Goal] : [],
            });
        }
        return FindingEngine.Sort(list);
    }

    /// <summary>Problems this profile does not show (they matter for another profile).</summary>
    public static int HiddenProblems(UsageProfile profile, IEnumerable<Finding> findings) =>
        findings.Count(f => f.IsProblem && f.Kind != FindingKind.GameAccess && !Finding(profile, f).Visible);
}
