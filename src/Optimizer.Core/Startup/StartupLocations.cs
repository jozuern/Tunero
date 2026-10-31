using System.Text;
using Optimizer.Core.Actions;

namespace Optimizer.Core.Startup;

/// <summary>
/// Autostart locations that are only listed (Autoruns-style): RunOnce, Active Setup, the Load value, Boot Execute,
/// KnownDLLs, Winsock providers, print monitors, LSA packages, network providers, codecs and DirectShow filters, WMI
/// event consumers, the Command Processor AutoRun, Explorer add-ons (icon overlays, shell service objects, copy hooks),
/// per-user context menu handlers, file type hijacks, SilentProcessExit monitors, credential providers and AppCertDlls.
/// The program that owns an entry changes it; the page shows where it is and whether the file is signed.
/// </summary>
public sealed partial class StartupScanner
{
    private static string Root(Hive hive) => hive == Hive.Machine ? "HKLM" : "HKCU";

    /// <summary>
    /// A DLL named without a folder ("localspl.dll", "msv1_0") is loaded from System32; for the 32-bit registrations
    /// (WOW6432Node, the 32-bit Winsock catalog) from SysWOW64, and a path into System32 is redirected there too.
    /// </summary>
    public static string? SystemFile(string? name, bool addDll = true, bool wow64 = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // These files are loaded by Windows itself (boot, LSA, spooler, Winsock): the machine's variables, not the user's.
        var expanded = Platform.PathExpander.Machine.Expand(name.Trim().Trim('"'));
        var system32 = Environment.SystemDirectory;
        var syswow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        if (Path.IsPathRooted(expanded))
            return wow64 && syswow64.Length > 0 && expanded.StartsWith(system32 + "\\", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(syswow64, expanded[(system32.Length + 1)..])
                : expanded;
        if (addDll && !Path.HasExtension(expanded)) expanded += ".dll";
        return Path.Combine(wow64 && syswow64.Length > 0 ? syswow64 : system32, expanded);
    }

    private static string[] Strings(object? value, bool split) => value switch
    {
        string[] multi => multi,
        string single when split => single.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
        string single => [single],
        _ => [],
    };

    /// <summary>RunOnce runs a command once at the next sign-in; RunOnceEx is used by installers.</summary>
    public IEnumerable<StartupEntry> RunOnceEntries()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Hive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                     (Hive.Machine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
                     (Hive.User, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                 })
        {
            using var key = Open(hive, path);
            foreach (var name in key?.GetValueNames().Where(n => n.Length > 0) ?? [])
            {
                var command = key!.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                yield return new StartupEntry(StartupKind.RunOnce, name, command, CommandLine.ImagePath(command, _userExpand), $@"{Root(hive)}\{path}", hive, null, $"runonce:{hive}:{path}:{name}");
            }
        }
        const string ex = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx";
        using var exKey = Open(Hive.Machine, ex);
        foreach (var sub in exKey?.GetSubKeyNames() ?? [])
        {
            using var s = exKey!.OpenSubKey(sub);
            foreach (var name in s?.GetValueNames().Where(n => n.Length > 0) ?? [])
            {
                var command = s!.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                yield return new StartupEntry(StartupKind.RunOnce, $@"{sub}\{name}", command, CommandLine.ImagePath(command?.Split('|')[0]), $@"HKLM\{ex}\{sub}", Hive.Machine, null, $"runonceex:{sub}:{name}");
            }
        }
    }

