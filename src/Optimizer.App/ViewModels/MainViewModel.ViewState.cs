using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Recreates all language-dependent items (after a scan, a language switch, a profile or Expert mode switch).</summary>
    public void Rebuild()
    {
        var selectedKey = SelectedItem?.Key;

        BuildProfileOptions();
        BuildFindingLists();
        OnPropertyChanged(nameof(TweakViewOptions));
        OnPropertyChanged(nameof(TweakImpactOptions));
        OnPropertyChanged(nameof(TweakImpactMin));
        OnPropertyChanged(nameof(TweaksProfileLink));
        OnPropertyChanged(nameof(TweakViewMode));

        FillTweaks();
        BuildRecommendations();
        BuildBanners();
        BuildChanges();
        BuildHardware();

        RestoreSelection(selectedKey);
        OnPropertyChanged(nameof(AboutText));
    }

    private (HardwareProfile? Profile, string Language) _hardwareShown;

    /// <summary>
    /// The hardware page shows the scan result only (no profile, Expert mode or theme brushes): built again for a new
    /// scan or language, so a switch elsewhere does not recreate its rows.
    /// </summary>
    private void BuildHardware()
    {
        var shown = (Profile, Loc.Instance.Language);
        if (ReferenceEquals(_hardwareShown.Profile, shown.Profile) && _hardwareShown.Language == shown.Language) return;
        _hardwareShown = shown;
        HardwareSections.Clear();
        HardwareTiles.Clear();
        if (Profile is null) return;
        foreach (var s in HardwareReport.Build(Profile, Loc.Instance)) HardwareSections.Add(s);
        foreach (var t in HardwareReport.Tiles(Profile, Loc.Instance)) HardwareTiles.Add(t);
    }

    /// <summary>
    /// Only the findings changed (the background CPU sample): the finding lists, score and plan are built again, while
    /// the tweak, network and hardware rows stay, so pages that show them need not draw again.
    /// </summary>
    private void RebuildFindings()
    {
        var selectedKey = SelectedItem?.Key;
        BuildFindingLists();
        BuildRecommendations();
        BuildBanners();
        RestoreSelection(selectedKey);
    }

    private void RestoreSelection(string? key) =>
        SelectedItem = key is null ? null :
            Findings.Cast<InspectorItem>().Concat(AdvisorItems).Concat(IgnoredItems).Concat(Tweaks).Concat(Network.DeviceTweaks).Append(GameAccess).FirstOrDefault(i => i?.Key == key);

    /// <summary>The finding and advisor lists, the score and the counts on the overview.</summary>
    private void BuildFindingLists()
    {
        var lang = Loc.Instance.Language;
        Fill(Findings, _state.ProfiledFindings.Where(f => f.Kind == FindingKind.Finding), lang);
        Fill(AdvisorItems, _state.ProfiledFindings.Where(f => f.Kind == FindingKind.Advisor), lang);
        OnPropertyChanged(nameof(FindingsEmpty));
        OnPropertyChanged(nameof(AdvisorEmpty));
        var access = _state.ProfiledFindings.FirstOrDefault(f => f.Kind == FindingKind.GameAccess);
        GameAccess = access is null ? null : new FindingItemViewModel(access, lang);
        BuildIgnored(lang);
        var hidden = ProfileView.HiddenProblems(_state.Usage, IgnoredResults.Without(_state.Findings, _state.IgnoredKeys()));
        HiddenFindingsText = hidden > 0 ? Loc.Instance.Format("Findings_HiddenByProfile", hidden) : null;
        var passed = ShowPassed ? 0 : _state.ProfiledFindings.Count(f => f.Kind == FindingKind.Advisor && f.Status is FindingStatus.Ok or FindingStatus.Unsupported);
        AdvisorPassedText = passed > 0 ? Loc.Instance.Format("Advisor_PassedHidden", passed) : null;
        BuildAdvisorSummary();

        // The score counts what matters for the profile, weighted by the profile's impact.
        Score = ReadinessScore.Compute(_state.ProfiledFindings);
        ScoreHint = Loc.Instance.Format("Dash_ScoreHintGoal", Labels.Current.Get(lang, $"effect.{_state.Usage.Goal}"));
        var findingProblems = _state.ProfiledFindings.Count(f => f.IsProblem && f.Kind == FindingKind.Finding);
        var advisorProblems = _state.ProfiledFindings.Count(f => f.IsProblem && f.Kind == FindingKind.Advisor);
        // The verdict counts the problems listed right under it (BIOS and hardware have their own page and the link); it
        // is set once the recommendations are known (UpdateVerdict). The line under it says what the score counts.
        _findingProblems = findingProblems;
        // Checks that could not be answered are named: "100 of 100" next to a "?" card needs the count to make sense.
        var passedChecks = _state.ProfiledFindings.Count(f => f.Status == FindingStatus.Ok);
        var unknownChecks = _state.ProfiledFindings.Count(f => f.Status == FindingStatus.Unknown);
        ReadinessLine = unknownChecks > 0
            ? Loc.Instance.Format("Dash_ReadinessUnknown", ProfileName(_state.Usage), Score, passedChecks, unknownChecks)
            : Loc.Instance.Format("Dash_Readiness", ProfileName(_state.Usage), Score, passedChecks);
        AdvisorLinkText = advisorProblems > 0 ? Loc.Instance.Format("Dash_AdvisorLink", advisorProblems) : null;
        // The navigation shows where something waits: problems on the overview, things to change by hand.
        OverviewBadge = findingProblems > 0 ? findingProblems.ToString() : null;
        AdvisorBadge = advisorProblems > 0 ? advisorProblems.ToString() : null;
        LastScanText = _state.LastScanTime is { } t ? Loc.Instance.Format("LastScan", t.ToString("HH:mm")) : "";
        if (Profile is { } prof)
            Headline = string.Join(", ", new[] { prof.Cpu?.ShortName, prof.Gpus?.FirstOrDefault(g => g.Kind == GpuKind.Discrete)?.Name ?? prof.Gpus?.FirstOrDefault()?.Name,
                $"Windows 11 {prof.Os.DisplayVersion}" }.Where(x => !string.IsNullOrEmpty(x)));
    }

    private void Fill(ObservableCollection<FindingItemViewModel> target, IEnumerable<Finding> source, string lang)
    {
        target.Clear();
        // Problems first, then what could not be checked, then notes and passed checks (stable within each).
        static int Rank(FindingStatus s) => s switch
        {
            FindingStatus.Problem => 0,
            FindingStatus.Unknown => 1,
            FindingStatus.Info => 2,
            FindingStatus.Ok => 3,
            _ => 4,
        };
        foreach (var f in source.Where(f => ShowPassed || f.Status is not (FindingStatus.Ok or FindingStatus.Unsupported)).OrderBy(f => Rank(f.Status)))
            target.Add(new FindingItemViewModel(f, lang, FixFor(f), _state.Usage.Goal));
    }

    /// <summary>Runtime fix of the finding, or the first catalog tweak that fixes it and is not on yet.</summary>
    private TweakDefinition? FixFor(Finding f) =>
        f.Fix is { } fix
            ? ProfileView.RuntimeFixAllowed(fix, _state.Profiled) ? fix : null
            : _state.Profiled.FirstOrDefault(p => p.CanFix(f.Id) && (ExpertMode || p.Tweak.EffectiveRisk != Risk.Expert))?.Tweak;

    /// <param name="profileFilter">Tweaks page: only what the active profile rates (unless "Only this profile" is off).</param>
    private IEnumerable<ProfiledTweak> VisibleTweaks(bool profileFilter = true) =>
        _state.Profiled.Where(p => ExpertMode || p.Tweak.EffectiveRisk != Risk.Expert || p.Status.HasBackup)
                 .Where(p => p.Status.State != TweakState.NotApplicable || ShowPassed)
                 .Where(p => !profileFilter || !OnlyProfile || p.Relevant);

    private const string RecommendedGroup = "recommended";
    private const string AgainstGroup = "against";
    private const string IgnoredGroup = "ignored";

    private readonly List<TweakGroupRow> _tweakGroups = [];

    /// <summary>Groups open on the Tweaks page this session: recommended and against the profile start open, the sections closed (all open in a short list).</summary>
    private readonly HashSet<string> _openGroups = [RecommendedGroup, AgainstGroup];

    /// <summary>Groups the user closed this session: a short list opens every group, except these.</summary>
    private readonly HashSet<string> _closedGroups = [];

    /// <summary>Up to this many tweaks in the list, every group starts open: a few closed cards showed nothing to act on.</summary>
    private const int OpenAllUpTo = 15;

    /// <summary>The sections of the Tweaks page (<see cref="Optimizer.Core.Tweaks.TweakSections"/>) with their icons.</summary>
    private static readonly (string Key, SymbolRegular Icon, string[] Categories)[] TweakSections =
        Optimizer.Core.Tweaks.TweakSections.All.Select(s => (s.Key, SectionIcon(s.Key), s.Categories)).ToArray();

    private static SymbolRegular SectionIcon(string key) => key switch
    {
        "performance" => SymbolRegular.Flash24,
        "network" => SymbolRegular.Router24,
        "background" => SymbolRegular.Alert24,
        "privacy" => SymbolRegular.EyeOff24,
        "security" => SymbolRegular.Shield24,
        "desktop" => SymbolRegular.Window24,
        "browser" => SymbolRegular.Globe24,
        _ => SymbolRegular.Apps24,
    };

    private static int SectionIndex(string category) => Optimizer.Core.Tweaks.TweakSections.IndexOf(category);

    private Dictionary<string, int>? _categoryOrder;

    /// <summary>
    /// Fills the Tweaks page: recommended first, then applied changes that work against the profile, then the sections,
    /// then the ignored recommendations (closed). In a group the larger impact first, then by category in catalog order,
    /// so related tweaks stay together. A search opens every group with a match.
    /// </summary>
    private void FillTweaks()
    {
        var lang = Loc.Instance.Language;
        var q = TweakSearch.Trim();
        bool Matches(ProfiledTweak p) => q.Length == 0 || p.Tweak.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Runner.Title(p.Tweak).Contains(q, StringComparison.CurrentCultureIgnoreCase) || Runner.Summary(p.Tweak).Contains(q, StringComparison.CurrentCultureIgnoreCase);
        _categoryOrder ??= _services.Catalog.Tweaks.Select(t => t.Category).Distinct()
            .Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        int CategoryOrder(string category) => _categoryOrder.TryGetValue(category, out var i) ? i : int.MaxValue;
        int Order(string key) => key switch
        {
            RecommendedGroup => -2,
            AgainstGroup => -1,
            IgnoredGroup => int.MaxValue,
            _ => Array.FindIndex(TweakSections, s => s.Key == key),
        };

        Tweaks.Clear();
        _tweakGroups.Clear();
        var groups = VisibleTweaks()
            .Where(p => !OnlyRecommended || p.Recommended)
            .Where(p => !_onlyChanged || p.Status.HasBackup)
            // "Works against the profile" (a negative impact) has no rating: an impact filter leaves it out.
            .Where(p => _tweakImpactMin == 0 || p.Impact >= _tweakImpactMin)
            // DNS servers are one choice, made on Graphics & DNS: not a second list of tweaks here.
            .Where(p => !p.Tweak.Id.StartsWith("network.dns.", StringComparison.Ordinal))
            .Where(Matches)
            .GroupBy(p => p.Ignored ? IgnoredGroup : p.Recommended ? RecommendedGroup : p.Flagged ? AgainstGroup : TweakSections[SectionIndex(p.Tweak.Category)].Key)
            .OrderBy(g => Order(g.Key))
            .ToList();
        var fewTweaks = groups.Sum(g => g.Count()) <= OpenAllUpTo;
        var inPlan = PlanTweakIds();
        foreach (var g in groups)
        {
            var items = g.OrderByDescending(p => p.Impact)
                .ThenBy(p => CategoryOrder(p.Tweak.Category))
                .Select(p => new TweakItemViewModel(p.Status, lang, _services.Engine, this, profiled: p, profile: _state.Usage) { OffersIgnore = OffersIgnore(p, inPlan) })
                .ToList();
            foreach (var item in items)
            {
                ShowPick(item);
                Tweaks.Add(item);
            }
            // "12 of 34 on, 3 by this app": the same split as the page summary; "0 by this app" is left out.
            var byApp = items.Count(i => i.IsOn && i.HasBackup);
            var summary = Loc.Instance.Format("Tweaks_GroupCount", items.Count, items.Count(i => i.IsOn))
                + (byApp > 0 ? ", " + Loc.Instance.Format("Tweaks_CountByApp", byApp) : "");
            _tweakGroups.Add(new TweakGroupRow(g.Key, GroupTitle(g.Key), GroupIcon(g.Key), summary, items)
            {
                IsExpanded = q.Length > 0 || _openGroups.Contains(g.Key) || (fewTweaks && !_closedGroups.Contains(g.Key)),
            });
        }
        ShowTweakRows();
        UpdatePicks();
        var visible = VisibleTweaks().ToList();
        // "On" alone would count settings that were already that way as changes of this app. Counts of zero are left out:
        // "202 tweaks: 91 already set" reads, "0 changed by this app, 0 recommended" was noise.
        var parts = new[]
        {
            (Count: visible.Count(p => p.Status.IsOn && p.Status.HasBackup), Key: "Tweaks_CountByApp"),
            (Count: visible.Count(p => p.Status.IsOn && !p.Status.HasBackup), Key: "Tweaks_CountAlreadySet"),
            (Count: visible.Count(p => p.Recommended), Key: "Tweaks_CountRecommended"),
        }.Where(x => x.Count > 0).Select(x => Loc.Instance.Format(x.Key, x.Count)).ToList();
        var total = visible.Count == 1 ? Loc.Instance["Tweaks_SummaryOne"] : Loc.Instance.Format("Tweaks_Summary", visible.Count);
        TweaksSummary = parts.Count == 0 ? total : $"{total}: {string.Join(", ", parts)}";
    }

    /// <summary>The tweaks of the Overview's plan, fixes and what it leaves out included.</summary>
    private HashSet<string> PlanTweakIds() => _state.Plan.Items.Concat(_state.Plan.Excluded).Select(i => i.Tweak.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>What the app urges for this PC (recommended, in the plan, flagged against the profile) can be ignored.</summary>
    private static bool OffersIgnore(ProfiledTweak p, HashSet<string> inPlan) =>
        !p.Ignored && (p.Recommended || p.Flagged || inPlan.Contains(p.Tweak.Id));

    private static string GroupTitle(string key) => key switch
    {
        RecommendedGroup => Loc.Instance["Tweaks_GroupRecommended"],
        AgainstGroup => Loc.Instance["Tweaks_GroupAgainstOn"],
        IgnoredGroup => Loc.Instance["Tweaks_GroupIgnored"],
        "performance" => Loc.Instance["Tweaks_SectionPerformance"],
        "network" => Loc.Instance["Tweaks_SectionNetwork"],
        "background" => Loc.Instance["Tweaks_SectionBackground"],
        "privacy" => Loc.Instance["Tweaks_SectionPrivacy"],
        "security" => Loc.Instance["Tweaks_SectionSecurity"],
        "desktop" => Loc.Instance["Tweaks_SectionDesktop"],
        _ => Loc.Instance["Tweaks_SectionBrowser"],
    };

    private static SymbolRegular GroupIcon(string key) => key switch
    {
        RecommendedGroup => SymbolRegular.ThumbLike24,
        AgainstGroup => SymbolRegular.Warning24,
        IgnoredGroup => SymbolRegular.EyeOff24,
        _ => TweakSections.First(s => s.Key == key).Icon,
    };

    private void ShowTweakRows()
    {
        TweakRows.Clear();
        foreach (var g in _tweakGroups)
        {
            TweakRows.Add(g);
            if (g.IsExpanded)
                foreach (var t in g.Items) TweakRows.Add(t);
        }
    }

    /// <summary>
    /// Opens or closes one group; only its rows are added or removed, so the list keeps its scroll position. Takes any
    /// row for the same reason as <see cref="Select"/>.
    /// </summary>
    [RelayCommand]
    private void ToggleTweakGroup(object? row)
    {
        if (row is not TweakGroupRow group) return;
        group.IsExpanded = !group.IsExpanded;
        RememberGroup(group.Key, group.IsExpanded);
        var at = TweakRows.IndexOf(group) + 1;
        if (at == 0) return;
        if (group.IsExpanded)
            for (var i = 0; i < group.Items.Count; i++) TweakRows.Insert(at + i, group.Items[i]);
        else
            for (var i = 0; i < group.Items.Count; i++) TweakRows.RemoveAt(at);
    }

    /// <summary>
    /// Shows a file selected in Explorer, or opens a folder (Settings, Diagnostics, the install media result). Explorer runs
    /// as the signed-in user; nothing in the folder is started. A missing path or an Explorer that did not start is named.
    /// </summary>
    [RelayCommand]
    private void ShowInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ExplorerShell.ShowInExplorer(this, path, _services.Elevation, openFolder: true);
    }

    [RelayCommand]
    private void DismissIntro()
    {
        _settings.IntroSeen = true;
        _settings.Save();
        BuildBanners();
    }

    /// <summary>From a page that needs a setting (Expert mode, the VirusTotal key) to Settings.</summary>
    [RelayCommand]
    private void OpenSettings() => CurrentPage = Page.Settings;

    /// <summary>Turns on the safe tweaks of one group after one confirmation that lists every change.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ApplyTweakGroupAsync(object? row)
    {
        if (row is not TweakGroupRow { CanApplyAll: true } group) return;
        await Runner.ApplyBatchAsync(group.Applicable.Select(i => i.Tweak).ToList(), Loc.Instance.Format("Tweaks_GroupApplyTitle", group.Title),
            Loc.Instance["Tweaks_GroupApplyIntro"]);
    }

    [RelayCommand]
    private void OpenAllTweakGroups() => SetAllTweakGroups(true);

    [RelayCommand]
    private void CloseAllTweakGroups() => SetAllTweakGroups(false);

    private void SetAllTweakGroups(bool open)
    {
        foreach (var g in _tweakGroups)
        {
            g.IsExpanded = open;
            RememberGroup(g.Key, open);
        }
        ShowTweakRows();
    }

    private void RememberGroup(string key, bool open)
    {
        if (open)
        {
            _openGroups.Add(key);
            _closedGroups.Remove(key);
        }
        else
        {
            _openGroups.Remove(key);
            _closedGroups.Add(key);
        }
    }

    /// <summary>Developer switch --category: opens that group on the Tweaks page (a section key or a catalog category).</summary>
    public void OpenTweakGroup(string key)
    {
        // A catalog category opens its section; a section key or another group that is not shown opens nothing.
        if (_tweakGroups.All(g => g.Key != key) && Optimizer.Core.Tweaks.TweakSections.Contains(key)) key = TweakSections[SectionIndex(key)].Key;
        if (_tweakGroups.FirstOrDefault(g => g.Key == key) is { IsExpanded: false } group) ToggleTweakGroup(group);
    }

    /// <summary>Rows for catalog tweaks shown on other pages (GPU settings, DNS), with the same Expert filter as the Tweaks page.</summary>
    public IEnumerable<TweakItemViewModel> CatalogItems(Func<TweakDefinition, bool> filter)
    {
        var inPlan = PlanTweakIds();
        return VisibleTweaks(profileFilter: false).Where(p => filter(p.Tweak) && p.Status.State != TweakState.NotApplicable)
            .Select(p => new TweakItemViewModel(p.Status, Loc.Instance.Language, _services.Engine, this, profiled: p, profile: _state.Usage) { OffersIgnore = OffersIgnore(p, inPlan) })
            .ToList();
    }

    /// <summary>The button counts the ticked lines of the plan.</summary>
    private void UpdateRecommendedButton() =>
        RecommendedButtonText = Loc.Instance.Format("Rec_Button", Recommendations.Count(r => r.IsSelected));

    private void BuildRecommendations()
    {
        var lang = Loc.Instance.Language;
        string Reason(RecommendedItem i) =>
            i.FixesFinding is { } f ? Loc.Instance.Format("Rec_Fixes", new FindingItemViewModel(f, lang).Title)
            : i.ReasonKey is { } k ? Labels.Current.Get(lang, k) : Loc.Instance["Rec_Matches"];
        string Excluded(RecommendedItem i) =>
            i.Blocked is { } b ? ChangeRunner.BlockText(b, lang)
            : i.Tweak.EffectiveRisk == Risk.Expert ? Labels.Current.Get(lang, "risk.Expert")
            : i.Tweak.AntiCheatSensitive ? Labels.Current.Get(lang, "badge.antiCheat")
            : Labels.Current.Get(lang, $"reversibility.{i.Tweak.Reversibility}");
        // The eye ignores the line's tweak only, a fix's too: the problem it fixes stays listed and counted (its details can
        // ignore it). A fix built for this PC has no button: it is no catalog tweak the Tweaks page could list again.
        RecommendationLine WithIgnore(RecommendationLine line, RecommendedItem i)
        {
            if (_state.Profiled.Any(p => p.Tweak.Id == i.Tweak.Id)) line.IgnoreTweakId = i.Tweak.Id;
            return line;
        }
        Recommendations.Clear();
        foreach (var i in _state.Plan.Items)
            Recommendations.Add(WithIgnore(new RecommendationLine(Runner.Title(i.Tweak), Reason(i), i.Impact, i.Tweak, UpdateRecommendedButton), i));
        RecommendationsExcluded.Clear();
        foreach (var i in _state.Plan.Excluded)
            RecommendationsExcluded.Add(WithIgnore(new RecommendationLine(Runner.Title(i.Tweak), Excluded(i), i.Impact, i.Tweak), i));
        // As many as the Tweaks page lists in the view the link opens.
        var ignoredTweaks = VisibleTweaks(profileFilter: false).Count(p => p.Ignored);
        IgnoredTweaksText = ignoredTweaks > 0 ? Loc.Instance.Format("Tweaks_IgnoredCount", ignoredTweaks) : null;
        HasRecommendations = _state.Plan.Items.Count > 0;
        // Nothing to apply in one click, but fixes that need something first: say so instead of "nothing to recommend".
        RecommendationsEmptyText = _state.Plan.Excluded.Count > 0 ? Loc.Instance["Rec_EmptyButExcluded"] : Loc.Instance["Rec_Empty"];
        UpdateRecommendedButton();
        RecommendationsIntro = Loc.Instance.Format("Rec_IntroProfile", ProfileName(_state.Usage));

        // Changes on this PC that lower the profile's goal (for example a battery drain in the Battery profile).
        AgainstProfile.Clear();
        foreach (var p in _state.Profiled.Where(p => p.Flagged).OrderBy(p => p.Impact))
            AgainstProfile.Add(new AgainstLine(p.Tweak.Id, Runner.Title(p.Tweak), p.ReasonKey is { } k ? Labels.Current.Get(lang, k) : "", p.Status.HasBackup));
        HasAgainst = AgainstProfile.Count > 0;
        AgainstTitle = Loc.Instance.Format("Rec_AgainstTitle", ProfileName(_state.Usage));
        // After the lines against the profile: the verdict names them when no problem is found.
        UpdateVerdict();
    }

    /// <summary>Opens a tweak in the details pane on the Tweaks page, as the search does.</summary>
    [RelayCommand]
    private void ShowTweak(string? tweakId) => RevealTweak(tweakId);

    /// <summary>
    /// Goes to a tweak on the Tweaks page and opens it in the details pane (the search, Details on the Overview). The
    /// filters that could hide it are cleared first: the impact filter (a tweak that works against the profile has no
    /// rating), the narrow views and the search, and a tweak that does not apply here gets the view that lists it. A tweak
    /// the Expert filter leaves out of the list still opens in the pane, so the click is never lost.
    /// </summary>
    private void RevealTweak(string? tweakId)
    {
        if (_state.Profiled.FirstOrDefault(p => p.Tweak.Id == tweakId) is not { } p) return;
        if (p.Tweak.Id.StartsWith("network.dns.", StringComparison.Ordinal))
        {
            // DNS servers are chosen on Graphics & DNS, not listed on the Tweaks page.
            CurrentPage = Page.Network;
            return;
        }
        TweakImpactMin = 0;
        if (p.Status.State == TweakState.NotApplicable) TweakViewMode = TweakView.Everything;
        else if (TweakViewMode != TweakView.Everything) TweakViewMode = TweakView.Applicable;
        var title = Runner.Title(p.Tweak);
        TweakSearch = title;
        CurrentPage = Page.Tweaks;
        SelectedItem = Tweaks.FirstOrDefault(t => t.Tweak.Id == p.Tweak.Id)
            ?? new TweakItemViewModel(p.Status, Loc.Instance.Language, _services.Engine, this, profiled: p, profile: _state.Usage) { OffersIgnore = OffersIgnore(p, PlanTweakIds()) };
    }

    public void BuildChanges()
    {
        var lang = Loc.Instance.Language;
        ChangeRecords.Clear();
        foreach (var b in _backups.OrderByDescending(b => b.LastApplied))
        {
            var t = _services.Catalog.Get(b.TweakId) ?? _state.Findings.Select(f => f.Fix).FirstOrDefault(f => f?.Id == b.TweakId) ?? _backupTweaks.GetValueOrDefault(b.TweakId);
            var title = t is null ? b.TweakId : Runner.Title(t);
            if (t is not null) ChangeLogText.Remember(t);
            var state = _state.TweakStates.Concat(_state.DeviceStates).FirstOrDefault(s => s.Tweak.Id == b.TweakId)?.State
                ?? (_otherChangeStates.TryGetValue(b.TweakId, out var other) ? other : null);
            var drift = _state.Drift.FirstOrDefault(d => d.Tweak.Id == b.TweakId);
            if (drift is not null) state = TweakState.RevertedByWindows;
            ChangeRecords.Add(new ChangeRecordItem(
                b.TweakId,
                title,
                b.LastApplied.LocalDateTime.ToString("g"),
                Loc.Instance.Format("Changes_Entries", b.Entries.Count),
                state is null ? "" : Labels.Current.Get(lang, $"state.{state}"),
                state is TweakState.RevertedByWindows ? "Problem" : "Ok",
                t is not null)
            {
                ResetText = drift is null ? null : DriftReason(drift),
            });
        }
        OnPropertyChanged(nameof(ChangesEmpty));

        var sr = _restoreEnabled;
        RestorePointText = sr switch { true => Loc.Instance["Rp_On"], false => Loc.Instance["Rp_Off"], _ => Loc.Instance["Rp_Unknown"] };
        CanEnableRestorePoints = sr == false && IsElevated;
        RestorePointIcon = sr switch { true => SymbolRegular.ShieldCheckmark24, false => SymbolRegular.ShieldDismiss24, _ => SymbolRegular.Shield24 };

        ChangeLog.Clear();
        foreach (var e in Log.Newest(e => e.Source == "change", 200))
            ChangeLog.Add(new LogLine(e.Time.ToString("HH:mm:ss"), ChangeLogText.Text(e, Loc.Instance.Language)));
        OnPropertyChanged(nameof(ChangeLogEmpty));
    }

    private bool? SafeRestoreEnabled()
    {
        if (!IsElevated) return null; // System Restore WMI needs admin rights; never guess "off"
        try
        {
            return _services.RestorePoints.IsEnabled();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Developer aid for screenshots: shows the drift banner for three catalog tweaks without touching anything.</summary>
    public void PreviewDrift()
    {
        _state.PreviewDrift(_services.Catalog.Tweaks.Where(t => t.IsBatchSafe).Take(3)
            .Select(t => new DriftItem(t, new Optimizer.Core.Backup.TweakBackup { TweakId = t.Id }, "26300.9000", _services.Os.BuildString))
            .ToList());
        BuildBanners();
    }

    private void BuildBanners()
    {
        Banners.Clear();
        if (Profile is null) return;
        // First start: how the app works, until the user dismisses it.
        if (!_settings.IntroSeen)
            Banners.Add(new Banner(Loc.Instance["Intro_Text"], false) { ActionText = Loc.Instance["Intro_Dismiss"], Action = DismissIntroCommand, ActionIcon = SymbolRegular.Checkmark20 });
        if (_state.Drift.Count > 0)
        {
            var names = _state.Drift.Take(4).Select(d => Runner.Title(d.Tweak)).ToList();
            // "; " keeps titles with quotes, commas or parentheses apart.
            var list = string.Join("; ", names) + (_state.Drift.Count > names.Count ? " " + Loc.Instance.Format("Drift_More", _state.Drift.Count - names.Count) : "");
            var update = _state.Drift.FirstOrDefault(d => d.WindowsUpdatedSince);
            var text = update is not null ? Loc.Instance.Format("Drift_BannerUpdate", update.Current, list) : Loc.Instance.Format("Drift_BannerOther", list);
            Banners.Add(new Banner(text, true) { ActionText = Loc.Instance["Drift_ApplyAgain"], Action = ReapplyDriftCommand });
        }
        else if (_state.UpdateNotice is { } notice)
        {
            Banners.Add(new Banner(Loc.Instance.Format("WinUpdated_AllGood", notice.Last, notice.Current, notice.Backups), false));
        }
        if (UpdateAvailable)
            Banners.Add(CanUpdateInApp
                ? new Banner(UpdateStatus!, false) { ActionText = Loc.Instance["Update_Install"], Action = UpdateNowCommand, ActionIcon = SymbolRegular.ArrowDownload20 }
                : new Banner(UpdateStatus!, false) { ActionText = Loc.Instance["Update_Open"], Action = OpenReleasePageCommand, ActionIcon = SymbolRegular.Open20 });
        if (_state.ProfileAutoSet) Banners.Add(new Banner(Loc.Instance.Format("Profile_AutoSet", ProfileName(_state.Usage)), false));
        var pending = _state.TweakStates.Concat(_state.DeviceStates).Count(s => s.State is TweakState.PendingRestart or TweakState.UndoPendingRestart);
        if (pending > 0) Banners.Add(new Banner(Loc.Instance.Format("Banner_Restart", pending), true));
        var os = Profile.Os;
        var gate = OsGate.Evaluate(os.Build, os.NativeArchitecture);
        if (gate == OsGateResult.SupportedNotValidated)
            Banners.Add(new Banner(Loc.Instance.Format("Banner_NotValidated", os.BuildString), true));
        if (gate == OsGateResult.Unsupported)
            Banners.Add(new Banner(Loc.Instance["Banner_Unsupported"], true));
        if (os.FlightingActive) Banners.Add(new Banner(Loc.Instance.Format("Banner_Insider", os.FlightingDetail), false));
        if (Profile.Managed?.IsManaged == true) Banners.Add(new Banner(Loc.Instance["Banner_Managed"], true));
        if (Profile.Elevation is { } e)
        {
            if (!e.IsElevated) Banners.Add(new Banner(Loc.Instance["Banner_NotElevated"], true));
            // The signed-in account Windows cannot look up: user settings go to the account the app runs as, and the banner says so.
            var account = e.Banner switch
            {
                AccountBanner.SeparateAdmin => "Banner_SeparateAdmin",
                AccountBanner.UnresolvedUser => "Banner_UnresolvedUser",
                _ => null,
            };
            if (account is not null) Banners.Add(new Banner(Loc.Instance.Format(account, e.ProcessUser, e.SessionUser), true));
        }
    }
}
