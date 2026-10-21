using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

public enum RowAction { None, Apply, ApplyAgain, Undo }

/// <summary>What a picked row adds to the Tweaks page's selection: turning it on in one batch, or undoing it.</summary>
public enum PickKind { None, TurnOn, Undo }

/// <summary>One tweak row (card with a switch) and its inspector page, in the current language.</summary>
public sealed class TweakItemViewModel : InspectorItem
{
    private readonly IPageHost? _owner;
    private bool _switch;

    /// <param name="subjectAsTitle">Lists of one kind (games, devices): the subject is the title, the section explains the rest.</param>
    /// <param name="profiled">The status seen through the active profile: its impact, recommendation and "works against".</param>
    public TweakItemViewModel(TweakStatus status, string lang, TweakEngine engine, IPageHost? owner, bool subjectAsTitle = false,
        ProfiledTweak? profiled = null, UsageProfile? profile = null)
    {
        _owner = owner;
        Status_ = status;
        var t = status.Tweak;
        var labels = Labels.Current;
        var page = DocStore.GetOrEnglish(t.DocId, lang);
        var title = page?.Title ?? t.Id;
        Title = t.Subject is { Length: > 0 } s ? subjectAsTitle ? s : $"{title}: {s}" : title;
        Summary = subjectAsTitle && t.Subject is { Length: > 0 } ? "" : page?.Section(DocHeadings.Summary(page.Language))?.Body ?? "";
        Category = t.Category;

        StatusText = labels.Get(lang, $"state.{status.State}");
        Status = status.State switch
        {
            TweakState.Applied or TweakState.PendingRestart => "Ok",
            TweakState.Partial or TweakState.RevertedByWindows or TweakState.AppliedIneffective => "Problem",
            TweakState.Unsupported or TweakState.NotApplicable => "Unsupported",
            _ => "Neutral",
        };

        var impact = profiled?.Impact ?? status.Impact;
        var goal = profile?.Goal ?? "gaming";
        var catalogView = profile is null || profile.UsesCatalog;
        Impact = Math.Max(impact, 0);
        if (profiled is { WorksAgainst: true })
        {
            // Lowers the profile's goal: say so in the detail line and, while it is on, as a warning under the title.
            var why = profiled.ReasonKey is { } k ? labels.Get(lang, k) : "";
            ImpactText = Loc.Instance["Impact_Against"];
            ImpactTooltip = string.IsNullOrEmpty(why) ? ImpactText : $"{ImpactText}: {why}";
            AgainstNote = profiled.Flagged ? ImpactTooltip : null;
            EffectsText = "";
        }
        else
        {
            ImpactText = Loc.Instance.Format("Impact_Short", impact);
            var reason = catalogView && status.ImpactReasonKey is { } r ? ". " + labels.Get(lang, r) : "";
            ImpactTooltip = Loc.Instance.Format("Impact_TooltipGoal", labels.Get(lang, $"effect.{goal}"), impact) + reason;
            EffectsText = catalogView
                ? string.Join(", ", status.Effects.Select(e => labels.Get(lang, $"effect.{e}")))
                : impact > 0 ? labels.Get(lang, $"effect.{goal}") : "";
        }

        // Tags under the title; risks (caution color) first, then facts about the change.
        // Each tag explains itself in its tooltip.
        var badges = new List<Badge>();
        if (t.EffectiveRisk != Risk.Safe)
            badges.Add(new(labels.Get(lang, $"badge.risk{t.EffectiveRisk}"), true,
                t.EffectiveRisk == Risk.Expert ? Loc.Instance["Badge_TipExpert"] : Loc.Instance["Badge_TipModerate"]));
        if (t.IsBootCritical) badges.Add(new(labels.Get(lang, "badge.bootCritical"), true, Loc.Instance["Badge_TipBoot"]));
        if (t.AntiCheatSensitive) badges.Add(new(labels.Get(lang, "badge.antiCheat"), true, Loc.Instance["Badge_TipAntiCheat"]));
        if (t.Reversibility != Reversibility.Reversible)
            badges.Add(new(labels.Get(lang, $"reversibility.{t.Reversibility}"), true,
                t.Reversibility == Reversibility.Reinstall ? Loc.Instance["Badge_TipReinstall"] : Loc.Instance["Badge_TipPermanent"]));
        if (t.Preview) badges.Add(new(labels.Get(lang, "badge.preview"), false, Loc.Instance["Badge_TipPreview"]));
        if (t.Undocumented) badges.Add(new(labels.Get(lang, "badge.undocumented"), false, Loc.Instance["Badge_TipUndocumented"]));
        if (t.Impact.Basis == "disputed") badges.Add(new(labels.Get(lang, "badge.disputed"), false, Loc.Instance["Badge_TipDisputed"]));
        if (t.Restart) badges.Add(new(labels.Get(lang, "badge.restart"), false, Loc.Instance["Badge_TipRestart"]));
        if (t.SignOut) badges.Add(new(labels.Get(lang, "badge.signOut"), false, Loc.Instance["Badge_TipSignOut"]));
        if (t.TakesMinutes) badges.Add(new(labels.Get(lang, "badge.takesMinutes"), false, Loc.Instance["Badge_TipMinutes"]));
        Badges = badges;
        BadgesText = string.Join(", ", badges.Select(b => b.Text));
        IsRecommended = profiled?.Recommended ?? status.Recommended;
        IsIgnored = profiled?.Ignored == true;
        var recReason = profiled is null ? t.RecommendReasonKey : profiled.ReasonKey;
        RecommendedText = IsRecommended
            ? labels.Get(lang, "badge.recommended") + (recReason is { } rk ? ": " + labels.Get(lang, rk) : "")
            : null;

        // Hard blocks disable the switch; the anti-cheat block can be overridden in the confirmation dialog.
        var hard = status.Blocks.Where(b => !b.CanOverride).ToList();
        // "Needs administrator rights" applies to every row; the page shows it once as a banner instead.
        var first = status.Blocks.FirstOrDefault(b => b.ReasonKey != "block.notElevated");
        BlockText = first is null ? null : ChangeRunner.BlockText(first, lang);
        var canChange = status.State is not (TweakState.NotApplicable or TweakState.Unsupported or TweakState.EnforcedByPolicy);
        IsOn = status.IsOn;
        HasBackup = status.HasBackup;
        _switch = IsOn;
        // On: possible when not blocked. Off: possible when this app made the change (it has the original values).
        CanToggle = canChange && (IsOn ? HasBackup : hard.Count == 0);
        var outside = canChange && IsOn && !HasBackup ? Loc.Instance["Tweak_AlreadyOn"] : null;
        // Already on before this app: there is no original value to restore, so a switch could never be turned off.
        // Say that in words instead of showing a switch that looks broken.
        IsSetOutside = outside is not null;
        // On and off are what the switch shows, and "Already on" next to it says the rest (its tooltip explains it): the
        // row's line names a state only when it is something else (partly applied, reset by Windows, set by a policy).
        var stateShown = IsSetOutside || status.State is TweakState.NotApplied or TweakState.Applied;
        CompactMeta = stateShown ? "" : StatusText;
        // The badges are drawn as tags next to the title, and the impact as the rating mark beside the switch: neither is
        // repeated in this line, which keeps what the tweak affects. "Works against the profile" has no mark and stays
        // here. A tweak without any effect on the profile's goal (most privacy and Explorer settings) leaves "No FPS
        // effect" out too: the details still name it.
        var against = profiled is { WorksAgainst: true };
        var noEffect = Impact == 0 && !against;
        IsDisputed = t.Impact.Basis == "disputed";
        Rating = against ? null : Impact;
        MetaText = Meta(stateShown ? "" : StatusText, against ? ImpactText : null, noEffect ? "" : EffectsText);
        Note = IsSetOutside ? Loc.Instance["Tweak_AlreadyOnTip"] : null;
        // Only real blocks are shown as a warning; "set outside this app" is part of the detail line.
        ToggleNote = canChange && !IsOn && hard.Count > 0 ? BlockText : null;
        Action = ActionFor(status.State, canChange, HasBackup);
        ActionText = Action switch
        {
            RowAction.Undo => Loc.Instance["Tweak_Undo"],
            RowAction.Apply => Loc.Instance["Tweak_TurnOn"],
            RowAction.ApplyAgain => Loc.Instance["Tweak_Reapply"],
            _ => null,
        };
        ActionEnabled = Action == RowAction.Undo || hard.Count == 0;
        // Several at once go through the batch, which takes only what Apply recommended takes; the rest keeps its own
        // confirmation (its switch). A change this app made can always be undone with the others. Missing admin rights
        // block every row alike, as for the group's Turn on: the page says so once.
        var canTurnOn = canChange && !IsOn && hard.All(b => b.ReasonKey == "block.notElevated");
        Pick = canChange && HasBackup ? PickKind.Undo
            : canTurnOn && t.IsBatchSafe ? PickKind.TurnOn
            : PickKind.None;
        PickNote = Pick == PickKind.None && canTurnOn ? Loc.Instance["Tweaks_PickAlone"] : null;
        // In the details pane there is no page banner: missing admin rights are named here too.
        var adminNote = status.Blocks.Any(b => b.ReasonKey == "block.notElevated") ? ChangeRunner.BlockText(status.Blocks.First(b => b.ReasonKey == "block.notElevated"), lang) : null;
        ActionNote = Action is RowAction.Undo or RowAction.None ? null : BlockText ?? adminNote;

        // Inspector document: hand-written sections plus generated "What changes" and "Undo". Built when the row is
        // opened, because the preview reads the current values from the system.
        var heading = Title;
        MarkdownFactory = () =>
        {
            if (page is null) return $"# {heading}";
            // A break opportunity after each backslash, so a long registry path wraps at a key, not inside a name.
            var changes = ChangeRunner.ShortTargets(engine.Preview(t)).Select(c => c with { Target = c.Target.Replace("\\", "\\\u200B") }).ToList();
            var notes = new List<string>();
            if (t.Scope == TweakScope.User || t.Actions.OfType<Optimizer.Core.Actions.RegistryAction>().Any(a => a.Hive == Optimizer.Core.Actions.Hive.User))
                notes.Add(labels.Get(lang, "changes.userScope"));
            var undo = new List<string> { labels.Get(lang, t.Reversibility switch { Reversibility.Reinstall => "undo.reinstall", Reversibility.Permanent => "undo.permanent", _ => "undo.reversible" }) };
            if (t.Restart) undo.Add(labels.Get(lang, "undo.restart"));
            if (t.SignOut) undo.Add(labels.Get(lang, "undo.signOut"));
            if (t.IsBootCritical) undo.Add(labels.Get(lang, "undo.bootCritical"));
            if (t.Preview) undo.Add(labels.Get(lang, "preview.warning"));
            return $"# {heading}\n\n" + DocStore.RenderTweak(page, changes, notes, string.Join(" ", undo));
        };
    }

