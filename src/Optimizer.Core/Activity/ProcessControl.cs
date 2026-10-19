using System.Runtime.InteropServices;
using Optimizer.Core.Interop;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Activity;

/// <summary>End task for the Live readings program list. Asking first and the refusal rules are the caller's (ProgramRules).</summary>
public static class ProcessControl
{
    private const int ErrorInvalidParameter = 87, ErrorAccessDenied = 5;

    /// <summary>The Win32 error End returns for a process that is already gone (or whose PID a new process took).</summary>
    public const int Gone = ErrorInvalidParameter;

    /// <summary>
    /// False when Windows refuses to let programs end the process (protected processes such as the Defender engine).
    /// Opens a handle only; nothing is ended. A process that is gone counts as endable (End then reports it gone).
    /// </summary>
    public static bool CanEnd(int pid)
    {
        var handle = Native.OpenProcess(NativeWrite.ProcessTerminate, false, pid);
        if (handle != IntPtr.Zero)
        {
            Native.CloseHandle(handle);
            return true;
        }
        return Marshal.GetLastPInvokeError() != ErrorAccessDenied;
    }

    /// <summary>
    /// Ends the process only if it is still the one the list saw: a PID that a new process took over meanwhile has a
    /// different create time and is left alone (ERROR_INVALID_PARAMETER, as for a process that is gone).
    /// </summary>
    public static (bool Ok, int Error) End(int pid, long createTime)
    {
        var handle = Native.OpenProcess(NativeWrite.ProcessTerminate | Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return (false, Marshal.GetLastPInvokeError());
        try
        {
            if (!NativeWrite.GetProcessTimes(handle, out var created, out _, out _, out _) || created != createTime)
                return (false, ErrorInvalidParameter);
            if (!NativeWrite.TerminateProcess(handle, 1)) return (false, Marshal.GetLastPInvokeError());
            Log.Info("activity", "process ended", new { pid });
            return (true, 0);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
