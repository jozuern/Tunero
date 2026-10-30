using Optimizer.Core.Backup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;
using Xunit;

namespace Optimizer.Core.Tests;

public class ChangeTransferTests
{
    private static readonly TweakCatalog Catalog = TweakCatalog.Current;
    private static readonly Facts Pro = new Facts().Set("os.edition", "Professional").Set("os.build", 26300);

    [Fact]
    public void ExportKeepsAppliedCatalogTweaksAndOfferedPrioritiesOnly()
    {
        var backedUp = new[] { "explorer.fileExtensions", "privacy.telemetryOff", "startup.runkey.steam-1a2b3c4d", "priority.game-12345678" };
        var applied = new HashSet<string> { "explorer.fileExtensions", "startup.runkey.steam-1a2b3c4d" };
        var rules = new[] { new PriorityRule("game.exe", CpuPriority.High, true), new PriorityRule("other.exe", null, false, 24) };

        var (file, pcSpecific) = ChangeTransfer.Create(backedUp, applied, Catalog, rules, "0.6.9", "26300.1");

        // privacy.telemetryOff has a backup but is not on any more (undone in part): not exported.
        Assert.Equal(["explorer.fileExtensions"], file.Tweaks);
        Assert.Equal(1, pcSpecific);
        Assert.Equal([new TransferPriority("game.exe", CpuPriority.High, true)], file.Priorities);
    }

    [Fact]
    public void RoundTripThroughText()
    {
        var file = new TransferFile(ChangeTransfer.FileKind, 1, "0.6.9", "26300.1", DateTimeOffset.Now, ["explorer.fileExtensions"], [new TransferPriority("game.exe", CpuPriority.AboveNormal, false)]);
        var read = ChangeTransfer.Parse(ChangeTransfer.Serialize(file));
        Assert.NotNull(read);
        Assert.Equal(file.Tweaks, read.Tweaks);
        Assert.Equal(file.Priorities, read.Priorities);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"kind":"something-else","format":1,"tweaks":[],"priorities":[]}""")]
    [InlineData("""{"kind":"pcoptimizer-changes","format":2,"tweaks":[],"priorities":[]}""")]
    [InlineData("""{"kind":"pcoptimizer-changes","format":1,"tweaks":["..\\x"],"priorities":[]}""")]
    [InlineData("""{"kind":"pcoptimizer-changes","format":1,"tweaks":[],"priorities":[{"exe":"C:\\x\\game.exe","cpu":"High","lowIo":false}]}""")]
    [InlineData("""{"kind":"pcoptimizer-changes","format":1,"tweaks":[],"priorities":[{"exe":"game.exe","cpu":"Realtime","lowIo":false}]}""")]
    [InlineData("""{"kind":"pcoptimizer-changes","format":1,"tweaks":[],"priorities":[{"exe":"game.exe","cpu":"24","lowIo":false}]}""")]
    public void InvalidFilesAreRejected(string text) => Assert.Null(ChangeTransfer.Parse(text));

    /// <summary>A picked file is read whole only up to the size limit, which is checked on the stream that is read.</summary>
    [Fact]
    public async Task ReadingAFileChecksItsSize()
    {
        var folder = TestFolders.Create("transfer");
        try
        {
            var file = new TransferFile(ChangeTransfer.FileKind, 1, "0.6.9", "26300.1", DateTimeOffset.Now, ["explorer.fileExtensions"], []);
            var good = Path.Combine(folder, "good.tunero");
            await File.WriteAllTextAsync(good, ChangeTransfer.Serialize(file), new System.Text.UTF8Encoding(true));
            Assert.Equal(["explorer.fileExtensions"], (await ChangeTransfer.ReadFileAsync(good))!.Tweaks);

            var large = Path.Combine(folder, "large.tunero");
            await File.WriteAllTextAsync(large, new string(' ', (int)ChangeTransfer.MaxFileBytes) + ChangeTransfer.Serialize(file));
            Assert.Null(await ChangeTransfer.ReadFileAsync(large));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [Fact]
    public void FileCanOnlySelectCatalogTweaks()
    {
        // Actions in the file are ignored: the import resolves ids in this version's catalog only.
        var text = """{"kind":"pcoptimizer-changes","format":1,"tweaks":["explorer.fileExtensions","made.up"],"priorities":[],"actions":[{"type":"registry"}]}""";
        var file = ChangeTransfer.Parse(text)!;
        var plan = ChangeTransfer.Plan(file, Catalog, Pro, new HashSet<string>());
        Assert.Equal(["explorer.fileExtensions"], plan.Together.Select(t => t.Id));
        Assert.Equal(1, plan.Unknown);
    }

    [Fact]
    public void PlanSortsByTheApplyRecommendedRules()
    {
        var file = new TransferFile(ChangeTransfer.FileKind, 1, "", "", DateTimeOffset.Now,
            ["explorer.fileExtensions", "security.vbsOff", "personalize.darkMode", "privacy.consumerFeaturesOff"],
            [new TransferPriority("game.exe", CpuPriority.High, false)]);
        var plan = ChangeTransfer.Plan(file, Catalog, Pro, new HashSet<string> { "personalize.darkMode" });

        Assert.Equal(["explorer.fileExtensions"], plan.Together.Select(t => t.Id));
        // Expert and anti-cheat sensitive changes (and every start priority) need their own confirmation.
        Assert.Contains(plan.OneByOne, t => t.Id == "security.vbsOff");
        Assert.Contains(plan.OneByOne, t => t.Subject == "game.exe");
        Assert.Equal(1, plan.AlreadyOn);
        Assert.Equal(1, plan.NotForThisPc); // consumer features: Enterprise and Education only
        Assert.All(plan.Together, t => Assert.True(t.IsBatchSafe));
    }
}
