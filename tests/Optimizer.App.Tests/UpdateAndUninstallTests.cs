using Optimizer.App.Services;
using Optimizer.Core.Tweaks;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The update and uninstall decisions without the window: which way an update goes (release page, package manager),
/// what the uninstall starts and when it stops. Nothing starts and no file changes: the launches and the shell are
/// recorded, the cleanup script is only built.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class UpdateAndUninstallTests : IDisposable
{
    private const string Exe = @"C:\TuneroTest\Tunero.exe";
    private const string DataRoot = @"C:\TuneroTest\Data";

    private readonly FakeShell _shell = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<System.Diagnostics.ProcessStartInfo> _started = [];
    private readonly List<(string File, string? Arguments)> _openedAsUser = [];

    private Launch Recorded => new()
    {
        Start = psi =>
        {
            _started.Add(psi);
            return 4242;
        },
        OpenAsUser = (file, arguments, _) =>
        {
            _openedAsUser.Add((file, arguments));
            return true;
        },
    };

    private static InstallFacts Facts(bool published = true, bool elevated = true, PackageInstall? package = null) =>
        new(Exe, published, elevated, () => package, null);

    private static readonly PackageInstall Winget = new(PackageManager.Winget, "jozuern.Tunero_Microsoft.Winget.Source_8wekyb3d8bbwe", @"C:\TuneroTest\Packages\jozuern.Tunero", Exe);

    private static Func<Task<ReleaseCheckResult>> Newer => () => Task.FromResult(new ReleaseCheckResult(ReleaseCheckStatus.NewerAvailable, new Version(9, 0, 0)));

    public void Dispose()
    {
        ChangeGate.Instance.Busy = false;
        ChangeGate.Instance.Scanning = false;
        ChangeGate.Instance.Updating = false;
    }

    [Fact]
    public async Task ACheckRecordsTheReleaseAndAFailureAsError()
    {
        var update = new UpdateService(Facts(), _dialogs, _shell, Recorded, Newer);
        var changes = 0;
        update.StateChanged += (_, _) => changes++;
        await update.CheckAsync();
        Assert.True(update.UpdateAvailable);
        Assert.True(changes >= 2);

        var failing = new UpdateService(Facts(), _dialogs, _shell, Recorded, () => throw new System.Net.Http.HttpRequestException("offline"));
        await failing.CheckAsync();
        Assert.Equal(ReleaseCheckStatus.Error, failing.Release!.Status);
        Assert.False(failing.UpdateAvailable);
    }

    [Fact]
    public async Task ACopyThatCannotReplaceItselfOpensTheReleasePage()
    {
        var update = new UpdateService(Facts(published: false), _dialogs, _shell, Recorded, Newer);
        await update.CheckAsync();
        Assert.False(update.CanUpdateInApp);
        await update.UpdateNowAsync();
        Assert.Equal([ReleaseCheck.LatestReleaseUrl], _shell.Links);
        Assert.Empty(_dialogs.Asked);
        Assert.False(_shell.ShutDown);
    }

    [Fact]
    public async Task AWingetCopyHandsTheUpdateToWingetAsTheUser()
    {
        var update = new UpdateService(Facts(package: Winget), _dialogs, _shell, Recorded, Newer);
        await update.CheckAsync();
        Assert.True(update.CanUpdateInApp);
        Assert.False(update.CanSelfUpdate);
        await update.UpdateNowAsync();
        var (file, arguments) = Assert.Single(_openedAsUser);
        Assert.EndsWith("powershell.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winget upgrade --id jozuern.Tunero", arguments);
        Assert.Empty(_started);
        Assert.True(_shell.ShutDown);
    }

    [Fact]
    public async Task NoToTheQuestionStartsNothing()
    {
        _dialogs.Answer = false;
        var update = new UpdateService(Facts(package: Winget), _dialogs, _shell, Recorded, Newer);
        await update.CheckAsync();
        await update.UpdateNowAsync();
        Assert.Single(_dialogs.Asked);
        Assert.Empty(_openedAsUser);
        Assert.False(_shell.ShutDown);
    }

    [Fact]
    public async Task AnUpdateWaitsForARunningChange()
    {
        var update = new UpdateService(Facts(package: Winget), _dialogs, _shell, Recorded, Newer);
        await update.CheckAsync();
        ChangeGate.Instance.Busy = true;
        await update.UpdateNowAsync();
        Assert.Empty(_dialogs.Asked);
        Assert.Empty(_openedAsUser);
        Assert.Equal(Loc.Instance["Update_Busy"], Assert.Single(_shell.Results).Text);
    }

    private UninstallService Uninstall(InstallFacts facts, Func<string, Task<bool>>? undoAll = null) =>
        new(facts, _dialogs, _shell, new UpdateService(facts, _dialogs, _shell, Recorded, Newer), () => 3,
            undoAll ?? (_ => Task.FromResult(true)), () => null, DataRoot, Recorded);

    [Fact]
    public async Task OnlyThePublishedElevatedExeUninstallsItself()
    {
        Assert.False(Uninstall(Facts(published: false)).CanUninstall);
        Assert.False(Uninstall(Facts(elevated: false)).CanUninstall);
        await Uninstall(Facts(published: false)).UninstallAsync();
        Assert.Empty(_dialogs.Uninstalls);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task KeepChangesStartsTheCleanupAndCloses()
    {
        _dialogs.Uninstall = UninstallChoice.Keep;
        var undone = false;
        await Uninstall(Facts(), _ => Task.FromResult(undone = true)).UninstallAsync();
        Assert.False(undone);
        Assert.Equal([3], _dialogs.Uninstalls);
        var cleanup = Assert.Single(_started);
        Assert.EndsWith("powershell.exe", cleanup.FileName, StringComparison.OrdinalIgnoreCase);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cleanup.Arguments.Split(' ')[^1]));
        Assert.Contains(DataRoot, script);
        Assert.Contains(Exe, script);
        Assert.True(_shell.ShutDown);
    }

    /// <summary>An installed copy takes its Installed apps entry with it, and the cleanup removes its Program Files folder.</summary>
    [Fact]
    public async Task AnInstalledCopyRemovesItsEntryAndFolder()
    {
        _dialogs.Uninstall = UninstallChoice.Keep;
        var removed = 0;
        var facts = Facts();
        var service = new UninstallService(facts, _dialogs, _shell, new UpdateService(facts, _dialogs, _shell, Recorded, Newer), () => 3,
            _ => Task.FromResult(true), () => null, DataRoot, Recorded, new UninstallService.InstalledCopy(@"C:\TuneroTest", () => removed++));
        await service.UninstallAsync();
        Assert.Equal(1, removed);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(Assert.Single(_started).Arguments.Split(' ')[^1]));
        Assert.Contains(@"$installFolder = 'C:\TuneroTest'", script);
    }

    /// <summary>When the cleanup cannot start, the app stays, so Windows still lists it.</summary>
    [Fact]
    public async Task AFailedCleanupStartKeepsTheInstalledAppsEntry()
    {
        _dialogs.Uninstall = UninstallChoice.Keep;
        var removed = 0;
        var facts = Facts();
        var failing = new Launch { Start = _ => throw new System.ComponentModel.Win32Exception(5) };
        var service = new UninstallService(facts, _dialogs, _shell, new UpdateService(facts, _dialogs, _shell, Recorded, Newer), () => 3,
            _ => Task.FromResult(true), () => null, DataRoot, failing, new UninstallService.InstalledCopy(@"C:\TuneroTest", () => removed++));
        await service.UninstallAsync();
        Assert.Equal(0, removed);
        Assert.False(_shell.ShutDown);
        Assert.Equal(InfoBarSeverity.Error, Assert.Single(_shell.Results).Severity);
    }

    [Fact]
    public async Task AFailedUndoStopsTheUninstall()
    {
        _dialogs.Uninstall = UninstallChoice.UndoFirst;
        await Uninstall(Facts(), _ => Task.FromResult(false)).UninstallAsync();
        Assert.Empty(_started);
        Assert.False(_shell.ShutDown);
    }

    [Fact]
    public async Task CancelChangesNothing()
    {
        _dialogs.Uninstall = UninstallChoice.Cancel;
        await Uninstall(Facts()).UninstallAsync();
        Assert.Empty(_started);
        Assert.Empty(_openedAsUser);
        Assert.False(_shell.ShutDown);
    }

    [Fact]
    public async Task AWingetCopyIsRemovedByWingetAndTheCleanupKeepsItsExe()
    {
        _dialogs.Uninstall = UninstallChoice.Keep;
        await Uninstall(Facts(package: Winget)).UninstallAsync();
        var (_, arguments) = Assert.Single(_openedAsUser);
        Assert.Contains("winget uninstall --product-code", arguments);
        var cleanup = Assert.Single(_started);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cleanup.Arguments.Split(' ')[^1]));
        // winget removes the exe: the cleanup waits for that and deletes only the data folder.
        Assert.Contains("$files = @()", script);
        Assert.True(_shell.ShutDown);
    }
}

