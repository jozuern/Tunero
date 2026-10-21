using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Apps;

namespace Optimizer.App.Tests;

/// <summary>Install &amp; update after "Check for updates": Update only where winget offers a newer version, back after Update all.</summary>
public sealed class AppUpdateCheckTests
{
    private static AppRow Row(bool installed = true, string scope = "machine") =>
        new(new AppEntry { Id = "7zip.7zip", Name = "7-Zip", Scope = scope }, "en", installed);

    [Fact]
    public void UpdateShowsOnlyWithANewerVersion()
    {
        var row = Row();
        Assert.True(row.CanUpdate);

        row.ShowUpdate(new WingetUpgrade("7-Zip 23.01 (x64 edition)", "7zip.7zip", "23.01.00.0", "26.04"));
        Assert.True(row.CanUpdate);
        Assert.Equal(Loc.Instance.Format("Apps_UpdateAvailable", "23.01.00.0", "26.04"), row.StateText);

        row.ShowUpdate(null);
        Assert.False(row.CanUpdate);
        Assert.Equal(Loc.Instance["Apps_UpToDate"], row.StateText);

        row.ForgetUpdateCheck();
        Assert.True(row.CanUpdate);
        Assert.Equal(Loc.Instance["Apps_Installed"], row.StateText);
    }

    /// <summary>Not installed, or installed per user (it updates in its own installer): the check changes nothing.</summary>
    [Fact]
    public void OtherRowsKeepTheirState()
    {
        var missing = Row(installed: false);
        missing.ShowUpdate(null);
        Assert.Equal("", missing.StateText);
        Assert.False(missing.CanUpdate);

        var perUser = Row(scope: "user");
        perUser.ShowUpdate(new WingetUpgrade("7-Zip", "7zip.7zip", "1", "2"));
        Assert.Equal(Loc.Instance["Apps_Installed"], perUser.StateText);
        Assert.False(perUser.CanUpdate);
    }
}