    /// <summary>Active Setup: a command each user runs once at sign-in until the component version is recorded for that user.</summary>
    public IEnumerable<StartupEntry> ActiveSetup()
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Active Setup\Installed Components", @"SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components" })
        {
            using var key = Open(Hive.Machine, path);
            foreach (var id in key?.GetSubKeyNames() ?? [])
            {
                using var c = key!.OpenSubKey(id);
                if (c?.GetValue("StubPath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is not { Length: > 0 } stub) continue;
                if (c.GetValue("IsInstalled") is int installed && installed == 0) continue;
                var name = c.GetValue("")?.ToString() is { Length: > 0 } n ? ResolveIndirect(n) ?? n : id;
                yield return new StartupEntry(StartupKind.ActiveSetup, name, stub, CommandLine.ImagePath(stub, _userExpand), $@"HKLM\{path}\{id}", Hive.Machine, null, $"activesetup:{path}:{id}");
            }
        }
    }

    /// <summary>The old "Load" and "Run" values under Windows NT\CurrentVersion\Windows of the user; normally empty.</summary>
    public IEnumerable<StartupEntry> LoadValues()
    {
        const string path = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";
        using var key = Open(Hive.User, path);
        foreach (var name in new[] { "Load", "Run" })
        foreach (var item in Strings(key?.GetValue(name), split: true))
            yield return new StartupEntry(StartupKind.LoadValue, $"{name}: {Path.GetFileName(item)}", item, CommandLine.ImagePath(item, _userExpand), $@"HKCU\{path}\{name}", Hive.User, null, $"load:{name}:{item}")
            {
                Suspicious = true,
            };
    }

    public const string BootExecuteDefault = "autocheck autochk *";

    /// <summary>Native programs the Session Manager runs before Windows starts (normally only the disk check).</summary>
    public IEnumerable<StartupEntry> BootExecute()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Session Manager";
        using var key = Open(Hive.Machine, path);
        foreach (var name in new[] { "BootExecute", "SetupExecute", "Execute", "S0InitialCommand" })
        foreach (var line in Strings(key?.GetValue(name), split: false).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "autocheck autochk *": autocheck marks a program that may be skipped; the program is the next word.
            var program = parts.Length > 1 && parts[0].Equals("autocheck", StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[0];
            yield return new StartupEntry(StartupKind.BootExecute, $"{name}: {program}", line, SystemFile(Path.HasExtension(program) ? program : program + ".exe"),
                $@"HKLM\{path}\{name}", Hive.Machine, null, $"bootexecute:{name}:{line}")
            {
                Suspicious = !(name == "BootExecute" && string.Equals(line.Trim(), BootExecuteDefault, StringComparison.OrdinalIgnoreCase)),
            };
        }
    }

    /// <summary>DLLs Windows maps from System32 for every process; a file other than Windows' own here is loaded into everything.</summary>
    public IEnumerable<StartupEntry> KnownDlls()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs";
        using var key = Open(Hive.Machine, path);
        foreach (var name in key?.GetValueNames().Where(n => n.Length > 0 && !n.StartsWith("DllDirectory", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            if (key!.GetValue(name) is not string { Length: > 0 } file) continue;
            yield return new StartupEntry(StartupKind.KnownDll, name, file, SystemFile(file), $@"HKLM\{path}", Hive.Machine, null, $"knowndll:{name}");
        }
    }

    /// <summary>Winsock transport and name space providers: DLLs loaded into every program that uses the network.</summary>
    public IEnumerable<StartupEntry> WinsockProviders()
    {
        foreach (var (catalog, value) in new[]
                 {
                     (@"Protocol_Catalog9\Catalog_Entries", "PackedCatalogItem"), (@"Protocol_Catalog9\Catalog_Entries64", "PackedCatalogItem"),
                     (@"NameSpace_Catalog5\Catalog_Entries", "LibraryPath"), (@"NameSpace_Catalog5\Catalog_Entries64", "LibraryPath"),
                 })
        {
            var path = $@"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\{catalog}";
            using var key = Open(Hive.Machine, path);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in key?.GetSubKeyNames() ?? [])
            {
                using var e = key!.OpenSubKey(id);
                var dll = e?.GetValue(value, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) switch
                {
                    byte[] packed => PackedPath(packed),
                    string s => s,
                    _ => null,
                };
                if (dll is null || !seen.Add(dll)) continue;
                var name = e!.GetValue("DisplayString")?.ToString() is { Length: > 0 } d ? ResolveIndirect(d) ?? d : Path.GetFileName(dll);
                // Catalog_Entries is the 32-bit catalog (loaded into 32-bit programs), Catalog_Entries64 the 64-bit one.
                var wow64 = !catalog.EndsWith("64", StringComparison.Ordinal);
                yield return new StartupEntry(StartupKind.WinsockProvider, name, dll, SystemFile(dll, wow64: wow64), $@"HKLM\{path}", Hive.Machine, null, $"winsock:{catalog}:{dll}");
            }
        }
    }

    /// <summary>
    /// PackedCatalogItem starts with the provider DLL path as a zero-terminated ANSI string. Read as Latin-1, so a path
    /// with umlauts keeps its letters instead of turning into "?".
    /// </summary>
    public static string? PackedPath(byte[] packed)
    {
        var end = Array.IndexOf(packed, (byte)0);
        if (end <= 0) return null;
        var text = Encoding.Latin1.GetString(packed, 0, end).Trim();
        return text.Length > 0 && !text.Any(char.IsControl) ? text : null;
    }

    /// <summary>Print monitors: DLLs the print spooler (a SYSTEM service) loads.</summary>
    public IEnumerable<StartupEntry> PrintMonitors()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Print\Monitors";
        using var key = Open(Hive.Machine, path);
        foreach (var name in key?.GetSubKeyNames() ?? [])
        {
            using var m = key!.OpenSubKey(name);
            if (m?.GetValue("Driver") is not string { Length: > 0 } dll) continue;
            yield return new StartupEntry(StartupKind.PrintMonitor, name, dll, SystemFile(dll), $@"HKLM\{path}\{name}", Hive.Machine, null, $"printmonitor:{name}");
        }
    }

