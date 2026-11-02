using Optimizer.Core.Catalog;
using Optimizer.Core.Debloat;

namespace Optimizer.Core.Tests;

/// <summary>Apps that were not reviewed: only removable packages that are neither reviewed, protected nor part of Windows.</summary>
public class DebloatUnreviewedTests
{
    [Fact]
    public void OnlyPackagesNobodyReviewedAndWindowsDoesNotNeed()
    {
        var installed = new[]
        {
            new InstalledAppx("Contoso.SomeGame", "Contoso.SomeGame_abc", "1.0", false),
            new InstalledAppx("Microsoft.BingNews", "Microsoft.BingNews_8we", "1.0", false),              // reviewed list
            new InstalledAppx("Microsoft.WindowsStore", "Microsoft.WindowsStore_8we", "1.0", false),      // protected
            new InstalledAppx("Microsoft.Windows.SecureBoot", "Microsoft.Windows.SecureBoot_cw5", "1.0", false), // part of Windows
            new InstalledAppx("Microsoft.XboxIdentityProvider", "Microsoft.XboxIdentityProvider_8we", "1.0", false), // games need it
            new InstalledAppx("Fabrikam.Locked", "Fabrikam.Locked_x", "1.0", true),                       // non-removable
            new InstalledAppx("Bad'Name", "x", "1.0", false),                                              // never into a script
            new InstalledAppx("Alpha.Tool", "Alpha.Tool_x", "2.0", false),
        };
        var offered = DebloatService.Unreviewed(CatalogData.Current.Appx, installed);
        Assert.Equal(["Alpha.Tool", "Contoso.SomeGame"], offered.Select(a => a.Name));
    }

    /// <summary>The Windows App Runtime main, singleton and DDLM packages are not frameworks and are removable, but Windows App SDK
    /// apps need them: they are protected, so neither the unreviewed list nor the install media offers them.</summary>
    [Theory]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.1.8")]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.2")]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Singleton")]
    [InlineData("Microsoft.WinAppRuntime.DDLM.5001.311.2039.0-x6")]
    public void WindowsAppRuntimePackagesAreNeverOffered(string name)
    {
        var appx = CatalogData.Current.Appx;
        Assert.True(appx.IsProtected(name), name);
        var installed = new[] { new InstalledAppx(name, name + "_8wekyb3d8bbwe", "1.0", false) };
        Assert.Empty(DebloatService.Unreviewed(appx, installed));
    }

    /// <summary>The exclusions only narrow the unreviewed list: reviewed apps under the same prefixes are still offered.</summary>
    [Fact]
    public void ReviewedAppsUnderExcludedPrefixesStayReviewed()
    {
        var appx = CatalogData.Current.Appx;
        Assert.NotEmpty(appx.UnreviewedExcluded);
        var reviewed = appx.Apps.First(e => appx.IsUnreviewedExcluded(e.Name));
        var installed = new[] { new InstalledAppx(reviewed.Name, reviewed.Name + "_x", "1.0", false) };
        Assert.Empty(DebloatService.Unreviewed(appx, installed));
    }
}
