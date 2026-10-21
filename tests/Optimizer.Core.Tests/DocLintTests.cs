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
    /// The text of tunero.dev: its English and German copy and its pages and components, of the current site
    /// (website/src) and of the next one (website-v2/src).
    /// </summary>
    private static IEnumerable<string> WebsiteCopy() =>
        RepositoryFiles("*.ts").Concat(RepositoryFiles("*.astro")).Concat(RepositoryFiles("*.svelte"))
            .Where(f => Path.GetRelativePath(RepoPaths.Root, f).Replace('\\', '/') is var p
                        && (p.StartsWith("website/src/", StringComparison.Ordinal) || p.StartsWith("website-v2/src/", StringComparison.Ordinal)));

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

    /// <summary>"value(s)", "Wert(e)", "Änderung(en)" and similar.</summary>
    [GeneratedRegex(@"\p{L}\((?:s|e|en|n|es)\)")]
    private static partial Regex ParenPlural();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();
}
