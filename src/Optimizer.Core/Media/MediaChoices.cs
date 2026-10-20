using System.Globalization;
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
    /// <summary>The answer file's architecture: x64 media only in this version.</summary>
    public const string Architecture = "amd64";

    /// <summary>The catalog's spelling of <see cref="Architecture"/>.</summary>
    public const string CatalogArchitecture = "x64";

    private const string FallbackRegion = "en-US";
    private const string FallbackKeyboard = "0409:00000409";

    /// <summary>The apps the page offers for removal: the reviewed list without the protected ones.</summary>
    public static IEnumerable<AppxEntry> OfferedApps(AppxCatalog appx) => appx.Apps.Where(a => !appx.IsProtected(a.Name));

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
        var first = Tunero is { } copy ? new FirstSignInFile(copy.Profile, AppLanguage, copy.Theme) : null;
        return new MediaRequest(Source, Edition, IsoIndex, RemoveApps, answers, Tunero?.Exe, first, OutputIso);
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
