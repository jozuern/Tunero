using System.IO;
using Optimizer.Core.Logging;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;

namespace Optimizer.App.Services;

/// <summary>What <see cref="FirstSignInStart.Run(string?, AppSettings, Action, InstallService.Steps, Launch)"/> did.</summary>
public enum FirstSignInOutcome
{
    /// <summary>Not started from %WINDIR%\Setup\Tunero with a valid file beside it: nothing was done.</summary>
    NotFirstSignIn,

    /// <summary>Installed into Program Files; the installed copy was started with the profile.</summary>
    Installed,

    /// <summary>Not installed (a build without Tunero's signature, a failed step): this copy was started again with the profile.</summary>
    RunsFromSetupFolder,

    /// <summary>Nothing could be started; the PC stays as Windows Setup left it.</summary>
    Failed,
}

/// <summary>
/// The first sign-in on a PC installed from Tunero's install media. Windows runs <c>Tunero.exe --first-sign-in</c> from
/// %WINDIR%\Setup\Tunero (FirstLogonCommands, elevated for the new administrator account) before the desktop appears, so
/// this start shows nothing and ends quickly: it saves the language, theme and profile chosen on the media, installs the
/// exe into Program Files with its Start menu entry (<see cref="AppInstall"/>, as the first start's setup does), and
/// starts the installed copy with <see cref="ApplySwitch"/> and <see cref="InstallService.InstalledFrom"/>. That copy opens
/// its window, applies the profile's recommendations and removes the setup folder (<see cref="InstallService.RemoveOldCopy(string, string)"/>).
/// When the copy cannot be installed, this exe is started again with the profile from where it is. Every failure is
/// logged and ends the run: Windows always goes on to the desktop.
/// </summary>
public static class FirstSignInStart
{
    /// <summary>The installed copy's start: open the window and apply this profile's recommendations after the first scan.</summary>
    public const string ApplySwitch = "--first-sign-in-apply";

    /// <summary>--first-sign-in: reads the file beside <paramref name="exe"/> (only in the setup folder) and goes on from there.</summary>
    public static FirstSignInOutcome Run(string? exe, AppSettings settings, Action save, InstallService.Steps steps, Launch launch)
    {
        var file = exe is null ? null : FirstSignIn.Read(exe, TrustedPath.IsAdminOnlyWritableFolder);
        if (exe is null || file is null)
        {
            Log.Warn("firstsignin", "not started from the setup folder, or its file is missing or not valid: nothing done", new { exe });
            return FirstSignInOutcome.NotFirstSignIn;
        }
        return Run(exe, file, settings, save, steps, launch, Loc.Instance.SetLanguage);
    }

    /// <summary>The run for a file already read and trusted (tests pass one, and no <paramref name="useLanguage"/>).</summary>
    /// <param name="useLanguage">Switches the app's language to the file's (the Start menu entry's description is in it).</param>
    internal static FirstSignInOutcome Run(string exe, FirstSignInFile file, AppSettings settings, Action save, InstallService.Steps steps, Launch launch,
        Action<string>? useLanguage = null)
    {
        Log.Info("firstsignin", "setting up this PC", new { exe, file.Profile, file.Language, file.Theme });
        // The choices on the media are the app's settings from now on: the installed copy reads them at its start, and later
        // starts keep them. The setup screen and the Start menu question are answered by this run.
        settings.Language = file.Language;
        settings.Theme = file.Theme;
        settings.Profile = file.Profile;
        settings.StartMenuAsked = true;
        settings.SetupDeclined = false;
        save();
        useLanguage?.Invoke(file.Language);

        var result = steps.Install();
        if (result.InstalledExe is { } installed)
        {
            try
            {
                steps.AddStartMenu(installed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // Installed apps lists it and Settings offers the entry again: no reason to give up the install.
                Log.Error("firstsignin", "installed, but the Start menu entry failed", ex);
            }
            if (Start(launch, installed, [InstallService.InstalledFrom, exe, ApplySwitch, file.Profile]))
            {
                Log.Info("firstsignin", "installed, starting the installed copy", new { installed });
                return FirstSignInOutcome.Installed;
            }
            steps.Undo(installed);
        }
        else
        {
            Log.Warn("firstsignin", $"not installed ({result.FailedAt}: {result.Error}); setting up this PC from the setup folder");
        }
        if (Start(launch, exe, [ApplySwitch, file.Profile]))
        {
            Log.Info("firstsignin", "started again from the setup folder; the folder stays", new { exe });
            return FirstSignInOutcome.RunsFromSetupFolder;
        }
        return FirstSignInOutcome.Failed;
    }

    /// <summary>Starts the exe on its own (this run ends right after); false when it did not start.</summary>
    private static bool Start(Launch launch, string exe, IEnumerable<string> arguments)
    {
        try
        {
            launch.Start(InstallService.ExeStart(exe, arguments));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("firstsignin", $"{exe} did not start", ex);
            return false;
        }
    }
}
