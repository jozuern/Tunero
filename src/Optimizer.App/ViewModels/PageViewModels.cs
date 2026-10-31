using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core;
using Optimizer.Core.Actions;
using Optimizer.Core.Docs;
using Optimizer.Core.Logging;
using Optimizer.Core.Network;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.ViewModels;

/// <summary>Shared plumbing: lazy first load, busy state, the owner for results and the inspector.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    private Task? _load;

    protected PageViewModel(IPageHost owner)
    {
        Owner = owner;
        // What a page read may no longer be true after any change (a switch on the page changes only its own row).
        owner.Runner.Changed += (_, _) => _dataStale = true;
    }

    protected IPageHost Owner { get; }

    /// <summary>A change was made since the last full load: a language switch reads the page again instead of relabeling.</summary>
    private bool _dataStale;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isWorking;

    public Task EnsureLoadedAsync() => _load ??= ReloadAsync();

    /// <summary>The page builds rows from the scan result (<see cref="MainViewModel.Profile"/>).</summary>
    protected virtual bool UsesProfile => false;

    /// <summary>The scan result the last full load used; null when it ran before the first scan finished (or after it failed).</summary>
    private Optimizer.Core.Hardware.HardwareProfile? _loadedFrom;

    /// <summary>
    /// A scan brought a new result: a page that loaded without one reads again; the others update what they show from it
    /// (<see cref="ProfileUpdated"/>).
    /// </summary>
    public void OnProfileChanged()
    {
        if (!UsesProfile || _load is null || Owner.Profile is null) return;
        if (_loadedFrom is null)
        {
            // A load that waits for this scan reads its result itself: a reload now would only run it a second time.
            if (!_waitingForScan) ReloadAsync().Forget($"{GetType().Name} reload with the scan result");
        }
        else
        {
            ProfileUpdated();
        }
    }

    /// <summary>A load waits for the first scan's result (<see cref="RunOnceAsync"/>).</summary>
    private bool _waitingForScan;

    /// <summary>After a later scan, for pages whose rows come from the scan result alone (cheap to build again).</summary>
    protected virtual void ProfileUpdated()
    {
    }

    /// <summary>The page was opened (or its data loaded ahead) at least once.</summary>
    protected bool WasLoaded => _load is not null;

    /// <summary>
    /// Runs a page operation that changes the system (see <see cref="Services.ChangeRunner.RunExclusiveAsync"/>): the page
    /// shows it as working, no tweak, scan or update runs at the same time, and an error is logged and shown.
    /// </summary>
    /// <param name="slow">The banner says that it can take several minutes.</param>
    /// <param name="duration">Another wording for that (<see cref="Durations"/>), such as DISM's wait for Windows updates.</param>
    protected Task<bool> RunWorkAsync(string what, Func<Task> work, bool slow = false, string? duration = null) =>
        RunWorkAsync(what, Loc.Instance["Busy"], _ => work(), slow: slow, duration: duration);

    /// <summary>The same with the progress banner titled <paramref name="title"/>, and Stop when <paramref name="stop"/> is given.</summary>
    protected async Task<bool> RunWorkAsync(string what, string title, Func<Services.WorkProgress.Scope, Task> work, CancellationTokenSource? stop = null, bool slow = false,
        string? duration = null)
    {
        IsWorking = true;
        try
        {
            return await Owner.Runner.RunExclusiveAsync(what, title, work, stop, slow, duration);
        }
        finally
        {
            IsWorking = false;
            _dataStale = true;
        }
    }

    partial void OnIsLoadingChanged(bool value) => LoadingChanged();

    partial void OnIsWorkingChanged(bool value) => WorkingChanged();

    /// <summary>For commands that must not start while the page's own work (<see cref="RunWorkAsync(string, Func{Task})"/>) runs.</summary>
    protected virtual void WorkingChanged()
    {
    }

    /// <summary>For "nothing found" texts that must stay hidden while the page still loads.</summary>
    protected virtual void LoadingChanged()
    {
    }

    /// <summary>
    /// After a language switch: a page that was opened builds its rows again in the new language, from the data it
    /// already read (<see cref="RelabelAsync"/>).
    /// </summary>
    public void OnLanguageChanged()
    {
        if (_load is not null) RunAsync(fullLoad: false).Forget($"{GetType().Name} language switch");
    }

    private Task? _running;
    private bool _reloadRequested;
    private bool _relabelRequested;

    /// <summary>The load that runs now, or a finished task (the --perf report waits for page loads with it).</summary>
    public Task Loading => _running ?? Task.CompletedTask;

    /// <summary>
    /// One load at a time: a reload asked for while one runs (Refresh, the first load and a reload after a change can
    /// overlap) runs once after it, so two loads never fill the same list at once.
    /// </summary>
    [RelayCommand]
    public Task ReloadAsync() => RunAsync(fullLoad: true);

    /// <summary>
    /// A relabel asked for while a load runs follows that load (its rows may be in the old language); a reload asked for
    /// during a relabel replaces the relabel.
    /// </summary>
    private Task RunAsync(bool fullLoad)
    {
        if (_running is { IsCompleted: false })
        {
            if (fullLoad) _reloadRequested = true;
            else _relabelRequested = true;
            return _running;
        }
        return _running = RunUntilCurrentAsync(fullLoad);
    }

    private async Task RunUntilCurrentAsync(bool fullLoad)
    {
        while (true)
        {
            _reloadRequested = false;
            _relabelRequested = false;
            await RunOnceAsync(fullLoad);
            if (_reloadRequested) fullLoad = true;
            else if (_relabelRequested) fullLoad = false;
            else return;
        }
    }

    private async Task RunOnceAsync(bool fullLoad)
    {
        if (_dataStale) fullLoad = true;
        // A change during this load marks the data stale again.
        if (fullLoad) _dataStale = false;
        // Relabeling reads nothing: no loading state, the rows only change their text.
        if (fullLoad) IsLoading = true;
        try
        {
            // Opened while the first scan still runs: its result comes first (an empty list would read as "nothing found").
            if (fullLoad && UsesProfile && Owner.Profile is null)
            {
                _waitingForScan = true;
                try
                {
                    await Owner.ScanInProgress;
                }
                finally
                {
                    _waitingForScan = false;
                }
            }
            if (fullLoad) _loadedFrom = Owner.Profile;
            await (fullLoad ? LoadAsync() : RelabelAsync());
        }
        catch (Exception ex)
        {
            Log.Error("ui", $"{GetType().Name} load failed", ex);
            Owner.ShowResult(Loc.Instance.Format("Result_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            if (fullLoad) IsLoading = false;
        }
    }

    /// <summary>Reads the system and builds the rows.</summary>
    protected abstract Task LoadAsync();

    /// <summary>
    /// Builds the rows again in the current language from what <see cref="LoadAsync"/> read last. Pages whose reading is
    /// slow (tools, the startup list, folder sizes) override it; the others read again.
    /// </summary>
    protected virtual Task RelabelAsync() => LoadAsync();

    protected string Lang => Loc.Instance.Language;
}

/// <summary>A row with a switch that runs an engine tweak (startup entries, tasks, features). Same pattern as tweak rows.</summary>
public abstract partial class SwitchRow : ObservableObject
{
    private bool _switch;

    protected SwitchRow(bool isOn)
    {
        IsOn = isOn;
        _switch = isOn;
    }

    public bool IsOn { get; protected set; }

    private bool _canToggle = true;

    public bool CanToggle
    {
        get => _canToggle;
        protected set => SetProperty(ref _canToggle, value);
    }

    public bool SwitchState
    {
        get => _switch;
        set
        {
            if (value == _switch) return;
            _switch = value;
            OnPropertyChanged();
            if (value != IsOn) RunAsync(value).Forget("switch");
        }
    }

    private async Task RunAsync(bool on)
    {
        try
        {
            // Return from the binding setter before the confirmation opens: a modal dialog inside the setter keeps the
            // switch's click (and a screen reader's toggle call) waiting until the dialog closes.
            await Task.Yield();
            if (await ToggleAsync(on)) IsOn = on;
        }
        finally
        {
            _switch = IsOn;
            OnPropertyChanged(nameof(SwitchState));
        }
    }

    /// <summary>Returns true when the change was written.</summary>
    protected abstract Task<bool> ToggleAsync(bool on);
}

// ---------------- Graphics & DNS ----------------

public sealed record DnsResultRow(string Name, string Server, string Median, string Best, string Answered)
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;
}

