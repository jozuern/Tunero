namespace Optimizer.Core.Hardware.Probes;

/// <param name="SystemComponent">Hidden in Settings as a part of Windows or another product (WebView2, runtime parts).</param>
public sealed record InstalledProgram(string Name, string? Version, string? Publisher, bool PerUser, bool SystemComponent = false);

/// <summary>
/// Installed programs for the checks and the app catalog: the Apps page's list (<see cref="Apps.Programs.Read"/>, machine
/// 64/32-bit and the session user's hive, without updates) plus the entries Settings hides as system components, marked,
/// one entry per name. Never uses Win32_Product: querying it triggers MSI self-repair.
/// </summary>
public static class InstalledPrograms
{
    public static IReadOnlyList<InstalledProgram> Read(string? userSid = null) =>
        Apps.Programs.Read(new Platform.SystemRegistryRoots(userSid), systemComponents: true)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.SystemComponent).First())
            .Select(p => new InstalledProgram(p.Name, p.Version, p.Publisher, p.PerUser, p.SystemComponent))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
