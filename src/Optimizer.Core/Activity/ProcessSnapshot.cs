using System.Diagnostics;
using System.Runtime.InteropServices;
using Optimizer.Core.Interop;

namespace Optimizer.Core.Activity;

/// <summary>One process at the time of a snapshot. (Pid, CreateTime) tells a process apart from a later one with the same PID.</summary>
/// <param name="PrivateWorkingSet">Bytes of the working set no other process shares (Task Manager's Memory column).</param>
public sealed record ProcessSample(int Pid, int ParentPid, long CreateTime, string ImageName,
    long CpuTime100ns, long PrivateWorkingSet, IReadOnlyList<int> ThreadIds);

/// <param name="TakenTicks">Stopwatch ticks when the snapshot was read.</param>
public sealed record ProcessSnapshot(long TakenTicks, IReadOnlyList<ProcessSample> Processes);

/// <summary>
/// Every process with its CPU time, private memory and threads in one NtQuerySystemInformation(SystemProcessInformation)
/// call, the source Task Manager uses too. Only reads.
/// </summary>
public static class ProcessSnapshotReader
{
    // SYSTEM_PROCESS_INFORMATION (64 bit): NextEntryOffset 0, NumberOfThreads 4, WorkingSetPrivateSize 8, CreateTime 32,
    // UserTime 40, KernelTime 48, ImageName (UNICODE_STRING: Length 56, Buffer 64), UniqueProcessId 80,
    // InheritedFromUniqueProcessId 88; 256 bytes, followed by NumberOfThreads SYSTEM_THREAD_INFORMATION entries of 80
    // bytes each with ClientId.UniqueThread at 48.
    private const int ProcessEntrySize = 256, ThreadEntrySize = 80, ThreadIdOffset = 48;

    private static int _bufferSize = 512 * 1024;

    public static ProcessSnapshot Read()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The process snapshot reads the 64 bit layout.");
        while (true)
        {
            var size = _bufferSize;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buffer, size, out var needed);
                if (status == Native.StatusInfoLengthMismatch)
                {
                    _bufferSize = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }
                if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
                return new ProcessSnapshot(Stopwatch.GetTimestamp(), Parse(buffer));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static unsafe List<ProcessSample> Parse(IntPtr buffer)
    {
        var list = new List<ProcessSample>(512);
        var entry = (byte*)buffer;
        while (true)
        {
            var next = *(uint*)entry;
            var pid = (int)*(long*)(entry + 80);
            if (pid != 0) // System Idle Process
            {
                var threadCount = (int)*(uint*)(entry + 4);
                var threads = new int[threadCount];
                for (var i = 0; i < threadCount; i++)
                    threads[i] = (int)*(long*)(entry + ProcessEntrySize + i * ThreadEntrySize + ThreadIdOffset);
                var nameLength = *(ushort*)(entry + 56);
                var namePtr = *(char**)(entry + 64);
                var name = namePtr == null ? "" : new string(namePtr, 0, nameLength / 2);
                list.Add(new ProcessSample(pid, (int)*(long*)(entry + 88), *(long*)(entry + 32), name,
                    *(long*)(entry + 40) + *(long*)(entry + 48), *(long*)(entry + 8), threads));
            }
            if (next == 0) break;
            entry += next;
        }
        return list;
    }

    /// <summary>The full path of a process's exe; null when the process is gone or protected.</summary>
    public static unsafe string? ImagePath(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return Native.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Windows stops with a bug check when this process ends; false when it cannot be opened.</summary>
    public static bool IsCritical(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return Native.IsProcessCritical(handle, out var critical) && critical;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>The operating system ID of the calling thread (the IDs the snapshot and the disk trace use).</summary>
    public static int CurrentThreadId() => Native.GetCurrentThreadId();
}
