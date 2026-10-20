using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Optimizer.Core.Media;

/// <summary>A keyboard the answer file can set: Windows' input profile ("0407:00000407") with its keyboard and language.</summary>
public sealed record InputProfile(string Code, string Keyboard, string LanguageTag);

/// <summary>
/// The regions and keyboards the install media page offers. Nothing is typed in, and every list comes from Windows itself:
/// regions are the specific cultures with a Windows locale id of their own, keyboards are the ones Windows sets up for each
/// of them (GetLocaleInfoEx LOCALE_SKEYBOARDSTOINSTALL, the same "language:keyboard" pairs the answer file takes), named by
/// the layout's name in the registry. So every value that reaches the answer file is one Windows Setup knows.
/// </summary>
public static partial class MediaLocales
{
    private const int LocaleCustomUnspecified = 0x1000;
    private const uint LocaleIDefaultAnsiCodePage = 0x1004;
    private const uint LocaleSKeyboardsToInstall = 0x5E;
    private const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    private static readonly Lazy<IReadOnlyList<InputProfile>> AllKeyboards =
        new(() => ProfilesFrom(Regions().Select(c => (c.Name, KeyboardsToInstall(c.Name))), LayoutName));

    /// <summary>Every keyboard Windows sets up for one of the <see cref="Regions"/>, once each. Reads the registry once.</summary>
    public static IReadOnlyList<InputProfile> Keyboards => AllKeyboards.Value;

    /// <summary>The specific cultures with a Windows locale id of their own (no custom or ICU-only names).</summary>
    public static IReadOnlyList<CultureInfo> Regions() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(c => c.LCID != LocaleCustomUnspecified && c.Name.Length > 0)
            .ToList();

    /// <summary>The keyboard Windows sets up first for a region, or null.</summary>
    public static string? DefaultKeyboard(CultureInfo region) =>
        KeyboardsToInstall(region.Name)?.Split(';').Select(Normalize).FirstOrDefault(c => c is not null);

    /// <summary>
    /// The input profiles of Windows' per-locale lists ("0407:00000407;0409:00000409"), once each in list order. A keyboard
    /// layout is named by <paramref name="layoutName"/> (its registry name), an input method (IME, GUIDs instead of a
    /// layout id) by its language.
    /// </summary>
    internal static IReadOnlyList<InputProfile> ProfilesFrom(IEnumerable<(string Locale, string? List)> lists, Func<string, string?> layoutName)
    {
        var profiles = new List<InputProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locale, list) in lists)
        {
            if (list is null) continue;
            foreach (var entry in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Normalize(entry) is not { } code || !seen.Add(code)) continue;
                var culture = Culture(int.Parse(code[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                var tag = culture?.Name ?? locale;
                var keyboard = code[5] == '{'
                    ? $"{culture?.DisplayName ?? tag} IME"
                    : layoutName(code[5..]) ?? code[5..];
                profiles.Add(new InputProfile(code, keyboard, tag));
            }
        }
        return profiles;
    }

    /// <summary>"0c07:00000407" as "0C07:00000407"; null for anything that is not a language id with a layout id or two GUIDs.</summary>
    private static string? Normalize(string entry) => InputProfileCode().IsMatch(entry.Trim()) ? entry.Trim().ToUpperInvariant() : null;

    [GeneratedRegex(@"^[0-9A-Fa-f]{4}:([0-9A-Fa-f]{8}|\{[0-9A-Fa-f-]{36}\}\{[0-9A-Fa-f-]{36}\})$")]
    private static partial Regex InputProfileCode();

    private static CultureInfo? Culture(int lcid)
    {
        try
        {
            return CultureInfo.GetCultureInfo(lcid);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The system locale for a region: the region itself when Windows has a code page for programs without Unicode, and
    /// en-US for Unicode-only locales, which Windows does not take as the system locale.
    /// </summary>
    public static string SystemLocale(string region) => int.TryParse(LocaleInfo(region, LocaleIDefaultAnsiCodePage), NumberStyles.None,
        CultureInfo.InvariantCulture, out var page) && page > 0 ? region : "en-US";

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
    /// picks it only when <see cref="Keyboards"/> has it. Call it on the UI thread: the layout is per thread.
    /// </summary>
    public static string? ThisPcKeyboard() => KeyboardOf(GetKeyboardLayout(0).ToInt64());

    internal static string? KeyboardOf(long hkl)
    {
        var language = (int)(hkl & 0xFFFF);
        var layout = (int)((hkl >> 16) & 0xFFFF);
        if (language == 0 || layout == 0 || (layout & 0xF000) == 0xF000) return null;
        return $"{language:X4}:0000{layout:X4}";
    }

    private static string? KeyboardsToInstall(string locale) => LocaleInfo(locale, LocaleSKeyboardsToInstall);

    private static string? LocaleInfo(string locale, uint type)
    {
        var buffer = new StringBuilder(1024);
        return GetLocaleInfoEx(locale, type, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    /// <summary>The layout's display name in Windows' language (its MUI string), else its English "Layout Text"; null when unknown.</summary>
    private static string? LayoutName(string klid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{LayoutsKey}\{klid}");
            if (key is null) return null;
            if (key.GetValue("Layout Display Name") is string display && display.StartsWith('@'))
            {
                var buffer = new StringBuilder(256);
                if (SHLoadIndirectString(display, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0) return buffer.ToString();
            }
            return key.GetValue("Layout Text") as string is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
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

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint length, IntPtr reserved);
}
