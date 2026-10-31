using System.Diagnostics;

namespace Optimizer.Core.Activity;

public enum DeviceKind { Processor, Memory, Gpu, Drive }

/// <param name="Id">The adapter LUID for a graphics card, the Windows disk number for a drive, 0 otherwise.</param>
public readonly record struct DeviceKey(DeviceKind Kind, long Id);

public enum ActivityColumn { Name, Now, Read, Write, SinceOn, Engine }

/// <summary>A process of a program; the create time tells it apart from a later process with the same PID.</summary>
public readonly record struct ProcessRef(int Pid, long CreateTime);

/// <summary>
/// What one program does on one device. Now and SinceOn by device: processor % of the whole processor and CPU seconds;
/// memory private bytes and the peak; graphics card % of the busiest engine and the peak; drive bytes per second
/// (ReadNow + WriteNow) and the bytes since the readings were switched on. Share is 0 to 1 of the device.
/// An ended program uses nothing now (Now 0) and keeps its Since on value until it is dropped.
/// </summary>
public sealed record ProgramActivity(ProgramIdentity Program, IReadOnlyList<ProcessRef> Processes, double Now, double ReadNow,
    double WriteNow, double SinceOn, double Share, string? Engine, bool Ended);

/// <param name="OtherNow">Drive bytes per second no program can be named for (threads that ended before they were seen).</param>
public sealed record DeviceActivity(DeviceKey Device, IReadOnlyList<ProgramActivity> Programs, double OtherNow, double OtherSinceOn);

public sealed record ActivityTick(ProcessSnapshot Snapshot, IReadOnlyList<GpuEngineSample> Gpu, IReadOnlyList<DiskIoTotal> Disk);

/// <summary>
/// Turns one tick of raw readings (process snapshot, GPU engines, disk totals) into the programs that use each device.
/// Pure: everything it knows comes from the ticks, so it is tested without touching the PC.
/// </summary>
public sealed class ActivityModel(IProgramNames names, int logicalProcessors, TimeSpan endedKeep)
{
    private const int SystemPid = 4;

    private sealed class Entry(ProgramIdentity program)
    {
        public ProgramIdentity Program = program;
        public List<ProcessRef> Processes = [];
        public double Now, ReadNow, WriteNow, SinceOn;
        public string? Engine;
        public bool Live;
        public long LastLiveTicks;
    }

    private sealed record Known(ProcessSample Process, ProgramIdentity Program, long SeenTicks);

    private readonly Dictionary<(DeviceKey Device, ProgramKey Program), Entry> _entries = [];
    private readonly Dictionary<(int Pid, long Create), long> _cpu = [];
    private readonly Dictionary<int, Known> _recent = [];
    private readonly Dictionary<int, (int Pid, long SeenTicks)> _threads = [];
    private readonly Dictionary<long, (double Now, double SinceOn)> _driveOther = [];
    private long? _lastTicks;

    /// <summary>
    /// The readings start again after a pause (the Health page was hidden): the next tick is a first one, so the time away
    /// counts neither for Now nor for Since on. Since on itself is kept.
    /// </summary>
    public void Resume()
    {
        _cpu.Clear();
        _lastTicks = null;
    }

