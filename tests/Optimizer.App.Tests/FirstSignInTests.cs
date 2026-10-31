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
    private static PcSnapshot Pc(Facts? facts = null, IReadOnlyList<Optimizer.Core.Findings.Finding>? findings = null) =>
        new(facts ?? new Facts().Set("system.laptop", false), TweakCatalog.Current.Visible.Select(Status).ToList(), new HashSet<string>(), findings ?? []);

    /// <summary>A desktop with integrated graphics and 4 GB: it offers no laptop profile, and Low-end PC fits it best (not Gaming).</summary>
    private static Facts SmallDesktop() => new Facts().Set("system.laptop", false).Set("gpu.hasDiscrete", false).Set("memory.totalGb", 4.0);

    /// <summary>A problem the plan fixes first (power.turboRestore).</summary>
    private static readonly Optimizer.Core.Findings.Finding Turbo = new()
    {
        Id = "F6.turbo", Kind = Optimizer.Core.Findings.FindingKind.Finding, Status = Optimizer.Core.Findings.FindingStatus.Problem, Impact = 3,
    };

    private sealed record PlanIds(string Profile, IReadOnlyList<string> Items, IReadOnlyList<string> Excluded);

    /// <summary>
    /// The Overview's plan, through the window's own code: a scan of the window's state (<see cref="ViewModels.ScanState"/>)
    /// that reads this snapshot, with --profile <paramref name="forced"/> and the saved profile and ignore lists of
    /// <paramref name="settings"/> (a copy: the window saves the profile its first scan picks).
    /// </summary>
    private static PlanIds WindowPlan(PcSnapshot pc, string? forced, AppSettings settings)
    {
        var copy = new AppSettings
        {
            Profile = settings.Profile, IgnoredFindings = [.. settings.IgnoredFindings], IgnoredTweaks = [.. settings.IgnoredTweaks],
        };
        var source = new FakeScanSource { BaseFacts = pc.Facts, FindingFacts = false, States = pc.States, Findings = _ => pc.Findings };
        var state = new ViewModels.ScanState(Optimizer.Core.Catalog.CatalogData.Current, copy, () => { }, source, () => false) { ProfileOverride = forced };
        SingleThread.Run(() => state.ScanAsync());
        Assert.False(state.ScanFailed);
        return new(state.Usage.Id, state.Plan.Items.Select(i => i.Tweak.Id).ToList(), state.Plan.Excluded.Select(i => i.Tweak.Id).ToList());
    }

    /// <summary>--apply-recommended's plan for the same scan and settings (CommandLineVerbs passes them so); null when it stops.</summary>
    private static PlanIds? CommandLinePlan(PcSnapshot pc, string? profile, AppSettings settings) =>
        CommandLineApply.RecommendedPlan(pc with { Ignored = settings.IgnoredFindings.ToList() }, profile, settings.Profile, settings.IgnoredTweaks) is { } plan
            ? new(plan.Profile.Id, plan.Together.Select(t => t.Id).ToList(), plan.InApp.Select(t => t.Id).ToList())
            : null;

    /// <summary>
    /// The window and the command line pick the same profile and the same plan, in the same order, for every way the
    /// profile is chosen (--profile, saved, nothing saved, saved but not offered on this PC) and with ignored results and
    /// recommendations. Either side going its own way fails here.
    /// </summary>
    [Theory]
    [InlineData("gaming", null, false, "gaming")]
    [InlineData("quiet", "office", false, "quiet")]
    [InlineData(null, "office", false, "office")]
    [InlineData(null, "office", true, "office")]
    [InlineData(null, null, false, "lowEnd")]
    [InlineData(null, null, true, "lowEnd")]
    // A laptop profile copied to a desktop and an id a later version dropped: Gaming for the session, not the suggestion.
    [InlineData(null, "battery", false, "gaming")]
    [InlineData(null, "dropped", true, "gaming")]
    public void TheWindowAndTheCommandLinePickTheSamePlan(string? forced, string? saved, bool withIgnored, string expected)
    {
        var pc = Pc(SmallDesktop(), [Turbo]);
        var settings = new AppSettings { Profile = saved };
        if (withIgnored)
        {
            settings.IgnoredFindings.Add(Optimizer.Core.Findings.IgnoredResults.Key(Turbo));
            settings.IgnoredTweaks.Add(WindowPlan(pc, forced, new AppSettings { Profile = saved }).Items.Last());
        }
        var window = WindowPlan(pc, forced, settings);
        var commandLine = CommandLinePlan(pc, forced, settings);
        Assert.NotNull(commandLine);
        Assert.Equal(expected, window.Profile);
        Assert.Equal(window.Profile, commandLine.Profile);
        Assert.NotEmpty(window.Items);
        Assert.Equal(window.Items, commandLine.Items);
        Assert.Equal(window.Excluded, commandLine.Excluded);
        // The fix of the problem comes first, unless the result is ignored (Quiet works against turning the boost back on).
        Assert.Equal(!withIgnored && expected != "quiet", window.Items[0] == "power.turboRestore");
        if (withIgnored) Assert.DoesNotContain(settings.IgnoredTweaks[0], window.Items);
    }

    /// <summary>
    /// --profile with a profile this PC does not offer: the command line stops (exit 9, nothing applied), the window opens
    /// with the profile it would pick without the switch.
    /// </summary>
    [Fact]
    public void AnOfferedProfileIsNeededOnlyOnTheCommandLine()
    {
        var pc = Pc(SmallDesktop());
        var settings = new AppSettings { Profile = "office" };
        Assert.Null(CommandLinePlan(pc, "battery", settings));
        Assert.Equal(WindowPlan(pc, null, settings), WindowPlan(pc, "battery", settings), PlanIdsComparer.Instance);
        Assert.Equal("office", WindowPlan(pc, "battery", settings).Profile);
    }

    private sealed class PlanIdsComparer : IEqualityComparer<PlanIds>
    {
        public static readonly PlanIdsComparer Instance = new();
        public bool Equals(PlanIds? x, PlanIds? y) =>
            x is not null && y is not null && x.Profile == y.Profile && x.Items.SequenceEqual(y.Items) && x.Excluded.SequenceEqual(y.Excluded);
        public int GetHashCode(PlanIds obj) => obj.Profile.GetHashCode(StringComparison.Ordinal);
    }

    private static BatchOutcome AllApplied(IReadOnlyList<TweakDefinition> tweaks) =>
        new(tweaks.Select(t => new BatchItemResult(t, new ApplyResult(ApplyOutcome.Applied, []))).ToList());

    /// <summary>
    /// The window's first sign-in applies exactly what --apply-recommended applies for the profile (the plan the Overview
    /// shows), and both leave out (and the command line lists) the changes that need their own confirmation: previews,
    /// Expert, boot-critical, anti-cheat.
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
        Assert.Equal(WindowPlan(pc, "gaming", new AppSettings()), CommandLinePlan(pc, "gaming", new AppSettings())!, PlanIdsComparer.Instance);
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

    /// <summary>A recommendation ignored in the app is neither applied by --apply-recommended nor listed for the app.</summary>
    [Fact]
    public async Task IgnoredRecommendationIsLeftOut()
    {
        var pc = Pc();
        var all = CommandLineApply.RecommendedPlan(pc, "gaming", null)!;
        var together = all.Together[0].Id;
        var inApp = all.InApp[0].Id;
        var applied = new List<TweakDefinition>();
        var lines = new List<string>();
        var code = await CommandLineApply.ApplyRecommendedPlanAsync(pc, "gaming", null, t =>
        {
            applied.AddRange(t);
            return Task.FromResult(AllApplied(t));
        }, lines, [together, inApp]);
        Assert.Equal(CommandLineApply.Done, code);
        Assert.DoesNotContain(applied, t => t.Id == together);
        Assert.Equal(all.Together.Count - 1, applied.Count);
        var plan = CommandLineApply.RecommendedPlan(pc, "gaming", null, [together, inApp])!;
        Assert.DoesNotContain(plan.InApp, t => t.Id == inApp);
        Assert.Equal(all.InApp.Count - 1, plan.InApp.Count);
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

    /// <summary>The media's apps go with the pending marker to the started copy, which installs them after the profile.</summary>
    [Fact]
    public void TheAppsToInstallTravelWithTheMarker()
    {
        RunWith(new FirstSignInFile("gaming", "en", "System") { Apps = ["Valve.Steam", "Mozilla.Firefox"] });
        Assert.True(Markers.TakePending("gaming", Now.AddMinutes(1), out var apps));
        Assert.Equal(["Valve.Steam", "Mozilla.Firefox"], apps);
    }

    /// <summary>A list the media page could not have made (edited in the data folder) installs nothing; the profile still applies.</summary>
    [Fact]
    public void AppsThePageDoesNotOfferAreNotInstalled()
    {
        Assert.True(Markers.SetPending("gaming", Now, ["Contoso.Tool"]));
        Assert.True(Markers.TakePending("gaming", Now.AddMinutes(1), out var apps));
        Assert.Empty(apps);
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
