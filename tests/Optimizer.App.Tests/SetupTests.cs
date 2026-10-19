using System.IO;
using Optimizer.App.Services;
using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The first start's setup (Install into Program Files, Start menu entry, Run from here) without the window: the install
/// steps, the Start menu and the launch are recorded, nothing changes on this PC.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class SetupTests : IDisposable
{
    public void Dispose() => ChangeGate.Instance.Busy = false;

    private const string Running = @"C:\Users\Test\Downloads\Tunero.exe";
    private const string Installed = @"C:\Program Files\Tunero\Tunero.exe";

    private readonly FakeShell _shell = new();
    private readonly AppSettings _settings = new();
    private readonly List<System.Diagnostics.ProcessStartInfo> _started = [];
    private readonly List<string> _startMenu = [];
    private readonly List<string> _undone = [];
    private int _installs;
    private int _saves;
    private bool _busyDuringInstall;

    private InstallService Service(InstallResult? result = null, bool startMenuFails = false, bool launchFails = false) =>
        new(Running, _settings, () => _saves++, _shell,
            new InstallService.Steps(
                () =>
                {
                    _installs++;
                    _busyDuringInstall = ChangeGate.Instance.Busy;
                    return result ?? new InstallResult(Installed, null, null);
                },
                exe => _undone.Add(exe),
                exe =>
                {
                    if (startMenuFails) throw new IOException("no Start menu");
                    _startMenu.Add(exe);
                }),
            new Launch
            {
                Start = psi =>
                {
                    if (launchFails) throw new System.ComponentModel.Win32Exception(5);
                    _started.Add(psi);
                    return 4242;
                },
            });

    [Fact]
    public void RunFromHereIsRemembered()
    {
        Service().RunHere();
        Assert.True(_settings.SetupDeclined);
        Assert.True(_settings.StartMenuAsked);
        Assert.Equal(1, _saves);
        Assert.Equal(0, _installs);
        Assert.False(_shell.ShutDown);
    }

    [Fact]
    public async Task OnlyTheStartMenuAddsTheEntryForThisExe()
    {
        Assert.False(await Service().InstallAsync(toProgramFiles: false, startMenu: true));
        Assert.Equal([Running], _startMenu);
        Assert.Equal(0, _installs);
        Assert.Empty(_started);
        Assert.True(_settings.SetupDeclined);
    }

    [Fact]
    public async Task NeitherBoxIsRunFromHere()
    {
        Assert.False(await Service().InstallAsync(toProgramFiles: false, startMenu: false));
        Assert.Empty(_startMenu);
        Assert.True(_settings.SetupDeclined);
    }

    [Fact]
    public async Task InstallStartsTheInstalledCopyAndCloses()
    {
        Assert.True(await Service().InstallAsync(toProgramFiles: true, startMenu: true));
        Assert.Equal([Installed], _startMenu);
        var start = Assert.Single(_started);
        Assert.Equal(Installed, start.FileName);
        Assert.Equal([InstallService.InstalledFrom, Running], start.ArgumentList);
        Assert.True(_shell.ShutDown);
        Assert.True(_settings.StartMenuAsked);
    }

    [Fact]
    public async Task AFailedInstallKeepsRunningFromHere()
    {
        Assert.False(await Service(new InstallResult(null, InstallStep.Check, "not signed")).InstallAsync(toProgramFiles: true, startMenu: true));
        Assert.Empty(_startMenu);
        Assert.Empty(_started);
        Assert.False(_shell.ShutDown);
        var (text, severity) = Assert.Single(_shell.Results);
        Assert.Contains("not signed", text);
        Assert.Equal(InfoBarSeverity.Warning, severity);
        Assert.False(_settings.SetupDeclined);
    }

    [Fact]
    public async Task AFailedStartMenuEntryUndoesTheInstall()
    {
        Assert.False(await Service(startMenuFails: true).InstallAsync(toProgramFiles: true, startMenu: true));
        Assert.Equal([Installed], _undone);
        Assert.Empty(_started);
        Assert.False(_shell.ShutDown);
        Assert.Contains(Loc.Instance["Setup_StepStartMenu"], Assert.Single(_shell.Results).Text);
    }

    /// <summary>A change, scan or update that runs would be cut off when the app closes for the installed copy: Install waits.</summary>
    [Fact]
    public async Task InstallWaitsForARunningChange()
    {
        ChangeGate.Instance.Busy = true;
        Assert.False(await Service().InstallAsync(toProgramFiles: true, startMenu: true));
        Assert.Equal(0, _installs);
        Assert.False(_shell.ShutDown);
        Assert.Equal(Loc.Instance["Setup_Busy"], Assert.Single(_shell.Results).Text);
    }

    [Fact]
    public async Task AFailedStartOfTheInstalledCopyUndoesTheInstall()
    {
        Assert.False(await Service(launchFails: true).InstallAsync(toProgramFiles: true, startMenu: false));
        Assert.Equal([Installed], _undone);
        Assert.False(_shell.ShutDown);
    }

    /// <summary>The entry written for the installed copy would start a deleted exe: it starts this one again.</summary>
    [Fact]
    public async Task AFailedStartLeavesTheStartMenuEntryOnThisExe()
    {
        Assert.False(await Service(launchFails: true).InstallAsync(toProgramFiles: true, startMenu: true));
        Assert.Equal([Installed, Running], _startMenu);
        Assert.False(ChangeGate.Instance.Busy);
    }

    /// <summary>Closing the window or signing out during the copy would stop it half way: the install holds the gate.</summary>
    [Fact]
    public async Task TheInstallHoldsTheChangeGateWhileItRuns()
    {
        Assert.False(await Service(new InstallResult(null, InstallStep.Check, "not signed")).InstallAsync(toProgramFiles: true, startMenu: false));
        Assert.True(_busyDuringInstall);
        Assert.False(ChangeGate.Instance.Busy);

        Assert.True(await Service().InstallAsync(toProgramFiles: true, startMenu: false));
        Assert.False(ChangeGate.Instance.Busy);
    }
}
