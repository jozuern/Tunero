using Optimizer.App.Services;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Profiles;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

/// <summary>What <see cref="ScanState.Updated"/> says changed; the window builds the matching rows again.</summary>
internal enum ScanUpdate
{
    /// <summary>A scan started (<see cref="ScanState.IsScanning"/>).</summary>
    Started,

    /// <summary>Every tweak's state was read again: explanations built from the old states are out of date.</summary>
    StatesRead,

    /// <summary>The scan's result is in (profile, findings, tweak states, plan): every list is built again.</summary>
    Scanned,

    /// <summary>The scan is over, after its result or with an error (<see cref="ScanState.ScanFailed"/>).</summary>
    Ended,

    /// <summary>The background CPU sample completed the hardware profile and the findings; lists follow with one of the next two.</summary>
    SampleAdded,

    /// <summary>The tweak states were read again after the sample: every list is built again.</summary>
    TweaksRead,

    /// <summary>Only the findings and the plan changed (the sample): the finding lists and the plan are built again.</summary>
    FindingsChanged,
}

/// <summary>The data of the "all changes survived the Windows update" notice.</summary>
internal sealed record WindowsUpdateNotice(string Last, string Current, int Backups);

/// <summary>
/// The window's scan and plan state without WPF: the last scan's hardware profile, findings and tweak states, the
/// profile in use and the plan "Apply recommended" applies, the changes Windows reset, and the logic around them (one
/// scan at a time, the background CPU sample merged after the page is ready, the profile choice after a scan, ignored
/// results and recommendations). <see cref="MainViewModel"/> keeps the text, the observable properties and the rows and
/// builds them again on <see cref="Updated"/>.
/// <para>
/// Single-threaded: every call, and every continuation after an await, runs on the caller's thread (the window's UI
/// thread). The system is read off that thread through <see cref="IScanSource"/> in one trip per step, and every await
/// here comes back to the caller's context (none is configured to skip it), so a result always reaches the thread that
/// owns this state. ArchitectureTests holds that.
/// </para>
/// </summary>
/// <param name="saveSettings">Saves <paramref name="settings"/> (the profile the first scan picks, the ignore lists, the Windows version seen).</param>
/// <param name="changeBusy">A change runs now: it rescans when it is done, so the background sample does not read the states in the middle of it.</param>
/// <param name="now">The clock for <see cref="LastScanTime"/>; null: the PC's.</param>
internal sealed class ScanState(CatalogData catalog, AppSettings settings, Action saveSettings, IScanSource source, Func<bool> changeBusy,
    Func<DateTime>? now = null)
{
    private Task? _scan;
    private bool _rescanRequested;
    private bool _reuseSlowParts;

    /// <summary>What changed; raised on the thread that owns this state, before the call or scan that changed it goes on.</summary>
    public event Action<ScanUpdate>? Updated;

    /// <summary>A probe of the running scan started (its name, for the status line).</summary>
    public event Action<string>? Probed;

    /// <summary>
    /// What the window refreshes after every scan, good or failed, before the next one starts or the background sample is
    /// merged (the Changes page's backups); the scan task includes it.
    /// </summary>
    public Func<Task>? AfterScan { get; set; }

    /// <summary>Developer switch --profile: use this profile for the session without saving it.</summary>
    public string? ProfileOverride { get; set; }

    /// <summary>The hardware the last scan read; null before the first scan has finished.</summary>
    public HardwareProfile? Profile { get; private set; }
    public IReadOnlyList<Finding> Findings { get; private set; } = [];
    public IReadOnlyList<TweakStatus> TweakStates { get; private set; } = [];
    public IReadOnlyList<TweakStatus> DeviceStates { get; private set; } = [];
    public IReadOnlyList<DriftItem> Drift { get; private set; } = [];
    public Facts Facts { get; private set; } = new();
    public DateTime? LastScanTime { get; private set; }

    /// <summary>Once per Windows update, when every change survived it (a reset shows in the drift banner instead).</summary>
    public WindowsUpdateNotice? UpdateNotice { get; private set; }

    /// <summary>Usage profile: decides recommendations, impact, "works against" and which findings count (no setting by itself).</summary>
    public UsageProfile Usage { get; private set; } = catalog.Profiles.Default;

    /// <summary>The profile that fits this PC best (the picker offers it when another one is in use).</summary>
    public UsageProfile? Suggested { get; private set; }

    /// <summary>The first scan picked and saved the profile (a banner mentions it once).</summary>
    public bool ProfileAutoSet { get; private set; }

    public IReadOnlyList<ProfiledTweak> Profiled { get; private set; } = [];
    public IReadOnlyList<Finding> ProfiledFindings { get; private set; } = [];

    /// <summary>The ignored results the active profile would show, with its impact (set with the plan, <see cref="ApplyProfile"/>).</summary>
    public IReadOnlyList<Finding> IgnoredFindings { get; private set; } = [];
    public RecommendationPlan Plan { get; private set; } = new([], []);

    public bool IsScanning { get; private set; }

    /// <summary>The last scan stopped with an error.</summary>
    public bool ScanFailed { get; private set; }

    /// <summary>The last finished background CPU sample, reused by a quick rescan after a change.</summary>
    public IReadOnlyList<ProcessCpu>? LastBackgroundSample { get; private set; }

    /// <summary>The scan that runs now, or a finished task.</summary>
    public Task ScanInProgress => _scan is { IsCompleted: false } scan ? scan : Task.CompletedTask;

    private void Raise(ScanUpdate update) => Updated?.Invoke(update);

    /// <summary>The ignored results' keys, as a copy a background read can use while the list changes.</summary>
    public IReadOnlySet<string> IgnoredKeys() => settings.IgnoredFindings.ToHashSet(StringComparer.Ordinal);

    /// <summary>The ids of the tweaks whose recommendation the user ignores.</summary>
    public IReadOnlySet<string> IgnoredTweakIds() => settings.IgnoredTweaks.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// One scan at a time. A call while a scan runs asks for one more scan after it (the running one may have read the
    /// system before a change finished) and returns the task that ends after both, so two scans never race and an
    /// older result can never overwrite a newer one.
    /// </summary>
    /// <param name="reuseSlowParts">After a change: only what a change can affect is read again (the next scan that starts uses it).</param>
    public Task ScanAsync(bool reuseSlowParts = false)
    {
        if (reuseSlowParts) _reuseSlowParts = true;
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
        Task<IReadOnlyList<ProcessCpu>>? pendingSample = null;
        IsScanning = true;
        Raise(ScanUpdate.Started);
        var failed = false;
        try
        {
            var progress = new Progress<string>(probe => Probed?.Invoke(probe));
            // After a change only what a change can affect is read again; "Scan again" reads everything.
            var previous = _reuseSlowParts ? Profile : null;
            _reuseSlowParts = false;
            var lastSample = LastBackgroundSample;
            var ignored = IgnoredKeys();
            // Scan, findings and every tweak's state in one trip to the background: at the start the UI thread is busy
            // drawing the window, and a return to it in between waited for that (the states were read only after the
            // window was drawn). The page is ready without the 3 second background CPU sample; that one check follows
            // when it is done.
            var (scanned, tweaks) = await Task.Run(async () =>
            {
                var result = await source.RunAsync(progress, previous, lastSample);
                return (result, source.ReadStates(result.Profile, result.Findings, ignored));
            });
            // A result ignored while the scan ran is not in the facts it read: read the states again with it, or a
            // tweak recommended for that result would stay recommended until the next scan.
            for (var current = IgnoredKeys(); !current.SetEquals(ignored); current = IgnoredKeys())
            {
                var keys = ignored = current;
                tweaks = await Task.Run(() => source.ReadStates(scanned.Profile, scanned.Findings, keys));
            }
            pendingSample = scanned.PendingSample;
            Findings = scanned.Findings;
            Profile = scanned.Profile;
            ShowTweakStates(tweaks);
            LastScanTime = now?.Invoke() ?? DateTime.Now;
            Raise(ScanUpdate.Scanned);
        }
        catch (Exception ex)
        {
            Log.Error("ui", "scan failed", ex);
            failed = true;
        }
        finally
        {
            ScanFailed = failed;
            IsScanning = false;
            Raise(ScanUpdate.Ended);
        }
        if (AfterScan is { } after) await after();
        if (pendingSample is not null && Profile is { } profile) CompleteBackgroundSampleAsync(profile, pendingSample).Forget("background CPU sample");
    }

    /// <summary>
    /// The 3 second background CPU sample finished after the page was ready: its check (F11) is evaluated now. Skipped
    /// when a newer scan replaced the result meanwhile; that scan's own sample follows.
    /// </summary>
    private async Task CompleteBackgroundSampleAsync(HardwareProfile scanned, Task<IReadOnlyList<ProcessCpu>> pending)
    {
        IReadOnlyList<ProcessCpu>? sample;
        try
        {
            sample = await pending;
        }
        catch (Exception ex)
        {
            Log.Warn("scan", $"background CPU sample failed: {ex.Message}");
            return;
        }
        LastBackgroundSample = sample;
        if (!ReferenceEquals(Profile, scanned) || IsScanning) return;
        var ignored = IgnoredKeys();
        var (completed, findings) = await Task.Run(() => source.WithSample(scanned, sample));
        var facts = await Task.Run(() => source.BuildFacts(completed, findings, ignored));
        if (!ReferenceEquals(Profile, scanned) || IsScanning) return;
        Findings = findings;
        Profile = completed;
        Raise(ScanUpdate.SampleAdded);
        // Reading every tweak's state takes most of a second and recreating every row makes each page draw again; the
        // sample changes only its own check, which no tweak reads today. Both happen only when a tweak depends on it, or
        // when a result was ignored meanwhile (the facts just built still hold it).
        if (TweaksDependOn(Facts, facts) || !ignored.SetEquals(IgnoredKeys()))
        {
            // A change that runs now rescans when it is done; states read in the middle of it would only be older.
            if (changeBusy()) return;
            await RefreshTweaksAsync();
            // A scan or change that started meanwhile has newer states: its rebuild follows.
            if (!ReferenceEquals(Profile, completed) || IsScanning) return;
            Raise(ScanUpdate.TweaksRead);
            return;
        }
        Facts = facts;
        ApplyProfile();
        Raise(ScanUpdate.FindingsChanged);
    }

    /// <summary>
    /// True when the facts differ in one the tweak rows depend on: any fact that is not a finding's (the engine reads
    /// hardware and system facts directly), or a finding that a tweak fixes or a tweak or profile condition reads.
    /// </summary>
    private bool TweaksDependOn(Facts before, Facts after) =>
        TweaksDependOn(before, after, TweakStates.Concat(DeviceStates).Select(s => s.Tweak), catalog.Profiles);

    /// <inheritdoc cref="TweaksDependOn(Facts, Facts)"/>
    internal static bool TweaksDependOn(Facts before, Facts after, IEnumerable<TweakDefinition> tweakDefinitions, ProfileCatalog profileCatalog)
    {
        var changed = before.All.Keys.Union(after.All.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => !Equals(before.Get(k), after.Get(k))).ToList();
        if (changed.Count == 0) return false;
        if (changed.Any(k => !k.StartsWith("finding.", StringComparison.OrdinalIgnoreCase))) return true;
        var tweaks = tweakDefinitions.ToList();
        // "finding.F31.memoryIntegrity.problem": the result's id has a dot of its own (FactsBuilder adds .problem and .status).
        var findingIds = changed.Select(FindingId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tweaks.Any(t => t.Fixes.Any(findingIds.Contains))) return true;
        var profiles = profileCatalog.Profiles;
        var read = tweaks.SelectMany(t => t.Conditions())
            .Concat(profiles.Select(p => p.SuggestWhen).OfType<Condition>())
            .Concat(profiles.SelectMany(p => p.Tweaks).Select(pt => pt.RecommendWhen).OfType<Condition>())
            .SelectMany(c => c.ReferencedFacts())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return changed.Any(read.Contains);

        static string FindingId(string fact)
        {
            const string prefix = "finding.";
            var dot = fact.LastIndexOf('.');
            return dot > prefix.Length ? fact[prefix.Length..dot] : fact[prefix.Length..];
        }
    }

    /// <summary>
    /// Re-reads every tweak's state (after the ignore lists or the background sample changed; a scan reads them in its
    /// own background trip, and after a change the window rescans). While a scan runs nothing is read: the scan reads them
    /// when it ends, with the ignore lists as they are then, and the plan is only rated again now.
    /// </summary>
    public async Task RefreshTweaksAsync()
    {
        if (Profile is null) return;
        if (IsScanning)
        {
            ApplyProfile();
            return;
        }
        var profile = Profile;
        var findings = Findings;
        var ignored = IgnoredKeys();
        // One trip to the background and back (each return to the UI thread waits for what it is drawing).
        var read = await Task.Run(() => source.ReadStates(profile, findings, ignored));
        // A scan that started or ended meanwhile reads (or read) newer states.
        if (!ReferenceEquals(Profile, profile) || IsScanning) return;
        ShowTweakStates(read);
    }

    private void ShowTweakStates(TweakRead read)
    {
        (Facts, TweakStates, DeviceStates, Drift) = (read.Facts, read.Catalog, read.Device, read.Drift);
        Raise(ScanUpdate.StatesRead);
        EnsureProfile();
        ApplyProfile();
        foreach (var d in Drift)
            Log.Warn("drift", $"{d.Tweak.Id} is no longer in place", new { d.AppliedOn, d.Current, d.WindowsUpdatedSince });

        // Once per Windows update: say so when all changes survived it (a reset shows in the drift banner instead).
        var current = source.WindowsBuild;
        if (settings.LastSeenWindowsVersion is { } last && last != current && read.Backups > 0 && Drift.Count == 0)
            UpdateNotice = new WindowsUpdateNotice(last, current, read.Backups);
        if (settings.LastSeenWindowsVersion != current)
        {
            settings.LastSeenWindowsVersion = current;
            saveSettings();
        }
    }

    /// <summary>
    /// Picks the active profile after a scan (<see cref="ChooseProfile"/>); on the first start the one that fits this PC
    /// best is saved, and a banner then mentions it once.
    /// </summary>
    private void EnsureProfile()
    {
        Suggested = catalog.Profiles.Suggest(Facts);
        var choice = ChooseProfile(catalog.Profiles, ProfileOverride, settings.Profile, Facts);
        Usage = choice.Profile;
        if (choice.Source != ProfileSource.Suggested) return;
        ProfileAutoSet = true;
        settings.Profile = Usage.Id;
        saveSettings();
        Log.Info("profile", "profile suggested", new { profile = Usage.Id });
    }

    /// <summary>
    /// The window's profile: --profile when this PC offers it (one it does not offer is passed over here, where the
    /// command line stops), else the command line's rule without it (<see cref="ProfileCatalog.Choose(string?, Facts)"/>):
    /// a laptop profile copied to a desktop, or one no longer offered, is Gaming for this session only.
    /// </summary>
    private static ProfileChoice ChooseProfile(ProfileCatalog profiles, string? forced, string? saved, Facts facts) =>
        profiles.Choose(forced, saved, facts) ?? profiles.Choose(saved, facts);

    /// <summary>
    /// Rates tweaks and findings through the active profile and rebuilds the recommendation plan: the scan's catalog tweak
    /// states and every result, with the results and recommendations the user ignores, through the function
    /// --apply-recommended uses too.
    /// </summary>
    private void ApplyProfile() =>
        (Profiled, ProfiledFindings, IgnoredFindings, Plan) = Recommendations.ForProfile(Usage, TweakStates, Findings, Facts,
            settings.IgnoredFindings.ToHashSet(StringComparer.Ordinal), IgnoredTweakIds());

    /// <summary>Another profile, from the picker (<paramref name="save"/>) or for this session only: everything is rated again, nothing is read.</summary>
    public void SetProfile(string id, bool save)
    {
        Usage = catalog.Profiles.Get(id);
        if (save)
        {
            ProfileAutoSet = false;
            settings.Profile = Usage.Id;
            saveSettings();
            Log.Info("profile", "profile changed", new { profile = Usage.Id });
        }
        ApplyProfile();
    }

    /// <summary>
    /// Ignores a result (a passed check cannot be ignored) or stops ignoring it. The facts change with the ignored results
    /// (what is recommended for them): the states are read again.
    /// </summary>
    public async Task SetResultIgnoredAsync(Finding finding, bool ignored)
    {
        var key = IgnoredResults.Key(finding);
        if (ignored)
        {
            if (!IgnoredResults.CanIgnore(finding)) return;
            if (!settings.IgnoredFindings.Contains(key, StringComparer.Ordinal)) settings.IgnoredFindings.Add(key);
        }
        else
        {
            settings.IgnoredFindings.RemoveAll(k => k == key);
        }
        saveSettings();
        await RefreshTweaksAsync();
    }

    /// <summary>The facts do not depend on ignored tweaks: the plan is rated again, nothing is read.</summary>
    public void SetTweakIgnored(string tweakId, bool ignored)
    {
        settings.IgnoredTweaks.RemoveAll(id => id == tweakId);
        if (ignored) settings.IgnoredTweaks.Add(tweakId);
        saveSettings();
        ApplyProfile();
    }

    /// <summary>
    /// The changes Windows reset that "Apply again" applies together (batch-safe ones), and how many are left for the
    /// Changes page, where each is applied with its own confirmation.
    /// </summary>
    public (IReadOnlyList<TweakDefinition> Safe, int Individually) DriftToReapply()
    {
        var safe = Drift.Where(d => d.Tweak.IsBatchSafe).Select(d => d.Tweak).ToList();
        return (safe, Drift.Count - safe.Count);
    }

    /// <summary>Developer aid for screenshots: shows these changes as reset by Windows, without touching anything.</summary>
    public void PreviewDrift(IReadOnlyList<DriftItem> items) => Drift = items;
}