    public IReadOnlyDictionary<DeviceKey, DeviceActivity> Update(ActivityTick tick)
    {
        names.Refresh(tick.Snapshot.Processes);
        var now = tick.Snapshot.TakenTicks;
        var elapsed = _lastTicks is { } last && now > last ? (now - last) / (double)Stopwatch.Frequency : 0;
        _lastTicks = now;
        var keep = (long)(endedKeep.TotalSeconds * Stopwatch.Frequency);

        foreach (var e in _entries.Values) (e.Now, e.ReadNow, e.WriteNow, e.Live) = (0, 0, 0, false);
        foreach (var k in _driveOther.Keys.ToList()) _driveOther[k] = (0, _driveOther[k].SinceOn);

        // Who runs now: identities, threads, live processes per program. The identity is this tick's: a svchost's name and
        // services follow the services it hosts now, so an entry takes it over below instead of keeping the first one seen.
        var live = new Dictionary<ProgramKey, (ProgramIdentity Program, List<ProcessRef> Refs)>();
        var byPid = new Dictionary<int, (ProcessSample Process, ProgramIdentity Program)>();
        foreach (var p in tick.Snapshot.Processes)
        {
            var program = names.Identify(p);
            byPid[p.Pid] = (p, program);
            _recent[p.Pid] = new Known(p, program, now);
            foreach (var t in p.ThreadIds) _threads[t] = (p.Pid, now);
            if (!live.TryGetValue(program.Key, out var current)) live[program.Key] = current = (program, []);
            current.Refs.Add(new ProcessRef(p.Pid, p.CreateTime));
        }

        // Processor and memory
        var seenCpu = new HashSet<(int, long)>();
        foreach (var (p, program) in byPid.Values)
        {
            var id = (p.Pid, p.CreateTime);
            seenCpu.Add(id);
            if (_cpu.TryGetValue(id, out var before) && elapsed > 0)
            {
                var delta = Math.Max(0, p.CpuTime100ns - before);
                if (delta > 0)
                {
                    var cpu = Get(new DeviceKey(DeviceKind.Processor, 0), program);
                    cpu.Now += delta / (elapsed * 10_000_000 * logicalProcessors) * 100;
                    cpu.SinceOn += delta / 10_000_000.0;
                }
            }
            _cpu[id] = p.CpuTime100ns;
            if (p.PrivateWorkingSet > 0) Get(new DeviceKey(DeviceKind.Memory, 0), program).Now += p.PrivateWorkingSet;
        }
        foreach (var id in _cpu.Keys.Where(k => !seenCpu.Contains(k)).ToList()) _cpu.Remove(id);
        foreach (var e in _entries.Where(e => e.Key.Device.Kind == DeviceKind.Memory).Select(e => e.Value)) e.SinceOn = Math.Max(e.SinceOn, e.Now);

        // Graphics cards: per program the busiest engine of its processes
        foreach (var g in tick.Gpu.GroupBy(s => (s.Luid, s.Pid)))
        {
            if (!byPid.TryGetValue(g.Key.Pid, out var owner)) continue;
            var busiest = g.MaxBy(s => s.Percent)!;
            var e = Get(new DeviceKey(DeviceKind.Gpu, g.Key.Luid), owner.Program);
            if (busiest.Percent <= e.Now) continue;
            e.Now = busiest.Percent;
            e.Engine = busiest.EngineType;
            e.SinceOn = Math.Max(e.SinceOn, e.Now);
        }

        // Drives: the issuing thread's program; Windows' own threads (cached writes, read-ahead) give way to the file's program
        foreach (var d in tick.Disk)
        {
            int? issuer = _threads.TryGetValue(d.ThreadId, out var t) ? t.Pid : null;
            var credited = issuer is null or SystemPid && d.FilePid is not (0 or SystemPid) && _recent.ContainsKey(d.FilePid) ? d.FilePid : issuer;
            var device = new DeviceKey(DeviceKind.Drive, d.DiskNumber);
            var bytes = d.ReadBytes + d.WriteBytes;
            if (credited is not { } pid || !_recent.TryGetValue(pid, out var known))
            {
                var other = _driveOther.GetValueOrDefault(d.DiskNumber);
                _driveOther[d.DiskNumber] = (other.Now + (elapsed > 0 ? bytes / elapsed : 0), other.SinceOn + bytes);
                continue;
            }
            var e = Get(device, known.Program);
            if (elapsed > 0)
            {
                e.ReadNow += d.ReadBytes / elapsed;
                e.WriteNow += d.WriteBytes / elapsed;
                e.Now = e.ReadNow + e.WriteNow;
            }
            e.SinceOn += bytes;
            if (!live.ContainsKey(known.Program.Key)) e.LastLiveTicks = Math.Max(e.LastLiveTicks, known.SeenTicks);
        }

        // Live or ended; ended programs are kept for a while, then dropped
        foreach (var (key, e) in _entries.ToList())
        {
            if (live.TryGetValue(key.Program, out var current))
            {
                e.Live = true;
                e.LastLiveTicks = now;
                e.Program = current.Program;
                e.Processes = current.Refs;
            }
            else if (now - e.LastLiveTicks > keep) _entries.Remove(key);
        }
        foreach (var pid in _recent.Where(r => now - r.Value.SeenTicks > keep).Select(r => r.Key).ToList()) _recent.Remove(pid);
        foreach (var tid in _threads.Where(r => now - r.Value.SeenTicks > keep).Select(r => r.Key).ToList()) _threads.Remove(tid);

        return Build();
    }

