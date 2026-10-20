namespace Optimizer.Core.Apps;

/// <summary>One package Chocolatey has a newer version for (choco outdated).</summary>
public sealed record ChocolateyOutdated(string Id, string Current, string Available)
{
    // What screen readers announce for this item in a list.
    public override string ToString() => Id;
}

/// <summary>
/// Chocolatey's command lines for Install and update. choco.exe runs elevated, and only from the folder the trust check
/// accepted (<see cref="PackageManagers"/>), with the environment of <see cref="HardenEnvironment"/>. Options as documented
/// at https://docs.chocolatey.org/en-us/choco/commands/upgrade/ and .../outdated/.
/// </summary>
public static class Chocolatey
{
    /// <summary>
    /// Every run Tunero starts ignores Chocolatey's HTTP cache. Elevated, choco keeps it in C:\ProgramData\ChocolateyHttpCache
    /// (https://docs.chocolatey.org/en-us/choco/features/http-caching/), outside the root the trust check covers, where
    /// users can create the folder or add files: cached source answers planted there would steer an elevated run.
    /// "--ignore-http-cache: Ignore any HTTP caches that have previously been created when querying sources, and create
    /// new caches. Available in 2.1.0+" (https://docs.chocolatey.org/en-us/choco/commands/upgrade/ and .../outdated/).
    /// </summary>
    public const string IgnoreHttpCache = "--ignore-http-cache";

    /// <summary>The first Chocolatey CLI with <see cref="IgnoreHttpCache"/>; an older one is not run elevated.</summary>
    public static readonly Version MinimumVersion = new(2, 1, 0);

    /// <summary>
    /// The outdated packages, one per line as "id|current|available|pinned" (--limit-output); pinned packages are left
    /// out. A read: nothing is installed.
    /// </summary>
    public const string OutdatedArguments = "outdated --limit-output --ignore-pinned " + IgnoreHttpCache;

    /// <summary>
    /// Every outdated package, without questions or progress percentages. Tunero's own package is left out: its exe is
    /// running and cannot be replaced, and it updates through Update now in Settings.
    /// </summary>
    public const string UpgradeAllArguments = "upgrade all --yes --no-progress --except=" + Updates.PackageInstall.ChocolateyId + " " + IgnoreHttpCache;

    /// <summary>One package from the update list.</summary>
    public static string UpgradeArguments(string id) =>
        IsSafeId(id) && !IsTunero(id) ? $"upgrade {id} --yes --no-progress {IgnoreHttpCache}" : throw new ArgumentException($"invalid Chocolatey id {id}");

    /// <summary>
    /// Whether choco.exe's product version has <see cref="IgnoreHttpCache"/>: 2.1.0 or newer. A pre-release of 2.1.0 counts
    /// as older (semantic versioning); an unknown version is not trusted with it.
    /// </summary>
    public static bool CanIgnoreHttpCache(string? productVersion)
    {
        var text = productVersion?.Trim().Split(' ', '+')[0] ?? "";
        var dash = text.IndexOf('-');
        var prerelease = dash >= 0;
        if (!Version.TryParse(prerelease ? text[..dash] : text, out var v)) return false;
        var normalized = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        return normalized > MinimumVersion || (normalized == MinimumVersion && !prerelease);
    }

    /// <summary>
    /// Ids go into a command line: letters, digits and . - _ + only, starting with a letter or digit (so never an option).
    /// Like <see cref="Winget.IsSafeId"/>, but Chocolatey ids can be one or two characters long (jq).
    /// </summary>
    public static bool IsSafeId(string id) => id.Length is > 0 and < 100 && char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    private static bool IsTunero(string id) => id.Equals(Updates.PackageInstall.ChocolateyId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Success for upgrade and outdated: 0; 2 with enhanced exit codes (nothing to upgrade, or outdated packages found);
    /// 1641 and 3010 when an installer started or asks for a restart.
    /// </summary>
    public static bool IsSuccess(int exitCode) => exitCode is 0 or 2 or 1641 or 3010;

    /// <summary>
    /// What choco outdated found: the packages, or null when Chocolatey could not check. That is a run that failed, and also
    /// one that could not reach a source and found no update: choco then still lists every package with the installed
    /// version as the newest and exits with 0 (2.7.4 with the network off, PCO-Test 2026-10-19). Updates another source
    /// found are shown even when one source did not answer.
    /// </summary>
    public static IReadOnlyList<ChocolateyOutdated>? OutdatedResult(int exitCode, string output)
    {
        if (!IsSuccess(exitCode)) return null;
        var list = ParseOutdated(output);
        return list.Count == 0 && CouldNotReachSource(output) ? null : list;
    }

    /// <summary>Chocolatey's messages when a package source does not answer.</summary>
    private static bool CouldNotReachSource(string output) =>
        output.Contains("Unable to connect to source", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Unable to load the service index", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lines of choco outdated --limit-output that are packages. Pinned packages, ids that are not safe to pass on and
    /// Tunero itself (updated through Update now) are left out, and so are lines whose newest version is the installed one
    /// and everything else Chocolatey prints.
    /// </summary>
    public static IReadOnlyList<ChocolateyOutdated> ParseOutdated(string output)
    {
        var list = new List<ChocolateyOutdated>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 4 || !IsSafeId(parts[0]) || IsTunero(parts[0]) || parts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            var (current, available) = (parts[1].Trim(), parts[2].Trim());
            if (current.Length == 0 || available.Length == 0 || current == available) continue;
            list.Add(new ChocolateyOutdated(parts[0], current, available));
        }
        return list;
    }

    /// <summary>
    /// The folders under Chocolatey's root that an elevated choco.exe loads code or settings from: its PowerShell helpers,
    /// installed extensions (modules imported into every run) and hooks (scripts run before and after each package), its
    /// configuration (package sources), the packages' own scripts in lib, the tools it calls (7-Zip, shimgen) and the shims
    /// and redirects in bin and redirects.
    /// </summary>
    public static readonly IReadOnlyList<string> CodeFolders = ["helpers", "extensions", "hooks", "config", "lib", "tools", "bin", "redirects"];

    /// <summary>
    /// Whether choco.exe in <paramref name="root"/> may run elevated: the exe passes <paramref name="isAdminOnlyFile"/>, and
    /// the root and every one of <see cref="CodeFolders"/> that exists pass <paramref name="isAdminOnlyFolder"/> (a folder
    /// that does not exist cannot be created by users once the root is admin-only). The packages' folders inside lib keep
    /// lib's permissions unless an installer changed them; they are not walked one by one.
    /// </summary>
    public static bool IsTrustedInstall(string root, Func<string, bool> isAdminOnlyFile, Func<string, bool> isAdminOnlyFolder) =>
        isAdminOnlyFile(Path.Combine(root, "choco.exe")) && isAdminOnlyFolder(root) &&
        CodeFolders.Select(f => Path.Combine(root, f)).Where(Directory.Exists).All(isAdminOnlyFolder);

    /// <summary>
    /// The environment of an elevated choco.exe (after <see cref="Platform.ProcessHardening.Apply"/>): choco takes its root,
    /// and with it its configuration (package sources), PowerShell helpers and extensions, from ChocolateyInstall before
    /// its own folder, and a user can set that variable and the other Chocolatey* ones for their account. All of them are
    /// removed, and ChocolateyInstall points to the checked folder.
    /// </summary>
    public static void HardenEnvironment(IDictionary<string, string?> env, string root)
    {
        foreach (var name in env.Keys.Where(k => k.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)).ToList()) env.Remove(name);
        env["ChocolateyInstall"] = root;
    }
}
