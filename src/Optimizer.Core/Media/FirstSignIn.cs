using System.Text.Json;
using Optimizer.Core.Catalog;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Media;

/// <summary>What the install media tells the copied Tunero to apply at the first sign-in.</summary>
public sealed record FirstSignInFile(string Profile, string Language, string Theme);

/// <summary>
/// The small JSON file next to Tunero.exe in %WINDIR%\Setup\Tunero on a PC installed from the media. The copied app runs
/// elevated at the first sign-in, so the file counts only when the exe sits in that folder, the folder is writable by
/// administrators only, and the content is small and valid.
/// </summary>
public static class FirstSignIn
{
    public const string Switch = "--first-sign-in";
    public const string FileName = "first-sign-in.json";
    private const long MaxBytes = 4096;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Setup", "Tunero");

    public static string Serialize(FirstSignInFile f) => JsonSerializer.Serialize(f, Json);

    /// <summary>A known profile id, language en or de, theme System, Dark or Light: what <see cref="Read"/> accepts.</summary>
    public static bool IsValid(FirstSignInFile file) =>
        file.Profile is not null && file.Language is not null && file.Theme is not null
        && CatalogData.Current.Profiles.Profiles.Any(p => p.Id == file.Profile)
        && (file.Language is "en" or "de")
        && (file.Theme is "System" or "Dark" or "Light");

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
