using System.IO;
using Optimizer.App.Services;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// The first sign-in on a PC installed from Tunero's media (--first-sign-in, then --first-sign-in-apply in the installed
/// copy): the start before any window, the plan the window applies, and the cleanup of the setup folder. Installs,
/// Start menu entries and launches are recorded; files live in a temp folder; nothing changes on this PC.
/// </summary>
public sealed class FirstSignInTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tunero-first-sign-in-{Guid.NewGuid():N}");
    private readonly AppSettings _settings = new();
    private readonly List<System.Diagnostics.ProcessStartInfo> _started = [];
    private readonly List<string> _startMenu = [];
    private readonly List<string> _undone = [];
    private int _installs;
    private int _saves;

    private const string Installed = @"C:\Program Files\Tunero\Tunero.exe";

    public FirstSignInTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Exe => Path.Combine(_folder, AppInstall.ExeName);

    /// <summary>Tunero's data folder (a temp stand-in) with the run-once markers.</summary>
    private FirstSignInMarkers Markers => new(Path.Combine(_folder, "data"));

    private static readonly DateTime Now = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    private FirstSignInOutcome RunWith(FirstSignInFile file, InstallService.Steps? steps = null, Launch? launch = null, DateTime? at = null) =>
        FirstSignInStart.Run(Exe, file, _settings, () => _saves++, steps ?? Steps(), launch ?? Launch(), Markers, at ?? Now);

    private InstallService.Steps Steps(InstallResult? result = null, string? newerInstalled = null, bool newerTrusted = true,
        Exception? newerFails = null) => new(
        () =>
        {
            _installs++;
            return result ?? new InstallResult(Installed, null, null);
        },
        exe => _undone.Add(exe),
        exe => _startMenu.Add(exe))
    {
        NewerInstalled = () => newerFails is null ? newerInstalled : throw newerFails,
        TrustedExe = _ => newerTrusted,
    };

    private Launch Launch(Func<System.Diagnostics.ProcessStartInfo, bool>? fails = null) => new()
    {
        Start = psi =>
        {
            if (fails?.Invoke(psi) == true) throw new System.ComponentModel.Win32Exception(5);
            _started.Add(psi);
            return 4242;
        },
    };

    private static TweakStatus Status(TweakDefinition t) =>
        new(t, TweakState.NotApplied, t.Impact.Gaming, t.Impact.Effect, null, true, [], false);

    /// <summary>A scan where every visible tweak is recommended and off: batch-safe and other changes alike.</summary>
    private static PcSnapshot Pc() =>
        new(new Facts().Set("system.laptop", false), TweakCatalog.Current.Visible.Select(Status).ToList(), new HashSet<string>(), []);

    private static BatchOutcome AllApplied(IReadOnlyList<TweakDefinition> tweaks) =>
        new(tweaks.Select(t => new BatchItemResult(t, new ApplyResult(ApplyOutcome.Applied, []))).ToList());

    /// <summary>
    /// The window's first sign-in applies exactly what --apply-recommended applies for the profile, and both leave out
    /// (and the command line lists) the changes that need their own confirmation: previews, Expert, boot-critical, anti-cheat.
    /// </summary>
    [Fact]
    public async Task PlanIsTheSameForWindowAndCommandLine()
    {
        var pc = Pc();
        var applied = new List<TweakDefinition>();
        var lines = new List<string>();
        var code = await CommandLineApply.ApplyRecommendedPlanAsync(pc, "gaming", null, together =>
        {
            applied.AddRange(together);
            return Task.FromResult(AllApplied(together));
        }, lines);

        var window = CommandLineApply.RecommendedPlan(pc, "gaming", null);
        Assert.Equal(CommandLineApply.Done, code);
        Assert.NotNull(window);
        Assert.Equal("gaming", window.Profile.Id);
        Assert.NotEmpty(applied);
        Assert.Equal(applied.Select(t => t.Id), window.Together.Select(t => t.Id));
        Assert.All(applied, t => Assert.True(t.IsBatchSafe, t.Id));
        Assert.NotEmpty(window.InApp);
        Assert.All(window.InApp, t => Assert.False(t.IsBatchSafe, t.Id));
        Assert.All(window.InApp, t => Assert.DoesNotContain(t, applied));
        Assert.Equal(window.InApp.Count, lines.Count(l => l.StartsWith(Loc.Instance.Format("Cli_NeedsApp", ""), StringComparison.Ordinal)));
    }

    /// <summary>A laptop profile on a desktop PC: neither path picks anything.</summary>
    [Fact]
    public async Task AProfileThisPcDoesNotOfferAppliesNothing()
    {
        var pc = Pc();
        var called = false;
        var code = await CommandLineApply.ApplyRecommendedPlanAsync(pc, "laptopGaming", null, together =>
        {
            called = true;
            return Task.FromResult(AllApplied(together));
        }, []);
        Assert.Equal(CommandLineApply.UnknownProfile, code);
        Assert.False(called);
        Assert.Null(CommandLineApply.RecommendedPlan(pc, "laptopGaming", null));
        Assert.Null(CommandLineApply.RecommendedPlan(pc, "turbo", null));
    }

    /// <summary>An exe outside %WINDIR%\Setup\Tunero (here a temp folder with a valid file beside it) is no first sign-in: nothing happens.</summary>
    [Fact]
    public void FirstSignInIgnoredOutsideTheSetupFolder()
    {
        File.WriteAllText(Exe, "");
        File.WriteAllText(Path.Combine(_folder, FirstSignIn.FileName), FirstSignIn.Serialize(new FirstSignInFile("gaming", "de", "Dark")));
        Assert.Equal(FirstSignInOutcome.NotFirstSignIn, FirstSignInStart.Run(Exe, _settings, () => _saves++, Steps(), Launch()));
        Assert.Equal(0, _installs);
        Assert.Empty(_started);
        Assert.Empty(_startMenu);
        Assert.Equal(0, _saves);
        Assert.Null(_settings.Language);
    }

    [Fact]
    public void FirstSignInInstallsAndStartsTheInstalledCopy()
    {
        var outcome = RunWith(new FirstSignInFile("office", "de", "Dark"));
        Assert.Equal(FirstSignInOutcome.Installed, outcome);
        Assert.Equal(1, _installs);
        Assert.Equal([Installed], _startMenu);
        var start = Assert.Single(_started);
        Assert.Equal(Installed, start.FileName);
        Assert.Equal([InstallService.InstalledFrom, Exe, FirstSignInStart.ApplySwitch, "office"], start.ArgumentList);
        // The choices on the media are the app's settings from now on (the installed copy reads them at its start).
        Assert.Equal("de", _settings.Language);
        Assert.Equal("Dark", _settings.Theme);
        Assert.Equal("office", _settings.Profile);
        Assert.True(_settings.StartMenuAsked);
        Assert.False(_settings.SetupDeclined);
        Assert.True(_saves > 0);
        Assert.Empty(_undone);
    }

    /// <summary>A copy that cannot be installed (a build without Tunero's signature) still sets up the PC, from where it is.</summary>
    [Fact]
    public void AFailedInstallStartsThisCopyWithTheProfile()
    {
        var outcome = RunWith(new FirstSignInFile("gaming", "en", "System"),
            Steps(new InstallResult(null, InstallStep.Check, "the copy is not signed by Tunero's publisher")));
        Assert.Equal(FirstSignInOutcome.RunsFromSetupFolder, outcome);
        Assert.Empty(_startMenu);
        var start = Assert.Single(_started);
        Assert.Equal(Exe, start.FileName);
        Assert.Equal([FirstSignInStart.ApplySwitch, "gaming"], start.ArgumentList);
    }

    /// <summary>
    /// The installed copy does not start: the install is undone, the Start menu entry made for it starts this copy, and this
    /// copy sets up the PC instead.
    /// </summary>
    [Fact]
    public void AFailedStartOfTheInstalledCopyUndoesTheInstall()
    {
        var outcome = RunWith(new FirstSignInFile("gaming", "en", "System"), launch: Launch(psi => psi.FileName == Installed));
        Assert.Equal(FirstSignInOutcome.RunsFromSetupFolder, outcome);
        Assert.Equal([Installed], _undone);
        Assert.Equal([Installed, Exe], _startMenu);
        Assert.Equal(Exe, Assert.Single(_started).FileName);
    }

    /// <summary>Nothing could be started: the run ends quietly (Windows goes on to the desktop), and says so in the log.</summary>
    [Fact]
    public void NothingStartsWithoutBreakingTheSignIn()
    {
        var outcome = RunWith(new FirstSignInFile("gaming", "en", "System"), Steps(new InstallResult(null, InstallStep.Copy, "disk full")), Launch(_ => true));
        Assert.Equal(FirstSignInOutcome.Failed, outcome);
        // Nothing runs that could take it: the pending marker goes again.
        Assert.False(Markers.HasPending);
        Assert.Empty(_started);
    }

    /// <summary>
    /// Run once: the start in the setup folder leaves a pending marker; the started copy takes it (a done marker replaces
    /// it) before it applies anything, and a second take finds none.
    /// </summary>
    [Fact]
    public void TheApplyRunsOnceForThePendingProfile()
    {
        Assert.Equal(FirstSignInOutcome.Installed, RunWith(new FirstSignInFile("gaming", "en", "System")));
        Assert.True(Markers.TakePending("gaming", Now.AddMinutes(1)));
        Assert.False(Markers.TakePending("gaming", Now.AddMinutes(2)));
        Assert.True(Markers.Done);
    }

    /// <summary>A start with --first-sign-in-apply but no pending marker (typed by hand, a shortcut) opens normally and applies nothing.</summary>
    [Fact]
    public void WithoutAPendingMarkerNothingIsApplied()
    {
        Assert.False(Markers.TakePending("gaming", Now));
        Assert.False(Markers.Done);
    }

    /// <summary>A pending marker for another profile, or one more than a day off, does not apply (and is used up).</summary>
    [Theory]
    [InlineData("office", 1)]
    [InlineData("gaming", 25 * 60)]
    [InlineData("gaming", -25 * 60)]
    public void AStaleOrOtherPendingMarkerAppliesNothing(string profile, int minutesLater)
    {
        RunWith(new FirstSignInFile("gaming", "en", "System"));
        Assert.False(Markers.TakePending(profile, Now.AddMinutes(minutesLater)));
        Assert.False(Markers.TakePending("gaming", Now.AddMinutes(1)));
    }

    /// <summary>After the first sign-in has applied, a later --first-sign-in (a leftover setup folder, Windows Setup again) does nothing.</summary>
    [Fact]
    public void ALaterFirstSignInIsANoOp()
    {
        RunWith(new FirstSignInFile("gaming", "en", "System"));
        Assert.True(Markers.TakePending("gaming", Now.AddMinutes(1)));
        _started.Clear();
        var installs = _installs;
        _settings.Language = null;

        Assert.Equal(FirstSignInOutcome.AlreadyDone, RunWith(new FirstSignInFile("office", "de", "Dark"), at: Now.AddDays(3)));
        Assert.Equal(installs, _installs);
        Assert.Empty(_started);
        Assert.Null(_settings.Language);
        Assert.False(Markers.TakePending("office", Now.AddDays(3)));
    }

    /// <summary>A newer Tunero already installed (an upgrade kept Program Files) is never replaced by the older copy from the media.</summary>
    [Fact]
    public void ANewerInstalledCopyIsKept()
    {
        var outcome = RunWith(new FirstSignInFile("gaming", "en", "System"), Steps(newerInstalled: Installed));
        Assert.Equal(FirstSignInOutcome.KeptNewer, outcome);
        Assert.Equal(0, _installs);
        Assert.Empty(_undone);
        var start = Assert.Single(_started);
        Assert.Equal(Installed, start.FileName);
        Assert.Equal([InstallService.InstalledFrom, Exe, FirstSignInStart.ApplySwitch, "gaming"], start.ArgumentList);
    }

    /// <summary>
    /// The done marker wins: a pending marker that could not be deleted after the apply (or one written later) never lets
    /// a start apply again, and is removed.
    /// </summary>
    [Fact]
    public void APendingMarkerBesideTheDoneMarkerAppliesNothing()
    {
        RunWith(new FirstSignInFile("gaming", "en", "System"));
        Assert.True(Markers.TakePending("gaming", Now.AddMinutes(1)));
        Assert.True(Markers.SetPending("gaming", Now.AddMinutes(2)));
        Assert.False(Markers.TakePending("gaming", Now.AddMinutes(3)));
        Assert.False(Markers.HasPending);
    }

    /// <summary>Reading the installed version fails: the run goes on as without a newer copy.</summary>
    [Fact]
    public void AFailedLookForANewerCopyInstallsAsUsual()
    {
        var outcome = RunWith(new FirstSignInFile("gaming", "en", "System"), Steps(newerFails: new IOException("registry unreadable")));
        Assert.Equal(FirstSignInOutcome.Installed, outcome);
        Assert.Equal(1, _installs);
        Assert.Equal(Installed, Assert.Single(_started).FileName);
    }

    /// <summary>
    /// A newer copy that users could change or that is not signed by Tunero's publisher is not started elevated: the
    /// normal install runs, with its own checks.
    /// </summary>
    [Fact]
    public void AnUntrustedNewerCopyIsNotStarted()
    {
        const string elsewhere = @"C:\Tools\Tunero.exe";
        var outcome = RunWith(new FirstSignInFile("gaming", "en", "System"), Steps(newerInstalled: elsewhere, newerTrusted: false));
        Assert.Equal(FirstSignInOutcome.Installed, outcome);
        Assert.Equal(1, _installs);
        Assert.DoesNotContain(_started, psi => psi.FileName == elsewhere);
    }

    /// <summary>The setup folder of a kept newer copy goes too, although its exe differs from the installed one; the result says it is gone.</summary>
    [Fact]
    public void FinishRemovesAnOlderSetupExe()
    {
        var setup = Path.Combine(_folder, "Setup", "Tunero");
        Directory.CreateDirectory(setup);
        var old = Path.Combine(setup, AppInstall.ExeName);
        File.WriteAllText(old, "older exe");
        var installed = Path.Combine(_folder, "installed.exe");
        File.WriteAllText(installed, "newer exe");

        Assert.True(InstallService.RemoveOldCopy(old, installed, setup));
        Assert.False(Directory.Exists(setup));
        Assert.True(File.Exists(installed));
    }

    /// <summary>The installed copy's first start removes the setup folder (here a temp stand-in) with the downloaded exe and its file.</summary>
    [Fact]
    public void FinishDeletesTheSetupFolder()
    {
        var setup = Path.Combine(_folder, "Setup", "Tunero");
        Directory.CreateDirectory(setup);
        var old = Path.Combine(setup, AppInstall.ExeName);
        File.WriteAllText(old, "exe");
        File.WriteAllText(Path.Combine(setup, FirstSignIn.FileName), "{}");
        var installed = Path.Combine(_folder, "installed.exe");
        File.WriteAllText(installed, "exe");

        Assert.True(InstallService.RemoveOldCopy(old, installed, setup));
        Assert.False(Directory.Exists(setup));
        Assert.True(File.Exists(installed));
    }

    /// <summary>A downloaded exe outside the setup folder: only that exe goes, the folder stays.</summary>
    [Fact]
    public void FinishKeepsOtherFolders()
    {
        var setup = Path.Combine(_folder, "Setup", "Tunero");
        Directory.CreateDirectory(setup);
        File.WriteAllText(Path.Combine(setup, FirstSignIn.FileName), "{}");
        var downloads = Path.Combine(_folder, "Downloads");
        Directory.CreateDirectory(downloads);
        var old = Path.Combine(downloads, AppInstall.ExeName);
        File.WriteAllText(old, "exe");
        File.WriteAllText(Path.Combine(downloads, "other.txt"), "keep");
        var installed = Path.Combine(_folder, "installed.exe");
        File.WriteAllText(installed, "exe");

        Assert.True(InstallService.RemoveOldCopy(old, installed, setup));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(downloads, "other.txt")));
        Assert.True(Directory.Exists(setup));
    }

    /// <summary>The installed copy holds the session's one window; the start in the setup folder never takes or waits for it.</summary>
    [Fact]
    public void TheInstalledCopyKeepsToOneWindow()
    {
        string[] installed = [InstallService.InstalledFrom, @"C:\Windows\Setup\Tunero\Tunero.exe", FirstSignInStart.ApplySwitch, "gaming"];
        Assert.True(SingleInstance.Applies(installed));
        Assert.True(SingleInstance.WaitsForOther(installed));
        Assert.True(SingleInstance.Applies([FirstSignInStart.ApplySwitch, "gaming"]));
        Assert.False(SingleInstance.Applies([FirstSignIn.Switch]));
    }

    /// <summary>--first-sign-in has no window: a refusal or an error is never a dialog in front of the first desktop.</summary>
    [Fact]
    public void FirstSignInIsACommandLineRun()
    {
        Assert.True(new App.CliArgs([FirstSignIn.Switch]).IsCommandLineRun);
        Assert.False(new App.CliArgs([FirstSignInStart.ApplySwitch, "gaming"]).IsCommandLineRun);
    }
}
