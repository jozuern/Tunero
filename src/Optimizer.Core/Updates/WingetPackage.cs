namespace Optimizer.Core.Updates;

/// <summary>
/// Tunero installed with winget (portable package): updates go through winget, so its record of the installed
/// version stays right, instead of the app replacing its own exe.
/// </summary>
public static class WingetPackage
{
    public const string Id = "jozuern.Tunero";

    /// <summary>The package id before the rename (submitted, never published); a copy installed from a manifest file under it is still recognized.</summary>
    public const string LegacyId = "jozuern.PCOptimizer";

    /// <summary>
    /// Name of the package folder the exe sits in, or null when it is not one. winget keeps a portable package in a folder
    /// named after the package id, with the source appended for a source
    /// ("jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe"), under a root its settings can move. Started through
    /// the command link in WinGet\Links, the path is the link's: it is followed to the file first. winget registers a
    /// portable package in Installed apps under this name, so it is also the product code that finds the installation,
    /// from any source.
    /// </summary>
    public static string? PackageFolder(string exePath)
    {
        var path = exePath;
        try
        {
            if (File.ResolveLinkTarget(exePath, returnFinalTarget: true) is { } target) path = target.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        return IsPackageFolder(folder) ? folder : null;
    }

    public static bool IsPackageFolder(string? folder) =>
        folder is not null && new[] { Id, LegacyId }.Any(id => folder.Equals(id, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase));

    public static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>
    /// PowerShell arguments for the update, started as the signed-in user (never elevated): wait until this process has
    /// ended (winget cannot replace a running exe), run winget, keep the window open with <paramref name="failedText"/>
    /// when it did not succeed, then start Tunero again (Windows asks for administrator rights as on any start).
    /// </summary>
    public static string UpdateArguments(int processId, string exePath, string runningText, string failedText) =>
        Platform.PowerShellScript.Command(
            $"Write-Host {Quote(runningText)}; " +
            $"Wait-Process -Id {processId} -Timeout 60 -ErrorAction SilentlyContinue; " +
            $"winget upgrade --id {Id} --exact --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity; " +
            $"if ($LASTEXITCODE -ne 0) {{ Read-Host {Quote(failedText)} }}; " +
            $"Start-Process -FilePath {Quote(exePath)}", interactive: true);

    /// <summary>
    /// PowerShell arguments for the uninstall, started as the signed-in user (never elevated, winget refuses to remove a
    /// user-scope package with administrator rights): wait until every process named <paramref name="processName"/> has
    /// ended, run winget, and keep the window open with <paramref name="failedText"/> when it did not succeed. The package
    /// is named by its product code (<see cref="PackageFolder"/>): the id alone finds only a package from the winget
    /// source, not one installed from a manifest file.
    /// </summary>
    public static string UninstallArguments(string productCode, string processName, string runningText, string failedText) =>
        Platform.PowerShellScript.Command(
            $"Write-Host {Quote(runningText)}; " +
            $"Wait-Process -Name {Quote(processName)} -Timeout 60 -ErrorAction SilentlyContinue; " +
            $"winget uninstall --product-code {Quote(productCode)} --accept-source-agreements --disable-interactivity; " +
            $"if ($LASTEXITCODE -ne 0) {{ Read-Host {Quote(failedText)} }}", interactive: true);

    /// <summary>
    /// A PowerShell string in single quotes. PowerShell also ends such a string at the typographic single quotes, so every
    /// one of them is doubled; double quotes would end the -Command argument and are left out.
    /// </summary>
    public static string Quote(string text)
    {
        var sb = new System.Text.StringBuilder("'");
        foreach (var c in text)
        {
            if (c == '"') continue;
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}
