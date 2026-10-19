using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Activity;
using Optimizer.Core.Platform;

namespace Optimizer.App.ViewModels;

/// <summary>One program in a device card's list. Its texts change in place on every read; the row is kept by its key.</summary>
public sealed partial class ActivityRow(ProgramKey key, string name) : ObservableObject
{
    public ProgramKey Key { get; } = key;
    public string Name { get; } = name;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _now = "";
    [ObservableProperty] private string _read = "";
    [ObservableProperty] private string _write = "";
    [ObservableProperty] private string _sinceOn = "";
    [ObservableProperty] private string _engine = "";
    [ObservableProperty] private double _fill;
    [ObservableProperty] private bool _ended;
    [ObservableProperty] private bool _isSelected;

    /// <summary>The latest reading of this program on its device (for the details).</summary>
    public ProgramActivity? Activity { get; set; }

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Ended ? $"{Name}: {Loc.Instance["Activity_Ended"]}, {SinceOn}" : $"{Name}: {Now}, {SinceOn}";
}

/// <summary>What the details of a selected program show and allow. Built off the UI thread (it reads the signature).</summary>
public sealed record ProgramDetails(string Name, string Publisher, string? Signature, string? Path, int ProcessCount,
    IReadOnlyList<string> Services, string? Note, string? EndRefusal, bool CanAddRule, bool Ended)
{
    public string ProcessesText => ProcessCount == 1 ? Loc.Instance["Activity_OneProcess"] : Loc.Instance.Format("Activity_Processes", ProcessCount);
    public string? ServicesText => Services.Count == 0 ? null : string.Join(", ", Services);

    /// <summary>Publisher, signature and process count on one line, without the parts that are missing.</summary>
    public string MetaText => string.Join(", ", new[] { Publisher, Signature, ProcessesText }.Where(s => !string.IsNullOrEmpty(s)));
    public bool CanEnd => EndRefusal is null && !Ended && ProcessCount > 0;

    public static ProgramDetails Build(ProgramActivity p, Func<int, bool> isCritical, Func<string?, Optimizer.Core.Startup.SignatureInfo> verify, int ownPid)
    {
        var image = p.Program.ImageName;
        var critical = p.Processes.Any(r => isCritical(r.Pid));
        var refusal = p.Processes.Select(r => ProgramRules.EndRefusalKey(image, critical, r.Pid, ownPid)).FirstOrDefault(k => k is not null)
            ?? (p.Processes.Count == 0 ? ProgramRules.EndRefusalKey(image, critical, 0, ownPid) : null);
        var signature = p.Program.Path is { } path ? verify(path) : null;
        var lang = Loc.Instance.Language;
        return new ProgramDetails(p.Program.Name,
            signature?.Publisher ?? Loc.Instance["Startup_NoPublisher"],
            signature is null ? null : Optimizer.Core.Docs.Labels.Current.Get(lang, $"signature.{signature.Status}"),
            p.Program.Path, p.Processes.Count, p.Program.Services,
            ProgramRules.WindowsNoteKey(image) is { } note ? Loc.Instance[note] : null,
            refusal is null ? null : Loc.Instance[refusal],
            ProgramRules.CanAddPriorityRule(image, critical), p.Ended);
    }
}

/// <summary>What the program list does with the system: injected, so tests never read or end a real process.</summary>
/// <param name="Terminate">Ends one process if it is still the one with that create time: (ended, Win32 error).</param>
/// <param name="AddRule">Opens the start priority rules with this exe name.</param>
public sealed record ActivityActions(IDialogs Dialogs, IShell Shell, Func<int, long, (bool Ok, int Error)> Terminate,
    Func<ProgramActivity, ProgramDetails> Details, Action<string> OpenLocation, Action<string> AddRule);

/// <summary>
/// The programs using one device (a Live readings card): the three busiest in the card, the full sortable list folded.
/// Values update in place; rows are only added, removed or moved.
/// </summary>
public sealed partial class ActivityList(DeviceKind kind, ActivityActions? actions = null) : ObservableObject
{
    [ObservableProperty] private ActivityRow? _selected;
    [ObservableProperty] private ProgramDetails? _details;

    partial void OnSelectedChanged(ActivityRow? oldValue, ActivityRow? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Details = null;
        if (newValue?.Activity is { } activity && actions is not null) LoadDetailsAsync(newValue, activity, actions).Forget("program details");
    }

    private async Task LoadDetailsAsync(ActivityRow row, ProgramActivity activity, ActivityActions actions)
    {
        var details = await Task.Run(() => actions.Details(activity));
        if (ReferenceEquals(Selected, row)) Details = details;
    }

