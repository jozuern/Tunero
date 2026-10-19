using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Activity;

/// <summary>One engine of one graphics adapter used by one process (3D, VideoDecode, Copy and so on).</summary>
/// <param name="Luid">The adapter's locally unique ID as (HighPart &lt;&lt; 32) | LowPart.</param>
public sealed record GpuEngineSample(int Pid, long Luid, string EngineType, double Percent);

/// <summary>The instance names of the "GPU Engine" counter set, the source of Task Manager's per process GPU column.</summary>
public static partial class GpuEngineName
{
    // pid_1234_luid_0x00000000_0x0000C2F5_phys_0_eng_3_engtype_VideoDecode
    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9A-Fa-f]{1,8})_0x([0-9A-Fa-f]{1,8})_phys_\d+_eng_\d+_engtype_(.*)$")]
    private static partial Regex Instance();

    public static GpuEngineSample? Parse(string instance, double percent)
    {
        var m = Instance().Match(instance);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pid)) return null;
        var high = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var low = uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var engine = m.Groups[4].Value;
        return new GpuEngineSample(pid, (long)high << 32 | low, engine.Length == 0 ? "Other" : engine, percent);
    }
}

/// <summary>
/// Reads "\GPU Engine(*)\Utilization Percentage" through PDH. The counter is a rate: the first read only primes it.
/// Only reads; the query is closed on Dispose.
/// </summary>
public sealed class GpuEngineReader : IDisposable
{
    private IntPtr _query;
    private readonly IntPtr _counter;
    private bool _primed;

    private GpuEngineReader(IntPtr query, IntPtr counter) => (_query, _counter) = (query, counter);

    /// <summary>Null when the counter set is missing (old drivers, some remote sessions); logged.</summary>
    public static GpuEngineReader? TryOpen()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out var query) != 0)
        {
            Log.Warn("activity", "PDH query could not be opened");
            return null;
        }
        var status = Native.PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var counter);
        if (status != 0)
        {
            Native.PdhCloseQuery(query);
            Log.Warn("activity", $"GPU Engine counters not available: 0x{status:X8}");
            return null;
        }
        return new GpuEngineReader(query, counter);
    }

    public IReadOnlyList<GpuEngineSample> Read()
    {
        ObjectDisposedException.ThrowIf(_query == IntPtr.Zero, this);
        if (Native.PdhCollectQueryData(_query) != 0) return [];
        if (!_primed)
        {
            _primed = true;
            return [];
        }
        uint size = 0;
        var status = Native.PdhGetFormattedCounterArray(_counter, Native.PdhFmtDouble | Native.PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != Native.PdhMoreData || size == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = Native.PdhGetFormattedCounterArray(_counter, Native.PdhFmtDouble | Native.PdhFmtNoCap100, ref size, out var count, buffer);
            if (status != 0) return [];
            var list = new List<GpuEngineSample>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * 24;
                var cstatus = (uint)Marshal.ReadInt32(item, 8);
                if (cstatus is not (Native.PdhCstatusValidData or Native.PdhCstatusNewData)) continue;
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (value <= 0) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                if (name is not null && GpuEngineName.Parse(name, value) is { } sample) list.Add(sample);
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

public sealed record GpuAdapter(long Luid, string Name);

/// <summary>The hardware graphics adapters with the LUIDs the GPU Engine counters name (DXGI, read only).</summary>
public static class GpuAdapters
{
    // IDXGIFactory1::EnumAdapters1 is vtable slot 12, IDXGIAdapter1::GetDesc1 slot 10, IUnknown::Release slot 2.
    // DXGI_ADAPTER_DESC1: Description WCHAR[128] at 0, AdapterLuid (LowPart 296, HighPart 300), Flags 304; 312 bytes.
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002), FlagSoftware = 2;

    public static unsafe IReadOnlyList<GpuAdapter> Read()
    {
        if (Native.CreateDXGIFactory1(Native.IidDxgiFactory1, out var factory) < 0) return [];
        var list = new List<GpuAdapter>();
        try
        {
            var factoryVtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryVtbl[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0) break;
                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)adapterVtbl[10];
                    var desc = stackalloc byte[312];
                    if (getDesc1(adapter, desc) < 0) continue;
                    if ((*(uint*)(desc + 304) & FlagSoftware) != 0) continue;
                    var name = new string((char*)desc).Trim();
                    var luid = (long)*(int*)(desc + 300) << 32 | *(uint*)(desc + 296);
                    list.Add(new GpuAdapter(luid, name));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return list;
    }

    private static unsafe void Release(IntPtr unknown) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)unknown)[2])(unknown);
}
