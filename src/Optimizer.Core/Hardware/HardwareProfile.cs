using Optimizer.Core.Platform;

namespace Optimizer.Core.Hardware;

public enum Vendor { Unknown, Intel, Amd, Nvidia, Microsoft, Qualcomm, Other }

/// <summary>Everything the scanner collected. Each section is null when its probe failed (-> findings report Unknown).</summary>
public sealed record HardwareProfile
{
    public required BuildInfo Os { get; init; }
    public ElevationInfo? Elevation { get; init; }
    public ManagedDeviceInfo? Managed { get; init; }
    public CpuInfo? Cpu { get; init; }
    public IReadOnlyList<GpuInfo>? Gpus { get; init; }
    public MemoryInfo? Memory { get; init; }
    public IReadOnlyList<DisplayInfo>? Displays { get; init; }
    public FirmwareInfo? Firmware { get; init; }
    public PowerInfo? Power { get; init; }
    public StorageInfo? Storage { get; init; }
    public IReadOnlyList<NetworkAdapterInfo>? Network { get; init; }
    public SoftwareInfo? Software { get; init; }
    public SystemInfo? System { get; init; }

    /// <summary>Battery capacity (design vs. full charge); null without a battery or usable driver data.</summary>
    public Probes.BatteryHealth? Battery { get; init; }

    /// <summary>Data for the advisor and network checks (NIC capabilities, Wi-Fi band, NVMe links, Secure Boot certificates, NVIDIA state).</summary>
    public HardwareExtras? Extras { get; init; }

    /// <summary>Probe name -> error message, for the Hardware page and the log.</summary>
    public IReadOnlyDictionary<string, string> ProbeErrors { get; init; } = new Dictionary<string, string>();

    public bool IsLaptop => System?.IsLaptop ?? false;

    /// <summary>The probe (its name in <see cref="ProbeErrors"/>: "Storage", "Memory", "Power", "Extras") failed or timed out.</summary>
    public bool ProbeFailed(string probe) => ProbeErrors.ContainsKey(probe);
}

public sealed record SystemInfo(string Manufacturer, string Model, IReadOnlyList<int> ChassisTypes, bool HasBattery, bool LidPresent, bool HypervisorPresent)
{
    private static readonly HashSet<int> LaptopChassis = [8, 9, 10, 11, 14, 30, 31, 32];
    public bool IsLaptop => HasBattery && (LidPresent || ChassisTypes.Any(LaptopChassis.Contains));

    /// <summary>
    /// A virtual machine (Hyper-V and Azure, VMware, VirtualBox, QEMU/KVM, Xen, Parallels, Amazon EC2, Google Compute Engine), from the maker and model the firmware
    /// reports. Not HypervisorPresent: that is also true on a real PC with memory integrity or Hyper-V on.
    /// </summary>
    public bool IsVirtualMachine =>
        (Manufacturer == "Microsoft Corporation" && Model == "Virtual Machine") ||
        VirtualMakers.Any(v => Manufacturer.Contains(v, StringComparison.OrdinalIgnoreCase) || Model.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] VirtualMakers = ["VMware", "VirtualBox", "innotek", "QEMU", "KVM", "Xen", "Parallels", "Bochs", "Amazon EC2", "Google Compute Engine"];
}

// ---------------- CPU ----------------
public sealed record CacheDomain(int Level, long SizeBytes, ulong Mask, int Group);

public sealed record CpuInfo(
    string Name,
    Vendor Vendor,
    int Family,
    int Model,
    int Stepping,
    int Cores,
    int Threads,
    int MaxClockMhz,
    string Socket,
    uint? MicrocodeCurrent,
    uint? MicrocodeBios,
    string MicrocodeSource,
    IReadOnlyList<CacheDomain> L3Domains,
    IReadOnlyDictionary<int, int> CoresByEfficiencyClass)
{
    /// <summary>Hybrid = more than one efficiency class (via EfficiencyClass, not by model).</summary>
    public bool IsHybrid => CoresByEfficiencyClass.Count > 1;

    public int PerformanceCores => CoresByEfficiencyClass.Count == 0 ? Cores : CoresByEfficiencyClass[CoresByEfficiencyClass.Keys.Max()];

    /// <summary>The name for headings and tiles: "Intel Core i7-9700K" instead of "Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz".</summary>
    public string ShortName => Shorten(Name);

    /// <summary>
    /// Drops what the processor's brand string adds to the model: trademark signs, the clock after "@", "CPU", and the
    /// "8-Core Processor" of AMD. A name that would come out empty stays as it is.
    /// </summary>
    public static string Shorten(string name)
    {
        var s = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("\u00AE", "").Replace("\u2122", "");
        var at = s.IndexOf('@');
        if (at >= 0) s = s[..at];
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("CPU", StringComparison.OrdinalIgnoreCase) && !w.Equals("Processor", StringComparison.OrdinalIgnoreCase)
                && !(w.EndsWith("-Core", StringComparison.OrdinalIgnoreCase) && char.IsDigit(w[0]))));
        return s.Length > 0 ? s : name.Trim();
    }
}

