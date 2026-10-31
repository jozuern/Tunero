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
    /// <summary>The ignored results the active profile would show, with its impact (set with the plan, <see cref="ApplyProfile"/>).</summary>
    private IReadOnlyList<Finding> _ignoredFindings = [];

    public ObservableCollection<FindingItemViewModel> IgnoredItems { get; } = [];

    [ObservableProperty] private string? _ignoredText;
    [ObservableProperty] private bool _showIgnored;

    /// <summary>"Ignored recommendations: 2. Show them" in the Overview's plan card; null when none.</summary>
    [ObservableProperty] private string? _ignoredTweaksText;

    /// <summary>The ignored results' keys, as a copy a background read can use while the list changes.</summary>
    private IReadOnlySet<string> IgnoredKeys() => _settings.IgnoredFindings.ToHashSet(StringComparer.Ordinal);

    /// <summary>The ids of the tweaks whose recommendation the user ignores.</summary>
    private IReadOnlySet<string> IgnoredTweakIds() => _settings.IgnoredTweaks.ToHashSet(StringComparer.Ordinal);

    private void BuildIgnored(string lang)
    {
        IgnoredItems.Clear();
        foreach (var f in _ignoredFindings) IgnoredItems.Add(new FindingItemViewModel(f, lang, null, _usage.Goal) { IsIgnored = true });
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
        var key = IgnoredResults.Key(item.Finding);
        if (!_settings.IgnoredFindings.Contains(key, StringComparer.Ordinal)) _settings.IgnoredFindings.Add(key);
        _settings.Save();
        await AfterIgnoreChangedAsync();
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
        _settings.IgnoredFindings.RemoveAll(k => k == IgnoredResults.Key(item.Finding));
        _settings.Save();
        await AfterIgnoreChangedAsync();
        ShowResult(Loc.Instance.Format("Findings_UnignoredResult", item.Title));
    }

    /// <summary>The ignore button of a line in the Overview's plan or under "works against this profile".</summary>
    [RelayCommand]
    private void IgnoreTweak(string? tweakId)
    {
        if (_profiled.FirstOrDefault(p => p.Tweak.Id == tweakId) is not { } p) return;
        SetTweakIgnored(p.Tweak.Id, true);
        ShowResult(Loc.Instance.Format("Tweaks_IgnoredResult", Runner.Title(p.Tweak)));
    }

    /// <summary>From the Overview to the Tweaks page with the ignored recommendations open (search and filters cleared).</summary>
    [RelayCommand]
    private void ShowIgnoredTweaks()
    {
        TweakImpactMin = 0;
        TweakSearch = "";
        // An ignored tweak can be one the active profile does not rate: the view that lists every tweak for this PC.
        if (TweakViewMode != TweakView.Everything) TweakViewMode = TweakView.Applicable;
        CurrentPage = Page.Tweaks;
        if (_tweakGroups.Any(g => g.Key == IgnoredGroup)) OpenTweakGroup(IgnoredGroup);
    }

    /// <summary>The facts do not depend on ignored tweaks: the profile view and every list are built again, nothing is read.</summary>
    private void SetTweakIgnored(string tweakId, bool ignored)
    {
        _settings.IgnoredTweaks.RemoveAll(id => id == tweakId);
        if (ignored) _settings.IgnoredTweaks.Add(tweakId);
        _settings.Save();
        ApplyProfile();
        Rebuild();
    }

    /// <summary>The facts change with the ignored results (what is recommended for them): read them again, then every list.</summary>
    private async Task AfterIgnoreChangedAsync()
    {
        await RefreshTweaksAsync();
        Rebuild();
    }
}