    private Entry Get(DeviceKey device, ProgramIdentity program)
    {
        if (!_entries.TryGetValue((device, program.Key), out var e)) _entries[(device, program.Key)] = e = new Entry(program);
        return e;
    }

    private Dictionary<DeviceKey, DeviceActivity> Build()
    {
        var devices = new Dictionary<DeviceKey, DeviceActivity>();
        var groups = _entries.GroupBy(e => e.Key.Device).ToDictionary(g => g.Key, g => g.Select(e => e.Value).ToList());
        foreach (var key in groups.Keys.Concat(_driveOther.Keys.Select(d => new DeviceKey(DeviceKind.Drive, d)))
                     .Append(new DeviceKey(DeviceKind.Processor, 0)).Append(new DeviceKey(DeviceKind.Memory, 0)).Distinct())
        {
            var entries = groups.GetValueOrDefault(key) ?? [];
            var other = key.Kind == DeviceKind.Drive ? _driveOther.GetValueOrDefault(key.Id) : default;
            var total = key.Kind switch
            {
                DeviceKind.Processor or DeviceKind.Gpu => 100,
                DeviceKind.Memory => entries.Where(e => e.Live).Sum(e => e.Now),
                _ => entries.Where(e => e.Live).Sum(e => e.Now) + other.Now,
            };
            var rows = entries.Where(e => e.Now > 0 || e.SinceOn > 0)
                .Select(e => new ProgramActivity(e.Program, e.Processes, e.Now, e.ReadNow, e.WriteNow, e.SinceOn,
                    total > 0 ? e.Now / total : 0, e.Engine, !e.Live))
                .ToList();
            devices[key] = new DeviceActivity(key, Sort(rows, ActivityColumn.Now, descending: true), other.Now, other.SinceOn);
        }
        return devices;
    }

    /// <summary>By the column; ties by Since on (descending), then by name.</summary>
    public static IReadOnlyList<ProgramActivity> Sort(IEnumerable<ProgramActivity> rows, ActivityColumn column, bool descending)
    {
        Func<ProgramActivity, double> value = column switch
        {
            ActivityColumn.Read => r => r.ReadNow,
            ActivityColumn.Write => r => r.WriteNow,
            ActivityColumn.SinceOn => r => r.SinceOn,
            _ => r => r.Now,
        };
        IOrderedEnumerable<ProgramActivity> sorted = column switch
        {
            ActivityColumn.Name => descending
                ? rows.OrderByDescending(r => r.Program.Name, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(r => r.Program.Name, StringComparer.CurrentCultureIgnoreCase),
            ActivityColumn.Engine => descending
                ? rows.OrderByDescending(r => r.Engine ?? "", StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(r => r.Engine ?? "", StringComparer.CurrentCultureIgnoreCase),
            _ => descending ? rows.OrderByDescending(value) : rows.OrderBy(value),
        };
        return sorted.ThenByDescending(r => r.SinceOn).ThenBy(r => r.Program.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
