using System.Text.RegularExpressions;
using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tools;

public sealed class FeatureCatalog
{
    public List<FeatureEntry> Features { get; init; } = [];
}

public sealed class FeatureEntry : IBilingual
{
    /// <summary>DISM feature name.</summary>
    public string Name { get; init; } = "";

    public string Title { get; init; } = "";
    public string TitleDe { get; init; } = "";
    public string En { get; init; } = "";
    public string De { get; init; } = "";

    /// <summary>Starts the Windows hypervisor when on (VBS-related performance note, anti-cheat note).</summary>
    public bool Hypervisor { get; init; }

    /// <summary>A Windows capability (Feature on Demand) instead of an optional feature; Name is the capability name.</summary>
    public bool Capability { get; init; }

    /// <summary>"off" = recommended off (e.g. SMB1), "on" = needed by games when off, null = your choice.</summary>
    public string? Recommend { get; init; }

    public string Label(string lang) => Bilingual.Pick(lang, Title, TitleDe);
}

/// <summary>
/// A Windows optional feature, switched with DISM (/English output, so the state is never localized). Most changes
/// need a restart; DISM exit code 3010 means "done, restart required".
/// </summary>
public sealed partial class OptionalFeatureAction : TweakAction
{
    public string Feature { get; init; } = "";
    public bool Enabled { get; init; }

    public override string TargetKey => $"feature:{Feature}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.feature", Feature);
    public override StoredValue Desired(ActionContext c) => new(true, "feature", Enabled ? "Enabled" : "Disabled");

    public override StoredValue? Read(ActionContext c) => ReadState(c.Processes, Feature) is { } s ? new StoredValue(true, "feature", s) : null;

    public override void Apply(ActionContext c) => Set(c.Processes, Feature, Enabled);

    /// <summary>
    /// Back to the original state. A feature that was on is turned on again without /All first: its parent features were
    /// on before (or it could not have been on), and /All would also turn on parents the user had turned off since. Only
    /// when DISM refuses that (a parent is off now) it runs with /All.
    /// </summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (original.Data == "Disabled") Set(c.Processes, Feature, false);
        else if (original.Data == "Enabled" && !TrySet(c.Processes, Feature, true, withParents: false)) Set(c.Processes, Feature, true);
    }

    /// <summary>"Enabled", "Disabled", or null when the feature does not exist on this edition.</summary>
    public static string? ReadState(IProcessRunner processes, string feature)
    {
        if (!IsSafeName(feature)) return null;
        var (code, output) = processes.Run("dism.exe", $"/Online /English /Get-FeatureInfo /FeatureName:{feature}", TimeSpan.FromMinutes(2));
        if (code != 0) return null;
        var m = StateRegex().Match(output);
        if (!m.Success) return null;
        // "Enable Pending" / "Disable Pending" count as the target state (a restart finishes them).
        return m.Groups[1].Value.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled";
    }

    private static void Set(IProcessRunner processes, string feature, bool enabled)
    {
        var (code, output) = Run(processes, feature, enabled, withParents: true);
        if (code is not (0 or 3010)) throw new InvalidOperationException($"DISM failed ({code}): {output.Trim()}");
    }

    private static bool TrySet(IProcessRunner processes, string feature, bool enabled, bool withParents) =>
        Run(processes, feature, enabled, withParents).ExitCode is 0 or 3010;

    private static (int ExitCode, string Output) Run(IProcessRunner processes, string feature, bool enabled, bool withParents)
    {
        if (!IsSafeName(feature)) throw new ArgumentException($"invalid feature name {feature}");
        var args = enabled
            ? $"/Online /English /Enable-Feature /FeatureName:{feature}{(withParents ? " /All" : "")} /NoRestart"
            : $"/Online /English /Disable-Feature /FeatureName:{feature} /NoRestart";
        return processes.Run("dism.exe", args, TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// All feature states in one DISM call (/Get-Features /Format:Table, English): "Name | State" rows. Pending states
    /// count as their target state.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadAll(IProcessRunner processes)
    {
        var (code, output) = processes.Run("dism.exe", "/Online /English /Get-Features /Format:Table", TimeSpan.FromMinutes(3));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (code != 0) return map;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[0].StartsWith("Feature Name", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith('-')) continue;
            var state = parts[1];
            if (!state.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) && !state.StartsWith("Disable", StringComparison.OrdinalIgnoreCase)) continue;
            map[parts[0]] = state.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled";
        }
        return map;
    }

    public static bool IsSafeName(string name) => name.Length is > 0 and < 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    [GeneratedRegex(@"^State\s*:\s*(Enabled|Disabled|Enable Pending|Disable Pending|Disabled with Payload Removed)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StateRegex();

    /// <summary>The id of a feature switch: the feature name as <see cref="TweakIds.Slug"/> makes it.</summary>
    public static string TweakId(string feature, bool enabled) => $"feature.{(enabled ? "on" : "off")}.{TweakIds.Slug(feature)}";

    /// <summary>The id earlier versions gave a feature switch (the lowercase feature name); their backups keep it.</summary>
    public static string LegacyTweakId(string feature, bool enabled) => $"feature.{(enabled ? "on" : "off")}.{feature.ToLowerInvariant()}";

    /// <summary>
    /// Engine tweak for one feature switch (backup, undo, restart badge). With <paramref name="hasBackup"/> (the backup
    /// store's check) a switch an earlier version made keeps its old id, so it is found, undone and never made twice.
    /// </summary>
    public static TweakDefinition Tweak(FeatureEntry f, bool enabled, Func<string, bool>? hasBackup = null) => new()
    {
        Id = TweakIds.OrLegacy(TweakId(f.Name, enabled), LegacyTweakId(f.Name, enabled), hasBackup),
        Docs = "feature.change",
        Subject = f.Title,
        Category = "Features",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Risk = f.Hypervisor && enabled ? Risk.Moderate : Risk.Safe,
        Restart = true,
        Verify = "afterRestart",
        Hidden = true,
        Actions = [new OptionalFeatureAction { Feature = f.Name, Enabled = enabled }],
        Sources = ["https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/enable-or-disable-windows-features-using-dism"],
    };
}
