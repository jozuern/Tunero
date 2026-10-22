using System.Globalization;
using Optimizer.Core.Apps;
using Optimizer.Core.Debloat;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Media;

/// <summary>The Tunero copy on the media: the exe to copy and what its first sign-in applies (profile, theme).</summary>
public sealed record TuneroCopy(string Exe, string Profile, string Theme);

/// <summary>
/// The choices that make one build, from the install media page or a --build-media request file
/// (<see cref="MediaRequestFile"/>). <see cref="ToRequest"/> is the one mapping to a <see cref="MediaRequest"/> both use.
/// <paramref name="Edition"/> is the EditionID, or for an own ISO the image's edition or name; <paramref name="IsoIndex"/>
/// and <paramref name="IsoLanguage"/> are what the page read from an own ISO's chosen image (null: the build finds the
/// image by edition and takes its language). <paramref name="AppLanguage"/> is the app's (en or de).
/// <paramref name="Region"/> and <paramref name="Keyboard"/> are null when nothing was chosen.
/// </summary>
public sealed record MediaChoices(
    MediaSource Source,
    string Edition,
    int? IsoIndex,
    string? IsoLanguage,
    string AppLanguage,
    IReadOnlyList<string> RemoveApps,
    bool SkipSetup,
    string? AccountName,
    string? Region,
    string? Keyboard,
    bool AllowUnsupported,
    TuneroCopy? Tunero,
    string OutputIso)
{
    /// <summary>
    /// Expert: apps of the own ISO's image that the reviewed list does not cover (<see cref="UnreviewedApps"/>), by package
    /// name, removed like the ticked ones.
    /// </summary>
    public IReadOnlyList<string> RemoveUnreviewed { get; init; } = [];

    /// <summary>Apps of the app catalog (ids) the copied Tunero installs with winget at the first sign-in; needs <see cref="Tunero"/>.</summary>
    public IReadOnlyList<string> InstallApps { get; init; } = [];

    /// <summary>How the install image is stored on the media: Faster build (install.wim) or Smaller ISO (install.esd).</summary>
    public MediaCompression Compression { get; init; } = MediaCompression.Wim;

    /// <summary>Expert: registry values written into install.wim (<see cref="MediaImageSettings"/>).</summary>
    public MediaImageSettings Settings { get; init; } = MediaImageSettings.None;

    /// <summary>The answer file's architecture: x64 media only in this version.</summary>
    public const string Architecture = "amd64";

    /// <summary>The catalog's spelling of <see cref="Architecture"/>.</summary>
    public const string CatalogArchitecture = "x64";

    private const string FallbackRegion = "en-US";
    private const string FallbackKeyboard = "0409:00000409";

    /// <summary>The apps the page offers for removal: the reviewed list without the protected ones.</summary>
    public static IEnumerable<AppxEntry> OfferedApps(AppxCatalog appx) => appx.Apps.Where(a => !appx.IsProtected(a.Name));

    /// <summary>
    /// The groups of the reviewed list that Microsoft's own image can hold. Manufacturer apps come with a PC maker's image,
    /// the third-party apps (Spotify, Candy Crush) are downloaded after the first sign-in, and the legacy ones are gone
    /// since Windows 10: none of them was in the 26300 image (PCO-Test, 2026-10-21, 47 provisioned apps).
    /// </summary>
    public static readonly IReadOnlySet<string> MicrosoftImageGroups = new HashSet<string>(StringComparer.Ordinal) { "consumer", "microsoft", "media", "xbox" };

    /// <summary>
    /// The apps the download's image holds when that build and edition were measured (mediaimages.json), as package names;
    /// null for a build or edition nobody measured.
    /// </summary>
    public static IReadOnlyList<string>? MeasuredApps(MediaImageCatalog images, EsdFile download) =>
        int.TryParse(download.Build.Split('.')[0], out var build)
        && images.Images.FirstOrDefault(i => i.Build == build) is { } image
        && image.Editions.FirstOrDefault(e => string.Equals(e.Key, download.Edition, StringComparison.OrdinalIgnoreCase)).Value is { } apps
            ? apps
            : null;

    /// <summary>
    /// The apps the page lists: with <paramref name="imageApps"/> (the full package names an own ISO's image holds, see
    /// <see cref="DismOutput.ImageAppFolder"/>) exactly the offered ones among them, else those of
    /// <see cref="MicrosoftImageGroups"/> (the download: its image is read only during the build).
    /// </summary>
    public static IEnumerable<AppxEntry> PageApps(AppxCatalog appx, IReadOnlyCollection<string>? imageApps)
    {
        if (imageApps is null) return OfferedApps(appx).Where(a => MicrosoftImageGroups.Contains(a.Group));
        var names = imageApps.Select(p => p.Split('_')[0]).ToList();
        return OfferedApps(appx).Where(a => names.Any(a.Matches));
    }

    /// <summary>
    /// The apps of an own ISO's image (full package names) that the reviewed list does not cover, as on Remove apps
    /// (<see cref="DebloatService.Unreviewed"/>): not protected, not a part of Windows or of what games need. By package name.
    /// </summary>
    public static IReadOnlyList<string> UnreviewedApps(AppxCatalog appx, IReadOnlyCollection<string> imageApps) =>
        DebloatService.Unreviewed(appx, imageApps.Select(p => p.Split('_')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new InstalledAppx(name, "", "", false))).Select(a => a.Name).ToList();

    /// <summary>
    /// The apps the first sign-in can install: the whole app catalog. Machine-wide ones are installed by the elevated copy,
    /// per-user ones as the account that signs in first (<see cref="Winget.InstallForUserAndWaitAsync"/>).
    /// </summary>
    public static IEnumerable<AppEntry> InstallableApps(AppCatalog apps) => apps.Apps.Where(a => a.Scope is "machine" or "user" && Winget.IsSafeId(a.Id));

    /// <summary>Ticked when the page opens (and what a request file's "consumer" means): the consumer group.</summary>
    public static bool ChosenByDefault(AppxEntry app) => app.Group == "consumer";

    /// <summary>
    /// The output ISO and the build's work folder beside it belong on a local fixed drive: not a network share, a \\?\ or
    /// \\.\ device path, or a removable drive. Checked by the page and --build-media before anything runs, and by the
    /// build again (<see cref="MediaSteps.NewWorkFolder(string)"/>). Never touches the network: a UNC path is refused by
    /// its spelling.
    /// </summary>
    public static bool IsLocalOutput(string path)
    {
        try
        {
            return TrustedPath.IsOnLocalFixedDrive(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The app language's Windows language: de-de for German, en-us otherwise.</summary>
    public static string WindowsLanguage(string appLanguage) => appLanguage == "de" ? "de-de" : "en-us";

    public MediaRequest ToRequest()
    {
        var region = Region ?? FallbackRegion;
        // Setup's language: the download's, or the one the own ISO's image names (the build checks it with DISM).
        var uiLanguage = Source.Download is { } esd
            ? CultureName(esd.LanguageCode, FallbackRegion)
            : CultureName(IsoLanguage, CultureName(WindowsLanguage(AppLanguage), FallbackRegion));
        var answers = new AnswerChoices(Architecture, uiLanguage, Keyboard ?? FallbackKeyboard, MediaLocales.SystemLocale(region), region,
            SkipSetup ? AccountName?.Trim() : null, SkipSetup, AllowUnsupported, Tunero is not null);
        var first = Tunero is { } copy ? new FirstSignInFile(copy.Profile, AppLanguage, copy.Theme) { Apps = InstallApps.Count > 0 ? InstallApps : null } : null;
        return new MediaRequest(Source, Edition, IsoIndex, RemoveApps, answers, Tunero?.Exe, first, OutputIso) { RemoveUnreviewed = RemoveUnreviewed, Compression = Compression, Settings = Settings };
    }

    /// <summary>A language tag as Windows writes it ("de-DE"), or <paramref name="fallback"/> for one .NET does not know.</summary>
    private static string CultureName(string? code, string fallback)
    {
        if (code is null) return fallback;
        try
        {
            return new CultureInfo(code).Name;
        }
        catch (CultureNotFoundException)
        {
            return fallback;
        }
    }
}

/// <summary>mediaimages.json: the apps of Microsoft's image per build and edition, measured (see its comment).</summary>
public sealed class MediaImageCatalog
{
    public List<MediaImageApps> Images { get; init; } = [];
}

/// <summary>One measured build: package names per EditionID.</summary>
public sealed class MediaImageApps
{
    public int Build { get; init; }
    public Dictionary<string, List<string>> Editions { get; init; } = [];
}
