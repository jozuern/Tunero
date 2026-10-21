using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Startup;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// What the engine reports about its own records: backups an undo of everything cannot read, the history folder, the one
/// backup read of a detection, a backup that could not be updated after a change, and shell extensions a user blocked.
/// </summary>
public class EngineBookkeepingTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false);
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };
    private const string Blocked = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static TweakDefinition Tweak(string id, params TweakAction[] actions) => new()
    {
        Id = id, Category = "Test", Hidden = true, Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] }, Actions = [.. actions],
    };

    private static RegistryAction Reg(string name) => new()
    {
        Hive = Hive.Machine, Path = @"SOFTWARE\PCOTest\Bookkeeping", Name = name, Kind = "dword", Value = System.Text.Json.JsonSerializer.SerializeToElement(1),
    };

    /// <summary>Undo all used to pass over a backup it could not read; the change it records stayed in place unmentioned.</summary>
    [Fact]
    public async Task UndoAllReportsABackupItCannotRead()
    {
        using var fx = new EngineFixture();
        var kept = Tweak("test.kept", Reg("Kept"));
        var damaged = Tweak("test.damagedLater", Reg("Damaged"));
        await fx.Engine.ApplyAsync(kept, Facts, Options);
        await fx.Engine.ApplyAsync(damaged, Facts, Options);
        File.WriteAllText(Path.Combine(fx.Store.BackupFolder, "test.damagedLater.json"), "{\"TweakId\":\"test.dam"); // cut off

        var results = fx.Engine.RevertAll();
        Assert.Contains(results, r => r.Tweak.Id == "test.kept" && r.Result.Success);
        var failed = Assert.Single(results, r => !r.Result.Success);
        Assert.Contains("test.damagedLater.json" + BackupStore.DamagedSuffix, Assert.Single(failed.Result.Errors));
        // The damaged file stays (it holds the originals) and is reported again by the next undo of everything.
        Assert.Single(fx.Engine.RevertAll(), r => !r.Result.Success);
    }

    [Fact]
    public async Task TheHistoryKeepsOnlyTheNewestFiles()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i < BackupStore.KeptHistory + 5; i++)
        {
            var old = Path.Combine(fx.Store.HistoryFolder, $"old.{i:D4}-20250101-000000.json");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }
        var t = Tweak("test.history", Reg("History"));
        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.True(fx.Engine.Revert(t).Success);

        var left = Directory.GetFiles(fx.Store.HistoryFolder, "*.json");
        Assert.Equal(BackupStore.KeptHistory, left.Length);
        Assert.Contains(left, f => Path.GetFileName(f).StartsWith("test.history-", StringComparison.Ordinal));
        Assert.DoesNotContain(left, f => Path.GetFileName(f).StartsWith("old.0000-", StringComparison.Ordinal));
    }

    /// <summary>The detection reads the backups once; another account's backup still blocks the tweak, as a single check does.</summary>
    [Fact]
    public void DetectAllSeesAnotherAccountsBackupFromItsOneRead()
    {
        using var fx = new EngineFixture();
        var store = new BackupStore(fx.BackupRoot, secure: false, userSid: "S-1-5-21-1-1-1-1002");
        var engine = new TweakEngine(fx.Context, store, fx.RestorePoints, "test", 26300);
        var t = Tweak("test.otherAccount", Reg("Other"));
        var legacy = new TweakBackup
        {
            TweakId = t.Id, Owner = "S-1-5-21-1-1-1-1001",
            Entries = [new BackupEntry { TargetKey = t.Actions[0].TargetKey, Description = "Other", Original = new StoredValue(false, "dword", null) }],
        };
        File.WriteAllText(Path.Combine(store.BackupFolder, "test.otherAccount.json"), System.Text.Json.JsonSerializer.Serialize(legacy));

        var status = Assert.Single(engine.DetectAll([t], Facts));
        Assert.Contains(status.Blocks, b => b.ReasonKey == "block.otherAccount");
        Assert.False(status.HasBackup);
        Assert.Contains(engine.Detect(t, Facts).Blocks, b => b.ReasonKey == "block.otherAccount");
        Assert.Contains(t.Id, store.ReadAll().OtherAccounts);
    }

    /// <summary>The originals are saved before the first write; when the save after the change fails, the result says so.</summary>
    [Fact]
    public async Task AFailedSaveAfterTheChangeIsReported()
    {
        using var fx = new EngineFixture();
        var file = Path.Combine(fx.Store.BackupFolder, "test.saveFails.json");
        // The change itself makes the backup file read-only, so the save that records what was written fails.
        fx.Processes.Handler = (_, args) =>
        {
            if (args.Contains("Disable-MMAgent")) File.SetAttributes(file, FileAttributes.ReadOnly);
            return null;
        };
        try
        {
            var r = await fx.Engine.ApplyAsync(Tweak("test.saveFails", new MemoryCompressionAction { Enabled = false }), Facts, Options);
            Assert.Equal(ApplyOutcome.Applied, r.Outcome);
            Assert.True(r.BackupNotUpdated);
            Assert.Equal("True", fx.Store.Get("test.saveFails")!.Entries.Single().Original.Data); // Undo still has the original
        }
        finally
        {
            if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ApplyReportsNothingWhenTheSaveWorks()
    {
        using var fx = new EngineFixture();
        var r = await fx.Engine.ApplyAsync(Tweak("test.saves", Reg("Saves")), Facts, Options);
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.False(r.BackupNotUpdated);
    }

    /// <summary>Explorer also honors the user's own block list: such an extension is off, and turning it on clears it there.</summary>
    [Fact]
    public async Task AShellExtensionBlockedByTheUserIsOffAndCanBeTurnedOn()
    {
        using var fx = new EngineFixture();
        const string clsid = "{23170F69-40C1-278A-1000-000100020000}";
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\7-Zip", "", "string", clsid);
        RegistryValue.Write(fx.Registry, Hive.User, Blocked, clsid, "string", "");

        var entry = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions());
        Assert.False(entry.Enabled);
        Assert.Equal(Hive.User, entry.Hive);
        var on = StartupTweaks.Set(entry, enabled: true)!;
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(on, Facts, Options)).Outcome);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.User, Blocked, clsid).Existed);
        Assert.True(Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions()).Enabled);
        Assert.True(fx.Engine.Revert(on).Success);
        Assert.True(RegistryValue.Read(fx.Registry, Hive.User, Blocked, clsid).Existed);

        // Blocked for the whole PC as well: the machine's list decides, as before.
        RegistryValue.Write(fx.Registry, Hive.Machine, Blocked, clsid, "string", "");
        Assert.Equal(Hive.Machine, Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, null).ShellExtensions()).Hive);
    }
}
