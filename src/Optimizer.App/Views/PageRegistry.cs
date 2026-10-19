using System.Windows.Input;
using Optimizer.App.ViewModels;

namespace Optimizer.App.Views;

/// <summary>
/// One page of the window: its view, how it reads its own data when it opens (pages that only show the scan read
/// nothing) and what F5 reads again (null: a new scan).
/// </summary>
public sealed record PageInfo(Page Id, Type View, Func<MainViewModel, Task>? Load = null, Func<MainViewModel, ICommand?>? Reload = null);

/// <summary>Every page in one list: the navigation, the window and the view model all go by it.</summary>
public static class PageRegistry
{
    public static readonly IReadOnlyList<PageInfo> All =
    [
        new(Page.Overview, typeof(OverviewPage)),
        new(Page.Tweaks, typeof(TweaksPage)),
        new(Page.Advisor, typeof(AdvisorPage)),
        new(Page.Network, typeof(NetworkPage), vm => vm.Network.EnsureLoadedAsync()),
        // Remove apps also lists the installed programs, which the Apps view model reads.
        new(Page.Debloat, typeof(DebloatPage), vm => Task.WhenAll(vm.Debloat.EnsureLoadedAsync(), vm.Apps.EnsureLoadedAsync()),
            vm => vm.DebloatTab == 1 ? vm.Apps.LoadProgramsCommand : vm.Debloat.ReloadCommand),
        new(Page.Cleanup, typeof(CleanupPage), vm => vm.Cleanup.EnsureLoadedAsync(), vm => vm.Cleanup.ReloadCommand),
        // Opening reads only the drive list; F5 reads it again (an analysis runs only on Analyze).
        new(Page.Storage, typeof(StoragePage), vm => vm.Storage.EnsureLoadedAsync(), vm => vm.Storage.ReloadCommand),
        new(Page.Startup, typeof(StartupPage), vm => vm.Startup.EnsureLoadedAsync(), vm => vm.Startup.ReloadCommand),
        new(Page.Services, typeof(ServicesPage), vm => vm.ServicesPage.EnsureLoadedAsync(), vm => vm.ServicesPage.ReloadCommand),
        new(Page.Apps, typeof(AppsPage), vm => vm.Apps.EnsureLoadedAsync(), vm => vm.Apps.ReloadCommand),
        new(Page.Tools, typeof(ToolsPage), vm => vm.Tools.EnsureLoadedAsync()),
        // Opening builds the lists (regions, keyboards, apps); the catalog is read only on "Check for the newest version".
        new(Page.InstallMedia, typeof(InstallMediaPage), vm => vm.InstallMedia.EnsureLoadedAsync()),
        new(Page.Health, typeof(HealthPage), vm => vm.Health.EnsureLoadedAsync()),
        new(Page.Changes, typeof(ChangesPage)),
        new(Page.Hardware, typeof(HardwarePage)),
        new(Page.Settings, typeof(SettingsPage)),
    ];

    public static PageInfo Of(Page page) => All.First(p => p.Id == page);

    /// <summary>The page shown by a view of this type; null for a type that is no page.</summary>
    public static PageInfo? ForView(Type view) => All.FirstOrDefault(p => p.View == view);
}
