using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;

namespace Optimizer.App.Tests;

/// <summary>The summary bar and the review before the build: pure text from the choices, nothing is read from this PC.</summary>
public sealed class InstallMediaSummaryTests
{
    private static MediaChoices Choices(int apps = 0, string? account = null, bool allowUnsupported = false, string? profile = null, int unreviewed = 0, int install = 0, bool smaller = false)
    {
        // Real app names of the reviewed list, so the review can sort them into their groups.
        var names = CatalogData.Current.Appx.Apps.Select(a => a.Name).Take(apps).ToList();
        var esd = new EsdFile("x.esd", "de-de", "German (Germany)", "Professional", "x64", 6L << 30, new string('a', 64), new Uri("http://dl.delivery.mp.microsoft.com/x.esd"));
        return new MediaChoices(new MediaSource(esd, null, null), "Professional", null, null, "en", names, account is not null, account, "de-AT", null, allowUnsupported,
            profile is null ? null : new TuneroCopy("Tunero.exe", profile, "System"), @"C:\Users\Anna\Downloads\Windows11_Tunero.iso")
        {
            RemoveUnreviewed = Enumerable.Range(0, unreviewed).Select(i => $"Vendor.App{i}").ToList(),
            InstallApps = Enumerable.Range(0, install).Select(i => $"Vendor.Install{i}").ToList(),
            Compression = smaller ? MediaCompression.Recovery : MediaCompression.Wim,
        };
    }

    [Fact]
    public void BarNamesEditionAppsAccountAndSpace()
    {
        var text = InstallMediaSummary.Bar(Choices(apps: 18, account: "joshu", profile: "gaming"), "Windows 11 Pro", "26300", 21_400_000_000);
        Assert.Contains("Windows 11 Pro 26300", text);
        Assert.Contains(Loc.Instance.Format("Media_Summary_Apps", 18), text);
        Assert.Contains("joshu", text);
        Assert.Contains(Loc.Instance["Profile_gaming"], text);
        Assert.EndsWith(Loc.Instance.Format("Media_Summary_Needed", ByteSize.Format(21_400_000_000)), text);
    }

    [Fact]
    public void BarLeavesOutWhatIsOff()
    {
        var text = InstallMediaSummary.Bar(Choices(apps: 0), "Windows 11 Pro", null, 1);
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Apps", 0), text);
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Account", ""), text);
        Assert.StartsWith("Windows 11 Pro,", text);
    }

    [Fact]
    public void PartsAppearInTheBarAndTheReviewOnlyWhenChosen()
    {
        Assert.DoesNotContain(Loc.Instance.Format("Media_PartsSummary", 0), InstallMediaSummary.Bar(Choices(), "Windows 11 Pro", null, 1));
        Assert.DoesNotContain(InstallMediaSummary.Review(Choices(), "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_PartsTitle"]);

        var on = Choices() with { RemoveParts = ["wordpad", "recall"] };
        Assert.Contains(Loc.Instance.Format("Media_PartsSummary", 2), InstallMediaSummary.Bar(on, "Windows 11 Pro", null, 1));
        var review = InstallMediaSummary.Review(on, "Windows 11 Pro", null, 1);
        var line = Assert.Single(review.Changes, l => l.Target == Loc.Instance["Media_PartsTitle"]);
        Assert.Contains("WordPad", line.After);
        Assert.Contains("Recall", line.After);
        Assert.Empty(review.Warnings);
    }

    [Fact]
    public void ReviewNamesSpeechRecognitionWhenTextToSpeechTakesItAlong()
    {
        var only = InstallMediaSummary.Review(Choices() with { RemoveParts = ["textToSpeech"] }, "Windows 11 Pro", null, 1);
        var line = Assert.Single(only.Changes, l => l.Target == Loc.Instance["Media_PartsTitle"]).After;
        Assert.Contains(CatalogData.Current.MediaParts.Single(p => p.Id == "textToSpeech").Label("en"), line);
        Assert.Contains(CatalogData.Current.MediaParts.Single(p => p.Id == "speech").Label("en"), line);
        // Ticked as well: named once.
        var both = InstallMediaSummary.Review(Choices() with { RemoveParts = ["textToSpeech", "speech"] }, "Windows 11 Pro", null, 1);
        var text = Assert.Single(both.Changes, l => l.Target == Loc.Instance["Media_PartsTitle"]).After;
        Assert.Equal(2, text.Split(", ").Length);
    }

    [Fact]
    public void BarSaysSmallerIsoOnlyWhenChosen()
    {
        Assert.Contains(Loc.Instance["Media_CompressionSmaller"], InstallMediaSummary.Bar(Choices(smaller: true), "Windows 11 Pro", null, 1));
        Assert.DoesNotContain(Loc.Instance["Media_CompressionSmaller"], InstallMediaSummary.Bar(Choices(), "Windows 11 Pro", null, 1));
    }

    [Fact]
    public void ReviewHasALineForSmallerIsoAndNoneForTheDefault()
    {
        var smaller = InstallMediaSummary.Review(Choices(smaller: true), "Windows 11 Pro", null, 1);
        Assert.Contains(smaller.Changes, l => l.Target == Loc.Instance["Media_CompressionTitle"] && l.After == Loc.Instance["Media_CompressionSmaller"]);
        Assert.Empty(smaller.Warnings);
        Assert.DoesNotContain(InstallMediaSummary.Review(Choices(), "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_CompressionTitle"]);
    }

