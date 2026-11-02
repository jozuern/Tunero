namespace Optimizer.Core.Platform;

/// <summary>
/// The app's entry in the signed-in user's Start menu ("Tunero PC Optimizer.lnk" in Programs). Windows Search lists apps
/// from the Start menu, and a portable exe (also one winget installed) has no entry there otherwise. It matches an app
/// only by the words of the entry's name, not its comment or the exe's description (VM test, 2026-10-13), so the name
/// carries "PC Optimizer" for people who search for that.
/// </summary>
public static class StartMenuShortcut
{
    public const string FileName = "Tunero PC Optimizer.lnk";

    /// <summary>
    /// The entry's earlier names: "Tunero.lnk" (0.8.0 to 0.9.7) and "PCOptimizer.lnk" (0.7.3 and older); found, followed
    /// and removed like the new one, and renamed by <see cref="RenameLegacy"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyFileNames = ["Tunero.lnk", "PCOptimizer.lnk"];

    private static IEnumerable<string> AllFileNames => LegacyFileNames.Prepend(FileName);

    /// <summary>
    /// Start menu programs folder of the signed-in user. Under Administrator protection or a separate admin account this
    /// process runs as another user, whose Start menu the signed-in user never sees: the folder then comes from the
    /// signed-in user's profile.
    /// </summary>
    public static string? ProgramsFolder(ElevationInfo? elevation, string? sessionProfile) =>
        elevation is { UserMismatch: true }
            ? sessionProfile is null ? null : Path.Combine(sessionProfile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs")
            : Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public static bool Exists(string folder) => AllFileNames.Any(name => File.Exists(Path.Combine(folder, name)));

    /// <summary>Exe the shortcut starts, or null when there is none.</summary>
    public static string? Target(string folder)
    {
        var path = AllFileNames.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
        if (path is null) return null;
        // Written with an absolute path; one with %variables% counts with the machine's (Program Files).
        return PathExpander.Machine.Expand(ShellLinkFile.Read(path).Target);
    }

    /// <summary>
    /// Writes the shortcut. The folder belongs to the signed-in user, who can replace it or a file in it with a link while
    /// the elevated app writes: the shortcut is saved in the app's own temp folder first and then copied into a file that
    /// is created new and checked for its real path (<see cref="SafeDelete.CreateNew"/>), so it can only land in the
    /// Programs folder itself. An earlier shortcut is deleted through <see cref="SafeDelete"/> first.
    /// </summary>
    /// <param name="workFolder">Where the shortcut is saved first; null uses the tools' TEMP folder (admin-only in the app).</param>
    public static void Create(string folder, string exe, string description, string? workFolder = null)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"{folder} does not exist");
        workFolder ??= ProcessHardening.TempFolder ?? Path.GetTempPath();
        var draft = Path.Combine(workFolder, $"{Guid.NewGuid():N}.lnk");
        try
        {
            ShellLinkFile.Write(draft, exe, description);
            var bytes = File.ReadAllBytes(draft);
            Remove(folder);
            using var target = SafeDelete.CreateNew(Path.Combine(folder, FileName));
            target.Write(bytes);
        }
        finally
        {
            if (File.Exists(draft)) File.Delete(draft);
        }
    }

    /// <summary>
    /// An entry under an earlier name that starts <paramref name="exe"/> is written again under <see cref="FileName"/> (and
    /// the old one deleted), so Windows Search also finds it as "PC Optimizer". An entry that already has the new name, or
    /// that starts another copy of the app, stays as it is. True when it renamed one.
    /// </summary>
    public static bool RenameLegacy(string folder, string exe, string description, string? workFolder = null)
    {
        // A shortcut to something that is not a file (a shell folder, a URL) has an empty target.
        if (File.Exists(Path.Combine(folder, FileName)) || Target(folder) is not { Length: > 0 } target) return false;
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return false;
        Create(folder, exe, description, workFolder);
        return true;
    }

    /// <summary>
    /// Deletes the shortcut, under its current and its old names, through <see cref="SafeDelete"/>: a link in its path is
    /// refused instead of followed.
    /// </summary>
    public static void Remove(string folder)
    {
        foreach (var name in AllFileNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) || Directory.Exists(path)) SafeDelete.DeleteFile(path);
        }
    }
}
