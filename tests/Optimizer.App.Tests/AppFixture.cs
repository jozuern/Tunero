using Optimizer.App.Services;
using Optimizer.Core.Actions;
using Optimizer.Core.Platform;
using Optimizer.Core.Tests;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// The app's services on the fakes of Optimizer.Core.Tests (<see cref="EngineFixture"/>: the registry sandbox, fake
/// services, tasks, processes, restore points and change lock, a backup store in a temp folder), with the window around
/// the pages (<see cref="FakePageHost"/>). The signed-in user has a profile folder in a temp folder, listed in the
/// sandbox's profile list. Nothing is read from or changed on this PC. Tests that apply or undo use the process-wide
/// change gate: <c>[Collection(nameof(ChangeGateCollection))]</c>.
/// </summary>
internal sealed class AppFixture : IDisposable
{
    public const string UserSid = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    public const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public AppFixture(bool elevated = true, params string[] nics)
    {
        Pc = new EngineFixture(nics);
        ProfileFolder = TestFolders.Create("app-profile");
        RegistryValue.Write(Pc.Registry, Hive.Machine, $@"{ProfileList}\{UserSid}", "ProfileImagePath", "expandString", ProfileFolder);
        var elevation = new ElevationInfo(elevated, @"PC\Tester", UserSid, @"PC\Tester", UserSid, AdministratorProtection: false);
        var os = new BuildInfo(26300, 1000, "26H2", "Professional", "Windows 11 Pro", CpuArchitecture.X64, false, null);
        Services = new AppServices(Pc.Context, Pc.Store, elevation, Pc.RestorePoints, Pc.ChangeLock, os);
        Host = new FakePageHost(Services, Dialogs) { Facts = new Facts().Set("elevated", elevated).Set("os.build", 26300).Set("system.laptop", false) };
        // As the window does: the runner's result lines go to the result bar.
        Host.Runner.Status += (_, m) => Host.ShowResult(m.Text, m.Severity);
    }

    /// <summary>The fake PC: registry sandbox, services, tasks, processes, backups.</summary>
    public EngineFixture Pc { get; }

    public string ProfileFolder { get; }
    public AppServices Services { get; }

    /// <summary>Confirms every change (without the anti-cheat note) and answers every question with yes, until a test says otherwise.</summary>
    public FakeDialogs Dialogs { get; } = new() { ConfirmApplyResult = new ConfirmResult(false) };

    public FakePageHost Host { get; }
    public ChangeRunner Runner => Host.Runner;

    public void Dispose()
    {
        ChangeGate.Instance.Busy = false;
        ChangeGate.Instance.Scanning = false;
        ChangeGate.Instance.Updating = false;
        Pc.Dispose();
        TestFolders.Delete(ProfileFolder);
    }
}