    /// <summary>Authentication, notification and security packages that LSASS loads (sign-in and password handling).</summary>
    public IEnumerable<StartupEntry> LsaPackages()
    {
        foreach (var (path, values) in new[]
                 {
                     (@"SYSTEM\CurrentControlSet\Control\Lsa", new[] { "Authentication Packages", "Notification Packages", "Security Packages" }),
                     (@"SYSTEM\CurrentControlSet\Control\Lsa\OSConfig", new[] { "Security Packages" }),
                 })
        {
            using var key = Open(Hive.Machine, path);
            foreach (var value in values)
            foreach (var item in Strings(key?.GetValue(value), split: true).Select(i => i.Trim().Trim('"')).Where(i => i.Length > 0))
                yield return new StartupEntry(StartupKind.LsaPackage, $"{value}: {item}", item, SystemFile(item), $@"HKLM\{path}\{value}", Hive.Machine, null, $"lsa:{path}:{value}:{item}");
        }
    }

    /// <summary>Network providers in the order Windows asks them (Windows network, WebDAV, and those of VPN or file sharing software).</summary>
    public IEnumerable<StartupEntry> NetworkProviders()
    {
        using var order = Open(Hive.Machine, @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order");
        foreach (var name in (order?.GetValue("ProviderOrder")?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = $@"SYSTEM\CurrentControlSet\Services\{name}\NetworkProvider";
            using var p = Open(Hive.Machine, path);
            var dll = p?.GetValue("ProviderPath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            var display = p?.GetValue("Name")?.ToString() is { Length: > 0 } n ? n : name;
            yield return new StartupEntry(StartupKind.NetworkProvider, display, dll, SystemFile(dll), $@"HKLM\{path}", Hive.Machine, null, $"netprovider:{name}");
        }
    }

    /// <summary>Audio and video codecs registered for the older multimedia interfaces (loaded by programs that play media).</summary>
    public IEnumerable<StartupEntry> Codecs()
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Drivers32" })
        {
            using var key = Open(Hive.Machine, path);
            foreach (var name in key?.GetValueNames().Where(n => n.Length > 0) ?? [])
            {
                // Only file names: the key also holds DWORD settings (MidisrvTransferComplete).
                if (key!.GetValue(name) is not string { Length: > 0 } dll) continue;
                var wow64 = path.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);
                yield return new StartupEntry(StartupKind.Codec, name, dll, SystemFile(dll, wow64: wow64), $@"HKLM\{path}", Hive.Machine, null, $"codec:{path}:{name}");
            }
        }
    }

    /// <summary>
    /// Permanent WMI event consumers that run a command or a script (root\subscription). Windows itself rarely uses
    /// them and malware does, so each one is marked for a look.
    /// </summary>
    public static IEnumerable<StartupEntry> WmiConsumers()
    {
        var list = new List<StartupEntry>();
        // A damaged WMI repository can hang a query: give up after 15 seconds instead of blocking the page.
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true, Rewindable = false };
        using var searcher = new System.Management.ManagementObjectSearcher(@"root\subscription", "SELECT * FROM __EventConsumer", options);
        using var results = searcher.Get();
        foreach (var o in results.OfType<System.Management.ManagementObject>())
        {
            using (o)
            {
                var cls = o["__CLASS"]?.ToString() ?? "";
                if (cls is not ("CommandLineEventConsumer" or "ActiveScriptEventConsumer")) continue;
                var name = o["Name"]?.ToString() ?? cls;
                var command = cls == "CommandLineEventConsumer"
                    ? o["CommandLineTemplate"]?.ToString() ?? o["ExecutablePath"]?.ToString()
                    : o["ScriptFileName"]?.ToString() is { Length: > 0 } file ? file
                    : o["ScriptText"]?.ToString() is { Length: > 0 } text ? "(script) " + text.ReplaceLineEndings(" ")[..Math.Min(120, text.ReplaceLineEndings(" ").Length)]
                    : "(script)";
                list.Add(new StartupEntry(StartupKind.WmiConsumer, name, command, CommandLine.ImagePath(command, Platform.PathExpander.Machine.Expand), $@"WMI root\subscription\{cls}", Hive.Machine, null, $"wmi:{cls}:{name}")
                {
                    Suspicious = true,
                });
            }
        }
        return list;
    }

