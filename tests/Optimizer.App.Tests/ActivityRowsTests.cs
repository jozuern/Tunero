using Optimizer.App.ViewModels;
using Optimizer.Core.Activity;
using Optimizer.Core.Startup;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>Live readings: a program's details and End task, with fakes (no process is read or ended).</summary>
public class ActivityRowsTests
{
    private static ProgramActivity Program(string image, string name, params int[] pids) =>
        new(new ProgramIdentity(new ProgramKey("name:" + image), name, image == "steam.exe" ? @"C:\Steam\steam.exe" : null, image, []),
            pids.Select(p => new ProcessRef(p, 7)).ToList(), 10, 0, 0, 10, 0.5, null, false);

    private static ProgramDetails Details(ProgramActivity p, bool critical = false, bool protectedProcess = false) =>
        ProgramDetails.Build(p, _ => critical, _ => new SignatureInfo(SignatureStatus.Signed, "Valve Corp.", false), ownPid: 1, canEnd: _ => !protectedProcess);

    /// <summary>Windows does not let programs end a protected process (the Defender engine): no End task, no rule.</summary>
    [Fact]
    public void ProtectedProcessIsRefused()
    {
        var details = Details(Program("MsMpEng.exe", "MsMpEng", 900), protectedProcess: true);
        Assert.False(details.CanEnd);
        Assert.False(details.CanAddRule);
        Assert.NotNull(details.EndRefusal);
    }

