using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Hardware.Probes;

public static class MemoryProbe
{
    public static MemoryInfo Read()
    {
        var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, PartNumber, Manufacturer, DeviceLocator, BankLabel, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory")
            .Select(r => new MemoryModule(
                r.Long("Capacity") ?? 0,
                r.Int("Speed"),
                r.Int("ConfiguredClockSpeed"),
                r.Str("PartNumber"),
                r.Str("Manufacturer"),
                r.Str("DeviceLocator"),
                r.Str("BankLabel"),
                r.Int("SMBIOSMemoryType") ?? 0,
                r.Int("FormFactor") ?? 0))
            .ToList();
        var total = Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault()?.Long("TotalPhysicalMemory") ?? modules.Sum(m => m.CapacityBytes);
        return new MemoryInfo(Math.Max(total, modules.Sum(m => m.CapacityBytes)), modules);
    }
}

public static class SystemProbe
{
    public static SystemInfo Read()
    {
        var cs = Wmi.Query("SELECT Manufacturer, Model, HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault() ?? [];
        var chassis = Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure").SelectMany(r => r.IntArray("ChassisTypes")).ToList();
        return new SystemInfo(cs.Str("Manufacturer"), cs.Str("Model"), chassis, PowerProbe.HasBattery(), PowerProbe.HasLid(), cs.Bool("HypervisorPresent") ?? false);
    }
}

public static class StorageProbe
{
    public static StorageInfo Read(CatalogData catalog)
    {
        const string ns = @"root\Microsoft\Windows\Storage";
        // The disks come from Windows' storage provider, which can fail or hang; free space does not need it, so a full
        // system drive is still reported then.
        List<PhysicalDisk> disks;
        var letterToDisk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var disksUnread = false;
        try
        {
            disks = ReadDisks(catalog);
            foreach (var p in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", ns))
            {
                var letter = p.Str("DriveLetter");
                if (letter.Length == 1 && letter[0] != '\0' && p.Int("DiskNumber") is { } n) letterToDisk[letter] = n;
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or TimeoutException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("scan", $"disks not read: {ex.Message}");
            disks = [];
            letterToDisk.Clear();
            disksUnread = true;
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var volumes = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new Volume(d.RootDirectory.FullName, d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace,
                letterToDisk.TryGetValue(d.Name[..1], out var n) ? n : null,
                string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new StorageInfo(disks, volumes) { DisksUnread = disksUnread };
    }

    private static List<PhysicalDisk> ReadDisks(CatalogData catalog) =>
        Wmi.PhysicalDisks()
            .Select(r =>
            {
                var name = r.Str("FriendlyName");
                var model = r.Str("Model");
                return new PhysicalDisk(
                    r.Int("DeviceId") ?? -1,
                    name,
                    r.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                    r.Int("BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", 1 => "SCSI", 3 => "ATA", _ => "Other" },
                    r.Long("Size") ?? 0,
                    r.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    catalog.Storage.IsSmr(model) || catalog.Storage.IsSmr(name));
            })
            .ToList();
}

public static class NetworkProbe
{
    /// <summary>Real network cards only (<see cref="Actions.NicAdapters.IsPhysical"/>): a VPN or a virtual switch reported as Ethernet is not counted.</summary>
    public static List<NetworkAdapterInfo> Read()
    {
        var physical = Actions.NicAdapters.Enumerate(new SystemRegistryRoots(null), includeSynthetic: true)
            .Select(a => a.InterfaceGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
            .Where(n => physical.Contains(n.Id))
            .Select(n => new NetworkAdapterInfo(n.Name, n.Description,
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet",
                n.OperationalStatus == OperationalStatus.Up ? n.Speed : 0,
                n.OperationalStatus == OperationalStatus.Up))
            .ToList();
    }
}

public static partial class SoftwareProbe
{
    public static SoftwareInfo Read(CatalogData catalog)
    {
        var antiCheats = new List<AntiCheatPresence>();
        foreach (var ac in catalog.AntiCheat.AntiCheats)
        {
            // Registration under Services, not running state (Vanguard On-Demand only loads while a Riot game runs).
            var found = ac.Services.Where(s => Reg.HklmKeyExists($@"SYSTEM\CurrentControlSet\Services\{s}")).ToList();
            if (found.Count > 0) antiCheats.Add(new AntiCheatPresence(ac.Id, ac.Name, found));
        }

        var launchers = new List<string>();
        var libraries = new List<string>();

        // Machine-wide keys only: under elevation HKCU may belong to another account.
        var steam = Reg.HklmString(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (steam is not null)
        {
            launchers.Add("Steam");
            libraries.AddRange(SteamLibraries(steam));
        }
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher")))
            launchers.Add("Epic Games");
        if (Reg.HklmKeyExists(@"SOFTWARE\WOW6432Node\GOG.com\Games")) launchers.Add("GOG");
        if (Reg.HklmKeyExists(@"SYSTEM\CurrentControlSet\Services\GamingServices")) launchers.Add("Xbox / Game Pass");
        if (Reg.HklmKeyExists(@"SOFTWARE\WOW6432Node\Riot Games") || Reg.HklmKeyExists(@"SYSTEM\CurrentControlSet\Services\vgc")) launchers.Add("Riot");
        if (Reg.HklmKeyExists(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\Battle.net")) launchers.Add("Battle.net");
        if (Reg.HklmKeyExists(@"SOFTWARE\Electronic Arts\EA Desktop")) launchers.Add("EA app");
        if (Reg.HklmKeyExists(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher")) launchers.Add("Ubisoft Connect");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            libraries.AddRange(XboxLibraries(drive.RootDirectory.FullName));

        var libs = libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new SoftwareInfo(antiCheats, launchers, libs) { Games = SteamGames(libs), OtherGameFolders = OtherLauncherGames() };
    }

    /// <summary>
    /// The Xbox app's library folders on a drive. Its ".GamingRoot" marker in the drive root names them (after an 8 byte
    /// header, folder names relative to the root in UTF-16, separated by zeros); "XboxGames" is the default name. Only
    /// folders that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> XboxLibraries(string driveRoot)
    {
        var marker = Path.Combine(driveRoot, ".GamingRoot");
        if (!File.Exists(marker)) return [];
        var found = new List<string>();
        try
        {
            var bytes = File.ReadAllBytes(marker);
            if (bytes.Length > 8 && bytes.Length < 64 * 1024)
                foreach (var name in System.Text.Encoding.Unicode.GetString(bytes, 8, (bytes.Length - 8) & ~1).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var relative = name.Trim().TrimStart('\\');
                    if (relative.Length == 0 || relative.Contains(':') || relative.Contains("..", StringComparison.Ordinal) || relative.Any(char.IsControl)) continue;
                    var folder = Path.Combine(driveRoot, relative);
                    if (Directory.Exists(folder)) found.Add(folder);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Logging.Log.Warn("probe", $"Xbox library marker {marker} not readable: {ex.Message}");
        }
        var standard = Path.Combine(driveRoot, "XboxGames");
        if (found.Count == 0 && Directory.Exists(standard)) found.Add(standard);
        return found;
    }

    /// <summary>
    /// Install folders of games from Epic (launcher manifests), GOG and Ubisoft Connect (machine-wide registry), so the
    /// storage analyzer never offers their files for deletion. Steam and Xbox libraries are in GameLibraryPaths.
    /// </summary>
    private static IReadOnlyList<string> OtherLauncherGames()
    {
        var folders = new List<string>();
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        try
        {
            if (Directory.Exists(manifests))
                foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(item));
                        if (doc.RootElement.TryGetProperty("InstallLocation", out var location) && location.GetString() is { Length: > 0 } path) folders.Add(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                    {
                        // an unreadable manifest: skipped
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("probe", $"Epic manifests not readable: {ex.Message}");
        }
        foreach (var id in Reg.HklmSubKeys(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            if (Reg.HklmString($@"SOFTWARE\WOW6432Node\GOG.com\Games\{id}", "path") is { Length: > 0 } path) folders.Add(path);
        foreach (var id in Reg.HklmSubKeys(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
            if (Reg.HklmString($@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\{id}", "InstallDir") is { Length: > 0 } path) folders.Add(path.Replace('/', '\\'));
        return folders.Where(f => Path.IsPathFullyQualified(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SteamLibraries(string steamPath)
    {
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return [steamPath];
        try
        {
            return VdfPathRegex().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();
        }
        catch (Exception)
        {
            return [steamPath];
        }
    }

    /// <summary>Installed Steam games from appmanifest_*.acf (name, installdir) and a best guess for the game's main exe.</summary>
    private static List<InstalledGame> SteamGames(IEnumerable<string> libraries)
    {
        var games = new List<InstalledGame>();
        foreach (var lib in libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var name = AcfValue(text, "name");
                    var dir = AcfValue(text, "installdir");
                    if (name is null || dir is null) continue;
                    var installDir = Path.Combine(apps, "common", dir);
                    if (!Directory.Exists(installDir)) continue;
                    games.Add(new InstalledGame(name, "Steam", installDir, GuessExecutable(installDir)));
                }
                catch (Exception)
                {
                    // unreadable manifest: skip
                }
            }
        }
        return games;
    }

    private static string? AcfValue(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Largest .exe within two folder levels, ignoring helpers (crash handlers, installers, anti-cheat, launchers).</summary>
    public static string? GuessExecutable(string installDir)
    {
        try
        {
            return Directory.EnumerateFiles(installDir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Where(f => !HelperExe().IsMatch(Path.GetFileName(f)))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"crash|unins|setup|redist|vc_?redist|dxsetup|launcher|helper|update|easyanticheat|battleye|be_service|ue4prereq|dotnet|cefprocess|webhelper|report", RegexOptions.IgnoreCase)]
    private static partial Regex HelperExe();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRegex();
}
