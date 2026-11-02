using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Optimizer.App.Services;

namespace Optimizer.App.Tests;

/// <summary>
/// What keeps a command line run alive at sign-out (<see cref="ShutdownBlock"/>): a hidden top-level window (Windows sends
/// the query only to top-level windows, and a visible one would show up on the desktop), which refuses the query and
/// names the reason while it exists. The window belongs to the test process and is gone after the test; that Windows
/// then waits for the run is checked in the VM.
/// </summary>
public sealed class ShutdownBlockTests
{
    private const int QueryEndSession = 0x0011; // WM_QUERYENDSESSION
    private const uint ParentAncestor = 1; // GA_PARENT

    [Fact]
    public void AHiddenTopLevelWindowRefusesTheQueryWithTheReasonUntilDisposed()
    {
        const string Reason = "Tunero is still working.";
        Exception? error = null;
        var sta = new Thread(() =>
        {
            try
            {
                IntPtr handle;
                using (var block = new ShutdownBlock(Reason))
                {
                    handle = block.Handle;
                    Assert.NotEqual(IntPtr.Zero, handle);
                    Assert.True(IsWindow(handle));
                    Assert.False(IsWindowVisible(handle));
                    // Top-level: no parent or owner, and not a message-only window (whose parent is the message window).
                    Assert.Equal(IntPtr.Zero, GetParent(handle));
                    Assert.Equal(GetDesktopWindow(), GetAncestor(handle, ParentAncestor));
                    Assert.Equal(IntPtr.Zero, SendMessage(handle, QueryEndSession, IntPtr.Zero, IntPtr.Zero));

                    var buffer = new char[256];
                    var size = (uint)buffer.Length;
                    Assert.True(ShutdownBlockReasonQuery(handle, buffer, ref size), $"no reason: error {Marshal.GetLastPInvokeError()}");
                    Assert.Equal(Reason, new string(buffer).TrimEnd('\0'));
                }
                Assert.False(IsWindow(handle));
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        // The assertion that failed on the window's thread, with its own message
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonQuery(IntPtr window, [Out] char[] buffer, ref uint size);
}
