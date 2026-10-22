using Optimizer.Core.Actions;

namespace Optimizer.Core.Tests;

/// <summary>A whole registry key: saved with every value and subkey, never half (too large means not offered), summarized for people.</summary>
public class RegistryKeyActionTests
{
    [Fact]
    public void AKeyOverTheLimitIsNotOffered()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i <= RegistryKeyAction.MaxValues; i++)
            RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Huge\Part" + (i / 500), "v" + i, "dword", "1");
        var action = new RegistryKeyAction { Hive = Hive.Machine, Path = @"SOFTWARE\Fixture\Huge" };
        Assert.Null(action.Read(fx.Context));
        Assert.Equal(ActionState.Unsupported, action.State(fx.Context));
    }

    [Fact]
    public void AMissingKeyCountsAsDone()
    {
        using var fx = new EngineFixture();
        var action = new RegistryKeyAction { Hive = Hive.Machine, Path = @"SOFTWARE\Fixture\NotThere" };
        Assert.Equal(ActionState.Applied, action.State(fx.Context));
    }

    [Fact]
    public void TheConfirmationShowsTheKeysSizeNotItsData()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Fixture\Menu", "A", "string", "x");
        RegistryValue.Write(fx.Registry, Hive.User, @"Software\Fixture\Menu\command", "", "string", "notepad.exe");
        var action = new RegistryKeyAction { Hive = Hive.User, Path = @"Software\Fixture\Menu" };
        var line = action.Change(fx.Context);
        Assert.Equal("@regkey:2:1", line.Before);
        Assert.Equal("key with 2 values and 1 subkeys", Docs.Labels.Current.Display("en", line.Before));
        Assert.Equal("Schlüssel mit 2 Werten und 1 Unterschlüsseln", Docs.Labels.Current.Display("de", line.Before));
    }

    [Fact]
    public void UndoWritesTheKeyBackOverWhatAHalfDeleteLeft()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Half", "A", "string", "x");
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Half\Locked", "B", "dword", "1");
        var action = new RegistryKeyAction { Hive = Hive.Machine, Path = @"SOFTWARE\Fixture\Half" };
        var original = action.Read(fx.Context)!;
        // The delete stopped at the locked subkey: the key and that subkey are left, the value A is gone.
        using (var key = fx.Registry.Open(Hive.Machine, @"SOFTWARE\Fixture\Half", writable: true)) key!.DeleteValue("A");
        var left = action.Read(fx.Context)!;

        Assert.True(action.IsStillApplied(fx.Context, left));
        Assert.Equal(RestoreOutcome.Restored, action.RestoreIfUnchanged(fx.Context, original, StoredValue.Missing));
        Assert.True(action.Read(fx.Context)!.SameAs(original));
    }

    [Fact]
    public void AKeyMadeAgainWithOtherDataIsLeftAlone()
    {
        using var fx = new EngineFixture();
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Again", "A", "string", "x");
        var action = new RegistryKeyAction { Hive = Hive.Machine, Path = @"SOFTWARE\Fixture\Again" };
        var original = action.Read(fx.Context)!;
        action.Apply(fx.Context);
        RegistryValue.Write(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Again", "A", "string", "new");

        Assert.Equal(RestoreOutcome.ChangedSince, action.RestoreIfUnchanged(fx.Context, original, StoredValue.Missing));
        Assert.Equal("new", RegistryValue.Read(fx.Registry, Hive.Machine, @"SOFTWARE\Fixture\Again", "A").Data);
    }
}
