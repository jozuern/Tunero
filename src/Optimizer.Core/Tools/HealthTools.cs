using Optimizer.Core.Actions;
using Optimizer.Core.Hardware;

namespace Optimizer.Core.Tools;

/// <summary>
/// System file check and component store repair with live output. No time limit: /RestoreHealth can take an hour on a
/// slow connection, so the page's Stop (cancelling <c>ct</c>) is what ends them early.
/// </summary>
public static class SystemRepair
{
    public static string Sfc => Path.Combine(Environment.SystemDirectory, "sfc.exe");
    public static string Dism => Path.Combine(Environment.SystemDirectory, "dism.exe");

    /// <summary>sfc /scannow. Exit 0 = no violations or repaired; the last lines say which.</summary>
    public static Task<int> ScanNowAsync(IProcessRunner processes, IProgress<string>? lines, CancellationToken ct) =>
        processes.RunAsync(Sfc, "/scannow", null, ct, lines, RunOptions.Utf16);

    /// <summary>DISM /ScanHealth (read-only check of the component store).</summary>
    public static Task<int> ScanHealthAsync(IProcessRunner processes, IProgress<string>? lines, CancellationToken ct) =>
        processes.RunAsync(Dism, "/Online /Cleanup-Image /ScanHealth", null, ct, lines, RunOptions.Oem);

    /// <summary>DISM /RestoreHealth (repairs the component store from Windows Update). Run sfc again afterwards.</summary>
    public static Task<int> RestoreHealthAsync(IProcessRunner processes, IProgress<string>? lines, CancellationToken ct) =>
        processes.RunAsync(Dism, "/Online /Cleanup-Image /RestoreHealth", null, ct, lines, RunOptions.Oem);
}

public sealed record DiskHealth(
    string Name,
    string MediaType,
    string BusType,
    string Health,
    int? TemperatureC,
    int? TemperatureMaxC,
    int? WearPercent,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected,
    long? PowerOnHours)
{
    /// <summary>Windows' own verdict (HealthStatus) or an SSD that used 90 % or more of its rated write endurance.</summary>
    public bool IsProblem => Health is "Warning" or "Unhealthy" || WearPercent >= 90 || ReadErrorsUncorrected > 0 || WriteErrorsUncorrected > 0;

    public bool IsCritical => Health == "Unhealthy" || WriteErrorsUncorrected > 0;

    /// <summary>The disk number Windows uses (MSFT_PhysicalDisk.DeviceId): two drives of the same model differ here.</summary>
    public string DeviceId { get; init; } = "";
}

/// <summary>
/// Drive health from Storage Management (MSFT_PhysicalDisk.HealthStatus and MSFT_StorageReliabilityCounter, which
/// Windows fills from S.M.A.R.T. / NVMe health logs). The reliability counters need administrator rights.
/// </summary>
public static class DiskHealthReader
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<DiskHealth> Read()
    {
        var counters = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var c in Wmi.Query("SELECT DeviceId, Temperature, TemperatureMax, Wear, ReadErrorsUncorrected, WriteErrorsUncorrected, PowerOnHours FROM MSFT_StorageReliabilityCounter", Scope))
                counters[c.Str("DeviceId")] = c;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Management.ManagementException)
        {
            // not elevated: health status only
        }

        var list = new List<DiskHealth>();
        foreach (var d in Wmi.PhysicalDisks())
        {
            counters.TryGetValue(d.Str("DeviceId"), out var c);
            list.Add(new DiskHealth(
                d.Str("FriendlyName"),
                d.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                d.Int("BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", _ => "Other" },
                d.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                Positive(c, "Temperature"),
                Positive(c, "TemperatureMax"),
                c is null ? null : (int?)Number(c, "Wear"),
                Number(c, "ReadErrorsUncorrected"),
                Number(c, "WriteErrorsUncorrected"),
                Number(c, "PowerOnHours")) { DeviceId = d.Str("DeviceId") });
        }
        return list;
    }

    private static long? Number(Dictionary<string, object?>? row, string name) =>
        row is not null && row.TryGetValue(name, out var v) && v is not null && long.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out var n) ? n : null;

    /// <summary>0 means "not reported" for temperatures.</summary>
    private static int? Positive(Dictionary<string, object?>? row, string name) => Number(row, name) is { } n and > 0 and < 200 ? (int)n : null;
}
