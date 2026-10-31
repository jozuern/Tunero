using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.Services;

/// <summary>"Show in Explorer" for every page: one way to start Explorer, and a word in the result bar when it cannot.</summary>
public static class ExplorerShell
{
    /// <summary>
    /// Selects <paramref name="path"/> in File Explorer, or opens it when it is a folder and <paramref name="openFolder"/>
    /// says so, started as the signed-in user (<see cref="DeElevatedLauncher.ShowInExplorer"/>). A path that no longer
    /// exists, or an Explorer that did not start (Administrator protection without a working fallback), is named in the
    /// result bar, so the click never does nothing. Pages with their own words pass them.
    /// </summary>
    public static void ShowInExplorer(this IShell shell, string path, ElevationInfo? elevation, bool openFolder = false,
        string? missingText = null, string? failedText = null)
    {
        var started = DeElevatedLauncher.ShowInExplorer(path, elevation, openFolder);
        if (started is null) shell.ShowResult(missingText ?? Loc.Instance.Format("Explorer_Missing", path), InfoBarSeverity.Warning);
        else if (started == DeElevatedLauncher.Path.Failed) shell.ShowResult(failedText ?? Loc.Instance.Format("Explorer_Failed", path), InfoBarSeverity.Warning);
    }
}
