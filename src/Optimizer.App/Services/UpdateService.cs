using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.Services;

/// <summary>
/// The update of this copy: the release check (opt-in at start, or "Check now") and Update now, which replaces the exe
/// after its signature and SHA-256 are checked, or hands the update to winget, Chocolatey or Scoop. It reaches the
/// window only through <see cref="IShell"/> and the dialogs; the view model shows its state.
/// </summary>
public sealed class UpdateService(InstallFacts install, IDialogs dialogs, IShell shell, Launch? launch = null, Func<Task<ReleaseCheckResult>>? check = null)
{
    private readonly Launch _launch = launch ?? new Launch();

    private readonly Func<Task<ReleaseCheckResult>> _check = check ?? (async () =>
    {
        using var releases = new ReleaseCheck();
        return await releases.CheckAsync(AppInfo.Version);
    });

    /// <summary>Raised when the release state or <see cref="Updating"/> changed, so the view model reads them again.</summary>
    public event EventHandler? StateChanged;

    public ReleaseCheckResult? Release { get; private set; }

    public bool Checking { get; private set; }

    public bool UpdateAvailable => Release?.Status == ReleaseCheckStatus.NewerAvailable;

    public PackageInstall? Package => install.Package;

    /// <summary>
    /// Self-update only replaces the published single-file exe running elevated, and not a package manager's copy. The
    /// exe's own name does not matter: browsers save a second download as "Tunero (1).exe".
    /// </summary>
    public bool CanSelfUpdate => UpdateAvailable && Release?.Assets is not null && UpdateSignature.IsConfigured && install.Elevated &&
        install.IsPublishedExe && install.Package is null;

    /// <summary>"Update now" is offered: the app replaces its exe, or hands the update to the package manager.</summary>
    public bool CanUpdateInApp => CanSelfUpdate || (UpdateAvailable && install.Package is not null);