[CollectionDefinition(nameof(ChangeGateCollection), DisableParallelization = true)]
public sealed class ChangeGateCollection;

internal sealed class FakeShell : IShell
{
    private readonly List<(string Text, InfoBarSeverity Severity)> _results = [];

    /// <summary>What was shown so far (a copy: the update's download progress arrives from other threads).</summary>
    public List<(string Text, InfoBarSeverity Severity)> Results
    {
        get
        {
            lock (_results) return [.. _results];
        }
    }

    public List<string> Links { get; } = [];
    public bool ShutDown { get; private set; }

    public void ShowResult(string text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        lock (_results) _results.Add((text, severity));
    }
    public void OpenLink(string url) => Links.Add(url);
    public void Shutdown() => ShutDown = true;
}

internal sealed class FakeDialogs : IDialogs
{
    public bool Answer { get; set; } = true;
    public UninstallChoice Uninstall { get; set; } = UninstallChoice.Cancel;
    public List<string> Asked { get; } = [];
    public List<int> Uninstalls { get; } = [];

    /// <summary>What the confirmation returns (null: cancelled), and what it was asked.</summary>
    public ConfirmResult? ConfirmApplyResult { get; set; }
    public List<ConfirmRequest> ConfirmApplyRequests { get; } = [];
    public int ConfirmApplyCalls => ConfirmApplyRequests.Count;

