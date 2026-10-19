using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Hardware;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>The window as the Storage page sees it: a finished PC scan without game libraries, and the banner.</summary>
internal sealed class FakePageHost : IPageHost
{
    public FakePageHost() => Runner = new ChangeRunner(null!, new FakeDialogs(), () => throw new NotSupportedException(), () => new HashSet<string>(), () => false);

    public List<(string Text, InfoBarSeverity Severity)> Results { get; } = [];

    public void ShowResult(string text, InfoBarSeverity severity = InfoBarSeverity.Informational) => Results.Add((text, severity));
    public void OpenLink(string url) => throw new NotSupportedException();
    public void Shutdown() => throw new NotSupportedException();

    public AppServices Services => throw new NotSupportedException();
    public ChangeRunner Runner { get; }

    public HardwareProfile? Profile { get; set; } = new()
    {
        Os = new BuildInfo(26300, 0, "26H2", "Professional", "Windows 11 Pro", CpuArchitecture.X64, false, null),
        Software = new SoftwareInfo([], [], []),
    };

    public Facts Facts => throw new NotSupportedException();
    public bool ExpertMode => false;
    public HashSet<string> AppliedIds() => [];
    public IReadOnlyList<TweakStatus> DeviceStates => [];
    public IEnumerable<TweakItemViewModel> CatalogItems(Func<TweakDefinition, bool> filter) => [];
    public TweakDefinition? ResolveTweak(string id) => null;
    public Task ToggleAsync(TweakItemViewModel item, bool on) => throw new NotSupportedException();
    public Task ScanInProgress => Task.CompletedTask;
    public IAsyncRelayCommand ScanCommand => throw new NotSupportedException();
    public Page CurrentPage => Page.Storage;
    public InspectorItem? SelectedItem { get; set; }
    public string? VirusTotalKey => null;
    public string? PickSaveFile(string defaultName, string filterName, string extension) => null;
}
