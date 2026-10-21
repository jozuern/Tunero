using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

/// <summary>
/// Select mode of the Tweaks page: a check box on each row, then one confirmation for all picked rows that can be turned on
/// together (the batch of Apply recommended) and one for all picked changes of this app to undo. Picks are kept by tweak id,
/// so a search, a filter or the rebuild after a scan keeps them; a run clears the ones it handled.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, (TweakDefinition Tweak, PickKind Kind)> _picks = new(StringComparer.Ordinal);

    [ObservableProperty] private bool _tweakSelectMode;

    [ObservableProperty] private int _pickedOnCount;
    [ObservableProperty] private int _pickedUndoCount;

    public bool HasPicks => PickedOnCount + PickedUndoCount > 0;

    /// <summary>"3 selected" (hidden picks included: a search does not drop them).</summary>
    public string TweaksPickedText => Loc.Instance.Format("Tweaks_Picked", PickedOnCount + PickedUndoCount);

    public string TurnOnPickedText => Loc.Instance.Format("Tweaks_PickedTurnOn", PickedOnCount);

    public string UndoPickedText => Loc.Instance.Format("Tweaks_PickedUndo", PickedUndoCount);

    partial void OnTweakSelectModeChanged(bool value)
    {
        if (!value) _picks.Clear();
        foreach (var row in Tweaks)
        {
            row.ShowPick = value;
            if (!value) row.IsPicked = false;
        }
        UpdatePicks();
    }

    /// <summary>A rebuilt row takes its pick back; a pick whose row can no longer be picked (now on outside this app) is dropped.</summary>
    private void ShowPick(TweakItemViewModel row)
    {
        row.ShowPick = TweakSelectMode;
        if (_picks.ContainsKey(row.Tweak.Id))
        {
            if (row.CanPick) _picks[row.Tweak.Id] = (row.Tweak, row.Pick);
            else _picks.Remove(row.Tweak.Id);
        }
        row.IsPicked = _picks.ContainsKey(row.Tweak.Id);
        row.PickChanged = OnRowPicked;
    }

    private void OnRowPicked(TweakItemViewModel row)
    {
        var id = row.Tweak.Id;
        if (!row.IsPicked)
        {
            _picks.Remove(id);
        }
        else
        {
            _picks[id] = (row.Tweak, row.Pick);
            // Alternatives that exclude each other (the power plans): picking one drops the others, as one switch would.
            foreach (var other in Tweaks.Where(o => o.IsPicked && o != row && Conflict(o.Tweak, row.Tweak)).ToList()) other.IsPicked = false;
            foreach (var key in _picks.Where(p => p.Key != id && Conflict(p.Value.Tweak, row.Tweak)).Select(p => p.Key).ToList()) _picks.Remove(key);
        }
        UpdatePicks();
    }

    private static bool Conflict(TweakDefinition a, TweakDefinition b) => a.ConflictsWith.Contains(b.Id) || b.ConflictsWith.Contains(a.Id);

    private void UpdatePicks()
    {
        PickedOnCount = _picks.Values.Count(p => p.Kind == PickKind.TurnOn);
        PickedUndoCount = _picks.Values.Count(p => p.Kind == PickKind.Undo);
        OnPropertyChanged(nameof(HasPicks));
        OnPropertyChanged(nameof(TweaksPickedText));
        OnPropertyChanged(nameof(TurnOnPickedText));
        OnPropertyChanged(nameof(UndoPickedText));
    }

    /// <summary>Picks every listed row that can be picked (closed groups too), skipping alternatives to one already picked.</summary>
    [RelayCommand]
    private void PickAllTweaks()
    {
        foreach (var row in Tweaks.Where(r => r.CanPick && !r.IsPicked).ToList())
            if (!_picks.Values.Any(p => Conflict(p.Tweak, row.Tweak))) row.IsPicked = true;
    }

    [RelayCommand]
    private void ClearTweakPicks()
    {
        _picks.Clear();
        foreach (var row in Tweaks) row.IsPicked = false;
        UpdatePicks();
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task TurnOnPickedAsync()
    {
        var tweaks = Picked(PickKind.TurnOn);
        var results = await Runner.ApplyBatchAsync(tweaks, Loc.Instance["Tweaks_PickedTurnOnTitle"], Loc.Instance["Tweaks_PickedTurnOnIntro"]);
        Unpick(results.Select(r => r.Tweak));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task UndoPickedAsync()
    {
        var tweaks = Picked(PickKind.Undo);
        Unpick(await Runner.UndoBatchAsync(tweaks));
    }

    private List<TweakDefinition> Picked(PickKind kind) => _picks.Values.Where(p => p.Kind == kind).Select(p => p.Tweak).ToList();

    /// <summary>What a run handled leaves the selection (the rows are rebuilt after the change anyway).</summary>
    private void Unpick(IEnumerable<TweakDefinition> handled)
    {
        foreach (var t in handled) _picks.Remove(t.Id);
        foreach (var row in Tweaks) row.IsPicked = _picks.ContainsKey(row.Tweak.Id);
        UpdatePicks();
    }
}
