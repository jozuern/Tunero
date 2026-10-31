using Optimizer.Core.Activity;

namespace Optimizer.Core.Tests;

/// <summary>Live readings: which program a process belongs to, and what may be done with it.</summary>
public class ActivityModelTests
{
    private static ProcessSample Sample(int pid, string image, long create = 1) => new(pid, 4, create, image, 0, 0, []);

    private static ProgramNames Names(Dictionary<int, string?>? paths = null, Dictionary<string, string?>? descriptions = null,
        Dictionary<int, IReadOnlyList<string>>? services = null) =>
        new(pid => paths is not null && paths.TryGetValue(pid, out var p) ? p : null,
            path => descriptions is not null && descriptions.TryGetValue(path, out var d) ? d : null,
            () => services ?? [],
            names => "Service host: " + string.Join(", ", names));

    [Fact]
    public void SamePathIsOneProgram()
    {
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var names = Names(new() { [10] = chrome, [11] = chrome }, new() { [chrome] = "Google Chrome" });
        var a = names.Identify(Sample(10, "chrome.exe"));
        var b = names.Identify(Sample(11, "chrome.exe"));
        Assert.Equal(a.Key, b.Key);
        Assert.Equal("Google Chrome", a.Name);
        Assert.Equal(chrome, a.Path);
    }

    [Fact]
    public void UnreadablePathGroupsByImage()
    {
        var id = Names().Identify(Sample(900, "MsMpEng.exe"));
        Assert.Equal("name:msmpeng.exe", id.Key.Value);
        Assert.Null(id.Path);
        Assert.Equal("MsMpEng", id.Name);
    }

    [Fact]
    public void EachSvchostIsItsOwnProgram()
    {
        const string svchost = @"C:\Windows\System32\svchost.exe";
        var names = Names(new() { [20] = svchost, [21] = svchost }, services: new() { [20] = ["Windows Update"], [21] = ["DNS Client", "Network Store Interface Service"] });
        names.Refresh([Sample(20, "svchost.exe"), Sample(21, "svchost.exe")]);
        var a = names.Identify(Sample(20, "svchost.exe"));
        var b = names.Identify(Sample(21, "svchost.exe"));
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal("Service host: Windows Update", a.Name);
        Assert.Equal(["DNS Client", "Network Store Interface Service"], b.Services);
    }

    [Fact]
    public void NoDescriptionFallsBackToExe() =>
        Assert.Equal("tool", Names(new() { [5] = @"C:\Tools\tool.exe" }).Identify(Sample(5, "tool.exe")).Name);

