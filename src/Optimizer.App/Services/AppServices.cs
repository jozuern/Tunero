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
        Session = new ChangeSession(Engine, RestorePoints, ChangeLock.DataFolder);

        // bcdedit needs admin rights; without them the BCD part of F21 is reported as unknown.
        BcdValues = Elevation.IsElevated ? () => Context.Bcd.CurrentValues() : () => null;
        Log.Info("app", "services ready", new { Elevation.IsElevated, Elevation.AdministratorProtection, user = Elevation.SessionUser });
    }

    /// <summary>
    /// For tests: the services on the adapters, store, restore points and change lock given (fakes and a registry sandbox).
    /// Unlike the app's constructor it reads nothing from this PC (no Windows build, no network adapters, no elevation)
    /// and unloads no leftover Default profile hive; everything else, <see cref="ProfilePath"/> too, reads through
    /// <paramref name="context"/> as in the app.
    /// </summary>
    internal AppServices(ActionContext context, BackupStore store, ElevationInfo elevation, IRestorePoints restorePoints, IChangeLock changeLock, BuildInfo os)
    {
        Elevation = elevation;
        Os = os;
        Store = store;
        Context = context;
        RestorePoints = restorePoints;
        Engine = new TweakEngine(Context, Store, RestorePoints, Optimizer.Core.Platform.AppInfo.Text, Os.Build, Os.BuildString);
        Session = new ChangeSession(Engine, RestorePoints, changeLock);
        BcdValues = Elevation.IsElevated ? () => Context.Bcd.CurrentValues() : () => null;
    }

    public ElevationInfo Elevation { get; }
    public BuildInfo Os { get; }
    public BackupStore Store { get; }
    public ActionContext Context { get; }
    public IRestorePoints RestorePoints { get; }
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

    /// <summary>Profile folder of the signed-in user, from the profile list (through <see cref="Context"/>, so tests read their sandbox).</summary>
    public string? ProfilePath
    {
        get
        {
            if (UserSid is not { } sid) return null;
            try
            {
                using var key = Context.Registry.Open(Hive.Machine, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", writable: false);
                return key?.GetValue("ProfileImagePath")?.ToString();
            }
            catch (Exception)
            {
                // As Reg.HklmString read it before: an unreadable profile list means no known profile folder.
                return null;
            }
        }
    }

    /// <summary>
    /// Expands per-user paths and command lines for the signed-in user (<see cref="UserSid"/>, <see cref="ProfilePath"/>),
    /// not for the account this app runs as. Read anew each time: the user's own variables can change while the app runs.
    /// </summary>
    public PathExpander UserPaths => PathExpander.ForUser(ProfilePath, Context.Registry);

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

    /// <summary>
    /// The signed-in user's profile and their Desktop, Documents, Downloads, Pictures, Music, Videos and Saved Games, also
    /// when moved: the Storage page keeps these, and under a separate admin account this process's own known folders are
    /// that account's. The ones that cannot be found are left out.
    /// </summary>
    public IReadOnlyList<string> SessionKnownFolders()
    {
        (string Value, string Fallback)[] folders =
        [
            ("Desktop", "Desktop"), ("Personal", "Documents"), ("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads"), ("My Pictures", "Pictures"),
            ("My Music", "Music"), ("My Video", "Videos"), ("{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}", "Saved Games"),
        ];
        var found = folders.Select(f => SessionFolder(f.Value, f.Fallback)).OfType<string>().ToList();
        if (ProfilePath is { } profile) found.Add(profile);
        return found;
    }

    /// <summary>A known folder of the signed-in user from User Shell Folders (<paramref name="value"/>), else its usual place.</summary>
    private string? SessionFolder(string value, string fallback)
    {
        if (ProfilePath is not { } profile) return null;
        try
        {
            using var key = Context.Registry.Open(Hive.User, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", writable: false);
            var raw = key?.GetValue(value, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var folder = raw is { Length: > 0 } ? UserPaths.Expand(raw) : Path.Combine(profile, fallback);
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

    /// <summary>
    /// Save dialog for a file; returns the chosen path or null. <paramref name="folder"/>: where it opens (null: Documents).
    /// <paramref name="title"/> is the dialog's caption (null: Windows' own).
    /// </summary>
    string? PickSaveFile(string defaultName, string filterName, string extension, string? folder = null, string? title = null);

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

    /// <summary>How long the change can take when that is minutes (a Windows feature): the dialog says it with a clock under the tags.</summary>
    public string? Duration { get; init; }

    /// <summary>The text of the main button (null: "Apply", or "Undo" for an undo).</summary>
    public string? Primary { get; init; }

    /// <summary>The line left of the buttons (null: the note about the saved original values).</summary>
    public string? Footer { get; init; }
}

public sealed record ConfirmResult(bool AcknowledgedAntiCheat);

public enum UninstallChoice { UndoFirst, Keep, Cancel }
