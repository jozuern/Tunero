using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Optimizer.Core.Backup;
using Optimizer.Core.Catalog;
using Optimizer.Core.Debloat;
using Optimizer.Core.Platform;
using AppLog = Optimizer.Core.Logging.Log;

namespace Optimizer.Core.Media;

/// <summary>The stages of a build in their order. Catalog is the page's own step before <see cref="MediaBuild.RunAsync"/>.</summary>
public enum MediaStage { Catalog, Download, Check, Unpack, Apps, Parts, Settings, Shrink, Compress, Files, Iso, Verify, Cleanup }

/// <summary>
/// How the install image is stored on the media. Wim: install.wim with maximum compression (the build is faster).
/// Recovery: install.esd with recovery compression (a smaller ISO; the build takes longer).
/// </summary>
public enum MediaCompression { Wim, Recovery }

/// <summary>Where the Windows image comes from: exactly one of a catalog file to download or the user's own ISO. Cab is the catalog the page checked.</summary>
public sealed record MediaSource(EsdFile? Download, string? IsoPath, CabLocation? Cab);

/// <summary>
/// One build. <paramref name="Edition"/> is the EditionID (like "Professional"); for an own ISO it may also be the image
/// name. <paramref name="IsoIndex"/> picks the image of an own ISO (null: found by <paramref name="Edition"/>).
/// <paramref name="RemoveApps"/> are names from the appx.json allowlist, <see cref="RemoveUnreviewed"/> package names the
/// list does not cover (Expert, own ISO). <paramref name="TuneroExe"/>,
/// <paramref name="FirstSignIn"/> and <c>Answers.FirstSignInTunero</c> are given together or not at all.
/// </summary>
public sealed record MediaRequest(
    MediaSource Source,
    string Edition,
    int? IsoIndex,
    IReadOnlyList<string> RemoveApps,
    AnswerChoices Answers,
    string? TuneroExe,
    FirstSignInFile? FirstSignIn,
    string OutputIso)
{
    /// <summary>Package names outside the allowlist, removed only when the image holds exactly that name (<see cref="MediaChoices.UnreviewedApps"/>).</summary>
    public IReadOnlyList<string> RemoveUnreviewed { get; init; } = [];

    /// <summary>How the install image is stored on the media (<see cref="MediaCompression"/>).</summary>
    public MediaCompression Compression { get; init; } = MediaCompression.Wim;

    /// <summary>Registry values written into install.wim (Expert); none by default.</summary>
    public MediaImageSettings Settings { get; init; } = MediaImageSettings.None;
}

/// <summary>
/// Expert settings written into the registry of the mounted install.wim, so they apply to every PC installed from the ISO.
/// <paramref name="PreventDeviceEncryption"/> keeps Windows from turning on device encryption by itself;
/// <paramref name="NoReservedStorage"/> keeps no space for updates;
/// <paramref name="AllowUnsupportedUpgrades"/> lets PCs installed from the ISO accept later upgrades without the hardware checks;
/// <see cref="MediaBuild.Validate"/> refuses it without <see cref="AnswerChoices.AllowUnsupported"/>.
/// </summary>
public sealed record MediaImageSettings(bool PreventDeviceEncryption, bool NoReservedStorage, bool AllowUnsupportedUpgrades)
{
    public static MediaImageSettings None { get; } = new(false, false, false);

    /// <summary>Whether any setting is on.</summary>
    public bool Any => PreventDeviceEncryption || NoReservedStorage || AllowUnsupportedUpgrades;

    /// <summary>
    /// The DWORD values of the settings that are on: the hive (SYSTEM or SOFTWARE), the key inside it, the name and the
    /// value. The one place for them. <paramref name="controlSet"/> is the image's ("ControlSet001"); an offline SYSTEM
    /// hive has no CurrentControlSet.
    /// </summary>
    internal static IReadOnlyList<(string Hive, string Key, string Name, int Value)> Values(MediaImageSettings s, string controlSet)
    {
        var values = new List<(string, string, string, int)>();
        // Documented by Microsoft: PreventDeviceEncryption = 1 under HKLM\SYSTEM\CurrentControlSet\Control\BitLocker.
        if (s.PreventDeviceEncryption) values.Add(("SYSTEM", $@"{controlSet}\Control\BitLocker", "PreventDeviceEncryption", 1));
        // Microsoft does not document 0 for this value: Preview on the page until the test VM shows reserved storage Disabled.
        if (s.NoReservedStorage) values.Add(("SOFTWARE", @"Microsoft\Windows\CurrentVersion\ReserveManager", "ShippedWithReserves", 0));
        // Not documented by Microsoft any more (the 2021 page is gone) and not tested in the VM yet: Preview on the page.
        // Read by the running Windows when a newer setup.exe upgrades it; not under a ControlSet.
        if (s.AllowUnsupportedUpgrades) values.Add(("SYSTEM", @"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1));
        return values;
    }
}

/// <summary>
/// A finished build: the ISO at the output path, its size and its SHA-256 (upper-case hex, of the verified file before it
/// was moved there) and the build's log file.
/// </summary>
public sealed record MediaBuildResult(string Iso, long Bytes, string Sha256)
{
    public string? LogPath { get; init; }
}

/// <summary>A stage started (Percent null) or moved on (0 to 100). Detail names the file or package being worked on, or is null.</summary>
public sealed record MediaProgress(MediaStage Stage, double? Percent, string? Detail);

/// <summary>
/// A build failed. <see cref="Key"/> is the Strings key of the reason when the build knows it (see <see cref="Keys"/>),
/// <see cref="Code"/> the DISM error or Win32 code when there is one; the message is for the log and names the log file.
/// </summary>
public sealed class MediaBuildException(MediaStage stage, string message, int? code = null, string? key = null, Exception? inner = null)
    : Exception(message, inner)
{
    public MediaStage Stage { get; } = stage;
    public int? Code { get; } = code;
    public string? Key { get; } = key;

    /// <summary>The build's log file, or null when the failure came before the build started logging.</summary>
    public string? LogPath { get; init; }

    /// <summary>The ISO is open in another program (mounted in Explorer, for example): the page can say so.</summary>
    public bool InUse => InnerException is IsoAttachException { InUse: true };

    /// <summary>Every <see cref="Key"/> the build uses; the page needs a text for each.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "Media_OutputInvalid", "Media_OutputLink", "Media_OutputReadOnly", "Media_OutputInUse", "Media_OutputNotLocal",
        "Media_OutputMoveFailed", "Media_SourceMissing", "Media_WorkFolderFailed",
        "Media_NotEnoughSpace", "Media_NotWindows11", "Media_NotX64", "Media_NoUefiBoot", "Media_EditionMissing",
        "Media_ImageCheckFailed", "Media_Unhealthy", "Media_FilesCheckFailed", "Media_BootImageAnswerFile", "Media_IsoInUse",
        "Media_VerifyFailed", "Media_SettingsFailed",
    ];
}

/// <summary>
/// A build's work folder (absolute path) and its lock file <see cref="MediaBuild.LockFileName"/>, held open without
/// sharing until disposed: <see cref="MediaBuild.CleanLeftoversAsync"/> leaves the folder alone while it is held.
/// </summary>
public sealed class WorkFolder : IDisposable
{
    private FileStream? _lock;

    private WorkFolder(string path, FileStream lockFile)
    {
        Path = path;
        _lock = lockFile;
    }

    public string Path { get; }

