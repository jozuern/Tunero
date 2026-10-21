using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// Work that can take minutes says so the same way everywhere: a tag and a clock line for a Windows feature switch, the
/// banner while it runs, and nothing of it for a change that is done in seconds. Nothing reads or changes the system.
/// </summary>
public sealed class DurationHintTests
{
    private static readonly TweakDefinition Feature = OptionalFeatureAction.Tweak(CatalogData.Current.Features.Features.First(f => !f.Capability), true);
    private static readonly TweakDefinition Fast = TweakCatalog.Current.Tweaks.First(t => !t.Hidden && !t.TakesMinutes);

    private static TweakStatus Status(TweakDefinition t) => new(t, TweakState.NotApplied, 0, [], null, false, [], false);

    [Fact]
    public void AFeatureSwitchGetsTheTagAndTheClockInItsConfirmation()
    {
        var tag = Labels.Current.Get(Loc.Instance.Language, "badge.takesMinutes");
        var request = ChangeRunner.ConfirmRequestFor(Feature, Status(Feature), false, "Feature", "", []);
        // DISM waits while Windows installs updates: the note says so.
        Assert.Equal(Durations.Servicing, request.Duration);
        Assert.Contains(tag, request.Badges);

        var row = new TweakItemViewModel(Status(Feature), Loc.Instance.Language, null!, null);
        var badge = Assert.Single(row.Badges, b => b.Text == tag);
        Assert.Equal(Loc.Instance["Badge_TipMinutes"], badge.Tip);
    }

    [Fact]
    public void AFastChangeSaysNothingAboutMinutes()
    {
        var tag = Labels.Current.Get(Loc.Instance.Language, "badge.takesMinutes");
        var request = ChangeRunner.ConfirmRequestFor(Fast, Status(Fast), true, "Fast", "", []);
        Assert.Null(request.Duration);
        Assert.DoesNotContain(tag, request.Badges);
        Assert.DoesNotContain(new TweakItemViewModel(Status(Fast), Loc.Instance.Language, null!, null).Badges, b => b.Text == tag);
    }

    [Fact]
    public void TheBannerSaysItOnlyForSlowWork()
    {
        var work = new WorkProgress();
        using (work.Begin("Cleaning up"))
            Assert.Null(work.DurationText);
        using (work.Begin("Updating apps", slow: true))
            Assert.Equal(Durations.Minutes, work.DurationText);
        // A wording that fits better replaces "several minutes": DISM's wait for updates, the media's measured half hour.
        using (work.Begin("Checking system files (SFC)", duration: Durations.Servicing))
            Assert.Equal(Durations.Servicing, work.DurationText);
        using (work.Begin("Building the Windows ISO", duration: Durations.HalfHour))
            Assert.Equal(Durations.HalfHour, work.DurationText);
        Assert.Null(work.DurationText);
    }

    /// <summary>A batch says it while the step that runs is a slow one, and not for the fast steps around it.</summary>
    [Fact]
    public void ABatchSaysItPerStep()
    {
        var work = new WorkProgress();
        using var scope = work.Begin("Undoing all changes");
        scope.Duration = Durations.Servicing;
        scope.Step(0, 2, "Hyper-V");
        Assert.Equal(Durations.Servicing, work.DurationText);
        scope.Duration = null;
        scope.Step(1, 2, "Game Mode");
        Assert.Null(work.DurationText);
    }

    /// <summary>The free key allows 4 lookups a minute (one every 15.5 s): 9 files take two minutes, 8 do not.</summary>
    [Fact]
    public void VirusTotalSaysItFromTwoMinutesOn()
    {
        var spacing = Optimizer.Core.Startup.VirusTotalClient.PublicApiSpacing;
        Assert.False(StartupViewModel.VirusTotalTakesMinutesFor(0, spacing));
        Assert.False(StartupViewModel.VirusTotalTakesMinutesFor(8, spacing));
        Assert.True(StartupViewModel.VirusTotalTakesMinutesFor(9, spacing));
    }

    [Fact]
    public void TheQuestionEndsWithTheSentence()
    {
        Assert.Equal($"Removes old files. {Loc.Instance["Hint_TakesMinutesSentence"]}", Durations.WithSentence("Removes old files."));
        Assert.Equal($"Removes old files. {Loc.Instance["Hint_TakesMinutesServicingSentence"]}", Durations.WithServicingSentence("Removes old files. "));
    }

    /// <summary>The one catalog tweak that runs DISM shows the tag in its row and the note in its confirmation.</summary>
    [Fact]
    public void ReservedStorageGetsTheTag()
    {
        var t = TweakCatalog.Current.Get("storage.reservedStorageOff")!;
        Assert.Equal(Durations.Servicing, ChangeRunner.ConfirmRequestFor(t, Status(t), true, "Reserved storage", "", []).Duration);
        Assert.Contains(new TweakItemViewModel(Status(t), Loc.Instance.Language, null!, null).Badges,
            b => b.Text == Labels.Current.Get(Loc.Instance.Language, "badge.takesMinutes"));
    }
}
