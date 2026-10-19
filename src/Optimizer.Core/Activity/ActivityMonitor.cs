using System.Runtime.InteropServices;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Activity;

/// <summary>The raw readings behind Live readings' program lists; faked in tests and in screenshots.</summary>
public interface IActivitySources : IDisposable
{
    ProcessSnapshot Snapshot();

    /// <summary>Null when the GPU engine counters are not available.</summary>
    IReadOnlyList<GpuEngineSample>? Gpu();

    /// <summary>Null with a Strings key when the disk trace does not run.</summary>
    IReadOnlyList<DiskIoTotal>? Disk(out string? unavailableKey);

    IReadOnlyList<GpuAdapter> Adapters();
}

/// <summary>The real sources: process snapshot, PDH GPU engines and the disk trace (elevated only).</summary>
public sealed class NativeActivitySources : IActivitySources
{
    private readonly GpuEngineReader? _gpu;
    private readonly DiskTrace? _disk;
    private readonly string? _diskUnavailable;
    private readonly IReadOnlyList<GpuAdapter> _adapters;

    private NativeActivitySources(GpuEngineReader? gpu, DiskTrace? disk, string? diskUnavailable, IReadOnlyList<GpuAdapter> adapters) =>
        (_gpu, _disk, _diskUnavailable, _adapters) = (gpu, disk, diskUnavailable, adapters);

    public static NativeActivitySources Open(bool elevated)
    {
        IReadOnlyList<GpuAdapter> adapters;
        try
        {
            adapters = GpuAdapters.Read();
        }
        catch (Exception ex)
        {
            Log.Warn("activity", $"graphics adapters not read: {ex.Message}");
            adapters = [];
        }
        DiskTrace? disk = null;
        string? unavailable = null;
        if (!elevated) unavailable = "Activity_TraceNotElevated";
        else
        {
            try
            {
                disk = DiskTrace.Start();
            }
            catch (DiskTraceUnavailableException ex)
            {
                unavailable = ex.Message;
            }
        }
        return new NativeActivitySources(GpuEngineReader.TryOpen(), disk, unavailable, adapters);
    }

    public ProcessSnapshot Snapshot() => ProcessSnapshotReader.Read();

    public IReadOnlyList<GpuEngineSample>? Gpu() => _gpu?.Read();

    public IReadOnlyList<DiskIoTotal>? Disk(out string? unavailableKey)
    {
        unavailableKey = _diskUnavailable;
        return _disk?.Drain();
    }

    public IReadOnlyList<GpuAdapter> Adapters() => _adapters;

    public void Dispose()
    {
        _disk?.Dispose();
        _gpu?.Dispose();
    }
}

public sealed record ActivityResult(IReadOnlyDictionary<DeviceKey, DeviceActivity> Devices, bool GpuAvailable, string? DiskUnavailableKey);

/// <summary>Reads the sources once per tick into the model. A failing source never stops the others.</summary>
public sealed class ActivityMonitor(IActivitySources sources, ActivityModel model) : IDisposable
{
    private ActivityResult? _last;
    private readonly HashSet<string> _logged = [];

    public IReadOnlyList<GpuAdapter> Adapters => sources.Adapters();

    public ActivityResult Read()
    {
        ProcessSnapshot snapshot;
        try
        {
            snapshot = sources.Snapshot();
        }
        catch (Exception ex)
        {
            LogOnce("snapshot", ex);
            return _last ?? new ActivityResult(new Dictionary<DeviceKey, DeviceActivity>(), false, null);
        }
        IReadOnlyList<GpuEngineSample>? gpu;
        try
        {
            gpu = sources.Gpu();
        }
        catch (Exception ex)
        {
            LogOnce("gpu", ex);
            gpu = null;
        }
        IReadOnlyList<DiskIoTotal>? disk;
        string? diskKey;
        try
        {
            disk = sources.Disk(out diskKey);
        }
        catch (Exception ex)
        {
            LogOnce("disk", ex);
            (disk, diskKey) = (null, "Activity_TraceFailed");
        }
        return _last = new ActivityResult(model.Update(new ActivityTick(snapshot, gpu ?? [], disk ?? [])), gpu is not null, diskKey);
    }

    private void LogOnce(string source, Exception ex)
    {
        if (_logged.Add(source)) Log.Warn("activity", $"{source}: {ex.Message}");
    }

    public void Dispose() => sources.Dispose();
}

/// <summary>Which device key a Live readings card (a LibreHardwareMonitor component) shows.</summary>
public static class DeviceMapping
{
    /// <summary>"/nvme/3", "/ssd/3", "/hdd/3": LibreHardwareMonitor names storage by the Windows disk number.</summary>
    public static int? DiskNumber(string libreHardwareId)
    {
        var parts = libreHardwareId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] is "nvme" or "ssd" or "hdd" && int.TryParse(parts[1], out var n) ? n : null;
    }

    /// <summary>The adapter of that name; null when there is none or two share the name.</summary>
    public static long? GpuLuid(string libreHardwareName, IReadOnlyList<GpuAdapter> adapters)
    {
        var matches = adapters.Where(a => string.Equals(a.Name, libreHardwareName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Luid : null;
    }
}

/// <summary>The services each svchost process hosts (display names), read from the service control manager.</summary>
public static class ServiceHosts
{
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Read()
    {
        var result = new Dictionary<int, IReadOnlyList<string>>();
        var scm = NativeWrite.OpenSCManager(null, null, Native.ScManagerEnumerateService);
        if (scm == IntPtr.Zero) return result;
        var buffer = IntPtr.Zero;
        try
        {
            var resume = 0;
            Native.EnumServicesStatusEx(scm, Native.ScEnumProcessInfo, Native.ServiceWin32, Native.ServiceActive, IntPtr.Zero, 0, out var needed, out _, ref resume, IntPtr.Zero);
            if (needed <= 0) return result;
            needed += 4096;
            buffer = Marshal.AllocHGlobal(needed);
            resume = 0;
            if (!Native.EnumServicesStatusEx(scm, Native.ScEnumProcessInfo, Native.ServiceWin32, Native.ServiceActive, buffer, needed, out _, out var count, ref resume, IntPtr.Zero))
                return result;
            var byPid = new Dictionary<int, List<string>>();
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * 56;
                var pid = Marshal.ReadInt32(item, 44);
                if (pid == 0) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 8)) ?? Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                if (!byPid.TryGetValue(pid, out var list)) byPid[pid] = list = [];
                list.Add(name);
            }
            foreach (var (pid, list) in byPid) result[pid] = list.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            return result;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            NativeWrite.CloseServiceHandle(scm);
        }
    }
}
