using System.Text.RegularExpressions;
using System.Xml.Linq;
using Optimizer.Core.Docs;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>Explanation lint and user-visible text rules: every finding fails the test, in every build configuration.</summary>
public partial class DocLintTests(ITestOutputHelper output)
{
    [Fact]
    public void ExplanationsAreComplete()
    {
        var issues = DocLint.Run();
        foreach (var issue in issues) output.WriteLine(issue.ToString());
        // An error in every configuration: plain "dotnet test" (Debug) must report what CI reports.
        Assert.Empty(issues);
    }

    /// <summary>
    /// Every source a tweak lists in the catalog is cited on its EN and DE page (the DE page may link the de-de version):
    /// a source nobody can see on the page proves nothing there.
    /// </summary>
    [Fact]
    public void CatalogSourcesAreCitedOnTheirPages()
    {
        static string Norm(string url) => Regex.Replace(url.ToLowerInvariant(), "/(en-us|de-de)/", "/").TrimEnd('/');
        var problems = new List<string>();
        foreach (var t in Tweaks.TweakCatalog.Current.Tweaks)
            foreach (var lang in DocStore.Languages)
                if (DocStore.Get(t.DocId, lang) is { } page)
                    problems.AddRange(t.Sources.Where(s => !Norm(page.Raw).Contains(Norm(s), StringComparison.Ordinal))
                        .Select(s => $"{t.Id} ({lang}): {s} is not cited on {t.DocId}.md"));
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    [Fact]
    public void LabelsExistInBothLanguages() =>
        Assert.DoesNotContain(DocLint.Run(), i => i.DocId == "labels.json");

    /// <summary>Style rule: no middle dots, bullets, en or em dashes, ellipsis characters or "(s)" plurals in anything the user reads.</summary>
    [Fact]
    public void UserVisibleTextHasNoBannedTypography()
    {
        var offenders = new List<string>();
        foreach (var name in Catalog.CatalogData.ResourceNames("Catalog."))
        {
            var text = Catalog.CatalogData.ReadResourceText(name);
            // JSON comments ("_comment") are for maintainers, not shown in the app.
            var visible = name.EndsWith(".json", StringComparison.Ordinal)
                ? string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("\"_comment\"", StringComparison.Ordinal)))
                : text;
            offenders.AddRange(BannedTypography(name, visible));
        }
        // The app's own UI text: resource string values and literal text in XAML (comments are for maintainers).
        var app = RepoPaths.App;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(app, "Resources"), "Strings*.resx"))
            offenders.AddRange(BannedTypography(Path.GetFileName(file),
                string.Join("\n", XDocument.Load(file).Descendants("data").Select(d => (string?)d.Element("value") ?? ""))));
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(app, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            offenders.AddRange(BannedTypography(relative, XamlComment().Replace(File.ReadAllText(file), "")));
        }
        foreach (var o in offenders) output.WriteLine(o);
        Assert.Empty(offenders);
    }

    /// <summary>
    /// No control characters in sources: a shell edit turns "\v" or "\a" in a path literal into a vertical tab or a bell
    /// (ProcessHardening once had "WindowsPowerShell", vertical tab, "1.0"), which compiles and silently breaks the path.
    /// </summary>
    [Fact]
    public void SourcesHaveNoControlCharacters()
    {
        string[] extensions = [".cs", ".xaml", ".json", ".resx", ".md", ".yml", ".props", ".csproj", ".ps1"];
        var offenders = new List<string>();
        foreach (var top in new[] { "src", "tests", "docs", ".github", "scripts" })
        {
            var folder = Path.Combine(RepoPaths.Root, top);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepoPaths.Root, file);
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                    relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].Any(c => c < ' ' && c != '\t' && c != '\r'))
                        offenders.Add($"{relative}:{i + 1}");
            }
        }
        foreach (var o in offenders) output.WriteLine(o);
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Every UI string key exists in English and German: the keys of both resource files match, every key the code or
    /// XAML names is defined, and so are the keys built at run time (profile names and descriptions, sensor types).
    /// </summary>
    [Fact]
    public void UiStringKeysExistInBothLanguages()
    {
        HashSet<string> Keys(string file) => XDocument.Load(Path.Combine(RepoPaths.App, "Resources", file)).Descendants("data")
            .Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
        var en = Keys("Strings.resx");
        var de = Keys("Strings.de.resx");
        var missing = en.Except(de).Select(k => $"{k}: no German text").Concat(de.Except(en).Select(k => $"{k}: no English text")).ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepoPaths.App, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in CodeKeyPattern().Matches(File.ReadAllText(file))) used.Add(m.Groups[1].Value);
            // Keys picked in a condition (x ? "A" : "B") or read through a local alias (var l = Loc.Instance; l["Hw_Cpu"])
            // count too: every key-shaped literal on a line that reads the strings.
            foreach (var line in File.ReadLines(file).Where(line => LocLinePattern().IsMatch(line)))
                foreach (Match m in KeyLiteralPattern().Matches(line)) used.Add(m.Groups[1].Value);
        }
        foreach (var file in Directory.EnumerateFiles(RepoPaths.App, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in XamlKeyPattern().Matches(File.ReadAllText(file))) used.Add(m.Groups[1].Value);
        foreach (var p in Catalog.CatalogData.Current.Profiles.Profiles) used.UnionWith([$"Profile_{p.Id}", $"ProfileDesc_{p.Id}"]);
        foreach (var type in new[] { "Temperature", "Load", "Clock", "Power", "Fan" }) used.Add($"SensorType_{type}");
        // Quick fix cards build their keys from the fix id; the question only for fixes that ask first.
        foreach (var fix in Tools.QuickFixes.All)
        {
            used.UnionWith([$"Quick_{fix.Id}", $"Quick_{fix.Id}Hint", $"Quick_{fix.Id}Done"]);
            if (fix.Confirm) used.Add($"Quick_{fix.Id}Confirm");
        }
        missing.AddRange(used.Where(k => !en.Contains(k)).Select(k => $"{k}: used but not defined"));

        foreach (var m in missing) output.WriteLine(m);
        Assert.Empty(missing);
    }

    /// <summary>
    /// The install media page builds keys at run time: a text for every reason a build can fail with
    /// (MediaBuildException.Keys), every stage, every offered edition, every account name problem and catalog step.
    /// </summary>
    [Fact]
    public void MediaKeysExistInBothLanguages()
    {
        string[] keys =
        [
            .. Media.MediaBuildException.Keys,
            .. Enum.GetNames<Media.MediaStage>().Select(s => "Media_Stage_" + s),
            .. Media.MediaCatalog.OfferedEditions.Select(e => "Media_Edition_" + e),
            "Media_NameEmpty", "Media_NameTooLong", "Media_NameInvalid", "Media_NameReserved",
            .. new[] { "search", "cab", "unpack" }.Select(s => "Media_CatalogStep_" + s),
        ];
        var missing = new List<string>();
        foreach (var file in new[] { "Strings.resx", "Strings.de.resx" })
        {
            var defined = XDocument.Load(Path.Combine(RepoPaths.App, "Resources", file)).Descendants("data")
                .Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
            missing.AddRange(keys.Where(k => !defined.Contains(k)).Select(k => $"{file}: {k}"));
        }
        foreach (var m in missing) output.WriteLine(m);
        Assert.Empty(missing);
    }

    [GeneratedRegex(@"Loc\.Instance(?:\[|\.Format\()""(\w+)""")]
    private static partial Regex CodeKeyPattern();

    [GeneratedRegex(@"Loc\.Instance|\bl\[""")]
    private static partial Regex LocLinePattern();

    [GeneratedRegex(@"""([A-Z][A-Za-z0-9]*_\w+)""")]
    private static partial Regex KeyLiteralPattern();

    [GeneratedRegex(@"\{l:Tr (\w+)\}")]
    private static partial Regex XamlKeyPattern();

    /// <summary>The same rule for the repository's own documents, and for code (comments included), minus the plural rule there.</summary>
    [Fact]
    public void DocumentsAndCodeHaveNoBannedTypography()
    {
        var offenders = new List<string>();
        foreach (var file in RepositoryFiles("*.md").Where(f => !f.Replace('\\', '/').Contains("/Catalog/Docs/", StringComparison.Ordinal) && !IsQuotedReference(f)))
            offenders.AddRange(BannedTypography(Path.GetRelativePath(RepoPaths.Root, file), File.ReadAllText(file)));
        foreach (var file in RepositoryFiles("*.cs").Concat(RepositoryFiles("*.csproj")).Concat(RepositoryFiles("*.yml")))
            offenders.AddRange(BannedCharacters(Path.GetRelativePath(RepoPaths.Root, file), File.ReadAllText(file)));
        foreach (var file in WebsiteCopy())
            offenders.AddRange(BannedTypography(Path.GetRelativePath(RepoPaths.Root, file), File.ReadAllText(file)));
        foreach (var o in offenders) output.WriteLine(o);
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Research notes that quote other tools' feature names and descriptions word for word; they are not shown in the
    /// app, and rewriting the quotes would misstate what those tools say.
    /// </summary>
    private static bool IsQuotedReference(string file) =>
        file.EndsWith("Windows_Optimizer_Tools_Feature_Reference.md", StringComparison.Ordinal);

    /// <summary>
    /// The text of tunero.dev: its English and German copy and its pages and components (website/src).
    /// </summary>
    private static IEnumerable<string> WebsiteCopy() =>
        RepositoryFiles("*.ts").Concat(RepositoryFiles("*.astro")).Concat(RepositoryFiles("*.svelte"))
            .Where(f => Path.GetRelativePath(RepoPaths.Root, f).Replace('\\', '/').StartsWith("website/src/", StringComparison.Ordinal));

    private static readonly char[] Banned = ['\u2013', '\u2014', '\u2022', '\u00B7', '\u2026'];

    private static IEnumerable<string> BannedTypography(string name, string text)
    {
        foreach (var hit in BannedCharacters(name, text)) yield return hit;
        foreach (Match m in ParenPlural().Matches(text)) yield return $"{name}: \"{m.Value}\" plural";
    }

    private static IEnumerable<string> BannedCharacters(string name, string text) =>
        Banned.Where(text.Contains).Select(c => $"{name}: U+{(int)c:X4}");

    private static bool IsBuildOutput(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tracked-looking files of the repository: build output, .git, the local audit folder and installed npm packages
    /// (node_modules, their own text) are skipped.
    /// </summary>
    private static IEnumerable<string> RepositoryFiles(string pattern) =>
        Directory.EnumerateFiles(RepoPaths.Root, pattern, SearchOption.AllDirectories).Where(f =>
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, f).Replace('\\', '/');
            return !IsBuildOutput("/" + relative) && !relative.StartsWith(".git/", StringComparison.Ordinal) && !relative.StartsWith(".audit/", StringComparison.Ordinal)
                   && !("/" + relative).Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
                   && !relative.StartsWith(".claude/", StringComparison.Ordinal) && !relative.StartsWith(".superpowers/", StringComparison.Ordinal)
                   && !relative.StartsWith("artifacts/", StringComparison.Ordinal)
                   && !relative.Equals("CLAUDE.md", StringComparison.Ordinal);
        });

    /// <summary>
    /// An explanation page that sends the reader to an app page names it as the navigation rail does in that language:
    /// "the Health page", "the **Startup** page" (EN), "die Seite Zustand", "auf der Seite **Autostart**" (DE). A rename
    /// in the UI (Home to Overview, Debloat to Remove apps) otherwise leaves pages pointing at a page that does not exist.
    /// </summary>
    [Fact]
    public void ExplanationPagesNameCurrentAppPages()
    {
        var problems = new List<string>();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lang in DocStore.Languages)
        {
            var titles = NavigationTitles(lang == "en" ? "Strings.resx" : "Strings.de.resx");
            foreach (var name in Catalog.CatalogData.ResourceNames($"Catalog.Docs.{lang}."))
            {
                var id = name[$"Catalog.Docs.{lang}.".Length..^".md".Length];
                foreach (var named in PageNames(lang, Catalog.CatalogData.ReadResourceText(name), titles).Where(n => !titles.Contains(n)))
                {
                    var key = $"{lang}/{id}: {named}";
                    if (OtherPrograms.ContainsKey(key)) allowed.Add(key);
                    else problems.Add($"{lang}/{id}.md: \"{named}\" is not a page of the app (navigation: {string.Join(", ", titles)})");
                }
            }
        }
        // An entry nobody needs any more would let a stale name back in on that page.
        problems.AddRange(OtherPrograms.Keys.Where(k => !allowed.Contains(k)).Select(k => $"OtherPrograms: \"{k}\" no longer matches; remove it"));
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    /// <summary>The patterns catch the stale names an audit found by hand and leave ordinary words alone.</summary>
    [Theory]
    [InlineData("en", "To remove OneDrive completely, use the Debloat page instead.", "Debloat")]
    [InlineData("en", "Use the benchmark on the Home page before and after.", "Home")]
    [InlineData("en", "Open the **Startup** page in this app.", "Startup")]
    [InlineData("en", "a tweak and the Services & tasks page on one service", "Services & tasks")]
    [InlineData("en", "Download it from the board vendor's support page.", "")]
    [InlineData("en", "Open the support page for your board.", "")]
    [InlineData("de", "Um OneDrive ganz zu entfernen, nutze die Seite Debloat.", "Debloat")]
    [InlineData("de", "Öffne in dieser App die Seite **Netzwerk**.", "Netzwerk")]
    [InlineData("de", "Entferne sie auf der Seite Apps entfernen.", "Apps entfernen")]
    [InlineData("de", "Der Benchmark auf der Seite Zustand misst die Wirkung.", "Zustand")]
    [InlineData("de", "Windows zeigt seine Seite mit Datenschutzeinstellungen.", "")]
    public void PageNamePatternsFindTheNamedPage(string lang, string text, string expected)
    {
        var titles = NavigationTitles(lang == "en" ? "Strings.resx" : "Strings.de.resx");
        Assert.Equal(expected == "" ? [] : new[] { expected }, PageNames(lang, text, titles));
    }

    private static IEnumerable<string> PageNames(string lang, string text, HashSet<string> titles) =>
        lang == "en" ? EnglishPageNames(text) : GermanPageNames(text, titles);

    /// <summary>
    /// Pages of other programs that the page name patterns also match. Keyed by "language/doc id: name", so a stale
    /// app page name (like "Home") is still caught everywhere else.
    /// </summary>
    private static readonly Dictionary<string, string> OtherPrograms = new(StringComparer.Ordinal)
    {
        ["en/personalize.settingsHomeHidden: Home"] = "the Home page of the Windows Settings app, which the tweak hides",
        ["en/privacy.recentFilesOff: File Explorer home"] = "the home page of File Explorer",
        ["en/edge.newTabQuiet: Edge new tab"] = "Edge's new tab page",
        ["en/startup.runkey: Startup apps"] = "the Startup apps page of Task Manager and Settings",
        ["en/A.virtualization: CPU or Advanced"] = "pages of the BIOS setup",
        ["en/G.access: BIOS boot or security"] = "pages of the BIOS setup",
        ["de/startup.runkey: Autostart-Apps"] = "die Seite Autostart-Apps im Task-Manager und in den Einstellungen",
    };

    /// <summary>The navigation rail's page titles of one language: the Nav_ strings minus the toggle and the group headers.</summary>
    private static HashSet<string> NavigationTitles(string file) =>
        XDocument.Load(Path.Combine(RepoPaths.App, "Resources", file)).Descendants("data")
            .Where(d => NavTitleKey().IsMatch((string)d.Attribute("name")!))
            .Select(d => ((string?)d.Element("value") ?? "").Trim())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>"the X page" with X starting in a capital letter, one to four words, optionally in bold.</summary>
    private static IEnumerable<string> EnglishPageNames(string text) =>
        EnglishPageName().Matches(text).Select(m => m.Groups["name"].Value);

    /// <summary>
    /// "die Seite X" / "der Seite X" with X starting in a capital letter or in bold. German titles have lowercase
    /// words ("Apps entfernen"), so a plain X counts as a title when one starts the text; otherwise its first word is
    /// reported.
    /// </summary>
    private static IEnumerable<string> GermanPageNames(string text, HashSet<string> titles)
    {
        foreach (Match m in GermanPageName().Matches(text))
        {
            if (m.Groups["bold"].Success) { yield return m.Groups["bold"].Value; continue; }
            var rest = m.Groups["rest"].Value;
            var title = titles.FirstOrDefault(t => rest.StartsWith(t, StringComparison.Ordinal)
                                                   && (rest.Length == t.Length || !(char.IsLetterOrDigit(rest[t.Length]) || rest[t.Length] == '-')));
            yield return title ?? GermanWord().Match(rest).Value;
        }
    }

    [GeneratedRegex(@"^Nav_(?!Toggle$|Group)\w+$")]
    private static partial Regex NavTitleKey();

    [GeneratedRegex(@"\b[Tt]he (?:\*\*(?<name>[^*\n]+)\*\*|(?<name>\p{Lu}[\p{L}\d]*(?: (?:&|[\p{L}\d]+)){0,3})) page\b")]
    private static partial Regex EnglishPageName();

    [GeneratedRegex(@"\b(?:[Dd]ie|[Dd]er|[Dd]ieser|[Ee]ine|[Ee]iner) Seite (?:\*\*(?<bold>[^*\n]+)\*\*|(?<rest>\p{Lu}[^\n]*))")]
    private static partial Regex GermanPageName();

    [GeneratedRegex(@"^[\p{L}\d-]+")]
    private static partial Regex GermanWord();

    /// <summary>
    /// German text says "du": no formal "Sie", "Ihnen" or "Ihr", "Ihre", "Ihrem", "Ihren", "Ihrer", "Ihres", in the app's
    /// strings, the German explanation pages and the German catalog text. "Ihnen" and the "Ihr" forms count everywhere:
    /// at the start of a sentence "Ihre Sicherungen" reads as "your backups" even when "their" was meant (U47 of the
    /// 2026-10-31 audit), so such a sentence names the noun instead. "Sie" counts inside a sentence ("solange Sie",
    /// "Klicken Sie"); at the start of one it is the usual "it" or "they" ("Sie braucht Windows 11 24H2"), and on
    /// 2026-10-31 all 59 of them meant that. Text in German quotes („...“) is a quoted Windows or Microsoft name and is
    /// left alone (the policy „Verbessern Sie die Freihand- und Tipperkennung“).
    /// </summary>
    [Fact]
    public void GermanTextHasNoFormalAddress()
    {
        var problems = new List<string>();
        void Check(string name, string text) =>
            problems.AddRange(FormalAddress(text).Select(hit => $"{name}: \"{hit}\""));

        foreach (var data in XDocument.Load(Path.Combine(RepoPaths.App, "Resources", "Strings.de.resx")).Descendants("data"))
            Check($"Strings.de.resx {(string)data.Attribute("name")!}", (string?)data.Element("value") ?? "");
        foreach (var name in Catalog.CatalogData.ResourceNames("Catalog.Docs.de."))
            Check(name, Catalog.CatalogData.ReadResourceText(name));
        foreach (var name in Catalog.CatalogData.ResourceNames("Catalog.").Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
            foreach (var value in GermanJsonValues(System.Text.Json.JsonDocument.Parse(Catalog.CatalogData.ReadResourceText(name),
                         new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement))
                Check(name, value);

        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("Das gilt, solange Sie BitLocker nicht selbst einschalten.", true)]
    [InlineData("Klicken Sie auf Weiter.", true)]
    [InlineData("Ihre Sicherungen werden mit der App gelöscht.", true)]
    [InlineData("Bleibt erhalten. Ihr Inhalt darf weg.", true)]
    [InlineData("Die App speichert Ihre Daten.", true)]
    [InlineData("Die Richtlinie wirkt sofort. Sie braucht Windows 11 24H2.", false)]
    [InlineData("1. Sie startet bei der Anmeldung.", false)]
    [InlineData("Die Behebung: Sie stellt den Wert zurück.", false)]
    [InlineData("Die Richtlinie „Verbessern Sie die Freihand- und Tipperkennung“ wird abgeschaltet.", false)]
    [InlineData("Programme, die sie brauchen, und ihre Daten.", false)]
    public void FormalAddressPatternFindsFormalForms(string text, bool formal) =>
        Assert.Equal(formal, FormalAddress(text).Any());

    /// <summary>The string values of every "de" property, at any depth.</summary>
    private static IEnumerable<string> GermanJsonValues(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    if (p.Name == "de" && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) yield return p.Value.GetString()!;
                    else foreach (var v in GermanJsonValues(p.Value)) yield return v;
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var v in GermanJsonValues(item)) yield return v;
                break;
        }
    }

    /// <summary>Formal forms, with a few words of context; see <see cref="GermanTextHasNoFormalAddress"/>.</summary>
    private static IEnumerable<string> FormalAddress(string text)
    {
        foreach (var line in text.Split('\n'))
            foreach (Match m in FormalWord().Matches(line))
            {
                var before = line[..m.Index];
                if (before.LastIndexOf('„') > before.LastIndexOf('“')) continue;
                // "Sie" at a sentence start: the line's start, after a list marker, heading or bold mark, or after
                // . ! ? : or an opening bracket or quote.
                var lead = SentenceLead().Replace(before, "");
                if (m.Value == "Sie" && (lead.Length == 0 || ".!?:([‚„\"".Contains(lead[^1]))) continue;
                var start = Math.Max(0, m.Index - 30);
                yield return line[start..Math.Min(line.Length, m.Index + m.Length + 30)].Trim();
            }
    }

    [GeneratedRegex(@"(?<![\p{L}\d])(?:Sie|Ihnen|Ihr|Ihre|Ihrem|Ihren|Ihrer|Ihres)(?![\p{L}\d])")]
    private static partial Regex FormalWord();

    /// <summary>Trailing whitespace and markup before a word: spaces, bold or italic marks, list numbers, dashes and heading marks at the line start.</summary>
    [GeneratedRegex(@"(?:^\s*(?:#+|[-*]|\d+\.)?)?[\s*_]*$")]
    private static partial Regex SentenceLead();

    /// <summary>"value(s)", "Wert(e)", "Änderung(en)" and similar.</summary>
    [GeneratedRegex(@"\p{L}\((?:s|e|en|n|es)\)")]
    private static partial Regex ParenPlural();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();
}
