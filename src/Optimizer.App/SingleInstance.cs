using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Optimizer.Core.Logging;

namespace Optimizer.App;

/// <summary>
/// One window per Windows session: a plain start while Tunero is open brings that window to the front instead of opening
/// a second one that shows and changes the same settings. Starts with switches (developer switches, command line runs,
/// the reapply task) neither take the lock nor wait for it.
/// </summary>
/// <remarks>
/// The copy that holds the lock says who it is in a small named memory block next to it (<see cref="InstanceRecord"/>): its
/// process id and, once it is shown, its window. A second start reads that instead of looking for processes by name, so a
/// copy saved as "Tunero (1).exe" or renamed finds the open window too. Both names are per session (Local\) and live only
/// while a Tunero process holds them: nothing stays behind and nothing runs in the background.
/// </remarks>
public static class SingleInstance
{
    /// <summary>Passed by the self-update to the new exe: it waits until the closing copy lets go of the lock.</summary>
    public const string AfterUpdate = "--after-update";

    /// <summary>How long the new exe waits for the old one to close after an update.</summary>
    public static readonly TimeSpan UpdateWait = TimeSpan.FromSeconds(30);

    /// <summary>How long a second start waits for the open copy's window (two quick starts: it is still starting).</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(10);

    // Local\: per session, so another account signed in on the same PC keeps its own window. The same name as in the
    // versions before the record, so an open copy of those still keeps a new one from opening a second window.
    private const string Name = @"Local\Tunero.SingleInstance.6c2f9a41";

    /// <summary>The holder's <see cref="InstanceRecord"/>.</summary>
    private const string RecordName = Name + ".Window";

    /// <summary>
    /// A start takes part when it has no arguments, or only <see cref="AfterUpdate"/>, or only the setup's
    /// <see cref="Services.InstallService.InstalledFrom"/> and its path (with the first sign-in's profile after it, or that
    /// profile alone: <see cref="Services.FirstSignInStart"/>). --first-sign-in itself never takes the lock, so the copy it
    /// starts is not refused.
    /// </summary>
    public static bool Applies(IReadOnlyList<string> args) => args.Count == 0 || WaitsForOther(args) || IsUninstall(args);

    /// <summary>Uninstall from Windows' Installed apps: never beside an open window, which would keep running after it.</summary>
    public static bool IsUninstall(IReadOnlyList<string> args) => args.Count == 1 && args[0].Equals(Uninstall, StringComparison.OrdinalIgnoreCase);

    public const string Uninstall = "--uninstall";

    /// <summary>The start comes from a closing copy (an update, the setup, the first sign-in): it waits for that copy to let go of the lock.</summary>
    public static bool WaitsForOther(IReadOnlyList<string> args) =>
        (args.Count == 1 && args[0].Equals(AfterUpdate, StringComparison.OrdinalIgnoreCase)) ||
        (args.Count == 2 && (args[0].Equals(Services.InstallService.InstalledFrom, StringComparison.OrdinalIgnoreCase) || IsFirstSignInApply(args, 0))) ||
        (args.Count == 4 && args[0].Equals(Services.InstallService.InstalledFrom, StringComparison.OrdinalIgnoreCase) && IsFirstSignInApply(args, 2));

