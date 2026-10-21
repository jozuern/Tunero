using Optimizer.Core.Tweaks;

namespace Optimizer.App.Services;

/// <summary>
/// One wording for work that can take minutes: the clock line next to its button (the DurationHint style), the progress
/// banner while it runs (<see cref="WorkProgress.DurationText"/>), and a sentence at the end of its question. Only for
/// work that does take minutes, never for work that is done in seconds.
/// </summary>
public static class Durations
{
    /// <summary>chkdsk /scan, Update all, a long VirusTotal check.</summary>
    public static string Minutes => Loc.Instance["Hint_TakesMinutes"];

    /// <summary>
    /// DISM and SFC (system files, component store cleanup, Windows features, reserved storage): they wait while Windows
    /// services its own updates. In PCO-Test DISM ScanHealth showed no progress for over 25 minutes while TiWorker ran.
    /// </summary>
    public static string Servicing => Loc.Instance["Hint_TakesMinutesServicing"];

    /// <summary>The install media build: in PCO-Test the download alone took 21 minutes and unpacking the image 12 (docs/vm-test-plan.md, VM 4).</summary>
    public static string HalfHour => Loc.Instance["Hint_TakesHalfHour"];

    /// <summary>The note for a tweak: every action that takes minutes runs DISM (<see cref="Optimizer.Core.Actions.TweakAction.TakesMinutes"/>).</summary>
    public static string? For(TweakDefinition t) => t.TakesMinutes ? Servicing : null;

    /// <summary>The note for several tweaks at once: when any of them takes minutes.</summary>
    public static string? For(IEnumerable<TweakDefinition> tweaks) => tweaks.Any(t => t.TakesMinutes) ? Servicing : null;

    /// <summary>The text with "This can take several minutes." as its last sentence.</summary>
    public static string WithSentence(string text) => $"{text.TrimEnd()} {Loc.Instance["Hint_TakesMinutesSentence"]}";

    /// <summary>The same for DISM based work: "..., longer while Windows is installing updates."</summary>
    public static string WithServicingSentence(string text) => $"{text.TrimEnd()} {Loc.Instance["Hint_TakesMinutesServicingSentence"]}";
}
