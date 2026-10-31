using Optimizer.App.Services;

namespace Optimizer.App.ViewModels;

/// <summary>What the Tweaks page lists: one choice instead of three switches that overlapped.</summary>
public enum TweakView
{
    /// <summary>What the active profile rates (the default).</summary>
    Profile,

    /// <summary>Only what is recommended for this PC.</summary>
    Recommended,

    /// <summary>Only what this app changed (it has the original values).</summary>
    Changed,

    /// <summary>Everything that applies to this PC, whatever the profile.</summary>
    Applicable,

    /// <summary>Everything, also what does not apply to this PC.</summary>
    Everything,
}

public sealed record TweakViewOption(TweakView View, string Text)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Text;
}

/// <summary>The impact filter: tweaks rated at least <paramref name="Min"/> of 5 for the profile's goal (0 = all).</summary>
public sealed record TweakImpactOption(int Min, string Text)
{
    public override string ToString() => Text;
}

public sealed partial class MainViewModel
{
    /// <summary>"Profile: Gaming (change on the overview)": the profile is set in one place.</summary>
    public string TweaksProfileLink => Loc.Instance.Format("Tweaks_ProfileLink", ProfileName(_state.Usage));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenOverview() => CurrentPage = Page.Overview;

    /// <summary>Only tweaks this app changed (the Changed view).</summary>
    private bool _onlyChanged;

    /// <summary>The choices in the current language (built again after a language switch; the combo box binds the value).</summary>
    public IReadOnlyList<TweakViewOption> TweakViewOptions =>
    [
        new(TweakView.Profile, Loc.Instance["TweakView_Profile"]),
        new(TweakView.Recommended, Loc.Instance["TweakView_Recommended"]),
        new(TweakView.Changed, Loc.Instance["TweakView_Changed"]),
        new(TweakView.Applicable, Loc.Instance["TweakView_Applicable"]),
        new(TweakView.Everything, Loc.Instance["TweakView_Everything"]),
    ];

    /// <summary>"Any impact", "Impact 1 or more" up to "Impact 5": the rating the row's mark shows (the active profile's).</summary>
    public IReadOnlyList<TweakImpactOption> TweakImpactOptions =>
    [
        new(0, Loc.Instance["TweakImpact_Any"]),
        .. Enumerable.Range(1, 4).Select(n => new TweakImpactOption(n, Loc.Instance.Format("TweakImpact_AtLeast", n))),
        new(5, Loc.Instance["TweakImpact_Five"]),
    ];

    /// <summary>Only tweaks with at least this impact for the profile (0 = all). Not saved, like the view and the search.</summary>
    public int TweakImpactMin
    {
        get => _tweakImpactMin;
        set
        {
            value = Math.Clamp(value, 0, 5);
            if (value == _tweakImpactMin) return;
            _tweakImpactMin = value;
            OnPropertyChanged();
            RefillTweaks();
        }
    }

    private int _tweakImpactMin;

    /// <summary>
    /// The view as the switches it replaces: Recommended, Profile and the "does not apply" list keep their own properties
    /// (the overview, the search and the developer switches use them), so the choice is read from and written to them.
    /// </summary>
    public TweakView TweakViewMode
    {
        get => OnlyRecommended ? TweakView.Recommended
            : _onlyChanged ? TweakView.Changed
            : OnlyProfile ? TweakView.Profile
            : ShowPassed ? TweakView.Everything
            : TweakView.Applicable;
        set
        {
            if (value == TweakViewMode) return;
            var showPassed = value == TweakView.Everything;
            var rebuild = showPassed != ShowPassed;
            // The fields directly, then one refill: each property would rebuild the list on its own.
#pragma warning disable MVVMTK0034
            _onlyRecommended = value == TweakView.Recommended;
            _onlyProfile = value == TweakView.Profile;
            _showPassed = showPassed;
#pragma warning restore MVVMTK0034
            _onlyChanged = value == TweakView.Changed;
            OnPropertyChanged(nameof(OnlyRecommended));
            OnPropertyChanged(nameof(OnlyProfile));
            OnPropertyChanged(nameof(ShowPassed));
            OnPropertyChanged();
            // "Does not apply" also changes the overview's passed checks (one setting for both): a full rebuild then.
            if (rebuild) Rebuild();
            else RefillTweaks();
        }
    }
}
