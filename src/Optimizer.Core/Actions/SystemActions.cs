using System.Globalization;

namespace Optimizer.Core.Actions;

/// <summary>Service start type by short name (never display name). Per-user services: use the template name.</summary>
public sealed class ServiceAction : TweakAction
{
    public string Name { get; init; } = "";
    public ServiceStart StartType { get; init; } = ServiceStart.Manual;

    public override string TargetKey => $"svc:{Name}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.service", Name);

    public override StoredValue Desired(ActionContext c) => new(true, "service", StartType.ToString());

    public override StoredValue? Read(ActionContext c) =>
        c.Services.GetStartType(Name) is { } s ? new StoredValue(true, "service", s.ToString()) : null;

    public override void Apply(ActionContext c) => c.Services.SetStartType(Name, StartType);

    /// <summary>A service that was uninstalled since has nothing to restore (undo would otherwise fail on every attempt).</summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (c.Services.GetStartType(Name) is null) return;
        if (original.Existed && Enum.TryParse<ServiceStart>(original.Data, out var s)) c.Services.SetStartType(Name, s);
    }
}

/// <summary>
/// A power setting on the active scheme, by GUID or powercfg alias (localization-safe). The engine fills in
/// <see cref="Scheme"/> with the active scheme when it expands the tweak, so each scheme gets its own backup entry:
/// applying the tweak again after switching plans backs up the new plan's original instead of losing it.
/// </summary>
public sealed class PowerSettingAction : TweakAction
{
    /// <summary>Without a plan of its own the setting goes to the active plan (unchanged when that cannot be read).</summary>
    public override IEnumerable<TweakAction> Expand(ExpansionContext x) => [Scheme is null && x.ActiveScheme is { } scheme ? For(scheme) : this];

    public string Subgroup { get; init; } = "";
    public string Setting { get; init; } = "";
    public uint? Ac { get; init; }
    public uint? Dc { get; init; }

    /// <summary>The scheme this action reads and writes; null in the catalog (the active scheme at expansion).</summary>
    public Guid? Scheme { get; init; }

    private Guid Sub => PowerAliases.Resolve(Subgroup);
    private Guid Set => PowerAliases.Resolve(Setting);

    // Backups written before schemes were part of the key have no scheme; their key stays the same.
    public override string TargetKey => Scheme is { } s ? $"pwr:{s}:{Sub}:{Set}" : $"pwr:{Sub}:{Set}";

