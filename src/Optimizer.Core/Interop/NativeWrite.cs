using System.Runtime.InteropServices;

namespace Optimizer.Core.Interop;

/// <summary>
/// P/Invoke declarations that change system state. Only called from the action layer, which backs up
/// every value before it writes.
/// </summary>
internal static partial class NativeWrite
{
    // ---------- kernel32: End task (Live readings) ----------
    internal const uint ProcessTerminate = 0x0001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ---------- advapi32: services ----------
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryConfig = 0x0001, ServiceChangeConfig = 0x0002, ServiceQueryStatus = 0x0004;
    internal const uint ServiceStopped = 1, ServiceRunning = 4;
    internal const uint ServiceNoChange = 0xFFFFFFFF;
    internal const uint ServiceConfigDelayedAutoStartInfo = 3;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName,
        string? password, string? displayName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_DELAYED_AUTO_START_INFO { public int fDelayedAutostart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    internal static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    // ---------- powrprof ----------
    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerDuplicateScheme(IntPtr rootPowerKey, in Guid sourceSchemeGuid, ref IntPtr destinationSchemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerDeleteScheme(IntPtr rootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteFriendlyName(IntPtr rootPowerKey, in Guid schemeGuid, IntPtr subGroup, IntPtr setting, byte[] buffer, uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index, [Out] byte[] buffer, ref uint bufferSize);

    internal const uint AccessScheme = 16;

    // ---------- user32 ----------
    internal const uint CdsUpdateRegistry = 0x00000001;
    internal const int DispChangeSuccessful = 0, DispChangeRestart = 1;
    internal const uint DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ChangeDisplaySettingsEx(string deviceName, ref Native.DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    internal const uint SpiSetMouse = 0x0004;

    /// <summary>Live mouse values for the current session; called without SPIF_UPDATEINIFILE.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint action, uint param, int[] pvParam, uint winIni);

    // Keyboard accessibility features: STICKYKEYS and TOGGLEKEYS are {cbSize, dwFlags}; FILTERKEYS has four more DWORDs.
    internal const uint SpiGetFilterKeys = 0x0032, SpiSetFilterKeys = 0x0033, SpiGetToggleKeys = 0x0034, SpiSetToggleKeys = 0x0035;
    internal const uint SpiGetStickyKeys = 0x003A, SpiSetStickyKeys = 0x003B;
    internal const uint SpifUpdateIniFile = 0x01, SpifSendChange = 0x02;

    /// <summary>For the accessibility structures, passed as a DWORD array (cbSize first).</summary>
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    internal static extern bool SystemParametersInfoDwords(uint action, uint param, [In, Out] uint[] pvParam, uint winIni);

    internal const uint HwndBroadcast = 0xFFFF, WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    // ---------- cfgmgr32: device restart ----------
    internal const uint CmDisableUiNotOk = 0x00000004; // fail instead of prompting when the device is in use

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Enable_DevNode(uint devInst, uint flags);

    // ---------- advapi32: DWORD values in a loaded offline hive (install media) ----------
    // Opened and created with REG_OPTION_BACKUP_RESTORE: with SeBackupPrivilege and SeRestorePrivilege enabled the access
    // check is skipped, so keys of the image that only TrustedInstaller may write are written without changing their ACL.
    internal const uint RegOptionBackupRestore = 0x4, RegDword = 4, KeySetValue = 0x2, KeyQueryValue = 0x1;

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegCreateKeyEx(IntPtr key, string subKey, int reserved, string? keyClass, uint options, uint samDesired,
        IntPtr securityAttributes, out IntPtr result, out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(IntPtr key, string subKey, uint options, uint samDesired, out IntPtr result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegSetValueEx(IntPtr key, string valueName, int reserved, uint type, ref int data, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegQueryValueEx(IntPtr key, string valueName, IntPtr reserved, out uint type, out int data, ref uint size);

    [LibraryImport("advapi32.dll")]
    internal static partial int RegCloseKey(IntPtr key);

    // ---------- virtdisk: an ISO attached read-only, without a drive letter, for as long as its handle is open ----------
    // A temporary change: IsoReader attaches to read the ISO and detaches in a finally (no permanent lifetime).
    internal const uint VirtualStorageTypeDeviceIso = 1;
    internal static readonly Guid VirtualStorageTypeVendorMicrosoft = new("ec984aec-a0f9-47e9-901f-71415a66345b");
    internal const uint VirtualDiskAccessRead = 0x000d0000;    // ATTACH_RO | DETACH | GET_INFO
    internal const uint AttachVirtualDiskFlagReadOnly = 0x1, AttachVirtualDiskFlagNoDriveLetter = 0x2;
    internal const int AttachVirtualDiskVersion1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct VIRTUAL_STORAGE_TYPE { public uint DeviceId; public Guid VendorId; }

    /// <summary>The union after Version holds ULONGLONGs (Version2), so it starts at 8 and the structure is 24 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        [FieldOffset(0)] public int Version;
        [FieldOffset(8)] public uint Version1Reserved;
    }

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint OpenVirtualDisk(in VIRTUAL_STORAGE_TYPE storageType, string path, uint accessMask, uint flags, IntPtr parameters,
        out Microsoft.Win32.SafeHandles.SafeFileHandle handle);

    [LibraryImport("virtdisk.dll")]
    internal static partial uint AttachVirtualDisk(Microsoft.Win32.SafeHandles.SafeFileHandle handle, IntPtr securityDescriptor, uint flags,
        uint providerSpecificFlags, in ATTACH_VIRTUAL_DISK_PARAMETERS parameters, IntPtr overlapped);

    [LibraryImport("virtdisk.dll")]
    internal static partial uint DetachVirtualDisk(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint flags, uint providerSpecificFlags);

    // ---------- kernel32 / advapi32: moving a finished file by its handle (install media output) ----------

    /// <summary>Sets parts of an object's security descriptor exactly as given, through its handle (no inheritance computed from a path).</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint securityInformation, byte[] descriptor);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    /// <summary>
    /// NtSetInformationFile (ZwSetInformationFile in the WDK docs) with a caller-built buffer. Used for
    /// FileRenameInformationEx with a RootDirectory handle: kernel32's SetFileInformationByHandle answers
    /// ERROR_INVALID_PARAMETER for a name relative to a RootDirectory (checked 2026-10-20). Returns the NTSTATUS.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetInformationFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, out IO_STATUS_BLOCK ioStatus, IntPtr info,
        uint length, int infoClass);

    [LibraryImport("ntdll.dll")]
    internal static partial uint RtlNtStatusToDosError(int status);
}
