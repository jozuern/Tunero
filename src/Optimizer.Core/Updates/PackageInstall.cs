using Optimizer.Core.Platform;

namespace Optimizer.Core.Updates;

public enum PackageManager { Winget, Chocolatey, Scoop }

/// <summary>
/// Tunero installed by a package manager: updates and the uninstall go through it, so its record of the installed version
/// stays right, instead of the app replacing or deleting its own exe. winget and a Scoop install in the user's profile
/// run as the signed-in user; Chocolatey runs elevated, and only from a folder that only administrators can change.
/// </summary>
public sealed record PackageInstall(PackageManager Manager, string Name, string Folder, string Exe, bool Global = false)
{
    /// <summary>The package id on the Chocolatey community repository and the app name in the Tunero Scoop bucket.</summary>
    public const string ChocolateyId = "tunero";
    public const string ScoopName = "tunero";

    public string DisplayName => Manager switch
    {
        PackageManager.Winget => "winget",
        PackageManager.Chocolatey => "Chocolatey",
        _ => "Scoop",
    };

    /// <summary>What to type in a terminal, shown when the package manager cannot be started from the app.</summary>
    public string UpdateCommand => Manager switch
    {
        PackageManager.Winget => $"winget upgrade {WingetPackage.Id}",
        PackageManager.Chocolatey => $"choco upgrade {ChocolateyId}",
        _ => Global ? $"scoop update {ScoopName} --global" : $"scoop update {ScoopName}",
    };

    public string UninstallCommand => Manager switch
    {
        PackageManager.Winget => $"winget uninstall {WingetPackage.Id}",
        PackageManager.Chocolatey => $"choco uninstall {ChocolateyId}",
        _ => Global ? $"scoop uninstall {ScoopName} --global" : $"scoop uninstall {ScoopName}",
    };

