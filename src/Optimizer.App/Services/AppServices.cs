using System.IO;
using System.Net.NetworkInformation;
using Optimizer.Core.Actions;
using Optimizer.Core.Backup;
using Optimizer.Core.Findings.Checks;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Services;

/// <summary>Wires the engine to the real system: session user's hive, secured backup store, restore points.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        Elevation = ElevationInfo.Read();
        Os = BuildInfo.Read();
        var root = BackupStore.DefaultRoot;
        // Not elevated (Debug build): read-only use, the ProgramData ACL cannot be set and nothing can be applied anyway.
        // Backups of the account's own settings are kept per account (the data folder is shared by every account).
        var sid = Elevation.SessionUserSid ?? Elevation.ProcessUserSid;
        Store = new BackupStore(root, secure: Elevation.IsElevated, userSid: sid);
        Context = SystemNotify.CreateContext(sid, Store.ExportFolder, CurrentInterfaceIds);
        // A Tunero that ended while it had the Default profile loaded (killed during a change) left it under HKU. Not in
        // SystemNotify.CreateContext: the read-only Hardware tests create a context too and must not unload anything.
        if (Elevation.IsElevated) DefaultProfileHive.UnloadLeftovers();
        RestorePoints = new RestorePointService(Context.Processes);
        Engine = new TweakEngine(Context, Store, RestorePoints, Optimizer.Core.Platform.AppInfo.Text, Os.Build, Os.BuildString);
        Session = new ChangeSession(Engine, RestorePoints);

        // bcdedit needs admin rights; without them the BCD part of F21 is reported as unknown.
        BcdValues = Elevation.IsElevated ? () => Context.Bcd.CurrentValues() : () => null;
        Log.Info("app", "services ready", new { Elevation.IsElevated, Elevation.AdministratorProtection, user = Elevation.SessionUser });
    }

    public ElevationInfo Elevation { get; }
    public BuildInfo Os { get; }
    public BackupStore Store { get; }
    public ActionContext Context { get; }
    public RestorePointService RestorePoints { get; }
    public TweakEngine Engine { get; }

    /// <summary>The steps of every change, shared by the window (<see cref="ChangeRunner"/>) and the command line.</summary>
    public ChangeSession Session { get; }
    public TweakCatalog Catalog => TweakCatalog.Current;

    /// <summary>The search over the catalog, tools, settings and the lists the pages load (built on the first search).</summary>
    public SearchIndex Search { get; } = new(TweakCatalog.Current, Optimizer.Core.Catalog.CatalogData.Current);

    /// <summary>
    /// Connected physical adapters ("{nic}" values and DNS presets): virtual switches, VPN and the host's Hyper-V adapters
    /// are left alone, so a VPN keeps its own DNS servers. Inside a Hyper-V guest its synthetic adapter counts.
    /// </summary>
    private IReadOnlyList<string> _interfaceIds = [];
    private DateTime _interfaceIdsAt = DateTime.MinValue;

    /// <summary>Read again at most every 10 seconds: a scan expands many tweaks, and an adapter can connect after the start.</summary>
    private IReadOnlyList<string> CurrentInterfaceIds()
    {
        if (DateTime.UtcNow - _interfaceIdsAt > TimeSpan.FromSeconds(10))
        {
            _interfaceIds = ActiveInterfaceIds();
            _interfaceIdsAt = DateTime.UtcNow;
        }
        return _interfaceIds;
    }

    private static IReadOnlyList<string> ActiveInterfaceIds()
    {
        try
        {
            return NicAdapters.ConnectedPhysicalInterfaceIds(new SystemRegistryRoots(null));
        }
        catch (Exception ex) when (ex is NetworkInformationException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public static string DataFolder => Optimizer.Core.Platform.DataPaths.Root;

    /// <summary>BCD elements of {current} for the findings (bcdedit needs admin rights; not elevated = unknown).</summary>
    public Func<IReadOnlyDictionary<string, string>?> BcdValues { get; }

    /// <summary>Extracted helper tools (PresentMon) and capture files.</summary>
    public static string ToolsFolder => Optimizer.Core.Platform.DataPaths.Tools;

    /// <summary>SID of the signed-in user (not the elevated account), for per-user paths and registry.</summary>
    public string? UserSid => Elevation.SessionUserSid ?? Elevation.ProcessUserSid;

    /// <summary>Profile folder of the signed-in user, from the profile list.</summary>
    public string? ProfilePath => UserSid is { } sid
        ? Reg.HklmString($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", "ProfileImagePath")
        : null;

    /// <summary>
    /// The signed-in user's Documents folder (also when moved, to OneDrive for example), for the file dialogs: under a
    /// separate admin account this process's own Documents would be that account's. Null when it cannot be found.
    /// </summary>
    public string? SessionDocumentsFolder() => SessionFolder("Personal", "Documents");

    /// <summary>The signed-in user's account name without the PC or domain (not the elevated account's); null when unknown.</summary>
    public string? SessionUserName()
    {
        if (UserSid is not { } sid) return null;
        try
        {
            var account = new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value;
            return account[(account.LastIndexOf('\\') + 1)..];
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Principal.IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>The signed-in user's Downloads folder, where the install media page saves its ISO. Null when it cannot be found.</summary>
    public string? SessionDownloadsFolder() => SessionFolder("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads");

    /// <summary>A known folder of the signed-in user from User Shell Folders (<paramref name="value"/>), else its usual place.</summary>
    private string? SessionFolder(string value, string fallback)
    {
        if (ProfilePath is not { } profile) return null;
        try
        {
            using var key = Context.Registry.Open(Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", writable: false);
            var raw = key?.GetValue(value, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var folder = raw is { Length: > 0 } ? MachineEnvironment.ForUser(profile)(raw) : Path.Combine(profile, fallback);
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Dialogs the view model needs; implemented by the views.</summary>
public interface IDialogs
{
    /// <summary>Shows the exact changes; returns null when cancelled, otherwise whether the anti-cheat warning was acknowledged.</summary>
    ConfirmResult? ConfirmApply(ConfirmRequest request);

    /// <summary>System Protection is off: Enable / Continue / Cancel.</summary>
    RestorePointChoice AskRestorePoint();

    bool ConfirmExpertMode();

    bool ConfirmUndoAll(int count);

    /// <summary>Uninstall: Undo and uninstall / Keep changes and uninstall / Cancel (only the last two without active changes).</summary>
    UninstallChoice ConfirmUninstall(int activeChanges, string dataFolder);

    /// <summary>Generic question; returns true when the primary button was chosen.</summary>
    bool Ask(string title, string text, string primary);

    /// <summary>Save dialog for a file; returns the chosen path or null. <paramref name="folder"/>: where it opens (null: Documents).</summary>
    string? PickSaveFile(string defaultName, string filterName, string extension, string? folder = null);

    /// <summary>Open dialog for a changes file; returns the chosen path or null.</summary>
    string? PickOpenFile(string filterName, string extension);
}

public sealed record ConfirmRequest(
    string Title,
    string Summary,
    IReadOnlyList<ChangeLine> Changes,
    IReadOnlyList<string> Badges,
    IReadOnlyList<string> Warnings,
    string? AntiCheatWarning,
    bool IsUndo)
{
    /// <summary>Reasons the change cannot be made now (Expert mode off, a policy): shown first, and Apply stays off.</summary>
    public IReadOnlyList<string> Blocks { get; init; } = [];
}

public sealed record ConfirmResult(bool AcknowledgedAntiCheat);

public enum UninstallChoice { UndoFirst, Keep, Cancel }
