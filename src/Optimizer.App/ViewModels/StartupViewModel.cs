using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

public sealed partial class StartupRow : SwitchRow
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;

    private readonly AppServices _services;
    private readonly ChangeRunner _runner;

    public StartupRow(StartupEntry entry, string lang, AppServices services, ChangeRunner runner) : base(entry.Enabled ?? true)
    {
        _services = services;
        _runner = runner;
        Entry = entry;
        Tab = StartupViewModel.TabOf(entry);
        KindText = Labels.Current.Get(lang, $"startupKind.{entry.Kind}");
        SimpleKindText = SimpleKind();
        _switchable = entry.Enabled is not null && StartupTweaks.Set(entry, !(entry.Enabled ?? true)) is not null;
        // Until the signature is read, a service counts as Windows' own.
        UpdateCanToggle(microsoftOrUnknown: true);
    }

    private readonly bool _switchable;

    /// <summary>
    /// A service follows the Services page's rule (<see cref="ServiceRow.EditFor"/>): Windows' own services only when the
    /// service catalog explains that Manual is fine, so Dhcp or the firewall cannot be switched off here. A service this
    /// page set to Manual can always be switched back.
    /// </summary>
    private void UpdateCanToggle(bool microsoftOrUnknown)
    {
        CanToggle = _switchable && (Entry.Kind != StartupKind.Service || Entry.Enabled == false
            || ServiceRow.EditFor(CatalogData.Current.Services.Find(Entry.Target ?? ""), microsoftOrUnknown) != ServiceEdit.ReadOnly);
        ReadOnlyNote = CanToggle ? null : Loc.Instance["Startup_ReadOnly"];
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.Name;

    /// <summary>A registry value Autoruns-style Delete can remove, with a backup (<see cref="StartupTweaks.Delete"/>).</summary>
    public bool CanDelete => StartupTweaks.Delete(Entry) is not null;

    public string DeleteLabel => Loc.Instance.Format("Startup_DeleteName", Name);
    public string MoreOptionsLabel => Loc.Instance.Format("Startup_MoreOptionsName", Name);

    /// <summary>The tab the entry is listed on; fixed by its kind (and for a task, its trigger).</summary>
    public StartupTab Tab { get; }

    public string KindText { get; }

    /// <summary>
    /// The kind in plain words for the simple view: where an app at sign-in is registered, the location's name on the
    /// Advanced tab, and nothing on the tabs that hold one kind only (services, drivers, other tasks).
    /// </summary>
    public string SimpleKindText { get; }

    private string SimpleKind() => Tab switch
    {
        StartupTab.SignIn => Entry.Kind switch
        {
            StartupKind.RunKey => Loc.Instance["Startup_PlainRegistry"],
            StartupKind.StartupFolder => Loc.Instance["Startup_PlainFolder"],
            StartupKind.LogonTask => Loc.Instance["Startup_PlainTask"],
            StartupKind.PolicyRun => Loc.Instance["Startup_PlainPolicy"],
            StartupKind.RunOnce => Loc.Instance["Startup_PlainRunOnce"],
            _ => "",
        },
        StartupTab.Advanced => KindText,
        _ => "",
    };
    public string Location => Entry.Location;
    public string? Command => Entry.Command;
    [ObservableProperty] private string? _readOnlyNote;
    public bool Suspicious => Entry.Suspicious;

    /// <summary>
    /// <see cref="StartupEntry.RunsScriptHost"/> parses the command line and checks its paths: read once per row, in the
    /// background with the signature (<see cref="SetSignature"/>), and only here if something asks before that.
    /// </summary>
    private bool? _runsScriptHost;

    private bool RunsScriptHost => _runsScriptHost ??= Entry.RunsScriptHost;

    /// <summary>Signed by Microsoft does not mean harmless here: the command line decides what the script host runs.</summary>
    public string? ScriptHostNote => RunsScriptHost ? Loc.Instance["Startup_ScriptHost"] : null;

    /// <summary>Hidden by "Hide Microsoft entries" only when signed by Microsoft and nothing else needs a look.</summary>
    public bool IsPlainMicrosoft => IsMicrosoft && !Entry.Suspicious && !RunsScriptHost;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MetaText), nameof(SimpleMetaText))] private string _publisherText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MetaText), nameof(SimpleMetaText))] private string _signatureText = "";
    [ObservableProperty] private bool _isMicrosoft;

    /// <summary>Kind, publisher and signature (technical view); parts not known yet (signatures load after the list) are left out.</summary>
    public string MetaText => string.Join(", ", new[] { KindText, PublisherText, SignatureText }.Where(x => x.Length > 0));

    /// <summary>The same line in the simple view, with the kind in plain words or left out.</summary>
    public string SimpleMetaText => string.Join(", ", new[] { SimpleKindText, PublisherText, SignatureText }.Where(x => x.Length > 0));
    [ObservableProperty] private bool _needsAttention;

    /// <summary>Why the row needs attention, as short tags ("Not signed", "Runs a script"): the warning icon alone says only that.</summary>
    [ObservableProperty] private IReadOnlyList<string> _attentionTags = [];
    [ObservableProperty] private string? _virusTotalText;
    [ObservableProperty] private string? _virusTotalUrl;

    /// <summary>Added or changed since the saved snapshot (set after a comparison).</summary>
    [ObservableProperty] private bool _isNew;

    public void SetSignature(SignatureInfo s, bool runsScriptHost, string lang)
    {
        _runsScriptHost = runsScriptHost;
        IsMicrosoft = s.IsMicrosoft;
        // The Services page's rule: without a valid signature (CryptSvc off, a failed check) a file in the Windows folder
        // still counts as Windows' own, so Dhcp or the firewall never become switchable here.
        UpdateCanToggle(ServiceRow.IsWindowsService(s, Entry.ImagePath) || s.Status == SignatureStatus.Error);
        PublisherText = s.Publisher ?? Loc.Instance["Startup_NoPublisher"];
        // Not signed and a bad signature are tags under the name: the line does not say it a second time.
        SignatureText = s.Status is SignatureStatus.Unsigned or SignatureStatus.Invalid ? "" : Labels.Current.Get(lang, $"signature.{s.Status}");
        NeedsAttention = Entry.Suspicious || runsScriptHost || s.Status is SignatureStatus.Unsigned or SignatureStatus.Invalid;
        var tags = new List<string>();
        if (s.Status == SignatureStatus.Invalid) tags.Add(Loc.Instance["Startup_TagBadSignature"]);
        else if (s.Status == SignatureStatus.Unsigned) tags.Add(Loc.Instance["Startup_TagUnsigned"]);
        if (runsScriptHost) tags.Add(Loc.Instance["Startup_TagScript"]);
        if (Entry.Suspicious) tags.Add(Loc.Instance["Startup_TagUnusual"]);
        AttentionTags = tags;
    }

    protected override Task<bool> ToggleAsync(bool on) => Switching.SetAsync(_services, _runner, enabled => StartupTweaks.Set(Entry, enabled), on);
}

