using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Search;

public enum SearchKind { Tweak, Finding, DebloatApp, InstalledProgram, Service, ScheduledTask, StartupEntry, AppInstall, Tool, Setting, NotIncluded }

/// <param name="Page">The page key as the --page switch takes it (Tweaks, Network, Advisor, Overview and so on).</param>
/// <param name="ItemId">What the page selects: a tweak or finding id (as --select takes it), a package, service or task
/// name, a startup entry key, an installed program's key; for tools and settings the resource key of the card's title.</param>
/// <param name="Url">A page outside the app (docs/not-included.md for <see cref="SearchKind.NotIncluded"/>), else null.</param>
public sealed record SearchResult(SearchKind Kind, string Page, string? ItemId, string Title, string Snippet, int Score, string? Url);

/// <summary>One thing the search can find, in the language of the UI.</summary>
/// <param name="Keywords">Ids, registry value names, service names and synonyms: matched, never shown.</param>
public sealed record SearchItem(SearchKind Kind, string Page, string? ItemId, string Title, string Snippet, IReadOnlyList<string> Keywords, string? Url = null);

/// <summary>
/// Search over everything the app offers: catalog tweaks, findings, popular tweaks it leaves out (with the reason), and
/// lists the pages load at run time (services, tasks, startup entries, apps). Matching ignores case, accents and
/// umlauts ("Ubersicht" finds "Übersicht"), every word of the query must match, and titles weigh more than keywords and
/// descriptions. Thread-safe: the lists are immutable snapshots that a refresh swaps as a whole, so a search running on
/// another thread sees either the old or the new list, never half of one.
/// </summary>
public sealed partial class SearchEngine
{
    private sealed record Indexed(SearchItem Item, string Title, string Id, string Keys, string Snippet);

    /// <summary>Results kept for tweaks the app leaves out, however many other items match.</summary>
    public const int MaxNotIncluded = 3;

    private Indexed[] _static = [];
    private ImmutableDictionary<SearchKind, ImmutableArray<Indexed>> _runtime = ImmutableDictionary<SearchKind, ImmutableArray<Indexed>>.Empty;

    /// <summary>Replaces the fixed part of the index (catalog, tools, settings), for example after a language switch.</summary>
    public void SetStatic(IEnumerable<SearchItem> items) => Volatile.Write(ref _static, items.Select(Index).ToArray());

    /// <summary>Replaces the items of one kind that a page loaded (services after a refresh, for example).</summary>
    public void UpdateRuntime(SearchKind kind, IEnumerable<SearchItem> items)
    {
        var indexed = items.Select(Index).ToImmutableArray();
        ImmutableInterlocked.AddOrUpdate(ref _runtime, kind, indexed, (_, _) => indexed);
    }

