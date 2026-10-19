using Optimizer.Core.Tools;

namespace Optimizer.Core.Activity;

/// <summary>What processes are grouped by: "path:&lt;full path&gt;", "svchost:&lt;pid&gt;" or "name:&lt;image&gt;" (lowercase).</summary>
public sealed record ProgramKey(string Value);

/// <param name="Services">The services a svchost process hosts (display names); empty for other programs.</param>
public sealed record ProgramIdentity(ProgramKey Key, string Name, string? Path, string ImageName, IReadOnlyList<string> Services);

public interface IProgramNames
{
    ProgramIdentity Identify(ProcessSample p);

    /// <summary>Called once per tick before <see cref="Identify"/> with the processes that run now.</summary>
    void Refresh(IReadOnlyList<ProcessSample> alive) { }
}

/// <summary>
/// Groups processes into programs: the processes of one exe file are one program (all Chrome processes); each svchost is
/// its own program named after its services, because merging them would hide which service is busy; a process whose path
/// cannot be read (protected ones like the Defender engine) is grouped by its image name. Cached per process instance.
/// </summary>
/// <param name="serviceHostName">Formats a svchost's name from its service names (the app's localized text).</param>
public sealed class ProgramNames(Func<int, string?> imagePath, Func<string, string?> fileDescription,
    Func<IReadOnlyDictionary<int, IReadOnlyList<string>>> serviceNamesByPid, Func<IReadOnlyList<string>, string> serviceHostName) : IProgramNames
{
    private readonly Dictionary<(int Pid, long Create), ProgramIdentity> _cache = [];
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _services = new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>
    /// Reads the service list again, forgets svchost names (they follow the services) and processes that ended. An ended
    /// process keeps its identity in the model, which holds it while the program is shown as ended.
    /// </summary>
    public void Refresh(IReadOnlyList<ProcessSample> alive)
    {
        _services = serviceNamesByPid();
        var keep = alive.Select(p => (p.Pid, p.CreateTime)).ToHashSet();
        foreach (var key in _cache.Where(c => !keep.Contains(c.Key) || c.Value.Key.Value.StartsWith("svchost:", StringComparison.Ordinal)).Select(c => c.Key).ToList())
            _cache.Remove(key);
    }

    public ProgramIdentity Identify(ProcessSample p)
    {
        if (_cache.TryGetValue((p.Pid, p.CreateTime), out var known)) return known;
        var identity = Build(p);
        _cache[(p.Pid, p.CreateTime)] = identity;
        return identity;
    }

    private ProgramIdentity Build(ProcessSample p)
    {
        var bare = p.ImageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.ImageName[..^4] : p.ImageName;
        var path = imagePath(p.Pid);
        if (string.Equals(p.ImageName, "svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var services = _services.TryGetValue(p.Pid, out var s) ? s : [];
            return new ProgramIdentity(new ProgramKey($"svchost:{p.Pid}"), services.Count > 0 ? serviceHostName(services) : bare, path, p.ImageName, services);
        }
        if (path is null) return new ProgramIdentity(new ProgramKey("name:" + p.ImageName.ToLowerInvariant()), bare, null, p.ImageName, []);
        if (!_descriptions.TryGetValue(path, out var description)) _descriptions[path] = description = fileDescription(path);
        return new ProgramIdentity(new ProgramKey("path:" + path.ToLowerInvariant()),
            string.IsNullOrWhiteSpace(description) ? bare : description.Trim(), path, p.ImageName, []);
    }
}

/// <summary>What the Live readings program list allows for a process.</summary>
public static class ProgramRules
{
    /// <summary>Processes Windows needs: ending one crashes Windows, signs you out or breaks the display.</summary>
    private static readonly HashSet<string> WindowsCore = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "dwm.exe",
        "Memory Compression", "Secure System",
    };

    // Each note says what the process is, as Microsoft describes it:
    // MsMpEng.exe: https://learn.microsoft.com/defender-endpoint/troubleshoot-performance-issues
    // SearchIndexer.exe: https://learn.microsoft.com/windows/win32/search/-search-indexing-process-overview
    // TiWorker.exe / TrustedInstaller.exe: https://learn.microsoft.com/windows/deployment/update/how-windows-update-works
    // Memory Compression: https://learn.microsoft.com/windows/whats-new/ (memory manager compression store, Windows 10)
    // System: https://learn.microsoft.com/windows-hardware/drivers/kernel/ (kernel and drivers run in the System process)
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng.exe"] = "Activity_Note_MsMpEng",
        ["SearchIndexer.exe"] = "Activity_Note_SearchIndexer",
        ["TiWorker.exe"] = "Activity_Note_TiWorker",
        ["TrustedInstaller.exe"] = "Activity_Note_TiWorker",
        ["Memory Compression"] = "Activity_Note_MemoryCompression",
        ["System"] = "Activity_Note_System",
    };

    /// <summary>A Strings key saying why End task is not offered, or null when it is.</summary>
    public static string? EndRefusalKey(string imageName, bool critical, int pid, int ownPid) =>
        pid == ownPid ? "Activity_EndSelf"
        : critical || WindowsCore.Contains(imageName) ? "Activity_EndCritical"
        : null;

    public static string? WindowsNoteKey(string imageName) => Notes.GetValueOrDefault(imageName);

    public static bool CanAddPriorityRule(string imageName, bool critical) =>
        !critical && !WindowsCore.Contains(imageName) && ProgramPriority.IsValidExe(imageName);
}
