using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Optimizer.Core.Actions;

/// <summary>Sets or deletes one registry value. Hive "user" writes to the session user's hive, never the elevated account's.</summary>
public sealed class RegistryAction : TweakAction
{
    public Hive Hive { get; init; } = Hive.Machine;
    public string Path { get; init; } = "";

    /// <summary>A path with "{nic}" stands for the same value under every active network interface.</summary>
    public override IEnumerable<TweakAction> Expand(ExpansionContext x) =>
        !Path.Contains("{nic}", StringComparison.Ordinal) ? [this]
        : x.NetworkInterfaces.Select(id => new RegistryAction
        {
            Hive = Hive, Path = Path.Replace("{nic}", id), Name = Name, Kind = Kind, Value = Value, Delete = Delete,
            Notify = Notify, MissingMeans = MissingMeans, RemoveKeyOnUndo = RemoveKeyOnUndo?.Replace("{nic}", id),
        });

    /// <summary>Value name; empty = the key's default value.</summary>
    public string Name { get; init; } = "";

    /// <summary>dword, qword, string, expandString, multiString, binary.</summary>
    public string Kind { get; init; } = "dword";

    /// <summary>
    /// Number, string, hex string (binary) or string array (multiString). A delete action has none: the empty element is
    /// left out when the action is stored with a backup (it cannot be written as JSON).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Value { get; init; }

    /// <summary>true = the tweak removes the value.</summary>
    public bool Delete { get; init; }

    /// <summary>Optional live refresh after writing: "mouse", "settings".</summary>
    public string? Notify { get; init; }

    /// <summary>What Windows assumes when the value is absent (e.g. Game Mode is on by default). Used for state only.</summary>
    public JsonElement? MissingMeans { get; init; }

    /// <summary>
    /// For tweaks where the key's existence matters: on undo, when the value did not exist before, the keys from
    /// <see cref="Path"/> up to this key (relative to the hive) are removed, each only if it is empty by then. Keys that
    /// held other values or subkeys before the change stay.
    /// </summary>
    public string? RemoveKeyOnUndo { get; init; }

    /// <summary>The same value in another hive (the Default profile for accounts created later); no live refresh there.</summary>
    public RegistryAction ForHive(Hive hive) => new()
    {
        Hive = hive, Path = Path, Name = Name, Kind = Kind, Value = Value, Delete = Delete, MissingMeans = MissingMeans, RemoveKeyOnUndo = RemoveKeyOnUndo,
    };

    public override ActionState State(ActionContext c)
    {
        using var hold = c.Registry.Hold(Hive);
        var current = Read(c);
        if (current is null) return ActionState.Unsupported;
        if (!current.Existed && MissingMeans is { } m && !Delete && Serialize(Kind, m) == Serialize(Kind, Value)) return ActionState.Applied;
        return current.SameAs(Desired(c)) ? ActionState.Applied : ActionState.NotApplied;
    }

    public override string TargetKey => $"reg:{Hive}:{Path}\\{Name}".ToLowerInvariant();

    public override TargetText TargetLabel(ActionContext c) => TargetText.Verbatim($"{c.Registry.DisplayRoot(Hive)}\\{Path}\\{(Name.Length == 0 ? "(Default)" : Name)}");

    /// <summary>
    /// Also still applied when Windows removed the value and its absence means what was written (Game Mode is on by
    /// default): the setting did not change, so undo still brings back the original.
    /// </summary>
    public override bool IsStillApplied(ActionContext c, StoredValue applied)
    {
        using var hold = c.Registry.Hold(Hive);
        if (Read(c) is not { } current || current.SameAs(applied)) return true;
        return !current.Existed && !Delete && MissingMeans is { } m && Serialize(Kind, m) == Serialize(Kind, Value) && applied.SameAs(Desired(c));
    }

    public override StoredValue Desired(ActionContext c) => Delete ? StoredValue.Missing : new StoredValue(true, Kind, Serialize(Kind, Value));

    public override StoredValue? Read(ActionContext c)
    {
        using var hold = c.Registry.Hold(Hive);
        return RegistryValue.Read(c.Registry, Hive, Path, Name);
    }

    public override void Apply(ActionContext c)
    {
        using var hold = c.Registry.Hold(Hive);
        if (Delete) RegistryValue.Delete(c.Registry, Hive, Path, Name);
        else RegistryValue.Write(c.Registry, Hive, Path, Name, Kind, Serialize(Kind, Value));
        if (Notify is not null) c.Notify?.Invoke(Notify);
    }

