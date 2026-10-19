using Optimizer.Core.Platform;
using Optimizer.Core.Startup;

namespace Optimizer.Core.Apps;

public enum LeftoverKind { Folder, Shortcut }

/// <summary>A folder or shortcut an uninstalled program left behind; <see cref="Bytes"/> is 0 for a shortcut.</summary>
public sealed record Leftover(LeftoverKind Kind, string Path, long Bytes)
{
    /// <summary>When anything in the folder last changed; set for folders no program uses (<see cref="ProgramOrphans"/>).</summary>
    public DateTime? LastChangeUtc { get; init; }
}

/// <summary>
/// Where leftovers may be offered: the program folders of the PC and of the signed-in user, the Start menu and desktop
/// folders for shortcuts, and folders that are never offered (this app and its data).
/// </summary>
public sealed record LeftoverRoots(IReadOnlyList<string> Folders, IReadOnlyList<string> ShortcutFolders, IReadOnlyList<string> Keep)
{
    /// <param name="profile">The signed-in user's profile folder; null leaves out the per-user folders.</param>
    public static LeftoverRoots ForPc(string? profile)
    {
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        var shortcuts = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        if (profile is { Length: > 0 })
        {
            folders.Add(Path.Combine(profile, "AppData", "Local"));
            folders.Add(Path.Combine(profile, "AppData", "Roaming"));
            shortcuts.Add(Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"));
            shortcuts.Add(Path.Combine(profile, "Desktop"));
        }
        var keep = new List<string> { DataPaths.Root };
        if (Path.GetDirectoryName(Environment.ProcessPath) is { } self) keep.Add(self);
        return new LeftoverRoots(folders.Where(f => f.Length > 0).ToList(), shortcuts.Where(f => f.Length > 0).ToList(), keep);
    }
}

/// <summary>
/// What an uninstalled program left behind, the way Bulk Crap Uninstaller looks for it, but only where the evidence is
/// clear: the program's own folder (its install location, the folder of its uninstaller or its icon) when it still
/// exists after the uninstall entry is gone, and Start menu or desktop shortcuts that point into that folder. A folder is
/// offered only below a program folder (Program Files, ProgramData, the user's AppData), not at the first level of a
/// folder many programs share (Common Files, Microsoft, Programs), and never when another uninstall entry, a hidden
/// component or a service still points into it or above it. Everything found goes to the Recycle Bin after the user
/// reviewed it, never straight away.
/// </summary>
public static class ProgramLeftovers
{
    /// <summary>Folders below a program folder that hold the files of many programs or of Windows: only their subfolders can be leftovers.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Microsoft", "Microsoft Shared", "Microsoft.NET", "Windows", "WindowsApps", "ModifiableWindowsApps",
        "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows NT", "Windows Mail", "Windows Media Player",
        "Windows Photo Viewer", "Windows Sidebar", "Windows Security", "Windows Portable Devices", "WindowsPowerShell", "PowerShell",
        "PackageManagement", "Package Cache", "Internet Explorer", "Reference Assemblies", "MSBuild", "dotnet", "Uninstall Information",
        "Programs", "Packages", "Temp", "Application Data", "Start Menu", "Desktop", "Documents", "regid.1991-06.com.microsoft",
        "USOShared", "USOPrivate", "ssh", "Comms", "SoftwareDistribution", "NVIDIA Corporation", "NVIDIA", "Intel", "AMD",
        "ATI Technologies", "Realtek",
        // Developer and server infrastructure that no single program records as its own (NuGet's machine-wide config
        // folder holds Visual Studio's fallback package source, nothing points into it, and it rarely changes).
        "NuGet", "Windows Kits", "Microsoft SDKs", "IIS", "IIS Express", "Microsoft Visual Studio", "Microsoft Help Viewer",
    };

    /// <summary>Shared folders deeper down (relative to a program folder): never offered themselves, only what is inside them.</summary>
    private static readonly HashSet<string> SharedDeeper = new(StringComparer.OrdinalIgnoreCase)
    {
        @"Microsoft\WindowsApps", @"Microsoft\Windows", @"Microsoft\Windows\Start Menu", @"Microsoft\Windows\Start Menu\Programs",
        @"Microsoft\Windows\Start Menu\Programs\Startup", @"Microsoft\WinGet", @"Microsoft\WinGet\Packages", @"Microsoft\WinGet\Links",
        @"Microsoft\Edge", @"Microsoft\EdgeUpdate", @"Microsoft\OneDrive", @"Microsoft\Teams", @"Microsoft\Windows Defender",
        @"Microsoft\Crypto", @"Microsoft\Protect", @"Microsoft\Credentials", @"Microsoft\Vault", @"Microsoft\Office",
    };

