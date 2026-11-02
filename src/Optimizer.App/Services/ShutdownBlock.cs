using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Optimizer.App.Services;

/// <summary>
/// Holds a sign-out or shutdown while a command line run (CommandLineVerbs) changes the PC. Such a run has no visible
/// window, and Windows ends an app without one when it refuses the end of the session, unless it named a reason with
/// ShutdownBlockReasonCreate. This is a hidden top-level window with that reason that refuses the query while it exists;
/// Windows then lists Tunero with the reason among the apps it waits for. Create and dispose it on the UI thread: the
/// reason belongs to the thread that created the window.
/// </summary>
internal sealed class ShutdownBlock : IDisposable
{
    private const int QueryEndSession = 0x0011; // WM_QUERYENDSESSION

    private readonly HwndSource _window;
    private readonly bool _reasonSet;

    public ShutdownBlock(string reason)
    {
        // No WS_VISIBLE (the default parameters include it) and no parent: a hidden top-level window, which receives the
        // query (a message-only window would not).
        _window = new HwndSource(new HwndSourceParameters("Tunero") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(Refuse);
        _reasonSet = ShutdownBlockReasonCreate(_window.Handle, reason);
        if (!_reasonSet) Optimizer.Core.Logging.Log.Warn("app", $"no shutdown reason set: error {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>The hidden window, for the tests.</summary>
    internal IntPtr Handle => _window.Handle;

    public void Dispose()
    {
        if (_reasonSet) ShutdownBlockReasonDestroy(_window.Handle);
        _window.RemoveHook(Refuse);
        _window.Dispose();
    }

    // FALSE to the query: Windows waits and shows the reason. The window lives only as long as the run.
    private static IntPtr Refuse(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != QueryEndSession) return IntPtr.Zero;
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