    private static bool IsFirstSignInApply(IReadOnlyList<string> args, int at) => args[at].Equals(Services.FirstSignInStart.ApplySwitch, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the lock for this process's lifetime (dispose it when the app ends). Null when an open Tunero holds it. A lock
    /// that cannot be opened (another program created the name and denies access) does not keep Tunero from starting.
    /// </summary>
    /// <param name="window">This run opens a window (<see cref="InstanceLock.ShowsWindow"/> follows); false for a run without
    /// one (--build-media's cleanup), beside which a plain start opens its window.</param>
    public static InstanceLock? TryAcquire(TimeSpan wait, out bool heldByOther, bool window = true) =>
        TryAcquire(Name, RecordName, wait, out heldByOther, window);

    /// <param name="name">The lock's name (tests: one of their own).</param>
    /// <param name="recordName">The record's name next to it.</param>
    internal static InstanceLock? TryAcquire(string name, string recordName, TimeSpan wait, out bool heldByOther, bool window)
    {
        heldByOther = false;
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            Log.Error("app", "the single-instance lock cannot be opened; starting without it", ex);
            return null;
        }
        try
        {
            if (mutex.WaitOne(wait)) return new InstanceLock(mutex, recordName, window);
        }
        catch (AbandonedMutexException)
        {
            // The previous copy ended without letting go (killed or crashed): the lock is this process's now.
            return new InstanceLock(mutex, recordName, window);
        }
        mutex.Dispose();
        heldByOther = true;
        return null;
    }

    /// <summary>
    /// Brings the open Tunero window of this session to the front, restored if it was minimized. Waits a few seconds for
    /// a copy that is still starting. False when the lock's holder is no Tunero window of this session (a run without a
    /// window, or another program that took the name): then this start goes on.
    /// </summary>
    public static bool ShowOther() => ShowOther(RecordName, Environment.ProcessId);

