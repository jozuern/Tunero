using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Search;

namespace Optimizer.App.ViewModels;

/// <summary>One line in the search results: what it is, a line about it, and where it is.</summary>
/// <param name="LiveSnippet">For a check: its summary for this PC's result (the index holds the text for a problem).</param>
public sealed record SearchResultItem(SearchResult Result, string Where, string? LiveSnippet = null)
{
    /// <summary>What kind of thing it is, at a glance: a tweak, a check, a program, a service, a tool.</summary>
    public Wpf.Ui.Controls.SymbolRegular Icon => Result.Kind switch
    {
        SearchKind.Tweak => Wpf.Ui.Controls.SymbolRegular.Wrench20,
        SearchKind.Finding => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle20,
        SearchKind.DebloatApp or SearchKind.InstalledProgram or SearchKind.AppInstall => Wpf.Ui.Controls.SymbolRegular.Apps20,
        SearchKind.Service or SearchKind.ScheduledTask => Wpf.Ui.Controls.SymbolRegular.Server20,
        SearchKind.StartupEntry => Wpf.Ui.Controls.SymbolRegular.Rocket20,
        SearchKind.Tool => Wpf.Ui.Controls.SymbolRegular.Toolbox20,
        SearchKind.Setting => Wpf.Ui.Controls.SymbolRegular.Settings20,
        SearchKind.NotIncluded => Wpf.Ui.Controls.SymbolRegular.Info20,
        _ => Wpf.Ui.Controls.SymbolRegular.Search20,
    };

    public string Title => Result.Title;
    public string Snippet => LiveSnippet ?? Result.Snippet;

    // What screen readers announce for this item in a list.
    public override string ToString() => $"{Title}, {Where}";
}

