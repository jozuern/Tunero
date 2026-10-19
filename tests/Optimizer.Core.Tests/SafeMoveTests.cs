using System.Security.AccessControl;
using System.Security.Principal;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>
/// The handle-only move of a finished file out of a locked folder into a user folder, in temp folders. What only the
/// elevated app in the VM shows: an Administrators-locked source folder, an output at a drive root, and a race against a
/// standard user swapping the folder while the move runs.
/// </summary>
public sealed class SafeMoveTests : IDisposable
{
    private readonly string _root = TestFolders.Create("safemove");

    public void Dispose() => TestFolders.Delete(_root);

    /// <summary>A folder only this user may enter, protected from inheritance, like the build's locked work folder.</summary>
    private string LockedSource(string content)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "work"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
        var file = Path.Combine(folder.FullName, "output.iso");
        File.WriteAllText(file, content);
        return file;
    }

    private string Output()
    {
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        return output;
    }

    private static string[] Rules(string file) =>
        new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => $"{r.IdentityReference.Value} {r.AccessControlType} {(int)r.FileSystemRights:x}").Order().ToArray();

    [Fact]
    public void MovesIntoTheFolderAndReplacesAnExistingFile()
    {
        var source = LockedSource("new ISO");
        var output = Output();
        File.WriteAllText(Path.Combine(output, "Win11.iso"), "older ISO");
        Assert.True(SafeMove.IntoFolder(source, output, "Win11.iso"));
        Assert.Equal("new ISO", File.ReadAllText(Path.Combine(output, "Win11.iso")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void TakesTheFolderPermissions()
    {
        var source = LockedSource("new ISO");
        var output = Output();
        var fresh = Path.Combine(output, "fresh.txt");
        File.WriteAllText(fresh, "x");
        Assert.NotEqual(Rules(fresh), Rules(source));
        Assert.True(SafeMove.IntoFolder(source, output, "Win11.iso"));
        var moved = Path.Combine(output, "Win11.iso");
        Assert.Equal(Rules(fresh), Rules(moved));
        var security = new FileInfo(moved).GetAccessControl();
        Assert.False(security.AreAccessRulesProtected);
        Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => Assert.True(r.IsInherited));
    }

    [Fact]
    public void RefusesAFolderThatIsALink()
    {
        var source = LockedSource("new ISO");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var link = Path.Combine(_root, "link");
        TestFolders.Junction(link, elsewhere);
        Assert.Throws<FolderNotHeldException>(() => SafeMove.IntoFolder(source, link, "Win11.iso"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void NeverFollowsALinkAtTheName()
    {
        var source = LockedSource("new ISO");
        var output = Output();
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
        var moved = Path.Combine(output, "Win11.iso");
        TestFolders.Junction(moved, elsewhere);
        SafeMove.IntoFolder(source, output, "Win11.iso");
        // The link itself was replaced by the file; nothing reached its target.
        Assert.Equal("new ISO", File.ReadAllText(moved));
        Assert.Equal((FileAttributes)0, File.GetAttributes(moved) & (FileAttributes.ReparsePoint | FileAttributes.Directory));
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(elsewhere).Select(Path.GetFileName));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(elsewhere, "keep.txt")));
    }

    /// <summary>
    /// SafeMove reads the target folder's permissions, so its pin asks for READ_CONTROL, but only on that folder: the
    /// folders above it, and every folder of a default pin (updater, storage cleanup), keep the old access. A parent that
    /// lets its owner list it but not read its permissions (an OWNER RIGHTS entry replaces the owner's implicit rights)
    /// must not stop either.
    /// </summary>
    [Fact]
    public void PinAsksToReadPermissionsOnlyOfTheTargetFolder()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "listonly"));
        var child = Directory.CreateDirectory(Path.Combine(parent.FullName, "out"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
            FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Traverse |
            FileSystemRights.Delete | FileSystemRights.Synchronize, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        // The child gets its own protected full control first, so the parent's new list only changes the parent.
        var childSecurity = new DirectorySecurity();
        childSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        childSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        child.SetAccessControl(childSecurity);
        parent.SetAccessControl(security);
        try
        {
            using (var plain = SafeDelete.Pin(child.FullName)) Assert.NotNull(plain);
            using (var reading = SafeDelete.Pin(child.FullName, allowRoot: true, readPermissions: true)) Assert.NotNull(reading);

            var source = LockedSource("new ISO");
            Assert.True(SafeMove.IntoFolder(source, child.FullName, "Win11.iso"));
            Assert.Equal("new ISO", File.ReadAllText(Path.Combine(child.FullName, "Win11.iso")));
        }
        finally
        {
            // Listing, the child's full control and Delete on the parent are enough to remove it (no permissions read).
            Directory.Delete(parent.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"sub\Win11.iso")]
    [InlineData("..")]
    [InlineData("C:Win11.iso")]
    public void RefusesANameThatIsNotAPlainFileName(string name)
    {
        var source = LockedSource("new ISO");
        Assert.Throws<ArgumentException>(() => SafeMove.IntoFolder(source, Output(), name));
        Assert.True(File.Exists(source));
    }
}
