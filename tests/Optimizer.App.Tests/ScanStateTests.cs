using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// The window's scan and plan state without the window: one scan at a time, the background CPU sample, the profile
/// after a scan, ignored results and recommendations, and the changes Windows reset. The system is a fake source with
/// canned results (<see cref="FakeScanSource"/>), settings are saved through a counter, and every test runs on one
/// thread with a synchronization context, as the window does (<see cref="SingleThread"/>).
/// </summary>
public sealed class ScanStateTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 8, 0, 0);

    // The Windows version the fake reports is already seen: saving it does not count.
    private readonly AppSettings _settings = new() { Profile = "gaming", LastSeenWindowsVersion = "26300.1000" };
    private readonly FakeScanSource _source = new();
    private readonly List<ScanUpdate> _updates = [];
    private readonly HashSet<int> _updateThreads = [];
    private int _saves;
    private bool _changeBusy;

    private ScanState State()
    {
        var state = new ScanState(CatalogData.Current, _settings, () => _saves++, _source, () => _changeBusy, () => Now);
        state.Updated += update =>
        {
            _updates.Add(update);
            _updateThreads.Add(Environment.CurrentManagedThreadId);
        };
        return state;
    }

    private static Finding LowDiskSpace => new()
    {
        Id = "F17.lowDiskSpace", Kind = FindingKind.Finding, Status = FindingStatus.Problem, Impact = 2,
    };

    private static IEnumerable<string> PlanIds(ScanState state) => state.Plan.Items.Concat(state.Plan.Excluded).Select(i => i.Tweak.Id);

    // The scan loop.

    [Fact]
    public void ASecondScanWhileOneRunsJoinsIt() => SingleThread.Run(async () =>
    {
        var state = State();
        var gate = _source.Hold(1);
        var first = state.ScanAsync();
        await _source.Started(1);
        Assert.Same(first, state.ScanAsync());
        Assert.Same(first, state.ScanAsync(reuseSlowParts: true));
        Assert.Same(first, state.ScanInProgress);
        gate.SetResult();
        await first;
    });

    [Fact]
    public void RequestsDuringARunGiveExactlyOneMoreScan() => SingleThread.Run(async () =>
    {
        var state = State();
        var gate = _source.Hold(1);
        var scan = state.ScanAsync();
        await _source.Started(1);
        _ = state.ScanAsync();
        _ = state.ScanAsync();
        _ = state.ScanAsync();
        gate.SetResult();
        await scan;
        Assert.Equal(2, _source.Runs);
        Assert.Equal(1, _source.MaxRunning);
        Assert.Equal([ScanUpdate.Started, ScanUpdate.StatesRead, ScanUpdate.Scanned, ScanUpdate.Ended,
            ScanUpdate.Started, ScanUpdate.StatesRead, ScanUpdate.Scanned, ScanUpdate.Ended], _updates);
    });

    [Fact]
    public void ARequestDuringTheFollowUpGivesAThird() => SingleThread.Run(async () =>
    {
        var state = State();
        var gate1 = _source.Hold(1);
        var gate2 = _source.Hold(2);
        var scan = state.ScanAsync();
        await _source.Started(1);
        _ = state.ScanAsync();
        gate1.SetResult();
        await _source.Started(2);
        Assert.Same(scan, state.ScanAsync());
        gate2.SetResult();
        await scan;
        Assert.Equal(3, _source.Runs);
        Assert.Equal(1, _source.MaxRunning);
    });

    [Fact]
    public void TheNewestResultWins() => SingleThread.Run(async () =>
    {
        var state = State();
        var gate = _source.Hold(1);
        var scan = state.ScanAsync();
        await _source.Started(1);
        _ = state.ScanAsync();
        gate.SetResult();
        await scan;
        Assert.Same(_source.Profiles[2], state.Profile);
        Assert.Equal(2, _updates.Count(u => u == ScanUpdate.Scanned));
    });

    [Fact]
    public void IsScanningCoversTheScan() => SingleThread.Run(async () =>
    {
        var state = State();
        bool? scanningAtStart = null, scanningAtResult = null, scanningAtEnd = null;
        state.Updated += update =>
        {
            if (update == ScanUpdate.Started) scanningAtStart = state.IsScanning;
            if (update == ScanUpdate.Scanned) scanningAtResult = state.IsScanning;
            if (update == ScanUpdate.Ended) scanningAtEnd = state.IsScanning;
        };
        var gate = _source.Hold(1);
        var scan = state.ScanAsync();
        await _source.Started(1);
        Assert.True(state.IsScanning);
        Assert.False(state.ScanInProgress.IsCompleted);
        gate.SetResult();
        await scan;
        Assert.False(state.IsScanning);
        Assert.True(state.ScanInProgress.IsCompleted);
        Assert.Equal((true, true, false), (scanningAtStart, scanningAtResult, scanningAtEnd));
    });

    /// <summary>The window's dispatcher owns the state: every update and every continuation comes back to its thread.</summary>
    [Fact]
    public void UpdatesComeOnTheCallersThread() => SingleThread.Run(async () =>
    {
        var thread = Environment.CurrentManagedThreadId;
        var sample = new TaskCompletionSource<IReadOnlyList<ProcessCpu>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Sample = run => run == 1 ? sample.Task : null;
        var probes = new List<int>();
        var state = State();
        state.Probed += _ => probes.Add(Environment.CurrentManagedThreadId);
        await state.ScanAsync();
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        _ = Task.Run(() => sample.SetResult([]));
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.FindingsChanged), "the sample");
        Assert.Equal([thread], _updateThreads);
        Assert.Equal([thread], probes.Distinct());
    });

    [Fact]
    public void TheWindowsRefreshAfterAScanIsPartOfIt() => SingleThread.Run(async () =>
    {
        var state = State();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        state.AfterScan = () =>
        {
            refreshes++;
            Assert.Equal(ScanUpdate.Ended, _updates[^1]);
            return refreshed.Task;
        };
        var scan = state.ScanAsync();
        await SingleThread.Until(() => refreshes == 1, "the refresh");
        Assert.False(scan.IsCompleted);
        refreshed.SetResult();
        await scan;
    });

    [Fact]
    public void AChangeRescanReusesTheSlowPartsOnce() => SingleThread.Run(async () =>
    {
        var state = State();
        await state.ScanAsync();
        var first = state.Profile;
        await state.ScanAsync(reuseSlowParts: true);
        await state.ScanAsync();
        Assert.Null(_source.RunArgs[0].Previous);
        Assert.Same(first, _source.RunArgs[1].Previous);
        Assert.Null(_source.RunArgs[2].Previous);
    });

    [Fact]
    public void AReuseRequestDuringAFullScanReachesTheFollowUp() => SingleThread.Run(async () =>
    {
        var state = State();
        await state.ScanAsync();
        var gate = _source.Hold(2);
        var scan = state.ScanAsync();
        await _source.Started(2);
        _ = state.ScanAsync(reuseSlowParts: true);
        gate.SetResult();
        await scan;
        Assert.Null(_source.RunArgs[1].Previous);
        Assert.Same(_source.Profiles[2], _source.RunArgs[2].Previous);
    });

    [Fact]
    public void AFailedScanKeepsTheLastResult() => SingleThread.Run(async () =>
    {
        _source.Findings = _ => [SamplePc.Turbo];
        var state = State();
        await state.ScanAsync();
        var (profile, plan, facts) = (state.Profile, state.Plan, state.Facts);
        _source.Fails = run => run == 2;
        _updates.Clear();
        await state.ScanAsync();
        Assert.True(state.ScanFailed);
        Assert.Equal([ScanUpdate.Started, ScanUpdate.Ended], _updates);
        Assert.Same(profile, state.Profile);
        Assert.Same(plan, state.Plan);
        Assert.Same(facts, state.Facts);

        _source.Fails = _ => false;
        await state.ScanAsync();
        Assert.False(state.ScanFailed);
    });

    [Fact]
    public void AFailureInTheFollowUpEndsTheLoop() => SingleThread.Run(async () =>
    {
        var state = State();
        _source.Fails = run => run == 2;
        var gate = _source.Hold(1);
        var scan = state.ScanAsync();
        await _source.Started(1);
        _ = state.ScanAsync();
        gate.SetResult();
        await scan;
        Assert.Equal(2, _source.Runs);
        Assert.True(state.ScanFailed);
        Assert.True(state.ScanInProgress.IsCompleted);
        Assert.Same(_source.Profiles[1], state.Profile);
    });

    [Fact]
    public void LastScanTimeUsesTheClock() => SingleThread.Run(async () =>
    {
        var state = State();
        Assert.Null(state.LastScanTime);
        await state.ScanAsync();
        Assert.Equal(Now, state.LastScanTime);
    });

    // The background CPU sample.

    private TaskCompletionSource<IReadOnlyList<ProcessCpu>> SampleOfRun(int run)
    {
        var sample = new TaskCompletionSource<IReadOnlyList<ProcessCpu>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Sample = r => r == run ? sample.Task : null;
        return sample;
    }

    private static readonly Finding BackgroundCpu = new()
    {
        Id = "F11.backgroundCpu", Kind = FindingKind.Advisor, Status = FindingStatus.Info,
    };

    [Fact]
    public void TheSampleUpdatesFindingsWithoutReadingTweaks() => SingleThread.Run(async () =>
    {
        var sample = SampleOfRun(1);
        _source.WithSampleFindings = f => [.. f, BackgroundCpu];
        var state = State();
        await state.ScanAsync();
        var scanned = state.Profile;
        sample.SetResult([new ProcessCpu("game.exe", 42, 0.3, 1 << 20)]);
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.FindingsChanged), "the sample");
        Assert.Equal(1, _source.WithSampleCalls);
        Assert.Single(_source.Reads);
        Assert.Contains(BackgroundCpu, state.Findings);
        Assert.NotSame(scanned, state.Profile);
        Assert.Equal("info", state.Facts.Get("finding.F11.backgroundCpu.status"));
        Assert.Equal([ScanUpdate.SampleAdded, ScanUpdate.FindingsChanged], _updates.TakeLast(2));
        Assert.Single(state.LastBackgroundSample!);
    });

    [Fact]
    public void ASampleForAReplacedScanIsDropped() => SingleThread.Run(async () =>
    {
        var sample = SampleOfRun(1);
        var state = State();
        await state.ScanAsync();
        var gate = _source.Hold(2);
        var scan = state.ScanAsync();
        await _source.Started(2);
        sample.SetResult([]);
        await SingleThread.Until(() => state.LastBackgroundSample is not null, "the sample");
        gate.SetResult();
        await scan;
        Assert.Equal(0, _source.WithSampleCalls);
        Assert.Same(_source.Profiles[2], state.Profile);
        Assert.DoesNotContain(ScanUpdate.SampleAdded, _updates);
    });

    [Fact]
    public void AFaultedSampleChangesNothing() => SingleThread.Run(async () =>
    {
        var sample = SampleOfRun(1);
        var state = State();
        await state.ScanAsync();
        var profile = state.Profile;
        var count = _updates.Count;
        sample.SetException(new InvalidOperationException("no counters"));
        await Task.Delay(100);
        Assert.Null(state.LastBackgroundSample);
        Assert.Equal(count, _updates.Count);
        Assert.Same(profile, state.Profile);
    });

    [Fact]
    public void AFinishedSampleFeedsTheQuickRescan() => SingleThread.Run(async () =>
    {
        var sample = SampleOfRun(1);
        var state = State();
        await state.ScanAsync();
        IReadOnlyList<ProcessCpu> processes = [new ProcessCpu("game.exe", 42, 0.3, 1 << 20)];
        sample.SetResult(processes);
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.FindingsChanged), "the sample");
        await state.ScanAsync(reuseSlowParts: true);
        Assert.Null(_source.RunArgs[0].LastSample);
        Assert.Same(processes, _source.RunArgs[1].LastSample);
    });

    [Fact]
    public void ASampleATweakReadsRereadsStates() => SingleThread.Run(async () =>
    {
        var sample = SampleOfRun(1);
        _source.SampleFacts = f => f.Set("os.build", 26301);
        var state = State();
        await state.ScanAsync();
        sample.SetResult([]);
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.TweaksRead), "the states read again");
        Assert.Equal(2, _source.Reads.Count);
        Assert.Equal(26301, state.Facts.Get("os.build"));
        Assert.DoesNotContain(ScanUpdate.FindingsChanged, _updates);
    });

    [Fact]
    public void ASampleDuringAChangeLeavesTheStates() => SingleThread.Run(async () =>
    {
        var sample = SampleOfRun(1);
        _source.SampleFacts = f => f.Set("os.build", 26301);
        var state = State();
        await state.ScanAsync();
        _changeBusy = true;
        sample.SetResult([]);
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.SampleAdded), "the sample");
        await Task.Delay(50);
        Assert.Single(_source.Reads);
        Assert.Equal(26300, state.Facts.Get("os.build"));
        Assert.NotSame(_source.Profiles[1], state.Profile);
        Assert.DoesNotContain(ScanUpdate.TweaksRead, _updates);
        Assert.DoesNotContain(ScanUpdate.FindingsChanged, _updates);
    });

    // Which fact changes make the tweak states worth reading again.

    private static bool DependOn(Facts before, Facts after) =>
        ScanState.TweaksDependOn(before, after, TweakCatalog.Current.Visible, CatalogData.Current.Profiles);

    [Fact]
    public void NoChangedFactIsNoDependency() => Assert.False(DependOn(SamplePc.Desktop(), SamplePc.Desktop()));

    [Fact]
    public void AHardwareFactAlwaysCounts() => Assert.True(DependOn(SamplePc.Desktop(), SamplePc.Desktop().Set("os.build", 26301)));

    [Fact]
    public void AFindingNoTweakReadsDoesNotCount() =>
        Assert.False(DependOn(SamplePc.Desktop(), SamplePc.Desktop().Set("finding.F11.backgroundCpu.status", "info")));

    /// <summary>Result ids have a dot of their own (F31.memoryIntegrity): the whole id counts, not its first part.</summary>
    [Fact]
    public void AFindingATweakFixesCounts()
    {
        var fixer = TweakCatalog.Current.Visible.First(t => t.Fixes.Count > 0 && t.Conditions().All(c => !c.ReferencedFacts().Any(f => f.StartsWith("finding.", StringComparison.Ordinal))));
        var id = fixer.Fixes[0];
        Assert.DoesNotContain(CatalogData.Current.Profiles.Profiles.SelectMany(p => p.Tweaks), pt => pt.RecommendWhen?.ReferencedFacts().Contains($"finding.{id}.problem") == true);
        Assert.True(ScanState.TweaksDependOn(SamplePc.Desktop(), SamplePc.Desktop().Set($"finding.{id}.problem", true), [fixer], CatalogData.Current.Profiles));
    }

    [Fact]
    public void AFindingAProfileConditionReadsCounts() =>
        Assert.True(DependOn(SamplePc.Desktop(), SamplePc.Desktop().Set("finding.F17.lowDiskSpace.problem", true)));

    // The profile after a scan.

    [Fact]
    public void NothingSavedSavesTheSuggestionOnce() => SingleThread.Run(async () =>
    {
        _settings.Profile = null;
        _source.BaseFacts = SamplePc.LowEndDesktop();
        var state = State();
        await state.ScanAsync();
        Assert.Equal("lowEnd", state.Usage.Id);
        Assert.Equal("lowEnd", _settings.Profile);
        Assert.Equal(1, _saves);
        Assert.True(state.ProfileAutoSet);

        await state.ScanAsync();
        Assert.Equal(1, _saves);
        Assert.Equal("lowEnd", state.Usage.Id);
    });

    /// <summary>A laptop profile copied to a desktop: Gaming for the session; what is saved stays for the laptop.</summary>
    [Fact]
    public void ALaptopProfileOnADesktopIsGamingForTheSessionOnly() => SingleThread.Run(async () =>
    {
        _settings.Profile = "laptopGaming";
        _source.BaseFacts = SamplePc.LowEndDesktop();
        var state = State();
        await state.ScanAsync();
        Assert.Equal("gaming", state.Usage.Id);
        Assert.Equal("lowEnd", state.Suggested?.Id);
        Assert.Equal("laptopGaming", _settings.Profile);
        Assert.Equal(0, _saves);
        Assert.False(state.ProfileAutoSet);

        _source.BaseFacts = SamplePc.Laptop();
        await state.ScanAsync();
        Assert.Equal("laptopGaming", state.Usage.Id);
        Assert.Equal(0, _saves);
    });

    [Fact]
    public void AnOfferedOverrideIsUsedWithoutSaving() => SingleThread.Run(async () =>
    {
        var state = State();
        state.ProfileOverride = "office";
        await state.ScanAsync();
        Assert.Equal("office", state.Usage.Id);
        Assert.Equal("gaming", _settings.Profile);
        Assert.Equal(0, _saves);
    });

    /// <summary>--profile with a profile this PC does not offer: the window opens with the saved one (the command line stops).</summary>
    [Fact]
    public void AnOverrideThisPcDoesNotOfferFallsBackToTheSaved() => SingleThread.Run(async () =>
    {
        _settings.Profile = "office";
        var state = State();
        state.ProfileOverride = "battery";
        await state.ScanAsync();
        Assert.Equal("office", state.Usage.Id);
        Assert.Equal(0, _saves);
    });

    [Fact]
    public void PickingAProfileSavesAndRatesAgainWithoutReading() => SingleThread.Run(async () =>
    {
        _settings.Profile = null;
        var state = State();
        await state.ScanAsync();
        Assert.True(state.ProfileAutoSet);
        var (saves, plan) = (_saves, state.Plan);
        state.SetProfile("office", save: true);
        Assert.Equal("office", state.Usage.Id);
        Assert.Equal("office", _settings.Profile);
        Assert.Equal(saves + 1, _saves);
        Assert.False(state.ProfileAutoSet);
        Assert.NotSame(plan, state.Plan);
        Assert.Single(_source.Reads);
    });

    [Fact]
    public void ASessionProfileIsNotSaved() => SingleThread.Run(async () =>
    {
        var state = State();
        await state.ScanAsync();
        state.SetProfile("office", save: false);
        Assert.Equal("office", state.Usage.Id);
        Assert.Equal("gaming", _settings.Profile);
        Assert.Equal(0, _saves);
    });

    // Ignored results and recommendations.

    [Fact]
    public void IgnoringAResultReadsTheStatesWithoutIt() => SingleThread.Run(async () =>
    {
        _source.Findings = _ => [SamplePc.Turbo];
        var state = State();
        await state.ScanAsync();
        Assert.Equal("power.turboRestore", state.Plan.Items[0].Tweak.Id);
        Assert.Equal(true, state.Facts.Get("finding.F6.turbo.problem"));

        await state.SetResultIgnoredAsync(SamplePc.Turbo, true);
        var key = IgnoredResults.Key(SamplePc.Turbo);
        Assert.Equal([key], _settings.IgnoredFindings);
        Assert.Equal(1, _saves);
        Assert.Equal(2, _source.Reads.Count);
        Assert.Contains(key, _source.Reads[^1]);
        Assert.Null(state.Facts.Get("finding.F6.turbo.problem"));
        // Out of the plan and the list, still under "Ignored" with the profile's impact.
        Assert.DoesNotContain(state.Plan.Items, i => i.FixesFinding?.Id == SamplePc.Turbo.Id);
        Assert.DoesNotContain(state.ProfiledFindings, f => f.Id == SamplePc.Turbo.Id);
        Assert.Equal(SamplePc.Turbo.Impact, Assert.Single(state.IgnoredFindings).Impact);

        await state.SetResultIgnoredAsync(SamplePc.Turbo, true);
        Assert.Equal([key], _settings.IgnoredFindings);
    });

    [Fact]
    public void APassedCheckCannotBeIgnored() => SingleThread.Run(async () =>
    {
        _source.Findings = _ => [SamplePc.Passed];
        var state = State();
        await state.ScanAsync();
        await state.SetResultIgnoredAsync(SamplePc.Passed, true);
        Assert.Empty(_settings.IgnoredFindings);
        Assert.Equal(0, _saves);
        Assert.Single(_source.Reads);
    });

    /// <summary>A settings file of an older version can hold a key twice: Stop ignoring removes both.</summary>
    [Fact]
    public void StopIgnoringRemovesEveryCopy() => SingleThread.Run(async () =>
    {
        var key = IgnoredResults.Key(SamplePc.Turbo);
        _settings.IgnoredFindings.AddRange([key, key]);
        _source.Findings = _ => [SamplePc.Turbo];
        var state = State();
        await state.ScanAsync();
        Assert.DoesNotContain(state.ProfiledFindings, f => f.Id == SamplePc.Turbo.Id);

        await state.SetResultIgnoredAsync(SamplePc.Turbo, false);
        Assert.Empty(_settings.IgnoredFindings);
        Assert.Equal(2, _source.Reads.Count);
        Assert.Empty(_source.Reads[^1]);
        Assert.Contains(state.ProfiledFindings, f => f.Id == SamplePc.Turbo.Id);
        Assert.Equal("power.turboRestore", state.Plan.Items[0].Tweak.Id);
    });

    [Fact]
    public void IgnoringATweakReadsNothing() => SingleThread.Run(async () =>
    {
        var state = State();
        await state.ScanAsync();
        var tweak = state.Plan.Items[0].Tweak.Id;
        state.SetTweakIgnored(tweak, true);
        state.SetTweakIgnored(tweak, true);
        Assert.Equal([tweak], _settings.IgnoredTweaks);
        Assert.Single(_source.Reads);
        Assert.DoesNotContain(tweak, PlanIds(state));
        Assert.True(state.Profiled.Single(p => p.Tweak.Id == tweak).Ignored);

        state.SetTweakIgnored(tweak, false);
        Assert.Empty(_settings.IgnoredTweaks);
        Assert.Contains(tweak, state.Plan.Items.Select(i => i.Tweak.Id));
        Assert.Equal(3, _saves);
    });

    /// <summary>
    /// A result ignored while a scan runs: the scan read the facts before the ignore, so it reads the states again with
    /// it, and a tweak its rules recommend for that result (Storage Sense for low disk space, Office) is not recommended
    /// after the scan. While the scan runs nothing is read beside it.
    /// </summary>
    [Fact]
    public void AResultIgnoredDuringAScanHoldsAfterIt() => SingleThread.Run(async () =>
    {
        _settings.Profile = "office";
        _source.Findings = _ => [LowDiskSpace];
        var state = State();
        await state.ScanAsync();
        Assert.Contains("storage.storageSenseOn", PlanIds(state));

        var gate = _source.Hold(2);
        var scan = state.ScanAsync();
        await _source.Started(2);
        await state.SetResultIgnoredAsync(LowDiskSpace, true);
        gate.SetResult();
        await scan;
        var key = IgnoredResults.Key(LowDiskSpace);
        // The first scan, the second before the ignore, and the second again with it; nothing beside the scan.
        Assert.Equal(3, _source.Reads.Count);
        Assert.Contains(key, _source.Reads[^1]);
        Assert.Null(state.Facts.Get("finding.F17.lowDiskSpace.problem"));
        Assert.DoesNotContain("storage.storageSenseOn", PlanIds(state));
        Assert.Equal(1, _source.MaxRunning);
    });

    /// <summary>
    /// A result ignored while the background sample is merged, and the sample lands while that read runs: the read is
    /// dropped (its profile was replaced) and the facts built with the sample still hold the result, so the states are
    /// read again with it.
    /// </summary>
    [Fact]
    public void AResultIgnoredDuringTheSampleHoldsAfterIt() => SingleThread.Run(async () =>
    {
        _settings.Profile = "office";
        _source.Findings = _ => [LowDiskSpace];
        var sample = SampleOfRun(1);
        var state = State();
        await state.ScanAsync();
        _source.WithSampleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sample.SetResult([]);
        await SingleThread.Until(() => _source.WithSampleCalls == 1, "the sample");
        var ignore = state.SetResultIgnoredAsync(LowDiskSpace, true);
        await SingleThread.Until(() => _source.Reads.Count == 2, "the read after the ignore");
        _source.WithSampleGate.SetResult();
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.SampleAdded), "the sample merged");
        _source.ReadGate.SetResult();
        await ignore;
        await SingleThread.Until(() => _updates.Contains(ScanUpdate.TweaksRead) || _updates.Contains(ScanUpdate.FindingsChanged), "the sample's lists");
        Assert.Contains(IgnoredResults.Key(LowDiskSpace), _source.Reads[^1]);
        Assert.Null(state.Facts.Get("finding.F17.lowDiskSpace.problem"));
        Assert.DoesNotContain("storage.storageSenseOn", PlanIds(state));
    });

    // Changes Windows reset, and the notice after a Windows update.

    [Fact]
    public void OnlyBatchSafeDriftIsReappliedTogether() => SingleThread.Run(async () =>
    {
        var safe = TweakCatalog.Current.Visible.First(t => t.IsBatchSafe);
        var expert = TweakCatalog.Current.Visible.First(t => t.EffectiveRisk == Risk.Expert && !t.IsBootCritical);
        var bootCritical = TweakCatalog.Current.Visible.First(t => t.IsBootCritical);
        _source.DriftTweaks.AddRange([safe, expert, bootCritical]);
        var state = State();
        await state.ScanAsync();
        var (together, individually) = state.DriftToReapply();
        Assert.Equal([safe], together);
        Assert.Equal(2, individually);
    });

    [Fact]
    public void NoDriftNothingToReapply() => SingleThread.Run(async () =>
    {
        var state = State();
        await state.ScanAsync();
        var (together, individually) = state.DriftToReapply();
        Assert.Empty(together);
        Assert.Equal(0, individually);
    });

    [Fact]
    public void AllChangesSurvivedAWindowsUpdate() => SingleThread.Run(async () =>
    {
        _settings.LastSeenWindowsVersion = "26300.900";
        _source.Backups = 3;
        var state = State();
        await state.ScanAsync();
        Assert.Equal(new WindowsUpdateNotice("26300.900", "26300.1000", 3), state.UpdateNotice);
        Assert.Equal("26300.1000", _settings.LastSeenWindowsVersion);
        Assert.Equal(1, _saves);

        await state.ScanAsync();
        Assert.Equal(1, _saves);
    });

    [Fact]
    public void AResetAfterAnUpdateGivesNoAllGoodNotice() => SingleThread.Run(async () =>
    {
        _settings.LastSeenWindowsVersion = "26300.900";
        _source.Backups = 3;
        _source.DriftTweaks.Add(TweakCatalog.Current.Visible.First(t => t.IsBatchSafe));
        var state = State();
        await state.ScanAsync();
        Assert.Null(state.UpdateNotice);
        Assert.Single(state.Drift);
        Assert.Equal("26300.1000", _settings.LastSeenWindowsVersion);
    });

    [Fact]
    public void NoBackupsNoNotice() => SingleThread.Run(async () =>
    {
        _settings.LastSeenWindowsVersion = "26300.900";
        var state = State();
        await state.ScanAsync();
        Assert.Null(state.UpdateNotice);
    });

    [Fact]
    public void TheSameBuildSavesNothing() => SingleThread.Run(async () =>
    {
        _source.Backups = 3;
        var state = State();
        await state.ScanAsync();
        Assert.Null(state.UpdateNotice);
        Assert.Equal(0, _saves);
    });
}