    [Fact]
    public void IdentityIsCachedPerProcessInstance()
    {
        var calls = 0;
        var names = new ProgramNames(_ => { calls++; return @"C:\a.exe"; }, _ => null, () => new Dictionary<int, IReadOnlyList<string>>(), n => string.Join(",", n));
        names.Identify(Sample(5, "a.exe"));
        names.Identify(Sample(5, "a.exe"));
        Assert.Equal(1, calls);
        names.Identify(Sample(5, "a.exe", create: 2)); // the PID was reused
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Registry")]
    [InlineData("smss.exe")]
    [InlineData("csrss.exe")]
    [InlineData("wininit.exe")]
    [InlineData("services.exe")]
    [InlineData("LSASS.EXE")]
    [InlineData("winlogon.exe")]
    [InlineData("dwm.exe")]
    [InlineData("Memory Compression")]
    [InlineData("Secure System")]
    public void WindowsProcessesAreNotEnded(string image) => Assert.Equal("Activity_EndCritical", ProgramRules.EndRefusalKey(image, false, 100, 1));

    [Fact]
    public void EndRefusals()
    {
        Assert.Equal("Activity_EndCritical", ProgramRules.EndRefusalKey("foo.exe", critical: true, 100, 1));
        Assert.Equal("Activity_EndSelf", ProgramRules.EndRefusalKey("Tunero.exe", false, 77, ownPid: 77));
        Assert.Null(ProgramRules.EndRefusalKey("steam.exe", false, 100, 1));
    }

    /// <summary>Ending a service host can stop a service Windows restarts the PC for (RpcSs): refused, the Services page is the place.</summary>
    [Fact]
    public void ServiceHostsAreNotEnded()
    {
        Assert.Equal("Activity_EndServiceHost", ProgramRules.EndRefusalKey("svchost.exe", false, 900, 1));
        Assert.False(ProgramRules.CanAddPriorityRule("svchost.exe", false));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\notepad.exe")]
    [InlineData("Registry", null)]
    [InlineData("MemCompression", null)]
    [InlineData(@"relative\tool.exe", null)]
    [InlineData("", null)]
    public void ImagePathMustBeFullyQualified(string raw, string? expected) => Assert.Equal(expected, ProcessSnapshotReader.FullPathOrNull(raw));

    /// <summary>Leaving the Health page and coming back: the time away counts neither for Now nor for Since on.</summary>
    [Fact]
    public void ResumeForgetsTheTimeAway()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "game.exe", cpuSeconds: 1)]));
        model.Update(Tick(2, [P(10, "game.exe", cpuSeconds: 2)]));
        model.Resume();
        var devices = model.Update(Tick(600, [P(10, "game.exe", cpuSeconds: 300)]));
        var row = Row(devices, Cpu, "game");
        Assert.Equal(0, row.Now);
        Assert.Equal(1.0, row.SinceOn, 6);
        Assert.Equal(25.0, Row(model.Update(Tick(602, [P(10, "game.exe", cpuSeconds: 302)])), Cpu, "game").Now, 6);
    }

    [Fact]
    public void PriorityRuleOnlyForNormalPrograms()
    {
        Assert.True(ProgramRules.CanAddPriorityRule("steam.exe", false));
        Assert.False(ProgramRules.CanAddPriorityRule("csrss.exe", false));
        Assert.False(ProgramRules.CanAddPriorityRule("Memory Compression", false));
        Assert.False(ProgramRules.CanAddPriorityRule("foo.exe", critical: true));
    }

    [Fact]
    public void WindowsNotes()
    {
        Assert.Equal("Activity_Note_MsMpEng", ProgramRules.WindowsNoteKey("MsMpEng.exe"));
        Assert.Equal("Activity_Note_MemoryCompression", ProgramRules.WindowsNoteKey("Memory Compression"));
        Assert.Null(ProgramRules.WindowsNoteKey("steam.exe"));
    }

    // ---------- the model: Now, Since on, shares, ended programs ----------

    private const double MB = 1024 * 1024;

    /// <summary>Groups by image name only; enough to test the model.</summary>
    private sealed class FakeNames : IProgramNames
    {
        public ProgramIdentity Identify(ProcessSample p) =>
            new(new ProgramKey("name:" + p.ImageName.ToLowerInvariant()), p.ImageName.Replace(".exe", ""), null, p.ImageName, []);
    }

    private static ProcessSample P(int pid, string image, double cpuSeconds = 0, long memory = 0, long create = 1, params int[] threads) =>
        new(pid, 1, create, image, (long)(cpuSeconds * 10_000_000), memory, threads);

    private static ActivityTick Tick(double atSeconds, IEnumerable<ProcessSample> processes, IEnumerable<GpuEngineSample>? gpu = null, IEnumerable<DiskIoTotal>? disk = null) =>
        new(new ProcessSnapshot((long)(atSeconds * System.Diagnostics.Stopwatch.Frequency), processes.ToList()), (gpu ?? []).ToList(), (disk ?? []).ToList());

    private static ActivityModel Model() => new(new FakeNames(), logicalProcessors: 4, TimeSpan.FromSeconds(30));

    private static ProgramActivity Row(IReadOnlyDictionary<DeviceKey, DeviceActivity> devices, DeviceKey device, string name) =>
        Assert.Single(devices[device].Programs, p => p.Program.Name == name);

    private static readonly DeviceKey Cpu = new(DeviceKind.Processor, 0), Memory = new(DeviceKind.Memory, 0);

    [Fact]
    public void CpuPercentOfWholeProcessor()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "game.exe", cpuSeconds: 1)]));
        var row = Row(model.Update(Tick(2, [P(10, "game.exe", cpuSeconds: 3)])), Cpu, "game");
        Assert.Equal(25.0, row.Now, 6);   // 2 s of CPU in 2 s on 4 logical processors
        Assert.Equal(0.25, row.Share, 6);
        Assert.Equal(2.0, row.SinceOn, 6); // CPU seconds since on
    }

    [Fact]
    public void FirstTickIsZero()
    {
        var devices = Model().Update(Tick(0, [P(10, "game.exe", cpuSeconds: 50)]));
        Assert.DoesNotContain(devices[Cpu].Programs, p => p.Now != 0);
    }

    [Fact]
    public void CpuTimeBackwardsIsZero()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "game.exe", cpuSeconds: 5)]));
        var devices = model.Update(Tick(2, [P(10, "game.exe", cpuSeconds: 4)]));
        Assert.DoesNotContain(devices[Cpu].Programs, p => p.Now < 0 || p.SinceOn < 0);
    }

    /// <summary>
    /// A service that starts later inside a running svchost (a trigger-start one like Windows Update) shows up in the
    /// program's name and services on the next read, instead of the list the process had when it was first seen.
    /// </summary>
    [Fact]
    public void SvchostIdentityFollowsItsServices()
    {
        const string svchost = @"C:\Windows\System32\svchost.exe";
        var services = new Dictionary<int, IReadOnlyList<string>> { [30] = ["DNS Client"] };
        var model = new ActivityModel(new ProgramNames(_ => svchost, _ => null, () => services, n => "Service host: " + string.Join(", ", n)),
            logicalProcessors: 4, TimeSpan.FromSeconds(30));
        model.Update(Tick(0, [P(30, "svchost.exe", cpuSeconds: 1)]));
        Assert.Equal(["DNS Client"], Assert.Single(model.Update(Tick(2, [P(30, "svchost.exe", cpuSeconds: 2)]))[Cpu].Programs).Program.Services);

        services = new() { [30] = ["DNS Client", "Windows Update"] };
        var row = Assert.Single(model.Update(Tick(4, [P(30, "svchost.exe", cpuSeconds: 3)]))[Cpu].Programs);
        Assert.Equal("Service host: DNS Client, Windows Update", row.Program.Name);
        Assert.Equal(["DNS Client", "Windows Update"], row.Program.Services);
    }

    [Fact]
    public void PidReuseStartsFresh()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "old.exe", cpuSeconds: 9, create: 1)]));
        model.Update(Tick(2, [P(10, "old.exe", cpuSeconds: 10, create: 1)]));
        var devices = model.Update(Tick(4, [P(10, "new.exe", cpuSeconds: 1, create: 2)]));
        Assert.DoesNotContain(devices[Cpu].Programs, p => p.Program.Name == "new" && p.Now != 0); // not 1 - 10 s of CPU
        Assert.True(Row(devices, Cpu, "old").Ended);
        Assert.Equal(0, Row(devices, Cpu, "old").Now);
    }

    [Fact]
    public void DiskBytesPerSecondAndShare()
    {
        var model = Model();
        var processes = new[] { P(10, "a.exe", threads: [100]), P(20, "b.exe", threads: [200]) };
        model.Update(Tick(0, processes));
        var devices = model.Update(Tick(2, processes, disk: [new DiskIoTotal(3, 100, 0, (long)(400 * MB), 0), new DiskIoTotal(3, 200, 0, 0, (long)(100 * MB))]));
        var drive = new DeviceKey(DeviceKind.Drive, 3);
        var a = Row(devices, drive, "a");
        Assert.Equal(200 * MB, a.ReadNow, 1);
        Assert.Equal(200 * MB, a.Now, 1);
        Assert.Equal(0.8, a.Share, 6);
        Assert.Equal(400 * MB, a.SinceOn, 1);
        var b = Row(devices, drive, "b");
        Assert.Equal(50 * MB, b.WriteNow, 1);
        Assert.Equal(0.2, b.Share, 6);
    }

    [Fact]
    public void CachedWritesGoToTheFileOwner()
    {
        var model = Model();
        var processes = new[] { P(4, "System", threads: [40]), P(10, "steam.exe", threads: [100]) };
        model.Update(Tick(0, processes));
        var devices = model.Update(Tick(2, processes, disk: [new DiskIoTotal(1, 40, 10, 0, (long)(300 * MB)), new DiskIoTotal(1, 40, 0, 0, (long)(10 * MB))]));
        var drive = new DeviceKey(DeviceKind.Drive, 1);
        Assert.Equal(150 * MB, Row(devices, drive, "steam").WriteNow, 1); // the lazy writer wrote Steam's file
        Assert.Equal(5 * MB, Row(devices, drive, "System").WriteNow, 1);  // no program known for that file
    }

    [Fact]
    public void FileOwnerThatEndedStillCounts()
    {
        var model = Model();
        model.Update(Tick(0, [P(4, "System", threads: [40]), P(10, "copy.exe", threads: [100])]));
        var devices = model.Update(Tick(2, [P(4, "System", threads: [40])], disk: [new DiskIoTotal(1, 40, 10, 0, (long)(64 * MB))]));
        var row = Row(devices, new DeviceKey(DeviceKind.Drive, 1), "copy");
        Assert.True(row.Ended);
        Assert.Equal(64 * MB, row.SinceOn, 1);
    }

    [Fact]
    public void UnknownThreadIsOtherActivity()
    {
        var model = Model();
        var processes = new[] { P(10, "a.exe", threads: [100]) };
        model.Update(Tick(0, processes));
        var devices = model.Update(Tick(2, processes, disk: [new DiskIoTotal(3, 999, 0, (long)(10 * MB), 0), new DiskIoTotal(3, 100, 0, (long)(30 * MB), 0)]));
        var drive = devices[new DeviceKey(DeviceKind.Drive, 3)];
        Assert.Equal(5 * MB, drive.OtherNow, 1);
        Assert.Equal(10 * MB, drive.OtherSinceOn, 1);
        Assert.Equal(0.75, Assert.Single(drive.Programs).Share, 6); // shares count Other activity too
    }

    [Fact]
    public void DiskWithoutCardStillWorks()
    {
        var model = Model();
        var processes = new[] { P(10, "a.exe", threads: [100]) };
        model.Update(Tick(0, processes));
        var devices = model.Update(Tick(2, processes, disk: [new DiskIoTotal(9, 100, 0, 1024, 0)]));
        Assert.True(devices.ContainsKey(new DeviceKey(DeviceKind.Drive, 9)));
        Assert.False(devices.ContainsKey(new DeviceKey(DeviceKind.Drive, 3)));
    }

    [Fact]
    public void GpuTakesBusiestEngine()
    {
        var model = Model();
        var processes = new[] { P(10, "game.exe") };
        var devices = model.Update(Tick(0, processes, gpu: [new GpuEngineSample(10, 0xC2F5, "3D", 30), new GpuEngineSample(10, 0xC2F5, "VideoDecode", 60)]));
        var row = Row(devices, new DeviceKey(DeviceKind.Gpu, 0xC2F5), "game");
        Assert.Equal(60, row.Now);
        Assert.Equal("VideoDecode", row.Engine);
        Assert.Equal(0.6, row.Share, 6);
    }

    [Fact]
    public void TwoGpusTwoCards()
    {
        var devices = Model().Update(Tick(0, [P(10, "game.exe")], gpu: [new GpuEngineSample(10, 1, "3D", 70), new GpuEngineSample(10, 2, "Copy", 5)]));
        Assert.Equal(70, Row(devices, new DeviceKey(DeviceKind.Gpu, 1), "game").Now);
        Assert.Equal(5, Row(devices, new DeviceKey(DeviceKind.Gpu, 2), "game").Now);
    }

    [Fact]
    public void SinceOnAccumulatesAndMemoryKeepsThePeak()
    {
        var model = Model();
        var processes = new[] { P(10, "a.exe", threads: [100]) };
        model.Update(Tick(0, [P(10, "a.exe", memory: 100, threads: [100])], disk: [new DiskIoTotal(0, 100, 0, 10, 0)]));
        model.Update(Tick(2, [P(10, "a.exe", memory: 300, threads: [100])], disk: [new DiskIoTotal(0, 100, 0, 20, 0)]));
        var devices = model.Update(Tick(4, [P(10, "a.exe", memory: 200, threads: [100])], disk: [new DiskIoTotal(0, 100, 0, 30, 0)]));
        Assert.Equal(60, Row(devices, new DeviceKey(DeviceKind.Drive, 0), "a").SinceOn);
        var memory = Row(devices, Memory, "a");
        Assert.Equal(200, memory.Now);
        Assert.Equal(300, memory.SinceOn);
    }

    [Fact]
    public void ProcessesOfOneProgramAddUp()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "chrome.exe", cpuSeconds: 1, memory: 100), P(11, "chrome.exe", cpuSeconds: 1, memory: 50)]));
        var row = Row(model.Update(Tick(2, [P(10, "chrome.exe", cpuSeconds: 2, memory: 100), P(11, "chrome.exe", cpuSeconds: 2, memory: 50)])), Memory, "chrome");
        Assert.Equal(150, row.Now);
        Assert.Equal(2, row.Processes.Count);
    }

    [Fact]
    public void EndedStays30Seconds()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "tool.exe", cpuSeconds: 1)]));
        model.Update(Tick(2, [P(10, "tool.exe", cpuSeconds: 2)]));
        Assert.True(Row(model.Update(Tick(30, [])), Cpu, "tool").Ended);
        Assert.DoesNotContain(model.Update(Tick(34, []))[Cpu].Programs, p => p.Program.Name == "tool");
    }

    [Fact]
    public void IdleProgramsAreLeftOut()
    {
        var model = Model();
        model.Update(Tick(0, [P(10, "idle.exe", cpuSeconds: 1)]));
        Assert.DoesNotContain(model.Update(Tick(2, [P(10, "idle.exe", cpuSeconds: 1)]))[Cpu].Programs, p => p.Program.Name == "idle");
    }

    [Fact]
    public void SortByColumns()
    {
        static ProgramActivity R(string name, double now, double since) =>
            new(new ProgramIdentity(new ProgramKey(name), name, null, name, []), [], now, 0, 0, since, 0, null, false);
        var rows = new[] { R("b", 5, 10), R("a", 5, 20), R("c", 9, 1) };
        Assert.Equal(["c", "a", "b"], ActivityModel.Sort(rows, ActivityColumn.Now, descending: true).Select(r => r.Program.Name));
        Assert.Equal(["a", "b", "c"], ActivityModel.Sort(rows, ActivityColumn.Name, descending: false).Select(r => r.Program.Name));
        Assert.Equal(["a", "b", "c"], ActivityModel.Sort(rows, ActivityColumn.SinceOn, descending: true).Select(r => r.Program.Name));
    }

    // ---------- the monitor and the mapping to the sensor cards ----------

    [Theory]
    [InlineData("/nvme/3", 3)]
    [InlineData("/hdd/0", 0)]
    [InlineData("/ssd/12", 12)]
    [InlineData("/gpu-nvidia/0", null)]
    [InlineData("/nvme/x", null)]
    [InlineData("", null)]
    public void DiskNumberFromId(string id, int? expected) => Assert.Equal(expected, DeviceMapping.DiskNumber(id));

    [Fact]
    public void GpuLuidByName()
    {
        GpuAdapter[] adapters = [new(0x11, "NVIDIA GeForce RTX 2070"), new(0x22, "Intel(R) UHD Graphics 630"), new(0x33, "Twin"), new(0x44, "Twin")];
        Assert.Equal(0x11, DeviceMapping.GpuLuid("nvidia geforce rtx 2070", adapters));
        Assert.Null(DeviceMapping.GpuLuid("AMD Radeon", adapters));
        Assert.Null(DeviceMapping.GpuLuid("Twin", adapters)); // two cards of one name cannot be told apart
    }

    private sealed class FakeSources : IActivitySources
    {
        public Func<IReadOnlyList<GpuEngineSample>?> GpuRead { get; init; } = () => [];
        public Func<IReadOnlyList<DiskIoTotal>?> DiskRead { get; init; } = () => [];
        public string? DiskKey { get; init; }
        public bool Disposed { get; private set; }

        public ProcessSnapshot Snapshot() => new(System.Diagnostics.Stopwatch.GetTimestamp(), [P(10, "a.exe", memory: 1000, threads: [100])]);
        public IReadOnlyList<GpuEngineSample>? Gpu() => GpuRead();
        public IReadOnlyList<DiskIoTotal>? Disk(out string? unavailableKey)
        {
            unavailableKey = DiskKey;
            return DiskRead();
        }
        public IReadOnlyList<GpuAdapter> Adapters() => [];
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void MonitorSurvivesFailingGpu()
    {
        using var monitor = new ActivityMonitor(new FakeSources { GpuRead = () => throw new InvalidOperationException("driver") }, Model());
        var result = monitor.Read();
        Assert.False(result.GpuAvailable);
        Assert.Contains(result.Devices[Memory].Programs, p => p.Program.Name == "a");
    }

    [Fact]
    public void MonitorReportsDiskUnavailable()
    {
        using var monitor = new ActivityMonitor(new FakeSources { DiskRead = () => null, DiskKey = "Activity_TraceDenied" }, Model());
        var result = monitor.Read();
        Assert.Equal("Activity_TraceDenied", result.DiskUnavailableKey);
        Assert.DoesNotContain(result.Devices.Keys, k => k.Kind == DeviceKind.Drive);
    }

    [Fact]
    public void MonitorDisposesItsSources()
    {
        var sources = new FakeSources();
        new ActivityMonitor(sources, Model()).Dispose();
        Assert.True(sources.Disposed);
    }
}
