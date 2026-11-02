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
/// apply, and the names of the icon-only title bar buttons. No window is opened and nothing on the system is read (the
/// runner's apply runs on the fakes of <see cref="AppFixture"/>, so it takes the change gate).
/// </summary>
[Collection(nameof(ChangeGateCollection))]
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
        var protection = new ChangeRunner.Protection(new FakeRestorePoints { Enabled = enabled });
        protection.Read();
        Assert.Equal(RestorePointChoice.Cancel, protection.Ask(dialogs));
        Assert.Equal([told], dialogs.RestorePointQuestions);
    }

    [Fact]
    public void AFailedProtectionReadAsksAsIfItWereOff()
    {
        var dialogs = new FakeDialogs();
        var protection = new ChangeRunner.Protection(new ThrowingRestorePoints());
        protection.Read();
        protection.Ask(dialogs);
        Assert.Equal([false], dialogs.RestorePointQuestions);
    }

    /// <summary>
    /// The question itself reads nothing: System Protection is read on the engine's thread as the restore point starts,
    /// since that WMI query on the window's thread held the window before the question (for seconds when WMI is the trouble).
    /// </summary>
    [Fact]
    public void TheQuestionAsksWmiNothingOnTheWindowsThread()
    {
        var dialogs = new FakeDialogs();
        new ChangeRunner.Protection(new ThrowingRestorePoints()).Ask(dialogs);
        Assert.Equal([false], dialogs.RestorePointQuestions);
    }

    /// <summary>
    /// The question's buttons come in Tab order as on screen, the primary one first and Cancel last, while the callers
    /// list them the other way round: Tab went from Cancel on the right to the left. The window is built, not shown.
    /// </summary>
    [Fact]
    public void TheQuestionsButtonsComeInTabOrderAsOnScreen()
    {
        string[]? labels = null;
        int[]? columns = null;
        Exception? error = null;
        var sta = new Thread(() =>
        {
            try
            {
                var window = new MessageWindow("Title", "Text", "Cancel", "Continue", "Turn on");
                var buttons = window.Buttons.Children.OfType<Wpf.Ui.Controls.Button>().ToList();
                labels = buttons.Select(b => b.Content as string ?? "").ToArray();
                columns = buttons.Select(System.Windows.Controls.Grid.GetColumn).ToArray();
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        Assert.Null(error);
        Assert.NotNull(labels);
        Assert.NotNull(columns);
        Assert.Equal(["Turn on", "Continue", "Cancel"], labels);
        Assert.Equal([0, 1, 2], columns);
    }

    /// <summary>Through the runner, one change and a batch: the question gets what was read as the restore point started.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AnApplyAsksWithWhatTheRestorePointStepRead(bool enabled, bool batch)
    {
        using var fx = new AppFixture();
        fx.Pc.RestorePoints.Enabled = enabled;
        fx.Pc.RestorePoints.CreateFails = true;
        var tweak = TweakCatalog.Current.Get("explorer.fileExtensions")!;
        if (batch) Assert.Empty(await fx.Runner.ApplyBatchAsync([tweak], "Title", "Summary"));
        else Assert.False(await fx.Runner.ApplyAsync(tweak));
        Assert.Equal([enabled], fx.Dialogs.RestorePointQuestions);
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
