using Optimizer.Core.Platform;
using Optimizer.Core.Tools;

namespace Optimizer.Core.Tests;

/// <summary>Quick fixes run only Windows tools from System32, in order, and stop at the first failing step unless a step must always run.</summary>
public class QuickFixTests
{
    [Fact]
    public void EveryStepIsAWindowsTool()
    {
        foreach (var fix in QuickFixes.All)
        foreach (var step in fix.Steps.Where(s => s.File != QuickFixes.WingetTool))
            Assert.True(File.Exists(ProcessHardening.ResolveSystemTool(step.File)), $"{fix.Id}: {step.File} is not in System32");
    }

    /// <summary>winget runs only from the protected App Installer folder; without it the fix fails instead of running "winget" from anywhere.</summary>
    [Fact]
    public void WingetStepsUseTheTrustedWinget()
    {
        var runner = new FakeProcesses();
        const string trusted = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0_x64__8wekyb3d8bbwe\winget.exe";
        Assert.True(QuickFixes.Run(runner, QuickFixes.Get("WingetSourceReset"), () => trusted).Ok);
        Assert.Equal([$"{trusted} source reset --force --disable-interactivity"], runner.Calls);

        var none = new FakeProcesses();
        var (ok, output) = QuickFixes.Run(none, QuickFixes.Get("WingetSourceReset"), () => null);
        Assert.False(ok);
        Assert.Contains("not found", output);
        Assert.Empty(none.Calls);
    }

    [Fact]
    public void AdvancedStartupChangesNoBootSetting()
    {
        // Restart into the menu once: shutdown /r /o, never bcdedit (a Safe Mode flag would need its own way back).
        var step = Assert.Single(QuickFixes.Get("AdvancedStartup").Steps);
        Assert.Equal(("shutdown.exe", "/r /o /t 0"), (step.File, step.Arguments));
        Assert.DoesNotContain(QuickFixes.All.SelectMany(f => f.Steps), s => s.File.Contains("bcdedit", StringComparison.OrdinalIgnoreCase));
        // It restarts the PC at once: its own tag, not "Restart needed" as for Winsock, whose restart can wait.
        Assert.True(QuickFixes.Get("AdvancedStartup") is { RestartsNow: true, NeedsRestart: false });
        Assert.Equal(["AdvancedStartup"], QuickFixes.All.Where(f => f.RestartsNow).Select(f => f.Id));
    }

    /// <summary>
    /// The icon caches Windows 11 uses (Explorer's iconcache_*.db) go with the old IconCache.db; the thumbnail caches stay,
    /// and a file another program holds open is left and counted without stopping the others.
    /// </summary>
    [Fact]
    public void IconCacheDeletesEveryIconCacheAndSkipsOpenOnes()
    {
        var local = Path.Combine(Path.GetTempPath(), "pco-iconcache-" + Guid.NewGuid().ToString("N"));
        var explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        Directory.CreateDirectory(explorer);
        try
        {
            foreach (var f in new[] { Path.Combine(local, "IconCache.db"), Path.Combine(explorer, "iconcache_16.db"), Path.Combine(explorer, "iconcache_idx.db"),
                         Path.Combine(explorer, "iconcache_32.db"), Path.Combine(explorer, "thumbcache_32.db") })
                File.WriteAllText(f, "x");
            Assert.Equal(4, IconCache.Files(local).Count);
            (int Deleted, int Skipped) result;
            // Open without delete sharing, as a program that still reads the cache.
            using (File.Open(Path.Combine(explorer, "iconcache_32.db"), FileMode.Open, FileAccess.Read, FileShare.Read))
                result = IconCache.Delete(local);
            Assert.Equal((3, 1), result);
            Assert.Equal(["iconcache_32.db", "thumbcache_32.db"], Directory.GetFiles(explorer).Select(Path.GetFileName).Order());
            Assert.False(File.Exists(Path.Combine(local, "IconCache.db")));
            // Nothing there (a new profile): nothing to do.
            Assert.Equal((0, 0), IconCache.Delete(Path.Combine(local, "missing")));
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }
    }

    [Fact]
    public void IdsAreUniqueAndSourcesAreLinks()
    {
        Assert.Equal(QuickFixes.All.Count, QuickFixes.All.Select(f => f.Id).Distinct().Count());
        Assert.All(QuickFixes.All, f => Assert.StartsWith("https://learn.microsoft.com/", f.Source));
    }

    [Fact]
    public void StepsRunInOrderAndStopAtTheFirstFailure()
    {
        var runner = new FakeProcesses();
        var (ok, _) = QuickFixes.Run(runner, QuickFixes.Get("IpRenew"));
        Assert.True(ok);
        Assert.Equal(["ipconfig.exe /release", "ipconfig.exe /renew"], runner.Calls);

        // A failed /release must not leave the PC without an address: /renew still runs.
        var failing = new FakeProcesses { Handler = (_, args) => args == "/release" ? (1, "one adapter failed") : null };
        var (ok2, output) = QuickFixes.Run(failing, QuickFixes.Get("IpRenew"));
        Assert.False(ok2);
        Assert.Equal("one adapter failed", output);
        Assert.Equal(["ipconfig.exe /release", "ipconfig.exe /renew"], failing.Calls);

        // Other fixes stop at the first failure.
        var stop = new FakeProcesses { Handler = (file, _) => file == "powershell.exe" ? (1, "W32Time missing") : null };
        Assert.False(QuickFixes.Run(stop, QuickFixes.Get("TimeSync")).Ok);
        Assert.Single(stop.Calls);
    }