    [Fact]
    public void TheImageSettingsAppearInTheBarTheReviewAndTheWarningsOnlyWhenOn()
    {
        var off = Choices();
        Assert.DoesNotContain(Loc.Instance["Media_EncryptionTitle"], InstallMediaSummary.Bar(off, "Windows 11 Pro", null, 1));
        Assert.DoesNotContain(InstallMediaSummary.Review(off, "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_ReservedTitle"]);

        var on = off with { Settings = new(true, true, false) };
        var bar = InstallMediaSummary.Bar(on, "Windows 11 Pro", null, 1);
        Assert.Contains(Loc.Instance["Media_EncryptionTitle"], bar);
        Assert.Contains(Loc.Instance["Media_ReservedTitle"], bar);
        var review = InstallMediaSummary.Review(on, "Windows 11 Pro", null, 1);
        Assert.Contains(review.Changes, l => l.Target == Loc.Instance["Media_EncryptionTitle"] && l.After == Loc.Instance["Media_Review_On"]);
        Assert.Contains(review.Changes, l => l.Target == Loc.Instance["Media_ReservedTitle"] && l.After == Loc.Instance["Media_Review_On"]);
        Assert.Contains(review.Warnings, w => w.Contains(Loc.Instance["Media_EncryptionText"], StringComparison.Ordinal));
        Assert.Contains(review.Warnings, w => w.Contains(Loc.Instance["Media_ReservedText"], StringComparison.Ordinal));
    }

    [Fact]
    public void LaterUpgradesAppearInTheBarTheReviewAndTheWarnings()
    {
        var off = Choices(allowUnsupported: true);
        Assert.DoesNotContain(Loc.Instance["Media_UpgradesTitle"], InstallMediaSummary.Bar(off, "Windows 11 Pro", null, 1));
        Assert.DoesNotContain(InstallMediaSummary.Review(off, "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_UpgradesTitle"]);
        var on = off with { Settings = new(false, false, true) };
        Assert.Contains(Loc.Instance["Media_UpgradesTitle"], InstallMediaSummary.Bar(on, "Windows 11 Pro", null, 1));
        var review = InstallMediaSummary.Review(on, "Windows 11 Pro", null, 1);
        Assert.Contains(review.Changes, l => l.Target == Loc.Instance["Media_UpgradesTitle"] && l.After == Loc.Instance["Media_Review_On"]);
        Assert.Contains(review.Warnings, w => w.Contains(Loc.Instance["Media_UpgradesText"], StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewListsEveryActiveChoiceAndWarnsForExpertOptions()
    {
        var r = InstallMediaSummary.Review(Choices(apps: 18, account: "joshu", allowUnsupported: true, profile: "office", unreviewed: 2, install: 3), "Windows 11 Pro", "26300", 1);
        Assert.Equal(Loc.Instance["Media_BuildButton"], r.Primary);
        Assert.Equal(Loc.Instance["Media_Review_Title"], r.Title);
        Assert.Contains(@"C:\Users\Anna\Downloads\Windows11_Tunero.iso", r.Summary);
        // The groups of the apps add up to the 18.
        var groups = r.Changes.Where(l => l.Target.StartsWith(Loc.Instance.Format("Media_Review_AppsGroup", "").TrimEnd(), StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(groups);
        Assert.Equal(18, groups.Sum(l => int.Parse(l.After)));
        Assert.Contains(r.Changes, l => l.Target == Loc.Instance["Media_SkipSetupTitle"] && l.After.Contains("joshu"));
        Assert.Contains(r.Changes, l => l.Target == Loc.Instance["Media_ProfileTitle"] && l.After == Loc.Instance["Profile_office"]);
        Assert.Contains(r.Changes, l => l.Target == Loc.Instance["Media_InstallTitle"] && l.After.Contains('3'));
        Assert.Contains(r.Changes, l => l.Target == Loc.Instance["Media_UnreviewedTitle"] && l.After.Contains('2'));
        Assert.Contains(r.Changes, l => l.Target == Loc.Instance["Media_UnsupportedTitle"]);
        Assert.Equal(2, r.Warnings.Count);
        Assert.Contains(r.Warnings, w => w.Contains(Loc.Instance["Media_UnsupportedTitle"]));
        Assert.Contains(r.Warnings, w => w.Contains(Loc.Instance["Media_UnreviewedTitle"]));
        // A review lists choices: no "now" value to compare with.
        Assert.All(r.Changes, l => Assert.Equal("", l.Before));
    }

    [Fact]
    public void ReviewWithoutExpertOptionsHasNoWarnings()
    {
        var r = InstallMediaSummary.Review(Choices(apps: 2), "Windows 11 Pro", null, 1);
        Assert.Empty(r.Warnings);
        Assert.DoesNotContain(r.Changes, l => l.Target == Loc.Instance["Media_UnsupportedTitle"]);
        Assert.DoesNotContain(r.Changes, l => l.Target == Loc.Instance["Media_ProfileTitle"]);
    }
}
