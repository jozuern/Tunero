using Optimizer.Core.Catalog;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>The checkpoint file of a work folder: what its hash covers, how it is written and what reading refuses.</summary>
public sealed class MediaCheckpointTests : IDisposable
{
    private readonly string _folder = TestFolders.Create("checkpoint");

    public void Dispose() => TestFolders.Delete(_folder);

    private static readonly EsdFile Esd = new(
        "26300.9711.260410-1914.br_release_CLIENTCONSUMER_RET_x64FRE_en-us.esd", "en-us", "English (United States)", "Professional", "x64",
        5L << 30, new string('a', 64), new Uri("http://dl.delivery.mp.microsoft.com/filestreamingservice/files/x.esd"));

    private static AnswerChoices Plain => new("amd64", "en-US", "0409:00000409", "en-US", "en-US", null, false, false, false);

    private static MediaRequest Download() => new(new MediaSource(Esd, null, null), "Professional", null, [], Plain, null, null, @"C:\out\Win11.iso");

    private static MediaCheckpoint Ready => new(CheckpointKind.ImageReady, new string('1', 64), new string('2', 64), new string('3', 64), "26300",
        new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(2))) { UiLanguage = "en-US" };

    private string FilePath => Path.Combine(_folder, MediaCheckpoint.FileName);

    [Fact]
    public void HashIgnoresTheOutputPathOnly()
    {
        var a = Download();
        var b = a with { OutputIso = @"D:\other.iso" };
        var c = a with { RemoveApps = ["Microsoft.BingNews"] };
        Assert.Equal(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(b));
        Assert.NotEqual(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(c));
        Assert.Matches("^[0-9A-F]{64}$", MediaCheckpoint.HashOf(a));
    }

    /// <summary>Every choice that shapes the media counts; the order of a list and the case of a name do not.</summary>
    [Fact]
    public void HashCoversEveryChoiceButNotTheOrderOfALIst()
    {
        var a = Download() with { RemoveApps = ["Microsoft.BingNews", "Microsoft.BingWeather"], RemoveParts = ["wordpad", "recall"] };
        Assert.Equal(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(a with { RemoveApps = ["microsoft.bingweather", "Microsoft.BingNews"], RemoveParts = ["recall", "wordpad"] }));
        var changed = new MediaRequest[]
        {
            a with { Edition = "Core" },
            a with { Compression = MediaCompression.Recovery },
            a with { Settings = new MediaImageSettings(true, false, false) },
            a with { RemoveParts = ["wordpad"] },
            a with { Answers = a.Answers with { SkipSetup = true, AccountName = "Anna" } },
            a with { Source = new MediaSource(Esd with { Sha256 = new string('b', 64) }, null, null) },
            a with { Source = new MediaSource(null, @"C:\iso\Win11.iso", null), IsoIndex = 2 },
            a with { Answers = a.Answers with { FirstSignInTunero = true }, TuneroExe = @"C:\Tunero.exe", FirstSignIn = new FirstSignInFile("gaming", "en", "System") },
        };
        foreach (var other in changed) Assert.NotEqual(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(other));
    }

    /// <summary>
    /// Not choices of the page: where the running Tunero lies, the app's language and theme (the first sign-in file is written
    /// after both checkpoints, from the app as it is then), and the catalog's download address.
    /// </summary>
    [Fact]
    public void HashLeavesOutWhatIsNoChoiceOfThePage()
    {
        var a = Download() with { Answers = Plain with { FirstSignInTunero = true }, TuneroExe = @"C:\Tunero.exe", FirstSignIn = new FirstSignInFile("gaming", "en", "System") };
        Assert.Equal(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(a with { TuneroExe = @"D:\Apps\Tunero.exe" }));
        Assert.Equal(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(a with { FirstSignIn = new FirstSignInFile("gaming", "de", "Dark") }));
        Assert.Equal(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(a with { Source = new MediaSource(Esd with { Url = new Uri("http://example.invalid/x.esd") }, null, null) }));
        Assert.NotEqual(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(a with { FirstSignIn = new FirstSignInFile("office", "en", "System") }));
    }

    /// <summary>
    /// Region and keyboard are choices only with Skip setup: without it the page greys them out and choices.json does not
    /// keep them, so a resume after a restart must not depend on them.
    /// </summary>
    [Fact]
    public void HashCoversRegionAndKeyboardOnlyWithSkipSetup()
    {
        var a = Download();
        Assert.Equal(MediaCheckpoint.HashOf(a), MediaCheckpoint.HashOf(a with { Answers = a.Answers with { InputLocale = "0407:00000407", SystemLocale = "de-DE", UserLocale = "de-DE" } }));
        var skipped = a with { Answers = a.Answers with { SkipSetup = true, AccountName = "Anna" } };
        Assert.NotEqual(MediaCheckpoint.HashOf(skipped), MediaCheckpoint.HashOf(skipped with { Answers = skipped.Answers with { InputLocale = "0407:00000407" } }));
        Assert.NotEqual(MediaCheckpoint.HashOf(skipped), MediaCheckpoint.HashOf(skipped with { Answers = skipped.Answers with { UserLocale = "de-DE" } }));
    }

    [Fact]
    public void SavedCheckpointReadsBack()
    {
        Ready.Save(_folder);
        Assert.Equal(Ready, MediaCheckpoint.Read(_folder));
        Assert.False(File.Exists(FilePath + ".tmp"));
        var downloaded = new MediaCheckpoint(CheckpointKind.Downloaded, new string('1', 64), new string('A', 64), null, "", DateTimeOffset.Now);
        downloaded.Save(_folder);
        Assert.Equal(downloaded, MediaCheckpoint.Read(_folder));
    }

    [Fact]
    public void MissingOrBrokenCheckpointReadsAsNone()
    {
        Assert.Null(MediaCheckpoint.Read(_folder));
        Assert.Null(MediaCheckpoint.Read(Path.Combine(_folder, "missing")));
        foreach (var text in new[]
                 {
                     "", "not json", "[]", "{}",
                     Ready.ToJson().Replace("ImageReady", "Finished", StringComparison.Ordinal),
                     Ready.ToJson().Replace(new string('2', 64), "12", StringComparison.Ordinal),
                     Ready.ToJson().Replace("\"26300\"", "\"26300; rd /s\"", StringComparison.Ordinal),
                     Ready.ToJson().Replace("\"en-US\"", "\"xx-<>\"", StringComparison.Ordinal),
                     Ready.ToJson().Replace("{", "{\"extra\": 1,", StringComparison.Ordinal),
                     // ImageReady without its boot.wim hash, Downloaded with one.
                     (Ready with { BootSha256 = null }).ToJson(),
                     (Ready with { Kind = CheckpointKind.Downloaded }).ToJson(),
                 })
        {
            File.WriteAllText(FilePath, text);
            Assert.Null(MediaCheckpoint.Read(_folder));
        }
    }

    [Fact]
    public void OversizedCheckpointReadsAsNone()
    {
        File.WriteAllText(FilePath, Ready.ToJson() + new string(' ', 70_000));
        Assert.Null(MediaCheckpoint.Read(_folder));
    }

    /// <summary>A checkpoint file that is a link (here its folder is a junction) is never followed.</summary>
    [Fact]
    public void CheckpointInALinkedFolderReadsAsNone()
    {
        var target = Path.Combine(_folder, "target");
        Directory.CreateDirectory(target);
        Ready.Save(target);
        var link = Path.Combine(_folder, "link");
        TestFolders.Junction(link, target);
        Assert.NotNull(MediaCheckpoint.Read(target));
        Assert.Null(MediaCheckpoint.Read(link));
    }

    [Fact]
    public void CheckpointExpiresAfterFourteenDays()
    {
        var now = Ready.Saved;
        Assert.False(Ready.IsExpired(now.AddDays(13)));
        Assert.True(Ready.IsExpired(now.AddDays(15)));
    }

    [Fact]
    public void ResumeStageFollowsTheKind()
    {
        Assert.Equal(MediaStage.Files, Ready.ResumesAt);
        Assert.Equal(MediaStage.Check, (Ready with { Kind = CheckpointKind.Downloaded }).ResumesAt);
    }

    /// <summary>The saved choices are the request as a request file; the page reads them back like a remembered file.</summary>
    [Fact]
    public void RequestFileFromARequestMatchesTheOneFromItsChoices()
    {
        var choices = new MediaChoices(new MediaSource(Esd, null, null), "Professional", null, null, "en", ["Microsoft.BingNews"], true, "Anna",
            "de-AT", "0C07:00000407", true, new TuneroCopy(@"C:\Tunero.exe", CatalogData.Current.Profiles.Default.Id, "Dark"), @"C:\out\Win11.iso")
        {
            InstallApps = ["Mozilla.Firefox"],
            RemoveParts = ["wordpad"],
            Compression = MediaCompression.Recovery,
            Settings = new MediaImageSettings(true, true, true),
        };
        Assert.Equal(MediaRequestFile.FromChoices(choices, null, null).ToJson(), MediaRequestFile.FromRequest(choices.ToRequest()).ToJson());
        var own = choices with { Source = new MediaSource(null, @"C:\iso\Win11.iso", null), Edition = "Windows 11 Pro", IsoIndex = 2, IsoLanguage = "de-DE", Tunero = null, InstallApps = [], RemoveUnreviewed = ["Contoso.Notes"] };
        Assert.Equal(MediaRequestFile.FromChoices(own, null, null).ToJson(), MediaRequestFile.FromRequest(own.ToRequest()).ToJson());
    }
}
