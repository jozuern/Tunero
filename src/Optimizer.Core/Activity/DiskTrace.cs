using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Activity;

/// <summary>One completed disk read or write, the thread that issued it and the file it belongs to (0: none).</summary>
public readonly record struct DiskIoEvent(int DiskNumber, int Bytes, int ThreadId, bool Write, long FileKey);

/// <summary>
/// Bytes one thread read and wrote on one physical disk since the last drain. <paramref name="FilePid"/> is the process
/// that last read or wrote that file through the file system (0: none seen): Windows writes cached data and reads ahead
/// from its own threads, so for those bytes the file says which program they are for.
/// </summary>
public readonly record struct DiskIoTotal(int DiskNumber, int ThreadId, int FilePid, long ReadBytes, long WriteBytes);

/// <summary>
/// The payload of the kernel's DiskIo Read (10) and Write (11) events, class DiskIo_TypeGroup1
/// (https://learn.microsoft.com/windows/win32/etw/diskio-typegroup1): DiskNumber at 0, TransferSize at 8, FileObject
/// at 24, IssuingThreadId at 48 with 64 bit pointers (FileObject and Irp are pointers) or at 40 with 32 bit ones. The
/// FileObject value is the file's key, the value FileIo events carry as FileKey.
/// </summary>
public static class DiskIoPayload
{
    public static DiskIoEvent? Parse(ReadOnlySpan<byte> data, int opcode, bool pointer64)
    {
        if (opcode is not (10 or 11)) return null;
        var threadOffset = pointer64 ? 48 : 40;
        if (data.Length < threadOffset + 4) return null;
        var fileKey = pointer64 ? BitConverter.ToInt64(data.Slice(24, 8)) : BitConverter.ToUInt32(data.Slice(24, 4));
        return new DiskIoEvent(BitConverter.ToInt32(data[..4]), BitConverter.ToInt32(data.Slice(8, 4)),
            BitConverter.ToInt32(data.Slice(threadOffset, 4)), opcode == 11, fileKey);
    }
}

/// <summary>A program read (or wrote) the file with this key through the file system.</summary>
public readonly record struct FileIoEvent(long FileKey, bool Write);

/// <summary>
/// File system requests that say a program reads or writes a file. These events are logged by the program's own thread,
/// so their header names it. Read (67) and Write (68), class FileIo_ReadWrite
/// (https://learn.microsoft.com/windows/win32/etw/fileio-readwrite): Offset, IrpPtr and FileObject come first, so
/// FileKey is at 24 with 64 bit pointers and at 16 with 32 bit ones. SetInfo (69), class FileIo_Info
/// (https://learn.microsoft.com/windows/win32/etw/fileio-info): IrpPtr and FileObject, then FileKey at 16 or 8; it counts
/// as writing, because copies Windows makes inside the kernel issue no write requests of the program but it sets the
/// new file's size.
/// </summary>
public static class FileIoPayload
{
    public static FileIoEvent? Parse(ReadOnlySpan<byte> data, int opcode, bool pointer64)
    {
        var offset = (opcode, pointer64) switch
        {
            (67 or 68, true) => 24,
            (67 or 68, false) => 16,
            (69, true) => 16,
            (69, false) => 8,
            _ => -1,
        };
        var size = pointer64 ? 8 : 4;
        if (offset < 0 || data.Length < offset + size) return null;
        var key = pointer64 ? BitConverter.ToInt64(data.Slice(offset, 8)) : BitConverter.ToUInt32(data.Slice(offset, 4));
        return new FileIoEvent(key, opcode != 67);
    }
}

/// <summary>
/// Sums disk events per disk, thread and file owner between drains, and remembers which program last used each file.
/// Add and NoteFile are called from the trace thread, Drain from the reader.
/// </summary>
public sealed class DiskIoAccumulator
{
    /// <summary>Files remembered at most; past it the list starts over (a busy PC touches many files).</summary>
    private const int MaxFiles = 100_000;

    private readonly Lock _lock = new();
    private Dictionary<(int Disk, int Thread, int FilePid), (long Read, long Write)> _totals = [];
    private readonly Dictionary<long, int> _writers = [], _readers = [];

