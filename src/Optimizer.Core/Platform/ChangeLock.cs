namespace Optimizer.Core.Platform;

/// <summary>
/// The cross-process change lock as <see cref="Tweaks.ChangeSession"/> takes it: <see cref="ChangeLock.DataFolder"/> in
/// the app, a fake in tests (which must not create a file in the real data folder).
/// </summary>
public interface IChangeLock
{
    /// <summary>The lock, held until it is disposed; null when another process holds it for longer than <paramref name="wait"/>.</summary>
    IDisposable? TryAcquire(TimeSpan wait);
}

/// <summary>
/// One change at a time across processes: the window and a command line run (<c>--apply</c>, <c>--undo-all</c>), or
/// two accounts, share the data folder and its backups. Two applies of the same tweak at once could otherwise both find
/// no backup, and the second would record the first one's new value as the original. The lock is a file in the
/// admin-only data folder held open without sharing; Windows closes it when the process ends, so a crash never leaves it
/// taken. It is not tied to a thread, so it can be held across awaits. Only <see cref="Tweaks.ChangeSession"/> takes it:
/// every entry point that changes the PC goes through the session, so none can skip it.
/// </summary>
public sealed class ChangeLock : IDisposable
{
    private readonly FileStream _file;

    private ChangeLock(FileStream file) => _file = file;

    public static string DefaultFile => Path.Combine(DataPaths.Root, "changes.lock");

    /// <summary>The lock file in the data folder, shared by every Tunero on this PC.</summary>
    public static IChangeLock DataFolder { get; } = new FileLock(null);

    /// <summary>The lock, or null when another process holds it for longer than <paramref name="wait"/>.</summary>
    public static ChangeLock? TryAcquire(TimeSpan wait = default, string? file = null)
    {
        file ??= DefaultFile;
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                return new ChangeLock(new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logging.Log.Warn("lock", $"change lock not taken: {ex.Message}");
                return null;
            }
        }
    }

    public void Dispose() => _file.Dispose();

    /// <summary>The lock in <paramref name="file"/> (null: <see cref="DefaultFile"/>).</summary>
    public sealed class FileLock(string? file) : IChangeLock
    {
        public IDisposable? TryAcquire(TimeSpan wait) => ChangeLock.TryAcquire(wait, file);
    }
}
