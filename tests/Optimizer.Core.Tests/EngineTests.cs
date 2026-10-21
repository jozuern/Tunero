using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Engine round trips against a sandboxed registry and fake system APIs. Nothing on the real system changes.</summary>
public class EngineTests
{
    private static readonly TweakCatalog Catalog = TweakCatalog.Current;

    private static Facts Facts(Action<Facts>? extra = null)
    {
        // The facts the catalog's safety guards read: unknown, they would block (fail closed) like a failed probe.
        var f = new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false)
            .Set("gpu.hasDiscrete", true).Set("gpu.supportsHags", true).Set("memory.totalGb", 32.0).Set("storage.ssdOnly", true)
            .Set("power.modernStandby", false).Set("cpu.x3dMultiCcd", false).Set("os.insider", false);
        extra?.Invoke(f);
        return f;
    }

    private static readonly ApplyOptions Expert = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private static TweakDefinition T(string id) => Catalog.Get(id) ?? throw new InvalidOperationException(id);

    private static StoredValue Reg(EngineFixture fx, Hive hive, string path, string name) => RegistryValue.Read(fx.Registry, hive, path, name);

    [Fact]
    public void CatalogLoadsWithUniqueIdsAndActions()
    {
        Assert.True(Catalog.Visible.Count() >= 45, $"only {Catalog.Visible.Count()} tweaks");
        Assert.All(Catalog.Tweaks, t => Assert.NotEmpty(t.Actions));
        Assert.All(Catalog.Tweaks.Where(t => t.IsBootCritical), t => Assert.Equal(Risk.Expert, t.EffectiveRisk));
    }

    [Fact]
    public async Task ApplyTwiceUndoOnceRestoresTrueOriginal()
    {
        using var fx = new EngineFixture();
        const string path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "HwSchMode", "dword", "1");
        var t = T("gpu.hags");

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert)).Outcome);
        Assert.Equal("2", Reg(fx, Hive.Machine, path, "HwSchMode").Data);
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert); // second apply: nothing to do, original stays

        var undo = fx.Engine.Revert(t);
        Assert.True(undo.Success);
        Assert.Equal("1", Reg(fx, Hive.Machine, path, "HwSchMode").Data);
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task OriginalThatDidNotExistIsDeletedOnUndo()
    {
        using var fx = new EngineFixture();
        var t = T("power.throttlingOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.True(Reg(fx, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff").Existed);
        fx.Engine.Revert(t);
        Assert.False(Reg(fx, Hive.Machine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff").Existed);
    }

    [Fact]
    public async Task FailureRollsBackEarlierActionsOfTheSameTweak()
    {
        using var fx = new EngineFixture();
        fx.Services.Start["DiagTrack"] = ServiceStart.Automatic;
        fx.Services.FailOnWrite.Add("DiagTrack");
        var t = T("privacy.telemetryOff"); // registry first, then the service (fails)

        var result = await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.False(Reg(fx, Hive.Machine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry").Existed);
        Assert.Equal(ServiceStart.Automatic, fx.Services.Start["DiagTrack"]);
    }

    [Fact]
    public async Task UndoLeavesValuesWindowsAlreadyChanged()
    {
        using var fx = new EngineFixture();
        const string path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "HwSchMode", "dword", "1");
        var t = T("gpu.hags");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);

        RegistryValue.Write(fx.Registry, Hive.Machine, path, "HwSchMode", "dword", "3"); // a feature update wrote a new value
        var undo = fx.Engine.Revert(t);

        Assert.Single(undo.AlreadyRevertedByWindows);
        Assert.Equal("3", Reg(fx, Hive.Machine, path, "HwSchMode").Data);
    }

    [Fact]
    public async Task DriftIsReportedAsRevertedByWindows()
    {
        using var fx = new EngineFixture();
        var t = T("privacy.advertisingIdOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(t, Facts()));

        RegistryValue.Delete(fx.Registry, Hive.Machine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy");
        Assert.Equal(TweakState.RevertedByWindows, fx.Engine.DetectState(t, Facts()));
    }

    [Fact]
    public async Task SharedBitmaskKeepsTheOtherTweaksBits()
    {
        using var fx = new EngineFixture();
        const string path = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
        // A second tweak owning another bit of the same value (0x08).
        var other = new TweakDefinition
        {
            Id = "test.otherBit", Category = "Network", Hidden = true, Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Actions = [new RegistryBitsAction { Hive = Hive.Machine, Path = path, Name = "DisabledComponents", Set = 8 }],
        };
        await fx.Engine.ApplyAsync(T("network.preferIpv4"), Facts(), new HashSet<string>(), Expert);
        await fx.Engine.ApplyAsync(other, Facts(), new HashSet<string>(), Expert);
        Assert.Equal("40", Reg(fx, Hive.Machine, path, "DisabledComponents").Data); // 0x20 | 0x08

        fx.Engine.Revert(T("network.preferIpv4"));
        Assert.Equal("8", Reg(fx, Hive.Machine, path, "DisabledComponents").Data);
        fx.Engine.Revert(other);
        var final = Reg(fx, Hive.Machine, path, "DisabledComponents");
        Assert.True(!final.Existed || final.Data == "0"); // 0 = Windows default (all IPv6 components enabled)
    }

    [Fact]
    public async Task TokenListKeepsOtherTokens()
    {
        using var fx = new EngineFixture();
        const string path = @"Software\Microsoft\DirectX\UserGpuPreferences";
        RegistryValue.Write(fx.Registry, Hive.User, path, "DirectXUserGlobalSettings", "string", "AutoHDREnable=1;");
        var t = T("gpu.windowedOptimizations");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal("AutoHDREnable=1;SwapEffectUpgradeEnable=1;", Reg(fx, Hive.User, path, "DirectXUserGlobalSettings").Data);
        fx.Engine.Revert(t);
        Assert.Equal("AutoHDREnable=1;", Reg(fx, Hive.User, path, "DirectXUserGlobalSettings").Data);
    }

    [Fact]
    public void GuardRulesBlock()
    {
        using var fx = new EngineFixture();
        var opts = new ApplyOptions { ExpertMode = true };

        Assert.Contains(fx.Engine.Preflight(T("security.vbsOff"), Facts(f => f.Set("anticheat.strict", true)), opts), b => b.ReasonKey == "block.antiCheat");
        Assert.DoesNotContain(fx.Engine.Preflight(T("security.vbsOff"), Facts(f => f.Set("anticheat.strict", true)), new ApplyOptions { ExpertMode = true, AcknowledgeAntiCheat = true }), b => b.ReasonKey == "block.antiCheat");
        Assert.Contains(fx.Engine.Preflight(T("power.gamingPlan"), Facts(f => f.Set("cpu.x3dMultiCcd", true)), opts), b => b.ReasonKey == "block.x3dBalanced");
        Assert.Contains(fx.Engine.Preflight(T("leftover.usePlatformClock"), Facts(), new ApplyOptions()), b => b.ReasonKey == "block.expertMode");
        Assert.Contains(fx.Engine.Preflight(T("gpu.hags"), Facts(f => f.Set("elevated", false)), opts), b => b.ReasonKey == "block.notElevated");
        Assert.Contains(fx.Engine.Preflight(T("power.hibernateOff"), Facts(f => f.Set("power.modernStandby", true).Set("system.laptop", true)), opts), b => b.ReasonKey == "block.modernStandbyHibernate");
    }

    [Fact]
    public async Task WindowsDefaultsDoNotBlockConflictingTweaks()
    {
        // Balanced is the Windows default, so power.balancedPlan detects as applied; the Gaming plan must still apply.
        using var fx = new EngineFixture();
        var balanced = T("power.balancedPlan");
        var gaming = T("power.gamingPlan");
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(balanced, Facts()));
        var applied = new HashSet<string> { balanced.Id };
        Assert.DoesNotContain(fx.Engine.Preflight(gaming, Facts(), Expert), b => b.ReasonKey == "block.conflict");

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(gaming, Facts(), applied, Expert)).Outcome);
        Assert.Equal("Tunero Gaming", fx.Power.SchemeNames[fx.Power.Active]);
        Assert.True(fx.Engine.Revert(gaming).Success);
        Assert.Equal(FakePower.Balanced, fx.Power.Active);
        Assert.DoesNotContain("Tunero Gaming", fx.Power.SchemeNames.Values);
    }

    [Fact]
    public void PowerPlanPreviewShowsPlanNames()
    {
        using var fx = new EngineFixture();
        fx.Power.Active = FakePower.High;
        var line = Assert.Single(fx.Engine.Preview(T("power.balancedPlan")));
        // Windows' own plans by a label key, shown in the app's language (Windows names them in its own language).
        Assert.Equal(("@plan.highPerformance", "@plan.balanced"), (line.Before, line.After));
        var labels = Docs.Labels.Current;
        Assert.Equal(("High performance", "Ausbalanciert"), (labels.Display("en", line.Before), labels.Display("de", line.After)));
        Assert.Equal("Tunero Gaming", Assert.Single(fx.Engine.Preview(T("power.gamingPlan"))).After);
    }

    [Fact]
    public void ChangeTargetsShowInGerman()
    {
        var labels = Docs.Labels.Current;
        Assert.Equal("Aktiver Energiesparplan", labels.Target("de", "Active power plan"));
        Assert.Equal("Dienst Spooler (Starttyp)", labels.Target("de", "Service Spooler (start type)"));
        Assert.Equal("Startkonfiguration {current}: disabledynamictick", labels.Target("de", "Boot configuration {current}: disabledynamictick"));
        // The more specific template wins over the one that would also match ("adapter" as a name).
        Assert.Equal("IPv4-DNS-Server des Adapters {abc}", labels.Target("de", "IPv4 DNS servers of adapter {abc}"));
        Assert.Equal("Service Spooler (start type)", labels.Target("en", "Service Spooler (start type)"));
        Assert.Equal(@"HKLM\SOFTWARE\Test\Value", labels.Target("de", @"HKLM\SOFTWARE\Test\Value"));

        // Every target a catalog tweak shows is a registry path or has a German form.
        using var fx = new EngineFixture();
        var english = Catalog.Tweaks.SelectMany(t => fx.Engine.Preview(t)).Select(c => c.Target)
            .Where(target => !target.StartsWith("HK", StringComparison.Ordinal) && labels.Target("de", target) == target)
            .Distinct().ToList();
        Assert.True(english.Count == 0, "no German form: " + string.Join("; ", english));
    }

    [Fact]
    public async Task ConflictBlocksWhileTheOtherChangeIsBackedUp()
    {
        using var fx = new EngineFixture();
        var none = new HashSet<string>();
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(T("power.ultimatePlan"), Facts(), none, Expert)).Outcome);
        var r = await fx.Engine.ApplyAsync(T("power.gamingPlan"), Facts(), none, Expert);
        Assert.Equal(ApplyOutcome.Blocked, r.Outcome);
        Assert.Contains(r.Blocks!, b => b.ReasonKey == "block.conflict" && b.Detail == "power.ultimatePlan");

        // After undo the Gaming plan is free again.
        fx.Engine.Revert(T("power.ultimatePlan"));
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(T("power.gamingPlan"), Facts(), none, Expert)).Outcome);
    }

    /// <summary>
    /// The first sign-in on a new installation applies without a restore point (there is nothing to go back to): none is
    /// made and the frequency limit is not touched; a later change in the same session still makes one.
    /// </summary>
    [Fact]
    public async Task WithoutRestorePointMakesNoneAndLeavesTheNextChangeItsOwn()
    {
        using var fx = new EngineFixture();
        var r = await fx.Engine.ApplyAsync(T("gpu.hags"), Facts(), new HashSet<string>(), new ApplyOptions { ExpertMode = true, WithoutRestorePoint = true });
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Equal(0, fx.RestorePoints.Created);
        Assert.Null(fx.Store.Get(TweakEngine.RestorePointFrequencyTweak));
        Assert.NotNull(fx.Store.Get("gpu.hags"));

        await fx.Engine.ApplyAsync(T("power.throttlingOff"), Facts(), new HashSet<string>(), new ApplyOptions { ExpertMode = true });
        Assert.Equal(1, fx.RestorePoints.Created);
    }

    [Fact]
    public async Task RestorePointOncePerSessionAndFrequencyIsBackedUp()
    {
        using var fx = new EngineFixture();
        var opts = new ApplyOptions { ExpertMode = true };
        await fx.Engine.ApplyAsync(T("gpu.hags"), Facts(), new HashSet<string>(), opts);
        await fx.Engine.ApplyAsync(T("power.throttlingOff"), Facts(), new HashSet<string>(), opts);

        Assert.Equal(1, fx.RestorePoints.Created);
        Assert.Equal("0", Reg(fx, Hive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency").Data);
        Assert.NotNull(fx.Store.Get(TweakEngine.RestorePointFrequencyTweak));
    }

    [Fact]
    public async Task DisabledSystemProtectionNeedsADecision()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var t = T("gpu.hags");
        Assert.Equal(ApplyOutcome.NeedsRestorePointDecision, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), new ApplyOptions())).Outcome);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), new ApplyOptions { ContinueWithoutRestorePoint = true })).Outcome);
    }

    [Fact]
    public async Task PowerSettingUndoTargetsTheChangedScheme()
    {
        using var fx = new EngineFixture();
        var sub = PowerAliases.Resolve("SUB_PROCESSOR");
        var max = PowerAliases.Resolve("PROCTHROTTLEMAX");
        fx.Power.Ac[(FakePower.Balanced, sub, max)] = 99;
        var t = T("power.turboRestore");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal(100u, fx.Power.Ac[(FakePower.Balanced, sub, max)]);
        Assert.NotEmpty(fx.Power.Exports);

        fx.Power.Active = FakePower.High; // user switched plans in between
        fx.Engine.Revert(t);
        Assert.Equal(99u, fx.Power.Ac[(FakePower.Balanced, sub, max)]);
    }

    [Fact]
    public async Task GamingPlanIsCreatedAndRemovedOnUndo()
    {
        using var fx = new EngineFixture();
        var t = T("power.gamingPlan");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal("Tunero Gaming", fx.Power.SchemeNames[fx.Power.Active]);
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(t, Facts()));

        fx.Engine.Revert(t);
        Assert.Equal(FakePower.Balanced, fx.Power.Active);
        Assert.DoesNotContain("Tunero Gaming", fx.Power.SchemeNames.Values);
    }

    [Fact]
    public async Task BootCriticalExportsBcdFirst()
    {
        using var fx = new EngineFixture();
        fx.Bcd.Values["useplatformclock"] = "Yes";
        var t = T("leftover.usePlatformClock");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Single(fx.Bcd.Exports);
        Assert.False(fx.Bcd.Values.ContainsKey("useplatformclock"));
        fx.Engine.Revert(t);
        Assert.Equal("yes", fx.Bcd.Values["useplatformclock"]);
    }

    [Fact]
    public async Task BcdUndoWritesBackTheOriginalValue()
    {
        // "No" must come back as "no", not as a forced "yes" (that would turn HPET on).
        using var fx = new EngineFixture();
        fx.Bcd.Values["useplatformclock"] = "No";
        var t = T("leftover.usePlatformClock");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.False(fx.Bcd.Values.ContainsKey("useplatformclock"));
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("no", fx.Bcd.Values["useplatformclock"]);
    }

    [Fact]
    public void BcdValueIsCompared()
    {
        // "disabledynamictick No" is present but not the desired "yes".
        using var fx = new EngineFixture();
        fx.Bcd.Values["disabledynamictick"] = "No";
        var a = new BcdAction { Element = "disabledynamictick", Value = "yes" };
        Assert.Equal(ActionState.NotApplied, a.State(fx.Context));
        fx.Bcd.Values["disabledynamictick"] = "Yes";
        Assert.Equal(ActionState.Applied, a.State(fx.Context));
    }

    [Fact]
    public void BcdOutputIsParsedWithValues()
    {
        var map = Platform.SystemBcdStore.Parse("Windows-Startladeprogramm\r\n-------------------------\r\nBezeichner              {current}\r\nuseplatformclock        No\r\nnx                      OptIn\r\n");
        Assert.Equal("No", map["useplatformclock"]);
        Assert.Equal("OptIn", map["nx"]);
        Assert.False(map.ContainsKey("Bezeichner"));
    }

    [Fact]
    public void MissingValueMeansDefaultForGameMode()
    {
        using var fx = new EngineFixture();
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(T("gpu.gameMode"), Facts()));
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "dword", "0");
        Assert.Equal(TweakState.Partial, fx.Engine.DetectState(T("gpu.gameMode"), Facts()));
    }

    [Fact]
    public async Task PerInterfaceValuesExpandToEachNic()
    {
        using var fx = new EngineFixture("{AAAA}", "{BBBB}");
        var t = T("network.nagleOff");
        Assert.Equal(2, fx.Engine.Expand(t).Count);
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal("1", Reg(fx, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{BBBB}", "TcpAckFrequency").Data);
    }

    [Fact]
    public async Task UndoRestoresAdaptersThatAreNoLongerConnected()
    {
        // Applied with Ethernet and Wi-Fi up, undone with only Wi-Fi up: Ethernet values must still be restored.
        using var fx = new EngineFixture("{AAAA}", "{BBBB}");
        const string eth = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{AAAA}";
        RegistryValue.Write(fx.Registry, Hive.Machine, eth, "TcpAckFrequency", "dword", "2");
        var t = T("network.nagleOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal("1", Reg(fx, Hive.Machine, eth, "TcpAckFrequency").Data);

        var wifiOnly = new TweakEngine(WithInterfaces(fx.Context, "{BBBB}"), fx.Store, fx.RestorePoints, "test", 26300);
        Assert.Equal(TweakState.Applied, wifiOnly.DetectState(t, Facts())); // not "reset by Windows"
        var r = wifiOnly.Revert(t);
        Assert.True(r.Success);
        Assert.Equal("2", Reg(fx, Hive.Machine, eth, "TcpAckFrequency").Data);
        Assert.False(Reg(fx, Hive.Machine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{BBBB}", "TcpAckFrequency").Existed);
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task UndoKeepsEntriesThatCannotBeRestored()
    {
        using var fx = new EngineFixture();
        fx.Services.Start["SvcA"] = ServiceStart.Automatic;
        fx.Services.Start["SvcB"] = ServiceStart.Automatic;
        var t = new TweakDefinition
        {
            Id = "test.twoServices", Category = "Services", Hidden = true, Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Actions = [new ServiceAction { Name = "SvcA", StartType = ServiceStart.Manual }, new ServiceAction { Name = "SvcB", StartType = ServiceStart.Manual }],
        };
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        fx.Services.FailOnWrite.Add("SvcA");
        var first = fx.Engine.Revert(t);
        Assert.False(first.Success);
        Assert.Equal(ServiceStart.Automatic, fx.Services.Start["SvcB"]);
        // Only SvcA is left in the backup; the retry restores it and does not call SvcB "reset by Windows".
        Assert.Single(fx.Store.Get(t.Id)!.Entries);
        fx.Services.FailOnWrite.Clear();
        var second = fx.Engine.Revert(t);
        Assert.True(second.Success);
        Assert.Empty(second.AlreadyRevertedByWindows);
        Assert.Equal(ServiceStart.Automatic, fx.Services.Start["SvcA"]);
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task FailedRollbackKeepsTheBackup()
    {
        using var fx = new EngineFixture();
        fx.Services.Start["SvcA"] = ServiceStart.Automatic;
        fx.Services.Start["SvcB"] = ServiceStart.Automatic;
        var t = new TweakDefinition
        {
            Id = "test.rollback", Category = "Services", Hidden = true, Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Actions = [new ServiceAction { Name = "SvcA", StartType = ServiceStart.Manual }, new ServiceAction { Name = "SvcB", StartType = ServiceStart.Manual }],
        };
        // SvcA is written, SvcB fails, and rolling back SvcA fails too (the fake throws on every later SvcA write).
        var services = new ThrowAfterFirstWrite(fx.Services, "SvcA", "SvcB");
        var engine = new TweakEngine(WithServices(fx.Context, services), fx.Store, fx.RestorePoints, "test", 26300);
        var r = await engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.Equal(ServiceStart.Manual, fx.Services.Start["SvcA"]);
        var backup = fx.Store.Get(t.Id);
        Assert.NotNull(backup);
        Assert.Equal("Automatic", backup!.Entry(new ServiceAction { Name = "SvcA" }.TargetKey)!.Original.Data);
        // Undo with working services restores it.
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(ServiceStart.Automatic, fx.Services.Start["SvcA"]);
    }

    private sealed class ThrowAfterFirstWrite(FakeServices inner, string once, string never) : IServiceManager
    {
        private bool _written;
        public ServiceStart? GetStartType(string name) => inner.GetStartType(name);

        public void SetStartType(string name, ServiceStart start)
        {
            if (name.Equals(never, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("access denied");
            if (name.Equals(once, StringComparison.OrdinalIgnoreCase) && _written) throw new InvalidOperationException("access denied");
            _written = true;
            inner.SetStartType(name, start);
        }
    }

    private static ActionContext WithInterfaces(ActionContext c, params string[] nics) => Copy(c, c.Services, nics);
    private static ActionContext WithServices(ActionContext c, IServiceManager services) => Copy(c, services, c.NetworkInterfaceIds);

    private static ActionContext Copy(ActionContext c, IServiceManager services, IReadOnlyList<string> nics) => new()
    {
        Registry = c.Registry, Services = services, Power = c.Power, Bcd = c.Bcd, Tasks = c.Tasks, Displays = c.Displays, Processes = c.Processes,
        PowerMode = c.PowerMode, Devices = c.Devices, Network = c.Network, MemoryAgent = c.MemoryAgent, Nvidia = c.Nvidia, Accessibility = c.Accessibility, Notify = c.Notify, ExportFolder = c.ExportFolder,
        NetworkInterfaceIds = nics,
    };

    [Fact]
    public async Task ClassicContextMenuKeyIsRemovedOnUndo()
    {
        using var fx = new EngineFixture();
        var t = T("explorer.classicContextMenu");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        using (var created = fx.Registry.Open(Hive.User, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", false))
            Assert.NotNull(created);
        fx.Engine.Revert(t);
        using var removed = fx.Registry.Open(Hive.User, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false);
        Assert.Null(removed);
    }

    [Fact]
    public async Task FirstOriginalIsNeverOverwritten()
    {
        using var fx = new EngineFixture();
        const string path = @"SYSTEM\CurrentControlSet\Control\FileSystem";
        // 0x80000002: system managed, last access updates on (the Windows default on small volumes).
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "NtfsDisableLastAccessUpdate", "dword", "2147483650");
        var t = T("storage.lastAccessOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "NtfsDisableLastAccessUpdate", "dword", "0");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert); // re-apply after drift

        Assert.Equal("2147483650", fx.Store.Get(t.Id)!.Entries.Single().Original.Data);
        fx.Engine.Revert(t);
        Assert.Equal("2147483650", Reg(fx, Hive.Machine, path, "NtfsDisableLastAccessUpdate").Data);
    }

    [Fact]
    public async Task MemoryCompressionUsesMmAgent()
    {
        using var fx = new EngineFixture();
        var t = T("memory.compressionOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Assert.False(fx.Processes.MemoryCompression);
        fx.Engine.Revert(t);
        Assert.True(fx.Processes.MemoryCompression);
        // Reads go to the MMAgent's WMI class: PowerShell starts only for the cmdlets that change the setting.
        Assert.All(fx.Processes.Calls, c => Assert.DoesNotContain("Get-MMAgent", c));
        Assert.Contains(fx.Processes.Calls, c => c.Contains("Disable-MMAgent"));
    }

    /// <summary>Simulated restart: pending changes take effect and the backup's restart time lies before the boot.</summary>
    private static void Restart(EngineFixture fx, string id)
    {
        fx.Processes.Restart();
        var beforeBoot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromHours(1);
        if (fx.Store.Get(id) is { PendingRestartSince: not null } b)
        {
            b.PendingRestartSince = beforeBoot;
            fx.Store.Save(b);
        }
        if (fx.Store.GetPendingUndo(id) is { } p)
            fx.Store.SavePendingUndo(new Backup.PendingUndo { TweakId = p.TweakId, Since = beforeBoot, Entries = p.Entries });
    }

    [Fact]
    public async Task MemoryCompressionTakesEffectAfterRestartAndUndoRestoresIt()
    {
        using var fx = new EngineFixture();
        fx.Processes.MemoryCompressionAfterRestart = true;
        var t = T("memory.compressionOff");

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert)).Outcome);
        Assert.True(fx.Processes.MemoryCompression); // still the running value
        Assert.Equal(TweakState.PendingRestart, fx.Engine.DetectState(t, Facts()));
        Assert.Empty(fx.Engine.CheckDrift(Facts()));

        Restart(fx, t.Id);
        Assert.False(fx.Processes.MemoryCompression);
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(t, Facts()));

        var undo = fx.Engine.Revert(t);
        Assert.True(undo.Success);
        Assert.Empty(undo.AlreadyRevertedByWindows);
        Assert.Contains(fx.Processes.Calls, c => c.Contains("Enable-MMAgent"));
        // Undone, but still off until the restart: not shown as on, and not as a change on the Changes page.
        Assert.Equal(TweakState.UndoPendingRestart, fx.Engine.DetectState(t, Facts()));
        Assert.False(fx.Engine.Detect(t, Facts()).IsOn);
        Assert.Null(fx.Store.Get(t.Id));

        Restart(fx, t.Id);
        Assert.True(fx.Processes.MemoryCompression);
        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(t, Facts()));
        Assert.Null(fx.Store.GetPendingUndo(t.Id));
    }

    [Fact]
    public async Task MemoryCompressionApplyAgainWhileTheUndoWaitsForTheRestart()
    {
        using var fx = new EngineFixture();
        fx.Processes.MemoryCompressionAfterRestart = true;
        var t = T("memory.compressionOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        Restart(fx, t.Id);
        fx.Engine.Revert(t); // Enable-MMAgent pending, compression still off
        Assert.Equal(TweakState.UndoPendingRestart, fx.Engine.DetectState(t, Facts()));

        // Turning it on again must not be "nothing to do": the pending Enable has to be replaced by Disable.
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert)).Outcome);
        Assert.Equal(2, fx.Processes.Calls.Count(c => c.Contains("Disable-MMAgent")));
        Assert.Null(fx.Store.GetPendingUndo(t.Id));
        Assert.Equal("True", fx.Store.Get(t.Id)!.Entries[0].Original.Data); // the real original, not the running value
        Restart(fx, t.Id);
        Assert.False(fx.Processes.MemoryCompression);

        // And the next undo turns it on again.
        fx.Engine.Revert(t);
        Restart(fx, t.Id);
        Assert.True(fx.Processes.MemoryCompression);
    }

    [Fact]
    public async Task MemoryCompressionUndoBeforeTheRestartCancelsTheChange()
    {
        using var fx = new EngineFixture();
        fx.Processes.MemoryCompressionAfterRestart = true;
        var t = T("memory.compressionOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Contains(fx.Processes.Calls, c => c.Contains("Enable-MMAgent"));
        // It never took effect, so nothing waits for the restart.
        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(t, Facts()));
        Assert.Null(fx.Store.GetPendingUndo(t.Id));
        fx.Processes.Restart();
        Assert.True(fx.Processes.MemoryCompression);
    }

    [Fact]
    public async Task MemoryCompressionBackupOfOlderVersionsIsStillUndone()
    {
        // Version 0.4.0 stored the running value (the original) as applied; undo after the restart must still run.
        using var fx = new EngineFixture();
        fx.Processes.MemoryCompressionAfterRestart = true;
        var t = T("memory.compressionOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        var b = fx.Store.Get(t.Id)!;
        b.Entries[0].Applied = b.Entries[0].Original;
        fx.Store.Save(b);
        Restart(fx, t.Id);

        Assert.True(fx.Engine.Revert(t).Success);
        fx.Processes.Restart();
        Assert.True(fx.Processes.MemoryCompression);
    }

    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    /// <summary>powercfg /hibernate as on real Windows: writes HibernateEnabled, "on" fails when the firmware has no S4.</summary>
    private static void FakePowercfg(EngineFixture fx) => fx.Processes.Handler = (file, args) =>
    {
        if (file != "powercfg.exe" || !args.StartsWith("/hibernate", StringComparison.Ordinal)) return null;
        var on = args.EndsWith(" on", StringComparison.Ordinal);
        if (on && fx.Power.Hibernation == false) return (1, "The system firmware does not support hibernation.");
        RegistryValue.Write(fx.Registry, Hive.Machine, PowerKey, "HibernateEnabled", "dword", on ? "1" : "0");
        return (0, "");
    };

    [Fact]
    public async Task HibernationRoundTrip()
    {
        using var fx = new EngineFixture();
        FakePowercfg(fx);
        RegistryValue.Write(fx.Registry, Hive.Machine, PowerKey, "HibernateEnabled", "dword", "1");
        var t = T("power.hibernateOff");

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert)).Outcome);
        Assert.Equal("0", Reg(fx, Hive.Machine, PowerKey, "HibernateEnabled").Data);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("1", Reg(fx, Hive.Machine, PowerKey, "HibernateEnabled").Data);
        Assert.Null(fx.Store.Get(t.Id));
    }

    [Fact]
    public async Task HibernationIsUnsupportedWithoutS4()
    {
        using var fx = new EngineFixture();
        FakePowercfg(fx);
        fx.Power.Hibernation = false;
        var t = T("power.hibernateOff");

        Assert.Equal(TweakState.Unsupported, fx.Engine.DetectState(t, Facts()));
        Assert.Equal(ApplyOutcome.Blocked, (await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert)).Outcome);
        Assert.DoesNotContain(fx.Processes.Calls, c => c.Contains("/hibernate"));
    }

    [Fact]
    public async Task HibernationBackupWithoutS4CanBeUndone()
    {
        // A backup made before the S4 check (version 0.4.0) on a PC without S4: undo has nothing to turn on and succeeds.
        using var fx = new EngineFixture();
        FakePowercfg(fx);
        var t = T("power.hibernateOff");
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), Expert);
        fx.Power.Hibernation = false;

        var undo = fx.Engine.Revert(t);
        Assert.True(undo.Success, string.Join("; ", undo.Errors));
        Assert.Null(fx.Store.Get(t.Id));
        Assert.DoesNotContain(fx.Processes.Calls, c => c.EndsWith("/hibernate on", StringComparison.Ordinal));
    }

    [Fact]
    public void NotApplicableOnSmallRam() =>
        Assert.Equal(TweakState.NotApplicable, new EngineFixture().Engine.DetectState(T("memory.compressionOff"), Facts(f => f.Set("memory.totalGb", 8.0))));

    [Fact]
    public void ImpactOverrideForFrameGeneration()
    {
        Assert.Equal(0, TweakEngine.ImpactFor(T("gpu.hags"), Facts()).Impact);
        var (impact, effects, _) = TweakEngine.ImpactFor(T("gpu.hags"), Facts(f => f.Set("gpu.supportsFrameGeneration", true)));
        Assert.Equal(3, impact);
        Assert.Contains("prerequisite", effects);
    }

    [Fact]
    public async Task RevertAllUndoesEverythingReversible()
    {
        using var fx = new EngineFixture();
        foreach (var id in new[] { "gpu.hags", "storage.lastAccessOff", "explorer.fileExtensions" })
            await fx.Engine.ApplyAsync(T(id), Facts(), new HashSet<string>(), Expert);
        var results = fx.Engine.RevertAll();
        Assert.True(results.Count >= 3);
        Assert.All(results, r => Assert.True(r.Result.Success));
        Assert.Empty(fx.Store.All());
    }
}
