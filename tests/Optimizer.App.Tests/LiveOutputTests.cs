using Optimizer.App.Services;

namespace Optimizer.App.Tests;

/// <summary>
/// The output box buffer of a tool run (<see cref="LiveOutput"/>): no line is lost when lines arrive from several threads
/// at once (Progress posts to the thread pool where there is no window), a repeated progress line stays one line, and
/// the oldest lines go past the limit. Nothing runs a tool.
/// </summary>
public sealed class LiveOutputTests
{
    [Fact]
    public void LinesFromManyThreadsAreAllKept()
    {
        var shown = "";
        var output = new LiveOutput(text => shown = text, max: 10_000);
        Parallel.For(0, 2_000, i =>
        {
            if (i % 10 == 0) output.AddRange([$"result {i}"]);
            else output.Add($"line {i}");
        });
        var lines = shown.Split('\n');
        Assert.Equal(2_000, lines.Length);
        Assert.Equal(2_000, lines.Distinct().Count());
    }

    [Fact]
    public async Task ReportedLinesReachTheBoxWithTheLineCallback()
    {
        var shown = "";
        var seen = new List<string>();
        var output = new LiveOutput(text => Volatile.Write(ref shown, text), onLine: l =>
        {
            lock (seen) seen.Add(l);
        });
        output.Progress.Report("(1/2) Found Git");
        output.Progress.Report("(2/2) Found 7-Zip");
        int SeenCount()
        {
            lock (seen) return seen.Count;
        }
        // Progress posts its callbacks: they may run a moment after the report, and a line reaches the box just before
        // the line callback sees it, so wait for both.
        for (var i = 0; i < 800 && (Volatile.Read(ref shown).Split('\n').Length < 2 || SeenCount() < 2); i++) await Task.Delay(25);
        Assert.Equal(["(1/2) Found Git", "(2/2) Found 7-Zip"], Volatile.Read(ref shown).Split('\n').Order());
        Assert.Equal(2, SeenCount());
    }

    [Fact]
    public void AProgressLineIsMergedAndTheOldestLinesGo()
    {
        var shown = "";
        var output = new LiveOutput(text => shown = text, max: 3);
        output.Add("Downloading Git");
        output.Add("  10 %");
        output.Add("  55 %");
        Assert.Equal("Downloading Git\n  55 %", shown);
        output.AddRange(["Installing", "Done", "Exit code 0"]);
        Assert.Equal("Installing\nDone\nExit code 0", shown);
    }
}
