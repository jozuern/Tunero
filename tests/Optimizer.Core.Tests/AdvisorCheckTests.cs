using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;
using Optimizer.Core.Hardware.Probes;
using Optimizer.Core.Interop;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Advisor checks with mocked profiles: every rule, its edge cases and the rendered explanation.</summary>
public class AdvisorCheckTests
{
    private static readonly CatalogData C = CatalogData.Current;

    private static HardwareProfile P(Func<HardwareProfile, HardwareProfile>? edit = null)
    {
        var p = new HardwareProfile { Os = TestData.Os(), Firmware = TestData.Firmware() };
        return edit is null ? p : edit(p);
    }

    private static Finding One(IFindingCheck check, HardwareProfile p) => check.Evaluate(p, C).Single();

    /// <summary>Every finding must render in both languages without unresolved placeholders or block markers.</summary>
    private static void AssertRenders(Finding f)
    {
        foreach (var lang in DocStore.Languages)
        {
            var page = DocStore.Get(f.Id, lang);
            Assert.NotNull(page);
            var md = DocStore.RenderFinding(page!, f, Labels.Current);
            Assert.DoesNotContain("{{", md);
            Assert.DoesNotContain(":::", md);
            foreach (var fact in f.Facts)
            {
                Assert.True(Labels.Current.Has(lang, fact.LabelKey), $"label {fact.LabelKey} missing ({lang})");
                if (fact.Value.StartsWith('@')) Assert.True(Labels.Current.Has(lang, "value." + fact.Value[1..]), $"value {fact.Value} missing ({lang})");
            }
        }
    }

    private static DisplayInfo Display(uint output = 10, int maxOffered = 144, EdidInfo? edid = null, Vendor vendor = Vendor.Nvidia, bool internalPanel = false, string adapter = "NVIDIA GeForce RTX 2070") =>
        new(@"\\.\DISPLAY1", "27G1G4", "", internalPanel, output, 1920, 1080, new RefreshRate(144000, 1000), maxOffered,
            [new DisplayMode(1920, 1080, maxOffered, 32), new DisplayMode(1280, 720, maxOffered, 32)], "", vendor, adapter, false, false, edid);

    private static EdidInfo Edid(int min, int max, bool continuous = true) =>
        new("AOC", 0x2701, "27G1G4", min, max, 1920, 1080, 144) { ContinuousFrequency = continuous };

    // ---------------- F2 ----------------

    [Theory]
    [InlineData(144, 60, 60.0, true)]
    [InlineData(75, 60, 75.0, true)]    // a 75 Hz monitor (preferred mode 75 Hz) held at 60 Hz
    [InlineData(76, 60, 60.0, false)]   // a 60 Hz monitor whose range limits end at 76 Hz
    [InlineData(75, 60, null, false)]
    [InlineData(144, 144, 144.0, false)]
    [InlineData(165, 144, 165.0, false)] // 14 % gap: within tolerance
    [InlineData(70, 50, 60.0, false)]
    public void EdidLimit(int edid, int offered, double? preferred, bool limited) => Assert.Equal(limited, EdidRefreshCheck.IsLimited(edid, offered, preferred));

    [Fact]
    public void EdidRefreshOverHdmiIsProblemWithHdmiVariant()
    {
        var f = One(new EdidRefreshCheck(), P(p => p with { Displays = [Display(output: 5, maxOffered: 60, edid: Edid(48, 144))] }));
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Equal("hdmi", f.Variant);
        AssertRenders(f);
    }

    [Fact]
    public void EdidWithoutRangeIsUnsupported()
    {
        var f = One(new EdidRefreshCheck(), P(p => p with { Displays = [Display(edid: new EdidInfo("AOC", 1, null, null, null, null, null, null))] }));
        Assert.Equal(FindingStatus.Unsupported, f.Status);
    }

    // ---------------- F7 ----------------

    private static HardwareProfile Nv(uint? frl = null, uint? pstate = null, uint? vsync = null, bool vrrEnabled = false) => P(p => p with
    {
        Gpus = [TestData.Gpu("NVIDIA GeForce RTX 2070", Vendor.Nvidia, 256L << 20)],
        Displays = [Display()],
        Extras = new HardwareExtras
        {
            Nvidia = new NvidiaInfo("617.42", frl, vsync, pstate, null, null,
                new Dictionary<string, NvidiaVrr> { [@"\\.\DISPLAY1"] = new(vrrEnabled, true, false, false) }),
        },
    });

    [Fact]
    public void LowGlobalFrameCapIsProblemAndFixResetsOnlyIt()
    {
        var f = One(new NvidiaGlobalCheck(), Nv(frl: 60));
        Assert.Equal(FindingStatus.Problem, f.Status);
        var action = Assert.IsType<NvidiaDrsAction>(Assert.Single(f.Fix!.Actions));
        Assert.Equal(Nvapi.SettingFrameRateLimiter, action.SettingId);
        Assert.Null(action.Value);
        AssertRenders(f);
    }

