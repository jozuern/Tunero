using System.Runtime.InteropServices;

namespace Optimizer.Core.Interop;

/// <summary>
/// Event Tracing for Windows, for the Live readings disk trace. This file starts and stops one temporary real-time trace
/// session (<see cref="Activity.DiskTrace"/>), lists the running sessions to find its own leftovers and reads its events;
/// it writes nothing else. Layouts are the 64 bit ones.
/// </summary>
internal static unsafe partial class Etw
{
    internal const uint WnodeFlagTracedGuid = 0x00020000;
    internal const uint EventTraceRealTimeMode = 0x00000100, EventTraceSystemLoggerMode = 0x02000000;
    internal const uint EventTraceFlagDiskIo = 0x00000100, EventTraceFlagFileIoInit = 0x04000000;
    internal const uint EventTraceControlStop = 1;
    internal const uint ProcessTraceModeRealTime = 0x00000100, ProcessTraceModeEventRecord = 0x10000000;
    internal const ushort EventHeaderFlag32BitHeader = 0x0020, EventHeaderFlag64BitHeader = 0x0040;
    internal const int ErrorAccessDenied = 5, ErrorAlreadyExists = 183, ErrorMoreData = 234, ErrorNoSystemResources = 1450, ErrorWmiInstanceNotFound = 4201;
    internal const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    /// <summary>EVENT_TRACE_PROPERTIES is 120 bytes; the session name follows it in the same buffer.</summary>
    internal const int PropertiesSize = 120;

    /// <summary>The event class of disk I/O events (DiskIo).</summary>
    internal static readonly Guid DiskIoGuid = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");

    /// <summary>The event class of file system requests (FileIo).</summary>
    internal static readonly Guid FileIoGuid = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int StartTrace(out ulong sessionHandle, string sessionName, byte* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ControlTrace(ulong sessionHandle, string? sessionName, byte* properties, uint controlCode);

    /// <summary>Lists the running sessions into the given EVENT_TRACE_PROPERTIES buffers (read only).</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "QueryAllTracesW")]
    internal static partial int QueryAllTraces(byte** propertyArray, uint propertyArrayCount, out uint loggerCount);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static partial ulong OpenTrace(EVENT_TRACE_LOGFILEW* logfile);

    [LibraryImport("advapi32.dll")]
    internal static partial int ProcessTrace(ulong* handles, uint count, IntPtr startTime, IntPtr endTime);

    [LibraryImport("advapi32.dll")]
    internal static partial int CloseTrace(ulong handle);

    /// <summary>EVENT_TRACE_LOGFILEW: 448 bytes (CurrentEvent 88 bytes at 32, LogfileHeader 280 bytes at 120).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_LOGFILEW
    {
        public char* LogFileName;
        public char* LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public fixed byte CurrentEvent[88];
        public fixed byte LogfileHeader[280];
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    // EVENT_RECORD (112 bytes): EVENT_HEADER at 0 (Flags 4, ProcessId 12, ProviderId 24, EventDescriptor.Opcode 45),
    // UserDataLength 86, UserData 96, UserContext 104.
    internal const int RecordFlags = 4, RecordProcessId = 12, RecordProviderId = 24, RecordOpcode = 45, RecordUserDataLength = 86, RecordUserData = 96, RecordUserContext = 104;
}
