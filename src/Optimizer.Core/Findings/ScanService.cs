using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Findings;

/// <param name="PendingSample">The 3 second background CPU sample when the scan did not wait for it; complete the result with <see cref="ScanService.WithSample"/>.</param>
public sealed record ScanResult(HardwareProfile Profile, IReadOnlyList<Finding> Findings, Task<IReadOnlyList<ProcessCpu>>? PendingSample);

/// <summary>
/// One scan, in the order the app needs it and the same for the window and the --report switch: the tweak reads that
/// start tools (PowerShell, DISM, bcdedit) begin beside the hardware probes, the background CPU sample starts after those
/// tools ended (they would count as background activity), the NVIDIA reads of the detection start once the scan's own
/// NVIDIA read is closed, and the checks are evaluated on the result.
/// </summary>
/// <param name="engine">Reads ahead for the tweak detection that follows; null when no detection follows (the report).</param>
/// <param name="today">Today's date for the age checks (<see cref="FindingEngine"/>); null: the PC's clock.</param>
public sealed class ScanService(CatalogData catalog, IRegistryRoots? registry, Func<IReadOnlyDictionary<string, string>?>? bcd, TweakEngine? engine = null,
    IEnumerable<TweakDefinition>? tweaks = null, Func<DateTime>? today = null)
{
    /// <param name="previous">The last result, after a change made by this app: slow parts no change can affect are reused.</param>
    /// <param name="lastSample">The last finished background CPU sample, reused by a quick rescan after a change.</param>
    /// <param name="waitForBackgroundSample">False: return without the CPU sample and leave it in <see cref="ScanResult.PendingSample"/>.</param>
    public async Task<ScanResult> RunAsync(IProgress<string>? progress = null, HardwareProfile? previous = null, IReadOnlyList<ProcessCpu>? lastSample = null,
        bool waitForBackgroundSample = true, CancellationToken ct = default)
    {
        var visible = tweaks?.ToList() ?? [];
        var toolReads = engine?.PrefetchReads(visible, EarlyRead.WithScan);
        Observe(toolReads, "early tool reads");
        var scanner = new HardwareScanner(catalog);
        var scanned = await scanner.ScanAsync(progress, ct, previous, waitForBackgroundSample, toolReads).ConfigureAwait(false);
        Observe(engine?.PrefetchReads(visible, EarlyRead.AfterScan), "early NVIDIA reads");
        // A quick rescan after a change reuses the last finished sample.
        if (scanned.Extras is { BackgroundCpu: null } && scanner.PendingBackgroundSample is null && lastSample is { } last)
            scanned = HardwareScanner.WithBackgroundSample(scanned, last);
        var step = System.Diagnostics.Stopwatch.StartNew();
        var findings = Evaluate(scanned);
        Log.Debug("scan", "findings evaluated", new { ms = step.ElapsedMilliseconds });
        return new ScanResult(scanned, findings, scanner.PendingBackgroundSample);
    }

    public IReadOnlyList<Finding> Evaluate(HardwareProfile profile) => new FindingEngine(catalog, registry, bcd, today).Evaluate(profile);

    /// <summary>The result with the background CPU sample that finished after it, and the checks evaluated again.</summary>
    public (HardwareProfile Profile, IReadOnlyList<Finding> Findings) WithSample(HardwareProfile scanned, IReadOnlyList<ProcessCpu>? sample)
    {
        var completed = HardwareScanner.WithBackgroundSample(scanned, sample);
        return (completed, Evaluate(completed));
    }

    /// <summary>A read ahead that fails only costs time later: it is logged, never lost as an unobserved task exception.</summary>
    private static void Observe(Task? task, string what) =>
        task?.ContinueWith(t => Log.Warn("scan", $"{what} failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
}
