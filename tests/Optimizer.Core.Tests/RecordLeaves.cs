using System.Collections;
using System.Reflection;

namespace Optimizer.TestSupport;

/// <summary>
/// For the completeness guards of the install media choices (one option lives in many hand-kept places): walks every
/// settable property of a record and of the records inside it, and makes copies of the root with one leaf changed. A new
/// property is found without anyone listing it, so a guard that checks every leaf fails for the one a place forgot.
/// <para>
/// Changes: a bool flipped, an enum to its next value, a number plus one, a text with "x" added, a list of names with
/// "Extra.Name" added, another address or byte array. An override (by path) gives its own value; an override on the path
/// of a record changes that record as a whole instead of its leaves. Get-only properties are left out: they follow from
/// the others. A record that is null in the root has nothing to change and comes back with a null copy.
/// </para>
/// Linked into Optimizer.App.Tests as well.
/// </summary>
internal static class RecordLeaves
{
    public const string ExtraName = "Extra.Name";

    /// <summary>Every leaf of <paramref name="root"/>: its path ("Answers.SkipSetup") and the root with only that leaf changed (null: a null record).</summary>
    public static IReadOnlyList<(string Path, T? Changed)> Of<T>(T root, IReadOnlyDictionary<string, Func<object?, object?>>? overrides = null) where T : class =>
        Walk(root, "", overrides ?? new Dictionary<string, Func<object?, object?>>())
            .Select(leaf => (leaf.Path, leaf.Make is { } make ? (T)make() : null))
            .ToList();

    /// <summary>The value at <paramref name="path"/> in <paramref name="root"/>, or null when a record on the way is null.</summary>
    public static object? Get(object? root, string path)
    {
        foreach (var name in path.Split('.'))
        {
            if (root is null) return null;
            root = root.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(root);
        }
        return root;
    }

    /// <summary>Equal values, lists by their items in order.</summary>
    public static bool Same(object? a, object? b) =>
        a is IEnumerable x and not string && b is IEnumerable y and not string
            ? x.Cast<object?>().SequenceEqual(y.Cast<object?>())
            : Equals(a, b);

    /// <summary>A path is a record's when other paths lie inside it (a record that is null in one root and set in another).</summary>
    public static bool IsInside(string path, IEnumerable<string> paths) => paths.Any(p => p.StartsWith(path + ".", StringComparison.Ordinal));

    private static IEnumerable<(string Path, Func<object>? Make)> Walk(object obj, string prefix, IReadOnlyDictionary<string, Func<object?, object?>> overrides)
    {
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0))
        {
            var path = prefix + p.Name;
            var value = p.GetValue(obj);
            if (overrides.TryGetValue(path, out var change))
            {
                var changed = change(value);
                yield return (path, () => With(obj, p, changed));
            }
            else if (IsRecord(p.PropertyType))
            {
                if (value is null)
                {
                    yield return (path, null);
                    continue;
                }
                foreach (var (inner, make) in Walk(value, path + ".", overrides))
                    yield return (inner, make is null ? null : () => With(obj, p, make()));
            }
            else
            {
                var changed = Change(value, p.PropertyType, path);
                yield return (path, () => With(obj, p, changed));
            }
        }
    }

    private static bool IsRecord(Type t) => t.GetMethod("<Clone>$") is not null;

    /// <summary>A copy of the record <paramref name="obj"/> (its own Clone, as "with" uses) with <paramref name="p"/> set.</summary>
    private static object With(object obj, PropertyInfo p, object? value)
    {
        var copy = obj.GetType().GetMethod("<Clone>$")!.Invoke(obj, null)!;
        p.SetValue(copy, value);
        return copy;
    }

    private static object? Change(object? value, Type type, string path)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool)) return !(value is true);
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            var index = value is null ? -1 : Array.IndexOf(values, value);
            return values.GetValue((index + 1) % values.Length);
        }
        if (t == typeof(int)) return (value is int i ? i : 0) + 1;
        if (t == typeof(long)) return (value is long l ? l : 0) + 1;
        if (t == typeof(string)) return (value as string ?? "") + "x";
        if (t == typeof(Uri)) return new Uri((value as Uri)?.AbsoluteUri is { } uri ? uri + "x" : "http://example.invalid/changed");
        if (t == typeof(byte[])) return (value as byte[] ?? []).Append((byte)0xA5).ToArray();
        if (t.IsAssignableFrom(typeof(List<string>))) return (value as IEnumerable<string> ?? []).Append(ExtraName).ToList();
        throw new InvalidOperationException($"{path}: no change for a {t.Name}; give one in the test's overrides");
    }
}
