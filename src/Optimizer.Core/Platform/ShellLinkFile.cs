using System.Runtime.InteropServices;
using System.Text;

namespace Optimizer.Core.Platform;

/// <summary>
/// .lnk files through the shell's own IShellLinkW, early-bound. Reads the same target and arguments as WScript.Shell's
/// CreateShortcut (all 245 shortcuts of a test PC, 2026-10-13) without the late-bound calls, which took five times as
/// long and compiled a binder on first use.
/// </summary>
internal static class ShellLinkFile
{
    /// <summary>
    /// Program and arguments of a shortcut; the target is empty for a link to a non-file item. The target comes back as
    /// stored, its %variables% not expanded (SLGP_RAWPATH): the shell would expand them with this process's environment,
    /// which under a separate administrator account is not the signed-in user's. The caller expands it
    /// (<see cref="PathExpander"/>).
    /// </summary>
    public static (string Target, string Arguments) Read(string lnk)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
            var buffer = new StringBuilder(32768);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, RawPath);
            var target = buffer.ToString();
            buffer.Clear();
            link.GetArguments(buffer, buffer.Capacity);
            return (target, buffer.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>A new shortcut to <paramref name="exe"/>, saved to <paramref name="file"/>.</summary>
    public static void Write(string file, string exe, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(file, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>SLGP_RAWPATH: the target as stored, %variables% and all.</summary>
    private const uint RawPath = 0x4;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
