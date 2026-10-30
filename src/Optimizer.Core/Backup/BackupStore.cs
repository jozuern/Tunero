using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Optimizer.Core.Actions;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Backup;

public sealed class BackupEntry
{
    public required string TargetKey { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// The target as a label key and its parts, so the Changes page shows it in the UI language. Entries written by 1.0.0
    /// and older have only the English <see cref="Description"/>.
    /// </summary>
    public TargetText? Label { get; init; }

    /// <summary>The true original: written once, at the first apply (first-original rule).</summary>
    public required StoredValue Original { get; init; }

    /// <summary>The value after our last apply. Undo skips targets whose value no longer matches (Windows reset it).</summary>
    public StoredValue? Applied { get; set; }

    /// <summary>
    /// JSON of the exact action that changed this target (for example the DNS action of one adapter). Undo uses it when
    /// the tweak no longer expands to this target (adapter not connected, game list changed), so no original is lost.
    /// </summary>
    public string? Action { get; set; }
}

/// <summary>
/// Version of the backup and pending-undo files. Undo must work on every file an earlier release wrote: the property
/// names, the action type names (<see cref="TweakAction"/>), the target keys and the stored value encodings of format 1
/// are frozen, and tests restore a saved file of every action type (tests/Optimizer.Core.Tests/Fixtures/backups). A
/// change that older versions would misread gets a new number; a file of a newer format is never applied or undone.
/// </summary>
public static class BackupFormat
{
    public const int Current = 1;
}

public sealed class TweakBackup
{
    /// <summary>Files written before the format was recorded are format 1.</summary>
    public int Format { get; init; } = BackupFormat.Current;

