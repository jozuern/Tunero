using System.Xml.Linq;
using Optimizer.Core.Media;
using Xunit;

namespace Optimizer.Core.Tests;

public class AnswerFileTests
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    private static AnswerChoices Choices(bool skip = false, bool unsupported = false, bool tunero = false, string? name = "Josh", string arch = "amd64") =>
        new(arch, "de-DE", "0407:00000407", "de-DE", "de-DE", name, skip, unsupported, tunero);

    private static XDocument Parse(AnswerChoices c) => XDocument.Parse(AnswerFile.Build(c));

    private static XElement Pass(XDocument d, string pass) =>
        d.Root!.Elements(Ns + "settings").Single(s => (string?)s.Attribute("pass") == pass);

    private static XElement Component(XElement pass, string name) =>
        pass.Elements(Ns + "component").Single(x => (string?)x.Attribute("name") == name);

    [Fact]
    public void NothingChosenGivesOnlyTheWinPeLanguages()
    {
        var d = Parse(Choices());
        var pass = Assert.Single(d.Root!.Elements(Ns + "settings"));
        Assert.Equal("windowsPE", (string?)pass.Attribute("pass"));
        var comp = Assert.Single(pass.Elements(Ns + "component"));
        Assert.Equal("Microsoft-Windows-International-Core-WinPE", (string?)comp.Attribute("name"));
        Assert.Equal("de-DE", (string?)comp.Element(Ns + "SetupUILanguage")!.Element(Ns + "UILanguage"));
        Assert.Equal("0407:00000407", (string?)comp.Element(Ns + "InputLocale"));
    }

    [Fact]
    public void UnsupportedAddsFourLabConfigCommandsInWinPe()
    {
        var setup = Component(Pass(Parse(Choices(unsupported: true)), "windowsPE"), "Microsoft-Windows-Setup");
        var cmds = setup.Element(Ns + "RunSynchronous")!.Elements(Ns + "RunSynchronousCommand").ToList();
        Assert.Equal(4, cmds.Count);
        string[] names = ["BypassTPMCheck", "BypassSecureBootCheck", "BypassCPUCheck", "BypassRAMCheck"];
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal((i + 1).ToString(), (string?)cmds[i].Element(Ns + "Order"));
            var path = (string)cmds[i].Element(Ns + "Path")!;
            Assert.Contains(names[i], path);
            Assert.Contains("HKLM\\SYSTEM\\Setup\\LabConfig", path);
            Assert.EndsWith("/t REG_DWORD /d 1 /f", path);
        }
    }

    [Fact]
    public void SkipSetupHidesOobeAndCreatesTheAccount()
    {
        var shell = Component(Pass(Parse(Choices(skip: true)), "oobeSystem"), "Microsoft-Windows-Shell-Setup");
        var oobe = shell.Element(Ns + "OOBE")!;
        Assert.Equal("3", (string?)oobe.Element(Ns + "ProtectYourPC"));
        Assert.Equal("true", (string?)oobe.Element(Ns + "HideOnlineAccountScreens"));
        var acct = shell.Element(Ns + "UserAccounts")!.Element(Ns + "LocalAccounts")!.Element(Ns + "LocalAccount")!;
        Assert.Equal("Josh", (string?)acct.Element(Ns + "Name"));
        Assert.Equal("Administrators", (string?)acct.Element(Ns + "Group"));
        Assert.Equal("", (string?)acct.Element(Ns + "Password")!.Element(Ns + "Value"));
        Assert.Equal("true", (string?)acct.Element(Ns + "Password")!.Element(Ns + "PlainText"));
        var auto = shell.Element(Ns + "AutoLogon")!;
        Assert.Equal("1", (string?)auto.Element(Ns + "LogonCount"));
        Assert.Equal("true", (string?)auto.Element(Ns + "Enabled"));
        Assert.Equal("Josh", (string?)auto.Element(Ns + "Username"));
        Assert.Equal("true", (string?)auto.Element(Ns + "Password")!.Element(Ns + "PlainText"));
    }

    [Fact]
    public void SkipSetupSetsRegionInOobeSystem()
    {
        var ic = Component(Pass(Parse(Choices(skip: true)), "oobeSystem"), "Microsoft-Windows-International-Core");
        Assert.Equal("0407:00000407", (string?)ic.Element(Ns + "InputLocale"));
        Assert.Equal("de-DE", (string?)ic.Element(Ns + "SystemLocale"));
        Assert.Equal("de-DE", (string?)ic.Element(Ns + "UserLocale"));
        Assert.Equal("de-DE", (string?)ic.Element(Ns + "UILanguage"));
    }

    [Fact]
    public void FirstLogonOrderIsAutoLogonFixPasswordThenTunero()
    {
        var shell = Component(Pass(Parse(Choices(skip: true, tunero: true)), "oobeSystem"), "Microsoft-Windows-Shell-Setup");
        var cmds = shell.Element(Ns + "FirstLogonCommands")!.Elements(Ns + "SynchronousCommand").ToList();
        Assert.Equal(3, cmds.Count);
        Assert.Equal(["1", "2", "3"], cmds.Select(x => (string)x.Element(Ns + "Order")!));
        Assert.Equal("reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v AutoLogonCount /t REG_DWORD /d 0 /f",
            (string?)cmds[0].Element(Ns + "CommandLine"));
        Assert.Equal("net user \"Josh\" /logonpasswordchg:yes", (string?)cmds[1].Element(Ns + "CommandLine"));
        Assert.Equal(AnswerFile.TuneroCommand, (string?)cmds[2].Element(Ns + "CommandLine"));
    }

    [Fact]
    public void ProfileWithoutSkipSetupOnlyAddsTheTuneroCommand()
    {
        var d = Parse(Choices(tunero: true, name: null));
        var shell = Component(Pass(d, "oobeSystem"), "Microsoft-Windows-Shell-Setup");
        Assert.Null(shell.Element(Ns + "UserAccounts"));
        Assert.Null(shell.Element(Ns + "OOBE"));
        Assert.Null(shell.Element(Ns + "AutoLogon"));
        var cmd = Assert.Single(shell.Element(Ns + "FirstLogonCommands")!.Elements(Ns + "SynchronousCommand"));
        Assert.Equal("1", (string?)cmd.Element(Ns + "Order"));
        Assert.Equal(AnswerFile.TuneroCommand, (string?)cmd.Element(Ns + "CommandLine"));
    }

    [Fact]
    public void NoDiskConfiguration()
    {
        var all = Parse(Choices(skip: true, unsupported: true, tunero: true));
        Assert.DoesNotContain(all.Descendants(), e => e.Name.LocalName == "DiskConfiguration");
    }

    [Theory]
    [InlineData("amd64")]
    [InlineData("arm64")]
    public void ArchitectureIsOnEveryComponent(string arch)
    {
        var comps = Parse(Choices(skip: true, unsupported: true, tunero: true, arch: arch)).Descendants(Ns + "component").ToList();
        Assert.True(comps.Count >= 4);
        Assert.All(comps, c =>
        {
            Assert.Equal(arch, (string?)c.Attribute("processorArchitecture"));
            Assert.Equal("31bf3856ad364e35", (string?)c.Attribute("publicKeyToken"));
            Assert.Equal("neutral", (string?)c.Attribute("language"));
            Assert.Equal("nonSxS", (string?)c.Attribute("versionScope"));
        });
    }

    [Fact]
    public void DeclarationIsUtf8AndWcmIsDeclared()
    {
        var xml = AnswerFile.Build(Choices(skip: true));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\"", xml);
    }

    [Theory]
    [InlineData("Anna & Ben<")]
    public void SpecialCharactersAreEscaped(string name)
    {
        Assert.NotNull(AccountName.Problem(name));
        Assert.Throws<ArgumentException>(() => AnswerFile.Build(Choices(skip: true, name: name)));
        var d = Parse(Choices(skip: true, name: "Anna & Ben"));
        Assert.Equal("Anna & Ben", (string?)d.Descendants(Ns + "LocalAccount").Single().Element(Ns + "Name"));
    }

    [Fact]
    public void SkipSetupWithoutAccountNameThrows() =>
        Assert.Throws<ArgumentException>(() => AnswerFile.Build(Choices(skip: true, name: null)));

    [Theory]
    [InlineData("", "Media_NameEmpty")]
    [InlineData("abcdefghijklmnopqrstu", "Media_NameTooLong")]
    [InlineData("a/b", "Media_NameInvalid")]
    [InlineData("a\"b", "Media_NameInvalid")]
    [InlineData("a\tb", "Media_NameInvalid")]
    [InlineData("...", "Media_NameInvalid")]
    [InlineData("  ", "Media_NameInvalid")]
    [InlineData("abc.", "Media_NameInvalid")]
    [InlineData("guest", "Media_NameReserved")]
    [InlineData("SYSTEM", "Media_NameReserved")]
    [InlineData("Josh", null)]
    [InlineData("abcdefghijklmnopqrst", null)]
    public void AccountNameRules(string name, string? key) => Assert.Equal(key, AccountName.Problem(name));
}
