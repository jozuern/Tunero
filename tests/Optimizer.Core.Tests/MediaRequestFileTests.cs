using System.Text.Json.Nodes;
using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Optimizer.TestSupport;

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

    [Fact]
    public void TheImageSettingsAreOptionalAndOffWhenAbsent()
    {
        var json = Download();
        Assert.Equal(MediaImageSettings.None, Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").Settings);
        json["preventDeviceEncryption"] = null;
        json["noReservedStorage"] = null;
        Assert.Equal(MediaImageSettings.None, Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").Settings);
        json["preventDeviceEncryption"] = true;
        json["noReservedStorage"] = false;
        var request = Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest();
        Assert.Equal(new MediaImageSettings(true, false, false), request.Settings);
        json["noReservedStorage"] = true;
        Assert.Equal(new MediaImageSettings(true, true, false), Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").Settings);
    }

    [Theory]
    [InlineData("preventDeviceEncryption", "\"yes\"")]
    [InlineData("preventDeviceEncryption", "1")]
    [InlineData("noReservedStorage", "\"true\"")]
    [InlineData("noReservedStorage", "[]")]
    public void ABadImageSettingIsRefusedByName(string field, string value)
    {
        var json = Download();
        json[field] = JsonNode.Parse(value);
        var refused = Refused(json);
        Assert.Equal(field, refused.Field);
        Assert.Contains("true or false", refused.Message);
    }

    [Fact]
    public void AllowUnsupportedUpgradesIsOptionalAndNeedsAllowUnsupported()
    {
        var json = Download();
        json["allowUnsupported"] = true;
        Assert.False(Parse(json).AllowUnsupportedUpgrades);
        json["allowUnsupportedUpgrades"] = true;
        var request = Parse(json).ToChoices(Files, "en", Path.Combine(_root, "Tunero.exe"), "Dark").ToRequest();
        Assert.Equal(new MediaImageSettings(false, false, true), request.Settings);
        json["allowUnsupported"] = false;
        var refused = Refused(json);
        Assert.Equal("allowUnsupportedUpgrades", refused.Field);
        json["allowUnsupported"] = true;
        json["allowUnsupportedUpgrades"] = "yes";
        Assert.Contains("true or false", Refused(json).Message);
    }

    [Fact]
    public void AllowUnsupportedUpgradesWithoutAllowUnsupportedIsDroppedWhenKeepingWhatFits()
    {
        var json = Download();
        json["allowUnsupported"] = false;
        json["allowUnsupportedUpgrades"] = true;
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Equal([new("allowUnsupportedUpgrades")], file.Dropped);
        Assert.False(file.AllowUnsupportedUpgrades);
    }

    [Fact]
    public void ABadImageSettingIsDroppedAndNamedWhenKeepingWhatFits()
    {
        var json = Download();
        json["preventDeviceEncryption"] = "yes";
        json["noReservedStorage"] = true;
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Equal([new("preventDeviceEncryption")], file.Dropped);
        Assert.False(file.PreventDeviceEncryption);
        Assert.True(file.NoReservedStorage);
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
            RemoveParts = ["wordpad", "speech", "recall"],
            RemoveApps = MediaChoices.PageApps(CatalogData.Current.Appx, MediaChoices.MeasuredApps(CatalogData.Current.MediaImages, Files[1])).Select(a => a.Name).Take(5).ToList(),
        },
        "recovery" => Choices(new MediaSource(Files[1], null, null)) with { Compression = MediaCompression.Recovery },
        "settings" => Choices(new MediaSource(Files[1], null, null)) with { Settings = new(true, true, false) },
        "upgrades" => Choices(new MediaSource(Files[1], null, null)) with { AllowUnsupported = true, Settings = new(false, false, true) },
        "encryptionOnly" => Choices(new MediaSource(Files[1], null, null)) with { Settings = new(true, false, false) },
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
    [InlineData("settings")]
    [InlineData("encryptionOnly")]
    [InlineData("upgrades")]
    public void ChoicesRoundTripThroughTheRequestFile(string name)
    {
        var c = Set(name);
        var json = MediaRequestFile.FromChoices(c, c.Tunero?.Profile, c.Tunero?.Theme).ToJson();
        var back = MediaRequestFile.Parse(json, Keyboards).ToChoices(Files, c.AppLanguage, c.Tunero?.Exe, c.Tunero?.Theme ?? "Dark");
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(c.ToRequest()), System.Text.Json.JsonSerializer.Serialize(back.ToRequest()));
        // The file written again is the same file.
        Assert.Equal(json, MediaRequestFile.FromChoices(back, c.Tunero?.Profile, c.Tunero?.Theme).ToJson());
        Assert.Equal(c.Compression, back.Compression);
        Assert.Equal(c.Settings, back.Settings);
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

    [Fact]
    public void PartsAreOptionalAndNamedByCatalogId()
    {
        var json = Download();
        Assert.Empty(Parse(json).RemoveParts);
        json["removeParts"] = new JsonArray("wordpad", "RECALL", "wordpad");
        Assert.Equal(["wordpad", "recall"], Parse(json).RemoveParts);
        json["removeParts"] = null;
        Assert.Empty(Parse(json).RemoveParts);
        Assert.Contains("removeParts", MediaRequestFile.FieldNames);
    }

    [Theory]
    [InlineData("[\"defender\"]", "defender")]
    [InlineData("[1]", "string")]
    [InlineData("\"wordpad\"", "list")]
    public void APartThatIsNotInTheCatalogIsRefused(string value, string mentions)
    {
        var json = Download();
        json["removeParts"] = JsonNode.Parse(value);
        var refused = Refused(json);
        Assert.Equal("removeParts", refused.Field);
        Assert.Contains(mentions, refused.Message);
    }

    [Fact]
    public void AnUnknownPartIsDroppedByNameTheRestIsKept()
    {
        var json = Download();
        json["removeParts"] = new JsonArray("wordpad", "defender");
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Equal([new MediaDroppedValue("removeParts", "defender")], file.Dropped);
        Assert.Equal(["wordpad"], file.RemoveParts);
    }

    [Fact]
    public void PartsReachTheRequest()
    {
        var c = Set("download") with { RemoveParts = ["wordpad"] };
        Assert.Equal(["wordpad"], c.ToRequest().RemoveParts);
        Assert.Empty(Set("download").ToRequest().RemoveParts);
    }

    // ---- completeness: a choice the request file forgets is lost from the remembered, saved and resumed choices ----

    /// <summary>Properties of <see cref="MediaRequestFile"/> that are no field of the file, with the reason.</summary>
    private static readonly Dictionary<string, string> NotFields = new()
    {
        ["Dropped"] = "what reading the file could not use, never written",
        ["StandIns"] = "which fields hold a stand-in after reading, never written",
    };

    /// <summary>
    /// Choices (paths into <see cref="MediaChoices"/>) the request file does not hold, with the reason. Everything else
    /// comes back through <see cref="MediaRequestFile.FromChoices"/>, <see cref="MediaRequestFile.ToJson"/>,
    /// <see cref="MediaRequestFile.Parse"/> and <see cref="MediaRequestFile.ToChoices"/>.
    /// </summary>
    private static readonly Dictionary<string, string> NotInTheFile = new()
    {
        ["IsoIndex"] = "the page reads the own ISO again and finds the image by the edition or name the file holds",
        ["IsoLanguage"] = "the page reads it from the own ISO's image again",
        ["AppLanguage"] = "the app's language when the file is used; FromChoices leaves it out on purpose",
        ["Tunero.Exe"] = "the running Tunero, given to ToChoices",
        ["Source.Cab"] = "the catalog the page checks again before a build",
    };

    /// <summary>A download (or an own ISO) with every choice on and valid, so that each one can be changed alone.</summary>
    private MediaChoices FullChoices(bool ownIso)
    {
        var c = new MediaChoices(new MediaSource(Files[1], null, null), "Professional", null, null, "en", MeasuredPageApps().Take(2).ToList(),
            SkipSetup: true, AccountName: "Anna", Region: "de-AT", Keyboard: "0C07:00000407", AllowUnsupported: true,
            Tunero: new TuneroCopy(Path.Combine(_root, "Tunero.exe"), "office", "Light"), OutputIso: Output)
        {
            InstallApps = ["Mozilla.Firefox"],
            RemoveUnreviewed = ["Contoso.Notes"],
            RemoveParts = ["recall"],
        };
        return ownIso ? c with { Source = new MediaSource(null, OwnIso(), null), Edition = "ProfessionalWorkstation" } : c;
    }

    private static List<string> MeasuredPageApps() =>
        MediaChoices.PageApps(CatalogData.Current.Appx, MediaChoices.MeasuredApps(CatalogData.Current.MediaImages, Files[1])).Select(a => a.Name).ToList();

    /// <summary>Other values the request file accepts, for the choices whose generic change (RecordLeaves) it would refuse.</summary>
    private Dictionary<string, Func<object?, object?>> FileValues()
    {
        var otherIso = Path.Combine(_root, "Other.iso");
        File.WriteAllText(otherIso, "iso");
        return new()
        {
            ["Source.Download"] = _ => Files[3],
            ["Source.IsoPath"] = _ => otherIso,
            ["Edition"] = _ => "Core",
            ["RemoveApps"] = _ => MeasuredPageApps().Take(3).ToList(),
            ["AccountName"] = _ => "Berta",
            ["Region"] = _ => "de-DE",
            ["Keyboard"] = _ => "0407:00000407",
            ["Tunero.Profile"] = _ => "gaming",
            ["Tunero.Theme"] = _ => "Dark",
            ["OutputIso"] = _ => Path.Combine(_root, "other.iso"),
            ["RemoveUnreviewed"] = _ => new List<string> { "Contoso.Notes", "Contoso.Paint" },
            ["RemoveParts"] = _ => new List<string> { "recall", "wordpad" },
            ["InstallApps"] = _ => new List<string> { "Mozilla.Firefox", "Valve.Steam" },
        };
    }

    [Fact]
    public void EveryPropertyOfTheRequestFileIsAFieldAndToJsonWritesEachOne()
    {
        var properties = typeof(MediaRequestFile).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && !NotFields.ContainsKey(p.Name))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).ToList();
        var problems = properties.Except(MediaRequestFile.FieldNames).Select(p => $"MediaRequestFile.{p} is not in FieldNames: add the field (Parse, ToJson) or name it in NotFields")
            .Concat(MediaRequestFile.FieldNames.Except(properties).Select(f => $"FieldNames has {f}, which is no property of MediaRequestFile"))
            .ToList();
        var written = JsonNode.Parse((MediaRequestFile.FromChoices(FullChoices(false), null, null) with { AppLanguage = "de" }).ToJson())!.AsObject().Select(p => p.Key).ToList();
        problems.AddRange(MediaRequestFile.FieldNames.Except(written).Select(f => $"MediaRequestFile.ToJson does not write {f}"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Every choice of <see cref="MediaChoices"/>, changed alone, comes back from the request file the page remembers, saves
    /// and resumes from, unless <see cref="NotInTheFile"/> names it. A new choice that the file misses fails here with its name.
    /// </summary>
    [Fact]
    public void EveryChoiceComesBackFromTheRequestFileUnlessItIsNamedAsLeftOut()
    {
        var problems = new List<string>();
        var outcomes = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var values = FileValues();
        foreach (var full in new[] { FullChoices(false), FullChoices(true) })
        {
            foreach (var (path, changed) in RecordLeaves.Of(full, values))
            {
                if (!outcomes.TryGetValue(path, out var list)) outcomes[path] = list = [];
                if (changed is null)
                {
                    list.Add(null);
                    continue;
                }
                var value = RecordLeaves.Get(changed, path);
                if (RecordLeaves.Same(value, RecordLeaves.Get(full, path)))
                {
                    problems.Add($"MediaChoices.{path}: FileValues gives the value it already has");
                    continue;
                }
                try
                {
                    var back = MediaRequestFile.Parse(MediaRequestFile.FromChoices(changed, null, null).ToJson(), Keyboards)
                        .ToChoices(Files, full.AppLanguage, full.Tunero?.Exe, "System");
                    list.Add(RecordLeaves.Same(RecordLeaves.Get(back, path), value) ? "" : "lost");
                }
                catch (MediaRequestFileException ex)
                {
                    list.Add("refused: " + ex.Message);
                }
            }
        }
        foreach (var record in outcomes.Keys.Where(p => RecordLeaves.IsInside(p, outcomes.Keys)).ToList()) outcomes.Remove(record);
        foreach (var (path, results) in outcomes)
        {
            var back = results.Contains("");
            if (NotInTheFile.ContainsKey(path))
            {
                if (back) problems.Add($"MediaChoices.{path} comes back from the request file: take it off NotInTheFile");
            }
            else if (results.All(r => r is null))
            {
                problems.Add($"MediaChoices.{path} is in a record that is null in every FullChoices: set it in one");
            }
            else if (!back)
            {
                problems.Add(results.FirstOrDefault(r => r?.StartsWith("refused", StringComparison.Ordinal) == true) is { } refused
                    ? $"MediaChoices.{path}: the request file {refused}; give a value it accepts in FileValues"
                    : $"MediaChoices.{path} does not come back from the request file: add it to MediaRequestFile (FromChoices, ToJson, Parse, ToChoices) or name it in NotInTheFile with the reason");
            }
        }
        problems.AddRange(NotInTheFile.Keys.Where(k => !outcomes.ContainsKey(k)).Select(k => $"NotInTheFile names {k}, which is no choice of MediaChoices"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    public static IEnumerable<object[]> FieldNames => MediaRequestFile.FieldNames.Select(f => new object[] { f });

    /// <summary>
    /// A remembered or saved file with one field that cannot be used keeps the rest: the field is named in the page's note
    /// and holds a stand-in the page does not apply. A new field without a stand-in would make the whole file unusable.
    /// </summary>
    [Theory]
    [MemberData(nameof(FieldNames))]
    public void AFieldThatCannotBeUsedIsDroppedAndNamedAndTheRestIsKept(string field)
    {
        var json = Download();
        json[field] = 42;
        var file = MediaRequestFile.ParseKeepingWhatFits(json.ToJsonString(), Keyboards);
        Assert.Contains(new MediaDroppedValue(field), file.Dropped);
        Assert.Contains(field, file.StandIns);
        if (field != "allowUnsupported") Assert.True(file.AllowUnsupported);
        if (field != "skipSetup") Assert.Equal("Anna", file.SkipSetup?.AccountName);
    }
}