    public override void Restore(ActionContext c, StoredValue original)
    {
        using var hold = c.Registry.Hold(Hive);
        if (!original.Existed)
        {
            RegistryValue.Delete(c.Registry, Hive, Path, Name);
            // Keys the apply created (recorded with the original) go again when they are empty; a key that existed
            // before, even empty, stays. Backups from older versions know only RemoveKeyOnUndo.
            if (RegistryValue.CreatedKeys(original) is { } created)
            {
                if (created > 0) RegistryValue.DeleteEmptyKeys(c.Registry, Hive, Path, RegistryValue.Ancestor(Path, created - 1));
            }
            else if (RemoveKeyOnUndo is { Length: > 0 } key)
            {
                RegistryValue.DeleteEmptyKeys(c.Registry, Hive, Path, key);
            }
        }
        else
        {
            RegistryValue.Write(c.Registry, Hive, Path, Name, original.Kind ?? Kind, original.Data ?? "");
        }
        if (Notify is not null) c.Notify?.Invoke(Notify);
    }

    internal static string Serialize(string kind, JsonElement value) => kind switch
    {
        "multiString" when value.ValueKind == JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(v => v.GetString())),
        "dword" when value.ValueKind == JsonValueKind.Number => value.GetUInt32().ToString(CultureInfo.InvariantCulture),
        "qword" when value.ValueKind == JsonValueKind.Number => value.GetUInt64().ToString(CultureInfo.InvariantCulture),
        "dword" or "qword" when value.ValueKind == JsonValueKind.String => ParseNumber(value.GetString()!).ToString(CultureInfo.InvariantCulture),
        _ => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
    };

    private static ulong ParseNumber(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(s[2..], 16) : ulong.Parse(s, CultureInfo.InvariantCulture);
}

/// <summary>Sets and/or clears bits in a DWORD (e.g. Tcpip6 DisabledComponents shared by several tweaks).</summary>
public sealed class RegistryBitsAction : TweakAction
{
    public Hive Hive { get; init; } = Hive.Machine;
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public uint Set { get; init; }
    public uint Clear { get; init; }

    public override string TargetKey => $"regbits:{Hive}:{Path}\\{Name}:{Set:X}:{Clear:X}".ToLowerInvariant();

    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.registryBits", $"{c.Registry.DisplayRoot(Hive)}\\{Path}\\{Name}");

    private uint Current(ActionContext c) =>
        RegistryValue.Read(c.Registry, Hive, Path, Name) is { Existed: true, Data: { } d } && uint.TryParse(d, out var v) ? v : 0;

    public override StoredValue Desired(ActionContext c) =>
        new(true, "dword", ((Current(c) | Set) & ~Clear).ToString(CultureInfo.InvariantCulture));

    public override StoredValue? Read(ActionContext c) => RegistryValue.Read(c.Registry, Hive, Path, Name);

    public override ActionState State(ActionContext c)
    {
        var v = Current(c);
        return (v & Set) == Set && (v & Clear) == 0 ? ActionState.Applied : ActionState.NotApplied;
    }

    public override void Apply(ActionContext c) => RegistryValue.Write(c.Registry, Hive, Path, Name, "dword", Desired(c).Data!);

    /// <summary>Other tweaks may own other bits of the same value; only our bits count.</summary>
    public override bool IsStillApplied(ActionContext c, StoredValue applied) => State(c) == ActionState.Applied;