public sealed record FilterOption(string Key, string Text)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Text;
}

/// <summary>The Startup page's tabs, in their order on the page.</summary>
public enum StartupTab
{
    SignIn, Tasks, Services, Drivers, Advanced,
}

/// <summary>A tab header: its name and, once the signatures are checked, how many entries it shows under the filters.</summary>
public sealed partial class StartupTabItem(StartupTab tab) : ObservableObject
{
    public StartupTab Tab { get; } = tab;
    [ObservableProperty] private string _header = StartupViewModel.TabName(tab);

    // What screen readers announce for the tab.
    public override string ToString() => Header;
}

public sealed partial class StartupViewModel(IPageHost owner, AppServices services, ChangeRunner runner) : PageViewModel(owner)
{
    private List<StartupRow> _all = [];
    private CancellationTokenSource? _vtCancel;

    public ObservableCollection<StartupRow> Rows { get; } = [];

    public IReadOnlyList<StartupTabItem> Tabs { get; } = [.. Enum.GetValues<StartupTab>().Select(t => new StartupTabItem(t))];

    /// <summary>The open tab, kept for the session: first the apps at sign-in, as Task Manager lists them.</summary>
    [ObservableProperty] private int _selectedTab;

    /// <summary>The Advanced tab's kind filter: All, then the kinds found on this PC.</summary>
    public ObservableCollection<FilterOption> AdvancedKinds { get; } = [];

