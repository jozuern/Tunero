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
        KindText = Labels.Current.Get(lang, $"startupKind.{entry.Kind}");
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
    public string KindText { get; }
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

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MetaText))] private string _publisherText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MetaText))] private string _signatureText = "";
    [ObservableProperty] private bool _isMicrosoft;

    /// <summary>Kind, publisher and signature; parts not known yet (signatures load after the list) are left out.</summary>
    public string MetaText => string.Join(", ", new[] { KindText, PublisherText, SignatureText }.Where(x => x.Length > 0));
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

public sealed partial class StartupViewModel(IPageHost owner, AppServices services, ChangeRunner runner) : PageViewModel(owner)
{
    private List<StartupRow> _all = [];
    private CancellationTokenSource? _vtCancel;

    public ObservableCollection<StartupRow> Rows { get; } = [];
    public ObservableCollection<FilterOption> Kinds { get; } = [];
    private string? _kindsLang;

    [ObservableProperty] private bool _hideMicrosoft = true;
    [ObservableProperty] private FilterOption? _selectedKind;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _virusTotalStatus = "";
    [ObservableProperty] private bool _isCheckingVirusTotal;
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
    partial void OnSelectedKindChanged(FilterOption? value) => Filter();

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
        // Built again after a language switch; the chosen kind stays selected.
        if (Kinds.Count == 0 || _kindsLang != lang)
        {
            // First view: the programs that start at sign-in (what Task Manager shows); every location is one choice away.
            var selectedKey = SelectedKind?.Key ?? SignInKey;
            _kindsLang = lang;
            Kinds.Clear();
            Kinds.Add(new FilterOption(SignInKey, Loc.Instance["Startup_SignInKinds"]));
            Kinds.Add(new FilterOption("", Loc.Instance["Startup_AllKinds"]));
            foreach (var k in Enum.GetValues<StartupKind>())
                Kinds.Add(new FilterOption(k.ToString(), Labels.Current.Get(lang, $"startupKind.{k}")));
            // Set the field directly: Filter() runs right after.
#pragma warning disable MVVMTK0034
            _selectedKind = Kinds.FirstOrDefault(k => k.Key == selectedKey) ?? Kinds[0];
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(SelectedKind));
        }
        // Signatures in the background, shown together: Microsoft entries are hidden once verified. Until then the counts
        // of other vendors and of what needs attention would be wrong (every entry still unverified): the summary waits.
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

    private bool _checkingSignatures;

    private const string SignInKey = "signIn";

    private static bool IsSignIn(StartupEntry e) => e.Kind is StartupKind.RunKey or StartupKind.StartupFolder || e is { Kind: StartupKind.LogonTask, StartsWithWindows: true };

    private void Filter()
    {
        var kind = SelectedKind?.Key ?? SignInKey;
        var q = Search.Trim();
        bool Matches(StartupRow r) => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.Command?.Contains(q, StringComparison.OrdinalIgnoreCase) == true || r.Location.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.MetaText.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.AttentionTags.Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        Rows.Clear();
        foreach (var r in _all.Where(r => (kind.Length == 0 || (kind == SignInKey ? IsSignIn(r.Entry) : r.Entry.Kind.ToString() == kind)) && !(HideMicrosoft && r.IsPlainMicrosoft) && (!OnlyNew || r.IsNew) && (!OnlyAttention || r.NeedsAttention) && Matches(r))
                     .OrderByDescending(r => r.IsNew).ThenByDescending(r => r.NeedsAttention).ThenBy(r => r.Entry.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            Rows.Add(r);
        OnPropertyChanged(nameof(NoMatch));
        var atLogon = _all.Count(r => StartupTweaks.CountsForF16(r.Entry) && !r.IsPlainMicrosoft);
        SummaryText = _checkingSignatures ? Loc.Instance.Format("Startup_SummaryChecking", _all.Count)
            : Loc.Instance.Format("Startup_Summary", _all.Count, atLogon, _all.Count(r => r.NeedsAttention));
        OnPropertyChanged(nameof(ServicesHintTab));
        OnPropertyChanged(nameof(ShowServicesHint));
    }

    public bool ShowServicesHint => ServicesHintTab >= 0;

    /// <summary>Entries were read but the search and filters leave none: the page says so.</summary>
    public bool NoMatch => _all.Count > 0 && Rows.Count == 0;

    /// <summary>
    /// Services and scheduled tasks are listed here too (everything that starts), but their own page has the start types
    /// with explanations and every task: with that kind chosen, the tab of Services &amp; tasks to point to, else -1.
    /// </summary>
    public int ServicesHintTab => SelectedKind?.Key switch
    {
        nameof(StartupKind.Service) => 0,
        nameof(StartupKind.LogonTask) => 1,
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
            var csv = StartupExport.ToCsv(rows, headers, StartupExport.SeparatorFor(System.Globalization.CultureInfo.CurrentCulture));
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
