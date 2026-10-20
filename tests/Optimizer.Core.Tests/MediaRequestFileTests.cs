using System.Text.Json.Nodes;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>
/// The request file of --build-media and the mapping it shares with the install media page (MediaChoices). Only a temp
/// folder is touched: nothing downloads, attaches, or runs DISM.
/// </summary>
public sealed class MediaRequestFileTests : IDisposable
{
    private readonly string _root = TestFolders.Create("media-request");

    public void Dispose() => TestFolders.Delete(_root);

    private static readonly IReadOnlyList<InputProfile> Keyboards =
    [
        new("0407:00000407", "German", "de-DE"), new("0C07:00000407", "German", "de-AT"), new("0409:00000409", "US", "en-US"),
    ];

    private static readonly IReadOnlyList<EsdFile> Files =
    [
        Esd("26100.4000", "de-de", "Professional"), Esd("26300.9711", "de-de", "Professional"), Esd("26300.9711", "de-de", "Core"),
        Esd("26300.9711", "en-us", "Professional"),
    ];

    private static EsdFile Esd(string build, string code, string edition) =>
        new($"{build}.261011-1601.ge_release_CLIENTCONSUMER_RET_x64FRE_{code}.esd", code, code, edition, "x64",
            6L << 30, new string('a', 64), new Uri($"http://dl.delivery.mp.microsoft.com/files/{build}-{code}-{edition}.esd"));

    private string Output => Path.Combine(_root, "out.iso");

    /// <summary>A complete download request; tests change or remove one field.</summary>
    private JsonObject Download() => new()
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

    private string OwnIso()
    {
        var iso = Path.Combine(_root, "Win11.iso");
        File.WriteAllText(iso, "iso");
        return iso;
    }

    private static MediaRequestFile Parse(JsonNode json) => MediaRequestFile.Parse(json.ToJsonString(), Keyboards);

    private static MediaRequestFileException Refused(JsonNode json) => Assert.Throws<MediaRequestFileException>(() => Parse(json));

    [Fact]
    public void ADownloadRequestIsRead()
    {
        var file = Parse(Download());
        Assert.True(file.IsDownload);
        Assert.Equal("de-de", file.Language);
        Assert.Equal("Professional", file.Edition);
        Assert.Equal(new MediaSetupChoice("Anna", "de-AT", "0C07:00000407"), file.SkipSetup);
        Assert.Equal("office", file.Profile);
        Assert.True(file.AllowUnsupported);
        Assert.Equal(Output, file.Output);
    }

