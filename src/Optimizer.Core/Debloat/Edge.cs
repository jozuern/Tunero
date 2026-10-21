using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Debloat;

/// <summary>
/// Microsoft Edge, the browser (not the WebView2 Runtime, a program of its own that stays) on this PC, for the Debloat
/// page. <paramref name="NoRemove"/>: its uninstall entry has NoRemove 1, so Settings offers no Uninstall.
/// <paramref name="RegionAllows"/>: what Windows' region policy says for the device region, null when either is unknown.
/// </summary>
public sealed record EdgeState(bool Installed, bool NoRemove, string? Region, bool? RegionAllows)
{
    /// <summary>Windows lets the user remove Edge in Settings: installed, not marked NoRemove, and not ruled out by region.</summary>
    public bool WindowsAllows => Installed && !NoRemove && RegionAllows != false;

    public string? BlockKey => !Installed ? "block.edgeNotInstalled" : WindowsAllows ? null : "block.edgeNotAllowed";
}

/// <summary>
/// Whether Windows lets the user remove Edge. Edge's own uninstaller decides by the device region (the one chosen during
/// setup, logged as "Device region: DE") and by who starts it: started by Tunero, or by cmd.exe from System32, it ends
/// with 532 ("Caller is not under windir", "Uninstall was blocked") even in Germany (PCO-Test, build 26300, Edge 155,
/// 2026-10-21). So Tunero never runs it: the page says whether Windows allows it and opens Installed apps in Settings.
/// The region list is Windows' own, the policy "Edge is uninstallable." in System32\IntegratedServicesRegionPolicySet.json.
/// </summary>
public static class Edge
{
    public const string UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge";
    private const string DeviceRegionKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion";

    /// <summary>The policy "Edge is uninstallable." in Windows' region policy file.</summary>
    public const string UninstallablePolicy = "{1bca278a-5d11-4acf-ad2f-f9ab6d7f93a6}";

    /// <summary>Installed apps in Settings, where Windows offers Uninstall for Edge.</summary>
    public const string InstalledAppsPage = "ms-settings:appsfeatures";

    public static string PolicyFile => Path.Combine(Environment.SystemDirectory, "IntegratedServicesRegionPolicySet.json");

    public static EdgeState Read(IRegistryRoots registry) => Read(registry, ReadPolicy(PolicyFile), Iso2);

    internal static EdgeState Read(IRegistryRoots registry, string? policyJson, Func<int, string?> iso2)
    {
        bool installed = false, noRemove = false;
        string? region = null;
        try
        {
            using (var key = registry.Open(Hive.Machine, UninstallKey, writable: false))
            {
                installed = key?.GetValue("DisplayName") is string { Length: > 0 };
                noRemove = key?.GetValue("NoRemove") is int nr && nr == 1;
            }
            using var geo = registry.Open(Hive.Machine, DeviceRegionKey, writable: false);
            if (geo?.GetValue("DeviceRegion") is int id) region = iso2(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("debloat", $"Edge state not readable: {ex.Message}");
        }
        var allows = region is null || policyJson is null ? null : Allows(policyJson, region);
        return new EdgeState(installed, noRemove, region, allows);
    }

    /// <summary>
    /// What the policy "Edge is uninstallable." says for a two-letter region ("DE"): its default state, changed by the region
    /// lists. Null when the file has no such policy or cannot be read.
    /// </summary>
    public static bool? Allows(string policyJson, string region)
    {
        try
        {
            using var doc = JsonDocument.Parse(policyJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("policies", out var policies) || policies.ValueKind != JsonValueKind.Array) return null;
            foreach (var p in policies.EnumerateArray())
            {
                if (!p.TryGetProperty("guid", out var guid) || !string.Equals(guid.GetString(), UninstallablePolicy, StringComparison.OrdinalIgnoreCase)) continue;
                var state = p.TryGetProperty("defaultState", out var d) && d.GetString() == "enabled";
                if (p.TryGetProperty("conditions", out var c) && c.TryGetProperty("region", out var r))
                {
                    if (Lists(r, "enabled", region)) state = true;
                    if (Lists(r, "disabled", region)) state = false;
                }
                return state;
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Log.Warn("debloat", $"region policy not readable: {ex.Message}");
            return null;
        }
    }

    private static bool Lists(JsonElement region, string name, string code) =>
        region.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
        && list.EnumerateArray().Any(e => string.Equals(e.GetString(), code, StringComparison.OrdinalIgnoreCase));

    private static string? ReadPolicy(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("debloat", $"{path} not readable: {ex.Message}");
            return null;
        }
    }

    /// <summary>The two-letter code of a Windows geographical location id (94 is DE), or null.</summary>
    public static unsafe string? Iso2(int geoId)
    {
        var buffer = stackalloc char[8];
        var n = Interop.Native.GetGeoInfo(geoId, Interop.Native.GeoIso2, buffer, 8, 0);
        return n > 1 ? new string(buffer, 0, n - 1) : null;
    }
}