    /// <summary>
    /// The package manager this exe belongs to, or null for a downloaded copy. The folder layouts:
    /// winget: ...\jozuern.Tunero_&lt;source&gt;\Tunero.exe (<see cref="WingetPackage.PackageFolder"/>);
    /// Chocolatey: &lt;root&gt;\lib\tunero\tools\Tunero.exe; Scoop: &lt;root&gt;\apps\tunero\&lt;version or current&gt;\Tunero.exe,
    /// where a root outside the user's profile is a global install (scoop install --global).
    /// </summary>
    public static PackageInstall? Detect(string exePath, string? profilePath = null)
    {
        if (WingetPackage.PackageFolder(exePath) is { } wingetFolder) return new(PackageManager.Winget, wingetFolder, Path.GetDirectoryName(exePath)!, exePath);
        string full;
        try
        {
            full = Path.GetFullPath(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        if (Is(dir, "tools") && Is(dir.Parent, ChocolateyId) && Is(dir.Parent?.Parent, "lib") && dir.Parent!.Parent!.Parent is { } chocoRoot)
            return new(PackageManager.Chocolatey, ChocolateyId, chocoRoot.FullName, full);
        if (dir.Parent is { } app && Is(app, ScoopName) && Is(app.Parent, "apps") && app.Parent!.Parent is { } scoopRoot)
        {
            var global = profilePath is null || !IsBelow(scoopRoot.FullName, profilePath);
            return new(PackageManager.Scoop, ScoopName, app.FullName, full, global);
        }
        return null;
    }

    private static bool Is(DirectoryInfo? dir, string name) => dir is not null && dir.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Started as the signed-in user (winget, Scoop in the user's profile) instead of elevated (Chocolatey).</summary>
    public bool RunsAsUser => Manager != PackageManager.Chocolatey;

    /// <summary>
    /// choco.exe for the elevated run: only &lt;root&gt;\choco.exe, and only when the file and its folder can be changed by
    /// administrators alone and no folder above lets anyone else rename or replace what is in it (Chocolatey's installer
    /// locks its folder this way; C:\ProgramData above it only lets users add new folders,
    /// <see cref="TrustedPath.IsAdminOnlyBelowSharedFolders"/>). A Chocolatey in a user's folder (as UniGetUI installs it)
    /// is not run: the app shows the command instead. A global Scoop install never runs from the app: Scoop itself lives
    /// in the user's profile, where the user could change it.
    /// </summary>
    public string? TrustedChocolatey(Func<string, bool>? isAdminOnly = null)
    {
        if (Manager != PackageManager.Chocolatey) return null;
        var choco = Path.Combine(Folder, "choco.exe");
        // The elevated window starts this exe again after the update, without a prompt: it must be just as protected.
        var check = isAdminOnly ?? TrustedPath.IsAdminOnlyBelowSharedFolders;
        return check(choco) && check(Exe) ? choco : null;
    }

    /// <summary>
    /// The environment of the elevated Chocolatey window (after <see cref="ProcessHardening.Apply"/>): every Chocolatey*
    /// variable a user could set is removed and ChocolateyInstall points to the checked folder (<see cref="Apps.Chocolatey.HardenEnvironment"/>).
    /// </summary>
    public void HardenChocolateyEnvironment(IDictionary<string, string?> env)
    {
        if (Manager != PackageManager.Chocolatey) return;
        Apps.Chocolatey.HardenEnvironment(env, Folder);
    }

    /// <summary>The package manager can be started from the app (else the app shows the command to type).</summary>
    public bool CanRunFromApp(Func<string, bool>? isAdminOnly = null) => Manager switch
    {
        PackageManager.Chocolatey => TrustedChocolatey(isAdminOnly) is not null,
        PackageManager.Scoop => !Global,
        _ => true,
    };

    /// <summary>
    /// The exe to start after the update: Scoop installs every version in its own folder, and "current" points to the
    /// newest one; the other package managers replace the exe in place.
    /// </summary>
    public string ExeAfterUpdate(string exePath) =>
        Manager == PackageManager.Scoop ? Path.Combine(Folder, "current", Path.GetFileName(exePath)) : exePath;

    /// <summary>
    /// PowerShell arguments for the update: wait until this process has ended (no package manager can replace a running
    /// exe), run the package manager, keep the window open with <paramref name="failedText"/> when it did not succeed,
    /// then start Tunero again. From the signed-in user's window (winget, Scoop) Windows asks for administrator rights as
    /// on any start; Chocolatey's window is elevated and starts it elevated, which is why <see cref="TrustedChocolatey"/>
    /// also requires the exe to be admin-only. Chocolatey and Scoop end without an error when their repository does not
    /// offer the new version yet (Chocolatey's moderation takes days): with <paramref name="expectedVersion"/> the window
    /// also checks the exe's version, so that case shows <paramref name="failedText"/> instead of quietly starting the old
    /// version again. winget reports it with an exit code.
    /// </summary>
    public string UpdateArguments(int processId, string exePath, string runningText, string failedText, string? choco = null, string? expectedVersion = null)
    {
        if (Manager == PackageManager.Winget) return WingetPackage.UpdateArguments(processId, exePath, runningText, failedText);
        var command = Manager == PackageManager.Chocolatey
            ? $"& {WingetPackage.Quote(choco ?? throw new ArgumentNullException(nameof(choco)))} upgrade {ChocolateyId} --yes --no-progress"
            : $"scoop update {ScoopName}";
        var exe = WingetPackage.Quote(ExeAfterUpdate(exePath));
        var versionCheck = expectedVersion is null ? null
            : $"$v = (Get-Item -LiteralPath {exe} -ErrorAction SilentlyContinue).VersionInfo.FileVersion; " +
              $"$ok = $ok -and $v -and ([version]$v -ge [version]{WingetPackage.Quote(System.Version.Parse(expectedVersion).ToString())})";
        return Window(
            $"Write-Host {WingetPackage.Quote(runningText)}; " +
            $"Wait-Process -Id {processId} -Timeout 60 -ErrorAction SilentlyContinue; " +
            Run(command, failedText, versionCheck) +
            $"; Start-Process -FilePath {exe}");
    }

    /// <summary>
    /// The window's command line. Scoop is a PowerShell script (scoop.ps1), which the default execution policy (Restricted)
    /// would refuse: the policy is lifted for this one PowerShell process only. Chocolatey and winget are exe files and
    /// need no exception.
    /// </summary>
    private string Window(string script) => PowerShellScript.Command(script, interactive: true, bypassPolicy: Manager == PackageManager.Scoop);

    /// <summary>
    /// Runs the command and keeps the window open with <paramref name="failedText"/> when it failed. $LASTEXITCODE starts at
    /// 0, because a script that sets no exit code leaves it unset (null, which is not 0); $? is read right after the command,
    /// before anything else can reset it. <paramref name="check"/> can turn $ok false afterwards.
    /// </summary>
    private static string Run(string command, string failedText, string? check = null) =>
        $"$global:LASTEXITCODE = 0; {command}; $ok = $? -and $LASTEXITCODE -eq 0; " +
        (check is null ? "" : check + "; ") +
        $"if (-not $ok) {{ Read-Host {WingetPackage.Quote(failedText)} }}";

    /// <summary>
    /// PowerShell arguments for the uninstall: wait until every process named <paramref name="processName"/> has ended,
    /// run the package manager and keep the window open with <paramref name="failedText"/> when it did not succeed. The data
    /// folder is removed by the app's own cleanup (<see cref="SelfUninstall"/>), as for a downloaded copy.
    /// </summary>
    public string UninstallArguments(string processName, string runningText, string failedText, string? choco = null)
    {
        if (Manager == PackageManager.Winget) return WingetPackage.UninstallArguments(Name, processName, runningText, failedText);
        var command = Manager == PackageManager.Chocolatey
            ? $"& {WingetPackage.Quote(choco ?? throw new ArgumentNullException(nameof(choco)))} uninstall {ChocolateyId} --yes --no-progress"
            : $"scoop uninstall {ScoopName}";
        return Window(
            $"Write-Host {WingetPackage.Quote(runningText)}; " +
            $"Wait-Process -Name {WingetPackage.Quote(processName)} -Timeout 60 -ErrorAction SilentlyContinue; " +
            Run(command, failedText));
    }
}
