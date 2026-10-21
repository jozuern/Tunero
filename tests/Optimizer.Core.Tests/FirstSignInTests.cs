using Optimizer.Core.Catalog;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

public sealed class FirstSignInTests : IDisposable
{
    private readonly string _folder = TestFolders.Create("firstsignin");
    private string Exe => Path.Combine(_folder, "Tunero.exe");

    public void Dispose() => TestFolders.Delete(_folder);

    private void Write(string json) => File.WriteAllText(Path.Combine(_folder, FirstSignIn.FileName), json);

    private static string ProfileId => CatalogData.Current.Profiles.Default.Id;

    [Fact]
    public void RoundTrip()
    {
        var file = new FirstSignInFile(ProfileId, "de", "Dark");
        Write(FirstSignIn.Serialize(file));
        Assert.Equal(file, FirstSignIn.Read(Exe, _ => true, _folder));
    }

    [Fact]
    public void OtherFolderIsIgnored()
    {
        Write(FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "System")));
        Assert.Null(FirstSignIn.Read(@"C:\Users\x\Downloads\Tunero.exe", _ => true, _folder));
        Assert.Null(FirstSignIn.Read(Exe, _ => true));
    }

    [Fact]
    public void FolderCheckIsCaseInsensitive()
    {
        var file = new FirstSignInFile(ProfileId, "en", "Light");
        Write(FirstSignIn.Serialize(file));
        Assert.Equal(file, FirstSignIn.Read(Exe.ToUpperInvariant(), _ => true, _folder));
    }

    [Fact]
    public void UserWritableFolderIsIgnored()
    {
        Write(FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "System")));
        Assert.Null(FirstSignIn.Read(Exe, _ => false, _folder));
    }

    [Fact]
    public void MissingFileIsIgnored() => Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));

    [Fact]
    public void UnknownProfileIsIgnored()
    {
        Write(FirstSignIn.Serialize(new FirstSignInFile("turbo", "en", "System")));
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
    }

    [Fact]
    public void UnknownLanguageOrThemeIsIgnored()
    {
        Write(FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "fr", "System")));
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
        Write(FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "Purple")));
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
    }

    [Fact]
    public void AppsToInstallRoundTrip()
    {
        var file = new FirstSignInFile(ProfileId, "en", "System") { Apps = ["Valve.Steam", "EpicGames.EpicGamesLauncher"] };
        Write(FirstSignIn.Serialize(file));
        Assert.Equal(file, FirstSignIn.Read(Exe, _ => true, _folder));
        // Without apps the file stays as before: no "apps" field.
        Assert.DoesNotContain("apps", FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "System")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Contoso.NotInTheCatalog")]
    [InlineData("Playnite.Playnite")]
    [InlineData("Valve.Steam", "Valve.Steam")]
    public void AppsThePageDoesNotOfferAreIgnored(params string[] apps)
    {
        // Unknown ids, per-user apps (the first sign-in cannot wait for them) and an id twice: the whole file is refused.
        Write(FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "System") { Apps = apps }));
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
    }

    [Fact]
    public void InvalidJsonIsIgnored()
    {
        Write("{ not json");
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
        Write("null");
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
    }

    [Fact]
    public void OversizedFileIsIgnored()
    {
        var json = FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "System"));
        Write(json + new string(' ', 20000));
        Assert.Null(FirstSignIn.Read(Exe, _ => true, _folder));
    }

    [Fact]
    public void JunctionedExeFolderIsIgnored()
    {
        var real = TestFolders.Create("firstsignin-real");
        var link = Path.Combine(_folder, "link");
        try
        {
            File.WriteAllText(Path.Combine(real, FirstSignIn.FileName), FirstSignIn.Serialize(new FirstSignInFile(ProfileId, "en", "System")));
            TestFolders.Junction(link, real);
            Assert.Null(FirstSignIn.Read(Path.Combine(link, "Tunero.exe"), _ => true, link));
        }
        finally
        {
            TestFolders.Delete(real);
        }
    }
}
