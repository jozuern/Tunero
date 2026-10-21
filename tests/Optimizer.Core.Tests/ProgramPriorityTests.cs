using Optimizer.Core.Actions;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Start priority rules in Image File Execution Options, applied and undone in the sandbox.</summary>
public class ProgramPriorityTests
{
    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true);
    private static readonly ApplyOptions Options = new() { ExpertMode = true, ContinueWithoutRestorePoint = true, AcknowledgeAntiCheat = true };

    [Theory]
    [InlineData("game.exe", true)]
    [InlineData("Cyberpunk2077.exe", true)]
    [InlineData("My Game.exe", true)]
    [InlineData(@"..\evil.exe", false)]
    [InlineData(@"C:\Games\game.exe", false)]
    [InlineData("game.bat", false)]
    [InlineData("", false)]
    public void OnlyPlainProgramNames(string name, bool valid) => Assert.Equal(valid, ProgramPriority.IsValidExe(name));

    [Fact]
    public async Task RuleIsWrittenReadAndRemovedWithItsKeys()
    {
        using var fx = new EngineFixture();
        var t = ProgramPriority.Tweak("game.exe", CpuPriority.AboveNormal, lowIo: false);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(t, Facts, Options)).Outcome);
        var rule = Assert.Single(ProgramPriority.Read(fx.Registry));
        Assert.Equal(new PriorityRule("game.exe", CpuPriority.AboveNormal, false), rule);
        // Not flagged as a program redirect: no Debugger value.
        Assert.Empty(new StartupScanner(fx.Registry, fx.Tasks, null).ImageHijacks());

        Assert.True(fx.Engine.Revert(t).Success);
        Assert.Empty(ProgramPriority.Read(fx.Registry));
        using (var key = fx.Registry.Open(Hive.Machine, $@"{StartupScanner.Ifeo}\game.exe", writable: false)) Assert.Null(key);

        var low = ProgramPriority.Tweak("backup.exe", CpuPriority.Low, lowIo: true);
        await fx.Engine.ApplyAsync(low, Facts, Options);
        Assert.Equal(new PriorityRule("backup.exe", CpuPriority.Low, true), Assert.Single(ProgramPriority.Read(fx.Registry)));
        // Undocumented, but proven in the VM: no longer a Preview, and the proof travels with the rule.
        Assert.True(low.Undocumented && !low.Preview && low.AntiCheatSensitive);
        Assert.Equal([ProgramPriority.Proof, ProgramPriority.MemoryProof], low.Proof);
        Assert.DoesNotContain(Enum.GetValues<CpuPriority>(), p => (int)p == 4); // never realtime

        // The same program again without low disk priority: the earlier value goes, undo brings it back.
        var again = ProgramPriority.Tweak("backup.exe", CpuPriority.BelowNormal, lowIo: false);
        await fx.Engine.ApplyAsync(again, Facts, Options);
        Assert.Equal(new PriorityRule("backup.exe", CpuPriority.BelowNormal, false), Assert.Single(ProgramPriority.Read(fx.Registry)));

        // Low memory priority: PagePriority 2 (MEMORY_PRIORITY_LOW), read back, and gone with the rule.
        var memory = ProgramPriority.Tweak("launcher.exe", CpuPriority.BelowNormal, lowIo: false, lowMemory: true);
        await fx.Engine.ApplyAsync(memory, Facts, Options);
        Assert.Equal(new StoredValue(true, "dword", "2"), RegistryValue.Read(fx.Registry, Hive.Machine, $@"{StartupScanner.Ifeo}\launcher.exe\PerfOptions", "PagePriority"));
        Assert.Contains(ProgramPriority.Read(fx.Registry), r => r == new PriorityRule("launcher.exe", CpuPriority.BelowNormal, false) { LowMemory = true });
        Assert.True(fx.Engine.Revert(memory).Success);
        Assert.False(RegistryValue.Read(fx.Registry, Hive.Machine, $@"{StartupScanner.Ifeo}\launcher.exe\PerfOptions", "PagePriority").Existed);
    }

    /// <summary>A rule another program set (Realtime) is listed and can be removed with a backup, so it can come back.</summary>
    [Fact]
    public async Task ARuleFromAnotherProgramIsListedAndRemovable()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{StartupScanner.Ifeo}\game.exe\PerfOptions", "CpuPriorityClass", "dword", "4");
        RegistryValue.Write(fx.Registry, Hive.Machine, $@"{StartupScanner.Ifeo}\bad name+.exe\PerfOptions", "CpuPriorityClass", "dword", "3");
        var rule = Assert.Single(ProgramPriority.Read(fx.Registry)); // names the app cannot handle are left out
        Assert.Equal(new PriorityRule("game.exe", null, false, 4), rule);

        var remove = ProgramPriority.RemoveTweak("game.exe");
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(remove, Facts, Options)).Outcome);
        Assert.Empty(ProgramPriority.Read(fx.Registry));
        Assert.True(fx.Engine.Revert(remove).Success);
        Assert.Equal(4, Assert.Single(ProgramPriority.Read(fx.Registry)).OtherCpu);
    }
}
