using System.Text.Json;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Startup;

/// <summary>One saved autostart entry: enough to recognize it again (location key and command).</summary>
public sealed record SnapshotEntry(StartupKind Kind, string Name, string? Command, string Location, string Key);

/// <summary>The saved list and when it was taken.</summary>
public sealed record StartupSnapshotData(DateTimeOffset TakenAt, IReadOnlyList<SnapshotEntry> Entries);

/// <summary>Entries added or changed since the snapshot, and entries that are gone.</summary>
public sealed record SnapshotDiff(DateTimeOffset TakenAt, IReadOnlySet<string> NewKeys, IReadOnlyList<SnapshotEntry> Removed);

/// <summary>
/// Saves the autostart list and compares a later scan with it ("what did that installer add?"). An entry counts as
/// new when its location key is new or its command changed. The file lives in the protected data folder.
/// </summary>
public static class StartupSnapshot
{
    public static string DefaultFile => DataPaths.StartupSnapshot;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static StartupSnapshotData Take(IEnumerable<StartupEntry> entries, DateTimeOffset now) =>
        new(now, entries.Select(e => new SnapshotEntry(e.Kind, e.Name, e.Command, e.Location, e.Key)).ToList());

    public static void Save(string file, StartupSnapshotData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Platform.DurableFile.WriteAllText(file, JsonSerializer.Serialize(data, Json));
    }

    /// <summary>Null when there is no snapshot or it cannot be read (damaged, locked, no access).</summary>
    public static StartupSnapshotData? Load(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<StartupSnapshotData>(File.ReadAllText(file), Json);
            // An edited file can have "Entries": null.
            return data is null ? null : data with { Entries = data.Entries ?? [] };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Logging.Log.Warn("startup", $"snapshot {file} not readable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The key and the command. A command that is one quoted path counts as the same path unquoted: Startup folder files
    /// and shortcuts without arguments were stored unquoted by older versions, so an older snapshot does not call them changed.
    /// </summary>
    private static string Identity(string key, string? command)
    {
        var c = command?.Trim() ?? "";
        if (c.Length >= 2 && c[0] == '"' && c.IndexOf('"', 1) == c.Length - 1) c = c[1..^1];
        return key + "\n" + c;
    }

    public static SnapshotDiff Compare(StartupSnapshotData snapshot, IEnumerable<StartupEntry> current)
    {
        var before = snapshot.Entries.Select(e => Identity(e.Key, e.Command)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = current.ToList();
        var now = list.Select(e => Identity(e.Key, e.Command)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = list.Where(e => !before.Contains(Identity(e.Key, e.Command))).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = snapshot.Entries.Where(e => !now.Contains(Identity(e.Key, e.Command))).ToList();
        return new SnapshotDiff(snapshot.TakenAt, added, removed);
    }
}
