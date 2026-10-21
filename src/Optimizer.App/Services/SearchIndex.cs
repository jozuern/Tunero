using Optimizer.Core.Catalog;
using Optimizer.Core.Search;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Services;

public interface ISearchIndex
{
    /// <summary>Best matches first, in the current UI language. Safe to call from any thread.</summary>
    IReadOnlyList<SearchResult> Search(string query, int max = 30);
}

/// <summary>
/// The app's search (<see cref="SearchEngine"/>) with its content: the catalog, the tools and settings the pages offer,
/// and the lists the pages load at run time (<see cref="UpdateRuntime"/>). The fixed part is built for the UI language
/// on the first search and again after a language switch.
/// </summary>
public sealed class SearchIndex(TweakCatalog tweaks, CatalogData catalog) : ISearchIndex
{
    private readonly SearchEngine _engine = new();
    private readonly Lock _buildGate = new();
    private string? _language;

    public IReadOnlyList<SearchResult> Search(string query, int max = 30)
    {
        EnsureStatic(Loc.Instance.Language);
        return _engine.Search(query, max);
    }

    /// <summary>Replaces the items of one kind after a page loaded them (services, tasks, startup entries, installed apps).</summary>
    /// <param name="items">Page key, item id (what the page selects), title and one line of description.</param>
    public void UpdateRuntime(SearchKind kind, IEnumerable<(string Page, string ItemId, string Title, string Snippet)> items) =>
        _engine.UpdateRuntime(kind, items.Select(i => new SearchItem(kind, i.Page, i.ItemId, i.Title, i.Snippet, [i.ItemId])).ToList());

    private void EnsureStatic(string language)
    {
        if (Volatile.Read(ref _language) == language) return;
        lock (_buildGate)
        {
            if (_language == language) return;
            _engine.SetStatic(SearchEngine.CatalogItems(tweaks, catalog.NotIncluded, language).Concat(PageItems()).ToList());
            Volatile.Write(ref _language, language);
        }
    }

