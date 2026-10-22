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
        { "accountName", "Jürgen", "ASCII" },
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
    [InlineData(@"\\server\share\out.iso")]
    [InlineData(@"\\?\C:\out.iso")]
    public void AnOutputOffTheLocalDrivesIsRefused(string output)
    {
        var json = Download();
        json["output"] = output;
        var refused = Refused(json);
        Assert.Equal("output", refused.Field);
        Assert.Contains("local fixed drive", refused.Message);
    }

    [Theory]
    [InlineData(@"\\server\share\out.iso", false)]
    [InlineData(@"\\?\C:\out.iso", false)]
    [InlineData(@"\\.\C:\out.iso", false)]
    public void OnlyLocalFixedDrivesTakeTheOutput(string output, bool local) => Assert.Equal(local, MediaChoices.IsLocalOutput(output));

    [Fact]
    public void TheTempFolderTakesTheOutput() => Assert.True(MediaChoices.IsLocalOutput(Output));

    [Fact]
    public void AppLanguageAndThemeAreOptional()
    {
        var json = Download();
        Assert.Null(Parse(json).AppLanguage);
        Assert.Null(Parse(json).Theme);
        json["appLanguage"] = null;
        json["theme"] = null;
        Assert.Null(Parse(json).AppLanguage);
        json["appLanguage"] = "DE";
        json["theme"] = "dark";
        var file = Parse(json);
        Assert.Equal(("de", "Dark"), (file.AppLanguage, file.Theme));
    }

    [Theory]
    [InlineData("appLanguage", "\"fr\"", "en or de")]
    [InlineData("appLanguage", "1", "string")]
    [InlineData("theme", "\"Blue\"", "System, Light or Dark")]
    public void ABadAppLanguageOrThemeIsRefused(string field, string value, string reason)
    {
        var json = Download();
        json[field] = JsonNode.Parse(value);
        var refused = Refused(json);
        Assert.Equal(field, refused.Field);
        Assert.Contains(reason, refused.Message);
    }

    [Fact]
    public void AppLanguageAndThemeGoToTheFirstSignInAndSetup()
    {
        var json = Download();
        json["appLanguage"] = "de";
        json["theme"] = "Light";
        var request = Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest();
        Assert.Equal(new FirstSignInFile("office", "de", "Light"), request.FirstSignIn);

        // An own ISO's Setup language falls back to the app language's Windows language.
        json["source"] = OwnIso();
        json.Remove("language");
        Assert.Equal("de-DE", Parse(json).ToChoices(null, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest().Answers.UiLanguage);
        json.Remove("appLanguage");
        json.Remove("theme");
        var defaults = Parse(json).ToChoices(null, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest();
        Assert.Equal("en-US", defaults.Answers.UiLanguage);
        Assert.Equal(new FirstSignInFile("office", "en", "Dark"), defaults.FirstSignIn);
    }

    [Fact]
    public void AppsToInstallAndUnreviewedAppsAreOptional()
    {
        var json = Download();
        var plain = Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest();
        Assert.Null(plain.FirstSignIn!.Apps);
        Assert.Empty(plain.RemoveUnreviewed);

        json["installApps"] = new JsonArray("Valve.Steam", "Mozilla.Firefox", "Valve.Steam", "Playnite.Playnite");
        json["removeUnreviewed"] = new JsonArray("Contoso.Notes");
        var request = Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest();
        Assert.Equal(["Valve.Steam", "Mozilla.Firefox", "Playnite.Playnite"], request.FirstSignIn!.Apps);
        Assert.Equal(["Contoso.Notes"], request.RemoveUnreviewed);
    }

    [Fact]
    public void CompressionIsOptionalAndWimWhenAbsent()
    {
        var json = Download();
        Assert.Equal(MediaCompression.Wim, Parse(json).Compression);
        json["compression"] = null;
        Assert.Equal(MediaCompression.Wim, Parse(json).Compression);
        json["compression"] = "wim";
        Assert.Equal(MediaCompression.Wim, Parse(json).Compression);
        json["compression"] = "Recovery";
        var file = Parse(json);
        Assert.Equal(MediaCompression.Recovery, file.Compression);
        Assert.Equal(MediaCompression.Recovery, file.ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest().Compression);
    }

    [Theory]
    [InlineData("\"zip\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("true")]
    public void ABadCompressionIsRefusedByName(string value)
    {
        var json = Download();
        json["compression"] = JsonNode.Parse(value);
        var refused = Refused(json);
        Assert.Equal("compression", refused.Field);
        Assert.Contains("wim", refused.Message);
        Assert.Contains("recovery", refused.Message);
    }

    [Fact]
    public void ABadCompressionIsDroppedAndNamedWhenKeepingWhatFits()
    {
        var json = Download();
        json["compression"] = "zip";
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Equal([new("compression")], file.Dropped);
        Assert.Equal(MediaCompression.Wim, file.Compression);
    }

    [Theory]
    [InlineData("installApps", "[\"Contoso.NotAnApp\"]", "Contoso.NotAnApp")]
    [InlineData("installApps", "\"Valve.Steam\"", "list")]
    [InlineData("removeUnreviewed", "[\"Microsoft.BingNews\"]", "reviewed list")]
    [InlineData("removeUnreviewed", "[\"Microsoft.WindowsStore\"]", "protected")]
    public void BadAppListsAreRefused(string field, string value, string reason)
    {
        var json = Download();
        json[field] = JsonNode.Parse(value);
        var refused = Refused(json);
        Assert.Equal(field, refused.Field);
        Assert.Contains(reason, refused.Message);
    }

    /// <summary>The download's list: the measured apps of its build and edition; nothing for a build nobody measured.</summary>
    [Fact]
    public void MeasuredImagesNameTheirApps()
    {
        var images = CatalogData.Current.MediaImages;
        var pro = MediaChoices.MeasuredApps(images, Esd("26300.9711", "de-de", "Professional"))!;
        var proN = MediaChoices.MeasuredApps(images, Esd("26300.9711", "de-de", "ProfessionalN"))!;
        Assert.Contains("Microsoft.BingNews", pro);
        Assert.DoesNotContain("Microsoft.People", pro);
        // N editions lack the media apps.
        Assert.Contains("Microsoft.ZuneMusic", pro);
        Assert.DoesNotContain("Microsoft.ZuneMusic", proN);
        Assert.Null(MediaChoices.MeasuredApps(images, Esd("26100.4000", "de-de", "Professional")));
        // Every edition the page offers was measured for 26300.
        Assert.All(MediaCatalog.OfferedEditions, e => Assert.NotNull(MediaChoices.MeasuredApps(images, Esd("26300.9711", "en-us", e))));
    }

    [Fact]
    public void AppsToInstallNeedTheProfile()
    {
        var json = Download();
        json["profile"] = null;
        json["installApps"] = new JsonArray("Valve.Steam");
        Assert.Equal("installApps", Refused(json).Field);
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

    // ---- remembered and saved choices ----

    private MediaChoices Set(string name) => name switch
    {
        "download" => Choices(new MediaSource(Files[1], null, null)) with { Tunero = null, InstallApps = [] },
        "ownIso" => Choices(new MediaSource(null, OwnIso(), null)) with { Edition = "ProfessionalWorkstation", RemoveApps = [] },
        "allOn" => Choices(new MediaSource(Files[1], null, null)) with
        {
            AllowUnsupported = true, InstallApps = ["Valve.Steam", "Mozilla.Firefox"], RemoveUnreviewed = ["Contoso.Notes"],
            RemoveApps = MediaChoices.PageApps(CatalogData.Current.Appx, MediaChoices.MeasuredApps(CatalogData.Current.MediaImages, Files[1])).Select(a => a.Name).Take(5).ToList(),
        },
        "recovery" => Choices(new MediaSource(Files[1], null, null)) with { Compression = MediaCompression.Recovery },
        _ => Choices(new MediaSource(null, OwnIso(), null)) with
        {
            SkipSetup = false, AccountName = null, Region = null, Keyboard = null, AllowUnsupported = false, Tunero = null, RemoveApps = [],
        },
    };

    [Theory]
    [InlineData("download")]
    [InlineData("ownIso")]
    [InlineData("allOn")]
    [InlineData("allOff")]
    [InlineData("recovery")]
    public void ChoicesRoundTripThroughTheRequestFile(string name)
    {
        var c = Set(name);
        var json = MediaRequestFile.FromChoices(c, c.Tunero?.Profile, c.Tunero?.Theme).ToJson();
        var back = MediaRequestFile.Parse(json, Keyboards).ToChoices(Files, c.AppLanguage, c.Tunero?.Exe, c.Tunero?.Theme ?? "Dark");
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(c.ToRequest()), System.Text.Json.JsonSerializer.Serialize(back.ToRequest()));
        // The file written again is the same file.
        Assert.Equal(json, MediaRequestFile.FromChoices(back, c.Tunero?.Profile, c.Tunero?.Theme).ToJson());
        Assert.Equal(c.Compression, back.Compression);
    }

    [Fact]
    public void TheFileIsCamelCaseIndentedAndNamesTheSource()
    {
        var json = MediaRequestFile.FromChoices(Set("download"), null, null).ToJson();
        Assert.Contains("\"source\": \"download\"", json);
        Assert.Contains("\"language\": \"de-de\"", json);
        Assert.Contains("\"allowUnsupported\"", json);
        Assert.Contains(Environment.NewLine + "  \"edition\"", json);
        Assert.DoesNotContain("appLanguage", json);
        var own = MediaRequestFile.FromChoices(Set("ownIso"), "gaming", "Dark").ToJson();
        Assert.Contains("\"source\": " + System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "Win11.iso")), own);
        Assert.Contains("\"profile\": \"gaming\"", own);
        Assert.Contains("\"theme\": \"Dark\"", own);
    }

    [Fact]
    public void WhatNoLongerFitsIsDroppedAndNamedTheRestIsKept()
    {
        var json = Download();
        json["source"] = Path.Combine(_root, "gone.iso");
        json["installApps"] = new JsonArray("Valve.Steam", "Contoso.NotAnApp");
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Equal([new("source"), new("installApps", "Contoso.NotAnApp")], file.Dropped);
        Assert.Equal("office", file.Profile);
        Assert.Equal(["Valve.Steam"], file.InstallApps);
        Assert.Equal(new MediaSetupChoice("Anna", "de-AT", "0C07:00000407"), file.SkipSetup);
        Assert.True(file.AllowUnsupported);
        Assert.Contains("source", file.StandIns);
        Assert.Contains("language", file.StandIns);
        Assert.DoesNotContain("edition", file.StandIns);
    }

    [Fact]
    public void ARenamedProfileTakesTheAppsToInstallAlong()
    {
        var json = Download();
        json["profile"] = "old";
        json["installApps"] = new JsonArray("Valve.Steam");
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Equal([new("profile"), new("installApps")], file.Dropped);
        Assert.Null(file.Profile);
        Assert.Empty(file.InstallApps);
    }

    [Fact]
    public void AFileThatFitsDropsNothing()
    {
        var file = MediaRequestFile.ParseKeepingWhatFits(Download().ToJsonString(), Keyboards);
        Assert.Empty(file.Dropped);
        Assert.Empty(file.StandIns);
    }

    [Fact]
    public void TextThatIsNoRequestIsStillRefused()
    {
        Assert.Equal("request", Assert.Throws<MediaRequestFileException>(() => MediaRequestFile.ParseKeepingWhatFits("[1]", Keyboards)).Field);
        Assert.Equal("request", Assert.Throws<MediaRequestFileException>(() => MediaRequestFile.ParseKeepingWhatFits("{", Keyboards)).Field);
    }

    [Fact]
    public void AFieldOfAnotherVersionIsDroppedByName()
    {
        var json = Download();
        json["compression"] = "max";
        Assert.Equal([new("compression")], MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards).Dropped);
    }
}
