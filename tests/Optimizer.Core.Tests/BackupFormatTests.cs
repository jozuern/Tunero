using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// Undo must work on every backup file a released version wrote. The files under Fixtures/backups were written by the
/// engine itself, one per action type (and a pending undo); they are never edited. Each one is restored here: the system
/// is put into the state it had before, the stored actions are applied as the old version did, and undo has to bring
/// back every original. A change to an action's properties, its type name, its target key or the encoding of its stored
/// value fails here. To add files for a new format or action type, set TUNERO_WRITE_BACKUP_FIXTURES=1 and run
/// <see cref="WriteFixtures"/> once; add files, never replace them.
/// </summary>
public class BackupFormatTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false);
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private static string FixtureFolder => Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "backups");

    private const string NicGuid = "{22222222-2222-2222-2222-222222222222}";
    private static readonly string NicKey = $@"{NicAdapters.ClassPath}\0007";
    private const string DnsGuid = "{33333333-3333-3333-3333-333333333333}";

    /// <summary>One change per action type: the starting state of the PC and the tweak that changes it.</summary>
    private sealed record Scenario(string Name, Action<EngineFixture> Setup, params TweakAction[] Actions)
    {
        public string Id => "fixture." + Name;
    }

    private static RegistryAction Reg(Hive hive, string path, string name, string kind, object value, bool delete = false) => new()
    {
        Hive = hive, Path = path, Name = name, Kind = kind, Value = JsonSerializer.SerializeToElement(value), Delete = delete,
    };

    private static readonly Scenario[] Scenarios =
    [
        new("registry", fx => RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Registry", "Level", "dword", "3"),
            Reg(Hive.Machine, @"SOFTWARE\Fixture\Registry", "Level", "dword", 0),
            Reg(Hive.Machine, @"SOFTWARE\Fixture\Registry\New\Deeper", "Name", "string", "fixture"),
            Reg(Hive.User, @"Software\Fixture\Registry", "Mode", "qword", 5)),
        new("registryDelete", fx => RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Delete", "Gone", "string", "here"),
            Reg(Hive.Machine, @"SOFTWARE\Fixture\Delete", "Gone", "string", "", delete: true)),
        new("registryKey", fx =>
            {
                RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\WholeKey", "", "string", "default");
                RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\WholeKey", "Paths", "multiString", "a\nb");
                RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\WholeKey\Sub\Deeper", "Blob", "binary", "0102FF");
                RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\WholeKey\Sub", "Home", "expandString", "%SystemRoot%\\x");
            },
            new RegistryKeyAction { Hive = Hive.Machine, Path = @"SOFTWARE\Fixture\WholeKey" }),
        new("registryBits", fx => RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Bits", "Flags", "dword", "16"),
            new RegistryBitsAction { Hive = Hive.Machine, Path = @"SOFTWARE\Fixture\Bits", Name = "Flags", Set = 0x1, Clear = 0x10 }),
        new("registryBinaryBits", fx => RegistryValue.Write(fx.Registry, Hive.User, @"Software\Fixture\Binary", "Mask", "binary", "9012038010000000"),
            new RegistryBinaryBitsAction { Hive = Hive.User, Path = @"Software\Fixture\Binary", Name = "Mask", Set = "00", Clear = "10" }),
        new("registryToken", fx => RegistryValue.Write(fx.Registry, Hive.User, @"Software\Fixture\Token", "Settings", "string", "SwapEffectUpgradeEnable=0;"),
            new RegistryTokenAction { Hive = Hive.User, Path = @"Software\Fixture\Token", Name = "Settings", Token = "SwapEffectUpgradeEnable", Value = "1" }),
        new("service", fx => fx.Services.Start["FixtureSvc"] = ServiceStart.Automatic,
            new ServiceAction { Name = "FixtureSvc", StartType = ServiceStart.Disabled }),
        new("powerSetting", _ => { },
            new PowerSettingAction { Subgroup = "SUB_PROCESSOR", Setting = "PERFBOOSTMODE", Ac = 2, Dc = 0 }),
        new("powerScheme", _ => { },
            new PowerSchemeAction { DuplicateFrom = "highPerformance", Name = "Tunero Fixture" }),
        new("bcd", fx => fx.Bcd.Values["description"] = "Windows 11",
            new BcdAction { Element = "disabledynamictick", Value = "yes" }),
        new("scheduledTask", fx => fx.Tasks.Enabled[@"\Microsoft\Windows\Fixture\Task"] = true,
            new ScheduledTaskAction { Path = @"\Microsoft\Windows\Fixture\Task", Enabled = false }),
        new("hibernation", fx => RegistryValue.Write(fx.Registry, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", "dword", "1"),
            new HibernationAction { Enabled = false }),
        new("memoryCompression", fx => fx.Processes.MemoryCompression = true,
            new MemoryCompressionAction { Enabled = false }),
        new("displayMode", fx => fx.Displays.Refresh[@"\\.\DISPLAY1"] = 60,
            new DisplayModeAction { GdiName = @"\\.\DISPLAY1", Width = 2560, Height = 1440, RefreshHz = 144 }),
        new("nvidiaDrs", _ => { },
            new NvidiaDrsAction { Profile = "global", SettingId = 0x10835002, Value = 1 }),
        new("powerMode", fx => fx.PowerMode.Current = Guid.Empty,
            new PowerModeAction { Overlay = new Guid("ded574b5-45a0-4f42-8737-46345c09c238") }),
        new("nicProperty", SetUpNic,
            new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" }, Dwords = new(StringComparer.OrdinalIgnoreCase) { ["PnPCapabilities"] = 24 } }),
        new("dns", fx => fx.Network.SetDns(DnsGuid, null),
            new DnsAction { InterfaceGuid = DnsGuid, Servers = "1.1.1.1,1.0.0.1" }),
        new("accessibilityShortcut", _ => { },
            new AccessibilityShortcutAction { Feature = AccessibilityFeature.StickyKeys, Shortcut = false }),
        new("systemParameter", _ => { },
            new SystemParameterAction { Parameter = SystemParameter.MenuShowDelay, Value = 100 }),
        new("dohAutoUpgrade", fx => fx.Network.Doh = new Dictionary<string, bool> { ["1.1.1.1"] = false, ["8.8.8.8"] = true },
            new DohAutoUpgradeAction { Servers = ["1.1.1.1", "8.8.8.8"], AutoUpgrade = true }),
        new("reservedStorage", _ => { },
            new ReservedStorageAction { Enabled = false }),
        new("startupApproved", fx => RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Fixture", "binary", "020000000000000000000000"),
            new Startup.StartupApprovedAction { Hive = Hive.User, Path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", Name = "Fixture", Enabled = false }),
        new("optionalFeature", _ => { },
            new OptionalFeatureAction { Feature = "Microsoft-Hyper-V-All", Enabled = true }),
        new("optionalCapability", _ => { },
            new OptionalCapabilityAction { Capability = "Media.WindowsMediaPlayer~~~~0.0.12.0", Installed = false }),
        new("startupFileDelete", _ => SetUpStartupFile(),
            new Startup.StartupFileDeleteAction { Path = FixtureStartupFile }),
        new("scheduledTaskDelete", fx => fx.Tasks.Xml[@"\Fixture\Updater"] = FixtureTaskXml,
            new Startup.ScheduledTaskDeleteAction { Path = @"\Fixture\Updater" }),
        new("ownTask", _ => { },
            new OwnTaskAction { Path = Tweaks.ReapplyTask.TaskPath("S-1-5-21-1-2-3-1001"), Xml = Tweaks.ReapplyTask.Xml(@"C:\Program Files\Tunero\Tunero.exe", "S-1-5-21-1-2-3-1001") }),
    ];

    /// <summary>
    /// A real file in a Startup folder below the temp folder. The fixture stores <see cref="FixturePlaceholder"/> instead of
    /// the temp folder of the PC that wrote it (no user name in the repository); <see cref="Swap"/> maps one to the other.
    /// </summary>
    private static string FixtureStartupFile => Path.Combine(FixtureRoot, @"Microsoft\Windows\Start Menu\Programs\Startup\Fixture.lnk");

    private static string FixtureRoot => Path.Combine(Path.GetTempPath(), "tunero-backup-fixture");
    private const string FixturePlaceholder = @"C:\TuneroBackupFixture";

    /// <summary>Replaces a folder in the backup's text: plain, JSON-escaped and escaped twice (the stored action), each also in lower case (target keys).</summary>
    private static string Swap(string text, string from, string to)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal);
        foreach (var level in new[] { 2, 1, 0 })
        {
            var (f, t) = (from, to);
            for (var i = 0; i < level; i++) (f, t) = (Escape(f), Escape(t));
            text = text.Replace(f, t, StringComparison.Ordinal).Replace(f.ToLowerInvariant(), t.ToLowerInvariant(), StringComparison.Ordinal);
        }
        return text;
    }

    private static void SetUpStartupFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FixtureStartupFile)!);
        if (File.Exists(FixtureStartupFile)) File.Delete(FixtureStartupFile);
        File.WriteAllBytes(FixtureStartupFile, [0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0x00]);
    }

    private const string FixtureTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal id="Author"><UserId>S-1-5-18</UserId></Principal></Principals>
          <Actions Context="Author"><Exec><Command>C:\Fixture\updater.exe</Command></Exec></Actions>
        </Task>
        """;

    private static void SetUpNic(EngineFixture fx)
    {
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "NetCfgInstanceId", "string", NicGuid);
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "DeviceInstanceID", "string", @"PCI\VEN_8086&DEV_15BC\7");
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "Characteristics", "dword", "132");
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "DriverDesc", "string", "Fixture Ethernet");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{NicKey}\Ndi\params\*EEE\enum", "0", "string", "Disabled");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{NicKey}\Ndi\params\*EEE\enum", "1", "string", "Enabled");
        RegistryValue.Write(fx.Registry, Hive.Machine, NicKey, "*EEE", "string", "1");
    }

    private static EngineFixture Prepare(Scenario s)
    {
        var fx = new EngineFixture(DnsGuid);
        fx.Processes.Handler = new FakeSystemTools(fx).Run;
        s.Setup(fx);
        return fx;
    }

    private static TweakDefinition Tweak(Scenario s) => new()
    {
        Id = s.Id,
        Category = "Test",
        Hidden = true,
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Actions = [.. s.Actions],
    };

    /// <summary>Writes the fixture files of this version (only with TUNERO_WRITE_BACKUP_FIXTURES=1); a file that exists is kept.</summary>
    [Fact]
    public async Task WriteFixtures()
    {
        if (Environment.GetEnvironmentVariable("TUNERO_WRITE_BACKUP_FIXTURES") != "1") return;
        var folder = Path.Combine(FixtureFolder, $"format{BackupFormat.Current}");
        Directory.CreateDirectory(folder);
        foreach (var s in Scenarios)
        {
            using var fx = Prepare(s);
            var t = Tweak(s);
            var r = await fx.Engine.ApplyAsync(t, Facts, Options);
            Assert.True(r.Outcome is ApplyOutcome.Applied or ApplyOutcome.AppliedIneffective, $"{s.Name}: {r.Outcome} {r.Error}");
            var file = Path.Combine(folder, s.Name + ".json");
            if (!File.Exists(file)) File.WriteAllText(file, Swap(File.ReadAllText(Path.Combine(fx.Store.BackupFolder, s.Id + ".json")), FixtureRoot, FixturePlaceholder));
        }

        // An undo whose value takes effect after a restart is kept as a pending undo.
        var pendingScenario = Scenarios.Single(s => s.Name == "memoryCompression");
        using (var fx = Prepare(pendingScenario))
        {
            fx.Processes.MemoryCompressionAfterRestart = true;
            var t = Tweak(pendingScenario);
            await fx.Engine.ApplyAsync(t, Facts, Options);
            fx.Processes.Restart();
            Assert.True(fx.Engine.Revert(t).Success);
            var file = Path.Combine(folder, "pending-undo.memoryCompression.json");
            if (!File.Exists(file)) File.Copy(Path.Combine(fx.Store.PendingUndoFolder, pendingScenario.Id + ".json"), file);
        }
    }

    private static IEnumerable<string> BackupFixtures() =>
        (Directory.Exists(FixtureFolder) ? Directory.EnumerateFiles(FixtureFolder, "*.json", SearchOption.AllDirectories) : [])
        .Where(f => !Path.GetFileName(f).StartsWith("pending-undo.", StringComparison.Ordinal))
        .Select(f => Path.GetRelativePath(FixtureFolder, f))
        .Order(StringComparer.Ordinal);

    public static TheoryData<string> FixtureFiles() => new(BackupFixtures());

    [Fact]
    public void EveryActionTypeHasAFixture()
    {
        var types = typeof(TweakAction).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!).ToList();
        var stored = BackupFixtures()
            .SelectMany(file => JsonSerializer.Deserialize<TweakBackup>(File.ReadAllText(Path.Combine(FixtureFolder, file)))!.Entries)
            .Select(e => JsonDocument.Parse(e.Action!).RootElement.GetProperty("type").GetString())
            .ToHashSet();
        Assert.All(types, t => Assert.Contains(t, stored));
    }

    /// <summary>
    /// Every action type names its target with a label each language has, with a part for every placeholder. A backup of
    /// 1.0.0 or older holds only the English text: read back by pattern, it gives the same text as the label.
    /// </summary>
    [Fact]
    public void EveryActionTypeRendersItsTargetInEveryLanguage()
    {
        var labels = Optimizer.Core.Docs.Labels.Current;
        var covered = new HashSet<Type>();
        foreach (var s in Scenarios)
        {
            using var fx = Prepare(s);
            foreach (var a in s.Actions)
            {
                covered.Add(a.GetType());
                var label = a.TargetLabel(fx.Context);
                Assert.Equal(labels.Render("en", label), a.Describe(fx.Context));
                if (label.Key is null)
                {
                    Assert.Single(label.Args);
                    continue;
                }
                foreach (var lang in Optimizer.Core.Docs.DocStore.Languages)
                {
                    Assert.True(labels.Has(lang, label.Key), $"{lang}: no label {label.Key} ({s.Name})");
                    var used = System.Text.RegularExpressions.Regex.Matches(labels.Get(lang, label.Key), @"\{(\d)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                    Assert.True(used.SetEquals(Enumerable.Range(0, label.Args.Count)), $"{lang}: {label.Key} has placeholders {string.Join(",", used)} for {label.Args.Count} parts");
                }
                Assert.Equal(labels.Render("de", label), labels.Target("de", labels.Render("en", label)));
            }
        }
        var types = typeof(TweakAction).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().Select(a => a.DerivedType);
        Assert.All(types, t => Assert.Contains(t, covered));
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void BackupOfAnEarlierVersionIsUndone(string fixture)
    {
        var json = Swap(File.ReadAllText(Path.Combine(FixtureFolder, fixture)), FixturePlaceholder, FixtureRoot);
        var backup = JsonSerializer.Deserialize<TweakBackup>(json)!;
        var scenario = Scenarios.Single(s => s.Id == backup.TweakId);
        using var fx = Prepare(scenario);

        // The PC as the earlier version left it: its stored actions applied to the starting state.
        var stored = backup.Entries.Select(e => (Entry: e, Action: JsonSerializer.Deserialize<TweakAction>(e.Action!, TweakCatalog.JsonOptions)!)).ToList();
        foreach (var (entry, action) in stored)
        {
            Assert.Equal(entry.TargetKey, action.TargetKey);
            switch (action)
            {
                // The plan the earlier version created, under the id it recorded (a new apply would create another one).
                case PowerSchemeAction p when Guid.TryParse(entry.Applied?.Data, out var plan):
                    fx.Power.SchemeNames[plan] = p.Name!;
                    fx.Power.Active = plan;
                    break;
                // The exact bytes it wrote (they hold the time of the change).
                case Startup.StartupApprovedAction:
                    action.Restore(fx.Context, entry.Applied!);
                    break;
                default:
                    action.Apply(fx.Context);
                    break;
            }
        }
        File.WriteAllText(Path.Combine(fx.Store.BackupFolder, Path.GetFileName(fixture).Replace(scenario.Name, scenario.Id, StringComparison.Ordinal)), json);

        var t = fx.Engine.Resolve(backup.TweakId);
        Assert.NotNull(t);
        var result = fx.Engine.Revert(t);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Empty(result.AlreadyRevertedByWindows);
        foreach (var (entry, action) in stored)
        {
            var now = action.Read(fx.Context);
            Assert.True(now is not null && now.SameAs(entry.Original), $"{fixture} {entry.TargetKey}: {now?.Display} after undo, was {entry.Original.Display}");
        }
        Assert.Null(fx.Store.Get(backup.TweakId));
    }

    [Fact]
    public void PendingUndoOfAnEarlierVersionIsRead()
    {
        foreach (var file in Directory.EnumerateFiles(FixtureFolder, "pending-undo.*.json", SearchOption.AllDirectories))
        {
            using var fx = new EngineFixture();
            var pending = JsonSerializer.Deserialize<PendingUndo>(File.ReadAllText(file))!;
            File.Copy(file, Path.Combine(fx.Store.PendingUndoFolder, pending.TweakId + ".json"));
            var read = fx.Store.GetPendingUndo(pending.TweakId);
            Assert.NotNull(read);
            Assert.NotEmpty(read.Entries);
            Assert.All(read.Entries, e => Assert.Equal(pending.Entries.Single(p => p.TargetKey == e.TargetKey).Original.Data, e.Original.Data));
        }
    }

    /// <summary>A file of a newer format is never applied or undone: an older version could misread it.</summary>
    [Fact]
    public void BackupOfANewerFormatIsRefused()
    {
        using var fx = new EngineFixture();
        var newer = new TweakBackup { TweakId = "test.newer", Format = BackupFormat.Current + 1, Entries = [] };
        File.WriteAllText(Path.Combine(fx.Store.BackupFolder, "test.newer.json"), JsonSerializer.Serialize(newer));
        var ex = Assert.Throws<BackupUnreadableException>(() => fx.Store.Get("test.newer"));
        Assert.Equal(BackupUnreadableException.Reason.NewerFormat, ex.Why);
        Assert.Empty(fx.Store.All());
    }

    /// <summary>The type names and target keys in the backup files of released versions; renaming one breaks their undo.</summary>
    [Fact]
    public void TypeNamesAreFrozen()
    {
        var types = typeof(TweakAction).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!).Order(StringComparer.Ordinal);
        Assert.Equal(
            ["accessibilityShortcut", "bcd", "displayMode", "dns", "dohAutoUpgrade", "hibernation", "memoryCompression", "nicProperty", "nvidiaDrs",
             "optionalCapability", "optionalFeature", "ownTask", "powerMode", "powerScheme", "powerSetting", "registry", "registryBinaryBits", "registryBits",
             "registryKey", "registryToken", "reservedStorage", "scheduledTask", "scheduledTaskDelete", "service", "startupApproved", "startupFileDelete", "systemParameter"],
            types);
    }
}
