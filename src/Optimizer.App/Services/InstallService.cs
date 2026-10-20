using System.IO;
using Optimizer.Core.Logging;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.Services;

/// <summary>
/// The first start's setup (<see cref="AppInstall"/>): Install copies the exe into Program Files, adds the Start menu
/// entry for the copy, starts it with <see cref="InstalledFrom"/> and ends this one; Run from here keeps the exe where it
/// is. A step that fails undoes the install, and the app keeps running from here.
/// </summary>
/// <param name="save">Saves <paramref name="settings"/> (tests pass a counter: nothing is written).</param>
public sealed class InstallService(string runningExe, AppSettings settings, Action save, IShell shell, InstallService.Steps steps, Launch? launch = null)
{
    /// <summary>Passed to the installed copy with the downloaded exe's path: it waits for this one to close, then deletes it.</summary>
    public const string InstalledFrom = "--installed-from";

    /// <summary>The steps that change the PC; the app passes the real ones, tests record them.</summary>
    /// <param name="Install">Copies, checks and registers (<see cref="AppInstall.Install"/>).</param>
    /// <param name="Undo">Removes an install again (<see cref="AppInstall.Undo"/>).</param>
    /// <param name="AddStartMenu">Writes the Start menu entry for an exe.</param>
    public sealed record Steps(Func<InstallResult> Install, Action<string> Undo, Action<string> AddStartMenu)
    {
        /// <summary>
        /// The real steps for <paramref name="runningExe"/>: into <see cref="AppInstall.DefaultFolder"/>, checked there
        /// (Tunero's signature, the version, folders only administrators can change), with the entry under HKLM, and the
        /// Start menu entry in <paramref name="startMenuFolder"/>.
        /// </summary>
        public static Steps ForThisPc(string runningExe, Func<string?> startMenuFolder)
        {
            var machine = new SystemRegistryRoots(null);
            return new Steps(
                () => AppInstall.Install(new InstallRequest(runningExe, AppInstall.DefaultFolder, AppInfo.Text, AppInstall.IsTrustedFile,
                    path => System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion, TrustedPath.IsAdminOnlyWritableFolder,
                    TrustedPath.IsAdminOnlyBelowSharedFolders), machine),
                exe => AppInstall.Undo(exe, machine),
                exe => StartMenuShortcut.Create(startMenuFolder() ?? throw new DirectoryNotFoundException("no Start menu folder"), exe, Loc.Instance["StartMenu_Description"]));
        }
    }

    /// <summary>
    /// The installed copy's first start: deletes the exe it was installed from (<see cref="AppInstall.DeleteOldCopy"/>), and
    /// when that was the copy Windows Setup put into %WINDIR%\Setup\Tunero (<see cref="FirstSignInStart"/>), that folder
    /// with its file too. True when the old exe is gone.
    /// </summary>
    public static bool RemoveOldCopy(string oldExe, string installedExe) => RemoveOldCopy(oldExe, installedExe, FirstSignIn.Folder);

    /// <param name="setupFolder">The setup folder (tests pass a temp stand-in).</param>
    internal static bool RemoveOldCopy(string oldExe, string installedExe, string setupFolder)
    {
        var removed = AppInstall.DeleteOldCopy(oldExe, installedExe);
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(oldExe)), Path.GetFullPath(setupFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            RemoveSetupFolder(setupFolder);
        return removed;
    }

