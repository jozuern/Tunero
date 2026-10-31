using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Logging;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The scan that runs now, or a finished task.</summary>
    public Task ScanInProgress => _state.ScanInProgress;

    /// <summary>
    /// One scan at a time (<see cref="ScanState.ScanAsync"/>): a call while a scan runs asks for one more scan after it
    /// and returns the task that ends after both.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    public Task ScanAsync() => _state.ScanAsync();

    /// <summary>The scan state changed: the status, the observable properties and the rows that show it follow.</summary>
    private void OnScanUpdated(ScanUpdate update)
    {
        Profile = _state.Profile;
        switch (update)
        {
            case ScanUpdate.Started:
                IsScanning = true;
                ScanStatus = Loc.Instance["Scan_Running"];
                Headline = Loc.Instance["Headline_Scanning"];
                break;
            case ScanUpdate.StatesRead:
                InspectorItem.ClearMarkdownCache();
                break;
            case ScanUpdate.Scanned:
                var rows = System.Diagnostics.Stopwatch.StartNew();
                Rebuild();
                Network.Rebuild();
                foreach (var page in new PageViewModel[] { Debloat, Apps, Tools, Health }) page.OnProfileChanged();
                Log.Debug("scan", "rows built", new { ms = rows.ElapsedMilliseconds });
                break;
            case ScanUpdate.Ended:
                var error = _state.ScanFailed ? Loc.Instance["Scan_Failed"] : null;
                ScanFailed = error is not null;
                IsScanning = false;
                ScanStatus = error ?? Loc.Instance["Scan_Idle"];
                OnPropertyChanged(nameof(LogFile));
                // The pane footer shows the scan status only while a scan runs: an error goes to the result bar, which stays.
                if (error is not null) ShowResult(error, InfoBarSeverity.Error);
                break;
            case ScanUpdate.TweaksRead:
                Rebuild();
                Network.Rebuild();
                break;
            case ScanUpdate.FindingsChanged:
                RebuildFindings();
                break;
        }
    }

    /// <summary>Picker entries in the current language: the profiles this PC offers (laptop profiles only on laptops).</summary>
    private void BuildProfileOptions()
    {
        var options = _catalog.Profiles.Profiles.Where(p => p.IsAvailable(_state.Facts))
            .Select(p => new ProfileOption(p.Id, ProfileName(p), Loc.Instance[$"ProfileDesc_{p.Id}"],
                Enum.TryParse<SymbolRegular>(p.Icon, out var icon) ? icon : SymbolRegular.Games24))
            .ToList();
        // Replacing the list while a combo box is changing its selection would blank it: only when the entries differ.
        if (!options.SequenceEqual(ProfileOptions))
        {
            ProfileOptions.Clear();
            foreach (var o in options) ProfileOptions.Add(o);
        }
        // Set the field directly: the change handler is for user choices.
#pragma warning disable MVVMTK0034
        _selectedProfile = ProfileOptions.FirstOrDefault(o => o.Id == _state.Usage.Id);
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedProfile));
        ProfileDescription = Loc.Instance[$"ProfileDesc_{_state.Usage.Id}"];
        SuggestedProfileText = _state.Suggested is { } s && s.Id != _state.Usage.Id && Profile is not null ? Loc.Instance.Format("Profile_Suggested", ProfileName(s)) : null;
    }

    /// <summary>Applies every reset change again (one confirmation). Expert changes are left for the Changes page.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ReapplyDriftAsync()
    {
        var (safe, expert) = _state.DriftToReapply();
        if (safe.Count > 0) await Runner.ApplyBatchAsync(safe, Loc.Instance["Drift_ApplyAgain"], Loc.Instance["Drift_ConfirmIntro"]);
        if (expert > 0) ShowResult(Loc.Instance.Format("Drift_ExpertIndividually", expert));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ReapplyRecordAsync(string tweakId)
    {
        if (ResolveTweak(tweakId) is { } t) await Runner.ApplyAsync(t);
    }

    private string DriftReason(DriftItem d) =>
        d.WindowsUpdatedSince ? Loc.Instance.Format("Drift_ResetUpdate", d.AppliedOn, d.Current) : Loc.Instance["Drift_ResetOther"];
}