    /// <summary>
    /// The sides it writes (mains, battery) of the setting on its plan: a mains-only and a battery-only tweak of one
    /// setting do not conflict. Without a plan (an older backup, or the plan could not be read) it stands for every plan.
    /// </summary>
    public override IEnumerable<SharedTarget> SharedTargets()
    {
        var key = $"pwr:{Sub}:{Set}:{Scheme}".ToLowerInvariant();
        var sides = new[] { Ac is null ? null : "ac", Dc is null ? null : "dc" }.OfType<string>().ToList();
        return sides.Count == 0 ? [new(key, IsPrefix: Scheme is null)] : sides.Select(side => new SharedTarget(key, side, IsPrefix: Scheme is null));
    }
    // The plan by the name Windows shows (its GUID only when it has none), as in the active-plan preview.
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.powerSetting",
        SchemeOf(c) is var scheme && c.Power.Schemes().FirstOrDefault(s => s.Id == scheme).Name is { Length: > 0 } name ? name : scheme.ToString(),
        Setting.ToString(), Subgroup.ToString());

    public PowerSettingAction For(Guid scheme) => new() { Subgroup = Subgroup, Setting = Setting, Ac = Ac, Dc = Dc, Scheme = scheme };

    private Guid SchemeOf(ActionContext c) => Scheme ?? c.Power.ActiveScheme();

    // Data = "<scheme>;<ac>;<dc>" so undo writes back to the scheme that was changed, even if another plan is active now.
    private static string Format(Guid scheme, uint? ac, uint? dc) => $"{scheme};{ac?.ToString(CultureInfo.InvariantCulture)};{dc?.ToString(CultureInfo.InvariantCulture)}";

    public override StoredValue Desired(ActionContext c)
    {
        var scheme = SchemeOf(c);
        return new StoredValue(true, "power", Format(scheme, Ac ?? c.Power.ReadAc(scheme, Sub, Set), Dc ?? c.Power.ReadDc(scheme, Sub, Set)));
    }

    public override StoredValue? Read(ActionContext c)
    {
        var scheme = SchemeOf(c);
        var ac = c.Power.ReadAc(scheme, Sub, Set);
        return ac is null ? null : new StoredValue(true, "power", Format(scheme, ac, c.Power.ReadDc(scheme, Sub, Set)));
    }

    public override void Apply(ActionContext c)
    {
        var scheme = SchemeOf(c);
        if (Ac is { } ac) c.Power.WriteAc(scheme, Sub, Set, ac);
        if (Dc is { } dc) c.Power.WriteDc(scheme, Sub, Set, dc);
        if (c.Power.ActiveScheme() == scheme) c.Power.SetActive(scheme); // re-activating applies the new values
    }

    /// <summary>
    /// Compared on the scheme that was changed, not on whatever plan is active now. Mains and battery are compared and
    /// restored on their own: when the user changed only the mains value since, the
    /// battery value still goes back (and the mains value stays as the user set it).
    /// </summary>
    public override RestoreOutcome RestoreIfUnchanged(ActionContext c, StoredValue original, StoredValue? applied)
    {
        if (applied is null) return base.RestoreIfUnchanged(c, original, applied);
        var a = applied.Data?.Split(';') ?? [];
        var o = original.Data?.Split(';') ?? [];
        if (!original.Existed || a.Length < 3 || o.Length < 3 || !Guid.TryParse(a[0], out var scheme) || !c.Power.SchemeExists(scheme))
            return RestoreOutcome.ChangedSince;
        int wanted = 0, restored = 0;
        if (Ac is not null)
        {
            wanted++;
            if (c.Power.ReadAc(scheme, Sub, Set)?.ToString(CultureInfo.InvariantCulture) == a[1] && uint.TryParse(o[1], out var ac))
            {
                c.Power.WriteAc(scheme, Sub, Set, ac);
                restored++;
            }
        }
        if (Dc is not null)
        {
            wanted++;
            if (c.Power.ReadDc(scheme, Sub, Set)?.ToString(CultureInfo.InvariantCulture) == a[2] && uint.TryParse(o[2], out var dc))
            {
                c.Power.WriteDc(scheme, Sub, Set, dc);
                restored++;
            }
        }
        if (restored > 0 && c.Power.ActiveScheme() == scheme) c.Power.SetActive(scheme);
        return restored == 0 ? RestoreOutcome.ChangedSince : restored == wanted ? RestoreOutcome.Restored : RestoreOutcome.PartlyRestored;
    }

    /// <summary>
    /// Writes back only the values this action changed: a mains-only tweak and a battery-only tweak on the same setting
    /// (for example processor boost) are undone independently.
    /// </summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (!original.Existed || original.Data is null) return;
        var parts = original.Data.Split(';');
        if (!Guid.TryParse(parts[0], out var scheme) || !c.Power.SchemeExists(scheme)) return;
        if (Ac is not null && parts.Length > 1 && uint.TryParse(parts[1], out var ac)) c.Power.WriteAc(scheme, Sub, Set, ac);
        if (Dc is not null && parts.Length > 2 && uint.TryParse(parts[2], out var dc)) c.Power.WriteDc(scheme, Sub, Set, dc);
        if (c.Power.ActiveScheme() == scheme) c.Power.SetActive(scheme);
    }
}

/// <summary>Activates a scheme, optionally creating it first as a copy of a template (e.g. High performance -> "Tunero Gaming").</summary>
public sealed class PowerSchemeAction : TweakAction
{
    /// <summary>Scheme to activate: "balanced", "highPerformance", "powerSaver" or a GUID.</summary>
    public string? Activate { get; init; }

