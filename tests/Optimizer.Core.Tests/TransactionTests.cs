using System.Text.Json;
using Microsoft.Win32;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Docs;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Apply and undo when something goes wrong half-way, when one part changed since, and with damaged backups.</summary>
public class TransactionTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false);
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private static TweakDefinition Tweak(string id, params TweakAction[] actions) => new()
    {
        Id = id,
        Category = "Test",
        Hidden = true,
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Actions = [.. actions],
    };

    private static RegistryAction Reg(string path, string name, object value, string? removeKeyOnUndo = null) => new()
    {
        Hive = Hive.Machine, Path = path, Name = name, Kind = "dword", Value = JsonSerializer.SerializeToElement(value), RemoveKeyOnUndo = removeKeyOnUndo,
    };

    private static PowerSettingAction Boost(uint? ac, uint? dc) => new() { Subgroup = "SUB_PROCESSOR", Setting = "PERFBOOSTMODE", Ac = ac, Dc = dc };

    private static (Guid, Guid, Guid) BoostKey(EngineFixture fx) =>
        (fx.Power.Active, PowerAliases.Resolve("SUB_PROCESSOR"), PowerAliases.Resolve("PERFBOOSTMODE"));

    private static TweakEngine Engine(EngineFixture fx, IPowerManager power) => new(new ActionContext
    {
        Registry = fx.Context.Registry, Services = fx.Context.Services, Power = power, Bcd = fx.Context.Bcd, Tasks = fx.Context.Tasks,
        Displays = fx.Context.Displays, Processes = fx.Context.Processes, PowerMode = fx.Context.PowerMode, Devices = fx.Context.Devices,
        Network = fx.Context.Network, MemoryAgent = fx.Context.MemoryAgent, Nvidia = fx.Context.Nvidia, Accessibility = fx.Context.Accessibility, ExportFolder = fx.Context.ExportFolder, NetworkInterfaceIds = fx.Context.NetworkInterfaceIds,
    }, fx.Store, fx.RestorePoints, "test", 26300);

    /// <summary>Writes the mains value, then fails on the battery value: the action itself is half-done.</summary>
    private sealed class FailingBatteryWrite(FakePower inner) : IPowerManager
    {
        public Guid ActiveScheme() => inner.ActiveScheme();
        public uint? ReadAc(Guid s, Guid g, Guid k) => inner.ReadAc(s, g, k);
        public uint? ReadDc(Guid s, Guid g, Guid k) => inner.ReadDc(s, g, k);
        public void WriteAc(Guid s, Guid g, Guid k, uint v) => inner.WriteAc(s, g, k, v);
        public void WriteDc(Guid s, Guid g, Guid k, uint v) => throw new InvalidOperationException("access denied");
        public void SetActive(Guid s) => inner.SetActive(s);
        public bool SchemeExists(Guid s) => inner.SchemeExists(s);
        public Guid Duplicate(Guid source, string name) => inner.Duplicate(source, name);
        public void Delete(Guid s) => inner.Delete(s);
        public IReadOnlyList<(Guid Id, string Name)> Schemes() => inner.Schemes();
        public void Export(Guid scheme, string file) => inner.Export(scheme, file);
        public bool? HibernationSupported() => inner.HibernationSupported();
    }

    [Fact]
    public async Task AnActionThatFailsHalfWayIsRolledBackItself()
    {
        using var fx = new EngineFixture();
        var engine = Engine(fx, new FailingBatteryWrite(fx.Power));
        var t = Tweak("test.boost", Boost(0, 0));
        var r = await engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.DoesNotContain("rollback failed", r.Error);
        // The mains value written before the failure is back, and nothing is left to undo.
        Assert.Equal(50u, fx.Power.ReadAc(BoostKey(fx).Item1, BoostKey(fx).Item2, BoostKey(fx).Item3));
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task AWriteWindowsRefusesIsNotReportedAsHalfChanged()
    {
        using var fx = new EngineFixture();
        fx.Services.Start["SvcA"] = ServiceStart.Automatic;
        fx.Services.FailOnWrite.Add("SvcA");
        var t = Tweak("test.refused", new ServiceAction { Name = "SvcA", StartType = ServiceStart.Disabled });
        var r = await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.DoesNotContain("rollback failed", r.Error);
        Assert.Null(fx.Store.Get(t.Id));
    }

    /// <summary>
    /// A failed apply that rolled back cleanly leaves an earlier backup as it was: the entry this run added for the new
    /// target would later read as "reset by Windows".
    /// </summary>
    [Fact]
    public async Task AFailedReapplyLeavesTheEarlierBackupAsItWas()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\PCOTest\Reapply";
        await fx.Engine.ApplyAsync(Tweak("test.reapply", Reg(path, "Value", 1)), Facts, new HashSet<string>(), Options);
        fx.Services.Start["SvcB"] = ServiceStart.Automatic;
        fx.Services.FailOnWrite.Add("SvcB");
        var wider = Tweak("test.reapply", Reg(path, "Value", 1), new ServiceAction { Name = "SvcB", StartType = ServiceStart.Disabled });

        Assert.Equal(ApplyOutcome.Failed, (await fx.Engine.ApplyAsync(wider, Facts, new HashSet<string>(), Options)).Outcome);
        var backup = fx.Store.Get("test.reapply")!;
        Assert.Single(backup.Entries);
        Assert.NotEqual(TweakState.RevertedByWindows, fx.Engine.DetectState(wider, Facts));
    }

    /// <summary>A rollback removes only a plan this apply created, not an existing plan with the same name.</summary>
    [Fact]
    public async Task RollbackKeepsAPlanThatExistedBefore()
    {
        using var fx = new EngineFixture();
        var existing = Guid.NewGuid();
        fx.Power.SchemeNames[existing] = "Tunero Test";
        fx.Services.Start["SvcC"] = ServiceStart.Automatic;
        fx.Services.FailOnWrite.Add("SvcC");
        var t = Tweak("test.planfail", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" },
            new ServiceAction { Name = "SvcC", StartType = ServiceStart.Disabled });

        Assert.Equal(ApplyOutcome.Failed, (await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options)).Outcome);
        Assert.True(fx.Power.SchemeNames.ContainsKey(existing));
        Assert.Equal(FakePower.Balanced, fx.Power.Active);
    }

    /// <summary>
    /// "Apply recommended": a tweak that is not batch-safe is skipped, and without a restore point the batch stops before
    /// the first change. The run after the user's decision applies each tweak exactly once; a tweak that fails rolls back
    /// only itself.
    /// </summary>
    [Fact]
    public async Task ApplyRecommendedSkipsUnsafeTweaksAndStopsBeforeChangingAnything()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\PCOTest\Batch";
        var a = Tweak("test.batchA", Reg(path, "A", 1));
        var preview = new TweakDefinition
        {
            Id = "test.batchPreview", Category = "Test", Hidden = true, Preview = true,
            Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] }, Actions = [Reg(path, "P", 1)],
        };
        fx.Services.Start["SvcBatch"] = ServiceStart.Automatic;
        fx.Services.FailOnWrite.Add("SvcBatch");
        var failing = Tweak("test.batchFail", Reg(path, "F", 1), new ServiceAction { Name = "SvcBatch", StartType = ServiceStart.Disabled });
        var b = Tweak("test.batchB", Reg(path, "B", 1));
        TweakDefinition[] batch = [a, preview, failing, b];

        fx.RestorePoints.Enabled = false;
        var first = await fx.Engine.ApplyBatchAsync(batch, Facts, new HashSet<string>(), new ApplyOptions { ExpertMode = true });
        Assert.Equal(ApplyOutcome.NeedsRestorePointDecision, Assert.Single(first).Result.Outcome);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, path, "A").Existed);

        var second = await fx.Engine.ApplyBatchAsync(batch, Facts, new HashSet<string>(), Options);
        Assert.Equal([ApplyOutcome.Applied, ApplyOutcome.Blocked, ApplyOutcome.Failed, ApplyOutcome.Applied], second.Select(r => r.Result.Outcome));
        Assert.Contains(second[1].Result.Blocks!, x => x.ReasonKey == "block.notBatchSafe");
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, path, "P").Existed);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, path, "F").Existed); // rolled back with its failed service
        Assert.Equal("1", RegistryValue.Read(fx.Registry, Hive.Machine, path, "B").Data);

        // Once more: everything already applied is left alone.
        var third = await fx.Engine.ApplyBatchAsync([a, b], Facts, new HashSet<string>(), Options);
        Assert.All(third, r => Assert.Equal(ApplyOutcome.NothingToDo, r.Result.Outcome));
    }

    /// <summary>A backup file another process holds open is not "no backup": apply and undo refuse instead of guessing.</summary>
    [Fact]
    public async Task ALockedBackupBlocksApplyAndUndo()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\PCOTest\Locked";
        var t = Tweak("test.locked", Reg(path, "Value", 1));
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        using (new FileStream(Path.Combine(fx.Store.BackupFolder, "test.locked.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<BackupUnreadableException>(() => fx.Store.Get(t.Id));
            Assert.True(fx.Store.Exists(t.Id));
            Assert.False(fx.Engine.Revert(t).Success);
            RegistryValue.Write(fx.Registry, Hive.Machine, path, "Value", "dword", "0");
            Assert.Equal(ApplyOutcome.Failed, (await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options)).Outcome);
        }
        Assert.NotNull(fx.Store.Get(t.Id)); // the originals are still there
    }

    [Fact]
    public async Task OnlyTheSideThatIsStillAsAppliedIsUndone()
    {
        using var fx = new EngineFixture();
        var t = Tweak("test.boost", Boost(0, 0));
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options)).Outcome);
        var (s, g, k) = BoostKey(fx);
        fx.Power.WriteAc(s, g, k, 3); // the user picked another mains value since
        var r = fx.Engine.Revert(t);
        Assert.True(r.Success);
        Assert.Single(r.AlreadyRevertedByWindows);
        Assert.Equal(3u, fx.Power.ReadAc(s, g, k));  // the user's choice stays
        Assert.Equal(50u, fx.Power.ReadDc(s, g, k)); // the battery value goes back
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task ADamagedBackupBlocksApplyAndIsKept()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\PCOTest\Damaged";
        var t = Tweak("test.damaged", Reg(path, "Value", 1));
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        var file = Path.Combine(fx.Store.BackupFolder, "test.damaged.json");
        File.WriteAllText(file, "{\"TweakId\":\"test.dam"); // cut off by a power loss

        Assert.Null(fx.Store.Get(t.Id));
        Assert.True(fx.Store.IsDamaged(t.Id));
        Assert.True(File.Exists(file + BackupStore.DamagedSuffix));
        var r = await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        Assert.Equal(ApplyOutcome.Blocked, r.Outcome);
        Assert.Contains(r.Blocks!, b => b.ReasonKey == "block.backupDamaged");
        Assert.False(fx.Engine.Revert(t).Success);
        Assert.True(Labels.Current.Has("en", "block.backupDamaged") && Labels.Current.Has("de", "block.backupDamaged"));
    }

    /// <summary>A damaged backup that cannot be moved aside (a scanner holds it) is not "no backup": apply and undo stop.</summary>
    [Fact]
    public async Task ADamagedBackupThatCannotBeMovedAsideIsNotIgnored()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\PCOTest\DamagedHeld";
        var t = Tweak("test.damagedheld", Reg(path, "Value", 1));
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        var file = Path.Combine(fx.Store.BackupFolder, "test.damagedheld.json");
        File.WriteAllText(file, "{\"TweakId\":\"test.dam");
        using (var key = fx.Registry.Open(Hive.Machine, path, writable: true)) key!.SetValue("Value", 0); // so apply has work to do
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<BackupUnreadableException>(() => fx.Store.Get(t.Id));
            Assert.Equal(ApplyOutcome.Failed, (await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options)).Outcome);
            Assert.False(fx.Engine.Revert(t).Success);
        }
        Assert.Equal("{\"TweakId\":\"test.dam", File.ReadAllText(file));
        Assert.True(Labels.Current.Has("en", "block.otherAccount") && Labels.Current.Has("de", "block.otherAccount"));
    }

    [Fact]
    public void BackupsOfIdsWithReplacedCharactersDoNotShareAFile()
    {
        using var fx = new EngineFixture();
        fx.Store.Save(new TweakBackup { TweakId = "startup.a:b" });
        fx.Store.Save(new TweakBackup { TweakId = "startup.a/b" });
        Assert.Equal("startup.a:b", fx.Store.Get("startup.a:b")!.TweakId);
        Assert.Equal("startup.a/b", fx.Store.Get("startup.a/b")!.TweakId);
    }

    [Fact]
    public async Task PowerModeIsUndoneOnThePowerSourceItWasChangedOn()
    {
        using var fx = new EngineFixture();
        var efficiency = Guid.NewGuid();
        var t = Tweak("test.powermode", new PowerModeAction { Overlay = efficiency });
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        Assert.Equal(efficiency, fx.PowerMode.Current);

        fx.PowerMode.Battery = true; // unplugged: Windows shows the battery mode now
        fx.PowerMode.Current = Guid.Empty;
        Assert.NotEqual(TweakState.RevertedByWindows, fx.Engine.DetectState(t, Facts));
        var onBattery = fx.Engine.Revert(t);
        Assert.False(onBattery.Success);
        Assert.NotNull(fx.Store.Get(t.Id)); // kept for later
        Assert.Equal(Guid.Empty, fx.PowerMode.Current);

        fx.PowerMode.Battery = false;
        fx.PowerMode.Current = efficiency;
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(Guid.Empty, fx.PowerMode.Current);
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task UndoRemovesTheKeysApplyCreatedButNotKeysThatExisted()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Policies", "Other", "dword", "1");
        using (fx.Registry.Open(Hive.Machine, @"SOFTWARE\Policies\EmptyBefore", writable: true, create: true)) { }

        var created = Tweak("test.created", Reg(@"SOFTWARE\Policies\PCOTest\Sub", "Value", 1));
        var existing = Tweak("test.existing", Reg(@"SOFTWARE\Policies\EmptyBefore", "Value", 1, removeKeyOnUndo: @"SOFTWARE\Policies\EmptyBefore"));
        await fx.Engine.ApplyAsync(created, Facts, new HashSet<string>(), Options);
        await fx.Engine.ApplyAsync(existing, Facts, new HashSet<string>(), Options);
        fx.Engine.Revert(created);
        fx.Engine.Revert(existing);

        using (var gone = fx.Registry.Open(Hive.Machine, @"SOFTWARE\Policies\PCOTest", false)) Assert.Null(gone);
        using (var policies = fx.Registry.Open(Hive.Machine, @"SOFTWARE\Policies", false)) Assert.NotNull(policies);
        using (var kept = fx.Registry.Open(Hive.Machine, @"SOFTWARE\Policies\EmptyBefore", false)) Assert.NotNull(kept);
    }

    [Fact]
    public void RawRegistryValuesKeepTheirBytes()
    {
        using var fx = new EngineFixture();
        using (var key = fx.Registry.Open(Hive.Machine, @"SOFTWARE\PCOTest", writable: true, create: true))
            key!.SetValue("Raw", new byte[] { 1, 2, 3 }, RegistryValueKind.None);
        var read = RegistryValue.Read(fx.Registry, Hive.Machine, @"SOFTWARE\PCOTest", "Raw");
        Assert.Equal(new StoredValue(true, "none", "010203"), read);
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\PCOTest", "Copy", read.Kind!, read.Data!);
        Assert.Equal(read, RegistryValue.Read(fx.Registry, Hive.Machine, @"SOFTWARE\PCOTest", "Copy"));
    }

    [Fact]
    public void AStringIsNotTheSameAsANumberWithTheSameText()
    {
        Assert.False(new StoredValue(true, "string", "1").SameAs(new StoredValue(true, "dword", "1")));
        Assert.True(new StoredValue(true, "dword", "1").SameAs(new StoredValue(true, "dword", "1")));
        Assert.True(new StoredValue(true, null, "1").SameAs(new StoredValue(true, "dword", "1"))); // older backups without a kind
        Assert.True(new StoredValue(false, "missingKeys:2").SameAs(StoredValue.Missing));
    }

    [Fact]
    public async Task HibernationIsTurnedOffAndBackOn()
    {
        using var fx = new EngineFixture();
        const string power = @"SYSTEM\CurrentControlSet\Control\Power";
        RegistryValue.Write(fx.Registry, Hive.Machine, power, "HibernateEnabled", "dword", "1");
        fx.Processes.Handler = (file, args) =>
        {
            if (file != "powercfg.exe" || !args.StartsWith("/hibernate", StringComparison.Ordinal)) return null;
            RegistryValue.Write(fx.Registry, Hive.Machine, power, "HibernateEnabled", "dword", args.EndsWith("on", StringComparison.Ordinal) ? "1" : "0");
            return (0, "");
        };
        var t = Tweak("test.hibernate", new HibernationAction { Enabled = false });
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options)).Outcome);
        Assert.Equal("0", RegistryValue.Read(fx.Registry, Hive.Machine, power, "HibernateEnabled").Data);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("1", RegistryValue.Read(fx.Registry, Hive.Machine, power, "HibernateEnabled").Data);
        Assert.Contains("powercfg.exe /hibernate on", fx.Processes.Calls);
    }

    [Fact]
    public async Task RefreshRateIsRestoredAndADisconnectedDisplayIsLeftAlone()
    {
        using var fx = new EngineFixture();
        fx.Displays.Refresh[@"\\.\DISPLAY1"] = 60;
        var t = Tweak("test.display", new DisplayModeAction { GdiName = @"\\.\DISPLAY1", Width = 2560, Height = 1440, RefreshHz = 144 });
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        Assert.Equal(144, fx.Displays.Refresh[@"\\.\DISPLAY1"]);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(60, fx.Displays.Refresh[@"\\.\DISPLAY1"]);

        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        fx.Displays.Refresh.Remove(@"\\.\DISPLAY1"); // monitor unplugged
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task UndoRestoresTheOriginalWhenWindowsRemovedAValueWhoseAbsenceMeansTheSame()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\TuneroTest\GameMode";
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "Auto", "dword", "0");
        var t = Tweak("test.gameMode", new RegistryAction
        {
            Hive = Hive.Machine, Path = path, Name = "Auto", Value = System.Text.Json.JsonSerializer.SerializeToElement(1),
            MissingMeans = System.Text.Json.JsonSerializer.SerializeToElement(1),
        });
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        RegistryValue.Delete(fx.Registry, Hive.Machine, path, "Auto"); // a feature update removes the value; Windows assumes 1

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("0", RegistryValue.Read(fx.Registry, Hive.Machine, path, "Auto")!.Data);
    }

    [Fact]
    public async Task UndoOfTheRefreshRateKeepsAResolutionChosenSince()
    {
        using var fx = new EngineFixture();
        fx.Displays.Refresh[@"\\.\DISPLAY1"] = 60;
        var t = Tweak("test.display", new DisplayModeAction { GdiName = @"\\.\DISPLAY1", Width = 2560, Height = 1440, RefreshHz = 144 });
        await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), Options);
        fx.Displays.Size[@"\\.\DISPLAY1"] = (1920, 1080); // the user picks another resolution in Settings

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(60, fx.Displays.Refresh[@"\\.\DISPLAY1"]);
        Assert.Equal((1920, 1080), fx.Displays.Size[@"\\.\DISPLAY1"]);
    }

    [Fact]
    public async Task TheRestorePointLimitGoesBackWhenNoRestorePointWasMade()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.CreateFails = true;
        var t = Tweak("test.needsPoint", Reg(@"SOFTWARE\PCOTest", "Value", 1));
        var r = await fx.Engine.ApplyAsync(t, Facts, new HashSet<string>(), new ApplyOptions { ExpertMode = true });
        Assert.Equal(ApplyOutcome.NeedsRestorePointDecision, r.Outcome);
        Assert.Null(fx.Store.Get(TweakEngine.RestorePointFrequencyTweak));
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency").Existed);
    }
}
