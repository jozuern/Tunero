using Optimizer.Core.Catalog;
using Optimizer.Core.Media;
using Optimizer.TestSupport;

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

    // ---- completeness: a field the hash or choices.json forgets makes Resume reuse an image built with other settings ----

    /// <summary>
    /// The fields of <see cref="MediaRequest"/> (and the records inside it) that <see cref="MediaCheckpoint.HashOf"/> leaves out
    /// on purpose, each with the reason; its comment says the same. Every other field must change the hash.
    /// </summary>
    private static readonly Dictionary<string, string> NotHashed = new()
    {
        ["OutputIso"] = "a resume may write elsewhere on the same drive",
        ["TuneroExe"] = "where the running Tunero lies, not a choice of the page",
        ["FirstSignIn.Language"] = "the app's language; the first sign-in file is written after both checkpoints",
        ["FirstSignIn.Theme"] = "the app's theme; the first sign-in file is written after both checkpoints",
        ["Source.Download.Url"] = "the download address; the file is named by its name, size and SHA-256",
        ["Source.Download.Language"] = "the catalog's display name of LanguageCode, which is hashed",
        ["Source.Cab.Url"] = "the catalog the page checked, not the image",
        ["Source.Cab.Size"] = "the catalog the page checked, not the image",
        ["Source.Cab.Sha256"] = "the catalog the page checked, not the image",
    };

    /// <summary>
    /// Hashed fields that choices.json (<see cref="MediaRequestFile.FromRequest"/>) does not hold, each with how the page
    /// makes them again when it applies the file before Resume. A value made differently gives another hash, which the
    /// resume refuses (Media_ResumeInvalid): it never builds on from the saved image.
    /// </summary>
    private static readonly Dictionary<string, string> MadeAgainOnResume = new()
    {
        ["IsoIndex"] = "the page finds the own ISO's image again by the edition or name the file holds",
        ["Answers.Architecture"] = "always MediaChoices.Architecture",
        ["Answers.UiLanguage"] = "the download's language, or the language of the own ISO's image, read again",
        ["Answers.SystemLocale"] = "follows the region (MediaLocales.SystemLocale)",
        ["Answers.FirstSignInTunero"] = "on exactly when the file names a profile",
        ["Source.Download.FileName"] = "the page picks the catalog's newest file for the language and edition again",
        ["Source.Download.Edition"] = "the request's edition, which the file holds",
        ["Source.Download.Architecture"] = "always MediaChoices.CatalogArchitecture",
        ["Source.Download.Size"] = "the catalog's, for the file the page picks again",
        ["Source.Download.Sha256"] = "the catalog's, for the file the page picks again",
    };

    /// <summary>A download and an own ISO with every choice on: Skip setup (the region and keyboard count only with it), a profile, apps, parts.</summary>
    private static MediaRequest[] FullRequests()
    {
        var answers = new AnswerChoices("amd64", "de-DE", "0C07:00000407", "de-AT", "de-AT", "Anna", SkipSetup: true, AllowUnsupported: true, FirstSignInTunero: true);
        var cab = new CabLocation(new Uri("http://example.invalid/products.cab"), 1234, new byte[32]);
        var download = new MediaRequest(new MediaSource(Esd, null, cab), "Professional", null, ["Microsoft.BingNews"], answers, @"C:\Tunero.exe",
            new FirstSignInFile("gaming", "en", "System") { Apps = ["Mozilla.Firefox"] }, @"C:\out\Win11.iso")
        {
            RemoveUnreviewed = ["Contoso.Notes"],
            RemoveParts = ["wordpad"],
        };
        return [download, download with { Source = new MediaSource(null, @"C:\iso\Win11.iso", null), IsoIndex = 2 }];
    }

    /// <summary>
    /// Per field: whether changing it alone changes <paramref name="of"/> in each base request (null: the field is inside a
    /// record that is null there). A record that is null in one base and set in another counts by its fields.
    /// </summary>
    private static Dictionary<string, List<bool?>> Changes(Func<MediaRequest, string> of)
    {
        var result = new Dictionary<string, List<bool?>>(StringComparer.Ordinal);
        foreach (var request in FullRequests())
        {
            var before = of(request);
            foreach (var (path, changed) in RecordLeaves.Of(request))
            {
                if (!result.TryGetValue(path, out var list)) result[path] = list = [];
                list.Add(changed is null ? null : of(changed) != before);
            }
        }
        foreach (var record in result.Keys.Where(p => RecordLeaves.IsInside(p, result.Keys)).ToList()) result.Remove(record);
        return result;
    }

    /// <summary>
    /// Every field of the request changes the hash, unless <see cref="NotHashed"/> names it, and a named one does not. A new
    /// field that the hash misses fails here with its name.
    /// </summary>
    [Fact]
    public void EveryFieldOfTheRequestChangesTheHashUnlessItIsNamedAsLeftOut()
    {
        var problems = new List<string>();
        var changes = Changes(MediaCheckpoint.HashOf);
        foreach (var (path, outcomes) in changes)
        {
            if (outcomes.All(o => o is null))
                problems.Add($"MediaRequest.{path} is in a record that is null in every request of FullRequests: set it in one");
            else if (NotHashed.ContainsKey(path) && outcomes.Contains(true))
                problems.Add($"MediaRequest.{path} changes MediaCheckpoint.HashOf: take it off NotHashed");
            else if (!NotHashed.ContainsKey(path) && !outcomes.Contains(true))
                problems.Add($"MediaRequest.{path} does not change MediaCheckpoint.HashOf: add it there, or to NotHashed with the reason");
        }
        problems.AddRange(NotHashed.Keys.Where(k => !changes.ContainsKey(k)).Select(k => $"NotHashed names {k}, which is no field of MediaRequest"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Resume applies choices.json to the page and builds on only when the page's request has the saved hash: every hashed
    /// field is in the file, or <see cref="MadeAgainOnResume"/> says how the page makes it again.
    /// </summary>
    [Fact]
    public void EveryHashedFieldIsInTheSavedChoicesUnlessThePageMakesItAgain()
    {
        var problems = new List<string>();
        var hashed = Changes(MediaCheckpoint.HashOf).Where(c => c.Value.Contains(true)).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var saved = Changes(r => MediaRequestFile.FromRequest(r).ToJson());
        foreach (var path in hashed)
        {
            var inFile = saved[path].Contains(true);
            if (!inFile && !MadeAgainOnResume.ContainsKey(path))
                problems.Add($"MediaRequest.{path} is hashed but MediaRequestFile.FromRequest (choices.json) does not hold it: add it to the request file, or to MadeAgainOnResume with how the page makes it again");
            else if (inFile && MadeAgainOnResume.ContainsKey(path))
                problems.Add($"MediaRequest.{path} is in choices.json: take it off MadeAgainOnResume");
        }
        problems.AddRange(MadeAgainOnResume.Keys.Where(k => !hashed.Contains(k)).Select(k => $"MadeAgainOnResume names {k}, which is no hashed field of MediaRequest"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
