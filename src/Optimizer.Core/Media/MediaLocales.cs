using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Optimizer.Core.Media;

/// <summary>A keyboard the answer file can set: Windows' input profile ("0407:00000407") with its keyboard and language.</summary>
public sealed record InputProfile(string Code, string Keyboard, string LanguageTag);

/// <summary>
/// The regions and keyboards the install media page offers. Nothing is typed in: regions are the specific cultures
/// Windows itself knows (an LCID of their own), keyboards are Windows' default input profiles as Microsoft lists them
/// (Data/input-profiles.txt), so every value that reaches the answer file is one Windows Setup accepts.
/// </summary>
public static class MediaLocales
{
    private const int LocaleCustomUnspecified = 0x1000;
    private const uint LocaleIDefaultAnsiCodePage = 0x1004;

    private static readonly Lazy<(IReadOnlyList<InputProfile> Profiles, IReadOnlyDictionary<string, string> RegionDefaults)> Data = new(Load);

    /// <summary>Every input profile of Microsoft's list, once each, in the list's order.</summary>
    public static IReadOnlyList<InputProfile> Keyboards => Data.Value.Profiles;

    /// <summary>The specific cultures with a Windows locale id of their own (no custom or ICU-only names).</summary>
    public static IReadOnlyList<CultureInfo> Regions() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(c => c.LCID != LocaleCustomUnspecified && c.Name.Length > 0)
            .ToList();

    /// <summary>Microsoft's default input profile for a region (by its English name, as the list names them), or null.</summary>
    public static string? DefaultKeyboard(CultureInfo region) =>
        Data.Value.RegionDefaults.TryGetValue(region.EnglishName, out var code) ? code : null;

    /// <summary>
    /// The system locale for a region: the region itself when Windows has a code page for programs without Unicode, and
    /// en-US for Unicode-only locales, which Windows does not take as the system locale.
    /// </summary>
    public static string SystemLocale(string region) => AnsiCodePage(region) is > 0 ? region : "en-US";

    /// <summary>
    /// The signed-in user's region format (Windows' user locale), or null when it cannot be read. Not checked against
    /// <see cref="Regions"/> (cheap at start): the page picks it only when its list has it.
    /// </summary>
    public static string? ThisPcRegion()
    {
        var buffer = new StringBuilder(85);
        return GetUserDefaultLocaleName(buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    /// <summary>
    /// The input profile of the keyboard layout this thread uses now, when it is a plain layout (the layout id is the
    /// handle's high word); null otherwise (variant layouts need the registry to name them). Like the region, the page
    /// picks it only when <see cref="Keyboards"/> has it.
    /// </summary>
    public static string? ThisPcKeyboard() => KeyboardOf(GetKeyboardLayout(0).ToInt64());

    internal static string? KeyboardOf(long hkl)
    {
        var language = (int)(hkl & 0xFFFF);
        var layout = (int)((hkl >> 16) & 0xFFFF);
        if (language == 0 || layout == 0 || (layout & 0xF000) == 0xF000) return null;
        return $"{language:X4}:0000{layout:X4}";
    }

    private static int? AnsiCodePage(string locale)
    {
        var buffer = new StringBuilder(8);
        return GetLocaleInfoEx(locale, LocaleIDefaultAnsiCodePage, buffer, buffer.Capacity) > 0 &&
               int.TryParse(buffer.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : null;
    }

    private static (IReadOnlyList<InputProfile>, IReadOnlyDictionary<string, string>) Load()
    {
        var profiles = new List<InputProfile>();
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Catalog.CatalogData.ReadResourceText("Media.input-profiles.txt").Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var cells = text.Split('|');
            if (cells.Length < 4 || (cells.Length - 1) % 3 != 0) throw new InvalidDataException("Bad line in input-profiles.txt: " + text);
            for (var i = 1; i < cells.Length; i += 3)
            {
                var profile = new InputProfile(cells[i + 2], cells[i + 1], cells[i]);
                if (i == 1) defaults.TryAdd(cells[0], profile.Code);
                if (!profiles.Any(p => p.Code.Equals(profile.Code, StringComparison.OrdinalIgnoreCase))) profiles.Add(profile);
            }
        }
        return (profiles, defaults);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetUserDefaultLocaleName(StringBuilder name, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetLocaleInfoEx(string locale, uint type, StringBuilder data, int length);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetKeyboardLayout(uint thread);
}