    /// <summary>
    /// A program read or wrote this file through the file system. Disk writes go to the last writer and disk reads to the
    /// last reader, so a virus scanner reading a new file is not blamed when Windows writes it out.
    /// </summary>
    public void NoteFile(FileIoEvent e, int pid)
    {
        if (e.FileKey == 0 || pid is 0 or 4) return; // the file system work of Windows itself (System) names no program
        lock (_lock)
        {
            var owners = e.Write ? _writers : _readers;
            if (owners.Count >= MaxFiles) owners.Clear();
            owners[e.FileKey] = pid;
        }
    }

    public void Add(DiskIoEvent e)
    {
        lock (_lock)
        {
            var filePid = e.FileKey != 0 && (e.Write ? _writers : _readers).TryGetValue(e.FileKey, out var owner) ? owner : 0;
            ref var total = ref CollectionsMarshal.GetValueRefOrAddDefault(_totals, (e.DiskNumber, e.ThreadId, filePid), out _);
            if (e.Write) total.Write += e.Bytes;
            else total.Read += e.Bytes;
        }
    }

    public IReadOnlyList<DiskIoTotal> Drain()
    {
        Dictionary<(int Disk, int Thread, int FilePid), (long Read, long Write)> totals;
        lock (_lock)
        {
            if (_totals.Count == 0) return [];
            totals = _totals;
            _totals = [];
        }
        return totals.Select(t => new DiskIoTotal(t.Key.Disk, t.Key.Thread, t.Key.FilePid, t.Value.Read, t.Value.Write)).ToList();
    }
}

/// <summary>Why the disk trace cannot run; <see cref="Exception.Message"/> is a Strings resource key.</summary>
public sealed class DiskTraceUnavailableException(string reasonKey) : Exception(reasonKey);

/// <summary>
/// A private real-time kernel trace session that receives disk read and write events and the file system's read and
/// write requests, so each disk's activity can be put down to the programs that cause it, including the cached writes and
/// read-ahead Windows does later from its own threads (matched through the file). A system logger session (Windows 8 and later) does not take the single
/// "NT Kernel Logger" slot that Process Monitor and the Windows Performance Recorder use. It runs only while Live
/// readings are on. Each copy of the app names its session after its process ("Tunero disk activity 1234"), so a copy
/// in another user's session never stops this one; a session left behind by a crash carries a process ID that no
/// longer runs and is stopped before a new one starts.
/// </summary>
public sealed unsafe class DiskTrace : IDisposable
{
    public const string SessionName = "Tunero disk activity";

    /// <summary>This process's session name: the shared prefix and the process ID.</summary>
    public static string OwnSessionName => $"{SessionName} {Environment.ProcessId}";

    /// <summary>Sessions listed at most when leftovers are looked for (Windows runs at most 64 at a time).</summary>
    private const int MaxSessions = 64;

    private static readonly Guid SessionGuid = new("5b1f3c52-2d0e-4c8a-9f4e-7d3a1c6b8e21");

    private readonly DiskIoAccumulator _accumulator = new();
    private GCHandle _self;
    private ulong _traceHandle = Etw.InvalidProcessTraceHandle;

    /// <summary>This copy's session: Dispose stops it by handle, so it can never stop another copy's session.</summary>
    private ulong _sessionHandle;
    private Thread? _thread;
    private int _disposed, _ended;
    private string _name = "";

    /// <summary>The trace stopped on its own (another tool stopped the session, for example): it delivers nothing any more.</summary>
    public bool Ended => Volatile.Read(ref _ended) != 0;

    private DiskTrace() { }

