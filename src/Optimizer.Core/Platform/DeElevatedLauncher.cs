using System.Diagnostics;
using System.Runtime.InteropServices;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Platform;

/// <summary>
/// Starts user-facing processes (browser links, launchers) as the signed-in user, never elevated.
/// 1) Shell technique: ask the desktop's Explorer to ShellExecute (Raymond Chen). Works when Explorer runs as the
///    same user as this process.
/// 2) Fallback: a one-shot scheduled task for the session user with an interactive token (Administrator protection,
///    over-the-shoulder elevation), deleted after it started.
/// 3) If both fail, the caller shows the link for the user to open.
/// </summary>
public static class DeElevatedLauncher
{
    public enum Path { NotElevated, Shell, ScheduledTask, Failed }

    /// <summary>
    /// Links the app opens: web pages and Microsoft Store pages. Anything else handed to ShellExecute (a file path, a
    /// file: or other protocol link) could start a program, so it is refused.
    /// </summary>
    public static bool IsLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == "ms-windows-store");

    public static Path Open(string target, string? arguments, ElevationInfo? elevation)
    {
        if (elevation is { IsElevated: false })
        {
            Process.Start(new ProcessStartInfo(target, arguments ?? "") { UseShellExecute = true });
            return Path.NotElevated;
        }
        // Under Administrator protection or a separate admin account, Explorer belongs to another user: use the task.
        if (elevation is not { UserMismatch: true } && TryShell(target, arguments)) return Log(Path.Shell, target);
        if (elevation?.SessionUser is { } user && TryTask(target, arguments, user)) return Log(Path.ScheduledTask, target);
        return Log(Path.Failed, target);
    }

    /// <summary>
    /// File Explorer, as the signed-in user, with <paramref name="path"/> selected in its folder; a folder is opened
    /// instead when <paramref name="openFolder"/> says so. Nothing in it is started. Null, without starting anything, when
    /// the path is not a full path or no longer exists; the caller says so, as it does for <see cref="Path.Failed"/>.
    /// </summary>
    public static Path? ShowInExplorer(string path, ElevationInfo? elevation, bool openFolder = false)
    {
        var folder = Directory.Exists(path);
        if (ExplorerArguments(path, folder ? ItemKind.Folder : File.Exists(path) ? ItemKind.File : ItemKind.Missing, openFolder) is not { } arguments)
            return null;
        return Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), arguments, elevation);
    }

    public enum ItemKind { Missing, File, Folder }

    /// <summary>
    /// Explorer's command line for <see cref="ShowInExplorer"/>: "/select" with the quoted full path, or the quoted folder
    /// to open it. Null for a relative path (Explorer would read it against its own folder) or a missing item.
    /// </summary>
    public static string? ExplorerArguments(string path, ItemKind kind, bool openFolder)
    {
        if (kind == ItemKind.Missing || string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.Contains('"')) return null;
        return kind == ItemKind.Folder && openFolder ? $"\"{path}\"" : $"/select,\"{path}\"";
    }

    private static Path Log(Path path, string target)
    {
        Logging.Log.Info("launcher", $"open {target}", new { path = path.ToString() });
        return path;
    }

    // ---------------- shell technique ----------------

    private static bool TryShell(string target, string? arguments)
    {
        try
        {
            var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
            object loc = 0; // CSIDL_DESKTOP
            object empty = Type.Missing;
            var dispatch = shellWindows.FindWindowSW(ref loc, ref empty, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */);
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
            var iid = typeof(IShellBrowser).GUID;
            ((IServiceProvider)dispatch).QueryService(ref sid, ref iid, out var browserObj);
            var browser = (IShellBrowser)browserObj;
            browser.QueryActiveShellView(out var view);
            var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
            view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch, out var folderView);
            var app = (IShellDispatch2)((IShellFolderViewDual)folderView).Application;
            app.ShellExecute(target, arguments ?? "", "", "open", 1);
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("launcher", $"shell technique failed: {ex.Message}");
            return false;
        }
    }

    // ---------------- waiting for a program run as the user ----------------

    /// <summary>
    /// Runs <paramref name="exe"/> as the signed-in <paramref name="user"/>, not elevated, in that user's session, and waits
    /// for it: a one-time scheduled task as in <see cref="Open"/>, watched until it is no longer running. Returns the
    /// program's exit code (the task's last result), or null when it could not be started or ran longer than
    /// <paramref name="limit"/> (then it is ended). The task is deleted afterwards in any case.
    /// </summary>
    public static async Task<int?> RunAsUserAndWaitAsync(string exe, string arguments, string user, TimeSpan limit, CancellationToken ct)
    {
        var name = $"Tunero-open-{Guid.NewGuid():N}";
        lock (Pending) Pending.Add(name);
        try
        {
            var service = TaskSchedulerCom.Connect();
            var folder = service.GetFolder("\\");
            var def = service.NewTask(0);
            var principal = def.Principal;
            principal.UserId = user;
            principal.LogonType = TaskSchedulerCom.LogonInteractiveToken;
            principal.RunLevel = TaskSchedulerCom.RunLevelLua;
            var settings = def.Settings;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            // Task Scheduler ends the program itself when the watch below has given up.
            settings.ExecutionTimeLimit = $"PT{(int)Math.Ceiling(limit.TotalMinutes) + 1}M";
            var action = (TaskSchedulerCom.IExecAction)def.Actions.Create(TaskSchedulerCom.ActionExec);
            action.Path = exe;
            action.Arguments = arguments;
            var task = folder.RegisterTaskDefinition(name, def, TaskSchedulerCom.CreateOrUpdate, null, null, TaskSchedulerCom.LogonInteractiveToken, null);
            var runAt = DateTime.Now;
            task.Run(null);
            Logging.Log.Info("launcher", $"run as {user} and wait: {exe} {arguments}");
            var started = false;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < limit)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var state = task.State;
                if (state is TaskSchedulerCom.StateRunning or TaskSchedulerCom.StateQueued)
                {
                    started = true;
                    continue;
                }
                // Ready again after it ran (a quick program may end before the first look: it has a last run time then).
                if (state == TaskSchedulerCom.StateReady && (started || HasRunSince(task.LastRunTime, task.LastTaskResult, runAt))) return task.LastTaskResult;
                if (!started && clock.Elapsed > TimeSpan.FromSeconds(30))
                {
                    Logging.Log.Warn("launcher", $"{exe} did not start as {user} (task state {state})");
                    return null;
                }
            }
            Logging.Log.Warn("launcher", $"{exe} ran longer than {limit} as {user}: ended");
            task.Stop(0);
            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        {
            Logging.Log.Warn("launcher", $"run as {user} failed: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                TaskSchedulerCom.Connect().GetFolder("\\").DeleteTask(name, 0);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException)
            {
                Logging.Log.Warn("launcher", $"one-time task {name} not deleted: {ex.Message}");
            }
            lock (Pending) Pending.Remove(name);
        }
    }

    // SCHED_S_TASK_HAS_NOT_RUN: the last result of a task that never ran.
    private const int TaskHasNotRun = 0x00041303;

    /// <summary>
    /// Whether a task with this last run time (an OLE automation date in local time) and last result ran at or after
    /// <paramref name="since"/>. A task that never ran reports 1999-11-30 (not 0) and SCHED_S_TASK_HAS_NOT_RUN, so a
    /// still waiting one-time task is not taken for a finished one. Two seconds of tolerance for the clock.
    /// </summary>
    internal static bool HasRunSince(double lastRunTime, int lastTaskResult, DateTime since)
    {
        if (lastTaskResult == TaskHasNotRun) return false;
        DateTime lastRun;
        try
        {
            lastRun = DateTime.FromOADate(lastRunTime);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return lastRun >= since - TimeSpan.FromSeconds(2);
    }

    // ---------------- scheduled-task fallback ----------------

    private static bool TryTask(string target, string? arguments, string user)
    {
        string? name = $"Tunero-open-{Guid.NewGuid():N}";
        TaskSchedulerCom.ITaskFolder? folder = null;
        try
        {
            var service = TaskSchedulerCom.Connect();
            folder = service.GetFolder("\\");
            DeleteLeftovers(folder);
            var def = service.NewTask(0);
            var principal = def.Principal;
            principal.UserId = user;
            principal.LogonType = TaskSchedulerCom.LogonInteractiveToken; // runs in the user's session with the user's token
            principal.RunLevel = TaskSchedulerCom.RunLevelLua;             // not elevated
            var settings = def.Settings;
            settings.DisallowStartIfOnBatteries = false;
            var isUrl = target.Contains("://", StringComparison.Ordinal);
            // Task Scheduler ends a task's program when the time limit is reached: a link only starts the browser, but
            // an uninstaller or installer can run as long as the user needs (PT0S = no limit).
            settings.ExecutionTimeLimit = isUrl ? "PT1M" : "PT0S";
            var action = (TaskSchedulerCom.IExecAction)def.Actions.Create(TaskSchedulerCom.ActionExec);
            action.Path = isUrl ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") : target;
            // The link in its escaped form: a quote inside the raw text could otherwise end the argument early.
            action.Arguments = isUrl ? $"\"{(Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : target)}\"" : arguments ?? "";
            var task = folder.RegisterTaskDefinition(name, def, TaskSchedulerCom.CreateOrUpdate, null, null, TaskSchedulerCom.LogonInteractiveToken, null);
            task.Run(null);
            // The task is deleted once it has started; waiting for that happens off the UI thread.
            lock (Pending) Pending.Add(name);
            DeleteLater(name);
            name = null;
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("launcher", $"task fallback failed: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (name is not null) folder?.DeleteTask(name, 0);
            }
            catch (Exception)
            {
                // the task may not exist if registration failed
            }
        }
    }

    /// <summary>One-time tasks of this process that are still waiting for <see cref="DeleteLater"/>.</summary>
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deletes the one-time task after it had time to start, on a background thread with its own connection.</summary>
    private static void DeleteLater(string name) => Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            TaskSchedulerCom.Connect().GetFolder("\\").DeleteTask(name, 0);
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("launcher", $"one-time task {name} not deleted: {ex.Message}");
        }
        finally
        {
            lock (Pending) Pending.Remove(name);
        }
    });

    /// <summary>
    /// One-time tasks an earlier run left behind (the app ended within the 3 seconds before <see cref="DeleteLater"/>):
    /// they have no trigger and never run again, but stay in the Task Scheduler root until deleted.
    /// </summary>
    private static void DeleteLeftovers(TaskSchedulerCom.ITaskFolder folder)
    {
        try
        {
            var names = new List<string>();
            var tasks = folder.GetTasks(TaskSchedulerCom.EnumHidden);
            for (var i = 1; i <= tasks.Count; i++) names.Add(tasks.Item(i).Name);
            // "PCOptimizer-open-": the name before the app was renamed.
            foreach (var name in names.Where(n => n.StartsWith("Tunero-open-", StringComparison.OrdinalIgnoreCase) || n.StartsWith("PCOptimizer-open-", StringComparison.OrdinalIgnoreCase)))
            {
                lock (Pending)
                    if (Pending.Contains(name)) continue;
                folder.DeleteTask(name, 0);
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("launcher", $"leftover one-time tasks not removed: {ex.Message}");
        }
    }

    // ---------------- COM interfaces (vtable order matters) ----------------

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
        int RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object loc);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)] object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int swfwOptions);
    }

    /// <summary>The desktop view's automation object (shell32 type library); only its first member is used.</summary>
    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellFolderViewDual
    {
        object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }

    /// <summary>
    /// Shell.Application of the desktop's Explorer. IShellDispatch2 extends IShellDispatch, whose 23 members (slots 7 to
    /// 29) come first; they are never called here, so they are declared as placeholders.
    /// </summary>
    [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellDispatch2
    {
        void Slot07(); void Slot08(); void Slot09(); void Slot10(); void Slot11(); void Slot12(); void Slot13(); void Slot14();
        void Slot15(); void Slot16(); void Slot17(); void Slot18(); void Slot19(); void Slot20(); void Slot21(); void Slot22();
        void Slot23(); void Slot24(); void Slot25(); void Slot26(); void Slot27(); void Slot28(); void Slot29();
        int IsRestricted([MarshalAs(UnmanagedType.BStr)] string group, [MarshalAs(UnmanagedType.BStr)] string restriction);
        void ShellExecute([MarshalAs(UnmanagedType.BStr)] string file, object? arguments, object? directory, object? operation, object? show);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr widths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenu, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(int enable);
        void TranslateAcceleratorSB(IntPtr msg, short id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        void QueryActiveShellView(out IShellView view);
        void OnViewWindowActive(IShellView view);
        void SetToolbarItems(IntPtr buttons, uint count, uint flags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enterMode);
        void TranslateAccelerator(IntPtr msg);
        void EnableModeless(int enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IShellView previous, IntPtr folderSettings, IShellBrowser browser, IntPtr rect, out IntPtr hwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr folderSettings);
        void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        void GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }
}