    [Fact]
    public void DiskScanChecksTheSystemDriveOnline()
    {
        Assert.Equal(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\') + " /scan", QuickFixes.Get("DiskScan").Steps.Single().Arguments);
        // chkdsk returns 1 when it fixed errors; 3 means problems remain.
        Assert.True(QuickFixes.Run(new FakeProcesses { Handler = (_, _) => (1, "fixed") }, QuickFixes.Get("DiskScan")).Ok);
        Assert.False(QuickFixes.Run(new FakeProcesses { Handler = (_, _) => (3, "queued") }, QuickFixes.Get("DiskScan")).Ok);
    }
}

/// <summary>DoH auto-upgrade: read from WMI, set through the documented cmdlet, each server's previous state restored on undo.</summary>
public class DohTests
{
    private sealed class DohState
    {
        public Dictionary<string, bool> Servers { get; } = new() { ["1.1.1.1"] = false, ["8.8.8.8"] = true, ["9.9.9.9"] = false };

        /// <summary>Windows' list (read through <see cref="FakeNetwork.Doh"/>) and the cmdlet that changes it.</summary>
        public DohState(EngineFixture fx)
        {
            fx.Network.Doh = Servers;
            fx.Processes.Handler = Handle;
        }

        public (int, string)? Handle(string file, string args)
        {
            if (!args.Contains("Set-DnsClientDohServerAddress")) return null;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(args, @"-ServerAddress '([^']+)' -AutoUpgrade \$(true|false)"))
                Servers[m.Groups[1].Value] = m.Groups[2].Value == "true";
            return (0, "");
        }
    }

    [Fact]
    public async Task AutoUpgradeIsTurnedOnAndEachServerRestored()
    {
        using var fx = new EngineFixture();
        var state = new DohState(fx);
        var t = Tweaks.TweakCatalog.Current.Tweaks.Single(x => x.Id == "network.dohAutoUpgrade");
        var action = Assert.IsType<Actions.DohAutoUpgradeAction>(Assert.Single(t.Actions));
        var only = new Tweaks.TweakDefinition
        {
            Id = "test.doh", Category = "Test", Hidden = true, Impact = new Tweaks.ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
            Actions = [new Actions.DohAutoUpgradeAction { Servers = ["1.1.1.1", "8.8.8.8", "9.9.9.9"] }],
        };
        var facts = new Tweaks.Facts().Set("os.build", 26300).Set("elevated", true);
        var options = new Tweaks.ApplyOptions { ExpertMode = true, ContinueWithoutRestorePoint = true };
        Assert.Equal(Tweaks.ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(only, facts, options)).Outcome);
        Assert.All(state.Servers.Values, Assert.True);
        Assert.True(fx.Engine.Revert(only).Success);
        Assert.False(state.Servers["1.1.1.1"]);
        Assert.True(state.Servers["8.8.8.8"]); // was on before: stays on
        Assert.False(state.Servers["9.9.9.9"]);

        // A server Windows does not know: the tweak is unsupported instead of failing later.
        state.Servers.Remove("9.9.9.9");
        Assert.Null(only.Actions[0].Read(fx.Context));
        Assert.Contains("149.112.112.112", action.Servers);
        Assert.Equal("1.1.1.1=1;8.8.8.8=0", Actions.DohAutoUpgradeAction.Format(new Dictionary<string, bool> { ["8.8.8.8"] = false, ["1.1.1.1"] = true }));
    }

    /// <summary>Each server is set in its own run, so a failure is not hidden by a later success; all are tried.</summary>
    [Fact]
    public void AFailingServerIsReportedAndTheOthersStillSet()
    {
        using var fx = new EngineFixture();
        var state = new DohState(fx);
        fx.Processes.Handler = (f, a) => a.Contains("'8.8.8.8'") ? (1, "access denied") : state.Handle(f, a);
        var action = new Actions.DohAutoUpgradeAction { Servers = ["1.1.1.1", "8.8.8.8", "9.9.9.9"] };
        var ex = Assert.Throws<InvalidOperationException>(() => action.Apply(fx.Context));
        Assert.Contains("8.8.8.8", ex.Message);
        Assert.True(state.Servers["1.1.1.1"]);
        Assert.True(state.Servers["9.9.9.9"]);
        Assert.All(fx.Processes.Calls.Where(c => c.Contains("Set-DnsClientDohServerAddress")), c => Assert.Contains("$ErrorActionPreference='Stop'", c));
    }

    /// <summary>Every DoH server in the catalog is a valid IP address (a typo would be dropped silently).</summary>
    [Fact]
    public void CatalogServersAreAddresses()
    {
        var action = Assert.IsType<Actions.DohAutoUpgradeAction>(Tweaks.TweakCatalog.Current.Tweaks.Single(x => x.Id == "network.dohAutoUpgrade").Actions.Single());
        Assert.All(action.Servers, s => Assert.True(System.Net.IPAddress.TryParse(s, out _), s));
    }

    /// <summary>With the cache on (the app's runner), a scan reads once; a write drops the cached value.</summary>
    [Fact]
    public void SlowReadsAreSharedUntilAWrite()
    {
        using var fx = new EngineFixture();
        Actions.ReadCache.Enable(fx.Processes, TimeSpan.FromMinutes(5));
        _ = new DohState(fx);
        var action = new Actions.DohAutoUpgradeAction { Servers = ["1.1.1.1"] };
        action.Read(fx.Context);
        action.Read(fx.Context);
        Assert.Equal(1, fx.Network.DohReads);
        action.Apply(fx.Context);
        Assert.Equal("1.1.1.1=1", action.Read(fx.Context)!.Data);
        Assert.Equal(2, fx.Network.DohReads);
    }
}