    /// <summary>
    /// What the row's button does. Reset by Windows comes first: such a tweak has a backup, but the button applies it
    /// again (as its label says); Undo stays available on the Changes page.
    /// </summary>
    public static RowAction ActionFor(TweakState state, bool canChange, bool hasBackup) =>
        !canChange ? RowAction.None
        : state == TweakState.RevertedByWindows ? RowAction.ApplyAgain
        : hasBackup ? RowAction.Undo
        : state is TweakState.Applied or TweakState.PendingRestart or TweakState.AppliedIneffective ? RowAction.None
        : RowAction.Apply;

    public RowAction Action { get; }

    public PickKind Pick { get; }

    /// <summary>Why the row cannot be picked although it can be turned on (it needs its own confirmation).</summary>
    public string? PickNote { get; }

    public bool CanPick => Pick != PickKind.None;

    /// <summary>Tweaks page in Select mode: the row shows its check box (the switch stays).</summary>
    public bool ShowPick
    {
        get => _showPick;
        set => SetProperty(ref _showPick, value);
    }

    /// <summary>The row's check box; the page keeps the picks by tweak id, so a rebuild after a scan keeps them.</summary>
    public bool IsPicked
    {
        get => _picked;
        set
        {
            if (value && !CanPick) value = false;
            if (SetProperty(ref _picked, value)) PickChanged?.Invoke(this);
        }
    }