    /// <summary>Template to duplicate (GUID or alias); the copy gets <see cref="Name"/>.</summary>
    public string? DuplicateFrom { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// Names the plan had before (the app was called PCOptimizer up to 0.7.3): a plan an earlier version created is found
    /// and used again instead of a second copy.
    /// </summary>
    public List<string> FormerNames { get; init; } = [];

    public override string TargetKey => "pwr:activescheme";
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.activePowerPlan");

    private Guid? Target(ActionContext c)
    {
        if (DuplicateFrom is not null && Name is not null)
        {
            var schemes = c.Power.Schemes();
            foreach (var name in FormerNames.Prepend(Name))
                if (schemes.FirstOrDefault(s => s.Name == name).Id is var id && id != Guid.Empty) return id;
            return null;
        }
        return Activate is null ? null : PowerAliases.Resolve(Activate);
    }

    public override StoredValue Desired(ActionContext c) =>
        new(true, "scheme", Target(c)?.ToString() ?? $"new:{Name}");

    public override StoredValue? Read(ActionContext c) => new(true, "scheme", c.Power.ActiveScheme().ToString());

    /// <summary>
    /// The plan's name, not its GUID: Windows' own plans by a label key ("@plan.powerSaver"), which the app shows in its
    /// language (Windows names them in the language of Windows: "Energiesparmodus" in an English app), other plans as
    /// Windows shows them, and a plan still to be created by the name it gets.
    /// </summary>
    protected override string Show(ActionContext c, StoredValue value)
    {
        if (value.Data is { } data && data.StartsWith("new:", StringComparison.Ordinal)) return data[4..];
        if (Guid.TryParse(value.Data, out var builtIn) && PowerAliases.BuiltInName(builtIn) is { } alias) return "@plan." + alias;
        return Guid.TryParse(value.Data, out var id) && c.Power.Schemes().FirstOrDefault(s => s.Id == id).Name is { Length: > 0 } name ? name : value.Display;
    }

    /// <summary>
    /// The plans that existed before <see cref="Apply"/> ran on this instance, and the plan it activated: a rollback
    /// removes only newer plans, and switches back when the activated plan is still active.
    /// </summary>
    private HashSet<Guid>? _schemesBeforeApply;
    private Guid? _activated;
    private bool _created;

    /// <summary>Kind of the applied value: the plan was created by this apply (undo removes it) or already existed (kept).</summary>
    public const string CreatedKind = "scheme-created", ExistingKind = "scheme-existing";

    public override void Apply(ActionContext c)
    {
        if (DuplicateFrom is not null && Name is not null) _schemesBeforeApply = c.Power.Schemes().Select(s => s.Id).ToHashSet();
        var target = Target(c);
        _created = false;
        if (target is null && DuplicateFrom is not null && Name is not null)
        {
            target = c.Power.Duplicate(PowerAliases.Resolve(DuplicateFrom), Name);
            _created = true;
        }
        if (target is { } t)
        {
            _activated = t;
            c.Power.SetActive(t);
        }
    }

    /// <summary>
    /// The active plan, marked as created by this apply or found: a plan with the app's name that existed before (left by
    /// an earlier run, or the user's own) is switched away from on undo but never deleted.
    /// </summary>
    public override StoredValue? AppliedValue(ActionContext c) =>
        Read(c) is { } value && DuplicateFrom is not null && Name is not null ? value with { Kind = _created ? CreatedKind : ExistingKind } : Read(c);

    /// <summary>
    /// Applied again after the user picked another plan: the plan is found by its name now, but it is still the one the
    /// first apply created, and undo must remove it.
    /// </summary>
    public override StoredValue? MergeApplied(StoredValue? earlier, StoredValue? now) =>
        earlier is { Kind: CreatedKind } && now is { Kind: ExistingKind } && string.Equals(earlier.Data, now.Data, StringComparison.OrdinalIgnoreCase)
            ? now with { Kind = CreatedKind }
            : now;

    /// <summary>
    /// Undo always runs: when the user picked another plan since, the active plan stays as it is, but a plan this action
    /// created is still removed (otherwise it would be left behind for good). The plan this action activated (its GUID)
    /// identifies a created plan even after the user renamed it.
    /// </summary>
    public override RestoreOutcome RestoreIfUnchanged(ActionContext c, StoredValue original, StoredValue? applied)
    {
        // Backups from before the kind was recorded say "scheme": the plan counts as created, as it did then.
        RestoreCore(c, original, applied is { Existed: true } && Guid.TryParse(applied.Data, out var id) ? id : null, applied?.Kind != ExistingKind);
        return RestoreOutcome.Restored;
    }

    public override void Restore(ActionContext c, StoredValue original) => RestoreCore(c, original, null, true);

    private void RestoreCore(ActionContext c, StoredValue original, Guid? appliedPlan, bool appliedPlanCreated)
    {
        var active = c.Power.ActiveScheme();
        List<Guid> created = [];
        if (DuplicateFrom is not null && Name is not null)
        {
            // The plan this action created and activated, found by its GUID (the name may have been changed since). Only
            // that plan is removed, and only when this apply created it (a plan with the name that already existed was
            // only activated): another plan with the same name may be the user's own copy. Without the GUID (a
            // rollback right after a failed apply) the plans with the name that did not exist before this apply are the
            // ones just created; a plan an earlier apply created (and the user may have customized) stays.
            if (appliedPlan is { } plan)
            {
                if (appliedPlanCreated && c.Power.SchemeExists(plan) && !PowerAliases.IsBuiltIn(plan) && (!Guid.TryParse(original.Data, out var before) || before != plan))
                    created.Add(plan);
            }
            else if (_schemesBeforeApply is { } before)
            {
                created = c.Power.Schemes().Where(s => s.Name == Name && !before.Contains(s.Id)).Select(s => s.Id).ToList();
            }
            // Neither the plan's id nor the plans from before this apply are known (an action read back from a backup
            // without its applied value): a plan with the name may be the user's own copy, so none is deleted.
        }
        var ours = created.Contains(active) || (Activate is not null && active == PowerAliases.Resolve(Activate)) ||
                   (appliedPlan is { } activated ? activated == active : _activated == active);
        if (ours)
        {
            // The previous plan may have been deleted in the meantime: Balanced exists on every PC.
            var previous = Guid.TryParse(original.Data, out var p) && c.Power.SchemeExists(p) && !created.Contains(p) ? p : PowerAliases.Resolve("balanced");
            if (previous != active) c.Power.SetActive(previous);
        }
        // Schemes this action created are removed again on undo.
        foreach (var id in created.Where(id => id != c.Power.ActiveScheme()))
            c.Power.Delete(id);
    }
}

/// <summary>A BCD element on {current}. Presence-based (bcdedit values are localized, element names are not). Boot-critical.</summary>
public sealed class BcdAction : TweakAction
{
    public string Element { get; init; } = "";
    public string? Value { get; init; }
    public bool Delete { get; init; }

