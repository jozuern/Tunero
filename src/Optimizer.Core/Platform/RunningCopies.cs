namespace Optimizer.Core.Platform;

/// <summary>
/// Whether another Tunero process uses the data folder, whatever its exe is called ("Tunero (1).exe", a renamed copy) and
/// in whichever session: every elevated process holds a file in the admin-only data folder open with sharing while it
/// runs, and a start that cannot open it without sharing knows another one runs. Windows closes the file when a process
/// ends, so a crash never leaves it taken. What it decides: whether the start may empty the tools' TEMP folder
/// (<see cref="ProcessHardening.UseTempFolder"/>), which another copy's DISM or installer may still be using.
/// </summary>
public static class RunningCopies
{
    public static string DefaultFile => Path.Combine(DataPaths.Root, "running.lock");

    /// <summary>
    /// Joins the running copies for this process's lifetime: keep the returned file open until the process ends (null when
    /// the file could not be opened). <paramref name="othersRun"/> says whether another process held it at this moment; when
    /// the file cannot be read it is true, so nothing is emptied on a guess.
    /// </summary>
    public static FileStream? Join(out bool othersRun, string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) othersRun = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            othersRun = true;
        }
        // Another start may hold it without sharing for the moment of its own check.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < 25)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logging.Log.Warn("app", $"running copies file not held: {ex.Message}");
                return null;
            }
        }
    }
}