    /// <summary>A folder path from a registry value: quotes, an icon index (",0") and a trailing backslash removed; a file gives its folder.</summary>
    public static string? CleanPath(string? value, Func<string, bool>? isFile = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = Environment.ExpandEnvironmentVariables(value.Trim());
        // "C:\App\app.exe",0 or C:\App\app.exe,0: the icon index after the path.
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma].Trim();
        s = s.Trim('"').Trim();
        if (s.Length < 3 || !Path.IsPathFullyQualified(s)) return null;
        try
        {
            s = Path.GetFullPath(s).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return (isFile ?? File.Exists)(s) ? Path.GetDirectoryName(s) : s;
    }

    private const string MsiComponents = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components";

    /// <summary>
    /// The folder of each per-machine Windows Installer product, from the files it registered (Installer\UserData\S-1-5-18\
    /// Components, one value per product under its packed code). Many MSI packages record no install location (7-Zip's),
    /// and the uninstall removes these records: read them before it. A product whose files have no common folder below
    /// a drive gets none. One pass over the components for all products.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MsiFolders(Actions.IRegistryRoots registry, IEnumerable<string> productCodes)
    {
        var packed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in productCodes)
            if (PackedGuid(code) is { } p) packed[p] = code;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (packed.Count == 0) return result;
        using var components = registry.Open(Actions.Hive.Machine, MsiComponents, writable: false);
        if (components is null) return result;
        var files = packed.Values.ToDictionary(c => c, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in components.GetSubKeyNames())
        {
            using var component = components.OpenSubKey(name);
            if (component is null) continue;
            foreach (var value in component.GetValueNames())
                // Registry key paths ("01:\Software\...") and empty values are skipped: only file and folder paths count.
                if (packed.TryGetValue(value, out var code) && component.GetValue(value) is string path && path.Length > 3 && char.IsAsciiLetter(path[0])
                    && path[1] == ':' && path[2] == '\\')
                    // A folder component ends with a backslash; a file gives its folder.
                    files[code].Add(path.EndsWith('\\') ? path.TrimEnd('\\') : Path.GetDirectoryName(path) ?? path);
        }
        foreach (var (code, folders) in files)
            if (CommonFolder(folders) is { } common) result[code] = common;
        return result;
    }

    /// <summary>The deepest folder all of <paramref name="folders"/> lie in; null when that is a drive root or there are none.</summary>
    public static string? CommonFolder(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0) return null;
        var common = folders[0].TrimEnd('\\').Split('\\');
        var length = common.Length;
        foreach (var f in folders.Skip(1))
        {
            var parts = f.TrimEnd('\\').Split('\\');
            length = Math.Min(length, parts.Length);
            for (var i = 0; i < length; i++)
                if (!string.Equals(parts[i], common[i], StringComparison.OrdinalIgnoreCase)) { length = i; break; }
        }
        return length < 2 ? null : string.Join('\\', common.Take(length));
    }

    /// <summary>
    /// Windows Installer's packed form of a product code: the first three groups reversed, then each byte of the last
    /// two with its two hex digits swapped ({23170F69-40C1-2702-...} becomes 96F07132 1C04 2072 ...).
    /// </summary>
    public static string? PackedGuid(string productCode)
    {
        if (!Guid.TryParse(productCode, out var g)) return null;
        var h = g.ToString("N").ToUpperInvariant();
        static string Reversed(string s) => new(s.Reverse().ToArray());
        var sb = new System.Text.StringBuilder(32).Append(Reversed(h[..8])).Append(Reversed(h[8..12])).Append(Reversed(h[12..16]));
        for (var i = 16; i < 32; i += 2) sb.Append(h[i + 1]).Append(h[i]);
        return sb.ToString();
    }

    /// <summary>
    /// File count and bytes below the program's existing folders. An uninstaller can remove the uninstall entry before it
    /// deletes the files (VS Code's): the leftover check waits until this stops changing.
    /// </summary>
    public static (int Files, long Bytes) Footprint(IEnumerable<string> folders)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        int files = 0;
        long bytes = 0;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    files++;
                    bytes += f.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder deleted while it is read: the next look counts again.
            }
        }
        return (files, bytes);
    }

    /// <summary>The program's own folders: its install location and the folders of its uninstaller and its icon (not System32's msiexec).</summary>
    public static IReadOnlyList<string> Folders(DesktopProgram p, Func<string, bool>? isFile = null)
    {
        isFile ??= File.Exists;
        var list = new List<string>();
        if (CleanPath(p.InstallLocation, isFile) is { } location) list.Add(location);
        foreach (var command in new[] { p.UninstallString, p.QuietUninstallString })
        {
            // The program the line starts: a rundll32 line gives rundll32 (skipped), not a DLL in a shared folder.
            if (p.ProductCode is not null || CommandLine.Program(command, null, isFile) is not var (image, _)) continue;
            if (image.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.IsPathFullyQualified(image) && Path.GetDirectoryName(image) is { } folder) list.Add(folder.TrimEnd('\\'));
        }
        // An icon is often the program itself; an icon in the Installer cache or in Windows says nothing about the folder.
        if (CleanPath(p.DisplayIcon, _ => true) is { } icon && !icon.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase))
            list.Add(icon);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// True when the folder lies below a program folder (not at the first level of a shared one), holds nothing an
    /// installed program or service still uses, and is neither inside nor above such a folder or a folder to keep.
    /// </summary>
    public static bool MayOffer(string folder, LeftoverRoots roots, IEnumerable<string> referenced)
    {
        var f = folder.TrimEnd('\\');
        var root = roots.Folders.Select(r => r.TrimEnd('\\'))
            .Where(r => f.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is null) return false;
        var parts = f[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || (parts.Length == 1 && Shared.Contains(parts[0])) || SharedDeeper.Contains(string.Join('\\', parts))) return false;
        if (roots.Folders.Any(r => Inside(r.TrimEnd('\\'), f) || Same(r.TrimEnd('\\'), f))) return false;
        if (roots.Keep.Any(k => Same(k.TrimEnd('\\'), f) || Inside(k.TrimEnd('\\'), f) || Inside(f, k.TrimEnd('\\')))) return false;
        // A path of something still installed in the folder, or a still installed program whose folder contains it. An
        // entry that names a whole program folder (Program Files itself, which some installers write) says nothing.
        return !referenced.Any(r => Same(r, f) || Inside(r, f)
                                    || (Inside(f, r) && !roots.Folders.Any(root => Same(root, r) || Inside(root, r))));
    }

    /// <summary>
    /// Leftovers of a program whose uninstall entry is gone. <paramref name="referenced"/> comes from
    /// <see cref="ProgramOrphans.ReadEvidence"/>, read after the uninstall.
    /// </summary>
    public static IReadOnlyList<Leftover> Find(DesktopProgram removed, LeftoverRoots roots, IReadOnlyCollection<string> referenced,
        Func<string, string?>? shortcutTarget = null)
    {
        shortcutTarget ??= ShortcutTarget;
        var folders = Folders(removed).Where(f => MayOffer(f, roots, referenced)).ToList();
        if (folders.Count == 0) return [];
        // A folder inside another one found here is moved with it.
        folders = folders.Where(f => !folders.Any(o => Inside(f, o))).ToList();
        var found = new List<Leftover>();
        foreach (var f in folders)
        {
            if (Directory.Exists(f) && SafeDelete.HasNoLinks(f)) found.Add(new Leftover(LeftoverKind.Folder, f, Size(f)));
        }
        foreach (var shortcutFolder in roots.ShortcutFolders.Where(Directory.Exists))
        {
            foreach (var lnk in Shortcuts(shortcutFolder))
            {
                if (shortcutTarget(lnk) is not { Length: > 0 } target) continue;
                if (folders.Any(f => target.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) && SafeDelete.HasNoLinks(lnk))
                    found.Add(new Leftover(LeftoverKind.Shortcut, lnk, 0));
            }
        }
        return found;
    }

    /// <summary>Leftovers of several removed programs; each shortcut is read once, not once per program.</summary>
    public static IReadOnlyList<(DesktopProgram Program, Leftover Leftover)> FindAll(IEnumerable<DesktopProgram> removed, LeftoverRoots roots,
        IReadOnlyCollection<string> referenced)
    {
        var targets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Cached(string lnk) => targets.TryGetValue(lnk, out var t) ? t : targets[lnk] = ShortcutTarget(lnk);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return removed.SelectMany(p => Find(p, roots, referenced, Cached).Select(l => (p, l))).Where(x => seen.Add(x.l.Path)).ToList();
    }

    internal static IEnumerable<string> Shortcuts(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 4,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static string? ShortcutTarget(string lnk)
    {
        try
        {
            return ShellLinkFile.Read(lnk).Target;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Bytes of the files in the folder, without following links.</summary>
    public static long Size(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="folder"/>.</summary>
    private static bool Inside(string path, string folder) => path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
