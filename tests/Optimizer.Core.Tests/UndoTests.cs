using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Undo in the cases that went wrong before: removed tweaks, plan switches, keys that existed, value types.</summary>
public class UndoTests
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

    private static RegistryAction Reg(string path, string name, object value, string kind = "dword", string? removeKeyOnUndo = null) => new()
    {
        Hive = Hive.Machine, Path = path, Name = name, Kind = kind, Value = JsonSerializer.SerializeToElement(value), RemoveKeyOnUndo = removeKeyOnUndo,
    };

    /// <summary>
    /// A newer catalog version asks for another value under the same target: the value the app wrote is still there, so
    /// this is not a reset by Windows (no drift notice), and undo still restores the original.
    /// </summary>
    [Fact]
    public async Task NewValueInTheCatalogIsNotDrift()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\Test\Drift";
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "Level", "dword", "0");
        await fx.Engine.ApplyAsync(Tweak("test.level", Reg(path, "Level", 1)), Facts, Options);

        var newer = Tweak("test.level", Reg(path, "Level", 2));
        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(newer, Facts));

        // Windows resets it: that is drift.
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "Level", "dword", "0");
        Assert.Equal(TweakState.RevertedByWindows, fx.Engine.DetectState(newer, Facts));
    }

    [Fact]
    public void ChangeOfARemovedTweakCanStillBeUndone()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "SystemResponsiveness", "dword", "10");
        // Backup as an earlier version wrote it for a catalog tweak that no longer exists (no stored definition).
        var action = Reg(path, "SystemResponsiveness", 10);
        fx.Store.Save(new TweakBackup
        {
            TweakId = "latency.mmcss",
            Entries =
            [
                new BackupEntry
                {
                    TargetKey = action.TargetKey, Description = "SystemResponsiveness", Original = new StoredValue(true, "dword", "20"),
                    Applied = new StoredValue(true, "dword", "10"), Action = JsonSerializer.Serialize<TweakAction>(action, TweakCatalog.JsonOptions),
                },
            ],
        });

        var t = fx.Engine.Resolve("latency.mmcss");
        Assert.NotNull(t);
        Assert.Equal("retired", t!.DocId);
        Assert.Single(fx.Engine.RevertAll());
        Assert.Equal("20", RegistryValue.Read(fx.Registry, Hive.Machine, path, "SystemResponsiveness").Data);
        Assert.Null(fx.Store.Get("latency.mmcss"));
    }

    /// <summary>
    /// The data folder is shared by every Windows account. A backup of one account's own settings is kept for that
    /// account; another account neither sees it nor takes its originals, while backups of PC-wide settings are shared.
    /// </summary>
    [Fact]
    public void BackupsOfAnAccountsOwnSettingsStayWithThatAccount()
    {
        var root = TestFolders.Create("peruser");
        try
        {
            var alice = new BackupStore(root, secure: false, userSid: "S-1-5-21-1-1-1-1001");
            var bob = new BackupStore(root, secure: false, userSid: "S-1-5-21-1-1-1-1002");
            BackupEntry Entry(Hive hive) => new()
            {
                TargetKey = new RegistryAction { Hive = hive, Path = @"Software\Test", Name = "V" }.TargetKey,
                Description = "V", Original = new StoredValue(true, "dword", "1"), Applied = new StoredValue(true, "dword", "0"),
            };
            alice.Save(new TweakBackup { TweakId = "personalize.darkMode", Entries = [Entry(Hive.User)] });
            alice.Save(new TweakBackup { TweakId = "power.hibernateOff", Entries = [Entry(Hive.Machine)] });

            Assert.Equal("S-1-5-21-1-1-1-1001", alice.Get("personalize.darkMode")!.Owner);
            Assert.Null(bob.Get("personalize.darkMode"));
            Assert.Equal(["power.hibernateOff"], bob.All().Select(b => b.TweakId));
            Assert.Equal(2, alice.All().Count);

            // Bob applies the same tweak: his own backup, Alice's stays as it was.
            bob.Save(new TweakBackup { TweakId = "personalize.darkMode", Entries = [Entry(Hive.User)] });
            Assert.Equal("S-1-5-21-1-1-1-1002", bob.Get("personalize.darkMode")!.Owner);
            Assert.Equal("S-1-5-21-1-1-1-1001", alice.Get("personalize.darkMode")!.Owner);
            bob.Archive("personalize.darkMode");
            Assert.Null(bob.Get("personalize.darkMode"));
            Assert.NotNull(alice.Get("personalize.darkMode"));

            Assert.True(BackupStore.IsPerUser(Entry(Hive.User).TargetKey));
            Assert.True(BackupStore.IsPerUser("spi:stickykeys:hotkey"));
            Assert.False(BackupStore.IsPerUser(Entry(Hive.Machine).TargetKey));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    /// <summary>
    /// What is left of a backup after its per-user entries were restored concerns the whole PC: it belongs to no account,
    /// so another account sees it (and does not record changed values as originals). A shared backup an older version
    /// wrote for one account is never overwritten or deleted by another.
    /// </summary>
    [Fact]
    public void ASharedBackupIsNeverTakenOverByAnotherAccount()
    {
        var root = TestFolders.Create("peruser-owner");
        try
        {
            var alice = new BackupStore(root, secure: false, userSid: "S-1-5-21-1-1-1-1001");
            var bob = new BackupStore(root, secure: false, userSid: "S-1-5-21-1-1-1-1002");
            BackupEntry Entry(Hive hive) => new()
            {
                TargetKey = new RegistryAction { Hive = hive, Path = @"Software\Test", Name = "V" }.TargetKey,
                Description = "V", Original = new StoredValue(true, "dword", "1"), Applied = new StoredValue(true, "dword", "0"),
            };
            var mixed = new TweakBackup { TweakId = "test.mixed", Entries = [Entry(Hive.User), Entry(Hive.Machine)] };
            alice.Save(mixed);
            mixed.Entries.RemoveAt(0); // the per-user part was undone
            alice.Save(mixed);
            Assert.Null(bob.Get("test.mixed")!.Owner);
            Assert.False(bob.HeldByAnotherAccount("test.mixed"));

            // A shared backup with an owner (older version): Bob neither sees it nor may replace it.
            var legacy = new TweakBackup { TweakId = "test.legacy", Owner = "S-1-5-21-1-1-1-1001", Entries = [Entry(Hive.Machine)] };
            System.IO.File.WriteAllText(Path.Combine(root, "backups", "test.legacy.json"), System.Text.Json.JsonSerializer.Serialize(legacy));
            Assert.Null(bob.Get("test.legacy"));
            Assert.True(bob.HeldByAnotherAccount("test.legacy"));
            Assert.Throws<IOException>(() => bob.Save(new TweakBackup { TweakId = "test.legacy", Entries = [Entry(Hive.Machine)] }));
            bob.Save(new TweakBackup { TweakId = "test.legacy", Entries = [Entry(Hive.User)] });
            Assert.Equal("S-1-5-21-1-1-1-1001", alice.Get("test.legacy")!.Owner);
            Assert.Equal("S-1-5-21-1-1-1-1002", bob.Get("test.legacy")!.Owner);
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    /// <summary>
    /// A partial undo of a tweak with a per-user and a PC-wide entry leaves only the PC-wide one: that backup moves to the
    /// shared folder, and the account's older copy (with both entries) must not be read first or survive the archive.
    /// </summary>
    [Fact]
    public void ABackupThatLosesItsPerUserEntriesLeavesNoStaleCopy()
    {
        var root = TestFolders.Create("peruser-partial");
        try
        {
            var store = new BackupStore(root, secure: false, userSid: "S-1-5-21-1-1-1-1001");
            BackupEntry Entry(Hive hive) => new()
            {
                TargetKey = new RegistryAction { Hive = hive, Path = @"Software\Test", Name = "V" }.TargetKey,
                Description = "V", Original = new StoredValue(true, "dword", "1"), Applied = new StoredValue(true, "dword", "0"),
            };
            store.Save(new TweakBackup { TweakId = "test.mixed", Entries = [Entry(Hive.User), Entry(Hive.Machine)] });
            store.Save(new TweakBackup { TweakId = "test.mixed", Entries = [Entry(Hive.Machine)] });

            Assert.Equal([Entry(Hive.Machine).TargetKey], store.Get("test.mixed")!.Entries.Select(e => e.TargetKey));
            Assert.Single(store.All());
            store.Archive("test.mixed");
            Assert.Null(store.Get("test.mixed"));
            Assert.Empty(store.All());
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public async Task PowerSettingKeepsTheOriginalOfEveryPlanItChanged()
    {
        using var fx = new EngineFixture();
        var setting = new PowerSettingAction { Subgroup = "SUB_PROCESSOR", Setting = "PERFBOOSTMODE", Ac = 2 };
        var t = Tweak("test.boost", setting);
        var sub = PowerAliases.Resolve("SUB_PROCESSOR");
        var key = PowerAliases.Resolve("PERFBOOSTMODE");
        fx.Power.Ac[(FakePower.Balanced, sub, key)] = 0;
        fx.Power.Ac[(FakePower.High, sub, key)] = 1;

        await fx.Engine.ApplyAsync(t, Facts, Options); // on Balanced
        fx.Power.Active = FakePower.High;
        // Another plan is active: the tweak is simply not applied there, not "reset by Windows".
        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(t, Facts));
        await fx.Engine.ApplyAsync(t, Facts, Options); // on High performance

        Assert.Equal(2, fx.Store.Get(t.Id)!.Entries.Count);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(0u, fx.Power.Ac[(FakePower.Balanced, sub, key)]);
        Assert.Equal(1u, fx.Power.Ac[(FakePower.High, sub, key)]);
    }

    /// <summary>
    /// A later catalog version writes less under the same target (mains only). Applying it again after Windows reset
    /// the mains value must not replace the stored action: undo restores what the first version changed, battery too.
    /// </summary>
    [Fact]
    public async Task ReapplyWithANewerDefinitionKeepsTheFirstUndoAction()
    {
        using var fx = new EngineFixture();
        var sub = PowerAliases.Resolve("SUB_PROCESSOR");
        var key = PowerAliases.Resolve("PERFBOOSTMODE");
        fx.Power.Ac[(FakePower.Balanced, sub, key)] = 0;
        fx.Power.Dc[(FakePower.Balanced, sub, key)] = 0;
        var v1 = Tweak("test.boost", new PowerSettingAction { Subgroup = "SUB_PROCESSOR", Setting = "PERFBOOSTMODE", Ac = 2, Dc = 2 });
        var v2 = Tweak("test.boost", new PowerSettingAction { Subgroup = "SUB_PROCESSOR", Setting = "PERFBOOSTMODE", Ac = 2 });

        await fx.Engine.ApplyAsync(v1, Facts, Options);
        fx.Power.Ac[(FakePower.Balanced, sub, key)] = 0; // reset by Windows
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(v2, Facts, Options)).Outcome);

        Assert.True(fx.Engine.Revert(v2).Success);
        Assert.Equal(0u, fx.Power.Ac[(FakePower.Balanced, sub, key)]);
        Assert.Equal(0u, fx.Power.Dc[(FakePower.Balanced, sub, key)]);
    }

    /// <summary>A token tweak changes only its token: parts it cannot read and other tokens come back exactly.</summary>
    [Fact]
    public async Task TokenTweaksKeepTheRestOfTheValue()
    {
        using var fx = new EngineFixture();
        const string path = @"Software\PCOTest\DirectX";
        const string original = "VRROptimizeEnable=0;SomeFlag;AutoHDREnable=1;";
        RegistryValue.Write(fx.Registry, Hive.User, path, "Settings", "string", original);
        var t = Tweak("test.token", new RegistryTokenAction { Hive = Hive.User, Path = path, Name = "Settings", Token = "SwapEffectUpgradeEnable", Value = "1" });

        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Equal(original + "SwapEffectUpgradeEnable=1;", RegistryValue.Read(fx.Registry, Hive.User, path, "Settings").Data);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(original, RegistryValue.Read(fx.Registry, Hive.User, path, "Settings").Data);

        Assert.Equal("A=2;B=1;", RegistryTokenAction.WithToken("A=1;B=1;", "a", "2"));
        Assert.Equal("B=1;", RegistryTokenAction.WithToken("A=1;B=1;A=3;", "A", null));
    }

    [Fact]
    public async Task CreatedPlanIsRemovedEvenIfTheUserSwitchedPlans()
    {
        using var fx = new EngineFixture();
        var t = Tweak("test.plan", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" });
        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Contains("Tunero Test", fx.Power.SchemeNames.Values);

        fx.Power.Active = FakePower.High; // the user picked another plan
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(FakePower.High, fx.Power.Active); // the user's choice stays
        Assert.DoesNotContain("Tunero Test", fx.Power.SchemeNames.Values);
    }

    /// <summary>A value of a type .NET does not name (REG_DWORD_BIG_ENDIAN) comes back with its own type, not as REG_NONE.</summary>
    [Fact]
    public async Task UndoKeepsARawRegistryType()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\PCOTest\Raw";
        byte[] original = [0, 0, 0, 7];
        using (var key = fx.Registry.Open(Hive.Machine, path, writable: true, create: true)!)
            Assert.Equal(0, RegSetValueExW(key.Handle, "Value", 0, 5 /* REG_DWORD_BIG_ENDIAN */, original, original.Length));
        var t = Tweak("test.raw", Reg(path, "Value", 1));
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, Options)).Outcome);
        Assert.Equal("raw:5", fx.Store.Get(t.Id)!.Entries.Single().Original.Kind);
        Assert.True(fx.Engine.Revert(t).Success);
        using (var key = fx.Registry.Open(Hive.Machine, path, writable: false)!)
        {
            Assert.Equal(0, RegQueryValueExW(key.Handle, "Value", IntPtr.Zero, out var type, IntPtr.Zero, IntPtr.Zero));
            Assert.Equal(5u, type);
            Assert.Equal(original, (byte[])key.GetValue("Value")!);
        }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegSetValueExW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, int reserved, uint type, byte[] data, int size);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegQueryValueExW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, IntPtr reserved, out uint type, IntPtr data, IntPtr size);

    /// <summary>A plan with the app's name that existed before the apply (an earlier run's, or the user's own) is only activated: undo switches back and keeps it.</summary>
    [Fact]
    public async Task ExistingPlanWithTheNameIsKeptOnUndo()
    {
        using var fx = new EngineFixture();
        var existing = Guid.NewGuid();
        fx.Power.SchemeNames[existing] = "Tunero Test";
        var t = Tweak("test.plan", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" });
        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Equal(existing, fx.Power.Active);
        Assert.Equal(PowerSchemeAction.ExistingKind, fx.Store.Get(t.Id)!.Entries.Single().Applied!.Kind);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(FakePower.Balanced, fx.Power.Active);
        Assert.True(fx.Power.SchemeExists(existing));
    }

    /// <summary>
    /// Applied, then the user picked another plan, then "Apply again": the plan is found by its name the second time, but
    /// it is still the one the first apply created, so undo removes it.
    /// </summary>
    [Fact]
    public async Task ApplyAgainAfterAPlanSwitchStillRemovesTheCreatedPlan()
    {
        using var fx = new EngineFixture();
        var t = Tweak("test.plan", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" });
        await fx.Engine.ApplyAsync(t, Facts, Options);
        fx.Power.Active = FakePower.Balanced;
        await fx.Engine.ApplyAsync(Tweak("test.plan", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" }), Facts, Options);
        Assert.Equal(PowerSchemeAction.CreatedKind, fx.Store.Get(t.Id)!.Entries.Single().Applied!.Kind);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(FakePower.Balanced, fx.Power.Active);
        Assert.DoesNotContain("Tunero Test", fx.Power.SchemeNames.Values);
    }

    /// <summary>A read that fails is not a target that is absent: the apply stops before it writes anything.</summary>
    [Fact]
    public async Task AReadThatFailsStopsTheApply()
    {
        using var fx = new EngineFixture();
        const string path = @"SOFTWARE\Test\ReadFails";
        var t = Tweak("test.readFails", Reg(path, "A", 1), new ThrowingRead());
        var r = await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, path, "A").Existed);
        Assert.Null(fx.Store.Get(t.Id));
    }

    private sealed class ThrowingRead : TweakAction
    {
        public override string TargetKey => "test:throwing";
        public override StoredValue Desired(ActionContext c) => new(true, "test", "1");
        public override StoredValue? Read(ActionContext c) => throw new TimeoutException("the tool did not answer");
        public override ActionState State(ActionContext c) => ActionState.NotApplied;
        public override void Apply(ActionContext c) { }
        public override void Restore(ActionContext c, StoredValue original) { }
        public override TargetText TargetLabel(ActionContext c) => TargetText.Verbatim("test target");
    }

    /// <summary>Backups written before the kind was recorded ("scheme") keep the old behavior: the plan counts as created.</summary>
    [Fact]
    public async Task OlderBackupsStillRemoveTheirPlan()
    {
        using var fx = new EngineFixture();
        var t = Tweak("test.plan", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" });
        await fx.Engine.ApplyAsync(t, Facts, Options);
        var backup = fx.Store.Get(t.Id)!;
        Assert.Equal(PowerSchemeAction.CreatedKind, backup.Entries.Single().Applied!.Kind);
        backup.Entries.Single().Applied = backup.Entries.Single().Applied! with { Kind = "scheme" };
        fx.Store.Save(backup);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.DoesNotContain("Tunero Test", fx.Power.SchemeNames.Values);
    }

    [Fact]
    public async Task CreatedPlanIsRemovedWhenThePreviousPlanIsGone()
    {
        using var fx = new EngineFixture();
        var custom = Guid.NewGuid();
        fx.Power.SchemeNames[custom] = "My plan";
        fx.Power.Active = custom;
        var t = Tweak("test.plan", new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Test" });
        await fx.Engine.ApplyAsync(t, Facts, Options);
        fx.Power.SchemeNames.Remove(custom);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(FakePower.Balanced, fx.Power.Active);
        Assert.DoesNotContain("Tunero Test", fx.Power.SchemeNames.Values);
    }

    [Fact]
    public async Task UndoRemovesOnlyKeysThatAreEmptyAfterwards()
    {
        using var fx = new EngineFixture();
        const string root = @"Software\Classes\CLSID\{test}";
        const string path = root + @"\InprocServer32";
        var t = Tweak("test.keys", Reg(path, "", "", "string", root));

        // Keys did not exist: undo removes both.
        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.True(fx.Engine.Revert(t).Success);
        using (var k = fx.Registry.Open(Hive.Machine, root, writable: false)) Assert.Null(k);

        // The outer key existed with a value of its own: it stays, only the created subkey goes.
        RegistryValue.Write(fx.Registry, Hive.Machine, root, "Keep", "string", "x");
        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("x", RegistryValue.Read(fx.Registry, Hive.Machine, root, "Keep").Data);
        using (var k = fx.Registry.Open(Hive.Machine, path, writable: false)) Assert.Null(k);

        // The inner key existed with another value: nothing is removed.
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "ThreadingModel", "string", "Both");
        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("Both", RegistryValue.Read(fx.Registry, Hive.Machine, path, "ThreadingModel").Data);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, path, "").Existed);
    }

    [Fact]
    public void NicActionReadBackFromABackupNamesTheSameTarget()
    {
        var adapter = new NicAdapter(@"SYSTEM\Class\0001", "{11111111-1111-1111-1111-111111111111}", "Test NIC", @"PCI\VEN_8086", false,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["*eee"] = ["0", "1"] });
        var action = new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" }, Adapter = adapter };
        var stored = JsonSerializer.Deserialize<TweakAction>(JsonSerializer.Serialize<TweakAction>(action, TweakCatalog.JsonOptions), TweakCatalog.JsonOptions)!;
        Assert.Contains("*eee", action.TargetKey);
        Assert.Equal(action.TargetKey, stored.TargetKey);
    }

    [Fact]
    public async Task NicKeywordStoredAsDwordStaysADword()
    {
        using var fx = new EngineFixture();
        var key = $@"{NicAdapters.ClassPath}\0001";
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "NetCfgInstanceId", "string", "{11111111-1111-1111-1111-111111111111}");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "DeviceInstanceID", "string", @"PCI\VEN_8086&DEV_15BC\3");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "Characteristics", "dword", "132");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{key}\Ndi\params\*EEE\enum", "0", "string", "Disabled");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{key}\Ndi\params\*EEE\enum", "1", "string", "Enabled");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "*EEE", "dword", "1");
        var t = Tweak("test.eee", new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" } });

        await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Equal(new StoredValue(true, "dword", "0"), RegistryValue.Read(fx.Registry, Hive.Machine, key, "*EEE"));
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(new StoredValue(true, "dword", "1"), RegistryValue.Read(fx.Registry, Hive.Machine, key, "*EEE"));
    }

    /// <summary>
    /// The adapter is replaced and Windows gives its class key number to the new one: the undo stored for the old adapter
    /// writes nothing there (reported as changed since).
    /// </summary>
    [Fact]
    public async Task NicUndoNeverWritesIntoAnotherAdapter()
    {
        using var fx = new EngineFixture();
        var key = $@"{NicAdapters.ClassPath}\0001";
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "NetCfgInstanceId", "string", "{11111111-1111-1111-1111-111111111111}");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "DeviceInstanceID", "string", @"PCI\VEN_8086&DEV_15BC\3");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "Characteristics", "dword", "132");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{key}\Ndi\params\*EEE\enum", "0", "string", "Disabled");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{key}\Ndi\params\*EEE\enum", "1", "string", "Enabled");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "*EEE", "string", "1");
        var t = Tweak("test.eee", new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" } });
        await fx.Engine.ApplyAsync(t, Facts, Options);

        // Another adapter in the same slot, with the value the undo would compare against.
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "NetCfgInstanceId", "string", "{22222222-2222-2222-2222-222222222222}");
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "*EEE", "string", "0");

        var result = fx.Engine.Revert(t);
        Assert.Single(result.AlreadyRevertedByWindows);
        Assert.Equal("0", RegistryValue.Read(fx.Registry, Hive.Machine, key, "*EEE").Data);
    }

    [Fact]
    public async Task VisualEffectsChangeOnlyTheirOwnBitsOfUserPreferencesMask()
    {
        using var fx = new EngineFixture();
        const string path = @"Control Panel\Desktop";
        // Windows default plus "activate a window by hovering" (bit 0) and "underline access keys" (bit 5).
        RegistryValue.Write(fx.Registry, Hive.User, path, "UserPreferencesMask", "binary", "BF1E078012000000");
        var t = TweakCatalog.Current.Get("visual.bestPerformance")!;
        var mask = t.Actions.OfType<RegistryBinaryBitsAction>().Single();
        var only = Tweak("test.mask", mask);

        await fx.Engine.ApplyAsync(only, Facts, Options);
        Assert.Equal("B112038010000000", RegistryValue.Read(fx.Registry, Hive.User, path, "UserPreferencesMask").Data); // bits 0 and 5 kept

        // The user turns off "underline access keys" meanwhile: not a reset by Windows, and undo keeps the user's choice.
        RegistryValue.Write(fx.Registry, Hive.User, path, "UserPreferencesMask", "binary", "9112038010000000");
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(only, Facts));
        Assert.True(fx.Engine.Revert(only).Success);
        Assert.Equal("9F1E078012000000", RegistryValue.Read(fx.Registry, Hive.User, path, "UserPreferencesMask").Data);
    }

    [Fact]
    public void EthernetMeansInterfaceType6()
    {
        using var fx = new EngineFixture();
        void Add(string sub, string guid, int ifType)
        {
            var key = $@"{NicAdapters.ClassPath}\{sub}";
            RegistryValue.Write(fx.Registry, Hive.Machine, key, "NetCfgInstanceId", "string", guid);
            RegistryValue.Write(fx.Registry, Hive.Machine, key, "DeviceInstanceID", "string", @"PCI\VEN_1234&DEV_" + sub);
            RegistryValue.Write(fx.Registry, Hive.Machine, key, "Characteristics", "dword", "132");
            RegistryValue.Write(fx.Registry, Hive.Machine, key, "*IfType", "dword", ifType.ToString());
            RegistryValue.Write(fx.Registry, Hive.Machine, $@"{key}\Ndi\params\*EEE\enum", "0", "string", "Disabled");
        }
        Add("0001", "{11111111-1111-1111-1111-111111111111}", 6);   // Ethernet
        Add("0002", "{22222222-2222-2222-2222-222222222222}", 243); // mobile broadband modem
        Add("0003", "{33333333-3333-3333-3333-333333333333}", 71);  // Wi-Fi
        var t = Tweak("test.eth", new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" }, Media = "ethernet" });

        var expanded = fx.Engine.Expand(t).Cast<NicPropertyAction>().ToList();
        Assert.Equal(["{11111111-1111-1111-1111-111111111111}"], expanded.Select(a => a.Adapter!.InterfaceGuid));
    }

    [Fact]
    public async Task UndoOfAServiceThatWasUninstalledSucceeds()
    {
        using var fx = new EngineFixture();
        fx.Services.Start["OldSvc"] = ServiceStart.Automatic;
        var t = Tweak("test.svc", new ServiceAction { Name = "OldSvc", StartType = ServiceStart.Manual });
        await fx.Engine.ApplyAsync(t, Facts, Options);
        fx.Services.Start.Remove("OldSvc");

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Null(fx.Store.Get(t.Id));
    }
}

