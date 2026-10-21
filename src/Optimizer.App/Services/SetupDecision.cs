using System.IO;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;

namespace Optimizer.App.Services;

/// <summary>What the start shows over the first results: the setup screen, the Start menu question, or neither.</summary>
public enum SetupOffer
{
    /// <summary>The Start menu question (<see cref="ViewModels.MainViewModel.OfferStartMenuEntry"/>), as for every copy the setup is not for.</summary>
    StartMenuQuestion,

    /// <summary>The setup screen over the window.</summary>
    SetupScreen,

    /// <summary>Nothing now: a change started while the signature was checked, and the screen would cover its progress and Stop.</summary>
    None,
}

/// <param name="Offer">What to show.</param>
/// <param name="CanInstall">Settings offers the install; null where the decision did not look (the value stays as it is).</param>
public sealed record SetupChoice(SetupOffer Offer, bool? CanInstall);

/// <summary>
/// The checks the first start's setup decides by. The app passes the real ones (<see cref="ForThisPc"/>); tests pass their
/// answers, so nothing reads this PC's registry or signatures.
/// </summary>
/// <param name="InstalledExe">Where the setup puts the exe (C:\Program Files\Tunero\Tunero.exe).</param>
/// <param name="AdminOnlyFolder">Only administrators can change the exe and the folders above it (<see cref="TrustedPath.IsAdminOnlyBelowSharedFolders"/>).</param>
/// <param name="NewerInstalled">An installed Tunero newer than this one (<see cref="InstallService.NewerInstalledCopy"/>), or null.</param>
/// <param name="TrustedFile">The exe is signed by Tunero's publisher (<see cref="AppInstall.IsTrustedFile"/>).</param>
/// <param name="RefreshEntry">Writes the Installed apps entry for the installed copy again (its version after an update).</param>
public sealed record SetupChecks(string InstalledExe, Func<string, bool> AdminOnlyFolder, Func<string?> NewerInstalled, Func<string, bool> TrustedFile,
    Action<string> RefreshEntry)
{
    public static string DefaultInstalledExe => Path.Combine(AppInstall.DefaultFolder, AppInstall.ExeName);

    /// <summary>This process runs the copy the setup installed (<paramref name="installedExe"/>).</summary>
    public static bool IsInstalledCopy(string? exe, string installedExe) =>
        exe is not null && string.Equals(Path.GetFullPath(exe), installedExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>The real checks: Program Files, the HKLM entry, the file's signature.</summary>
    public static SetupChecks ForThisPc(Optimizer.Core.Actions.IRegistryRoots machine) => new(DefaultInstalledExe, TrustedPath.IsAdminOnlyBelowSharedFolders,
        () => InstallService.NewerInstalledCopy(machine), AppInstall.IsTrustedFile, exe => WriteEntry(machine, exe));

    private static void WriteEntry(Optimizer.Core.Actions.IRegistryRoots machine, string exe)
    {
        // The entry's uninstall command runs with administrator rights: only for an exe users cannot replace.
        if (!TrustedPath.IsAdminOnlyBelowSharedFolders(exe)) return;
        try
        {
            AppInstall.WriteEntry(machine, exe, AppInfo.Text, new FileInfo(exe).Length / 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("install", $"Installed apps entry not refreshed: {ex.Message}");
        }
    }
}

/// <summary>
/// At start, over the first results: the setup screen for a signed copy run from where it was downloaded, else the Start
/// menu question as before. The installed copy refreshes its Installed apps entry (the version after an update).
/// </summary>
public sealed class SetupDecision(InstallFacts install, AppSettings settings, SetupChecks checks)
{
    /// <summary>This process runs the copy the setup installed into Program Files.</summary>
    public bool RunsInstalled(string? exe) => SetupChecks.IsInstalledCopy(exe, checks.InstalledExe);

    /// <param name="plainStart">The start had no switches (<see cref="SingleInstance.Applies"/>).</param>
    public async Task<SetupChoice> DecideAsync(bool plainStart)
    {
        var exe = install.ExePath;
        if (exe is null || !install.IsPublishedExe || !install.Elevated) return new(SetupOffer.StartMenuQuestion, null);
        if (RunsInstalled(exe))
        {
            await Task.Run(() => checks.RefreshEntry(exe));
            return new(SetupOffer.StartMenuQuestion, null);
        }
        // The signature check reads the whole exe (about 70 MB): off the UI thread, and only when the rest allows the install.
        var (installable, installedHere) = await Task.Run(() =>
        {
            var here = checks.AdminOnlyFolder(exe);
            var packaged = install.Package is not null;
            // An older download does not replace a newer installed copy.
            var downgrade = checks.NewerInstalled() is not null;
            return (!here && !packaged && !downgrade && checks.TrustedFile(exe), here);
        });
        if (!AppInstall.ShouldOffer(true, true, plainStart, false, settings.SetupDeclined, installedHere, installable))
            return new(SetupOffer.StartMenuQuestion, installable);
        // A change started while the signature was checked: the screen would cover its progress and Stop. It is offered
        // again at the next start (nothing was declined), and Settings offers it meanwhile.
        return new(ChangeGate.Instance.CanChange ? SetupOffer.SetupScreen : SetupOffer.None, installable);
    }
}
