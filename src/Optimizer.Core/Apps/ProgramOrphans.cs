using Optimizer.Core.Actions;
using Optimizer.Core.Startup;

namespace Optimizer.Core.Apps;

/// <summary>What still points into program folders: paths in use and the names of installed programs and their publishers.</summary>
public sealed record OrphanEvidence(IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> Names);

/// <summary>
/// Folders in Program Files that belong to no installed program any more, as Bulk Crap Uninstaller's "leftover" scan
/// finds them, but only where nothing at all points into them. A folder is offered only when no uninstall entry (also the
/// hidden components), Windows Installer record, service, autostart entry, App Paths entry, COM server, running program or
/// Start menu or desktop shortcut uses a path inside it, its name does not match an installed program or its publisher,
/// and nothing in it changed for a month. The user reviews the list; ticked folders go to the Recycle Bin.
/// </summary>
public static class ProgramOrphans
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>A folder whose content changed within this many days may belong to something just installed or portable in use.</summary>
    public const int QuietDays = 30;

    /// <summary>
    /// Everything in the registry that points into program folders, plus <paramref name="extraPaths"/> (autostart entries
    /// and running programs, which the caller reads). <paramref name="user"/> expands the signed-in user's own uninstall
    /// entries (<see cref="Platform.PathExpander.ForUser(string?, IRegistryRoots?)"/>).
    /// </summary>
    public static OrphanEvidence ReadEvidence(IRegistryRoots registry, IEnumerable<string> extraPaths, Platform.PathExpander? user = null)
    {
        var paths = new List<string>(Programs.ReferencedPaths(registry, user));
        var names = new List<string>();
        var productCodes = new List<string>();
        foreach (var (hive, path) in new[] { (Hive.Machine, Uninstall), (Hive.Machine, Uninstall32), (Hive.User, Uninstall) })
        {
            ReadEach(registry, hive, path, (key, sub) =>
            {
                if (Guid.TryParse(sub, out _)) productCodes.Add(sub);
                using var app = key.OpenSubKey(sub);
                foreach (var value in new[] { "DisplayName", "Publisher" })
                    if (app?.GetValue(value) is string s && s.Trim().Length > 0) names.Add(s);
            });
        }
        // MSI packages often record no install location (7-Zip's): the files they registered say where they live.
        paths.AddRange(ProgramLeftovers.MsiFolders(registry, productCodes).Values);
        // Programs started by name (App Paths) and COM servers other programs load.
        ReadEach(registry, Hive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", (key, sub) =>
        {
            using var app = key.OpenSubKey(sub);
            AddPath(paths, app?.GetValue(null, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
            AddPath(paths, app?.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
        });
        foreach (var clsid in new[] { @"SOFTWARE\Classes\CLSID", @"SOFTWARE\WOW6432Node\Classes\CLSID" })
        {
            ReadEach(registry, Hive.Machine, clsid, (key, sub) =>
            {
                foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var s = key.OpenSubKey($@"{sub}\{server}");
                    if (s?.GetValue(null, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                        AddPath(paths, CommandLine.ImagePath(value, Platform.PathExpander.Machine.Expand));
                }
            });
        }
        foreach (var p in extraPaths) AddPath(paths, p);
        return new OrphanEvidence(paths, names);
    }

    /// <summary>
    /// What the user's taskbar pins start: a pin counts as use, like Start menu and desktop shortcuts (which <see
    /// cref="Find"/> reads itself).
    /// </summary>
    public static IReadOnlyList<string> PinnedTargets(string? profile)
    {
        if (profile is not { Length: > 0 }) return [];
        var pins = Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
        return Directory.Exists(pins) ? ProgramLeftovers.Shortcuts(pins).Select(ProgramLeftovers.ShortcutTarget).OfType<string>().ToList() : [];
    }

    /// <summary>
    /// A path that points into a folder; an 8.3 short name ("C:\PROGRA~2\Vendor") as its long form, so it matches the
    /// folder. Values here are the machine's or already expanded (autostart entries, running programs).
    /// </summary>
    private static void AddPath(List<string> paths, string? value)
    {
        if (ProgramLeftovers.CleanPath(value, Platform.PathExpander.Machine, _ => false) is not { } p) return;
        paths.Add(p.Contains('~') ? Platform.SafeDelete.LongPath(p) : p);
    }

    /// <summary>
    /// Reads every subkey on its own: one that cannot be read (only the system may) leaves out that entry, not the rest
    /// of the key, which would lose evidence and offer a folder still in use.
    /// </summary>
    internal static void ReadEach(IRegistryRoots registry, Hive hive, string path, Action<Microsoft.Win32.RegistryKey, string> read)
    {
        Microsoft.Win32.RegistryKey? key;
        string[] subs;
        try
        {
            key = registry.Open(hive, path, writable: false);
            if (key is null) return;
            subs = key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // no user hive, or a key only the system can read
            return;
        }
        using (key)
        {
            foreach (var sub in subs)
            {
                try
                {
                    read(key, sub);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // this entry only the system can read
                }
            }
        }
    }

    /// <summary>
    /// The folders directly in <paramref name="programFolders"/> (Program Files, both views) that nothing uses, with their
    /// size and the time anything in them last changed.
    /// </summary>
    public static IReadOnlyList<Leftover> Find(IEnumerable<string> programFolders, LeftoverRoots roots, OrphanEvidence evidence, DateTime nowUtc,
        Func<string, string?>? shortcutTarget = null)
    {
        // A Start menu or desktop shortcut into a folder counts as use.
        var shortcuts = roots.ShortcutFolders.Where(Directory.Exists).SelectMany(ProgramLeftovers.Shortcuts)
            .Select(shortcutTarget ?? ProgramLeftovers.ShortcutTarget).OfType<string>();
        var used = evidence.Paths.Concat(shortcuts).ToList();
        var names = evidence.Names.Select(Normalize).Where(n => n.Length >= 3).Distinct().ToList();
        var found = new List<Leftover>();
        foreach (var root in programFolders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var folder in Subfolders(root))
            {
                if (MatchesInstalledName(Path.GetFileName(folder), names) || !ProgramLeftovers.MayOffer(folder, roots, used) || !Platform.SafeDelete.HasNoLinks(folder)) continue;
                var last = LastChangeUtc(folder);
                if (nowUtc - last < TimeSpan.FromDays(QuietDays)) continue;
                found.Add(new Leftover(LeftoverKind.Folder, folder, ProgramLeftovers.Size(folder)) { LastChangeUtc = last });
            }
        }
        return found;
    }

    private static IEnumerable<string> Subfolders(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>"Vendor App" in Program Files and an installed "Vendor App 2.1 (x64)" or publisher "Vendor": the folder is in use.</summary>
    public static bool MatchesInstalledName(string folderName, IReadOnlyCollection<string> normalizedNames)
    {
        var n = Normalize(folderName);
        if (n.Length < 3) return true; // too short to tell: keep
        return normalizedNames.Any(name => name.Contains(n, StringComparison.Ordinal) || (name.Length >= 4 && n.Contains(name, StringComparison.Ordinal)));
    }

    /// <summary>Letters and digits only, lower case: "7-Zip 25.01 (x64)" becomes "7zip2501x64".</summary>
    public static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The newest write time of the folder and everything in it (links not followed).</summary>
    public static DateTime LastChangeUtc(string folder)
    {
        var last = Directory.GetLastWriteTimeUtc(folder);
        try
        {
            foreach (var e in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
                if (e.LastWriteTimeUtc > last) last = e.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return last;
    }
}