    [Fact]
    public void LowCapWithGsyncAndForcedVsyncIsResetToACapBelowTheRefreshRate()
    {
        // Removing the cap would leave G-SYNC without the cap it needs, and the next scan would offer the frame cap fix for
        // the same driver setting (which the engine blocks while the reset is in place). The reset sets the cap instead.
        var f = One(new NvidiaGlobalCheck(), Nv(frl: 30, pstate: Nvapi.PStatePreferMin, vsync: Nvapi.VSyncForceOn, vrrEnabled: true));
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Equal("yes", f.Params["capInReset"]);
        Assert.Equal("fix.nvidiaGlobal", f.Fix!.Id);
        var actions = f.Fix.Actions.Cast<NvidiaDrsAction>().ToDictionary(a => a.SettingId, a => a.Value);
        Assert.Equal((uint?)141, actions[Nvapi.SettingFrameRateLimiter]);
        Assert.Null(actions[Nvapi.SettingPreferredPState]);
        AssertRenders(f);
        // Once the cap is set, the check is content and offers no second fix for the same setting.
        Assert.Equal(FindingStatus.Ok, One(new NvidiaGlobalCheck(), Nv(frl: 141, vsync: Nvapi.VSyncForceOn, vrrEnabled: true)).Status);
        // Without forced V-Sync the reset removes the cap as before.
        Assert.Equal("", One(new NvidiaGlobalCheck(), Nv(frl: 30, vrrEnabled: true)).Params["capInReset"]);
    }

    [Fact]
    public void GsyncStyleCapIsOk() => Assert.Equal(FindingStatus.Ok, One(new NvidiaGlobalCheck(), Nv(frl: 141)).Status);

    [Fact]
    public void PreferMinimumPowerIsProblem() => Assert.Equal(FindingStatus.Problem, One(new NvidiaGlobalCheck(), Nv(pstate: Nvapi.PStatePreferMin)).Status);

    [Fact]
    public void ForcedVsyncIsInfoWithoutGsyncAndOkWithIt()
    {
        var f = One(new NvidiaGlobalCheck(), Nv(vsync: Nvapi.VSyncForceOn));
        Assert.Equal(FindingStatus.Info, f.Status);
        Assert.Null(f.Fix);
        AssertRenders(f);
        // With G-SYNC, forced V-Sync plus a cap a few FPS below the refresh rate is the recommended setup: fine.
        Assert.Equal(FindingStatus.Ok, One(new NvidiaGlobalCheck(), Nv(frl: 141, vsync: Nvapi.VSyncForceOn, vrrEnabled: true)).Status);
    }

    [Fact]
    public void GsyncWithForcedVsyncAndNoCapGetsACapBelowTheRefreshRate()
    {
        var f = One(new NvidiaGlobalCheck(), Nv(vsync: Nvapi.VSyncForceOn, vrrEnabled: true));
        Assert.Equal((FindingStatus.Info, 2), (f.Status, f.Impact));
        Assert.Equal("141", f.Params["capTarget"]); // 144 Hz display
        var cap = Assert.IsType<NvidiaDrsAction>(Assert.Single(f.Fix!.Actions));
        Assert.Equal((Nvapi.SettingFrameRateLimiter, (uint?)141), (cap.SettingId, cap.Value));
        Assert.True(f.Fix.Preview);
        AssertRenders(f);
        // G-SYNC without forced V-Sync: nothing to report.
        Assert.Equal(FindingStatus.Ok, One(new NvidiaGlobalCheck(), Nv(vrrEnabled: true)).Status);
    }

    // ---------------- F31 ----------------

    [Fact]
    public void MemoryIntegrityRunningIsInfoAndCostsMoreWithoutMbec()
    {
        var on = One(new MemoryIntegrityCheck(), P(p => p with { Firmware = TestData.Firmware() with { HvciRunning = true } }));
        Assert.Equal((FindingStatus.Info, 2, null), (on.Status, on.Impact, on.Variant));
        AssertRenders(on);
        var emulated = One(new MemoryIntegrityCheck(), P(p => p with { Firmware = TestData.Firmware() with { HvciRunning = true, MbecAvailable = false } }));
        Assert.Equal((FindingStatus.Info, 3, "noMbec"), (emulated.Status, emulated.Impact, emulated.Variant));
        AssertRenders(emulated);
        Assert.Equal(FindingStatus.Ok, One(new MemoryIntegrityCheck(), P(p => p with { Firmware = TestData.Firmware() })).Status);
    }

    [Fact]
    public void NoNvidiaGpuGivesNoF7() => Assert.Empty(new NvidiaGlobalCheck().Evaluate(P(), C));

    // ---------------- F8 ----------------

    private static PowerInfo Power(bool onAc = true, PowerPersonality personality = PowerPersonality.Balanced) =>
        new(Guid.Empty, "Balanced", personality, 100, 5, 2, 100, onAc, false, false, null);

    [Fact]
    public void BestEfficiencyOnAcIsProblemWithFix()
    {
        var f = One(new PowerModeCheck(), P(p => p with { Power = Power(), Extras = new HardwareExtras { PowerOverlay = FirmwareExtras.OverlayBetterBattery } }));
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Equal(FirmwareExtras.OverlayBestPerformance, Assert.IsType<PowerModeAction>(f.Fix!.Actions.Single()).Overlay);
        AssertRenders(f);
    }

    [Fact]
    public void PowerModeSkippedOnBatteryOrOtherPlans()
    {
        var extras = new HardwareExtras { PowerOverlay = FirmwareExtras.OverlayBetterBattery };
        Assert.Empty(new PowerModeCheck().Evaluate(P(p => p with { Power = Power(onAc: false), Extras = extras }), C));
        Assert.Empty(new PowerModeCheck().Evaluate(P(p => p with { Power = Power(personality: PowerPersonality.HighPerformance), Extras = extras }), C));
        Assert.Equal(FindingStatus.Ok, One(new PowerModeCheck(), P(p => p with { Power = Power(), Extras = new HardwareExtras { PowerOverlay = Guid.Empty } })).Status);
    }

    // ---------------- F14 / F15 ----------------

