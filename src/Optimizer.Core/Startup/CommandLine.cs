namespace Optimizer.Core.Startup;

/// <summary>Turns autostart command lines and service image paths into the file that actually runs.</summary>
public static class CommandLine
{
    private static readonly string[] Hosts = ["rundll32.exe", "rundll32"];

    /// <summary>
    /// Built-in Windows programs that run scripts or other code given on the command line. They are signed by
    /// Microsoft, so the signature says nothing about what an autostart entry does with them.
    /// </summary>
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "powershell_ise.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "regsvr32.exe",
        "rundll32.exe", "conhost.exe", "forfiles.exe", "msbuild.exe", "installutil.exe", "regasm.exe", "regsvcs.exe", "certutil.exe",
        "bitsadmin.exe", "wmic.exe", "hh.exe", "msxsl.exe", "bash.exe", "wsl.exe",
    };

    /// <summary>
    /// Interpreters other programs install (Python, Node.js, Java, Perl and similar): like a script host they run the
    /// script or inline code they are given, so an elevated command trusts them only with admin-only scripts.
    /// </summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "node.exe", "java.exe", "javaw.exe", "perl.exe", "ruby.exe", "rubyw.exe",
        "php.exe", "tclsh.exe", "wish.exe", "autohotkey.exe", "autohotkey32.exe", "autohotkey64.exe", "autohotkeyu32.exe",
        "autohotkeyu64.exe", "autoit3.exe", "autoit3_x64.exe",
    };

    /// <summary>
    /// Built-in Windows programs that start another program, installer or file given on the command line (explorer.exe
    /// C:\Users\Public\x.exe, msiexec /i, pcalua -a). Without arguments, or with arguments that only name Windows' own
    /// files, they are what they seem; Windows' shell entry is plain explorer.exe.
    /// </summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "pcalua.exe", "msiexec.exe", "cmstp.exe", "odbcconf.exe", "mavinject.exe", "control.exe", "schtasks.exe",
        "regedit.exe", "reg.exe", "msdt.exe", "presentationhost.exe", "sc.exe", "at.exe", "runonce.exe",
    };

    /// <summary>True when the image is a script host (PowerShell, cmd, mshta and similar), with or without ".exe".</summary>
    public static bool IsScriptHost(string? imagePath) => FileName(imagePath) is { } name && ScriptHosts.Contains(name);

    private static bool IsLauncher(string? imagePath) => FileName(imagePath) is { } name && Launchers.Contains(name);

    /// <summary>The file name, with ".exe" added when it has no extension ("powershell" runs powershell.exe).</summary>
    private static string? FileName(string? imagePath)
    {
        if (imagePath is not { Length: > 0 }) return null;
        var name = Path.GetFileName(imagePath.Trim().Trim('"'));
        return name.Length == 0 ? null : Path.HasExtension(name) ? name : name + ".exe";
    }

    /// <summary>
    /// rundll32 entry points that start another program, file or URL given as an argument (signed proxies for any
    /// program).
    /// </summary>
    private static readonly HashSet<string> ProxyExports = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExec_RunDLL", "ShellExec_RunDLLA", "ShellExec_RunDLLW", "FileProtocolHandler", "OpenURL", "OpenURLA", "RouteTheCall",
        "Control_RunDLL", "Control_RunDLLA", "Control_RunDLLW", "Control_RunDLLAsUser", "Control_RunDLLAsUserW", "RegisterOCX",
        "LaunchINFSection", "LaunchINFSectionEx", "InstallHinfSection", "InstallHinfSectionA", "InstallHinfSectionW", "LaunchApplication",
    };

    /// <summary>
    /// A script host whose command could run anything: inline code (powershell -enc, cmd /c with no file), chained
    /// commands, a URL, another script host as an argument (cmd /c powershell -enc), or a script outside
    /// System32/SysWOW64. A host that only runs files from those protected Windows folders (Windows' own tasks: cmd /c
    /// %SystemRoot%\system32\x.cmd) is not flagged; its files are signed or writable only by TrustedInstaller. The host
    /// is the program the line starts (rundll32 itself, not its DLL); a rundll32 line is flagged when it uses an entry
    /// point that starts something else, or passes further files or URLs.
    /// </summary>
    public static bool RunsUnverifiedScript(string? command, Func<string, string>? expand = null) =>
        RunsUntrustedCode(command, IsInProtectedSystemFolder, expand);

    /// <summary>
    /// Like <see cref="RunsUnverifiedScript"/>, with the caller's rule for a trusted file (an elevated uninstall trusts
    /// files only administrators can change). A script with a path that is not trusted, or with "..", or an alternate
    /// data stream, is untrusted.
    /// </summary>
    /// <param name="interpreters">Also treat <see cref="Interpreters"/> (Python, Node.js and similar) as script hosts.</param>
    public static bool RunsUntrustedCode(string? command, Func<string, bool> trusted, Func<string, string>? expand = null, Func<string, bool>? exists = null,
        bool interpreters = false)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        expand ??= Environment.ExpandEnvironmentVariables;
        if (Program(command, expand, exists) is not var (host, args)) return false;
        bool Trusted(string file) => !file.Contains("..", StringComparison.Ordinal) && !HasStream(file) && trusted(file);
        if (IsLauncher(host)) return LauncherStartsSomethingElse(args, Trusted);
        if (!IsScriptHost(host) && !(interpreters && FileName(host) is { } name && Interpreters.Contains(name))) return false;
        if (IsRundll(host)) return RundllStartsSomethingElse(args, Trusted);
        // A line break ends one command and starts the next in every host.
        if (args.IndexOfAny(['&', '|', '^', '`', ';', '\r', '\n']) >= 0 || args.Contains("://", StringComparison.Ordinal)) return true;
        var tokens = Tokens(args).ToList();
        // Another script host, or a launcher (cmd /c msiexec /i x.msi), as an argument runs whatever it is given.
        if (tokens.Any(t => IsScriptHost(t) || IsLauncher(t))) return true;
        // PowerShell inline code: -Command / -EncodedCommand / -ec and their accepted prefixes ("-c", "-enc", "/e").
        if (Path.GetFileName(host).StartsWith("p", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Any(t => t.Length >= 2 && t[0] is '-' or '/' && IsPrefixOf(t[1..], "command", "encodedcommand", "ec"))) return true;
            // Without -File the arguments are a command (-Command is the default): a subexpression, script block, splat or
            // call after a System32 path still runs code. With -File they are passed to the script as text.
            if (!tokens.Any(t => t.Length >= 2 && t[0] is '-' or '/' && IsPrefixOf(t[1..], "file")) && args.IndexOfAny(['$', '(', '{', '@']) >= 0)
                return true;
        }
        var files = tokens.Select(t => t.Split(',')[0]).Where(IsPathToken).ToList();
        if (files.Count == 0) return true; // inline code or a bare name looked up on PATH
        return !files.All(Trusted);
    }

    /// <summary>
    /// rundll32 runs the DLL's entry point; the DLL itself is judged by its signature (<see cref="ImagePath"/>). Flagged
    /// only when the entry point is a known proxy or more files or URLs follow (rundll32 x.dll,Entry C:\Users\x.exe).
    /// </summary>
    private static bool RundllStartsSomethingElse(string args, Func<string, bool> trusted)
    {
        var tokens = Tokens(args).Where(t => !t.StartsWith('/') && !t.StartsWith('-')).ToList();
        if (tokens.Count == 0) return false;
        var parts = tokens[0].Split(',', 2);
        if (parts.Length == 2 && ProxyExports.Contains(parts[1].Trim())) return true;
        if (args.Contains("://", StringComparison.Ordinal)) return true;
        return tokens.Skip(1).Any(t => IsPathToken(t) && !trusted(t));
    }

    /// <summary>
    /// A launcher (<see cref="Launchers"/>) is flagged when it is given a URL, a script host, or a file that is not
    /// trusted; switches, GUIDs and shell: names are left alone.
    /// </summary>
    private static bool LauncherStartsSomethingElse(string args, Func<string, bool> trusted)
    {
        if (args.Contains("://", StringComparison.Ordinal)) return true;
        var tokens = Tokens(args).Select(t => t.Split(',')[0]).ToList();
        if (tokens.Any(t => IsScriptHost(t))) return true;
        return tokens.Any(t => IsPathToken(t) && !trusted(t));
    }

    /// <summary>
    /// A token that names a file by path: with a backslash, and also with forward slashes, which Windows accepts in paths
    /// as well (C:/Users/Public/x.vbs). Switches such as /c or /i have their only slash first.
    /// </summary>
    private static bool IsPathToken(string token) =>
        token.Contains('\\') || (token.Length > 2 && token[1] == ':' && token[2] == '/') || token.IndexOf('/', 1) > 0;

    /// <summary>
    /// The path as Windows reads it: forward slashes as backslashes, "." and doubled separators removed. A path with ".."
    /// or a short (8.3) name is refused before this: both can lead out of a folder that looks protected.
    /// </summary>
    private static string Normalized(string file)
    {
        var path = file.Replace('/', '\\');
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Folders below System32 and SysWOW64 that standard users can write to (print color profiles, task files, crash
    /// dumps, fax and machine key stores): a script there is not one of Windows' own.
    /// </summary>
    private static readonly string[] UserWritableSystemFolders =
    [
        @"spool\drivers\color", @"spool\PRINTERS", @"spool\SERVERS", "Tasks", "Tasks_Migrated", @"com\dmp", "FxsTmp",
        @"Microsoft\Crypto\RSA\MachineKeys", "LogFiles", @"config\systemprofile",
    ];

    /// <summary>
    /// A file in System32 or SysWOW64 (or a subfolder only administrators can write to), without ".." and without an
    /// alternate data stream (System32\Tasks:x.vbs is a stream on a folder users can write to, not a file below System32).
    /// </summary>
    public static bool IsInProtectedSystemFolder(string file)
    {
        // "~": a short name (spool\PRINTE~1) can stand for a folder users can write to.
        if (file.Contains("..", StringComparison.Ordinal) || file.Contains('~') || HasStream(file)) return false;
        file = Normalized(file);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var root in new[] { Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64") })
        {
            if (!file.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = file[(root.Length + 1)..];
            return !UserWritableSystemFolders.Any(w => relative.StartsWith(w + "\\", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    /// <summary>A colon after the drive letter: an alternate data stream (x.txt:y.vbs). Safe for one-character tokens (a lone backslash).</summary>
    private static bool HasStream(string file) => file.Length > 2 && file.IndexOf(':', 2) >= 0;

    /// <summary>Folders directly below the Windows folder that standard users can write to.</summary>
    private static readonly string[] UserWritableWindowsFolders = ["Temp", "Tasks", "Tracing", @"Registration\CRMLog", @"debug\WIA"];

    /// <summary>
    /// A file in a folder below the Windows folder (or System32 and SysWOW64) that standard users can write to: Temp,
    /// Tasks, Tracing, the print spool and color folders and similar. Being in the Windows folder says nothing there.
    /// </summary>
    public static bool IsInUserWritableWindowsFolder(string file)
    {
        file = Normalized(file);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!file.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var relative = file[(windows.Length + 1)..];
        if (UserWritableWindowsFolders.Any(w => relative.StartsWith(w + "\\", StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var system in new[] { "System32", "SysWOW64" })
            if (relative.StartsWith(system + "\\", StringComparison.OrdinalIgnoreCase))
                return UserWritableSystemFolders.Any(w => relative[(system.Length + 1)..].StartsWith(w + "\\", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static bool IsPrefixOf(string s, params string[] words) =>
        words.Any(w => w.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Arguments split at spaces, quoted parts kept together (quotes removed).</summary>
    private static IEnumerable<string> Tokens(string args)
    {
        var rest = args.Trim();
        while (rest.Length > 0)
        {
            var (first, tail) = SplitFirst(rest);
            if (first.Length > 0) yield return first;
            if (tail.Length >= rest.Length) yield break;
            rest = tail;
        }
    }

    /// <summary>
    /// Image file of a command line: quoted or unquoted first token, environment variables expanded, kernel prefixes
    /// (\SystemRoot\, \??\, System32\drivers) resolved, rundll32 lines resolved to their DLL. Null when empty.
    /// </summary>
    public static string? ImagePath(string? command, Func<string, string>? expand = null, Func<string, bool>? exists = null)
    {
        if (Parse(command, expand, exists) is not var (first, rest, system, ex)) return null;
        if (IsRundll(first) && RundllDll(rest) is { Length: > 0 } dll) return Qualify(dll, system, ex);
        return Qualify(first, system, ex);
    }

    /// <summary>
    /// The program a command line starts and its arguments, for running it (uninstall commands): unlike
    /// <see cref="ImagePath"/>, a rundll32 line gives rundll32 itself with the DLL and entry point as arguments. Null
    /// when empty.
    /// </summary>
    public static (string File, string Arguments)? Program(string? command, Func<string, string>? expand = null, Func<string, bool>? exists = null)
    {
        if (Parse(command, expand, exists) is not var (first, rest, system, ex)) return null;
        return (Qualify(first, system, ex), rest);
    }

    /// <summary>The DLL of a rundll32 line; switches such as "/d" (Windows' own Autochk task) come before it.</summary>
    private static string? RundllDll(string args) => Tokens(args).FirstOrDefault(t => !t.StartsWith('/') && !t.StartsWith('-'))?.Split(',')[0];

    private static bool IsRundll(string file) => Hosts.Any(h => Path.GetFileName(file).Equals(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>First token (the program, unquoted paths with spaces resolved) and the rest of the line.</summary>
    private static (string First, string Tail, string Windows, Func<string, bool> Exists)? Parse(string? command, Func<string, string>? expand, Func<string, bool>? exists)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        expand ??= Environment.ExpandEnvironmentVariables;
        exists ??= File.Exists;
        var s = expand(command.Trim());
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(system, s[12..]);
        if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || s.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(system, s);

        var (first, rest) = SplitFirst(s);
        // An unquoted path with spaces is resolved like CreateProcess and the service manager do: the shortest prefix that
        // exists, each also with ".exe" (C:\Program.exe before C:\Program Files\App\app.exe). Taking the longest would
        // show the intended program while Windows runs a planted C:\Tools\My.exe for C:\Tools\My App\app.exe.
        if (!s.StartsWith('"') && !exists(first))
        {
            var parts = s.Split(' ');
            for (var n = 1; n <= parts.Length; n++)
            {
                var candidate = string.Join(' ', parts.Take(n));
                if (exists(candidate) || exists(candidate + ".exe"))
                {
                    first = exists(candidate) ? candidate : candidate + ".exe";
                    rest = string.Join(' ', parts.Skip(n));
                    break;
                }
            }
        }
        return (first, rest, system, exists);
    }

    private static (string First, string Tail) SplitFirst(string s)
    {
        s = s.TrimStart();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end < 0 ? (s[1..], "") : (s[1..end], s[(end + 1)..].Trim());
        }
        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    private static string Qualify(string file, string windows, Func<string, bool> exists)
    {
        if (Path.IsPathRooted(file)) return file;
        // Bare names (for example "ctfmon.exe") are found in System32 or the Windows folder like CreateProcess does, then
        // in Windows' own folders of the PATH ("powershell" lives in System32\WindowsPowerShell\v1.0).
        var system = Path.Combine(windows, "System32");
        foreach (var dir in new[] { system, windows, Path.Combine(system, "Wbem"), Path.Combine(system, @"WindowsPowerShell\v1.0"), Path.Combine(system, "OpenSSH") })
        {
            var candidate = Path.Combine(dir, file);
            if (exists(candidate)) return candidate;
            if (!Path.HasExtension(file) && exists(candidate + ".exe")) return candidate + ".exe";
        }
        return file;
    }
}