/// <summary>Rules every catalog entry has to follow.</summary>
public class CatalogRuleTests
{
    [Fact]
    public void ExpertAndBootCriticalTweaksArePreviewsAndPreviewsAreNeverBatched()
    {
        foreach (var t in TweakCatalog.Current.Tweaks.Where(t => !t.Hidden))
        {
            if (t.EffectiveRisk == Risk.Expert || t.IsBootCritical) Assert.True(t.Preview, $"{t.Id} is Expert or boot-critical but not marked preview");
            if (t.Preview) Assert.False(t.IsBatchSafe, t.Id);
        }
    }

    [Fact]
    public void NoTweakMixesPlanSwitchesAndPlanSettings()
    {
        // Settings are written to the plan that is active when the tweak is expanded, before a plan switch in the
        // same tweak would run.
        foreach (var t in TweakCatalog.Current.Tweaks)
            Assert.False(t.Actions.Any(a => a is PowerSchemeAction) && t.Actions.Any(a => a is PowerSettingAction), t.Id);
    }

    [Fact]
    public void ConsumerFeaturesPolicyOnlyAppliesWhereWindowsHonorsIt()
    {
        var t = TweakCatalog.Current.Get("privacy.consumerFeaturesOff")!;
        Assert.False(TweakEngine.AppliesTo(t, new Facts().Set("os.build", 26300).Set("os.edition", "Professional")));
        Assert.False(TweakEngine.AppliesTo(t, new Facts().Set("os.build", 26300).Set("os.edition", "Core")));
        Assert.True(TweakEngine.AppliesTo(t, new Facts().Set("os.build", 26300).Set("os.edition", "Enterprise")));
    }

