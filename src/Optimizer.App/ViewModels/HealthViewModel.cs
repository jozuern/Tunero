using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Apps;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;

namespace Optimizer.App.ViewModels;

public sealed record DiskRow(string Name, string Health, string Status, string Details)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;
}

/// <summary>One reading in a component's full list. Its texts change in place on every read: no row is built again.</summary>
public sealed partial class SensorRow(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _peak = "";

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>The readings of one kind (temperatures, load, ...) of a component, under a heading in its full list.</summary>
public sealed record SensorSection(string Title, IReadOnlyList<SensorRow> Rows);

/// <summary>
/// A component's headline value of one kind: the hottest temperature, the total load, the fastest core clock, the
/// first power reading, the fastest fan. Which sensor that is can change from read to read; the tile names it.
/// </summary>
public sealed partial class SensorTile(string type, string title) : ObservableObject
{
    public string Type { get; } = type;
    public string Title { get; } = title;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _peak = "";

    /// <summary>Share of 100 % for the bar under a load; null for other kinds.</summary>
    [ObservableProperty] private double? _fill;

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => $"{Title}: {Value} {Unit}, {Source}";
}

/// <summary>
/// One component (processor, graphics card, drive, ...): headline tiles, the programs using it and, folded, every
/// reading and every program.
/// </summary>
public sealed partial class SensorGroup(string id, string name, string kind, Wpf.Ui.Controls.SymbolRegular icon,
    IReadOnlyList<SensorTile> tiles, IReadOnlyList<SensorSection> sections, string allTitle) : ObservableObject
{
    /// <summary>The library's identifier of the component: keeps the full list open when the cards are built again.</summary>
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public Wpf.Ui.Controls.SymbolRegular Icon { get; } = icon;
    public IReadOnlyList<SensorTile> Tiles { get; } = tiles;
    public IReadOnlyList<SensorSection> Sections { get; } = sections;
    [ObservableProperty] private string _allTitle = allTitle;
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The library's hardware type (Cpu, GpuNvidia, Storage, ...).</summary>
    public string HardwareType { get; init; } = "";

    /// <summary>The programs using this component; null for components without such a list (mainboard, battery).</summary>
    public ActivityList? Activity { get; init; }

    /// <summary>False while the program list cannot be read for this component (no GPU engine counters).</summary>
    [ObservableProperty] private bool _showsActivity = true;

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;
}

public sealed record RunRow(string Label, string AvgFps, string Low, string Frames)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Label;
}

