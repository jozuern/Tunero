namespace Optimizer.App.Tests;

/// <summary>
/// Which starts keep to one window per session, how a second start finds the open copy (its record, not its process
/// name), and the lock with its record under names of the test's own (session objects that end with the test process).
/// Bringing the real window to the front is checked in the VM.
/// </summary>
public sealed class SingleInstanceTests
{
    [Theory]
    [InlineData]
    [InlineData("--after-update")]
    [InlineData("--AFTER-UPDATE")]
    public void APlainStartAndTheUpdatedExeKeepToOneWindow(params string[] args) => Assert.True(SingleInstance.Applies(args));

    /// <summary>The installed copy started by the setup waits for the downloaded one to close, like an updated exe.</summary>
    [Fact]
    public void TheInstalledCopyWaitsForTheDownloadedOne()
    {
        string[] args = ["--installed-from", @"C:\Users\Test\Downloads\Tunero.exe"];
        Assert.True(SingleInstance.Applies(args));
        Assert.True(SingleInstance.WaitsForOther(args));
        Assert.True(SingleInstance.WaitsForOther(["--after-update"]));
        Assert.False(SingleInstance.WaitsForOther([]));
    }

    /// <summary>Uninstall from Windows Settings never runs beside an open window: it would remove the entry while that one keeps running.</summary>
    [Fact]
    public void UninstallFromWindowsSettingsKeepsToOneWindow()
    {
        Assert.True(SingleInstance.Applies(["--uninstall"]));
        Assert.False(SingleInstance.WaitsForOther(["--uninstall"]));
    }

    /// <summary>Developer switches, command line runs and the reapply task neither block an open window nor wait for it.</summary>
    [Theory]
    [InlineData("--screenshot", "shot.png")]
    [InlineData("--perf", "perf.txt")]
    [InlineData("--apply", "changes.json")]
    [InlineData("--reapply")]
    [InlineData("--export", "out.json")]
    [InlineData("--lang", "de")]
    [InlineData("--after-update", "--screenshot", "shot.png")]
    [InlineData("--first-sign-in")]
    public void SwitchesStartBesideAnOpenWindow(params string[] args) => Assert.False(SingleInstance.Applies(args));

    private const int Holder = 4100;
    private const int Self = 4200;
    private static readonly IntPtr HolderWindow = new(0x10ABC);

    private static SingleInstance.OtherCopy Decide(InstanceRecord? record, bool holderRuns = true, int windowOwner = Holder) =>
        SingleInstance.Decide(record, Self, id => holderRuns && id == Holder, window => window == HolderWindow ? windowOwner : 0);

    /// <summary>The record names the open window: shown, whatever the exe of either copy is called ("Tunero (1).exe").</summary>
    [Fact]
    public void TheHoldersWindowIsShown() =>
        Assert.Equal(SingleInstance.OtherCopy.Show, Decide(new InstanceRecord(Holder, true, HolderWindow)));

    /// <summary>Two quick starts: the first one has the lock but no window yet; the second waits for it.</summary>
    [Fact]
    public void AHolderWithoutItsWindowYetIsWaitedFor()
    {
        Assert.Equal(SingleInstance.OtherCopy.NoWindowYet, Decide(new InstanceRecord(Holder, true, IntPtr.Zero)));
        // A window handle that now belongs to another process (the holder's window closed, the handle was reused).
        Assert.Equal(SingleInstance.OtherCopy.NoWindowYet, Decide(new InstanceRecord(Holder, true, HolderWindow), windowOwner: 999));
    }

    /// <summary>--build-media takes the lock for its cleanup without a window: a plain start opens its window beside it.</summary>
    [Fact]
    public void ARunWithoutAWindowLetsTheStartGoOn() =>
        Assert.Equal(SingleInstance.OtherCopy.StartHere, Decide(new InstanceRecord(Holder, false, IntPtr.Zero)));

    [Fact]
    public void ARecordOfThisProcessIsNoOtherCopy() =>
        Assert.Equal(SingleInstance.OtherCopy.StartHere, Decide(new InstanceRecord(Self, true, HolderWindow)));

    /// <summary>The previous holder ended (an update hands the lock over): the new holder's record follows.</summary>
    [Fact]
    public void ARecordOfAnEndedProcessIsStale() =>
        Assert.Equal(SingleInstance.OtherCopy.Stale, Decide(new InstanceRecord(Holder, true, HolderWindow), holderRuns: false));

    /// <summary>No record: a Tunero from before the record holds the lock, and is looked for by its process name as before.</summary>
    [Fact]
    public void WithoutARecordTheVersionBeforeIsLookedForByName() =>
        Assert.Equal(SingleInstance.OtherCopy.FromBefore, Decide(null));

    private static (string Lock, string Record) TestNames()
    {
        var name = @"Local\Tunero.Tests.SingleInstance." + Guid.NewGuid().ToString("N");
        return (name, name + ".Window");
    }

    /// <summary>The lock belongs to a thread: another thread stands for the second start.</summary>
    private static (bool Taken, bool HeldByOther) OnOtherThread(string name, string record, bool window = true)
    {
        (bool, bool) result = default;
        var thread = new Thread(() =>
        {
            var taken = SingleInstance.TryAcquire(name, record, TimeSpan.Zero, out var held, window);
            result = (taken is not null, held);
            if (taken is null) return;
            taken.Release();
            taken.Dispose();
        });
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void TheHolderWritesItsRecordAndASecondStartIsRefused()
    {
        var (name, record) = TestNames();
        using var held = SingleInstance.TryAcquire(name, record, TimeSpan.Zero, out var heldByOther, window: true);
        Assert.NotNull(held);
        Assert.False(heldByOther);
        Assert.Equal(new InstanceRecord(Environment.ProcessId, true, IntPtr.Zero), InstanceRecord.Read(record));

        held.ShowsWindow(HolderWindow);
        Assert.Equal(new InstanceRecord(Environment.ProcessId, true, HolderWindow), InstanceRecord.Read(record));

        Assert.Equal((false, true), OnOtherThread(name, record));

        held.Release();
        // Let go: the next start takes the lock and writes its own record over the old one.
        Assert.Equal((true, false), OnOtherThread(name, record, window: false));
    }

    [Fact]
    public void ARunWithoutAWindowSaysSo()
    {
        var (name, record) = TestNames();
        using var held = SingleInstance.TryAcquire(name, record, TimeSpan.Zero, out _, window: false);
        Assert.NotNull(held);
        Assert.Equal(new InstanceRecord(Environment.ProcessId, false, IntPtr.Zero), InstanceRecord.Read(record));
        // A second plain start beside it goes on at once (nothing to bring to the front).
        Assert.False(SingleInstance.ShowOther(record, Environment.ProcessId + 1));
        held.Release();
    }

    /// <summary>A block under the name that Tunero did not write (or not yet) reads as no record.</summary>
    [Fact]
    public void ABlockWithoutTheMarkIsNoRecord()
    {
        var (_, record) = TestNames();
        Assert.Null(InstanceRecord.Read(record));
        using var block = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(record, 24);
        Assert.Null(InstanceRecord.Read(record));
    }
}
