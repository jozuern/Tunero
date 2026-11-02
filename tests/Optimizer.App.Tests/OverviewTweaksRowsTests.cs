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
    // The service name of a service without a description: the title without its spaces.
    [InlineData("GameInputRedistService", null)]
    [InlineData("Game-Input_Redist Service", null)]
    [InlineData("", null)]
    [InlineData("GameInputSvc", "GameInputSvc")]
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

    /// <summary>A setting that was on before this app: the details pane says "Already on" as the row does, not "On".</summary>
    [Fact]
    public void ASettingMadeOutsideTheAppIsAlreadyOnInThePaneToo()
    {
        var outside = new TweakItemViewModel(new TweakStatus(Tweak, TweakState.Applied, 0, [], null, false, [], false), Loc.Instance.Language, null!, null);
        Assert.True(outside.IsSetOutside);
        Assert.Equal(Loc.Instance["Tweak_AlreadyOnShort"], outside.StatusText);
        var byApp = new TweakItemViewModel(new TweakStatus(Tweak, TweakState.Applied, 0, [], null, false, [], true), Loc.Instance.Language, null!, null);
        Assert.NotEqual(Loc.Instance["Tweak_AlreadyOnShort"], byApp.StatusText);
    }

    /// <summary>
    /// The hint over the works-against lines explains only the buttons they show, and says "in effect": the only line can
    /// name a protection that is off ("Memory integrity off" under "These settings are on" read as a contradiction).
    /// </summary>
    [Fact]
    public void TheWorksAgainstHintNamesOnlyTheButtonsShown()
    {
        var undo = Loc.Instance["Rec_AgainstHintUndo"];
        var details = Loc.Instance["Rec_AgainstHintDetails"];
        var outside = MainViewModel.AgainstHintText([new AgainstLine("security.vbsOff", "VBS off", "", CanUndo: false)]);
        Assert.StartsWith(Loc.Instance["Rec_AgainstHint"], outside);
        Assert.Contains(details, outside);
        Assert.DoesNotContain(undo, outside);
        var byApp = MainViewModel.AgainstHintText([new AgainstLine("a", "A", "", CanUndo: true)]);
        Assert.Contains(undo, byApp);
        Assert.DoesNotContain(details, byApp);
        var both = MainViewModel.AgainstHintText([new AgainstLine("a", "A", "", true), new AgainstLine("b", "B", "", false)]);
        Assert.Contains(undo, both);
        Assert.Contains(details, both);
    }

    /// <summary>
    /// Each view of the Tweaks page reads back as itself, and only Everything lists what does not apply: "Show passed
    /// checks" on the Overview and Manual checks is no part of it (it once added those tweaks under "For this profile").
    /// </summary>
    [Fact]
    public void EachTweakViewStandsForItsOwnFilters()
    {
        foreach (var view in Enum.GetValues<TweakView>())
        {
            var flags = TweakViewFlags.For(view);
            Assert.Equal(view, flags.View);
            Assert.Equal(view == TweakView.Everything, flags.ShowAll);
        }
        Assert.Equal(TweakView.Applicable, default(TweakViewFlags).View);
    }

    private static ProfiledTweak Profiled(TweakDefinition tweak, bool hasBackup, bool recommended = false) =>
        new(new TweakStatus(tweak, hasBackup ? TweakState.Applied : TweakState.NotApplied, 2, [], null, recommended, [], hasBackup), 2, true, recommended, null);

    /// <summary>
    /// The Tweaks page's link to Expert mode counts what Expert mode would add to the list: Expert tweaks this app did
    /// not change (one it changed stays listed so it can be undone), only those the page's filters keep, none when on.
    /// </summary>
    [Fact]
    public void TheExpertLinkCountsOnlyWhatExpertModeWouldAdd()
    {
        var expertTweak = TweakCatalog.Current.Tweaks.First(t => !t.Hidden && t.EffectiveRisk == Risk.Expert);
        var safeTweak = TweakCatalog.Current.Tweaks.First(t => !t.Hidden && t.EffectiveRisk != Risk.Expert);
        var hidden = Profiled(expertTweak, hasBackup: false);
        var changedByApp = Profiled(expertTweak, hasBackup: true);
        var hiddenRecommended = Profiled(expertTweak, hasBackup: false, recommended: true);
        var safe = Profiled(safeTweak, hasBackup: false);

        Assert.True(MainViewModel.HiddenAsExpert(hidden, expertMode: false));
        Assert.False(MainViewModel.HiddenAsExpert(changedByApp, expertMode: false));
        Assert.False(MainViewModel.HiddenAsExpert(safe, expertMode: false));
        Assert.False(MainViewModel.HiddenAsExpert(hidden, expertMode: true));

        ProfiledTweak[] inView = [hidden, changedByApp, hiddenRecommended, safe];
        Assert.Equal(2, MainViewModel.HiddenExpertCount(inView, expertMode: false, _ => true));
        Assert.Equal(0, MainViewModel.HiddenExpertCount(inView, expertMode: true, _ => true));
        // A tweak the page's filters leave out (here: "Recommended for this PC") would not show with Expert mode either.
        Assert.Equal(1, MainViewModel.HiddenExpertCount(inView, expertMode: false, p => p.Recommended));
        Assert.Equal(0, MainViewModel.HiddenExpertCount(inView, expertMode: false, _ => false));
    }

    /// <summary>The line under the score names settings against the profile and unanswered checks, each in both languages.</summary>
    [Theory]
    [InlineData(false, false, "Dash_Readiness")]
    [InlineData(false, true, "Dash_ReadinessUnknown")]
    [InlineData(true, false, "Dash_ReadinessAgainst")]
    [InlineData(true, true, "Dash_ReadinessAgainstUnknown")]
    public void TheReadinessLineNamesWhatItLeavesOut(bool against, bool unknown, string expected)
    {
        var key = MainViewModel.ReadinessKey(against, unknown);
        Assert.Equal(expected, key);
        var strings = new System.Resources.ResourceManager("Optimizer.App.Resources.Strings", typeof(Loc).Assembly);
        foreach (var culture in new[] { System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CultureInfo.GetCultureInfo("de") })
        {
            // tryParents: false, so a key missing in German does not pass with the English text.
            var value = strings.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} ({culture.Name})");
            Assert.Contains("{2}", value);
            Assert.Equal(unknown, value.Contains("{3}"));
        }
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
