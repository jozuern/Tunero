using Optimizer.Core.Actions;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>
/// Read-only checks of the real system adapters on this machine (Category=Hardware). Nothing is written:
/// these only call the Read/Get side of each adapter and the engine's state detection.
/// </summary>
public class SystemApiReadTests(ITestOutputHelper output)
{
    private static readonly ActionContext Ctx = ReadOnlyContext.Create(ElevationInfo.Read().SessionUserSid);

    [Fact]
    [Trait("Category", "Hardware")]
    public void PowerSchemesAndValuesAreReadable()
    {
        var active = Ctx.Power.ActiveScheme();
        Assert.NotEqual(Guid.Empty, active);
        var schemes = Ctx.Power.Schemes();
        Assert.Contains(schemes, s => s.Id == active);
        var max = Ctx.Power.ReadAc(active, PowerAliases.Resolve("SUB_PROCESSOR"), PowerAliases.Resolve("PROCTHROTTLEMAX"));
        Assert.NotNull(max);
        output.WriteLine($"active {active}, {schemes.Count} schemes, PROCTHROTTLEMAX AC = {max}");
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void ServicesAndTasksAreReadable()
    {
        Assert.NotNull(Ctx.Services.GetStartType("DiagTrack"));
        Assert.Null(Ctx.Services.GetStartType("TuneroDoesNotExist"));
        var appraiser = Ctx.Tasks.IsEnabled(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser");
        output.WriteLine($"DiagTrack = {Ctx.Services.GetStartType("DiagTrack")}, Appraiser enabled = {appraiser?.ToString() ?? "missing"}");
        Assert.Null(Ctx.Tasks.IsEnabled(@"\Tunero\DoesNotExist"));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void DetectingEveryCatalogTweakReadsWithoutAnError()
    {
        var folder = TestFolders.Create("read");
        try
        {
            var engine = new TweakEngine(Ctx, new Backup.BackupStore(folder, secure: false), new FakeRestorePoints(), "test", 26300);
            var facts = new Facts().Set("os.build", 26300).Set("memory.totalGb", 32.0).Set("gpu.hasDiscrete", true).Set("gpu.supportsHags", true).Set("storage.ssdOnly", true);
            // Time per tweak too: detection runs after every scan, and a slow reader shows up here.
            foreach (var t in TweakCatalog.Current.Tweaks)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var s = engine.Detect(t, facts);
                output.WriteLine($"{watch.ElapsedMilliseconds,6} ms  {s.State,-18} {s.Tweak.Id}");
            }
            var all = System.Diagnostics.Stopwatch.StartNew();
            engine.DetectAll(TweakCatalog.Current.Tweaks, facts);
            output.WriteLine($"DetectAll: {all.ElapsedMilliseconds} ms");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
        // The assertion is that no read throws; which state each tweak has depends on this PC (one it lacks reads
        // Unsupported), so the states are only written to the output.
    }
}