    /// <summary>Ending Chrome's main process takes the others along: those are gone (87), which is not a failure.</summary>
    [Fact]
    public async Task ProcessesAlreadyGoneCountAsEnded()
    {
        var shell = new FakeShell();
        var actions = new ActivityActions(new FakeDialogs { Answer = true }, shell, (pid, _) => pid == 10 ? (true, 0) : (false, 87), p => Details(p), _ => { }, _ => { });
        var list = new ActivityList(DeviceKind.Processor, actions);
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0), [Program("steam.exe", "Steam", 10, 11)], 0, 0), null);
        list.Selected = list.Rows[0];
        await list.EndTaskCommand.ExecuteAsync(null);
        Assert.Contains(shell.Results, r => r.Severity == InfoBarSeverity.Success);
        Assert.DoesNotContain(shell.Results, r => r.Severity == InfoBarSeverity.Warning);
    }

    /// <summary>A program that ended since its details opened is not ended again (no question, no call).</summary>
    [Fact]
    public async Task EndedProgramIsNotEndedAgain()
    {
        var dialogs = new FakeDialogs { Answer = true };
        var calls = 0;
        var actions = new ActivityActions(dialogs, new FakeShell(), (_, _) => { calls++; return (true, 0); }, p => Details(p), _ => { }, _ => { });
        var list = new ActivityList(DeviceKind.Processor, actions);
        var running = Program("steam.exe", "Steam", 10);
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0), [running], 0, 0), null);
        list.Selected = list.Rows[0];
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0), [running with { Ended = true }], 0, 0), null);
        await list.EndTaskCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Asked);
        Assert.Equal(0, calls);
    }

    /// <summary>The row keeps its place but takes the program's new name (a svchost whose services changed).</summary>
    [Fact]
    public void RowNameFollowsTheProgram()
    {
        var list = new ActivityList(DeviceKind.Processor);
        var first = Program("svchost.exe", "Service host: DNS Client", 30);
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0), [first], 0, 0), null);
        var row = list.Rows[0];
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0),
            [first with { Program = first.Program with { Name = "Service host: DNS Client, Windows Update" } }], 0, 0), null);
        Assert.Same(row, list.Rows[0]);
        Assert.Equal("Service host: DNS Client, Windows Update", row.Name);
    }

    /// <summary>Opening while an open still runs (page left and shown again): one set of sources, never two.</summary>
    [Fact]
    public async Task OverlappingOpensShareOneSession()
    {
        var created = 0;
        var gate = new TaskCompletionSource();
        var connection = new ActivityConnection(async () => { created++; await gate.Task; return new FakeSources(); });
        var first = connection.OpenAsync();
        var second = connection.OpenAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, created);
        Assert.True(connection.IsOpen);
    }

    /// <summary>Closed while the open still runs: the sources that arrive late are disposed, nothing stays open.</summary>
    [Fact]
    public async Task CloseDuringOpenDisposesTheLateSources()
    {
        var gate = new TaskCompletionSource();
        var sources = new FakeSources();
        var connection = new ActivityConnection(async () => { await gate.Task; return sources; });
        var open = connection.OpenAsync();
        connection.Close();
        gate.SetResult();
        await open;
        Assert.False(connection.IsOpen);
        Assert.True(sources.Disposed);
    }

    private sealed class FakeSources : IActivitySources
    {
        public bool Disposed { get; private set; }
        public ProcessSnapshot Snapshot() => new(0, []);
        public IReadOnlyList<GpuEngineSample>? Gpu() => [];
        public IReadOnlyList<DiskIoTotal>? Disk(out string? unavailableKey) { unavailableKey = null; return []; }
        public IReadOnlyList<GpuAdapter> Adapters() => [];
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void RefusedProgramHasNoEndButton()
    {
        var details = Details(Program("lsass.exe", "lsass", 600));
        Assert.NotNull(details.EndRefusal);
        Assert.False(details.CanAddRule);
        Assert.False(details.CanEnd);
    }

    [Fact]
    public void NormalProgramCanBeEndedAndGetARule()
    {
        var details = Details(Program("steam.exe", "Steam", 10, 11));
        Assert.Null(details.EndRefusal);
        Assert.True(details.CanEnd);
        Assert.True(details.CanAddRule);
        Assert.Equal("Valve Corp.", details.Publisher);
        Assert.Equal(2, details.ProcessCount);
    }

    [Fact]
    public void CriticalProcessIsRefused() => Assert.False(Details(Program("foo.exe", "foo", 10), critical: true).CanEnd);

    [Fact]
    public void WindowsNoteIsShown() => Assert.NotNull(Details(Program("MsMpEng.exe", "MsMpEng", 900)).Note);

    /// <summary>A protected process has no readable path, so no signature: the line has no empty part.</summary>
    [Fact]
    public void MetaLineSkipsMissingParts()
    {
        var meta = Details(Program("MsMpEng.exe", "MsMpEng", 900)).MetaText;
        Assert.DoesNotContain(", ,", meta);
        Assert.Contains(", ", meta); // publisher, then the process count
    }

    private static (ActivityList List, FakeDialogs Dialogs, FakeShell Shell, List<(int Pid, long Create)> Ended) List(bool answer, bool endWorks = true)
    {
        var dialogs = new FakeDialogs { Answer = answer };
        var shell = new FakeShell();
        var ended = new List<(int, long)>();
        var actions = new ActivityActions(dialogs, shell,
            (pid, create) => { ended.Add((pid, create)); return endWorks ? (true, 0) : (false, 5); },
            p => Details(p), _ => { }, _ => { });
        var list = new ActivityList(DeviceKind.Processor, actions);
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0), [Program("steam.exe", "Steam", 10, 11)], 0, 0), null);
        list.Selected = list.Rows[0];
        return (list, dialogs, shell, ended);
    }

    [Fact]
    public async Task EndTaskAsksFirst()
    {
        var (list, dialogs, _, ended) = List(answer: false);
        await list.EndTaskCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Asked);
        Assert.Empty(ended);

        var (yes, _, shell, endedYes) = List(answer: true);
        await yes.EndTaskCommand.ExecuteAsync(null);
        Assert.Equal([(10, 7L), (11, 7L)], endedYes);
        Assert.Contains(shell.Results, r => r.Severity == InfoBarSeverity.Success && r.Text.Contains("Steam"));
    }

    [Fact]
    public async Task EndTaskReportsFailure()
    {
        var (list, _, shell, _) = List(answer: true, endWorks: false);
        await list.EndTaskCommand.ExecuteAsync(null);
        Assert.Contains(shell.Results, r => r.Severity == InfoBarSeverity.Warning && r.Text.Contains("Steam"));
    }

    [Fact]
    public async Task RefusedProgramIsNeverEnded()
    {
        var dialogs = new FakeDialogs { Answer = true };
        var ended = new List<int>();
        var actions = new ActivityActions(dialogs, new FakeShell(), (pid, _) => { ended.Add(pid); return (true, 0); }, p => Details(p), _ => { }, _ => { });
        var list = new ActivityList(DeviceKind.Processor, actions);
        list.Update(new DeviceActivity(new DeviceKey(DeviceKind.Processor, 0), [Program("lsass.exe", "lsass", 600)], 0, 0), null);
        list.Selected = list.Rows[0];
        await list.EndTaskCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Asked);
        Assert.Empty(ended);
    }
}
