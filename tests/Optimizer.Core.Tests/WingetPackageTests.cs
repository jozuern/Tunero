using Optimizer.Core.Platform;
using Optimizer.Core.Updates;

namespace Optimizer.Core.Tests;

/// <summary>The update through winget for a winget install, and the Start menu entry.</summary>
public class WingetPackageTests
{
    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe", true)]
    [InlineData(@"C:\Program Files\WinGet\Packages\jozuern.PCOptimizer_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe", true)]
    [InlineData(@"D:\Portable\jozuern.PCOptimizer\Tunero.exe", true)] // winget's package root moved in its settings
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.Tunero_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe", true)] // the id after the rename
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.TuneroBeta_x\Tunero.exe", false)]
    [InlineData(@"C:\Users\a\Downloads\Tunero.exe", false)]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\jozuern.PCOptimizerBeta_x\Tunero.exe", false)]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\Other.App_Microsoft.Winget.Source_8wekyb3d8bbwe\Tunero.exe", false)]
    public void RecognizesTheWingetPackageFolder(string exe, bool expected) =>
        Assert.Equal(expected, WingetPackage.PackageFolder(exe) is not null);

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("it\u2019s", "'it\u2019\u2019s'")]
    [InlineData("say \"hi\"", "'say hi'")]
    [InlineData(@"C:\Users\O'Brien\Tunero (1).exe", @"'C:\Users\O''Brien\Tunero (1).exe'")]
    public void QuotesForPowerShell(string text, string expected) => Assert.Equal(expected, WingetPackage.Quote(text));

    [Fact]
    public void UpdateWaitsForTheAppThenRunsWingetForThisPackageAndStartsTheAppAgain()
    {
        var args = WingetPackage.UpdateArguments(4242, @"C:\Apps\Tunero.exe", "Updating", "Failed \"here\"");
        Assert.StartsWith("-NoProfile -Command \"", args);
        Assert.EndsWith("\"", args);
        Assert.Equal(2, args.Count(c => c == '"')); // only the quotes around the command
        var wait = args.IndexOf("Wait-Process -Id 4242", StringComparison.Ordinal);
        var upgrade = args.IndexOf("winget upgrade --id jozuern.Tunero --exact --source winget", StringComparison.Ordinal);
        var start = args.IndexOf(@"Start-Process -FilePath 'C:\Apps\Tunero.exe'", StringComparison.Ordinal);
        Assert.True(wait > 0 && upgrade > wait && start > upgrade);
    }

    [Fact]
    public void StartMenuEntryIsCreatedPointsToTheExeAndIsRemoved()
    {
        var folder = TestFolders.Create("startmenu");
        try
        {
            var exe = Path.Combine(folder, "Tunero.exe");
            File.WriteAllBytes(exe, []);
            Assert.False(StartMenuShortcut.Exists(folder));
            Assert.Null(StartMenuShortcut.Target(folder));

            StartMenuShortcut.Create(folder, exe, "Test", folder);
            Assert.True(StartMenuShortcut.Exists(folder));
            Assert.Equal(exe, StartMenuShortcut.Target(folder), ignoreCase: true);

            StartMenuShortcut.Remove(folder);
            Assert.False(StartMenuShortcut.Exists(folder));
            StartMenuShortcut.Remove(folder); // nothing left to remove
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [Theory]
    [InlineData("Tunero.lnk")]
    [InlineData("PCOptimizer.lnk")]
    public void StartMenuEntryUnderAnEarlierNameIsRenamedOnlyForThisExe(string legacy)
    {
        var folder = TestFolders.Create("startmenu-rename");
        try
        {
            var exe = Path.Combine(folder, "Tunero.exe");
            var other = Path.Combine(folder, "other", "Tunero.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllBytes(exe, []);
            File.WriteAllBytes(other, []);
            var current = Path.Combine(folder, StartMenuShortcut.FileName);
            var old = Path.Combine(folder, legacy);

            // An old entry that starts another copy stays as it is.
            StartMenuShortcut.Create(folder, other, "Test", folder);
            File.Move(current, old);
            Assert.False(StartMenuShortcut.RenameLegacy(folder, exe, "Test", folder));
            Assert.True(File.Exists(old));
            Assert.False(File.Exists(current));

            // One that starts this exe gets the new name, and the old file is gone.
            StartMenuShortcut.Create(folder, exe, "Test", folder);
            File.Move(current, old);
            Assert.True(StartMenuShortcut.RenameLegacy(folder, exe, "Test", folder));
            Assert.True(File.Exists(current));
            Assert.False(File.Exists(old));
            Assert.Equal(exe, StartMenuShortcut.Target(folder), ignoreCase: true);

            // With the new name in place there is nothing to do.
            Assert.False(StartMenuShortcut.RenameLegacy(folder, exe, "Test", folder));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [Fact]
    public void StartMenuFolderOfAnotherAdminAccountIsTheSignedInUsers()
    {
        var mismatch = new ElevationInfo(true, "admin", "S-1-5-21-1-500", "a", "S-1-5-21-1-1001", false);
        Assert.Equal(@"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs", StartMenuShortcut.ProgramsFolder(mismatch, @"C:\Users\a"));
        Assert.Null(StartMenuShortcut.ProgramsFolder(mismatch, null));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuShortcut.ProgramsFolder(null, @"C:\Users\a"));
    }

    /// <summary>
    /// Administrator protection needs the policy (TypeOfAdminApprovalMode 2) and the ADMIN_ account: an ordinary account
    /// that happens to be called ADMIN_x is a separate admin account, which gets the banner.
    /// </summary>
    [Theory]
    [InlineData("ADMIN_joshua", @"PC\ADMIN_joshua", 2, true)]
    [InlineData("joshua", @"PC\ADMIN_k3xq", 2, true)]
    [InlineData("ADMIN_tools", @"PC\ADMIN_tools", 1, false)]
    [InlineData("ADMIN_tools", @"PC\ADMIN_tools", null, false)]
    [InlineData("admin", @"PC\admin", 2, false)]
    public void AdministratorProtectionNeedsThePolicyAndTheSystemManagedAccount(string profile, string process, int? policy, bool expected) =>
        Assert.Equal(expected, ElevationInfo.IsAdministratorProtection(profile, process, policy));
}
