using System.Text.RegularExpressions;

namespace Optimizer.Core.Tests;

/// <summary>
/// The doc comments carry the reasons and the safety rules, and the next session reads them as true. An edit that puts a
/// member between a comment and its member leaves a summary on nothing (it then reads as the next member's) or two
/// summaries on one member; this catches the mechanical part. A single summary that describes the wrong member still
/// needs a human eye.
/// </summary>
public partial class DocCommentTests
{
    [Fact]
    public void EverySummaryBelongsToOneMember()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var name = Path.GetRelativePath(RepoPaths.Root, file);
            problems.AddRange(Problems(File.ReadAllLines(file)).Select(p => $"{name}:{p}"));
        }
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("/// <summary>A.</summary>\n/// <summary>B.</summary>\nvoid M() { }", "1: two <summary> blocks on one member")]
    [InlineData("/// <summary>A.</summary>\n\n[Obsolete]\n// note\n/// <summary>B.</summary>\nvoid M() { }", "1: the summary is followed by another doc comment (line 5) before a declaration")]
    [InlineData("/// <summary>A.</summary>\n/// <param name=\"x\">X.</param>\nvoid M(int x) { }", null)]
    [InlineData("/// <summary>A.</summary>\n[ObservableProperty] private bool _a;\n\n/// <summary>B.</summary>\n[ObservableProperty] private bool _b;", null)]
    public void TheRuleFindsAnOrphanAndADoubleSummary(string code, string? problem)
    {
        var found = Problems(code.Split('\n')).ToList();
        if (problem is null) Assert.Empty(found);
        else Assert.Equal(problem, Assert.Single(found));
    }

    /// <summary>
    /// A doc block is a run of /// lines. Lines that may stand between a block and its declaration: blank lines, lines with
    /// only attributes, plain // comments and preprocessor lines. Another doc block among them means the first one
    /// belongs to no member.
    /// </summary>
    private static IEnumerable<string> Problems(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count;)
        {
            if (!IsDoc(lines[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < lines.Count && IsDoc(lines[i])) i++;
            var block = string.Join("\n", lines.Skip(start).Take(i - start));
            if (Regex.Matches(block, "<summary>").Count > 1) yield return $"{start + 1}: two <summary> blocks on one member";
            if (!block.Contains("</summary>", StringComparison.Ordinal)) continue;
            var next = i;
            while (next < lines.Count && IsBetween(lines[next])) next++;
            if (next < lines.Count && IsDoc(lines[next]))
                yield return $"{start + 1}: the summary is followed by another doc comment (line {next + 1}) before a declaration";
        }
    }

    private static bool IsDoc(string line) => line.TrimStart().StartsWith("///", StringComparison.Ordinal);

    private static bool IsBetween(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || AttributesOnly().IsMatch(t) || (t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("///", StringComparison.Ordinal))
               || t.StartsWith('#');
    }

    /// <summary>"[Fact]", "[ObservableProperty] [NotifyPropertyChangedFor(nameof(X))]": attributes and nothing else.</summary>
    [GeneratedRegex(@"^(\s*\[[^\[\]]*(\[[^\[\]]*\][^\[\]]*)*\])+\s*$")]
    private static partial Regex AttributesOnly();
}
