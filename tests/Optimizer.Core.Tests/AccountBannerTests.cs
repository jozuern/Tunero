using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>
/// The "other account" banner (<see cref="ElevationInfo.Banner"/>): user settings go to the signed-in user when Windows
/// gives that user's SID, and to the account the app runs as when it cannot; both cases are shown, the second one also
/// under Administrator protection.
/// </summary>
public class AccountBannerTests
{
    private const string Admin = @"PC\admin", AdminSid = "S-1-5-21-1-500";
    private const string Anna = @"CONTOSO\anna", AnnaSid = "S-1-5-21-2-1104";

    private static ElevationInfo As(string process, string? processSid, string? session, string? sessionSid, bool adminProtection = false) =>
        new(true, process, processSid, session, sessionSid, adminProtection);

    [Fact]
    public void TheSameAccountShowsNothing()
    {
        Assert.Equal(AccountBanner.None, As(Anna, AnnaSid, Anna, AnnaSid).Banner);
        // Windows could not look the name up, but it is the account the app runs as: the fallback is the right one.
        Assert.Equal(AccountBanner.None, As(Anna, AnnaSid, Anna, null).Banner);
        Assert.Equal(AccountBanner.None, As(Anna, AnnaSid, "anna", null).Banner);
        // No signed-in user known at all (a service session): nothing to compare.
        Assert.Equal(AccountBanner.None, As(Admin, AdminSid, null, null).Banner);
    }

    [Fact]
    public void ASeparateAdminAccountWithAKnownUserSaysWhereUserSettingsGo()
    {
        Assert.Equal(AccountBanner.SeparateAdmin, As(Admin, AdminSid, Anna, AnnaSid).Banner);
        // Administrator protection elevates as a hidden account by design: user settings still reach the user by SID.
        Assert.Equal(AccountBanner.None, As(@"PC\ADMIN_anna", "S-1-5-21-1-1200", Anna, AnnaSid, adminProtection: true).Banner);
    }

    /// <summary>
    /// A domain account while no domain controller answers: its SID is unknown, so user settings would go to the
    /// elevated account without a word. The banner says so instead.
    /// </summary>
    [Fact]
    public void AnAccountWindowsCannotLookUpIsShown()
    {
        var unresolved = As(Admin, AdminSid, Anna, null);
        Assert.True(unresolved.SessionUserUnresolved);
        Assert.False(unresolved.UserMismatch);
        Assert.Equal(AccountBanner.UnresolvedUser, unresolved.Banner);
        Assert.Equal(AccountBanner.UnresolvedUser, As(@"PC\ADMIN_anna", "S-1-5-21-1-1200", Anna, null, adminProtection: true).Banner);
        // A bare name from Windows that is another account than the app's.
        Assert.Equal(AccountBanner.UnresolvedUser, As(Admin, AdminSid, "anna", null).Banner);
    }

    [Theory]
    [InlineData(@"CONTOSO\anna", @"contoso\ANNA", true)]
    [InlineData("anna", @"CONTOSO\anna", true)]
    [InlineData(@"CONTOSO\anna", @"PC\anna", false)]
    [InlineData(@"CONTOSO\anna", @"CONTOSO\ben", false)]
    public void AccountsCompareByDomainAndName(string a, string b, bool same) => Assert.Equal(same, ElevationInfo.SameAccount(a, b));
}
