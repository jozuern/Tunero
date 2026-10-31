using Optimizer.App.ViewModels;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Platform;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// A scan source with canned results for <see cref="ScanState"/>: nothing on this PC is read. A run waits for its gate
/// when one is held (<see cref="Hold"/>), so a test decides when each scan ends; every call is recorded.
/// </summary>
internal sealed class FakeScanSource : IScanSource
{
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _gates = [];
    private readonly Dictionary<int, TaskCompletionSource> _started = [];
    private int _running;

    /// <summary>The facts every read starts from (the hardware's); the results' facts are added as FactsBuilder adds them.</summary>
    public Facts BaseFacts { get; set; } = SamplePc.Desktop();

    /// <summary>Every visible catalog tweak, off and recommended where the catalog would (a stand-in for the detection).</summary>
    public IReadOnlyList<TweakStatus> States { get; set; } = SamplePc.States();

    /// <summary>False: the facts are <see cref="BaseFacts"/> as they are (a snapshot whose facts were built with its results).</summary>
    public bool FindingFacts { get; set; } = true;

    /// <summary>The scan's results, by run number (from 1).</summary>
    public Func<int, IReadOnlyList<Finding>> Findings { get; set; } = _ => [];

    /// <summary>The background CPU sample a run leaves pending, by run number; null: none.</summary>
    public Func<int, Task<IReadOnlyList<ProcessCpu>>?> Sample { get; set; } = _ => null;

    /// <summary>A run that fails, by run number.</summary>
    public Func<int, bool> Fails { get; set; } = _ => false;

    /// <summary>The findings with the sample (default: the scan's, unchanged).</summary>
    public Func<IReadOnlyList<Finding>, IReadOnlyList<Finding>> WithSampleFindings { get; set; } = f => f;

    /// <summary>A change to the facts the sample brings (default: none).</summary>
    public Action<Facts> SampleFacts { get; set; } = _ => { };

    public List<TweakDefinition> DriftTweaks { get; } = [];
    public int Backups { get; set; }
    public string WindowsBuild { get; set; } = "26300.1000";

    public int Runs { get; private set; }
    public int MaxRunning { get; private set; }
    public List<(HardwareProfile? Previous, IReadOnlyList<ProcessCpu>? LastSample)> RunArgs { get; } = [];
    public List<IReadOnlySet<string>> Reads { get; } = [];
    public int WithSampleCalls { get; private set; }

    /// <summary>The profile each run returned, by run number.</summary>
    public Dictionary<int, HardwareProfile> Profiles { get; } = [];

    /// <summary>Run <paramref name="run"/> waits until the returned gate is opened.</summary>
    public TaskCompletionSource Hold(int run)
    {
        lock (_lock) return _gates[run] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes when run <paramref name="run"/> has started.</summary>
    public Task Started(int run)
    {
        lock (_lock) return StartedGate(run).Task;
    }

    private TaskCompletionSource StartedGate(int run) =>
        _started.TryGetValue(run, out var gate) ? gate : _started[run] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ScanResult> RunAsync(IProgress<string>? progress, HardwareProfile? previous, IReadOnlyList<ProcessCpu>? lastSample)
    {
        int run;
        TaskCompletionSource? gate;
        lock (_lock)
        {
            run = ++Runs;
            MaxRunning = Math.Max(MaxRunning, ++_running);
            RunArgs.Add((previous, lastSample));
            gate = _gates.GetValueOrDefault(run);
            StartedGate(run).TrySetResult();
        }
        try
        {
            progress?.Report("CPU");
            if (gate is not null) await gate.Task;
            if (Fails(run)) throw new InvalidOperationException($"run {run} failed");
            var profile = new HardwareProfile { Os = new BuildInfo(26300, 1000, "26H2", "Professional", "Windows 11 Pro", CpuArchitecture.X64, false, null) };
            lock (_lock) Profiles[run] = profile;
            return new ScanResult(profile, Findings(run), Sample(run));
        }
        finally
        {
            lock (_lock) _running--;
        }
    }

    /// <summary>Held: merging the sample waits until it is opened.</summary>
    public TaskCompletionSource? WithSampleGate { get; set; }

    public (HardwareProfile Profile, IReadOnlyList<Finding> Findings) WithSample(HardwareProfile scanned, IReadOnlyList<ProcessCpu>? sample)
    {
        lock (_lock) WithSampleCalls++;
        WithSampleGate?.Task.Wait();
        return (scanned with { }, WithSampleFindings(Findings(Runs)));
    }

    public Facts BuildFacts(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignoredResults)
    {
        var facts = SamplePc.Copy(BaseFacts);
        if (!FindingFacts) return facts;
        foreach (var group in IgnoredResults.Without(findings, ignoredResults).GroupBy(x => x.Id))
        {
            facts.Set($"finding.{group.Key}.problem", group.Any(x => x.Status == FindingStatus.Problem));
            facts.Set($"finding.{group.Key}.status", group.First().Status.ToString().ToLowerInvariant());
        }
        if (WithSampleCalls > 0) SampleFacts(facts);
        return facts;
    }

    /// <summary>Held: reading the states waits until it is opened.</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    public TweakRead ReadStates(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignoredResults)
    {
        lock (_lock) Reads.Add(ignoredResults);
        ReadGate?.Task.Wait();
        var facts = BuildFacts(profile, findings, ignoredResults);
        var drift = DriftTweaks.Select(t => new DriftItem(t, new Optimizer.Core.Backup.TweakBackup { TweakId = t.Id }, "26100.1000", WindowsBuild)).ToList();
        return new TweakRead(facts, States, [], drift, Backups);
    }
}

/// <summary>Facts and tweak states of sample PCs, set directly (no scan, no detection).</summary>
internal static class SamplePc
{
    /// <summary>A desktop with a discrete GPU and 32 GB: Gaming fits it best.</summary>
    public static Facts Desktop() => Common().Set("system.laptop", false).Set("gpu.hasDiscrete", true).Set("memory.totalGb", 32.0);

    /// <summary>A desktop with integrated graphics and 4 GB: it offers no laptop profile, and Low-end PC fits it best.</summary>
    public static Facts LowEndDesktop() => Common().Set("system.laptop", false).Set("gpu.hasDiscrete", false).Set("memory.totalGb", 4.0);

    /// <summary>A laptop with a discrete GPU: it offers the laptop profiles.</summary>
    public static Facts Laptop() => Common().Set("system.laptop", true).Set("gpu.hasDiscrete", true).Set("memory.totalGb", 16.0);

    private static Facts Common() => new Facts().Set("elevated", true).Set("os.build", 26300);

    public static Facts Copy(Facts facts)
    {
        var copy = new Facts();
        foreach (var (name, value) in facts.All) copy.Set(name, value);
        return copy;
    }

    /// <summary>Every visible catalog tweak, off, recommended where its rule says so for a gaming PC.</summary>
    public static IReadOnlyList<TweakStatus> States() =>
        TweakCatalog.Current.Visible.Select(t => new TweakStatus(t, TweakState.NotApplied, t.Impact.Gaming, t.Impact.Effect, null, true, [], false)).ToList();

    /// <summary>A problem the plan fixes first (power.turboRestore), and that tweak's rules read (finding.F6.turbo.problem).</summary>
    public static readonly Finding Turbo = new()
    {
        Id = "F6.turbo", Kind = FindingKind.Finding, Status = FindingStatus.Problem, Impact = 3,
    };

    public static readonly Finding Passed = new()
    {
        Id = "F9.powerSaver", Kind = FindingKind.Finding, Status = FindingStatus.Ok, Impact = 2,
    };
}
