using System.Globalization;
using Optimizer.App.Services;
using Optimizer.Core;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Actions;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;

namespace Optimizer.App.ViewModels;

/// <summary>
/// What the install media page says about its choices in two places: the bar that stays at the bottom of the page (its
/// image line <see cref="Bar"/> and its changes line <see cref="BarChanges"/>) and the review before the build
/// (<see cref="Review"/>). Pure text from <see cref="MediaChoices"/>; nothing is read from this PC. A new choice adds one
/// part to the changes line and one line to the review.
/// </summary>
internal static class InstallMediaSummary
{
    private static Loc T => Loc.Instance;

    /// <summary>The image: "Windows 11 Pro 26300.9711, Language German (Germany)". The space it needs is the caption below it.</summary>
    public static string Bar(MediaChoices c, string editionTitle, string? build)
    {
        var parts = new List<string> { EditionText(editionTitle, build) };
        if (LanguageName(c) is { } language) parts.Add(T.Format("Media_Summary_Language", language));
        return string.Join(", ", parts);
    }

    /// <summary>What goes into the image: "Apps to remove: 18, Account Anna, Profile Office"; null when nothing does.</summary>
    public static string? BarChanges(MediaChoices c) => BarChangeParts(c) is { Count: > 0 } parts ? string.Join(", ", parts) : null;

    /// <summary>
    /// The parts of <see cref="BarChanges"/> one by one: the page sets each on its own, so a line breaks between two parts
    /// and never inside one ("Apps to install: 2").
    /// </summary>
    public static IReadOnlyList<string> BarChangeParts(MediaChoices c)
    {
        var parts = new List<string>();
        if (c.RemoveApps.Count > 0) parts.Add(T.Format("Media_Summary_Apps", c.RemoveApps.Count));
        if (c.RemoveUnreviewed.Count > 0) parts.Add(T.Format("Media_Summary_Unreviewed", c.RemoveUnreviewed.Count));
        // Counted as the review names them: with the parts another chosen part takes along.
        if (c.RemoveParts.Count > 0) parts.Add(T.Format("Media_PartsSummary", MediaParts.WithImplied(CatalogData.Current.MediaParts, c.RemoveParts).Count));
        if (c.SkipSetup && c.AccountName?.Trim() is { Length: > 0 } account) parts.Add(T.Format("Media_Summary_Account", account));
        if (c.Tunero is { } copy)
        {
            parts.Add(T.Format("Media_Summary_Profile", ProfileTitle(copy)));
            if (c.InstallApps.Count > 0) parts.Add(T.Format("Media_InstallSummary", c.InstallApps.Count));
        }
        if (c.Compression == MediaCompression.Recovery) parts.Add(T["Media_CompressionSmaller"]);
        if (c.AllowUnsupported) parts.Add(T["Media_UnsupportedTitle"]);
        if (c.Settings.AllowUnsupportedUpgrades) parts.Add(T["Media_UpgradesTitle"]);
        if (c.Settings.PreventDeviceEncryption) parts.Add(T["Media_EncryptionTitle"]);
        if (c.Settings.NoReservedStorage) parts.Add(T["Media_ReservedTitle"]);
        return parts;
    }

