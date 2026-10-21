using System.Text.Json;
using System.Text.RegularExpressions;
using Optimizer.Core.Actions;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;

namespace Optimizer.Core.Apps;

/// <summary>The package managers and developer tools Install and update knows, in the order the page lists them.</summary>
public enum ManagerKind { Winget, Chocolatey, Scoop, Store, Npm, Pip, DotnetTools, Cargo }

/// <summary>What "Update all apps" does with a package manager.</summary>
public enum ManagerRun
{
    /// <summary>Runs in this (elevated) app with its output read: winget for the whole PC, Chocolatey from its checked folder.</summary>
    Elevated,

    /// <summary>Runs as the signed-in user in a window of its own (Scoop, winget found only as the user's alias).</summary>
    AsUser,

    /// <summary>The Microsoft Store's Downloads and updates page opens as the signed-in user.</summary>
    StorePage,

    /// <summary>Found, but left out: Chocolatey in a folder that users can change would run with administrator rights.</summary>
    NotRun,

    /// <summary>A developer tool: listed with a command to copy, never run by Tunero.</summary>
    Copy,
}

/// <summary>
/// A package manager found on this PC. <paramref name="Path"/> is the program Update all runs (winget.exe, choco.exe,
/// Scoop's scoop.ps1) or, for a developer tool, the file that showed it is installed.
/// </summary>
public sealed record PackageManagerInfo(ManagerKind Kind, string Name, string Path, ManagerRun Run)
{
    public string? Version { get; init; }

    /// <summary>winget for the signed-in user's own packages (the user's execution alias, or the package folder).</summary>
    public string? UserPath { get; init; }

    /// <summary>Chocolatey's root folder (ChocolateyInstall for the elevated run).</summary>
    public string? Root { get; init; }

    /// <summary>What to type in a terminal to update a developer tool's packages, one command per line.</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>The commands are written for Command Prompt (a quoted program path, which PowerShell would read as text).</summary>
    public bool CommandPrompt { get; init; }

    /// <summary>Chocolatey in a trusted folder, but older than <see cref="Apps.Chocolatey.MinimumVersion"/>: not run elevated.</summary>
    public bool TooOld { get; init; }

    public bool InUpdateAll => Run is ManagerRun.Elevated or ManagerRun.AsUser or ManagerRun.StorePage;

    public bool IsDeveloperTool => Kind >= ManagerKind.Npm;
}

/// <summary>
/// What detection reads, so tests can point it at a temp folder: the known folders, the signed-in user's profile, the
/// machine and user environment variables (from the registry, not this process's environment), the winget finders, the
/// Store package, the folder trust check and file versions. Files are checked with File.Exists and Directory.Exists.
/// </summary>
public sealed class ManagerSystem
{
    public required string ProgramFiles { get; init; }
    public required string ProgramData { get; init; }
    public required string Windows { get; init; }

    /// <summary>The signed-in user's profile folder (not the elevated account's); null when unknown.</summary>
    public string? ProfilePath { get; init; }

    public string? WingetTrusted { get; init; }
    public string? WingetForUser { get; init; }

    /// <summary>The installed Microsoft.WindowsStore package's version, or null when it is not installed.</summary>
    public Func<string?> StoreVersion { get; init; } = () => null;

    /// <summary>A machine variable (HKLM Session Manager\Environment), expanded with the machine values.</summary>
    public Func<string, string?> MachineVariable { get; init; } = _ => null;

    /// <summary>A variable of the signed-in user (HKCU\Environment of the session user), expanded for that user.</summary>
    public Func<string, string?> UserVariable { get; init; } = _ => null;

    /// <summary>Whether a program may run elevated (<see cref="TrustedPath.IsAdminOnlyBelowSharedFolders"/>).</summary>
    public Func<string, bool> IsAdminOnly { get; init; } = TrustedPath.IsAdminOnlyBelowSharedFolders;

    /// <summary>Whether an elevated program may load code from a folder (<see cref="TrustedPath.IsAdminOnlyFolderBelowSharedFolders"/>).</summary>
    public Func<string, bool> IsAdminOnlyFolder { get; init; } = TrustedPath.IsAdminOnlyFolderBelowSharedFolders;