/// <summary>Windows capabilities through DISM: English state names, add and remove, undo.</summary>
public class CapabilityTests
{
    [Fact]
    public async Task CapabilityIsRemovedAndAddedBack()
    {
        using var fx = new EngineFixture();
        var installed = new HashSet<string> { "OpenSSH.Client~~~~0.0.1.0" };
        fx.Processes.Handler = (file, args) =>
        {
            if (file != "dism.exe") return null;
            var name = System.Text.RegularExpressions.Regex.Match(args, "/CapabilityName:(\\S+)").Groups[1].Value;
            if (args.Contains("/Get-Capabilities"))
                return (0, "Capability Identity | State\n------------------- | -----\nOpenSSH.Client~~~~0.0.1.0 | " + (installed.Contains("OpenSSH.Client~~~~0.0.1.0") ? "Installed" : "Not Present") + "\nWMIC~~~~ | Not Present\n");
            if (args.Contains("/Get-CapabilityInfo")) return (0, $"Capability Identity : {name}\nState : {(installed.Contains(name) ? "Installed" : "Not Present")}\n");
            if (args.Contains("/Remove-Capability")) installed.Remove(name);
            if (args.Contains("/Add-Capability")) installed.Add(name);
            return (0, "The operation completed successfully.");
        };
        var all = OptionalCapabilityAction.ReadAll(fx.Processes);
        Assert.Equal("Enabled", all["OpenSSH.Client~~~~0.0.1.0"]);
        Assert.Equal("Disabled", all["WMIC~~~~"]);

        var entry = Catalog.CatalogData.Current.Features.Features.Single(f => f.Name == "OpenSSH.Client~~~~0.0.1.0");
        Assert.True(entry.Capability);
        var off = OptionalCapabilityAction.Tweak(entry, installed: false);
        var facts = new Tweaks.Facts().Set("os.build", 26300).Set("elevated", true);
        var options = new Tweaks.ApplyOptions { ExpertMode = true, ContinueWithoutRestorePoint = true };
        Assert.Equal(Tweaks.ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(off, facts, options)).Outcome);
        Assert.DoesNotContain("OpenSSH.Client~~~~0.0.1.0", installed);
        Assert.True(fx.Engine.Revert(off).Success);
        Assert.Contains("OpenSSH.Client~~~~0.0.1.0", installed);

        // Reserved storage: off and back on; DISM refusing while an update uses the space is a failed apply.
        var reserved = true;
        var inUse = false;
        fx.Processes.Handler = (file, args) =>
        {
            if (args.Contains("/Get-ReservedStorageState")) return (0, $"Reserved storage is {(reserved ? "enabled" : "disabled")}.");
            if (!args.Contains("/Set-ReservedStorageState")) return null;
            if (inUse) return (1, "This operation is not supported when reserved storage is in use.");
            reserved = args.Contains("/State:Enabled");
            return (0, "The operation completed successfully.");
        };
        var rs = Tweaks.TweakCatalog.Current.Tweaks.Single(x => x.Id == "storage.reservedStorageOff");
        Assert.Equal(Tweaks.ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(rs, facts, options)).Outcome);
        Assert.False(reserved);
        Assert.True(fx.Engine.Revert(rs).Success);
        Assert.True(reserved);
        inUse = true;
        Assert.Equal(Tweaks.ApplyOutcome.Failed, (await fx.Engine.ApplyAsync(rs, facts, options)).Outcome);
        Assert.True(reserved);

        Assert.Null(OptionalCapabilityAction.MapState("Unknown"));
        Assert.Equal("Enabled", OptionalCapabilityAction.MapState("Install Pending"));
        Assert.False(OptionalCapabilityAction.IsSafeName("x; rm"));
        Assert.All(Catalog.CatalogData.Current.Features.Features.Where(f => f.Capability), f => Assert.True(OptionalCapabilityAction.IsSafeName(f.Name), f.Name));
    }
}
