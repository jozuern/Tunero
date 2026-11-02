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

    /// <summary>The task is not one of the "many programs at sign-in" (F16) nor of the Startup page's count.</summary>
    [Fact]
    public void TheTaskDoesNotCountAsAStartupProgram()
    {
        Assert.Equal(@"\Tunero\Apply changes again " + Sid, ReapplyTask.TaskPath(Sid));
        static Startup.StartupEntry Task(string name) =>
            new(Startup.StartupKind.LogonTask, name, Exe + " --reapply", Exe, "Task Scheduler", Actions.Hive.Machine, true, name);
        Assert.False(Startup.StartupTweaks.CountsForF16(Task(@"Tunero\Apply changes again " + Sid)));
        Assert.False(Startup.StartupTweaks.CountsForF16(Task(ReapplyTask.TaskPath(Sid))));
        Assert.True(Startup.StartupTweaks.CountsForF16(Task(@"Contoso\Updater")));
    }

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
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        var t = TweakCatalog.Current.Get("privacy.advertisingIdOff")!;
        await fx.Engine.ApplyAsync(t, Facts(), new ApplyOptions { ContinueWithoutRestorePoint = true });
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

    /// <summary>
    /// A change that outlived an update and was turned back by the user afterwards is the user's choice: the run records
    /// that it was still in place, so the later reset is neither applied again nor blamed on the update.
    /// </summary>
    [Fact]
    public async Task AChangeTheUserTurnedBackAfterAnUpdateStaysAsItIs()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        var t = TweakCatalog.Current.Get("privacy.advertisingIdOff")!;
        await fx.Engine.ApplyAsync(t, Facts(), new ApplyOptions { ContinueWithoutRestorePoint = true });
        UpdatedSince(fx, t.Id);

        // The first run after the update finds the change in place and records the new version.
        Assert.Same(BatchOutcome.Cancelled, await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), [])));
        Assert.Equal(fx.Engine.WindowsVersion, fx.Store.Get(t.Id)!.KeptOnVersion);

        // Then the user turns it back in Windows: not applied again, and not an update's doing.
        RegistryValue.Delete(fx.Registry, Hive.Machine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy");
        Assert.Same(BatchOutcome.Cancelled, await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), [])));
        Assert.False(Assert.Single(fx.Engine.CheckDrift(Facts())).WindowsUpdatedSince);

        // Applying it again starts over from that apply.
        await fx.Engine.ApplyAsync(t, Facts(), new ApplyOptions { ContinueWithoutRestorePoint = true });
        Assert.Null(fx.Store.Get(t.Id)!.KeptOnVersion);
    }

    /// <summary>The window's scan records it too, but only when no other Tunero holds the change lock.</summary>
    [Fact]
    public async Task KeptChangesAreRecordedOnlyUnderTheLock()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        var t = TweakCatalog.Current.Get("privacy.advertisingIdOff")!;
        await fx.Engine.ApplyAsync(t, Facts(), new ApplyOptions { ContinueWithoutRestorePoint = true });
        UpdatedSince(fx, t.Id);
        var kept = new List<string>();
        Assert.Empty(fx.Engine.CheckDrift(Facts(), keptAfterUpdate: kept));
        Assert.Equal([t.Id], kept);

        fx.ChangeLock.HeldByOther = true;
        Assert.False(session.NoteKept(kept));
        Assert.Null(fx.Store.Get(t.Id)!.KeptOnVersion);

        fx.ChangeLock.HeldByOther = false;
        Assert.True(session.NoteKept(kept));
        Assert.Equal(fx.Engine.WindowsVersion, fx.Store.Get(t.Id)!.KeptOnVersion);
    }

    private static TweakDefinition Writes(string id, int value) =>
        Test(id, new RegistryAction { Hive = Hive.Machine, Path = @"SOFTWARE\TuneroTest", Name = "Shared", Kind = "dword", Value = JsonSerializer.SerializeToElement(value) });

    /// <summary>
    /// Two changes that write one target differently (backups from before the conflict check by shared target, which now
    /// blocks the second): the overwritten one is not drift, and the run must not flip the target at every sign-in.
    /// </summary>
    [Fact]
    public async Task TwoChangesOnOneTargetAreLeftForTheUser()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true };
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(Writes("test.one", 1), Facts(), options)).Outcome);
        Assert.Equal(ApplyOutcome.Blocked, (await fx.Engine.ApplyAsync(Writes("test.zero", 0), Facts(), options)).Outcome);
        // The state an earlier version could leave: the second change applied over the first.
        var first = fx.Store.Get("test.one")!;
        fx.Store.Archive("test.one");
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(Writes("test.zero", 0), Facts(), options)).Outcome);
        fx.Store.Save(first);
        UpdatedSince(fx, "test.one");
        UpdatedSince(fx, "test.zero");
        Assert.DoesNotContain(fx.Engine.CheckDrift(Facts()), d => d.Tweak.Id == "test.one");
        Assert.Same(BatchOutcome.Cancelled, await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), [])));
    }

    private static TweakDefinition Test(string id, TweakAction action) => new()
    {
        Id = id,
        Docs = id,
        Category = "Test",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Risk = Risk.Safe,
        Hidden = true,
        Actions = [action],
    };

    private static PowerSettingAction Boost(uint? ac, uint? dc) =>
        new() { Subgroup = "54533251-82be-4824-96c1-47b60b740d00", Setting = "be337238-0d82-4146-a960-4f3749d470c7", Ac = ac, Dc = dc };

    /// <summary>
    /// A mains-only and a battery-only change of one power setting on one plan do not conflict (the apply allows both):
    /// both reset by an update, the run applies both again, as "Apply again" in the window does.
    /// </summary>
    [Fact]
    public async Task TheMainsAndTheBatterySideOfOneSettingAreBothAppliedAgain()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true };
        var mains = Test("test.boostAc", Boost(0, null));
        var battery = Test("test.boostDc", Boost(null, 0));
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(mains, Facts(), options)).Outcome);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(battery, Facts(), options)).Outcome);
        // Both backups name the setting by one key without its side.
        Assert.Equal(fx.Store.Get(mains.Id)!.Entries.Single().TargetKey, fx.Store.Get(battery.Id)!.Entries.Single().TargetKey);

        // A feature update puts the plan back to its defaults.
        fx.Power.Ac.Clear();
        fx.Power.Dc.Clear();
        UpdatedSince(fx, mains.Id);
        UpdatedSince(fx, battery.Id);

        var outcome = await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), []));
        Assert.Equal(2, outcome.Applied);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(mains, Facts()));
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(battery, Facts()));
    }

    /// <summary>
    /// A whole value and a bit of it, both applied by an earlier version: the apply refuses each for the other, so the run
    /// leaves both for the user instead of failing at every sign-in.
    /// </summary>
    [Fact]
    public async Task AValueAndABitOfItAreLeftForTheUser()
    {
        using var fx = new EngineFixture();
        fx.RestorePoints.Enabled = false;
        var session = new ChangeSession(fx.Engine, fx.RestorePoints, fx.ChangeLock);
        var options = new ApplyOptions { ContinueWithoutRestorePoint = true };
        var whole = Test("test.flagsWhole", new RegistryAction { Hive = Hive.Machine, Path = @"SOFTWARE\TuneroTest", Name = "Flags", Kind = "dword", Value = JsonSerializer.SerializeToElement(1) });
        var bit = Test("test.flagsBit", new RegistryBitsAction { Hive = Hive.Machine, Path = @"SOFTWARE\TuneroTest", Name = "Flags", Set = 0x4 });
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(whole, Facts(), options)).Outcome);
        Assert.Equal(ApplyOutcome.Blocked, (await fx.Engine.ApplyAsync(bit, Facts(), options)).Outcome);
        // The state an earlier version could leave: both applied.
        var first = fx.Store.Get(whole.Id)!;
        fx.Store.Archive(whole.Id);
        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(bit, Facts(), options)).Outcome);
        fx.Store.Save(first);
        Assert.NotEqual(fx.Store.Get(whole.Id)!.Entries.Single().TargetKey, fx.Store.Get(bit.Id)!.Entries.Single().TargetKey);

        RegistryValue.Delete(fx.Registry, Hive.Machine, @"SOFTWARE\TuneroTest", "Flags");
        UpdatedSince(fx, whole.Id);
        UpdatedSince(fx, bit.Id);
        Assert.Contains(fx.Engine.CheckDrift(Facts()), d => d.WindowsUpdatedSince);

        Assert.Same(BatchOutcome.Cancelled, await session.ReapplyResetAsync(new PcSnapshot(Facts(), [], new HashSet<string>(), [])));
    }
}
