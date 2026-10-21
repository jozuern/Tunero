using System.IO;
using System.Text;
using Optimizer.Core.Backup;
using Optimizer.Core.Catalog;
using Optimizer.Core.Findings;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Services;

/// <summary>
/// Changes without the window, for a fresh install or several PCs:
/// <c>--apply &lt;file&gt;</c> applies an exported changes file with the rules of Import (only what is safe to apply
/// together; previews, Expert, boot-critical and anti-cheat sensitive changes need their own confirmation in the app
/// and are listed instead), <c>--apply-recommended</c> applies the Overview's recommended list for the profile
/// (<c>--profile &lt;id&gt;</c>, else the saved one), <c>--export &lt;file&gt;</c> writes the changes file the Changes
/// page exports (<c>--include apps,services,startup</c> adds its opt-in sections), and <c>--undo-all</c> undoes every
/// change of this app. Removed apps in a file are listed by <c>--apply</c>, never removed: that cannot be undone and
/// needs its own question in the app. An apply creates a restore point
/// first, as the window does, and stops when that is not possible unless <c>--no-restore-point</c> is given. A run waits
/// for a change of another Tunero (<see cref="ChangeLock"/>). The result goes to the session log and, with
/// <c>--log &lt;file&gt;</c>, to a new text file; the exit code says how it went.
/// </summary>
public static class CommandLineApply
{
    public const int Done = 0;
    public const int SomeFailed = 1;
    public const int InvalidFile = 2;
    public const int NotElevated = 3;
    public const int NoRestorePoint = 4;
    public const int Busy = 5;
    public const int Error = 6;
    public const int NotSupported = 7;
    /// <summary>The start was refused before anything ran: a profiler variable, or an unsafe data or runtime folder.</summary>
    public const int StartRefused = 8;
    /// <summary>--profile names a profile this version does not know, or one this PC does not offer (a laptop profile on a desktop).</summary>
    public const int UnknownProfile = 9;
    /// <summary>--include names a part the export does not have.</summary>
    public const int UnknownSection = 10;
    /// <summary>--build-media: the catalog or the build failed (<see cref="CommandLineMedia"/>; stage and reason in the log).</summary>
    public const int MediaBuildFailed = 11;

    /// <summary>The names --include takes, in the order of the Changes page.</summary>
    public static readonly IReadOnlyList<string> Sections = ["apps", "services", "startup"];

    /// <summary>How long a run waits for a change of another Tunero (the window, another run) to finish.</summary>
    public static TimeSpan LockWait { get; set; } = TimeSpan.FromMinutes(5);

    public static async Task<int> ApplyAsync(string path, AppServices services, string? logPath, bool withoutRestorePoint)
    {
        var lines = new List<string>();
        var code = await Locked(lines, () => ApplyCoreAsync(path, services, withoutRestorePoint, lines));
        Finish("apply", lines, code, logPath);
        return code;
    }

    /// <param name="profileId">The profile named with --profile; it must exist and fit this PC.</param>
    /// <param name="savedProfileId">The profile saved in the app, used without --profile; when this PC no longer offers it (or none was saved), the one that fits this PC best.</param>
    /// <param name="ignored">The results ignored in the app: nothing is applied for them, as on the Overview.</param>
    public static async Task<int> ApplyRecommendedAsync(AppServices services, string? profileId, string? savedProfileId, IReadOnlyCollection<string> ignored,
        IReadOnlyCollection<string> ignoredTweaks, string? logPath, bool withoutRestorePoint)
    {
        var lines = new List<string>();
        var code = await Locked(lines, () => ApplyRecommendedCoreAsync(services, profileId, savedProfileId, ignored, ignoredTweaks, withoutRestorePoint, lines));
        Finish("apply recommended", lines, code, logPath);
        return code;
    }

