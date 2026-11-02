using System.Text.RegularExpressions;
using Optimizer.Core.Findings;

namespace Optimizer.Core.Docs;

public sealed record LintIssue(string DocId, string Language, string Message)
{
    public override string ToString() => $"[{Language}] {DocId}: {Message}";
}

/// <summary>
/// Explanation lint. Severity: warning in Debug builds, error in Release builds: the caller decides.
/// </summary>
public static partial class DocLint
{
    public const int MaxSummaryLength = 200;

    public static IEnumerable<string> RequiredDocIds() =>
        FindingEngine.CreateChecks(null).SelectMany(c => c.DocIds).Distinct(StringComparer.Ordinal);

    /// <summary>Explanation pages for tweaks: every catalog entry plus the runtime fixes.</summary>
    public static IEnumerable<string> RequiredTweakDocIds() =>
        Tweaks.TweakCatalog.Current.Tweaks.Select(t => t.DocId)
            .Concat(Findings.Checks.RuntimeFixes.DocIds).Concat(Tweaks.DeviceTweaks.DocIds).Concat(Startup.StartupTweaks.DocIds).Concat(["service.change", "feature.change", "capability.change", "priority.change", Tweaks.ReapplyTask.TweakId, Tweaks.NewUserDefaults.Id])
            .Distinct(StringComparer.Ordinal);

    public static List<LintIssue> Run()
    {
        var issues = new List<LintIssue>();
        foreach (var id in RequiredTweakDocIds())
        foreach (var lang in DocStore.Languages)
        {
            var page = DocStore.Get(id, lang);
            if (page is null)
            {
                issues.Add(new LintIssue(id, lang, "missing explanation page"));
                continue;
            }
            issues.AddRange(Check(page, DocHeadings.TweakRequired[lang], [DocHeadings.GeneratedChanges[lang], DocHeadings.GeneratedUndo[lang]]));
        }

        // Every tweak needs at least one source and a valid impact (catalog lint).
        foreach (var t in Tweaks.TweakCatalog.Current.Tweaks)
        {
            if (t.Sources.Count == 0) issues.Add(new LintIssue(t.Id, "catalog", "no sources"));
            if (t.Impact.Gaming is < 0 or > 5) issues.Add(new LintIssue(t.Id, "catalog", "impact outside 0-5"));
            if (t.Impact.Effect.Count == 0) issues.Add(new LintIssue(t.Id, "catalog", "no effect tag"));
            if (t.Impact.Basis is not ("measured" or "situational" or "disputed")) issues.Add(new LintIssue(t.Id, "catalog", $"unknown basis '{t.Impact.Basis}'"));
            if (t.Actions.Count == 0 && !t.Hidden) issues.Add(new LintIssue(t.Id, "catalog", "no actions"));
            foreach (var o in t.ImpactOverrides.Where(o => o.Gaming is < 0 or > 5))
                issues.Add(new LintIssue(t.Id, "catalog", $"impact override {o.Gaming} outside 0-5"));
            issues.AddRange(CheckUndocumented(t));
            foreach (var a in t.Actions.OfType<Actions.PowerSettingAction>())
                if (!Actions.PowerAliases.IsKnown(a.Subgroup) || !Actions.PowerAliases.IsKnown(a.Setting))
                    issues.Add(new LintIssue(t.Id, "catalog", "power setting must use a GUID or a known alias"));
        }
        foreach (var id in RequiredDocIds())
        foreach (var lang in DocStore.Languages)
        {
            var page = DocStore.Get(id, lang);
            if (page is null)
            {
                issues.Add(new LintIssue(id, lang, "missing explanation page"));
                continue;
            }
            issues.AddRange(Check(page, DocHeadings.FindingRequired[lang], [DocHeadings.Generated[lang]]));
        }

        // Every label key used must exist in both languages.
        var labels = Labels.Current;
        foreach (var lang in DocStore.Languages)
        foreach (var key in labels.Keys("en").Where(k => !labels.Has(lang, k)))
            issues.Add(new LintIssue("labels.json", lang, $"missing label '{key}'"));
        return issues;
    }

    public static IEnumerable<LintIssue> Check(DocPage page, string[] required, string[] generated)
    {
        var lang = page.Language;
        if (string.IsNullOrWhiteSpace(page.Title)) yield return new LintIssue(page.Id, lang, "missing '# Title'");

        var headings = page.Sections.Select(s => s.Heading).ToList();
        var position = -1;
        foreach (var h in required)
        {
            var i = headings.IndexOf(h);
            if (i < 0) yield return new LintIssue(page.Id, lang, $"missing section '## {h}'");
            else if (i < position) yield return new LintIssue(page.Id, lang, $"section '## {h}' out of order");
            else position = i;
        }
        foreach (var g in generated.Where(headings.Contains))
            yield return new LintIssue(page.Id, lang, $"'## {g}' is generated and must not be hand-written");
        if (!headings.Contains(DocHeadings.Sources[lang])) yield return new LintIssue(page.Id, lang, $"missing section '## {DocHeadings.Sources[lang]}'");

        foreach (var s in page.Sections.Where(s => string.IsNullOrWhiteSpace(StripDirectives(s.Body))))
            yield return new LintIssue(page.Id, lang, $"section '## {s.Heading}' is empty");

        // Summary ≤ 200 characters per variant (each variant is shown alone).
        var summary = page.Section(DocHeadings.Summary(lang))?.Body ?? "";
        foreach (var variantText in SplitVariants(summary))
        {
            var plain = PlaceholderPattern().Replace(variantText, "xxxxxxxx").Trim();
            if (plain.Length > MaxSummaryLength)
                yield return new LintIssue(page.Id, lang, $"summary is {plain.Length} characters (max {MaxSummaryLength})");
        }

        // Every [n] reference must resolve to an entry in the Sources section.
        var sources = page.Section(DocHeadings.Sources[lang])?.Body ?? "";
        var sourceNumbers = SourceEntryPattern().Matches(sources).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        var body = string.Join("\n", page.Sections.Where(s => s.Heading != DocHeadings.Sources[lang]).Select(s => s.Body));
        var cited = new HashSet<int>();
        foreach (Match m in ReferencePattern().Matches(body))
        {
            var n = int.Parse(m.Groups[1].Value);
            cited.Add(n);
            if (!sourceNumbers.Contains(n)) yield return new LintIssue(page.Id, lang, $"reference [{n}] has no entry under '## {DocHeadings.Sources[lang]}'");
        }
        // A source must support something on the page: a listed source the text never cites is removed or cited.
        foreach (var n in sourceNumbers.Where(n => !cited.Contains(n)).Order())
            yield return new LintIssue(page.Id, lang, $"source {n} is listed but the text never cites [{n}]");
    }

