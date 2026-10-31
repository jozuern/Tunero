using Optimizer.App.Services;
using Optimizer.App.ViewModels;

namespace Optimizer.App.Tests;

/// <summary>
/// A driver card has its age on the right, where the ages of all cards line up, and the date in the caption after maker
/// and version. Nothing reads the drivers of this PC.
/// </summary>
public sealed class DriverItemTests
{
    [Fact]
    public void TheAgeStandsAloneAndTheDateJoinsTheCaption()
    {
        var date = new DateTime(2026, 6, 17);
        var (shown, age) = DriverItem.Describe(date, 120);
        Assert.Equal(date.ToString("d", System.Globalization.CultureInfo.CurrentCulture), shown);
        Assert.Equal(Loc.Instance.Format("Drv_AgeMonths", 4), age);
        var item = new DriverItem("Intel Graphics", "Graphics", "Intel Corporation", "32.0.101.7088", shown, age, null, false);
        Assert.Equal($"Graphics, Intel Corporation 32.0.101.7088, {shown}", item.MetaText);
        Assert.Equal($"Intel Graphics, {age}", item.ToString());

        Assert.Equal(Loc.Instance["Drv_AgeNew"], DriverItem.Describe(date, 10).Age);
        Assert.Equal(Loc.Instance.Format("Drv_AgeYears", 3), DriverItem.Describe(date, 3 * 365).Age);
        // A placeholder date is not shown as one; a date after today has no age.
        Assert.Equal(("", Loc.Instance["Drv_NoRealDate"]), DriverItem.Describe(new DateTime(2006, 6, 21), null));
        Assert.Equal((shown, ""), DriverItem.Describe(date, -3));
        Assert.Equal(("", ""), DriverItem.Describe(null, null));
        // Without provider, version and date the caption is the class alone.
        Assert.Equal("Audio", new DriverItem("Speaker", "Audio", "", "", "", "", null, false).MetaText);
    }
}
