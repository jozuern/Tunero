using Optimizer.App.Services;
using Optimizer.Core.Catalog;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// The command line lists one line per tweak, so a result that does not name its tweak (nothing to do, blocked, failed)
/// gets the title in front; the window shows the plain text next to the tweak it applied (VM run 2026-10-21).
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class CliResultLineTests : IDisposable
{
    private readonly string _language = Loc.Instance.Language;

    public CliResultLineTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage(_language);

    private static readonly TweakDefinition Tweak = TweakCatalog.Current.Tweaks.Single(t => t.Id == "personalize.taskbarSearchIcon");

    private static string Title => ChangeRunner.TitleOf(Tweak, "en");

    [Fact]
    public void ABlockedOrUnneededChangeNamesItsTweak()
    {
        var blocked = new ApplyResult(ApplyOutcome.Blocked, [], Blocks: [new Block("block.notBatchSafe")]);
        Assert.StartsWith("Not applied: " + Title + ". ", ChangeRunner.ResultLine(Tweak, Title, blocked));
        Assert.Equal("Not applied: " + Title + ". Nothing to change, the setting is already in place.",
            ChangeRunner.ResultLine(Tweak, Title, new ApplyResult(ApplyOutcome.NothingToDo, [])));
    }

    [Fact]
    public void AFailureNamesItsTweakAndReason()
    {
        Assert.Equal("Not applied: " + Title + ". Nothing was left half-changed. Reason: DISM failed (0x800f0975): in use.",
            ChangeRunner.ResultLine(Tweak, Title, new ApplyResult(ApplyOutcome.Failed, [], Error: "DISM failed (0x800f0975): in use.")));
    }

    [Fact]
    public void AnAppliedChangeKeepsItsOwnText()
    {
        var applied = new ApplyResult(ApplyOutcome.Applied, []);
        Assert.Equal(ChangeRunner.ResultText(Tweak, Title, applied), ChangeRunner.ResultLine(Tweak, Title, applied));
    }
}
