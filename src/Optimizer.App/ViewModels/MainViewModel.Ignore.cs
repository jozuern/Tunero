using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Findings;
using Optimizer.Core.Profiles;

namespace Optimizer.App.ViewModels;

/// <summary>
/// Results the user ignores (<see cref="IgnoredResults"/>): an ignored result leaves the lists, the score, the problem
/// counts and the facts the recommendations read; it stays one click away under "Ignored" and comes back with Stop
/// ignoring. Recommendations the user ignores (<see cref="ProfiledTweak.Ignored"/>, kept by tweak id in every profile):
/// the tweak is no longer recommended, offered in the plan or flagged as working against the profile, and waits in the
/// Tweaks page's "Ignored recommendations" group, where it can still be turned on or off by hand.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<FindingItemViewModel> IgnoredItems { get; } = [];

    [ObservableProperty] private string? _ignoredText;
    [ObservableProperty] private bool _showIgnored;

    /// <summary>"Ignored recommendations: 2. Show them" in the Overview's plan card; null when none.</summary>
    [ObservableProperty] private string? _ignoredTweaksText;

    private void BuildIgnored(string lang)
    {
        IgnoredItems.Clear();
        foreach (var f in _state.IgnoredFindings) IgnoredItems.Add(new FindingItemViewModel(f, lang, null, _state.Usage.Goal) { IsIgnored = true });
        IgnoredText = IgnoredItems.Count > 0 ? Loc.Instance.Format("Findings_IgnoredCount", IgnoredItems.Count) : null;
        if (IgnoredItems.Count == 0) ShowIgnored = false;
    }

    [RelayCommand]
    private void ToggleIgnored() => ShowIgnored = !ShowIgnored;

    // Take any inspector row, as RunAction does: the buttons' parameter is the selected item, a result or a tweak, and a
    // typed command would throw when it asks whether it can run.
    [RelayCommand]
    private async Task IgnoreAsync(InspectorItem? row)
    {
        if (row is TweakItemViewModel { OffersIgnore: true } tweak)
        {
            SetTweakIgnored(tweak.Tweak.Id, true);
            ShowResult(Loc.Instance.Format("Tweaks_IgnoredResult", tweak.Title));
            return;
        }
        if (row is not FindingItemViewModel item || !item.CanIgnore) return;
        await AfterIgnoreChangedAsync(item.Finding, true);
        ShowResult(Loc.Instance.Format("Findings_IgnoredResult", item.Title));
    }

    [RelayCommand]
    private async Task StopIgnoringAsync(InspectorItem? row)
    {
        if (row is TweakItemViewModel { IsIgnored: true } tweak)
        {
            SetTweakIgnored(tweak.Tweak.Id, false);
            ShowResult(Loc.Instance.Format("Tweaks_UnignoredResult", tweak.Title));
            return;
        }
        if (row is not FindingItemViewModel item) return;
        await AfterIgnoreChangedAsync(item.Finding, false);
        ShowResult(Loc.Instance.Format("Findings_UnignoredResult", item.Title));
    }

    /// <summary>The ignore button of a line in the Overview's plan or under "works against this profile".</summary>
    [RelayCommand]
    private void IgnoreTweak(string? tweakId)
    {
        if (_state.Profiled.FirstOrDefault(p => p.Tweak.Id == tweakId) is not { } p) return;
        SetTweakIgnored(p.Tweak.Id, true);
        ShowResult(Loc.Instance.Format("Tweaks_IgnoredResult", Runner.Title(p.Tweak)));
    }

    /// <summary>From the Overview to the Tweaks page with the ignored recommendations open (search and filters cleared).</summary>
    [RelayCommand]
    private void ShowIgnoredTweaks() => RevealTweakGroup(IgnoredGroup);

    /// <summary>The facts do not depend on ignored tweaks: the profile view and every list are built again, nothing is read.</summary>
    private void SetTweakIgnored(string tweakId, bool ignored)
    {
        _state.SetTweakIgnored(tweakId, ignored);
        Rebuild();
    }

    /// <summary>The facts change with the ignored results (what is recommended for them): read them again, then every list.</summary>
    private async Task AfterIgnoreChangedAsync(Finding finding, bool ignored)
    {
        await _state.SetResultIgnoredAsync(finding, ignored);
        Rebuild();
    }
}