// ---------------- GPU ----------------
public enum GpuKind { Discrete, Integrated, Basic, Virtual, Unknown }

public enum TriState { Unknown, No, Yes }

public sealed record PcieLink(int? CurrentGen, int? CurrentWidth, int? MaxGen, int? MaxWidth)
{
    public static int? SpeedCodeToGen(uint? code) => code is >= 1 and <= 7 ? (int)code : null;
}

public sealed record GpuInfo(
    string Name,
    Vendor Vendor,
    string PnpDeviceId,
    string? DriverVersion,
    DateTime? DriverDate,
    string? InfFile,
    string? DriverProvider,
    long? VramBytes,
    GpuKind Kind,
    long? LargestBarBytes,
    PcieLink? CardLink,
    PcieLink? PlatformPortLink,
    int SwitchHopsSkipped,
    TriState HagsEnabled)
{
    public string? NvidiaDriverVersion => Vendor == Vendor.Nvidia ? NvidiaVersion(DriverVersion) : null;

    /// <summary>Windows driver version -> NVIDIA version: 32.0.15.6094 -> 560.94 (last five digits).</summary>
    public static string? NvidiaVersion(string? windowsVersion)
    {
        if (string.IsNullOrEmpty(windowsVersion)) return null;
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }
}

// ---------------- Memory ----------------
public sealed record MemoryModule(
    long CapacityBytes,
    int? SpeedMts,
    int? ConfiguredMts,
    string PartNumber,
    string Manufacturer,
    string DeviceLocator,
    string BankLabel,
    int SmbiosMemoryType,
    int FormFactor)
{
    public string Type => SmbiosMemoryType switch
    {
        26 => "DDR4",
        34 => "DDR5",
        24 => "DDR3",
        35 => "LPDDR5",
        30 => "LPDDR4",
        _ => "Unknown",
    };
}

public sealed record MemoryInfo(long TotalBytes, IReadOnlyList<MemoryModule> Modules);

// ---------------- Displays ----------------
public sealed record RefreshRate(uint Numerator, uint Denominator)
{
    public double Hz => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    public override string ToString() => $"{Hz:0.##} Hz";
}

public sealed record DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel);

public sealed record DisplayInfo(
    string GdiName,
    string FriendlyName,
    string MonitorDevicePath,
    bool IsInternal,
    uint OutputTechnology,
    int Width,
    int Height,
    RefreshRate CurrentRefresh,
    int MaxOfferedRefreshAtCurrentResolution,
    IReadOnlyList<DisplayMode> Modes,
    string AdapterDevicePath,
    Vendor AdapterVendor,
    string? AdapterName,
    bool HdrSupported,
    bool HdrEnabled,
    EdidInfo? Edid)
{
    /// <summary>Primary display (desktop origin at 0,0). False when the position could not be read.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>
    /// The monitor reports a name of its own (Windows' friendly name or the EDID name). Laptop panels often report none,
    /// and then <see cref="FriendlyName"/> is only the generic "Display" (<see cref="Probes.DisplayProbe.UnnamedDisplay"/>).
    /// </summary>
    public bool HasOwnName => !string.IsNullOrWhiteSpace(FriendlyName) && FriendlyName != Probes.DisplayProbe.UnnamedDisplay;
}

public sealed record EdidInfo(string ManufacturerId, ushort ProductCode, string? Name, int? MinVHz, int? MaxVHz, int? PreferredWidth, int? PreferredHeight, double? PreferredRefreshHz)
{
    /// <summary>EDID 1.4 feature byte bit 0: the display accepts a continuous range of frequencies (a hint for VRR panels).</summary>
    public bool ContinuousFrequency { get; init; }

