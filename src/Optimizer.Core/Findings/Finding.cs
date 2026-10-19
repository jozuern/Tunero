using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;

namespace Optimizer.Core.Findings;

/// <summary>Every check returns OK / Problem / Unsupported / Unknown. Unknown never produces advice.</summary>
public enum FindingStatus { Ok, Problem, Unsupported, Unknown, Info }

public enum FindingKind { Finding, Advisor, GameAccess }

/// <summary>Effect tags.</summary>
public static class Effect
{
    public const string Fps = "fps", Lows = "lows", Latency = "latency", Stutter = "stutter",
        Stability = "stability", Input = "input", Prerequisite = "prerequisite", None = "none";
}

/// <summary>
/// One line of the generated "What we found" section. <see cref="LabelKey"/> resolves through labels.json;
/// a value starting with '@' is a label key too (e.g. "@yes"), so facts stay language-neutral.
/// </summary>
public sealed record Fact(string LabelKey, string Value);

public sealed record Finding
{
    /// <summary>Explanation page id (Catalog/Docs/&lt;lang&gt;/&lt;Id&gt;.md).</summary>
    public required string Id { get; init; }

    /// <summary>Distinguishes several results of one check (e.g. one per display).</summary>
    public string? InstanceKey { get; init; }

    /// <summary>Shown after the title, e.g. the monitor name.</summary>
    public string? Subject { get; init; }

    public required FindingKind Kind { get; init; }
    public required FindingStatus Status { get; init; }

    /// <summary>0-5 ⚡; null for Game access items (shown separately from ⚡).</summary>
    public int? Impact { get; init; }

    public IReadOnlyList<string> Effects { get; init; } = [];
    public bool Critical { get; init; }

    /// <summary>Variant of the explanation, e.g. "slotLimited" vs "trainedDown" (selects a ":::variant" block in the page).</summary>
    public string? Variant { get; init; }

    public IReadOnlyList<Fact> Facts { get; init; } = [];

    /// <summary>Placeholder values for the explanation page ({{name}}).</summary>
    public IReadOnlyDictionary<string, string> Params { get; init; } = new Dictionary<string, string>();

    /// <summary>One-click fix built for this PC (e.g. the exact display mode); catalog fixes are linked via TweakDefinition.Fixes.</summary>
    public Tweaks.TweakDefinition? Fix { get; init; }

    public bool IsProblem => Status == FindingStatus.Problem;
    public string Key => InstanceKey is null ? Id : $"{Id}#{InstanceKey}";

    /// <summary>
    /// The result of a check whose data could not be read (its probe failed or timed out): Unknown, with no advice. Without
    /// it the check would simply be missing, and the score would rise as if it had passed.
    /// </summary>
    public static Finding NotRead(string id) => new()
    {
        Id = id,
        Kind = id.StartsWith("A.", StringComparison.Ordinal) ? FindingKind.Advisor
            : id.StartsWith("G.", StringComparison.Ordinal) ? FindingKind.GameAccess : FindingKind.Finding,
        Status = FindingStatus.Unknown,
    };
}

public interface IFindingCheck
{
    /// <summary>Ids of the explanation pages this check can produce (used by the docs lint).</summary>
    IReadOnlyList<string> DocIds { get; }

    /// <summary>Kind of the results (used for the Unknown result when the check fails). Default: from the doc id prefix.</summary>
    FindingKind Kind => DocIds.FirstOrDefault() switch
    {
        { } id when id.StartsWith("A.", StringComparison.Ordinal) => FindingKind.Advisor,
        { } id when id.StartsWith("G.", StringComparison.Ordinal) => FindingKind.GameAccess,
        _ => FindingKind.Finding,
    };

    IEnumerable<Finding> Evaluate(HardwareProfile profile, CatalogData catalog);
}

/// <summary>
/// A check whose advice concerns physical hardware or its firmware (memory modules and channels, BIOS, PCIe links,
/// disks, microcode). In a virtual machine that hardware is the host's or emulated: the check does not run there.
/// </summary>
public interface IPhysicalHardwareCheck : IFindingCheck
{
}
