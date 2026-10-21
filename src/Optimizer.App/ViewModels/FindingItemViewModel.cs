using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

/// <summary>A finding/advisor item as shown in the list and the inspector, in the current language.</summary>
public sealed class FindingItemViewModel : InspectorItem
{
    /// <param name="goal">The active profile's goal ("gaming", "battery", ...): names what the impact is measured on.</param>
    public FindingItemViewModel(Finding finding, string lang, TweakDefinition? fix = null, string goal = "gaming")
    {
        Finding = finding;
        Fix = fix;
        var labels = Labels.Current;
        var page = DocStore.GetOrEnglish(finding.Id, lang);
        var title = page is null ? finding.Id : DocStore.Substitute(page.Title, finding.Params);
        Title = finding.Subject is { Length: > 0 } s && !title.Contains(s, StringComparison.OrdinalIgnoreCase) ? $"{title}: {s}" : title;
        Summary = page is null ? "" : DocStore.Summary(page, finding);
        var heading = Title;
        MarkdownFactory = () => page is null ? $"# {finding.Id}\n\n{Loc.Instance["Details_NoPage"]}" : $"# {heading}\n\n" + DocStore.RenderFinding(page, finding, labels);
        StatusText = labels.Get(lang, $"status.{finding.Status}");
        Status = finding.Status.ToString();
        EffectsText = string.Join(", ", finding.Effects.Where(e => e != Effect.Prerequisite || finding.Impact is null).Select(e => labels.Get(lang, $"effect.{e}")));
        var rated = finding.Impact is not null && !finding.Critical && finding.Status != FindingStatus.Unsupported;
        Impact = rated ? Math.Clamp(finding.Impact!.Value, 0, 5) : null;
        ImpactText = Impact is { } i ? Loc.Instance.Format("Impact_Short", i) : "";
        ImpactTooltip = Impact is { } j ? Loc.Instance.Format("Impact_TooltipGoal", labels.Get(lang, $"effect.{goal}"), j) : "";
        CriticalText = finding.Critical ? Loc.Instance["Impact_Critical"] : null;
        // "No FPS effect" and "Needed to start games" are not things a finding affects: they stand without "Affects:".
        var plain = finding.Effects.All(e => e is Effect.None or Effect.Prerequisite);
        // The impact is the row's rating mark, not words in this line (as on tweak rows); "Critical" has no mark and stays.
        var rowImpact = CriticalText ?? "";
        Rating = Impact;
        MetaText = plain
            ? string.Join(". ", new[] { Meta(StatusText, rowImpact, ""), EffectsText }.Where(x => x.Length > 0))
            : Meta(StatusText, rowImpact, EffectsText);
        if (fix is not null && finding.Status == FindingStatus.Problem) ActionText = Loc.Instance["Fix_Apply"];
    }

    public Finding Finding { get; }

    /// <summary>One-click fix for this finding (runtime fix or catalog tweak), if any.</summary>
    public TweakDefinition? Fix { get; }

    public bool IsProblem => Finding.Status == FindingStatus.Problem;

    /// <summary>A result the user can ignore: a problem, a note or an unknown on the Overview or Manual checks (not a passed check).</summary>
    public bool CanIgnore => IgnoredResults.CanIgnore(Finding);

    /// <summary>Listed under "Ignored": the details pane offers Stop ignoring instead.</summary>
    public bool IsIgnored { get; init; }

    public bool OffersIgnore => CanIgnore && !IsIgnored;

    public string IgnoreText => Loc.Instance["Findings_Ignore"];
    public override string Key => "finding:" + Finding.Key;
}