    public required string TweakId { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastApplied { get; set; } = DateTimeOffset.Now;
    public int WindowsBuild { get; init; }
    public string AppVersion { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
    public List<BackupEntry> Entries { get; init; } = [];

    /// <summary>Windows version ("26300.9550") of the last apply, to tell a reset by an update from other causes.</summary>
    public string? AppliedOnVersion { get; set; }

    /// <summary>
    /// Windows version on which a scan last found this change still in place after an update (null until then, cleared by
    /// the next apply): a reset on this version is not the update's doing.
    /// </summary>
    public string? KeptOnVersion { get; set; }

    /// <summary>Set for tweaks that take effect after a restart; cleared once the boot time changes.</summary>
    public DateTimeOffset? PendingRestartSince { get; set; }

    /// <summary>Files exported before the change (BCD store, power scheme).</summary>
    public List<string> Exports { get; init; } = [];

    /// <summary>
    /// SID of the Windows account whose settings the backup holds (values in the user's registry part, accessibility
    /// shortcuts). Null for changes to the whole PC. Such a backup is kept and shown only for that account.
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// JSON of the tweak definition for tweaks built at runtime (startup entries, services, features, device and game
    /// tweaks, fixes). Lets undo work after a restart, when the page that built the tweak is not open.
    /// </summary>
    public string? Definition { get; set; }

    public BackupEntry? Entry(string targetKey) => Entries.FirstOrDefault(e => e.TargetKey == targetKey);
}

/// <summary>An undo whose restored values take effect after the next restart (<see cref="BackupStore.GetPendingUndo"/>).</summary>
public sealed class PendingUndo
{
    public int Format { get; init; } = BackupFormat.Current;

    public required string TweakId { get; init; }

    /// <summary>Time of the undo; a boot after it means the restored values are in effect.</summary>
    public DateTimeOffset Since { get; init; } = DateTimeOffset.Now;

    /// <summary>The restored entries: their originals are what the system will have after the restart.</summary>
    public List<BackupEntry> Entries { get; init; } = [];
}

/// <summary>
/// JSON backups in %ProgramData%\Tunero\backups. The folder is locked to Administrators + SYSTEM (no inheritance):
/// an elevated app restoring values from user-writable files would be a privilege-escalation path. The data folder is
/// shared by every Windows account, so a backup of one account's own settings (HKCU values, accessibility shortcuts)
/// lives in backups\users\&lt;SID&gt; and is seen only when the app runs for that account; otherwise a second account
/// would take the first account's originals for its own.
/// </summary>
/// <summary>A backup file whose originals cannot be read now, and why.</summary>
public sealed record UnreadableBackup(string File, string Reason);

/// <summary>What one read of the backup folders found (<see cref="BackupStore.ReadAll"/>).</summary>
public sealed record BackupFolderRead(IReadOnlyList<TweakBackup> Own, IReadOnlySet<string> OtherAccounts, IReadOnlyList<UnreadableBackup> Unreadable);

public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly bool _secure;
    private readonly string? _userSid;

    public BackupStore(string root, bool secure = true, string? userSid = null)
    {
        Root = root;
        _secure = secure;
        _userSid = userSid;
        // Locked before the subfolders are created, so new folders inherit the locked permissions. A folder that cannot be
        // made safe is refused: restoring values from files a standard user can change would be a way to admin rights.
        if (secure && !SecureFolder.Lock(root)) throw new UnauthorizedAccessException($"{root} cannot be locked to administrators");
        Directory.CreateDirectory(BackupFolder);
        Directory.CreateDirectory(HistoryFolder);
        Directory.CreateDirectory(PendingUndoFolder);
        Directory.CreateDirectory(ExportFolder);
    }

    public static string DefaultRoot => Platform.DataPaths.Root;

    public string Root { get; }
    public string BackupFolder => Path.Combine(Root, "backups");
    public string HistoryFolder => Path.Combine(Root, "backups", "history");
    public string PendingUndoFolder => Path.Combine(Root, "backups", "pending-undo");
    public string ExportFolder => Path.Combine(Root, "exports");

    /// <summary>Backups of the current account's own settings; null when no account is known (tests, tools).</summary>
    public string? UserFolder => _userSid is { } sid ? Path.Combine(BackupFolder, "users", Sanitize(sid)) : null;

    /// <summary>A target in the user's registry part or a per-user system setting (accessibility shortcut).</summary>
    public static bool IsPerUser(string targetKey)
    {
        if (targetKey.StartsWith("spi:", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = targetKey.Split(':', 3);
        return parts.Length == 3 && parts[1].Equals("user", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The backup file of a tweak. Ids that need replaced characters get a short hash of the real id, so two ids never
    /// share a file; a file under the older plain name is still found.
    /// </summary>
    private static string FileIn(string folder, string tweakId)
    {
        var name = Sanitize(tweakId);
        if (name == tweakId) return Path.Combine(folder, name + ".json");
        var hashed = Path.Combine(folder, $"{name}-{IdHash(tweakId)}.json");
        var legacy = Path.Combine(folder, name + ".json");
        return !File.Exists(hashed) && File.Exists(legacy) && Read(legacy)?.TweakId == tweakId ? legacy : hashed;
    }

    /// <summary>The existing backup file of the tweak: the account's own first, then the one for the whole PC.</summary>
    private string FileFor(string tweakId)
    {
        if (UserFolder is { } user && FileIn(user, tweakId) is var own && (File.Exists(own) || File.Exists(own + DamagedSuffix))) return own;
        return FileIn(BackupFolder, tweakId);
    }

    /// <summary>A backup in the shared folder that belongs to another account (written by a version that recorded the owner).</summary>
    private bool OfAnotherAccount(TweakBackup? b) => b?.Owner is { } owner && _userSid is { } sid && !owner.Equals(sid, StringComparison.OrdinalIgnoreCase);

    private static string IdHash(string id) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();

    /// <summary>Suffix of a backup file that could not be read: kept for the user, never overwritten.</summary>
    public const string DamagedSuffix = ".damaged";

    /// <summary>
    /// The tweak's backup, or null when there is none. A file that exists but stays locked (another process is replacing
    /// it), or a damaged file that cannot be moved aside, throws <see cref="BackupUnreadableException"/>: treating it as
    /// "no backup" would let an apply record the changed values as originals (and overwrite the file that still holds
    /// them), or let an undo report success without restoring anything.
    /// </summary>
    public TweakBackup? Get(string tweakId)
    {
        var file = FileFor(tweakId);
        if (!File.Exists(file)) return null;
        // A file not owned by Administrators was not written by this app (it sets the owner on every save): never read,
        // and never "no backup" either, which would let an apply overwrite it or an undo report success.
        if (!Trusted(file)) throw new BackupUnreadableException(file, BackupUnreadableException.Reason.Untrusted);
        var (backup, damaged) = TryRead(file);
        if (damaged && !Quarantine(file)) throw new BackupUnreadableException(file);
        if (!damaged && backup is null) throw new BackupUnreadableException(file);
        if (backup is { Format: > BackupFormat.Current }) throw new BackupUnreadableException(file, BackupUnreadableException.Reason.NewerFormat);
        return OfAnotherAccount(backup) ? null : backup;
    }

    /// <summary>
    /// A backup of the tweak in the shared folder that belongs to another Windows account (written by an older version):
    /// it holds that account's originals, so this account must not apply the tweak and overwrite it.
    /// </summary>
    public bool HeldByAnotherAccount(string tweakId)
    {
        var shared = FileIn(BackupFolder, tweakId);
        return File.Exists(shared) && Trusted(shared) && OfAnotherAccount(TryRead(shared).Backup);
    }

    /// <summary>True when the tweak has a backup of this account, also when the file is locked right now (undo then reports it).</summary>
    public bool Exists(string tweakId)
    {
        try
        {
            return Get(tweakId) is not null;
        }
        catch (BackupUnreadableException)
        {
            return true;
        }
    }

    /// <summary>
    /// The tweak's backup file exists but cannot be read (cut off by a power loss, edited by hand). It still holds the
    /// only copy of the originals, so the tweak must not be applied again until the user removes it.
    /// </summary>
    public bool IsDamaged(string tweakId) => File.Exists(DamagedFile(tweakId));

    public string DamagedFile(string tweakId) => FileFor(tweakId) + DamagedSuffix;

    public IReadOnlyList<TweakBackup> All() => ReadAll().Own;

    /// <summary>
    /// One read of every backup file: this account's backups (newest first), the tweaks whose backup in the shared
    /// folder belongs to another account, and the files whose originals cannot be read (damaged, locked, not owned by
    /// Administrators, or written by a newer version), which an undo of everything must report instead of passing over.
    /// </summary>
    public BackupFolderRead ReadAll()
    {
        var list = new List<TweakBackup>();
        var others = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new List<UnreadableBackup>();
        var folders = UserFolder is { } user && Directory.Exists(user) ? new[] { user, BackupFolder } : [BackupFolder];
        foreach (var file in folders.SelectMany(f => Directory.EnumerateFiles(f, "*.json")))
        {
            if (!Trusted(file))
            {
                unreadable.Add(new UnreadableBackup(file, "not owned by Administrators or SYSTEM, so it is not read"));
                continue;
            }
            var (backup, damaged) = TryRead(file);
            if (backup is { Format: > BackupFormat.Current })
            {
                Log.Warn("backup", $"{file} was written by a newer version of the app (format {backup.Format}); it is left as it is");
                unreadable.Add(new UnreadableBackup(file, $"written by a newer version of the app (format {backup.Format})"));
                continue;
            }
            if (backup is not null && OfAnotherAccount(backup)) others.Add(backup.TweakId);
            else if (backup is not null && !list.Any(b => b.TweakId == backup.TweakId)) list.Add(backup);
            else if (damaged) unreadable.Add(new UnreadableBackup(Quarantine(file) ? file + DamagedSuffix : file, "damaged; it still holds the original values"));
            else if (backup is null) unreadable.Add(new UnreadableBackup(file, "locked by another program"));
        }
        // Damaged files moved aside earlier still hold originals that no undo can restore.
        foreach (var file in folders.SelectMany(f => Directory.EnumerateFiles(f, "*" + DamagedSuffix)))
            if (!unreadable.Any(u => u.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
                unreadable.Add(new UnreadableBackup(file, "damaged; it still holds the original values"));
        return new BackupFolderRead(list.OrderByDescending(b => b.LastApplied).ToList(), others, unreadable);
    }

    /// <summary>
    /// A backup with any per-user target goes to the account's folder and records the account; a copy in the shared
    /// folder (from an older version, or saved before its first per-user entry) is removed. A backup without per-user
    /// targets (left after a partial undo) goes to the shared folder, and the account's copy is removed: it would be
    /// read first and bring back entries that were already restored.
    /// </summary>
    /// A backup in the shared folder that belongs to another account is never deleted or overwritten.
    /// </summary>
    public void Save(TweakBackup backup)
    {
        var shared = FileIn(BackupFolder, backup.TweakId);
        if (UserFolder is { } user && backup.Entries.Any(e => IsPerUser(e.TargetKey)))
        {
            backup.Owner = _userSid;
            Directory.CreateDirectory(user);
            WriteDurably(FileIn(user, backup.TweakId), JsonSerializer.Serialize(backup, Json));
            if (File.Exists(shared) && !HeldByAnotherAccount(backup.TweakId)) File.Delete(shared);
            return;
        }
        // Only changes to the whole PC are left: they concern every account, so the backup belongs to none.
        backup.Owner = null;
        if (HeldByAnotherAccount(backup.TweakId)) throw new IOException($"{shared} holds the original values of another Windows account; it is not overwritten.");
        WriteDurably(shared, JsonSerializer.Serialize(backup, Json));
        if (UserFolder is { } folder && FileIn(folder, backup.TweakId) is var own && File.Exists(own)) File.Delete(own);
    }

    /// <summary>
    /// Written to a temporary file, flushed to the disk and then moved over the old file: a power loss right after a
    /// boot configuration change leaves either the old or the new backup, never an empty one.
    /// </summary>
    private void WriteDurably(string file, string json)
    {
        // The data folder trusts only files owned by Administrators or SYSTEM. The default owner is set at start, but where
        // that failed (UAC off, Administrator protection) a file owned by the account would be ignored on the next read
        // and removed on the next start, with the originals in it: the owner is set on the new file before it replaces
        // the old one, or the write fails and the old backup stays.
        Platform.DurableFile.WriteAllText(file, json, tmp =>
        {
            if (_secure && !SecureFolder.IsOwnedByAdmins(tmp) && !SecureFolder.MakeOwnedByAdmins(tmp))
                throw new IOException($"{file} could not be given to the Administrators group; the change is not made.");
        });
    }

    /// <summary>Moves a damaged backup aside (it still holds the originals). False when it could not be moved.</summary>
    private static bool Quarantine(string file)
    {
        try
        {
            var target = file + DamagedSuffix;
            if (File.Exists(target)) target = $"{file}-{DateTime.Now:yyyyMMdd-HHmmss}{DamagedSuffix}";
            File.Move(file, target);
            Log.Error("backup", $"unreadable backup moved to {target}; the tweak cannot be applied again until it is removed");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("backup", $"unreadable backup {file} could not be moved aside: {ex.Message}");
            return false;
        }
    }

    /// <summary>After a complete undo the record moves to history (kept for support and a manual way back).</summary>
    public void Archive(string tweakId)
    {
        var file = FileFor(tweakId);
        if (!File.Exists(file)) return;
        File.Move(file, Path.Combine(HistoryFolder, $"{Sanitize(tweakId)}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
        // A second copy of this account's backup (written by a version that kept both) would surface as a change that is
        // still applied.
        var shared = FileIn(BackupFolder, tweakId);
        if (shared != file && File.Exists(shared) && Trusted(shared) && TryRead(shared).Backup is { } other && !OfAnotherAccount(other))
            File.Move(shared, Path.Combine(HistoryFolder, $"{Sanitize(tweakId)}-{DateTime.Now:yyyyMMdd-HHmmss}-shared.json"), overwrite: true);
        PruneHistory();
    }

    /// <summary>Undone changes kept in the history folder; older ones are deleted so it does not grow with every undo.</summary>
    public const int KeptHistory = 500;

    /// <summary>Keeps the newest <see cref="KeptHistory"/> files. A file that cannot be deleted now is tried at the next undo.</summary>
    private void PruneHistory()
    {
        try
        {
            foreach (var old in new DirectoryInfo(HistoryFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeptHistory))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("backup", $"history not pruned: {ex.Message}");
        }
    }

    // ---------------- undo that takes effect after a restart ----------------

    private string PendingUndoFile(string tweakId) => Path.Combine(PendingUndoFolder, Sanitize(tweakId) + ".json");

    /// <summary>
    /// An undone change whose restored value takes effect only after a restart (memory compression). Kept apart from the
    /// backups, so the Changes page, Undo all and drift detection do not see it as a change that is still applied.
    /// </summary>
    public PendingUndo? GetPendingUndo(string tweakId)
    {
        var file = PendingUndoFile(tweakId);
        if (!File.Exists(file) || !Trusted(file)) return null;
        try
        {
            var pending = JsonSerializer.Deserialize<PendingUndo>(File.ReadAllText(file), Json);
            return pending is { Format: > BackupFormat.Current } ? null : pending;
        }
        catch (Exception ex)
        {
            Log.Error("backup", $"unreadable pending undo {file}", ex);
            return null;
        }
    }

    public void SavePendingUndo(PendingUndo pending) => WriteDurably(PendingUndoFile(pending.TweakId), JsonSerializer.Serialize(pending, Json));

    public void ClearPendingUndo(string tweakId)
    {
        var file = PendingUndoFile(tweakId);
        if (File.Exists(file)) File.Delete(file);
    }

    private bool Trusted(string file)
    {
        if (!_secure) return true;
        if (SecureFolder.IsOwnedByAdmins(file)) return true;
        Log.Warn("backup", $"ignored backup file not owned by Administrators/SYSTEM: {file}");
        return false;
    }

    private static TweakBackup? Read(string file) => TryRead(file).Backup;

    /// <summary>
    /// Damaged: the content cannot be parsed. A file that is only locked for a moment is not damaged: it is read again
    /// a few times, and (null, false) means it stayed locked.
    /// </summary>
    private static (TweakBackup? Backup, bool Damaged) TryRead(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var backup = JsonSerializer.Deserialize<TweakBackup>(File.ReadAllText(file), Json);
                return backup is { TweakId.Length: > 0 } ? (backup, false) : (null, true);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                Log.Error("backup", $"unreadable backup {file}", ex);
                return (null, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex is FileNotFoundException or DirectoryNotFoundException) return (null, false);
                if (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                    continue;
                }
                Log.Warn("backup", $"backup {file} not readable right now: {ex.Message}");
                return (null, false);
            }
        }
    }

    private static string Sanitize(string id) => string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
}

/// <summary>
/// A backup file exists but cannot be used: locked by another process, written by a newer version of the app, or not
/// written by this app (not owned by Administrators). Nothing may rely on "no backup".
/// </summary>
public sealed class BackupUnreadableException(string file, BackupUnreadableException.Reason reason = BackupUnreadableException.Reason.Locked)
    : IOException(reason switch
    {
        Reason.NewerFormat => $"The backup file {file} was written by a newer version of Tunero. Update Tunero to undo this change.",
        Reason.Untrusted => $"The backup file {file} was not written by Tunero (it is not owned by Administrators) and is not used.",
        _ => $"The backup file {file} cannot be read right now. Try again in a moment.",
    })
{
    public enum Reason { Locked, NewerFormat, Untrusted }

    public string File { get; } = file;
    public Reason Why { get; } = reason;
}

public static class SecureFolder
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// Makes the data folder safe before the elevated app writes or reads anything in it (log, settings, backups).
    /// A standard user can create C:\ProgramData\Tunero before the first elevated start: as a junction (every
    /// write would land where the link points) or as a real folder with files in it (planted backups and settings,
    /// and a handle kept open could keep adding files after the permissions change). A link is removed; a folder not
    /// owned by Administrators or SYSTEM was not created by this app and is deleted, never adopted. Returns false
    /// when the folder cannot be made safe; the caller must not use it then.
    /// </summary>
    public static bool PrepareRoot(string folder)
    {
        try
        {
            if (!Lock(folder)) return false;
            var info = new DirectoryInfo(folder);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && IsOwnedByAdmins(info) && IsLocked(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("backup", $"data folder {folder} cannot be secured", ex);
            return false;
        }
    }

    private static DirectorySecurity LockedSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(Admins);
        return security;
    }

    /// <summary>The folder's own permissions: protected from inheritance and only Administrators and SYSTEM allowed.</summary>
    public static bool IsLocked(DirectoryInfo folder)
    {
        try
        {
            var security = folder.GetAccessControl();
            if (!security.AreAccessRulesProtected) return false;
            return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .All(r => r.AccessControlType == AccessControlType.Deny || r.IdentityReference == Admins || r.IdentityReference == System);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Administrators + SYSTEM full control, inheritance from ProgramData removed, for the folder and everything in it.
    /// A missing folder is created with these permissions in one step, so no other account can add anything while it is
    /// new. An existing folder that Administrators or SYSTEM do not own (created by another account) is moved aside and
    /// deleted, never adopted. Below a trusted folder the permissions are locked first; then links and entries that
    /// Administrators or SYSTEM do not own are deleted (an entry is never given a new owner), and the rest falls back to
    /// the inherited, locked permissions. Returns false when the folder cannot be made safe.
    /// </summary>
    public static bool Lock(string folder)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Path.Exists(folder) && ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !IsOwnedByAdmins(new DirectoryInfo(folder))))
                {
                    Log.Warn("backup", $"{folder} was a link or not created by an administrator; removed");
                    RemoveForeign(folder);
                }
                if (!Path.Exists(folder))
                {
                    try
                    {
                        new DirectoryInfo(folder).Create(LockedSecurity());
                    }
                    catch (IOException) when (Path.Exists(folder))
                    {
                        continue; // created by someone else in between: check its owner again
                    }
                }
                var info = new DirectoryInfo(folder);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !IsOwnedByAdmins(info)) continue;

                // Trusted root: lock it before looking at its content, so nothing new can be added while it is cleaned.
                info.SetAccessControl(LockedSecurity());
                RemoveLinks(folder);
                RemoveUntrusted(folder);
                foreach (var dir in info.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    var s = new DirectorySecurity();
                    s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    dir.SetAccessControl(s);
                }
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var s = new FileSecurity();
                    s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    file.SetAccessControl(s);
                }
                return true;
            }
            Log.Error("backup", $"could not lock {folder}: another account keeps creating it");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("backup", $"could not lock {folder}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes a folder (or link) another account created: renamed to a random name first, so the account cannot reuse
    /// the path while it is deleted, then deleted without following links.
    /// </summary>
    private static void RemoveForeign(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(folder); // removes the link itself
            return;
        }
        var aside = Path.Combine(Path.GetDirectoryName(folder)!, $"{Path.GetFileName(folder)}.untrusted-{Guid.NewGuid():N}");
        Directory.Move(folder, aside);
        DeleteTree(aside);
    }

    /// <summary>
    /// Deletes junctions and symbolic links at <paramref name="folder"/> and below (only the links, never their
    /// targets), so nothing the elevated app writes or reads there can be redirected.
    /// </summary>
    public static void RemoveLinks(string folder)
    {
        if (!Path.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
        {
            Log.Warn("backup", $"{folder} was a link; replaced by a real folder");
            Directory.Delete(folder); // removes the link itself
            return;
        }
        var stack = new Stack<string>([folder]);
        while (stack.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Log.Warn("backup", $"removed link {entry.FullName}");
                    entry.Delete(); // a directory link is removed without touching its target
                }
                else if (entry is DirectoryInfo d)
                {
                    stack.Push(d.FullName);
                }
            }
        }
    }

    /// <summary>
    /// Deletes files and folders below <paramref name="folder"/> that neither Administrators nor SYSTEM own. Everything
    /// the elevated app creates is owned by Administrators; anything else was put there by another account and is
    /// never read (a planted backup could make Undo write chosen registry values as administrator). Call after
    /// <see cref="RemoveLinks"/>.
    /// </summary>
    public static void RemoveUntrusted(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        while (stack.Count > 0)
        {
            foreach (var entry in stack.Pop().EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; // RemoveLinks handles links
                if (IsOwnedByAdmins(entry))
                {
                    if (entry is DirectoryInfo trusted) stack.Push(trusted);
                    continue;
                }
                Log.Warn("backup", $"removed {entry.FullName}: not created by an administrator");
                if (entry is DirectoryInfo d)
                {
                    DeleteTree(d.FullName);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
        }
    }

    /// <summary>Deletes the folder and everything in it: links are removed first (never their targets), read-only flags cleared.</summary>
    internal static void DeleteTree(string folder)
    {
        RemoveLinks(folder);
        if (!Directory.Exists(folder)) return;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(folder, recursive: true);
    }

    public static bool IsOwnedByAdmins(string file) => IsOwnedByAdmins(new FileInfo(file));

    /// <summary>Makes the Administrators group the owner of a file this elevated app wrote. False when Windows refused.</summary>
    public static bool MakeOwnedByAdmins(string file)
    {
        try
        {
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            security.SetOwner(Admins);
            info.SetAccessControl(security);
            return IsOwnedByAdmins(info);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            Log.Warn("backup", $"owner of {file} not set to Administrators: {ex.Message}");
            return false;
        }
    }

    public static bool IsOwnedByAdmins(FileSystemInfo entry)
    {
        try
        {
            var owner = entry switch
            {
                DirectoryInfo d => d.GetAccessControl().GetOwner(typeof(SecurityIdentifier)),
                FileInfo f => f.GetAccessControl().GetOwner(typeof(SecurityIdentifier)),
                _ => null,
            };
            return owner == Admins || owner == System;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
