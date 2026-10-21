using System.Text.Json.Serialization;
using Optimizer.Core.Actions;

namespace Optimizer.Core.Tweaks;

public enum Risk { Safe, Moderate, Expert }

public enum Reversibility { Reversible, Reinstall, Permanent }

public enum TweakScope { Machine, User }

public sealed class ImpactInfo
{
    /// <summary>0-5 gaming impact before hardware overrides.</summary>
    public int Gaming { get; init; }

    /// <summary>measured | situational | disputed.</summary>
    public string Basis { get; init; } = "situational";

    public List<string> Effect { get; init; } = [];
}

public sealed class ImpactOverride
{
    public required Condition When { get; init; }
    public int Gaming { get; init; }
    public List<string>? Effect { get; init; }
    public string? ReasonKey { get; init; }
}

public sealed class AppliesTo
{
    public int MinBuild { get; init; } = 26100;
    public int? MaxBuild { get; init; }
    public List<string>? CpuVendor { get; init; }
    public List<string>? GpuVendor { get; init; }

    /// <summary>any | desktop | laptop.</summary>
    public string FormFactor { get; init; } = "any";

    public Condition? When { get; init; }
}

/// <summary>One catalog entry (schema v2). Text lives in Catalog/Docs, not here.</summary>
public sealed class TweakDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }

    /// <summary>Shown after the title for tweaks built for one device or game (e.g. the game name).</summary>
    public string? Subject { get; init; }

    /// <summary>Explanation page id; defaults to <see cref="Id"/>.</summary>
    public string? Docs { get; init; }

    public required ImpactInfo Impact { get; init; }
    public List<ImpactOverride> ImpactOverrides { get; init; } = [];
    public Risk Risk { get; init; } = Risk.Safe;
    public Reversibility Reversibility { get; init; } = Reversibility.Reversible;
    public bool Restart { get; init; }

    /// <summary>Sign out and in again (per-session settings such as visual effects).</summary>
    public bool SignOut { get; init; }

    public TweakScope Scope { get; init; } = TweakScope.Machine;
    public bool BootCritical { get; init; }
    public bool AntiCheatSensitive { get; init; }

    /// <summary>
    /// Risky enough that it needs testing on real Windows before it can be called safe, and that testing has not been
    /// done yet (only the registry sandbox and fakes). Shown as "Preview" and never part of "Apply recommended".
    /// </summary>
    public bool Preview { get; init; }

    /// <summary>
    /// The value or interface the tweak uses is not documented by Microsoft or the vendor: the value behind a documented
    /// Settings switch, or a value known from widely used tools. Allowed only for harmless, fully reversible changes; the
    /// explanation page says so and the app shows a badge (CONTRIBUTING, "Undocumented values").
    /// </summary>
    public bool Undocumented { get; init; }

    /// <summary>
    /// For <see cref="Undocumented"/> tweaks: what proves that the value works (a VM test result that checked the effect,
    /// or a source that shows it working). Without proof an undocumented tweak stays a <see cref="Preview"/>.
    /// </summary>
    public List<string> Proof { get; init; } = [];

    /// <summary>Internal tweaks are not listed (e.g. the restore point frequency the engine sets itself).</summary>
    public bool Hidden { get; init; }

    public AppliesTo AppliesTo { get; init; } = new();
    public required List<TweakAction> Actions { get; init; }
    public List<string> ConflictsWith { get; init; } = [];
    public List<Condition> BlockedWhen { get; init; } = [];
    public Condition? RecommendWhen { get; init; }

    /// <summary>Every condition of this tweak: applies to, blocked when, recommended when and the impact overrides.</summary>
    public IEnumerable<Condition> Conditions() =>
        BlockedWhen.Concat(ImpactOverrides.Select(o => o.When)).Append(RecommendWhen).Append(AppliesTo.When).OfType<Condition>();

    /// <summary>Label key that explains why the tweak is recommended for this PC (shown next to "Recommended").</summary>
    public string? RecommendReasonKey { get; init; }

    /// <summary>"actions" (re-read after apply) or "afterRestart" (confirmed on the next boot).</summary>
    public string Verify { get; init; } = "actions";

    public List<string> Sources { get; init; } = [];

    /// <summary>Finding ids this tweak fixes (the finding shows a one-click fix).</summary>
    public List<string> Fixes { get; init; } = [];

    [JsonIgnore]
    public string DocId => Docs ?? Id;

    /// <summary>Boot-critical tweaks are always Expert.</summary>
    [JsonIgnore]
    public Risk EffectiveRisk => BootCritical || Actions.Any(a => a.IsBootCritical) ? Risk.Expert : Risk;

    [JsonIgnore]
    public bool IsBootCritical => BootCritical || Actions.Any(a => a.IsBootCritical);

    /// <summary>One of its actions can take several minutes (<see cref="TweakAction.TakesMinutes"/>), so new tweaks of that kind get the tag too.</summary>
    [JsonIgnore]
    public bool TakesMinutes => Actions.Any(a => a.TakesMinutes);

    /// <summary>
    /// May be part of "Apply recommended": Safe or Moderate, fully reversible, not boot-critical, no anti-cheat
    /// sensitivity, not a preview. Expert items are never included.
    /// </summary>
    [JsonIgnore]
    public bool IsBatchSafe => EffectiveRisk != Risk.Expert && Reversibility == Reversibility.Reversible && !IsBootCritical && !AntiCheatSensitive && !Preview;
}
