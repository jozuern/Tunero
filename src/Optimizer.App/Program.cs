using System.Diagnostics;
using System.IO;
using System.Resources;
using System.Runtime.InteropServices;
using Optimizer.Core.Backup;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;

namespace Optimizer.App;

/// <summary>
/// Entry point. Runs before WPF loads anything: the data folder is secured first (the log and settings live there),
/// native libraries are tied to System32 and the protected folder, and a single-file exe moves its extracted
/// libraries out of %TEMP% (see <see cref="SingleFileRuntime"/>).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Assembly.Location is empty only inside a single-file bundle.
#pragma warning disable IL3000
        var bundle = string.IsNullOrEmpty(typeof(Program).Assembly.Location);
#pragma warning restore IL3000
        // Command line runs (--apply, --apply-recommended, --export, --reapply, --undo-all) have no window: a refusal is an exit code,
        // never a dialog a script waits on.
        // The same rule as the start that follows (App.CliArgs): "--apply" without a file opens the window in both.
        var commandLineRun = new App.CliArgs(args).IsCommandLineRun;
        int Refuse(string text)
        {
            if (!commandLineRun) ShowError(text);
            return commandLineRun ? Services.CommandLineApply.StartRefused : 1;
        }
        // A data folder from before the rename gets the new name first, before anything reads or secures it.
        DataPaths.MigrateLegacy();
        var runtimeBase = SingleFileRuntime.ExtractBase(DataPaths.Root);
        if (bundle)
        {
            NativeLibraryGuard.Install(SingleFileRuntime.TrustedFolders(AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string, runtimeBase));
        }

        // A profiler or diagnostic port set in the user's environment (HKCU\Environment) was active before this line; an
        // elevated app with code from a user-writable place in it must not go on. Startup hooks are off (StartupHookSupport=false).
        if (DataPaths.ProcessIsElevated && ProcessHardening.DiagnosticsRequested(Environment.GetEnvironmentVariable) is { } variable)
        {
            return Refuse(string.Format(Text("Startup_ProfilerSet"), variable));
        }

        // Files the app creates must be owned by Administrators, or the data folder would distrust them (UAC off,
        // built-in Administrator, Administrator protection).
        if (DataPaths.ProcessIsElevated) DefaultOwner.SetAdministrators();
        if (DataPaths.ProcessIsElevated && !SecureFolder.PrepareRoot(DataPaths.Root))
        {
            return Refuse(string.Format(Text("Startup_DataFolderUnsafe"), DataPaths.Root));
        }
        Log.EnableFile();
        if (DataPaths.MovedFrom is { } oldFolder) Log.Info("app", "data folder moved to the new name", new { from = oldFolder, to = DataPaths.Root });
        // Tools started elevated get an admin-only TEMP instead of the user's (DISM, installers and uninstallers run
        // code from their TEMP).
        if (DataPaths.ProcessIsElevated) ProcessHardening.UseTempFolder(DataPaths.Temp, empty: !OtherCopyRuns());

        // The handoff from the first process is read once and then cleared: every program this process starts inherits
        // its environment, and a new Tunero (the one an update starts) would take itself for the restarted one and refuse.
        var restarted = Environment.GetEnvironmentVariable(SingleFileRuntime.RestartedVariable) == "1";
        LaunchedAt = long.TryParse(Environment.GetEnvironmentVariable(SingleFileRuntime.LaunchedVariable), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var ticks) && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        Environment.SetEnvironmentVariable(SingleFileRuntime.RestartedVariable, null);
        Environment.SetEnvironmentVariable(SingleFileRuntime.LaunchedVariable, null);
        var start = SingleFileRuntime.Decide(bundle, Environment.GetEnvironmentVariable(SingleFileRuntime.ExtractVariable), runtimeBase,
            restarted, DataPaths.ProcessIsElevated);
        if (start == RuntimeStart.Restart)
        {
            if (RunWithProtectedRuntime(args, runtimeBase) is { } exitCode) return exitCode;
            start = SingleFileRuntime.AfterFailedRestart(DataPaths.ProcessIsElevated);
        }
        if (start == RuntimeStart.Refuse)
        {
            Log.Error("app", "the native runtime folder is not the protected one; refusing to start elevated");
            return Refuse(string.Format(Text("Startup_RuntimeFolderUnsafe"), SingleFileRuntime.ExtractVariable, SingleFileRuntime.RestartedVariable));
        }

        // One window per session: a second plain start shows the open window and ends. After a self-update the new exe
        // waits for the old one to close.
        Mutex? instance = null;
        if (SingleInstance.Applies(args))
        {
            instance = SingleInstance.TryAcquire(SingleInstance.WaitsForOther(args) ? SingleInstance.UpdateWait : TimeSpan.Zero, out var heldByOther);
            if (heldByOther)
            {
                // Windows' Uninstall button while Tunero is open: that window's Settings > Uninstall does it.
                if (SingleInstance.IsUninstall(args)) MessageBoxW(IntPtr.Zero, Text("Uninstall_CloseFirst"), "Tunero", 0x40 /* MB_ICONINFORMATION */);
                if (SingleInstance.ShowOther())
                {
                    Log.Info("app", "already open in this session: showed that window");
                    return 0;
                }
                Log.Info("app", "the single-instance lock is taken but no other copy runs in this session; starting without it");
            }
        }
        try
        {
            // The embedded catalogs take about 200 ms to read (JSON, and the serializer's first use): read beside WPF's
            // start, which loads the theme dictionaries meanwhile, instead of after it on the UI thread. A caller that
            // comes first waits for the same read; an error reaches it as before (the lazy values keep it).
            Task.Run(Preload);
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            instance?.ReleaseMutex();
            instance?.Dispose();
        }
    }

    /// <summary>The first process's start (UTC) when this one is its restart for the protected runtime folder; for --perf.</summary>
    public static DateTime? LaunchedAt { get; private set; }

    private static void Preload()
    {
        try
        {
            _ = Optimizer.Core.Tweaks.TweakCatalog.Current;
            _ = Optimizer.Core.Catalog.CatalogData.Current;
            _ = Optimizer.Core.Docs.Labels.Current;
        }
        catch (Exception)
        {
            // Reported where the app uses the catalog.
        }
    }

    /// <summary>
    /// Starts this exe again with the extraction folder inside the data folder and waits for it (developer switches
    /// such as --report wait for the exit code). Null when that is not possible.
    /// </summary>
    private static int? RunWithProtectedRuntime(string[] args, string runtimeBase)
    {
        if (Environment.ProcessPath is not { } exe) return null;
        try
        {
            Directory.CreateDirectory(runtimeBase);
            // Hold the exe without write or delete sharing while the new process starts, so it cannot be swapped.
            using var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            // The restarted process must not inherit startup hooks, profilers or runtime switches from the user's environment.
            ProcessHardening.RemoveCodeLoadingVariables(start.Environment, SingleFileRuntime.ExtractVariable);
            start.Environment[SingleFileRuntime.ExtractVariable] = runtimeBase;
            start.Environment[SingleFileRuntime.RestartedVariable] = "1";
            using (var self = Process.GetCurrentProcess())
                start.Environment[SingleFileRuntime.LaunchedVariable] = self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.Start(start);
            if (child is null) return null;
            hold.Dispose();
            SingleInstance.PassForeground(child.Id);
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("app", "restart with the protected runtime folder failed", ex);
            return null;
        }
    }

    /// <summary>Another process of this exe's name runs (a second window, a command line run, another account's copy).</summary>
    private static bool OtherCopyRuns()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(self.ProcessName);
            var count = others.Length;
            foreach (var p in others) p.Dispose();
            return count > 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static string Text(string key)
    {
        var resources = new ResourceManager("Optimizer.App.Resources.Strings", typeof(Program).Assembly);
        var german = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        return resources.GetString(key, new System.Globalization.CultureInfo(german ? "de" : "en")) ?? key;
    }

    private static void ShowError(string text) => MessageBoxW(IntPtr.Zero, text, "Tunero", 0x10 /* MB_ICONERROR */);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
