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
    /// <summary>Settings > Uninstall (<see cref="UninstallService"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task UninstallAsync() => Uninstalls.UninstallAsync();

    private System.Windows.Threading.DispatcherTimer? _resultTimer;

    /// <summary>A result that stays until it is closed, whatever its severity (the first sign-in's, <see cref="FirstSignInApplyAsync"/>).</summary>
    private string? _stickyResult;

    /// <summary>The style of the result bar: failures in the caution or error style, so they are not read as done.</summary>
    [ObservableProperty] private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    /// <summary>The save dialog, for pages that write a file the user picks (the Startup list export).</summary>
    public string? PickSaveFile(string defaultName, string filterName, string extension) => _dialogs.PickSaveFile(defaultName, filterName, extension);

    /// <summary>
    /// Shows the result of an action at the bottom; failures in the caution or error style, so they are not read as done.
    /// It closes by itself after long enough to read it; errors, warnings and the first sign-in's result stay until closed.
    /// </summary>
    public void ShowResult(string text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        ResultSeverity = severity;
        ResultText = text;
        ResultOpen = !string.IsNullOrWhiteSpace(text);
        ResultShowsChanges = false;
        ResultShowsUpdate = false;
        if (_resultTimer is null)
        {
            _resultTimer = new System.Windows.Threading.DispatcherTimer();
            _resultTimer.Tick += (_, _) =>
            {
                _resultTimer.Stop();
                ResultOpen = false;
            };
        }
        _resultTimer.Stop();
        // Errors and warnings stay until closed: they must not disappear before they are read. So does the first sign-in's result.
        if (!ResultOpen || severity is InfoBarSeverity.Error or InfoBarSeverity.Warning || text == _stickyResult) return;
        _resultTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(5 + text.Length / 15.0, 8, 20));
        _resultTimer.Start();
    }

    [RelayCommand]
    private void SetLanguage(string lang)
    {
        _settings.Language = lang;
        _settings.Save();
        Loc.Instance.SetLanguage(lang);
        OnPropertyChanged(nameof(AboutText));
        OnPropertyChanged(nameof(UpdateStatus));
    }

    [RelayCommand]
    private void OpenLink(string url)
    {
        if (!DeElevatedLauncher.IsLink(url))
        {
            Log.Warn("launcher", $"not a web or Store link, not opened: {url}");
            return;
        }
        if (DeElevatedLauncher.Open(url, null, _services.Elevation) == DeElevatedLauncher.Path.Failed) ShowResult(url);
    }

    // Takes any row: the Tweaks list reuses a card for a group heading and for a tweak, and while it switches the
    // template the old card asks with the other kind of row (a typed command would throw).
    [RelayCommand]
    private void Select(object? row)
    {
        if (row is InspectorItem item) SelectedItem = item;
    }

    [RelayCommand]
    private void CloseInspector() => SelectedItem = null;

    public void SaveVirusTotalKey(string? key)
    {
        _settings.VirusTotalKey = string.IsNullOrWhiteSpace(key) ? null : Optimizer.Core.Startup.Dpapi.Protect(key.Trim());
        _settings.Save();
        VirusTotalConfigured = _settings.VirusTotalKey is not null;
    }
}
