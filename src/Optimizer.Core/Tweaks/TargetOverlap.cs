using System.Runtime.CompilerServices;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Tweaks;

/// <summary>
/// The conflict check by shared target. Two tweaks that write the same target (one NVIDIA driver setting, one registry
/// value, one side of a power setting) conflict whether or not the catalog says so: the second one's backup would record
/// the first one's value as the original, so undoing either makes the other read as reset by Windows, and "Undo all"
/// depends on the order. Declared conflicts (<see cref="TweakDefinition.ConflictsWith"/>) count too, in both directions.
/// </summary>
public static class TargetOverlap
{
    private static readonly ConditionalWeakTable<TweakDefinition, IReadOnlyList<SharedTarget>> Cache = [];

    /// <summary>What an action writes; nothing when it cannot say (a power setting alias this version does not know).</summary>
    public static IReadOnlyList<SharedTarget> Of(TweakAction a)
    {
        try
        {
            return a.SharedTargets().ToList();
        }
        catch (Exception ex)
        {
            Log.Debug("engine", $"targets of {a.GetType().Name} unknown: {ex.Message}");
            return [];
        }
    }

    /// <summary>What the tweak writes by its actions as the catalog has them (templates not expanded), kept per definition.</summary>
    public static IReadOnlyList<SharedTarget> Of(TweakDefinition t) => Cache.GetValue(t, d => d.Actions.SelectMany(Of).Distinct().ToList());

    public static bool Overlap(IEnumerable<SharedTarget> a, IReadOnlyList<SharedTarget> b) => a.Any(x => b.Any(x.Overlaps));

    /// <summary>
    /// Whether two tweaks write a shared target, by their catalog actions: for a plan or a choice made before anything is
    /// applied. The engine compares the actions expanded for this PC with what the backups recorded.
    /// </summary>
    public static bool SharesTarget(TweakDefinition a, TweakDefinition b) =>
        !string.Equals(a.Id, b.Id, StringComparison.Ordinal) && Overlap(Of(a), Of(b));

    /// <summary>A declared conflict (either way) or a shared target.</summary>
    public static bool Conflict(TweakDefinition a, TweakDefinition b) =>
        a.ConflictsWith.Contains(b.Id) || b.ConflictsWith.Contains(a.Id) || SharesTarget(a, b);
}

/// <summary>One target that a change this app made (and has not undone) wrote: the backup and its entry.</summary>
/// <param name="Action">The action that wrote it (stored with the entry); null when the entry has none or it is unreadable.</param>
internal sealed record TargetWriter(TweakBackup Backup, BackupEntry Entry, TweakAction? Action, SharedTarget Target);

/// <summary>Every target the backups of this account hold, for the conflict check by shared target.</summary>
internal sealed class TargetIndex
{
    // By whole target; the prefix targets (a registry key with everything below it) are few and compared one by one.
    private readonly Dictionary<string, List<TargetWriter>> _byKey = new(StringComparer.Ordinal);
    private readonly List<TargetWriter> _prefixes = [];
    private readonly List<TargetWriter> _all = [];

    /// <summary>Declared conflicts of the changes in the backups: tweak id, the ids it names.</summary>
    private readonly List<(TweakBackup Backup, IReadOnlyList<string> ConflictsWith)> _declared = [];

    public TargetIndex(IEnumerable<TweakBackup> backups, Func<TweakBackup, TweakDefinition?> resolve, Func<BackupEntry, TweakAction?> storedAction)
    {
        foreach (var backup in backups)
        {
            if (resolve(backup) is { ConflictsWith.Count: > 0 } definition) _declared.Add((backup, definition.ConflictsWith));
            foreach (var entry in backup.Entries)
            {
                var action = storedAction(entry);
                // Without its action (backups of older versions) the entry's key stands for the whole target.
                var targets = action is null ? [new SharedTarget(entry.TargetKey)] : TargetOverlap.Of(action);
                foreach (var target in targets.Distinct())
                {
                    var writer = new TargetWriter(backup, entry, action, target);
                    _all.Add(writer);
                    if (target.IsPrefix) _prefixes.Add(writer);
                    else if (_byKey.TryGetValue(target.Key, out var list)) list.Add(writer);
                    else _byKey[target.Key] = [writer];
                }
            }
        }
    }

    public bool IsEmpty => _all.Count == 0 && _declared.Count == 0;

    /// <summary>The targets of other tweaks than <paramref name="tweakId"/> that overlap <paramref name="target"/>.</summary>
    public IEnumerable<TargetWriter> Writers(SharedTarget target, string tweakId)
    {
        IEnumerable<TargetWriter> candidates = target.IsPrefix ? _all : (_byKey.GetValueOrDefault(target.Key) ?? []).Concat(_prefixes);
        return candidates.Where(w => !string.Equals(w.Backup.TweakId, tweakId, StringComparison.Ordinal) && w.Target.Overlaps(target));
    }

    /// <summary>The backups of other tweaks that declare a conflict with <paramref name="tweakId"/>.</summary>
    public IEnumerable<TweakBackup> DeclaringConflictWith(string tweakId) =>
        _declared.Where(d => !string.Equals(d.Backup.TweakId, tweakId, StringComparison.Ordinal) && d.ConflictsWith.Contains(tweakId)).Select(d => d.Backup);
}