    /// <param name="include">The value of --include: a comma separated list of <see cref="Sections"/>, or null.</param>
    public static async Task<int> ExportAsync(string path, AppServices services, string? logPath, string? include = null)
    {
        var lines = new List<string>();
        var parts = (include ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var code = parts.FirstOrDefault(p => !Sections.Contains(p, StringComparer.OrdinalIgnoreCase)) is { } unknown
            ? Unknown(lines, unknown)
            : await Locked(lines, () => ExportCoreAsync(path, services, parts.ToHashSet(StringComparer.OrdinalIgnoreCase), lines));
        Finish("export", lines, code, logPath);
        return code;
    }

    /// <summary>--reapply (the re-apply task, <see cref="ReapplyTask"/>): applies again what Windows reset.</summary>
    public static async Task<int> ReapplyAsync(AppServices services, string? logPath)
    {
        var lines = new List<string>();
        var code = await Locked(lines, () => ReapplyCoreAsync(services, lines));
        Finish("reapply", lines, code, logPath);
        return code;
    }

    private static int Unknown(List<string> lines, string section)
    {
        lines.Add(Loc.Instance.Format("Cli_UnknownSection", section, string.Join(", ", Sections)));
        return UnknownSection;
    }

    private static async Task<int> ReapplyCoreAsync(AppServices services, List<string> lines)
    {
        if (!services.Elevation.IsElevated)
        {
            lines.Add(Loc.Instance["Cli_NotElevated"]);
            return NotElevated;
        }
        var outcome = await services.Session.ReapplyResetAsync(await ScanAsync(services));
        if (outcome.Results.Count == 0)
        {
            lines.Add(Loc.Instance["Cli_NothingReset"]);
            return Done;
        }
        var lang = Loc.Instance.Language;
        foreach (var r in outcome.Results) lines.Add(ChangeRunner.ResultText(r.Tweak, ChangeRunner.TitleOf(r.Tweak, lang), r.Result));
        lines.Add(Loc.Instance.Format("Cli_ReapplySummary", outcome.Applied, outcome.Failed));
        return outcome.Failed > 0 ? SomeFailed : Done;
    }

    public static async Task<int> UndoAllAsync(AppServices services, string? logPath)
    {
        var lines = new List<string>();
        var code = await Locked(lines, () => UndoCoreAsync(services, lines));
        Finish("undo all", lines, code, logPath);
        return code;
    }

    /// <summary>Runs <paramref name="work"/> as the only change on this PC (<see cref="ChangeLock"/>), or returns <see cref="Busy"/>.</summary>
    private static async Task<int> Locked(List<string> lines, Func<Task<int>> work)
    {
        using var crossProcess = await Task.Run(() => ChangeLock.TryAcquire(LockWait));
        if (crossProcess is null)
        {
            lines.Add(Loc.Instance["Cli_Busy"]);
            return Busy;
        }
        // An unexpected error ends the run with its own exit code and a line in the log file; a run without a window
        // must never wait on an error dialog.
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            Log.Error("cli", "the run failed", ex);
            lines.Add(Loc.Instance.Format("Cli_Error", ex.Message));
            return Error;
        }
    }

