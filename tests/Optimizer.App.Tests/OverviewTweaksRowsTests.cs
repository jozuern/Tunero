using System.IO;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Profiles;
using Optimizer.Core.Search;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// What the Overview, the Tweaks page and the search show for one item: the search's second line, a tweak that works
/// against the profile, and "Show in Explorer" for a path that is gone. Nothing reads or changes the system.
/// </summary>
public sealed class OverviewTweaksRowsTests
{
    private static readonly TweakDefinition Tweak = TweakCatalog.Current.Tweaks.First(t => !t.Hidden);

    [Theory]
    [InlineData("GameInput Redist Service", null)]
    [InlineData("  gameinput redist service ", null)]
    [InlineData("", null)]
    [InlineData("Handles game controllers.", "Handles game controllers.")]
    public void ASnippetThatOnlyRepeatsTheTitleIsLeftOut(string snippet, string? expected)
    {
        var item = new SearchResultItem(new SearchResult(SearchKind.Service, "Services", "GameInputSvc", "GameInput Redist Service", snippet, 1, null), "Services");
        Assert.Equal(expected, item.Snippet);
    }

    [Fact]
    public void TheLiveSnippetComesFirst()
    {
        var item = new SearchResultItem(new SearchResult(SearchKind.Finding, "Overview", "x", "Title", "From the index", 1, null), "Overview", "For this PC");
        Assert.Equal("For this PC", item.Snippet);
    }

    [Theory]
    [InlineData(TweakState.Applied, true)]
    [InlineData(TweakState.NotApplied, false)]
    public void ATweakAgainstTheProfileSaysSoUnderTheTitleOnOrOff(TweakState state, bool on)
    {
        var status = new TweakStatus(Tweak, state, 0, [], null, false, [], false);
        var profiled = new ProfiledTweak(status, -2, true, false, null, Flagged: on);
        var row = new TweakItemViewModel(status, Loc.Instance.Language, null!, null, profiled: profiled);
        // The warning under the title carries it, not the faint detail line.
        Assert.Equal(Loc.Instance["Impact_Against"], row.AgainstNote);
        Assert.DoesNotContain(Loc.Instance["Impact_Against"], row.MetaText);
        Assert.Null(row.Rating);
    }

    [Fact]
    public void AMissingPathIsNamedInsteadOfStartingExplorer()
    {
        var host = new FakePageHost();
        var gone = Path.Combine(Path.GetTempPath(), "tunero-gone-" + Guid.NewGuid().ToString("N"), "file.txt");
        host.ShowInExplorer(gone, null);
        Assert.Equal([(Loc.Instance.Format("Explorer_Missing", gone), InfoBarSeverity.Warning)], host.Results);

        // A page's own words win.
        host.Results.Clear();
        host.ShowInExplorer(gone, null, missingText: "gone");
        Assert.Equal("gone", Assert.Single(host.Results).Text);

        // A relative path is never handed to Explorer either (it would read it against its own folder).
        host.Results.Clear();
        host.ShowInExplorer("file.txt", null);
        Assert.Equal(InfoBarSeverity.Warning, Assert.Single(host.Results).Severity);
    }
}
