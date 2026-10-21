using System.Text.Json.Serialization;

namespace Optimizer.Core.Actions;

public enum ActionState { Applied, NotApplied, Unsupported }

/// <summary>A stored value: whether it existed, its kind and a serialized form. Used for originals and applied values.</summary>
public sealed record StoredValue(bool Existed, string? Kind = null, string? Data = null)
{
    public static readonly StoredValue Missing = new(false);

    /// <summary>
    /// The value for people: a whole registry key by its size, a deleted startup file (its bytes stay in the backup) by its
    /// size and a deleted task (its XML) by the program it ran, never the raw content (megabytes of Base64 in a dialog).
    /// </summary>
    public string Display => !Existed ? "(not set)" : Kind switch
    {
        "regkey" => RegistryKeyAction.Summary(Data),
        "file" => FormattableString.Invariant($"@file:{Base64Bytes(Data)}"),
        "taskXml" => Startup.ScheduledTaskXml.Command(Data ?? "") ?? "@task",
        _ => Data ?? "",
    };

    private static long Base64Bytes(string? data) =>
        data is null ? 0 : data.Length / 4L * 3 - (data.EndsWith("==", StringComparison.Ordinal) ? 2 : data.EndsWith('=') ? 1 : 0);

    /// <summary>Same presence and data, and the same registry type when both are registry values (a REG_SZ "1" is not a DWORD 1).</summary>
    public bool SameAs(StoredValue other) =>
        Existed == other.Existed && (!Existed || (string.Equals(Data, other.Data, StringComparison.Ordinal) &&
                                                  (!IsRegistryKind(Kind) || !IsRegistryKind(other.Kind) || string.Equals(Kind, other.Kind, StringComparison.Ordinal))));

    // Other actions use the kind for their own purposes (a label, or the raw bytes of a startup entry).
    private static bool IsRegistryKind(string? kind) => kind is "dword" or "qword" or "string" or "expandString" or "multiString" or "binary" or "none" || kind?.StartsWith("raw:", StringComparison.Ordinal) == true;
}

/// <summary>What an undo did with one target.</summary>
public enum RestoreOutcome
{
    /// <summary>The original is back.</summary>
    Restored,

    /// <summary>Nothing restored: the value is no longer the one this app wrote (Windows or the user changed it).</summary>
    ChangedSince,

    /// <summary>The parts still as applied were restored; parts changed since (one side of a power setting) were left.</summary>
    PartlyRestored,
}

/// <summary>
/// A change target as a label key ("target.service", see Catalog/Data/labels.json) and the parts that stay as they are
/// in every language (names, paths, ids), so each language renders it from its own template. Without a key the target
/// reads the same in every language (a registry path, a file) and its one part is the text.
/// </summary>
public sealed record TargetText
{
    public string? Key { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];

    public static TargetText Of(string key, params string[] args) => new() { Key = key, Args = args };

    public static TargetText Verbatim(string text) => new() { Args = [text] };

    // Records compare lists by reference: two texts of the same target are equal.
    public bool Equals(TargetText? other) => other is not null && Key == other.Key && Args.SequenceEqual(other.Args);

    public override int GetHashCode() => HashCode.Combine(Key, string.Join('\n', Args));
}

/// <summary>
/// One exact change for the confirmation dialog and the generated "What changes" section. <see cref="Target"/> is the
/// English text (logs); <see cref="Label"/>, when set, renders it in the UI language.
/// </summary>
public sealed record ChangeLine(string Target, string Before, string After, TargetText? Label = null)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Target;
}

/// <summary>When the scan starts a slow read of an action, so that the tweak detection finds it done.</summary>
public enum EarlyRead
{
    None,

    /// <summary>Starts a tool (PowerShell, DISM, bcdedit) and shares the result through <see cref="ReadCache"/>: read beside the hardware probes.</summary>
    WithScan,

    /// <summary>
    /// Opens the NVIDIA driver settings, which stay open for a few seconds: read right after the hardware probes, whose
    /// own NVIDIA read must not run at the same time (the driver settings are never opened twice at once).
    /// </summary>
    AfterScan,
}

/// <summary>
/// One step of a tweak. Every action can read its current state, describe the change, apply it and restore a
/// stored original. Actions never decide policy (that is the engine's and the guard rules' job).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegistryAction), "registry")]
[JsonDerivedType(typeof(RegistryKeyAction), "registryKey")]
[JsonDerivedType(typeof(OwnTaskAction), "ownTask")]
[JsonDerivedType(typeof(RegistryBitsAction), "registryBits")]
[JsonDerivedType(typeof(RegistryTokenAction), "registryToken")]
[JsonDerivedType(typeof(RegistryBinaryBitsAction), "registryBinaryBits")]
[JsonDerivedType(typeof(ServiceAction), "service")]
[JsonDerivedType(typeof(PowerSettingAction), "powerSetting")]
[JsonDerivedType(typeof(PowerSchemeAction), "powerScheme")]
[JsonDerivedType(typeof(BcdAction), "bcd")]
[JsonDerivedType(typeof(ScheduledTaskAction), "scheduledTask")]
[JsonDerivedType(typeof(HibernationAction), "hibernation")]
[JsonDerivedType(typeof(MemoryCompressionAction), "memoryCompression")]
[JsonDerivedType(typeof(DisplayModeAction), "displayMode")]
[JsonDerivedType(typeof(NvidiaDrsAction), "nvidiaDrs")]
[JsonDerivedType(typeof(PowerModeAction), "powerMode")]
[JsonDerivedType(typeof(NicPropertyAction), "nicProperty")]
[JsonDerivedType(typeof(DnsAction), "dns")]
[JsonDerivedType(typeof(AccessibilityShortcutAction), "accessibilityShortcut")]
[JsonDerivedType(typeof(SystemParameterAction), "systemParameter")]
[JsonDerivedType(typeof(DohAutoUpgradeAction), "dohAutoUpgrade")]
[JsonDerivedType(typeof(ReservedStorageAction), "reservedStorage")]
[JsonDerivedType(typeof(Startup.StartupApprovedAction), "startupApproved")]
[JsonDerivedType(typeof(Startup.StartupFileDeleteAction), "startupFileDelete")]
[JsonDerivedType(typeof(Startup.ScheduledTaskDeleteAction), "scheduledTaskDelete")]
[JsonDerivedType(typeof(Tools.OptionalFeatureAction), "optionalFeature")]
[JsonDerivedType(typeof(Tools.OptionalCapabilityAction), "optionalCapability")]
public abstract class TweakAction
{
    /// <summary>Stable key of the changed target, used for the first-original backup rule.</summary>
    public abstract string TargetKey { get; }

