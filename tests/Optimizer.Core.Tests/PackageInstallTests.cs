using Optimizer.Core.Updates;

namespace Optimizer.Core.Tests;

/// <summary>Updates and the uninstall of a copy installed with winget, Chocolatey or Scoop go through that package manager.</summary>
public class PackageInstallTests
{
    private const string Profile = @"C:\Users\a";

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.Tunero_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe", PackageManager.Winget, false)]
    [InlineData(@"C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe", PackageManager.Chocolatey, false)]
    [InlineData(@"D:\Tools\choco\lib\Tunero\tools\Tunero.exe", PackageManager.Chocolatey, false)] // ChocolateyInstall moved
    [InlineData(@"C:\Users\a\scoop\apps\tunero\current\Tunero.exe", PackageManager.Scoop, false)]
    [InlineData(@"C:\Users\a\scoop\apps\tunero\0.8.0\Tunero.exe", PackageManager.Scoop, false)]
    [InlineData(@"C:\ProgramData\scoop\apps\tunero\current\Tunero.exe", PackageManager.Scoop, true)] // scoop install --global
    public void RecognizesThePackageManagersFolders(string exe, PackageManager manager, bool global)
    {
        var package = PackageInstall.Detect(exe, Profile);
        Assert.NotNull(package);
        Assert.Equal(manager, package.Manager);
        Assert.Equal(global, package.Global);
    }

    [Theory]
    [InlineData(@"C:\Users\a\Downloads\Tunero.exe")]
    [InlineData(@"C:\ProgramData\chocolatey\lib\tunero-beta\tools\Tunero.exe")]
    [InlineData(@"C:\ProgramData\chocolatey\lib\tunero\Tunero.exe")]
    [InlineData(@"C:\Users\a\scoop\apps\other\current\Tunero.exe")]
    [InlineData(@"C:\Users\a\scoop\tunero\current\Tunero.exe")]
    public void ADownloadedCopyBelongsToNoPackageManager(string exe) => Assert.Null(PackageInstall.Detect(exe, Profile));

    [Fact]
    public void ScoopRunsItsScriptAsTheUserWithoutTreatingAMissingExitCodeAsAFailure()
    {
        var package = PackageInstall.Detect(@"C:\Users\a\scoop\apps\tunero\0.8.0\Tunero.exe", Profile)!;
        Assert.True(package.RunsAsUser);
        Assert.True(package.CanRunFromApp());
        var args = package.UpdateArguments(4242, @"C:\Users\a\scoop\apps\tunero\0.8.0\Tunero.exe", "Updating", "Failed");
        Assert.StartsWith("-NoProfile -ExecutionPolicy Bypass -Command \"", args);
        Assert.Equal(2, args.Count(c => c == '"'));
        var wait = args.IndexOf("Wait-Process -Id 4242", StringComparison.Ordinal);
        var reset = args.IndexOf("$global:LASTEXITCODE = 0; scoop update tunero; $ok = $? -and $LASTEXITCODE -eq 0;", StringComparison.Ordinal);
        var failed = args.IndexOf("if (-not $ok) { Read-Host 'Failed' }", StringComparison.Ordinal);
        // The new version is in its own folder; "current" points to it.
        var start = args.IndexOf(@"Start-Process -FilePath 'C:\Users\a\scoop\apps\tunero\current\Tunero.exe'", StringComparison.Ordinal);
        Assert.True(wait > 0 && reset > wait && failed > reset && start > failed, args);

        var uninstall = package.UninstallArguments("Tunero", "Removing", "Failed");
        Assert.StartsWith("-NoProfile -ExecutionPolicy Bypass -Command \"", uninstall);
        Assert.Contains("Wait-Process -Name 'Tunero'", uninstall);
        Assert.Contains("$global:LASTEXITCODE = 0; scoop uninstall tunero; $ok = $? -and $LASTEXITCODE -eq 0;", uninstall);
    }

    [Fact]
    public void AGlobalScoopInstallIsNotRunFromTheElevatedApp()
    {
        var package = PackageInstall.Detect(@"C:\ProgramData\scoop\apps\tunero\current\Tunero.exe", Profile)!;
        Assert.False(package.CanRunFromApp());
        Assert.Equal("scoop update tunero --global", package.UpdateCommand);
        Assert.Equal("scoop uninstall tunero --global", package.UninstallCommand);
    }

