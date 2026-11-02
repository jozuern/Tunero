using System.Globalization;
using System.Resources;
using Optimizer.App.Services;
using Optimizer.App.Views;
using Optimizer.Core.Actions;
using Optimizer.Core.Tests;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// What the dialogs say: the restore point question after no restore point came of it, the confirmation with nothing to
/// apply, and the names of the icon-only title bar buttons. No window is opened and nothing on the system is read.
/// </summary>
public sealed class DialogTextTests
{
    private static readonly ResourceManager Strings = new("Optimizer.App.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>
    /// The engine asks when System Protection is off and when it is on but Windows refused to create a restore point:
    /// the question is told which, so it does not claim protection is off and offer to turn it on.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void TheRestorePointQuestionKnowsWhetherProtectionIsOn(bool? enabled, bool told)
    {
        var dialogs = new FakeDialogs();
        Assert.Equal(RestorePointChoice.Cancel, ChangeRunner.AskRestorePoint(dialogs, new FakeRestorePoints { Enabled = enabled }));
        Assert.Equal([told], dialogs.RestorePointQuestions);
    }

    [Fact]
    public void AFailedProtectionReadAsksAsIfItWereOff()
    {
        var dialogs = new FakeDialogs();
        ChangeRunner.AskRestorePoint(dialogs, new ThrowingRestorePoints());
        Assert.Equal([false], dialogs.RestorePointQuestions);
    }

    private sealed class ThrowingRestorePoints : Optimizer.Core.Backup.IRestorePoints
    {
        public bool? IsEnabled() => throw new InvalidOperationException("WMI is not available");
        public void Enable(int maxPercent = 5) => throw new NotSupportedException();
        public Task<bool> CreateAsync(string description) => throw new NotSupportedException();
    }

    /// <summary>Every value already set: the blue bar alone says it, without red reasons it cannot be applied or warnings.</summary>
    [Fact]
    public void AConfirmationWithNothingToApplyShowsNoBlocksOrWarnings()
    {
        var same = new ConfirmRequest("Title", "", [new ChangeLine("HKCU\\Key\\Value", "1", "1")], [], ["A warning"], "Anti-cheat", false)
        {
            Blocks = ["Needs administrator rights"],
        };
        Assert.True(ConfirmWindow.NothingToApply(same));
        var (blocks, warnings) = ConfirmWindow.Notes(same, nothingToDo: true);
        Assert.Empty(blocks);
        Assert.Empty(warnings);

        // A real change keeps both, the anti-cheat warning last.
        var change = same with { Changes = [new ChangeLine("HKCU\\Key\\Value", "0", "1")] };
        Assert.False(ConfirmWindow.NothingToApply(change));
        (blocks, warnings) = ConfirmWindow.Notes(change, nothingToDo: false);
        Assert.Equal(["Needs administrator rights"], blocks);
        Assert.Equal(["A warning", "Anti-cheat"], warnings);

        // An undo and a value that cannot be read always count as something to do.
        Assert.False(ConfirmWindow.NothingToApply(same with { IsUndo = true }));
        Assert.False(ConfirmWindow.NothingToApply(same with { Changes = [new ChangeLine("HKCU\\Key\\Value", "?", "?")] }));
    }

    [Theory]
    [InlineData("TitleBarCloseButton", "", "Common_Close")]
    [InlineData("TitleBarMaximizeButton", "", "Window_Maximize")]
    [InlineData("TitleBarMinimizeButton", "", "Window_Minimize")]
    [InlineData("", "PART_ToggleButton", "Nav_Toggle")]
    [InlineData("OkButton", "", null)]
    public void TitleBarButtonsGetANameInBothLanguages(string name, string automationId, string? key)
    {
        Assert.Equal(key, IconButtonNames.KeyFor(name, automationId));
        if (key is null) return;
        var en = Strings.GetString(key, CultureInfo.GetCultureInfo("en"));
        var de = Strings.GetString(key, CultureInfo.GetCultureInfo("de"));
        Assert.False(string.IsNullOrWhiteSpace(en), key);
        Assert.False(string.IsNullOrWhiteSpace(de), key);
    }
}
