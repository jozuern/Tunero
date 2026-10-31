using System.Runtime.InteropServices;
using System.Text;
using Optimizer.Core.Catalog;
using Optimizer.Core.Interop;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Hardware.Probes;

/// <summary>
/// Active displays via the CCD API: exact rational refresh rates, target -> monitor device path -> EDID,
/// adapter per target. The registry keeps EDIDs of every monitor ever connected, so only mapped EDIDs are used.
/// </summary>
public static class DisplayProbe
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The name of a display that reports none (often a laptop panel); the System info page writes its own then.</summary>
    public const string UnnamedDisplay = "Display";

    public static List<DisplayInfo> Read(CatalogData catalog)
    {
        var result = new List<DisplayInfo>();
        // A display that is added or removed between the two calls (or the refresh-rate change a tweak just made) makes
        // the second one report ERROR_INSUFFICIENT_BUFFER: the sizes are read again, as the API documentation advises.
        uint pathCount = 0, modeCount = 0;
        Native.DISPLAYCONFIG_PATH_INFO[] paths = [];
        Native.DISPLAYCONFIG_MODE_INFO[] modes = [];
        for (var attempt = 0; ; attempt++)
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QdcOnlyActivePaths, out pathCount, out modeCount) != 0) return result;
            paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
            var error = Native.QueryDisplayConfig(Native.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == 0) break;
            if (error != ErrorInsufficientBuffer || attempt == 2) return result;
        }

        foreach (var path in paths.Take((int)pathCount))
        {
            var src = path.sourceInfo;
            var tgt = path.targetInfo;

            var sourceName = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = Header(Native.DeviceInfoGetSourceName, Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), src.adapterId, src.id),
            };
            Native.DisplayConfigGetDeviceInfo(ref sourceName);

            var targetName = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = Header(Native.DeviceInfoGetTargetName, Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), tgt.adapterId, tgt.id),
            };
            Native.DisplayConfigGetDeviceInfo(ref targetName);

            var adapterName = new Native.DISPLAYCONFIG_ADAPTER_NAME
            {
                header = Header(Native.DeviceInfoGetAdapterName, Marshal.SizeOf<Native.DISPLAYCONFIG_ADAPTER_NAME>(), tgt.adapterId, 0),
            };
            Native.DisplayConfigGetDeviceInfo(ref adapterName);

            var (hdrSupported, hdrEnabled) = ReadHdr(tgt.adapterId, tgt.id);

            int width = 0, height = 0;
            var primary = false;
            if (src.modeInfoIdx < modeCount && modes[src.modeInfoIdx].infoType == Native.ModeInfoTypeSource)
            {
                width = (int)modes[src.modeInfoIdx].sourceWidth;
                height = (int)modes[src.modeInfoIdx].sourceHeight;
                // The primary display's desktop starts at (0,0) (DISPLAYCONFIG_SOURCE_MODE.position).
                primary = modes[src.modeInfoIdx].sourcePositionX == 0 && modes[src.modeInfoIdx].sourcePositionY == 0;
            }

            var gdi = sourceName.viewGdiDeviceName ?? "";
            var modeList = EnumModes(gdi);
            var maxAtRes = modeList.Where(m => m.Width == width && m.Height == height).Select(m => m.RefreshHz).DefaultIfEmpty(0).Max();

            var adapterPath = adapterName.adapterDevicePath ?? "";
            var vendor = GpuProbe.VendorFromPnp(adapterPath, catalog);
            var edid = ReadEdid(targetName.monitorDevicePath);

            result.Add(new DisplayInfo(
                gdi,
                string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName)
                    ? edid?.Name is { } edidName && !string.IsNullOrWhiteSpace(edidName) ? edidName : UnnamedDisplay
                    : targetName.monitorFriendlyDeviceName,
                targetName.monitorDevicePath ?? "",
                IsInternalOutput(tgt.outputTechnology),
                tgt.outputTechnology,
                width,
                height,
                new RefreshRate(tgt.refreshRate.Numerator, tgt.refreshRate.Denominator),
                maxAtRes,
                modeList,
                adapterPath,
                vendor,
                null,
                hdrSupported,
                hdrEnabled,
                edid) { IsPrimary = primary });
        }
        return result;
    }

    /// <summary>
    /// HDR supported and on. The second version of the query (24H2 and later) names HDR itself; the first one only knows
    /// "advanced color", which also covers an SDR display with Auto Color Management (wide color gamut) on, so there an
    /// active advanced color counts as HDR only when wide color is not what is enforced (bit 2), and "supported" stays approximate.
    /// </summary>
    private static (bool Supported, bool Enabled) ReadHdr(Native.LUID adapter, uint target)
    {
        var info2 = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            header = Header(Native.DeviceInfoGetAdvancedColorInfo2, Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(), adapter, target),
        };
        if (Native.DisplayConfigGetDeviceInfo(ref info2) == 0)
            return HdrFromInfo2(info2.value, info2.activeColorMode);
        var info = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = Header(Native.DeviceInfoGetAdvancedColorInfo, Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(), adapter, target),
        };
        return Native.DisplayConfigGetDeviceInfo(ref info) == 0 ? HdrFromInfo1(info.value) : (false, false);
    }

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2: bit 4 highDynamicRangeSupported, and the active color mode is HDR.</summary>
    public static (bool Supported, bool Enabled) HdrFromInfo2(uint value, uint activeColorMode) =>
        ((value & 0x10) != 0, activeColorMode == Native.AdvancedColorModeHdr);

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO: bit 0 supported, bit 1 enabled, bit 2 wide color enforced (not HDR).</summary>
    public static (bool Supported, bool Enabled) HdrFromInfo1(uint value) =>
        ((value & 0x1) != 0, (value & 0x2) != 0 && (value & 0x4) == 0);

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values for embedded DisplayPort and embedded UDI.</summary>
    public const uint OutputDisplayPortEmbedded = 11, OutputUdiEmbedded = 13;

    /// <summary>
    /// Built-in panel: drivers report either ..._INTERNAL or the embedded DisplayPort/UDI values for it; Microsoft says
    /// callers should treat the embedded values as internal (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY remarks).
    /// </summary>
    public static bool IsInternalOutput(uint outputTechnology) =>
        outputTechnology is Native.OutputTechnologyInternal or OutputDisplayPortEmbedded or OutputUdiEmbedded;

    private static Native.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, Native.LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    private static List<DisplayMode> EnumModes(string gdiName)
    {
        var list = new HashSet<DisplayMode>();
        if (string.IsNullOrEmpty(gdiName)) return [];
        var dm = new Native.DEVMODE { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
        for (var i = 0; Native.EnumDisplaySettingsEx(gdiName, i, ref dm, 0); i++)
        {
            if ((dm.dmDisplayFlags & Native.DmInterlaced) != 0) continue;
            // 0 and 1 Hz mean "the hardware's default rate", not a rate that could be chosen.
            if (dm.dmDisplayFrequency <= 1) continue;
            list.Add(new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, (int)dm.dmBitsPerPel));
        }
        return list.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.RefreshHz).ToList();
    }

    /// <summary>"\\?\DISPLAY#AOC2701#5&amp;x&amp;0&amp;UID4352#{guid}" -> "DISPLAY\AOC2701\5&amp;x&amp;0&amp;UID4352".</summary>
    public static string? MonitorPathToInstanceId(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        var p = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) p = p[..guid];
        return p.Replace('#', '\\');
    }

    private static EdidInfo? ReadEdid(string? monitorDevicePath)
    {
        var instance = MonitorPathToInstanceId(monitorDevicePath);
        if (instance is null) return null;
        return Reg.HklmValue($@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters", "EDID") is byte[] edid ? ParseEdid(edid) : null;
    }

    /// <summary>EDID 1.x base block: manufacturer, product, name descriptor, range limits, preferred timing.</summary>
    public static EdidInfo? ParseEdid(byte[] e)
    {
        if (e.Length < 128 || e[0] != 0x00 || e[1] != 0xFF || e[7] != 0x00) return null;
        var m = (e[8] << 8) | e[9];
        var mfg = new string([(char)('A' - 1 + ((m >> 10) & 0x1F)), (char)('A' - 1 + ((m >> 5) & 0x1F)), (char)('A' - 1 + (m & 0x1F))]);
        var product = (ushort)(e[10] | (e[11] << 8));
        string? name = null;
        int? minV = null, maxV = null, prefW = null, prefH = null;
        double? prefHz = null;
        for (var d = 54; d <= 108; d += 18)
        {
            if (e[d] != 0 || e[d + 1] != 0)
            {
                if (prefW is not null) continue; // first detailed timing = preferred
                var clock = (e[d] | (e[d + 1] << 8)) * 10_000.0;
                var hActive = e[d + 2] | ((e[d + 4] & 0xF0) << 4);
                var hBlank = e[d + 3] | ((e[d + 4] & 0x0F) << 8);
                var vActive = e[d + 5] | ((e[d + 7] & 0xF0) << 4);
                var vBlank = e[d + 6] | ((e[d + 7] & 0x0F) << 8);
                var total = (double)(hActive + hBlank) * (vActive + vBlank);
                prefW = hActive;
                prefH = vActive;
                prefHz = total > 0 ? Math.Round(clock / total, 2) : null;
                continue;
            }
            var tag = e[d + 3];
            if (tag == 0xFC)
            {
                name = Encoding.ASCII.GetString(e, d + 5, 13).Split('\n')[0].Trim();
            }
            else if (tag == 0xFD)
            {
                var flags = e[d + 4];
                minV = e[d + 5] + ((flags & 0x01) != 0 ? 255 : 0);
                maxV = e[d + 6] + ((flags & 0x02) != 0 ? 255 : 0);
            }
        }
        // Byte 18/19 = EDID version/revision; byte 24 bit 0 means "continuous frequency" only in EDID 1.4.
        var continuous = e[18] == 1 && e[19] >= 4 && (e[24] & 0x01) != 0;
        return new EdidInfo(mfg, product, name, minV, maxV, prefW, prefH, prefHz) { ContinuousFrequency = continuous };
    }
}
