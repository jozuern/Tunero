using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>
/// The catalog the install media page loads, over fakes: the search and the cab come from a fake handler, expand.exe is a
/// fake that writes the sample products.xml, the work folder is a temp folder. Nothing goes to the network or runs a tool.
/// </summary>
public sealed class MediaCatalogSourceTests : IDisposable
{
    private const string CabUrl = "http://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/catalog";
    private readonly string _root = TestFolders.Create("mediacatalog");
    private readonly FakeMediaSteps _steps;
    private readonly FakeProcesses _processes = new();
    private readonly byte[] _cab = Encoding.ASCII.GetBytes("MSCF fake products.cab");

    public MediaCatalogSourceTests()
    {
        _steps = new FakeMediaSteps(_root);
        // expand.exe "<cab>" -F:* "<dir>": writes the one file the real cab holds.
        _processes.Handler = (file, args) =>
        {
            if (file != "expand.exe") return null;
            var target = args.Split('"')[3];
            File.Copy(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", "products-sample.xml"), Path.Combine(target, "products.cab"));
            return (0, "");
        };
    }

    public void Dispose() => TestFolders.Delete(_root);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private Handler Microsoft(bool cabTimesOut = false) => new(request => request.Method == HttpMethod.Post
        ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""[{"FileLocations":[{"FileName":"products.cab","Size":{{_cab.Length}},"Digest":"{{Convert.ToBase64String(SHA256.HashData(_cab))}}","Url":"{{CabUrl}}"}]}]"""),
        }
        : cabTimesOut ? throw new TaskCanceledException("timeout") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_cab) });

    private MediaCatalogSource Source(Handler handler, bool signed = true) => new(_steps, _processes, _root, handler, _ => signed);

    [Fact]
    public async Task LoadsTheCatalogAndDeletesTheWorkFolder()
    {
        var steps = new List<string>();
        var files = await Source(Microsoft()).LoadAsync("amd64", new SyncProgress<string>(steps.Add), CancellationToken.None);
        Assert.Equal(9, files.Count);
        Assert.Equal(["search", "cab", "unpack"], steps);
        Assert.Single(_processes.Calls, c => c.StartsWith("expand.exe \"" + _steps.WorkFolder, StringComparison.OrdinalIgnoreCase) && c.Contains(" -F:* "));
        Assert.Contains("DeleteWorkFolder", _steps.Calls);
        Assert.False(Directory.Exists(_steps.WorkFolder));
    }

    [Fact]
    public async Task ACabWithoutMicrosoftsSignatureIsRefused()
    {
        var ex = await Assert.ThrowsAsync<MediaDownloadException>(() => Source(Microsoft(), signed: false).LoadAsync("amd64", null, CancellationToken.None));
        Assert.Equal("cab", ex.Step);
        Assert.Empty(_processes.Calls);
        Assert.False(Directory.Exists(_steps.WorkFolder));
    }

    [Fact]
    public async Task AFailedUnpackNamesTheStep()
    {
        _processes.Handler = (_, _) => (1, "Can't open input file");
        var ex = await Assert.ThrowsAsync<MediaDownloadException>(() => Source(Microsoft()).LoadAsync("amd64", null, CancellationToken.None));
        Assert.Equal("unpack", ex.Step);
        Assert.False(Directory.Exists(_steps.WorkFolder));
    }

    /// <summary>A disk error while the cab is checked is a failure of the cab step, not of unpacking.</summary>
    [Fact]
    public async Task AnIoErrorInTheCabStepNamesTheCabStep()
    {
        var source = new MediaCatalogSource(_steps, _processes, _root, Microsoft(), _ => throw new IOException("disk"));
        var ex = await Assert.ThrowsAsync<MediaDownloadException>(() => source.LoadAsync("amd64", null, CancellationToken.None));
        Assert.Equal("cab", ex.Step);
        Assert.False(Directory.Exists(_steps.WorkFolder));
    }

    /// <summary>A request that times out (HttpClient's TaskCanceledException, not a Stop) is a failed search.</summary>
    [Fact]
    public async Task ATimeoutIsAFailedSearch()
    {
        var handler = new Handler(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
        var ex = await Assert.ThrowsAsync<MediaDownloadException>(() => Source(handler).LoadAsync("amd64", null, CancellationToken.None));
        Assert.Equal("search", ex.Step);
        Assert.Null(_steps.WorkFolder);
    }

    /// <summary>The same while the cab downloads: the cab step fails and the work folder goes.</summary>
    [Fact]
    public async Task ATimeoutOfTheCabIsAFailedCabStep()
    {
        var ex = await Assert.ThrowsAsync<MediaDownloadException>(() => Source(Microsoft(cabTimesOut: true)).LoadAsync("amd64", null, CancellationToken.None));
        Assert.Equal("cab", ex.Step);
        Assert.False(Directory.Exists(_steps.WorkFolder));
    }

    [Fact]
    public async Task ASearchWithoutTheCabFails()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var ex = await Assert.ThrowsAsync<MediaDownloadException>(() => Source(handler).LoadAsync("amd64", null, CancellationToken.None));
        Assert.Equal("search", ex.Step);
        Assert.Single(handler.Requests);
        Assert.Null(_steps.WorkFolder);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>The regions and keyboards the page offers come from Windows' own lists.</summary>
public sealed class MediaLocalesTests
{
    /// <summary>
    /// Windows' keyboards to install per locale (LOCALE_SKEYBOARDSTOINSTALL), once each, named by the layout's name and the
    /// language; an input method (IME) by its language.
    /// </summary>
    [Fact]
    public void KeyboardsComeFromTheLocalesLists()
    {
        (string, string?)[] lists =
        [
            ("de-DE", "0407:00000407"), ("de-AT", "0c07:00000407"), ("de-CH", "0807:00000807;100C:0000100C"),
            ("en-US", "0409:00000409"), ("xx-XX", null),
            ("ja-JP", "0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}"),
        ];
        var names = new Dictionary<string, string> { ["00000407"] = "German", ["00000807"] = "Swiss German", ["0000100C"] = "Swiss French", ["00000409"] = "US" };
        var keyboards = MediaLocales.ProfilesFrom(lists, klid => names.GetValueOrDefault(klid));
        Assert.Equal(
            ["0407:00000407", "0C07:00000407", "0807:00000807", "100C:0000100C", "0409:00000409", "0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}"],
            keyboards.Select(k => k.Code));
        Assert.Equal(("German", "de-AT"), (keyboards[1].Keyboard, keyboards[1].LanguageTag));
        Assert.Equal("ja-JP", keyboards[^1].LanguageTag);
        Assert.False(string.IsNullOrEmpty(keyboards[^1].Keyboard));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void KeyboardsOfThisWindows()
    {
        var keyboards = MediaLocales.Keyboards;
        Assert.True(keyboards.Count > 100);
        Assert.Contains(keyboards, k => k.Code == "0407:00000407");
        Assert.Equal(keyboards.Count, keyboards.Select(k => k.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keyboards, k => Assert.Matches(@"^[0-9A-F]{4}:([0-9A-F]{8}|\{[0-9A-F-]{36}\}\{[0-9A-F-]{36}\})$", k.Code));
        Assert.All(keyboards, k => Assert.False(string.IsNullOrWhiteSpace(k.Keyboard)));
    }

    [Fact]
    public void RegionsHaveDefaultKeyboards()
    {
        Assert.Equal("0C07:00000407", MediaLocales.DefaultKeyboard(new CultureInfo("de-AT")));
        Assert.Equal("0809:00000809", MediaLocales.DefaultKeyboard(new CultureInfo("en-GB")));
        Assert.Contains(MediaLocales.Regions(), c => c.Name == "de-CH");
        Assert.DoesNotContain(MediaLocales.Regions(), c => c.LCID == 0x1000);
    }

    [Fact]
    public void KeyboardOfALayoutHandle()
    {
        Assert.Equal("0407:00000407", MediaLocales.KeyboardOf(0x04070407));
        Assert.Equal("0409:00000409", MediaLocales.KeyboardOf(0x04090409));
        // A variant layout (high word F0xx) is named only through the registry: no guess.
        Assert.Null(MediaLocales.KeyboardOf(unchecked((long)0xF0010409)));
    }

    [Fact]
    public void UnicodeOnlyRegionsKeepAnAnsiSystemLocale()
    {
        Assert.Equal("de-DE", MediaLocales.SystemLocale("de-DE"));
        // Hindi has no code page for programs without Unicode.
        Assert.Equal("en-US", MediaLocales.SystemLocale("hi-IN"));
    }
}