    private static async Task<int> ApplyCoreAsync(string path, AppServices services, bool withoutRestorePoint, List<string> lines)
    {
        if (!services.Elevation.IsElevated)
        {
            lines.Add(Loc.Instance["Cli_NotElevated"]);
            return NotElevated;
        }
        TransferFile? file;
        try
        {
            file = new FileInfo(path).Length > ChangeTransfer.MaxFileBytes ? null : ChangeTransfer.Parse(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            lines.Add(Loc.Instance.Format("Cli_FileUnreadable", path, ex.Message));
            return InvalidFile;
        }
        if (file is null)
        {
            lines.Add(Loc.Instance.Format("Cli_FileInvalid", path));
            return InvalidFile;
        }

        var pc = await ScanAsync(services);
        var targets = TransferData.Targets(services, file, pc.Profile);
        var plan = ChangeTransfer.Plan(file, services.Catalog, pc.Facts, pc.Applied, targets);
        Log.Info("cli", "apply plan", new { together = plan.Together.Count, oneByOne = plan.OneByOne.Count, apps = plan.Apps.Count, plan.AlreadyOn, plan.NotForThisPc, plan.Unknown });
        // Removing an app cannot be undone: the command line only names them, the app's Import asks for them.
        if (plan.Apps.Count > 0)
            lines.Add(Loc.Instance.Format("Cli_AppsInApp", string.Join(", ", plan.Apps.Select(a => a.Entry.Label(Loc.Instance.Language)))));
        return await ApplyTogetherAsync(Apply(services, pc, withoutRestorePoint), plan.Together, plan.OneByOne, lines,
            (ok, failed, skipped) => Loc.Instance.Format("Cli_Summary", ok, failed, skipped, plan.AlreadyOn, plan.NotForThisPc, plan.Unknown));
    }

    /// <summary>
    /// The Overview's "Apply recommended" without the window: the same plan for the same profile (fixes of problems
    /// first, then what the profile recommends for this PC), the same rule for what applies together, and the rest
    /// listed for the app.
    /// </summary>
    private static async Task<int> ApplyRecommendedCoreAsync(AppServices services, string? profileId, string? savedProfileId, IReadOnlyCollection<string> ignored,
        IReadOnlyCollection<string> ignoredTweaks, bool withoutRestorePoint, List<string> lines)
    {
        if (!services.Elevation.IsElevated)
        {
            lines.Add(Loc.Instance["Cli_NotElevated"]);
            return NotElevated;
        }
        var pc = await ScanAsync(services, ignored);
        return await ApplyRecommendedPlanAsync(pc, profileId, savedProfileId, Apply(services, pc, withoutRestorePoint), lines, ignoredTweaks);
    }

    /// <summary>
    /// --apply-recommended after its scan: the plan of <see cref="RecommendedPlan"/>, its batch-safe part applied with
    /// <paramref name="apply"/>, the rest listed. Tests pass a recording <paramref name="apply"/>.
    /// </summary>
    internal static async Task<int> ApplyRecommendedPlanAsync(PcSnapshot pc, string? profileId, string? savedProfileId,
        Func<IReadOnlyList<TweakDefinition>, Task<BatchOutcome>> apply, List<string> lines, IReadOnlyCollection<string>? ignoredTweaks = null)
    {
        if (RecommendedPlan(pc, profileId, savedProfileId, ignoredTweaks) is not { } plan)
        {
            var profiles = CatalogData.Current.Profiles;
            lines.Add(Loc.Instance.Format("Cli_UnknownProfile", profileId ?? "", string.Join(", ", profiles.Profiles.Where(p => p.IsAvailable(pc.Facts)).Select(p => p.Id))));
            return UnknownProfile;
        }
        lines.Add(Loc.Instance.Format("Cli_Profile", ViewModels.MainViewModel.ProfileName(plan.Profile)));
        return await ApplyTogetherAsync(apply, plan.Together, plan.InApp, lines,
            (ok, failed, skipped) => Loc.Instance.Format("Cli_RecommendedSummary", ok, failed, skipped));
    }

    /// <summary>
    /// The scan and plan of <see cref="RecommendedPlan"/>, for the window's first sign-in (Windows install media) and
    /// --apply-recommended alike, so both always pick the same changes.
    /// </summary>
    public static async Task<(PcSnapshot Pc, RecommendedSelection? Plan)> RecommendedPlanAsync(AppServices services, string? profileId, string? savedProfileId,
        IReadOnlyCollection<string> ignored, IReadOnlyCollection<string> ignoredTweaks)
    {
        var pc = await ScanAsync(services, ignored);
        return (pc, RecommendedPlan(pc, profileId, savedProfileId, ignoredTweaks));
    }

    /// <summary>
    /// The Overview's recommended list for the profile (<paramref name="profileId"/>, which must exist and fit this PC;
    /// without it the saved one, else the one that fits this PC best): <see cref="RecommendedSelection.Together"/> is
    /// what applies without a confirmation, <see cref="RecommendedSelection.InApp"/> the previews, Expert, boot-critical
    /// and anti-cheat sensitive changes that need their own. Null when the profile is unknown or not offered here.
    /// Tweaks whose recommendation the user ignores (<paramref name="ignoredTweaks"/>) are left out, as in the window.
    /// </summary>
    public static RecommendedSelection? RecommendedPlan(PcSnapshot pc, string? profileId, string? savedProfileId, IReadOnlyCollection<string>? ignoredTweaks = null)
    {
        var profiles = CatalogData.Current.Profiles;
        var profile = profileId is not null
            ? profiles.Profiles.FirstOrDefault(p => p.Id == profileId && p.IsAvailable(pc.Facts))
            : profiles.Profiles.FirstOrDefault(p => p.Id == savedProfileId && p.IsAvailable(pc.Facts)) ?? profiles.Suggest(pc.Facts);
        if (profile is null) return null;
        var plan = Recommendations.Build(Optimizer.Core.Profiles.ProfileView.For(profile, pc.States, pc.Facts, ignoredTweaks?.ToHashSet(StringComparer.Ordinal)),
            Optimizer.Core.Profiles.ProfileView.Findings(profile, pc.Findings));
        Log.Info("cli", "recommended plan", new { profile = profile.Id, items = plan.Items.Count, excluded = plan.Excluded.Count });
        return new RecommendedSelection(profile, plan.Items.Select(i => i.Tweak).ToList(), plan.Excluded.Select(i => i.Tweak).ToList());
    }

    /// <summary>The changes file of the Changes page's Export, written to <paramref name="path"/>.</summary>
    private static async Task<int> ExportCoreAsync(string path, AppServices services, IReadOnlySet<string> include, List<string> lines)
    {
        if (!services.Elevation.IsElevated)
        {
            lines.Add(Loc.Instance["Cli_NotElevated"]);
            return NotElevated;
        }
        var pc = await ScanAsync(services);
        var sections = include.Count > 0
            ? TransferData.Sections(services, pc.Facts, include.Contains("apps"), include.Contains("services"), include.Contains("startup"))
            : null;
        var (file, pcSpecific) = ChangeTransfer.Create(services.Store.All().Select(b => b.TweakId), pc.Applied, services.Catalog,
            Optimizer.Core.Tools.ProgramPriority.Read(services.Context.Registry), AppInfo.Text, services.Os.BuildString, sections);
        try
        {
            // Only a new file when elevated: whoever starts the exe names the path, and the app writes with administrator rights.
            await using var stream = new FileStream(path, DataPaths.ProcessIsElevated ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(ChangeTransfer.Serialize(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            lines.Add(Loc.Instance.Format("Cli_FileUnwritable", path, ex.Message));
            return InvalidFile;
        }
        lines.Add(Loc.Instance.Format("Cli_Exported", file.Count, path, pcSpecific));
        return Done;
    }

    /// <summary>The scan the window runs: the facts decide what applies to this PC and what is on already.</summary>
    private static Task<PcSnapshot> ScanAsync(AppServices services, IReadOnlyCollection<string>? ignored = null) =>
        ChangeSession.ScanAsync(CatalogData.Current, services.Catalog, services.Engine, services.Context.Registry, services.BcdValues, ignored);

    /// <summary>
    /// Applies a set in one go with one restore point. Without a restore point the run stops, unless --no-restore-point
    /// said to go on (there is nobody to ask).
    /// </summary>
    private static Func<IReadOnlyList<TweakDefinition>, Task<BatchOutcome>> Apply(AppServices services, PcSnapshot pc, bool withoutRestorePoint) =>
        together => services.Session.ApplyBatchAsync(together, pc.Facts, pc.Applied, new ApplyOptions { ContinueWithoutRestorePoint = withoutRestorePoint },
            () => RestorePointChoice.Cancel);

    /// <summary>Applies the batch-safe changes with <paramref name="apply"/> and lists the rest for the app.</summary>
    private static async Task<int> ApplyTogetherAsync(Func<IReadOnlyList<TweakDefinition>, Task<BatchOutcome>> apply, IReadOnlyList<TweakDefinition> together,
        IReadOnlyList<TweakDefinition> inApp, List<string> lines, Func<int, int, int, string> summary)
    {
        var lang = Loc.Instance.Language;
        var outcome = await apply(together);
        if (together.Count > 0 && (outcome.Results.Count == 0 || outcome.StoppedForRestorePoint))
        {
            lines.Add(Loc.Instance["Cli_NoRestorePoint"]);
            return NoRestorePoint;
        }
        foreach (var r in outcome.Results) lines.Add(ChangeRunner.ResultText(r.Tweak, ChangeRunner.TitleOf(r.Tweak, lang), r.Result));
        foreach (var t in inApp) lines.Add(Loc.Instance.Format("Cli_NeedsApp", ChangeRunner.TitleOf(t, lang)));
        lines.Add(summary(outcome.Applied, outcome.Failed, outcome.Skipped + inApp.Count));
        return outcome.Failed > 0 ? SomeFailed : Done;
    }

    private static async Task<int> UndoCoreAsync(AppServices services, List<string> lines)
    {
        if (!services.Elevation.IsElevated)
        {
            lines.Add(Loc.Instance["Cli_NotElevated"]);
            return NotElevated;
        }
        var outcome = await services.Session.UndoAllAsync();
        var lang = Loc.Instance.Language;
        foreach (var (tweak, result) in outcome.Results)
        {
            var title = ChangeRunner.TitleOf(tweak, lang);
            lines.Add(result.Success ? Loc.Instance.Format("Result_Undone", title) : Loc.Instance.Format("Result_UndoIncomplete", title, string.Join("; ", result.Errors)));
        }
        lines.Add(Loc.Instance.Format("Cli_UndoSummary", outcome.Undone, outcome.Failed));
        return outcome.Failed > 0 ? SomeFailed : Done;
    }

    /// <summary>
    /// Logs every line and writes them to <paramref name="logPath"/>. An elevated run only creates a new file there,
    /// never overwrites one: whoever starts the exe names the path, and the app writes with administrator rights.
    /// </summary>
    private static void Finish(string what, List<string> lines, int code, string? logPath)
    {
        foreach (var line in lines) Log.Info("cli", line);
        Log.Info("cli", $"{what} finished", new { exitCode = code });
        if (logPath is null) return;
        try
        {
            using var file = new FileStream(logPath, DataPaths.ProcessIsElevated ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            foreach (var line in lines) writer.WriteLine(line);
            writer.WriteLine(Loc.Instance.Format("Cli_ExitCode", code));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn("cli", $"log file not written: {ex.Message}");
        }
    }
}

/// <summary>
/// The profile and its recommended list (<see cref="CommandLineApply.RecommendedPlan"/>): <paramref name="Together"/>
/// applies without a confirmation, <paramref name="InApp"/> needs its own and is only listed.
/// </summary>
public sealed record RecommendedSelection(Optimizer.Core.Profiles.UsageProfile Profile, IReadOnlyList<TweakDefinition> Together, IReadOnlyList<TweakDefinition> InApp);