    [Fact]
    public void ChocolateyRunsElevatedOnlyFromAnAdminOnlyFolder()
    {
        var package = PackageInstall.Detect(@"C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe", Profile)!;
        Assert.False(package.RunsAsUser);
        Assert.False(package.CanRunFromApp(_ => false));
        Assert.Null(package.TrustedChocolatey(_ => false));
        // The app's exe is started elevated again after the update: it has to be admin-only too.
        Assert.Null(package.TrustedChocolatey(path => path == @"C:\ProgramData\chocolatey\choco.exe", _ => true));
        // choco loads its helpers, extensions and configuration from its root: that folder must be admin-only too.
        var checkedFolders = new List<string>();
        Assert.Null(package.TrustedChocolatey(_ => true, folder =>
        {
            checkedFolders.Add(folder);
            return false;
        }));
        Assert.Equal([@"C:\ProgramData\chocolatey"], checkedFolders);
        var choco = package.TrustedChocolatey(path => path.StartsWith(@"C:\ProgramData\chocolatey\", StringComparison.Ordinal), _ => true);
        Assert.Equal(@"C:\ProgramData\chocolatey\choco.exe", choco);
        // The update runs with --ignore-http-cache, which Chocolatey has from 2.1.0 on: an older one shows the command instead.
        static bool AdminOnly(string path) => path.StartsWith(@"C:\ProgramData\chocolatey", StringComparison.Ordinal);
        Assert.Equal(choco, package.TrustedChocolateyForUpgrade(AdminOnly, AdminOnly, _ => "2.4.1"));
        Assert.Null(package.TrustedChocolateyForUpgrade(AdminOnly, AdminOnly, _ => "2.0.1"));
        Assert.Null(package.TrustedChocolateyForUpgrade(AdminOnly, AdminOnly, _ => null));
        Assert.Null(package.TrustedChocolateyForUpgrade(_ => false, AdminOnly, _ => "2.4.1"));

        var args = package.UpdateArguments(7, @"C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe", "Updating", "Failed", choco);
        Assert.StartsWith("-NoProfile -Command \"", args); // an exe: no execution policy exception
        Assert.Contains(@"$global:LASTEXITCODE = 0; & 'C:\ProgramData\chocolatey\choco.exe' upgrade tunero --yes --no-progress --ignore-http-cache; $ok = $? -and $LASTEXITCODE -eq 0;", args);
        Assert.Contains(@"Start-Process -FilePath 'C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe'", args);
        Assert.Throws<ArgumentNullException>(() => package.UpdateArguments(7, @"C:\x\Tunero.exe", "a", "b"));
        Assert.Contains(@"& 'C:\ProgramData\chocolatey\choco.exe' uninstall tunero --yes --no-progress", package.UninstallArguments("Tunero", "a", "b", choco));
        Assert.Equal("choco upgrade tunero", package.UpdateCommand);
    }

    [Fact]
    public void ElevatedChocolateyGetsNoChocolateyVariablesFromTheUser()
    {
        var package = PackageInstall.Detect(@"C:\ProgramData\chocolatey\lib\tunero\tools\Tunero.exe", Profile)!;
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ChocolateyInstall"] = @"C:\Users\a\fake",
            ["ChocolateyToolsLocation"] = @"C:\Users\a\tools",
            ["chocolateyProxyLocation"] = "http://proxy",
            ["Path"] = @"C:\Windows\System32",
        };
        package.HardenChocolateyEnvironment(env);
        Assert.Equal(@"C:\ProgramData\chocolatey", env["ChocolateyInstall"]);
        Assert.Equal(2, env.Count);
        Assert.Equal(@"C:\Windows\System32", env["Path"]);

        // winget and Scoop run as the signed-in user with that user's own environment: nothing to change there.
        var scoop = new Dictionary<string, string?> { ["ChocolateyInstall"] = "x" };
        PackageInstall.Detect(@"C:\Users\a\scoop\apps\tunero\current\Tunero.exe", Profile)!.HardenChocolateyEnvironment(scoop);
        Assert.Equal("x", scoop["ChocolateyInstall"]);
    }

    [Fact]
    public void WingetKeepsItsOwnCommands()
    {
        const string exe = @"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.Tunero_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe";
        var package = PackageInstall.Detect(exe, Profile)!;
        Assert.Equal(WingetPackage.UpdateArguments(1, exe, "a", "b"), package.UpdateArguments(1, exe, "a", "b"));
        Assert.Contains("winget uninstall --product-code 'jozuern.Tunero_Microsoft.Winget.Source_8wekyb3d8bbwe'", package.UninstallArguments("Tunero", "a", "b"));
        Assert.Equal("winget upgrade jozuern.Tunero", package.UpdateCommand);
    }
}
