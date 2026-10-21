using System.Runtime.InteropServices;
using System.Security.Principal;
using Optimizer.Core.Interop;

namespace Optimizer.Core.Platform;

/// <summary>
/// Who the process runs as vs. who is signed in. Read-only; the action layer uses it for user-scope writes.
/// </summary>
public sealed record ElevationInfo(
    bool IsElevated,
    string ProcessUser,
    string? ProcessUserSid,
    string? SessionUser,
    string? SessionUserSid,
    bool AdministratorProtection)
{
    public bool UserMismatch => SessionUserSid is not null && ProcessUserSid is not null &&
                                !string.Equals(SessionUserSid, ProcessUserSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Banner only for the separate-admin-account case; under Administrator protection the mismatch is expected.</summary>
    public bool ShowSeparateAdminBanner => UserMismatch && !AdministratorProtection;

    /// <summary>
    /// Windows names the signed-in account but cannot give its SID (a domain account while no domain controller answers),
    /// and it is not the account this app runs as: user settings then go to this app's own account (the app falls back to
    /// <see cref="ProcessUserSid"/>), not to the signed-in user. Also under Administrator protection, whose hidden
    /// account is never the user's.
    /// </summary>
    public bool SessionUserUnresolved => SessionUser is { Length: > 0 } session && SessionUserSid is null && !SameAccount(session, ProcessUser);

    /// <summary>Which "other account" banner the window shows, if any.</summary>
    public AccountBanner Banner => SessionUserUnresolved ? AccountBanner.UnresolvedUser : ShowSeparateAdminBanner ? AccountBanner.SeparateAdmin : AccountBanner.None;

    /// <summary>"DOMAIN\name" against "DOMAIN\name", or a bare name (no domain from Windows) against the name part.</summary>
    internal static bool SameAccount(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Contains('\\') && b.Contains('\\')) return false;
        return string.Equals(a.Split('\\').Last(), b.Split('\\').Last(), StringComparison.OrdinalIgnoreCase);
    }

    public static ElevationInfo Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var (sessionUser, sessionSid) = ReadSessionUser();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var processName = identity.Name;
        var adminProtection = IsAdministratorProtection(Path.GetFileName(profile), processName,
            Reg.HklmValue(PolicySystem, "TypeOfAdminApprovalMode") as int?);
        return new ElevationInfo(elevated, processName, identity.User?.Value, sessionUser, sessionSid, adminProtection);
    }

    private const string PolicySystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>
    /// Administrator protection: elevation runs as a hidden system-managed account named ADMIN_ and a suffix, with its
    /// own profile. The name alone could be an ordinary account called ADMIN_x, so the policy must be on as well:
    /// "User Account Control: Configure type of Admin Approval Mode" = 2, Admin Approval Mode with Administrator
    /// protection (Microsoft's LocalPoliciesSecurityOptions CSP, values 1 legacy and 2). Microsoft does not document its
    /// registry value; TypeOfAdminApprovalMode under Policies\System is the one Appinfo reads (call4cloud.nl, Administrator
    /// Protection in Windows 25H2 Explained) and the one guides set to 2 to turn it on (kapilarya.com).
    /// </summary>
    internal static bool IsAdministratorProtection(string profileFolder, string processName, int? typeOfAdminApprovalMode) =>
        typeOfAdminApprovalMode == 2 &&
        (profileFolder.StartsWith("ADMIN_", StringComparison.OrdinalIgnoreCase) ||
         processName.Split('\\').Last().StartsWith("ADMIN_", StringComparison.OrdinalIgnoreCase));

    private static (string?, string?) ReadSessionUser()
    {
        try
        {
            var user = QueryWts(Native.WtsUserName);
            if (string.IsNullOrEmpty(user)) return (null, null);
            var domain = QueryWts(Native.WtsDomainName);
            var account = string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
            string? sid = null;
            try
            {
                sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
            }
            catch (IdentityNotMappedException)
            {
            }
            return (account, sid);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string? QueryWts(int infoClass)
    {
        if (!Native.WTSQuerySessionInformation(IntPtr.Zero, Native.WtsCurrentSession, infoClass, out var buffer, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }
}

/// <summary>
/// The "other account" banner: none, a separate administrator account (user settings go to the signed-in user), or a
/// signed-in account Windows cannot look up (user settings go to the account the app runs as).
/// </summary>
public enum AccountBanner { None, SeparateAdmin, UnresolvedUser }

/// <summary>Domain join and MDM enrollment. Policies may override changes on such devices.</summary>
public sealed record ManagedDeviceInfo(bool DomainJoined, bool EntraJoined, bool MdmEnrolled)
{
    public bool IsManaged => DomainJoined || EntraJoined || MdmEnrolled;

    public static ManagedDeviceInfo Read()
    {
        var domain = false;
        try
        {
            if (Native.NetGetJoinInformation(null, out var name, out var type) == 0)
            {
                domain = type == Native.NetSetupDomainName;
                Native.NetApiBufferFree(name);
            }
        }
        catch (Exception)
        {
        }

        var entra = Reg.HklmSubKeys(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo").Length > 0;

        var mdm = false;
        foreach (var sub in Reg.HklmSubKeys(@"SOFTWARE\Microsoft\Enrollments"))
        {
            var path = $@"SOFTWARE\Microsoft\Enrollments\{sub}";
            // Real MDM: EnrollmentType 6 (MDM full) or 13 (MDM via Entra join), state 1. Windows creates built-in
            // "Deploy/Cloud/Local Authority" enrollments (types 28/30/31) on unmanaged PCs too (seen on the Gaming PC).
            if (Reg.HklmInt(path, "EnrollmentType") is 6 or 13 && Reg.HklmInt(path, "EnrollmentState") == 1)
            {
                mdm = true;
                break;
            }
        }
        return new ManagedDeviceInfo(domain, entra, mdm);
    }
}
