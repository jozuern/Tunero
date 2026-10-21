using System.Text.RegularExpressions;
using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tools;

/// <summary>
/// A Windows capability (Feature on Demand, Settings > System > Optional features), added or removed with DISM
/// (/English output). Adding one downloads it from Windows Update. Stored value "Enabled" (installed) or "Disabled".
/// </summary>
public sealed partial class OptionalCapabilityAction : TweakAction
{
    public string Capability { get; init; } = "";
    public bool Installed { get; init; }

    public override string TargetKey => $"capability:{Capability}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.capability", Capability);
    public override StoredValue Desired(ActionContext c) => new(true, "feature", Installed ? "Enabled" : "Disabled");

    /// <summary>Adding downloads the capability from Windows Update, and DISM services the image either way (30 minutes allowed below).</summary>
    public override bool TakesMinutes => true;

    public override StoredValue? Read(ActionContext c) => ReadState(c.Processes, Capability) is { } s ? new StoredValue(true, "feature", s) : null;

    public override void Apply(ActionContext c) => Set(c.Processes, Capability, Installed);

    public override void Restore(ActionContext c, StoredValue original)
    {
        if (original.Data is "Enabled" or "Disabled") Set(c.Processes, Capability, original.Data == "Enabled");
    }

    public static bool IsSafeName(string name) => name.Length is > 0 and < 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~');

    /// <summary>"Installed" and "Install Pending" count as installed; null when Windows does not know the capability.</summary>
    public static string? MapState(string state) => state.Trim() switch
    {
        var s when s.StartsWith("Installed", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Install Pending", StringComparison.OrdinalIgnoreCase) => "Enabled",
        var s when s.StartsWith("Not Present", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Staged", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("Uninstall Pending", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Removed", StringComparison.OrdinalIgnoreCase) => "Disabled",
        _ => null,
    };

    public static string? ReadState(IProcessRunner processes, string capability)
    {
        if (!IsSafeName(capability)) return null;
        var (code, output) = processes.Run("dism.exe", $"/Online /English /Get-CapabilityInfo /CapabilityName:{capability}", TimeSpan.FromMinutes(2));
        if (code != 0) return null;
        var m = StateRegex().Match(output);
        return m.Success ? MapState(m.Groups[1].Value) : null;
    }

    private static void Set(IProcessRunner processes, string capability, bool installed)
    {
        if (!IsSafeName(capability)) throw new ArgumentException($"invalid capability name {capability}");
        var args = installed ? $"/Online /English /Add-Capability /CapabilityName:{capability} /NoRestart" : $"/Online /English /Remove-Capability /CapabilityName:{capability} /NoRestart";
        var (code, output) = processes.Run("dism.exe", args, TimeSpan.FromMinutes(30));
        if (code is not (0 or 3010)) throw new InvalidOperationException(Optimizer.Core.Media.DismOutput.Failure(code, output));
    }

    /// <summary>All capability states in one DISM call (/Get-Capabilities /Format:Table): "Capability Identity | State" rows.</summary>
    public static IReadOnlyDictionary<string, string> ReadAll(IProcessRunner processes)
    {
        var (code, output) = processes.Run("dism.exe", "/Online /English /Get-Capabilities /Format:Table", TimeSpan.FromMinutes(3));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (code != 0) return map;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[0].StartsWith('-')) continue;
            if (MapState(parts[1]) is { } state) map[parts[0]] = state;
        }
        return map;
    }

    [GeneratedRegex(@"^State\s*:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex StateRegex();

    public static TweakDefinition Tweak(FeatureEntry f, bool installed) => new()
    {
        Id = $"capability.{(installed ? "on" : "off")}.{TweakIds.Slug(f.Name)}",
        Docs = "capability.change",
        Subject = f.Title,
        Category = "Features",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Risk = Risk.Safe,
        Hidden = true,
        Actions = [new OptionalCapabilityAction { Capability = f.Name, Installed = installed }],
        Sources = ["https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/features-on-demand-non-language-fod"],
    };
}