    public override EarlyRead EarlyRead => EarlyRead.WithScan;

    /// <summary>
    /// Only for backups written before values were read (they stored "set"): the value written back on undo when the
    /// element existed before.
    /// </summary>
    public string RestoreValue { get; init; } = "yes";

    private const string LegacyPresent = "set";

    public override bool IsBootCritical => true;
    public override string TargetKey => $"bcd:{Element}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.boot", Element);

    public override StoredValue Desired(ActionContext c) => Delete ? StoredValue.Missing : new StoredValue(true, "bcd", Normalize(Value ?? "yes"));

    /// <summary>The element's value ("yes", "no", numbers): so "useplatformclock No" is not mistaken for "Yes".</summary>
    public override StoredValue? Read(ActionContext c) =>
        c.Bcd.CurrentValues().TryGetValue(Element, out var v) ? new StoredValue(true, "bcd", Normalize(v)) : StoredValue.Missing;

    public override void Apply(ActionContext c)
    {
        if (Delete) c.Bcd.Delete(Element);
        else c.Bcd.Set(Element, Value ?? "yes");
    }

    public override void Restore(ActionContext c, StoredValue original)
    {
        if (!original.Existed)
        {
            c.Bcd.Delete(Element);
            return;
        }
        var value = original.Data == LegacyPresent ? (Delete ? RestoreValue : Value ?? "yes") : original.Data;
        // Only plain tokens go back to bcdedit; anything else (a localized word, a device path) is not guessed at.
        if (string.IsNullOrEmpty(value) || !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
            throw new InvalidOperationException($"BCD {Element}: original value \"{value}\" cannot be written back automatically");
        c.Bcd.Set(Element, value);
    }

    /// <summary>
    /// bcdedit prints booleans as Yes/No in the display language (Ja/Nein on German Windows, the app's two languages);
    /// numbers and other tokens are kept as printed (lower case).
    /// </summary>
    public static string Normalize(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "on" or "1" or "ja" => "yes",
        "no" or "false" or "off" or "0" or "nein" => "no",
        var other => other,
    };
}

/// <summary>Enables or disables a scheduled task by full path.</summary>
public sealed class ScheduledTaskAction : TweakAction
{
    public string Path { get; init; } = "";
    public bool Enabled { get; init; }