    /// <summary>The value this action writes (for drift detection and feature-update-aware undo).</summary>
    public abstract StoredValue Desired(ActionContext c);

    /// <summary>Current value of the target, or null when the target does not exist on this PC (-> Unsupported).</summary>
    public abstract StoredValue? Read(ActionContext c);

    public abstract void Apply(ActionContext c);

    /// <summary>Writes a stored original back (deleting the target when it did not exist).</summary>
    public abstract void Restore(ActionContext c, StoredValue original);

    /// <summary>
    /// The actions this one stands for on this PC. A template gives one per target ("{nic}" one per active network
    /// interface, a power setting the active plan, adapter properties every adapter of the medium); a plain action is itself.
    /// </summary>
    public virtual IEnumerable<TweakAction> Expand(ExpansionContext x) => [this];

    /// <summary>The changed target as a label key and its parts, e.g. "target.service" and "Spooler".</summary>
    public abstract TargetText TargetLabel(ActionContext c);

    /// <summary>The target in English, e.g. "HKLM\SYSTEM\...\HwSchMode" or "Service Spooler (start type)": logs, backups, errors.</summary>
    public string Describe(ActionContext c) => Docs.Labels.Current.Render("en", TargetLabel(c));

    /// <summary>Boot configuration and similar changes that the app's own undo cannot fix if the PC does not start.</summary>
    public virtual bool IsBootCritical => false;

    /// <summary>
    /// The change takes effect only after a restart and <see cref="Read"/> returns the running value, so the old value
    /// until then (memory compression). The engine stores the desired value as applied and undoes it also before the restart.
    /// </summary>
    public virtual bool TakesEffectAfterRestart => false;

    /// <summary>
    /// Applying or undoing it can take several minutes, longer while Windows installs updates: DISM installing or removing
    /// a Windows feature, or any DISM call while Windows' own servicing (TiWorker) runs, which DISM waits for. The tweak
    /// gets a tag, its confirmation says so and the progress banner too (<see cref="Tweaks.TweakDefinition.TakesMinutes"/>).
    /// </summary>
    public virtual bool TakesMinutes => false;

    /// <summary>Whether the scan reads this action's state ahead of the tweak detection (<see cref="Tweaks.TweakEngine.PrefetchReads"/>).</summary>
    public virtual EarlyRead EarlyRead => EarlyRead.None;

    /// <summary>
    /// What the backup records as applied after <see cref="Apply"/> ran on this instance: the value read back, or the
    /// desired value for a change that waits for a restart. Actions that need to remember how they applied (a power plan
    /// that was created rather than found) add that to the value.
    /// </summary>
    public virtual StoredValue? AppliedValue(ActionContext c) => TakesEffectAfterRestart ? Desired(c) : Read(c);

    /// <summary>
    /// The applied value to record when the backup already has one from an earlier apply (apply again after Windows or the
    /// user changed the value). By default the new one; an action keeps what only the earlier apply could know.
    /// </summary>
    public virtual StoredValue? MergeApplied(StoredValue? earlier, StoredValue? now) => now;

    public virtual ActionState State(ActionContext c)
    {
        var current = Read(c);
        if (current is null) return ActionState.Unsupported;
        return current.SameAs(Desired(c)) ? ActionState.Applied : ActionState.NotApplied;
    }

    /// <summary>
    /// Undo check: is the value we applied still in place? (If not, Windows or the user changed it and undo leaves it.)
    /// Actions whose target is shared or scheme-specific override this.
    /// </summary>
    public virtual bool IsStillApplied(ActionContext c, StoredValue applied) => Read(c) is not { } current || current.SameAs(applied);

    /// <summary>
    /// Undo of one target: restores the original unless the value is no longer what this app wrote. Null
    /// <paramref name="applied"/> restores unconditionally. Actions made of several parts (mains and battery, several
    /// adapter keywords) override this to compare and restore each part on its own.
    /// </summary>
    public virtual RestoreOutcome RestoreIfUnchanged(ActionContext c, StoredValue original, StoredValue? applied)
    {
        if (applied is not null && !IsStillApplied(c, applied)) return RestoreOutcome.ChangedSince;
        Restore(c, original);
        return RestoreOutcome.Restored;
    }

    public ChangeLine Change(ActionContext c)
    {
        var current = Read(c);
        var label = TargetLabel(c);
        return new ChangeLine(Docs.Labels.Current.Render("en", label), current is null ? "(unavailable)" : Show(c, current), Show(c, Desired(c)), label);
    }

    /// <summary>A value as the preview shows it. Actions that store an id (a power plan GUID) show its name instead.</summary>
    protected virtual string Show(ActionContext c, StoredValue value) => value.Display;
}
