using Optimizer.Core.Actions;
using Optimizer.Core.Hardware;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>
/// Four PCs as a scan would read them, for the expected plans (<see cref="ExpectedPlanTests"/>): an NVIDIA gaming
/// desktop, a gaming laptop, an older PC and a Home edition PC. Built in C#, not JSON, so a renamed or new field of the
/// scan breaks the build instead of being missed. Every date is fixed and the checks get <see cref="Today"/>: the plans
/// never change with the calendar. Each PC has a problem a change fixes, so the plans show fixes as well; <see cref="Arrange"/>
/// gives the sandbox and the fakes the state the scan reported, so the tweak states agree with the checks.
/// </summary>
internal static class SamplePcs
{
    /// <summary>The date the age checks (BIOS, graphics driver) count from.</summary>
    public static readonly DateTime Today = new(2026, 10, 1);

    public static IReadOnlyDictionary<string, HardwareProfile> All { get; } = new Dictionary<string, HardwareProfile>(StringComparer.Ordinal)
    {
        ["nvidiaDesktop"] = NvidiaDesktop(),
        ["gamingLaptop"] = GamingLaptop(),
        ["olderPc"] = OlderPc(),
        ["homeEdition"] = HomeEdition(),
    };

    /// <summary>
    /// What the scan of <paramref name="pc"/> read, written to the sandbox and the fakes the tweak detection reads: the
    /// graphics scheduler, the active power plan, TRIM, and the mouse as Windows ships it. Everything else stays as the
    /// fixture starts (an empty sandbox).
    /// </summary>
    public static void Arrange(EngineFixture fx, HardwareProfile pc)
    {
        if (pc.Gpus?.FirstOrDefault()?.HagsEnabled is TriState.Yes or TriState.No)
            RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "dword",
                pc.Gpus[0].HagsEnabled == TriState.Yes ? "2" : "1");
        if (pc.Power is { } power)
        {
            fx.Power.SchemeNames[power.ActiveScheme] = power.ActiveSchemeName;
            fx.Power.Active = power.ActiveScheme;
        }
        if (pc.Extras?.Trim?.DisableDeleteNotification is { } trim)
            RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "DisableDeleteNotification", "dword",
                trim.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (name, value) in new[] { ("MouseSpeed", "1"), ("MouseThreshold1", "6"), ("MouseThreshold2", "10") })
            RegistryValue.Write(fx.Registry, Hive.User, @"Control Panel\Mouse", name, "string", value);
    }

    private static BuildInfo Os(int build, string edition) =>
        new(build, 7000, build >= 26300 ? "26H2" : build >= 26200 ? "25H2" : "24H2", edition, edition == "Core" ? "Windows 11 Home" : "Windows 11 Pro",
            CpuArchitecture.X64, false, null);

    private static readonly ElevationInfo Elevated = new(true, @"PC\Player", "S-1-5-21-1-1001", @"PC\Player", "S-1-5-21-1-1001", false);

    private static readonly ManagedDeviceInfo Unmanaged = new(false, false, false);

    private static CpuInfo Cpu(string name, Vendor vendor, int family, int model, int cores, int threads, int mhz, string socket,
        IReadOnlyDictionary<int, int> classes) =>
        new(name, vendor, family, model, 2, cores, threads, mhz, socket, 0x12B, 0x12B, "registry", [new CacheDomain(3, 32L << 20, ulong.MaxValue, 0)], classes);

    private static GpuInfo Gpu(string name, Vendor vendor, GpuKind kind, string driver, DateTime driverDate, long vram, string device) =>
        new(name, vendor, $@"PCI\{device}\4&1A2B3C4D&0&0008", driver, driverDate, "oem12.inf", vendor.ToString().ToUpperInvariant(), vram, kind,
            kind == GpuKind.Discrete ? vram : 256L << 20, null, null, 0, kind == GpuKind.Discrete ? TriState.Yes : TriState.Unknown);

    private static MemoryModule Ddr(int gb, int rated, int configured, int type, string slot) =>
        new((long)gb << 30, rated, configured, "KF560C36-16", "Kingston", slot, "BANK 0", type, 8);

    private static DisplayInfo Display(string name, bool internalPanel, int width, int height, int currentHz, int maxHz, Vendor adapter, string adapterName) =>
        new(@"\\.\DISPLAY1", name, @"\\?\DISPLAY#ABC1234#5&1a2b3c4d&0&UID256", internalPanel, internalPanel ? 0x80000000u : 10u, width, height,
            new RefreshRate((uint)currentHz * 1000, 1000), maxHz, [], @"\\?\PCI#VEN", adapter, adapterName, false, false, null)
        { IsPrimary = true };

    private static FirmwareInfo Firmware(DateTime biosDate, string board, bool memoryIntegrity) =>
        new(true, TriState.Yes, TriState.Yes, "2.0", TriState.Yes, memoryIntegrity ? 2 : 0, memoryIntegrity, false, true, true, "American Megatrends Inc.",
            "1.20", biosDate, "Micro-Star International Co., Ltd.", board, PartitionStyle.Gpt);

    private static PowerInfo Power(PowerPersonality personality, bool laptop, uint boost = 2) =>
        new(personality == PowerPersonality.PowerSaver ? new Guid("a1841308-3541-4fab-bc81-f71556f20b4a") : new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
            personality == PowerPersonality.PowerSaver ? "Power saver" : "Balanced", personality, 100, 5, boost, null, true, false, laptop, laptop ? 80 : null);

    private static StorageInfo Ssd(long size = 2_000_000_000_000) =>
        new([new PhysicalDisk(0, "Samsung SSD 990 PRO 2TB", "SSD", "NVMe", size, "Healthy", false)],
            [new Volume(@"C:\", "", size, size / 2, 0, true)]);

    private static NetworkAdapterInfo Ethernet(long bps = 1_000_000_000) => new("Ethernet", "Realtek PCIe 2.5GbE Family Controller", "Ethernet", bps, true);

    private static HardwareExtras Extras(bool trimOn = true) => new()
    {
        Trim = new TrimSetting(trimOn ? 0 : 1),
        StartupPrograms = ["Steam", "Discord"],
        BackgroundCpu = [],
    };

    private static SoftwareInfo Steam() => new([], ["Steam"], [@"C:\Program Files (x86)\Steam"]);

    /// <summary>A current gaming desktop: Core i7-13700K, GeForce RTX 4070, 32 GB DDR5, a 165 Hz monitor left at 60 Hz.</summary>
    private static HardwareProfile NvidiaDesktop() => new()
    {
        Os = Os(26300, "Professional"),
        Elevation = Elevated,
        Managed = Unmanaged,
        System = new SystemInfo("Micro-Star International Co., Ltd.", "MS-7E06", [3], false, false, false),
        Cpu = Cpu("13th Gen Intel(R) Core(TM) i7-13700K", Vendor.Intel, 6, 183, 16, 24, 3400, "LGA1700", new Dictionary<int, int> { [0] = 8, [1] = 8 }),
        Gpus =
        [
            Gpu("NVIDIA GeForce RTX 4070", Vendor.Nvidia, GpuKind.Discrete, "32.0.15.7680", new DateTime(2026, 8, 20), 12L << 30, "VEN_10DE&DEV_2786"),
            Gpu("Intel(R) UHD Graphics 770", Vendor.Intel, GpuKind.Integrated, "32.0.101.6078", new DateTime(2026, 6, 2), 128L << 20, "VEN_8086&DEV_A780"),
        ],
        Memory = new MemoryInfo(32L << 30, [Ddr(16, 6000, 6000, 34, "DIMMA2"), Ddr(16, 6000, 6000, 34, "DIMMB2")]),
        Displays = [Display("AOC Q27G2G4", false, 2560, 1440, 60, 165, Vendor.Nvidia, "NVIDIA GeForce RTX 4070")],
        Firmware = Firmware(new DateTime(2025, 11, 4), "MAG Z790 TOMAHAWK WIFI (MS-7E06)", memoryIntegrity: true),
        Power = Power(PowerPersonality.Balanced, laptop: false),
        Storage = Ssd(),
        Network = [Ethernet()],
        Software = Steam(),
        Extras = Extras(),
    };

    /// <summary>A gaming laptop: Core i7-13700H with Iris Xe and a GeForce RTX 4060 Laptop GPU, 16 GB, a 144 Hz panel at 60 Hz, on mains, Wi-Fi only.</summary>
    private static HardwareProfile GamingLaptop() => new()
    {
        Os = Os(26300, "Professional"),
        Elevation = Elevated,
        Managed = Unmanaged,
        System = new SystemInfo("Lenovo", "Legion Pro 5 16IRX8", [10], true, true, false),
        Battery = new Hardware.Probes.BatteryHealth(80_000, 74_000),
        Cpu = Cpu("13th Gen Intel(R) Core(TM) i7-13700H", Vendor.Intel, 6, 186, 14, 20, 2400, "BGA1744", new Dictionary<int, int> { [0] = 8, [1] = 6 }),
        Gpus =
        [
            Gpu("NVIDIA GeForce RTX 4060 Laptop GPU", Vendor.Nvidia, GpuKind.Discrete, "32.0.15.7680", new DateTime(2026, 8, 20), 8L << 30, "VEN_10DE&DEV_28E0"),
            Gpu("Intel(R) Iris(R) Xe Graphics", Vendor.Intel, GpuKind.Integrated, "32.0.101.6078", new DateTime(2026, 6, 2), 128L << 20, "VEN_8086&DEV_A7A0"),
        ],
        Memory = new MemoryInfo(16L << 30, [Ddr(8, 5200, 5200, 34, "DIMM 0"), Ddr(8, 5200, 5200, 34, "DIMM 1")]),
        Displays = [Display("Built-in display", true, 2560, 1600, 60, 144, Vendor.Intel, "Intel(R) Iris(R) Xe Graphics")],
        Firmware = Firmware(new DateTime(2026, 3, 12), "LNVNB161216", memoryIntegrity: true),
        Power = Power(PowerPersonality.Balanced, laptop: true),
        Storage = Ssd(1_000_000_000_000),
        Network = [new NetworkAdapterInfo("Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", "Wi-Fi", 1_200_000_000, true)],
        Software = Steam(),
        Extras = Extras(),
    };

    /// <summary>
    /// An older office PC: Core i5-8400 with its integrated graphics only, 6 GB of mixed DDR4, Windows on a hard disk,
    /// a BIOS from 2018 and the Power saver plan, on 24H2.
    /// </summary>
    private static HardwareProfile OlderPc() => new()
    {
        Os = Os(26100, "Professional"),
        Elevation = Elevated,
        Managed = Unmanaged,
        System = new SystemInfo("Dell Inc.", "OptiPlex 5060", [3], false, false, false),
        Cpu = Cpu("Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz", Vendor.Intel, 6, 158, 6, 6, 2800, "LGA1151", new Dictionary<int, int> { [0] = 6 }),
        Gpus = [Gpu("Intel(R) UHD Graphics 630", Vendor.Intel, GpuKind.Integrated, "31.0.101.2127", new DateTime(2024, 2, 15), 128L << 20, "VEN_8086&DEV_3E92")],
        Memory = new MemoryInfo(6L << 30, [Ddr(4, 2666, 2666, 26, "DIMM1"), Ddr(2, 2400, 2400, 26, "DIMM2")]),
        Displays = [Display("DELL P2219H", false, 1920, 1080, 60, 60, Vendor.Intel, "Intel(R) UHD Graphics 630")],
        Firmware = Firmware(new DateTime(2018, 9, 20), "0P03DX", memoryIntegrity: false),
        Power = Power(PowerPersonality.PowerSaver, laptop: false),
        Storage = new StorageInfo([new PhysicalDisk(0, "ST1000DM010-2EP102", "HDD", "SATA", 1_000_000_000_000, "Healthy", false)],
            [new Volume(@"C:\", "", 1_000_000_000_000, 300_000_000_000, 0, true)]),
        Network = [Ethernet()],
        Software = new SoftwareInfo([], [], []),
        Extras = Extras(),
    };

    /// <summary>A Home edition gaming PC on 25H2: Ryzen 5 7600 with a Radeon RX 7600, 16 GB, TRIM turned off.</summary>
    private static HardwareProfile HomeEdition() => new()
    {
        Os = Os(26200, "Core"),
        Elevation = Elevated,
        Managed = Unmanaged,
        System = new SystemInfo("ASUS", "System Product Name", [3], false, false, false),
        Cpu = Cpu("AMD Ryzen 5 7600 6-Core Processor", Vendor.Amd, 25, 97, 6, 12, 3800, "AM5", new Dictionary<int, int> { [0] = 6 }),
        Gpus = [Gpu("AMD Radeon RX 7600", Vendor.Amd, GpuKind.Discrete, "32.0.21025.10016", new DateTime(2026, 7, 30), 8L << 30, "VEN_1002&DEV_7480")],
        Memory = new MemoryInfo(16L << 30, [Ddr(8, 6000, 6000, 34, "DIMM_A2"), Ddr(8, 6000, 6000, 34, "DIMM_B2")]),
        Displays = [Display("Acer KG241Q", false, 1920, 1080, 144, 144, Vendor.Amd, "AMD Radeon RX 7600")],
        Firmware = Firmware(new DateTime(2026, 5, 6), "TUF GAMING B650-PLUS", memoryIntegrity: false),
        Power = Power(PowerPersonality.Balanced, laptop: false),
        Storage = Ssd(1_000_000_000_000),
        Network = [Ethernet(2_500_000_000)],
        Software = Steam(),
        Extras = Extras(trimOn: false),
    };
}
