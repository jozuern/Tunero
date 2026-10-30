using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Optimizer.Core.Actions;
using Optimizer.Core.Debloat;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Backup;

/// <summary>A start priority rule in a transfer file.</summary>
public sealed record TransferPriority(string Exe, CpuPriority Cpu, bool LowIo)
{
    /// <summary>Low memory priority (PagePriority); missing in files from 1.0.4 and older, which means off.</summary>
    public bool LowMemory { get; init; }
}

/// <summary>A service start type in a transfer file; applied only where this PC's rules for that service allow it.</summary>
public sealed record TransferService(string Name, ServiceStart Start);

/// <summary>A startup entry turned off, by its kind and name; only an entry this PC has is turned off, never deleted.</summary>
public sealed record TransferStartup(StartupKind Kind, string Name);

/// <summary>
/// The changes of one PC as a file, to apply them on another PC or after a reinstall. It holds only catalog tweak ids
/// and start priority rules (program name and class), never actions: a file can only select what this version of the
/// app offers, so a changed or foreign file cannot make the elevated app write anything else. Device tweaks, fixes of
/// findings and NVIDIA profiles belong to one PC and are left out. Three sections are opt-in when exporting: reviewed
/// apps that were removed (by their name in the app list; removing cannot be undone, so the import asks for them on
/// their own), service start types (applied only where the target's rules allow that change) and startup entries that
/// were turned off (by kind and name, only entries the target has). Older versions skip these sections.
/// </summary>
public sealed record TransferFile(
    string Kind,
    int Format,
    string AppVersion,
    string WindowsBuild,
    DateTimeOffset Created,
    IReadOnlyList<string> Tweaks,
    IReadOnlyList<TransferPriority> Priorities)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Apps { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TransferService>? Services { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TransferStartup>? Startup { get; init; }

    [JsonIgnore]
    public int Count => Tweaks.Count + Priorities.Count + (Apps?.Count ?? 0) + (Services?.Count ?? 0) + (Startup?.Count ?? 0);
}

/// <summary>The opt-in sections chosen for an export (null: not chosen).</summary>
public sealed record ExportSections(IReadOnlyList<string>? Apps = null, IReadOnlyList<TransferService>? Services = null, IReadOnlyList<TransferStartup>? Startup = null);

/// <summary>What the target PC has, read only when the file brings a section: its services, startup entries and the reviewed apps it could remove.</summary>
public sealed record TransferTargets(IReadOnlyList<ServiceRow>? Services, IReadOnlyList<StartupEntry>? Startup, IReadOnlyList<DebloatItem>? Apps);

/// <summary>What an import would do on this PC.</summary>
public sealed record ImportPlan(
    IReadOnlyList<TweakDefinition> Together,
    IReadOnlyList<TweakDefinition> OneByOne,
    int AlreadyOn,
    int NotForThisPc,
    int Unknown)
{
    /// <summary>Reviewed apps the file lists as removed that this PC has and may remove: the import asks for them on their own.</summary>
    public IReadOnlyList<DebloatItem> Apps { get; init; } = [];

    public bool IsEmpty => Together.Count == 0 && OneByOne.Count == 0 && Apps.Count == 0;
}

public static partial class ChangeTransfer
{
    public const string FileKind = "pcoptimizer-changes";
    public const int CurrentFormat = 1;
    public const string Extension = ".tunero.json";

    /// <summary>What the import dialog lists: every .json, so files saved before the rename (".pcoptimizer.json") still show.</summary>
    public const string ImportPattern = ".json";

    /// <summary>A transfer file is a few kilobytes; anything larger is not one.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private const int MaxEntries = 2000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,119}$")]
    private static partial Regex IdRegex();

    /// <summary>
    /// A service key name as Windows allows it (CreateService: at most 256 characters, no slash or backslash), so a
    /// third-party name with a space ("Rockstar Service") exports and imports. The name is only compared with the
    /// services this PC has; it never becomes a path.
    /// </summary>
    public static bool IsServiceName(string? name) =>
        name is { Length: > 0 and <= 256 } && !name.Any(c => c is '/' or '\\' || char.IsControl(c));

    /// <summary>
    /// The file for the catalog tweaks this app changed and that are on now (<paramref name="backedUp"/> and
    /// <paramref name="applied"/>), and the start priority rules this app offers. Returns the file and how many changes
    /// were left out because they belong to this PC only.
    /// </summary>
    public static (TransferFile File, int PcSpecific) Create(IEnumerable<string> backedUp, IReadOnlySet<string> applied, TweakCatalog catalog,
        IEnumerable<PriorityRule> priorities, string appVersion, string windowsBuild, ExportSections? sections = null)
    {
        var tweaks = new List<string>();
        var pcSpecific = 0;
        foreach (var id in backedUp.Distinct(StringComparer.Ordinal))
        {
            if (id.StartsWith("priority.", StringComparison.Ordinal)) continue;
            if (catalog.Get(id) is { Hidden: false } && applied.Contains(id)) tweaks.Add(id);
            // Service and startup changes travel in their sections when chosen; everything else run-time stays here.
            else if (catalog.Get(id) is null && !(sections?.Services is not null && id.StartsWith("service.start.", StringComparison.Ordinal))
                     && !(sections?.Startup is not null && id.StartsWith("startup.off.", StringComparison.Ordinal))) pcSpecific++;
        }
        var rules = priorities.Where(r => r.Cpu is not null && ProgramPriority.IsValidExe(r.Exe))
            .Select(r => new TransferPriority(r.Exe, r.Cpu!.Value, r.LowIo) { LowMemory = r.LowMemory }).ToList();
        tweaks.Sort(StringComparer.Ordinal);
        return (new TransferFile(FileKind, CurrentFormat, appVersion, windowsBuild, DateTimeOffset.Now, tweaks, rules)
        {
            Apps = sections?.Apps?.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Services = sections?.Services?.DistinctBy(s => s.Name.ToLowerInvariant()).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Startup = sections?.Startup?.Distinct().OrderBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        }, pcSpecific);
    }

    /// <summary>The removed apps section: the app list's names of the removed packages (only reviewed apps).</summary>
    public static IReadOnlyList<string> AppSection(IEnumerable<RemovedApp> removed, AppxCatalog catalog) =>
        removed.SelectMany(r => catalog.Apps.Where(e => e.Matches(r.Name)).Select(e => e.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The services section: the start types this app set and that are in place now (<paramref name="applied"/>).</summary>
    public static IReadOnlyList<TransferService> ServiceSection(IEnumerable<TweakDefinition> applied) =>
        applied.Where(t => t.Id.StartsWith("service.start.", StringComparison.Ordinal))
            .SelectMany(t => t.Actions.OfType<ServiceAction>()).Where(a => IsServiceName(a.Name)).Select(a => new TransferService(a.Name, a.StartType)).ToList();

    /// <summary>The startup section: the entries this app turned off and that are still off (<paramref name="applied"/>).</summary>
    public static IReadOnlyList<TransferStartup> StartupSection(IEnumerable<TweakDefinition> applied) =>
        applied.Where(t => t.Id.StartsWith("startup.off.", StringComparison.Ordinal) && t.Subject is { Length: > 0 })
            .Select(t => Enum.TryParse<StartupKind>(t.Id.Split('.')[2], ignoreCase: true, out var kind) ? new TransferStartup(kind, t.Subject!) : null)
            .OfType<TransferStartup>().ToList();

    public static string Serialize(TransferFile file) => JsonSerializer.Serialize(file, Json);

    /// <summary>
    /// Reads a transfer file the user picked (the window's Import, <c>--apply</c>): null when it is larger than
    /// <see cref="MaxFileBytes"/> or not a valid one. The size is checked on the same stream that is read, so a link to a
    /// larger file cannot get past it.
    /// </summary>
    public static async Task<TransferFile?> ReadFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (stream.Length > MaxFileBytes) return null;
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return Parse(await reader.ReadToEndAsync());
    }

    /// <summary>Reads a transfer file; null when it is not one, is from a newer format, or holds anything invalid.</summary>
    public static TransferFile? Parse(string text)
    {
        if (text.Length > MaxFileBytes) return null;
        TransferFile? file;
        try
        {
            file = JsonSerializer.Deserialize<TransferFile>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (file is null || file.Kind != FileKind || file.Format is < 1 or > CurrentFormat) return null;
        if (file.Tweaks is null || file.Priorities is null || file.Count > MaxEntries) return null;
        if (file.Tweaks.Any(id => id is null || !IdRegex().IsMatch(id))) return null;
        if (file.Apps?.Any(a => a is null || !IdRegex().IsMatch(a)) == true) return null;
        if (file.Services?.Any(s => s is null || !IsServiceName(s.Name) || !Enum.IsDefined(s.Start)) == true) return null;
        if (file.Startup?.Any(s => s is null || !Enum.IsDefined(s.Kind) || s.Name is not { Length: > 0 and <= 260 } || s.Name.Any(char.IsControl)) == true) return null;
        if (file.Priorities.Any(p => p is null || p.Exe is null || !ProgramPriority.IsValidExe(p.Exe) || !Enum.IsDefined(p.Cpu))) return null;
        return file;
    }

    /// <summary>
    /// Sorts the file's changes for this PC: what can be applied together after one confirmation (the same rules as
    /// Apply recommended), what needs its own confirmation (previews, Expert, anti-cheat sensitive, boot-critical), and
    /// what is skipped because it is on already, does not apply here or is unknown to this version.
    /// </summary>
    /// <param name="targets">What this PC has for the file's sections; without it the sections count as not for this PC.</param>
    public static ImportPlan Plan(TransferFile file, TweakCatalog catalog, Facts facts, IReadOnlySet<string> applied, TransferTargets? targets = null)
    {
        var together = new List<TweakDefinition>();
        var oneByOne = new List<TweakDefinition>();
        int on = 0, notHere = 0, unknown = 0;
        void Add(TweakDefinition t)
        {
            if (applied.Contains(t.Id)) on++;
            else if (!TweakEngine.AppliesTo(t, facts)) notHere++;
            else (t.IsBatchSafe ? together : oneByOne).Add(t);
        }
        foreach (var id in file.Tweaks.Distinct(StringComparer.Ordinal))
        {
            if (catalog.Get(id) is { Hidden: false } t) Add(t);
            else unknown++;
        }
        foreach (var p in file.Priorities.DistinctBy(p => p.Exe.ToLowerInvariant()))
            Add(ProgramPriority.Tweak(p.Exe, p.Cpu, p.LowIo, p.LowMemory));
        // Services: only a change this PC's rules allow for the service (ServiceManager.Change), else it is not for this PC.
        foreach (var s in file.Services ?? [])
        {
            var row = targets?.Services?.FirstOrDefault(r => string.Equals(ServiceManager.BaseName(r.Name), s.Name, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(r.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null) notHere++;
            else if (row.Start == s.Start) on++;
            else if (ServiceManager.Change(row, s.Start) is { } t) (t.IsBatchSafe ? together : oneByOne).Add(t);
            else notHere++;
        }
        // Startup entries: turned off where this PC has the same entry; never deleted. A service follows the Startup
        // page's rule: not one of Windows' own that the service catalog keeps read-only (Dhcp, the firewall).
        foreach (var s in file.Startup ?? [])
        {
            var entry = targets?.Startup?.FirstOrDefault(e => e.Kind == s.Kind && string.Equals(e.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) notHere++;
            else if (entry.Kind == StartupKind.Service
                     && targets?.Services?.FirstOrDefault(r => string.Equals(r.Name, entry.Target, StringComparison.OrdinalIgnoreCase)) is not { Edit: not ServiceEdit.ReadOnly }) notHere++;
            else if (entry.Enabled == false) on++;
            else if (StartupTweaks.Set(entry, enabled: false) is { } t) (t.IsBatchSafe ? together : oneByOne).Add(t);
            else notHere++;
        }
        // Apps: reviewed ones this PC has and may remove (no guard against it); the rest is gone already or not offered here.
        var wanted = (file.Apps ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apps = targets?.Apps?.Where(i => i.BlockKey is null && wanted.Contains(i.Entry.Name)).ToList() ?? [];
        notHere += wanted.Count(name => !apps.Any(a => string.Equals(a.Entry.Name, name, StringComparison.OrdinalIgnoreCase)));
        return new ImportPlan(together, oneByOne, on, notHere, unknown) { Apps = apps };
    }
}
