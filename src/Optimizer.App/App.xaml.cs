using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.App.Views;
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

    // A command line verb (CommandLineVerbs) is running: it changes the PC without the window, so ChangeGate.Busy is not set.
    private bool _commandLineRunning;

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

    /// <summary>
    /// Sign-out or shutdown during a change (a batch, an undo, a winget run, or a command line verb such as --undo-all,
    /// --reapply or --build-media): Windows is asked to wait, so the change does not stop halfway. Without the cancel
    /// WPF ends the app at the query itself, even when another app then stops the shutdown. Windows shows the app in its
    /// "waiting for apps" list, where the user can still go on.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (ChangeGate.Instance.Busy || _commandLineRunning)
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
            int exitCode;
            _commandLineRunning = true;
            try
            {
                exitCode = await CommandLineVerbs.RunAsync(verb, args, services, _settings);
            }
            finally
            {
                _commandLineRunning = false;
            }
            Shutdown(exitCode);
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
