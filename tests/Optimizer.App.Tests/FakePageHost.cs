using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Hardware;
using Optimizer.Core.Platform;
using Optimizer.Core.Tests;
using Optimizer.Core.Tweaks;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The window as a page sees it: a finished PC scan without game libraries, and the banner. Without services the runner
/// only runs page operations (<see cref="ChangeRunner.RunExclusiveAsync(string, Func{Task})"/>); with the services of an
/// <see cref="AppFixture"/> it applies and undoes through the engine on fakes, and the catalog rows are detected there.
/// </summary>
internal sealed class FakePageHost : IPageHost
{
    private readonly AppServices? _services;

    /// <param name="changeLock">
    /// The cross-process change lock: one in memory (<see cref="FakeChangeLock"/>), so no test takes the real changes.lock in
    /// the data folder. With services their session's lock counts instead.
    /// </param>
    public FakePageHost(AppServices? services = null, IDialogs? dialogs = null, IChangeLock? changeLock = null)
    {
        _services = services;
        Runner = new ChangeRunner(services!, dialogs ?? new FakeDialogs(), () => Facts, () => ExpertMode,
            services?.Session ?? new ChangeSession(null!, null!, changeLock ?? new FakeChangeLock()))
        {
            ScanInProgress = () => ScanInProgress,
        };
    }

    /// <summary>What was shown so far; read it once the page's work has ended (page work reports from other threads).</summary>
    public List<(string Text, InfoBarSeverity Severity)> Results { get; } = [];

    public void ShowResult(string text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        lock (Results) Results.Add((text, severity));
    }

    public void OpenLink(string url) => throw new NotSupportedException();
    public void Shutdown() => throw new NotSupportedException();

    public AppServices Services => _services ?? throw new NotSupportedException();
    public ChangeRunner Runner { get; }

    public HardwareProfile? Profile { get; set; } = new()
    {
        Os = new BuildInfo(26300, 0, "26H2", "Professional", "Windows 11 Pro", CpuArchitecture.X64, false, null),
        Software = new SoftwareInfo([], [], []),
    };

    private Facts? _facts;

    /// <summary>The facts of the last scan; only there where a test sets them.</summary>
    public Facts Facts
    {
        get => _facts ?? throw new NotSupportedException();
        set => _facts = value;
    }

    public bool ExpertMode => false;
    public HashSet<string> AppliedIds() => [];
    public IReadOnlyList<TweakStatus> DeviceStates => [];

    /// <summary>With services: the visible catalog tweaks the filter picks, as the engine detects them now.</summary>
    public IEnumerable<TweakItemViewModel> CatalogItems(Func<TweakDefinition, bool> filter) => _services is not { } s ? [] :
        TweakCatalog.Current.Visible.Where(filter).Select(t => new TweakItemViewModel(s.Engine.Detect(t, Facts), Loc.Instance.Language, s.Engine, this)).ToList();

    /// <summary>As the window resolves an id: the catalog, else a change the engine knows from its backup.</summary>
    public TweakDefinition? ResolveTweak(string id) => _services is not { } s ? null : TweakCatalog.Current.Get(id) ?? s.Engine.Resolve(id);

    public Task ToggleAsync(TweakItemViewModel item, bool on) => throw new NotSupportedException();

    /// <summary>The scan that runs now (the window's rescan after a change), or a completed task.</summary>
    public Task ScanInProgress { get; set; } = Task.CompletedTask;

    public IAsyncRelayCommand ScanCommand => throw new NotSupportedException();
    public Page CurrentPage { get; set; } = Page.Storage;
    public InspectorItem? SelectedItem { get; set; }
    public string? VirusTotalKey => null;
    public string? PickSaveFile(string defaultName, string filterName, string extension) => null;
}