    /// <summary>
    /// The question before the build: the source and the output file, one line for every choice that is on, and the texts of
    /// the Expert options that are on as warnings. The lines have no "now" value, only what goes into the ISO.
    /// <paramref name="keyboardTitle"/> is the chosen keyboard's name as the page lists it (the summary reads nothing from
    /// this PC, and the names come from its registry).
    /// </summary>
    public static ConfirmRequest Review(MediaChoices c, string editionTitle, string? build, long neededBytes, string? keyboardTitle = null)
    {
        var source = c.Source.IsoPath ?? T["Media_Review_SourceDownload"];
        var changes = new List<ChangeLine> { Line(T["Media_Edition"], EditionText(editionTitle, build)) };
        if (LanguageName(c) is { } language) changes.Add(Line(T["Media_Language"], language));
        foreach (var (group, count) in AppGroups(c)) changes.Add(Line(T.Format("Media_Review_AppsGroup", group), count.ToString(CultureInfo.CurrentCulture)));
        if (c.RemoveParts.Count > 0) changes.Add(Line(T["Media_PartsTitle"], PartsText(c)));
        if (c.SkipSetup)
        {
            changes.Add(Line(T["Media_SkipSetupTitle"], SetupText(c)));
            // The page passes the keyboard's name (from Windows' layout list); its code where it has none.
            if ((keyboardTitle ?? c.Keyboard) is { Length: > 0 } keyboard) changes.Add(Line(T["Media_Keyboard"], keyboard));
        }
        if (c.Tunero is { } copy)
        {
            changes.Add(Line(T["Media_ProfileTitle"], ProfileTitle(copy)));
            if (c.InstallApps.Count > 0) changes.Add(Line(T["Media_InstallTitle"], T.Format("Media_InstallSummary", c.InstallApps.Count)));
        }
        if (c.Compression == MediaCompression.Recovery) changes.Add(Line(T["Media_CompressionTitle"], T["Media_CompressionSmaller"]));
        var warnings = new List<string>();
        if (c.RemoveUnreviewed.Count > 0)
        {
            changes.Add(Line(T["Media_UnreviewedTitle"], T.Format("Media_AppsSummary", c.RemoveUnreviewed.Count)));
            warnings.Add(T.Format("Media_Review_Warning", T["Media_UnreviewedTitle"], T["Media_UnreviewedText"]));
        }
        if (c.Settings.PreventDeviceEncryption)
        {
            changes.Add(Line(T["Media_EncryptionTitle"], T["Media_Review_On"]));
            warnings.Add(T.Format("Media_Review_Warning", T["Media_EncryptionTitle"], T["Media_EncryptionText"]));
        }
        if (c.Settings.NoReservedStorage)
        {
            changes.Add(Line(T["Media_ReservedTitle"], T["Media_Review_On"]));
            warnings.Add(T.Format("Media_Review_Warning", T["Media_ReservedTitle"], T["Media_ReservedText"]));
        }
        if (c.AllowUnsupported)
        {
            changes.Add(Line(T["Media_UnsupportedTitle"], T["Media_Review_On"]));
            warnings.Add(T.Format("Media_Review_Warning", T["Media_UnsupportedTitle"], T["Media_UnsupportedText"]));
        }
        if (c.Settings.AllowUnsupportedUpgrades)
        {
            changes.Add(Line(T["Media_UpgradesTitle"], T["Media_Review_On"]));
            warnings.Add(T.Format("Media_Review_Warning", T["Media_UpgradesTitle"], T["Media_UpgradesText"]));
        }
        // Duration stays empty until the build times are measured in the test VM (docs/vm-test-plan.md).
        return new ConfirmRequest(T["Media_Review_Title"], T.Format("Media_Review_Summary", source, c.OutputIso), changes, [], warnings, null, false)
        {
            Primary = T["Media_BuildButton"],
            Footer = T["Media_Review_Footer"],
        };
    }

    private static ChangeLine Line(string what, string text) => new(what, "", text);

    private static string EditionText(string editionTitle, string? build) =>
        build is { Length: > 0 } ? T.Format("Media_Summary_Edition", editionTitle, build) : editionTitle;

    private static string ProfileTitle(TuneroCopy copy) => T[$"Profile_{copy.Profile}"];

    /// <summary>The Windows language of the image: the download's, or the one the own ISO's image names.</summary>
    private static string? LanguageName(MediaChoices c) => CultureText(c.Source.Download?.LanguageCode ?? c.IsoLanguage);

    private static string? CultureText(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        try
        {
            return new CultureInfo(code).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>"Account Anna, German (Austria)".</summary>
    private static string SetupText(MediaChoices c)
    {
        var region = CultureText(c.Region) ?? "";
        return c.AccountName?.Trim() is { Length: > 0 } name ? T.Format("Media_SetupSummary", name, region) : T.Format("Media_SetupSummaryNoName", region);
    }

    /// <summary>
    /// The names of the parts the build removes in the app's language, in the catalog's order: "WordPad, Recall". A part
    /// that another chosen part takes along (text to speech takes speech recognition) is named too.
    /// </summary>
    private static string PartsText(MediaChoices c) =>
        string.Join(", ", MediaParts.WithImplied(CatalogData.Current.MediaParts, c.RemoveParts).Select(p => p.Label(c.AppLanguage)));

    /// <summary>The ticked apps by group of the reviewed list, in the page's order; names the list does not know go last.</summary>
    private static IEnumerable<(string Group, int Count)> AppGroups(MediaChoices c)
    {
        var appx = CatalogData.Current.Appx;
        var groups = c.RemoveApps.GroupBy(name => appx.Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.Group);
        foreach (var g in groups.OrderBy(g => g.Key is null ? int.MaxValue : GroupOrder(g.Key)))
            yield return (g.Key is null ? T["Media_Review_AppsOther"] : Labels.Current.Get(c.AppLanguage, $"debloatGroup.{g.Key}"), g.Count());
    }

    private static readonly string[] GroupKeys = ["consumer", "microsoft", "xbox", "media", "legacy", "thirdParty", "oem"];

    private static int GroupOrder(string key) => Array.IndexOf(GroupKeys, key) is var i and >= 0 ? i : GroupKeys.Length;
}