    [Fact]
    public void ConsumerIsThePagesDefaultSet()
    {
        var appx = CatalogData.Current.Appx;
        var expected = MediaChoices.OfferedApps(appx).Where(MediaChoices.ChosenByDefault).Select(a => a.Name).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Parse(Download()).RemoveApps);
        Assert.All(expected, name => Assert.False(appx.IsProtected(name)));
        Assert.All(expected, name => Assert.Equal("consumer", appx.Apps.First(a => a.Name == name).Group));
    }

    [Fact]
    public void AppsAreNamedOneByOne()
    {
        var json = Download();
        var offered = MediaChoices.OfferedApps(CatalogData.Current.Appx).Select(a => a.Name).Take(2).ToList();
        json["removeApps"] = new JsonArray(offered[0], offered[1].ToUpperInvariant(), offered[0]);
        Assert.Equal(offered, Parse(json).RemoveApps);
        json["removeApps"] = new JsonArray();
        Assert.Empty(Parse(json).RemoveApps);
    }

    [Fact]
    public void AnOwnIsoRequestIsRead()
    {
        var json = Download();
        json["source"] = OwnIso();
        json.Remove("language");
        json["edition"] = "ProfessionalWorkstation";
        json["skipSetup"] = null;
        json["profile"] = null;
        var file = Parse(json);
        Assert.False(file.IsDownload);
        Assert.Null(file.Language);
        Assert.Null(file.SkipSetup);
        Assert.Null(file.Profile);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("language")]
    [InlineData("edition")]
    [InlineData("removeApps")]
    [InlineData("skipSetup")]
    [InlineData("profile")]
    [InlineData("allowUnsupported")]
    [InlineData("output")]
    public void AMissingFieldIsRefused(string field)
    {
        var json = Download();
        json.Remove(field);
        var refused = Refused(json);
        Assert.Equal(field, refused.Field);
        Assert.StartsWith(field + ": ", refused.Message);
        Assert.Contains("missing", refused.Message);
    }

    [Theory]
    [InlineData("accountName")]
    [InlineData("region")]
    [InlineData("keyboard")]
    public void AMissingSetupFieldIsRefused(string field)
    {
        var json = Download();
        json["skipSetup"]!.AsObject().Remove(field);
        Assert.Equal("skipSetup." + field, Refused(json).Field);
    }

    public static TheoryData<string, string, string> BadValues => new()
    {
        { "source", "\"C:relative.iso\"", "absolute" },
        { "source", "\"Win11.iso\"", "absolute" },
        { "source", "\"C:\\\\Windows\\\\notepad.exe\"", ".iso" },
        { "source", "\"Download\"", "download" },
        { "source", "42", "string" },
        { "edition", "\"Enterprise\"", "Professional" },
        { "edition", "\"\"", "empty" },
        { "removeApps", "\"all\"", "consumer" },
        { "removeApps", "[\"Contoso.NotAnApp\"]", "Contoso.NotAnApp" },
        { "removeApps", "[1]", "string" },
        { "removeApps", "null", "consumer" },
        { "skipSetup", "\"Anna\"", "object" },
        { "profile", "\"turbo\"", "turbo" },
        { "allowUnsupported", "\"yes\"", "true or false" },
        { "output", "\"out.iso\"", "absolute" },
        { "output", "\"C:\\\\out.img\"", ".iso" },
        { "output", "\"C:\\\\no-such-folder-0b9e\\\\out.iso\"", "folder" },
        { "language", "\"\"", "empty" },
    };

    [Theory]
    [MemberData(nameof(BadValues))]
    public void ABadValueIsRefused(string field, string value, string reason)
    {
        var json = Download();
        json[field] = JsonNode.Parse(value);
        var refused = Refused(json);
        Assert.Equal(field, refused.Field);
        Assert.Contains(reason, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string, string> BadSetupValues => new()
    {
        { "accountName", "Administrator", "reserved" },
        { "accountName", "Anna/Bert", "character" },
        { "accountName", "ThisNameIsLongerThan20", "20" },
        { "accountName", "   ", "empty" },
        { "region", "de", "region" },
        { "region", "xx-YY", "region" },
        { "keyboard", "0407", "input profile" },
        { "keyboard", "0410:00000410", "keyboard" },
    };

    [Theory]
    [MemberData(nameof(BadSetupValues))]
    public void ABadSetupValueIsRefused(string field, string value, string reason)
    {
        var json = Download();
        json["skipSetup"]![field] = value;
        var refused = Refused(json);
        Assert.Equal("skipSetup." + field, refused.Field);
        Assert.Contains(reason, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupValuesAreWrittenAsWindowsDoes()
    {
        var json = Download();
        json["skipSetup"] = new JsonObject { ["accountName"] = " Anna ", ["region"] = "de-at", ["keyboard"] = "0c07:00000407" };
        Assert.Equal(new MediaSetupChoice("Anna", "de-AT", "0C07:00000407"), Parse(json).SkipSetup);
    }

    [Fact]
    public void AnUnknownFieldIsRefused()
    {
        var json = Download();
        json["skipsetup"] = null;
        Assert.Equal("skipsetup", Refused(json).Field);
        json.Remove("skipsetup");
        json["skipSetup"]!["password"] = "x";
        Assert.Equal("skipSetup.password", Refused(json).Field);
    }

    [Fact]
    public void ALanguageBelongsToADownloadOnly()
    {
        var json = Download();
        json["source"] = OwnIso();
        Assert.Equal("language", Refused(json).Field);
    }

    [Fact]
    public void AnOwnIsoMustExistAndDifferFromTheOutput()
    {
        var json = Download();
        json.Remove("language");
        json["source"] = Path.Combine(_root, "missing.iso");
        Assert.Contains("does not exist", Refused(json).Message);
        json["source"] = OwnIso();
        json["output"] = OwnIso();
        Assert.Equal("output", Refused(json).Field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"source\":")]
    public void NotAnObjectIsRefused(string text)
    {
        var refused = Assert.Throws<MediaRequestFileException>(() => MediaRequestFile.Parse(text, Keyboards));
        Assert.Equal("request", refused.Field);
    }

    // ---- to the page's choices ----

    [Fact]
    public void TheCatalogImageIsTheNewestForLanguageAndEdition()
    {
        var choices = Parse(Download()).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark");
        Assert.Equal(Files[1], choices.Source.Download);
        Assert.Null(choices.Source.IsoPath);
        Assert.Null(choices.Source.Cab);
        Assert.Equal("Professional", choices.Edition);
        Assert.Null(choices.IsoIndex);
        Assert.Equal(new TuneroCopy(Path.Combine(_root, "Tunero.exe"), "office", "Dark"), choices.Tunero);
        Assert.True(choices.SkipSetup);
        Assert.Equal(("Anna", "de-AT", "0C07:00000407"), (choices.AccountName, choices.Region, choices.Keyboard));
    }

    [Fact]
    public void AnImageTheCatalogLacksIsRefused()
    {
        var json = Download();
        json["language"] = "fr-fr";
        Assert.Equal("language", Assert.Throws<MediaRequestFileException>(() => Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "System")).Field);
        json["language"] = "en-us";
        json["edition"] = "Core";
        json["profile"] = null;
        Assert.Equal("edition", Assert.Throws<MediaRequestFileException>(() => Parse(json).ToChoices(Files, "en", null, "System")).Field);
    }

    [Fact]
    public void AProfileNeedsTheTuneroCopy()
    {
        var refused = Assert.Throws<MediaRequestFileException>(() => Parse(Download()).ToChoices(Files, "en", null, "System"));
        Assert.Equal("profile", refused.Field);
        Assert.Contains("published", refused.Message);
        Assert.Equal("profile", Assert.Throws<MediaRequestFileException>(() => Parse(Download()).CheckTuneroCopy(null)).Field);
        Parse(Download()).CheckTuneroCopy(Path.Combine(_root, "Tunero.exe"));
    }

    [Fact]
    public void AnOwnIsoIsFoundByItsEdition()
    {
        var json = Download();
        var iso = OwnIso();
        json["source"] = iso;
        json.Remove("language");
        json["skipSetup"] = null;
        json["profile"] = null;
        var choices = Parse(json).ToChoices(null, "de", Path.Combine(_root, "Tunero.exe"), "System");
        Assert.Equal(new MediaSource(null, iso, null), choices.Source);
        Assert.Equal("Professional", choices.Edition);
        Assert.Null(choices.IsoIndex);
        Assert.Null(choices.IsoLanguage);
        Assert.Null(choices.Tunero);
        Assert.False(choices.SkipSetup);
    }

    // ---- the shared mapping ----

    private MediaChoices Choices(MediaSource source, string app = "en") => new(source, "Professional", null, null, app, ["Microsoft.BingNews"],
        SkipSetup: true, AccountName: " Anna ", Region: "de-AT", Keyboard: "0C07:00000407", AllowUnsupported: true,
        Tunero: new TuneroCopy(Path.Combine(_root, "Tunero.exe"), "office", "Light"), OutputIso: Output);

    [Fact]
    public void TheMappingFillsTheAnswerFileAndTheFirstSignIn()
    {
        var request = Choices(new MediaSource(Files[1], null, null)).ToRequest();
        Assert.Equal(new AnswerChoices("amd64", "de-DE", "0C07:00000407", MediaLocales.SystemLocale("de-AT"), "de-AT", "Anna", true, true, true), request.Answers);
        Assert.Equal(new FirstSignInFile("office", "en", "Light"), request.FirstSignIn);
        Assert.Equal(Path.Combine(_root, "Tunero.exe"), request.TuneroExe);
        Assert.Equal(["Microsoft.BingNews"], request.RemoveApps);
        Assert.Equal(("Professional", (int?)null, Output), (request.Edition, request.IsoIndex, request.OutputIso));
    }

    [Fact]
    public void WithoutSetupAndTuneroTheMappingUsesThePagesFallbacks()
    {
        var request = (Choices(new MediaSource(null, @"C:\Win11.iso", null), app: "de") with
        {
            SkipSetup = false, Region = null, Keyboard = null, Tunero = null,
        }).ToRequest();
        // Setup's language of an own ISO without a known image language: the app language's Windows language.
        Assert.Equal(new AnswerChoices("amd64", "de-DE", "0409:00000409", "en-US", "en-US", null, false, true, false), request.Answers);
        Assert.Null(request.FirstSignIn);
        Assert.Null(request.TuneroExe);
    }

    [Fact]
    public void AnOwnIsosImageLanguageIsSetupsLanguage()
    {
        var request = (Choices(new MediaSource(null, @"C:\Win11.iso", null)) with { IsoLanguage = "fr-fr", IsoIndex = 3 }).ToRequest();
        Assert.Equal("fr-FR", request.Answers.UiLanguage);
        Assert.Equal(3, request.IsoIndex);
    }

    [Theory]
    [InlineData("de", "de-de")]
    [InlineData("en", "en-us")]
    public void TheAppLanguageHasAWindowsLanguage(string app, string windows) => Assert.Equal(windows, MediaChoices.WindowsLanguage(app));
}