    /// <summary>
    /// Deletes the setup folder's files one by one through <see cref="SafeDelete"/> (never following a link), then the
    /// empty folder. A subfolder or link in it stays, and so does the folder then; the log says why.
    /// </summary>
    private static void RemoveSetupFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            if (!SafeDelete.HasNoLinks(folder))
            {
                Log.Warn("install", $"{folder} is or lies under a link: not removed");
                return;
            }
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (entry is FileInfo && (entry.Attributes & FileAttributes.ReparsePoint) == 0) SafeDelete.DeleteFile(entry.FullName);
                else Log.Warn("install", $"{entry.FullName} is a folder or a link: left in place");
            }
            SafeDelete.DeleteEmptyDirectory(folder);
            Log.Info("install", "setup folder removed", new { folder });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("install", $"setup folder {folder} not removed: {ex.Message}");
        }
    }

    /// <summary>
    /// The start of a Tunero exe the setup put in place (or of this one again, <see cref="FirstSignInStart"/>): no shell, and
    /// none of the variables that load code into a process.
    /// </summary>
    internal static System.Diagnostics.ProcessStartInfo ExeStart(string exe, IEnumerable<string> arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        ProcessHardening.RemoveCodeLoadingVariables(start.Environment);
        return start;
    }

    private readonly Launch _launch = launch ?? new Launch();

    public void RunHere() => Remember(declined: true);

    /// <summary>
    /// True when the installed copy was started and this app is closing. The copy and its checks (about 70 MB, the
    /// signature) run off the UI thread; a running change, scan or update is waited for, since the app closes afterwards.
    /// While the install runs it holds the change gate: closing the window or signing out then would stop it between
    /// moving the earlier exe aside and putting the new one in place.
    /// </summary>
    public async Task<bool> InstallAsync(bool toProgramFiles, bool startMenu)
    {
        if (!toProgramFiles)
        {
            if (startMenu) AddStartMenu(runningExe);
            Remember(declined: true);
            return false;
        }
        if (!ChangeGate.Instance.CanChange)
        {
            shell.ShowResult(Loc.Instance["Setup_Busy"], InfoBarSeverity.Warning);
            return false;
        }
        bool started;
        ChangeGate.Instance.Busy = true;
        try
        {
            started = await InstallAndStartAsync(startMenu);
        }
        finally
        {
            ChangeGate.Instance.Busy = false;
        }
        if (!started) return false;
        Remember(declined: false);
        shell.Shutdown();
        return true;
    }

    private async Task<bool> InstallAndStartAsync(bool startMenu)
    {
        var result = await Task.Run(steps.Install);
        if (result.InstalledExe is not { } installed)
        {
            Log.Error("install", $"not installed: {result.FailedAt} {result.Error}");
            shell.ShowResult(Loc.Instance.Format("Setup_Failed", StepName(result.FailedAt), result.Error ?? ""), InfoBarSeverity.Warning);
            return false;
        }
        if (startMenu)
        {
            try
            {
                steps.AddStartMenu(installed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                Log.Error("install", "installed, but the Start menu entry failed; undone", ex);
                steps.Undo(installed);
                shell.ShowResult(Loc.Instance.Format("Setup_Failed", Loc.Instance["Setup_StepStartMenu"], ex.Message), InfoBarSeverity.Warning);
                return false;
            }
        }
        try
        {
            _launch.Start(ExeStart(installed, [InstalledFrom, runningExe]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("install", "installed, but the installed copy did not start; undone", ex);
            steps.Undo(installed);
            // The new entry started the copy that is gone now: the app keeps running from here, so the entry starts this exe.
            if (startMenu) AddStartMenu(runningExe);
            shell.ShowResult(Loc.Instance.Format("Setup_Failed", Loc.Instance["Setup_StepStart"], ex.Message), InfoBarSeverity.Warning);
            return false;
        }
        Log.Info("install", "installed, starting the installed copy", new { installed });
        return true;
    }

    private void AddStartMenu(string exe)
    {
        try
        {
            steps.AddStartMenu(exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("startmenu", "entry not added", ex);
            shell.ShowResult(Loc.Instance.Format("StartMenu_Failed", ex.Message), InfoBarSeverity.Warning);
        }
    }

    private void Remember(bool declined)
    {
        settings.SetupDeclined = declined;
        settings.StartMenuAsked = true;
        save();
    }

    private static string StepName(InstallStep? step) => Loc.Instance[step switch
    {
        InstallStep.Folder => "Setup_StepFolder",
        InstallStep.Copy => "Setup_StepCopy",
        InstallStep.Check => "Setup_StepCheck",
        _ => "Setup_StepEntry",
    }];
}