    /// <summary>The product version a file carries in its version resource (read, never started).</summary>
    public Func<string, string?> FileVersion { get; init; } = ReadFileVersion;

    public static string? ReadFileVersion(string path)
    {
        try
        {
            return CleanVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"10.0.13 @Commit: 4099f83" and "6.0.0+abc" -> the version alone; empty -> null.</summary>
    public static string? CleanVersion(string? productVersion) =>
        productVersion?.Trim().Split(' ', '+')[0] is { Length: > 0 } v ? v : null;

    /// <summary>This PC as the elevated app sees it, for the signed-in user of <paramref name="registry"/> (Hive.User).</summary>
    public static ManagerSystem ForThisPc(IRegistryRoots registry, string? profilePath, string? wingetTrusted, string? wingetForUser) => new()
    {
        ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ProfilePath = profilePath,
        WingetTrusted = wingetTrusted,
        WingetForUser = wingetForUser,
        StoreVersion = () => PackageManagers.InstalledStoreVersion()?.ToString(),
        MachineVariable = name => Variable(registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", name, PathExpander.Machine.Expand),
        UserVariable = name => Variable(registry, Hive.User, "Environment", name, PathExpander.ForUser(profilePath, registry).Expand),
    };

    private static string? Variable(IRegistryRoots registry, Hive hive, string path, string name, Func<string, string> expand)
    {
        try
        {
            using var key = registry.Open(hive, path, writable: false);
            return key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string { Length: > 0 } raw ? expand(raw) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// Finds the package managers for Install and update: winget, Chocolatey, Scoop and the Microsoft Store, which "Update all
/// apps" runs, and developer tools (npm, pip, .NET global tools, cargo), which it lists with a command to copy. Detection
/// only checks that files exist and reads version resources and small text files; it starts nothing, because programs in
/// the user's folders could be anything and this app runs with administrator rights.
/// </summary>
public static partial class PackageManagers
{
    /// <summary>
    /// The Store's Downloads and updates page (https://learn.microsoft.com/windows/apps/develop/launch/launch-store-app).
    /// The documented way to start Store app updates without the Store, the MDM bridge's
    /// MDM_EnterpriseModernAppManagement_AppManagement01.UpdateScanMethod, must run as Local System ("The following
    /// script must be executed under local system user",
    /// https://learn.microsoft.com/windows/client-management/using-powershell-scripting-with-the-wmi-bridge-provider),
    /// which an admin process is not: the page opens for the signed-in user, who selects Get updates there.
    /// </summary>
    public const string StoreUpdatesLink = "ms-windows-store://downloadsandupdates";

    public static IReadOnlyList<PackageManagerInfo> Detect(ManagerSystem system)
    {
        var found = new List<PackageManagerInfo?>
        {
            Winget(system), Chocolatey(system), Scoop(system), Store(system),
            Npm(system), Pip(system), DotnetTools(system), Cargo(system),
        };
        return found.OfType<PackageManagerInfo>().ToList();
    }

    /// <summary>What Update all runs, in this order: winget, Chocolatey, Scoop, then the Store page.</summary>
    public static IReadOnlyList<PackageManagerInfo> UpdateAllPlan(IEnumerable<PackageManagerInfo> found) =>
        found.Where(m => m.InUpdateAll && !m.IsDeveloperTool).OrderBy(m => m.Kind).ToList();

    private static PackageManagerInfo? Winget(ManagerSystem s)
    {
        if (s.WingetTrusted is { } trusted)
            return new(ManagerKind.Winget, "winget", trusted, ManagerRun.Elevated)
            {
                UserPath = s.WingetForUser,
                Version = Apps.Winget.PackageVersion(Path.GetFileName(Path.GetDirectoryName(trusted)) ?? "")?.ToString(),
            };
        return s.WingetForUser is { } user ? new(ManagerKind.Winget, "winget", user, ManagerRun.AsUser) : null;
    }

    /// <summary>
    /// choco.exe in the folder ChocolateyInstall names (the machine variable, then the user's, as UniGetUI sets it), or in
    /// ProgramData\chocolatey. The first install that passes the trust check runs elevated, as for Tunero's own Chocolatey
    /// update (<see cref="PackageInstall.TrustedChocolatey"/>): choco.exe and the folders it loads code from
    /// (<see cref="Apps.Chocolatey.IsTrustedInstall"/>). A user's variable cannot hide a trusted install that way; with
    /// none trusted, the first one found is listed as not included.
    /// </summary>
    private static PackageManagerInfo? Chocolatey(ManagerSystem s)
    {
        // Without the user's profile the user's variable would be expanded with this (elevated) account's values.
        var user = s.ProfilePath is null ? null : s.UserVariable("ChocolateyInstall");
        var found = new[] { s.MachineVariable("ChocolateyInstall"), user, Path.Combine(s.ProgramData, "chocolatey") }
            .OfType<string>()
            .Where(Path.IsPathFullyQualified)
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => File.Exists(Path.Combine(root, "choco.exe")))
            .ToList();
        if (found.Count == 0) return null;
        var trusted = found.FirstOrDefault(root => Apps.Chocolatey.IsTrustedInstall(root, s.IsAdminOnly, s.IsAdminOnlyFolder));
        var root = trusted ?? found[0];
        var choco = Path.Combine(root, "choco.exe");
        var version = s.FileVersion(choco);
        // Every elevated run ignores Chocolatey's HTTP cache, an option that came with 2.1.0 (Apps.Chocolatey.IgnoreHttpCache).
        var tooOld = trusted is not null && !Apps.Chocolatey.CanIgnoreHttpCache(version);
        return new(ManagerKind.Chocolatey, "Chocolatey", choco, trusted is not null && !tooOld ? ManagerRun.Elevated : ManagerRun.NotRun)
        {
            Root = root,
            Version = version,
            TooOld = tooOld,
        };
    }

    /// <summary>
    /// Scoop of the signed-in user: the folder in the user's SCOOP variable, else scoop in the profile. Its version comes
    /// from the changelog of the Scoop checkout (apps\scoop\current); Scoop itself is never started from here.
    /// </summary>
    private static PackageManagerInfo? Scoop(ManagerSystem s)
    {
        // Without the user's profile, the user's SCOOP value would be expanded with this (elevated) account's values.
        if (s.ProfilePath is not { } profile) return null;
        var root = s.UserVariable("SCOOP") ?? Path.Combine(profile, "scoop");
        if (!Path.IsPathFullyQualified(root)) return null;
        var shim = Path.Combine(root, "shims", "scoop.ps1");
        if (!File.Exists(shim)) return null;
        return new(ManagerKind.Scoop, "Scoop", shim, ManagerRun.AsUser) { Version = ScoopVersion(Path.Combine(root, @"apps\scoop\current\CHANGELOG.md")) };
    }

    private static string? ScoopVersion(string changelog)
    {
        try
        {
            if (!File.Exists(changelog)) return null;
            foreach (var line in File.ReadLines(changelog).Take(200))
                if (ChangelogVersion().Match(line) is { Success: true } m) return m.Groups[1].Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static PackageManagerInfo? Store(ManagerSystem s) =>
        s.StoreVersion() is { } version ? new(ManagerKind.Store, "Microsoft Store", StoreUpdatesLink, ManagerRun.StorePage) { Version = version } : null;

    /// <summary>npm of a Node.js installed in Program Files; its version from npm's package.json.</summary>
    private static PackageManagerInfo? Npm(ManagerSystem s)
    {
        var node = Path.Combine(s.ProgramFiles, "nodejs");
        var npm = Path.Combine(node, "npm.cmd");
        if (!File.Exists(npm)) return null;
        return new(ManagerKind.Npm, "npm", npm, ManagerRun.Copy) { Version = JsonVersion(Path.Combine(node, @"node_modules\npm\package.json")), Commands = ["npm update -g"] };
    }

    /// <summary>
    /// Python: the py launcher (in Windows for every user, or in the user's profile) and installs in Program Files or the
    /// user's Programs\Python folder. The command lists outdated packages only: the user picks what to update.
    /// </summary>
    private static PackageManagerInfo? Pip(ManagerSystem s)
    {
        var launchers = new List<string> { Path.Combine(s.Windows, "py.exe") };
        var pythonRoots = new List<string> { s.ProgramFiles };
        if (s.ProfilePath is { } profile)
        {
            launchers.Add(Path.Combine(profile, @"AppData\Local\Programs\Python\Launcher\py.exe"));
            pythonRoots.Add(Path.Combine(profile, @"AppData\Local\Programs\Python"));
        }
        var newest = pythonRoots.SelectMany(PythonFolders)
            .Select(d => (Exe: Path.Combine(d.Folder, "python.exe"), d.Minor))
            .Where(p => File.Exists(p.Exe))
            .OrderByDescending(p => p.Minor)
            .Select(p => p.Exe)
            .FirstOrDefault();
        var launcher = launchers.FirstOrDefault(File.Exists);
        if (launcher is null && newest is null) return null;
        // Without the launcher the command names python.exe by its path, in quotes: Command Prompt runs that as it is.
        var command = launcher is not null ? "py -m pip list --outdated" : $"\"{newest}\" -m pip list --outdated";
        return new(ManagerKind.Pip, "pip", newest ?? launcher!, ManagerRun.Copy)
        {
            Version = newest is null ? null : s.FileVersion(newest),
            Commands = [command],
            CommandPrompt = launcher is null,
        };
    }

    /// <summary>Python3NN folders (python.org's names) with their minor version for the order.</summary>
    private static IEnumerable<(string Folder, int Minor)> PythonFolders(string root)
    {
        string[] folders;
        try
        {
            folders = Directory.Exists(root) ? Directory.GetDirectories(root, "Python3*") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folders = [];
        }
        foreach (var folder in folders)
            if (int.TryParse(Path.GetFileName(folder)["Python3".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor))
                yield return (folder, minor);
    }

    /// <summary>
    /// .NET global tools of the signed-in user (profile\.dotnet\tools\.store holds one folder per tool id), with the
    /// dotnet of Program Files. Listed only when there is a tool to update.
    /// </summary>
    private static PackageManagerInfo? DotnetTools(ManagerSystem s)
    {
        var dotnet = Path.Combine(s.ProgramFiles, "dotnet", "dotnet.exe");
        if (s.ProfilePath is not { } profile || !File.Exists(dotnet)) return null;
        var tools = SubfolderNames(Path.Combine(profile, @".dotnet\tools\.store")).Where(IsSafeName).Order(StringComparer.Ordinal).ToList();
        if (tools.Count == 0) return null;
        return new(ManagerKind.DotnetTools, ".NET tools", dotnet, ManagerRun.Copy)
        {
            Version = s.FileVersion(dotnet),
            Commands = tools.Select(t => $"dotnet tool update -g {t}").ToList(),
        };
    }

    /// <summary>
    /// cargo of the signed-in user: with cargo-update installed one command updates every crate; otherwise cargo install
    /// per crate from crates.io (it installs a crate again only when a newer version exists). Crates from git or a local
    /// path are left out: cargo install by name would fetch a different crate from crates.io.
    /// </summary>
    private static PackageManagerInfo? Cargo(ManagerSystem s)
    {
        if (s.ProfilePath is not { } profile) return null;
        var home = Path.Combine(profile, ".cargo");
        var cargo = Path.Combine(home, @"bin\cargo.exe");
        if (!File.Exists(cargo)) return null;
        IReadOnlyList<string> commands = File.Exists(Path.Combine(home, @"bin\cargo-install-update.exe"))
            ? ["cargo install-update -a"]
            : RegistryCrates(Path.Combine(home, ".crates2.json")).Select(c => $"cargo install {c}").ToList();
        return commands.Count == 0 ? null : new(ManagerKind.Cargo, "cargo", cargo, ManagerRun.Copy) { Commands = commands };
    }

    /// <summary>The two ways cargo names crates.io as a crate's source (the git index and the sparse index).</summary>
    private static readonly string[] CratesIo = ["(registry+https://github.com/rust-lang/crates.io-index)", "(sparse+https://index.crates.io/)"];

    /// <summary>The crates.io crates in cargo's install record (keys "name version (source)"); other registries are left out.</summary>
    private static IEnumerable<string> RegistryCrates(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("installs", out var installs) || installs.ValueKind != JsonValueKind.Object) return [];
            return installs.EnumerateObject()
                .Select(p => p.Name.Split(' ', 3))
                .Where(p => p.Length == 3 && CratesIo.Contains(p[2], StringComparer.Ordinal) && IsSafeName(p[0]))
                .Select(p => p[0])
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>A tool or crate name shown in a command to copy: letters, digits, . - _ only.</summary>
    private static bool IsSafeName(string name) => name.Length is > 0 and < 100 && char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static IEnumerable<string> SubfolderNames(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>().ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? JsonVersion(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ---------------- Scoop and the Store ----------------

    /// <summary>
    /// PowerShell arguments for Scoop's window, started as the signed-in user (never elevated): Scoop updates itself and
    /// its buckets, then every app, and the window waits with <paramref name="doneText"/> so its output stays readable.
    /// scoop.ps1 is a script, which the default execution policy would refuse: the policy is lifted for this PowerShell only.
    /// </summary>
    public static string ScoopUpdateArguments(string scoopPs1, string doneText)
    {
        var scoop = WingetPackage.Quote(scoopPs1);
        return PowerShellScript.Command($"& {scoop} update; & {scoop} update *; Read-Host {WingetPackage.Quote(doneText)}", interactive: true, bypassPolicy: true);
    }

    /// <summary>"Microsoft.WindowsStore_22608.1401.7.0_x64__8wekyb3d8bbwe" -> 22608.1401.7.0; bundles and other packages -> null.</summary>
    public static Version? StorePackageVersion(string package)
    {
        var parts = package.Split('_');
        if (parts.Length != 5 || parts[0] != "Microsoft.WindowsStore" || parts[3].Length != 0 || parts[4] != "8wekyb3d8bbwe" || parts[2] is not ("x64" or "arm64" or "x86")) return null;
        return Version.TryParse(parts[1], out var v) ? v : null;
    }

    /// <summary>The Store package registered for the machine (HKLM, admin-only) with its folder under Program Files\WindowsApps.</summary>
    public static Version? InstalledStoreVersion()
    {
        var protectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
        return Apps.Winget.ReadPackageFolders("Microsoft.WindowsStore_")
            .Where(p => p.Folder.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(p.Folder))
            .Select(p => StorePackageVersion(p.Package))
            .OfType<Version>()
            .OrderDescending()
            .FirstOrDefault();
    }

    // ---------------- demo ----------------

    /// <summary>Fixed results for screenshots (--pm-demo): every manager found and some developer tools. Nothing is read.</summary>
    public static IReadOnlyList<PackageManagerInfo> Demo() =>
    [
        new(ManagerKind.Winget, "winget", @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe", ManagerRun.Elevated)
        {
            Version = "1.29.380.0", UserPath = @"C:\Users\Demo\AppData\Local\Microsoft\WindowsApps\winget.exe",
        },
        new(ManagerKind.Chocolatey, "Chocolatey", @"C:\ProgramData\chocolatey\choco.exe", ManagerRun.Elevated) { Version = "2.4.1", Root = @"C:\ProgramData\chocolatey" },
        new(ManagerKind.Scoop, "Scoop", @"C:\Users\Demo\scoop\shims\scoop.ps1", ManagerRun.AsUser) { Version = "0.5.3" },
        new(ManagerKind.Store, "Microsoft Store", StoreUpdatesLink, ManagerRun.StorePage) { Version = "22608.1401.7.0" },
        new(ManagerKind.Npm, "npm", @"C:\Program Files\nodejs\npm.cmd", ManagerRun.Copy) { Version = "10.9.2", Commands = ["npm update -g"] },
        new(ManagerKind.Pip, "pip", @"C:\Program Files\Python313\python.exe", ManagerRun.Copy) { Version = "3.13.1", Commands = ["py -m pip list --outdated"] },
        new(ManagerKind.DotnetTools, ".NET tools", @"C:\Program Files\dotnet\dotnet.exe", ManagerRun.Copy)
        {
            Version = "10.0.0", Commands = ["dotnet tool update -g csharpier", "dotnet tool update -g dotnet-ef"],
        },
    ];

    [GeneratedRegex(@"^##\s*\[v?(\d+(?:\.\d+)+)\]")]
    private static partial Regex ChangelogVersion();
}
