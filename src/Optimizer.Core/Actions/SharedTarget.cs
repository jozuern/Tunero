namespace Optimizer.Core.Actions;

/// <summary>
/// Something an action writes, for the conflict check by shared target (<see cref="TweakAction.SharedTargets"/>): two
/// changes that write an overlapping target conflict, because the second one's backup would record the first one's value
/// as the original. Undoing either would then make the other read as reset by Windows, and "Undo all" would depend on
/// the order.
/// </summary>
/// <param name="Key">The target as a whole: one registry value, one NVIDIA driver setting, one power setting of one plan.</param>
/// <param name="Part">
/// One part of it (one side of a power setting, one bit or one token of a value); null for the whole target. Two parts of
/// one target overlap only when they are the same part; the whole target overlaps each of its parts.
/// </param>
/// <param name="IsPrefix">
/// <paramref name="Key"/> stands for every target whose key starts with it: a registry key deleted with everything below
/// it, or a power setting recorded before backups named the plan (it was written on the plan active then).
/// </param>
public readonly record struct SharedTarget(string Key, string? Part = null, bool IsPrefix = false)
{
    public bool Overlaps(SharedTarget other)
    {
        var sameTarget = IsPrefix || other.IsPrefix
            ? (IsPrefix && other.Key.StartsWith(Key, StringComparison.Ordinal)) || (other.IsPrefix && Key.StartsWith(other.Key, StringComparison.Ordinal))
            : string.Equals(Key, other.Key, StringComparison.Ordinal);
        return sameTarget && (Part is null || other.Part is null || string.Equals(Part, other.Part, StringComparison.Ordinal));
    }

    /// <summary>A registry value as a whole: what <see cref="RegistryAction"/> writes, and what the bit and token actions write a part of.</summary>
    public static string RegistryValueKey(Hive hive, string path, string name) => $"reg:{hive}:{path}\\{name}".ToLowerInvariant();

    public override string ToString() => (IsPrefix ? Key + "*" : Key) + (Part is null ? "" : "#" + Part);
}