public sealed partial class NetworkViewModel(IPageHost owner, AppServices services) : PageViewModel(owner)
{
    public ObservableCollection<TweakItemViewModel> GameProfiles { get; } = [];
    public ObservableCollection<TweakItemViewModel> DeviceTweaks { get; } = [];
    public ObservableCollection<TweakItemViewModel> DnsOptions { get; } = [];
    public ObservableCollection<TweakItemViewModel> GpuTweaks { get; } = [];
    public ObservableCollection<DnsResultRow> DnsResults { get; } = [];

    [ObservableProperty] private string _currentDns = "";
    [ObservableProperty] private bool _hasNvidia;
    /// <summary>The scan found graphics cards and none is NVIDIA (not while the first scan still runs).</summary>
    [ObservableProperty] private bool _noNvidia;
    [ObservableProperty] private string _benchmarkStatus = "";

    public bool ShowDeviceTweaks => Owner.ExpertMode && DeviceTweaks.Count > 0;
    public bool GameProfilesEmpty => GameProfiles.Count == 0;

    /// <summary>
    /// The game rows in view. A library can list dozens of games, which made the page endless: without a search the first
    /// few show, plus every game whose profile is on (the ones people come back for), and "Show all" opens the rest.
    /// </summary>
    public ObservableCollection<TweakItemViewModel> GameProfilesShown { get; } = [];

    private const int GamesShownFirst = 6;

    [ObservableProperty] private string _gameSearch = "";
    [ObservableProperty] private bool _showAllGames;
    [ObservableProperty] private string? _showAllGamesText;

    partial void OnGameSearchChanged(string value) => ShowGames();
    partial void OnShowAllGamesChanged(bool value) => ShowGames();

    /// <summary>The search box only for a list longer than what shows at first.</summary>
    public bool GameSearchVisible => GameProfiles.Count > GamesShownFirst;

    public bool GameSearchEmpty => GameProfiles.Count > 0 && GameProfilesShown.Count == 0;

    [RelayCommand]
    private void ExpandGames() => ShowAllGames = true;

