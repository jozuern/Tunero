using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Optimizer.App.Services;
using Optimizer.Core.Media;

namespace Optimizer.App.ViewModels;

/// <summary>Where one stage of the install media build stands.</summary>
public enum MediaStageState { Waiting, Running, Done, Failed, Stopped, NotRun }

/// <summary>One line of the build progress list on the install media page: a stage, its state, what it is doing now and how long it took.</summary>
public sealed partial class MediaStageRow(MediaStage stage) : ObservableObject
{
    public MediaStage Stage { get; } = stage;

    public string Title => Loc.Instance[$"Media_Stage_{Stage}"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsStopped), nameof(StateText), nameof(Note))]
    private MediaStageState _state = MediaStageState.Waiting;

    /// <summary>What the stage is doing (the package being removed, the file being copied), or why it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPercent))]
    private double? _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan? _duration;

    // The state glyph: one of these shows (a not-run stage looks like a waiting one, and says "Not run" in its note).
    public bool IsWaiting => State is MediaStageState.Waiting or MediaStageState.NotRun;
    public bool IsRunning => State == MediaStageState.Running;
    public bool IsDone => State == MediaStageState.Done;
    public bool IsFailed => State == MediaStageState.Failed;
    public bool IsStopped => State == MediaStageState.Stopped;

    public bool HasPercent => Percent is not null && State == MediaStageState.Running;

    /// <summary>"12:05" or "1:02:05", empty while the stage has not begun.</summary>
    public string DurationText => Duration is { } t
        ? t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture)
        : "";

    /// <summary>The line under the title: the detail, else "Stopped" or "Not run"; null when there is nothing to say.</summary>
    public string? Note => Detail is { Length: > 0 } detail ? detail
        : State == MediaStageState.Stopped ? Loc.Instance["Media_StageStopped"]
        : State == MediaStageState.NotRun ? Loc.Instance["Media_StageNotRun"]
        : null;

    /// <summary>The state in words, for screen readers (the glyph says it to the eye).</summary>
    public string StateText => Loc.Instance[State switch
    {
        MediaStageState.Running => "Media_StageRunning",
        MediaStageState.Done => "Media_StageDone",
        MediaStageState.Failed => "Media_StageFailed",
        MediaStageState.Stopped => "Media_StageStopped",
        MediaStageState.NotRun => "Media_StageNotRun",
        _ => "Media_StageWaiting",
    }];

    // What screen readers announce for this item in a list.
    public override string ToString() => $"{Title}, {StateText}";
}
