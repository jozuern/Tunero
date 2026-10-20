using System.Security.AccessControl;
using System.Security.Principal;

namespace Optimizer.Core.Platform;

/// <summary>
/// Decides whether a program may run elevated: the file and every folder above it must be owned by Administrators,
/// SYSTEM or TrustedInstaller, and no one else may change, replace or add files there (a DLL placed next to a program is
/// loaded by it). Anything else is started as the signed-in user instead.
/// </summary>
public static class TrustedPath
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private const FileSystemRights Changing =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool IsTrusted(IdentityReference? sid) => sid == Admins || sid == LocalSystem || sid == TrustedInstaller;

    public static bool IsAdminOnlyWritable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsOnLocalFixedDrive(full) || !File.Exists(full) || !SafeDelete.HasNoLinks(full)) return false;
            if (!Check(new FileInfo(full).GetAccessControl())) return false;
            return FolderChainIsAdminOnly(new DirectoryInfo(Path.GetDirectoryName(full)!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// A program like <see cref="IsAdminOnlyWritable"/>, except that folders above its own folder may let others add new
    /// entries, as <c>C:\ProgramData</c> lets every user create folders. Adding an entry cannot change, rename or replace
    /// an existing one (that takes delete rights, which are still refused), so the path keeps pointing to the same
    /// admin-only folder. The program's own folder stays strict: a DLL added next to a program is loaded by it. For package
    /// managers installed under <c>C:\ProgramData</c> (Chocolatey).
    /// </summary>
    public static bool IsAdminOnlyBelowSharedFolders(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsOnLocalFixedDrive(full) || !File.Exists(full) || !SafeDelete.HasNoLinks(full)) return false;
            if (!Check(new FileInfo(full).GetAccessControl())) return false;
            return FolderIsAdminOnlyBelowSharedFolders(new DirectoryInfo(Path.GetDirectoryName(full)!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// A folder in which only Administrators, SYSTEM or TrustedInstaller can add or change files, while folders above it
    /// may let others add new entries (as <see cref="IsAdminOnlyBelowSharedFolders"/>): Chocolatey's root and the folders
    /// it loads scripts and modules from, under <c>C:\ProgramData</c>.
    /// </summary>
    public static bool IsAdminOnlyFolderBelowSharedFolders(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var dir = new DirectoryInfo(full);
            return IsOnLocalFixedDrive(full) && dir.Exists && FolderIsAdminOnlyBelowSharedFolders(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool FolderIsAdminOnlyBelowSharedFolders(DirectoryInfo folder)
    {
        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0 || !Check(folder.GetAccessControl())) return false;
        for (var dir = folder.Parent; dir?.Parent is not null; dir = dir.Parent)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || !Check(dir.GetAccessControl(), Replacing)) return false;
        }
        return true;
    }

    /// <summary>A folder (for example a PATH entry) in which only Administrators, SYSTEM or TrustedInstaller can add or change files.</summary>
    public static bool IsAdminOnlyWritableFolder(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var dir = new DirectoryInfo(full);
            return IsOnLocalFixedDrive(full) && dir.Exists && FolderChainIsAdminOnly(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// A path on a local fixed drive with a drive letter. On a share or mapped drive the server reports owner and
    /// permissions, so they prove nothing, and the folder walk ends at the share root.
    /// </summary>
    public static bool IsOnLocalFixedDrive(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.Length < 3 || fullPath[1] != ':') return false;
        return new DriveInfo(fullPath[..1]).DriveType == DriveType.Fixed;
    }

    private static bool FolderChainIsAdminOnly(DirectoryInfo start)
    {
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            // The drive root lets users create folders by default; what matters is that they cannot change the folders below.
            if (dir.Parent is null) break;
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || !Check(dir.GetAccessControl())) return false;
        }
        return true;
    }

    /// <summary>GENERIC_ALL and GENERIC_WRITE: some installers write these generic bits into an entry instead of file rights.</summary>
    private const int GenericWriting = 0x10000000 | 0x40000000;

    /// <summary>
    /// Rights on a folder above the program's own that could change what the path leads to: deleting or renaming what is
    /// in it, or taking the folder over. Creating new entries (write data, append data, attributes, GENERIC_WRITE) is left out.
    /// </summary>
    private const FileSystemRights Replacing =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>GENERIC_ALL alone: it includes delete and permission rights, GENERIC_WRITE does not.</summary>
    private const int GenericAll = 0x10000000;

    private static bool Check(FileSystemSecurity security, FileSystemRights forbidden)
    {
        if (!IsTrusted(security.GetOwner(typeof(SecurityIdentifier)))) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsTrusted(rule.IdentityReference)) continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (rule.IdentityReference == CreatorOwner) continue;
            if ((rule.FileSystemRights & forbidden) != 0 || ((int)rule.FileSystemRights & GenericAll) != 0) return false;
        }
        return true;
    }

    private static bool Check(FileSystemSecurity security)
    {
        if (!IsTrusted(security.GetOwner(typeof(SecurityIdentifier)))) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsTrusted(rule.IdentityReference)) continue;
            // Inherit-only entries apply to what is created inside, not to this file or folder.
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            // CREATOR OWNER rights only reach files someone creates, which the other rules decide.
            if (rule.IdentityReference == CreatorOwner) continue;
            if ((rule.FileSystemRights & Changing) != 0 || ((int)rule.FileSystemRights & GenericWriting) != 0) return false;
        }
        return true;
    }
}
