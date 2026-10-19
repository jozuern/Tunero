using Optimizer.Core.Actions;
using Optimizer.Core.Cleanup;
using Optimizer.Core.Debloat;
using Optimizer.Core.Platform;
using Optimizer.Core.Startup;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>Read-only checks of startup entries, AppX packages and cleanup sizes on this machine (Category=Hardware). Scans only: nothing is changed or deleted.</summary>
public class StartupCleanupReadTests(ITestOutputHelper output)
{
    private static readonly ElevationInfo Elevation = ElevationInfo.Read();
    private static readonly ActionContext Ctx = ReadOnlyContext.Create(Elevation.SessionUserSid);
    private static string? Profile => Elevation.SessionUserSid is { } sid ? Reg.HklmString($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", "ProfileImagePath") : null;

    [Fact]
    [Trait("Category", "Hardware")]
    public void StartupScanFindsEntriesWithImagePaths()
    {
        var entries = new StartupScanner(Ctx.Registry, Ctx.Tasks, Profile).ScanAll();
        foreach (var g in entries.GroupBy(e => e.Kind)) output.WriteLine($"{g.Key}: {g.Count()}");
        foreach (var e in entries.Where(e => e.Kind is StartupKind.RunKey or StartupKind.StartupFolder or StartupKind.Winlogon))
            output.WriteLine($"  {e.Kind} {e.Name} enabled={e.Enabled} suspicious={e.Suspicious} -> {e.ImagePath}");
        Assert.Contains(entries, e => e.Kind == StartupKind.Service);
        Assert.Contains(entries, e => e.Kind == StartupKind.Winlogon && e.Name == "Userinit" && !e.Suspicious);
        var services = entries.Where(e => e.Kind == StartupKind.Service).ToList();
        var resolved = services.Count(e => e.ImagePath is not null && File.Exists(e.ImagePath));
        output.WriteLine($"service image paths resolved: {resolved}/{services.Count}");
        Assert.True(resolved >= services.Count * 0.9, $"only {resolved}/{services.Count} service files found");
    }

    /// <summary>
    /// The install trusts a publisher only under a root of the machine's stores. A signed program of this PC (the .NET
    /// host) chains to one; a made-up thumbprint is in none. Set TUNERO_RELEASE_EXE to a downloaded release to check it too.
    /// </summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void SignedProgramsChainToARootOfTheMachineStores()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var signature = SignatureVerifier.Verify(dotnet);
        output.WriteLine($"dotnet {signature}");
        Assert.Equal(SignatureStatus.Signed, signature.Status);
        Assert.True(Platform.AppInstall.IsMachineRoot(Assert.IsType<string>(signature.RootThumbprint)));
        Assert.False(Platform.AppInstall.IsMachineRoot(new string('0', 40)));
        if (Environment.GetEnvironmentVariable("TUNERO_RELEASE_EXE") is { Length: > 0 } release)
        {
            output.WriteLine($"release {SignatureVerifier.Verify(release)}");
            Assert.True(Platform.AppInstall.IsTrustedFile(release));
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void SignaturesOfKnownFiles()
    {
        var sys = Environment.SystemDirectory;
        var notepad = SignatureVerifier.Verify(Path.Combine(sys, "notepad.exe"));
        var kernel = SignatureVerifier.Verify(Path.Combine(sys, "ntoskrnl.exe"));
        var driver = SignatureVerifier.Verify(Path.Combine(sys, @"drivers\tcpip.sys"));
        var unsigned = Path.Combine(Path.GetTempPath(), $"pco-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0]);
        try
        {
            var none = SignatureVerifier.Verify(unsigned);
            var shell = SignatureVerifier.Verify(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            var thirdParty = Directory.GetFiles(Path.Combine(sys, "drivers"), "nvhda64v.sys").Concat(Directory.GetFiles(Path.Combine(sys, "drivers"), "iaStor*.sys"))
                .Select(SignatureVerifier.Verify).FirstOrDefault(s => s.Status == SignatureStatus.Signed);
            output.WriteLine($"notepad {notepad}\nntoskrnl {kernel}\ntcpip {driver}\nexplorer {shell}\nthird-party driver {thirdParty}\nunsigned {none}");
            Assert.Equal(SignatureStatus.Signed, kernel.Status);
            Assert.True(kernel.IsMicrosoft);
            // Microsoft's own files chain to a Microsoft root (when the chain can be read at all).
            Assert.NotEqual(false, kernel.MicrosoftRoot);
            Assert.NotEqual(false, notepad.MicrosoftRoot);
            Assert.NotEqual(false, shell.MicrosoftRoot);
            // Edge is signed by "Microsoft Corporation" under the Microsoft application root (2011).
            var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(edge))
            {
                var e = SignatureVerifier.Verify(edge);
                output.WriteLine($"msedge {e}");
                Assert.True(e.IsMicrosoft);
            }
            if (thirdParty is not null) Assert.False(thirdParty.IsMicrosoft);
            Assert.Equal(SignatureStatus.Signed, driver.Status);
            Assert.NotEqual(SignatureStatus.Signed, none.Status);
        }
        finally
        {
            File.Delete(unsigned);
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void AppxListAndOffer()
    {
        var service = new DebloatService(Ctx.Processes, Path.GetTempPath());
        var installed = service.ListInstalled(allUsers: Elevation.IsElevated);
        output.WriteLine($"{installed.Count} packages");
        Assert.Contains(installed, a => a.Name == "Microsoft.WindowsStore");
        var offered = DebloatService.Offer(Catalog.CatalogData.Current.Appx, installed, new Hardware.HardwareProfile { Os = BuildInfo.Read() }, Catalog.CatalogData.Current);
        foreach (var o in offered) output.WriteLine($"  {o.Entry.Name} {o.Installed.Version} block={o.BlockKey}");
        Assert.DoesNotContain(offered, o => o.Entry.Name == "Microsoft.WindowsStore");
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void CleanupScanOnly()
    {
        foreach (var c in CleanupEngine.Categories(Profile, Elevation.SessionUserSid))
        {
            var s = CleanupEngine.Scan(c);
            output.WriteLine($"{c.Id}: {s.Files} files, {s.Bytes / 1048576.0:0.0} MB in {s.ExistingRoots.Count} folder(s)");
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task HealthToolsReadOnThisPc()
    {
        foreach (var d in Tools.DiskHealthReader.Read()) output.WriteLine($"disk {d}");
        using (var monitor = new Tools.ThrottleMonitor())
        {
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(1000);
                var s = monitor.Sample();
                output.WriteLine($"cpu limit={s.CpuPerformanceLimit:0.#} perf={s.CpuPerformance:0.#} util={s.CpuUtility:0.#} gpus={s.Gpus.Count}");
                Assert.NotNull(s.CpuPerformanceLimit);
            }
        }
        var top = await Tools.ProcessSampler.SampleAsync(TimeSpan.FromSeconds(2));
        foreach (var p in top.Take(5)) output.WriteLine($"proc {p.Name} {p.CpuShare * 100:0.0} %");
        Assert.NotEmpty(top);
        output.WriteLine($"PawnIO installed: {Tools.Sensors.PawnIoInstalled}");
        using var sensors = new Tools.Sensors();
        var readings = sensors.Read();
        foreach (var r in readings.Where(r => r.Type == "Temperature").Take(10)) output.WriteLine($"sensor {r.Hardware} / {r.Sensor} = {r.Value}");
        output.WriteLine($"{readings.Count} sensor readings");
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void OneDriveStateIsReadable()
    {
        var state = OneDrive.Read(Ctx.Registry, Profile, maxFiles: 20_000);
        output.WriteLine($"setup={state.SetupPath} folder={state.UserFolder} kfm=[{string.Join(",", state.RedirectedFolders)}] cloudOnly={state.CloudOnlyFiles} truncated={state.ScanTruncated} block={state.BlockKey}");
    }
}
