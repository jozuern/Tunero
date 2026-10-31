using System.Diagnostics;
using System.IO;

namespace Optimizer.Core.Tests;

/// <summary>Temp folders for tests: created under %TEMP%, removed without ever following a junction.</summary>
internal static class TestFolders
{
    public static string Create(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pco-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A directory junction (no admin rights or Developer Mode needed, unlike a symbolic link).</summary>
    public static void Junction(string link, string target)
    {
        using var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(link), $"junction {link} was not created");
    }

    /// <summary>Deletes the folder: links are removed first without touching their targets, read-only flags are cleared.</summary>
    public static void Delete(string folder)
    {
        try
        {
            DeleteTree(new DirectoryInfo(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open by the system; the temp folder is cleaned by Windows later.
        }
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            dir.Delete();
            return;
        }
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            if (entry is DirectoryInfo sub)
            {
                DeleteTree(sub);
                continue;
            }
            entry.Attributes = FileAttributes.Normal;
            entry.Delete();
        }
        dir.Attributes = FileAttributes.Directory;
        dir.Delete();
    }
}