    public static DiskTrace Start()
    {
        if (!Environment.Is64BitProcess) throw new DiskTraceUnavailableException("Activity_TraceFailed");
        var name = OwnSessionName;
        StopLeftovers(name);
        var buffer = Properties(withFlags: true);
        ulong session;
        fixed (byte* p = buffer)
        {
            var error = Etw.StartTrace(out session, name, p);
            if (error != 0)
            {
                Log.Warn("activity", $"disk trace not started: {error}");
                throw new DiskTraceUnavailableException(error switch
                {
                    Etw.ErrorAccessDenied => "Activity_TraceDenied",
                    Etw.ErrorNoSystemResources => "Activity_TraceBusy",
                    _ => "Activity_TraceFailed",
                });
            }
        }
        var trace = new DiskTrace { _sessionHandle = session, _name = name };
        try
        {
            trace.Open();
        }
        catch
        {
            trace.Dispose();
            throw;
        }
        return trace;
    }

    private void Open()
    {
        _self = GCHandle.Alloc(this);
        fixed (char* name = _name)
        {
            var logfile = new Etw.EVENT_TRACE_LOGFILEW
            {
                LoggerName = name,
                ProcessTraceMode = Etw.ProcessTraceModeRealTime | Etw.ProcessTraceModeEventRecord,
                EventRecordCallback = (IntPtr)(delegate* unmanaged[Stdcall]<byte*, void>)&OnEvent,
                Context = GCHandle.ToIntPtr(_self),
            };
            _traceHandle = Etw.OpenTrace(&logfile);
        }
        if (_traceHandle == Etw.InvalidProcessTraceHandle)
        {
            Log.Warn("activity", $"disk trace not opened: {Marshal.GetLastPInvokeError()}");
            throw new DiskTraceUnavailableException("Activity_TraceFailed");
        }
        var handle = _traceHandle;
        _thread = new Thread(() =>
        {
            var h = handle;
            var error = Etw.ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _ended, 1);
            Log.Warn("activity", $"disk trace ended: {error}");
        }) { IsBackground = true, Name = "Disk trace" };
        _thread.Start();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(byte* record)
    {
        var provider = *(Guid*)(record + Etw.RecordProviderId);
        var disk = provider == Etw.DiskIoGuid;
        if (!disk && provider != Etw.FileIoGuid) return;
        var context = *(IntPtr*)(record + Etw.RecordUserContext);
        if (context == IntPtr.Zero || GCHandle.FromIntPtr(context).Target is not DiskTrace trace) return;
        var pointer64 = (*(ushort*)(record + Etw.RecordFlags) & Etw.EventHeaderFlag32BitHeader) == 0;
        var data = new ReadOnlySpan<byte>(*(byte**)(record + Etw.RecordUserData), *(ushort*)(record + Etw.RecordUserDataLength));
        var opcode = record[Etw.RecordOpcode];
        if (disk)
        {
            if (DiskIoPayload.Parse(data, opcode, pointer64) is { } e) trace._accumulator.Add(e);
        }
        else if (FileIoPayload.Parse(data, opcode, pointer64) is { } file)
            trace._accumulator.NoteFile(file, *(int*)(record + Etw.RecordProcessId));
    }

    public IReadOnlyList<DiskIoTotal> Drain() => _accumulator.Drain();

    /// <summary>Stops a session by its handle, or by name when the handle is 0; no error when there is none.</summary>
    private static void Stop(ulong session, string? name = null)
    {
        var buffer = Properties(withFlags: false);
        fixed (byte* p = buffer)
        {
            var error = Etw.ControlTrace(session, session == 0 ? name : null, p, Etw.EventTraceControlStop);
            if (error is not (0 or Etw.ErrorWmiInstanceNotFound)) Log.Warn("activity", $"disk trace not stopped: {error}");
        }
    }

    /// <summary>
    /// Stops what crashes left behind: this process's own name (a reused process ID) and every "Tunero disk activity
    /// &lt;pid&gt;" session whose process no longer runs. A running copy's session is never touched.
    /// </summary>
    private static void StopLeftovers(string own)
    {
        Stop(0, own);
        foreach (var name in SessionNames().Where(n => IsLeftover(n, own, ProcessRuns)))
            Stop(0, name);
    }

    /// <summary>
    /// A session of this app that no running process owns. The plain name older versions used is left alone: a running
    /// older copy could own it.
    /// </summary>
    internal static bool IsLeftover(string session, string own, Func<int, bool> runs) =>
        session != own
        && session.StartsWith(SessionName + " ", StringComparison.Ordinal)
        && int.TryParse(session.AsSpan(SessionName.Length + 1), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var pid)
        && !runs(pid);