/// <summary>
/// Search across the app (Ctrl+F): tweaks, findings, the lists the pages loaded, tools and settings, and popular tweaks
/// the app leaves out with the reason. Opening a result goes to its page and shows the item there.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The tab each page with tabs shows (bound to the page's tab control), so a search result can pick one.</summary>
    [ObservableProperty] private int _servicesTab;
    [ObservableProperty] private int _debloatTab;
    [ObservableProperty] private int _appsTab;
    [ObservableProperty] private int _toolsTab;

    [ObservableProperty] private string _globalSearch = "";
    [ObservableProperty] private bool _searchResultsOpen;

    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    /// <summary>Shown when nothing matches, so an empty list does not look like a search that did not run.</summary>
    [ObservableProperty] private bool _searchEmpty;

    private int _searchGeneration;

    partial void OnGlobalSearchChanged(string value) => SearchAsync(value).Forget("search");

    private async Task SearchAsync(string query)
    {
        var generation = ++_searchGeneration;
        var q = query.Trim();
        if (q.Length < 2)
        {
            SearchResults.Clear();
            SearchResultsOpen = false;
            SearchEmpty = false;
            return;
        }
        // The first search builds the index for the UI language: off the UI thread.
        var results = await Task.Run(() => _services.Search.Search(q, 20));
        if (generation != _searchGeneration) return; // a newer query is on its way
        SearchResults.Clear();
        foreach (var r in results) SearchResults.Add(new SearchResultItem(r, WhereText(r), LiveSnippet(r)));
        SearchEmpty = SearchResults.Count == 0;
        SearchResultsOpen = true;
    }

    /// <summary>
    /// A check that the scan evaluated: its summary for the result on this PC ("All required features are on" for a passed
    /// check, not the problem text the index holds), or null to keep the index's line.
    /// </summary>
    private string? LiveSnippet(SearchResult r)
    {
        if (r.Kind != SearchKind.Finding || _profiledFindings.FirstOrDefault(f => f.Id == r.ItemId) is not { } finding) return null;
        return Optimizer.Core.Docs.DocStore.GetOrEnglish(finding.Id, Loc.Instance.Language) is { } page
               && Optimizer.Core.Docs.DocStore.Summary(page, finding) is { Length: > 0 } summary
            ? string.Join(' ', summary.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : null;
    }

    /// <summary>"Tweaks", "Services and tasks" or "Not included in Tunero": the page in its navigation name.</summary>
    private static string WhereText(SearchResult r) =>
        r.Kind == SearchKind.NotIncluded ? Loc.Instance["Search_NotIncluded"] : Loc.Instance[PageNameKey(r.Page)];

    private static string PageNameKey(string page) => page switch
    {
        "Overview" => "Nav_Dashboard",
        "Advisor" => "Nav_Advisor",
        "Network" => "Nav_Network",
        "Hardware" => "Nav_Hardware",
        _ => $"Nav_{page}",
    };

    /// <summary>Goes to the result's page and shows the item: selected in the details pane, or filtered in the page's list.</summary>
    [RelayCommand]
    private void OpenSearchResult(SearchResultItem? item)
    {
        if (item?.Result is not { } r) return;
        SearchResultsOpen = false;
        if (r.Kind == SearchKind.NotIncluded)
        {
            // Not in the app: the reason is the answer; the list of what is left out has the details.
            if (r.Url is { } url) OpenLinkCommand.Execute(url);
            else ShowResult(r.Snippet);
            return;
        }
        if (!Enum.TryParse<Page>(r.Page, out var page)) return;
        switch (r.Kind)
        {
            case SearchKind.Tweak when r.ItemId?.StartsWith("network.dns.", StringComparison.Ordinal) == true:
                // DNS servers are chosen on Graphics & DNS.
                CurrentPage = Page.Network;
                return;
            case SearchKind.Tweak when page == Page.Tweaks:
                // The search filter opens the groups and shows the tweak even outside the profile.
                TweakViewMode = TweakView.Applicable;
                TweakSearch = r.Title;
                CurrentPage = page;
                SelectedItem = Tweaks.FirstOrDefault(t => t.Tweak.Id == r.ItemId);
                return;
            case SearchKind.Finding:
                // An ignored result is listed under "Ignored results" on the Overview, not among the passed checks.
                if (r.ItemId is { } ignoredId && Findings.Concat(AdvisorItems).All(f => f.Finding.Id != ignoredId)
                    && IgnoredItems.FirstOrDefault(f => f.Finding.Id == ignoredId) is { } ignored)
                {
                    CurrentPage = Page.Overview;
                    ShowIgnored = true;
                    SelectedItem = ignored;
                    return;
                }
                CurrentPage = page;
                if (r.ItemId is { } id && Findings.Concat(AdvisorItems).All(f => f.Finding.Id != id)) ShowPassed = true;
                SelectedItem = Findings.Concat(AdvisorItems).Append(GameAccess).FirstOrDefault(f => f?.Finding.Id == r.ItemId);
                return;
            case SearchKind.Service:
                ServicesTab = 0;
                ServicesPage.ShowWindowsServices = true;
                ServicesPage.Search = r.ItemId ?? r.Title;
                break;
            case SearchKind.ScheduledTask:
                ServicesTab = 1;
                if (r.ItemId?.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) == true) ServicesPage.ShowMicrosoftTasks = true;
                ServicesPage.TaskSearch = r.Title;
                break;
            case SearchKind.StartupEntry:
                Startup.Reveal(r.Title);
                break;
            case SearchKind.InstalledProgram:
                DebloatTab = 1;
                Apps.ProgramSearch = r.Title;
                break;
            case SearchKind.DebloatApp:
                DebloatTab = 0;
                break;
            case SearchKind.AppInstall:
                AppsTab = 0;
                break;
            default:
                SelectTabFor(page, r.ItemId);
                break;
        }
        CurrentPage = page;
    }

    /// <summary>A tool or setting on a page with tabs: the tab that holds it (the item id is its title's string key).</summary>
    private void SelectTabFor(Page page, string? key)
    {
        key ??= "";
        switch (page)
        {
            case Page.Tools:
                ToolsTab = key.StartsWith("Feat_", StringComparison.Ordinal) ? 1
                    : key.StartsWith("Prio_", StringComparison.Ordinal) ? 2
                    : 0;
                break;
            case Page.Apps:
                AppsTab = key.StartsWith("Drv_", StringComparison.Ordinal) ? 1 : 0;
                break;
            case Page.Debloat:
                DebloatTab = key.StartsWith("Programs_", StringComparison.Ordinal) ? 1 : 0;
                break;
            case Page.Services:
                ServicesTab = key.StartsWith("Task_", StringComparison.Ordinal) ? 1 : 0;
                break;
        }
    }

    /// <summary>Graphics & DNS: the network repairs are on Tools, first tab.</summary>
    [RelayCommand]
    private void OpenToolsFixes()
    {
        ToolsTab = 0;
        CurrentPage = Page.Tools;
    }

    /// <summary>Startup with services or tasks chosen: their own page, on the tab for them.</summary>
    [RelayCommand]
    private void OpenServicesTab(int tab)
    {
        ServicesTab = tab;
        CurrentPage = Page.Services;
    }

    /// <summary>Graphics & DNS: the other network settings are tweaks, in the Network group of the Tweaks page.</summary>
    [RelayCommand]
    private void OpenTweaksNetwork()
    {
        CurrentPage = Page.Tweaks;
        OpenTweakGroup("network");
    }

    /// <summary>F5: reads the current page again (pages that load their own lists), else scans the PC again.</summary>
    [RelayCommand]
    private void Refresh()
    {
        var reload = Views.PageRegistry.Of(CurrentPage).Reload?.Invoke(this);
        if (reload is not null) reload.Execute(null);
        else if (ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }
}
