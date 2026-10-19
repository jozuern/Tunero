using CommunityToolkit.Mvvm.ComponentModel;
using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public enum Page { Overview, Tweaks, Advisor, Network, Debloat, Cleanup, Storage, Startup, Services, Apps, Tools, InstallMedia, Health, Changes, Hardware, Settings }

/// <summary>A notice at the top of the page (Fluent InfoBar).</summary>
public sealed record Banner(string Text, bool IsWarning)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Text;

    public InfoBarSeverity Severity => IsWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

    /// <summary>Optional button in the info bar (e.g. "Apply again").</summary>
    public string? ActionText { get; init; }

    public System.Windows.Input.ICommand? Action { get; init; }

    public SymbolRegular ActionIcon { get; init; } = SymbolRegular.ArrowSync20;
}

public sealed record SummaryItem(string Label, string Value)
{
    /// <summary>Optional second line under the value (secondary text), for example the model under a disk size.</summary>
    public string? Detail { get; init; }

    /// <summary>Optional usage bar under the value, 0 to 1 (used share of a volume).</summary>
    public double? Fill { get; init; }

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => $"{Label}: {Value}";
}

public sealed record HwSection(string Title, IReadOnlyList<SummaryItem> Items)
{
    public SymbolRegular Icon { get; init; } = SymbolRegular.Info24;

    /// <summary>IDs and raw values, shown only with "Technical details" on (and always in the copied text and the report).</summary>
    public IReadOnlyList<SummaryItem> Technical { get; init; } = [];

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Title;
}

/// <summary>A summary tile at the top of the System info page.</summary>
public sealed record HwTile(string Caption, string Value, string Detail, SymbolRegular Icon)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => $"{Caption}: {Value}, {Detail}";
}

/// <summary>
/// Heading row of a group on the Tweaks page: recommended, on but working against the profile, or a category. A click
/// opens or closes it; the rows of an open group follow it in the same list, so the list still builds only the cards
/// in view.
/// </summary>
public sealed partial class TweakGroupRow(string key, string title, SymbolRegular icon, string summary, IReadOnlyList<TweakItemViewModel> items) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public SymbolRegular Icon { get; } = icon;
    public string Summary { get; } = summary;
    public IReadOnlyList<TweakItemViewModel> Items { get; } = items;

    /// <summary>
    /// What "Turn on" in the heading applies: the group's tweaks that are off and safe to apply together (safe, fully
    /// reversible, not boot-critical, no anti-cheat concern, not a preview), with nothing blocking them.
    /// </summary>
    public IReadOnlyList<TweakItemViewModel> Applicable { get; } = ApplicableOf(items);

    /// <summary>
    /// Off, Safe risk and batch-safe, and nothing blocks it (missing admin rights block every row alike: the button is
    /// then off with the rest of the page). Alternatives that exclude each other (the power plans) are left out: which
    /// one to use is a choice, not something to turn on all at once.
    /// </summary>
    private static List<TweakItemViewModel> ApplicableOf(IReadOnlyList<TweakItemViewModel> items)
    {
        var candidates = items.Where(i => !i.IsOn && i.CanToggle && i.Tweak.IsBatchSafe && i.Tweak.EffectiveRisk == Risk.Safe
                                          && i.Status_.Blocks.All(b => b.ReasonKey == "block.notElevated")).ToList();
        var ids = candidates.Select(i => i.Tweak.Id).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(i => !i.Tweak.ConflictsWith.Any(ids.Contains)
                                     && !candidates.Any(o => o.Tweak.ConflictsWith.Contains(i.Tweak.Id))).ToList();
    }

    public bool CanApplyAll => Applicable.Count > 0;

    public string ApplyAllText => Loc.Instance.Format("Tweaks_GroupApply", Applicable.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    public SymbolRegular Chevron => IsExpanded ? SymbolRegular.ChevronUp20 : SymbolRegular.ChevronDown20;

    /// <summary>What screen readers announce for the heading.</summary>
    public string AutomationName => $"{Title}, {Summary}";

    public override string ToString() => AutomationName;
}

public sealed record ChangeRecordItem(string TweakId, string Title, string When, string Details, string StateText, string Status, bool CanUndo)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Title;

    /// <summary>Set when the change is no longer in place: why (Windows update or other) for the row.</summary>
    public string? ResetText { get; init; }

    public bool IsReset => ResetText is not null;
}

public sealed record LogLine(string Time, string Text)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => $"{Time} {Text}";
}

/// <summary>
/// One line of the "Apply recommended" plan: what, why, and its impact. Lines of the plan can be unticked, so the user
/// applies only what they agree with.
/// </summary>
public sealed partial class RecommendationLine(string title, string reason, int impact, TweakDefinition? tweak = null, Action? selectionChanged = null) : ObservableObject
{
    public string Title { get; } = title;
    public string Reason { get; } = reason;

    /// <summary>The line's rating mark (the impact for the profile) and its words for screen readers and the tooltip.</summary>
    public int Rating { get; } = impact;
    public string ImpactText { get; } = Loc.Instance.Format("Impact_Short", impact);
    public bool IsDisputed { get; } = tweak?.Impact.Basis == "disputed";
    public TweakDefinition? Tweak { get; } = tweak;

    [ObservableProperty] private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => selectionChanged?.Invoke();

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Title;
}

/// <summary>A usage profile in the picker.</summary>
public sealed record ProfileOption(string Id, string Name, string Description, SymbolRegular Icon)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;
}

/// <summary>
/// A setting on this PC that works against the active profile: Undo when this app made it, otherwise Details, whose page
/// says how to change it back (the app has no original value to restore).
/// </summary>
public sealed record AgainstLine(string TweakId, string Title, string Reason, bool CanUndo)
{
    public bool SetOutside => !CanUndo;

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Title;
}
