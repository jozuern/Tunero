using System.IO;
using System.Text.RegularExpressions;
using Optimizer.App.Services;

namespace Optimizer.App.Tests;

/// <summary>
/// The one table of the command line (<see cref="CommandLineVerbs"/>): parsing and dispatch read it, and the help in
/// README and CONTRIBUTING names exactly its switches.
/// </summary>
public sealed partial class CommandLineVerbsTests
{
    [Theory]
    [InlineData(new[] { "--apply", "changes.json" }, "--apply")]
    [InlineData(new[] { "--apply-recommended", "--profile", "gaming" }, "--apply-recommended")]
    [InlineData(new[] { "--export", "out.json", "--include", "apps" }, "--export")]
    [InlineData(new[] { "--reapply" }, "--reapply")]
    [InlineData(new[] { "--undo-all", "--log", "x.txt" }, "--undo-all")]
    [InlineData(new[] { "--build-media", "request.json" }, "--build-media")]
    [InlineData(new[] { "--first-sign-in" }, "--first-sign-in")]
    // The first sign-in comes first, as it did before any other run.
    [InlineData(new[] { "--undo-all", "--first-sign-in" }, "--first-sign-in")]
    [InlineData(new[] { "--APPLY", "changes.json" }, "--apply")]
    public void TheStartRunsTheVerbItNames(string[] args, string verb)
    {
        var cli = new App.CliArgs(args);
        Assert.Equal(verb, CommandLineVerbs.Find(cli)?.Name);
        Assert.True(cli.IsCommandLineRun);
    }

    /// <summary>A verb that needs a file opens the window without one, as before; switches alone are no run.</summary>
    [Theory]
    [InlineData]
    [InlineData("--apply")]
    [InlineData("--export")]
    [InlineData("--build-media")]
    [InlineData("--profile", "gaming")]
    [InlineData("--uninstall")]
    [InlineData("--first-sign-in-apply", "gaming")]
    public void WithoutAVerbTheWindowOpens(params string[] args)
    {
        Assert.Null(CommandLineVerbs.Find(new App.CliArgs(args)));
        Assert.False(new App.CliArgs(args).IsCommandLineRun);
    }

    [Fact]
    public void EachSwitchIsListedOnce()
    {
        var names = CommandLineVerbs.Verbs.Select(v => v.Name).Concat(CommandLineVerbs.Switches.Select(s => s.Name)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>README's Command line table explains exactly the verbs and switches the table marks for it.</summary>
    [Fact]
    public void ReadmeExplainsEverySwitchOfTheTable()
    {
        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        var section = readme[readme.IndexOf("## Command line", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", 3, StringComparison.Ordinal)];
        var documented = SwitchRow().Matches(section).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = CommandLineVerbs.Verbs.Where(v => v.Help == CliHelp.Readme).Select(v => v.Name)
            .Concat(CommandLineVerbs.Switches.Where(s => s.Help == CliHelp.Readme).Select(s => s.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ContributingExplainsTheInstallMediaVerbs()
    {
        var contributing = File.ReadAllText(Path.Combine(Root, "CONTRIBUTING.md"));
        var documented = SwitchRow().Matches(contributing).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var verb in CommandLineVerbs.Verbs.Where(v => v.Help == CliHelp.Contributing))
            Assert.Contains(verb.Name, documented);
    }

    /// <summary>
    /// The developer switches are read where they are used (<c>args.Has("--x")</c>, <c>args.Value("--x")</c>, mostly in
    /// App.Developer.cs), not from the table above, so their help is kept by hand: every switch the app reads is in
    /// CONTRIBUTING's Developer switches tables or README's Command line table, and every switch those tables name is
    /// still read (by such a call, as a verb or switch of <see cref="CommandLineVerbs"/>, or as a "--x" constant).
    /// </summary>
    [Fact]
    public void EveryDeveloperSwitchIsDocumentedAndEveryDocumentedOneIsRead()
    {
        var contributing = File.ReadAllText(Path.Combine(Root, "CONTRIBUTING.md"));
        var start = contributing.IndexOf("Developer switches (", StringComparison.Ordinal);
        Assert.True(start >= 0, "CONTRIBUTING.md has no Developer switches section");
        var developer = contributing[start..contributing.IndexOf("\n## ", start, StringComparison.Ordinal)];
        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        var commandLine = readme[readme.IndexOf("## Command line", StringComparison.Ordinal)..];
        commandLine = commandLine[..commandLine.IndexOf("\n## ", 3, StringComparison.Ordinal)];
        var documented = TableSwitches(developer).Concat(TableSwitches(commandLine)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var constants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (file.StartsWith(Path.Combine(Root, "src", "Optimizer.App") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                read.UnionWith(SwitchRead().Matches(text).Select(m => m.Groups[1].Value));
            constants.UnionWith(SwitchConstant().Matches(text).Select(m => m.Groups[1].Value));
        }
        Assert.Contains("--screenshot", read);

        Assert.Empty(read.Where(s => !documented.Contains(s)).Order(StringComparer.Ordinal));
        var known = read.Concat(constants).Concat(CommandLineVerbs.Verbs.Select(v => v.Name)).Concat(CommandLineVerbs.Switches.Select(s => s.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(documented.Where(s => !known.Contains(s)).Order(StringComparer.Ordinal));
    }

    /// <summary>Every "--name" inside `code` on the table lines of a section (several per cell: "`--confirm <id> --confirm-shot <file>`").</summary>
    private static IEnumerable<string> TableSwitches(string section) =>
        section.Split('\n').Where(l => l.StartsWith('|'))
            .SelectMany(l => CodeSpan().Matches(l))
            .SelectMany(span => SwitchName().Matches(span.Groups[1].Value).Select(m => m.Value));

    [GeneratedRegex(@"\.(?:Has|Value)\(""(--[a-z0-9-]+)""\)")]
    private static partial Regex SwitchRead();

    [GeneratedRegex(@"const string \w+ = ""(--[a-z0-9-]+)""")]
    private static partial Regex SwitchConstant();

    [GeneratedRegex("`([^`]*)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z0-9-]*")]
    private static partial Regex SwitchName();

    /// <summary>A table row that explains a switch: "| `--name" at the start of the line.</summary>
    [GeneratedRegex(@"^\| `(--[a-z-]+)", RegexOptions.Multiline)]
    private static partial Regex SwitchRow();

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) && Directory.Exists(Path.Combine(dir.FullName, "src", "Optimizer.App"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
