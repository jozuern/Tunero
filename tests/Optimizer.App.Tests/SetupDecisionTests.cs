using Optimizer.App.Services;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;

namespace Optimizer.App.Tests;

/// <summary>
/// What the start shows over the first results: the setup screen, the Start menu question, or neither. The checks (folder
/// rights, a newer installed copy, the signature) are the test's answers; nothing reads this PC's registry or files.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class SetupDecisionTests : IDisposable
{
    private const string Downloaded = @"C:\Users\Test\Downloads\Tunero.exe";
    private const string Installed = @"C:\Program Files\Tunero\Tunero.exe";

    private readonly AppSettings _settings = new();
    private readonly List<string> _refreshed = [];
    private readonly List<string> _checked = [];

    public void Dispose() => ChangeGate.Instance.Busy = false;

    private static readonly PackageInstall Winget = new(PackageManager.Winget, "jozuern.Tunero_Microsoft.Winget.Source_8wekyb3d8bbwe", @"C:\TuneroTest\Packages\jozuern.Tunero", Downloaded);

    private SetupDecision Decision(string? exe = Downloaded, bool published = true, bool elevated = true, PackageInstall? package = null,
        bool adminOnly = false, string? newerInstalled = null, bool trusted = true) =>
        new(new InstallFacts(exe, published, elevated, () => package, null), _settings, new SetupChecks(Installed,
            path =>
            {
                _checked.Add("folder");
                return adminOnly;
            },
            () => newerInstalled,
            path =>
            {
                _checked.Add("signature");
                return trusted;
            },
            path => _refreshed.Add(path)));

    [Fact]
    public async Task ASignedDownloadGetsTheSetupScreen()
    {
        Assert.Equal(new SetupChoice(SetupOffer.SetupScreen, true), await Decision().DecideAsync(plainStart: true));
        Assert.Empty(_refreshed);
    }

    /// <summary>Only the published exe run elevated can install itself: a build or a run without rights gets the Start menu question and no check runs.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ABuildOrARunWithoutRightsGetsTheStartMenuQuestion(bool published, bool elevated)
    {
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, null), await Decision(published: published, elevated: elevated).DecideAsync(true));
        Assert.Empty(_checked);
    }

    [Fact]
    public async Task WithoutAPathTheStartMenuQuestionComes() =>
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, null), await Decision(exe: null).DecideAsync(true));

    /// <summary>The installed copy refreshes its Installed apps entry (the version after an update) and asks only about the Start menu.</summary>
    [Fact]
    public async Task TheInstalledCopyRefreshesItsEntry()
    {
        var decision = Decision(exe: @"C:\PROGRAM FILES\Tunero\Tunero.exe");
        Assert.True(decision.RunsInstalled(@"C:\PROGRAM FILES\Tunero\Tunero.exe"));
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, null), await decision.DecideAsync(true));
        Assert.Equal([@"C:\PROGRAM FILES\Tunero\Tunero.exe"], _refreshed);
        Assert.Empty(_checked);
    }

    [Fact]
    public async Task AStartWithSwitchesShowsNoScreenButSettingsOffersTheInstall() =>
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, true), await Decision().DecideAsync(plainStart: false));

    [Fact]
    public async Task RunFromHereIsNotAskedAgain()
    {
        _settings.SetupDeclined = true;
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, true), await Decision().DecideAsync(true));
    }

    /// <summary>A copy in an admin-only folder is installed already (somewhere else than Program Files): nothing to offer.</summary>
    [Fact]
    public async Task ACopyInAnAdminOnlyFolderIsNotOffered()
    {
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, false), await Decision(adminOnly: true).DecideAsync(true));
        Assert.DoesNotContain("signature", _checked);
    }

    [Fact]
    public async Task APackageManagersCopyIsNotOffered()
    {
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, false), await Decision(package: Winget).DecideAsync(true));
        Assert.DoesNotContain("signature", _checked);
    }

    /// <summary>An older download never replaces a newer installed copy.</summary>
    [Fact]
    public async Task ANewerInstalledCopyIsNotReplaced() =>
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, false), await Decision(newerInstalled: Installed).DecideAsync(true));

    [Fact]
    public async Task AnUnsignedCopyIsNotOffered() =>
        Assert.Equal(new SetupChoice(SetupOffer.StartMenuQuestion, false), await Decision(trusted: false).DecideAsync(true));

    /// <summary>A change that started while the signature was checked: no screen over its progress, and no Start menu question either.</summary>
    [Fact]
    public async Task ARunningChangeDefersTheScreen()
    {
        ChangeGate.Instance.Busy = true;
        Assert.Equal(new SetupChoice(SetupOffer.None, true), await Decision().DecideAsync(true));
        Assert.False(_settings.SetupDeclined);
    }

    [Fact]
    public void TheInstalledCopyIsFoundByItsFullPath()
    {
        Assert.True(SetupChecks.IsInstalledCopy(@"C:\Program Files\Tunero\..\Tunero\Tunero.exe", Installed));
        Assert.False(SetupChecks.IsInstalledCopy(Downloaded, Installed));
        Assert.False(SetupChecks.IsInstalledCopy(null, Installed));
    }
}
