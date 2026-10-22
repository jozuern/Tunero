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
/// What the install media page says about its choices in two places: the one-line bar that stays at the bottom of the page
/// (<see cref="Bar"/>) and the review before the build (<see cref="Review"/>). Pure text from <see cref="MediaChoices"/>;
/// nothing is read from this PC. A new choice adds one line to each of the two.
/// </summary>
internal static class InstallMediaSummary
{
    private static Loc T => Loc.Instance;

    /// <summary>"Windows 11 Pro 26300, German (Germany), Apps to remove: 18, Account Anna, Office, 19.9 GB needed".</summary>
    public static string Bar(MediaChoices c, string editionTitle, string? build, long neededBytes)
    {
        var parts = new List<string> { EditionText(editionTitle, build) };
        if (LanguageName(c) is { } language) parts.Add(T.Format("Media_Summary_Language", language));
        if (c.RemoveApps.Count > 0) parts.Add(T.Format("Media_Summary_Apps", c.RemoveApps.Count));
        if (c.Compression == MediaCompression.Recovery) parts.Add(T["Media_CompressionSmaller"]);
        if (c.Settings.PreventDeviceEncryption) parts.Add(T["Media_EncryptionTitle"]);
        if (c.Settings.NoReservedStorage) parts.Add(T["Media_ReservedTitle"]);
        if (c.Settings.AllowUnsupportedUpgrades) parts.Add(T["Media_UpgradesTitle"]);
        if (c.SkipSetup && c.AccountName?.Trim() is { Length: > 0 } account) parts.Add(T.Format("Media_Summary_Account", account));
        if (c.Tunero is { } copy) parts.Add(T.Format("Media_Summary_Profile", ProfileTitle(copy)));
        if (neededBytes > 0) parts.Add(T.Format("Media_Summary_Needed", ByteSize.Format(neededBytes)));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The question before the build: the source and the output file, one line for every choice that is on, and the texts of
    /// the Expert options that are on as warnings. The lines have no "now" value, only what goes into the ISO.
    /// </summary>
    public static ConfirmRequest Review(MediaChoices c, string editionTitle, string? build, long neededBytes)
    {
        var source = c.Source.IsoPath ?? T["Media_Review_SourceDownload"];
        var changes = new List<ChangeLine> { Line(T["Media_Edition"], EditionText(editionTitle, build)) };
        if (LanguageName(c) is { } language) changes.Add(Line(T["Media_Language"], language));
        foreach (var (group, count) in AppGroups(c)) changes.Add(Line(T.Format("Media_Review_AppsGroup", group), count.ToString(CultureInfo.CurrentCulture)));
        if (c.SkipSetup) changes.Add(Line(T["Media_SkipSetupTitle"], SetupText(c)));
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
