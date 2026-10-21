using System.IO;
using System.Text.Json;
using Optimizer.Core.Logging;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;

namespace Optimizer.App.Services;

/// <summary>What <see cref="FirstSignInStart.Run(string?, AppSettings, Action, InstallService.Steps, Launch)"/> did.</summary>
public enum FirstSignInOutcome
{
    /// <summary>Not started from %WINDIR%\Setup\Tunero with a valid file beside it: nothing was done.</summary>
    NotFirstSignIn,

    /// <summary>The first sign-in has been set up before (<see cref="FirstSignInMarkers.Done"/>): nothing was done.</summary>
    AlreadyDone,

    /// <summary>Installed into Program Files; the installed copy was started with the profile.</summary>
    Installed,

    /// <summary>A newer Tunero is installed already: it was kept and started with the profile.</summary>
    KeptNewer,

    /// <summary>Not installed (a build without Tunero's signature, a failed step): this copy was started again with the profile.</summary>
    RunsFromSetupFolder,

    /// <summary>Nothing could be started; the PC stays as Windows Setup left it.</summary>
    Failed,
}

/// <summary>
/// The first sign-in on a PC installed from Tunero's install media. Windows runs <c>Tunero.exe --first-sign-in</c> from
/// %WINDIR%\Setup\Tunero (FirstLogonCommands, elevated for the new administrator account) before the desktop appears, so
/// this start shows nothing and ends quickly: it saves the language, theme and profile chosen on the media, leaves a
/// pending marker (<see cref="FirstSignInMarkers"/>), installs the exe into Program Files with its Start menu entry
/// (<see cref="AppInstall"/>, as the first start's setup does; a newer installed copy is kept), and starts the installed
/// copy with <see cref="ApplySwitch"/> and <see cref="InstallService.InstalledFrom"/>. That copy takes the marker (with the
/// apps to install, if the media named any), opens its window, applies the profile's recommendations, installs those apps
/// and removes the setup folder
/// (<see cref="InstallService.RemoveOldCopy(string, string)"/>). When the copy cannot be installed, this exe is started
/// again with the profile from where it is. It runs once: after the apply, a later --first-sign-in does nothing. Every
/// failure is logged and ends the run: Windows always goes on to the desktop.
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
        return Run(exe, file, settings, save, steps, launch, new FirstSignInMarkers(DataPaths.Root), DateTime.UtcNow, Loc.Instance.SetLanguage);
    }

    /// <summary>
    /// --first-sign-in-apply in the started copy: true when the pending marker for <paramref name="profileId"/> was there
    /// (it is used up now, so the apply runs once); false for any other start, which opens normally and applies nothing.
    /// </summary>
    public static bool TakePending(string profileId, out IReadOnlyList<string> apps) =>
        new FirstSignInMarkers(DataPaths.Root).TakePending(profileId, DateTime.UtcNow, out apps);

    /// <summary>The run for a file already read and trusted (tests pass one, their own markers, and no <paramref name="useLanguage"/>).</summary>
    /// <param name="useLanguage">Switches the app's language to the file's (the Start menu entry's description is in it).</param>
    internal static FirstSignInOutcome Run(string exe, FirstSignInFile file, AppSettings settings, Action save, InstallService.Steps steps, Launch launch,
        FirstSignInMarkers markers, DateTime now, Action<string>? useLanguage = null)
    {
        if (markers.Done)
        {
            Log.Info("firstsignin", "this PC was set up at its first sign-in already: nothing done", new { exe });
            return FirstSignInOutcome.AlreadyDone;
        }
        Log.Info("firstsignin", "setting up this PC", new { exe, file.Profile, file.Language, file.Theme, file.Apps });
        // Without the marker the started copy would apply nothing: then nothing is started either.
        if (!markers.SetPending(file.Profile, now, file.Apps)) return FirstSignInOutcome.Failed;
        // The choices on the media are the app's settings from now on: the installed copy reads them at its start, and later
        // starts keep them. The setup screen and the Start menu question are answered by this run.
        settings.Language = file.Language;
        settings.Theme = file.Theme;
        settings.Profile = file.Profile;
        settings.StartMenuAsked = true;
        settings.SetupDeclined = false;
        save();
        useLanguage?.Invoke(file.Language);

        // An upgrade or a reinstall that kept Program Files may hold a newer Tunero: the copy from the media never replaces it.
        if (NewerTrustedCopy(steps) is { } newer)
        {
            Log.Info("firstsignin", "a newer Tunero is installed: kept, starting it", new { newer });
            AddStartMenu(steps, newer);
            if (Start(launch, newer, [InstallService.InstalledFrom, exe, ApplySwitch, file.Profile])) return FirstSignInOutcome.KeptNewer;
        }
        else
        {
            var result = steps.Install();
            if (result.InstalledExe is { } installed)
            {
                var entry = AddStartMenu(steps, installed);
                if (Start(launch, installed, [InstallService.InstalledFrom, exe, ApplySwitch, file.Profile]))
                {
                    Log.Info("firstsignin", "installed, starting the installed copy", new { installed });
                    return FirstSignInOutcome.Installed;
                }
                steps.Undo(installed);
                // The entry started the copy that is gone now: it starts the one that sets up the PC instead.
                if (entry) AddStartMenu(steps, exe);
            }
            else
            {
                Log.Warn("firstsignin", $"not installed ({result.FailedAt}: {result.Error}); setting up this PC from the setup folder");
            }
        }
        if (Start(launch, exe, [ApplySwitch, file.Profile]))
        {
            Log.Info("firstsignin", "started again from the setup folder; the folder stays", new { exe });
            return FirstSignInOutcome.RunsFromSetupFolder;
        }
        // Nothing runs that could take the marker: it goes, so no later start finds it.
        markers.ClearPending();
        return FirstSignInOutcome.Failed;
    }

    /// <summary>
    /// A newer installed Tunero that may be started elevated (<see cref="InstallService.Steps.TrustedExe"/>), or null. A
    /// failed look counts as none, and a copy that fails the checks is not started: the normal install runs then.
    /// </summary>
    private static string? NewerTrustedCopy(InstallService.Steps steps)
    {
        try
        {
            if (steps.NewerInstalled?.Invoke() is not { } newer) return null;
            if (steps.TrustedExe?.Invoke(newer) == true) return newer;
            Log.Warn("firstsignin", "a newer Tunero is installed, but users could change it or it is not signed by Tunero's publisher: not started", new { newer });
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("firstsignin", $"looking for a newer installed Tunero failed; installing as usual: {ex.Message}");
            return null;
        }
    }

    /// <summary>The Start menu entry for <paramref name="exe"/>; a failure is logged (Settings offers the entry again).</summary>
    private static bool AddStartMenu(InstallService.Steps steps, string exe)
    {
        try
        {
            steps.AddStartMenu(exe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("firstsignin", $"the Start menu entry for {exe} failed", ex);
            return false;
        }
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

/// <summary>
/// Run-once markers in Tunero's data folder (admin-only when elevated, <see cref="SecureFolder"/>): --first-sign-in writes
/// "pending" with the profile, the time and the apps to install; the started copy applies only while that marker is there, for that profile and
/// within a day, and replaces it with "done" before it applies anything. With "done", a later --first-sign-in (a setup
/// folder that stayed, a reinstall that kept the data folder) does nothing, and no start applies without a confirmation.
/// </summary>
public sealed class FirstSignInMarkers(string folder)
{
    private const string PendingName = "first-sign-in.pending";
    private const string DoneName = "first-sign-in.done";

    /// <summary>How long after --first-sign-in its pending marker still counts.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private sealed record Marker(string Profile, DateTime At, IReadOnlyList<string>? Apps = null);

    private string Pending => Path.Combine(folder, PendingName);

    public bool Done => File.Exists(Path.Combine(folder, DoneName));

    public bool HasPending => File.Exists(Pending);

    /// <summary>Deletes the pending marker (best effort, logged).</summary>
    public void ClearPending()
    {
        try
        {
            File.Delete(Pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("firstsignin", $"the pending first sign-in marker was not removed: {ex.Message}");
        }
    }

    /// <summary>Writes the pending marker; false (logged) when it cannot be written.</summary>
    public bool SetPending(string profile, DateTime nowUtc, IReadOnlyList<string>? apps = null) => Write(PendingName, new Marker(profile, nowUtc, apps));

    public bool TakePending(string profile, DateTime nowUtc) => TakePending(profile, nowUtc, out _);

    /// <summary>
    /// Uses up the pending marker: true when it was there, for <paramref name="profile"/>, and less than <see cref="MaxAge"/>
    /// off <paramref name="nowUtc"/>, and the done marker is written. The pending marker is gone afterwards in any case.
    /// <paramref name="apps"/>: the apps to install with it; none when the list is not one the media page could make.
    /// </summary>
    public bool TakePending(string profile, DateTime nowUtc, out IReadOnlyList<string> apps)
    {
        apps = [];
        // Applied once already: a pending marker beside the done one (left when its delete failed) never applies again.
        if (Done)
        {
            Log.Info("firstsignin", "this PC was set up at its first sign-in already: nothing applied", new { profile });
            ClearPending();
            return false;
        }
        Marker? marker;
        try
        {
            if (!File.Exists(Pending))
            {
                Log.Info("firstsignin", "no pending first sign-in: nothing applied", new { profile });
                return false;
            }
            marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(Pending));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("firstsignin", $"the pending first sign-in could not be read: nothing applied ({ex.Message})");
            marker = null;
        }
        var valid = marker is not null && marker.Profile == profile && (nowUtc - marker.At).Duration() <= MaxAge;
        if (!valid) Log.Warn("firstsignin", "the pending first sign-in is for another profile or too old: nothing applied", new { profile, marker });
        var taken = valid && Write(DoneName, new Marker(profile, nowUtc));
        ClearPending();
        if (taken && marker!.Apps is { Count: > 0 } list)
        {
            if (FirstSignIn.ValidApps(list)) apps = list;
            else Log.Warn("firstsignin", "the apps of the pending first sign-in are not ones the media offers: none installed", new { list });
        }
        return taken;
    }

    private bool Write(string name, Marker marker)
    {
        try
        {
            Directory.CreateDirectory(folder);
            DurableFile.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(marker));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("firstsignin", $"the first sign-in marker {name} could not be written", ex);
            return false;
        }
    }
}
