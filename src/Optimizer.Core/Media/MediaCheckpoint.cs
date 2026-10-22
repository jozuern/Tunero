using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Media;

/// <summary>
/// Where a build can go on from. Downloaded: the ESD is in the work folder and was checked against the catalog's SHA-256;
/// a resume runs from Check on. ImageReady: the install image on the media is final (after Shrink or Compress) and the
/// untouched boot.wim is kept beside it; a resume runs Files, Iso, Verify and Publish.
/// </summary>
public enum CheckpointKind { Downloaded, ImageReady }

/// <summary>
/// checkpoint.json in a work folder: a build that fails or is stopped after it keeps the folder (without a mount) for
/// <see cref="MediaBuild.ResumeAsync"/>. <paramref name="RequestHash"/> is <see cref="HashOf"/> of the build's request,
/// <paramref name="FileSha256"/> the ESD's (Downloaded) or the final install image's (ImageReady), <paramref name="BootSha256"/>
/// that of <see cref="BootWimCopy"/> (ImageReady only), <paramref name="Build"/> the image's build number for the volume
/// name (ImageReady only, else empty). All hashes are upper-case hex.
/// <para>
/// The file is data, never code: <see cref="Read"/> takes it only from a real file at its own path (no link), at most
/// 64 KB, with exactly the fields <see cref="ToJson"/> writes and each value checked; anything else reads as no
/// checkpoint. The work folder is locked to Administrators and SYSTEM, and the resume checks that again
/// (<see cref="IMediaSteps.IsTrustedWorkFolder"/>) together with every hash before it builds from anything in it.
/// </para>
/// </summary>
public sealed record MediaCheckpoint(CheckpointKind Kind, string RequestHash, string FileSha256, string? BootSha256, string Build, DateTimeOffset Saved)
{
    public const string FileName = "checkpoint.json";

    /// <summary>The build's request as a request file (<see cref="MediaRequestFile.FromRequest"/>): the page applies it before Resume.</summary>
    public const string ChoicesFileName = "choices.json";

    /// <summary>A copy of sources\boot.wim from before Files commits into it: a commit cut off by a crash can leave boot.wim broken.</summary>
    public const string BootWimCopy = "boot.wim.ready";

    /// <summary>A saved build older than this is deleted at the next start like any leftover.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(14);

    private const long MaxBytes = 64 * 1024;
    private static readonly string[] Fields = ["kind", "requestHash", "fileSha256", "bootSha256", "build", "saved", "uiLanguage"];

    /// <summary>
    /// ImageReady only: Setup's language as the build set it from the image (it may differ from the request's when the image
    /// lacks that language), so the answer file of a resume is the one the build would have written.
    /// </summary>
    public string? UiLanguage { get; init; }

    /// <summary>The first stage a resume runs.</summary>
    public MediaStage ResumesAt => Kind == CheckpointKind.Downloaded ? MediaStage.Check : MediaStage.Files;

    public bool IsExpired(DateTimeOffset now) => now - Saved > KeptFor;

