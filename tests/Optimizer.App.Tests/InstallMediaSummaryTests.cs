using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;
using Optimizer.TestSupport;

namespace Optimizer.App.Tests;

/// <summary>The summary bar and the review before the build: pure text from the choices, nothing is read from this PC.</summary>
// With the tests that switch the app's language: one test here does too.
[Collection(nameof(ChangeGateCollection))]
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

    /// <summary>The bar's second line, empty when nothing goes into the image.</summary>
    private static string Changes(MediaChoices c) => InstallMediaSummary.BarChanges(c) ?? "";

    [Fact]
    public void BarNamesTheImageOnTheFirstLineAndTheChangesOnTheSecond()
    {
        var choices = Choices(apps: 18, account: "joshu", profile: "gaming");
        var image = InstallMediaSummary.Bar(choices, "Windows 11 Pro", "26300");
        Assert.StartsWith("Windows 11 Pro 26300, ", image);
        Assert.Contains(Loc.Instance.Format("Media_Summary_Language", new System.Globalization.CultureInfo("de-de").DisplayName), image);
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Apps", 18), image);
        var changes = Changes(choices);
        Assert.StartsWith(Loc.Instance.Format("Media_Summary_Apps", 18), changes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("joshu", changes);
        Assert.Contains(Loc.Instance["Profile_gaming"], changes);
        // The space is the caption below the bar (NeededText), not a part of it.
        Assert.DoesNotContain(ByteSize.Format(21_400_000_000), image + changes);
    }

    /// <summary>
    /// The changes line reads as one sentence in both languages: a capital only at its start and, in German, on the nouns
    /// (not "Zu entfernende Apps: 8, Zu entfernende Windows-Bestandteile: 2").
    /// </summary>
    [Theory]
    [InlineData("en", "Apps to remove: 3, Windows parts to remove: 2, account Anna, profile ", ", apps to install: 1, smaller ISO, allow unsupported PCs")]
    [InlineData("de", "Zu entfernende Apps: 3, zu entfernende Windows-Bestandteile: 2, Konto Anna, Profil ", ", zu installierende Apps: 1, kleinere ISO-Datei, nicht unterstützte PCs zulassen")]
    public void TheChangesLineReadsAsOneSentence(string language, string start, string end)
    {
        var before = Loc.Instance.Language;
        Loc.Instance.SetLanguage(language);
        try
        {
            var changes = Changes(Choices(apps: 3, account: "Anna", allowUnsupported: true, profile: "office", install: 1, smaller: true) with { RemoveParts = ["wordpad", "recall"] });
            Assert.StartsWith(start, changes, StringComparison.Ordinal);
            Assert.EndsWith(end, changes, StringComparison.Ordinal);
            // The first part alone starts with a capital too.
            Assert.StartsWith(language == "de" ? "Kleinere ISO-Datei" : "Smaller ISO", Changes(Choices(smaller: true)), StringComparison.Ordinal);
        }
        finally
        {
            Loc.Instance.SetLanguage(before);
        }
    }

    [Fact]
    public void BarLeavesOutWhatIsOff()
    {
        Assert.Null(InstallMediaSummary.BarChanges(Choices(apps: 0)));
        Assert.StartsWith("Windows 11 Pro,", InstallMediaSummary.Bar(Choices(apps: 0), "Windows 11 Pro", null));
    }

    [Fact]
    public void BarNamesUnsupportedPcsUnreviewedAppsAndAppsToInstall()
    {
        var off = Changes(Choices(apps: 1, profile: "office"));
        Assert.DoesNotContain(Loc.Instance["Media_Summary_Unsupported"], off, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Unreviewed", 2), off);
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Install", 3), off, StringComparison.OrdinalIgnoreCase);

        var on = Changes(Choices(apps: 1, allowUnsupported: true, profile: "office", unreviewed: 2, install: 3));
        Assert.Contains(Loc.Instance["Media_Summary_Unsupported"], on, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Loc.Instance.Format("Media_Summary_Unreviewed", 2), on);
        Assert.Contains(Loc.Instance.Format("Media_Summary_Install", 3), on, StringComparison.OrdinalIgnoreCase);
        // Apps to install need the Tunero copy (the profile): without it nothing installs them.
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Install", 3), Changes(Choices(apps: 1, install: 3)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartsAppearInTheBarAndTheReviewOnlyWhenChosen()
    {
        Assert.DoesNotContain(Loc.Instance.Format("Media_Summary_Parts", 0), Changes(Choices()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InstallMediaSummary.Review(Choices(), "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_PartsTitle"]);

        var on = Choices() with { RemoveParts = ["wordpad", "recall"] };
        Assert.Contains(Loc.Instance.Format("Media_Summary_Parts", 2), Changes(on), StringComparison.OrdinalIgnoreCase);
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
        // The bar counts what the review names.
        Assert.Contains(Loc.Instance.Format("Media_Summary_Parts", 2), Changes(Choices() with { RemoveParts = ["textToSpeech"] }), StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains(Loc.Instance["Media_Summary_Smaller"], Changes(Choices(smaller: true)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Loc.Instance["Media_Summary_Smaller"], Changes(Choices()), StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain(Loc.Instance["Media_Summary_Encryption"], Changes(off), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InstallMediaSummary.Review(off, "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_ReservedTitle"]);

        var on = off with { Settings = new(true, true, false) };
        var bar = Changes(on);
        Assert.Contains(Loc.Instance["Media_Summary_Encryption"], bar, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Loc.Instance["Media_Summary_Reserved"], bar, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain(Loc.Instance["Media_Summary_Upgrades"], Changes(off), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InstallMediaSummary.Review(off, "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_UpgradesTitle"]);
        var on = off with { Settings = new(false, false, true) };
        Assert.Contains(Loc.Instance["Media_Summary_Upgrades"], Changes(on), StringComparison.OrdinalIgnoreCase);
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

    // ---- completeness: a choice the bar or the review forgets goes into the ISO unseen ----

    /// <summary>Choices (paths into <see cref="MediaChoices"/>) the bar does not show, with the reason. Every other one changes it.</summary>
    private static readonly Dictionary<string, string> NotInTheBar = new()
    {
        ["Source.IsoPath"] = "the bar names the image, not its file; the review names the file",
        ["Source.Cab"] = "the catalog the page checked, not a choice",
        ["Edition"] = "the page passes the edition's title and build (editionTitle, build)",
        ["IsoIndex"] = "the page passes the image's title and build (editionTitle, build)",
        ["AppLanguage"] = "the app's language, not a choice of the page",
        ["Region"] = "the bar names the account only; the review names the region",
        ["Keyboard"] = "the bar keeps to the account; the review names the keyboard",
        ["Tunero.Exe"] = "the running Tunero, not a choice",
        ["Tunero.Theme"] = "the app's theme, which the copy starts with; not a choice of the page",
        ["OutputIso"] = "the output file has its own card, and the review names it",
    };

    /// <summary>Choices the review before the build does not show, with the reason. Every other one changes it.</summary>
    private static readonly Dictionary<string, string> NotInTheReview = new()
    {
        ["Source.Cab"] = "the catalog the page checked, not a choice",
        ["Edition"] = "the page passes the edition's title and build (editionTitle, build)",
        ["IsoIndex"] = "the page passes the image's title and build (editionTitle, build)",
        ["AppLanguage"] = "the app's language, not a choice of the page",
        ["Tunero.Exe"] = "the running Tunero, not a choice",
        ["Tunero.Theme"] = "the app's theme, which the copy starts with; not a choice of the page",
    };

    /// <summary>A download (or an own ISO) with Skip setup, a profile, apps to remove and install and unreviewed apps; the rest off.</summary>
    private static MediaChoices Full(bool ownIso)
    {
        var c = Choices(apps: 2, account: "Anna", profile: "office", unreviewed: 1, install: 1) with { Keyboard = "0C07:00000407" };
        return ownIso ? c with { Source = new MediaSource(null, @"C:\iso\Win11.iso", null), IsoIndex = 2, IsoLanguage = "fr-FR" } : c;
    }

    /// <summary>Real values where a generic change (RecordLeaves) would not read like a choice.</summary>
    private static readonly Dictionary<string, Func<object?, object?>> SummaryValues = new()
    {
        ["Source.Download"] = _ => new EsdFile("y.esd", "en-us", "English (United States)", "Professional", "x64", 6L << 30, new string('b', 64),
            new Uri("http://dl.delivery.mp.microsoft.com/y.esd")),
        ["IsoLanguage"] = _ => "de-DE",
        ["Region"] = _ => "de-DE",
        ["Keyboard"] = _ => "0407:00000407",
        ["RemoveApps"] = _ => CatalogData.Current.Appx.Apps.Select(a => a.Name).Take(5).ToList(),
        ["RemoveParts"] = _ => new List<string> { "wordpad" },
    };

    private static string BarText(MediaChoices c) => InstallMediaSummary.Bar(c, "Windows 11 Pro", "26300") + "\n" + InstallMediaSummary.BarChanges(c);

    private static string ReviewText(MediaChoices c)
    {
        var r = InstallMediaSummary.Review(c, "Windows 11 Pro", "26300", 1);
        return string.Join("\n", new[] { r.Title, r.Summary }.Concat(r.Changes.Select(l => $"{l.Target}={l.After}")).Concat(r.Warnings));
    }

    /// <summary>The problems of one place: a choice it does not show that is not named, or a named one it shows.</summary>
    private static List<string> Unshown(string place, Func<MediaChoices, string> text, Dictionary<string, string> exempt)
    {
        var shown = new Dictionary<string, List<bool?>>(StringComparer.Ordinal);
        foreach (var full in new[] { Full(false), Full(true) })
        {
            var before = text(full);
            foreach (var (path, changed) in RecordLeaves.Of(full, SummaryValues))
            {
                if (!shown.TryGetValue(path, out var list)) shown[path] = list = [];
                list.Add(changed is null ? null : text(changed) != before);
            }
        }
        foreach (var record in shown.Keys.Where(p => RecordLeaves.IsInside(p, shown.Keys)).ToList()) shown.Remove(record);
        var problems = new List<string>();
        foreach (var (path, results) in shown)
        {
            if (exempt.ContainsKey(path))
            {
                if (results.Contains(true)) problems.Add($"MediaChoices.{path} changes {place}: take it off its list of choices it does not show");
            }
            else if (results.All(r => r is null))
            {
                problems.Add($"MediaChoices.{path} is in a record that is null in every Full choices: set it in one");
            }
            else if (!results.Contains(true))
            {
                problems.Add($"MediaChoices.{path} does not change {place}: show it there (InstallMediaSummary), or name it in the test's list with the reason");
            }
        }
        problems.AddRange(exempt.Keys.Where(k => !shown.ContainsKey(k)).Select(k => $"The list for {place} names {k}, which is no choice of MediaChoices"));
        return problems;
    }

    /// <summary>
    /// Every choice, changed alone, changes the bar (image line or changes line) and the review, unless the place's list
    /// names it with the reason. A new choice that a place forgets fails here with its name.
    /// </summary>
    [Fact]
    public void EveryChoiceAppearsInTheBarAndTheReviewUnlessItIsNamedAsLeftOut()
    {
        var problems = Unshown("the summary bar (InstallMediaSummary.Bar, BarChanges)", BarText, NotInTheBar)
            .Concat(Unshown("the review (InstallMediaSummary.Review)", ReviewText, NotInTheReview)).ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ReviewNamesTheKeyboardByTheNameThePagePassesWithSkipSetup()
    {
        var skip = Choices(account: "Anna") with { Keyboard = "0C07:00000407" };
        var named = InstallMediaSummary.Review(skip, "Windows 11 Pro", null, 1, "German (de-AT)");
        Assert.Contains(named.Changes, l => l.Target == Loc.Instance["Media_Keyboard"] && l.After == "German (de-AT)");
        // Without a name from the page the review still says which keyboard: its code.
        Assert.Contains(InstallMediaSummary.Review(skip, "Windows 11 Pro", null, 1).Changes, l => l.Target == Loc.Instance["Media_Keyboard"] && l.After == "0C07:00000407");
        // Without Skip setup the keyboard is no choice of the page (its box is off): no line.
        Assert.DoesNotContain(InstallMediaSummary.Review(Choices() with { Keyboard = "0C07:00000407" }, "Windows 11 Pro", null, 1, "German (de-AT)").Changes,
            l => l.Target == Loc.Instance["Media_Keyboard"]);
    }

    [Fact]
    public void BarPartsMakeTheBarText()
    {
        var c = Choices(apps: 2, account: "Anna", profile: "office", install: 2);
        var parts = InstallMediaSummary.BarChangeParts(c);
        Assert.True(parts.Count >= 4);
        Assert.Equal(InstallMediaSummary.BarChanges(c), string.Join(", ", parts));
        Assert.Contains(Loc.Instance.Format("Media_Summary_Install", 2), parts);
        Assert.Empty(InstallMediaSummary.BarChangeParts(Choices()));
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