    /// <param name="recordName">The record's name (tests: one of their own).</param>
    /// <param name="self">This process's id: a record of its own is not another copy.</param>
    internal static bool ShowOther(string recordName, int self)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var record = InstanceRecord.Read(recordName);
            var other = Decide(record, self, ProcessRuns, WindowOwner);
            switch (other)
            {
                case OtherCopy.Show:
                    BringToFront(record!.Window);
                    return true;
                case OtherCopy.StartHere:
                    return false;
                case OtherCopy.FromBefore:
                    return ShowByProcessName();
            }
            // Still starting (no window yet: it opens one shortly), or the record is the previous holder's for a moment
            // (an update hands over the lock, and the new holder writes its own right after taking it).
            if (deadline.Elapsed > WindowWait) return other == OtherCopy.NoWindowYet;
            Thread.Sleep(200);
        }
    }

    /// <summary>What a start that found the lock taken does with the holder's record.</summary>
    internal enum OtherCopy
    {
        /// <summary>The record names a window of the running holder: bring it to the front and end.</summary>
        Show,

        /// <summary>The holder runs and its window is still coming: wait for it (and end after the wait in any case).</summary>
        NoWindowYet,

        /// <summary>The record names no running process: wait for the new holder's record, then start here.</summary>
        Stale,

        /// <summary>The holder opens no window (--build-media's cleanup), or the record is this process's own: start here.</summary>
        StartHere,

        /// <summary>No record: a Tunero from before the record holds the lock (looked for by its process name, as it did).</summary>
        FromBefore,
    }

    /// <param name="processRuns">The process with this id runs.</param>
    /// <param name="windowOwner">The process id a window belongs to; 0 when it is no window (any more).</param>
    internal static OtherCopy Decide(InstanceRecord? record, int self, Func<int, bool> processRuns, Func<IntPtr, int> windowOwner)
    {
        if (record is null) return OtherCopy.FromBefore;
        if (record.ProcessId == self || !record.OpensWindow) return OtherCopy.StartHere;
        if (record.ProcessId <= 0 || !processRuns(record.ProcessId)) return OtherCopy.Stale;
        if (record.Window == IntPtr.Zero) return OtherCopy.NoWindowYet;
        // A window handle is reused after its window closed: it counts only while it still belongs to the holder.
        return windowOwner(record.Window) == record.ProcessId ? OtherCopy.Show : OtherCopy.NoWindowYet;
    }

    /// <summary>
    /// Versions before the record: another process of this exe's name in this session, with its main window. Kept for a
    /// new copy started while an older one is open (the update itself closes the older one first).
    /// </summary>
    private static bool ShowByProcessName()
    {
        using var self = Process.GetCurrentProcess();
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var others = 0;
            foreach (var process in Process.GetProcessesByName(self.ProcessName))
            {
                using (process)
                {
                    if (process.Id == self.Id || process.SessionId != self.SessionId) continue;
                    others++;
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    BringToFront(window);
                    return true;
                }
            }
            if (others == 0) return false;
            // Another copy runs but has no window yet (two quick starts): it opens one shortly.
            if (deadline.Elapsed > WindowWait) return true;
            Thread.Sleep(200);
        }
    }

    private static void BringToFront(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9 /* SW_RESTORE */);
        SetForegroundWindow(window);
    }

    private static bool ProcessRuns(int processId)
    {
        // Limited query rights are granted across integrity levels, so an elevated holder is seen from a start without rights too.
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return GetExitCodeProcess(handle, out var code) && code == 259 /* STILL_ACTIVE */;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int WindowOwner(IntPtr window) => GetWindowThreadProcessId(window, out var processId) == 0 ? 0 : (int)processId;

    /// <summary>
    /// Lets the restarted process (protected runtime folder) take the foreground, which this process got from the shell,
    /// so it can bring the open window to the front.
    /// </summary>
    public static void PassForeground(int processId) => AllowSetForegroundWindow(processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Who holds the single-instance lock: its process id, whether it opens a window, and that window once it is shown. Written
/// only by the holder, into a named memory block of 24 bytes that lives as long as a process has it open.
/// </summary>
public sealed record InstanceRecord(int ProcessId, bool OpensWindow, IntPtr Window)
{
    internal const int Size = 24;

    // "TNR1": a block someone else created under the name, or one not written yet, reads as no record.
    private const int Magic = 0x31524E54;

    internal static void Write(MemoryMappedViewAccessor view, int processId, bool opensWindow)
    {
        view.Write(4, processId);
        view.Write(8, opensWindow ? 1 : 0);
        view.Write(16, 0L);
        view.Write(0, Magic);
    }

    internal static void WriteWindow(MemoryMappedViewAccessor view, IntPtr window) => view.Write(16, window.ToInt64());

    internal static InstanceRecord? Read(MemoryMappedViewAccessor view) =>
        view.Capacity < Size || view.ReadInt32(0) != Magic ? null
            : new InstanceRecord(view.ReadInt32(4), view.ReadInt32(8) != 0, new IntPtr(view.ReadInt64(16)));

    /// <summary>The record under <paramref name="name"/>; null when there is none (or it cannot be read).</summary>
    internal static InstanceRecord? Read(string name)
    {
        try
        {
            using var block = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = block.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
            return Read(view);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The single-instance lock of this process and its record (<see cref="SingleInstance.TryAcquire(TimeSpan, out bool, bool)"/>).</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly MemoryMappedFile? _block;
    private readonly MemoryMappedViewAccessor? _view;

    internal InstanceLock(Mutex mutex, string recordName, bool opensWindow)
    {
        _mutex = mutex;
        try
        {
            // An existing block (the previous holder is still closing, after an update) is written over: the lock is this
            // process's now.
            _block = MemoryMappedFile.CreateOrOpen(recordName, InstanceRecord.Size);
            _view = _block.CreateViewAccessor(0, InstanceRecord.Size);
            InstanceRecord.Write(_view, Environment.ProcessId, opensWindow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without the record a second start looks for the window by process name, as before.
            Log.Error("app", "the single-instance record cannot be written", ex);
            _view?.Dispose();
            _block?.Dispose();
            _view = null;
            _block = null;
        }
    }

    /// <summary>The window is shown: a second start brings it to the front.</summary>
    public void ShowsWindow(IntPtr window)
    {
        if (_view is not null) InstanceRecord.WriteWindow(_view, window);
    }

    /// <summary>Lets go of the lock (on the thread that took it). The record stays until <see cref="Dispose"/>; the next holder writes over it.</summary>
    public void Release() => _mutex.ReleaseMutex();

    public void Dispose()
    {
        _view?.Dispose();
        _block?.Dispose();
        _mutex.Dispose();
    }
}