    /// <summary>Only our bits go back: other tweaks may own other bits of the same value.</summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        var originalBits = original is { Existed: true, Data: { } d } && uint.TryParse(d, out var o) ? o : 0u;
        var mask = Set | Clear;
        var restored = (Current(c) & ~mask) | (originalBits & mask);
        if (!original.Existed && restored == 0) RegistryValue.Delete(c.Registry, Hive, Path, Name);
        else RegistryValue.Write(c.Registry, Hive, Path, Name, "dword", restored.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Clears and/or sets bits in a REG_BINARY value byte by byte (e.g. UserPreferencesMask, where visual effects share
/// the value with accessibility options). Only the bits in the masks are written or compared; the rest of the value
/// stays as Windows or the user set it.
/// </summary>
public sealed class RegistryBinaryBitsAction : TweakAction
{
    public Hive Hive { get; init; } = Hive.User;
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Hex bytes, byte 0 first: bits to turn on.</summary>
    public string Set { get; init; } = "";

    /// <summary>Hex bytes, byte 0 first: bits to turn off.</summary>
    public string Clear { get; init; } = "";

    public override string TargetKey => $"regbinbits:{Hive}:{Path}\\{Name}:{Set}:{Clear}".ToLowerInvariant();

    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.registryBits", $"{c.Registry.DisplayRoot(Hive)}\\{Path}\\{Name}");

    private byte[]? Current(ActionContext c) =>
        RegistryValue.Read(c.Registry, Hive, Path, Name) is { Existed: true, Kind: "binary", Data: { } d } ? Convert.FromHexString(d) : null;

    private static byte[] Mask(string hex) => hex.Length == 0 ? [] : Convert.FromHexString(hex);

    private static byte At(byte[] bytes, int i) => i < bytes.Length ? bytes[i] : (byte)0;

    private byte[] Combine(byte[] current)
    {
        var set = Mask(Set);
        var clear = Mask(Clear);
        var result = new byte[Math.Max(current.Length, Math.Max(set.Length, clear.Length))];
        for (var i = 0; i < result.Length; i++) result[i] = (byte)((At(current, i) | At(set, i)) & ~At(clear, i));
        return result;
    }

    /// <summary>A value that does not exist or is not binary is not changed (Unsupported).</summary>
    public override StoredValue? Read(ActionContext c) => Current(c) is { } bytes ? new StoredValue(true, "binary", Convert.ToHexString(bytes)) : null;

    public override StoredValue Desired(ActionContext c) =>
        new(true, "binary", Convert.ToHexString(Combine(Current(c) ?? [])));

    public override ActionState State(ActionContext c)
    {
        if (Current(c) is not { } bytes) return ActionState.Unsupported;
        var set = Mask(Set);
        var clear = Mask(Clear);
        for (var i = 0; i < Math.Max(set.Length, clear.Length); i++)
            if ((At(bytes, i) & At(set, i)) != At(set, i) || (At(bytes, i) & At(clear, i)) != 0) return ActionState.NotApplied;
        return ActionState.Applied;
    }

    public override void Apply(ActionContext c)
    {
        if (Current(c) is not { } bytes) throw new InvalidOperationException($"{Describe(c)} does not exist");
        RegistryValue.Write(c.Registry, Hive, Path, Name, "binary", Convert.ToHexString(Combine(bytes)));
    }

    /// <summary>Only our bits count: Windows or the user may have changed other bits of the same value since.</summary>
    public override bool IsStillApplied(ActionContext c, StoredValue applied) => State(c) == ActionState.Applied;

    /// <summary>Puts back only the masked bits from the original; every other bit keeps its current state.</summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (Current(c) is not { } bytes || original is not { Existed: true, Data: { } data }) return;
        var before = Convert.FromHexString(data);
        var set = Mask(Set);
        var clear = Mask(Clear);
        var result = (byte[])bytes.Clone();
        for (var i = 0; i < result.Length; i++)
        {
            var mask = (byte)(At(set, i) | At(clear, i));
            result[i] = (byte)((result[i] & ~mask) | (At(before, i) & mask));
        }
        RegistryValue.Write(c.Registry, Hive, Path, Name, "binary", Convert.ToHexString(result));
    }
}

/// <summary>Sets one "Key=Value;" token in a semicolon list (e.g. DirectXUserGlobalSettings), keeping the other tokens.</summary>
public sealed class RegistryTokenAction : TweakAction
{
    public Hive Hive { get; init; } = Hive.User;
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Token { get; init; } = "";
    public string Value { get; init; } = "";

    public override string TargetKey => $"regtoken:{Hive}:{Path}\\{Name}:{Token}".ToLowerInvariant();

    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.registryToken", $"{c.Registry.DisplayRoot(Hive)}\\{Path}\\{Name}", Token);

    public static Dictionary<string, string> Parse(string? s) =>
        (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);

    public static string Format(Dictionary<string, string> tokens) => string.Concat(tokens.Select(t => $"{t.Key}={t.Value};"));

    private string? CurrentString(ActionContext c) => RegistryValue.Read(c.Registry, Hive, Path, Name) is { Existed: true } v ? v.Data : null;

    public override StoredValue? Read(ActionContext c)
    {
        var tokens = Parse(CurrentString(c));
        return tokens.TryGetValue(Token, out var v) ? new StoredValue(true, "token", v) : StoredValue.Missing;
    }

    public override StoredValue Desired(ActionContext c) => new(true, "token", Value);

    public override void Apply(ActionContext c) =>
        RegistryValue.Write(c.Registry, Hive, Path, Name, "string", WithToken(CurrentString(c), Token, Value));

    public override void Restore(ActionContext c, StoredValue original)
    {
        var current = CurrentString(c);
        var restored = WithToken(current, Token, original.Existed ? original.Data ?? "" : null);
        // Nothing left in the value (only this token was in it): removed, as before the first apply.
        if (restored.Length == 0) RegistryValue.Delete(c.Registry, Hive, Path, Name);
        else RegistryValue.Write(c.Registry, Hive, Path, Name, "string", restored);
    }

    /// <summary>
    /// The value with only this token set (null: removed); every other part stays as it was, also parts this app cannot
    /// read (no "="), so undo gives back what Windows or another program stored.
    /// </summary>
    public static string WithToken(string? raw, string token, string? value)
    {
        var parts = new List<string>();
        var found = false;
        foreach (var part in (raw ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                if (!found && value is not null) parts.Add($"{part[..eq].Trim()}={value}");
                found = true;
                continue;
            }
            parts.Add(part);
        }
        if (!found && value is not null) parts.Add($"{token}={value}");
        return string.Concat(parts.Select(p => p + ";"));
    }
}

/// <summary>Typed registry read/write shared by the registry actions.</summary>
public static class RegistryValue
{
    /// <summary>
    /// The value, or a missing value whose kind records how many keys of the path do not exist ("missingKeys:2", or
    /// "missingKeys:0" in an existing key), so an undo removes exactly the keys the change created.
    /// </summary>
    public static StoredValue Read(IRegistryRoots roots, Hive hive, string path, string name)
    {
        using var key = roots.Open(hive, path, writable: false);
        if (key is null) return new StoredValue(false, $"{MissingKeysPrefix}{MissingKeyCount(roots, hive, path).ToString(CultureInfo.InvariantCulture)}");
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return new StoredValue(false, $"{MissingKeysPrefix}0");
        var kind = key.GetValueKind(name);
        return kind switch
        {
            RegistryValueKind.DWord => new StoredValue(true, "dword", unchecked((uint)(int)value).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => new StoredValue(true, "qword", unchecked((ulong)(long)value).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.ExpandString => new StoredValue(true, "expandString", (string)value),
            RegistryValueKind.MultiString => new StoredValue(true, "multiString", string.Join("\n", (string[])value)),
            RegistryValueKind.Binary => new StoredValue(true, "binary", Convert.ToHexString((byte[])value)),
            // REG_NONE: keep the bytes, not the text "System.Byte[]".
            RegistryValueKind.None when value is byte[] none => new StoredValue(true, "none", Convert.ToHexString(none)),
            // Types .NET does not name (REG_DWORD_BIG_ENDIAN, REG_LINK, REG_RESOURCE_LIST and others): the type number with
            // the bytes, so undo writes back the same type, not REG_NONE.
            _ when value is byte[] raw && RawType(key, name) is { } type => new StoredValue(true, $"{RawPrefix}{type.ToString(CultureInfo.InvariantCulture)}", Convert.ToHexString(raw)),
            _ when value is byte[] raw => new StoredValue(true, "none", Convert.ToHexString(raw)),
            _ => new StoredValue(true, "string", value.ToString()),
        };
    }

    private const string MissingKeysPrefix = "missingKeys:";

    /// <summary>Kind of a value with a type .NET does not name: "raw:" and the registry type number.</summary>
    private const string RawPrefix = "raw:";

    private static uint? RawType(RegistryKey key, string name) =>
        RegQueryValueExW(key.Handle, name, IntPtr.Zero, out var type, IntPtr.Zero, IntPtr.Zero) == 0 ? type : null;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, IntPtr data, IntPtr size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, int reserved, uint type, byte[] data, int size);

    /// <summary>How many keys the change created, from an original read by <see cref="Read"/>; null when not recorded.</summary>
    public static int? CreatedKeys(StoredValue original)
    {
        if (original.Existed) return null;
        if (original.Kind is null) return null;
        return original.Kind.StartsWith(MissingKeysPrefix, StringComparison.Ordinal) &&
               int.TryParse(original.Kind.AsSpan(MissingKeysPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The path without its last <paramref name="levels"/> keys.</summary>
    public static string Ancestor(string path, int levels)
    {
        var p = path.TrimEnd('\\');
        for (var i = 0; i < levels; i++)
        {
            var cut = p.LastIndexOf('\\');
            if (cut <= 0) break;
            p = p[..cut];
        }
        return p;
    }

    private static int MissingKeyCount(IRegistryRoots roots, Hive hive, string path)
    {
        var count = 0;
        var p = path.TrimEnd('\\');
        while (p.Length > 0)
        {
            using (var k = roots.Open(hive, p, writable: false))
                if (k is not null) break;
            count++;
            var cut = p.LastIndexOf('\\');
            p = cut > 0 ? p[..cut] : "";
        }
        return count;
    }

    public static void Write(IRegistryRoots roots, Hive hive, string path, string name, string kind, string data)
    {
        using var key = roots.Open(hive, path, writable: true, create: true)
                        ?? throw new InvalidOperationException($"Cannot open {roots.DisplayRoot(hive)}\\{path} for writing");
        try
        {
            SetValue(key, name, kind, data);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The key opened for writing, so the value itself is protected (build 26300 denies Dsh\AllowNewsAndInterests
            // to administrators while other values in the same key can be written).
            throw new UnauthorizedAccessException(
                $"Windows denied writing {roots.DisplayRoot(hive)}\\{path}\\{name}, also with administrator rights. Windows protects this value on this PC.", ex);
        }
    }

    private static void SetValue(RegistryKey key, string name, string kind, string data)
    {
        switch (kind)
        {
            case "dword":
                key.SetValue(name, unchecked((int)uint.Parse(data, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                break;
            case "qword":
                key.SetValue(name, unchecked((long)ulong.Parse(data, CultureInfo.InvariantCulture)), RegistryValueKind.QWord);
                break;
            case "expandString":
                key.SetValue(name, data, RegistryValueKind.ExpandString);
                break;
            case "multiString":
                key.SetValue(name, data.Split('\n'), RegistryValueKind.MultiString);
                break;
            case "binary":
                key.SetValue(name, Convert.FromHexString(data.Replace(" ", "")), RegistryValueKind.Binary);
                break;
            case "none":
                key.SetValue(name, Convert.FromHexString(data), RegistryValueKind.None);
                break;
            case not null when kind.StartsWith(RawPrefix, StringComparison.Ordinal) && uint.TryParse(kind.AsSpan(RawPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var type):
                var bytes = Convert.FromHexString(data);
                var result = RegSetValueExW(key.Handle, name, 0, type, bytes, bytes.Length);
                if (result != 0) throw new System.ComponentModel.Win32Exception(result, $"RegSetValueEx {name} failed");
                break;
            default:
                key.SetValue(name, data, RegistryValueKind.String);
                break;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> and its parents up to <paramref name="stopAt"/> (inclusive), each only while it
    /// has no values and no subkeys. <paramref name="stopAt"/> must be <paramref name="path"/> or one of its parents.
    /// </summary>
    public static void DeleteEmptyKeys(IRegistryRoots roots, Hive hive, string path, string stopAt)
    {
        var current = path.TrimEnd('\\');
        var stop = stopAt.TrimEnd('\\');
        if (!current.Equals(stop, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(stop + "\\", StringComparison.OrdinalIgnoreCase)) return;
        while (current.Length >= stop.Length)
        {
            using (var key = roots.Open(hive, current, writable: false))
            {
                if (key is not null && (key.ValueCount > 0 || key.SubKeyCount > 0)) return;
            }
            var i = current.LastIndexOf('\\');
            if (i <= 0) return;
            using (var parent = roots.Open(hive, current[..i], writable: true))
                parent?.DeleteSubKey(current[(i + 1)..], throwOnMissingSubKey: false);
            current = current[..i];
        }
    }

    public static void Delete(IRegistryRoots roots, Hive hive, string path, string name)
    {
        using var key = roots.Open(hive, path, writable: true);
        if (key?.GetValue(name) is not null) key.DeleteValue(name, throwOnMissingValue: false);
    }
}