    public ConfirmResult? ConfirmApply(ConfirmRequest request)
    {
        ConfirmApplyRequests.Add(request);
        return ConfirmApplyResult;
    }
    public RestorePointChoice AskRestorePoint() => RestorePointChoice.Cancel;
    public bool ConfirmExpertMode() => false;
    public bool ConfirmUndoAll(int count) => false;

    public UninstallChoice ConfirmUninstall(int activeChanges, string dataFolder)
    {
        Uninstalls.Add(activeChanges);
        return Uninstall;
    }

    public bool Ask(string title, string text, string primary)
    {
        Asked.Add(title);
        return Answer;
    }

    /// <summary>What the save dialog returns (null: cancelled), and how it was asked: name, file type, extension, caption.</summary>
    public string? SaveFile { get; set; }
    public List<(string Name, string Filter, string Extension, string? Title)> SaveRequests { get; } = [];

    public string? PickSaveFile(string defaultName, string filterName, string extension, string? folder = null, string? title = null)
    {
        SaveRequests.Add((defaultName, filterName, extension, title));
        return SaveFile;
    }

    /// <summary>What the open dialog returns (null: cancelled).</summary>
    public string? OpenFile { get; set; }

    public string? PickOpenFile(string filterName, string extension) => OpenFile;
}
