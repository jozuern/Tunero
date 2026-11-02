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

/// <summary>
/// The filters a view of the Tweaks page stands for. Only the Everything view lists what does not apply to this PC, and
/// no view reads or writes "Show passed checks" of the Overview and Manual checks.
/// </summary>
internal readonly record struct TweakViewFlags(bool OnlyRecommended, bool OnlyChanged, bool OnlyProfile, bool ShowAll)
{
    public static TweakViewFlags For(TweakView view) =>
        new(view == TweakView.Recommended, view == TweakView.Changed, view == TweakView.Profile, view == TweakView.Everything);

    /// <summary>The view the filters show; a narrower filter wins over a wider one.</summary>
    public TweakView View => OnlyRecommended ? TweakView.Recommended
        : OnlyChanged ? TweakView.Changed
        : OnlyProfile ? TweakView.Profile
        : ShowAll ? TweakView.Everything
        : TweakView.Applicable;
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

    /// <summary>
    /// The Everything view: tweaks that do not apply to this PC are listed too. A setting of the Tweaks page only; "Show
    /// passed checks" on the Overview and Manual checks is another one, so neither switch changes the other page's list.
    /// </summary>
    private bool _tweaksShowAll;

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
    /// The view as the switches it replaces: Recommended and Profile keep their own properties (the overview, the search
    /// and the developer switches use them), so the choice is read from and written to them.
    /// </summary>
    public TweakView TweakViewMode
    {
        get => new TweakViewFlags(OnlyRecommended, _onlyChanged, OnlyProfile, _tweaksShowAll).View;
        set
        {
            if (value == TweakViewMode) return;
            var flags = TweakViewFlags.For(value);
            // The fields directly, then one refill: each property would rebuild the list on its own.
#pragma warning disable MVVMTK0034
            _onlyRecommended = flags.OnlyRecommended;
            _onlyProfile = flags.OnlyProfile;
#pragma warning restore MVVMTK0034
            _onlyChanged = flags.OnlyChanged;
            _tweaksShowAll = flags.ShowAll;
            OnPropertyChanged(nameof(OnlyRecommended));
            OnPropertyChanged(nameof(OnlyProfile));
            OnPropertyChanged();
            RefillTweaks();
            // The Overview's link counts the ignored recommendations of the view it opens.
            UpdateIgnoredTweaksText();
        }
    }
}