    /// <summary>A click on a row selects it; a second click closes its details.</summary>
    [RelayCommand]
    private void Select(ActivityRow row) => Selected = ReferenceEquals(Selected, row) ? null : row;

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected?.Activity?.Program.Path is { } path) actions?.OpenLocation(path);
    }

    [RelayCommand]
    private void AddRule()
    {
        if (Selected?.Activity?.Program.ImageName is { } exe && Details?.CanAddRule == true) actions?.AddRule(exe);
    }

    /// <summary>Asks first, then ends every process of the program that still is the one the list saw.</summary>
    [RelayCommand]
    private async Task EndTaskAsync()
    {
        if (actions is null || Selected?.Activity is not { } activity) return;
        var details = Details ?? await Task.Run(() => actions.Details(activity));
        if (!details.CanEnd) return;
        var name = activity.Program.Name;
        if (!actions.Dialogs.Ask(Loc.Instance.Format("Activity_EndTitle", name), Loc.Instance.Format("Activity_EndText", details.ProcessesText),
                Loc.Instance["Activity_EndButton"])) return;
        var results = await Task.Run(() => activity.Processes.Select(p => actions.Terminate(p.Pid, p.CreateTime)).ToList());
        if (results.Where(r => !r.Ok).Select(r => (int?)r.Error).FirstOrDefault() is { } error)
            actions.Shell.ShowResult(Loc.Instance.Format("Activity_EndFailed", name, new System.ComponentModel.Win32Exception(error).Message),
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
        else
            actions.Shell.ShowResult(Loc.Instance.Format("Activity_EndDone", name), Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    /// <summary>Rows in the folded list before "Show all".</summary>
    public const int ListLength = 25;

    private readonly Dictionary<ProgramKey, ActivityRow> _rows = [];
    private IReadOnlyList<ProgramActivity> _programs = [];
    private bool _showAll;

    public DeviceKind Kind { get; } = kind;
    public bool ShowsReadWrite => Kind == DeviceKind.Drive;
    public bool ShowsEngine => Kind == DeviceKind.Gpu;
    public bool ShowsNow => Kind != DeviceKind.Drive;

    public ObservableCollection<ActivityRow> Top { get; } = [];
    public ObservableCollection<ActivityRow> Rows { get; } = [];

    [ObservableProperty] private string? _emptyText;
    [ObservableProperty] private string? _unavailableText;
    [ObservableProperty] private string? _showAllText;

    /// <summary>Drive data no program can be named for (threads that ended before a snapshot saw them).</summary>
    [ObservableProperty] private string? _otherText;
    [ObservableProperty] private ActivityColumn _sortColumn = ActivityColumn.Now;
    [ObservableProperty] private bool _sortDescending = true;

    /// <summary>Programs with any use since the readings were switched on.</summary>
    public int ProgramCount => _programs.Count;

    [RelayCommand]
    private void Sort(ActivityColumn column)
    {
        if (SortColumn == column) SortDescending = !SortDescending;
        else (SortColumn, SortDescending) = (column, column is not (ActivityColumn.Name or ActivityColumn.Engine));
        Apply();
    }

    // Which column header shows the sort chevron.
    public bool SortedByName => SortColumn == ActivityColumn.Name;
    public bool SortedByNow => SortColumn == ActivityColumn.Now;
    public bool SortedByRead => SortColumn == ActivityColumn.Read;
    public bool SortedByWrite => SortColumn == ActivityColumn.Write;
    public bool SortedBySinceOn => SortColumn == ActivityColumn.SinceOn;
    public bool SortedByEngine => SortColumn == ActivityColumn.Engine;
    public Wpf.Ui.Controls.SymbolRegular SortSymbol => SortDescending ? Wpf.Ui.Controls.SymbolRegular.ChevronDown12 : Wpf.Ui.Controls.SymbolRegular.ChevronUp12;

    partial void OnSortColumnChanged(ActivityColumn value)
    {
        foreach (var name in new[] { nameof(SortedByName), nameof(SortedByNow), nameof(SortedByRead), nameof(SortedByWrite), nameof(SortedBySinceOn), nameof(SortedByEngine) })
            OnPropertyChanged(name);
    }

    partial void OnSortDescendingChanged(bool value) => OnPropertyChanged(nameof(SortSymbol));

    [RelayCommand]
    private void ShowAll()
    {
        _showAll = true;
        Apply();
    }

    /// <summary>A new reading for this device; null when nothing used it yet. <paramref name="unavailable"/>: why no list can be shown.</summary>
    public void Update(DeviceActivity? device, string? unavailable)
    {
        UnavailableText = unavailable;
        _programs = unavailable is null ? device?.Programs ?? [] : [];
        foreach (var p in _programs)
        {
            if (!_rows.TryGetValue(p.Program.Key, out var row))
            {
                _rows[p.Program.Key] = row = new ActivityRow(p.Program.Key, p.Program.Name);
                if (p.Program.Path is { } path) ExeIcons.Load(path, icon => row.Icon = icon);
            }
            Fill(row, p);
        }
        var shown = _programs.Select(p => p.Program.Key).ToHashSet();
        foreach (var key in _rows.Keys.Where(k => !shown.Contains(k)).ToList()) _rows.Remove(key);
        if (Selected is { } selected && !shown.Contains(selected.Key)) Selected = null;
        var busy = device is not null && (device.Programs.Any(p => p.Now >= Visible) || device.OtherNow >= Visible);
        EmptyText = unavailable is null && !busy ? Loc.Instance["Activity_Nothing"] : null;
        OtherText = unavailable is null && device is { OtherSinceOn: >= 1024 * 1024 } ? Loc.Instance.Format("Activity_OtherLine", ByteSize.Format((long)device.OtherSinceOn)) : null;
        OnPropertyChanged(nameof(ProgramCount));
        Apply();
    }

    private void Apply()
    {
        var sorted = ActivityModel.Sort(_programs, SortColumn, SortDescending);
        Sync(Rows, (_showAll ? sorted : sorted.Take(ListLength)).Select(p => _rows[p.Program.Key]).ToList());
        Sync(Top, ActivityModel.Sort(_programs, ActivityColumn.Now, true).Where(p => p.Now >= Visible).Take(3).Select(p => _rows[p.Program.Key]).ToList());
        ShowAllText = !_showAll && sorted.Count > ListLength ? Loc.Instance.Format("Activity_ShowAll", sorted.Count) : null;
    }

    /// <summary>The least Now the card's top three show: a value that would read "0" there says nothing.</summary>
    private double Visible => Kind switch
    {
        DeviceKind.Processor => 0.05,
        DeviceKind.Gpu => 0.5,
        DeviceKind.Drive => 1024,
        _ => 1,
    };

    /// <summary>Makes the collection hold these rows in this order with as few changes as possible (no row is rebuilt).</summary>
    private static void Sync(ObservableCollection<ActivityRow> target, IReadOnlyList<ActivityRow> wanted)
    {
        var keep = wanted.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = target.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at < 0) target.Insert(i, wanted[i]);
            else target.Move(at, i);
        }
    }

    private void Fill(ActivityRow row, ProgramActivity p)
    {
        row.Activity = p;
        row.Ended = p.Ended;
        row.Fill = Math.Clamp(p.Share, 0, 1);
        row.Engine = p.Engine is { } engine ? EngineName(engine) : "";
        switch (Kind)
        {
            case DeviceKind.Processor:
                row.Now = Loc.Instance.Format("Activity_Percent", p.Now.ToString("0.0"));
                row.SinceOn = CpuTime(p.SinceOn);
                break;
            case DeviceKind.Memory:
                row.Now = ByteSize.Format((long)p.Now);
                row.SinceOn = ByteSize.Format((long)p.SinceOn);
                break;
            case DeviceKind.Gpu:
                row.Now = Loc.Instance.Format("Activity_Percent", p.Now.ToString("0"));
                row.SinceOn = Loc.Instance.Format("Activity_Percent", p.SinceOn.ToString("0"));
                break;
            default:
                row.Now = Rate(p.Now);
                row.Read = Rate(p.ReadNow);
                row.Write = Rate(p.WriteNow);
                row.SinceOn = ByteSize.Format((long)p.SinceOn);
                break;
        }
        if (p.Ended) row.Now = row.Read = row.Write = Loc.Instance["Activity_Ended"];
    }

    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "0" : Loc.Instance.Format("Activity_PerSecond", ByteSize.Format((long)bytesPerSecond));

    public static string CpuTime(double seconds) => seconds < 60
        ? Loc.Instance.Format("Activity_Seconds", seconds.ToString("0.0"))
        : Loc.Instance.Format("Activity_Minutes", (seconds / 60).ToString("0"));

    /// <summary>Task Manager's engine names: 3D, Copy, Video Decode, Video Encode, Compute.</summary>
    public static string EngineName(string engine) => engine switch
    {
        "VideoDecode" => Loc.Instance["Activity_EngineVideoDecode"],
        "VideoEncode" => Loc.Instance["Activity_EngineVideoEncode"],
        "VideoProcessing" => Loc.Instance["Activity_EngineVideoProcessing"],
        "Other" => Loc.Instance["Activity_EngineOther"],
        _ => engine,
    };
}

