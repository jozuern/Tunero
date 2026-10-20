using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Optimizer.Core.Logging;

namespace Optimizer.App;

/// <summary>
/// One window per Windows session: a plain start while Tunero is open brings that window to the front instead of opening
/// a second one that shows and changes the same settings. Starts with switches (developer switches, command line runs,
/// the reapply task) neither take the lock nor wait for it.
/// </summary>
public static class SingleInstance
{
    /// <summary>Passed by the self-update to the new exe: it waits until the closing copy lets go of the lock.</summary>
    public const string AfterUpdate = "--after-update";

    /// <summary>How long the new exe waits for the old one to close after an update.</summary>
    public static readonly TimeSpan UpdateWait = TimeSpan.FromSeconds(30);

    // Local\: per session, so another account signed in on the same PC keeps its own window.
    private const string Name = @"Local\Tunero.SingleInstance.6c2f9a41";

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
    public static Mutex? TryAcquire(TimeSpan wait, out bool heldByOther)
    {
        heldByOther = false;
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, Name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            Log.Error("app", "the single-instance lock cannot be opened; starting without it", ex);
            return null;
        }
        try
        {
            if (mutex.WaitOne(wait)) return mutex;
        }
        catch (AbandonedMutexException)
        {
            // The previous copy ended without letting go (killed or crashed): the lock is this process's now.
            return mutex;
        }
        mutex.Dispose();
        heldByOther = true;
        return null;
    }

    /// <summary>
    /// Brings the open Tunero window of this session to the front, restored if it was minimized. Waits a few seconds for
    /// a copy that is still starting. False when no other Tunero process runs in this session: then the lock's name was
    /// taken by another program, and this start goes on.
    /// </summary>
    public static bool ShowOther()
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
                    if (IsIconic(window)) ShowWindow(window, 9 /* SW_RESTORE */);
                    SetForegroundWindow(window);
                    return true;
                }
            }
            if (others == 0) return false;
            // Another copy runs but has no window yet (two quick starts): it opens one shortly.
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) return true;
            Thread.Sleep(200);
        }
    }

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
}
