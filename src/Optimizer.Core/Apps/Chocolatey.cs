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
    /// The outdated packages, one per line as "id|current|available|pinned" (--limit-output); pinned packages are left
    /// out. A read: nothing is installed.
    /// </summary>
    public const string OutdatedArguments = "outdated --limit-output --ignore-pinned";

    /// <summary>
    /// Every outdated package, without questions or progress percentages. Tunero's own package is left out: its exe is
    /// running and cannot be replaced, and it updates through Update now in Settings.
    /// </summary>
    public const string UpgradeAllArguments = "upgrade all --yes --no-progress --except=" + Updates.PackageInstall.ChocolateyId;

    /// <summary>One package from the update list.</summary>
    public static string UpgradeArguments(string id) =>
        IsSafeId(id) && !IsTunero(id) ? $"upgrade {id} --yes --no-progress" : throw new ArgumentException($"invalid Chocolatey id {id}");

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
    /// The lines of choco outdated --limit-output that are packages. Pinned packages, ids that are not safe to pass on and
    /// Tunero itself (updated through Update now) are left out, and so is everything else Chocolatey prints.
    /// </summary>
    public static IReadOnlyList<ChocolateyOutdated> ParseOutdated(string output)
    {
        var list = new List<ChocolateyOutdated>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 4 || !IsSafeId(parts[0]) || IsTunero(parts[0]) || parts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[1].Length == 0 || parts[2].Length == 0) continue;
            list.Add(new ChocolateyOutdated(parts[0], parts[1].Trim(), parts[2].Trim()));
        }
        return list;
    }

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
