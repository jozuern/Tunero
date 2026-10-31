using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

/// <summary>
/// What <see cref="ScanState"/> reads from the system: the scan pipeline, the facts and every tweak's state. The window's
/// version is <see cref="AppScanSource"/>; tests give canned results. Every method may run off the UI thread.
/// </summary>
internal interface IScanSource
{
    /// <summary>The scan without waiting for the 3 second background CPU sample (<see cref="ScanResult.PendingSample"/>).</summary>
    Task<ScanResult> RunAsync(IProgress<string>? progress, HardwareProfile? previous, IReadOnlyList<ProcessCpu>? lastSample);

    /// <summary>The result with the background CPU sample that finished after it, and the checks evaluated again.</summary>
    (HardwareProfile Profile, IReadOnlyList<Finding> Findings) WithSample(HardwareProfile scanned, IReadOnlyList<ProcessCpu>? sample);

    /// <summary>The facts the tweak conditions read, without the ignored results.</summary>
    Facts BuildFacts(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignoredResults);

    /// <summary>Every tweak's state, the facts it was read with (without the ignored results), the changes Windows reset and the number of backups.</summary>
    TweakRead ReadStates(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignoredResults);

    /// <summary>The Windows version this PC runs now ("26300.9550").</summary>
    string WindowsBuild { get; }
}

/// <summary>Every tweak's state, the facts it was read with, the changes Windows reset, and the number of backups.</summary>
internal sealed record TweakRead(Facts Facts, IReadOnlyList<TweakStatus> Catalog, IReadOnlyList<TweakStatus> Device,
    IReadOnlyList<DriftItem> Drift, int Backups);