    /// <summary>
    /// The Command Processor's AutoRun runs a command every time cmd.exe starts (also inside scripts and installers).
    /// Windows sets none, so each one is marked for a look.
    /// </summary>
    public IEnumerable<StartupEntry> CommandAutoRuns()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Hive.Machine, @"SOFTWARE\Microsoft\Command Processor"), (Hive.Machine, @"SOFTWARE\WOW6432Node\Microsoft\Command Processor"),
                     (Hive.User, @"Software\Microsoft\Command Processor"),
                 })
        {
            using var key = Open(hive, path);
            if (key?.GetValue("AutoRun", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is not { Length: > 0 } command) continue;
            yield return new StartupEntry(StartupKind.CommandAutoRun, "cmd.exe AutoRun", command, CommandLine.ImagePath(command, hive == Hive.User ? _userExpand : Platform.PathExpander.Machine.Expand),
                $@"{Root(hive)}\{path}\AutoRun", hive, null, $"cmdautorun:{hive}:{path}")
            {
                Suspicious = true,
            };
        }
    }

    /// <summary>
    /// Explorer add-ons loaded with every Explorer window: icon overlay handlers, shell service objects (loaded at
    /// Explorer's start), shared task schedulers and copy hooks (called for every folder copy or move).
    /// </summary>
    public IEnumerable<StartupEntry> ExplorerAddOns()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<StartupEntry> FromSubKeys(string path, string what)
        {
            using var key = Open(Hive.Machine, path);
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                using var sub = key!.OpenSubKey(name);
                var clsid = sub?.GetValue("")?.ToString();
                if (string.IsNullOrEmpty(clsid) && Guid.TryParse(name, out _)) clsid = name;
                if (Entry(name.Trim(), clsid, path, what) is { } e) yield return e;
            }
        }
        IEnumerable<StartupEntry> FromValues(string path, string what, bool clsidIsName)
        {
            using var key = Open(Hive.Machine, path);
            foreach (var name in key?.GetValueNames().Where(n => n.Length > 0) ?? [])
                if (Entry(clsidIsName ? NameOf(name) ?? name : name, clsidIsName ? name : key!.GetValue(name)?.ToString(), path, what) is { } e) yield return e;
        }
        StartupEntry? Entry(string name, string? clsid, string path, string what)
        {
            if (clsid is null || !Guid.TryParse(clsid, out var g)) return null;
            var id = g.ToString("B").ToUpperInvariant();
            if (!seen.Add($"{what}:{id}")) return null;
            var dll = ComServer(id, path.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase));
            return new StartupEntry(StartupKind.ExplorerAddOn, $"{what}: {name}", dll ?? id, dll, $@"HKLM\{path}", Hive.Machine, null, $"exploreraddon:{what}:{id}");
        }
        string? NameOf(string clsid)
        {
            if (!Guid.TryParse(clsid, out var g)) return null;
            using var cls = Open(Hive.Machine, $@"SOFTWARE\Classes\CLSID\{g.ToString("B").ToUpperInvariant()}");
            return cls?.GetValue("")?.ToString() is { Length: > 0 } n ? n : null;
        }

        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers" })
            foreach (var e in FromSubKeys(path, "Icon overlay")) yield return e;
        foreach (var path in new[] { @"SOFTWARE\Classes\Directory\shellex\CopyHookHandlers", @"SOFTWARE\Classes\Directory\Background\shellex\CopyHookHandlers" })
            foreach (var e in FromSubKeys(path, "Copy hook")) yield return e;
        foreach (var e in FromValues(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad", "Shell service object", clsidIsName: false)) yield return e;
        foreach (var e in FromValues(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler", "Shared task scheduler", clsidIsName: true)) yield return e;
    }

    /// <summary>
    /// Context menu handlers registered for this account only (HKCU\Software\Classes). A program installed for one
    /// user puts them here without administrator rights; they load into Explorer like the machine's own.
    /// </summary>
    public IEnumerable<StartupEntry> UserShellExtensions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handlerRoot in HandlerRoots)
        {
            using var key = Open(Hive.User, $@"Software\Classes\{handlerRoot}");
            foreach (var handler in key?.GetSubKeyNames() ?? [])
            {
                using var h = key!.OpenSubKey(handler);
                var clsid = h?.GetValue("")?.ToString();
                if (string.IsNullOrEmpty(clsid) && Guid.TryParse(handler, out _)) clsid = handler;
                if (clsid is null || !Guid.TryParse(clsid, out var g)) continue;
                var id = g.ToString("B").ToUpperInvariant();
                if (!seen.Add(id)) continue;
                // The account's own registration wins over the machine's: read its server first.
                using var server = Open(Hive.User, $@"Software\Classes\CLSID\{id}\InprocServer32");
                var dll = server?.GetValue("", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is { Length: > 0 } own
                    ? CommandLine.FilePath(own, _userExpand)
                    : ComServer(id);
                yield return new StartupEntry(StartupKind.ShellExtension, handler, dll ?? id, dll, $@"HKCU\Software\Classes\{handlerRoot}\{handler}", Hive.User, null, $"usershellext:{id}");
            }
        }
    }

    /// <summary>What Windows runs to open a program, a batch file or a script: "%1" %* by default.</summary>
    private static readonly (string Type, string Default)[] FileTypes =
    [
        ("exefile", "\"%1\" %*"), ("comfile", "\"%1\" %*"), ("batfile", "\"%1\" %*"), ("cmdfile", "\"%1\" %*"), ("piffile", "\"%1\" %*"),
    ];

    /// <summary>
    /// The open command of programs and batch files. Malware changes it so that it runs first, with the real program as
    /// an argument. Listed when it differs from Windows' default, or when the account has its own (HKCU), which Windows
    /// never writes.
    /// </summary>
    public IEnumerable<StartupEntry> FileTypeHijacks()
    {
        foreach (var (type, expected) in FileTypes)
        {
            foreach (var (hive, path) in new[] { (Hive.Machine, $@"SOFTWARE\Classes\{type}\shell\open\command"), (Hive.User, $@"Software\Classes\{type}\shell\open\command") })
            {
                using var key = Open(hive, path);
                if (key?.GetValue("", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() is not { Length: > 0 } command) continue;
                if (hive == Hive.Machine && string.Equals(command.Trim(), expected, StringComparison.OrdinalIgnoreCase)) continue;
                yield return new StartupEntry(StartupKind.FileTypeHijack, $"{type} open", command, CommandLine.ImagePath(command, hive == Hive.User ? _userExpand : Platform.PathExpander.Machine.Expand),
                    $@"{Root(hive)}\{path}", hive, null, $"filetype:{hive}:{type}")
                {
                    Suspicious = true,
                };
            }
        }
    }

    /// <summary>
    /// SilentProcessExit monitors: a program Windows starts whenever the named program ends. A debugging aid that
    /// Windows does not set up by itself, and a known way to start something unseen.
    /// </summary>
    public IEnumerable<StartupEntry> SilentProcessExits()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit";
        using var key = Open(Hive.Machine, path);
        foreach (var exe in key?.GetSubKeyNames() ?? [])
        {
            using var sub = key!.OpenSubKey(exe);
            if (sub?.GetValue("MonitorProcess")?.ToString() is not { Length: > 0 } monitor) continue;
            yield return new StartupEntry(StartupKind.SilentProcessExit, exe, monitor, CommandLine.ImagePath(monitor, Platform.PathExpander.Machine.Expand), $@"HKLM\{path}\{exe}", Hive.Machine, null, $"silentexit:{exe}")
            {
                Suspicious = true,
            };
        }
    }

    /// <summary>Credential providers: the tiles of the sign-in screen (password, PIN, Windows Hello, smart cards, and those of security software).</summary>
    public IEnumerable<StartupEntry> CredentialProviders()
    {
        foreach (var path in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers",
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Provider Filters",
                 })
        {
            using var key = Open(Hive.Machine, path);
            foreach (var id in key?.GetSubKeyNames() ?? [])
            {
                if (!Guid.TryParse(id, out var g)) continue;
                using var sub = key!.OpenSubKey(id);
                var name = sub?.GetValue("")?.ToString() is { Length: > 0 } n ? n : id;
                var dll = ComServer(g.ToString("B").ToUpperInvariant());
                yield return new StartupEntry(StartupKind.CredentialProvider, name, dll ?? id, dll, $@"HKLM\{path}\{id}", Hive.Machine, null, $"credprovider:{path}:{id}");
            }
        }
    }

    /// <summary>AppCertDlls: DLLs loaded into every process that starts a program. Windows sets none.</summary>
    public IEnumerable<StartupEntry> AppCertDlls()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";
        using var key = Open(Hive.Machine, path);
        foreach (var name in key?.GetValueNames().Where(n => n.Length > 0) ?? [])
        {
            if (key!.GetValue(name) is not string { Length: > 0 } dll) continue;
            yield return new StartupEntry(StartupKind.AppCertDll, name, dll, SystemFile(dll), $@"HKLM\{path}", Hive.Machine, null, $"appcert:{name}")
            {
                Suspicious = true,
            };
        }
    }

    /// <summary>DirectShow filters: codecs and splitters that media players load (the "Codecs" tab of Autoruns).</summary>
    public IEnumerable<StartupEntry> DirectShowFilters()
    {
        const string instance = @"SOFTWARE\Classes\CLSID\{083863F1-70DE-11D0-BD40-00A0C911CE86}\Instance";
        foreach (var path in new[] { instance, instance.Replace(@"SOFTWARE\Classes", @"SOFTWARE\Classes\WOW6432Node", StringComparison.Ordinal) })
        {
            using var key = Open(Hive.Machine, path);
            foreach (var id in key?.GetSubKeyNames() ?? [])
            {
                if (!Guid.TryParse(id, out var g)) continue;
                using var sub = key!.OpenSubKey(id);
                var name = sub?.GetValue("FriendlyName")?.ToString() is { Length: > 0 } n ? n : id;
                // Filters for 32-bit players are registered under WOW6432Node, their classes too.
                var dll = ComServer(g.ToString("B").ToUpperInvariant(), wow64: path != instance);
                if (dll is null) continue;
                yield return new StartupEntry(StartupKind.Codec, name, dll, dll, $@"HKLM\{path}\{id}", Hive.Machine, null, $"directshow:{path}:{id}");
            }
        }
    }
}
