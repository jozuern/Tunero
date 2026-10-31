using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.App.Views;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Wpf.Ui.Appearance;

namespace Optimizer.App;

/// <summary>
/// The developer switches of the window (--screenshot and the switches that go with it, --perf, --tab-order, --report):
/// they only read and render, and write their output to a file the caller names (<see cref="OutputFile"/>).
/// </summary>
public partial class App
{
    // Developer aid (--perf): when each step of the start finished.
    private readonly List<(string Name, DateTime At)> _startMarks = [];

    private void Mark(string name) => _startMarks.Add((name, DateTime.Now));

    /// <summary>Developer aid (--screenshot and the switches that go with it): renders the window and dialogs to PNG files.</summary>
    private async Task SaveScreenshotsAsync(CliArgs args, MainViewModel vm, MainWindow window, AppServices services, string shot)
    {
        if (Enum.TryParse<Page>(args.Value("--page"), true, out var page)) vm.CurrentPage = page;
        // The install media page with --media-demo reads its sample catalog on opening: the picture shows it read.
        if (vm.CurrentPage == Page.InstallMedia) await vm.InstallMedia.EnsureLoadedAsync();
        // Developer aid (VM test): the page with this own ISO read, its editions and the apps of the chosen one (attached read-only).
        if (args.Value("--media-iso") is { } mediaIso)
        {
            vm.InstallMedia.ExpandOptions = true;
            vm.InstallMedia.UseDownload = false;
            vm.InstallMedia.IsoPath = mediaIso;
            await vm.InstallMedia.IsoLoading;
            await vm.InstallMedia.AppsLoading;
        }
        if (args.Value("--pane") is "closed") window.Nav.IsPaneOpen = false;
        // The tab of a page with tabs (Startup, Services & tasks, Remove apps, Install & update, Tools), counted from 0.
        if (int.TryParse(args.Value("--page-tab"), CultureInfo.InvariantCulture, out var pageTab))
        {
            if (vm.CurrentPage == Page.Startup) vm.Startup.SelectedTab = pageTab;
            else if (vm.CurrentPage == Page.Services) vm.ServicesTab = pageTab;
            else if (vm.CurrentPage == Page.Debloat) vm.DebloatTab = pageTab;
            else if (vm.CurrentPage == Page.Apps) vm.AppsTab = pageTab;
            else if (vm.CurrentPage == Page.Tools) vm.ToolsTab = pageTab;
        }
        if (double.TryParse(args.Value("--width"), CultureInfo.InvariantCulture, out var width)) window.Width = Math.Max(window.MinWidth, width);
        if (double.TryParse(args.Value("--height"), CultureInfo.InvariantCulture, out var height)) window.Height = Math.Max(window.MinHeight, height);
        if (args.Value("--select") is { } id)
            vm.SelectedItem = vm.Findings.Concat(vm.AdvisorItems).Append(vm.GameAccess).FirstOrDefault(i => i?.Finding.Id == id);
        if (args.Value("--preview-drift") is "on") vm.PreviewDrift();
        if (Version.TryParse(args.Value("--update-shot"), out var newer)) vm.PreviewUpdateFound(newer);
        // The setup screen, or (card) Settings' Install entry, which the Debug exe would not offer (not elevated, not signed).
        if (args.Value("--setup-shot") is { } setupShot)
        {
            vm.CanInstall = true;
            vm.SetupShown = setupShot != "card";
        }
        if (args.Value("--category") is { } cat) vm.OpenTweakGroup(cat);
        if (int.TryParse(args.Value("--impact"), CultureInfo.InvariantCulture, out var impact)) vm.TweakImpactMin = impact;
        // Developer aid: Select mode with these tweaks picked (comma-separated ids, or "all" as Select all shown); nothing runs.
        if (args.Value("--pick") is { } pick)
        {
            vm.TweakSelectMode = true;
            if (pick == "all") vm.PickAllTweaksCommand.Execute(null);
            else
                foreach (var row in vm.Tweaks.Where(t => pick.Split(',').Contains(t.Tweak.Id))) row.IsPicked = true;
        }
        // Developer aid: the progress banner as a batch shows it (third of twelve changes, with Stop); nothing runs. With
        // "slow": as SFC shows it, with a percentage, Stop and the note that it can take several minutes.
        if (args.Value("--work-demo") == "slow")
            vm.Runner.Work.Begin(Loc.Instance["Work_Sfc"], new CancellationTokenSource(), duration: Durations.Servicing).Report(42, null);
        else if (args.Has("--work-demo") && vm.Tweaks.FirstOrDefault() is { } demo)
            vm.Runner.Work.Begin(Loc.Instance["Work_Apply"], new CancellationTokenSource()).Step(2, 12, vm.Runner.Title(demo.Tweak));
        // Developer aid: the Storage page with a folder analyzed (read only), on the tab --storage-tab names (0 to 3).
        if (args.Value("--storage-scan") is { } storageFolder)
        {
            vm.CurrentPage = Page.Storage;
            await vm.Storage.EnsureLoadedAsync();
            await vm.Storage.AnalyzeFolderAsync(storageFolder);
            if (int.TryParse(args.Value("--storage-tab"), CultureInfo.InvariantCulture, out var storageTab)) vm.Storage.Tab = Math.Clamp(storageTab, 0, 3);
            // --storage-type mp4: opens that extension's group on the File types tab and selects it.
            if (args.Value("--storage-type") is { } storageType && vm.Storage.TypeGroups.FirstOrDefault(g => Optimizer.Core.Storage.FileTypes.GroupOf(storageType) == g.Group) is { } typeGroup)
            {
                typeGroup.IsExpanded = true;
                vm.Storage.SelectedType = typeGroup.Extensions.FirstOrDefault(t => t.Extension == storageType.TrimStart('.').ToLowerInvariant());
            }
            // The file lists add their rows once their path checks are made.
            await vm.Storage.RowsBuilt;
            // --storage-select a.bin,docs: selects entries by name on the open tab for the command bar (Folders: the
            // first match; the file lists: every match). Only selects; nothing is opened, copied or moved.
            if (args.Value("--storage-select") is { } storageSelect && vm.Storage.Tree is { } storageTree)
            {
                var names = storageSelect.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (vm.Storage.Tab == StorageViewModel.TabFolders)
                    vm.Storage.SelectedEntry = Enumerable.Range(1, storageTree.Count - 1).FirstOrDefault(i => !storageTree.IsRemoved(i) && names.Contains(storageTree.Name(i)), -1);
                else
                    foreach (var row in vm.Storage.TypeFiles.Concat(vm.Storage.LargestFiles).Concat(vm.Storage.Duplicates).Where(r => names.Contains(System.IO.Path.GetFileName(r.Path))))
                        row.Selected = true;
            }
        }
        await Task.Delay(1500); // pages that load their own data (startup, services, apps)
        // Install & update reads its drivers last, which can take longer than that on a busy PC.
        for (var waited = 0; vm.CurrentPage == Page.Apps && vm.Apps.IsLoading && waited < 20_000; waited += 250) await Task.Delay(250);
        if (args.Value("--preview-uninstall") is "on")
        {
            vm.CurrentPage = Page.Debloat;
            vm.DebloatTab = 1;
            vm.Apps.PreviewUninstall();
        }
        // Developer aid: live sensor readings on the Health page (read only); "all" also opens every full list.
        if (args.Value("--sensors") is "on" or "all")
        {
            // --activity-demo: fixed programs in the program lists and one demo drive card (reads and ends nothing).
            if (args.Has("--activity-demo")) vm.Health.UseActivityDemo();
            vm.Health.SensorsOn = true;
            for (var waited = 0; vm.Health.SensorGroups.Count == 0 && waited < 15_000; waited += 250) await Task.Delay(250);
            await Task.Delay(2500); // the next read: some sensors report only from the second one
            if (args.Value("--sensors") is "all")
                foreach (var group in vm.Health.SensorGroups) group.IsExpanded = true;
            // --select-program Steam: that program's details open, as after a click on its row.
            if (args.Value("--select-program") is { } program)
            {
                vm.Health.SelectProgram(program);
                await Task.Delay(800);
            }
        }
        if (args.Value("--select") is { } tid && vm.Tweaks.FirstOrDefault(t => t.Tweak.Id == tid) is { } tweak)
        {
            vm.SelectedItem = tweak;
            // Its card in view: open the group it is in.
            if (vm.TweakRows.OfType<TweakGroupRow>().FirstOrDefault(g => g.Items.Contains(tweak)) is { } group) vm.OpenTweakGroup(group.Key);
        }
        // Developer aid: type into the search box (Ctrl+F) and show the results.
        if (args.Value("--search") is { } query)
        {
            vm.GlobalSearch = query;
            await Task.Delay(1500);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // The results are a popup, outside the window's picture: saved next to it.
            if (window.SearchPopup.Child is FrameworkElement results)
                SaveVisual(results, window, System.IO.Path.ChangeExtension(shot, null) + "-search.png");
            // Closed again: an open popup keeps the mouse captured, and on a real desktop Windows moves it onto the screen
            vm.SearchResultsOpen = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        // The inspector document is built in the background: wait for it before the picture.
        if (vm.SelectedItem is { } selected) await selected.MarkdownAsync();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // Developer aid: switch the theme while the page is open, as Settings does (checks text that keeps old colors).
        if (args.Value("--switch-theme") is { } switchTo)
        {
            ApplyTheme(switchTo, save: false);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        // Developer aid: show the bottom of long pages (Settings > About), or scroll down by a number of pixels.
        if (args.Value("--scroll") is { } scroll && (scroll == "end" || double.TryParse(scroll, CultureInfo.InvariantCulture, out _)))
        {
            foreach (var sv in Descendants<System.Windows.Controls.ScrollViewer>(window))
                if (scroll == "end") sv.ScrollToEnd();
                else sv.ScrollToVerticalOffset(double.Parse(scroll, CultureInfo.InvariantCulture));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        await Task.Delay(400);
        SaveScreenshot(window, shot);
        if (args.Value("--tab-order") is { } tabFile) WriteTabOrder(window, tabFile);
        // Developer aid: the More options menu of the first row that has one (Startup), saved next to the picture (a popup).
        if (args.Has("--more-options")
            && Descendants<System.Windows.Controls.Button>(window).FirstOrDefault(b => b.ContextMenu is not null && b.IsVisible) is { ContextMenu: { } menu } more)
        {
            StartupPage.OpenMenu(more);
            await Task.Delay(600); // the menu's opening animation
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // The popup's root: the menu with its border and shadow, as large as it opened.
            DependencyObject top = menu;
            while (VisualTreeHelper.GetParent(top) is { } parent) top = parent;
            SaveVisual(top as FrameworkElement ?? menu, window, System.IO.Path.ChangeExtension(shot, null) + "-menu.png");
            menu.IsOpen = false;
        }

        // Developer aid: render the confirmation dialog of a tweak without applying anything.
        if (args.Value("--confirm") is { } confirmId && (services.Catalog.Get(confirmId) ?? FeatureTweak(confirmId)) is { } ct && args.Value("--confirm-shot") is { } confirmShot)
        {
            var request = ChangeRunner.ConfirmRequestFor(ct, services.Engine.Detect(ct, vm.Facts), vm.ExpertMode,
                vm.Runner.Title(ct), vm.Runner.Summary(ct), services.Engine.Preview(ct));
            var dlg = new ConfirmWindow(request) { Owner = window };
            KeepOffScreen(dlg);
            dlg.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(400);
            SaveScreenshot(dlg, confirmShot);
            if (args.Value("--tab-order") is { } confirmTabs) WriteTabOrder(dlg, System.IO.Path.ChangeExtension(confirmTabs, null) + "-confirm.txt");
            dlg.Close();
        }

        // Developer aid: with --media-demo review, the review before the install media build (nothing is built).
        if (args.Value("--media-demo") is { } demoShows && demoShows.Split(',', StringSplitOptions.TrimEntries).Contains("review")
            && args.Value("--confirm-shot") is { } reviewShot && vm.InstallMedia.ReviewRequest() is { } review)
        {
            var dlg = new ConfirmWindow(review) { Owner = window };
            KeepOffScreen(dlg);
            dlg.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(400);
            SaveScreenshot(dlg, reviewShot);
            dlg.Close();
        }

        // Developer aid: render the question dialog with two buttons (update), with three (restore point), the uninstall
        // question with and without active changes, and the Expert mode question.
        if (args.Value("--message-shot") is { } messageShot)
        {
            var version = AppInfo.Version.ToString(3);
            var dialogs = new[]
            {
                (messageShot, new MessageWindow(Loc.Instance.Format("Update_ConfirmTitle", version), Loc.Instance.Format("Update_ConfirmText", version),
                    Loc.Instance["Confirm_Cancel"], Loc.Instance["Update_Restart"])),
                (System.IO.Path.ChangeExtension(messageShot, null) + "-3.png", new MessageWindow(Loc.Instance["Rp_AskTitle"], Loc.Instance["Rp_AskText"],
                    Loc.Instance["Confirm_Cancel"], Loc.Instance["Rp_Continue"], Loc.Instance["Rp_Enable"])),
                (System.IO.Path.ChangeExtension(messageShot, null) + "-uninstall.png", Dialogs.UninstallDialog(3, DataPaths.Root)),
                (System.IO.Path.ChangeExtension(messageShot, null) + "-uninstall-0.png", Dialogs.UninstallDialog(0, DataPaths.Root)),
                (System.IO.Path.ChangeExtension(messageShot, null) + "-expert.png", Dialogs.ExpertDialog()),
            };
            foreach (var (file, dlg) in dialogs)
            {
                dlg.Owner = window;
                KeepOffScreen(dlg);
                dlg.Show();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(400);
                SaveScreenshot(dlg, file);
                dlg.Close();
            }
        }

        // Developer aid: render the Licenses window (Settings > About).
        if (args.Value("--licenses-shot") is { } licensesShot)
        {
            var lic = new LicensesWindow(_ => { }) { Owner = window };
            KeepOffScreen(lic);
            lic.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(400);
            SaveScreenshot(lic, licensesShot);
            lic.Close();
        }
    }

    /// <summary>Headless scan -> Markdown report (developer aid for checking detection on real hardware).</summary>
    private static async Task<int> WriteReportAsync(string path, string lang, AppServices services)
    {
        try
        {
            var catalog = CatalogData.Current;
            // The window's scan pipeline, waiting for the background CPU sample (no tweak detection follows here).
            var (profile, findings, _) = await new ScanService(catalog, services.Context.Registry, services.BcdValues).RunAsync();
            var sb = new StringBuilder();
            sb.AppendLine($"# Tunero scan report ({DateTime.Now:yyyy-MM-dd HH:mm})").AppendLine();
            sb.AppendLine($"Readiness score: **{ReadinessScore.Compute(findings)}/100**, Windows {profile.Os.DisplayVersion} build {profile.Os.BuildString}, elevated: {profile.Elevation?.IsElevated}").AppendLine();
            foreach (var f in findings)
            {
                var item = new FindingItemViewModel(f, lang);
                sb.AppendLine($"## [{f.Status}] impact {f.Impact?.ToString() ?? "n/a"}/5: {item.Title}").AppendLine();
                sb.AppendLine(DocStore.Get(f.Id, lang) is { } page ? DocStore.RenderFinding(page, f, Labels.Current).Replace("\n## ", "\n### ") : "(no page)");
            }
            sb.AppendLine("## System info").AppendLine();
            sb.Append(HardwareReport.ToText(HardwareReport.Build(profile, Loc.Instance), bullet: "- "));
            using (var file = OutputFile(path))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                writer.Write(sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("app", "report failed", ex);
            return 1;
        }
    }

    private async Task WritePerfReportAsync(MainViewModel vm, string path)
    {
        var lines = new List<string>();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Mark("idle after the first scan");
        // The start, counted from the launch the user saw: the published exe starts itself once more for its protected
        // runtime folder, and the first process hands its start time to the second.
        DateTime processStart;
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) processStart = self.StartTime;
        var launch = Program.LaunchedAt?.ToLocalTime() ?? processStart;
        lines.Add(launch == processStart ? "start (ms after the process started)" : $"start (ms after the launch; this process started {(processStart - launch).TotalMilliseconds:0} ms after it)");
        foreach (var (name, at) in _startMarks) lines.Add($"{at:HH:mm:ss.fff} {(at - launch).TotalMilliseconds,6:0} ms  {name}");
        // What the scan spent its time on: the probe times and every tool it started, from the session log.
        lines.AddRange(["", "scan steps and tools (session log)"]);
        foreach (var entry in Log.Snapshot(Log.MaxEntries).Where(e => e.Source is "scan" or "command"))
        {
            var ms = entry.Data is { } data && System.Text.RegularExpressions.Regex.Match(data, @"""ms"":(\d+)") is { Success: true } m ? m.Groups[1].Value : "";
            lines.Add($"{entry.Time:HH:mm:ss.fff} {ms,6} ms  {entry.Source}: {(entry.Message.Length > 110 ? entry.Message[..110] : entry.Message)}");
        }
        lines.AddRange(["", "interactions (until the UI is idle)"]);

        async Task Measure(string name, Action action, Func<Task>? settled = null)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            action();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var idle = watch.ElapsedMilliseconds;
            if (settled is null)
            {
                lines.Add($"{idle,6} ms  {name}");
                return;
            }
            // Work that goes on in the background after the UI is idle again (pages that read their data again).
            await settled();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            lines.Add($"{idle,6} ms  {name} (done after {watch.ElapsedMilliseconds} ms)");
        }
        Task PagesLoaded() => Task.WhenAll(vm.Pages.Select(p => p.Loading));

        foreach (var page in Enum.GetValues<Page>()) await Measure($"open {page} (first time)", () => vm.CurrentPage = page);
        await Task.Delay(3000); // pages that load their own data finish in the background
        await PagesLoaded();
        foreach (var page in Enum.GetValues<Page>()) await Measure($"open {page} (again)", () => vm.CurrentPage = page);
        lines.Add($"rows: apps {vm.Apps.Apps.Count}, drivers {vm.Apps.Drivers.Count}, startup {vm.Startup.Rows.Count}, services {vm.ServicesPage.Rows.Count}, tasks {vm.ServicesPage.Tasks.Count}");
        vm.CurrentPage = Page.Tweaks;
        await Measure("Tweaks: rebuild after a scan", () => { vm.Rebuild(); vm.Network.Rebuild(); });
        await Measure("Tweaks: expand all groups", () => vm.OpenAllTweakGroupsCommand.Execute(null));
        await Measure("Tweaks: collapse all groups", () => vm.CloseAllTweakGroupsCommand.Execute(null));
        if (vm.TweakRows.OfType<TweakGroupRow>().MaxBy(g => g.Items.Count) is { } largest)
        {
            await Measure($"Tweaks: open group {largest.Key}", () => vm.ToggleTweakGroupCommand.Execute(largest));
            await Measure($"Tweaks: close group {largest.Key}", () => vm.ToggleTweakGroupCommand.Execute(largest));
        }
        await Measure("Tweaks: only recommended on", () => vm.OnlyRecommended = true);
        await Measure("Tweaks: only recommended off", () => vm.OnlyRecommended = false);
        var start = vm.SelectedProfile;
        foreach (var option in vm.ProfileOptions.Where(o => o != start).Take(2).Append(start).OfType<ProfileOption>())
            await Measure($"Tweaks: profile {option.Id}", () => vm.SetProfileForSession(option));
        var expert = vm.ExpertMode;
        await Measure($"Tweaks: Expert mode {(expert ? "off" : "on")}", () => vm.SetExpertModeForSession(!expert));
        await Measure($"Tweaks: Expert mode {(expert ? "on" : "off")}", () => vm.SetExpertModeForSession(expert));
        vm.CurrentPage = Page.Advisor;
        await Measure("Advisor: show passed on", () => vm.ShowPassed = true);
        await Measure("Advisor: show passed off", () => vm.ShowPassed = false);

        // Switches in Settings, on the Settings page as a user would; nothing is saved.
        vm.CurrentPage = Page.Settings;
        var language = Loc.Instance.Language;
        var other = language == "de" ? "en" : "de";
        await Measure($"Settings: language {other}", () => Loc.Instance.SetLanguage(other), PagesLoaded);
        await Measure($"Settings: language {language}", () => Loc.Instance.SetLanguage(language), PagesLoaded);
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        await Measure($"Settings: theme {(dark ? "Light" : "Dark")}", () => ApplyTheme(dark ? "Light" : "Dark", save: false));
        await Measure($"Settings: theme {(dark ? "Dark" : "Light")}", () => ApplyTheme(dark ? "Dark" : "Light", save: false));
        vm.CurrentPage = Page.Tweaks;
        await Measure($"Tweaks: theme {(dark ? "Light" : "Dark")}", () => ApplyTheme(dark ? "Light" : "Dark", save: false));
        await Measure($"Tweaks: theme {(dark ? "Dark" : "Light")}", () => ApplyTheme(dark ? "Dark" : "Light", save: false));
        using (var file = OutputFile(path))
        using (var writer = new StreamWriter(file))
            foreach (var line in lines) writer.WriteLine(line);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>
    /// Ends a developer run (--screenshot, --perf). Nobody can close its window below the screen, so a run that does not
    /// end after Shutdown (seen once: an elevated --search run on a real desktop kept running after both pictures) writes
    /// that to the log and ends the process after 10 seconds.
    /// </summary>
    private void EndDeveloperRun(int exitCode)
    {
        Shutdown(exitCode);
        new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));
            Log.Warn("app", "developer run still running 10 s after shutdown, ended", new { exitCode });
            Environment.Exit(exitCode);
        }) { IsBackground = true, Name = "developer run end" }.Start();
    }

    /// <summary>
    /// --confirm with the id of a Windows feature switch (Tools > Windows features), such as feature.on.tftp or
    /// capability.off.media.windowsmediaplayer: those tweaks are built when the switch is used, not read from the catalog.
    /// </summary>
    private static Optimizer.Core.Tweaks.TweakDefinition? FeatureTweak(string id) =>
        CatalogData.Current.Features.Features
            .SelectMany(f => new[] { true, false }.Select(on => f.Capability
                ? Optimizer.Core.Tools.OptionalCapabilityAction.Tweak(f, on) : Optimizer.Core.Tools.OptionalFeatureAction.Tweak(f, on)))
            .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Developer runs (--screenshot, --perf) render from the visual tree, not from the screen: the window opens below the
    /// primary monitor (so it keeps that monitor's DPI), without focus or taskbar button. Dialogs need it too: Windows
    /// moves a window centered on an owner off the screen back onto it.
    /// </summary>
    private static void KeepOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 100;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    /// <summary>
    /// Developer aid (--tab-order): goes through the window with Tab as a keyboard user does and writes every stop with
    /// the name and type a screen reader announces, so stops without a name and stops that do nothing show up without
    /// Narrator. The window stays inactive: WPF moves the logical focus, which is where Tab would go.
    /// </summary>
    private static void WriteTabOrder(Window window, string path)
    {
        var lines = new List<string>();
        var seen = new HashSet<DependencyObject>();
        var next = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next);
        window.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        // Links in the explanation are content elements, not UI elements: both kinds are stops.
        for (var i = 0; i < 400 && System.Windows.Input.FocusManager.GetFocusedElement(window) is DependencyObject element && seen.Add(element); i++)
        {
            var peer = element switch
            {
                UIElement ui => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(ui),
                ContentElement content => System.Windows.Automation.Peers.ContentElementAutomationPeer.CreatePeerForElement(content),
                _ => null,
            };
            var name = peer?.GetName() ?? "";
            var label = $"{i + 1,3}  {peer?.GetLocalizedControlType() ?? "?"}  \"{name}\"  {element.GetType().Name}"
                + (element is FrameworkElement { Name.Length: > 0 } fe ? $" #{fe.Name}" : "")
                + (name.Length == 0 ? "  NO NAME" : "");
            lines.Add(label);
            if (element is UIElement u) u.MoveFocus(next);
            else if (element is ContentElement c) c.MoveFocus(next);
        }
        using var writer = new StreamWriter(OutputFile(path));
        foreach (var line in lines) writer.WriteLine(line);
    }

    private static void SaveScreenshot(Window window, string path)
    {
        if (window.Content is FrameworkElement root) SaveVisual(root, window, path);
    }

    /// <summary>Renders one element (a window's content, or a popup that is not part of the window) to a PNG file.</summary>
    private static void SaveVisual(FrameworkElement root, Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var w = (int)(root.ActualWidth * dpi.DpiScaleX);
        var h = (int)(root.ActualHeight * dpi.DpiScaleY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black;
            dc.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            // The window's own area, 1:1: content drawn past the edge (after --width) would otherwise squeeze the picture.
            var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area }, null, area);
        }
        var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = OutputFile(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// A file a developer switch writes (report, screenshot, timings). Whoever starts the exe names the path, and the
    /// elevated app writes with administrator rights: then only a new file is created, an existing one is never
    /// overwritten.
    /// </summary>
    private static FileStream OutputFile(string path) =>
        new(path, DataPaths.ProcessIsElevated ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None);
}
