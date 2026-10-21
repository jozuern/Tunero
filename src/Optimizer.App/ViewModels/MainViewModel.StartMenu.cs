using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Hardware;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public sealed partial class MainViewModel
{
    private string? StartMenuFolder => StartMenuShortcut.ProgramsFolder(_services.Elevation, _services.ProfilePath);

    /// <summary>The app has an entry in the signed-in user's Start menu, so Windows Search finds it.</summary>
    public bool StartMenuEntry
    {
        get => StartMenuFolder is { } folder && StartMenuShortcut.Exists(folder);
        set
        {
            if (value != StartMenuEntry) SetStartMenuEntry(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Only the published exe gets an entry (a build folder is not where the app lives). Scoop creates its own entry (Scoop
    /// Apps) from the manifest and removes it with the app, so a Scoop copy does not add a second one.
    /// </summary>
    public bool CanAddToStartMenu => IsPublishedExe && Package?.Manager != PackageManager.Scoop;

    /// <summary>What the entry does, or why the switch is off here (a Scoop copy, a build that is not the published exe).</summary>
    public string StartMenuHint => Loc.Instance[
        CanAddToStartMenu ? "StartMenu_Hint"
        : Package?.Manager == PackageManager.Scoop ? "StartMenu_Scoop"
        : "StartMenu_NotHere"];

    private void SetStartMenuEntry(bool on)
    {
        if (StartMenuFolder is not { } folder || Environment.ProcessPath is not { } exe) return;
        try
        {
            if (on) StartMenuShortcut.Create(folder, exe, Loc.Instance["StartMenu_Description"]);
            else StartMenuShortcut.Remove(folder);
            Log.Info("startmenu", on ? "entry added" : "entry removed", new { folder });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("startmenu", "entry not changed", ex);
            ShowResult(Loc.Instance.Format("StartMenu_Failed", ex.Message), InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// Every start: an entry under an earlier name ("Tunero", "PCOptimizer") that starts this exe gets the new name. First
    /// start of the published exe: asks once whether to add the entry.
    /// </summary>
    public void OfferStartMenuEntry()
    {
        if (CanAddToStartMenu && StartMenuFolder is { } folder && Environment.ProcessPath is { } exe)
        {
            try
            {
                if (StartMenuShortcut.RenameLegacy(folder, exe, Loc.Instance["StartMenu_Description"])) Log.Info("startmenu", "entry renamed", new { folder });
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
            {
                Log.Error("startmenu", "entry not renamed", ex);
            }
        }
        if (_settings.StartMenuAsked || !CanAddToStartMenu || StartMenuEntry) return;
        _settings.StartMenuAsked = true;
        _settings.Save();
        if (_dialogs.Ask(Loc.Instance["StartMenu_AskTitle"], Loc.Instance["StartMenu_AskText"], Loc.Instance["StartMenu_Add"])) StartMenuEntry = true;
    }
}