/// <param name="addPriorityRule">Opens the start priority rules with this exe name (Live readings' program details).</param>
/// <param name="processes">Runs sfc, dism, PresentMon and winget; null: the app's own runner (tests pass a fake).</param>
public sealed partial class HealthViewModel(IPageHost owner, IDialogs dialogs, Action<string>? addPriorityRule = null,
    Optimizer.Core.Actions.IProcessRunner? processes = null) : PageViewModel(owner)
{
    private Optimizer.Core.Actions.IProcessRunner ToolRunner => processes ?? Owner.Services.Context.Processes;

    /// <summary>The disk rows come from the scan result: built again after every scan.</summary>
    protected override bool UsesProfile => true;

    protected override void ProfileUpdated() => BuildDisks();
    private CancellationTokenSource? _toolCancel;
    private CancellationTokenSource? _throttleCancel;
    private Sensors? _sensors;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private readonly List<FrameStats> _before = [];
    private readonly List<FrameStats> _after = [];

    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<SensorGroup> SensorGroups { get; } = [];
    public ObservableCollection<string> Processes { get; } = [];
    public ObservableCollection<RunRow> Runs { get; } = [];
    public ObservableCollection<int> DurationOptions { get; } = [60, 120, 300];
    public ObservableCollection<int> CaptureOptions { get; } = [30, 60, 90];

    [ObservableProperty] private string _toolOutput = "";
    [ObservableProperty] private bool _isToolRunning;
    [ObservableProperty] private int _throttleSeconds = 120;
    [ObservableProperty] private bool _isThrottleRunning;
    [ObservableProperty] private string _throttleStatus = "";
    [ObservableProperty] private string _throttleResult = "";
    [ObservableProperty] private string? _selectedProcess;

    /// <summary>Where the before and after measurement stands: which step is next, with the runs done so far.</summary>
    [ObservableProperty] private string _benchmarkStep = "";

    partial void OnSelectedProcessChanged(string? value) => UpdateBenchmarkStep();

    /// <summary>Three runs before (A), the change, three runs after (B): the result counts only with enough runs on both sides.</summary>
    private void UpdateBenchmarkStep()
    {
        BenchmarkStepNumber = SelectedProcess is null ? 1 : _before.Count < 3 ? 2 : _after.Count == 0 ? 3 : _after.Count < 3 ? 4 : 5;
        BenchmarkStep = BenchmarkStepNumber switch
        {
            1 => Loc.Instance["Bench_Step1"],
            2 => Loc.Instance.Format("Bench_Step2", _before.Count),
            3 => Loc.Instance["Bench_Step3"],
            4 => Loc.Instance.Format("Bench_Step4", _after.Count),
            _ => Loc.Instance["Bench_StepDone"],
        };
        BenchmarkStepLabel = BenchmarkStepNumber <= 4 ? Loc.Instance.Format("Bench_StepOf", BenchmarkStepNumber) : Loc.Instance["Bench_StepLabelDone"];
        OnPropertyChanged(nameof(CanCaptureBefore));
        OnPropertyChanged(nameof(CanCaptureAfter));
        OnPropertyChanged(nameof(HasRuns));
    }

    /// <summary>1 to 4 while the steps run, 5 when both sides have their runs: drives the four marks above the step.</summary>
    [ObservableProperty] private int _benchmarkStepNumber = 1;

    /// <summary>"Step 2 of 4" or "Done", above the instruction.</summary>
    [ObservableProperty] private string _benchmarkStepLabel = "";

    /// <summary>A capture needs the game; B also needs a run of A to compare with. One capture at a time.</summary>
    public bool CanCaptureBefore => SelectedProcess is not null && !IsCapturing;

    public bool CanCaptureAfter => SelectedProcess is not null && !IsCapturing && _before.Count > 0;

    public bool HasRuns => Runs.Count > 0;

    partial void OnIsCapturingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCaptureBefore));
        OnPropertyChanged(nameof(CanCaptureAfter));
    }
    [ObservableProperty] private int _captureSeconds = 60;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _benchmarkStatus = "";
    [ObservableProperty] private string _comparisonText = "";
    [ObservableProperty] private bool _sensorsOn;

    /// <summary>While the sensors open, or when they report nothing; empty once readings show.</summary>
    [ObservableProperty] private string _sensorsStatus = "";
    [ObservableProperty] private string _pawnIoText = "";
    [ObservableProperty] private bool _canInstallPawnIo;

    protected override async Task LoadAsync()
    {
        BuildDisks();
        await RefreshProcessesAsync();
        CanInstallPawnIo = !Sensors.PawnIoInstalled;
        PawnIoText = Loc.Instance[Sensors.PawnIoInstalled ? "Sensors_PawnIoOn" : "Sensors_PawnIoOff"];
        if (Optimizer.Core.Tools.HealthStore.LoadThrottle(Optimizer.Core.Tools.HealthStore.DefaultFolder) is { } last) ThrottleResult = Describe(last);
    }

    private void BuildDisks()
    {
        Disks.Clear();
        var lang = Lang;
        foreach (var d in Owner.Profile?.Extras?.DiskHealth ?? [])
        {
            var details = new List<string>();
            if (d.TemperatureC is { } t) details.Add($"{Labels.Current.Get(lang, "fact.diskTemperature")}: {t} °C");
            if (d.WearPercent is { } w) details.Add($"{Labels.Current.Get(lang, "fact.diskWear")}: {w} %");
            if (d.PowerOnHours is { } h) details.Add($"{Labels.Current.Get(lang, "fact.diskPowerOnHours")}: {h}");
            Disks.Add(new DiskRow($"{d.Name} ({d.MediaType}, {d.BusType})", Labels.Current.Get(lang, $"value.health{d.Health}"),
                d.IsCritical ? "Critical" : d.IsProblem ? "Problem" : d.Health == "Unknown" ? "Unknown" : "Ok",
                details.Count > 0 ? string.Join(", ", details) : Loc.Instance["Health_CountersNeedAdmin"]));
        }
    }

    // SFC / DISM

    private async Task RunToolAsync(Func<Optimizer.Core.Actions.IProcessRunner, IProgress<string>, CancellationToken, Task<int>> run, string title)
    {
        _toolCancel = new CancellationTokenSource();
        var token = _toolCancel.Token;
        IsToolRunning = true;
        var lines = new List<string>();
        Services.WorkProgress.Scope? banner = null;
        var progress = new Progress<string>(l =>
        {
            // sfc and dism print how far they are; other lines (the phase) keep the last percentage.
            if (ToolProgress.Percent(l) is { } percent) banner?.Report(percent, l);
            OutputLines.Add(lines, l);
            ToolOutput = string.Join("\n", lines);
        });
        try
        {
            // SFC and DISM repair system files: no tweak, scan or update at the same time.
            await RunWorkAsync("system file repair", title, async work =>
            {
                banner = work;
                try
                {
                    var code = await run(ToolRunner, progress, token);
                    lines.Add(Loc.Instance.Format("Health_ExitCode", code));
                    ToolOutput = string.Join("\n", lines);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ToolOutput += "\n" + Loc.Instance["Health_Stopped"];
                }
            }, _toolCancel);
        }
        finally
        {
            IsToolRunning = false;
            _toolCancel.Dispose();
            _toolCancel = null;
        }
    }

    [RelayCommand] private Task SfcAsync() => RunToolAsync(SystemRepair.ScanNowAsync, Loc.Instance["Work_Sfc"]);
    [RelayCommand] private Task DismScanAsync() => RunToolAsync(SystemRepair.ScanHealthAsync, Loc.Instance["Work_DismScan"]);
    [RelayCommand] private Task DismRestoreAsync() => RunToolAsync(SystemRepair.RestoreHealthAsync, Loc.Instance["Work_DismRestore"]);
    [RelayCommand] private void StopTool() => _toolCancel?.Cancel();

    // Throttle check

    [RelayCommand]
    private async Task StartThrottleAsync()
    {
        _throttleCancel = new CancellationTokenSource();
        IsThrottleRunning = true;
        try
        {
            using var monitor = new ThrottleMonitor();
            var none = Loc.Instance["Throttle_NotReported"];
            var progress = new Progress<ThrottleSample>(s => ThrottleStatus = Loc.Instance.Format("Throttle_Sample", monitor.Samples.Count,
                s.CpuPerformanceLimit is { } l ? $"{l:0} %" : none, s.CpuUtility is { } u ? $"{u:0} %" : none,
                s.Gpus.FirstOrDefault()?.TempC is { } t ? $"{t} °C" : none));
            var result = await monitor.RunAsync(TimeSpan.FromSeconds(ThrottleSeconds), progress, _throttleCancel.Token);
            if (result.Samples >= 10)
            {
                Optimizer.Core.Tools.HealthStore.SaveThrottle(Optimizer.Core.Tools.HealthStore.DefaultFolder, result);
                ThrottleResult = Describe(result);
                // Updates the throttling finding; while a change or an update runs the next scan does it.
                if (Owner.ScanCommand.CanExecute(null)) await Owner.ScanCommand.ExecuteAsync(null);
            }
            else
            {
                ThrottleResult = Loc.Instance["Throttle_TooShort"];
            }
        }
        catch (Exception ex)
        {
            ThrottleResult = Loc.Instance.Format("Result_Error", ex.Message);
        }
        finally
        {
            IsThrottleRunning = false;
            ThrottleStatus = "";
            _throttleCancel.Dispose();
            _throttleCancel = null;
        }
    }

    [RelayCommand] private void StopThrottle() => _throttleCancel?.Cancel();

    private static string Describe(ThrottleResult r) => Loc.Instance.Format("Throttle_Result",
        r.Measured.LocalDateTime.ToString("g"), r.Samples, $"{r.CpuBusyShare * 100:0}", $"{r.CpuLimitedShare * 100:0}",
        // The limit reasons come from NVML: both or neither are there.
        r.GpuReasonShare.TryGetValue("thermal", out var th) && r.GpuReasonShare.TryGetValue("powerLimit", out var pw)
            ? Loc.Instance.Format("Throttle_Gpu", $"{th * 100:0}", $"{pw * 100:0}")
            : Loc.Instance["Throttle_GpuNone"],
        r.CpuThrottled || r.GpuThermal || r.GpuSlowdown ? Loc.Instance["Throttle_Verdict_Bad"]
            : !r.CpuJudged ? Loc.Instance["Throttle_Verdict_NoLoad"] : Loc.Instance["Throttle_Verdict_Ok"]);

    // Benchmark

    /// <summary>Games found by the scan plus programs with a window; listing processes runs off the UI thread.</summary>
    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        var selected = SelectedProcess;
        var games = (Owner.Profile?.Software?.Games ?? []).Select(g => g.Executable).OfType<string>().ToList();
        var names = await Task.Run(() =>
        {
            var set = new SortedSet<string>(games.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId) set.Add(p.ProcessName + ".exe");
                    }
                    catch (Exception)
                    {
                        // protected process
                    }
                }
            }
            return set.Where(PresentMon.IsSafeProcessName).ToList();
        });
        Processes.Clear();
        foreach (var n in names) Processes.Add(n);
        SelectedProcess = selected is not null && Processes.Contains(selected) ? selected : null;
        UpdateBenchmarkStep();
    }

    [RelayCommand] private Task CaptureBeforeAsync() => CaptureAsync(_before, "A");
    [RelayCommand] private Task CaptureAfterAsync() => CaptureAsync(_after, "B");

    private CancellationTokenSource? _captureCancel;

    [RelayCommand]
    private void StopCapture() => _captureCancel?.Cancel();

    private async Task CaptureAsync(List<FrameStats> set, string label)
    {
        if (SelectedProcess is null) return;
        IsCapturing = true;
        _captureCancel = new CancellationTokenSource();
        try
        {
            var exe = await Task.Run(() => PresentMon.Extract(AppServices.ToolsFolder));
            BenchmarkStatus = Loc.Instance.Format("Bench_Capturing", SelectedProcess, CaptureSeconds);
            var stats = await PresentMon.CaptureAsync(ToolRunner, exe, SelectedProcess, CaptureSeconds, Optimizer.Core.Platform.DataPaths.Captures, null, _captureCancel.Token);
            if (stats is null)
            {
                BenchmarkStatus = Loc.Instance["Bench_NoFrames"];
                return;
            }
            set.Add(stats);
            Runs.Add(new RunRow($"{label}{set.Count}", Loc.Instance.Format("Bench_Fps", $"{stats.AverageFps:0.0}"), Loc.Instance.Format("Bench_Low", $"{stats.OnePercentLowFps:0.0}"), stats.Frames.ToString()));
            BenchmarkStatus = Loc.Instance["Bench_Done"];
            Compare();
            UpdateBenchmarkStep();
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatus = Loc.Instance["Bench_Stopped"];
        }
        catch (TimeoutException)
        {
            BenchmarkStatus = Loc.Instance["Bench_TimedOut"];
        }
        catch (Exception ex)
        {
            BenchmarkStatus = Loc.Instance.Format("Result_Error", ex.Message);
        }
        finally
        {
            IsCapturing = false;
            _captureCancel.Dispose();
            _captureCancel = null;
        }
    }

    private void Compare()
    {
        var c = PresentMon.Compare(_before, _after);
        ComparisonText = c.Result switch
        {
            Comparison.NotEnoughRuns => Loc.Instance.Format("Bench_NeedRuns", PresentMon.MinRuns),
            Comparison.NoMeasurableDifference => Loc.Instance.Format("Bench_NoDifference", $"{c.BeforeMean:0.0}", $"{c.AfterMean:0.0}"),
            Comparison.Better => Loc.Instance.Format("Bench_Better", $"{c.ChangePercent:0.0}", $"{c.BeforeMean:0.0}", $"{c.AfterMean:0.0}"),
            _ => Loc.Instance.Format("Bench_Worse", $"{-c.ChangePercent:0.0}", $"{c.BeforeMean:0.0}", $"{c.AfterMean:0.0}"),
        };
    }

    [RelayCommand]
    private void ClearRuns()
    {
        _before.Clear();
        _after.Clear();
        Runs.Clear();
        ComparisonText = "";
        UpdateBenchmarkStep();
    }

    // Sensors (opt-in)

    /// <summary>At exit: the sensor library (and its driver) is closed, not left to the end of the process.</summary>
    public void CloseSensors()
    {
        if (SensorsOn) SensorsOn = false;
    }

    partial void OnSensorsOnChanged(bool value)
    {
        if (value)
        {
            StartSensorsAsync().Forget("sensors");
        }
        else
        {
            _timer?.Stop();
            _timer = null;
            // A read still running keeps the library open; it is closed when that read ends.
            if (!_readingSensors) _sensors?.Dispose();
            _sensors = null;
            CloseActivity();
            _activityModel = null;
            _activityLists.Clear();
            SensorGroups.Clear();
            _sensorLayout = "";
            SensorsStatus = "";
        }
    }

    // Programs per device (opened with the sensors; the disk trace runs only while the Health page shows)

    private Optimizer.Core.Activity.ActivityModel? _activityModel;
    private ActivityConnection? _activityConnection;

    /// <summary>Developer aid (--activity-demo): fixed programs and one demo drive card instead of this PC's.</summary>
    private bool _activityDemo;

    public void UseActivityDemo() => _activityDemo = true;

    /// <summary>Developer aid (--select-program): selects a row of that name as a click would, on a drive card first.</summary>
    public void SelectProgram(string name)
    {
        foreach (var group in SensorGroups.OrderBy(g => g.Activity?.Kind == Optimizer.Core.Activity.DeviceKind.Drive ? 0 : 1))
            if (group.Activity?.Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                group.IsExpanded = true;
                group.Activity.Selected = row;
                return;
            }
    }

    private static readonly SensorReading[] DemoDrive =
    [
        new("/nvme/1", "Samsung SSD 990 PRO 2TB", "Storage", "Temperature", "Temperature", 52, 55),
        new("/nvme/1", "Samsung SSD 990 PRO 2TB", "Storage", "Total Activity", "Load", 80, 92),
        new("/nvme/1", "Samsung SSD 990 PRO 2TB", "Storage", "Read Activity", "Load", 78, 90),
        new("/nvme/1", "Samsung SSD 990 PRO 2TB", "Storage", "Write Activity", "Load", 6, 21),
    ];

    /// <summary>The page shows or hides: the disk trace and the GPU counters close when it hides, Since on carries over.</summary>
    public void PageShown(bool shown)
    {
        if (!shown) CloseActivity();
        else if (SensorsOn && _sensors is not null) OpenActivityAsync().Forget("program activity");
    }

    private Task OpenActivityAsync()
    {
        _activityModel ??= new Optimizer.Core.Activity.ActivityModel(_activityDemo ? DemoActivitySources.Names() : ActivityNames(),
            Environment.ProcessorCount, TimeSpan.FromSeconds(30));
        if (_activityConnection is null)
        {
            var demo = _activityDemo;
            _activityConnection = new ActivityConnection(() => Task.Run(() => demo
                ? new DemoActivitySources()
                : (Optimizer.Core.Activity.IActivitySources)Optimizer.Core.Activity.NativeActivitySources.Open(DataPaths.ProcessIsElevated)));
            // Last resort: the disk trace session must never outlive the app (CloseSensors at exit closes it first).
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseActivity();
        }
        return _activityConnection.OpenAsync();
    }

    private void CloseActivity() => _activityConnection?.Close();

    private Optimizer.Core.Activity.ActivityResult? ReadActivity() =>
        _activityModel is { } model ? _activityConnection?.Read(model) : null;

    /// <summary>What a program's details do on this PC: read its signature, end it, show it in Explorer, add a rule.</summary>
    private ActivityActions ActivityActions() => new(dialogs, Owner, Optimizer.Core.Activity.ProcessControl.End,
        p => ProgramDetails.Build(p, Optimizer.Core.Activity.ProcessSnapshotReader.IsCritical, Optimizer.Core.Startup.SignatureVerifier.Verify, Environment.ProcessId,
            canEnd: Optimizer.Core.Activity.ProcessControl.CanEnd),
        path =>
        {
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (File.Exists(path)) DeElevatedLauncher.Open(explorer, $"/select,\"{path}\"", Owner.Services.Elevation);
        },
        exe => addPriorityRule?.Invoke(exe));

    private static Optimizer.Core.Activity.ProgramNames ActivityNames() => new(
        Optimizer.Core.Activity.ProcessSnapshotReader.ImagePath,
        path =>
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(path).FileDescription;
            }
            catch (Exception)
            {
                return null;
            }
        },
        Optimizer.Core.Activity.ServiceHosts.Read,
        services => Loc.Instance.Format("Activity_ServiceHost", services.Count <= 3
            ? string.Join(", ", services)
            : Loc.Instance.Format("Activity_AndMore", string.Join(", ", services.Take(3)), services.Count - 3)));

    /// <summary>
    /// Opening the sensor library enumerates processor, board, drives, memory and battery and can take seconds: done off
    /// the UI thread. Switched off (or off and on again) meanwhile: the copy that is not needed is closed.
    /// </summary>
    private async Task StartSensorsAsync()
    {
        Sensors opened;
        SensorsStatus = Loc.Instance["Sensors_Opening"];
        try
        {
            opened = await Task.Run(() => new Sensors());
        }
        catch (Exception ex)
        {
            Owner.ShowResult(Loc.Instance.Format("Result_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
            SensorsOn = false;
            SensorsStatus = "";
            return;
        }
        if (!SensorsOn || _sensors is not null)
        {
            opened.Dispose();
            return;
        }
        _sensors = opened;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => ReadSensorsAsync().Forget("sensor reading");
        _timer.Start();
        if (Owner.CurrentPage == Page.Health) await OpenActivityAsync();
        ReadSensorsAsync().Forget("sensor reading");
    }

    private bool _readingSensors;

    /// <summary>
    /// One read at a time and only while the Health page is open: a slow read (a sensor driver that hangs for a moment)
    /// is never overlapped by the next tick, and switching the sensors off during a read closes them after it.
    /// </summary>
    private async Task ReadSensorsAsync()
    {
        if (_sensors is not { } s || _readingSensors || Owner.CurrentPage != Page.Health) return;
        _readingSensors = true;
        IReadOnlyList<SensorReading>? readings;
        Optimizer.Core.Activity.ActivityResult? activity = null;
        try
        {
            (readings, activity) = await Task.Run(() => (s.Read(), ReadActivity()));
        }
        catch (Exception ex)
        {
            // The last values stay: one failed read is no reason to empty the cards.
            Log.Warn("sensors", $"read failed: {ex.Message}");
            readings = null;
        }
        finally
        {
            _readingSensors = false;
            if (!ReferenceEquals(s, _sensors)) s.Dispose();
        }
        if (readings is null || !ReferenceEquals(s, _sensors)) return;
        ShowSensorReadings(_activityDemo ? [.. readings, .. DemoDrive] : readings);
        if (activity is not null) ShowActivity(activity);
    }

    /// <summary>Each card's program list from one activity reading; the GPU cards find their adapter by name.</summary>
    private void ShowActivity(Optimizer.Core.Activity.ActivityResult result)
    {
        var adapters = _activityConnection?.Adapters ?? [];
        foreach (var group in SensorGroups)
        {
            if (group.Activity is not { } list) continue;
            var key = DeviceOf(group, adapters);
            var unavailable = list.Kind == Optimizer.Core.Activity.DeviceKind.Drive && result.DiskUnavailableKey is { } why
                ? Loc.Instance.Format("Activity_DiskUnavailable", Loc.Instance[why])
                : null;
            group.ShowsActivity = key is not null && (list.Kind != Optimizer.Core.Activity.DeviceKind.Gpu || result.GpuAvailable);
            list.Update(key is { } k ? result.Devices.GetValueOrDefault(k) : null, unavailable);
            group.AllTitle = Loc.Instance.Format("Sensors_AllWithPrograms", group.Sections.Sum(s => s.Rows.Count), list.ProgramCount);
        }
    }

    private static Optimizer.Core.Activity.DeviceKey? DeviceOf(SensorGroup group, IReadOnlyList<Optimizer.Core.Activity.GpuAdapter> adapters) => group.Activity?.Kind switch
    {
        Optimizer.Core.Activity.DeviceKind.Processor => new(Optimizer.Core.Activity.DeviceKind.Processor, 0),
        Optimizer.Core.Activity.DeviceKind.Memory => new(Optimizer.Core.Activity.DeviceKind.Memory, 0),
        Optimizer.Core.Activity.DeviceKind.Drive => Optimizer.Core.Activity.DeviceMapping.DiskNumber(group.Id) is { } n
            ? new(Optimizer.Core.Activity.DeviceKind.Drive, n) : null,
        Optimizer.Core.Activity.DeviceKind.Gpu => Optimizer.Core.Activity.DeviceMapping.GpuLuid(group.Name, adapters) is { } luid
            ? new(Optimizer.Core.Activity.DeviceKind.Gpu, luid) : null,
        _ => null,
    };

    /// <summary>Which components get a program list.</summary>
    private static Optimizer.Core.Activity.DeviceKind? ActivityKind(string hardwareType) => hardwareType switch
    {
        "Cpu" => Optimizer.Core.Activity.DeviceKind.Processor,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => Optimizer.Core.Activity.DeviceKind.Gpu,
        "Memory" => Optimizer.Core.Activity.DeviceKind.Memory,
        "Storage" => Optimizer.Core.Activity.DeviceKind.Drive,
        _ => null,
    };

    /// <summary>The kinds of reading shown, in the order of the tiles and of the sections in the full list.</summary>
    private static readonly string[] SensorTypes = ["Temperature", "Load", "Clock", "Power", "Fan"];

    /// <summary>The sensors and language the cards were built for: a different set (a drive added) builds them again.</summary>
    private string _sensorLayout = "";

    /// <summary>The row of every shown reading, by its place in the read; the tiles with the readings they choose from.</summary>
    private SensorRow[] _sensorRows = [];
    private readonly List<(SensorTile Tile, int[] Readings)> _sensorTiles = [];

    /// <summary>Program lists by component: kept when the cards are built again, so sorting and the selection stay.</summary>
    private readonly Dictionary<string, ActivityList> _activityLists = [];

    /// <summary>
    /// Fills the cards in place every two seconds: they are built only when the set of sensors changes, so an open full
    /// list stays open and nothing flickers.
    /// </summary>
    private void ShowSensorReadings(IReadOnlyList<SensorReading> readings)
    {
        var shown = readings.Where(r => r.Value is not null && !r.IsEmpty && SensorTypes.Contains(r.Type)).Select(MergeMemory).ToList();
        var layout = Loc.Instance.Language + "\n" + string.Join("\n", shown.Select(r => $"{r.HardwareId}|{r.Type}|{r.Sensor}"));
        if (layout != _sensorLayout)
        {
            BuildSensorGroups(shown);
            _sensorLayout = layout;
        }
        SensorsStatus = shown.Count == 0 ? Loc.Instance["Sensors_None"] : "";
        for (var i = 0; i < shown.Count; i++)
        {
            var r = shown[i];
            _sensorRows[i].Value = SensorText(r.Type, r.Value!.Value);
            _sensorRows[i].Peak = r.Max is { } max ? SensorText(r.Type, max) : "";
        }
        foreach (var (tile, indices) in _sensorTiles)
        {
            if (Headline(tile.Type, indices.Select(i => shown[i]).ToList()) is not { Value: { } value } r) continue;
            (tile.Value, tile.Unit) = SensorNumber(r.Type, value);
            tile.Source = r.Sensor;
            tile.Peak = r.Max is { } max ? Loc.Instance.Format("Sensors_Peak", SensorText(r.Type, max)) : "";
            tile.Fill = r.Type == "Load" ? Math.Clamp(value / 100.0, 0, 1) : null;
        }
    }

    /// <summary>
    /// The library reports memory as two components with one reading each ("Total Memory", "Virtual Memory", both
    /// named "Memory"): one card with both, each reading named after its component.
    /// </summary>
    private static SensorReading MergeMemory(SensorReading r) =>
        r.HardwareType == "Memory" ? r with { HardwareId = "memory", Hardware = Loc.Instance["Hw_Memory"], Sensor = r.Hardware } : r;

    private void BuildSensorGroups(List<SensorReading> shown)
    {
        var l = Loc.Instance;
        var open = SensorGroups.Where(g => g.IsExpanded).Select(g => g.Id).ToHashSet();
        SensorGroups.Clear();
        _sensorTiles.Clear();
        _sensorRows = new SensorRow[shown.Count];
        // Processor and graphics first, as on the System info page; the library's order within a kind.
        var components = shown.Select((r, i) => (Reading: r, Index: i)).GroupBy(x => x.Reading.HardwareId)
            .OrderBy(g => SensorKind(g.First().Reading.HardwareType).Rank);
        foreach (var component in components)
        {
            var first = component.First().Reading;
            var (kind, icon, _) = SensorKind(first.HardwareType);
            if (first.HardwareType == "Storage" && Optimizer.Core.Activity.DeviceMapping.DiskNumber(first.HardwareId) is { } disk)
                kind = l.Format("Sensors_DriveNumber", disk);
            var tiles = new List<SensorTile>();
            var sections = new List<SensorSection>();
            foreach (var type in SensorTypes)
            {
                var ofType = component.Where(x => x.Reading.Type == type).ToList();
                if (ofType.Count == 0) continue;
                var rows = new List<SensorRow>();
                foreach (var (reading, index) in ofType) rows.Add(_sensorRows[index] = new SensorRow(reading.Sensor));
                sections.Add(new SensorSection(l[$"SensorType_{type}"], rows));
                if (Headline(type, ofType.Select(x => x.Reading).ToList()) is null) continue;
                var tile = new SensorTile(type, l[$"SensorType_{type}"]);
                tiles.Add(tile);
                _sensorTiles.Add((tile, ofType.Select(x => x.Index).ToArray()));
            }
            var activityKind = ActivityKind(first.HardwareType);
            SensorGroups.Add(new SensorGroup(first.HardwareId, first.Hardware, kind, icon, tiles, sections, l.Format("Sensors_All", component.Count()))
            {
                IsExpanded = open.Contains(first.HardwareId),
                HardwareType = first.HardwareType,
                Activity = activityKind is { } k ? _activityLists.GetValueOrDefault(first.HardwareId) ?? new ActivityList(k, _activityDemo ? DemoActivitySources.Actions(dialogs, Owner) : ActivityActions()) : null,
            });
            if (SensorGroups[^1].Activity is { } list) _activityLists[first.HardwareId] = list;
        }
    }

    /// <summary>
    /// The reading a tile shows: the hottest temperature (not "Distance to TjMax", the headroom left), the total load,
    /// the fastest core clock, the fastest fan, otherwise the first reading (package power). Null: no tile.
    /// </summary>
    private static SensorReading? Headline(string type, List<SensorReading> readings) => type switch
    {
        "Temperature" => readings.Where(r => !r.Sensor.Contains("Distance", StringComparison.OrdinalIgnoreCase)).MaxBy(r => r.Value),
        "Load" => readings.FirstOrDefault(r => r.Sensor.Contains("Total", StringComparison.OrdinalIgnoreCase)) ?? readings.FirstOrDefault(),
        "Clock" => readings.Where(r => r.Sensor.Contains("Core", StringComparison.OrdinalIgnoreCase)).MaxBy(r => r.Value) ?? readings.FirstOrDefault(),
        "Fan" => readings.MaxBy(r => r.Value),
        _ => readings.FirstOrDefault(),
    };

    private static (string Kind, Wpf.Ui.Controls.SymbolRegular Icon, int Rank) SensorKind(string hardwareType) => hardwareType switch
    {
        "Cpu" => (Loc.Instance["Hw_Cpu"], Wpf.Ui.Controls.SymbolRegular.DeveloperBoard24, 0),
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => (Loc.Instance["Hw_Gpu"], Wpf.Ui.Controls.SymbolRegular.DesktopPulse24, 1),
        "Memory" => ("", Wpf.Ui.Controls.SymbolRegular.Ram20, 2), // the card is already called memory (MergeMemory)
        "Motherboard" or "SuperIO" or "EmbeddedController" => (Loc.Instance["Hw_Board"], Wpf.Ui.Controls.SymbolRegular.DeveloperBoardLightning20, 3),
        "Storage" => (Loc.Instance["Sensors_Drive"], Wpf.Ui.Controls.SymbolRegular.HardDrive24, 4),
        "Battery" => (Loc.Instance["Sensors_Battery"], Wpf.Ui.Controls.SymbolRegular.Battery1024, 5),
        _ => ("", Wpf.Ui.Controls.SymbolRegular.Gauge24, 6),
    };

    /// <summary>Number and unit apart: the tiles set the number large. Formatted in the app's language.</summary>
    private static (string Number, string Unit) SensorNumber(string type, float value) => type switch
    {
        "Temperature" => ($"{value:0}", "°C"),
        "Load" => ($"{value:0}", "%"),
        "Clock" => ($"{value:N0}", "MHz"),
        "Power" => ($"{value:0.0}", "W"),
        "Fan" => ($"{value:N0}", "RPM"),
        _ => ($"{value}", ""),
    };

    private static string SensorText(string type, float value)
    {
        var (number, unit) = SensorNumber(type, value);
        return $"{number} {unit}";
    }

    [RelayCommand]
    private async Task InstallPawnIoAsync()
    {
        if (CatalogData.Current.Apps.Apps.FirstOrDefault(a => a.Id == "namazso.PawnIO") is not { } app) return;
        if (await Task.Run(() => Winget.FindTrusted()) is not { } winget)
        {
            PawnIoText = Loc.Instance["Apps_NoWinget"];
            return;
        }
        if (!dialogs.Ask(Loc.Instance["Sensors_PawnIoTitle"], Loc.Instance["Sensors_PawnIoText"], Loc.Instance["Apps_Install"])) return;
        await RunWorkAsync("install PawnIO", async () =>
        {
            var code = await Winget.InstallAsync(ToolRunner, winget, app, null, CancellationToken.None);
            PawnIoText = Winget.IsSuccess(code) ? Loc.Instance["Sensors_PawnIoOn"] : Loc.Instance.Format("Apps_Failed", $"0x{code:X8}");
            CanInstallPawnIo = !Winget.IsSuccess(code);
        });
    }
}