    public override string TargetKey => $"task:{Path}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.task", Path);
    public override StoredValue Desired(ActionContext c) => new(true, "task", Enabled ? "Enabled" : "Disabled");

    public override StoredValue? Read(ActionContext c) =>
        c.Tasks.IsEnabled(Path) is { } e ? new StoredValue(true, "task", e ? "Enabled" : "Disabled") : null;

    public override void Apply(ActionContext c) => c.Tasks.SetEnabled(Path, Enabled);

    /// <summary>A task that was removed since (by an update or its app) has nothing to restore.</summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (original.Existed && c.Tasks.IsEnabled(Path) is not null) c.Tasks.SetEnabled(Path, original.Data == "Enabled");
    }
}

/// <summary>
/// Hibernation on/off via powercfg (removes or recreates hiberfil.sys). State read from the registry. Unsupported when
/// the firmware has no S4 (VMs, some firmware): powercfg /hibernate on fails there, so an applied change could not be undone.
/// </summary>
public sealed class HibernationAction : TweakAction
{
    public bool Enabled { get; init; }

    public override string TargetKey => "power:hibernation";
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.hibernation");
    public override StoredValue Desired(ActionContext c) => new(true, "bool", Enabled ? "On" : "Off");

    public override StoredValue? Read(ActionContext c)
    {
        if (c.Power.HibernationSupported() == false) return null;
        var v = RegistryValue.Read(c.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
        return new StoredValue(true, "bool", v is { Existed: true, Data: "0" } ? "Off" : "On");
    }

    public override void Apply(ActionContext c) => Run(c, Enabled);

    public override void Restore(ActionContext c, StoredValue original)
    {
        // Without S4 there is no hibernation to turn back on (backups from before this check): nothing to restore.
        if (original.Data != "Off" && c.Power.HibernationSupported() == false) return;
        Run(c, original.Data != "Off");
    }

    private static void Run(ActionContext c, bool on)
    {
        var (code, output) = c.Processes.Run("powercfg.exe", on ? "/hibernate on" : "/hibernate off");
        if (code != 0) throw new InvalidOperationException($"powercfg /hibernate failed ({code}): {output}");
    }
}

/// <summary>Memory compression: read like Get-MMAgent (<see cref="IMemoryAgent"/>), changed with the MMAgent cmdlets (logged).</summary>
public sealed class MemoryCompressionAction : TweakAction
{
    public bool Enabled { get; init; }

    public override string TargetKey => "mmagent:memorycompression";
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.memoryCompression");
    public override StoredValue Desired(ActionContext c) => new(true, "bool", Enabled ? "True" : "False");

    // Enable-MMAgent and Disable-MMAgent change the setting for the next start; Get-MMAgent shows the running state.
    public override bool TakesEffectAfterRestart => true;

    public override EarlyRead EarlyRead => EarlyRead.WithScan;

    public override StoredValue? Read(ActionContext c) => ReadCache.Get(c, TargetKey, () =>
        c.MemoryAgent.MemoryCompression() is { } on ? new StoredValue(true, "bool", on ? "True" : "False") : null);

    public override void Apply(ActionContext c) => Run(c, Enabled);

    public override void Restore(ActionContext c, StoredValue original) => Run(c, original.Data == "True");

    private void Run(ActionContext c, bool on) => ReadCache.Writing(c, TargetKey, () =>
    {
        var verb = on ? "Enable-MMAgent" : "Disable-MMAgent";
        var (code, output) = c.Processes.Run(Platform.PowerShellScript.Exe, Platform.PowerShellScript.Command($"{verb} -MemoryCompression"));
        if (code != 0) throw new InvalidOperationException($"{verb} failed ({code}): {output}");
    });
}

/// <summary>Display mode (refresh rate) for one display; used by the one-click fix for F1.</summary>
public sealed class DisplayModeAction : TweakAction
{
    public string GdiName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshHz { get; init; }

