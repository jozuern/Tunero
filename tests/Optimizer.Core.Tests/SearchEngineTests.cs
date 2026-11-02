using Optimizer.Core.Catalog;
using Optimizer.Core.Search;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>The app-wide search: matching rules, the catalog content, and the list of tweaks left out.</summary>
public class SearchEngineTests
{
    private static SearchEngine CatalogEngine(string lang)
    {
        var engine = new SearchEngine();
        engine.SetStatic(SearchEngine.CatalogItems(TweakCatalog.Current, CatalogData.Current.NotIncluded, lang));
        return engine;
    }

    [Theory]
    [InlineData("Übersicht", "ubersicht")]
    [InlineData("Straße", "strasse")]
    [InlineData("HideFileExt", "hidefileext")]
    [InlineData("8.3 file names", "8 3 file names")]
    [InlineData("  Café -- crème  ", "cafe creme")]
    public void NormalizeIgnoresCaseAccentsAndPunctuation(string text, string expected) => Assert.Equal(expected, SearchEngine.Normalize(text));

    [Fact]
    public void EveryWordMustMatchAndTitlesComeFirst()
    {
        var engine = new SearchEngine();
        engine.SetStatic(
        [
            new SearchItem(SearchKind.Tweak, "Tweaks", "a", "Show file extensions", "Shows .exe", ["HideFileExt"]),
            new SearchItem(SearchKind.Tweak, "Tweaks", "b", "Compact view", "Less space between file rows", []),
            new SearchItem(SearchKind.NotIncluded, "Tweaks", null, "Extensions of hidden files", "Left out", []),
        ]);
        var hits = engine.Search("file ext");
        Assert.Equal(["a", null], hits.Select(h => h.ItemId));
        Assert.Equal("a", engine.Search("hidefileext").Single().ItemId);
        Assert.Equal("b", engine.Search("rows").Single().ItemId);
        Assert.Empty(engine.Search("file nothing"));
        Assert.Empty(engine.Search("   "));
    }

    [Fact]
    public void LeftOutTweaksStayVisibleAmongManyHits()
    {
        var engine = new SearchEngine();
        engine.SetStatic([new SearchItem(SearchKind.NotIncluded, "Tweaks", null, "Defender off", "Not included", ["defender"])]);
        engine.UpdateRuntime(SearchKind.Service, Enumerable.Range(0, 50).Select(i => new SearchItem(SearchKind.Service, "Services", $"d{i}", $"Defender service {i}", "", [])));
        var hits = engine.Search("defender", 10);
        Assert.Equal(10, hits.Count);
        // As good a match as the services: the reason comes first.
        Assert.Equal(SearchKind.NotIncluded, hits[0].Kind);
    }

    [Fact]
    public void RuntimeItemsReplaceTheirKindOnly()
    {
        var engine = new SearchEngine();
        engine.SetStatic([new SearchItem(SearchKind.Tool, "Tools", null, "Spooler tool", "", [])]);
        engine.UpdateRuntime(SearchKind.Service, [new SearchItem(SearchKind.Service, "Services", "Spooler", "Print Spooler", "", ["Spooler"])]);
        Assert.Equal(2, engine.Search("spooler").Count);
        engine.UpdateRuntime(SearchKind.Service, []);
        Assert.Equal(SearchKind.Tool, engine.Search("spooler").Single().Kind);
    }

    [Fact]
    public void SearchAndUpdatesCanRunAtTheSameTime()
    {
        var engine = CatalogEngine("en");
        var items = Enumerable.Range(0, 200).Select(i => new SearchItem(SearchKind.Service, "Services", $"svc{i}", $"Service {i}", "", [])).ToList();
        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0) engine.UpdateRuntime(SearchKind.Service, items.Take(i));
            else Assert.NotNull(engine.Search("service"));
        });
    }

    [Theory]
    [InlineData("en", "file extensions", "explorer.fileExtensions")]
    [InlineData("en", "HideFileExt", "explorer.fileExtensions")]
    [InlineData("de", "dateierweiterungen", "explorer.fileExtensions")]
    [InlineData("en", "resizable bar", "A.rebar")]
    public void CatalogItemsAreFound(string lang, string query, string id) =>
        Assert.Contains(CatalogEngine(lang).Search(query), r => r.ItemId == id);

    /// <summary>
    /// A key path gives its last part only: the target key and the key removed on undo repeat the whole path, and every
    /// folder in it ("CurrentVersion", "Explorer") made each tweak below it a whole-word match.
    /// </summary>
    [Theory]
    [InlineData("currentversion")]
    [InlineData("dword")]
    public void FoldersAndValueTypesOfAKeyAreNoSearchWords(string query) =>
        Assert.DoesNotContain(CatalogEngine("en").Search(query, 200), r => r.Kind == SearchKind.Tweak);

    [Theory]
    [InlineData("en", "defender")]
    [InlineData("en", "core parking")]
    [InlineData("de", "echtzeit")]
    [InlineData("en", "registry cleaner")]
    public void TweaksLeftOutExplainWhy(string lang, string query)
    {
        var hit = CatalogEngine(lang).Search(query).First(r => r.Kind == SearchKind.NotIncluded);
        Assert.False(string.IsNullOrWhiteSpace(hit.Snippet));
        Assert.Equal(CatalogData.Current.NotIncluded.Url, hit.Url);
    }

    /// <summary>
    /// A result is plain text: a check's page holds ":::" blocks per status or variant and {{placeholders}} for the facts
    /// of one PC, which have no meaning in a search result (they once showed up as "::: status Problem").
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void ResultsShowNoPageMarkup(string lang) =>
        Assert.All(SearchEngine.CatalogItems(TweakCatalog.Current, CatalogData.Current.NotIncluded, lang), item =>
        {
            Assert.DoesNotContain(":::", item.Title + item.Snippet, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", item.Title + item.Snippet, StringComparison.Ordinal);
        });

    /// <summary>A summary with one text per variant shows the first that needs no fact of this PC.</summary>
    [Fact]
    public void VariantSummaryShowsATextWithoutFacts() =>
        Assert.StartsWith("Checks whether Resizable BAR", SearchEngine.FindingItems("en").Single(i => i.ItemId == "A.rebar").Snippet, StringComparison.Ordinal);

    [Fact]
    public void NvidiaTweaksPointToTheGraphicsPage() =>
        Assert.All(SearchEngine.TweakItems(TweakCatalog.Current, "en").Where(i => i.ItemId!.StartsWith("nvidia.", StringComparison.Ordinal)),
            i => Assert.Equal("Network", i.Page));

    /// <summary>Every entry has both languages, and names a row of docs/not-included.md, so the two lists stay in step.</summary>
    [Fact]
    public void NotIncludedEntriesMatchTheDocument()
    {
        var doc = File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "not-included.md"));
        var rows = doc.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Tweak |", StringComparison.Ordinal)).ToList();
        var entries = CatalogData.Current.NotIncluded.Entries;
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        foreach (var e in entries)
        {
            Assert.True(rows.Any(r => r.StartsWith("| " + e.Doc, StringComparison.Ordinal)), $"{e.Id}: no row starting with \"{e.Doc}\"");
            foreach (var lang in new[] { "en", "de" })
            {
                Assert.True(e.Title.ContainsKey(lang) && e.Reason.ContainsKey(lang), $"{e.Id}: {lang} missing");
                Assert.True(e.Reason[lang].Length <= 260, $"{e.Id}: {lang} reason longer than 260 characters");
            }
        }
        Assert.Equal(rows.Count, entries.Count);
    }
}
