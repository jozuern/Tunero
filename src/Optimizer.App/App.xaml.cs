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
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Optimizer.App;

public partial class App : Application
{
    private AppSettings _settings = new();

    // The signed-in user, whose Windows app mode counts. With a separate admin account (or Administrator protection)
    // this process's HKCU is the admin's, and WPF-UI's theme watcher would follow that account instead.
    private string? _sessionSid;
    private bool _preferenceHooked;

    // Error reports (CrashReport): the services to open the GitHub form with, and whether this session offered one.
    private AppServices? _services;
    private bool _errorReportOffered;

    // What ApplyTheme set last: a Windows setting change that alters neither does not load the theme again.
    private (bool Dark, Color Accent)? _appliedTheme;

    // Developer aid (--perf): when each step of the start finished.
    private readonly List<(string Name, DateTime At)> _startMarks = [];

    private void Mark(string name) => _startMarks.Add((name, DateTime.Now));

    /// <summary>
    /// Sign-out or shutdown during a change (a batch, an undo, a winget run): Windows is asked to wait, so the change
    /// does not stop halfway. Windows shows the app in its "waiting for apps" list, where the user can still go on.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            (MainWindow?.DataContext as MainViewModel)?.Health.CloseSensors();
        }
        catch (Exception ex)
        {
            Optimizer.Core.Logging.Log.Warn("app", $"sensors not closed at exit: {ex.Message}");
        }
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (ChangeGate.Instance.Busy)
        {
            e.Cancel = true;
            Optimizer.Core.Logging.Log.Warn("app", $"{e.ReasonSessionEnding} asked to wait: a change is running");
        }
        base.OnSessionEnding(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Mark("WPF started");
        base.OnStartup(e);
        // The language (and with it the number format) is set here, outside an async method: the culture is an async
        // local, so a change inside an async method is undone when that method first waits, and the UI thread then
        // keeps Windows' regional format. Everything a click starts later would show "52,6 GB" on an English page.
        _settings = AppSettings.Load();
        Loc.Instance.SetLanguage(new CliArgs(e.Args).Value("--lang") ?? _settings.Language ?? Loc.Instance.Language);
        // A card is a button in WPF-UI, so every card was a Tab stop. Most are layout (a title with a switch or button
        // that has its own stop) and read as "pane" or as the name of their button: cards are no stops unless they have
        // a command (SelectableCard, the Tweaks group headers set Focusable). Not as a style for the card type: that
        // replaces WPF-UI's own and loses its template.
        UIElement.FocusableProperty.OverrideMetadata(typeof(CardControl), new FrameworkPropertyMetadata(false));
        ScrollBarNames.Register();
        StartAsync(e);
    }

    private async void StartAsync(StartupEventArgs e)
    {
        var commandLineRun = new CliArgs(e.Args).IsCommandLineRun;
        DispatcherUnhandledException += (_, ex) =>
        {
            // Log the root cause too: XAML and reflection errors wrap the real exception.
            Log.Error("ui", "unhandled exception", ex.Exception);
            if (ex.Exception.GetBaseException() is { } root && !ReferenceEquals(root, ex.Exception)) Log.Error("ui", "root cause", root);
            ex.Handled = true;
            CrashReport.Save(CrashReport.Describe(ex.Exception, AppInfo.Text, BuildInfo.Read().BuildString, DateTimeOffset.Now));
            // Before the window is shown there is nothing to keep running: say why and exit instead of staying invisible.
            // A command line run has no one to click a dialog away: it only ends, with its own exit code.
            if (MainWindow is not { IsVisible: true })
            {
                if (!commandLineRun)
                    System.Windows.MessageBox.Show(ex.Exception.GetBaseException().Message, "Tunero", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                Shutdown(commandLineRun ? CommandLineApply.Error : 1);
            }
            else
            {
                // The app keeps running; say that something failed and where to report it instead of failing silently.
                (MainWindow.DataContext as MainViewModel)?.ShowResult(Loc.Instance["Error_Unexpected"], Wpf.Ui.Controls.InfoBarSeverity.Error);
                // Offer the report once per session, after the current input is handled (not from inside the failing call).
                Dispatcher.BeginInvoke(() => OfferErrorReport(lastSession: false), DispatcherPriority.ApplicationIdle);
            }
        };

        // Failures outside the dispatcher (background tasks nobody awaited, other threads) are logged, not lost.
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("ui", "unobserved task exception", ex.Exception);
            ex.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is not Exception error) return;
            Log.Error("app", "unhandled exception", error);
            // The process ends now: the report is offered on the next start.
            CrashReport.Save(CrashReport.Describe(error, AppInfo.Text, BuildInfo.Read().BuildString, DateTimeOffset.Now));
        };

        var args = new CliArgs(e.Args);
        Log.Info("app", "start", new { version = AppInfo.Text, args = e.Args });

        // OS gate: block below 24H2 (26100) and non-x64 before scanning anything; 24H2 itself starts with a note.
        var os = BuildInfo.Read();
        var gate = OsGate.Evaluate(os.Build, os.NativeArchitecture);
        if (gate is OsGateResult.BlockedTooOld or OsGateResult.BlockedArchitecture)
        {
            var text = gate == OsGateResult.BlockedTooOld ? Loc.Instance.Format("Gate_TooOld", os.BuildString) : Loc.Instance["Gate_Arch"];
            Log.Warn("app", "blocked by OS gate", new { os.Build, os.NativeArchitecture });
            if (args.Value("--report") is null && !args.IsCommandLineRun) System.Windows.MessageBox.Show(text, Loc.Instance["Gate_Title"], System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Shutdown(args.IsCommandLineRun ? CommandLineApply.NotSupported : 2);
            return;
        }

        // Developer aid (--storage-compare C: --storage-out file.txt [--storage-reader FileTable|Folders]): reads the drive
        // from the file table and folder by folder, read only, writes the totals and times and exits. Needs nothing else
        // of the app.
        if (args.Value("--storage-compare") is { } compareDrive)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var root = compareDrive.TrimEnd('\\') + "\\";
                // --storage-reader FileTable or Folders: that reader only, with its memory (one reader per process).
                Optimizer.Core.Storage.ScanReader? only = Enum.TryParse<Optimizer.Core.Storage.ScanReader>(args.Value("--storage-reader"), true, out var reader) ? reader : null;
                var text = await Optimizer.Core.Storage.ReaderComparison.CompareAsync(root, DataPaths.ProcessIsElevated, only, CancellationToken.None);
                using (var file = OutputFile(args.Value("--storage-out") ?? "storage-compare.txt"))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                    writer.Write($"Elevated: {DataPaths.ProcessIsElevated}\n{text}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Log.Error("app", "storage comparison failed", ex);
                Shutdown(1);
            }
            return;
        }

        // After a self-update: remove the previous exe and the downloaded file.
        if (DataPaths.ProcessIsElevated && Environment.ProcessPath is { } runningExe) Updater.CleanUp(runningExe, DataPaths.Updates);

        var services = new AppServices();
        _services = services;
        Mark("services ready");
        _sessionSid = services.UserSid;

        if (args.Value("--report") is { } reportPath)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await WriteReportAsync(reportPath, Loc.Instance.Language, services));
            return;
        }

        // Runs without the window (CommandLineVerbs): the first sign-in of Tunero's install media, changes (CommandLineApply)
        // and the developer switch --build-media (CommandLineMedia). The exit code ends the process.
        if (CommandLineVerbs.Find(args) is { } verb)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await CommandLineVerbs.RunAsync(verb, args, services, _settings));
            return;
        }

        // Developer aid for screenshots: start with this profile for the session (never saved).
        var vm = new MainViewModel(_settings, services, new Dialogs(services.SessionDocumentsFolder)) { ProfileOverride = args.Value("--profile") };
        // Without the confirmation: only for runs without admin rights (Debug), not for whoever starts the elevated exe.
        if (args.Value("--expert") is "on" && !DataPaths.ProcessIsElevated) vm.EnableExpertForSession();
        // Developer aid for screenshots: the simple or the technical view of the lists for this run (never saved).
        if (args.Value("--technical") is "on" or "off") vm.ShowTechnicalForSession(args.Value("--technical") == "on");
        // Developer aid: the install media page with a sample catalog and a build that only shows its stages (no network,
        // nothing runs). --media-demo fail, done or expand (comma separated) shows a result or unfolds the cards; review also turns on an Expert option
        // and, with --confirm-shot, saves the review before the build.
        if (args.Has("--media-demo")) vm.InstallMedia.UseDemo(args.Value("--media-demo") is { } demo && !demo.StartsWith("--", StringComparison.Ordinal) ? demo : null);
        // Developer aid: Install and update with fixed package managers and a sample Chocolatey update list (nothing is read or
        // run); --pm-demo expand opens the folded card.
        if (args.Has("--pm-demo")) vm.Apps.UseManagerDemo(expand: args.Value("--pm-demo") is "expand");
        Mark("view model ready");
        // The scan starts before the window is built: its probes run on other threads while WPF creates the window and
        // the page (about a third of a second), and the results only reach the view model.
        var scan = vm.ScanAsync();
        var window = new MainWindow(vm);
        MainWindow = window;
        // A second start of this session finds this window through the single-instance record, whatever the exe is called.
        window.SourceInitialized += (_, _) => Program.Instance?.ShowsWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        Mark("window built");
        window.ContentRendered += (_, _) => Mark("window drawn");
        ApplyTheme(args.Value("--theme") ?? _settings.Theme, save: false);
        if (args.Value("--screenshot") is not null || args.Value("--perf") is not null) KeepOffScreen(window);
        window.Show();
        // "System" theme: WPF-UI's watcher switches the brushes when Windows changes its mode; content built from brushes follows.
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshThemedContent);
        // Developer aid: render the window while the first scan still runs (the Overview says the scan is reading the PC).
        if (args.Value("--shot-scanning") is { } scanningShot)
        {
            await Task.Delay(1200);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveScreenshot(window, scanningShot);
        }
        await scan;
        Mark("first scan shown");
        var preload = vm.PreloadPagesAsync();
        preload.Forget("page preload");
        // Developer aid: time the interactions that felt slow (page switches, filters, profile, rebuild) until the UI is idle.
        if (args.Value("--perf") is { } perfFile)
        {
            // A failure must end the run: the dispatcher handler keeps a shown window open, and nobody would close it.
            try
            {
                await preload;
                Mark("pages loaded in the background");
                await WritePerfReportAsync(vm, perfFile);
                EndDeveloperRun(0);
            }
            catch (Exception ex)
            {
                Log.Error("app", "perf report failed", ex);
                EndDeveloperRun(1);
            }
            return;
        }

        // Opt-in release check (off by default); screenshots stay offline and reproducible.
        if (args.Value("--screenshot") is null)
        {
            // Over the first results. Windows Settings' Uninstall button starts --uninstall; the installed copy's first start
            // removes the downloaded exe; a downloaded copy offers the setup (Program Files, Start menu), or the Start menu
            // question as before.
            // The first sign-in after an install from Tunero's media (FirstSignInStart): no setup screen or Start menu question;
            // the profile's recommendations are applied and Changes shows them. Only once, and only when --first-sign-in left
            // its pending marker: any other start with the switch opens normally and applies nothing.
            IReadOnlyList<string> firstSignInApps = [];
            var firstSignIn = args.Value(FirstSignInStart.ApplySwitch) is { } pending && FirstSignInStart.TakePending(pending, out firstSignInApps) ? pending : null;
            if (args.Has(SingleInstance.Uninstall)) vm.UninstallCommand.Execute(null);
            else if (args.Value(InstallService.InstalledFrom) is { } oldExe) vm.FinishInstallAsync(oldExe, report: firstSignIn is null).Forget("finish install");
            else if (firstSignIn is null) vm.OfferSetupAsync(SingleInstance.Applies(e.Args)).Forget("setup");
            if (firstSignIn is not null) vm.FirstSignInApplyAsync(firstSignIn, firstSignInApps).Forget("first sign-in");
            OfferErrorReport(lastSession: true);
            vm.CheckForUpdatesAsync(atStart: true).Forget("update check");
            // What killed or crashed install media builds left (work folders at drive roots, mounted images): only the
            // session's one window does it, elevated, and never beside a build (the page waits for it before building).
            if (DataPaths.ProcessIsElevated && Program.HoldsInstanceLock && !args.Has("--media-demo")) vm.InstallMedia.CleanLeftoversAsync().Forget("media cleanup");
        }

        if (args.Value("--screenshot") is { } shot)
        {
            // A failure must end the run: the dispatcher handler keeps a shown window open, and nobody would close it.
            try
            {
                await SaveScreenshotsAsync(args, vm, window, services, shot);
                EndDeveloperRun(0);
            }
            catch (Exception ex)
            {
                Log.Error("app", "screenshot failed", ex);
                EndDeveloperRun(1);
            }
        }
    }

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

        // Developer aid: render the question dialog with two buttons (update) and with three (restore point).
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

    /// <summary>"System", "Dark" or "Light" as chosen in Settings.</summary>
    public string ThemeSetting => _settings.Theme;

    /// <summary>
    /// Applies the Windows 11 Fluent theme (WPF-UI) with Mica and the Windows accent color. "System" follows the
    /// Windows app mode, also when it changes while the app runs.
    /// </summary>
    public void ApplyTheme(string theme, bool save = true)
    {
        // Case-insensitive so "--theme light" works like "--lang de"; the saved value keeps the canonical spelling.
        var setting = new[] { "Light", "Dark" }.FirstOrDefault(t => t.Equals(theme, StringComparison.OrdinalIgnoreCase)) ?? "System";
        FollowHighContrast();
        if (SystemParameters.HighContrast && TryApplyHighContrast())
        {
            if (save)
            {
                _settings.Theme = setting;
                _settings.Save();
            }
            return;
        }
        var (dark, accent) = TargetTheme(setting);
        var appTheme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        foreach (var key in ContrastBrushKeys) Resources.Remove(key);   // set by TryApplyHighContrast
        // The theme dictionary builds some brushes (the "on" fill of switches) from the accent colors when it loads.
        // Set the accent for the new theme first, then load the theme, then set the accent again for the brushes the
        // accent manager owns. Loading the theme with the Windows accent would leave switches gray on a gray accent.
        ApplicationAccentColorManager.Apply(accent, appTheme);
        ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(accent, appTheme);
        ApplyTreemapColors(dark);
        _appliedTheme = (dark, accent);
        // "System" follows Windows through our own hook, not WPF-UI's watcher: the watcher loads the theme with the
        // Windows accent, and it reads this process's HKCU, which is the admin's with a separate admin account.
        if (setting == "System") FollowSessionUserTheme();
        // Only a choice in Settings is the user's preference: a --theme override must not end up in settings.json
        // the next time anything else saves it.
        if (save)
        {
            _settings.Theme = setting;
            _settings.Save();
        }
        // Status colors and the explanation document read theme brushes when they are built: refreshed through
        // ApplicationThemeManager.Changed (also raised when Windows switches the mode).
    }

    /// <summary>
    /// A Windows contrast theme wins over Light and Dark: its colors come from Windows (WPF-UI's HC themes), without Mica.
    /// False when the theme could not be loaded; the normal theme is used then.
    /// </summary>
    private bool TryApplyHighContrast()
    {
        try
        {
            ApplicationThemeManager.Apply(ApplicationTheme.HighContrast, WindowBackdropType.None, false);
            // The contrast themes keep the accent text brushes the accent manager set for Light or Dark: the
            // "Recommended" lines were dark blue on black (VM, Night sky). They take Windows' link color, as text in the
            // accent color does in Windows' own apps; leaving the contrast theme sets the accent again (ApplyTheme).
            foreach (var key in ContrastBrushKeys) Resources[key] = SystemColors.HotTrackBrush;
            // Contrast themes have no colors for categories: the set stepped for a dark or light surface, by the theme's window color.
            var window = SystemColors.WindowColor;
            ApplyTreemapColors(window.R + window.G + window.B < 384);
            _appliedTheme = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("app", "high contrast theme not applied", ex);
            return false;
        }
    }

    /// <summary>
    /// The treemap's group colors (App.xaml) for a dark or light surface. Replacing the resources updates every
    /// reference to them, so the map and its legend follow a theme switch without being built again.
    /// </summary>
    private void ApplyTreemapColors(bool dark)
    {
        foreach (var group in Enum.GetNames<Optimizer.Core.Storage.FileGroup>())
        {
            if (Resources[$"Treemap{group}{(dark ? "Dark" : "Light")}"] is not Color color) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Resources[$"Treemap{group}Brush"] = brush;
        }
    }

    private static readonly string[] ContrastBrushKeys = ["AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush"];

    private bool _contrastHooked;

    /// <summary>Turning a contrast theme on or off in Windows applies to the open window too.</summary>
    private void FollowHighContrast()
    {
        if (_contrastHooked) return;
        _contrastHooked = true;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
            Dispatcher.BeginInvoke(() => ApplyTheme(_settings.Theme, save: false));
        };
    }

    /// <summary>
    /// After a theme switch (in Settings or by Windows): the explanation document is rendered again. Status colors are
    /// resource references (<see cref="Views.ThemeBrush"/>) and follow by themselves, so no rows are built again.
    /// </summary>
    private void RefreshThemedContent()
    {
        if (MainWindow is not MainWindow main) return;
        main.RenderDetails();
    }

    /// <summary>
    /// Dark or light, and the accent color: the Windows accent, or the Fluent default blue when the Windows accent is
    /// gray (switches, buttons and progress would be colorless).
    /// </summary>
    private (bool Dark, Color Accent) TargetTheme(string setting)
    {
        var dark = setting == "Dark" || (setting == "System" && !SystemUsesLightTheme(_sessionSid));
        var accent = ApplicationAccentColorManager.GetColorizationColor();
        return (dark, Saturation(accent) < 0.2 ? Color.FromRgb(0x00, 0x78, 0xD4) : accent);
    }

    private static double Saturation(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        return max == min ? 0 : l > 0.5 ? (max - min) / (2 - max - min) : (max - min) / (max + min);
    }

    /// <summary>
    /// "System" theme: Windows broadcasts app mode and accent changes to every window in the session, so re-read the
    /// signed-in user's app mode and accent on each change, and load the theme again only when one of them changed.
    /// </summary>
    private void FollowSessionUserTheme()
    {
        if (_preferenceHooked) return;
        _preferenceHooked = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_settings.Theme == "System" && TargetTheme("System") != _appliedTheme) ApplyTheme("System", save: false);
            });
        };
    }

    /// <summary>Windows "app mode" (Settings -> Personalization -> Colors) of the signed-in user. Read-only.</summary>
    private static bool SystemUsesLightTheme(string? sessionSid)
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        try
        {
            using var key = sessionSid is not null
                ? Microsoft.Win32.Registry.Users.OpenSubKey($@"{sessionSid}\{path}") ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)
                : Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch (Exception)
        {
            return false;
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
        File.WriteAllLines(path, lines);
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

    /// <summary>
    /// Offers the saved error report (CrashReport) once: a question, and on yes the GitHub bug form filled in with it,
    /// opened in the browser as the signed-in user. Nothing is sent unless the user submits the form there.
    /// </summary>
    private void OfferErrorReport(bool lastSession)
    {
        if (_errorReportOffered || _services is not { } services || CrashReport.Take() is not { } report) return;
        _errorReportOffered = true;
        var dialogs = new Dialogs();
        if (!dialogs.Ask(Loc.Instance["Crash_Title"], Loc.Instance[lastSession ? "Crash_TextLastSession" : "Crash_Text"], Loc.Instance["Crash_Report"])) return;
        var url = CrashReport.IssueUrl(ReleaseCheck.Repository, AppInfo.Text, services.Os.BuildString, Loc.Instance["Crash_What"], report);
        Log.Info("crash", "error report opened", new { lastSession });
        if (DeElevatedLauncher.Open(url, null, services.Elevation) == DeElevatedLauncher.Path.Failed)
            (MainWindow?.DataContext as MainViewModel)?.ShowResult(Loc.Instance["Crash_OpenFailed"], Wpf.Ui.Controls.InfoBarSeverity.Warning);
    }

    /// <summary>The switches, read the same way here and in <see cref="Program.Main"/> (which decides how a refusal is shown).</summary>
    internal sealed class CliArgs(string[] args)
    {
        public string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>A verb of <see cref="CommandLineVerbs"/>: no window, the result is the exit code (and the --log file).</summary>
        public bool IsCommandLineRun => CommandLineVerbs.Find(this) is not null;
    }
}
