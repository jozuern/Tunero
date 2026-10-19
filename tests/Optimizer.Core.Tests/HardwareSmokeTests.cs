using System.Text.Json;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>Runs the real scanner on this machine (read-only) and prints the results. Never fails on hardware differences.</summary>
public class HardwareSmokeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task ScanThisPc()
    {
        var profile = await new HardwareScanner(CatalogData.Current).ScanAsync();
        output.WriteLine(JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var f in new FindingEngine(CatalogData.Current).Evaluate(profile))
            output.WriteLine($"{f.Status,-11} ⚡{f.Impact?.ToString() ?? "-"} {f.Key} {f.Subject} {f.Variant}");
        Assert.NotNull(profile.Os);
    }

    /// <summary>
    /// The Default profile's hive passes the admin-only check on a real Windows (read-only: the path and its ACLs). If
    /// this fails, Copy to new accounts would refuse on every PC.
    /// </summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void TheDefaultProfileHiveIsFoundAndAdminOnly()
    {
        var file = Optimizer.Core.Platform.DefaultProfileHive.FilePath();
        output.WriteLine(file ?? "(none)");
        Assert.NotNull(file);
        Assert.EndsWith(@"\Default\NTUSER.DAT", file, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The pipeline the window and --report share: findings come with the scan, the CPU sample is waited for.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task ScanServiceRunsTheWholePipeline()
    {
        var result = await new ScanService(CatalogData.Current, null, null).RunAsync();
        Assert.NotNull(result.Profile.Os);
        Assert.Null(result.PendingSample);
        Assert.NotEmpty(result.Findings);
        Assert.Contains(result.Findings, f => f.Id == Optimizer.Core.Findings.Checks.LeftoverCheck.Id);
    }

    /// <summary>The Live readings process snapshot (read-only) finds the test host with its memory, CPU time and this thread.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void ProcessSnapshotFindsItself()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var snapshot = Optimizer.Core.Activity.ProcessSnapshotReader.Read();
        output.WriteLine($"{snapshot.Processes.Count} processes");
        var me = Assert.Single(snapshot.Processes, p => p.Pid == Environment.ProcessId);
        Assert.Equal(self.ProcessName + ".exe", me.ImageName, ignoreCase: true);
        Assert.True(me.PrivateWorkingSet > 1_000_000, $"private working set {me.PrivateWorkingSet}");
        Assert.True(me.CpuTime100ns > 0);
        Assert.Contains(Optimizer.Core.Activity.ProcessSnapshotReader.CurrentThreadId(), me.ThreadIds);
        Assert.DoesNotContain(snapshot.Processes, p => p.Pid == 0);
        Assert.EndsWith(self.ProcessName + ".exe", Optimizer.Core.Activity.ProcessSnapshotReader.ImagePath(Environment.ProcessId), StringComparison.OrdinalIgnoreCase);
        Assert.False(Optimizer.Core.Activity.ProcessSnapshotReader.IsCritical(Environment.ProcessId));
    }

    /// <summary>DXGI lists the hardware graphics adapters with the LUIDs the GPU Engine counters name (read-only).</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void GpuAdaptersRead()
    {
        var adapters = Optimizer.Core.Activity.GpuAdapters.Read();
        foreach (var a in adapters) output.WriteLine($"0x{a.Luid:X16} {a.Name}");
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.All(adapters, a => Assert.NotEqual(0, a.Luid));
        using var reader = Optimizer.Core.Activity.GpuEngineReader.TryOpen();
        Assert.NotNull(reader);
        reader.Read();
        Thread.Sleep(500);
        var samples = reader.Read();
        output.WriteLine($"{samples.Count} busy engines");
        Assert.All(samples, s => Assert.True(s.Percent > 0));
    }

    /// <summary>
    /// The drive cards of Live readings come from LibreHardwareMonitor; their identifiers must carry the Windows disk number
    /// the disk trace reports (Win32_DiskDrive.Index). Storage only, read-only.
    /// </summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void LibreDiskIdsAreDiskNumbers()
    {
        var computer = new LibreHardwareMonitor.Hardware.Computer { IsStorageEnabled = true };
        computer.Open();
        try
        {
            var indexes = Wmi.Query("SELECT Index FROM Win32_DiskDrive").Select(r => r.Int("Index")).ToHashSet();
            var ids = computer.Hardware.Select(h => h.Identifier.ToString()).ToList();
            foreach (var id in ids) output.WriteLine($"{id} {string.Join(",", indexes)}");
            // LibreHardwareMonitor opens the drives only with administrator rights (the app runs elevated).
            if (!Environment.IsPrivilegedProcess)
            {
                output.WriteLine("not elevated: no drives to compare");
                return;
            }
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.Contains(Optimizer.Core.Activity.DeviceMapping.DiskNumber(id), indexes));
        }
        finally
        {
            computer.Close();
        }
    }
}