    /// <summary>
    /// The SHA-256 of the request's choices as canonical JSON: every choice that shapes the media, lists sorted and names
    /// in lower case. Left out: the output path (a resume may write elsewhere on the same drive), the running Tunero's path,
    /// the app's language and theme in the first sign-in file (they follow the app, not a choice of the page, and the file
    /// is written after both checkpoints), and the catalog's download address and location (the file is named by its name,
    /// size and SHA-256).
    /// </summary>
    public static string HashOf(MediaRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        static JsonArray List(IEnumerable<string>? names) =>
            new((names ?? []).Select(n => n.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).Select(n => (JsonNode?)n).ToArray());
        var a = r.Answers;
        var source = r.Source.Download is { } d
            ? new JsonObject
            {
                ["download"] = d.FileName.ToLowerInvariant(), ["language"] = d.LanguageCode.ToLowerInvariant(), ["edition"] = d.Edition.ToLowerInvariant(),
                ["architecture"] = d.Architecture.ToLowerInvariant(), ["size"] = d.Size, ["sha256"] = d.Sha256.ToUpperInvariant(),
            }
            : new JsonObject { ["iso"] = r.Source.IsoPath?.ToLowerInvariant() };
        var root = new JsonObject
        {
            ["source"] = source,
            ["edition"] = r.Edition.ToLowerInvariant(),
            ["isoIndex"] = r.IsoIndex,
            ["removeApps"] = List(r.RemoveApps),
            ["removeUnreviewed"] = List(r.RemoveUnreviewed),
            ["removeParts"] = List(r.RemoveParts),
            ["compression"] = r.Compression.ToString(),
            ["preventDeviceEncryption"] = r.Settings.PreventDeviceEncryption,
            ["noReservedStorage"] = r.Settings.NoReservedStorage,
            ["allowUnsupportedUpgrades"] = r.Settings.AllowUnsupportedUpgrades,
            ["answers"] = new JsonObject
            {
                ["architecture"] = a.Architecture, ["uiLanguage"] = a.UiLanguage, ["inputLocale"] = a.InputLocale, ["systemLocale"] = a.SystemLocale,
                ["userLocale"] = a.UserLocale, ["accountName"] = a.AccountName, ["skipSetup"] = a.SkipSetup, ["allowUnsupported"] = a.AllowUnsupported,
                ["firstSignInTunero"] = a.FirstSignInTunero,
            },
            ["tunero"] = r.FirstSignIn is { } first ? new JsonObject { ["profile"] = first.Profile, ["apps"] = List(first.Apps) } : null,
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    /// <summary>Writes checkpoint.json.tmp, flushes it to the disk and renames it over checkpoint.json, so the file is whole or absent.</summary>
    public void Save(string workFolder)
    {
        var path = Path.Combine(workFolder, FileName);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(ToJson()));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    internal string ToJson()
    {
        var root = new JsonObject
        {
            ["kind"] = Kind.ToString(),
            ["requestHash"] = RequestHash,
            ["fileSha256"] = FileSha256,
            ["bootSha256"] = BootSha256,
            ["build"] = Build,
            ["saved"] = Saved.ToString("O", CultureInfo.InvariantCulture),
            ["uiLanguage"] = UiLanguage,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>The checkpoint of <paramref name="workFolder"/>, or null for a missing, linked, oversized or unreadable file or one with a value that does not fit.</summary>
    public static MediaCheckpoint? Read(string workFolder)
    {
        try
        {
            var path = Path.Combine(workFolder, FileName);
            if (!File.Exists(path) || SafeDelete.ReadFile(path, MaxBytes) is not { } bytes) return null;
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != Fields.Length
                || root.EnumerateObject().Any(p => !Fields.Contains(p.Name, StringComparer.Ordinal))) return null;
            if (!Enum.TryParse<CheckpointKind>(Text(root, "kind"), ignoreCase: false, out var kind) || !Enum.IsDefined(kind)
                || Text(root, "kind") != kind.ToString()) return null;
            var request = Text(root, "requestHash");
            var file = Text(root, "fileSha256");
            var boot = Text(root, "bootSha256");
            var build = Text(root, "build");
            var language = Text(root, "uiLanguage");
            if (!IsHash(request) || !IsHash(file)) return null;
            if (!DateTimeOffset.TryParseExact(Text(root, "saved"), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var saved)) return null;
            var ready = kind == CheckpointKind.ImageReady;
            if (ready != IsHash(boot) || (!ready && boot is not null)) return null;
            if (ready ? !IsBuildNumber(build) : build != "") return null;
            if (ready ? language is null || !MediaLocales.IsLocaleName(language) : language is not null) return null;
            return new MediaCheckpoint(kind, request!, file!, boot, build!, saved) { UiLanguage = language };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Deletes checkpoint.json: the folder is no saved build any more. Throws IOException or UnauthorizedAccessException.</summary>
    internal static void Delete(string workFolder) => File.Delete(Path.Combine(workFolder, FileName));

    /// <summary>The text of choices.json (<see cref="ChoicesFileName"/>), read like the remembered choices: null for a link, a missing or an oversized file.</summary>
    public static string? ReadChoices(string workFolder)
    {
        try
        {
            var path = Path.Combine(workFolder, ChoicesFileName);
            if (!File.Exists(path) || SafeDelete.ReadFile(path, MaxBytes) is not { } bytes) return null;
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A string value, or null for JSON null; anything else throws JsonException (read as no checkpoint).</summary>
    private static string? Text(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => throw new JsonException($"{name} is no string"),
        };
    }

    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigitUpper);

    private static bool IsBuildNumber(string? text) => text is { Length: >= 1 and <= 6 } && text.All(char.IsAsciiDigit);
}

/// <summary>A work folder with a saved build that can be resumed, its checkpoint, and how many bytes it holds on its drive.</summary>
public sealed record ResumableBuild(string WorkFolder, MediaCheckpoint Checkpoint)
{
    public long Bytes { get; init; }
}