    /// <summary>Creates the lock file in <paramref name="path"/> (it must not exist yet) and holds it.</summary>
    public static WorkFolder Hold(string path) =>
        new(path, new FileStream(System.IO.Path.Combine(path, MediaBuild.LockFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None));

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

/// <summary>What a build uses from the system: the tools, the download, the work folder and the log. Tests pass fakes.</summary>
public interface IMediaSteps
{
    IDism Dism { get; }
    IIsoWriter IsoWriter { get; }
    IIsoReader IsoReader { get; }

    /// <summary>Downloads the catalog file to <paramref name="target"/>, checked against its size and SHA-256 (<see cref="MediaDownload"/>).</summary>
    Task DownloadAsync(EsdFile file, string target, IProgress<(long Done, long Total)>? p, CancellationToken ct);

    /// <summary>
    /// Creates a new, locked work folder on the drive of <paramref name="outputIso"/> and returns it with its lock file
    /// already held, so no other Tunero ever sees it without one.
    /// </summary>
    WorkFolder NewWorkFolder(string outputIso);

    /// <summary>
    /// Deletes a work folder without following links: a folder that is itself a link is only unlinked, a folder that is not
    /// locked to Administrators and SYSTEM is refused (IOException), any path not named like a work folder too (ArgumentException).
    /// </summary>
    void DeleteWorkFolder(string folder);

    long FreeBytes(string path);

    /// <summary>The roots of the local fixed drives, where work folders are created.</summary>
    IEnumerable<string> LocalDrives();

    /// <summary>The work folders at the root of <paramref name="drive"/>.</summary>
    IEnumerable<string> LeftoverWorkFolders(string drive);

    /// <summary>
    /// The command lines of the dism.exe processes running now: empty when none runs, null when one runs but the command
    /// lines cannot be read. DISM outlives a killed build and goes on mounting or unmounting in its work folder
    /// (<see cref="MediaBuild.CleanLeftoversAsync"/>).
    /// </summary>
    IReadOnlyList<string>? DismCommandLines();

    /// <summary>
    /// Loads the SYSTEM hive file of a mounted image, read only, until the result is disposed (<see cref="OfflineSystemHive"/>).
    /// Throws IOException or UnauthorizedAccessException when it cannot be loaded.
    /// </summary>
    IOfflineHive LoadSystemHive(string file);

    /// <summary>
    /// Loads a hive file of the mounted install.wim (SYSTEM or SOFTWARE) for writing, until the result is disposed
    /// (<see cref="OfflineImageRegistry"/>). Throws IOException or UnauthorizedAccessException when it cannot be loaded.
    /// </summary>
    IWritableHive LoadWritableHive(string file);

    /// <summary>
    /// Unloads the hives no build holds: those of Tuneros no longer running and this process's given-up ones
    /// (<see cref="OfflineSystemHive.UnloadLeftovers"/>). Never throws.
    /// </summary>
    void UnloadLeftoverHives();

    /// <summary>Appends a line to the build's log. Never throws.</summary>
    void Log(string line);

    string LogPath { get; }
}

/// <summary>
/// Builds the changed install media: download or own ISO, unpack with DISM, remove apps, add the answer file (inside
/// boot.wim) and Tunero, write the ISO and read it back. The ISO is written and verified inside the locked work folder
/// and only then moved to the output path, so that path only ever holds a verified ISO and an older file there survives
/// every failure. Every safety check fails the build with a <see cref="MediaBuildException"/>; on any failure or Stop a
/// mounted image is discarded and the work folder deleted. A folder whose image cannot be unmounted is kept (deleting it
/// would reach into the mounted image) and removed by <see cref="CleanLeftoversAsync"/> at the next start.
/// </summary>
public sealed class MediaBuild(IMediaSteps steps)
{
    public const string WorkFolderPrefix = "TuneroMedia.";

    /// <summary>Held open without sharing for the whole build: a work folder whose lock file cannot be opened belongs to a running build.</summary>
    internal const string LockFileName = "build.lock";

    /// <summary>The folders of a work folder that DISM mounts install.wim and boot.wim into.</summary>
    private const string MountFolder = "mount", BootMountFolder = "bootmount";

    private const int Attempts = 3;
    private const long Gigabyte = 1L << 30;
    private const string TuneroOnMedia = @"sources\$OEM$\$$\Setup\Tunero";
    private static readonly Version MinimumVersion = new(10, 0, 22000);
    private static readonly Regex WorkFolderName = new(@"^TuneroMedia\.[0-9a-fA-F]{8}$", RegexOptions.Compiled);

    /// <summary>The pause before a cleanup step is tried again (a stopped DISM may still hold its files for a moment).</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the cleanup waits for a DISM that a killed build left running in its work folder. In PCO-Test such a mount
    /// ended within about 3 minutes; 15 leaves room for a slow disk and a commit, while a DISM still busy after that is
    /// more likely hung than slow. Its folder is then left for the next start.
    /// </summary>
    internal TimeSpan DismWaitLimit { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The pause between two looks at the running DISM processes.</summary>
    internal TimeSpan DismPollDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Free space the build needs on the output drive. Download: the ESD, 2.2 times its size for the media and the ISO, and
    /// 1 GB to spare. Own ISO (<paramref name="esdSize"/> is then the ISO's size): 2.5 times the ISO and 1 GB.
    /// Recovery compression adds the size once more: install.wim and install.esd exist side by side during the export.
    /// </summary>
    public static long NeededBytes(MediaSource s, long esdSize, MediaCompression compression = MediaCompression.Wim) =>
        (s.Download is not null ? esdSize + esdSize * 22 / 10 + Gigabyte : esdSize * 25 / 10 + Gigabyte)
        + (compression == MediaCompression.Recovery ? esdSize : 0);

    internal static bool IsWorkFolderName(string name) => WorkFolderName.IsMatch(name);

    /// <summary>The work folder (X:\TuneroMedia.0123abcd) that <paramref name="path"/> is or lies in, or null.</summary>
    internal static string? WorkFolderOf(string path)
    {
        if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || !char.IsAsciiLetter(path[0])) return null;
        var first = path[3..].Split('\\')[0];
        return IsWorkFolderName(first) ? path[..3] + first : null;
    }

    /// <summary>
    /// The stages a run of <paramref name="r"/> begins, in order: what the page lists while the build runs. Without Catalog
    /// (the page's own step) and Cleanup (shown only when a build fails or is stopped). Mirrors <c>Run.ExecuteAsync</c>;
    /// a test keeps the two equal, so a stage added there is added here.
    /// </summary>
    public static IReadOnlyList<MediaStage> PlannedStages(MediaRequest r)
    {
        List<MediaStage> stages = r.Source.Download is not null ? [MediaStage.Download, MediaStage.Check, MediaStage.Unpack] : [MediaStage.Unpack];
        if (ChangesInstallImage(r))
        {
            if (ChangesApps(r)) stages.Add(MediaStage.Apps);
            if (r.Settings.Any) stages.Add(MediaStage.Settings);
            stages.Add(MediaStage.Shrink);
        }
        if (r.Compression == MediaCompression.Recovery) stages.Add(MediaStage.Compress);
        stages.AddRange([MediaStage.Files, MediaStage.Iso, MediaStage.Verify]);
        return stages;
    }

    /// <summary>Whether install.wim is mounted for changes: when apps are removed or settings are written.</summary>
    private static bool ChangesInstallImage(MediaRequest r) => ChangesApps(r) || r.Settings.Any;

    private static bool ChangesApps(MediaRequest r) => r.RemoveApps.Count > 0 || r.RemoveUnreviewed.Count > 0;

    /// <summary>Runs the build and returns the verified ISO with its size and SHA-256. A wrong request throws <see cref="ArgumentException"/> before any step.</summary>
    public Task<MediaBuildResult> RunAsync(MediaRequest r, IProgress<MediaProgress> p, CancellationToken ct)
    {
        Validate(r);
        return new Run(steps, r, p, RetryDelay, ct).ExecuteAsync();
    }

    /// <summary>
    /// The engine's check of a request, which <see cref="RunAsync"/> runs first; --build-media calls it before the build
    /// to tell a refused request from a failure inside the build. Throws <see cref="ArgumentException"/>.
    /// </summary>
    public static void Validate(MediaRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(r.Source);
        ArgumentNullException.ThrowIfNull(r.Answers);
        ArgumentNullException.ThrowIfNull(r.RemoveApps);
        var s = r.Source;
        if ((s.Download is null) == (s.IsoPath is null)) throw new ArgumentException("Give exactly one source: a download or an ISO file.", nameof(r));
        // x64 only in this version: ARM64 media cannot be tested here.
        if (s.Download is { } d && !Same(d.Architecture, "x64")) throw new ArgumentException("Only x64 media can be built.", nameof(r));
        if (r.Answers.Architecture != "amd64") throw new ArgumentException("Only x64 media can be built: the answer file needs amd64.", nameof(r));
        if (s.IsoPath is { } iso && !Path.IsPathFullyQualified(iso)) throw new ArgumentException("The ISO path must be absolute.", nameof(r));
        if (s.Download is not null && r.IsoIndex is not null) throw new ArgumentException("An image index belongs to an own ISO only.", nameof(r));
        if (r.IsoIndex is < 1) throw new ArgumentException("Image indexes start at 1.", nameof(r));
        if (string.IsNullOrWhiteSpace(r.Edition)) throw new ArgumentException("No edition.", nameof(r));
        var apps = CatalogData.Current.Appx.Apps;
        foreach (var name in r.RemoveApps)
            if (!apps.Any(a => Same(a.Name, name))) throw new ArgumentException($"{name} is not in the app list.", nameof(r));
        ArgumentNullException.ThrowIfNull(r.RemoveUnreviewed);
        // The same rules as Remove apps for apps nobody reviewed: never protected, a part of Windows or what games need.
        var appx = CatalogData.Current.Appx;
        foreach (var name in r.RemoveUnreviewed)
            if (!DebloatService.IsSafeName(name) || apps.Any(a => a.Matches(name)) || appx.IsProtected(name) || appx.IsUnreviewedExcluded(name))
                throw new ArgumentException($"{name} cannot be removed as an app nobody reviewed.", nameof(r));
        ArgumentNullException.ThrowIfNull(r.Settings);
        // Later upgrades without the hardware checks only belong to media that skips them at the installation too.
        if (r.Settings.AllowUnsupportedUpgrades && !r.Answers.AllowUnsupported)
            throw new ArgumentException("Allowing unsupported upgrades needs Allow unsupported PCs.", nameof(r));
        // Only keyboards and locales Windows knows reach the answer file (Setup ignores or stops at others).
        if (!MediaLocales.IsInputLocale(r.Answers.InputLocale)) throw new ArgumentException($"{r.Answers.InputLocale} is not an input profile.", nameof(r));
        foreach (var locale in new[] { r.Answers.UiLanguage, r.Answers.SystemLocale, r.Answers.UserLocale })
            if (!MediaLocales.IsLocaleName(locale)) throw new ArgumentException($"{locale} is not a locale Windows knows.", nameof(r));
        AnswerFile.Build(r.Answers);   // throws ArgumentException for an invalid account name
        var tunero = r.TuneroExe is not null;
        if (tunero != (r.FirstSignIn is not null) || tunero != r.Answers.FirstSignInTunero)
            throw new ArgumentException("The Tunero copy, its first sign-in file and the answer file entry go together.", nameof(r));
        if (r.TuneroExe is { } exe && (!Path.IsPathFullyQualified(exe) || !File.Exists(exe)))
            throw new ArgumentException("The Tunero exe must be an existing file with an absolute path.", nameof(r));
        if (r.FirstSignIn is { } first && !FirstSignIn.IsValid(first))
            throw new ArgumentException("The first sign-in file names an unknown profile, language or theme.", nameof(r));
    }

    /// <summary>
    /// Removes what killed or crashed builds left: images still mounted in a work folder are discarded (remounted first
    /// where DISM needs that, also after a restart of the PC: <see cref="DiscardMountsAsync"/>), then the work folders at
    /// the root of every local fixed drive are deleted. A folder whose lock file is held (a build that still
    /// runs, in this or another Tunero) is left alone with its mount, and so is a folder whose image stays mounted.
    /// Without leftovers this only lists the mounted images and changes nothing. Never throws: DISM failures are logged
    /// (without the list of mounted images nothing is deleted) and a stop ends it quietly.
    /// <para>
    /// DISM is not ended with a killed build: it goes on mounting (or unmounting) in the work folder for minutes, while
    /// <c>/Get-MountedImageInfo</c> does not list that mount yet, and a delete would then reach into it. So the cleanup
    /// first waits for every dism.exe whose command line names a work folder no running build holds, up to
    /// <see cref="DismWaitLimit"/>; a folder whose DISM is still busy after that is left alone for the next start, and so
    /// is every leftover while a dism.exe runs whose command line cannot be read. DISM is never ended.
    /// </para>
    /// <para>
    /// After a hard restart of the PC DISM can list no mount at all while the mount folder still holds the image, and a
    /// delete is then refused inside it (VM 4b, 2026-10-20: "No mounted images found.", /Remount-Image refused, a manual
    /// /Cleanup-Mountpoints unmounted it). So when a leftover's mount folder is not empty although DISM lists no mount in
    /// the folder, or when a delete is refused while a mount folder is not empty, /Cleanup-Mountpoints runs (unless it ran
    /// in this cleanup already, also for a discard) and the list is read again before anything is deleted; a folder whose
    /// image is listed then is discarded or kept as above.
    /// </para>
    /// </summary>
    public async Task CleanLeftoversAsync(CancellationToken ct)
    {
        try
        {
            await CleanLeftoversCoreAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppLog.Info("media", "cleanup after an earlier build stopped");
        }
        catch (Exception ex) when (ex is DismException or IOException or UnauthorizedAccessException)
        {
            Warn(steps, "Cleanup after an earlier build failed: " + ex.Message);
        }
    }

    private async Task CleanLeftoversCoreAsync(CancellationToken ct)
    {
        if (await WaitForDismAsync(ct) is not { } busy) return;
        var mounted = await steps.Dism.MountedImagesAsync(ct);
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (folder is not null && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
        }
        foreach (var image in mounted) Add(WorkFolderOf(image.Dir));
        foreach (var drive in steps.LocalDrives())
            foreach (var folder in steps.LeftoverWorkFolders(drive))
                if (IsWorkFolderName(Path.GetFileName(folder.TrimEnd('\\')))) Add(folder.TrimEnd('\\'));
        if (folders.Count == 0) return;

        steps.Log("Cleaning up after an earlier build: " + string.Join(", ", folders));
        foreach (var running in folders.Where(IsHeldByABuild).ToList())
        {
            steps.Log($"{running} belongs to a build that is still running; left alone");
            folders.Remove(running);
        }
        foreach (var working in folders.Where(f => busy.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Warn(steps, $"{working} left alone: DISM still works in it after {DismWaitLimit.TotalMinutes:0} minutes; the next start tries again");
            folders.Remove(working);
        }
        // A SYSTEM hive a killed build left loaded holds a file of its mounted boot.wim open: unloaded first, or the discard fails.
        steps.UnloadLeftoverHives();
        var cleanedUp = false;
        var lost = folders.Where(f => !mounted.Any(m => IsInside(m.Dir, f)) && HasMountContent(f)).ToList();
        if (lost.Count > 0)
        {
            foreach (var folder in lost) steps.Log($"No mount listed for {folder}, but its mount folder is not empty: cleaning up DISM mount points");
            mounted = await CleanUpMountPointsAsync(steps, ct);
            cleanedUp = true;
        }
        var ours = mounted.Where(m => folders.Any(f => IsInside(m.Dir, f))).ToList();
        LogMounts(steps, ours);
        var still = mounted;
        if (ours.Count > 0)
        {
            still = (await DiscardMountsAsync(steps, ours, async dir =>
            {
                try
                {
                    await LoggedAsync(steps, DismArgs.Unmount(dir, commit: false), () => steps.Dism.UnmountAsync(dir, false, null, ct));
                    return true;
                }
                catch (DismException ex)
                {
                    AppLog.Warn("media", $"leftover image at {dir} could not be discarded: {ex.Message}");
                    return false;
                }
            }, cleanUpAlways: true, ct))!;
            cleanedUp = true;
            LogMounts(steps, still.Where(m => folders.Any(f => IsInside(m.Dir, f))));
        }
        foreach (var folder in folders)
        {
            if (StillMounted(folder)) continue;
            var refused = TryDelete(folder);
            // Refused while a mount folder has content: an image that DISM did not list may still be mounted there.
            if (!cleanedUp && (refused is IOException or UnauthorizedAccessException) && HasMountContent(folder))
            {
                steps.Log($"Deleting {folder} was refused while its mount folder is not empty: cleaning up DISM mount points");
                still = await CleanUpMountPointsAsync(steps, ct);
                cleanedUp = true;
                if (StillMounted(folder)) continue;
                refused = TryDelete(folder);
            }
            if (refused is not null) Warn(steps, $"{folder} left in place: {refused.Message}");
        }

        bool StillMounted(string folder)
        {
            if (!still.Any(m => IsInside(m.Dir, folder))) return false;
            Warn(steps, $"{folder} kept: an image is still mounted in it");
            return true;
        }

        Exception? TryDelete(string folder)
        {
            try
            {
                steps.DeleteWorkFolder(folder);
                steps.Log("Deleted " + folder);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return ex;
            }
        }
    }

    /// <summary>
    /// True when the mount folder or the boot.wim mount folder of <paramref name="folder"/> has an entry: an image may be
    /// mounted there that DISM does not list. Reads one entry at most and follows no link: a mount folder that is a link,
    /// or lies in one, is not looked into (DeleteWorkFolder only unlinks it). A mount folder that cannot be read counts as
    /// not empty, which only leads to /Cleanup-Mountpoints.
    /// </summary>
    private static bool HasMountContent(string folder)
    {
        foreach (var name in new[] { MountFolder, BootMountFolder })
        {
            var dir = Path.Combine(folder, name);
            if (!SafeDelete.HasNoLinks(dir)) continue;
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }).Any()) return true;
            }
            catch (DirectoryNotFoundException)
            {
                // No mount folder (yet): nothing to look at.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Waits until no dism.exe works in a leftover work folder any more, or <see cref="DismWaitLimit"/> has passed; returns
    /// the folders DISM still works in then (empty when it ended). A folder counts when a dism.exe's command line names it,
    /// unless a running build holds it (its own DISM). DISM mounts only into a work folder that exists, so the folders at
    /// the drive roots are all it can name. Null when a dism.exe runs whose command line cannot be read: any leftover may
    /// be its, so this start leaves them all alone (logged) and the next one tries again.
    /// </summary>
    private async Task<IReadOnlyList<string>?> WaitForDismAsync(CancellationToken ct)
    {
        var leftovers = steps.LocalDrives()
            .SelectMany(steps.LeftoverWorkFolders)
            .Select(f => f.TrimEnd('\\'))
            .Where(f => IsWorkFolderName(Path.GetFileName(f)))
            .ToList();
        var clock = Stopwatch.StartNew();
        var busy = DismBusyFolders(leftovers);
        if (busy is null) return Unreadable();
        if (busy.Count == 0) return [];
        steps.Log("Waiting for DISM to finish with " + string.Join(", ", busy));
        while (true)
        {
            if (clock.Elapsed >= DismWaitLimit) return busy;
            ct.ThrowIfCancellationRequested();
            await Task.Delay(DismPollDelay, ct);
            busy = DismBusyFolders(leftovers);
            if (busy is null) return Unreadable();
            if (busy.Count == 0)
            {
                steps.Log($"DISM finished after {clock.Elapsed.TotalSeconds:0} s");
                return [];
            }
        }

        IReadOnlyList<string>? Unreadable()
        {
            var alone = leftovers.Where(f => !IsHeldByABuild(f)).ToList();
            if (alone.Count > 0)
                Warn(steps, $"Left alone: {string.Join(", ", alone)} (DISM runs, but its command line cannot be read; the next start tries again)");
            return null;
        }
    }

    /// <summary>The leftovers that a running dism.exe names and no running build holds; null when DISM runs unreadable.</summary>
    private List<string>? DismBusyFolders(IReadOnlyList<string> leftovers)
    {
        if (steps.DismCommandLines() is not { } lines) return null;
        return leftovers.Where(f => lines.Any(line => Names(line, f)) && !IsHeldByABuild(f)).ToList();
    }

    /// <summary>Whether <paramref name="commandLine"/> names <paramref name="folder"/> or a path in it, without case.</summary>
    private static bool Names(string commandLine, string folder)
    {
        for (var at = commandLine.IndexOf(folder, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = commandLine.IndexOf(folder, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + folder.Length;
            var before = at == 0 ? ' ' : commandLine[at - 1];
            var after = end == commandLine.Length ? ' ' : commandLine[end];
            if ((char.IsWhiteSpace(before) || before is '"' or ':' or '=') && (char.IsWhiteSpace(after) || after is '\\' or '"')) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the folder's lock file exists and cannot be opened: a running build holds it. A lock file that is a link,
    /// or lies in one, is never opened (the folder may be anyone's): such a folder is no Tunero build, and
    /// DeleteWorkFolder then only unlinks or refuses it.
    /// </summary>
    private static bool IsHeldByABuild(string folder)
    {
        var lockFile = Path.Combine(folder, LockFileName);
        if (!SafeDelete.HasNoLinks(lockFile)) return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open, or not readable: leave it rather than risk a build's files.
            return true;
        }
    }

    private static bool IsInside(string path, string folder) =>
        string.Equals(path.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The names Setup looks for in its implicit search (Autounattend.xml, Unattend.xml), without case.</summary>
    private static bool IsAnswerFileName(string fileName) => Same(fileName, "autounattend.xml") || Same(fileName, "unattend.xml");

    private static void Warn(IMediaSteps steps, string message)
    {
        steps.Log(message);
        AppLog.Warn("media", message);
    }

    /// <summary>Runs one DISM command and writes its command line and exit code to the build log.</summary>
    /// <summary>DISM error: the image has pending operations that only finish at its first start.</summary>
    internal const int PendingOperations = unchecked((int)0x800F0806);

    private static async Task<T> LoggedAsync<T>(IMediaSteps steps, string arguments, Func<Task<T>> call)
    {
        steps.Log("dism " + arguments);
        try
        {
            var result = await call();
            steps.Log("dism exit code 0");
            return result;
        }
        catch (DismException ex)
        {
            var code = DismOutput.ErrorCode(ex.Output) is { } c ? $" (error 0x{c:x8})" : "";
            var output = ex.Output.ReplaceLineEndings(" ").Trim();
            steps.Log($"dism exit code {ex.ExitCode}{code}: {(output.Length > 600 ? output[^600..] : output)}");
            throw;
        }
        catch (OperationCanceledException)
        {
            steps.Log("dism stopped");
            throw;
        }
    }

    private static Task LoggedAsync(IMediaSteps steps, string arguments, Func<Task> call) =>
        LoggedAsync(steps, arguments, async () =>
        {
            await call();
            return true;
        });

    private static void LogMounts(IMediaSteps steps, IEnumerable<MountedImage> mounts)
    {
        foreach (var m in mounts) steps.Log($"Mounted image {m.Dir}: status {m.Status ?? "not shown"}");
    }

    /// <summary>
    /// Discards the mounted images of a work folder (<paramref name="discard"/> logs and never throws), with Microsoft's
    /// steps for a mount that cannot simply be discarded ("To delete a working directory" in WinPE: Mount and Customize,
    /// learn.microsoft.com/windows-hardware/manufacture/desktop/winpe-mount-and-customize): /Remount-Image, then
    /// /Unmount-Image /Discard, then /Cleanup-Mountpoints. An image whose Status is not Ok is remounted first: "Needs
    /// Remount" follows a restart of the PC while it was mounted (Take Inventory of an Image or Component Using DISM), and
    /// /Cleanup-Mountpoints does not delete images that /Remount-Image can recover (DISM Image Management Command-Line
    /// Options, .../dism-image-management-command-line-options-s14). Any other image is remounted only when its discard
    /// fails, and discarded again. /Cleanup-Mountpoints runs when a discard failed, or always with
    /// <paramref name="cleanUpAlways"/>; then the images DISM still lists are returned (a DismException of that list goes
    /// to the caller: without it nothing may be deleted). Null when every image was discarded and nothing more ran.
    /// </summary>
    private static async Task<IReadOnlyList<MountedImage>?> DiscardMountsAsync(IMediaSteps steps, IReadOnlyList<MountedImage> mounts,
        Func<string, Task<bool>> discard, bool cleanUpAlways, CancellationToken ct)
    {
        var failed = false;
        foreach (var mount in mounts)
        {
            // Each step is tried even when the one before failed, as Microsoft's list says: "Try each of the following steps".
            if (mount.NeedsRecovery)
            {
                await RemountAsync(steps, mount.Dir, ct);
                if (!await discard(mount.Dir)) failed = true;
                continue;
            }
            if (await discard(mount.Dir)) continue;
            if (await RemountAsync(steps, mount.Dir, ct) && await discard(mount.Dir)) continue;
            failed = true;
        }
        if (!failed && !cleanUpAlways) return null;
        return await CleanUpMountPointsAsync(steps, ct);
    }

    /// <summary>
    /// /Cleanup-Mountpoints (a failure is only logged), then the images DISM lists now. A DismException of that list goes to
    /// the caller: without it nothing may be deleted.
    /// </summary>
    private static async Task<IReadOnlyList<MountedImage>> CleanUpMountPointsAsync(IMediaSteps steps, CancellationToken ct)
    {
        try
        {
            await LoggedAsync(steps, DismArgs.CleanupMountpoints(), () => steps.Dism.CleanupMountpointsAsync(ct));
        }
        catch (DismException ex)
        {
            AppLog.Warn("media", $"DISM cleanup of mount points failed: {ex.Message}");
        }
        return await LoggedAsync(steps, DismArgs.MountedImageInfo(), () => steps.Dism.MountedImagesAsync(ct));
    }

    /// <summary>/Remount-Image for one image, logged; false when DISM fails.</summary>
    private static async Task<bool> RemountAsync(IMediaSteps steps, string dir, CancellationToken ct)
    {
        try
        {
            await LoggedAsync(steps, DismArgs.Remount(dir), () => steps.Dism.RemountAsync(dir, ct));
            return true;
        }
        catch (DismException ex)
        {
            AppLog.Warn("media", $"image at {dir} could not be remounted: {ex.Message}");
            return false;
        }
    }

    /// <summary>The state of one build, so a failure knows what to undo.</summary>
    private sealed class Run(IMediaSteps steps, MediaRequest r, IProgress<MediaProgress>? progress, TimeSpan retryDelay, CancellationToken ct)
    {
        private readonly Stopwatch _clock = new();
        private MediaStage _stage;
        private string? _work, _media, _mount, _bootMount, _workIso, _sha256;
        private long _isoBytes;
        private WorkFolder? _workFolder;

        /// <summary>The folder an image is mounted in right now (from the start of the mount on), or null. A failure or Stop discards it.</summary>
        private string? _mounted;

        /// <summary>The page's choices with Setup's language set to one the chosen image has (<see cref="AnswersFor"/>).</summary>
        private AnswerChoices _answers = r.Answers;

        private string Sources => Path.Combine(_media!, "sources");
        private string InstallWim => Path.Combine(Sources, "install.wim");
        private string InstallEsd => Path.Combine(Sources, "install.esd");

        /// <summary>The install image the media ends with: install.esd after the Compress stage, else install.wim.</summary>
        private string InstallImageName => r.Compression == MediaCompression.Recovery ? "install.esd" : "install.wim";
        private string BootWim => Path.Combine(Sources, "boot.wim");
        private string TuneroFolder => Path.Combine(_media!, TuneroOnMedia);

        public async Task<MediaBuildResult> ExecuteAsync()
        {
            var download = r.Source.Download;
            Begin(download is not null ? MediaStage.Download : MediaStage.Unpack);
            steps.Log($"Build from {download?.FileName ?? r.Source.IsoPath}, edition {r.Edition}{(r.IsoIndex is { } i ? $" (index {i})" : "")}, to {r.OutputIso}");
            try
            {
                CheckOutputAndSpace();
                // Comes with its lock file held: CleanLeftoversAsync of another Tunero leaves it alone from the start.
                _workFolder = steps.NewWorkFolder(r.OutputIso);
                _work = _workFolder.Path;
                if (!Path.IsPathFullyQualified(_work)) throw Fail($"The work folder {_work} is not an absolute path.", "Media_WorkFolderFailed");
                steps.Log("Work folder " + _work);
                _media = Path.Combine(_work, "media");
                _mount = Path.Combine(_work, MountFolder);
                _bootMount = Path.Combine(_work, BootMountFolder);

                string edition;
                if (download is not null)
                {
                    var esd = Path.Combine(_work, "image.esd");
                    var stage = _stage;
                    await steps.DownloadAsync(download, esd,
                        new Relay<(long Done, long Total)>(v => Report(stage, v.Total > 0 ? v.Done * 100.0 / v.Total : null, null)), ct);
                    Begin(MediaStage.Check);
                    var images = await CheckEsdAsync(esd);
                    Begin(MediaStage.Unpack);
                    edition = await UnpackDownloadAsync(esd, images);
                }
                else
                {
                    edition = await UnpackIsoAsync();
                }
                var build = await CheckImagesAsync(edition);

                if (ChangesInstallImage(r)) await ChangeInstallImageAsync();
                if (r.Compression == MediaCompression.Recovery) await CompressAsync(edition);
                Begin(MediaStage.Files);
                await WriteFilesAsync();
                Begin(MediaStage.Iso);
                await WriteIsoAsync(build);
                Begin(MediaStage.Verify);
                await VerifyAsync();
                Publish();
            }
            catch (Exception ex)
            {
                var failed = _stage;
                var stopped = ex is OperationCanceledException && ct.IsCancellationRequested;
                steps.Log(stopped
                    ? $"Stopped in stage {failed} after {_clock.Elapsed:hh\\:mm\\:ss}"
                    : $"{failed} failed after {_clock.Elapsed:hh\\:mm\\:ss}: {ex.GetType().Name}: {ex.Message}");
                await CleanUpAfterFailureAsync();
                if (stopped) throw;
                throw Wrap(failed, ex);
            }

            // The verified ISO is at the output path: nothing from here on may fail the build or touch it.
            Begin(MediaStage.Cleanup);
            ReleaseLock();
            if (!await DeleteWorkFolderAsync()) Warn(steps, $"work folder {_work} stays until the next start of Tunero removes it");
            steps.Log($"Cleanup done in {_clock.Elapsed:hh\\:mm\\:ss}");
            steps.Log("Build finished: " + r.OutputIso);
            return new MediaBuildResult(r.OutputIso, _isoBytes, _sha256!) { LogPath = steps.LogPath };
        }

        private void Begin(MediaStage stage)
        {
            if (_clock.IsRunning) steps.Log($"{_stage} done in {_clock.Elapsed:hh\\:mm\\:ss}");
            _stage = stage;
            _clock.Restart();
            steps.Log($"{stage} started");
            Report(stage, null, null);
        }

        /// <summary>A failing progress sink is logged, never allowed to break the build or its cleanup.</summary>
        private void Report(MediaStage stage, double? percent, string? detail)
        {
            try
            {
                progress?.Report(new MediaProgress(stage, percent, detail));
            }
            catch (Exception ex)
            {
                steps.Log($"Progress report failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private MediaBuildException Fail(string reason, string? key, int? code = null) =>
            new(_stage, $"{reason} Log: {steps.LogPath}", code, key) { LogPath = steps.LogPath };

        private MediaBuildException Wrap(MediaStage failed, Exception ex)
        {
            if (ex is MediaBuildException { LogPath: not null } own && own.Stage == failed) return own;
            var code = ex switch
            {
                MediaBuildException m => m.Code,
                DismException d => DismOutput.ErrorCode(d.Output) ?? d.ExitCode,
                IsoAttachException a => a.Win32Error,
                _ => (int?)null,
            };
            var key = ex switch
            {
                MediaBuildException m => m.Key,
                IsoAttachException { InUse: true } => "Media_IsoInUse",
                _ => null,
            };
            // An exception of a step keeps its cause inside, so InUse still sees an IsoAttachException.
            var inner = ex is MediaBuildException { InnerException: { } cause } ? cause : ex;
            return new MediaBuildException(failed, $"{ex.Message} Log: {steps.LogPath}", code, key, inner) { LogPath = steps.LogPath };
        }

        private IProgress<double> Percent(string? detail)
        {
            var stage = _stage;
            return new Relay<double>(v => Report(stage, v, detail));
        }

        private IProgress<double> Fraction(string? detail)
        {
            var stage = _stage;
            return new Relay<double>(v => Report(stage, v * 100, detail));
        }

        private Task<T> Dism<T>(string arguments, Func<Task<T>> call) => LoggedAsync(steps, arguments, call);

        private Task Dism(string arguments, Func<Task> call) => LoggedAsync(steps, arguments, call);

        private void CheckOutputAndSpace()
        {
            var output = r.OutputIso;
            if (!Path.IsPathFullyQualified(output) || !Same(Path.GetExtension(output), ".iso") || Directory.Exists(output) ||
                Path.GetDirectoryName(output) is not { } folder || !Directory.Exists(folder))
                throw Fail($"{output} is not a usable .iso file path in an existing folder.", "Media_OutputInvalid");
            CheckOutputCanBeReplaced(output, folder);
            long size;
            if (r.Source.Download is { } download)
            {
                size = download.Size;
            }
            else
            {
                var iso = Path.GetFullPath(r.Source.IsoPath!);
                if (Same(iso, Path.GetFullPath(output))) throw Fail("The output file is the source ISO.", "Media_OutputInvalid");
                var info = new FileInfo(iso);
                if (!info.Exists) throw Fail($"{iso} does not exist.", "Media_SourceMissing");
                size = info.Length;
            }
            var needed = NeededBytes(r.Source, size, r.Compression);
            var free = steps.FreeBytes(output);
            steps.Log($"Space: {needed} bytes needed, {free} free");
            if (free < needed) throw Fail($"The output drive has {free} bytes free; the build needs {needed}.", "Media_NotEnoughSpace");
        }

        /// <summary>
        /// What would only fail at the very end, after an hour of work, is refused before anything runs: an output folder
        /// that is or lies in a link, and an existing output file that is a link, read-only, or open in a program that does
        /// not let others delete it (an ISO mounted in Explorer). The final move checks the folder again by its handle.
        /// </summary>
        private void CheckOutputCanBeReplaced(string output, string folder)
        {
            if (!SafeDelete.HasNoLinks(folder)) throw Fail($"The output folder {folder} is or lies in a link.", "Media_OutputLink");
            if (SafeDelete.AttributesAndTag(output) is not { } existing) return;   // no file of that name (or it cannot be read)
            if ((existing.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw Fail($"{output} is a link.", "Media_OutputLink");
            if ((existing.Attributes & (uint)FileAttributes.ReadOnly) != 0) throw Fail($"{output} is read-only.", "Media_OutputReadOnly");
            // The final rename needs to delete the old file: open it for that, without following a link, and close it again.
            using var probe = Interop.Native.CreateFile(@"\\?\" + Path.GetFullPath(output), 0x00010000 /* DELETE */, 0x7 /* share all */, IntPtr.Zero,
                Interop.Native.OpenExisting, 0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (probe.IsInvalid)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error is 32 or 33) throw Fail($"{output} is open in another program (error {error}).", "Media_OutputInUse", error);
                throw Fail($"{output} cannot be replaced (error {error}).", "Media_OutputInvalid", error);
            }
        }

        /// <summary>
        /// The ESD's hash was checked by the download; this checks its layout, which does not depend on the language (the
        /// image names do): index 1 is the setup files without an edition, 2 and 3 are x64 WindowsPE images, editions follow.
        /// </summary>
        private async Task<IReadOnlyList<WimImage>> CheckEsdAsync(string esd)
        {
            var images = DismOutput.ParseWimInfo(await Dism(DismArgs.WimInfo(esd, null), () => steps.Dism.GetWimInfoAsync(esd, null, ct)));
            steps.Log($"{images.Count} images: " + string.Join("; ", images.Select(i => $"{i.Index} {i.Name}")));
            if (images.Count < 4 || !new[] { 1, 2, 3 }.All(n => images.Any(i => i.Index == n)))
                throw Fail($"The downloaded file is not Windows setup media ({images.Count} images).", "Media_NotWindows11");
            var (setup, _) = await DetailAsync(esd, 1);
            if (setup.Edition is not (null or "<undefined>"))
                throw Fail($"Image 1 of the download is {setup.Name} (edition {setup.Edition}), not the setup files.", "Media_NotWindows11");
            foreach (var index in new[] { 2, 3 })
            {
                var (boot, _) = await DetailAsync(esd, index);
                if (!Same(boot.Edition, "WindowsPE") || !Same(boot.Architecture, "x64"))
                    throw Fail($"Image {index} of the download is {boot.Name} ({boot.Edition}, {boot.Architecture}), not an x64 boot image.", "Media_NotWindows11");
            }
            return images;
        }

        /// <summary>Index 1 is the setup files, 2 and 3 the boot images (3 is Setup and boots), the editions follow.</summary>
        private async Task<string> UnpackDownloadAsync(string esd, IReadOnlyList<WimImage> images)
        {
            Directory.CreateDirectory(_media!);
            await Dism(DismArgs.Apply(esd, 1, _media!), () => steps.Dism.ApplyAsync(esd, 1, _media!, Percent("setup"), ct));
            RequireUefiBoot();
            Directory.CreateDirectory(Sources);
            // DISM adds to an existing destination file: the images must start from nothing.
            if (File.Exists(BootWim) || File.Exists(InstallWim) || File.Exists(Path.Combine(Sources, "install.esd")))
                throw Fail("The setup files already hold a Windows image.", "Media_ImageCheckFailed");
            await Dism(DismArgs.Export(esd, 2, BootWim, false), () => steps.Dism.ExportAsync(esd, 2, BootWim, false, Percent("boot.wim"), ct));
            await Dism(DismArgs.Export(esd, 3, BootWim, true), () => steps.Dism.ExportAsync(esd, 3, BootWim, true, Percent("boot.wim"), ct));
            var chosen = await FindEditionAsync(esd, images.Where(i => i.Index > 3), byName: false);
            await Dism(DismArgs.Export(esd, chosen.Index, InstallWim, false), () => steps.Dism.ExportAsync(esd, chosen.Index, InstallWim, false, Percent("install.wim"), ct));
            File.Delete(esd);
            return chosen.Edition!;
        }

        /// <summary>Copies the ISO and replaces its install.wim or install.esd with the chosen image alone.</summary>
        private async Task<string> UnpackIsoAsync()
        {
            await steps.IsoReader.CopyMediaAsync(r.Source.IsoPath!, _media!, Fraction(null), ct);
            RemoveOldSetupFiles();
            RequireUefiBoot();
            var wim = InstallWim;
            var esd = Path.Combine(Sources, "install.esd");
            var source = (File.Exists(wim), File.Exists(esd)) switch
            {
                (true, false) => wim,
                (false, true) => esd,
                (true, true) => throw Fail("The ISO has both sources\\install.wim and sources\\install.esd.", "Media_NotWindows11"),
                _ => throw Fail("The ISO has no sources\\install.wim or sources\\install.esd.", "Media_NotWindows11"),
            };
            if (!File.Exists(BootWim)) throw Fail("The ISO has no sources\\boot.wim.", "Media_NotWindows11");
            var images = DismOutput.ParseWimInfo(await Dism(DismArgs.WimInfo(source, null), () => steps.Dism.GetWimInfoAsync(source, null, ct)));
            steps.Log($"{images.Count} images: " + string.Join("; ", images.Select(i => $"{i.Index} {i.Name}")));

            WimImage chosen;
            if (r.IsoIndex is { } index)
            {
                (chosen, _) = await DetailAsync(source, index);
                if (!Same(chosen.Edition, r.Edition) && !Same(chosen.Name, r.Edition))
                    throw Fail($"Image {index} is {chosen.Name} ({chosen.Edition}), not {r.Edition}.", "Media_EditionMissing");
                CheckWindows11(chosen);
            }
            else
            {
                chosen = await FindEditionAsync(source, images, byName: true);
            }
            var exported = Path.Combine(_work!, "install.wim");
            await Dism(DismArgs.Export(source, chosen.Index, exported, false), () => steps.Dism.ExportAsync(source, chosen.Index, exported, false, Percent("install.wim"), ct));
            File.SetAttributes(source, FileAttributes.Normal);
            File.Delete(source);
            File.Move(exported, wim);
            return chosen.Edition!;
        }

        /// <summary>
        /// An own ISO may carry an answer file or a Tunero copy from an earlier build (or from elsewhere): they would run on
        /// the new PC although this build did not choose them. Removed right after the copy, checked gone, fail closed.
        /// </summary>
        private void RemoveOldSetupFiles()
        {
            var oem = Path.Combine(Sources, "$OEM$");
            if (Directory.Exists(oem))
            {
                var carried = Directory.EnumerateFiles(oem, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                    .Select(f => Path.GetRelativePath(_media!, f));
                steps.Log(@"The ISO carries under sources\$OEM$: " + string.Join(", ", carried));
            }
            foreach (var file in OldAnswerFiles())
            {
                steps.Log($"Removing {Path.GetRelativePath(_media!, file)} of the ISO");
                try
                {
                    SafeDelete.DeleteFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw Fail($"{Path.GetRelativePath(_media!, file)} on the ISO could not be removed: {ex.Message}", "Media_FilesCheckFailed");
                }
            }
            if (Path.Exists(TuneroFolder))
            {
                steps.Log($"Removing {TuneroOnMedia} of the ISO");
                SecureFolder.DeleteTree(TuneroFolder);
            }
            if (OldAnswerFiles().Count > 0 || Path.Exists(TuneroFolder))
                throw Fail("An answer file or Tunero copy on the ISO could not be removed.", "Media_FilesCheckFailed");
        }

        /// <summary>
        /// Answer files of the media: at its root and in sources (<see cref="WriteBootWimAsync"/>), and those that $OEM$
        /// copies into Panther, Sysprep and the root of the Windows drive ($1), where Setup also picks them up. Names
        /// compare without case.
        /// </summary>
        private List<string> OldAnswerFiles()
        {
            var windows = Path.Combine(Sources, "$OEM$", "$$");
            return
            [
                .. Named(_media!, IsAnswerFileName),
                .. Named(Sources, IsAnswerFileName),
                .. Named(Path.Combine(windows, "Panther"), IsAnswerFileName),
                .. Named(Path.Combine(windows, @"Panther\Unattend"), IsAnswerFileName),
                .. Named(Path.Combine(windows, @"System32\Sysprep"), n => Same(n, "unattend.xml")),
                .. Named(Path.Combine(Sources, "$OEM$", "$1"), IsAnswerFileName),
            ];
        }

        private void RequireUefiBoot()
        {
            if (!IsoBoot.HasUefiBoot(_media!))
                throw Fail($"The media has no UEFI boot files ({IsoBoot.UefiImage}, {IsoBoot.UefiLoader}).", "Media_NoUefiBoot");
        }

        /// <summary>
        /// The one image whose Edition (or, for an own ISO, Name) is the requested one. ParseWimInfo drops entries it cannot
        /// read, so "none" and "more than one" both fail here instead of picking something.
        /// </summary>
        private async Task<WimImage> FindEditionAsync(string image, IEnumerable<WimImage> candidates, bool byName)
        {
            var found = new List<WimImage>();
            foreach (var candidate in candidates)
            {
                var (detail, _) = await DetailAsync(image, candidate.Index);
                steps.Log($"Image {detail.Index}: {detail.Name}, edition {detail.Edition}, {detail.Architecture}, {detail.Version}");
                if (Same(detail.Edition, r.Edition) || (byName && Same(detail.Name, r.Edition))) found.Add(detail);
            }
            if (found.Count == 0) throw Fail($"{Path.GetFileName(image)} has no image of edition {r.Edition}.", "Media_EditionMissing");
            if (found.Count > 1)
                throw Fail($"{Path.GetFileName(image)} has {found.Count} images of edition {r.Edition} ({string.Join(", ", found.Select(f => f.Index))}).", "Media_ImageCheckFailed");
            CheckWindows11(found[0]);
            return found[0];
        }

        private async Task<(WimImage Image, string Output)> DetailAsync(string image, int index)
        {
            var output = await Dism(DismArgs.WimInfo(image, index), () => steps.Dism.GetWimInfoAsync(image, index, ct));
            var detail = DismOutput.ParseWimInfo(output).Where(i => i.Index == index).ToList();
            if (detail.Count != 1) throw Fail($"DISM gave no readable details for image {index} of {Path.GetFileName(image)}.", "Media_ImageCheckFailed");
            return (detail[0], output);
        }

        private void CheckWindows11(WimImage image)
        {
            if (!Same(image.Architecture, "x64"))
                throw Fail($"Image {image.Index} ({image.Name}) is for {image.Architecture ?? "an unknown architecture"}; only x64 media can be built.", "Media_NotX64");
            if (!Version.TryParse(image.Version, out var version) || version < MinimumVersion)
                throw Fail($"Image {image.Index} ({image.Name}) has version {image.Version ?? "unknown"}; Windows 11 starts at {MinimumVersion}.", "Media_NotWindows11");
            if (string.IsNullOrEmpty(image.Edition)) throw Fail($"Image {image.Index} ({image.Name}) names no edition.", "Media_ImageCheckFailed");
        }

        /// <summary>
        /// The safety checks after unpacking: install.wim holds exactly the chosen edition as x64 Windows 11; boot.wim holds
        /// the two x64 boot images and its header names image 2 (Setup) as the one that boots. Returns the build number.
        /// </summary>
        private async Task<string> CheckImagesAsync(string edition)
        {
            var installed = DismOutput.ParseWimInfo(await Dism(DismArgs.WimInfo(InstallWim, null), () => steps.Dism.GetWimInfoAsync(InstallWim, null, ct)));
            if (installed.Count != 1) throw Fail($"install.wim holds {installed.Count} images, not 1.", "Media_ImageCheckFailed");
            var (image, _) = await DetailAsync(InstallWim, 1);
            if (!Same(image.Edition, edition)) throw Fail($"install.wim holds edition {image.Edition}, not {edition}.", "Media_ImageCheckFailed");
            CheckWindows11(image);
            _answers = AnswersFor(image);

            var boot = DismOutput.ParseWimInfo(await Dism(DismArgs.WimInfo(BootWim, null), () => steps.Dism.GetWimInfoAsync(BootWim, null, ct)));
            if (boot.Count != 2) throw Fail($"boot.wim holds {boot.Count} images, not 2.", "Media_ImageCheckFailed");
            CheckBootHeader();
            var (setup, _) = await DetailAsync(BootWim, 2);
            if (!Same(setup.Architecture, "x64")) throw Fail($"boot.wim is for {setup.Architecture}; only x64 media can be built.", "Media_NotX64");
            steps.Log($"Images checked: {image.Name} ({image.Edition}, {image.Architecture}, {image.Version}), boot.wim boots image 2");
            return Version.Parse(image.Version!).Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The header of boot.wim names image 2 (Setup) of its 2 images as the one that boots.</summary>
        private void CheckBootHeader()
        {
            // DISM's "WIM Bootable" line does not show this (see WimHeader); the header does.
            var header = WimHeader.Read(BootWim);
            if (header is not { ImageCount: 2, BootIndex: 2 })
                throw Fail($"boot.wim does not name image 2 as the one that boots (header: {header?.ImageCount} images, boot index {header?.BootIndex}).", "Media_ImageCheckFailed");
        }

        /// <summary>
        /// Setup's language must be one the image has: the page reads it from the ISO, but this is checked against what DISM
        /// says. A language the image lacks becomes the one DISM marks as its default (else the first it lists).
        /// </summary>
        private AnswerChoices AnswersFor(WimImage image)
        {
            var a = r.Answers;
            if (image.Languages.Count == 0 || image.Languages.Contains(a.UiLanguage, StringComparer.OrdinalIgnoreCase)) return a;
            var language = image.DefaultLanguage ?? image.Languages[0];
            steps.Log($"Setup language {language} (the image's) instead of {a.UiLanguage}");
            return a with { UiLanguage = language };
        }

        /// <summary>
        /// Every change to install.wim happens in this one mount, in this order: apps, then (later steps) parts and
        /// settings, then the component cleanup, then the health check and a single commit. One mount for all of them
        /// saves a mount, an unmount and a commit of several GB per extra change.
        /// </summary>
        private async Task ChangeInstallImageAsync()
        {
            var apps = ChangesApps(r);
            var settings = r.Settings.Any;
            // The mount belongs to the first stage that changes the image.
            Begin(apps ? MediaStage.Apps : MediaStage.Settings);
            await MountAsync(InstallWim, 1, _mount!, readOnly: false, "install.wim");
            var removed = apps ? await RemoveAppsInMountAsync() : 0;
            if (settings)
            {
                if (apps) Begin(MediaStage.Settings);
                WriteSettings();
            }
            Begin(MediaStage.Shrink);
            var cleaned = await ComponentCleanupAsync();
            var health = await Dism(DismArgs.CheckHealth(_mount!), () => steps.Dism.CheckHealthAsync(_mount!, ct));
            if (!DismOutput.Healthy(health)) throw Fail("The image reports component store corruption; the changes were not saved.", "Media_Unhealthy");
            // Nothing removed and nothing cleaned: the image stays exactly as it was.
            await UnmountAsync(_mount!, commit: removed > 0 || cleaned || settings, "install.wim");
        }

        /// <summary>
        /// Writes <see cref="MediaImageSettings.Values"/> into the hives of the mounted image, each hive loaded only for its
        /// values and unloaded again before the next step (a loaded hive holds its file of the mount open, so the commit
        /// and the discard would fail). Every value is read back; one that does not read back as written fails the build.
        /// </summary>
        private void WriteSettings()
        {
            // The key names carry the control set, which only the SYSTEM hive knows: it is loaded first.
            var controlSet = "";
            try
            {
                foreach (var hiveName in (string[])["SYSTEM", "SOFTWARE"])
                {
                    // Which hives are needed does not depend on the control set.
                    if (!MediaImageSettings.Values(r.Settings, "").Any(v => v.Hive == hiveName)) continue;
                    ct.ThrowIfCancellationRequested();
                    using var hive = steps.LoadWritableHive(Path.Combine(_mount!, @"Windows\System32\config", hiveName));
                    ct.ThrowIfCancellationRequested();
                    if (hiveName == "SYSTEM") controlSet = hive.ControlSet;
                    foreach (var (_, key, name, value) in MediaImageSettings.Values(r.Settings, controlSet).Where(v => v.Hive == hiveName))
                    {
                        hive.SetDword(key, name, value);
                        if (hive.GetDword(key, name) != value) throw new IOException($@"{hiveName}\{key}\{name} does not read back as {value}.");
                        steps.Log($@"Image registry: {hiveName}\{key}\{name} = {value}");
                    }
                    // A hive that stays loaded would hold its file when DISM cleans up and commits: fail here, not there.
                    hive.Unload();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw Fail($"The settings could not be written into the image: {ex.Message}", "Media_SettingsFailed");
            }
        }

        /// <summary>
        /// Smaller ISO: exports the one image of install.wim into sources\install.esd with recovery compression, checks that
        /// the new file holds exactly that image, then deletes install.wim, so the media ends with one install image.
        /// </summary>
        private async Task CompressAsync(string edition)
        {
            Begin(MediaStage.Compress);
            // DISM adds to an existing destination file: the export must start from nothing.
            if (File.Exists(InstallEsd)) throw Fail("sources\\install.esd exists before the export.", "Media_ImageCheckFailed");
            await Dism(DismArgs.ExportRecovery(InstallWim, 1, InstallEsd), () => steps.Dism.ExportRecoveryAsync(InstallWim, 1, InstallEsd, Percent("install.esd"), ct));
            var images = DismOutput.ParseWimInfo(await Dism(DismArgs.WimInfo(InstallEsd, null), () => steps.Dism.GetWimInfoAsync(InstallEsd, null, ct)));
            if (images.Count != 1) throw Fail($"install.esd holds {images.Count} images, not 1.", "Media_ImageCheckFailed");
            var (image, _) = await DetailAsync(InstallEsd, 1);
            if (!Same(image.Edition, edition)) throw Fail($"install.esd holds edition {image.Edition}, not {edition}.", "Media_ImageCheckFailed");
            File.SetAttributes(InstallWim, FileAttributes.Normal);
            File.Delete(InstallWim);
            steps.Log($"install.esd written ({new FileInfo(InstallEsd).Length} bytes), install.wim removed");
        }

        /// <summary>Removes the chosen provisioned apps from the mounted image and returns how many it removed.</summary>
        private async Task<int> RemoveAppsInMountAsync()
        {
            var catalog = CatalogData.Current.Appx;
            // Validate checked that every name is an allowlist entry.
            var entries = catalog.Apps.Where(a => r.RemoveApps.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var provisioned = await Dism(DismArgs.Provisioned(_mount!), () => steps.Dism.ProvisionedAsync(_mount!, ct));
            // Provisioned names are full package names (Name_Version_Arch_ResourceId_PublisherId); the allowlist has the Name.
            var remove = provisioned.Where(p => PackageName(p) is var name && !catalog.IsProtected(name)
                && (entries.Any(e => e.Matches(name)) || r.RemoveUnreviewed.Contains(name, StringComparer.OrdinalIgnoreCase))).ToList();
            steps.Log($"{provisioned.Count} provisioned apps, {remove.Count} to remove");
            for (var i = 0; i < remove.Count; i++)
            {
                var package = remove[i];
                Report(_stage, i * 100.0 / remove.Count, PackageName(package));
                await Dism(DismArgs.RemoveProvisioned(_mount!, package), () => steps.Dism.RemoveProvisionedAsync(_mount!, package, ct));
            }
            return remove.Count;
        }

        /// <summary>
        /// /StartComponentCleanup without /ResetBase: superseded component versions go, every installed update stays
        /// removable. Pending operations (0x800F0806) mean the image has not had its first start, so nothing can be cleaned
        /// yet; that is logged and the build goes on, never failed and never forced. Returns whether the cleanup ran.
        /// </summary>
        private async Task<bool> ComponentCleanupAsync()
        {
            try
            {
                await Dism(DismArgs.ComponentCleanup(_mount!), () => steps.Dism.ComponentCleanupAsync(_mount!, Percent("install.wim"), ct));
                return true;
            }
            catch (DismException ex) when (DismOutput.ErrorCode(ex.Output) == PendingOperations)
            {
                steps.Log("Component cleanup skipped: 0x800F0806, operations wait for the first start");
                return false;
            }
        }

        private static string PackageName(string fullName) => fullName.Split('_')[0];

        /// <summary>
        /// Mounts into an empty folder of the work folder. Counted as mounted from the start: a mount that fails or stops
        /// halfway is discarded too (<see cref="CleanUpAfterFailureAsync"/>).
        /// </summary>
        private async Task MountAsync(string image, int index, string folder, bool readOnly, string detail)
        {
            Directory.CreateDirectory(folder);
            _mounted = folder;
            await Dism(DismArgs.Mount(image, index, folder, readOnly), () => steps.Dism.MountAsync(image, index, folder, readOnly, Percent(detail), ct));
        }

        private async Task UnmountAsync(string folder, bool commit, string detail)
        {
            await Dism(DismArgs.Unmount(folder, commit), () => steps.Dism.UnmountAsync(folder, commit, Percent(detail), ct));
            _mounted = null;
        }

        /// <summary>
        /// The answer file into boot.wim when a choice needs it (<see cref="WriteBootWimAsync"/>), and Tunero with its first
        /// sign-in file; each read back and checked. Last, the media itself must hold no answer file.
        /// </summary>
        private async Task WriteFilesAsync()
        {
            await WriteBootWimAsync();
            if (r.TuneroExe is { } exe)
            {
                Directory.CreateDirectory(TuneroFolder);
                var copy = Path.Combine(TuneroFolder, "Tunero.exe");
                File.Copy(exe, copy, overwrite: true);
                if (!SameContent(exe, copy)) throw Fail("The copied Tunero.exe differs from the running one.", "Media_FilesCheckFailed");
                File.WriteAllText(Path.Combine(TuneroFolder, FirstSignIn.FileName), FirstSignIn.Serialize(r.FirstSignIn!));
                // Read with the rules the new PC uses, so a file it would ignore never reaches the ISO.
                if (FirstSignIn.Read(copy, _ => true, TuneroFolder) != r.FirstSignIn)
                    throw Fail($"{FirstSignIn.FileName} does not read back as written.", "Media_FilesCheckFailed");
                steps.Log($"Copied Tunero from {exe}");
            }
            CheckMediaHasNoAnswerFile();
        }

        /// <summary>The name Setup in Windows PE looks for at the root of its system drive, X:\ (any case would do).</summary>
        private const string BootAnswerFile = "Autounattend.xml";

        /// <summary>
        /// The answer file lives only at X:\Autounattend.xml inside image 2 of boot.wim, the Windows PE that Setup runs in when
        /// the PC starts from the media. Microsoft's implicit answer file search order
        /// (https://learn.microsoft.com/windows-hardware/manufacture/desktop/windows-setup-automation-overview, "Implicit Answer
        /// File Search Order") also has the roots of removable media (rows 4 and 5), the \Sources folder of the media (row 6)
        /// and the root of the drive setup.exe runs from (row 8). A setup.exe started inside a running Windows sees all of them,
        /// and with an answer file there it offered only a clean install that moved the old Windows to Windows.old (VM test 1e).
        /// %SYSTEMDRIVE% (row 7) is X:\ only in the Windows PE of the media, and Setup caches the file it used into the new
        /// Windows' Panther folder, so the specialize and oobeSystem settings still apply.
        /// Every build also clears the image of answer files that Setup in Windows PE would prefer or also find (an own ISO can
        /// carry them, and files that were not chosen must not run on the new PC) and refuses an image whose registry points
        /// Setup to one. With nothing to write and nothing found, the image is discarded unchanged; a changed image is checked
        /// again after the commit, from what the committed boot.wim holds.
        /// </summary>
        private async Task WriteBootWimAsync()
        {
            var a = _answers;
            var xml = a.SkipSetup || a.AllowUnsupported || a.FirstSignInTunero ? AnswerFile.Build(a) : null;
            var root = _bootMount!;
            await MountAsync(BootWim, 2, root, readOnly: false, "boot.wim");
            CheckSetupRegistry(root);
            var removed = RemoveBootAnswerFiles(root);
            if (xml is not null)
            {
                var path = Path.Combine(root, BootAnswerFile);
                File.WriteAllText(path, xml);   // UTF-8 without a byte order mark, as the declaration says
                CheckAnswerFile(path, xml);
                steps.Log($"Wrote {BootAnswerFile} into image 2 of boot.wim");
            }
            var commit = xml is not null || removed;
            await UnmountAsync(root, commit, "boot.wim");
            if (!commit)
            {
                steps.Log("boot.wim left unchanged");
                return;
            }

            CheckBootHeader();
            await MountAsync(BootWim, 2, root, readOnly: true, "boot.wim");
            var found = BootAnswerFilesIn(root);
            if (xml is not null)
            {
                var ours = found.FirstOrDefault(f => Same(f, Path.Combine(root, BootAnswerFile)));
                if (ours is null) throw Fail($"The committed boot.wim has no {BootAnswerFile}.", "Media_FilesCheckFailed");
                CheckAnswerFile(ours, xml);
                found.Remove(ours);
            }
            if (found.Count > 0)
                throw Fail("The committed boot.wim still holds " + string.Join(", ", found.Select(f => Path.GetRelativePath(root, f))) + ".", "Media_FilesCheckFailed");
            await UnmountAsync(root, commit: false, "boot.wim");
            steps.Log("boot.wim checked after the commit: boots image 2" + (xml is not null ? $", {BootAnswerFile} as written" : ", no answer file"));
        }

        /// <summary>Setup reads HKLM\SYSTEM\Setup\UnattendFile before every other place: a boot image that sets it is refused.</summary>
        private void CheckSetupRegistry(string root)
        {
            string? pointer;
            try
            {
                using var hive = steps.LoadSystemHive(Path.Combine(root, @"Windows\System32\config\SYSTEM"));
                pointer = hive.SetupValue("UnattendFile");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                steps.Log($@"Registry of image 2 of boot.wim: Setup\UnattendFile could not be read, refused: {ex.Message}");
                throw Fail($"The registry of image 2 of boot.wim could not be read: {ex.Message}", "Media_ImageCheckFailed");
            }
            if (string.IsNullOrWhiteSpace(pointer))
            {
                steps.Log(@"Registry of image 2 of boot.wim: Setup\UnattendFile is not set");
                return;
            }
            steps.Log($@"Registry of image 2 of boot.wim: Setup\UnattendFile points to {pointer}, refused");
            throw Fail($@"The registry of image 2 of boot.wim points Setup to the answer file {pointer} (Setup\UnattendFile).", "Media_BootImageAnswerFile");
        }

        /// <summary>Removes every answer file of <see cref="BootAnswerFilesIn"/>, checked gone, fail closed. True when there were any.</summary>
        private bool RemoveBootAnswerFiles(string root)
        {
            var found = BootAnswerFilesIn(root);
            steps.Log(found.Count == 0
                ? "No answer files found in image 2 of boot.wim"
                : "Answer files found in image 2 of boot.wim: " + string.Join(", ", found.Select(f => Path.GetRelativePath(root, f))));
            foreach (var file in found)
            {
                steps.Log($"Removing {Path.GetRelativePath(root, file)} from image 2 of boot.wim");
                try
                {
                    SafeDelete.DeleteFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw Fail($"{Path.GetRelativePath(root, file)} in boot.wim could not be removed: {ex.Message}", "Media_FilesCheckFailed");
                }
            }
            if (BootAnswerFilesIn(root).Count > 0) throw Fail("An answer file in boot.wim could not be removed.", "Media_FilesCheckFailed");
            return found.Count > 0;
        }

        /// <summary>
        /// The answer files that Setup in Windows PE would use or also find in the image mounted at <paramref name="root"/>
        /// (X:\), with their names as they are (names compare without case).
        /// </summary>
        private static List<string> BootAnswerFilesIn(string root)
        {
            return
            [
                .. Named(root, IsAnswerFileName),
                .. Named(Path.Combine(root, "sources"), IsAnswerFileName),
                .. Named(Path.Combine(root, @"Windows\Panther"), IsAnswerFileName),
                .. Named(Path.Combine(root, @"Windows\Panther\Unattend"), n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)),
                .. Named(Path.Combine(root, @"Windows\System32\Sysprep"), n => Same(n, "unattend.xml")),
            ];
        }

        /// <summary>
        /// The files and folders directly in <paramref name="folder"/> whose name matches, hidden and system ones included. A
        /// folder that cannot be listed throws instead of reading as empty: the checks that use it fail closed.
        /// </summary>
        private static IEnumerable<string> Named(string folder, Func<string, bool> match) =>
            Directory.Exists(folder)
                ? Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }).Where(e => match(Path.GetFileName(e))).ToList()
                : [];

        /// <summary>A setup.exe started inside Windows finds an answer file at the media's root or in sources (<see cref="WriteBootWimAsync"/>): none may be there.</summary>
        private void CheckMediaHasNoAnswerFile()
        {
            var found = Named(_media!, IsAnswerFileName).Concat(Named(Sources, IsAnswerFileName)).ToList();
            if (found.Count > 0)
                throw Fail("The media holds an answer file outside boot.wim: " + string.Join(", ", found.Select(f => Path.GetRelativePath(_media!, f))) + ".", "Media_FilesCheckFailed");
        }

        private void CheckAnswerFile(string path, string expected)
        {
            if (File.ReadAllText(path) != expected) throw Fail($"{Path.GetFileName(path)} does not read back as written.", "Media_FilesCheckFailed");
            try
            {
                // Read from the bytes on disk as Setup does (the declaration names the encoding); both sides without layout whitespace.
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
                XDocument written, wanted;
                using (var reader = XmlReader.Create(path, settings)) written = XDocument.Load(reader);
                using (var reader = XmlReader.Create(new StringReader(AnswerFile.Build(_answers)), settings)) wanted = XDocument.Load(reader);
                if (written.Root?.Name != XName.Get("unattend", "urn:schemas-microsoft-com:unattend") || !XNode.DeepEquals(written, wanted))
                    throw Fail($"{Path.GetFileName(path)} does not match the choices.", "Media_FilesCheckFailed");
            }
            catch (XmlException ex)
            {
                throw Fail($"{Path.GetFileName(path)} is not valid XML: " + ex.Message, "Media_FilesCheckFailed");
            }
        }

        private static bool SameContent(string a, string b)
        {
            using var first = File.OpenRead(a);
            using var second = File.OpenRead(b);
            return first.Length == second.Length && SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
        }

        /// <summary>The ISO is written inside the locked work folder; the output path gets it only after Verify (<see cref="Publish"/>).</summary>
        private async Task WriteIsoAsync(string build)
        {
            _workIso = Path.Combine(_work!, "output.iso");
            var volume = IsoBoot.VolumeName($"WIN11_{build}_X64");
            steps.Log($"Writing {_workIso} (volume {volume})");
            await steps.IsoWriter.WriteAsync(_media!, _workIso, volume, Fraction(null), ct);
        }

        /// <summary>Reads the ISO back: its boot catalog and the files that decide whether Windows installs as chosen.</summary>
        private async Task VerifyAsync()
        {
            // boot.wim carries the answer file (WriteBootWimAsync): compared byte for byte like install.wim.
            var files = new List<string> { @"sources\" + InstallImageName, @"sources\boot.wim" };
            // Without etfsboot.com the media has no BIOS entry at all; the UEFI files are required (RequireUefiBoot).
            if (File.Exists(Path.Combine(_media!, IsoBoot.BiosImage))) files.Add(IsoBoot.BiosImage);
            files.Add(IsoBoot.UefiImage);
            files.Add(IsoBoot.UefiLoader);
            if (r.TuneroExe is not null)
            {
                files.Add(TuneroOnMedia + @"\Tunero.exe");
                files.Add(TuneroOnMedia + @"\" + FirstSignIn.FileName);
            }
            // The read-back is the first half of the stage's bar, the hash of the ISO the second.
            var stage = _stage;
            var check = await steps.IsoReader.VerifyAsync(_workIso!, _media!, files, new Relay<double>(v => Report(stage, v * 50, null)), ct);
            if (!check.BootCatalogOk || check.Mismatched.Count > 0)
            {
                var what = (check.BootCatalogOk ? Array.Empty<string>() : ["boot catalog"]).Concat(check.Mismatched);
                throw Fail("The written ISO differs from the media: " + string.Join(", ", what) + ".", "Media_VerifyFailed");
            }
            steps.Log($"ISO verified: boot catalog and {files.Count} files");
            (_isoBytes, _sha256) = await HashIsoAsync();
            steps.Log($"ISO SHA-256 {_sha256}, {_isoBytes} bytes");
        }

        /// <summary>The size and SHA-256 (upper-case hex) of the verified ISO, read in 1 MB blocks while it is still in the work folder: the bytes that <see cref="Publish"/> then moves.</summary>
        private async Task<(long Bytes, string Hash)> HashIsoAsync()
        {
            var stage = _stage;
            await using var stream = new FileStream(_workIso!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long done = 0;
            var lastPercent = -1;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                done += read;
                var percent = (int)(done * 100 / Math.Max(stream.Length, 1));
                if (percent == lastPercent) continue;
                lastPercent = percent;
                Report(stage, 50 + percent / 2.0, null);
            }
            return (done, Convert.ToHexString(hash.GetHashAndReset()));
        }

        /// <summary>
        /// Moves the verified ISO to the output path by handles only (<see cref="SafeMove.IntoFolder"/>): the output folder may
        /// be writable by others, so no path in it is resolved after it was checked. The ISO takes the output folder's
        /// permissions while it is still in the locked work folder, then that open file is renamed into the held folder
        /// (same drive: a rename).
        /// </summary>
        private void Publish()
        {
            var folder = Path.GetDirectoryName(r.OutputIso)!;
            bool inherited;
            try
            {
                inherited = SafeMove.IntoFolder(_workIso!, folder, Path.GetFileName(r.OutputIso));
            }
            catch (FolderNotHeldException ex)
            {
                throw Fail($"The ISO was not moved: {ex.Message}", "Media_OutputInvalid");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Fail($"The verified ISO could not be moved to {r.OutputIso}: {ex.Message}", "Media_OutputMoveFailed",
                    (ex.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode);
            }
            steps.Log("Verified ISO moved to " + r.OutputIso);
            if (!inherited) Warn(steps, $"{r.OutputIso} keeps the work folder's permissions (administrators only)");
        }

        /// <summary>Never throws: discards a mounted image (<see cref="DiscardMountsAsync"/>) and deletes the work folder (each retried), logging what it could not do.</summary>
        private async Task CleanUpAfterFailureAsync()
        {
            Report(MediaStage.Cleanup, null, null);
            if (_mounted is { } mount)
            {
                // A SYSTEM hive whose own unload gave up holds a file of the mounted boot.wim open: the discard would fail.
                steps.UnloadLeftoverHives();
                var none = CancellationToken.None;
                try
                {
                    // This build's own mount: its Status is not read first (it was Ok when mounted), a failing discard leads to the remount.
                    var still = await DiscardMountsAsync(steps, [new MountedImage(mount, null)], _ => RetryAsync("Discarding the mounted image",
                            () => Dism(DismArgs.Unmount(mount, commit: false), () => steps.Dism.UnmountAsync(mount, false, null, none)),
                            ex => ex is DismException or IOException), cleanUpAlways: false, none);
                    if (still is not null) LogMounts(steps, still.Where(m => IsInside(m.Dir, _work!)));
                    if (still is null || !still.Any(m => IsInside(m.Dir, mount))) _mounted = null;
                }
                catch (Exception ex)
                {
                    steps.Log("DISM cleanup failed: " + ex.Message);
                }
            }
            ReleaseLock();
            if (_work is null) return;
            if (_mounted is not null)
            {
                Warn(steps, $"The image is still mounted at {_mounted}; {_work} stays until the next start of Tunero removes it.");
                return;
            }
            if (await DeleteWorkFolderAsync()) steps.Log("Deleted " + _work);
            else Warn(steps, $"work folder {_work} stays until the next start of Tunero removes it");
        }

        private void ReleaseLock()
        {
            try
            {
                _workFolder?.Dispose();
            }
            catch (Exception ex)
            {
                steps.Log("Releasing the lock file failed: " + ex.Message);
            }
        }

        private Task<bool> DeleteWorkFolderAsync() =>
            RetryAsync("Deleting the work folder", () =>
            {
                steps.DeleteWorkFolder(_work!);
                return Task.CompletedTask;
            }, ex => ex is IOException or UnauthorizedAccessException);

        /// <summary>Up to three attempts, a short pause between them, while <paramref name="retry"/> says the failure can pass. Never throws.</summary>
        private async Task<bool> RetryAsync(string what, Func<Task> action, Func<Exception, bool> retry)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await action();
                    return true;
                }
                catch (Exception ex)
                {
                    steps.Log($"{what} failed (attempt {attempt} of {Attempts}): {ex.Message}");
                    if (attempt >= Attempts || !retry(ex)) return false;
                }
                await Task.Delay(retryDelay, CancellationToken.None);
            }
        }
    }

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>
/// The real steps: dism.exe, IMAPI2, virtdisk and Microsoft's download servers. The work folder is created at the root of
/// the output drive, which must be a local fixed drive, locked to Administrators and SYSTEM. Not covered by unit tests
/// beyond its folder listing and deletion; the VM test drives it.
/// </summary>
public sealed class MediaSteps : IMediaSteps
{
    private readonly Lock _gate = new();
    private readonly Func<DirectoryInfo, bool> _trusted;
    private readonly Func<string, bool> _lockFolder;

    public MediaSteps() : this(null)
    {
    }

    /// <param name="trusted">Tests only: replaces the check that a folder is owned by Administrators and locked.</param>
    /// <param name="lockFolder">Tests only: replaces <see cref="SecureFolder.Lock"/> (which needs administrator rights).</param>
    internal MediaSteps(Func<DirectoryInfo, bool>? trusted, Func<string, bool>? lockFolder = null)
    {
        _trusted = trusted ?? (d => SecureFolder.IsOwnedByAdmins(d) && SecureFolder.IsLocked(d));
        _lockFolder = lockFolder ?? SecureFolder.Lock;
    }

    public IDism Dism { get; } = new DismTool();
    public IIsoWriter IsoWriter { get; } = new IsoWriter();
    public IIsoReader IsoReader { get; } = new IsoReader();

    public string LogPath { get; } = Path.Combine(DataPaths.Logs, $"media-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public async Task DownloadAsync(EsdFile file, string target, IProgress<(long Done, long Total)>? p, CancellationToken ct)
    {
        using var download = new MediaDownload();
        await download.DownloadAsync("esd", file.Url, target, file.Size, Convert.FromHexString(file.Sha256), p, ct);
    }

    public WorkFolder NewWorkFolder(string outputIso) => NewWorkFolder(outputIso, null);

    /// <param name="root">Tests only: the folder that stands in for the output drive's root.</param>
    internal WorkFolder NewWorkFolder(string outputIso, string? root)
    {
        string full;
        try
        {
            full = Path.GetFullPath(outputIso);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MediaBuildException(MediaStage.Download, $"{outputIso} is not a usable path.", key: "Media_OutputInvalid", inner: ex);
        }
        if (!TrustedPath.IsOnLocalFixedDrive(full))
            throw new MediaBuildException(MediaStage.Download, $"{full} is not on a local fixed drive.", key: "Media_OutputNotLocal");
        var folder = Path.Combine(root ?? Path.GetPathRoot(full)!, MediaBuild.WorkFolderPrefix + RandomNumberGenerator.GetHexString(8, lowercase: true));
        if (Path.Exists(folder)) throw new MediaBuildException(MediaStage.Download, $"{folder} already exists.", key: "Media_WorkFolderFailed");
        // Lock creates the missing folder with only Administrators and SYSTEM allowed in one step, so no other account can
        // add anything to it, and refuses a folder that someone else created at that name in the meantime.
        if (!_lockFolder(folder) || !SafeDelete.HasNoLinks(folder) || !_trusted(new DirectoryInfo(folder)))
        {
            RemoveEmpty(folder);
            throw new MediaBuildException(MediaStage.Download, $"{folder} could not be created and locked.", key: "Media_WorkFolderFailed");
        }
        try
        {
            // The lock file exists before anyone else can see the folder as a work folder without a running build.
            return WorkFolder.Hold(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveEmpty(folder);
            throw new MediaBuildException(MediaStage.Download, $"The lock file in {folder} could not be created: {ex.Message}", key: "Media_WorkFolderFailed", inner: ex);
        }
    }

    /// <summary>
    /// Removes a fresh folder that could not be made a work folder: only when it is empty or a link (a non-recursive delete
    /// removes nothing else), since it may not be locked and so not trusted. Anything left is logged.
    /// </summary>
    private static void RemoveEmpty(string folder)
    {
        try
        {
            if (Path.Exists(folder)) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("media", $"{folder} could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// Any user can create a folder at a drive root with a work folder's name and change what is inside it while a delete
    /// recurses (a subfolder swapped for a junction). So only a folder that is owned by Administrators and locked to them
    /// and SYSTEM, where no one else can change anything, is deleted with its content; a link is only unlinked.
    /// </summary>
    public void DeleteWorkFolder(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        if (!MediaBuild.IsWorkFolderName(Path.GetFileName(full))) throw new ArgumentException($"{folder} is not a Tunero work folder.", nameof(folder));
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Removes the link itself, never its target.
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(full);
            else File.Delete(full);
            return;
        }
        if ((attributes & FileAttributes.Directory) == 0 || !_trusted(new DirectoryInfo(full)))
            throw new IOException($"{full} is not a folder locked to Administrators and SYSTEM; left in place.");
        SecureFolder.DeleteTree(full);
    }

    public long FreeBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    public IOfflineHive LoadSystemHive(string file) => OfflineSystemHive.Load(file);

    public IWritableHive LoadWritableHive(string file) => OfflineImageRegistry.Load(file);

    public void UnloadLeftoverHives() => OfflineSystemHive.UnloadLeftovers();

    /// <summary>
    /// Looks for dism.exe by name first (cheap) and reads the command lines from WMI only when one runs. Elevated, WMI
    /// gives every process's command line; when the query fails, or gives a dism.exe without one, the result is null
    /// (logged) and the cleanup leaves the leftovers for the next start.
    /// </summary>
    public IReadOnlyList<string>? DismCommandLines()
    {
        var running = Process.GetProcessesByName("dism");
        foreach (var p in running) p.Dispose();
        if (running.Length == 0) return [];
        try
        {
            var lines = Hardware.Wmi.Query("SELECT CommandLine FROM Win32_Process WHERE Name = 'dism.exe'")
                .Select(row => row.GetValueOrDefault("CommandLine") as string)
                .ToList();
            if (lines.All(l => l is not null)) return lines.OfType<string>().ToList();
            AppLog.Warn("media", "WMI gave a running DISM process without its command line");
            return null;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or TimeoutException or UnauthorizedAccessException)
        {
            AppLog.Warn("media", "the command lines of the running DISM processes could not be read: " + ex.Message);
            return null;
        }
    }

    public IEnumerable<string> LocalDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

    public IEnumerable<string> LeftoverWorkFolders(string drive)
    {
        try
        {
            return new DirectoryInfo(drive)
                .EnumerateDirectories(MediaBuild.WorkFolderPrefix + "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
                .Where(d => MediaBuild.IsWorkFolderName(d.Name))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Appends to media-yyyyMMdd-HHmmss.log in the data folder's logs. Like the app log it writes only once the data folder
    /// is secured (and never in tests).
    /// </summary>
    public void Log(string line)
    {
        if (!AppLog.FileEnabled) return;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must not break the build.
        }
    }
}