    /// <summary>An update runs: no change, scan or second update may start (<see cref="ChangeGate"/>).</summary>
    public bool Updating
    {
        get => ChangeGate.Instance.Updating;
        private set
        {
            ChangeGate.Instance.Updating = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Asks GitHub for the newest release. A failure is a result too (shown as "could not check").</summary>
    public async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            Release = await _check();
        }
        catch (Exception ex)
        {
            Log.Error("update", "release check failed", ex);
            Release = new ReleaseCheckResult(ReleaseCheckStatus.Error);
        }
        finally
        {
            Checking = false;
        }
        Log.Info("update", "release check", new { Release.Status, latest = Release.Latest?.ToString(3) });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Developer aid (--update-shot): as if the check had found <paramref name="latest"/>.</summary>
    public void PreviewFound(Version latest)
    {
        Release = new ReleaseCheckResult(ReleaseCheckStatus.NewerAvailable, latest);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Busy => ChangeGate.Instance.Busy || ChangeGate.Instance.Scanning || ChangeGate.Instance.Updating;

    /// <summary>After confirmation: download the release exe, check its signature and SHA-256, replace this exe and restart.</summary>
    public async Task UpdateNowAsync()
    {
        if (install.Package is { } package && UpdateAvailable && Release?.Latest is { } packageVersion)
        {
            UpdateWithPackageManager(package, packageVersion.ToString(3));
            return;
        }
        if (!CanSelfUpdate || Release is not { Latest: { } latest } release || install.ExePath is not { } exe)
        {
            shell.OpenLink(ReleaseCheck.LatestReleaseUrl);
            return;
        }
        // Any change (tweaks and page operations such as uninstalls) or scan must be finished first.
        if (Busy)
        {
            shell.ShowResult(Loc.Instance["Update_Busy"]);
            return;
        }
        var version = latest.ToString(3);
        if (!dialogs.Ask(Loc.Instance.Format("Update_ConfirmTitle", version), Loc.Instance.Format("Update_ConfirmText", version), Loc.Instance["Update_Restart"])) return;
        Updating = true;
        try
        {
            using var updater = new Updater();
            shell.ShowResult(Loc.Instance.Format("Update_Downloading", version, 0));
            var progress = new Progress<int>(p => shell.ShowResult(Loc.Instance.Format("Update_Downloading", version, p)));
            var download = await updater.DownloadAsync(release, DataPaths.Updates, progress);
            Log.Info("update", "download", new { download.Outcome, version });
            if (download.Outcome != UpdateOutcome.Ready)
            {
                shell.ShowResult(Loc.Instance[download.Outcome switch
                {
                    UpdateOutcome.ChecksumMismatch => "Update_BadChecksum",
                    UpdateOutcome.SignatureInvalid => "Update_BadSignature",
                    _ => "Update_Failed",
                }], InfoBarSeverity.Warning);
                return;
            }
            // A change may have started while the download ran: never replace the exe in the middle of it.
            if (ChangeGate.Instance.Busy)
            {
                shell.ShowResult(Loc.Instance["Update_Busy"]);
                return;
            }
            try
            {
                Updater.Install(download.FilePath!, exe);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Error("update", "replacing the exe failed", ex);
                shell.ShowResult(Loc.Instance.Format("Update_CannotReplace", ex.Message), InfoBarSeverity.Warning);
                return;
            }
            Log.Info("update", "installed, restarting", new { version });
            bool started;
            try
            {
                started = Updater.StartVerified(exe, download.Sha256!, path =>
                {
                    // Like the start's own restart: no profiler or startup hook from the user's environment in the new process.
                    var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false };
                    // This copy still holds the single-instance lock until it has closed: the new one waits for it.
                    start.ArgumentList.Add(SingleInstance.AfterUpdate);
                    ProcessHardening.RemoveCodeLoadingVariables(start.Environment);
                    _launch.Start(start);
                });
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // The new exe is in place but did not start: antivirus took it away or blocked it (an unsigned exe), or a
                // program holds it. The running version goes back in its place, so the shortcut still starts something.
                var rolledBack = Updater.RollBack(exe);
                Log.Error("update", $"installed, but the restart failed (previous exe put back: {rolledBack})", ex);
                shell.ShowResult(Loc.Instance.Format(rolledBack ? "Update_RestartFailedRolledBack" : "Update_RestartFailed", version), InfoBarSeverity.Warning);
                return;
            }
            if (!started)
            {
                // The running version stays in use: it goes back in place of the file that was swapped.
                var rolledBack = Updater.RollBack(exe);
                Log.Error("update", $"the installed exe changed before the restart; not started (previous exe put back: {rolledBack})");
                shell.ShowResult(Loc.Instance["Update_BadChecksum"], InfoBarSeverity.Warning);
                return;
            }
            shell.Shutdown();
        }
        finally
        {
            Updating = false;
        }
    }

    /// <summary>
    /// After confirmation: starts the package manager in its own window and ends the app (no package manager can replace a
    /// running exe). The window waits for the app to end, updates it and starts it again. winget and Scoop run as the
    /// signed-in user, Chocolatey elevated (<see cref="PackageInstall"/>).
    /// </summary>
    private void UpdateWithPackageManager(PackageInstall package, string version)
    {
        if (Busy || install.ExePath is not { } exe)
        {
            shell.ShowResult(Loc.Instance["Update_Busy"]);
            return;
        }
        // choco.exe is checked once, before the question: the same path goes into the window's command.
        var choco = package.TrustedChocolatey();
        if (package.Manager == PackageManager.Chocolatey ? choco is null : !package.CanRunFromApp())
        {
            shell.ShowResult(Loc.Instance.Format("Update_PackageManual", package.DisplayName, package.UpdateCommand), InfoBarSeverity.Warning);
            return;
        }
        // Chocolatey's window is elevated and starts Tunero elevated: Windows does not ask again.
        var text = package.RunsAsUser ? "Update_PackageText" : "Update_PackageTextElevated";
        if (!dialogs.Ask(Loc.Instance.Format("Update_ConfirmTitle", version), Loc.Instance.Format(text, package.DisplayName, version), Loc.Instance["Update_Restart"])) return;
        var arguments = package.UpdateArguments(Environment.ProcessId, exe, Loc.Instance.Format("Update_PackageRunning", package.DisplayName),
            Loc.Instance.Format("Update_PackageFailed", package.DisplayName), choco, version);
        if (!StartPackageManager(package, arguments, out _))
        {
            shell.ShowResult(Loc.Instance.Format("Update_PackageManual", package.DisplayName, package.UpdateCommand), InfoBarSeverity.Warning);
            return;
        }
        Log.Info("update", "handed to the package manager, closing", new { package.Manager, version });
        shell.Shutdown();
    }

    /// <summary>
    /// The package manager's PowerShell window: as the signed-in user, or elevated for Chocolatey, whose process id comes
    /// back in <paramref name="elevatedProcessId"/> (the uninstall cleanup waits for it).
    /// </summary>
    public bool StartPackageManager(PackageInstall package, string arguments, out int? elevatedProcessId)
    {
        elevatedProcessId = null;
        if (package.RunsAsUser) return _launch.OpenAsUser(WingetPackage.PowerShell, arguments, install.Elevation);
        try
        {
            var psi = ProcessLauncher.Info(PowerShellScript.Exe, arguments, hidden: false);
            package.HardenChocolateyEnvironment(psi.Environment);
            elevatedProcessId = _launch.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("package", "PowerShell not started", ex);
            return false;
        }
    }
}
