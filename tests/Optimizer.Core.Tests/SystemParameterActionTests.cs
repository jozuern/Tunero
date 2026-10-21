using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Menu and hover timing through SystemParametersInfo: set, undone to the earlier value, unavailable for another account.</summary>
public class SystemParameterActionTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true);
    private static readonly ApplyOptions Options = new() { ContinueWithoutRestorePoint = true };

    [Theory]
    [InlineData("personalize.menuDelayShort", SystemParameter.MenuShowDelay, 100u)]
    [InlineData("personalize.hoverTimeShort", SystemParameter.MouseHoverTime, 200u)]
    public async Task TimingIsSetAndTheEarlierValueComesBack(string id, SystemParameter parameter, uint value)
    {
        using var fx = new EngineFixture();
        fx.SystemParameters.Values[parameter] = 250; // the user had set something of their own
        var t = TweakCatalog.Current.Tweaks.Single(x => x.Id == id);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options)).Outcome);
        Assert.Equal(value, fx.SystemParameters.Values[parameter]);
        Assert.Equal(TweakState.Applied, fx.Engine.Detect(t, Facts).State);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(250u, fx.SystemParameters.Values[parameter]);
    }

    [Fact]
    public void AnotherAccountThanTheSessionUserCannotChangeIt()
    {
        using var fx = new EngineFixture();
        fx.SystemParameters.Unavailable = true;
        var action = new SystemParameterAction { Parameter = SystemParameter.MenuShowDelay, Value = 100 };
        Assert.Null(action.Read(fx.Context));
        Assert.Throws<InvalidOperationException>(() => action.Apply(fx.Context));
        Assert.Equal("spi:menushowdelay", action.TargetKey);
    }
}