    [ObservableProperty] private FilterOption? _selectedAdvancedKind;
    private bool _updatingKinds;

    [ObservableProperty] private bool _hideMicrosoft = true;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _virusTotalStatus = "";
    [ObservableProperty] private bool _isCheckingVirusTotal;

    /// <summary>The running check has enough files that the free key's pace (4 lookups a minute) makes it take minutes.</summary>
    [ObservableProperty] private bool _virusTotalTakesMinutes;

    /// <summary>
    /// The lookups are spaced by <paramref name="spacing"/>, so the check of <paramref name="files"/> files takes at least
    /// (files - 1) times that: from two minutes on, the status line says it can take several minutes.
    /// </summary>
    public static bool VirusTotalTakesMinutesFor(int files, TimeSpan spacing) => files > 1 && (files - 1) * spacing >= TimeSpan.FromMinutes(2);
    [ObservableProperty] private string _snapshotText = "";
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private bool _onlyNew;
    [ObservableProperty] private string _search = "";
    private bool _compareAfterLoad;

    partial void OnSearchChanged(string value) => Filter();
    partial void OnOnlyNewChanged(bool value) => Filter();

    /// <summary>Only entries that need attention (not signed, unusual place, a script host).</summary>
    [ObservableProperty] private bool _onlyAttention;

    partial void OnOnlyAttentionChanged(bool value) => Filter();
    partial void OnHideMicrosoftChanged(bool value) => Filter();
    partial void OnSelectedTabChanged(int value) => Filter();

    partial void OnSelectedAdvancedKindChanged(FilterOption? value)
    {
        if (!_updatingKinds) Filter();
    }

    private IReadOnlyList<StartupEntry>? _entries;

    protected override async Task LoadAsync()
    {
        var scanner = new StartupScanner(services.Context.Registry, services.Context.Tasks, services.ProfilePath);
        _entries = await Task.Run(() => scanner.ScanAll().Concat(SetToManual(scanner)).ToList());
        await ShowAsync(_entries);
    }

    /// <summary>
    /// Services this page set to Manual: they no longer start automatically, so the scan does not list them, and without
    /// them here there would be no switch to turn them back on (the backups of their changes name them).
    /// </summary>
    private IEnumerable<StartupEntry> SetToManual(StartupScanner scanner)
    {
        var prefix = $"startup.off.{StartupKind.Service.ToString().ToLowerInvariant()}.";
        foreach (var backup in services.Store.All().Where(b => b.TweakId.StartsWith(prefix, StringComparison.Ordinal)))
            if (services.Engine.Resolve(backup.TweakId)?.Actions.OfType<ServiceAction>().FirstOrDefault() is { } action
                && scanner.ManualService(action.Name) is { Enabled: false } entry)
                yield return entry;
    }

    // A language switch builds the rows from the entries already read; the signatures come from the verifier's cache.
    protected override Task RelabelAsync() => _entries is { } entries ? ShowAsync(entries) : LoadAsync();

