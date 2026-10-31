using Optimizer.App.Services;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

/// <summary>The window's <see cref="IScanSource"/>: this PC, through the app's services.</summary>
internal sealed class AppScanSource(AppServices services, CatalogData catalog) : IScanSource
{
    /// <summary>The scan pipeline (hardware probes, reads ahead for the tweak detection, checks), the same as --report uses.</summary>
    private ScanService ScanService => new(catalog, services.Context.Registry, services.BcdValues, services.Engine, services.Catalog.Visible);

    public Task<ScanResult> RunAsync(IProgress<string>? progress, HardwareProfile? previous, IReadOnlyList<ProcessCpu>? lastSample) =>
        ScanService.RunAsync(progress, previous, lastSample, waitForBackgroundSample: false);

    public (HardwareProfile Profile, IReadOnlyList<Finding> Findings) WithSample(HardwareProfile scanned, IReadOnlyList<ProcessCpu>? sample) =>
        ScanService.WithSample(scanned, sample);

    public Facts BuildFacts(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignoredResults) =>
        FactsBuilder.Build(profile, findings, catalog, services.Context.Registry, ignoredResults);

    /// <summary>Ignored results are not in the facts, so nothing is recommended for them.</summary>
    public TweakRead ReadStates(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignoredResults)
    {
        var engine = services.Engine;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var facts = FactsBuilder.Build(profile, findings, catalog, services.Context.Registry, ignoredResults);
        var states = engine.DetectAll(services.Catalog.Visible, facts);
        var device = engine.DetectAll(DeviceTweaks.Build(profile), facts);
        Log.Info("scan", "tweak states read", new { ms = watch.ElapsedMilliseconds });
        // The states just read are not read again (runtime tweaks of other pages are).
        var known = states.Concat(device).GroupBy(s => s.Tweak.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().State, StringComparer.Ordinal);
        var kept = new List<string>();
        var drift = engine.CheckDrift(facts, known, kept);
        // Changes still in place after a Windows update: a later reset is then not blamed on that update.
        services.Session.NoteKept(kept);
        return new TweakRead(facts, states, device, drift, services.Store.All().Count);
    }

    public string WindowsBuild => services.Os.BuildString;
}
