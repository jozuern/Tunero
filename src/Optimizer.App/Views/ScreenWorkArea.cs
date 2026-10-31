using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Optimizer.App.Views;

/// <summary>
/// The work area (the screen without the taskbar) of the monitor a window is on. <see cref="SystemParameters.WorkArea"/>
/// is the primary monitor's: on a second monitor below or beside it, a window fitted to that moved onto the primary one
/// or was cut to its height.
/// </summary>
internal static class ScreenWorkArea
{
    /// <summary>
    /// In device-independent pixels, as the window's Top and Height are; the primary monitor's while the window has no
    /// handle yet or Windows does not answer.
    /// </summary>
    public static Rect Of(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target) return SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var toDips = target.TransformFromDevice;
        var area = new Rect(toDips.Transform(new Point(info.Work.Left, info.Work.Top)), toDips.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        return area.Width > 0 && area.Height > 0 ? area : SystemParameters.WorkArea;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Bounds Monitor;
        public Bounds Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
