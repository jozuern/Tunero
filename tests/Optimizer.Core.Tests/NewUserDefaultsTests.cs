using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>"Also for new accounts": per-user tweaks that are on, copied to the Default profile as one undoable change.</summary>
public class NewUserDefaultsTests
{
    private static RegistryAction User(string name, int value) =>
        new() { Hive = Hive.User, Path = @"Software\TuneroTest\Explorer", Name = name, Kind = "dword", Value = JsonSerializer.SerializeToElement(value), Notify = "settings" };

    private static TweakDefinition T(string id, params TweakAction[] actions) => new()
    {
        Id = id,
        Category = "Explorer",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Actions = [.. actions],
    };

    private static (TweakDefinition, IReadOnlyList<TweakAction>) With(TweakDefinition t) => (t, t.Actions);

    [Fact]
    public void OnlyTweaksMadeOfUserValuesGoAlong()
    {
        var userOnly = T("explorer.a", User("A", 1), User("B", 0));
        var machine = T("explorer.m", User("C", 1), new RegistryAction { Hive = Hive.Machine, Path = @"SOFTWARE\TuneroTest", Name = "M", Value = JsonSerializer.SerializeToElement(1) });
        var withCall = T("explorer.s", User("D", 1), new SystemParameterAction { Parameter = SystemParameter.MenuShowDelay, Value = 100 });
        var hidden = new TweakDefinition { Id = "startup.off.runkey.x", Category = "Startup", Impact = userOnly.Impact, Hidden = true, Actions = [User("E", 1)] };

        var classes = T("explorer.classicContextMenu", new RegistryAction { Hive = Hive.User, Path = @"Software\Classes\CLSID\{x}\InprocServer32", Kind = "string", Value = JsonSerializer.SerializeToElement("") });

        var copy = NewUserDefaults.Build([With(userOnly), With(machine), With(withCall), With(hidden), With(classes)]);

        Assert.NotNull(copy);
        Assert.Equal(NewUserDefaults.Id, copy!.Id);
        Assert.True(copy.Hidden);
        var actions = copy.Actions.Cast<RegistryAction>().ToList();
        Assert.Equal(["A", "B"], actions.Select(a => a.Name));
        Assert.All(actions, a => Assert.Equal(Hive.DefaultUser, a.Hive));
        Assert.All(actions, a => Assert.Null(a.Notify));
        Assert.Null(NewUserDefaults.Build([With(machine), With(withCall)]));
    }

    [Fact]
    public async Task TheDefaultProfileGetsTheValuesAndUndoPutsItBack()
    {
        using var fx = new EngineFixture();
        const string path = @"Software\TuneroTest\Explorer";
        // The Default profile had B = 5 before; A did not exist.
        using (fx.Registry.Hold(Hive.DefaultUser)) RegistryValue.Write(fx.Registry, Hive.DefaultUser, path, "B", "dword", "5");
        var copy = NewUserDefaults.Build([With(T("explorer.a", User("A", 1), User("B", 0)))])!;
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        var loads = fx.Registry.DefaultProfileLoads;

        var result = await fx.Engine.ApplyAsync(copy, facts, new ApplyOptions { ContinueWithoutRestorePoint = true });

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        // Two loads (the check before the restore point, then the change), not one per value.
        Assert.Equal(loads + 2, fx.Registry.DefaultProfileLoads);
        Assert.All(result.Changes, c => Assert.StartsWith(@"Default\NTUSER.DAT", c.Target));
        using (fx.Registry.Hold(Hive.DefaultUser))
        {
            Assert.Equal("1", RegistryValue.Read(fx.Registry, Hive.DefaultUser, path, "A").Data);
            Assert.Equal("0", RegistryValue.Read(fx.Registry, Hive.DefaultUser, path, "B").Data);
        }
        // The signed-in user's hive is not touched.
        Assert.False(RegistryValue.Read(fx.Registry, Hive.User, path, "A").Existed);
        Assert.Equal(TweakState.Applied, fx.Engine.DetectState(copy, facts));

        // After a restart of the app the stored definition undoes it.
        var engine = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300);
        Assert.True(engine.Revert(engine.Resolve(NewUserDefaults.Id)!).Success);
        using (fx.Registry.Hold(Hive.DefaultUser))
        {
            Assert.False(RegistryValue.Read(fx.Registry, Hive.DefaultUser, path, "A").Existed);
            Assert.Equal("5", RegistryValue.Read(fx.Registry, Hive.DefaultUser, path, "B").Data);
        }
    }

    /// <summary>Scans neither check the copy for drift nor load the hive for it: the file must be free for Windows.</summary>
    [Fact]
    public async Task ScansDoNotLoadTheDefaultProfile()
    {
        using var fx = new EngineFixture();
        var copy = NewUserDefaults.Build([With(T("explorer.a", User("A", 1)))])!;
        var facts = new Facts().Set("os.build", 26300).Set("elevated", true);
        await fx.Engine.ApplyAsync(copy, facts, new ApplyOptions { ContinueWithoutRestorePoint = true });
        var loads = fx.Registry.DefaultProfileLoads;
        Assert.Empty(fx.Engine.CheckDrift(facts));
        Assert.Equal(loads, fx.Registry.DefaultProfileLoads);
    }

    /// <summary>When the hive cannot be loaded the copy fails with the reason; it never reports "already in place".</summary>
    [Fact]
    public async Task AHiveThatCannotLoadFailsTheCopy()
    {
        using var fx = new EngineFixture();
        fx.Registry.LoadFails = "The Default profile could not be loaded: in use";
        var copy = NewUserDefaults.Build([With(T("explorer.a", User("A", 1)))])!;
        var result = await fx.Engine.ApplyAsync(copy, new Facts().Set("os.build", 26300).Set("elevated", true),
            new ApplyOptions { ContinueWithoutRestorePoint = true });
        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("in use", result.Error);
        Assert.Empty(fx.Store.All());
    }

    /// <summary>The hive is loaded only inside a hold: an open without one is a bug, not a silent load that stays.</summary>
    [Fact]
    public void TheDefaultProfileIsOpenedOnlyInsideAHold()
    {
        using var fx = new EngineFixture();
        var registry = fx.Registry;
        Assert.Throws<InvalidOperationException>(() => registry.Open(Hive.DefaultUser, @"Software\TuneroTest", writable: false));

        // An action holds the hive by itself for each read and write, and lets it go again.
        var action = new RegistryAction { Hive = Hive.DefaultUser, Path = @"Software\TuneroTest", Name = "X", Value = JsonSerializer.SerializeToElement(1) };
        var context = fx.Context;
        Assert.Equal(ActionState.NotApplied, action.State(context));
        action.Apply(context);
        Assert.Equal(ActionState.Applied, action.State(context));
        Assert.Throws<InvalidOperationException>(() => registry.Open(Hive.DefaultUser, @"Software\TuneroTest", writable: false));
    }
}
