using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>The opt-in re-apply task: its own scheduled task (only in \Tunero\) and the run that applies reset changes again.</summary>
public class ReapplyTaskTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Exe = @"C:\Program Files\Tunero\Tunero.exe";

    private static Facts Facts() => new Facts().Set("os.build", 26300).Set("elevated", true).Set("system.laptop", false).Set("os.insider", false);

    private static OwnTaskAction Action(string exe = Exe) => new() { Path = ReapplyTask.TaskPath(Sid), Xml = ReapplyTask.Xml(exe, Sid) };

    [Fact]
    public void TheTaskIsRegisteredAndUndoRemovesIt()
    {
        using var fx = new EngineFixture();
        var action = Action();
        var original = action.Read(fx.Context)!;
        Assert.False(original.Existed);
        action.Apply(fx.Context);
        Assert.Contains("<Arguments>--reapply</Arguments>", fx.Tasks.Xml[ReapplyTask.TaskPath(Sid)]);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", fx.Tasks.Xml[ReapplyTask.TaskPath(Sid)]);
        Assert.Equal(ActionState.Applied, action.State(fx.Context));
        action.Restore(fx.Context, original);
        Assert.False(fx.Tasks.Xml.ContainsKey(ReapplyTask.TaskPath(Sid)));
    }

    /// <summary>Each account has its own task, and its backup is that account's.</summary>
    [Fact]
    public void TheTaskBelongsToOneAccount()
    {
        Assert.NotEqual(ReapplyTask.TaskPath(Sid), ReapplyTask.TaskPath("S-1-5-21-1-2-3-1002"));
        Assert.True(Backup.BackupStore.IsPerUser(Action().TargetKey));
    }

    /// <summary>
    /// A task left by a copy at another path (uninstalled, moved) does not count as on, so turning the setting on
    /// registers the task anew; undo deletes it and never brings back the old one with its old exe path.
    /// </summary>
    [Fact]
    public void ATaskForAnotherExeIsReplacedAndNotRestored()
    {
        using var fx = new EngineFixture();
        Action(@"C:\Program Files\Old\Tunero.exe").Apply(fx.Context);
        var action = Action();
        Assert.Equal(ActionState.NotApplied, action.State(fx.Context));
        var original = action.Read(fx.Context)!;
        action.Apply(fx.Context);
        Assert.Equal(ActionState.Applied, action.State(fx.Context));
        action.Restore(fx.Context, original);
        Assert.Empty(fx.Tasks.Xml);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData(@"\Tunero\")]
    [InlineData(@"\Tunero\Sub\Task")]
    [InlineData(@"\TuneroX\Task")]
    public void NoTaskOutsideTheAppsFolder(string path)
    {
        using var fx = new EngineFixture();
        var action = new OwnTaskAction { Path = path, Xml = "<Task />" };
        Assert.Throws<InvalidOperationException>(() => action.Apply(fx.Context));
        Assert.Empty(fx.Tasks.Xml);
    }

    /// <summary>
    /// "Apply again" registers the task from its backup: when the exe is gone or its folder can be changed by users since (a
    /// package manager removed the app), nothing is registered that would start a planted file elevated.
    /// </summary>
    [Fact]
    public void ATaskIsNotRegisteredForAProgramThatIsNoLongerTrusted()
    {
        using var fx = new EngineFixture();
        fx.UntrustedPrograms.Add(Exe);
        var action = Action();
        Assert.Throws<InvalidOperationException>(() => action.Apply(fx.Context));
        Assert.Empty(fx.Tasks.Xml);
    }

    [Fact]
    public void OnlyAnExeOnlyAdministratorsCanChangeGetsTheTask()
    {
        Assert.True(ReapplyTask.CanRegister(Exe, _ => true));
        Assert.False(ReapplyTask.CanRegister(@"C:\Users\x\Downloads\Tunero.exe", _ => false));
        Assert.False(ReapplyTask.CanRegister(null, _ => true));
    }

    /// <summary>The Startup page lists the task but neither switches nor deletes it: Settings does.</summary>
    [Fact]
    public void TheStartupPageLeavesTheTaskAlone()
    {
        var entry = new Startup.StartupEntry(Startup.StartupKind.LogonTask, "Apply changes again", Exe + " --reapply", Exe, "Task Scheduler", Hive.Machine, true,
            "task:" + ReapplyTask.TaskPath(Sid)) { Target = ReapplyTask.TaskPath(Sid) };
        Assert.Null(Startup.StartupTweaks.Set(entry, enabled: false));
        Assert.Null(Startup.StartupTweaks.Delete(entry));
    }

    /// <summary>Pretends that Windows was updated since the change was applied (the backup names an older version).</summary>
    private static void UpdatedSince(EngineFixture fx, string tweakId)
    {
        var backup = fx.Store.Get(tweakId)!;
        backup.AppliedOnVersion = "26100.1";
        fx.Store.Save(backup);
    }

    [Fact]
    public async Task TheRunAppliesWhatAnUpdateResetAndNothingElse()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints);
        var t = TweakCatalog.Current.Get("privacy.advertisingIdOff")!;
        await fx.Engine.ApplyAsync(t, Facts(), new HashSet<string>(), new ApplyOptions { ContinueWithoutRestorePoint = true });
        var nothing = await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>([t.Id]), []));
        Assert.Same(BatchOutcome.Cancelled, nothing);

        // Turned back without an update (the user, in Windows): stays as it is.
        RegistryValue.Delete(fx.Registry, Hive.Machine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy");
        Assert.Same(BatchOutcome.Cancelled, await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), [])));

        // Reset by an update: applied again. Without System Protection the run goes on (nobody is there to ask).
        UpdatedSince(fx, t.Id);
        var outcome = await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), []));
        Assert.Equal(1, outcome.Applied);
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(t, Facts()));
    }

    private static TweakDefinition Writes(string id, int value) => new()
    {
        Id = id,
        Docs = id,
        Category = "Test",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Risk = Risk.Safe,
        Hidden = true,
        Actions = [new RegistryAction { Hive = Hive.Machine, Path = @"SOFTWARE\TuneroTest", Name = "Shared", Kind = "dword", Value = JsonSerializer.SerializeToElement(value) }],
    };

    /// <summary>Two changes that write one target differently: one always reads as reset, and the run must not flip it at every sign-in.</summary>
    [Fact]
    public async Task TwoChangesOnOneTargetAreLeftForTheUser()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true };
        await fx.Engine.ApplyAsync(Writes("test.one", 1), Facts(), new HashSet<string>(), options);
        await fx.Engine.ApplyAsync(Writes("test.zero", 0), Facts(), new HashSet<string>(), options);
        UpdatedSince(fx, "test.one");
        UpdatedSince(fx, "test.zero");
        Assert.Contains(fx.Engine.CheckDrift(Facts()), d => d.Tweak.Id == "test.one");
        Assert.Same(BatchOutcome.Cancelled, await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), [])));
    }
}
