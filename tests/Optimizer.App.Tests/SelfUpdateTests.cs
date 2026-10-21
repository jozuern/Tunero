using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Optimizer.App.Services;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// Update now for the published exe, end to end through the app's own steps: the release check against a fake GitHub
/// answer (asset choice, also the names from before the rename), the download with its SHA-256 file and signature (a
/// throwaway key signs the same "PCOptimizer &lt;version&gt;" statement the release workflow signs), the swap of the exe,
/// the restart with --after-update, and the rollback when the restart fails. Only the network answers and the start of
/// the new exe are fake; every file is in a temp folder of the test's own.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class SelfUpdateTests : IDisposable
{
    private const string Api = "https://api.github.com/repositories/1412188031/releases/latest";
    private const string Download = "https://github.com/jozuern/Tunero/releases/download/v9.0.0/";
    private const string LegacyDownload = "https://github.com/jozuern/PCOptimizer/releases/download/v9.0.0/";

    private static readonly byte[] NewExe = Encoding.ASCII.GetBytes("the new exe of 9.0.0");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tunero-self-update-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly string _updates;
    private readonly FakeShell _shell = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<System.Diagnostics.ProcessStartInfo> _started = [];
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, byte[]> _server = [];

    public SelfUpdateTests()
    {
        Directory.CreateDirectory(_folder);
        _exe = Path.Combine(_folder, "Tunero (1).exe");
        _updates = Path.Combine(_folder, "updates");
        File.WriteAllText(_exe, "the running exe");
    }

    public void Dispose()
    {
        ChangeGate.Instance.Busy = false;
        ChangeGate.Instance.Scanning = false;
        ChangeGate.Instance.Updating = false;
        _key.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>The text the release workflow signs: the app's old name stays in it, so every installed version checks the same.</summary>
    private string Sign(string version, string hash, ECDsa? key = null) =>
        Convert.ToBase64String((key ?? _key).SignData(Encoding.UTF8.GetBytes($"PCOptimizer {version}\n{hash}\n"), HashAlgorithmName.SHA256));

    /// <summary>A release 9.0.0 with the exe, its SHA-256 file and signature under <paramref name="prefix"/> and <paramref name="exeName"/>.</summary>
    private void Publish(string prefix = Download, string exeName = "Tunero.exe", byte[]? served = null, string? hash = null, string? signature = null)
    {
        hash ??= Hash(NewExe);
        _server[Api] = Encoding.UTF8.GetBytes($$"""
            {"tag_name":"v9.0.0","draft":false,"prerelease":false,"assets":[
              {"name":"{{exeName}}","browser_download_url":"{{prefix}}{{exeName}}"},
              {"name":"{{exeName}}.sha256","browser_download_url":"{{prefix}}{{exeName}}.sha256"},
              {"name":"{{exeName}}.sig","browser_download_url":"{{prefix}}{{exeName}}.sig"}]}
            """);
        _server[prefix + exeName] = served ?? NewExe;
        _server[prefix + exeName + ".sha256"] = Encoding.ASCII.GetBytes($"{hash}  {exeName}\n");
        _server[prefix + exeName + ".sig"] = Encoding.ASCII.GetBytes(signature ?? Sign("9.0.0", hash));
    }

    private Launch Recorded(Exception? fails = null) => new()
    {
        Start = psi =>
        {
            if (fails is not null) throw fails;
            _started.Add(psi);
            return 4242;
        },
        OpenAsUser = (_, _, _) => throw new InvalidOperationException("a downloaded copy is never handed to a package manager"),
    };

    /// <summary>The app's update with the real check, download, swap and rollback; the server, the key and the folders are the test's.</summary>
    private UpdateService Service(Launch? launch = null, Func<UpdateSteps, UpdateSteps>? change = null, bool published = true, bool elevated = true)
    {
        var steps = new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates);
        return new UpdateService(new InstallFacts(_exe, published, elevated, () => null, null), _dialogs, _shell, launch ?? Recorded(),
            async () =>
            {
                using var check = new ReleaseCheck(new Server(_server));
                return await check.CheckAsync(new Version(1, 0, 0, 0));
            },
            change?.Invoke(steps) ?? steps);
    }

    private async Task<UpdateService> CheckedAsync(UpdateService update)
    {
        await update.CheckAsync();
        return update;
    }

    private List<(string Text, InfoBarSeverity Severity)> Warnings => _shell.Results.Where(r => r.Severity == InfoBarSeverity.Warning).ToList();

    private void AssertNothingReplaced()
    {
        Assert.Equal("the running exe", File.ReadAllText(_exe));
        Assert.False(File.Exists(_exe + ".old"));
        Assert.False(File.Exists(_exe + ".new"));
        Assert.Empty(_started);
        Assert.False(_shell.ShutDown);
        Assert.False(ChangeGate.Instance.Updating);
    }

    [Fact]
    public async Task TheDefaultStepsAreTheUpdatersOwn()
    {
        var steps = new UpdateSteps();
        Assert.Equal(UpdateSignature.IsConfigured, steps.SignatureConfigured);
        Assert.Equal((Action<string, string>)Updater.Install, steps.Install);
        Assert.Equal((Func<string, string, Action<string>, bool>)Updater.StartVerified, steps.StartVerified);
        Assert.Equal((Func<string, bool>)Updater.RollBack, steps.RollBack);
        // Without assets the default download asks nothing of the network and writes nothing.
        Assert.Equal(UpdateOutcome.NoAssets, (await steps.Download(new ReleaseCheckResult(ReleaseCheckStatus.NewerAvailable, new Version(9, 0, 0)), new Progress<int>())).Outcome);
    }

    [Theory]
    [InlineData(Download, "Tunero.exe")]
    // A release from before the rename: links under jozuern/PCOptimizer and the exe under its old name.
    [InlineData(LegacyDownload, "PCOptimizer.exe")]
    public async Task UpdateNowReplacesTheExeAndStartsTheNewOne(string prefix, string exeName)
    {
        Publish(prefix, exeName);
        var update = await CheckedAsync(Service());
        Assert.True(update.CanSelfUpdate);
        Assert.True(update.CanUpdateInApp);

        await update.UpdateNowAsync();

        Assert.Single(_dialogs.Asked);
        Assert.Equal(NewExe, File.ReadAllBytes(_exe));
        Assert.Equal("the running exe", File.ReadAllText(_exe + ".old"));
        Assert.True(File.Exists(Path.Combine(_updates, "Tunero-9.0.0.exe")));
        var start = Assert.Single(_started);
        Assert.Equal(_exe, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal([SingleInstance.AfterUpdate], start.ArgumentList);
        Assert.True(_shell.ShutDown);
        Assert.Empty(Warnings);
        Assert.False(update.Updating);
        Assert.False(ChangeGate.Instance.Updating);
    }

    [Fact]
    public async Task TheUpdateHoldsTheGateWhileItRuns()
    {
        Publish();
        var updatingDuringDownload = false;
        var update = await CheckedAsync(Service(change: steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates)
        {
            Download = async (release, progress) =>
            {
                updatingDuringDownload = ChangeGate.Instance.Updating;
                return await steps.Download(release, progress);
            },
        }));
        await update.UpdateNowAsync();
        Assert.True(updatingDuringDownload);
        Assert.False(ChangeGate.Instance.Updating);
        Assert.True(_shell.ShutDown);
    }

    [Fact]
    public async Task AReleaseSignedWithAnotherKeyIsNotInstalled()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(signature: Sign("9.0.0", Hash(NewExe), other));
        await (await CheckedAsync(Service())).UpdateNowAsync();
        Assert.Equal(Loc.Instance["Update_BadSignature"], Assert.Single(Warnings).Text);
        AssertNothingReplaced();
        Assert.False(Directory.Exists(_updates) && Directory.EnumerateFiles(_updates).Any());
    }

    /// <summary>A signature for another version (an older release's files replayed) does not count.</summary>
    [Fact]
    public async Task ASignatureForAnotherVersionIsNotAccepted()
    {
        Publish(signature: Sign("8.0.0", Hash(NewExe)));
        await (await CheckedAsync(Service())).UpdateNowAsync();
        Assert.Equal(Loc.Instance["Update_BadSignature"], Assert.Single(Warnings).Text);
        AssertNothingReplaced();
    }

    [Fact]
    public async Task AnExeWithAnotherHashIsNotInstalled()
    {
        Publish(served: Encoding.ASCII.GetBytes("a changed exe"));
        await (await CheckedAsync(Service())).UpdateNowAsync();
        Assert.Equal(Loc.Instance["Update_BadChecksum"], Assert.Single(Warnings).Text);
        AssertNothingReplaced();
        Assert.Empty(Directory.EnumerateFiles(_updates));
    }

    [Fact]
    public async Task AFailedDownloadChangesNothing()
    {
        Publish();
        _server.Remove(Download + "Tunero.exe");
        await (await CheckedAsync(Service())).UpdateNowAsync();
        Assert.Equal(Loc.Instance["Update_Failed"], Assert.Single(Warnings).Text);
        AssertNothingReplaced();
    }

    [Fact]
    public async Task NoToTheQuestionDownloadsNothing()
    {
        Publish();
        _dialogs.Answer = false;
        var downloads = 0;
        var update = await CheckedAsync(Service(change: steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates)
        {
            Download = (release, progress) =>
            {
                downloads++;
                return steps.Download(release, progress);
            },
        }));
        await update.UpdateNowAsync();
        Assert.Single(_dialogs.Asked);
        Assert.Equal(0, downloads);
        AssertNothingReplaced();
    }

    /// <summary>A change that started while the download ran: the exe is not replaced in the middle of it.</summary>
    [Fact]
    public async Task AChangeStartedDuringTheDownloadKeepsTheExe()
    {
        Publish();
        var update = await CheckedAsync(Service(change: steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates)
        {
            Download = async (release, progress) =>
            {
                var result = await steps.Download(release, progress);
                ChangeGate.Instance.Busy = true;
                return result;
            },
        }));
        await update.UpdateNowAsync();
        Assert.Contains(_shell.Results, r => r.Text == Loc.Instance["Update_Busy"]);
        AssertNothingReplaced();
    }

    [Fact]
    public async Task AnExeThatCannotBeReplacedStays()
    {
        Publish();
        var update = await CheckedAsync(Service(change: steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates)
        {
            Install = (_, _) => throw new IOException("in use"),
        }));
        await update.UpdateNowAsync();
        Assert.Equal(Loc.Instance.Format("Update_CannotReplace", "in use"), Assert.Single(Warnings).Text);
        AssertNothingReplaced();
    }

    /// <summary>Antivirus took the new exe or blocked its start: the previous version goes back in its place.</summary>
    [Fact]
    public async Task AFailedRestartPutsThePreviousExeBack()
    {
        Publish();
        await (await CheckedAsync(Service(Recorded(new System.ComponentModel.Win32Exception(5))))).UpdateNowAsync();
        Assert.Equal(Loc.Instance.Format("Update_RestartFailedRolledBack", "9.0.0"), Assert.Single(Warnings).Text);
        AssertNothingReplaced();
    }

    [Fact]
    public async Task AFailedRollBackSaysSo()
    {
        Publish();
        var update = await CheckedAsync(Service(Recorded(new System.ComponentModel.Win32Exception(5)),
            steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates) { RollBack = _ => false }));
        await update.UpdateNowAsync();
        Assert.Equal(Loc.Instance.Format("Update_RestartFailed", "9.0.0"), Assert.Single(Warnings).Text);
        Assert.False(_shell.ShutDown);
    }

    /// <summary>The installed exe changed between the swap and the restart: it is not started, and the previous one goes back.</summary>
    [Fact]
    public async Task AnExeChangedBeforeTheRestartIsNotStarted()
    {
        Publish();
        var update = await CheckedAsync(Service(change: steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates)
        {
            Install = (downloaded, running) =>
            {
                Updater.Install(downloaded, running);
                File.WriteAllText(running, "swapped by someone else");
            },
        }));
        await update.UpdateNowAsync();
        Assert.Equal(Loc.Instance["Update_BadChecksum"], Assert.Single(Warnings).Text);
        AssertNothingReplaced();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task OnlyThePublishedElevatedExeWithAKeyReplacesItself(bool published, bool elevated, bool key)
    {
        Publish();
        var update = await CheckedAsync(Service(published: published, elevated: elevated,
            change: steps => new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates) { SignatureConfigured = key }));
        Assert.True(update.UpdateAvailable);
        Assert.False(update.CanSelfUpdate);
        await update.UpdateNowAsync();
        Assert.Equal([ReleaseCheck.LatestReleaseUrl], _shell.Links);
        Assert.Empty(_dialogs.Asked);
        AssertNothingReplaced();
    }

    /// <summary>A release without a signature file offers no self-update: the release page opens instead.</summary>
    [Fact]
    public async Task AReleaseWithoutSignatureOpensTheReleasePage()
    {
        Publish();
        _server[Api] = Encoding.UTF8.GetBytes($$"""
            {"tag_name":"v9.0.0","assets":[
              {"name":"Tunero.exe","browser_download_url":"{{Download}}Tunero.exe"},
              {"name":"Tunero.exe.sha256","browser_download_url":"{{Download}}Tunero.exe.sha256"}]}
            """);
        var update = await CheckedAsync(Service());
        Assert.False(update.CanSelfUpdate);
        await update.UpdateNowAsync();
        Assert.Equal([ReleaseCheck.LatestReleaseUrl], _shell.Links);
        AssertNothingReplaced();
    }

    [Fact]
    public async Task AnUpToDateCopyOffersNothing()
    {
        Publish();
        var update = new UpdateService(new InstallFacts(_exe, true, true, () => null, null), _dialogs, _shell, Recorded(), async () =>
        {
            using var check = new ReleaseCheck(new Server(_server));
            return await check.CheckAsync(new Version(9, 0, 0, 0));
        }, new UpdateSteps(new Server(_server), _key.ExportSubjectPublicKeyInfoPem(), _updates));
        await update.CheckAsync();
        Assert.Equal(ReleaseCheckStatus.UpToDate, update.Release!.Status);
        Assert.False(update.UpdateAvailable);
        Assert.False(update.CanUpdateInApp);
    }

    /// <summary>GitHub's answers by URL; anything else is 404. Nothing leaves the test.</summary>
    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
