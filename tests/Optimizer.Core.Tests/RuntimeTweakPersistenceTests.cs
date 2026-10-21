using System.Diagnostics;
using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Catalog;
using Optimizer.Core.Cleanup;
using Optimizer.Core.Debloat;
using Optimizer.Core.Docs;
using Optimizer.Core.Findings;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Hardware;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

public class RuntimeTweakPersistenceTests
{
    /// <summary>Autoruns-style Delete: the value goes, the change is listed with a backup, and undo writes it back with its type.</summary>
    [Fact]
    public async Task DeletedStartupValuesComeBackOnUndo()
    {
        using var fx = new EngineFixture();
        const string runOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Gone", "expandString", @"%LOCALAPPDATA%\Gone\gone.exe");
        RegistryValue.Write(fx.Registry, Hive.Machine, runOnce, "Finish", "string", @"C:\Vendor\finish.exe");
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Command Processor", "AutoRun", "string", "doskey /macrofile=x");
        var scanner = new StartupScanner(fx.Registry, fx.Tasks, null);
        var run = scanner.RunKeys().Single(e => e.Name == "Gone");
        var once = scanner.RunOnceEntries().Single(e => e.Name == "Finish");
        var auto = scanner.CommandAutoRuns().Single();
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true };
        var deletes = new[] { run, once, auto }.Select(e => StartupTweaks.Delete(e)!).ToList();
        foreach (var t in deletes) Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, facts, options)).Outcome);

        scanner = new StartupScanner(fx.Registry, fx.Tasks, null);
        Assert.DoesNotContain(scanner.RunKeys(), e => e.Name == "Gone");
        Assert.Empty(scanner.RunOnceEntries());
        Assert.Empty(scanner.CommandAutoRuns());

        // After a restart of the app the stored definitions still undo it.
        var engine = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300);
        foreach (var t in deletes) Assert.True(engine.Revert(engine.Resolve(t.Id)!).Success);
        Assert.Equal(new StoredValue(true, "expandString", @"%LOCALAPPDATA%\Gone\gone.exe"), RegistryValue.Read(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Gone"));
        Assert.True(RegistryValue.Read(fx.Registry, Hive.Machine, runOnce, "Finish").Existed);
        Assert.True(RegistryValue.Read(fx.Registry, Hive.User, @"Software\Microsoft\Command Processor", "AutoRun").Existed);

        // Windows' own tasks, services and RunOnceEx entries have no Delete (files and other tasks: StartupDeleteTests).
        Assert.Null(StartupTweaks.Delete(run with { Kind = StartupKind.LogonTask, Target = @"\Microsoft\Windows\TextServicesFramework\MsCtfMonitor" }));
        Assert.Null(StartupTweaks.Delete(run with { Kind = StartupKind.Service }));
        Assert.Null(StartupTweaks.Delete(once with { Key = "runonceex:0001:x" }));
    }

    [Fact]
    public async Task RuntimeTweakCanBeUndoneAfterRestart()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Updater", "string", @"C:\Vendor\updater.exe");
        var entry = new StartupScanner(fx.Registry, fx.Tasks, null).RunKeys().Single();
        var tweak = StartupTweaks.Set(entry, enabled: false)!;
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        await fx.Engine.ApplyAsync(tweak, facts, new ApplyOptions { ContinueWithoutRestorePoint = true });

        // A new engine on the same backup store = the app after a restart, without the startup page.
        var engine = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300);
        var restored = engine.Resolve(tweak.Id);
        Assert.NotNull(restored);
        Assert.IsType<StartupApprovedAction>(restored!.Actions.Single());
        Assert.Equal(entry.Name, restored.Subject);
        // Also reverts the restore point frequency tweak the engine applied before the first change.
        var all = engine.RevertAll();
        Assert.All(all, r => Assert.True(r.Result.Success));
        Assert.Contains(all, r => r.Tweak.Id == tweak.Id);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.User, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Updater").Existed);
    }

    [Fact]
    public async Task DriftAfterWindowsUpdateIsReportedWithBothVersions()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Run", "Updater", "string", @"C:\Vendor\updater.exe");
        var entry = new StartupScanner(fx.Registry, fx.Tasks, null).RunKeys().Single();
        var tweak = StartupTweaks.Set(entry, enabled: false)!;
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        var before = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300, "26300.9000");
        await before.ApplyAsync(tweak, facts, new ApplyOptions { ContinueWithoutRestorePoint = true });
        Assert.Empty(before.CheckDrift(facts));

        // The update puts the entry back to enabled.
        RegistryValue.Delete(fx.Registry, Hive.User, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Updater");

        var after = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300, "26300.9550");
        var drift = Assert.Single(after.CheckDrift(facts));
        Assert.Equal(tweak.Id, drift.Tweak.Id);
        Assert.Equal("26300.9000", drift.AppliedOn);
        Assert.Equal("26300.9550", drift.Current);
        Assert.True(drift.WindowsUpdatedSince);

        // Same version: reset by something else, not by an update.
        var same = Assert.Single(before.CheckDrift(facts));
        Assert.False(same.WindowsUpdatedSince);
    }

    [Theory]
    [InlineData("26300.9000", "26300.9550", true)]
    [InlineData("26300.9550", "26300.9550", false)]
    [InlineData("26300", "26300.9550", false)] // older backup knows the build only
    [InlineData("26100", "26300.9550", true)]
    [InlineData(null, "26300.9550", false)]
    public void WindowsUpdatedSinceComparesWhatTheBackupKnows(string? appliedOn, string current, bool expected)
    {
        var item = new DriftItem(RuntimeFixes.PowerModeBestPerformance(), new Backup.TweakBackup { TweakId = "x" }, appliedOn, current);
        Assert.Equal(expected, item.WindowsUpdatedSince);
    }

    [Fact]
    public void RuntimeDefinitionsRoundTripThroughJson()
    {
        var defs = new List<TweakDefinition>
        {
            OptionalFeatureAction.Tweak(new FeatureEntry { Name = "SMB1Protocol", Title = "SMB 1.0" }, false),
            RuntimeFixes.PowerModeBestPerformance(),
            RuntimeFixes.NvidiaGlobalReset([Interop.Nvapi.SettingFrameRateLimiter]),
            DeviceTweaks.MsiMode(new MsiDevice(@"PCI\VEN_10DE&DEV_1F02\4&1", "RTX 2070", "gpu", 3, 1, null)),
        };
        foreach (var d in defs)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(d, TweakCatalog.JsonOptions);
            var back = System.Text.Json.JsonSerializer.Deserialize<TweakDefinition>(json, TweakCatalog.JsonOptions)!;
            Assert.Equal(d.Id, back.Id);
            Assert.Equal(d.Actions.Select(a => a.GetType()), back.Actions.Select(a => a.GetType()));
            Assert.Equal(d.Actions.Select(a => a.TargetKey), back.Actions.Select(a => a.TargetKey));
            Assert.Equal(d.IsBootCritical, back.IsBootCritical);
        }
    }
}