    /// <summary>Set by the Tweaks page for its own rows.</summary>
    internal Action<TweakItemViewModel>? PickChanged { get; set; }

    public string PickLabel => Loc.Instance.Format("Programs_SelectName", Title);

    private bool _showPick, _picked;

    public TweakStatus Status_ { get; }
    public TweakDefinition Tweak => Status_.Tweak;
    public string Category { get; }
    public string BadgesText { get; }
    public bool IsRecommended { get; }
    public string? RecommendedText { get; }

    /// <summary>Its recommendation is ignored: the details pane offers Stop ignoring.</summary>
    public bool IsIgnored { get; }

    /// <summary>Recommended, in the Overview's plan or flagged against the profile: the details pane offers to ignore that.</summary>
    public bool OffersIgnore { get; init; }

    public string IgnoreText => Loc.Instance["Tweaks_Ignore"];
    public string? BlockText { get; }
    public bool IsOn { get; }
    public bool HasBackup { get; }
    public bool CanToggle { get; }

    /// <summary>On, but not changed by this app (Windows default, policy or another tool): shown as text, not as a switch.</summary>
    public bool IsSetOutside { get; }

    public bool ShowSwitch => !IsSetOutside;

    /// <summary>Rows in lists of one kind (games, devices): only a state worth reading; the section names impact and tags.</summary>
    public string CompactMeta { get; }
    public string? ToggleNote { get; }

    /// <summary>Shown while the tweak is on and works against the active profile (for example costs battery life).</summary>
    public string? AgainstNote { get; }

    public override string Key => "tweak:" + Tweak.Id;

    /// <summary>
    /// Bound two-way to the row's switch. Setting it starts apply (on) or undo (off); the switch then shows the real
    /// state again (cancelled = it flips back, done = the rescan rebuilds the row).
    /// </summary>
    public bool SwitchState
    {
        get => _switch;
        set
        {
            if (value == _switch) return;
            _switch = value;
            OnPropertyChanged();
            if (_owner is not null && value != IsOn) _owner.ToggleAsync(this, value).Forget("tweak switch");
            else ResetSwitch();
        }
    }

    /// <summary>Shows the real state again; after a change that was made, its new state until the rescan rebuilds the row.</summary>
    public void ResetSwitch(bool? changedTo = null)
    {
        _switch = changedTo ?? IsOn;
        OnPropertyChanged(nameof(SwitchState));
    }
}

/// <summary>
/// A tag on a tweak row (Preview, Restart, Moderate risk); <paramref name="Caution"/> draws it in the caution color,
/// <paramref name="Tip"/> says what it means.
/// </summary>
public sealed record Badge(string Text, bool Caution, string? Tip = null)
{
    // What screen readers announce for this item in a list.
    public override string ToString() => Text;
}