    private async Task ShowAsync(IReadOnlyList<StartupEntry> entries)
    {
        var lang = Lang;
        _all = entries.Select(e => new StartupRow(e, lang, services, runner)).ToList();
        // Services and drivers are found on Services & tasks; here only what the Startup page is for.
        services.Search.UpdateRuntime(Optimizer.Core.Search.SearchKind.StartupEntry,
            _all.Where(r => r.Entry.Kind is not (StartupKind.Service or StartupKind.Driver)).Select(r => ("Startup", r.Name, r.Name, r.MetaText)));
        if (_compareAfterLoad && await Task.Run(() => StartupSnapshot.Load(StartupSnapshot.DefaultFile)) is { } snapshot)
            ShowComparison(StartupSnapshot.Compare(snapshot, entries));
        UpdateAdvancedKinds();
        // Signatures in the background, shown together: Microsoft entries are hidden once verified. Until then the counts
        // in the tabs and of what needs attention would be wrong (every entry still unverified): they wait.
        _checkingSignatures = true;
        Filter();
        var rows = _all.ToList();
        try
        {
            var signatures = await Task.Run(() => rows.AsParallel().AsOrdered().WithDegreeOfParallelism(4)
                .Select(row => (Signature: SignatureVerifier.Verify(row.Entry.ImagePath), ScriptHost: row.Entry.RunsScriptHost)).ToList());
            for (var i = 0; i < rows.Count; i++) rows[i].SetSignature(signatures[i].Signature, signatures[i].ScriptHost, lang);
        }
        finally
        {
            _checkingSignatures = false;
        }
        Filter();
    }

    /// <summary>Shows these rows as a finished load does, with their signatures already set (tests).</summary>
    internal void ShowRows(IEnumerable<StartupRow> rows)
    {
        _all = [.. rows];
        UpdateAdvancedKinds();
        Filter();
    }

    /// <summary>The Advanced tab's kinds, built again after a load or a language switch; the chosen kind stays selected.</summary>
    private void UpdateAdvancedKinds()
    {
        var selectedKey = SelectedAdvancedKind?.Key ?? "";
        _updatingKinds = true;
        try
        {
            AdvancedKinds.Clear();
            AdvancedKinds.Add(new FilterOption("", Loc.Instance["Startup_AllKinds"]));
            foreach (var kind in _all.Where(r => r.Tab == StartupTab.Advanced).Select(r => (Key: r.Entry.Kind.ToString(), Text: r.KindText)).Distinct()
                         .OrderBy(k => k.Text, StringComparer.CurrentCultureIgnoreCase))
                AdvancedKinds.Add(new FilterOption(kind.Key, kind.Text));
            SelectedAdvancedKind = AdvancedKinds.FirstOrDefault(k => k.Key == selectedKey) ?? AdvancedKinds[0];
        }
        finally
        {
            _updatingKinds = false;
        }
    }

    private bool _checkingSignatures;

    /// <summary>
    /// The tab an entry is listed on: what starts at sign-in (Run keys, the Startup folder, tasks at sign-in or boot,
    /// policy and RunOnce entries), the other tasks, services, drivers, and every other location under Advanced.
    /// </summary>
    public static StartupTab TabOf(StartupEntry e) => e.Kind switch
    {
        StartupKind.RunKey or StartupKind.StartupFolder or StartupKind.PolicyRun or StartupKind.RunOnce => StartupTab.SignIn,
        StartupKind.LogonTask => e.StartsWithWindows ? StartupTab.SignIn : StartupTab.Tasks,
        StartupKind.Service => StartupTab.Services,
        StartupKind.Driver => StartupTab.Drivers,
        _ => StartupTab.Advanced,
    };

    public static string TabName(StartupTab tab) => tab switch
    {
        StartupTab.SignIn => Loc.Instance["Startup_SignInKinds"],
        StartupTab.Tasks => Loc.Instance["Startup_TabTasks"],
        StartupTab.Services => Loc.Instance["Startup_TabServices"],
        StartupTab.Drivers => Loc.Instance["Startup_TabDrivers"],
        _ => Loc.Instance["Startup_TabAdvanced"],
    };

    private StartupTab CurrentTab => (StartupTab)Math.Clamp(SelectedTab, 0, Tabs.Count - 1);

    /// <summary>Entries of the open tab (and on Advanced, of the chosen kind), shown or not: what the summary counts.</summary>
    private int _tabTotal;

