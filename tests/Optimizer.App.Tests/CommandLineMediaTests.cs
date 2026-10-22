using System.IO;
using System.Text.Json.Nodes;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Logging;
using Optimizer.Core.Media;

namespace Optimizer.App.Tests;

/// <summary>
/// --build-media without the window: a fake catalog, a build that records the request (or fails as told), fixed keyboards
/// and a temp folder. Nothing downloads, attaches an ISO, runs DISM or cleans up real drives.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class CommandLineMediaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunero-media-cli-" + Guid.NewGuid().ToString("N"));
    private readonly List<MediaRequest> _requests = [];
    private readonly StringWriter _output = new();
    private int _cleanups;

    /// <summary>What the fake build does; null: reports progress, records the request and returns its output path.</summary>
    private Func<MediaRequest, IProgress<MediaProgress>, Task<string>>? _build;

    /// <summary>What the fake catalog does; null: returns <see cref="Files"/>.</summary>
    private Func<IReadOnlyList<EsdFile>>? _catalog;
    private int _catalogLoads;

    public CommandLineMediaTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyList<EsdFile> Files =
    [
        Esd("en-us", "English (United States)", "Professional"), Esd("de-de", "German (Germany)", "Professional"), Esd("de-de", "German (Germany)", "Core"),
    ];

    private static EsdFile Esd(string code, string language, string edition) =>
        new($"26300.9711.261011-1601.26h2_ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_{code}.esd", code, language, edition, "x64",
            6L << 30, new string('a', 64), new Uri($"http://dl.delivery.mp.microsoft.com/filestreamingservice/files/{code}.esd"));

    private static readonly IReadOnlyList<InputProfile> Keyboards =
    [
        new("0407:00000407", "German", "de-DE"), new("0C07:00000407", "German", "de-AT"), new("0409:00000409", "US", "en-US"),
    ];

    private sealed class FakeCatalog(Func<IReadOnlyList<EsdFile>> load) : IMediaCatalogSource
    {
        public Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct)
        {
            p?.Report("search");
            return Task.FromResult(load());
        }
    }

    private string Exe()
    {
        var exe = Path.Combine(_root, "Tunero.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "MZ");
        return exe;
    }

    private string Output => Path.Combine(_root, "out.iso");

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private MediaCommandLineSystem System(bool elevated = true, bool published = true) => new()
    {
        Elevated = elevated,
        TuneroExe = published ? Exe() : null,
        Theme = "System",
        Catalog = new FakeCatalog(() =>
        {
            _catalogLoads++;
            return _catalog?.Invoke() ?? Files;
        }),
        Build = async (request, progress, ct) =>
        {
            _requests.Add(request);
            if (_build is not null) return new MediaBuildResult(await _build(request, progress), 1234, Hash);
            for (var percent = 0; percent <= 100; percent++) progress.Report(new MediaProgress(MediaStage.Download, percent, null));
            progress.Report(new MediaProgress(MediaStage.Iso, null, null));
            return new MediaBuildResult(request.OutputIso, 1234, Hash);
        },
        Keyboards = () => Keyboards,
        CleanLeftovers = _ =>
        {
            _cleanups++;
            return Task.CompletedTask;
        },
        Output = _output,
    };

    private JsonObject Request() => new()
    {
        ["source"] = "download",
        ["language"] = "de-de",
        ["edition"] = "Professional",
        ["removeApps"] = "consumer",
        ["skipSetup"] = new JsonObject { ["accountName"] = "Anna", ["region"] = "de-AT", ["keyboard"] = "0C07:00000407" },
        ["profile"] = "office",
        ["allowUnsupported"] = true,
        ["output"] = Output,
    };

    private string Write(JsonNode request)
    {
        var path = Path.Combine(_root, $"request-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, request.ToJsonString());
        return path;
    }

    private string LogPath => Path.Combine(_root, "result.txt");

    private Task<int> Run(JsonNode request, MediaCommandLineSystem? system = null) =>
        CommandLineMedia.BuildAsync(Write(request), LogPath, system ?? System());

    [Fact]
    public async Task TheRequestIsThePagesForTheSameChoices()
    {
        Assert.Equal(CommandLineApply.Done, await Run(Request()));
        var fromFile = Assert.Single(_requests);
        _requests.Clear();

        // The page with the same choices: German download, Pro, the default apps, setup skipped, office, unsupported PCs.
        var page = new InstallMediaViewModel(new FakePageHost(), new FakeDialogs { ConfirmApplyResult = new ConfirmResult(false) }, new FakeCatalog(() => Files),
            (request, _, _) =>
            {
                _requests.Add(request);
                return Task.FromResult(new MediaBuildResult(request.OutputIso, 1, Hash));
            },
            new InstallMediaSystem
            {
                Elevated = true, ExePath = Exe(), IsPublishedExe = true, ProfileId = () => "gaming", Theme = () => "System",
                FreeBytes = _ => long.MaxValue, ReadPc = () => new ThisPcFacts("Core", false, "Bert", "en-US", _root),
                Keyboards = () => Keyboards,
            });
        await page.EnsureLoadedAsync();
        await page.CheckCatalogCommand.ExecuteAsync(null);
        page.Language = page.Languages.Single(l => l.Key == "de-de");
        page.Edition = page.Editions.Single(e => e.Key == "Professional");
        page.SkipSetup = true;
        page.AccountNameText = "Anna";
        page.Region = page.Regions.Single(r => r.Key == "de-AT");
        page.Keyboard = page.Keyboards.Single(k => k.Key == "0C07:00000407");
        page.ApplyProfile = true;
        page.Profile = page.Profiles.Single(p => p.Key == "office");
        page.AllowUnsupported = true;
        page.OutputPath = Output;
        await page.BuildCommand.ExecuteAsync(null);
        var fromPage = Assert.Single(_requests);

        Assert.Equal(fromPage.Source, fromFile.Source);
        Assert.Equal(fromPage.Edition, fromFile.Edition);
        Assert.Equal(fromPage.IsoIndex, fromFile.IsoIndex);
        Assert.Equal(fromPage.RemoveApps.Order(), fromFile.RemoveApps.Order());
        Assert.Equal(fromPage.Answers, fromFile.Answers);
        Assert.Equal(fromPage.TuneroExe, fromFile.TuneroExe);
        Assert.Equal(fromPage.FirstSignIn, fromFile.FirstSignIn);
        Assert.Equal(fromPage.OutputIso, fromFile.OutputIso);
    }

    [Fact]
    public async Task ProgressIsWrittenEveryFivePercent()
    {
        Assert.Equal(CommandLineApply.Done, await Run(Request()));
        var lines = _output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var download = lines.Where(l => l.Contains("Download ") && l.EndsWith('%')).ToList();
        Assert.Equal(21, download.Count);
        Assert.Contains(lines, l => l.EndsWith("Download 5%"));
        Assert.DoesNotContain(lines, l => l.EndsWith("Download 6%"));
        Assert.Contains(lines, l => l.Contains("Iso started"));
        Assert.Contains(lines, l => l.Contains("Done: " + Output));
        // The hash follows the path, so a VM script can compare it with the file it copies out.
        var done = Array.FindIndex(lines, l => l.Contains("Done: " + Output));
        Assert.EndsWith("SHA-256 " + Hash, lines[done + 1]);
        var log = await File.ReadAllTextAsync(LogPath);
        Assert.Contains("Download 100%", log);
        Assert.Contains(Loc.Instance.Format("Cli_ExitCode", 0), log);
        Assert.Equal(1, _cleanups);
    }

    [Fact]
    public async Task WithoutAdministratorRightsNothingRuns()
    {
        Assert.Equal(CommandLineApply.NotElevated, await Run(Request(), System(elevated: false)));
        Assert.Empty(_requests);
        Assert.Equal(0, _cleanups);
        Assert.Contains(Loc.Instance["Cli_NotElevated"], _output.ToString());
    }

    [Fact]
    public async Task AnInvalidRequestNamesTheFieldAndBuildsNothing()
    {
        var request = Request();
        request["edition"] = "Enterprise";
        Assert.Equal(CommandLineApply.InvalidFile, await Run(request));
        Assert.Empty(_requests);
        Assert.Equal(0, _cleanups);
        Assert.Contains("edition: ", _output.ToString());
        Assert.Contains("edition: ", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task AMissingRequestFileIsInvalid()
    {
        Assert.Equal(CommandLineApply.InvalidFile, await CommandLineMedia.BuildAsync(Path.Combine(_root, "none.json"), null, System()));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task AnImageTheCatalogLacksIsInvalid()
    {
        var request = Request();
        request["language"] = "fr-fr";
        Assert.Equal(CommandLineApply.InvalidFile, await Run(request));
        Assert.Empty(_requests);
        Assert.Contains("language: ", _output.ToString());
    }

    [Fact]
    public async Task AProfileNeedsThePublishedExe()
    {
        Assert.Equal(CommandLineApply.InvalidFile, await Run(Request(), System(published: false)));
        Assert.Empty(_requests);
        Assert.Contains("profile: ", _output.ToString());
    }

    [Fact]
    public async Task AFailedBuildNamesStageKeyAndLog()
    {
        _build = (_, _) => throw new MediaBuildException(MediaStage.Apps, @"DISM failed. Log: C:\logs\media.log", unchecked((int)0x800F081F), "Media_ImageCheckFailed")
        {
            LogPath = @"C:\logs\media.log",
        };
        Assert.Equal(CommandLineApply.MediaBuildFailed, await Run(Request()));
        var log = await File.ReadAllTextAsync(LogPath);
        Assert.Contains("Apps", log);
        Assert.Contains("Media_ImageCheckFailed", log);
        Assert.Contains("0x800F081F", log);
        Assert.Contains(@"C:\logs\media.log", log);
        Assert.Contains(Loc.Instance.Format("Cli_ExitCode", CommandLineApply.MediaBuildFailed), log);
    }

    [Fact]
    public async Task AFailedCatalogIsAFailedBuild()
    {
        _catalog = () => throw new MediaDownloadException("cab", "products.cab is not signed by Microsoft.");
        Assert.Equal(CommandLineApply.MediaBuildFailed, await Run(Request()));
        Assert.Empty(_requests);
        Assert.Contains("Catalog", _output.ToString());
        Assert.Contains("cab", _output.ToString());
    }

    [Fact]
    public async Task ARequestTheEngineRefusesIsInvalid()
    {
        // The engine's own check (MediaBuild.Validate) runs before the build: here the Tunero copy is missing.
        var system = System() with { TuneroExe = Path.Combine(_root, "gone", "Tunero.exe") };
        Assert.Equal(CommandLineApply.InvalidFile, await Run(Request(), system));
        Assert.Empty(_requests);
        Assert.Contains("The Tunero exe must be an existing file", _output.ToString());
    }

    [Fact]
    public async Task AnArgumentExceptionInsideTheBuildIsUnexpected()
    {
        _build = (_, _) => throw new ArgumentException("deep inside the build");
        Assert.Equal(CommandLineApply.Error, await Run(Request()));
        Assert.Contains("deep inside the build", _output.ToString());
        Assert.Contains(Log.Snapshot(Log.MaxEntries), e => e.Level == LogLevel.Error && e.Source == "cli" && e.Message == "build media failed");
    }

    [Fact]
    public async Task AnOversizedRequestFileIsNotRead()
    {
        var path = Path.Combine(_root, "big.json");
        await File.WriteAllTextAsync(path, Request().ToJsonString() + new string(' ', 64 * 1024));
        Assert.Equal(CommandLineApply.InvalidFile, await CommandLineMedia.BuildAsync(path, null, System()));
        Assert.Empty(_requests);
        Assert.Equal(0, _cleanups);
        Assert.Contains("larger than", _output.ToString());
    }

    [Theory]
    [InlineData(@"\\server\share\out.iso")]
    [InlineData(@"\\?\C:\out.iso")]
    public async Task AnOutputOffTheLocalDrivesIsRefusedBeforeCleanupAndNetwork(string output)
    {
        var request = Request();
        request["output"] = output;
        Assert.Equal(CommandLineApply.InvalidFile, await Run(request));
        Assert.Equal((0, 0), (_cleanups, _catalogLoads));
        Assert.Contains("output: ", _output.ToString());
        Assert.Contains("local fixed drive", _output.ToString());
    }

    [Fact]
    public async Task AppLanguageAndThemeReachTheFirstSignIn()
    {
        var request = Request();
        request["appLanguage"] = "de";
        request["theme"] = "Dark";
        Assert.Equal(CommandLineApply.Done, await Run(request));
        Assert.Equal(new FirstSignInFile("office", "de", "Dark"), Assert.Single(_requests).FirstSignIn);
    }

    [Fact]
    public async Task AnUnexpectedErrorEndsTheRun()
    {
        _build = (_, _) => throw new InvalidOperationException("boom");
        Assert.Equal(CommandLineApply.Error, await Run(Request()));
        Assert.Contains("boom", _output.ToString());
    }

    [Fact]
    public void TheSwitchIsACommandLineRun()
    {
        Assert.True(new App.CliArgs(["--build-media", "request.json"]).IsCommandLineRun);
        Assert.True(new App.CliArgs(["--build-media", "request.json", "--log", "x.txt"]).IsCommandLineRun);
        // Without a file, as --apply: the window opens.
        Assert.False(new App.CliArgs(["--build-media"]).IsCommandLineRun);
    }
}
