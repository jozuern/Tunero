using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// What the progress banner is fed: the steps of a batch and of undo all (with the restore point as its own step), a stop
/// between two changes, and the percentages and counts that sfc, dism and winget print.
/// </summary>
public class ProgressReportTests
{
    private static Facts Facts() => new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false)
        .Set("gpu.hasDiscrete", true).Set("gpu.supportsHags", true).Set("memory.totalGb", 32.0).Set("storage.ssdOnly", true)
        .Set("power.modernStandby", false).Set("cpu.x3dMultiCcd", false).Set("os.insider", false);

    private static TweakDefinition T(string id) => TweakCatalog.Current.Get(id) ?? throw new InvalidOperationException(id);

    /// <summary>Reports at once on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class Collect<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }

    [Fact]
    public async Task BatchReportsTheRestorePointAndEveryChangeWithTheTotal()
    {
        using var fx = new EngineFixture();
        var steps = new Collect<BatchStep>();
        var tweaks = new[] { T("gpu.hags"), T("explorer.fileExtensions") };
        var outcome = await new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock)
            .ApplyBatchAsync(tweaks, Facts(), new ApplyOptions(), () => RestorePointChoice.Cancel, steps);
        Assert.Equal(2, outcome.Applied);
        Assert.Equal(
        [
            new BatchStep(0, 2, tweaks[0]),
            new BatchStep(0, 2, tweaks[0], RestorePoint: true),
            new BatchStep(1, 2, tweaks[1]),
        ], steps.Items);
    }

    [Fact]
    public async Task StopEndsTheBatchBetweenTwoChanges()
    {
        using var fx = new EngineFixture();
        using var stop = new CancellationTokenSource();
        var tweaks = new[] { T("gpu.hags"), T("explorer.fileExtensions") };
        // Stop pressed while the first change runs: it is finished, the second one is not started.
        var steps = new Collect<BatchStep>();
        var outcome = await new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock).ApplyBatchAsync(tweaks, Facts(), new ApplyOptions(),
            () => RestorePointChoice.Cancel, new Progress(s =>
            {
                steps.Report(s);
                if (s.RestorePoint) stop.Cancel();
            }), stop.Token);
        Assert.True(outcome.Stopped);
        Assert.Equal((1, 0, 0), (outcome.Applied, outcome.Failed, outcome.Skipped));
        Assert.NotNull(fx.Store.Get("gpu.hags"));
        Assert.Null(fx.Store.Get("explorer.fileExtensions"));
    }

    private sealed class Progress(Action<BatchStep> report) : IProgress<BatchStep>
    {
        public void Report(BatchStep value) => report(value);
    }

    [Fact]
    public async Task UndoAllReportsEveryChange()
    {
        using var fx = new EngineFixture();
        // Without System Restore: no backup of the restore point frequency joins the two.
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        await session.ApplyBatchAsync([T("gpu.hags"), T("explorer.fileExtensions")], Facts(), new ApplyOptions(), () => RestorePointChoice.Continue);
        var steps = new Collect<BatchStep>();
        var undone = await session.UndoAllAsync(steps);
        Assert.Equal(2, undone.Undone);
        Assert.Equal([(0, 2), (1, 2)], steps.Items.Select(s => (s.Done, s.Total)));
        Assert.Equal(["explorer.fileExtensions", "gpu.hags"], steps.Items.Select(s => s.Tweak!.Id).Order(StringComparer.Ordinal));
    }

    [Theory]
    // sfc /scannow in English and German, dism /RestoreHealth in English and German (comma decimal)
    [InlineData("Verification 45% complete.", 45.0)]
    [InlineData("Überprüfung 7 % abgeschlossen.", 7.0)]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("[=====                      9.7%                           ]", 9.7)]
    [InlineData("[=====                      9,7%                           ]", 9.7)]
    [InlineData("Beginning verification phase of system scan.", null)]
    [InlineData("Image Version: 10.0.26300.1000", null)]
    [InlineData("Uptime 250%", null)]
    public void PercentOfAToolLine(string line, double? expected) => Assert.Equal(expected, ToolProgress.Percent(line));

    [Theory]
    [InlineData("(2/5) Found Mozilla Firefox (x64 en-US) [Mozilla.Firefox] Version 131.0", 1, 5)]
    [InlineData("(1/1) Gefunden Git [Git.Git] Version 2.47.0", 0, 1)]
    [InlineData("  ██████████████████████████████  58.3 MB / 58.3 MB", null, null)]
    [InlineData("(0/0) Found nothing", null, null)]
    [InlineData("(6/5) Found", null, null)]
    public void WingetPackageOfAll(string line, int? done, int? total) =>
        Assert.Equal(done is null ? null : (done.Value, total!.Value), ToolProgress.WingetPackage(line));
}