    /// <summary>A refresh range wide enough for variable refresh (e.g. 48 to 144 Hz). Hint only; the GPU driver decides.</summary>
    public bool LooksVrrCapable => ContinuousFrequency && MinVHz is <= 60 && MaxVHz is >= 90 && MaxVHz >= MinVHz * 1.5;
}

// ---------------- Firmware & security ----------------
public enum PartitionStyle { Unknown, Mbr, Gpt }

public sealed record FirmwareInfo(
    bool IsUefi,
    TriState SecureBoot,
    TriState TpmPresent,
    string? TpmSpecVersion,
    TriState TpmReady,
    int VbsStatus,
    bool HvciRunning,
    bool CredentialGuardRunning,
    bool MbecAvailable,
    bool DmaProtectionAvailable,
    string BiosVendor,
    string BiosVersion,
    DateTime? BiosDate,
    string BoardManufacturer,
    string BoardProduct,
    PartitionStyle SystemDiskPartitionStyle)
{
    public bool VbsRunning => VbsStatus == 2;

    /// <summary>TPM maker id (Win32_Tpm.ManufacturerIdTxt, "AMD" for fTPM); null when not elevated or unknown.</summary>
    public string? TpmManufacturer { get; init; }
}

// ---------------- Power ----------------
public enum PowerPersonality { Unknown, PowerSaver, Balanced, HighPerformance }

public sealed record PowerInfo(
    Guid ActiveScheme,
    string ActiveSchemeName,
    PowerPersonality Personality,
    uint? MaxProcessorStateAc,
    uint? MinProcessorStateAc,
    uint? BoostModeAc,
    uint? CoreParkingMinCoresAc,
    bool? OnAc, // null: ACLineStatus 255 ("unknown status")
    bool EnergySaverOn,
    bool ModernStandby,
    int? BatteryPercent);

// ---------------- Storage ----------------
public sealed record PhysicalDisk(int Number, string FriendlyName, string MediaType, string BusType, long SizeBytes, string Health, bool IsSmrSuspect);

public sealed record Volume(string Root, string Label, long SizeBytes, long FreeBytes, int? DiskNumber, bool IsSystem)
{
    public double FreeFraction => SizeBytes == 0 ? 1 : (double)FreeBytes / SizeBytes;
}

public sealed record StorageInfo(IReadOnlyList<PhysicalDisk> Disks, IReadOnlyList<Volume> Volumes)
{
    /// <summary>The disks could not be read (Windows' storage provider failed or did not answer); the volumes still were.</summary>
    public bool DisksUnread { get; init; }

    /// <summary>
    /// The physical disk a volume lies on, or null when that is not certain. The volume's disk number is the number of
    /// the disk Windows sees; for Storage Spaces that is the virtual disk, whose number a pooled physical disk can carry
    /// too (or none does). So only one disk with the number counts, and only when the volume fits on it.
    /// </summary>
    public PhysicalDisk? DiskOf(Volume? volume)
    {
        if (volume?.DiskNumber is not { } number) return null;
        var matches = Disks.Where(d => d.Number == number).Take(2).ToList();
        return matches is [var disk] && (disk.SizeBytes == 0 || volume.SizeBytes <= disk.SizeBytes) ? disk : null;
    }
}

// ---------------- Network ----------------
public sealed record NetworkAdapterInfo(string Name, string Description, string Type, long SpeedBps, bool IsUp);

// ---------------- Software ----------------
public sealed record AntiCheatPresence(string Id, string DisplayName, IReadOnlyList<string> FoundServices);

public sealed record InstalledGame(string Name, string Launcher, string InstallDir, string? Executable);

public sealed record SoftwareInfo(IReadOnlyList<AntiCheatPresence> AntiCheats, IReadOnlyList<string> Launchers, IReadOnlyList<string> GameLibraryPaths)
{
    public IReadOnlyList<InstalledGame> Games { get; init; } = [];

    /// <summary>Install folders of games from other launchers (Epic, GOG, Ubisoft Connect); protected in the storage analyzer.</summary>
    public IReadOnlyList<string> OtherGameFolders { get; init; } = [];
}
