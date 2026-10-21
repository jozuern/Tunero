using Optimizer.App.ViewModels;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// Which Tweaks page rows Select mode can pick: off and safe to apply together (the batch of Apply recommended) to turn on,
/// a change of this app to undo, nothing else. Rows of catalog tweaks; nothing reads or changes the system.
/// </summary>
public sealed class TweakPickTests
{
    private static readonly TweakDefinition BatchSafe = TweakCatalog.Current.Tweaks.First(t => t.IsBatchSafe && !t.Hidden);
    private static readonly TweakDefinition NotBatchSafe = TweakCatalog.Current.Tweaks.First(t => !t.IsBatchSafe && !t.Hidden);

    private static TweakItemViewModel Row(TweakDefinition t, TweakState state, bool backup = false, params Block[] blocks) =>
        new(new TweakStatus(t, state, 2, [], null, false, blocks, backup), "en", null!, null);

    [Fact]
    public void OffAndBatchSafeTurnsOn()
    {
        Assert.Equal(PickKind.TurnOn, Row(BatchSafe, TweakState.NotApplied).Pick);
        // Missing admin rights block every row alike (the page says so once): the row can still be picked.
        Assert.Equal(PickKind.TurnOn, Row(BatchSafe, TweakState.NotApplied, false, new Block("block.notElevated")).Pick);
        // Another hard block keeps it out.
        Assert.Equal(PickKind.None, Row(BatchSafe, TweakState.NotApplied, false, new Block("block.notDesktop")).Pick);
    }

    [Fact]
    public void ChangeThatNeedsItsOwnConfirmationIsNotPicked()
    {
        var row = Row(NotBatchSafe, TweakState.NotApplied);
        Assert.Equal(PickKind.None, row.Pick);
        Assert.NotNull(row.PickNote);
        row.IsPicked = true;
        Assert.False(row.IsPicked);
    }

    [Fact]
    public void ChangesOfThisAppAreUndone()
    {
        Assert.Equal(PickKind.Undo, Row(NotBatchSafe, TweakState.Applied, backup: true).Pick);
        Assert.Equal(PickKind.Undo, Row(BatchSafe, TweakState.RevertedByWindows, backup: true).Pick);
    }

    [Fact]
    public void OnOutsideThisAppIsNotPicked()
    {
        var row = Row(BatchSafe, TweakState.Applied);
        Assert.Equal(PickKind.None, row.Pick);
        Assert.Null(row.PickNote);
        Assert.Equal(PickKind.None, Row(BatchSafe, TweakState.NotApplicable).Pick);
    }

    [Fact]
    public void PickReportsTheChange()
    {
        var row = Row(BatchSafe, TweakState.NotApplied);
        var changes = 0;
        row.PickChanged = _ => changes++;
        row.IsPicked = true;
        row.IsPicked = true;
        row.IsPicked = false;
        Assert.Equal(2, changes);
    }
}