    [Theory]
    [InlineData("updates.driversExcluded", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate")]
    [InlineData("privacy.deviceMetadataOff", @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork")]
    [InlineData("system.registryBackup", @"SYSTEM\CurrentControlSet\Control\Session Manager\Configuration Manager", "EnablePeriodicBackup")]
    public async Task NewPolicyTweaksWriteOneValueAndUndoRemovesIt(string id, string path, string name)
    {
        using var fx = new EngineFixture();
        var t = TweakCatalog.Current.Get(id)!;
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true).Set("os.edition", "Professional");
        var options = new ApplyOptions { ExpertMode = true, ContinueWithoutRestorePoint = true };

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, facts, options)).Outcome);
        Assert.Equal("1", RegistryValue.Read(fx.Registry, Hive.Machine, path, name).Data);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, path, name).Existed);
    }

    [Theory]
    [InlineData("updates.driversExcluded")]
    [InlineData("privacy.deviceMetadataOff")]
    public void PoliciesMicrosoftListsForProAndUpAreNotOfferedOnHome(string id)
    {
        var t = TweakCatalog.Current.Get(id)!;
        Assert.False(TweakEngine.AppliesTo(t, new Facts().Set("os.build", 26300).Set("os.edition", "Core")));
        Assert.True(TweakEngine.AppliesTo(t, new Facts().Set("os.build", 26300).Set("os.edition", "Professional")));
    }

    [Fact]
    public void VbsTweakShowsNoGainWhenVbsIsAlreadyOff()
    {
        var t = TweakCatalog.Current.Get("security.vbsOff")!;
        Assert.Equal(0, TweakEngine.ImpactFor(t, new Facts().Set("vbs.running", false)).Impact);
        Assert.True(TweakEngine.ImpactFor(t, new Facts().Set("vbs.running", true)).Impact > 0);
    }
}
