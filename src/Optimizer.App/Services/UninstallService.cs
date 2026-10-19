using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.Services;

/// <summary>
/// Settings > Uninstall: undoes the app's changes if the user wants (and stops when one fails), removes the Start menu
/// entry, starts the cleanup that deletes the data folder and the exe once the app has ended (a package manager's copy
/// is removed by that package manager) and ends the app (<see cref="SelfUninstall"/>).
/// </summary>
/// <param name="activeChanges">How many changes the Changes page lists (the question offers to undo them).</param>
/// <param name="undoAll">Undoes every change without a second question; false when not everything could be undone.</param>
/// <param name="startMenuFolder">The signed-in user's Start menu folder, or null when it is not known.</param>
/// <param name="installed">This copy is the one the setup installed (<see cref="AppInstall"/>): its entry and folder go with it.</param>
public sealed class UninstallService(InstallFacts install, IDialogs dialogs, IShell shell, UpdateService update, Func<int> activeChanges,
    Func<string, Task<bool>> undoAll, Func<string?> startMenuFolder, string dataRoot, Launch? launch = null, UninstallService.InstalledCopy? installed = null)
{
    /// <summary>The install folder in Program Files and how to remove the Installed apps entry.</summary>
    public sealed record InstalledCopy(string Folder, Action RemoveEntry);

    private readonly Launch _launch = launch ?? new Launch();

    /// <summary>Only the published exe removes itself (a build folder is not where the app lives), and only elevated: the data folder is admin-only.</summary>
    public bool CanUninstall => install.IsPublishedExe && install.Elevated;

    public async Task UninstallAsync()
    {
        if (!CanUninstall || install.ExePath is not { } exe) return;
        if (ChangeGate.Instance.Busy || ChangeGate.Instance.Scanning || ChangeGate.Instance.Updating)
        {
            shell.ShowResult(Loc.Instance["Uninstall_Busy"]);
            return;
        }
        // Before anything is undone: a package manager the app may not start leaves the command to type. choco.exe is
        // checked once, here: the same path goes into the window's command after the undo.
        var package = install.Package;
        var choco = package?.TrustedChocolatey();
        if (package is not null && (package.Manager == PackageManager.Chocolatey ? choco is null : !package.CanRunFromApp()))
        {
            shell.ShowResult(Loc.Instance.Format("Uninstall_PackageManual", package.DisplayName, package.UninstallCommand), InfoBarSeverity.Warning);
            return;
        }
        var choice = dialogs.ConfirmUninstall(await Task.Run(activeChanges), dataRoot);
        if (choice == UninstallChoice.Cancel) return;
        if (choice == UninstallChoice.UndoFirst && !await undoAll(Loc.Instance["Uninstall_UndoFailed"])) return;

        var processName = System.IO.Path.GetFileNameWithoutExtension(exe);
        int? packageManagerProcess = null;
        if (package is not null && !update.StartPackageManager(package, package.UninstallArguments(processName,
                Loc.Instance.Format("Uninstall_PackageRunning", package.DisplayName), Loc.Instance.Format("Uninstall_PackageFailed", package.DisplayName),
                choco), out packageManagerProcess))
        {
            shell.ShowResult(Loc.Instance.Format("Uninstall_PackageManual", package.DisplayName, package.UninstallCommand), InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var script = SelfUninstall.CleanupScript(processName, dataRoot, exe, removedByPackageManager: package is not null, packageManagerProcess,
                installFolder: installed?.Folder);
            _launch.Start(ProcessLauncher.Info(PowerShellScript.Exe, SelfUninstall.PowerShellArguments(script)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("uninstall", "cleanup not started", ex);
            // The package manager's window already waits for the app to end: it removes the app, and the data folder
            // with the backups stays (deleted by hand, or by the next copy's Uninstall).
            if (package is null)
            {
                shell.ShowResult(Loc.Instance.Format("Uninstall_Failed", ex.Message), InfoBarSeverity.Error);
                return;
            }
        }
        // The entries go only once the removal is under way: a failed start above keeps the app listed and in Start.
        // A Start menu entry that cannot be removed (a link put in its place) does not stop the uninstall.
        try
        {
            if (startMenuFolder() is { } folder) StartMenuShortcut.Remove(folder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warn("uninstall", $"Start menu entry not removed: {ex.Message}");
        }
        // The Installed apps entry goes before the app closes: Windows would list an app whose files the cleanup removes.
        if (installed is not null)
        {
            try
            {
                installed.RemoveEntry();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn("uninstall", $"Installed apps entry not removed: {ex.Message}");
            }
        }
        Log.Info("uninstall", "cleanup started, closing", new { package = package?.Manager, undone = choice == UninstallChoice.UndoFirst });
        shell.Shutdown();
    }
}
