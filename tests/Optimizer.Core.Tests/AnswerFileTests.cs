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
    public void FirstLogonCommandsHaveOrders123()
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

    [Fact]
    public void SpecialCharactersAreEscaped()
    {
        const string name = "Juergen O'Neil";
        Assert.Null(AccountName.Problem(name));
        var d = Parse(Choices(skip: true, name: name));
        Assert.Equal(name, (string?)d.Descendants(Ns + "LocalAccount").Single().Element(Ns + "Name"));
        Assert.Equal("net user \"" + name + "\" /logonpasswordchg:yes",
            d.Descendants(Ns + "CommandLine").Select(x => (string)x).Single(x => x.StartsWith("net user")));

        // Other fields go through the same escaping.
        var c = Choices(skip: true) with { UserLocale = "a&b<c" };
        var ic = Component(Pass(Parse(c), "oobeSystem"), "Microsoft-Windows-International-Core");
        Assert.Equal("a&b<c", (string?)ic.Element(Ns + "UserLocale"));
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("")]
    [InlineData("AMD64")]
    public void UnknownArchitectureThrows(string arch) =>
        Assert.Throws<ArgumentException>(() => AnswerFile.Build(Choices(arch: arch)));

    [Fact]
    public void BothKnownArchitecturesBuild()
    {
        AnswerFile.Build(Choices(arch: "amd64"));
        AnswerFile.Build(Choices(arch: "arm64"));
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
    [InlineData("NONE", "Media_NameReserved")]
    [InlineData("none", "Media_NameReserved")]
    [InlineData("Users", "Media_NameReserved")]
    [InlineData("Benutzer", "Media_NameReserved")]
    [InlineData("Gäste", "Media_NameNotAscii")]
    [InlineData("defaultuser0", "Media_NameReserved")]
    [InlineData("Power Users", "Media_NameReserved")]
    [InlineData("Replicator", "Media_NameReserved")]
    [InlineData("IIS_IUSRS", "Media_NameReserved")]
    [InlineData("event log readers", "Media_NameReserved")]
    [InlineData("OpenSSH Users", "Media_NameReserved")]
    [InlineData("Print Operators", "Media_NameReserved")]
    [InlineData("IUSR", "Media_NameReserved")]
    [InlineData("a%b", "Media_NameInvalid")]
    [InlineData("a&b", "Media_NameInvalid")]
    [InlineData("a^b", "Media_NameInvalid")]
    [InlineData("Jürgen", "Media_NameNotAscii")]
    [InlineData("Zoë", "Media_NameNotAscii")]
    [InlineData("José", "Media_NameNotAscii")]
    [InlineData("Anna\u00A0Maria", "Media_NameNotAscii")]
    [InlineData("日本", "Media_NameNotAscii")]
    [InlineData("Juergen", null)]
    [InlineData("Anna Maria", null)]
    [InlineData("J.R", null)]
    [InlineData("Josh", null)]
    [InlineData("abcdefghijklmnopqrst", null)]
    public void AccountNameRules(string name, string? key) => Assert.Equal(key, AccountName.Problem(name));

    /// <summary>
    /// Every local group and account of Windows 11 Pro 26H2 in PCO-Test (Get-LocalGroup and Get-LocalUser, 2026-10-20,
    /// with Hyper-V, IIS and OpenSSH features on) is refused: a new account cannot share a name with any of them.
    /// </summary>
    [Fact]
    public void EveryGroupAndAccountOfAWindows11PcIsRefused()
    {
        string[] names =
        [
            "Access Control Assistance Operators", "Administrators", "Backup Operators", "Cryptographic Operators", "Device Owners",
            "Distributed COM Users", "Event Log Readers", "Guests", "Hyper-V Administrators", "IIS_IUSRS", "Network Configuration Operators",
            "OpenSSH Users", "Performance Log Users", "Performance Monitor Users", "Power Users", "Remote Desktop Users",
            "Remote Management Users", "Replicator", "System Managed Accounts Group", "User Mode Hardware Operators", "Users",
            "Administrator", "DefaultAccount", "Guest", "WDAGUtilityAccount",
        ];
        Assert.All(names, n => Assert.NotNull(AccountName.Problem(n)));
        // The ones short enough for an account name are refused because Windows has them, not for their length.
        Assert.All(names.Where(n => n.Length <= 20), n => Assert.Equal("Media_NameReserved", AccountName.Problem(n)));
    }

    /// <summary>
    /// Fixtures/media/autounattend-vm1.xml is the Autounattend.xml from boot.wim image 2 of the ISO that installed in the
    /// VM test (VM 1: every change, account Anna, en-US, allow unsupported PCs, Tunero copy). A change to the answer file
    /// must be proven in a VM again before this file is replaced.
    /// </summary>
    [Fact]
    public void Vm1AnswerFileIsTheOneThatInstalled()
    {
        var expected = File.ReadAllText(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", "autounattend-vm1.xml"));
        var built = AnswerFile.Build(new AnswerChoices("amd64", "en-US", "0409:00000409", "en-US", "en-US", "Anna",
            SkipSetup: true, AllowUnsupported: true, FirstSignInTunero: true));
        // Line endings may change on checkout (core.autocrlf); the content may not.
        Assert.Equal(expected.ReplaceLineEndings("\n"), built.ReplaceLineEndings("\n"));
    }
}
