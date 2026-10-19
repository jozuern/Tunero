using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Optimizer.Core.Interop;

namespace Optimizer.Core.Platform;

/// <summary>The target folder of a <see cref="SafeMove"/> is or lies in a link, or cannot be held.</summary>
public sealed class FolderNotHeldException(string message) : IOException(message);

/// <summary>
/// Moves a finished file out of a folder only administrators can change (the install media's work folder) into a folder
/// other accounts can write to (the user's output folder), from the elevated process, without resolving a path in that
/// folder after it was checked. The target folder is held (<see cref="SafeDelete.Pin"/>: no folder of its path can be
/// renamed or replaced meanwhile, none is a link), the file is opened once in the locked folder, takes the target
/// folder's inheritable permissions on that handle, and that same handle is renamed into the held folder's handle. The
/// rename (POSIX semantics) replaces the entry of that name, an existing file or a link (a junction too), and never
/// reaches a link's target.
/// </summary>
public static class SafeMove
{
    private const uint Delete = 0x00010000, ReadControl = 0x00020000, WriteDac = 0x00040000, FileReadAttributes = 0x80;
    private const uint FileShareRead = 1, OpenExisting = 3, FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 1, GroupSecurityInformation = 2, DaclSecurityInformation = 4;
    private const int FileRenameInformationEx = 65, ErrorInsufficientBuffer = 122;
    private const uint RenameReplaceIfExists = 0x1, RenamePosixSemantics = 0x2;

    private const int GenericRead = unchecked((int)0x80000000), GenericWrite = 0x40000000, GenericExecute = 0x20000000, GenericAll = 0x10000000;
    private const int FileGenericRead = 0x120089, FileGenericWrite = 0x120116, FileGenericExecute = 0x1200A0, FileAllAccess = 0x1F01FF;

    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier CreatorGroup = new(WellKnownSidType.CreatorGroupSid, null);

    /// <summary>
    /// Moves <paramref name="source"/> into <paramref name="folder"/> as <paramref name="name"/>. Returns false when the
    /// file was moved but could not take the folder's permissions (it then keeps the locked folder's ones; logged).
    /// Throws <see cref="FolderNotHeldException"/> when the folder is or lies in a link, and IOException when the move fails.
    /// </summary>
    public static bool IntoFolder(string source, string folder, string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':'))
            throw new ArgumentException($"\"{name}\" is not a plain file name.", nameof(name));
        using var pin = SafeDelete.Pin(folder, allowRoot: true, readPermissions: true) ?? throw new FolderNotHeldException($"{folder} is or lies in a link, or cannot be held.");
        using var file = Native.CreateFile(@"\\?\" + Path.GetFullPath(source), Delete | ReadControl | WriteDac | FileReadAttributes, FileShareRead,
            IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (file.IsInvalid) throw new IOException($"cannot open {source}", new Win32Exception(Marshal.GetLastWin32Error()));

        var inherited = true;
        try
        {
            TakeFolderPermissions(file, pin.Leaf);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            Logging.Log.Warn("media", $"{source} could not take the permissions of {folder}: {ex.Message}");
            inherited = false;
        }
        Rename(file, pin.Leaf, name);
        return inherited;
    }

    /// <summary>
    /// Gives the file what a new file in the folder would inherit: each of the folder's ACEs that files inherit, marked
    /// inherited, CREATOR OWNER and CREATOR GROUP replaced by the file's owner and group, generic rights mapped to file
    /// rights. Set on the handle as given; nothing is computed from a path.
    /// </summary>
    private static void TakeFolderPermissions(SafeFileHandle file, SafeFileHandle folder)
    {
        var folderDescriptor = new RawSecurityDescriptor(Security(folder, DaclSecurityInformation), 0);
        var fileDescriptor = new RawSecurityDescriptor(Security(file, OwnerSecurityInformation | GroupSecurityInformation), 0);
        if (folderDescriptor.DiscretionaryAcl is not { } folderAcl) throw new IOException("The folder has no access list to inherit.");
        var acl = new RawAcl(GenericAcl.AclRevision, folderAcl.Count);
        foreach (var ace in folderAcl)
        {
            if (ace is not CommonAce common || (common.AceFlags & AceFlags.ObjectInherit) == 0) continue;
            var sid = common.SecurityIdentifier == CreatorOwner ? fileDescriptor.Owner
                : common.SecurityIdentifier == CreatorGroup ? fileDescriptor.Group
                : common.SecurityIdentifier;
            if (sid is null) continue;
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.Inherited, common.AceQualifier, MapGeneric(common.AccessMask), sid, common.IsCallback, common.GetOpaque()));
        }
        // Without SE_DACL_AUTO_INHERITED: with it, Windows may keep the file's old inherited ACEs (the locked folder's)
        // and drop the inherited ones given here (seen in the test suite).
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, null, null, null, acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        if (!NativeWrite.SetKernelObjectSecurity(file, DaclSecurityInformation, bytes))
            throw new IOException("The permissions could not be set.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static int MapGeneric(int mask)
    {
        var specific = mask & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        if ((mask & GenericRead) != 0) specific |= FileGenericRead;
        if ((mask & GenericWrite) != 0) specific |= FileGenericWrite;
        if ((mask & GenericExecute) != 0) specific |= FileGenericExecute;
        if ((mask & GenericAll) != 0) specific |= FileAllAccess;
        return specific;
    }

    private static byte[] Security(SafeFileHandle handle, uint parts)
    {
        Native.GetKernelObjectSecurity(handle, parts, null, 0, out var needed);
        var error = Marshal.GetLastWin32Error();
        if (needed == 0) throw new IOException("The permissions could not be read.", new Win32Exception(error == ErrorInsufficientBuffer ? 0 : error));
        var buffer = new byte[needed];
        if (!Native.GetKernelObjectSecurity(handle, parts, buffer, needed, out _))
            throw new IOException("The permissions could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer;
    }

    /// <summary>
    /// FileRenameInformationEx with the held folder's handle as RootDirectory and the plain name: the name is looked up in
    /// that folder object, not through a path. FILE_RENAME_INFORMATION_EX: Flags (4 bytes), RootDirectory (handle,
    /// aligned), FileNameLength (bytes), FileName.
    /// </summary>
    private static void Rename(SafeFileHandle file, SafeFileHandle folder, string name)
    {
        var rootOffset = IntPtr.Size;
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        // The structure's size (three pointer-sized slots) plus the name and a terminating zero.
        var size = 3 * IntPtr.Size + 2 * name.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        var added = false;
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            folder.DangerousAddRef(ref added);
            Marshal.WriteInt32(buffer, 0, (int)(RenameReplaceIfExists | RenamePosixSemantics));
            Marshal.WriteIntPtr(buffer, rootOffset, folder.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, 2 * name.Length);
            Marshal.Copy(name.ToCharArray(), 0, buffer + nameOffset, name.Length);
            var status = NativeWrite.NtSetInformationFile(file, out _, buffer, (uint)size, FileRenameInformationEx);
            // NT_SUCCESS: success and informational codes are 0 or positive; warnings and errors set the sign bit.
            if (status < 0)
            {
                var error = (int)NativeWrite.RtlNtStatusToDosError(status);
                throw new IOException($"The file could not be moved to {name}: {new Win32Exception(error).Message}", new Win32Exception(error));
            }
        }
        finally
        {
            if (added) folder.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }
}
