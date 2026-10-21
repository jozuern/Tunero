using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>
/// Another Tunero process is found by the file every elevated copy holds open, not by its exe's name. Two handles in this
/// process stand for two processes (Windows checks the sharing per handle); the file is in a temp folder.
/// </summary>
public sealed class RunningCopiesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tunero-running-" + Guid.NewGuid().ToString("N"));

    private string LockFile => Path.Combine(_folder, "running.lock");

    public void Dispose() => TestFolders.Delete(_folder);

    [Fact]
    public void TheFirstCopyIsAloneAndTheNextOneSeesIt()
    {
        var first = RunningCopies.Join(out var othersAtFirst, LockFile);
        Assert.NotNull(first);
        Assert.False(othersAtFirst);

        using (var second = RunningCopies.Join(out var othersAtSecond, LockFile))
        {
            Assert.NotNull(second);
            Assert.True(othersAtSecond);
        }

        first.Dispose();
        // Every copy ended (or crashed: Windows closes the file): the next start is alone again.
        using var later = RunningCopies.Join(out var othersLater, LockFile);
        Assert.False(othersLater);
    }

    /// <summary>A copy that is checking at this moment (the file held without sharing) counts as running, and the start still joins.</summary>
    [Fact]
    public void ACopyInTheMiddleOfItsCheckCounts()
    {
        Directory.CreateDirectory(_folder);
        var checking = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Its own thread, not the pool: with the whole suite running, a pool task can start later than Join retries.
        var release = new Thread(() =>
        {
            Thread.Sleep(100);
            checking.Dispose();
        });
        release.Start();
        using var joined = RunningCopies.Join(out var others, LockFile);
        release.Join();
        Assert.True(others);
        Assert.NotNull(joined);
    }
}