/// <summary>The small icon of an exe, read once per path off the UI thread (ExtractIconEx needs no COM).</summary>
internal static class ExeIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string path, Action<ImageSource> show)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            if (cached is not null) show(cached);
            return;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        Task.Run(() =>
        {
            var icon = Cache.GetOrAdd(path, Read);
            if (icon is not null) dispatcher?.BeginInvoke(() => show(icon));
        }).Forget("program icon");
    }

    private static ImageSource? Read(string path)
    {
        try
        {
            if (ExtractIconEx(path, 0, IntPtr.Zero, out var small, 1) == 0 || small == IntPtr.Zero) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(small, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                DestroyIcon(small);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint icons);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>
/// Developer aid (--activity-demo): fixed programs for reproducible screenshots of Live readings, with one demo drive
/// card. Reads nothing about real processes and never ends one; only the graphics adapters are the PC's own, so the
/// demo GPU use lands on the real graphics card's card.
/// </summary>
internal sealed class DemoActivitySources : IActivitySources
{
    public const int DemoDisk = 1;
    private const long Create = 133_000_000_000_000_000;
    private int _tick;

    private static readonly (int Pid, string Image, string Path, string Description, int Thread, long Memory, double CpuPerTick)[] Programs =
    [
        (4, "System", "", "", 41, 0, 0.02),
        (100, "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", "Steam", 1001, 412L << 20, 0.18),
        (200, "MsMpEng.exe", "", "", 2001, 188L << 20, 0.09),
        (300, "SearchIndexer.exe", @"C:\Windows\System32\SearchIndexer.exe", "Microsoft Windows Search Indexer", 3001, 64L << 20, 0.03),
        (400, "Cyberpunk2077.exe", @"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk 2077", 4001, 7340L << 20, 1.6),
        (500, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", 5001, 920L << 20, 0.12),
        (501, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", 5011, 610L << 20, 0.05),
        (60, "Memory Compression", "", "", 61, 540L << 20, 0),
    ];

    public static ProgramNames Names() => new(
        pid => Programs.FirstOrDefault(p => p.Pid == pid).Path is { Length: > 0 } path ? path : null,
        path => Programs.FirstOrDefault(p => p.Path == path).Description,
        () => new Dictionary<int, IReadOnlyList<string>>(),
        services => string.Join(", ", services));

    /// <summary>Signed by the demo publisher; nothing is ended (access denied).</summary>
    public static ActivityActions Actions(IDialogs dialogs, IShell shell) => new(dialogs, shell, (_, _) => (false, 5),
        p => ProgramDetails.Build(p, _ => false, path => new Optimizer.Core.Startup.SignatureInfo(Optimizer.Core.Startup.SignatureStatus.Signed,
            path?.Contains("Steam") == true ? "Valve Corp." : "Microsoft Corporation", false), Environment.ProcessId),
        _ => { }, _ => { });

    public ProcessSnapshot Snapshot()
    {
        _tick++;
        return new ProcessSnapshot(System.Diagnostics.Stopwatch.GetTimestamp(), Programs.Select(p =>
            new ProcessSample(p.Pid, 1, Create, p.Image, (long)(_tick * p.CpuPerTick * 2 * Environment.ProcessorCount * 10_000_000), p.Memory, [p.Thread])).ToList());
    }

    public IReadOnlyList<GpuEngineSample>? Gpu() => Adapters() is [var gpu, ..]
        ? [new(400, gpu.Luid, "3D", 62), new(400, gpu.Luid, "Copy", 4), new(500, gpu.Luid, "VideoDecode", 9)]
        : [];

    /// <summary>Bytes of one two second tick: Steam reads 380 MB/s, Defender 31 MB/s, Search 4 MB/s, a game saves.</summary>
    public IReadOnlyList<DiskIoTotal>? Disk(out string? unavailableKey)
    {
        unavailableKey = null;
        return
        [
            new(DemoDisk, 1001, 0, 760L << 20, 4L << 20),
            new(DemoDisk, 2001, 0, 62L << 20, 0),
            new(DemoDisk, 3001, 0, 6L << 20, 2L << 20),
            new(DemoDisk, 41, 400, 0, 24L << 20), // the game's save file, written by Windows' lazy writer
        ];
    }

    private IReadOnlyList<GpuAdapter>? _adapters;

    public IReadOnlyList<GpuAdapter> Adapters() => _adapters ??= GpuAdapters.Read();

    public void Dispose() { }
}
