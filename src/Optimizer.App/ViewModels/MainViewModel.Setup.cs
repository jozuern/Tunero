using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Actions;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

/// <summary>
/// The first start's setup (<see cref="InstallService"/>, <see cref="AppInstall"/>): a screen over the window that offers to
/// put the exe into Program Files and add the Start menu entry, and Settings' way back to it.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly IRegistryRoots MachineRegistry = new SystemRegistryRoots(null);

    private InstallService? _installs;

    /// <summary>The setup screen covers the window.</summary>
    [ObservableProperty] private bool _setupShown;

    [ObservableProperty] private bool _setupToProgramFiles = true;
    [ObservableProperty] private bool _setupStartMenu = true;

    /// <summary>Settings offers the install: this copy could be installed (signed, downloaded, not installed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReapplyHint))]
    private bool _canInstall;

    /// <summary>Install runs (the copy and its checks take a few seconds): the screen's buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetupInstallCommand), nameof(SetupRunHereCommand))]
    private bool _setupBusy;

    private bool SetupIdle => !SetupBusy;

    /// <summary>
    /// Why Install did not go ahead, shown on the screen itself: the window's result bar is drawn under the screen, so a
    /// note there would be hidden while the screen stays open for a second try.
    /// </summary>
    [ObservableProperty] private string? _setupNote;

    partial void OnSetupShownChanged(bool value) => SetupNote = null;

    /// <summary>The folder the exe runs from now, for the screen's text.</summary>
    public string SetupFolder => Path.GetDirectoryName(Install.ExePath) ?? "";

    public string SetupLocationText => Loc.Instance.Format("Setup_Location", SetupFolder);
    public string SetupProgramFilesHint => Loc.Instance.Format("Setup_ProgramFilesHint", SetupFolder);

    /// <summary>This process runs the copy the setup installed into Program Files.</summary>
    private static bool RunsInstalled(string? exe) => SetupChecks.IsInstalledCopy(exe, SetupChecks.DefaultInstalledExe);

    /// <summary>What the uninstall removes besides the exe when this is the installed copy.</summary>
    private static UninstallService.InstalledCopy? InstalledCopyOf(string? exe) =>
        RunsInstalled(exe) ? new UninstallService.InstalledCopy(AppInstall.DefaultFolder, () => AppInstall.RemoveEntry(MachineRegistry)) : null;

    private InstallService Installs => _installs ??= new InstallService(Install.ExePath ?? "", _settings, _settings.Save, this,
        InstallService.Steps.ForThisPc(Install.ExePath ?? "", () => StartMenuFolder));

    /// <summary>
    /// At start, over the first results: the setup screen for a signed copy run from where it was downloaded, else the
    /// Start menu question as before (<see cref="SetupDecision"/>).
    /// </summary>
    public async Task OfferSetupAsync(bool plainStart)
    {
        var choice = await _setup.DecideAsync(plainStart);
        if (choice.CanInstall is { } canInstall) CanInstall = canInstall;
        switch (choice.Offer)
        {
            case SetupOffer.SetupScreen:
                SetupShown = true;
                break;
            case SetupOffer.StartMenuQuestion:
                OfferStartMenuEntry();
                break;
        }
    }

    /// <summary>
    /// The installed copy's first start (--installed-from): deletes the downloaded exe when it is still the same file (and
    /// the setup folder of a first sign-in it came from), and moves a Start menu entry that still starts the downloaded exe
    /// to the installed one. <paramref name="report"/> false: the result goes to the log only (the first sign-in shows its own).
    /// </summary>
    public async Task FinishInstallAsync(string oldExe, bool report = true)
    {
        if (Install.ExePath is not { } exe || !RunsInstalled(exe)) return;
        var removed = await Task.Run(() => InstallService.RemoveOldCopy(oldExe, exe));
        try
        {
            if (StartMenuFolder is { } folder && StartMenuShortcut.Target(folder) is { } target &&
                string.Equals(target, oldExe, StringComparison.OrdinalIgnoreCase))
                StartMenuShortcut.Create(folder, exe, Loc.Instance["StartMenu_Description"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("startmenu", "entry not moved to the installed copy", ex);
        }
        OnPropertyChanged(nameof(StartMenuEntry));
        Log.Info("install", "installed copy started", new { oldExe, removed });
        if (report)
            ShowResult(removed ? Loc.Instance.Format("Setup_Installed", AppInstall.DefaultFolder) : Loc.Instance.Format("Setup_OldCopyKept", AppInstall.DefaultFolder, oldExe),
                removed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    [RelayCommand(CanExecute = nameof(SetupIdle))]
    private async Task SetupInstallAsync()
    {
        SetupBusy = true;
        SetupNote = null;
        try
        {
            if (await Installs.InstallAsync(SetupToProgramFiles, SetupStartMenu)) return;
        }
        finally
        {
            SetupBusy = false;
        }
        // Refused because a change, scan or update runs: the screen stays for a second try once it has finished.
        if (SetupToProgramFiles && !ChangeGate.Instance.CanChange)
        {
            SetupNote = Loc.Instance["Setup_Busy"];
            return;
        }
        SetupShown = false;
        OnPropertyChanged(nameof(StartMenuEntry));
    }

    [RelayCommand(CanExecute = nameof(SetupIdle))]
    private void SetupRunHere()
    {
        Installs.RunHere();
        SetupShown = false;
    }

    /// <summary>Settings > Install in Program Files: the same screen again.</summary>
    [RelayCommand]
    private void OpenSetup()
    {
        SetupToProgramFiles = true;
        SetupStartMenu = !StartMenuEntry;
        SetupShown = true;
    }
}
