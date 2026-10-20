using System.Text.Json;
using Optimizer.Core.Catalog;

namespace Optimizer.Core.Media;

/// <summary>A --build-media request skips Windows' setup questions with this local account, region and keyboard.</summary>
public sealed record MediaSetupChoice(string AccountName, string Region, string Keyboard);

/// <summary>
/// A request file field and why it cannot be used, in plain English for the developer who wrote it (--build-media is a
/// developer switch). The message is "field: reason".
/// </summary>
public sealed class MediaRequestFileException(string field, string reason) : FormatException($"{field}: {reason}")
{
    public string Field { get; } = field;
}

/// <summary>
/// The request file of the developer switch --build-media (the VM tests): the install media page's choices as JSON, in
/// camelCase, every field given (null where allowed) except the optional appLanguage and theme, nothing else.
/// <see cref="Parse"/> checks each value as the page does
/// (it only offers what passes); <see cref="ToChoices"/> turns it into the page's <see cref="MediaChoices"/>, whose
/// <see cref="MediaChoices.ToRequest"/> is the page's own mapping.
/// <code>
/// { "source": "download" or "C:\\path\\Win11.iso", "language": "de-de" (download only), "edition": "Professional",
///   "removeApps": "consumer" or ["Microsoft.BingNews", ...], "skipSetup": { "accountName": "Anna", "region": "de-AT",
///   "keyboard": "0407:00000407" } or null, "profile": "gaming" or null, "allowUnsupported": false, "output": "C:\\out\\Win11.iso",
///   optional: "appLanguage": "en" or "de", "theme": "System", "Light" or "Dark" (absent or null: the app's) }
/// </code>
/// </summary>
public sealed record MediaRequestFile(
    string Source,
    string? Language,
    string Edition,
    IReadOnlyList<string> RemoveApps,
    MediaSetupChoice? SkipSetup,
    string? Profile,
    bool AllowUnsupported,
    string Output,
    string? AppLanguage = null,
    string? Theme = null)
{
    public const string Download = "download";

    /// <summary>"consumer" in removeApps: the apps the page ticks when it opens.</summary>
    public const string DefaultApps = "consumer";

    private static readonly string[] Fields = ["source", "language", "edition", "removeApps", "skipSetup", "profile", "allowUnsupported", "output", "appLanguage", "theme"];
    private static readonly string[] AppLanguages = ["en", "de"];
    private static readonly string[] Themes = ["System", "Light", "Dark"];
    private static readonly string[] SetupFields = ["accountName", "region", "keyboard"];

    public bool IsDownload => Source == Download;

    /// <summary>
    /// Reads and checks a request file. <paramref name="keyboards"/> are the ones the page offers (Windows' list). Throws
    /// <see cref="MediaRequestFileException"/> naming the first field that is missing or wrong.
    /// </summary>
    public static MediaRequestFile Parse(string json, IReadOnlyList<InputProfile> keyboards)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MediaRequestFileException("request", "not valid JSON: " + ex.Message);
        }
        if (root.ValueKind != JsonValueKind.Object) throw new MediaRequestFileException("request", "must be a JSON object");
        Only(root, Fields, "");

        var source = Text(root, "source");
        var download = source == Download;
        string? language = null;
        if (download)
        {
            language = Text(root, "language");
        }
        else
        {
            if (!Path.IsPathFullyQualified(source) || !Same(Path.GetExtension(source), ".iso"))
                throw new MediaRequestFileException("source", $"must be \"{Download}\" or an absolute path to an .iso file");
            if (!File.Exists(source)) throw new MediaRequestFileException("source", $"{source} does not exist");
            if (root.TryGetProperty("language", out var given) && given.ValueKind != JsonValueKind.Null)
                throw new MediaRequestFileException("language", "only a download has a language; an own ISO's image brings its own");
        }

        var edition = Text(root, "edition");
        if (download && !MediaCatalog.OfferedEditions.Contains(edition))
            throw new MediaRequestFileException("edition", $"{edition} is not an edition the page offers ({string.Join(", ", MediaCatalog.OfferedEditions)})");

        var apps = Apps(Required(root, "removeApps", ""));
        var setup = Setup(Required(root, "skipSetup", ""), keyboards);

        var profileValue = Required(root, "profile", "");
        string? profile = null;
        if (profileValue.ValueKind != JsonValueKind.Null)
        {
            profile = Text(root, "profile");
            var profiles = CatalogData.Current.Profiles.Profiles;
            if (!profiles.Any(p => p.Id == profile))
                throw new MediaRequestFileException("profile", $"{profile} is not a profile ({string.Join(", ", profiles.Select(p => p.Id))}), or null for no Tunero copy");
        }

        var unsupported = Required(root, "allowUnsupported", "");
        if (unsupported.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new MediaRequestFileException("allowUnsupported", "must be true or false");

        var output = Text(root, "output");
        if (!Path.IsPathFullyQualified(output)) throw new MediaRequestFileException("output", "must be an absolute path");
        if (!Same(Path.GetExtension(output), ".iso")) throw new MediaRequestFileException("output", "must end in .iso");
        if (!MediaChoices.IsLocalOutput(output))
            throw new MediaRequestFileException("output", $@"{output} is not on a local fixed drive (no network share, \\?\ path or removable drive): the build works beside it");
        if (Path.GetDirectoryName(output) is not { } folder || !Directory.Exists(folder))
            throw new MediaRequestFileException("output", $"the folder of {output} does not exist");
        if (Directory.Exists(output)) throw new MediaRequestFileException("output", $"{output} is a folder");
        if (!download && Same(Path.GetFullPath(source), Path.GetFullPath(output)))
            throw new MediaRequestFileException("output", "is the source ISO itself");

        return new MediaRequestFile(source, language, edition, apps, setup, profile, unsupported.GetBoolean(), output,
            Optional(root, "appLanguage", AppLanguages), Optional(root, "theme", Themes));
    }

    /// <summary>
    /// The page's choices for this request. <paramref name="catalog"/> is Microsoft's (a download picks its newest image for
    /// the language and edition, as the page does); <paramref name="tuneroExe"/> is the published exe, or null when this
    /// build is not one (the page then offers no profile either). Throws <see cref="MediaRequestFileException"/>.
    /// </summary>
    public MediaChoices ToChoices(IReadOnlyList<EsdFile>? catalog, string appLanguage, string? tuneroExe, string theme)
    {
        CheckTuneroCopy(tuneroExe);
        MediaSource source;
        if (IsDownload)
        {
            var files = catalog ?? throw new ArgumentNullException(nameof(catalog), "A download needs the catalog.");
            if (MediaCatalog.Editions(files, Language!, MediaChoices.CatalogArchitecture) is not { Count: > 0 } editions)
                throw new MediaRequestFileException("language", $"Microsoft's catalog has no {MediaChoices.CatalogArchitecture} image in {Language}");
            var esd = MediaCatalog.Pick(files, Language!, MediaChoices.CatalogArchitecture, Edition)
                      ?? throw new MediaRequestFileException("edition", $"Microsoft's catalog has no {Edition} image in {Language} (it has {string.Join(", ", editions)})");
            source = new MediaSource(esd, null, null);
        }
        else
        {
            source = new MediaSource(null, Source, null);
        }
        return new MediaChoices(source, Edition, null, null, AppLanguage ?? appLanguage, RemoveApps, SkipSetup is not null, SkipSetup?.AccountName,
            SkipSetup?.Region, SkipSetup?.Keyboard, AllowUnsupported, Profile is { } profile ? new TuneroCopy(tuneroExe!, profile, Theme ?? theme) : null, Output);
    }

    /// <summary>A profile needs the Tunero copy: <paramref name="tuneroExe"/> is the published exe, or null for any other build.</summary>
    public void CheckTuneroCopy(string? tuneroExe)
    {
        if (Profile is not null && tuneroExe is null)
            throw new MediaRequestFileException("profile", "the Tunero copy on the media needs the published single-file Tunero.exe; this is a development build (use null)");
    }

    private static IReadOnlyList<string> Apps(JsonElement value)
    {
        var offered = MediaChoices.OfferedApps(CatalogData.Current.Appx).ToList();
        if (value.ValueKind == JsonValueKind.String && value.GetString() == DefaultApps)
            return offered.Where(MediaChoices.ChosenByDefault).Select(a => a.Name).ToList();
        if (value.ValueKind != JsonValueKind.Array)
            throw new MediaRequestFileException("removeApps", $"must be \"{DefaultApps}\" (the page's default) or a list of app names ([] for none)");
        var names = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new MediaRequestFileException("removeApps", "every entry must be a string");
            var name = item.GetString()!;
            var app = offered.FirstOrDefault(a => Same(a.Name, name))
                      ?? throw new MediaRequestFileException("removeApps", $"{name} is not in the app list the page offers (appx.json, without protected apps)");
            if (!names.Contains(app.Name)) names.Add(app.Name);
        }
        return names;
    }

    private static MediaSetupChoice? Setup(JsonElement value, IReadOnlyList<InputProfile> keyboards)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new MediaRequestFileException("skipSetup", "must be an object with accountName, region and keyboard, or null to keep Windows' setup questions");
        Only(value, SetupFields, "skipSetup.");

        var account = Text(value, "accountName", "skipSetup.").Trim();
        if (AccountName.Problem(account) is { } problem)
            throw new MediaRequestFileException("skipSetup.accountName", problem switch
            {
                "Media_NameEmpty" => "is empty",
                "Media_NameTooLong" => "is longer than 20 characters",
                "Media_NameReserved" => $"{account} is reserved by Windows (an account or group name)",
                _ => $"{account} has a character Windows does not allow in a user name",
            });

        var regionName = Text(value, "region", "skipSetup.");
        var region = MediaLocales.IsLocaleName(regionName)
            ? MediaLocales.Regions().FirstOrDefault(c => Same(c.Name, regionName))
            : null;
        if (region is null)
            throw new MediaRequestFileException("skipSetup.region", $"{regionName} is not a region Windows knows (a specific culture like de-DE)");

        var keyboardCode = Text(value, "keyboard", "skipSetup.");
        if (!MediaLocales.IsInputLocale(keyboardCode) || keyboardCode.Contains(';'))
            throw new MediaRequestFileException("skipSetup.keyboard", $"{keyboardCode} is not an input profile like 0407:00000407");
        var keyboard = keyboards.FirstOrDefault(k => Same(k.Code, keyboardCode))
                       ?? throw new MediaRequestFileException("skipSetup.keyboard", $"{keyboardCode} is not a keyboard Windows sets up for any region");
        return new MediaSetupChoice(account, region.Name, keyboard.Code);
    }

    /// <summary>Refuses a field the request file does not have (a typo would otherwise be ignored).</summary>
    private static void Only(JsonElement obj, string[] fields, string prefix)
    {
        foreach (var property in obj.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
                throw new MediaRequestFileException(prefix + property.Name, $"not a field of the request file (fields: {string.Join(", ", fields)})");
    }

    /// <summary>An optional field: absent or null gives null, else one of <paramref name="allowed"/> (any case, returned as listed).</summary>
    private static string? Optional(JsonElement obj, string name, string[] allowed)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new MediaRequestFileException(name, "must be a string or null");
        var text = value.GetString()!;
        return allowed.FirstOrDefault(a => Same(a, text))
               ?? throw new MediaRequestFileException(name, $"{text} is not {string.Join(", ", allowed[..^1])} or {allowed[^1]} (or null for the app's)");
    }

    private static JsonElement Required(JsonElement obj, string name, string prefix) =>
        obj.TryGetProperty(name, out var value) ? value : throw new MediaRequestFileException(prefix + name, "is missing");

    private static string Text(JsonElement obj, string name, string prefix = "")
    {
        var value = Required(obj, name, prefix);
        if (value.ValueKind != JsonValueKind.String) throw new MediaRequestFileException(prefix + name, "must be a string");
        var text = value.GetString()!;
        return text.Trim().Length == 0 && name != "accountName" ? throw new MediaRequestFileException(prefix + name, "is empty") : text;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
