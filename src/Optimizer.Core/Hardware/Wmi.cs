using System.Management;

namespace Optimizer.Core.Hardware;

/// <summary>Small read-only WMI wrapper. Returns property bags so probes stay testable and free of COM objects.</summary>
public static class Wmi
{
    private static readonly TimeSpan PhysicalDiskLifetime = TimeSpan.FromSeconds(10);
    private static readonly Lock PhysicalDiskGate = new();
    private static (DateTime At, Lazy<List<Dictionary<string, object?>>> Rows)? _physicalDisks;

    /// <summary>
    /// MSFT_PhysicalDisk (DeviceId, FriendlyName, Model, MediaType, BusType, Size, HealthStatus). The storage probe, the
    /// drive health and the NVMe links of one scan all need it, and the Storage provider is slow to answer: one query
    /// is shared for a few seconds, and callers that ask while it runs wait for it. Callers must not change the rows.
    /// </summary>
    public static List<Dictionary<string, object?>> PhysicalDisks()
    {
        Lazy<List<Dictionary<string, object?>>> rows;
        lock (PhysicalDiskGate)
        {
            if (_physicalDisks is not { } cached || DateTime.UtcNow - cached.At >= PhysicalDiskLifetime)
            {
                cached = (DateTime.UtcNow, new Lazy<List<Dictionary<string, object?>>>(() =>
                    Query("SELECT DeviceId, FriendlyName, Model, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage")));
                _physicalDisks = cached;
            }
            rows = cached.Rows;
        }
        try
        {
            return rows.Value;
        }
        catch (Exception)
        {
            // A failed query is not kept: the next caller asks WMI again.
            lock (PhysicalDiskGate)
                if (_physicalDisks is { } failed && ReferenceEquals(failed.Rows, rows)) _physicalDisks = null;
            throw;
        }
    }

    public static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            return QueryOnce(wql, scope);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.CallCanceled)
        {
            // The WMI service cancels a call now and then (seen while another program queried it at the same time).
            return QueryOnce(wql, scope);
        }
    }

    private static List<Dictionary<string, object?>> QueryOnce(string wql, string scope)
    {
        var rows = new List<Dictionary<string, object?>>();
        // Connecting can hang as well as the query when the WMI service is stuck: both have a time limit.
        var managementScope = new ManagementScope(scope, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(20) });
        using var searcher = new ManagementObjectSearcher(managementScope, new ObjectQuery(wql));
        searcher.Options.Timeout = TimeSpan.FromSeconds(20);
        searcher.Options.Rewindable = false;
        using var results = searcher.Get();
        foreach (var obj in results)
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties) row[p.Name] = p.Value;
                rows.Add(row);
            }
        }
        return rows;
    }

    public static string Str(this Dictionary<string, object?> row, string name) => row.TryGetValue(name, out var v) ? v?.ToString()?.Trim() ?? "" : "";

    public static long? Long(this Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is not null && long.TryParse(v.ToString(), out var l) ? l : null;

    public static int? Int(this Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : null;

    public static bool? Bool(this Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is bool b ? b : null;

    public static int[] IntArray(this Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is Array a ? a.Cast<object>().Select(o => Convert.ToInt32(o)).ToArray() : [];

    /// <summary>
    /// The calendar date of a WMI CIM_DATETIME (yyyymmddHHMMSS.mmmmmmsUUU), for dates such as a BIOS release or driver
    /// date. Taken as written: converting the midnight UTC value to local time would show the day before west of UTC.
    /// </summary>
    public static DateTime? Date(this Dictionary<string, object?> row, string name)
    {
        var s = row.Str(name);
        return s.Length >= 8 && DateTime.TryParseExact(s[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }
}
