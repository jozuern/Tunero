using Optimizer.App.ViewModels;

namespace Optimizer.App.Tests;

/// <summary>
/// The switch of a startup entry, task or Windows feature row (<see cref="SwitchRow"/>): it shows what was written, never a
/// state that was cancelled or failed, and the confirmation opens after the binding's setter returned. Run on one thread
/// with a synchronization context, as the window's dispatcher runs it.
/// </summary>
public sealed class SwitchRowTests
{
    private sealed class TestRow(bool isOn, Func<bool, Task<bool>> toggle) : SwitchRow(isOn)
    {
        public bool On => IsOn;
        protected override Task<bool> ToggleAsync(bool on) => toggle(on);
    }

    /// <summary>Sets the switch as a click does and waits until the row shows its state again (the setter's notice and the one after the toggle).</summary>
    private static async Task Click(TestRow row, bool on, List<string> notices)
    {
        var before = notices.Count;
        row.SwitchState = on;
        await SingleThread.Until(() => notices.Count >= before + 2, "the switch to show its state again");
    }

    private static List<string> Notices(TestRow row)
    {
        var notices = new List<string>();
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchRow.SwitchState)) notices.Add(e.PropertyName);
        };
        return notices;
    }

    [Fact]
    public void ACancelledToggleShowsTheRealStateAgain() => SingleThread.Run(async () =>
    {
        var row = new TestRow(false, _ => Task.FromResult(false));
        var notices = Notices(row);
        await Click(row, true, notices);
        Assert.False(row.SwitchState);
        Assert.False(row.On);
        Assert.Equal(2, notices.Count);
    });

    [Fact]
    public void AWrittenToggleKeepsTheNewState() => SingleThread.Run(async () =>
    {
        var row = new TestRow(false, _ => Task.FromResult(true));
        var notices = Notices(row);
        await Click(row, true, notices);
        Assert.True(row.SwitchState);
        Assert.True(row.On);

        // And back: the row now starts from on.
        await Click(row, false, notices);
        Assert.False(row.SwitchState);
        Assert.False(row.On);
    });

    [Fact]
    public void AThrowingToggleResetsTheSwitch() => SingleThread.Run(async () =>
    {
        var row = new TestRow(false, _ => throw new InvalidOperationException("engine failed"));
        var notices = Notices(row);
        await Click(row, true, notices);
        Assert.False(row.SwitchState);
        Assert.False(row.On);
    });

    [Fact]
    public void TheToggleRunsOutsideTheSetter() => SingleThread.Run(async () =>
    {
        var setterReturned = false;
        bool? returnedWhenAsked = null;
        var row = new TestRow(false, _ =>
        {
            returnedWhenAsked = setterReturned;
            return Task.FromResult(true);
        });
        var notices = Notices(row);
        row.SwitchState = true;
        setterReturned = true;
        await SingleThread.Until(() => notices.Count >= 2, "the toggle");
        Assert.True(returnedWhenAsked);
    });
}
