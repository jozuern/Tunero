using System.Runtime.InteropServices;

namespace Optimizer.Core.Interop;

/// <summary>P/Invoke declarations. Everything here only reads system state; changes go through NativeWrite.</summary>
internal static partial class Native
{
    // ---------- kernel32 ----------
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFirmwareType(out int firmwareType);

    internal const int FirmwareTypeBios = 1, FirmwareTypeUefi = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    internal const ushort ImageFileMachineAmd64 = 0x8664, ImageFileMachineArm64 = 0xAA64;

    [LibraryImport("kernel32.dll", EntryPoint = "GetGeoInfoW", SetLastError = true)]
    internal static unsafe partial int GetGeoInfo(int location, int geoType, char* data, int dataCount, ushort langId);

    internal const int GeoIso2 = 4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    internal const int RelationProcessorCore = 0, RelationCache = 2, RelationAll = 0xFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ---------- powrprof ----------
    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadFriendlyName(IntPtr rootPowerKey, in Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, [Out] byte[]? buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadACValue(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint type, [Out] byte[]? buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, [Out] byte[] outputBuffer, uint outputBufferLength);

    internal const int SystemPowerCapabilities = 4;

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr LocalFree(IntPtr mem);

    // ---------- wtsapi32 / netapi32 ----------
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(IntPtr memory);

    internal const int WtsCurrentSession = -1, WtsUserName = 5, WtsDomainName = 7;

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int bufferType);

    [LibraryImport("netapi32.dll")]
    internal static partial int NetApiBufferFree(IntPtr buffer);

    internal const int NetSetupDomainName = 3;

    // ---------- user32: display configuration (CCD API) ----------
    internal const uint QdcOnlyActivePaths = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; public long Value => ((long)HighPart << 32) | LowPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO (64 bytes). Union members read through explicit offsets.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;          // 1 = source, 2 = target, 3 = desktop image
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        // source mode
        [FieldOffset(16)] public uint sourceWidth;
        [FieldOffset(20)] public uint sourceHeight;
        [FieldOffset(28)] public int sourcePositionX;  // DISPLAYCONFIG_SOURCE_MODE.position (POINTL), desktop coordinates
        [FieldOffset(32)] public int sourcePositionY;
        // target mode (DISPLAYCONFIG_VIDEO_SIGNAL_INFO)
        [FieldOffset(16)] public ulong pixelRate;
        [FieldOffset(32)] public DISPLAYCONFIG_RATIONAL vSyncFreq;
        [FieldOffset(40)] public uint activeWidth;
        [FieldOffset(44)] public uint activeHeight;
    }

    internal const uint ModeInfoTypeSource = 1, ModeInfoTypeTarget = 2;

    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    internal static partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    internal const uint DeviceInfoGetSourceName = 1, DeviceInfoGetTargetName = 2, DeviceInfoGetAdapterName = 4, DeviceInfoGetAdvancedColorInfo = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [DllImport("user32.dll")] internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
    [DllImport("user32.dll")] internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
    [DllImport("user32.dll")] internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);
    [DllImport("user32.dll")] internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    internal const uint OutputTechnologyInternal = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    internal const uint DmInterlaced = 0x2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);

    // ---------- cfgmgr32 ----------
    internal const uint CrSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey, out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_First_Log_Conf(out nuint logConf, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_Next_Res_Des(out nuint resDes, nuint current, uint forResource, out uint resourceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_Res_Des_Data_Size(out uint size, nuint resDes, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_Res_Des_Data(nuint resDes, byte[] buffer, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Free_Res_Des_Handle(nuint resDes);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Free_Log_Conf_Handle(nuint logConf);

    internal const uint AllocLogConf = 2, ResTypeAll = 0, ResTypeMem = 1, ResTypeMemLarge = 7;

    // ---------- processes (Live readings: programs per device) ----------
    internal const int SystemProcessInformation = 5;
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(IntPtr process, int flags, char* name, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll")]
    internal static partial int GetCurrentThreadId();

    // ---------- pdh (GPU engine counters) ----------
    internal const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    internal const uint PdhMoreData = 0x800007D2, PdhCstatusValidData = 0, PdhCstatusNewData = 1;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    internal static partial uint PdhCollectQueryData(IntPtr query);

    /// <summary>Items are PDH_FMT_COUNTERVALUE_ITEM_W (64 bit: name pointer 0, CStatus 8, double 16; 24 bytes).</summary>
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [LibraryImport("pdh.dll")]
    internal static partial uint PdhCloseQuery(IntPtr query);

    // ---------- dxgi (graphics adapters and their LUIDs) ----------
    // ---------- advapi32: which services run in which process ----------
    internal const uint ScManagerEnumerateService = 0x0004, ScEnumProcessInfo = 0, ServiceWin32 = 0x30, ServiceActive = 1;

    /// <summary>Items are ENUM_SERVICE_STATUS_PROCESSW (64 bit: ServiceName 0, DisplayName 8, ProcessId 44; 56 bytes).</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumServicesStatusEx(IntPtr scManager, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, IntPtr groupName);

    [LibraryImport("dxgi.dll")]
    internal static partial int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    internal static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    // ---------- shlwapi: a file as an IStream (IMAPI2 boot images) ----------
    internal const uint StgmReadShareDenyWrite = 0x00000020;   // STGM_READ | STGM_SHARE_DENY_WRITE

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create,
        IntPtr template, out System.Runtime.InteropServices.ComTypes.IStream stream);

    // ---------- virtdisk: the device path of an ISO attached through NativeWrite.AttachVirtualDisk ----------
    /// <summary>The attached disk's device path, such as \\.\CDROM1. <paramref name="sizeInBytes"/> is the buffer size in bytes.</summary>
    [LibraryImport("virtdisk.dll")]
    internal static unsafe partial uint GetVirtualDiskPhysicalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle, ref uint sizeInBytes, char* path);

    // ---------- kernel32: volumes and their device numbers ----------
    internal const uint FileShareReadWrite = 0x3, OpenExisting = 3;
    internal const uint IoctlStorageGetDeviceNumber = 0x002D1080;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_NUMBER { public uint DeviceType; public uint DeviceNumber; public uint PartitionNumber; }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    internal static unsafe partial IntPtr FindFirstVolume(char* volumeName, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FindNextVolume(IntPtr find, char* volumeName, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(IntPtr find);

    /// <summary>The requested parts of an object's security descriptor (self-relative), read through its handle (needs READ_CONTROL).</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint requestedInformation,
        byte[]? descriptor, uint length, out uint needed);

    /// <summary>CreateFileW; with access 0 it opens a device only to ask it questions (no read or write access).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        out STORAGE_DEVICE_NUMBER outBuffer, uint outSize, out uint returned, IntPtr overlapped);
}