    public override string TargetKey => $"display:{GdiName}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.refreshRate", GdiName, $"{Width}×{Height}");
    public override StoredValue Desired(ActionContext c) => new(true, "hz", RefreshHz.ToString(CultureInfo.InvariantCulture));

    public override StoredValue? Read(ActionContext c) =>
        c.Displays.CurrentRefresh(GdiName) is { } hz ? new StoredValue(true, "hz", hz.ToString(CultureInfo.InvariantCulture)) : null;

    public override void Apply(ActionContext c) => c.Displays.SetMode(GdiName, Width, Height, RefreshHz);

    /// <summary>
    /// Only the refresh rate goes back, at the resolution the display has now: the user may have changed it since. A
    /// display that is no longer connected has nothing to restore (it keeps its own mode for the next time).
    /// </summary>
    public override void Restore(ActionContext c, StoredValue original)
    {
        if (c.Displays.CurrentRefresh(GdiName) is null) return;
        if (int.TryParse(original.Data, out var hz)) c.Displays.SetRefresh(GdiName, hz);
    }
}

/// <summary>powercfg aliases and well-known scheme names -> GUIDs (never localized names).</summary>
public static class PowerAliases
{
    /// <summary>"balanced", "highPerformance", "powerSaver" or "ultimatePerformance" for one of Windows' own plans, else null.</summary>
    public static string? BuiltInName(Guid scheme) =>
        new[] { "balanced", "highPerformance", "powerSaver", "ultimatePerformance" }.FirstOrDefault(a => Resolve(a) == scheme);

    private static readonly Dictionary<string, Guid> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUB_PROCESSOR"] = new("54533251-82be-4824-96c1-47b60b740d00"),
        ["PROCTHROTTLEMAX"] = new("bc5038f7-23e0-4960-96da-33abaf5935ec"),
        ["PROCTHROTTLEMIN"] = new("893dee8e-2bef-41e0-89c6-b55d0929964c"),
        ["PERFBOOSTMODE"] = new("be337238-0d82-4146-a960-4f3749d470c7"),
        ["CPMINCORES"] = new("0cc5b647-c1df-4637-891a-dec35c318583"),
        ["SUB_USB"] = new("2a737441-1930-4402-8d77-b2bebba308a3"),
        ["USBSELECTIVESUSPEND"] = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
        ["SUB_PCIEXPRESS"] = new("501a4d13-42af-4429-9fd1-a8218c268e20"),
        ["ASPM"] = new("ee12f906-d277-404b-b6da-e5fa1a576df5"),
        // Checked with "powercfg /qh" on Windows 11 26H2 (Balanced defaults in brackets: mains / battery).
        ["SUB_WIRELESS"] = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"),        // no Windows alias
        ["WIRELESSPOWERSAVE"] = new("12bbebe6-58d6-4636-95bb-3217ef867c1a"),    // 0 Max performance .. 3 Max saving [0 / 2]
        ["SUB_SLEEP"] = new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),
        ["RTCWAKE"] = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"),             // allow wake timers: 0 off, 1 on, 2 important only [1 / 0]
        ["balanced"] = new("381b4222-f694-41f0-9685-ff5bb260df2e"),
        ["highPerformance"] = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        ["powerSaver"] = new("a1841308-3541-4fab-bc81-f71556f20b4a"),
        ["ultimatePerformance"] = new("e9a42b02-d5df-448d-aa00-03f14749eb61"),
    };

    public static Guid Resolve(string aliasOrGuid) =>
        Map.TryGetValue(aliasOrGuid, out var g) ? g
        : Guid.TryParse(aliasOrGuid, out var parsed) ? parsed
        : throw new ArgumentException($"Unknown power alias '{aliasOrGuid}'");

    public static bool IsKnown(string aliasOrGuid) => Map.ContainsKey(aliasOrGuid) || Guid.TryParse(aliasOrGuid, out _);

    /// <summary>The plans Windows ships (never deleted by an undo).</summary>
    public static bool IsBuiltIn(Guid scheme) =>
        scheme == Map["balanced"] || scheme == Map["highPerformance"] || scheme == Map["powerSaver"] || scheme == Map["ultimatePerformance"];
}
