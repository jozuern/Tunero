using System.Text.Json;
using Optimizer.Core.Catalog;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Media;

/// <summary>
/// What the install media tells the copied Tunero to apply at the first sign-in. <see cref="Apps"/>: app catalog ids it
/// installs with winget after the profile (<see cref="MediaChoices.InstallableApps"/>), or null.
/// </summary>
public sealed record FirstSignInFile(string Profile, string Language, string Theme)
{
    public IReadOnlyList<string>? Apps { get; init; }

    // The list by its content: the build reads the file back and compares it with what it wrote.
    public bool Equals(FirstSignInFile? other) =>
        other is not null && Profile == other.Profile && Language == other.Language && Theme == other.Theme
        && (Apps ?? []).SequenceEqual(other.Apps ?? []);

    public override int GetHashCode() => HashCode.Combine(Profile, Language, Theme, Apps?.Count ?? 0);
}

/// <summary>
/// The small JSON file next to Tunero.exe in %WINDIR%\Setup\Tunero on a PC installed from the media. The copied app runs
/// elevated at the first sign-in, so the file counts only when the exe sits in that folder, the folder is writable by
/// administrators only, and the content is small and valid.
/// </summary>
public static class FirstSignIn
{
    public const string Switch = "--first-sign-in";
    public const string FileName = "first-sign-in.json";
    private const long MaxBytes = 16384;

    /// <summary>More apps than the catalog has machine-wide ones is not a list the page made.</summary>
    public const int MaxApps = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Setup", "Tunero");

    public static string Serialize(FirstSignInFile f) => JsonSerializer.Serialize(f, Json);

    /// <summary>
    /// A known profile id, language en or de, theme System, Dark or Light, and apps (if any) that the page offers, each
    /// once: what <see cref="Read"/> accepts.
    /// </summary>
    public static bool IsValid(FirstSignInFile file) =>
        file.Profile is not null && file.Language is not null && file.Theme is not null
        && CatalogData.Current.Profiles.Profiles.Any(p => p.Id == file.Profile)
        && (file.Language is "en" or "de")
        && (file.Theme is "System" or "Dark" or "Light")
        && (file.Apps is null || ValidApps(file.Apps));

    /// <summary>Ids of <see cref="MediaChoices.InstallableApps"/>, each once, at most <see cref="MaxApps"/>.</summary>
    public static bool ValidApps(IReadOnlyList<string> apps)
    {
        if (apps.Count > MaxApps || apps.Distinct(StringComparer.OrdinalIgnoreCase).Count() != apps.Count) return false;
        var offered = MediaChoices.InstallableApps(CatalogData.Current.Apps).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return apps.All(a => a is not null && offered.Contains(a));
    }

    /// <summary>The file, or null when it must not be trusted or is not valid. Never throws.</summary>
    public static FirstSignInFile? Read(string exePath, Func<string, bool> adminOnlyFolder) => Read(exePath, adminOnlyFolder, Folder);

    internal static FirstSignInFile? Read(string exePath, Func<string, bool> adminOnlyFolder, string folder)
    {
        try
        {
            var exe = Path.GetFullPath(exePath);
            var expected = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var exeFolder = Path.GetDirectoryName(exe)?.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(exeFolder, expected, StringComparison.OrdinalIgnoreCase)) return null;
            if (!SafeDelete.HasNoLinks(exe) || !adminOnlyFolder(exeFolder!)) return null;
            var path = Path.Combine(exeFolder!, FileName);
            if (!File.Exists(path)) return null;
            var bytes = SafeDelete.ReadFile(path, MaxBytes);
            if (bytes is null) return null;
            var file = JsonSerializer.Deserialize<FirstSignInFile>(bytes, Json);
            return file is not null && IsValid(file) ? file : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
