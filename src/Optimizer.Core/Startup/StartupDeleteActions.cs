using System.Xml.Linq;
using Optimizer.Core.Actions;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Startup;

/// <summary>What the exported XML of a scheduled task says about how it runs.</summary>
public static class ScheduledTaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // taskschd.h TASK_LOGON_TYPE
    private const int S4U = 2, InteractiveToken = 3, Group = 4, ServiceAccount = 5;

    private static readonly HashSet<string> ServiceAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", "S-1-5-19", "S-1-5-20", "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE", @"NT AUTHORITY\SYSTEM",
        @"NT AUTHORITY\LOCAL SERVICE", @"NT AUTHORITY\NETWORK SERVICE",
    };

    private static XElement? Root(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root is { } r && r.Name.Namespace == Ns ? r : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The logon type and account to register the task with again (Task Scheduler's TASK_LOGON_TYPE). Null when the
    /// task keeps a password (Password, InteractiveTokenOrPassword) or the XML cannot be read: such a task cannot be put
    /// back without the password, so it is never deleted here.
    /// </summary>
    public static (int LogonType, string? User)? Principal(string xml)
    {
        if (Root(xml) is not { } root) return null;
        var principal = root.Element(Ns + "Principals")?.Element(Ns + "Principal");
        if (principal is null) return (InteractiveToken, null);
        var logon = principal.Element(Ns + "LogonType")?.Value.Trim();
        if (logon is "Password" or "InteractiveTokenOrPassword") return null;
        if (principal.Element(Ns + "GroupId")?.Value.Trim() is { Length: > 0 } group) return (Group, group);
        var user = principal.Element(Ns + "UserId")?.Value.Trim() is { Length: > 0 } u ? u : null;
        return logon switch
        {
            "S4U" => (S4U, user),
            "ServiceAccount" => (ServiceAccount, user),
            _ when user is not null && ServiceAccounts.Contains(user) => (ServiceAccount, user),
            _ => (InteractiveToken, user),
        };
    }

    /// <summary>The first program the task runs with its arguments, for the confirmation; null for a COM handler.</summary>
    /// <summary>The program the task's first Exec action starts, without its arguments; null when it has none.</summary>
    public static string? Program(string xml) =>
        Root(xml)?.Element(Ns + "Actions")?.Element(Ns + "Exec")?.Element(Ns + "Command")?.Value is { Length: > 0 } command ? command : null;

    public static string? Command(string xml)
    {
        var exec = Root(xml)?.Element(Ns + "Actions")?.Element(Ns + "Exec");
        if (exec?.Element(Ns + "Command")?.Value is not { Length: > 0 } command) return null;
        return exec.Element(Ns + "Arguments")?.Value is { Length: > 0 } args ? $"\"{command}\" {args}" : command;
    }
}

/// <summary>
/// Deletes a scheduled task of another program, as Autoruns does, after its XML went into the backup: undo registers it
/// again under the same path and account. Tasks that keep a password are left alone (they could not come back).
/// </summary>
public sealed class ScheduledTaskDeleteAction : TweakAction
{
    /// <summary>The task's full path, "\Vendor\Updater".</summary>
    public string Path { get; init; } = "";

    public override string TargetKey => $"taskdelete:{Path}".ToLowerInvariant();
    public override TargetText TargetLabel(ActionContext c) => TargetText.Of("target.taskDelete", (Path.StartsWith('\\') ? "" : "\\") + Path);

    public override StoredValue Desired(ActionContext c) => StoredValue.Missing;

    /// <summary>The XML as data; null (unsupported) when the task keeps a password and so could not be registered again.</summary>
    public override StoredValue? Read(ActionContext c)
    {
        if (c.Tasks.ExportXml(Path) is not { } xml) return StoredValue.Missing;
        return ScheduledTaskXml.Principal(xml) is null ? null : new StoredValue(true, "taskXml", xml);
    }

    public override void Apply(ActionContext c) => c.Tasks.Delete(Path);

    public override void Restore(ActionContext c, StoredValue original)
    {
        if (original is { Existed: true, Data: { } xml }) c.Tasks.Register(Path, xml);
        else if (c.Tasks.ExportXml(Path) is not null) c.Tasks.Delete(Path);
    }

    // The preview shows what the task runs, not its XML.
    protected override string Show(ActionContext c, StoredValue value) =>
        !value.Existed ? value.Display : value.Data is { } xml ? ScheduledTaskXml.Command(xml) ?? Path : Path;
}

/// <summary>
/// Deletes a file from a Startup folder (a shortcut, or a program or script placed there), as Autoruns does, after its
/// bytes went into the backup: undo writes the same file back. Only files in a Startup folder and up to
/// <see cref="MaxBytes"/>; the folder may belong to the signed-in user, so delete and restore go through
/// <see cref="SafeDelete"/>, which refuses a path that was redirected by a link.
/// </summary>
public sealed class StartupFileDeleteAction : TweakAction
{
    /// <summary>A shortcut is a few KB; a larger file is not copied into the backup.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    public string Path { get; init; } = "";

    /// <summary>A file in a user's Startup folder is that account's (its backup too); one in the machine's folder is the PC's.</summary>
    public override string TargetKey => (IsMachineStartupFile(Path) ? $"startupfile:{Path}" : $"startupfile:user:{Path}").ToLowerInvariant();

    private static bool IsMachineStartupFile(string path) =>
        System.IO.Path.GetDirectoryName(path) is { } folder && Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) is { Length: > 0 } common
        && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(folder), System.IO.Path.TrimEndingDirectorySeparator(common), StringComparison.OrdinalIgnoreCase);
    public override TargetText TargetLabel(ActionContext c) => TargetText.Verbatim(Path);

    public override StoredValue Desired(ActionContext c) => StoredValue.Missing;

    /// <summary>Base64 of the file; null (unsupported) for a path outside a Startup folder or a file too large to back up.</summary>
    public override StoredValue? Read(ActionContext c)
    {
        if (!IsStartupFile(Path)) return null;
        if (!File.Exists(Path)) return StoredValue.Missing;
        // Read only a real file at this very path, through the handle that was checked: through a link the elevated app
        // would copy some other file into the backup, and undo would then write it into the user's folder.
        return SafeDelete.ReadFile(Path, MaxBytes) is { } bytes ? new StoredValue(true, "file", Convert.ToBase64String(bytes)) : null;
    }

    public override void Apply(ActionContext c)
    {
        if (!IsStartupFile(Path)) throw new InvalidOperationException($"{Path} is not in a Startup folder");
        SafeDelete.DeleteFile(Path);
    }

    public override void Restore(ActionContext c, StoredValue original)
    {
        if (!IsStartupFile(Path)) throw new InvalidOperationException($"{Path} is not in a Startup folder");
        if (original is not { Existed: true, Data: { } data })
        {
            if (File.Exists(Path)) SafeDelete.DeleteFile(Path);
            return;
        }
        using var stream = SafeDelete.CreateNew(Path);
        stream.Write(Convert.FromBase64String(data));
    }

    // The preview names the file, not its bytes.
    protected override string Show(ActionContext c, StoredValue value) => value.Existed ? System.IO.Path.GetFileName(Path) : value.Display;

    /// <summary>
    /// A file directly in a Startup folder: the machine's (ProgramData) or a user's (AppData\Roaming). Checked again before
    /// every write, so a changed backup cannot make undo write somewhere else.
    /// </summary>
    public static bool IsStartupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.Contains("..", StringComparison.Ordinal)) return false;
        var folder = System.IO.Path.GetDirectoryName(path);
        return folder is not null && System.IO.Path.GetFileName(path) is { Length: > 0 } name && !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
               && folder.EndsWith(@"\Microsoft\Windows\Start Menu\Programs\Startup", StringComparison.OrdinalIgnoreCase);
    }
}
