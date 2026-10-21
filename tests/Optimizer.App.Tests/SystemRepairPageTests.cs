using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Platform;
using Optimizer.Core.Tools;

namespace Optimizer.App.Tests;

/// <summary>
/// The system file repair on the Tools page (sfc, DISM) through the page's tool runner, with a fake: the output shows
/// while the tool runs, the exit code follows, and Stop ends a long DISM run. Nothing starts.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class SystemRepairPageTests
{
    private readonly FakeToolRunner _tools = new();

    private HealthViewModel Page() => new(new FakePageHost(), new FakeDialogs(), null, _tools);

    /// <summary>The page's output is written from the progress callbacks, which may run a moment after the run.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task SfcShowsItsOutputAndExitCode()
    {
        _tools.Respond = (_, _) => (0, ["Beginning system scan.", "Verification 100% complete.", "Windows Resource Protection did not find any integrity violations."]);
        var page = Page();
        await page.SfcCommand.ExecuteAsync(null);
        var run = Assert.Single(_tools.Runs);
        Assert.Equal(SystemRepair.Sfc, run.File);
        Assert.Equal("/scannow", run.Arguments);
        Assert.Same(ToolEncoding.Utf16, run.Options?.Encoding);
        Assert.Null(run.Timeout);
        await WaitFor(() => page.ToolOutput.Contains("integrity violations", StringComparison.Ordinal));
        Assert.Contains(Loc.Instance.Format("Health_ExitCode", 0), page.ToolOutput);
        Assert.False(page.IsToolRunning);
    }

    /// <summary>A /RestoreHealth that runs long: Stop ends it (the process tree, with the real runner) and the output says so.</summary>
    [Fact]
    public async Task StopEndsALongDismRun()
    {
        _tools.Hangs = true;
        _tools.Respond = (_, _) => (0, ["Image Version: 10.0.26100.1", "[=====                      10.0%                          ]"]);
        var page = Page();
        var running = page.DismRestoreCommand.ExecuteAsync(null);
        await _tools.Hanging.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(page.IsToolRunning);
        await WaitFor(() => page.ToolOutput.Contains("10.0%", StringComparison.Ordinal));

        page.StopToolCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _tools.Ended);
        Assert.False(page.IsToolRunning);
        Assert.EndsWith(Loc.Instance["Health_Stopped"], page.ToolOutput);
        Assert.DoesNotContain(Loc.Instance.Format("Health_ExitCode", 0), page.ToolOutput);

        var run = Assert.Single(_tools.Runs);
        Assert.Equal(SystemRepair.Dism, run.File);
        Assert.Equal("/Online /Cleanup-Image /RestoreHealth", run.Arguments);
        Assert.Same(ToolEncoding.Oem, run.Options?.Encoding);
        Assert.Null(run.Timeout);
    }
}
