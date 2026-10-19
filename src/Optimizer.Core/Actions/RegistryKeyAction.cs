using System.Text.Json;

namespace Optimizer.Core.Actions;

/// <summary>
/// Deletes a whole registry key with its values and subkeys (a context menu handler, a shell folder, a policy key)
/// after saving all of it in the backup: undo writes the key back as it was, every value with its type. A key larger
/// than <see cref="MaxValues"/> values or deeper than <see cref="MaxDepth"/> levels is not offered (unsupported), so a
/// backup never holds half a key. The key's permissions are not part of the backup; restored keys inherit their parent's.
/// </summary>
public sealed class RegistryKeyAction : TweakAction
{
    public const int MaxValues = 2000;
    public const int MaxDepth = 8;

    public Hive Hive { get; init; } = Hive.Machine;
    public string Path { get; init; } = "";

    public override string TargetKey => $"regkey:{Hive}:{Path}".ToLowerInvariant();

    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.registryKey", $"{c.Registry.DisplayRoot(Hive)}\\{Path}");

    public override StoredValue Desired(ActionContext c) => StoredValue.Missing;

    /// <summary>The key as JSON (values with type, subkeys), missing when there is none; null when it is too large to save.</summary>
    public override StoredValue? Read(ActionContext c)
    {
        var budget = MaxValues;
        var snapshot = Snapshot(c.Registry, Path, 0, ref budget);
        if (snapshot is null) return KeyExists(c) ? null : StoredValue.Missing;
        return new StoredValue(true, "regkey", JsonSerializer.Serialize(snapshot));
    }

    public override void Apply(ActionContext c) => DeleteTree(c);

    /// <summary>Still applied only while the key is gone: a key Windows or a program made again is left alone by undo.</summary>
    public override bool IsStillApplied(ActionContext c, StoredValue applied) => !KeyExists(c);

    public override void Restore(ActionContext c, StoredValue original)
    {
        // Undo runs only while the key is still gone (a key Windows made again is left alone, see IsStillApplied); a
        // remainder of an undo that stopped half-way is cleared first, so the saved key comes back exactly.
        DeleteTree(c);
        if (!original.Existed || original.Data is null) return;
        var snapshot = JsonSerializer.Deserialize<KeySnapshot>(original.Data) ?? throw new InvalidOperationException($"{TargetKey}: the saved key is unreadable");
        Write(c.Registry, Path, snapshot);
    }

    private bool KeyExists(ActionContext c)
    {
        using var key = c.Registry.Open(Hive, Path, writable: false);
        return key is not null;
    }

    private void DeleteTree(ActionContext c)
    {
        var cut = Path.TrimEnd('\\').LastIndexOf('\\');
        if (cut <= 0) throw new InvalidOperationException($"{TargetKey}: a root-level key is never deleted");
        using var parent = c.Registry.Open(Hive, Path[..cut], writable: true);
        parent?.DeleteSubKeyTree(Path[(cut + 1)..].TrimEnd('\\'), throwOnMissingSubKey: false);
    }

    /// <summary>The key and everything below it; null when it does not exist or is over the limits.</summary>
    private KeySnapshot? Snapshot(IRegistryRoots roots, string path, int depth, ref int budget)
    {
        if (depth > MaxDepth) return null;
        string[] names, subkeys;
        using (var key = roots.Open(Hive, path, writable: false))
        {
            if (key is null) return null;
            names = key.GetValueNames();
            subkeys = key.GetSubKeyNames();
        }
        budget -= names.Length;
        if (budget < 0) return null;
        var snapshot = new KeySnapshot();
        foreach (var name in names)
        {
            var value = RegistryValue.Read(roots, Hive, path, name);
            if (value.Existed) snapshot.Values[name] = new SavedValue(value.Kind ?? "string", value.Data ?? "");
        }
        foreach (var sub in subkeys)
        {
            if (Snapshot(roots, $"{path}\\{sub}", depth + 1, ref budget) is not { } child) return null;
            snapshot.Keys[sub] = child;
        }
        return snapshot;
    }

    private void Write(IRegistryRoots roots, string path, KeySnapshot snapshot)
    {
        using (roots.Open(Hive, path, writable: true, create: true)) { }
        foreach (var (name, value) in snapshot.Values) RegistryValue.Write(roots, Hive, path, name, value.Kind, value.Data);
        foreach (var (sub, child) in snapshot.Keys) Write(roots, $"{path}\\{sub}", child);
    }

    /// <summary>A saved key as the confirmation shows it: "@regkey:values:subkeys" (Labels.Display words it), not the JSON.</summary>
    public static string Summary(string? json)
    {
        if (json is null) return "";
        try
        {
            var snapshot = JsonSerializer.Deserialize<KeySnapshot>(json);
            if (snapshot is null) return "";
            var (values, keys) = Count(snapshot);
            return $"@regkey:{values}:{keys}";
        }
        catch (JsonException)
        {
            return "";
        }

        static (int Values, int Keys) Count(KeySnapshot s) =>
            s.Keys.Values.Select(Count).Aggregate((s.Values.Count, s.Keys.Count), (sum, c) => (sum.Item1 + c.Values, sum.Item2 + c.Keys));
    }

    public sealed class KeySnapshot
    {
        public Dictionary<string, SavedValue> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, KeySnapshot> Keys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record SavedValue(string Kind, string Data);
}