    [Fact]
    public void RunningOverlayIsInfo()
    {
        var f = One(new OverlaysCheck(), P(p => p with { Extras = new HardwareExtras { RunningOverlays = ["Discord"] } }));
        Assert.Equal(FindingStatus.Info, f.Status);
        AssertRenders(f);
    }

    private static CpuInfo Amd(string name, int family, int model, string socket, params long[] l3Mb) => new(name, Vendor.Amd, family, model, 0, 8, 16, 4000, socket,
        null, null, "test", l3Mb.Select((s, i) => new CacheDomain(3, s << 20, 0, i)).ToList(), new Dictionary<int, int> { [0] = 8 });

    [Fact]
    public void ChipsetMissingIsProblemOnDualCcdX3dOnly()
    {
        var x3d = Amd("AMD Ryzen 9 9950X3D 16-Core Processor", 26, 68, "AM5", 96, 32);
        var plain = Amd("AMD Ryzen 7 7700X 8-Core Processor", 25, 97, "AM5", 32);
        var none = new HardwareExtras { Programs = [new InstalledProgram("Steam", null, null, false)] };
        var with = new HardwareExtras { Programs = [new InstalledProgram("AMD Chipset Software", "7.01.08.129", "AMD", false)] };

        var f = One(new AmdChipsetCheck(), P(p => p with { Cpu = x3d, Extras = none }));
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Equal("x3d", f.Variant);
        AssertRenders(f);
        Assert.Equal(FindingStatus.Info, One(new AmdChipsetCheck(), P(p => p with { Cpu = plain, Extras = none })).Status);
        Assert.Equal(FindingStatus.Ok, One(new AmdChipsetCheck(), P(p => p with { Cpu = x3d, Extras = with })).Status);
        Assert.Equal(FindingStatus.Unknown, One(new AmdChipsetCheck(), P(p => p with { Cpu = plain, Extras = new HardwareExtras() })).Status);
        Assert.Empty(new AmdChipsetCheck().Evaluate(P(p => p with { Cpu = TestData.Cpu9700K(), Extras = none }), C));
    }

    // ---------------- F20 ----------------

    [Fact]
    public void VrrPossibleButOffIsProblem()
    {
        var p = Nv() with { Displays = [Display(edid: Edid(48, 144))] };
        var f = One(new VrrCheck(), p);
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Equal("off", f.Variant);
        AssertRenders(f);
    }

    [Fact]
    public void VrrLookingMonitorNotPossibleIsInfo()
    {
        var p = P(p => p with
        {
            Displays = [Display(output: 5, edid: Edid(48, 144))],
            Extras = new HardwareExtras { Nvidia = new NvidiaInfo(null, null, null, null, null, null, new Dictionary<string, NvidiaVrr> { [@"\\.\DISPLAY1"] = new(false, false, false, false) }) },
        });
        var f = One(new VrrCheck(), p);
        Assert.Equal(FindingStatus.Info, f.Status);
        Assert.Equal("yes", f.Params["hdmi"]);
        AssertRenders(f);
    }

    [Fact]
    public void OtherVendorVrrIsInfoAndFixedMonitorIsSkipped()
    {
        // Unknown never carries advice; the setup steps for AMD/Intel VRR are information.
        var f = One(new VrrCheck(), P(p => p with { Displays = [Display(vendor: Vendor.Amd, edid: Edid(48, 144))] }));
        Assert.Equal(FindingStatus.Info, f.Status);
        Assert.Equal("otherVendor", f.Variant);
        AssertRenders(f);
        Assert.Empty(new VrrCheck().Evaluate(P(p => p with { Displays = [Display(vendor: Vendor.Amd, edid: Edid(56, 61))] }), C));
        Assert.Empty(new VrrCheck().Evaluate(P(p => p with { Displays = [Display(vendor: Vendor.Amd, edid: Edid(48, 144, continuous: false))] }), C));
    }

    // ---------------- F22 ----------------

    [Fact]
    public void SecureBootCerts()
    {
        HardwareProfile With(bool? kek, bool? db) => P(p => p with { Extras = new HardwareExtras { SecureBootCerts = new SecureBootCerts(kek, db, true, true, "InProgress") } });
        var info = One(new SecureBootCertsCheck(), With(false, true));
        Assert.Equal(FindingStatus.Info, info.Status);
        AssertRenders(info);
        Assert.Equal(FindingStatus.Ok, One(new SecureBootCertsCheck(), With(true, true)).Status);
        Assert.Equal(FindingStatus.Unknown, One(new SecureBootCertsCheck(), With(null, null)).Status);
        var noSecureBoot = P(p => p with { Firmware = TestData.Firmware() with { SecureBoot = TriState.No } });
        Assert.Empty(new SecureBootCertsCheck().Evaluate(noSecureBoot, C));
    }

    // ---------------- F24 / F25 ----------------

