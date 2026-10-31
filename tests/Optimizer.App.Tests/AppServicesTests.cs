using System.IO;
using Optimizer.Core.Actions;

namespace Optimizer.App.Tests;

/// <summary>
/// The signed-in user's folders in <see cref="Services.AppServices"/> come from the registry of its context, so the pages
/// see the user's profile, not the elevated account's, and tests see their sandbox instead of this PC.
/// </summary>
public sealed class AppServicesTests
{
    [Fact]
    public void TheProfileFolderComesFromTheProfileListOfTheContext()
    {
        using var fx = new AppFixture();
        Assert.Equal(fx.ProfileFolder, fx.Services.ProfilePath);

        // Documents not moved: the folder in the profile, when it exists.
        Assert.Null(fx.Services.SessionDocumentsFolder());
        var documents = Directory.CreateDirectory(Path.Combine(fx.ProfileFolder, "Documents")).FullName;
        Assert.Equal(documents, fx.Services.SessionDocumentsFolder());
        Assert.Contains(fx.ProfileFolder, fx.Services.SessionKnownFolders());

        // Moved (to OneDrive, say): User Shell Folders of the user's hive, expanded for that user.
        var moved = Directory.CreateDirectory(Path.Combine(fx.ProfileFolder, "OneDrive", "Documents")).FullName;
        RegistryValue.Write(fx.Pc.Registry, Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Personal", "expandString",
            @"%USERPROFILE%\OneDrive\Documents");
        Assert.Equal(moved, fx.Services.SessionDocumentsFolder());
    }

    [Fact]
    public void AUserMissingFromTheProfileListHasNoProfileFolder()
    {
        using var fx = new AppFixture();
        using (var list = fx.Pc.Registry.Open(Hive.Machine, AppFixture.ProfileList, writable: true)) list!.DeleteSubKeyTree(AppFixture.UserSid);
        Assert.Null(fx.Services.ProfilePath);
        Assert.Null(fx.Services.SessionDocumentsFolder());
        Assert.Empty(fx.Services.SessionKnownFolders());
    }
}
