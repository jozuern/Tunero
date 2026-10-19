using System.Xml.Linq;

namespace Optimizer.Core.Media;

/// <summary>What the answer file should do. Architecture is "amd64" or "arm64"; InputLocale is like "0407:00000407".</summary>
public sealed record AnswerChoices(
    string Architecture,
    string UiLanguage,
    string InputLocale,
    string SystemLocale,
    string UserLocale,
    string? AccountName,
    bool SkipSetup,
    bool AllowUnsupported,
    bool FirstSignInTunero);

/// <summary>
/// Builds autounattend.xml for the changed install media. Never touches disks (no DiskConfiguration), so Setup always
/// asks where to install. Elements and values are from Microsoft's unattend reference
/// (learn.microsoft.com/windows-hardware/customize/desktop/unattend/):
/// microsoft-windows-shell-setup-oobe-protectyourpc (3 turns Express settings off),
/// microsoft-windows-shell-setup-firstlogoncommands (commands run elevated for an administrator, all start together),
/// microsoft-windows-shell-setup-autologon-logoncount (known issue: Windows adds 1 to LogonCount, so one automatic
/// logon needs LogonCount 1 plus the AutoLogonCount reg command),
/// microsoft-windows-setup-runsynchronous-runsynchronouscommand (windowsPE pass, Order and Path),
/// microsoft-windows-international-core-winpe and microsoft-windows-international-core (languages and locales).
/// </summary>
public static class AnswerFile
{
    /// <summary>First sign-in command that starts the installed Tunero copy.</summary>
    public const string TuneroCommand = "\"%WINDIR%\\Setup\\Tunero\\Tunero.exe\" --first-sign-in";

    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    private static readonly string[] LabConfigValues = ["BypassTPMCheck", "BypassSecureBootCheck", "BypassCPUCheck", "BypassRAMCheck"];

    public static string Build(AnswerChoices c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.SkipSetup)
        {
            var problem = c.AccountName is null ? "Media_NameEmpty" : AccountName.Problem(c.AccountName);
            if (problem is not null)
                throw new ArgumentException("Invalid account name: " + problem, nameof(c));
        }

        var root = new XElement(Ns + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName));

        var winPe = new XElement(Ns + "settings", new XAttribute("pass", "windowsPE"));
        winPe.Add(Component(c, "Microsoft-Windows-International-Core-WinPE",
            new XElement(Ns + "SetupUILanguage", new XElement(Ns + "UILanguage", c.UiLanguage)),
            new XElement(Ns + "InputLocale", c.InputLocale),
            new XElement(Ns + "SystemLocale", c.SystemLocale),
            new XElement(Ns + "UILanguage", c.UiLanguage),
            new XElement(Ns + "UserLocale", c.UserLocale)));
        if (c.AllowUnsupported)
        {
            var commands = new XElement(Ns + "RunSynchronous");
            for (var i = 0; i < LabConfigValues.Length; i++)
            {
                commands.Add(new XElement(Ns + "RunSynchronousCommand", new XAttribute(Wcm + "action", "add"),
                    new XElement(Ns + "Order", i + 1),
                    new XElement(Ns + "Path", $"reg add HKLM\\SYSTEM\\Setup\\LabConfig /v {LabConfigValues[i]} /t REG_DWORD /d 1 /f")));
            }
            winPe.Add(Component(c, "Microsoft-Windows-Setup", commands));
        }
        root.Add(winPe);

        if (c.SkipSetup || c.FirstSignInTunero)
        {
            var oobe = new XElement(Ns + "settings", new XAttribute("pass", "oobeSystem"));
            if (c.SkipSetup)
            {
                // Without this component OOBE stops at the region page.
                oobe.Add(Component(c, "Microsoft-Windows-International-Core",
                    new XElement(Ns + "InputLocale", c.InputLocale),
                    new XElement(Ns + "SystemLocale", c.SystemLocale),
                    new XElement(Ns + "UILanguage", c.UiLanguage),
                    new XElement(Ns + "UserLocale", c.UserLocale)));
            }

            var shell = Component(c, "Microsoft-Windows-Shell-Setup");
            var logon = new XElement(Ns + "FirstLogonCommands");
            var order = 0;
            if (c.SkipSetup)
            {
                var name = c.AccountName!;
                shell.Add(
                    new XElement(Ns + "OOBE",
                        new XElement(Ns + "HideEULAPage", "true"),
                        new XElement(Ns + "HideOnlineAccountScreens", "true"),
                        new XElement(Ns + "HideWirelessSetupInOOBE", "true"),
                        new XElement(Ns + "ProtectYourPC", 3)),
                    new XElement(Ns + "UserAccounts",
                        new XElement(Ns + "LocalAccounts",
                            new XElement(Ns + "LocalAccount", new XAttribute(Wcm + "action", "add"),
                                new XElement(Ns + "Name", name),
                                new XElement(Ns + "Group", "Administrators"),
                                EmptyPassword()))),
                    new XElement(Ns + "AutoLogon",
                        EmptyPassword(),
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "LogonCount", 1),
                        new XElement(Ns + "Username", name)));
                logon.Add(Synchronous(++order, "Stop automatic sign-in after the first one",
                    "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v AutoLogonCount /t REG_DWORD /d 0 /f"));
                logon.Add(Synchronous(++order, "Ask for a new password at the next sign-in",
                    $"net user \"{name}\" /logonpasswordchg:yes"));
            }
            if (c.FirstSignInTunero)
                logon.Add(Synchronous(++order, "Start Tunero", TuneroCommand));
            shell.Add(logon);
            oobe.Add(shell);
            root.Add(oobe);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private static XElement Component(AnswerChoices c, string name, params object[] content) =>
        new(Ns + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", c.Architecture),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            content);

    private static XElement EmptyPassword() =>
        new(Ns + "Password", new XElement(Ns + "Value", ""), new XElement(Ns + "PlainText", "true"));

    private static XElement Synchronous(int order, string description, string commandLine) =>
        new(Ns + "SynchronousCommand", new XAttribute(Wcm + "action", "add"),
            new XElement(Ns + "Order", order),
            new XElement(Ns + "CommandLine", commandLine),
            new XElement(Ns + "Description", description));

    /// <summary>A StringWriter that reports UTF-8, so the XML declaration says utf-8.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
}

/// <summary>Rules for the local account the answer file creates.</summary>
public static class AccountName
{
    private const string InvalidChars = "\"/\\[]:;|=,+*?<>@";
    private static readonly string[] Reserved = ["Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount", "SYSTEM"];

    /// <summary>The Strings key naming the problem, or null when the name is fine.</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Media_NameEmpty";
        if (name.Length > 20) return "Media_NameTooLong";
        foreach (var ch in name)
            if (InvalidChars.Contains(ch) || char.IsControl(ch)) return "Media_NameInvalid";
        if (name.All(ch => ch is '.' or ' ') || name.EndsWith('.')) return "Media_NameInvalid";
        if (Reserved.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))) return "Media_NameReserved";
        return null;
    }
}
