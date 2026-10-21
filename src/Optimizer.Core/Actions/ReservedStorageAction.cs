using System.Text.RegularExpressions;

namespace Optimizer.Core.Actions;

/// <summary>
/// Reserved storage (space Windows keeps for updates), switched with DISM /Set-ReservedStorageState and read with
/// /Get-ReservedStorageState (English output). DISM refuses the change while an update is using the space.
/// </summary>
public sealed partial class ReservedStorageAction : TweakAction
{
    public bool Enabled { get; init; }

    public override string TargetKey => "dism:reservedstorage";
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.reservedStorage");
    public override EarlyRead EarlyRead => EarlyRead.WithScan;
    public override StoredValue Desired(ActionContext c) => new(true, "reservedstorage", Enabled ? "Enabled" : "Disabled");

    public override StoredValue? Read(ActionContext c) => ReadCache.Get(c, TargetKey, () =>
    {
        var (code, output) = c.Processes.Run("dism.exe", "/Online /English /Get-ReservedStorageState", TimeSpan.FromMinutes(2));
        if (code != 0) return null;
        var m = StateRegex().Match(output);
        return m.Success ? new StoredValue(true, "reservedstorage", m.Groups[1].Value.Equals("enabled", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled") : null;
    });

    public override void Apply(ActionContext c) => Set(c, Enabled);

    public override void Restore(ActionContext c, StoredValue original)
    {
        if (original.Data is "Enabled" or "Disabled") Set(c, original.Data == "Enabled");
    }

    private void Set(ActionContext c, bool enabled) => ReadCache.Writing(c, TargetKey, () =>
    {
        var (code, output) = c.Processes.Run("dism.exe", $"/Online /English /Set-ReservedStorageState /State:{(enabled ? "Enabled" : "Disabled")}", TimeSpan.FromMinutes(5));
        if (code != 0) throw new InvalidOperationException(Optimizer.Core.Media.DismOutput.Failure(code, output));
    });

    [GeneratedRegex(@"Reserved storage is (enabled|disabled)", RegexOptions.IgnoreCase)]
    private static partial Regex StateRegex();
}