    private void ShowGames()
    {
        var q = GameSearch.Trim();
        var matches = GameProfiles.Where(g => q.Length == 0 || g.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var shown = q.Length > 0 || ShowAllGames ? matches : matches.Where((g, i) => i < GamesShownFirst || g.IsOn).ToList();
        GameProfilesShown.Clear();
        foreach (var g in shown) GameProfilesShown.Add(g);
        ShowAllGamesText = shown.Count < matches.Count ? Loc.Instance.Format("Net_ShowAllGames", matches.Count) : null;
        OnPropertyChanged(nameof(GameSearchVisible));
        OnPropertyChanged(nameof(GameSearchEmpty));
    }

    /// <summary>Impact, effects and tags the rows of a list share ("Impact 1 of 5, 1 % lows, Preview"), shown once above it.</summary>
    [ObservableProperty] private string? _gameProfilesFacts;

    [ObservableProperty] private string? _deviceFacts;

    private static string? SharedFacts(IReadOnlyCollection<TweakItemViewModel> rows)
    {
        if (rows.Count == 0) return null;
        // "Impact 1 of 5. Affects: 1 % lows. Not yet tested on a real PC", in the words of a row's line
        static string Facts(TweakItemViewModel r) => string.Join(". ", new[]
        {
            r.ImpactText,
            r.EffectsText.Length > 0 ? Loc.Instance.Format("Meta_Affects", r.EffectsText) : "",
            r.BadgesText,
        }.Where(x => !string.IsNullOrEmpty(x)));
        var all = rows.Select(Facts).Distinct().ToList();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>The DNS presets as one choice: only one can be in use, so a list of switches said the wrong thing.</summary>
    public ObservableCollection<DnsChoice> DnsChoices { get; } = [];

    /// <summary>Shown when no preset matches the servers in use (set by hand or by another program).</summary>
    [ObservableProperty] private bool _dnsIsCustom;

    private DnsChoice? _selectedDns;

    /// <summary>Bound to the DNS combo box: choosing an entry undoes the preset this app set (if any) and applies the new one.</summary>
    public DnsChoice? SelectedDns
    {
        get => _selectedDns;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedDns)) return;
            _selectedDns = value;
            OnPropertyChanged();
            ChooseDnsAsync(value).Forget("DNS choice");
        }
    }

    /// <summary>Opens the explanation of the DNS presets (one page for all of them) in the details pane.</summary>
    [RelayCommand]
    private void ShowDnsDetails() => Owner.SelectedItem = (SelectedDns ?? DnsChoices.FirstOrDefault())?.Item;

    private void ShowDnsSelection()
    {
        _selectedDns = DnsChoices.FirstOrDefault(c => c.Item.IsOn);
        DnsIsCustom = _selectedDns is null && DnsChoices.Count > 0;
        OnPropertyChanged(nameof(SelectedDns));
    }

    private async Task ChooseDnsAsync(DnsChoice choice)
    {
        // Not inside the combo box's selection change: the confirmation dialog opens from here.
        await Task.Yield();
        try
        {
            if (choice.Item.IsOn) return;
            if (!ChangeGate.Instance.CanChange)
            {
                Owner.ShowResult(Loc.Instance["Change_Busy"]);
                return;
            }
            // Presets block each other while one has a backup: undo the one this app set first.
            if (DnsChoices.FirstOrDefault(c => c.Item.HasBackup && !ReferenceEquals(c, choice)) is { } current)
            {
                if (!await Owner.Runner.UndoAsync(current.Item.Tweak)) return;
                // The undo starts a rescan, and no change can run while it does: wait for it (it also brings the facts the
                // check below needs up to date) instead of having the apply refused as busy.
                // Not ScanCommand: a call during that rescan asks for one more, a full one, and the apply waited for it.
                await Owner.ScanInProgress;
                // Undo brings back the servers from before, which may already be the choice (usually automatic).
                var facts = Owner.Facts;
                if ((await Task.Run(() => services.Engine.Detect(choice.Item.Tweak, facts))).IsOn) return;
            }
            await Owner.Runner.ApplyAsync(choice.Item.Tweak);
        }
        finally
        {
            // A cancelled dialog shows the servers in use again; after a change the rescan rebuilds the list.
            ShowDnsSelection();
        }
    }

    protected override Task LoadAsync()
    {
        BuildRows();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilt after every scan and switch (device states come from the main scan), once the page was opened: until
    /// then nobody sees the rows, and opening the page builds them.
    /// </summary>
    public void Rebuild()
    {
        if (WasLoaded) BuildRows();
    }

    private void BuildRows()
    {
        var lang = Lang;
        var engine = services.Engine;
        GameProfiles.Clear();
        DeviceTweaks.Clear();
        foreach (var s in Owner.DeviceStates.OrderBy(s => s.Tweak.Subject, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new TweakItemViewModel(s, lang, engine, Owner, subjectAsTitle: true);
            if (s.Tweak.Id.StartsWith("nvidia.game.", StringComparison.Ordinal)) GameProfiles.Add(item);
            else DeviceTweaks.Add(item);
        }
        GpuTweaks.Clear();
        DnsOptions.Clear();
        foreach (var t in Owner.CatalogItems(t => t.Id.StartsWith("network.dns.", StringComparison.Ordinal))) DnsOptions.Add(t);
        DnsChoices.Clear();
        // Automatic (from the router) first, then the public resolvers in catalog order.
        foreach (var t in DnsOptions.OrderBy(t => t.Tweak.Subject is null ? 0 : 1))
            DnsChoices.Add(new DnsChoice(t, t.Tweak.Subject ?? Loc.Instance["Net_DnsAutomatic"]));
        ShowDnsSelection();
        foreach (var t in Owner.CatalogItems(t => t.Id.StartsWith("nvidia.", StringComparison.Ordinal))) GpuTweaks.Add(t);
        HasNvidia = Owner.Profile?.Gpus?.Any(g => g.Vendor == Optimizer.Core.Hardware.Vendor.Nvidia) == true;
        NoNvidia = Owner.Profile is not null && !HasNvidia;
        ShowCurrentDnsAsync().Forget("current DNS servers");
        GameProfilesFacts = SharedFacts(GameProfiles);
        DeviceFacts = SharedFacts(DeviceTweaks);
        ShowGames();
        OnPropertyChanged(nameof(ShowDeviceTweaks));
        OnPropertyChanged(nameof(GameProfilesEmpty));
    }

    /// <summary>The DNS servers in use, read off the UI thread (it asks every network adapter).</summary>
    private async Task ShowCurrentDnsAsync()
    {
        IReadOnlyList<System.Net.IPAddress> servers;
        try
        {
            servers = await Task.Run(DnsBenchmark.CurrentServers);
        }
        catch (Exception)
        {
            servers = [];
        }
        // The router's own address (or a link-local one) means nothing to most people: "Your router", the addresses in the tooltip.
        var detail = string.Join(", ", servers);
        CurrentDns = servers.Count == 0 ? Loc.Instance["Net_NoDns"]
            : servers.All(IsLocal) ? Loc.Instance.Format("Net_DnsRouter", string.Join(", ", servers.Where(s => !s.IsIPv6LinkLocal)))
            : detail;
        CurrentDnsDetail = detail;
    }

    [ObservableProperty] private string? _currentDnsDetail;

    /// <summary>An address in the home network: private IPv4 ranges, link-local or unique-local IPv6.</summary>
    private static bool IsLocal(System.Net.IPAddress a)
    {
        if (a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal || a.IsIPv6SiteLocal) return true;
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private string? _fastestPresetId;
    [ObservableProperty] private string? _fastestText;

    /// <summary>Turns on the DNS preset of the fastest public server from the benchmark (with the usual confirmation).</summary>
    [RelayCommand]
    private void UseFastest()
    {
        if (DnsChoices.FirstOrDefault(c => c.Item.Tweak.Id == _fastestPresetId) is { } preset) SelectedDns = preset;
        FastestText = null;
    }

    [RelayCommand]
    private async Task RunDnsBenchmarkAsync()
    {
        FastestText = null;
        IsWorking = true;
        DnsResults.Clear();
        try
        {
            var current = (await Task.Run(DnsBenchmark.CurrentServers)).Select((a, i) => (Loc.Instance.Format("Net_CurrentServer", i + 1), a));
            var servers = current.Concat(DnsBenchmark.PublicServers).DistinctBy(s => s.Item2).ToList();
            var progress = new Progress<string>(name => BenchmarkStatus = Loc.Instance.Format("Net_Testing", name));
            var results = await Task.Run(() => DnsBenchmark.RunAsync(servers, 2, progress));
            var best = results.FirstOrDefault(r => r.MedianMs is not null);
            // A preset for the fastest server, unless it is already in use.
            _fastestPresetId = best is null ? null : DnsBenchmark.PresetFor(best.Server);
            var preset = DnsOptions.FirstOrDefault(o => o.Tweak.Id == _fastestPresetId);
            // Offered unless it is in use already; choosing it undoes another preset of this app first.
            FastestText = preset is { IsOn: false } ? Loc.Instance.Format("Net_UseFastest", preset.Tweak.Subject ?? best!.Name) : null;
            foreach (var r in results)
                DnsResults.Add(new DnsResultRow(r.Name, r.Server.ToString(), r.MedianMs is { } m ? $"{m:0.0} ms" : Loc.Instance["Net_NoAnswer"],
                    r.BestMs is { } b ? $"{b:0.0} ms" : "", $"{r.Answered}/{r.Sent}"));
            BenchmarkStatus = Loc.Instance["Net_BenchmarkDone"];
        }
        catch (Exception ex)
        {
            BenchmarkStatus = Loc.Instance.Format("Result_Error", ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }
}

/// <summary>One entry of the DNS combo box: the preset's row (state, explanation) and its short name.</summary>
public sealed record DnsChoice(TweakItemViewModel Item, string Name)
{
    // What screen readers announce for this item in a combo box.
    public override string ToString() => Name;
}

// ---------------- Debloat ----------------

public sealed partial class DebloatItem(Optimizer.Core.Debloat.DebloatItem item, string lang, bool elevated) : ObservableObject
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;

    public Optimizer.Core.Debloat.DebloatItem Item { get; } = item;
    public string Name { get; } = item.Entry.Label(lang);
    public string PackageName => Item.Installed.Name;

    /// <summary>"Remove Camera": what a screen reader says for the button (each row has one).</summary>
    public string RemoveLabel => Optimizer.App.Services.Loc.Instance.Format("Debloat_RemoveName", Name);
    public string Text { get; } = item.Entry.Text(lang);
    public string Version => Item.Installed.Version;

    /// <summary>The group of the reviewed list ("Xbox components"): the page lists the apps under a header per group.</summary>
    public string Group { get; } = Labels.Current.Get(lang, $"debloatGroup.{item.Entry.Group}");

    // Removing for all users needs administrator rights: the page says that once above the list, so the row only
    // shows reasons that belong to this app.
    public string? BlockText { get; } = item.BlockKey is { } k ? Labels.Current.Get(lang, k) : null;
    public bool CanRemove => BlockText is null && elevated;

    /// <summary>Ticked for "Remove selected"; only an app that can be removed can be ticked.</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    /// <summary>Tells the page that the number of ticked apps changed.</summary>
    internal Action? SelectionChanged { get; set; }

    public string SelectLabel => Optimizer.App.Services.Loc.Instance.Format("Programs_SelectName", Name);
}

/// <summary>An installed package the reviewed list does not cover: its package name is all the app knows about it.</summary>
public sealed record UnreviewedRow(Optimizer.Core.Debloat.InstalledAppx App, bool CanRemove)
{
    public string Name => App.Name;
    public string Meta => Loc.Instance.Format("Debloat_UnreviewedMeta", App.Version);

    /// <summary>"Remove Xbox": what a screen reader says for the row's button.</summary>
    public string RemoveLabel => Loc.Instance.Format("Debloat_RemoveName", Name);

    // What screen readers announce for this item in a list.
    public override string ToString() => Name;
}

public sealed record RemovedRow(string Name, string When, string? StoreLink)
{
    /// <summary>"Reinstall Xbox": what a screen reader says for the row's button.</summary>
    public string ReinstallLabel => Loc.Instance.Format("Debloat_ReinstallName", Name);

    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Name;
}

public sealed partial class DebloatViewModel(IPageHost owner, AppServices services, IDialogs dialogs) : PageViewModel(owner)
{
    /// <summary>Offers and guards depend on the scan result (games, platform).</summary>
    protected override bool UsesProfile => true;
    private Optimizer.Core.Debloat.DebloatService Service => new(services.Context.Processes, AppServices.DataFolder);

    public ObservableCollection<DebloatItem> Items { get; } = [];
    public ObservableCollection<RemovedRow> Removed { get; } = [];

    /// <summary>Apps the reviewed list does not cover (DebloatService.Unreviewed), shown only when the switch is on.</summary>
    public ObservableCollection<UnreviewedRow> Unreviewed { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreviewedEmpty))]
    private bool _showUnreviewed;

    partial void OnShowUnreviewedChanged(bool value)
    {
        if (_data is not null) ShowUnreviewedRows(_data);
    }

    /// <summary>The switch is on and nothing beyond the reviewed list can be removed.</summary>
    public bool UnreviewedEmpty => ShowUnreviewed && !IsLoading && Unreviewed.Count == 0;

    [ObservableProperty] private string _oneDriveText = "";
    [ObservableProperty] private string? _oneDriveBlock;
    [ObservableProperty] private string? _cameBackText;
    [ObservableProperty] private bool _canUninstallOneDrive;
    [ObservableProperty] private bool _oneDriveInstalled;

    /// <summary>Why Windows does not let the user remove Edge here (not installed, region), or null.</summary>
    [ObservableProperty] private string? _edgeBlock;
    [ObservableProperty] private bool _canRemoveEdge;
    [ObservableProperty] private bool _edgeInstalled;
    [ObservableProperty] private string? _edgeMissing;

    public bool ItemsEmpty => !IsLoading && Items.Count == 0;

    /// <summary>Apps ticked for "Remove selected", as on Installed programs.</summary>
    [ObservableProperty] private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    /// <summary>The bar above the list: how many apps are ticked, or that ticking removes several at once.</summary>
    public string SelectionText => SelectedCount == 0 ? Loc.Instance["Debloat_SelectHint"] : Loc.Instance.Format("Debloat_SelectedCount", SelectedCount);

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    private void UpdateSelection() => SelectedCount = Items.Count(i => i.IsSelected);

    protected override void LoadingChanged()
    {
        OnPropertyChanged(nameof(ItemsEmpty));
        OnPropertyChanged(nameof(UnreviewedEmpty));
    }
    public bool RemovedEmpty => Removed.Count == 0;

    private sealed record DebloatData(
        IReadOnlyList<Optimizer.Core.Debloat.DebloatItem> Offered,
        IReadOnlyList<Optimizer.Core.Debloat.RemovedApp> Removed,
        Optimizer.Core.Debloat.OneDriveState OneDrive,
        Optimizer.Core.Debloat.EdgeState Edge,
        IReadOnlyList<Optimizer.Core.Debloat.RemovedApp> CameBack,
        IReadOnlyList<Optimizer.Core.Debloat.InstalledAppx> Unreviewed);

    private DebloatData? _data;

    protected override async Task LoadAsync()
    {
        var profile = Owner.Profile;
        var elevated = services.Elevation.IsElevated;
        _data = await Task.Run(() =>
        {
            var service = Service;
            var installed = service.ListInstalled(allUsers: elevated);
            var offer = profile is null ? [] : Optimizer.Core.Debloat.DebloatService.Offer(Optimizer.Core.Catalog.CatalogData.Current.Appx, installed, profile, Optimizer.Core.Catalog.CatalogData.Current);
            var state = Optimizer.Core.Debloat.OneDrive.Read(services.Context.Registry, services.ProfilePath);
            var removedList = service.Removed();
            var edge = Optimizer.Core.Debloat.Edge.Read(services.Context.Registry);
            return new DebloatData(offer, removedList, state, edge, Optimizer.Core.Debloat.DebloatService.CameBack(removedList, installed),
                Optimizer.Core.Debloat.DebloatService.Unreviewed(Optimizer.Core.Catalog.CatalogData.Current.Appx, installed));
        });
        Show(_data);
    }

    // A language switch builds the rows from the app list already read (listing the apps starts PowerShell).
    protected override Task RelabelAsync()
    {
        if (_data is null) return LoadAsync();
        Show(_data);
        return Task.CompletedTask;
    }

    private void Show(DebloatData data)
    {
        var lang = Lang;
        var elevated = services.Elevation.IsElevated;
        var (offered, removed, oneDrive, edge, cameBack, _) = data;
        // The ticks survive a reload and a language switch for the apps that are still listed.
        var ticked = Items.Where(i => i.IsSelected).Select(i => i.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items.Clear();
        foreach (var i in offered.OrderBy(i => i.Entry.Group).ThenBy(i => i.Entry.Label(lang)))
        {
            var item = new DebloatItem(i, lang, elevated);
            item.IsSelected = item.CanRemove && ticked.Contains(item.PackageName);
            item.SelectionChanged = UpdateSelection;
            Items.Add(item);
        }
        UpdateSelection();
        // A language switch with the count unchanged: the bar's text in the new language.
        OnPropertyChanged(nameof(SelectionText));
        _cameBack = cameBack.Select(r => r.Name).ToList();
        CameBackText = cameBack.Count == 0 ? null : Loc.Instance.Format("Debloat_CameBack", string.Join(", ", cameBack.Select(r => r.Name)));
        Removed.Clear();
        // The app name from the catalog (the record keeps the package name); the package name for apps no longer listed.
        var apps = Optimizer.Core.Catalog.CatalogData.Current.Appx.Apps;
        foreach (var r in removed.OrderByDescending(r => r.RemovedAt))
            Removed.Add(new RemovedRow(apps.FirstOrDefault(a => a.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))?.Label(lang) ?? r.Name,
                r.RemovedAt.LocalDateTime.ToString("g"), r.StoreLink));
        // "Not installed" is already the description; repeating it as a warning adds nothing.
        OneDriveBlock = oneDrive.Installed && oneDrive.BlockKey is { } b ? Labels.Current.Get(lang, b) : null;
        CanUninstallOneDrive = oneDrive.BlockKey is null;
        OneDriveInstalled = oneDrive.Installed;
        OneDriveText = oneDrive.Installed
            ? Loc.Instance.Format("Debloat_OneDriveInstalled", oneDrive.UserFolder ?? Loc.Instance["Debloat_OneDriveNotSignedIn"])
            : Loc.Instance["Debloat_NotInstalled"];
        // As for OneDrive: "not installed" is plain text, only a reason Windows gives is a warning.
        EdgeBlock = edge.Installed && edge.BlockKey is { } eb ? Labels.Current.Get(lang, eb) : null;
        EdgeMissing = edge.Installed ? null : Loc.Instance["Debloat_NotInstalled"];
        CanRemoveEdge = edge.WindowsAllows;
        EdgeInstalled = edge.Installed;
        OnPropertyChanged(nameof(ItemsEmpty));
        OnPropertyChanged(nameof(RemovedEmpty));
        ShowUnreviewedRows(data);
    }

    private void ShowUnreviewedRows(DebloatData data)
    {
        Unreviewed.Clear();
        if (ShowUnreviewed)
            foreach (var app in data.Unreviewed) Unreviewed.Add(new UnreviewedRow(app, services.Elevation.IsElevated));
        OnPropertyChanged(nameof(UnreviewedEmpty));
        // The search finds what the page lists: the reviewed apps, and the unreviewed ones while the switch shows them.
        services.Search.UpdateRuntime(Optimizer.Core.Search.SearchKind.DebloatApp,
            Items.Select(i => ("Debloat", i.PackageName, i.Name, i.Text)).Concat(Unreviewed.Select(u => ("Debloat", u.Name, u.Name, u.Meta))));
    }

    /// <summary>An app nobody reviewed: its own warning in the confirmation, removed like a reviewed one (all users, no Store link).</summary>
    [RelayCommand]
    private async Task RemoveUnreviewedAsync(UnreviewedRow? row)
    {
        if (row is null || !row.CanRemove) return;
        if (!dialogs.Ask(Loc.Instance.Format("Debloat_ConfirmTitle", row.Name), Loc.Instance.Format("Debloat_UnreviewedConfirm", row.Name), Loc.Instance["Debloat_Remove"])) return;
        await RunWorkAsync($"remove unreviewed {row.Name}", async () =>
        {
            var error = await Task.Run(() => Service.Remove(row.App));
            Owner.ShowResult(error is null ? Loc.Instance.Format("Debloat_Removed", row.Name) : Loc.Instance.Format("Result_Error", error),
                error is null ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Error);
        });
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(DebloatItem? item)
    {
        if (item is null || !item.CanRemove) return;
        var confirm = item.Item.Entry.CanReinstall ? "Debloat_ConfirmText" : "Debloat_ConfirmTextNoStore";
        if (!dialogs.Ask(Loc.Instance.Format("Debloat_ConfirmTitle", item.Name), Loc.Instance.Format(confirm, item.Text), Loc.Instance["Debloat_Remove"])) return;
        await RunWorkAsync($"remove {item.Item.Entry.Name}", async () =>
        {
            var error = await Task.Run(() => Service.Remove(item.Item.Installed, item.Item.Entry));
            Owner.ShowResult(error is null ? Loc.Instance.Format("Debloat_Removed", item.Name) : Loc.Instance.Format("Result_Error", error),
                error is null ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Error);
        });
        await ReloadAsync();
    }

    /// <summary>
    /// Removes the ticked apps one after the other after one confirmation that names each of them (and the ones the Store
    /// no longer offers). An app that fails does not stop the others; the result names it, the log has the reason.
    /// </summary>
    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var picked = Items.Where(i => i.IsSelected && i.CanRemove).ToList();
        if (picked.Count == 0) return;
        var text = Loc.Instance.Format("Debloat_ConfirmSelected", string.Join(", ", picked.Select(i => i.Name)));
        if (picked.Where(i => !i.Item.Entry.CanReinstall).Select(i => i.Name).ToList() is { Count: > 0 } noStore)
            text += "\n\n" + Loc.Instance.Format("Debloat_ConfirmSelectedNoStore", string.Join(", ", noStore));
        if (!dialogs.Ask(Loc.Instance["Debloat_ConfirmSelectedTitle"], text, Loc.Instance["Debloat_Remove"])) return;
        var removed = 0;
        var failed = new List<string>();
        var ran = await RunWorkAsync("remove selected apps", Loc.Instance["Debloat_Removing"], async scope =>
        {
            for (var i = 0; i < picked.Count; i++)
            {
                var item = picked[i];
                scope.Step(i, picked.Count, item.Name);
                var error = await Task.Run(() => Service.Remove(item.Item.Installed, item.Item.Entry));
                if (error is null)
                {
                    removed++;
                }
                else
                {
                    failed.Add(item.Name);
                    Optimizer.Core.Logging.Log.Warn("debloat", $"{item.Item.Entry.Name} not removed: {error}");
                }
            }
        });
        if (ran)
            Owner.ShowResult(failed.Count == 0 ? Loc.Instance.Format("Debloat_RemovedCount", removed) : Loc.Instance.Format("Debloat_RemovedSome", removed, string.Join(", ", failed)),
                failed.Count == 0 ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning);
        await ReloadAsync();
    }

    private IReadOnlyList<string> _cameBack = [];

    /// <summary>The user installed the apps again on purpose (for example with the Store link): stop the notice for them.</summary>
    [RelayCommand]
    private async Task DismissCameBackAsync()
    {
        var names = _cameBack;
        await Task.Run(() => Service.Forget(names));
        CameBackText = null;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task UninstallOneDriveAsync()
    {
        var state = await Task.Run(() => Optimizer.Core.Debloat.OneDrive.Read(services.Context.Registry, services.ProfilePath));
        if (state.BlockKey is { } block)
        {
            Owner.ShowResult(Labels.Current.Get(Lang, block));
            return;
        }
        if (!dialogs.Ask(Loc.Instance["Debloat_OneDriveTitle"], Loc.Instance["Debloat_OneDriveConfirm"], Loc.Instance["Debloat_Remove"])) return;
        // Through the change gate like every other change: not while a tweak, a scan or an update runs.
        await RunWorkAsync("OneDrive uninstall", () =>
        {
            try
            {
                var path = Optimizer.Core.Debloat.OneDrive.Uninstall(state, services.Elevation);
                Owner.ShowResult(path == Optimizer.Core.Platform.DeElevatedLauncher.Path.Failed ? Loc.Instance.Format("Result_Error", "OneDriveSetup") : Loc.Instance["Debloat_OneDriveStarted"]);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // OneDriveSetup.exe could not be started (removed or blocked meanwhile).
                Owner.ShowResult(Loc.Instance.Format("Result_Error", $"OneDriveSetup: {ex.Message}"), Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Opens Installed apps in Settings: Edge's uninstaller refuses to run for Tunero (Optimizer.Core.Debloat.Edge), so
    /// Windows removes it there. Read again first: the page may have been open for a while.
    /// </summary>
    [RelayCommand]
    private async Task RemoveEdgeAsync()
    {
        var state = await Task.Run(() => Optimizer.Core.Debloat.Edge.Read(services.Context.Registry));
        if (state.BlockKey is { } block)
        {
            Owner.ShowResult(Labels.Current.Get(Lang, block));
            await ReloadAsync();
            return;
        }
        try
        {
            var path = Optimizer.Core.Platform.DeElevatedLauncher.Open(Optimizer.Core.Debloat.Edge.InstalledAppsPage, null, services.Elevation);
            Owner.ShowResult(path == Optimizer.Core.Platform.DeElevatedLauncher.Path.Failed
                ? Loc.Instance.Format("Result_Error", Optimizer.Core.Debloat.Edge.InstalledAppsPage)
                : Loc.Instance["Debloat_EdgeOpened"]);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Owner.ShowResult(Loc.Instance.Format("Result_Error", $"{Optimizer.Core.Debloat.Edge.InstalledAppsPage}: {ex.Message}"), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void OpenStore(string link) => Owner.OpenLink(link);
}

// ---------------- Cleanup ----------------

public sealed partial class CleanupRow(Optimizer.Core.Cleanup.CleanupCategory category, string lang) : ObservableObject
{
    // What screen readers announce for this item in a list or combo box.
    public override string ToString() => Title;

    public Optimizer.Core.Cleanup.CleanupCategory Category { get; } = category;
    public string Title { get; } = Labels.Current.Get(lang, category.Id);
    public string? Warning { get; } = category.WarningKey is { } k ? Labels.Current.Get(lang, k) : null;

    [ObservableProperty] private bool _selected = category.DefaultSelected;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private long _bytes;

    /// <summary>The last size reading (kept for a language switch, which builds the rows again).</summary>
    public Optimizer.Core.Cleanup.CleanupScan? Scan { get; private set; }

    /// <summary>
    /// The exact folders (with the file pattern where only some files go, like thumbcache_*.db): the ones found on this
    /// PC once measured, every place the category looks before that or when none of them exists.
    /// </summary>
    [ObservableProperty]
    private string _foldersText = FoldersOf(category, category.Roots);

    public void SetScan(Optimizer.Core.Cleanup.CleanupScan scan)
    {
        Scan = scan;
        Bytes = scan.Bytes;
        // "157 MB in 1,566 files", as on the Storage page
        SizeText = scan.Bytes == 0 && scan.Files == 0 ? Loc.Instance["Cleanup_Nothing"]
            : scan.Files == 1 ? Loc.Instance.Format("Cleanup_SizeOne", CleanupViewModel.Size(scan.Bytes))
            : Loc.Instance.Format("Cleanup_Size", CleanupViewModel.Size(scan.Bytes), scan.Files);
        IsEmpty = scan.Bytes == 0 && scan.Files == 0;
        FoldersText = FoldersOf(Category, scan.ExistingRoots.Count > 0 ? scan.ExistingRoots : Category.Roots);
    }

    private static string FoldersOf(Optimizer.Core.Cleanup.CleanupCategory c, IEnumerable<string> roots) =>
        string.Join("\n", roots.Select(r => c.Pattern == "*" ? r : System.IO.Path.Combine(r, c.Pattern)));

    /// <summary>Nothing to clean in this category: its check box shows unticked and greyed out.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// What the check box shows: an empty category has nothing to delete, so it shows unticked (and cannot be ticked)
    /// instead of ticked but greyed out. The choice itself stays for when the category fills up again.
    /// </summary>
    public bool Checked
    {
        get => Selected && !IsEmpty;
        set => Selected = value;
    }

    partial void OnSelectedChanged(bool value) => OnPropertyChanged(nameof(Checked));

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(Checked));
}

public sealed partial class CleanupViewModel(IPageHost owner, AppServices services, IDialogs dialogs) : PageViewModel(owner)
{
    public ObservableCollection<CleanupRow> Rows { get; } = [];

    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _toolOutput = "";

    public static string Size(long bytes) => Optimizer.Core.Platform.ByteSize.Format(bytes);

    protected override async Task LoadAsync()
    {
        // The categories look up folders (and the Windows folder's size), so off the UI thread like the sizes.
        var profilePath = services.ProfilePath;
        var userSid = services.UserSid;
        var categories = await Task.Run(() => Optimizer.Core.Cleanup.CleanupEngine.Categories(profilePath, userSid));
        var rows = BuildRows(categories);
        // The folders are measured side by side; each row shows its size as soon as it is known.
        await Task.WhenAll(rows.Select(async row => row.SetScan(await Task.Run(() => Optimizer.Core.Cleanup.CleanupEngine.Scan(row.Category)))));
        UpdateTotal();
        UpdateEmptyText();
        SortBySize();
    }

    // A language switch builds the rows again with the sizes already measured.
    protected override Task RelabelAsync()
    {
        if (Rows.Count == 0 || Rows.Any(r => r.Scan is null)) return LoadAsync();
        var scans = Rows.Select(r => r.Scan!).ToList();
        var rows = BuildRows(Rows.Select(r => r.Category).ToList());
        for (var i = 0; i < rows.Count; i++) rows[i].SetScan(scans[i]);
        UpdateTotal();
        UpdateEmptyText();
        SortBySize();
        return Task.CompletedTask;
    }

    /// <summary>"Nothing to clean in: Windows Update downloads, Windows error reports": the empty categories in one line.</summary>
    [ObservableProperty] private string? _emptyText;

    /// <summary>Once every size is known: the biggest categories first (moved, not rebuilt, so ticks stay as they are).</summary>
    private void SortBySize()
    {
        var ordered = Rows.OrderByDescending(r => r.Bytes).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Rows.IndexOf(ordered[i]);
            if (at != i) Rows.Move(at, i);
        }
    }

    private void UpdateEmptyText()
    {
        var empty = Rows.Where(r => r.IsEmpty).Select(r => r.Title).ToList();
        EmptyText = empty.Count == 0 ? null : Loc.Instance.Format("Cleanup_EmptyList", string.Join(", ", empty));
    }

    /// <summary>New rows in the current language; a row the user ticked or cleared keeps that choice (also after cleaning).</summary>
    private List<CleanupRow> BuildRows(IReadOnlyList<Optimizer.Core.Cleanup.CleanupCategory> categories)
    {
        var lang = Lang;
        var selected = Rows.GroupBy(r => r.Category.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Selected, StringComparer.Ordinal);
        Rows.Clear();
        foreach (var c in categories)
            Rows.Add(new CleanupRow(c, lang) { Selected = selected.TryGetValue(c.Id, out var on) ? on : c.DefaultSelected });
        return Rows.ToList();
    }

    private void UpdateTotal() => TotalText = Loc.Instance.Format("Cleanup_Total", Size(Rows.Where(r => r.Selected).Sum(r => r.Bytes)));

    [RelayCommand]
    private void SelectionChanged() => UpdateTotal();

    [RelayCommand]
    private async Task CleanAsync()
    {
        var selected = Rows.Where(r => r.Selected && r.Bytes > 0).ToList();
        if (selected.Count == 0) return;
        var list = string.Join("\n", selected.Select(r => $"{r.Title}: {Size(r.Bytes)}"));
        if (!dialogs.Ask(Loc.Instance["Cleanup_ConfirmTitle"], Loc.Instance.Format("Cleanup_ConfirmText", list), Loc.Instance["Cleanup_Clean"])) return;
        long freed = 0;
        var skipped = 0;
        // Stop ends it after the category that runs.
        using var stop = new CancellationTokenSource();
        await RunWorkAsync("cleanup", Loc.Instance["Work_Cleanup"], async work =>
        {
            foreach (var (r, i) in selected.Select((r, i) => (r, i)))
            {
                if (stop.IsCancellationRequested) break;
                work.Step(i, selected.Count, r.Title);
                var result = await Task.Run(() => Optimizer.Core.Cleanup.CleanupEngine.Clean(r.Category));
                freed += result.FreedBytes;
                skipped += result.Skipped;
            }
            Owner.ShowResult(Loc.Instance.Format("Cleanup_Done", Size(freed), skipped), Wpf.Ui.Controls.InfoBarSeverity.Success);
        }, stop);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ComponentCleanupAsync()
    {
        if (!dialogs.Ask(Labels.Current.Get(Lang, "cleanup.componentStore"), Durations.WithServicingSentence(Loc.Instance["Cleanup_ComponentText"]), Loc.Instance["Cleanup_Clean"])) return;
        ToolOutput = Loc.Instance["Tools_Running"];
        if (!await RunWorkAsync("component store cleanup", async () =>
            {
                var (code, output) = await Task.Run(() => Optimizer.Core.Cleanup.CleanupEngine.ComponentStoreCleanup(services.Context.Processes));
                ToolOutput = code == 0 ? Loc.Instance["Tools_Done"] : Loc.Instance.Format("Result_Error", output.Trim().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? code.ToString());
            }, duration: Durations.Servicing))
            ToolOutput = "";
    }

    [RelayCommand]
    private async Task DeliveryOptimizationCleanupAsync()
    {
        // Like every other cleanup: what is deleted is said once before it happens.
        if (!dialogs.Ask(Loc.Instance["Cleanup_DoTitle"], Loc.Instance["Cleanup_DoHint"], Loc.Instance["Cleanup_Clean"])) return;
        await RunWorkAsync("delivery optimization cleanup", async () =>
        {
            var (code, output) = await Task.Run(() => Optimizer.Core.Cleanup.CleanupEngine.DeliveryOptimizationCleanup(services.Context.Processes));
            ToolOutput = code == 0 ? Loc.Instance["Tools_Done"] : Loc.Instance.Format("Result_Error", output.Trim());
        });
    }
}
