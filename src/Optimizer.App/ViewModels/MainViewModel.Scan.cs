using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public sealed partial class MainViewModel
{
    private Task? _scan;
    private bool _rescanRequested;

    /// <summary>The scan that runs now, or a finished task.</summary>
    public Task ScanInProgress => _scan is { IsCompleted: false } scan ? scan : Task.CompletedTask;

    /// <summary>
    /// One scan at a time. A call while a scan runs asks for one more scan after it (the running one may have read the
    /// system before a change finished) and returns the task that ends after both, so two scans never race and an
    /// older result can never overwrite a newer one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    public Task ScanAsync()
    {
        if (_scan is { IsCompleted: false })
        {
            _rescanRequested = true;
            return _scan;
        }
        return _scan = ScanUntilCurrentAsync();
    }

    private async Task ScanUntilCurrentAsync()
    {
        do
        {
            _rescanRequested = false;
            await ScanOnceAsync();
        }
        while (_rescanRequested);
    }

    private async Task ScanOnceAsync()
    {
        Task<IReadOnlyList<Optimizer.Core.Tools.ProcessCpu>>? pendingSample = null;
        IsScanning = true;
        ScanStatus = Loc.Instance["Scan_Running"];
        Headline = Loc.Instance["Headline_Scanning"];
        string? error = null;
        try
        {
            var progress = new Progress<string>(probe => ScanStatus = Loc.Instance.Format("Scan_Probe", probe));
            // After a change only what a change can affect is read again; "Scan again" reads everything.
            var previous = _reuseSlowParts ? Profile : null;
            _reuseSlowParts = false;
            var service = ScanService;
            var lastSample = _lastBackgroundSample;
            var ignored = IgnoredKeys();
            // Scan, findings and every tweak's state in one trip to the background: at the start the UI thread is busy
            // drawing the window, and a return to it in between waited for that (the states were read only after the
            // window was drawn). The page is ready without the 3 second background CPU sample; that one check follows
            // when it is done.
            var (profile, findings, sampleTask, tweaks) = await Task.Run(async () =>
            {
                var (p, f, sample) = await service.RunAsync(progress, previous, lastSample, waitForBackgroundSample: false);
                return (p, f, sample, ReadTweakStates(p, f, ignored));
            });
            pendingSample = sampleTask;
            _findings = findings;
            Profile = profile;
            ShowTweakStates(tweaks);
            _lastScanTime = DateTime.Now;
            var rows = System.Diagnostics.Stopwatch.StartNew();
            Rebuild();
            Network.Rebuild();
            foreach (var page in new PageViewModel[] { Debloat, Apps, Tools, Health }) page.OnProfileChanged();
            Log.Debug("scan", "rows built", new { ms = rows.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            Log.Error("ui", "scan failed", ex);
            error = Loc.Instance["Scan_Failed"];
        }
        finally
        {
            ScanFailed = error is not null;
            IsScanning = false;
            ScanStatus = error ?? Loc.Instance["Scan_Idle"];
            OnPropertyChanged(nameof(LogFile));
        }
        // The pane footer shows the scan status only while a scan runs: an error goes to the result bar, which stays.
        if (error is not null) ShowResult(error, InfoBarSeverity.Error);
        await RefreshChangesStateAsync();
        if (pendingSample is not null && Profile is { } scanned) CompleteBackgroundSampleAsync(scanned, pendingSample).Forget("background CPU sample");
    }

    private IReadOnlyList<Optimizer.Core.Tools.ProcessCpu>? _lastBackgroundSample;

    /// <summary>The scan pipeline (hardware probes, reads ahead for the tweak detection, checks), the same as --report uses.</summary>
    private ScanService ScanService => new(_catalog, _services.Context.Registry, _services.BcdValues, _services.Engine, _services.Catalog.Visible);

    /// <summary>
    /// The 3 second background CPU sample finished after the page was ready: its check (F11) is evaluated now. Skipped
    /// when a newer scan replaced the result meanwhile; that scan's own sample follows.
    /// </summary>
    private async Task CompleteBackgroundSampleAsync(HardwareProfile scanned, Task<IReadOnlyList<Optimizer.Core.Tools.ProcessCpu>> pending)
    {
        IReadOnlyList<Optimizer.Core.Tools.ProcessCpu>? sample;
        try
        {
            sample = await pending;
        }
        catch (Exception ex)
        {
            Log.Warn("scan", $"background CPU sample failed: {ex.Message}");
            return;
        }
        _lastBackgroundSample = sample;
        if (!ReferenceEquals(Profile, scanned) || IsScanning) return;
        var registry = _services.Context.Registry;
        var service = ScanService;
        var ignored = IgnoredKeys();
        var (completed, findings) = await Task.Run(() => service.WithSample(scanned, sample));
        var facts = await Task.Run(() => FactsBuilder.Build(completed, IgnoredResults.Without(findings, ignored), _catalog, registry));
        if (!ReferenceEquals(Profile, scanned) || IsScanning) return;
        _findings = findings;
        Profile = completed;
        // Reading every tweak's state takes most of a second and recreating every row makes each page draw again; the
        // sample changes only its own check, which no tweak reads today. Both happen only when a tweak depends on it.
        if (TweaksDependOn(_facts, facts))
        {
            // A change that runs now rescans when it is done; states read in the middle of it would only be older.
            if (ChangeGate.Instance.Busy) return;
            await RefreshTweaksAsync();
            // A scan or change that started meanwhile has newer states: its rebuild follows.
            if (!ReferenceEquals(Profile, completed) || IsScanning) return;
            Rebuild();
            Network.Rebuild();
            return;
        }
        _facts = facts;
        ApplyProfile();
        RebuildFindings();
    }

    /// <summary>
    /// True when the facts differ in one the tweak rows depend on: any fact that is not a finding's (the engine reads
    /// hardware and system facts directly), or a finding that a tweak fixes or a tweak or profile condition reads.
    /// </summary>
    private bool TweaksDependOn(Facts before, Facts after)
    {
        var changed = before.All.Keys.Union(after.All.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => !Equals(before.Get(k), after.Get(k))).ToList();
        if (changed.Count == 0) return false;
        if (changed.Any(k => !k.StartsWith("finding.", StringComparison.OrdinalIgnoreCase))) return true;
        var tweaks = _tweakStates.Concat(_deviceStates).Select(s => s.Tweak).ToList();
        var findingIds = changed.Select(k => k.Split('.')[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tweaks.Any(t => t.Fixes.Any(findingIds.Contains))) return true;
        var profiles = _catalog.Profiles.Profiles;
        var read = tweaks.SelectMany(t => t.Conditions())
            .Concat(profiles.Select(p => p.SuggestWhen).OfType<Condition>())
            .Concat(profiles.SelectMany(p => p.Tweaks).Select(pt => pt.RecommendWhen).OfType<Condition>())
            .SelectMany(c => c.ReferencedFacts())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return changed.Any(read.Contains);
    }

    /// <summary>Re-reads every tweak's state (after applying/undoing; a scan reads them in its own background trip).</summary>
    private async Task RefreshTweaksAsync()
    {
        if (Profile is null) return;
        var profile = Profile;
        var findings = _findings;
        var ignored = IgnoredKeys();
        // One trip to the background and back (each return to the UI thread waits for what it is drawing).
        ShowTweakStates(await Task.Run(() => ReadTweakStates(profile, findings, ignored)));
    }

    /// <summary>Every tweak's state, the facts it was read with, the changes Windows reset, and the number of backups.</summary>
    private sealed record TweakRead(Facts Facts, IReadOnlyList<TweakStatus> Catalog, IReadOnlyList<TweakStatus> Device,
        IReadOnlyList<DriftItem> Drift, int Backups);

    /// <summary>Reads the system: runs off the UI thread. Ignored results are not in the facts, so nothing is recommended for them.</summary>
    private TweakRead ReadTweakStates(HardwareProfile profile, IReadOnlyList<Finding> findings, IReadOnlySet<string> ignored)
    {
        var engine = _services.Engine;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var facts = FactsBuilder.Build(profile, IgnoredResults.Without(findings, ignored), _catalog, _services.Context.Registry);
        var catalog = engine.DetectAll(_services.Catalog.Visible, facts);
        var device = engine.DetectAll(DeviceTweaks.Build(profile), facts);
        Log.Info("scan", "tweak states read", new { ms = watch.ElapsedMilliseconds });
        // The states just read are not read again (runtime tweaks of other pages are).
        var known = catalog.Concat(device).GroupBy(s => s.Tweak.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().State, StringComparer.Ordinal);
        var drift = engine.CheckDrift(facts, known);
        return new TweakRead(facts, catalog, device, drift, _services.Store.All().Count);
    }

    private void ShowTweakStates(TweakRead read)
    {
        InspectorItem.ClearMarkdownCache();
        (_facts, _tweakStates, _deviceStates, _drift) = (read.Facts, read.Catalog, read.Device, read.Drift);
        var count = read.Backups;
        EnsureProfile();
        ApplyProfile();
        foreach (var d in _drift)
            Log.Warn("drift", $"{d.Tweak.Id} is no longer in place", new { d.AppliedOn, d.Current, d.WindowsUpdatedSince });

        // Once per Windows update: say so when all changes survived it (a reset shows in the drift banner instead).
        var current = _services.Os.BuildString;
        if (_settings.LastSeenWindowsVersion is { } last && last != current && count > 0 && _drift.Count == 0)
            _updateNotice = Loc.Instance.Format("WinUpdated_AllGood", last, current, count);
        if (_settings.LastSeenWindowsVersion != current)
        {
            _settings.LastSeenWindowsVersion = current;
            _settings.Save();
        }
    }

    /// <summary>
    /// Picks the active profile after a scan: the saved one if this PC offers it, else (first start) the one that fits
    /// this PC best, which a banner then mentions once.
    /// </summary>
    private void EnsureProfile()
    {
        var profiles = _catalog.Profiles;
        _suggested = profiles.Suggest(_facts);
        if (ProfileOverride is { } forced && profiles.Profiles.FirstOrDefault(p => p.Id == forced && p.IsAvailable(_facts)) is { } chosen)
        {
            _usage = chosen;
            return;
        }
        if (_settings.Profile is null)
        {
            _usage = _suggested;
            _profileAutoSet = true;
            _settings.Profile = _usage.Id;
            _settings.Save();
            Log.Info("profile", "profile suggested", new { profile = _usage.Id });
        }
        else
        {
            // A laptop profile copied to a desktop (or no longer offered) falls back to Gaming for this session only.
            _usage = profiles.Profiles.FirstOrDefault(p => p.Id == _settings.Profile && p.IsAvailable(_facts)) ?? profiles.Default;
        }
    }

    /// <summary>Rates tweaks and findings through the active profile and rebuilds the recommendation plan.</summary>
    private void ApplyProfile()
    {
        _profiled = ProfileView.For(_usage, _tweakStates, _facts, IgnoredTweakIds());
        _profiledFindings = WithoutIgnored(ProfileView.Findings(_usage, _findings));
        _plan = Optimizer.Core.Tweaks.Recommendations.Build(_profiled, _profiledFindings);
    }

    /// <summary>Picker entries in the current language: the profiles this PC offers (laptop profiles only on laptops).</summary>
    private void BuildProfileOptions()
    {
        var options = _catalog.Profiles.Profiles.Where(p => p.IsAvailable(_facts))
            .Select(p => new ProfileOption(p.Id, ProfileName(p), Loc.Instance[$"ProfileDesc_{p.Id}"],
                Enum.TryParse<SymbolRegular>(p.Icon, out var icon) ? icon : SymbolRegular.Games24))
            .ToList();
        // Replacing the list while a combo box is changing its selection would blank it: only when the entries differ.
        if (!options.SequenceEqual(ProfileOptions))
        {
            ProfileOptions.Clear();
            foreach (var o in options) ProfileOptions.Add(o);
        }
        // Set the field directly: the change handler is for user choices.
#pragma warning disable MVVMTK0034
        _selectedProfile = ProfileOptions.FirstOrDefault(o => o.Id == _usage.Id);
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedProfile));
        ProfileDescription = Loc.Instance[$"ProfileDesc_{_usage.Id}"];
        SuggestedProfileText = _suggested is { } s && s.Id != _usage.Id && Profile is not null ? Loc.Instance.Format("Profile_Suggested", ProfileName(s)) : null;
    }

    /// <summary>Applies every reset change again (one confirmation). Expert changes are left for the Changes page.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ReapplyDriftAsync()
    {
        var safe = _drift.Where(d => d.Tweak.IsBatchSafe).Select(d => d.Tweak).ToList();
        var expert = _drift.Count - safe.Count;
        if (safe.Count > 0) await Runner.ApplyBatchAsync(safe, Loc.Instance["Drift_ApplyAgain"], Loc.Instance["Drift_ConfirmIntro"]);
        if (expert > 0) ShowResult(Loc.Instance.Format("Drift_ExpertIndividually", expert));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ReapplyRecordAsync(string tweakId)
    {
        if (ResolveTweak(tweakId) is { } t) await Runner.ApplyAsync(t);
    }

    private string DriftReason(DriftItem d) =>
        d.WindowsUpdatedSince ? Loc.Instance.Format("Drift_ResetUpdate", d.AppliedOn, d.Current) : Loc.Instance["Drift_ResetOther"];
}