    private static NicDetail Nic(long speedBps, string? speedDuplex, int? max, string type = "Ethernet") => new("{1E2F708A-35FE-4B75-AA24-30EDD75652D3}",
        "Ethernet", "Intel(R) Ethernet Connection (7) I219-V", type, true, speedBps, max, @"SYSTEM\x\0001",
        speedDuplex is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["*SpeedDuplex"] = speedDuplex },
        new Dictionary<string, IReadOnlyList<string>>()) { DeviceInstanceId = @"PCI\VEN_8086&DEV_15BC\3", Characteristics = 0x4 };

    [Fact]
    public void ForcedEthernetSpeedIsProblemWithAutoFix()
    {
        var f = One(new EthernetSpeedCheck(), P(p => p with { Extras = new HardwareExtras { Nics = [Nic(100_000_000, "4", 1000)] } }));
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Equal("forced", f.Variant);
        var a = Assert.IsType<NicPropertyAction>(f.Fix!.Actions.Single());
        Assert.Equal("0", a.Properties["*SpeedDuplex"]);
        AssertRenders(f);
    }

    [Fact]
    public void EthernetNegotiationRules()
    {
        var neg = One(new EthernetSpeedCheck(), P(p => p with { Extras = new HardwareExtras { Nics = [Nic(100_000_000, "0", 1000)] } }));
        Assert.Equal("negotiated", neg.Variant);
        Assert.Null(neg.Fix);
        // 2.5G adapter on a 1G router is normal.
        Assert.Equal(FindingStatus.Ok, One(new EthernetSpeedCheck(), P(p => p with { Extras = new HardwareExtras { Nics = [Nic(1_000_000_000, "0", 2500)] } })).Status);
        // On battery the driver's power saving can lower the link: information, check again on mains power.
        var battery = One(new EthernetSpeedCheck(), P(p => p with { Power = Power(onAc: false), Extras = new HardwareExtras { Nics = [Nic(100_000_000, "0", 1000)] } }));
        Assert.Equal((FindingStatus.Info, "negotiatedBattery"), (battery.Status, battery.Variant));
        AssertRenders(battery);
    }

    private static Wlan.Connection Wifi(uint? khz, params uint[] same) => new("Intel(R) Wireless-AC 8260", "Home", "AABBCCDDEEFF", khz, same);

    [Fact]
    public void WifiBandRules()
    {
        HardwareProfile W(Wlan.Connection c, bool ethernetUp = false) => P(p => p with
        {
            Extras = new HardwareExtras { Wifi = [c], Nics = ethernetUp ? [Nic(1_000_000_000, "0", 1000)] : [] },
        });
        var problem = One(new WifiBandCheck(), W(Wifi(2_437_000, 2_437_000, 5_180_000)));
        Assert.Equal(FindingStatus.Problem, problem.Status);
        AssertRenders(problem);
        Assert.Equal(FindingStatus.Info, One(new WifiBandCheck(), W(Wifi(2_437_000, 2_437_000))).Status);
        Assert.Equal(FindingStatus.Ok, One(new WifiBandCheck(), W(Wifi(5_745_000))).Status);
        Assert.Equal(FindingStatus.Unknown, One(new WifiBandCheck(), W(Wifi(37))).Status); // misread layout
        Assert.Empty(new WifiBandCheck().Evaluate(W(Wifi(2_437_000), ethernetUp: true), C));
    }

    // ---------------- F26 / F27 ----------------

    [Theory]
    [InlineData(3, 2, 3, 4, null, null, FindingStatus.Problem, "trainedDown")]
    [InlineData(3, 2, 3, 4, 3, 2, FindingStatus.Info, "slotWidth")]
    [InlineData(3, 4, 4, 4, 3, 4, FindingStatus.Info, "slotGen")]
    [InlineData(1, 4, 4, 4, null, null, FindingStatus.Info, "genLower")]
    [InlineData(3, 4, 3, 4, null, null, FindingStatus.Ok, null)]
    public void NvmeLinkRules(int cg, int cw, int mg, int mw, int? pg, int? pw, FindingStatus status, string? variant)
    {
        var port = pg is null ? null : new PcieLink(pg, pw, pg, pw);
        var p = P(p => p with { Extras = new HardwareExtras { NvmeLinks = [new NvmeLink("Samsung SSD 970 EVO Plus 1TB", @"PCI\VEN_144D", new PcieLink(cg, cw, mg, mw), port)] } });
        var f = One(new NvmeLinkCheck(), p);
        Assert.Equal(status, f.Status);
        Assert.Equal(variant, f.Variant);
        AssertRenders(f);
    }

    [Fact]
    public void LaptopPanelOnIgpuIsInfo()
    {
        var igpu = TestData.Gpu("Intel(R) UHD Graphics", Vendor.Intel, null) with { Kind = GpuKind.Integrated };
        var dgpu = TestData.Gpu("NVIDIA GeForce RTX 4060 Laptop GPU", Vendor.Nvidia, null);
        var p = P(p => p with
        {
            System = new SystemInfo("Lenovo", "Legion", [10], true, true, false),
            Gpus = [igpu, dgpu],
            Displays = [Display(output: 0x80000000, internalPanel: true, adapter: igpu.Name, vendor: Vendor.Intel)],
        });
        var f = One(new LaptopPanelCheck(), p);
        Assert.Equal(FindingStatus.Info, f.Status);
        AssertRenders(f);
    }

    // ---------------- advisor ----------------

    [Fact]
    public void ApoOnlyForListedProcessors()
    {
        var k = TestData.Cpu9700K() with { Name = "Intel(R) Core(TM) i9-14900K" };
        var f = One(new ApoCheck(), P(p => p with { Cpu = k, Extras = new HardwareExtras() }));
        Assert.Equal(FindingStatus.Info, f.Status);
        Assert.Equal("dttNotFound", f.Variant);
        AssertRenders(f);
        Assert.Empty(new ApoCheck().Evaluate(P(p => p with { Cpu = TestData.Cpu9700K() }), C));
    }

    [Fact]
    public void AmdFtpmRules()
    {
        var cpu = Amd("AMD Ryzen 7 5800X 8-Core Processor", 25, 33, "AM4", 32);
        HardwareProfile F(string? tpm, Version? agesa, DateTime bios) => P(p => p with
        {
            Cpu = cpu,
            Firmware = TestData.Firmware() with { BiosDate = bios },
            Extras = new HardwareExtras { TpmManufacturer = tpm, Agesa = agesa, AgesaSource = agesa is null ? null : $"AGESA ComboAM4v2PI {agesa}" },
        });
        var problem = One(new AmdFtpmCheck(), F("AMD", new Version(1, 2, 0, 3), new DateTime(2021, 6, 1)));
        Assert.Equal(FindingStatus.Problem, problem.Status);
        AssertRenders(problem);
        Assert.Equal(FindingStatus.Ok, One(new AmdFtpmCheck(), F("AMD", new Version(1, 2, 0, 7), new DateTime(2021, 6, 1))).Status);
        var byDate = One(new AmdFtpmCheck(), F("AMD", null, new DateTime(2022, 1, 1)));
        Assert.Equal(("date", FindingStatus.Problem), (byDate.Variant, byDate.Status));
        Assert.Empty(new AmdFtpmCheck().Evaluate(F("IFX", null, new DateTime(2021, 1, 1)), C)); // discrete TPM: not affected
        Assert.False(AmdFtpmCheck.IsAm4(Amd("AMD Ryzen 7 7700X 8-Core Processor", 25, 97, "AM5", 32), false));
        Assert.True(AmdFtpmCheck.IsAm4(Amd("AMD Ryzen 5 3600 6-Core Processor", 23, 113, "", 16, 16), false));

        // The older ComboAM4PI line (1.0.0.x) is judged by the BIOS date, not against 1.2.0.7.
        var oldLine = One(new AmdFtpmCheck(), P(p => p with
        {
            Cpu = cpu,
            Firmware = TestData.Firmware() with { BiosDate = new DateTime(2023, 3, 1) },
            Extras = new HardwareExtras { TpmManufacturer = "AMD", Agesa = new Version(1, 0, 0, 9), AgesaSource = "AGESA ComboAM4PI 1.0.0.9" },
        }));
        Assert.Equal(("date", FindingStatus.Ok), (oldLine.Variant, oldLine.Status));
    }

    [Theory]
    [InlineData("AGESA ComboAM4v2PI 1.2.0.7", "1.2.0.7")]
    [InlineData("AGESA ComboAM4v2PI 1.2.0.Ca", "1.2.0.12")]  // letter versions follow 1.2.0.9
    [InlineData("AGESA ComboAM4v2PI 1.2.0.A", "1.2.0.10")]
    [InlineData("ComboAM5PI 1.2.0.2b", "1.2.0.2")]
    [InlineData("no agesa here", null)]
    public void ReadsAgesaVersionsWithLetters(string smbios, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), Optimizer.Core.Platform.FirmwareExtras.AgesaVersion([smbios]));

    private static MemoryModule Ddr(int configured, string part, int type) => new(16L << 30, configured, configured, part, "Corsair", "DIMM_A1", "BANK 0", type, 8);

    [Fact]
    public void RyzenMemoryRules()
    {
        var am4 = Amd("AMD Ryzen 7 5800X 8-Core Processor", 25, 33, "AM4", 32);
        HardwareProfile M(int mts, string part, int type) => P(p => p with { Cpu = am4, Memory = new MemoryInfo(32L << 30, [Ddr(mts, part, type), Ddr(mts, part, type)]) });
        var above = One(new RyzenMemoryCheck(), M(4000, "CMK16GX4M2Z4000C18", 26));
        Assert.Equal(("aboveSync", FindingStatus.Info), (above.Variant, above.Status));
        AssertRenders(above);
        Assert.Equal(FindingStatus.Ok, One(new RyzenMemoryCheck(), M(3600, "CMK16GX4M2Z3600C18", 26)).Status);
        Assert.Equal(FindingStatus.Ok, One(new RyzenMemoryCheck(), M(6000, "CMK32GX5M2B6000C30", 34)).Status);
        Assert.Equal("aboveSync", One(new RyzenMemoryCheck(), M(6400, "CMK32GX5M2B6400C32", 34)).Variant);
    }

    [Fact]
    public void BiosAgeAndSystemHdd()
    {
        var old = One(new BiosAgeCheck(), P(p => p with { Firmware = TestData.Firmware() with { BiosDate = DateTime.Today.AddDays(-400) } }));
        Assert.Equal(FindingStatus.Info, old.Status);
        Assert.Equal("https://www.asus.com/support/download-center/", old.Params["supportUrl"]);
        AssertRenders(old);

        var storage = new StorageInfo([new PhysicalDisk(0, "ST2000DM008", "HDD", "SATA", 2_000_000_000_000, "Healthy", false)],
            [new Volume(@"C:\", "", 2_000_000_000_000, 1_000_000_000_000, 0, true)]);
        var hdd = One(new SystemOnHddCheck(), P(p => p with { Storage = storage }));
        Assert.Equal(FindingStatus.Problem, hdd.Status);
        AssertRenders(hdd);

        // Storage Spaces: the volume's disk number belongs to the virtual disk. A pooled disk with the same number, two
        // disks with it, or a volume larger than the matching disk give no verdict instead of one about the wrong disk.
        var pooled = new StorageInfo([new PhysicalDisk(0, "ST2000DM008", "HDD", "SATA", 2_000_000_000_000, "Healthy", false)],
            [new Volume(@"C:\", "", 3_900_000_000_000, 1_000_000_000_000, 0, true)]);
        Assert.Empty(new SystemOnHddCheck().Evaluate(P(p => p with { Storage = pooled }), CatalogData.Current));
        var twice = storage with { Disks = [.. storage.Disks, new PhysicalDisk(0, "Samsung SSD 990 PRO", "SSD", "NVMe", 2_000_000_000_000, "Healthy", false)] };
        Assert.Null(twice.DiskOf(twice.Volumes[0]));
        Assert.NotNull(storage.DiskOf(storage.Volumes[0]));
    }

    [Fact]
    public void UnusedIgpuOnDesktopIsInfo()
    {
        var igpu = TestData.Gpu("Intel(R) UHD Graphics 630", Vendor.Intel, null) with { Kind = GpuKind.Integrated };
        var f = One(new IgpuUnusedCheck(), P(p => p with { Gpus = [igpu, TestData.Gpu("NVIDIA GeForce RTX 2070", Vendor.Nvidia, null)], Displays = [Display()] }));
        Assert.Equal(FindingStatus.Info, f.Status);
        AssertRenders(f);
    }

    // ---------------- Game access ----------------

    private static HardwareProfile Ac(string id, string name, Func<FirmwareInfo, FirmwareInfo>? fw = null) => P(p => p with
    {
        Firmware = fw?.Invoke(TestData.Firmware()) ?? TestData.Firmware(),
        Software = new SoftwareInfo([new AntiCheatPresence(id, name, ["vgk"])], [], []),
    });

    [Fact]
    public void VanguardWithoutSecureBootIsProblem()
    {
        var f = One(new GameAccessCheck(), Ac("vanguard", "Riot Vanguard", fw => fw with { SecureBoot = TriState.No }));
        Assert.Equal(FindingStatus.Problem, f.Status);
        Assert.Contains(f.Facts, x => x.LabelKey == "fact.required.secureBoot" && x.Value == "Riot Vanguard");
        AssertRenders(f);
    }

    [Fact]
    public void VanguardWithoutHvciIsInfoSometimes()
    {
        // TestData firmware: UEFI, Secure Boot, TPM 2.0 on; HVCI off.
        var f = One(new GameAccessCheck(), Ac("vanguard", "Riot Vanguard"));
        Assert.Equal(FindingStatus.Info, f.Status);
        Assert.Equal("sometimes", f.Variant);
        AssertRenders(f);
    }

    [Fact]
    public void LenientAntiCheatOnlyUsesBaseline()
    {
        Assert.Equal(FindingStatus.Ok, One(new GameAccessCheck(), Ac("battleye", "BattlEye")).Status);
        var noTpm = One(new GameAccessCheck(), Ac("battleye", "BattlEye", fw => fw with { TpmPresent = TriState.No }));
        Assert.Equal(("baseline", FindingStatus.Info), (noTpm.Variant, noTpm.Status));
        AssertRenders(noTpm);
        // Easy Anti-Cheat lets each game ask for TPM 2.0 (easy.ac): a missing TPM is a "sometimes" part there.
        var eac = One(new GameAccessCheck(), Ac("eac", "Easy Anti-Cheat", fw => fw with { TpmPresent = TriState.No }));
        Assert.Equal(("sometimes", "yes"), (eac.Variant, eac.Params["missing_tpm2"]));
        AssertRenders(eac);
    }

    [Fact]
    public void UnverifiedAntiCheatSaysSoOnItsPage()
    {
        var javelin = One(new GameAccessCheck(), Ac("javelin", "EA Javelin"));
        Assert.Equal("yes", javelin.Params["unverified_javelin"]);
        AssertRenders(javelin);
        Assert.Contains("not confirmed yet", DocStore.RenderFinding(DocStore.Get(javelin.Id, "en")!, javelin, Labels.Current));

        var vanguard = One(new GameAccessCheck(), Ac("vanguard", "Riot Vanguard"));
        Assert.False(vanguard.Params.ContainsKey("unverified_vanguard"));
    }

    // ---------------- Stability: virtualization, mixed memory, unexpected shutdowns ----------------

    private static HardwareProfile WithVirtualization(HardwareProfile p, bool hypervisor, bool cpu, bool firmware) =>
        p with { Extras = new HardwareExtras { Virtualization = new VirtualizationInfo(hypervisor, cpu, firmware) } };

    [Fact]
    public void RunningHypervisorCountsAsVirtualizationOn()
    {
        // As on the real test PC: memory integrity runs, both processor flags read false.
        var f = One(new VirtualizationCheck(), WithVirtualization(P(), hypervisor: true, cpu: false, firmware: false));
        Assert.Equal(FindingStatus.Ok, f.Status);
        AssertRenders(f);
    }

    [Fact]
    public void VirtualizationOffIsAProblemOnlyWithAnAntiCheatThatCanAskForMemoryIntegrity()
    {
        var plain = One(new VirtualizationCheck(), WithVirtualization(P(), false, true, false));
        Assert.Equal((FindingStatus.Info, "off"), (plain.Status, plain.Variant));
        AssertRenders(plain);

        var vanguard = One(new VirtualizationCheck(), WithVirtualization(Ac("vanguard", "Riot Vanguard"), false, true, false));
        Assert.Equal((FindingStatus.Problem, "antiCheat"), (vanguard.Status, vanguard.Variant));
        Assert.Equal("Riot Vanguard", vanguard.Params["antiCheats"]);
        AssertRenders(vanguard);

        Assert.Equal(FindingStatus.Unsupported, One(new VirtualizationCheck(), WithVirtualization(P(), false, false, false)).Status);
        Assert.Equal(FindingStatus.Unknown, One(new VirtualizationCheck(), P()).Status);
    }

    private static MemoryModule Ram(string locator, string part, int gb = 16) => new((long)gb << 30, 3200, 3200, part, "Corsair", locator, "", 26, 8);

    private static HardwareProfile WithRam(params MemoryModule[] modules) =>
        P(p => p with { Memory = new MemoryInfo(modules.Sum(m => m.CapacityBytes), modules) });

    [Fact]
    public void MixedMemoryKitsAreReported()
    {
        Assert.Equal(FindingStatus.Ok, One(new MixedMemoryCheck(), WithRam(Ram("A2", "CMW32GX4M2E3200C16"), Ram("B2", "CMW32GX4M2E3200C16"))).Status);
        var mixed = One(new MixedMemoryCheck(), WithRam(Ram("A2", "CMW32GX4M2E3200C16"), Ram("B2", "F4-3200C16-8GVKB", 8)));
        Assert.Equal(FindingStatus.Info, mixed.Status);
        AssertRenders(mixed);
        Assert.Empty(new MixedMemoryCheck().Evaluate(WithRam(Ram("A2", "X")), C));
    }

    private static HardwareProfile WithShutdowns(params UnexpectedShutdown[] events) =>
        P(p => p with { Extras = new HardwareExtras { UnexpectedShutdowns = events } });

    [Fact]
    public void UnexpectedShutdownsWithAStopErrorNameItsCode()
    {
        Assert.Equal(FindingStatus.Ok, One(new UnexpectedRestartCheck(), WithShutdowns()).Status);
        Assert.Equal(FindingStatus.Unknown, One(new UnexpectedRestartCheck(), P()).Status);

        var crash = One(new UnexpectedRestartCheck(), WithShutdowns(new UnexpectedShutdown(DateTime.UtcNow, 159, false)));
        Assert.Equal((FindingStatus.Problem, "stop", "0x0000009F"), (crash.Status, crash.Variant, crash.Params["stopCode"]));
        AssertRenders(crash);

        // One forced power off (power button held, no Stop error) is the user's own action: information only.
        var power = One(new UnexpectedRestartCheck(), WithShutdowns(new UnexpectedShutdown(DateTime.UtcNow, 0, true)));
        Assert.Equal((FindingStatus.Info, "power", "yes"), (power.Status, power.Variant, power.Params["powerButton"]));
        AssertRenders(power);
        var twice = One(new UnexpectedRestartCheck(), WithShutdowns(new UnexpectedShutdown(DateTime.UtcNow, 0, true), new UnexpectedShutdown(DateTime.UtcNow, 0, false)));
        Assert.Equal(FindingStatus.Problem, twice.Status);
    }

    [Fact]
    public void ReadsKernelPowerEventsFromWevtutilXml()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID><TimeCreated SystemTime='2026-10-01T18:20:05.1234567Z'/></System><EventData><Data Name='BugcheckCode'>0</Data><Data Name='PowerButtonTimestamp'>133912345678901234</Data></EventData></Event>
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID><TimeCreated SystemTime='2026-10-05T09:00:00.0000000Z'/></System><EventData><Data Name='BugcheckCode'>159</Data><Data Name='PowerButtonTimestamp'>0</Data></EventData></Event>
            """;
        var events = StabilityProbe.Parse(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal((159u, false), (events[0].BugcheckCode, events[0].PowerButton)); // newest first
        Assert.True(events[1].PowerButton);
        Assert.Empty(StabilityProbe.Parse(""));
    }
}

/// <summary>The extended action types (NIC, DNS, NVIDIA, power mode) against the sandbox registry and fakes.</summary>
public class ExtendedActionTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true);
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true };

    private static TweakDefinition Tweak(string id, params TweakAction[] actions) => new()
    {
        Id = id,
        Category = "Network",
        Impact = new ImpactInfo { Gaming = 1, Effect = ["latency"] },
        Actions = [.. actions],
        Sources = ["https://example.invalid"],
    };

    private static string AddAdapter(EngineFixture fx, string sub, string guid, string instance, int characteristics = 0x84, params (string Keyword, string[] Values)[] parameters)
    {
        var key = $@"{NicAdapters.ClassPath}\{sub}";
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "NetCfgInstanceId", "string", guid);
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "DeviceInstanceID", "string", instance);
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "Characteristics", "dword", characteristics.ToString());
        RegistryValue.Write(fx.Registry, Hive.Machine, key, "DriverDesc", "string", "Test NIC " + sub);
        foreach (var (kw, values) in parameters)
            foreach (var v in values)
                RegistryValue.Write(fx.Registry, Hive.Machine, $@"{key}\Ndi\params\{kw}\enum", v, "string", "label " + v);
        return key;
    }

    [Fact]
    public void HyperVGuestAdapterCountsForDnsButNotForHardwareProperties()
    {
        using var fx = new EngineFixture();
        const string guid = "{33333333-3333-3333-3333-333333333333}";
        AddAdapter(fx, "0001", guid, @"VMBUS\{f8615163-df3e-46c5-913f-f2d2f965ed0e}\{47bc6987-2d0c-40ff-8e80-e19e01c1f9b9}", 0x4, ("*EEE", ["0", "1"]));
        AddAdapter(fx, "0002", "{44444444-4444-4444-4444-444444444444}", @"ROOT\VMS_MP\0000", 0x4);

        Assert.Empty(NicAdapters.Enumerate(fx.Registry));
        Assert.Equal([guid], NicAdapters.Enumerate(fx.Registry, includeSynthetic: true).Select(a => a.InterfaceGuid));
        Assert.Empty(fx.Engine.Expand(Tweak("test.nic", new NicPropertyAction { Properties = new() { ["*EEE"] = "0" } })));

        // The DNS confirmation names the adapter; an unknown GUID still shows alone.
        Assert.Equal($"IPv4 DNS servers of Test NIC 0001 {guid}", new DnsAction { InterfaceGuid = guid }.Describe(fx.Context));
        Assert.Equal("IPv4 DNS servers of adapter {55555555-5555-5555-5555-555555555555}",
            new DnsAction { InterfaceGuid = "{55555555-5555-5555-5555-555555555555}" }.Describe(fx.Context));
    }

    [Fact]
    public async Task NicPropertiesTouchOnlyDeclaredKeywordsOnPhysicalAdaptersAndUndo()
    {
        using var fx = new EngineFixture();
        var physical = AddAdapter(fx, "0001", "{11111111-1111-1111-1111-111111111111}", @"PCI\VEN_8086&DEV_15BC\3", 0x84, ("*EEE", ["0", "1"]), ("*FlowControl", ["0", "3"]));
        RegistryValue.Write(fx.Registry, Hive.Machine, physical, "*EEE", "string", "1");
        var virtualNic = AddAdapter(fx, "0002", "{22222222-2222-2222-2222-222222222222}", @"ROOT\VMS_MP\0000", 0x1, ("*EEE", ["0", "1"]));

        var t = Tweak("test.nic", new NicPropertyAction
        {
            Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0", ["GreenEthernet"] = "0", ["*FlowControl"] = "7" },
        });
        Assert.Single(fx.Engine.Expand(t)); // the virtual adapter is never touched
        Assert.Equal(TweakState.NotApplied, fx.Engine.DetectState(t, Facts));

        var r = await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Equal("0", RegistryValue.Read(fx.Registry, Hive.Machine, physical, "*EEE").Data);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, physical, "GreenEthernet").Existed); // not declared by the driver
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, physical, "*FlowControl").Existed); // value not allowed
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, virtualNic, "*EEE").Existed);
        Assert.Equal([@"PCI\VEN_8086&DEV_15BC\3"], fx.Devices.Restarts);

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("1", RegistryValue.Read(fx.Registry, Hive.Machine, physical, "*EEE").Data);
        Assert.Equal(2, fx.Devices.Restarts.Count);
    }

    [Fact]
    public async Task NicRestartFailureKeepsTheChange()
    {
        using var fx = new EngineFixture();
        var key = AddAdapter(fx, "0001", "{11111111-1111-1111-1111-111111111111}", @"PCI\VEN_8086&DEV_15BC\3", 0x84, ("*EEE", ["0", "1"]));
        fx.Devices.Fail = true;
        var t = Tweak("test.nic", new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" } });
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, Options)).Outcome);
        Assert.Equal("0", RegistryValue.Read(fx.Registry, Hive.Machine, key, "*EEE").Data);
    }

    [Fact]
    public async Task NicWithoutAnyKeywordIsUnsupported()
    {
        using var fx = new EngineFixture();
        AddAdapter(fx, "0001", "{11111111-1111-1111-1111-111111111111}", @"PCI\VEN_8086&DEV_15BC\3", 0x84, ("*JumboPacket", ["1514"]));
        var t = Tweak("test.nic", new NicPropertyAction { Properties = new(StringComparer.OrdinalIgnoreCase) { ["*EEE"] = "0" } });
        Assert.Equal(TweakState.Unsupported, fx.Engine.DetectState(t, Facts));
        // Not "already in place": the adapter has nothing this tweak changes.
        var r = await fx.Engine.ApplyAsync(t, Facts, Options);
        Assert.Equal(ApplyOutcome.Blocked, r.Outcome);
        Assert.Contains(r.Blocks!, b => b.ReasonKey == "block.unsupported");
    }

    [Fact]
    public async Task NvidiaSettingAppliesAndUndoRemovesOwnValue()
    {
        using var fx = new EngineFixture();
        var t = Tweak("test.nv", new NvidiaDrsAction { Profile = "global", SettingId = Nvapi.SettingPreRenderLimit, Value = 1 });
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, Options)).Outcome);
        Assert.Equal(1u, fx.Nvidia.Values[("global", Nvapi.SettingPreRenderLimit)]);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.False(fx.Nvidia.Values.ContainsKey(("global", Nvapi.SettingPreRenderLimit)));
    }

    [Fact]
    public void NvidiaSettingWithoutDriverIsUnsupported()
    {
        using var fx = new EngineFixture();
        fx.Nvidia.Available = false;
        var t = Tweak("test.nv", new NvidiaDrsAction { SettingId = Nvapi.SettingPreRenderLimit, Value = 1 });
        Assert.Equal(TweakState.Unsupported, fx.Engine.DetectState(t, Facts));
    }

    [Fact]
    public async Task PowerModeAndUndo()
    {
        using var fx = new EngineFixture();
        fx.PowerMode.Current = FirmwareExtras.OverlayBetterBattery;
        var t = RuntimeFixes.PowerModeBestPerformance();
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, Options)).Outcome);
        Assert.Equal(FirmwareExtras.OverlayBestPerformance, fx.PowerMode.Current);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal(FirmwareExtras.OverlayBetterBattery, fx.PowerMode.Current);
    }

    [Fact]
    public async Task DnsPerInterfaceAndBackToDhcp()
    {
        const string nic = "{33333333-3333-3333-3333-333333333333}";
        using var fx = new EngineFixture(nic);
        var path = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic}";
        RegistryValue.Write(fx.Registry, Hive.Machine, path, "NameServer", "string", "");
        var t = Tweak("test.dns", new DnsAction { InterfaceGuid = "{nic}", Servers = "1.1.1.1,1.0.0.1" });
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, Options)).Outcome);
        Assert.Equal("1.1.1.1,1.0.0.1", RegistryValue.Read(fx.Registry, Hive.Machine, path, "NameServer").Data);
        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Equal("", RegistryValue.Read(fx.Registry, Hive.Machine, path, "NameServer").Data);
    }
}