    private static string StripDirectives(string body) =>
        string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith(":::", StringComparison.Ordinal)));

    /// <summary>
    /// Returns the text shown for every status × variant combination (each is shown alone in the UI), once without and
    /// once with every ":::if" value set, so conditional text counts towards the length too.
    /// </summary>
    private static IEnumerable<string> SplitVariants(string body)
    {
        var variants = VariantPattern().Matches(body)
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct().Cast<string?>().Append(null).ToList();
        var allSet = IfPattern().Matches(body).Select(m => m.Groups[1].Value).Distinct().ToDictionary(p => p, _ => "x");
        foreach (var parameters in new[] { new Dictionary<string, string>(), allSet })
        foreach (var status in Enum.GetValues<FindingStatus>())
        foreach (var v in variants)
        {
            var finding = new Finding { Id = "lint", Kind = FindingKind.Finding, Status = status, Variant = v, Params = parameters };
            yield return DocStore.ApplyBlocks(body, finding);
        }
    }

    /// <summary>
    /// Tweak pages that may say "not documented" about something other than an undocumented value of their own: SysMain
    /// (about other memory features), the multiplane overlay value (NVIDIA documents it, Microsoft does not) and the page
    /// of retired tweaks.
    /// </summary>
    public static readonly string[] UndocumentedMentionAllowed = ["memory.sysmainOff", "gpu.mpoOff", "retired"];

    /// <summary>
    /// Undocumented tweaks that are marked permanent although their undocumented values are fully reversible: what cannot
    /// be undone comes from a documented value of the same tweak. privacy.inkingTypingOff: the documented policy "Turn off
    /// automatic learning" deletes the personal dictionary; its undocumented per-user values only set the Settings switch.
    /// </summary>
    public static readonly string[] UndocumentedPermanentAllowed = ["privacy.inkingTypingOff"];

    /// <summary>
    /// A tweak that uses an undocumented value says so on both pages, and a page that says so belongs to a tweak marked
    /// <see cref="Tweaks.TweakDefinition.Undocumented"/> (which the app shows as a badge). Undocumented values are only
    /// allowed for harmless, fully reversible changes below Expert risk; a tweak in
    /// <see cref="UndocumentedPermanentAllowed"/> may be permanent because of a documented value.
    /// </summary>
    public static IEnumerable<LintIssue> CheckUndocumented(Tweaks.TweakDefinition t)
    {
        var en = DocStore.Get(t.DocId, "en")?.Raw ?? "";
        var de = DocStore.Get(t.DocId, "de")?.Raw ?? "";
        if (t.Undocumented)
        {
            if (!UndocumentedEnPattern().IsMatch(en)) yield return new LintIssue(t.DocId, "en", "marked undocumented, but the page does not say that Microsoft does not document the value");
            if (!UndocumentedDePattern().IsMatch(de)) yield return new LintIssue(t.DocId, "de", "marked undocumented, but the page does not say that the value is not documented");
            if (t.EffectiveRisk == Tweaks.Risk.Expert
                || (t.Reversibility != Tweaks.Reversibility.Reversible && !UndocumentedPermanentAllowed.Contains(t.Id)))
                yield return new LintIssue(t.Id, "catalog", "undocumented values are allowed only for reversible changes below Expert risk");
            // Owner rule: an undocumented value is fine once it is proven to work; until then it is a preview.
            if (t.Proof.Count == 0 && !t.Preview)
                yield return new LintIssue(t.Id, "catalog", "undocumented and no \"proof\" that it works: mark it \"preview\" until a test shows the effect");
        }
        else if (!UndocumentedMentionAllowed.Contains(t.DocId) && UndocumentedEnPattern().IsMatch(en))
        {
            yield return new LintIssue(t.Id, "catalog", "the page says the value is undocumented, but the tweak is not marked \"undocumented\"");
        }
    }

    [GeneratedRegex(@"does not document|not documented|undocumented", RegexOptions.IgnoreCase)]
    private static partial Regex UndocumentedEnPattern();

    [GeneratedRegex(@"nicht dokumentiert|undokumentiert|dokumentiert[^.]*\bnicht\b", RegexOptions.IgnoreCase)]
    private static partial Regex UndocumentedDePattern();

    [GeneratedRegex(@":::\s*if(?:not)?\s+(\w+)")]
    private static partial Regex IfPattern();

    [GeneratedRegex(@"\{\{\w+\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"(?<!\]\()\[(\d+)\](?!\()")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^\s*(\d+)\.\s", RegexOptions.Multiline)]
    private static partial Regex SourceEntryPattern();

    [GeneratedRegex(@":::\s*variant\s+([\w,\s]+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex VariantPattern();
}
