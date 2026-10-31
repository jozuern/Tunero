using System.Globalization;
using Optimizer.App.Views;

namespace Optimizer.App.Tests;

/// <summary>
/// The share of a panel's height a part may take (Installed programs: the bar above the list at most 60 percent), and no
/// limit while the panel has no height yet, so nothing is hidden before the first layout.
/// </summary>
public sealed class FractionConverterTests
{
    private static object Convert(object? value, string? parameter) =>
        new FractionConverter().Convert(value, typeof(double), parameter, CultureInfo.InvariantCulture);

    [Fact]
    public void AShareOfAKnownLength() => Assert.Equal(300.0, (double)Convert(500.0, "0.6"), 6);

    [Theory]
    [InlineData(0.0, "0.6")]
    [InlineData(500.0, null)]
    [InlineData(500.0, "a lot")]
    public void NoLimitWithoutALengthOrAShare(double length, string? share) => Assert.Equal(double.PositiveInfinity, Convert(length, share));

    [Fact]
    public void NoLimitForSomethingElse() => Assert.Equal(double.PositiveInfinity, Convert("500", "0.6"));
}
