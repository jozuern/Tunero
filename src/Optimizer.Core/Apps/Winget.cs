using System.Text.RegularExpressions;
using Optimizer.Core.Hardware.Probes;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Apps;

public sealed class AppCatalog
{
    public List<AppEntry> Apps { get; init; } = [];
}

public sealed class AppEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "tools";

    /// <summary>machine = installed elevated; user = per-user installer, started as the signed-in user.</summary>
    public string Scope { get; init; } = "machine";

    /// <summary>
    /// winget --scope ("machine" or "user") for packages that offer both installers: winget prefers the per-user one by
    /// default, also when it runs elevated. Null = winget's choice.
    /// </summary>
    public string? WingetScope { get; init; }

    /// <summary>
    /// winget --installer-type for packages whose manifest offers several installers, when winget's own pick installs the
    /// wrong thing (Microsoft.DirectX: winget prefers the packaged framework, games need the classic web setup). Null =
    /// winget's choice.
    /// </summary>
    public string? InstallerType { get; init; }

    /// <summary>Regex on installed program names; empty = cannot be detected.</summary>
    public string Detect { get; init; } = "";

    public string En { get; init; } = "";
    public string De { get; init; } = "";

    public string Text(string lang) => lang == "de" && De.Length > 0 ? De : En;

    public bool IsInstalled(IEnumerable<InstalledProgram> programs) =>
        Detect.Length > 1 && programs.Any(p => Regex.IsMatch(p.Name, Detect, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
}

/// <summary>
/// winget (App Installer) for the app page. Machine-scope installs run elevated with streamed output; per-user
/// installers (Discord, Spotify) are started as the signed-in user, so they land in that user's profile.
/// </summary>
public static class Winget
{
    private const string PackageRepository = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages";

    /// <summary>
    /// winget.exe for elevated runs: only from the App Installer package folder under Program Files\WindowsApps (writable
    /// only by TrustedInstaller), found through the machine's package repository (HKLM, admin-only). Never the execution
    /// alias in the user's WindowsApps folder: the user can put any program there, and this process runs it as admin.
    /// </summary>
    public static string? FindTrusted() => FindTrusted(ReadPackageFolders());

    public static string? FindTrusted(IEnumerable<(string Package, string Folder)> packages)
    {
        var protectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
        return packages
            .Select(p => (Version: PackageVersion(p.Package), p.Folder))
            .Where(p => p.Version is not null && p.Folder.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase) && !p.Folder.Contains("..", StringComparison.Ordinal))
            .OrderByDescending(p => p.Version)
            .Select(p => Path.Combine(p.Folder, "winget.exe"))
            .FirstOrDefault(p => File.Exists(p) && Platform.SafeDelete.HasNoLinks(p));
    }

    /// <summary>
    /// winget.exe for installers started as the signed-in user (they run with that user's rights anyway): the user's
    /// execution alias, else the package folder.
    /// </summary>
    public static string? FindForUser(string? profilePath)
    {
        if (profilePath is not null)
        {
            var alias = Path.Combine(profilePath, @"AppData\Local\Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return alias;
        }
        return FindTrusted();
    }

    /// <summary>"Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe" -> 1.29.380.0; other packages (language, bundle) -> null.</summary>
    public static Version? PackageVersion(string package)
    {
        const string prefix = "Microsoft.DesktopAppInstaller_", suffix = "_x64__8wekyb3d8bbwe";
        if (!package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !package.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        return Version.TryParse(package[prefix.Length..^suffix.Length], out var v) ? v : null;
    }

    /// <summary>Packages registered for the machine whose full name starts with <paramref name="prefix"/>, with their folders.</summary>
    internal static List<(string Package, string Folder)> ReadPackageFolders(string prefix = "Microsoft.DesktopAppInstaller_")
    {
        var list = new List<(string, string)>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PackageRepository);
            foreach (var name in key?.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                using var sub = key!.OpenSubKey(name);
                if (sub?.GetValue("Path") is string folder) list.Add((name, folder));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return list;
    }

    /// <summary>Only catalog ids are passed to winget, and only if they contain nothing but id characters.</summary>
    public static bool IsSafeId(string id) => id.Length is > 2 and < 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    public static string InstallArguments(string id, string? scope = null, string? installerType = null) =>
        $"install --id {id} --exact --source winget{ScopeArgument(scope)}{InstallerTypeArgument(installerType)} --accept-package-agreements --accept-source-agreements --silent --disable-interactivity";

    /// <summary>Upgrades one package to the newest version in the winget source (nothing to do when it is current).</summary>
    public static string UpgradeArguments(string id) =>
        $"upgrade --id {id} --exact --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity";

    /// <summary>Updates a machine-scope app with progress lines. Returns winget's exit code (0 = updated or already current).</summary>
    public static Task<int> UpgradeAsync(string winget, AppEntry app, IProgress<string>? lines, CancellationToken ct)
    {
        if (!IsSafeId(app.Id)) throw new ArgumentException($"invalid winget id {app.Id}");
        return StreamingProcess.RunAsync(winget, UpgradeArguments(app.Id), lines, ct);
    }

    /// <summary>
    /// Upgrades every package of one scope with a newer version in the winget source. Machine-scope packages run in the
    /// elevated app; per-user packages run as the signed-in user (an elevated run would install them for the wrong
    /// account or be refused, and would also try to replace a running copy of this app installed by winget).
    /// </summary>
    public static string UpgradeAllArguments(string scope) => scope is "machine" or "user"
        ? $"upgrade --all --scope {scope} --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity"
        : throw new ArgumentException($"unknown scope {scope}");

    /// <summary>Starts the upgrade of the per-user packages as the signed-in user, in its own window.</summary>
    public static DeElevatedLauncher.Path UpgradeAllForUser(string winget, ElevationInfo? elevation) =>
        DeElevatedLauncher.Open(winget, UpgradeAllArguments("user"), elevation);

    /// <summary>Only the two values winget documents; anything else is left out.</summary>
    private static string ScopeArgument(string? scope) => scope is "machine" or "user" ? $" --scope {scope}" : "";

    /// <summary>Only installer types winget documents; anything else is left out.</summary>
    private static string InstallerTypeArgument(string? type) =>
        type is "exe" or "msi" or "msix" or "inno" or "nullsoft" or "wix" or "burn" ? $" --installer-type {type}" : "";

    /// <summary>Installs a machine-scope app with progress lines. Returns winget's exit code (0 = success).</summary>
    public static Task<int> InstallAsync(string winget, AppEntry app, IProgress<string>? lines, CancellationToken ct)
    {
        if (!IsSafeId(app.Id)) throw new ArgumentException($"invalid winget id {app.Id}");
        if (app.Scope == "user") throw new InvalidOperationException("per-user apps are started with InstallForUser");
        return StreamingProcess.RunAsync(winget, InstallArguments(app.Id, app.WingetScope, app.InstallerType), lines, ct);
    }

    /// <summary>Starts a per-user installer as the signed-in user (window visible; the result shows on the next scan).</summary>
    public static DeElevatedLauncher.Path InstallForUser(string winget, AppEntry app, ElevationInfo? elevation)
    {
        if (!IsSafeId(app.Id)) throw new ArgumentException($"invalid winget id {app.Id}");
        return DeElevatedLauncher.Open(winget, InstallArguments(app.Id, app.WingetScope, app.InstallerType), elevation);
    }

    /// <summary>APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE: already installed, no newer version in the source.</summary>
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);

    /// <summary>winget's documented exit codes that are not failures for an install request.</summary>
    public static bool IsSuccess(int exitCode) => exitCode is 0
        or UpdateNotApplicable
        or unchecked((int)0x8A150109); // APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_FINISH

    /// <summary>APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER: for an install with --scope machine, the package has only a per-user installer.</summary>
    public const int NoApplicableInstaller = unchecked((int)0x8A150010);

    /// <summary>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND: a search found nothing.</summary>
    public const int NoPackagesFound = unchecked((int)0x8A150014);

    // ---------------- search ----------------

    /// <summary>A search goes into a command line: letters, digits, spaces and . - _ + only, 2 to 60 characters.</summary>
    public static bool IsSafeQuery(string query) =>
        query.Trim().Length is >= 2 and <= 60 && query.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '+');

    /// <summary>A substring search of name, id and moniker in the winget community repository (not the Store), at most 50 results.</summary>
    public static string SearchArguments(string query) =>
        IsSafeQuery(query) ? $"search --query \"{query.Trim()}\" --source winget --count 50 --accept-source-agreements --disable-interactivity"
        : throw new ArgumentException($"invalid search {query}");

    /// <summary>
    /// The table winget search prints: a header, a line of dashes, then one package per line in fixed-width columns. The
    /// headers are translated, so the columns are found by where the header words start; the first three are always name,
    /// id and version. Rows whose id winget shortened (ending in an ellipsis) or that hold anything but id characters are left out, and so
    /// are the notes below the table (empty lines are already gone when the lines come from <see cref="StreamingProcess"/>).
    /// </summary>
    public static IReadOnlyList<WingetSearchResult> ParseSearch(string output)
    {
        // Progress spinners are written with carriage returns: only the text after the last one is on screen.
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l[(l.LastIndexOf('\r') + 1)..]).ToList();
        var dashes = lines.FindIndex(l => l.Trim() is { Length: >= 10 } t && t.All(c => c == '-'));
        if (dashes < 1) return [];
        // winget pads its columns by display width: a Chinese or Japanese character takes two cells, so the columns are
        // measured in cells, not characters (a name in CJK would shift every column after it).
        var header = lines[dashes - 1];
        var starts = Enumerable.Range(0, header.Length).Where(i => header[i] != ' ' && (i == 0 || header[i - 1] == ' ')).Select(i => Cells(header, i)).ToList();
        if (starts.Count < 3) return [];
        var list = new List<WingetSearchResult>();
        foreach (var line in lines.Skip(dashes + 1))
        {
            if (string.IsNullOrWhiteSpace(line)) break;
            string Cut(int from, int to)
            {
                var (a, b) = (IndexAtCell(line, from), IndexAtCell(line, to));
                return a >= line.Length ? "" : line[a..b].Trim();
            }
            var name = Cut(0, starts[1]);
            var id = Cut(starts[1], starts[2]);
            var version = Cut(starts[2], starts.Count > 3 ? starts[3] : int.MaxValue);
            // Ids in the winget repository are Publisher.Package: a line without a dot is a note below the table.
            if (id.Contains('\u2026') || !IsSafeId(id) || !id.Contains('.') || name.Length == 0) continue;
            list.Add(new WingetSearchResult(name, id, version));
        }
        return list;
    }

    /// <summary>The display cells of <paramref name="text"/> before <paramref name="index"/>.</summary>
    private static int Cells(string text, int index)
    {
        var cells = 0;
        for (var i = 0; i < index && i < text.Length; i++) cells += CellWidth(text[i]);
        return cells;
    }

    /// <summary>The character index where cell <paramref name="cell"/> starts (the length when the line is shorter).</summary>
    private static int IndexAtCell(string text, int cell)
    {
        var cells = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (cells >= cell) return i;
            cells += CellWidth(text[i]);
        }
        return text.Length;
    }

    /// <summary>East Asian wide and full-width characters take two cells, the second half of a surrogate pair none.</summary>
    private static int CellWidth(char c) => c switch
    {
        >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or >= '豈' and <= '﫿'
            or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦' => 2,
        _ when char.IsHighSurrogate(c) => 2,
        _ when char.IsLowSurrogate(c) => 0,
        _ => 1,
    };

    /// <summary>
    /// Runs the search with the trusted winget; the packages it found (empty when none). A failed search (the source
    /// could not be opened) throws with winget's own last line, instead of reading as "nothing found".
    /// </summary>
    public static async Task<IReadOnlyList<WingetSearchResult>> SearchAsync(string winget, string query, CancellationToken ct)
    {
        var output = new System.Text.StringBuilder();
        var code = await StreamingProcess.RunAsync(winget, SearchArguments(query), new SynchronousProgress(l => { lock (output) output.AppendLine(l); }), ct);
        string text;
        lock (output) text = output.ToString();
        var found = ParseSearch(text);
        if (found.Count == 0 && code is not (0 or NoPackagesFound))
            throw new InvalidOperationException(text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"winget 0x{code:X8}");
        return found;
    }

    /// <summary>Reports on the reading thread: the parse after the run must see every line.</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

/// <summary>One result of a winget search.</summary>
public sealed record WingetSearchResult(string Name, string Id, string Version)
{
    // What screen readers announce for this item in a list.
    public override string ToString() => Name;
}