    /// <summary>The search, Hide Windows entries, Only new and Only attention; the tab and the Advanced kind are not part of it.</summary>
    private bool Passes(StartupRow r, string q) =>
        !(HideMicrosoft && r.IsPlainMicrosoft) && (!OnlyNew || r.IsNew) && (!OnlyAttention || r.NeedsAttention)
        && (q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.Command?.Contains(q, StringComparison.OrdinalIgnoreCase) == true || r.Location.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.MetaText.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.AttentionTags.Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase)));

    private void Filter()
    {
        var tab = CurrentTab;
        StartupKind? kind = tab == StartupTab.Advanced && Enum.TryParse<StartupKind>(SelectedAdvancedKind?.Key, out var k) ? k : null;
        var q = Search.Trim();
        var shownPerTab = new int[Tabs.Count];
        int total = 0, off = 0, attention = 0;
        var shown = new List<StartupRow>();
        foreach (var r in _all)
        {
            var passes = Passes(r, q);
            if (passes) shownPerTab[(int)r.Tab]++;
            if (r.Tab != tab || (kind is { } only && r.Entry.Kind != only)) continue;
            total++;
            if (r.Entry.Enabled == false) off++;
            if (r.NeedsAttention) attention++;
            if (passes) shown.Add(r);
        }
        Rows.Clear();
        foreach (var r in shown.OrderByDescending(r => r.IsNew).ThenByDescending(r => r.NeedsAttention).ThenBy(r => r.Entry.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            Rows.Add(r);
        // The counts wait for the signatures, as the summary does: until then Windows' own entries are not hidden yet.
        foreach (var t in Tabs)
            t.Header = _checkingSignatures || _all.Count == 0 ? TabName(t.Tab) : $"{TabName(t.Tab)} ({shownPerTab[(int)t.Tab]})";
        _tabTotal = total;
        OnPropertyChanged(nameof(NoMatch));
        SummaryText = _all.Count == 0 ? "" : Summary(tab, total, off, attention, total - shown.Count, _checkingSignatures);
        OnPropertyChanged(nameof(ServicesHintTab));
        OnPropertyChanged(nameof(ShowServicesHint));
        OnPropertyChanged(nameof(IsAdvancedTab));
    }

    /// <summary>
    /// What the open tab holds, in sentences: how many entries, how many of them are turned off, how many need attention
    /// and how many the search and filters hide. While the signatures are checked, a note instead of the last two.
    /// </summary>
    internal static string Summary(StartupTab tab, int total, int off, int attention, int hidden, bool checking)
    {
        var one = total == 1;
        var count = Loc.Instance.Format(tab switch
        {
            StartupTab.SignIn => one ? "Startup_CountSignInOne" : "Startup_CountSignIn",
            StartupTab.Tasks => one ? "Startup_CountTasksOne" : "Startup_CountTasks",
            StartupTab.Services => one ? "Startup_CountServicesOne" : "Startup_CountServices",
            StartupTab.Drivers => one ? "Startup_CountDriversOne" : "Startup_CountDrivers",
            _ => one ? "Startup_CountAdvancedOne" : "Startup_CountAdvanced",
        }, total);
        List<string> parts =
        [
            off == 0 ? Loc.Instance.Format("Startup_SummaryCount", count)
            : one ? Loc.Instance.Format("Startup_SummaryCountOff", count)
            : Loc.Instance.Format("Startup_SummaryCountSomeOff", count, off),
        ];
        if (checking)
        {
            parts.Add(Loc.Instance["Startup_SummaryChecking"]);
        }
        else
        {
            if (attention > 0) parts.Add(Loc.Instance.Format(attention == 1 ? "Startup_AttentionOne" : "Startup_Attention", attention));
            if (hidden > 0) parts.Add(Loc.Instance.Format("Startup_Hidden", hidden));
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// A search result: its tab, with every filter off that could hide it and its name in the search box (search results
    /// exist only after the page read its entries).
    /// </summary>
    public void Reveal(string name)
    {
        HideMicrosoft = false;
        OnlyAttention = false;
        OnlyNew = false;
        if (_all.FirstOrDefault(r => r.Name == name) is { } row) SelectedTab = (int)row.Tab;
        if (AdvancedKinds.Count > 0) SelectedAdvancedKind = AdvancedKinds[0];
        Search = name;
    }

    public bool IsAdvancedTab => CurrentTab == StartupTab.Advanced;

    public bool ShowServicesHint => ServicesHintTab >= 0;

    /// <summary>The open tab has entries, but the search and filters leave none: the page says so.</summary>
    public bool NoMatch => _tabTotal > 0 && Rows.Count == 0;

    /// <summary>
    /// Services and scheduled tasks are listed here too (everything that starts), but their own page has the start types
    /// with explanations and every task: on those tabs, the tab of Services &amp; tasks to point to, else -1.
    /// </summary>
    public int ServicesHintTab => CurrentTab switch
    {
        StartupTab.Services => 0,
        StartupTab.Tasks => 1,
        _ => -1,
    };

    /// <summary>Looks up the hashes of shown entries that are not signed by Microsoft (opt-in, own API key).</summary>
    [RelayCommand]
    private async Task CheckVirusTotalAsync()
    {
        if (Owner.VirusTotalKey is not { Length: > 0 } key)
        {
            VirusTotalStatus = Loc.Instance["Vt_NoKey"];
            return;
        }
        _vtCancel = new CancellationTokenSource();
        IsCheckingVirusTotal = true;
        var lang = Lang;
        try
        {
            using var client = new VirusTotalClient(key);
            var targets = Rows.Where(r => !r.IsMicrosoft && r.Entry.ImagePath is { } p && File.Exists(p))
                .GroupBy(r => r.Entry.ImagePath!, StringComparer.OrdinalIgnoreCase).ToList();
            VirusTotalTakesMinutes = VirusTotalTakesMinutesFor(targets.Count, client.Spacing);
            var done = 0;
            foreach (var g in targets)
            {
                _vtCancel.Token.ThrowIfCancellationRequested();
                VirusTotalStatus = Loc.Instance.Format("Vt_Progress", ++done, targets.Count);
                string hash;
                try
                {
                    var userSid = Owner.Services.UserSid;
                    if (!await Task.Run(() => VirusTotalClient.UserCanRead(g.Key, userSid))) continue;
                    hash = await Task.Run(() => VirusTotalClient.Sha256(g.Key));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // locked or removed meanwhile: the other files are still checked
                }
                var result = await client.LookupAsync(hash, _vtCancel.Token);
                var text = Labels.Current.Get(lang, $"vt.{result.Verdict}") +
                           (result.Verdict is VirusTotalVerdict.Malicious or VirusTotalVerdict.Suspicious ? $" ({result.Malicious + result.Suspicious}/{result.Total})" : "");
                foreach (var row in g)
                {
                    row.VirusTotalText = text;
                    row.VirusTotalUrl = result.Verdict is VirusTotalVerdict.NotFound or VirusTotalVerdict.Error ? null : result.ReportUrl;
                }
                if (result.Verdict is VirusTotalVerdict.InvalidKey) break;
            }
            VirusTotalStatus = Loc.Instance["Vt_Done"];
        }
        catch (OperationCanceledException) when (_vtCancel?.IsCancellationRequested == true)
        {
            VirusTotalStatus = Loc.Instance["Vt_Stopped"];
        }
        catch (OperationCanceledException ex)
        {
            // An HTTP timeout, not the Stop button.
            VirusTotalStatus = Loc.Instance.Format("Result_Error", ex.Message);
        }
        finally
        {
            IsCheckingVirusTotal = false;
            VirusTotalTakesMinutes = false;
            _vtCancel?.Dispose();
            _vtCancel = null;
        }
    }

    [RelayCommand]
    private void StopVirusTotal() => _vtCancel?.Cancel();

    /// <summary>Saves the current list, so a later comparison shows what an installer or update added.</summary>
    [RelayCommand]
    private async Task SaveSnapshotAsync()
    {
        // While the list loads it is empty or partial: saving it would replace a good snapshot.
        if (IsLoading || _all.Count == 0)
        {
            SnapshotText = Loc.Instance["Startup_SnapshotWait"];
            return;
        }
        var entries = _all.Select(r => r.Entry).ToList();
        try
        {
            await Task.Run(() => StartupSnapshot.Save(StartupSnapshot.DefaultFile, StartupSnapshot.Take(entries, DateTimeOffset.Now)));
            SnapshotText = Loc.Instance.Format("Startup_SnapshotSaved", entries.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SnapshotText = Loc.Instance.Format("Result_Error", ex.Message);
            return;
        }
        _compareAfterLoad = false;
        HasComparison = false;
        OnlyNew = false;
        foreach (var r in _all) r.IsNew = false;
        Filter();
    }

    /// <summary>Saves every entry (not only the filtered ones) as a CSV file, in the UI language.</summary>
    [RelayCommand]
    private async Task ExportListAsync()
    {
        if (IsLoading || _all.Count == 0)
        {
            SnapshotText = Loc.Instance["Startup_SnapshotWait"];
            return;
        }
        var name = $"Tunero-Startup-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.csv";
        if (Owner.PickSaveFile(name, Loc.Instance["Startup_ExportFileType"], ".csv") is not { } path) return;
        var rows = _all.Select(r => new StartupExportRow(r.Name, r.KindText,
            r.Entry.Enabled switch { true => Loc.Instance["Startup_ExportOn"], false => Loc.Instance["Startup_ExportOff"], null => Loc.Instance["Startup_ReadOnly"] },
            r.PublisherText, r.SignatureText, r.Command, r.Entry.ImagePath, r.Location)).ToList();
        string[] headers = [.. Loc.Instance["Startup_ExportHeaders"].Split('|')];
        try
        {
            var csv = StartupExport.ToCsv(rows, headers, StartupExport.SeparatorFor(Loc.Regional));
            await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(true));
            Owner.ShowResult(Loc.Instance.Format("Startup_Exported", rows.Count, Path.GetFileName(path)), Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Owner.ShowResult(Loc.Instance.Format("Result_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task CompareSnapshotAsync()
    {
        if (IsLoading || _all.Count == 0)
        {
            SnapshotText = Loc.Instance["Startup_SnapshotWait"];
            return;
        }
        if (await Task.Run(() => StartupSnapshot.Load(StartupSnapshot.DefaultFile)) is not { } snapshot)
        {
            SnapshotText = Loc.Instance["Startup_NoSnapshot"];
            return;
        }
        _compareAfterLoad = true;
        ShowComparison(StartupSnapshot.Compare(snapshot, _all.Select(r => r.Entry)));
        Filter();
    }

    private void ShowComparison(SnapshotDiff diff)
    {
        foreach (var r in _all) r.IsNew = diff.NewKeys.Contains(r.Entry.Key);
        var text = Loc.Instance.Format("Startup_SnapshotDiff", diff.TakenAt.LocalDateTime.ToString("g"), diff.NewKeys.Count, diff.Removed.Count);
        if (diff.Removed.Count > 0)
            text += "\n" + Loc.Instance.Format("Startup_SnapshotRemoved", string.Join(", ", diff.Removed.Take(10).Select(e => e.Name)) + (diff.Removed.Count > 10 ? $" (+{diff.Removed.Count - 10})" : ""));
        SnapshotText = text;
        HasComparison = true;
    }

    /// <summary>Selects the entry's file in File Explorer (started as the signed-in user, never elevated).</summary>
    [RelayCommand]
    private void ShowFile(StartupRow? row)
    {
        if (row?.Entry.ImagePath is not { } path || !File.Exists(path))
        {
            Owner.ShowResult(Loc.Instance["Startup_FileMissing"], Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }
        DeElevatedLauncher.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{path}\"", services.Elevation);
    }

    /// <summary>Deletes the registry value through the engine: it is backed up first, and the Changes page puts it back.</summary>
    [RelayCommand]
    private async Task DeleteEntryAsync(StartupRow? row)
    {
        if (row is null || StartupTweaks.Delete(row.Entry) is not { } t) return;
        if (await runner.ApplyAsync(t)) await ReloadAsync();
    }

    [RelayCommand]
    private void CopyLocation(StartupRow? row)
    {
        if (row is null) return;
        try
        {
            System.Windows.Clipboard.SetText(row.Entry.Location);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is held by another program; nothing to do.
        }
    }

    [RelayCommand]
    private void OpenReport(string? url)
    {
        if (url is not null) Owner.OpenLink(url);
    }
}