    private static bool ProcessRuns(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process with that ID
        }
        catch (Exception)
        {
            return true; // cannot tell: the session stays
        }
    }

    /// <summary>The names of the trace sessions that run now (QueryAllTracesW); empty when they cannot be listed.</summary>
    private static List<string> SessionNames()
    {
        const int nameBytes = 2048;
        const int entry = Etw.PropertiesSize + 2 * nameBytes;
        var buffer = new byte[entry * MaxSessions];
        for (var i = 0; i < MaxSessions; i++)
        {
            var span = buffer.AsSpan(i * entry, entry);
            BitConverter.TryWriteBytes(span[0..], entry);                                   // Wnode.BufferSize
            BitConverter.TryWriteBytes(span[112..], (uint)(Etw.PropertiesSize + nameBytes)); // LogFileNameOffset
            BitConverter.TryWriteBytes(span[116..], (uint)Etw.PropertiesSize);               // LoggerNameOffset
        }
        var names = new List<string>();
        fixed (byte* start = buffer)
        {
            var pointers = stackalloc byte*[MaxSessions];
            for (var i = 0; i < MaxSessions; i++) pointers[i] = start + i * entry;
            var error = Etw.QueryAllTraces(pointers, MaxSessions, out var count);
            if (error is not (0 or Etw.ErrorMoreData))
            {
                Log.Warn("activity", $"trace sessions not listed: {error}");
                return names;
            }
            for (var i = 0; i < Math.Min(count, (uint)MaxSessions); i++)
            {
                var offset = *(uint*)(pointers[i] + 116);
                if (offset is 0 or >= entry - 2) continue;
                // Bounded read: the name ends at its terminating zero or at the end of its room.
                var room = new ReadOnlySpan<char>(pointers[i] + offset, (int)((entry - offset) / 2));
                var end = room.IndexOf('\0');
                names.Add(new string(end < 0 ? room : room[..end]));
            }
        }
        return names;
    }

    /// <summary>EVENT_TRACE_PROPERTIES followed by room for the session name (offsets in the comments).</summary>
    private static byte[] Properties(bool withFlags)
    {
        var buffer = new byte[Etw.PropertiesSize + 2048];
        var span = buffer.AsSpan();
        BitConverter.TryWriteBytes(span[0..], buffer.Length);                 // Wnode.BufferSize
        if (withFlags)
        {
            SessionGuid.TryWriteBytes(span[24..]);                             // Wnode.Guid
            BitConverter.TryWriteBytes(span[40..], 1u);                        // Wnode.ClientContext: QueryPerformanceCounter
            BitConverter.TryWriteBytes(span[44..], Etw.WnodeFlagTracedGuid);  // Wnode.Flags
            BitConverter.TryWriteBytes(span[48..], 64u);                       // BufferSize (KB)
            BitConverter.TryWriteBytes(span[52..], 4u);                        // MinimumBuffers
            BitConverter.TryWriteBytes(span[56..], 32u);                       // MaximumBuffers
            BitConverter.TryWriteBytes(span[64..], Etw.EventTraceRealTimeMode | Etw.EventTraceSystemLoggerMode); // LogFileMode
            BitConverter.TryWriteBytes(span[68..], 1u);                        // FlushTimer (seconds)
            BitConverter.TryWriteBytes(span[72..], Etw.EventTraceFlagDiskIo | Etw.EventTraceFlagFileIoInit); // EnableFlags
        }
        BitConverter.TryWriteBytes(span[116..], (uint)Etw.PropertiesSize);     // LoggerNameOffset
        return buffer;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_sessionHandle != 0) Stop(_sessionHandle);
        if (_traceHandle != Etw.InvalidProcessTraceHandle) Etw.CloseTrace(_traceHandle);
        // The callback reads the handle until ProcessTrace returns: a thread that did not end keeps it (a small leak, no crash).
        if ((_thread?.Join(TimeSpan.FromSeconds(2)) ?? true) && _self.IsAllocated) _self.Free();
    }
}