    public IReadOnlyList<SearchResult> Search(string query, int max = 30)
    {
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || max <= 0) return [];
        var whole = string.Join(' ', terms);
        var all = Volatile.Read(ref _static).Concat(Volatile.Read(ref _runtime).Values.SelectMany(v => v));
        var hits = all
            .Select(e => (Entry: e, Score: Score(e, terms, whole)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        // What the app leaves out stays in view: "defender" matches dozens of services and startup entries, and the
        // reason Defender off is not offered must still show. It comes first when it matches at least as well as the best
        // thing the app offers (the search was probably for it), otherwise after everything the app offers.
        var leftOut = hits.Where(x => x.Entry.Item.Kind == SearchKind.NotIncluded).Take(Math.Min(MaxNotIncluded, max)).ToList();
        var offered = hits.Where(x => x.Entry.Item.Kind != SearchKind.NotIncluded).Take(max - leftOut.Count).ToList();
        var leftOutFirst = leftOut.Count > 0 && (offered.Count == 0 || leftOut[0].Score >= offered[0].Score);
        return (leftOutFirst ? leftOut.Concat(offered) : offered.Concat(leftOut))
            .Select(x => new SearchResult(x.Entry.Item.Kind, x.Entry.Item.Page, x.Entry.Item.ItemId, x.Entry.Item.Title, x.Entry.Item.Snippet, x.Score, x.Entry.Item.Url))
            .ToList();
    }

    /// <summary>0 when a word of the query matches nowhere; otherwise higher for matches in the title.</summary>
    private static int Score(Indexed e, string[] terms, string whole)
    {
        var score = 0;
        foreach (var term in terms)
        {
            var best = 0;
            if (StartsWord(e.Title, term)) best = 30;
            else if (e.Title.Contains(term, StringComparison.Ordinal)) best = 20;
            if (best < 25 && HasWord(e.Keys, term)) best = 25;
            if (best < 15 && (e.Id.Contains(term, StringComparison.Ordinal) || e.Keys.Contains(term, StringComparison.Ordinal))) best = 15;
            if (best < 5 && e.Snippet.Contains(term, StringComparison.Ordinal)) best = 5;
            if (best == 0) return 0;
            score += best;
        }
        if (e.Title == whole) score += 100;
        else if (e.Title.StartsWith(whole, StringComparison.Ordinal)) score += 50;
        else if (terms.Length > 1 && e.Title.Contains(whole, StringComparison.Ordinal)) score += 25;
        return score;
    }

    private static bool StartsWord(string text, string term) =>
        text.StartsWith(term, StringComparison.Ordinal) || text.Contains(" " + term, StringComparison.Ordinal);

    private static bool HasWord(string text, string term) => (" " + text + " ").Contains(" " + term + " ", StringComparison.Ordinal);

    private static Indexed Index(SearchItem item) =>
        new(item, Normalize(item.Title), Normalize(item.ItemId ?? ""), Normalize(string.Join(' ', item.Keywords)), Normalize(item.Snippet));

    /// <summary>
    /// Lower case without accents, ß as ss, and everything that is not a letter or digit as one space, so ids,
    /// registry names and paths split into words ("HideFileExt" stays one word, "8.3" becomes "8 3").
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Replace("ß", "ss", StringComparison.Ordinal).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------- items from the catalog ----------------

    /// <summary>Catalog tweaks, findings and the tweaks left out, in <paramref name="lang"/> (English where a page is missing).</summary>
    public static IEnumerable<SearchItem> CatalogItems(TweakCatalog tweaks, NotIncludedCatalog notIncluded, string lang) =>
        TweakItems(tweaks, lang).Concat(FindingItems(lang)).Concat(NotIncludedItems(notIncluded, lang));

    public static IEnumerable<SearchItem> TweakItems(TweakCatalog tweaks, string lang)
    {
        foreach (var t in tweaks.Visible)
        {
            var page = DocStore.GetOrEnglish(t.DocId, lang);
            var title = page?.Title ?? t.Id;
            if (t.Subject is { Length: > 0 } subject) title = $"{title}: {subject}";
            var keywords = new List<string> { t.Id, t.Category };
            if (t.Subject is { Length: > 0 }) keywords.Add(t.Subject);
            keywords.AddRange(t.Actions.SelectMany(ActionWords));
            yield return new SearchItem(SearchKind.Tweak, t.Id.StartsWith("nvidia.", StringComparison.Ordinal) ? "Network" : "Tweaks", t.Id, title,
                Summary(page), keywords);
        }
    }

    /// <summary>Findings have pages named after their check: A.* are BIOS and hardware advice, F* and G.* show on the Overview.</summary>
    public static IEnumerable<SearchItem> FindingItems(string lang)
    {
        const string prefix = "Catalog.Docs.en.";
        foreach (var name in CatalogData.ResourceNames(prefix))
        {
            var id = name[prefix.Length..^".md".Length];
            if (!FindingId().IsMatch(id)) continue;
            var page = DocStore.GetOrEnglish(id, lang);
            if (page is null) continue;
            yield return new SearchItem(SearchKind.Finding, id.StartsWith("A.", StringComparison.Ordinal) ? "Advisor" : "Overview", id,
                Placeholder().Replace(page.Title, "").Trim(), Summary(page), [id]);
        }
    }

    public static IEnumerable<SearchItem> NotIncludedItems(NotIncludedCatalog catalog, string lang) =>
        catalog.Entries.Select(e => new SearchItem(SearchKind.NotIncluded, "Tweaks", null, e.TitleIn(lang), e.ReasonIn(lang), [e.Id, .. e.Keywords],
            string.IsNullOrEmpty(catalog.Url) ? null : catalog.Url));

    /// <summary>
    /// The page's summary as a plain result line. A check's summary holds one text per status or variant (":::" blocks)
    /// and {{placeholders}} for the facts of this PC, which a search result does not have: it shows the text for a
    /// problem, or else the first text of any block, that needs no fact. Nothing when every text needs one.
    /// </summary>
    private static string Summary(DocPage? page)
    {
        var body = page?.Section(DocHeadings.Summary(page.Language))?.Body;
        if (string.IsNullOrWhiteSpace(body)) return "";
        var problem = new Finding { Id = page!.Id, Kind = FindingKind.Finding, Status = FindingStatus.Problem, Params = new Dictionary<string, string>() };
        return FirstComplete(DocStore.ApplyBlocks(body, problem)) ?? FirstComplete(Directive().Replace(body, "")) ?? "";

        static string? FirstComplete(string text) => ParagraphBreak().Split(text)
            .Select(p => string.Join(' ', p.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            .FirstOrDefault(p => p.Length > 0 && !p.Contains("{{", StringComparison.Ordinal));
    }

    /// <summary>A {{placeholder}} with the space before it: titles like "BIOS update with microcode {{min}}" lose it.</summary>
    [GeneratedRegex(@"\s*\{\{\w+\}\}")]
    private static partial Regex Placeholder();

    /// <summary>A ":::" line of a conditional block; it becomes a paragraph break.</summary>
    [GeneratedRegex(@"^[ \t]*:::.*$", RegexOptions.Multiline)]
    private static partial Regex Directive();

    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex ParagraphBreak();

    /// <summary>
    /// The names an action works on (registry value and key names, services, tasks): the words people search for. Left
    /// out: the target key and the key removed on undo, which repeat the whole path (every folder in it became a word,
    /// so "windows" or "explorer" listed every tweak below such a key), and the value type and refresh, which are
    /// internal words ("dword", "settings").
    /// </summary>
    private static IEnumerable<string> ActionWords(object action)
    {
        foreach (var p in action.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(string) || p.GetIndexParameters().Length > 0) continue;
            if (p.Name is nameof(Actions.TweakAction.TargetKey) or nameof(Actions.RegistryAction.RemoveKeyOnUndo)
                or nameof(Actions.RegistryAction.Kind) or nameof(Actions.RegistryAction.Notify)) continue;
            if (p.GetValue(action) is not string { Length: > 0 and < 200 } value) continue;
            // A key path contributes its last part ("Advanced"), not every folder above it.
            yield return p.Name == "Path" ? value.TrimEnd('\\').Split('\\')[^1] : value;
        }
    }

    [GeneratedRegex(@"^(A\.|F\d+\.|G\.)[A-Za-z0-9]+$")]
    private static partial Regex FindingId();
}
