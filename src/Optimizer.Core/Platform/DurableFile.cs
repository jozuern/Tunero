namespace Optimizer.Core.Platform;

/// <summary>
/// Writes a small data file so that a power loss or a crash leaves either the old or the new content, never a cut-off
/// one: written to a temporary file next to it, flushed to the disk, then moved over the old file.
/// </summary>
public static class DurableFile
{
    /// <param name="prepare">
    /// Runs on the temporary file before it replaces the old one (to set its owner: a rename keeps it). When it throws,
    /// the old file stays as it was.
    /// </param>
    public static void WriteAllText(string file, string text, Action<string>? prepare = null)
    {
        var tmp = file + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        try
        {
            prepare?.Invoke(tmp);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        File.Move(tmp, file, overwrite: true);
    }
}