    /// <summary>
    /// Tools and settings, named with the page's own texts. Keywords hold words people search for that the title does
    /// not contain, in both languages. A text the resources no longer have is left out instead of showing its key.
    /// </summary>
    private static IEnumerable<SearchItem> PageItems()
    {
        (SearchKind Kind, string Page, string TitleKey, string? HintKey, string[] Keywords)[] items =
        [
            (SearchKind.Tool, "Storage", "Storage_PageTitle", "Storage_PageHint", ["disk space", "large files", "duplicates", "treemap", "windirstat", "wiztree", "speicherplatz", "grosse dateien", "duplikate"]),
            (SearchKind.Tool, "Tools", "Feat_Title", "Feat_Hint", ["optional features", "hyper-v", "wsl", "sandbox", ".net 3.5", "optionale features"]),
            (SearchKind.Tool, "Tools", "Prio_Title", "Prio_Hint", ["priority", "cpu priority", "process priority", "prioritaet", "process lasso"]),
            (SearchKind.Tool, "Tools", "Quick_Dns", "Quick_DnsHint", ["ipconfig flushdns", "dns cache"]),
            (SearchKind.Tool, "Tools", "Quick_Winsock", "Quick_WinsockHint", ["netsh winsock reset", "network repair", "netzwerk reparieren"]),
            (SearchKind.Tool, "Tools", "Quick_IpRenew", null, ["ipconfig renew", "ip address", "ip-adresse"]),
            (SearchKind.Tool, "Tools", "Quick_TcpIpReset", null, ["netsh int ip reset", "network reset"]),
            (SearchKind.Tool, "Tools", "Quick_Explorer", "Quick_ExplorerHint", ["restart explorer", "taskbar frozen", "explorer neu starten"]),
            (SearchKind.Tool, "Tools", "Quick_IconCache", "Quick_IconCacheHint", ["icon cache", "iconcache.db", "blank icons", "symbolcache", "leere symbole"]),
            (SearchKind.Tool, "Tools", "Quick_DefenderUpdate", "Quick_DefenderUpdateHint", ["defender update", "virus definitions", "signature update", "virendefinitionen"]),
            (SearchKind.Tool, "Tools", "Quick_WingetSourceReset", "Quick_WingetSourceResetHint", ["winget repair", "winget source reset", "winget broken", "winget reparieren"]),
            (SearchKind.Tool, "Tools", "Quick_AdvancedStartup", "Quick_AdvancedStartupHint", ["safe mode", "abgesicherter modus", "advanced startup", "erweiterter start", "ddu"]),
            (SearchKind.Tool, "Tools", "Repair_Title", "Repair_Hint", ["windows update broken", "update repair", "softwaredistribution", "catroot2"]),
            (SearchKind.Tool, "Tools", "Health_SystemFiles", "Health_SystemFilesHint", ["sfc", "scannow", "dism", "restorehealth", "systemdateien"]),
            (SearchKind.Tool, "Health", "Throttle_Title", "Throttle_Hint", ["throttling", "thermal", "temperature", "drosselung", "temperatur"]),
            (SearchKind.Tool, "Health", "Bench_Title", "Bench_Hint", ["fps", "frame time", "presentmon", "benchmark", "1% lows"]),
            (SearchKind.Tool, "Health", "Health_Disks", null, ["smart", "ssd health", "hdd", "festplatte", "laufwerk"]),
            (SearchKind.Tool, "Health", "Sensors_Live", "Sensors_Hint", ["temperature", "fan", "clock", "sensors", "temperatur", "lufter", "pawnio"]),
            (SearchKind.Tool, "Network", "Net_DnsServers", "Net_DnsHint", ["dns", "cloudflare", "1.1.1.1", "quad9", "google dns", "adguard"]),
            (SearchKind.Tool, "Network", "Net_Benchmark", "Net_BenchmarkHint", ["dns speed", "dns test", "namebench"]),
            (SearchKind.Tool, "Network", "Net_GameProfiles", "Net_GameProfilesHint", ["nvidia profile", "per game", "pro spiel"]),
            (SearchKind.Tool, "Cleanup", "Cleanup_Title", "Cleanup_Hint", ["temp files", "junk", "disk cleanup", "temporaere dateien", "datentrager bereinigen"]),
            (SearchKind.Tool, "Cleanup", "Cleanup_ComponentTitle", "Cleanup_ComponentHint", ["winsxs", "component store", "komponentenspeicher"]),
            (SearchKind.Tool, "Debloat", "Debloat_Title", "Debloat_Hint", ["bloatware", "preinstalled apps", "remove apps", "apps entfernen", "vorinstalliert"]),
            (SearchKind.Tool, "Debloat", "Debloat_OneDriveTitle", null, ["onedrive", "uninstall onedrive"]),
            (SearchKind.Tool, "Debloat", "Debloat_EdgeTitle", "Debloat_EdgeText", ["edge", "microsoft edge", "uninstall edge", "edge entfernen", "edge deinstallieren", "browser"]),
            (SearchKind.Tool, "Debloat", "Programs_Title", "Programs_Hint", ["uninstall program", "programme deinstallieren", "installed apps", "bulk uninstall", "bulk crap uninstaller", "quiet uninstall", "leftovers", "mehrere deinstallieren", "reste"]),
            (SearchKind.Tool, "Apps", "Apps_UpdateAll", null, ["winget upgrade", "update apps", "apps aktualisieren"]),
            (SearchKind.Tool, "Apps", "Apps_SearchTitle", "Apps_SearchHint", ["winget search", "find app", "install any app", "app suchen", "winget suche"]),
            (SearchKind.Tool, "Apps", "Drv_Title", "Drv_Hint", ["driver update", "treiber", "chipset", "graphics driver", "grafiktreiber"]),
            (SearchKind.Tool, "Apps", "Drv_CleanTitle", "Drv_CleanHint", ["ddu", "display driver uninstaller", "clean driver install"]),
            (SearchKind.Tool, "Startup", "Startup_Title", "Startup_Hint", ["autostart", "autoruns", "startup programs", "run key"]),
            (SearchKind.Tool, "Startup", "Startup_SaveSnapshot", null, ["compare startup", "snapshot", "what changed"]),
            (SearchKind.Tool, "Changes", "Changes_RestorePoints", null, ["restore point", "system restore", "wiederherstellungspunkt", "systemwiederherstellung"]),
            (SearchKind.Tool, "Changes", "Changes_Transfer", "Changes_TransferText", ["export", "import", "backup settings", "other pc", "anderer pc"]),
            (SearchKind.Tool, "Changes", "Changes_UndoAll", null, ["revert", "restore defaults", "undo everything", "zuruecksetzen"]),
            (SearchKind.Setting, "Settings", "Settings_Language", null, ["deutsch", "english", "sprache"]),
            (SearchKind.Setting, "Settings", "Settings_Theme", null, ["dark mode", "light mode", "dunkel", "hell", "design"]),
            (SearchKind.Setting, "Settings", "StartMenu_Setting", "StartMenu_Hint", ["start menu", "shortcut", "verknuepfung", "startmenue"]),
            (SearchKind.Setting, "Settings", "Settings_Expert", "Settings_ExpertHint", ["advanced", "expert", "boot", "experte"]),
            (SearchKind.Setting, "Settings", "Update_AtStart", "Update_Hint", ["update tunero", "update pcoptimizer", "new version", "neue version"]),
            (SearchKind.Setting, "Settings", "Settings_Report", "Settings_ReportHint", ["bug", "issue", "fehler melden", "log"]),
            (SearchKind.Setting, "Settings", "Uninstall_Setting", "Uninstall_Hint", ["uninstall", "remove", "deinstallieren", "entfernen"]),
            (SearchKind.Setting, "Settings", "About_Support", null, ["donate", "ko-fi", "support", "spenden", "unterstuetzen"]),
        ];
        foreach (var (kind, page, titleKey, hintKey, keywords) in items)
        {
            var title = Loc.Instance[titleKey];
            if (title == titleKey) continue;
            var hint = hintKey is null ? "" : Loc.Instance[hintKey];
            // The title key names the card, so the page can open the tab it is on.
            yield return new SearchItem(kind, page, titleKey, title, hint == hintKey ? "" : hint, keywords);
        }
    }
}
