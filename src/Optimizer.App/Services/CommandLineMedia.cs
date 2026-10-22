using System.Globalization;
using System.IO;
using System.Text;
using Optimizer.App.ViewModels;
using Optimizer.Core.Logging;
using Optimizer.Core.Media;
using Optimizer.Core.Platform;

namespace Optimizer.App.Services;

/// <summary>What --build-media uses from the system. Tests pass fakes.</summary>
public sealed record MediaCommandLineSystem
{
    /// <summary>DISM, attaching an ISO and the locked work folder need administrator rights.</summary>
    public bool Elevated { get; init; }

    /// <summary>The published single-file exe that goes on the media for a profile, or null for any other build (as the page).</summary>
    public string? TuneroExe { get; init; }

    /// <summary>The app's theme setting, which the copy on the media starts with.</summary>
    public string Theme { get; init; } = "System";

    public required IMediaCatalogSource Catalog { get; init; }
    public required MediaBuilder Build { get; init; }

    /// <summary>The keyboards the page offers (Windows' list).</summary>
    public Func<IReadOnlyList<InputProfile>> Keyboards { get; init; } = () => MediaLocales.Keyboards;

    /// <summary>
    /// The cleanup after earlier builds that the app start runs, under its single-instance rule, which here also deletes
    /// saved builds: --build-media has no Resume, and scripted runs must not pile up work folders of several GB.
    /// </summary>
    public Func<CancellationToken, Task> CleanLeftovers { get; init; } = _ => Task.CompletedTask;

    /// <summary>The saved builds (<see cref="MediaBuild.FindResumable"/>): after a failure the run names the one it kept.</summary>
    public Func<IReadOnlyList<ResumableBuild>> FindResumable { get; init; } = () => [];

    /// <summary>Where the progress and result lines go besides the --log file (standard output).</summary>
    public TextWriter Output { get; init; } = TextWriter.Null;

    /// <summary>The real system: the page's catalog source and build, Windows' keyboards, standard output.</summary>
    public static MediaCommandLineSystem ForThisPc(AppServices services, string theme) => new()
    {
        Elevated = DataPaths.ProcessIsElevated,
        TuneroExe = MainViewModel.IsPublishedExe ? Environment.ProcessPath : null,
        Theme = theme,
        Catalog = InstallMediaSystem.CatalogSource(services),
        Build = InstallMediaSystem.BuildAsync,
        CleanLeftovers = CleanLeftoversAsOnlyCopy,
        FindResumable = () => new MediaBuild(new MediaSteps()).FindResumable(),
        Output = Console.Out,
    };

    /// <summary>
    /// The app start's cleanup (<see cref="InstallMediaSystem.CleanLeftoversAsync"/>), saved builds included, only while this run holds the
    /// single-instance lock, which it takes for the cleanup alone (a window of this session cleans up at its own start
    /// and never beside its builds; a build of any Tunero keeps its work folder locked). The lock belongs to a thread, so
    /// the cleanup runs to its end on one.
    /// </summary>
    private static Task CleanLeftoversAsOnlyCopy(CancellationToken ct) => Task.Run(() =>
    {
        using var instance = SingleInstance.TryAcquire(TimeSpan.Zero, out var heldByOther, window: false);
        if (instance is null)
        {
            Log.Info("cli", heldByOther ? "media cleanup left to the open Tunero window" : "media cleanup skipped: no single-instance lock");
            return;
        }
        try
        {
            InstallMediaSystem.CleanLeftoversAsync(ct, keepSaved: false).GetAwaiter().GetResult();
        }
        finally
        {
            instance.Release();
        }
    }, ct);
}

/// <summary>
/// Developer switch for the VM tests: <c>--build-media &lt;request.json&gt; [--log &lt;file&gt;]</c> builds the install
/// media page's ISO without the window, from a request file (<see cref="MediaRequestFile"/>) mapped by the page's own
/// <see cref="MediaChoices.ToRequest"/>, with the page's catalog source and build. Every line (progress at most every
/// 5 %, the result) goes to standard output and the --log file (a new file when elevated); the exit code says how it went:
/// <see cref="CommandLineApply.Done"/>, <see cref="CommandLineApply.InvalidFile"/> (the request, with the field and why),
/// <see cref="CommandLineApply.NotElevated"/>, <see cref="CommandLineApply.MediaBuildFailed"/> (stage, key, code and
/// the build log) or <see cref="CommandLineApply.Error"/>. Lines are English only: no user sees them.
/// </summary>
public static class CommandLineMedia
{
    public const string Switch = "--build-media";

    /// <summary>A request file is a few hundred bytes; anything far larger is not one.</summary>
    private const long MaxRequestBytes = 64 * 1024;

    public static async Task<int> BuildAsync(string requestPath, string? logPath, MediaCommandLineSystem system)
    {
        using var lines = new Lines(system.Output, logPath);
        var code = await RunAsync(requestPath, system, lines);
        lines.Write(Loc.Instance.Format("Cli_ExitCode", code));
        Log.Info("cli", "build media finished", new { exitCode = code });
        return code;
    }

    private static async Task<int> RunAsync(string requestPath, MediaCommandLineSystem system, Lines lines)
    {
        if (!system.Elevated)
        {
            lines.Write(Loc.Instance["Cli_NotElevated"]);
            return CommandLineApply.NotElevated;
        }
        MediaRequestFile file;
        try
        {
            // One read through the handle whose path was checked, never through a link, and never more than the cap.
            if (!SafeDelete.HasNoLinks(requestPath)) return Invalid(lines, $"request: {requestPath} is a link or lies in one");
            if (SafeDelete.ReadFile(requestPath, MaxRequestBytes) is not { } bytes)
                return Invalid(lines, $"request: {requestPath} is larger than {MaxRequestBytes} bytes, or its path leads elsewhere");
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            file = MediaRequestFile.Parse(await reader.ReadToEndAsync(), system.Keyboards());
        }
        catch (MediaRequestFileException ex)
        {
            return Invalid(lines, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Invalid(lines, $"request: {requestPath} cannot be read: {ex.Message}");
        }

        try
        {
            // A request that cannot be built is refused before the cleanup or the network.
            file.CheckTuneroCopy(system.TuneroExe);
            lines.Write("Cleaning up after earlier builds");
            await system.CleanLeftovers(CancellationToken.None);

            IReadOnlyList<EsdFile>? catalog = null;
            if (file.IsDownload)
            {
                lines.Write($"{MediaStage.Catalog} started");
                try
                {
                    catalog = await system.Catalog.LoadAsync(MediaChoices.Architecture, new Sync<string>(step => lines.Write($"{MediaStage.Catalog}: {step}")), CancellationToken.None);
                }
                catch (MediaDownloadException ex)
                {
                    lines.Write($"Build failed at stage {MediaStage.Catalog}, step {ex.Step}: {ex.Message}");
                    return CommandLineApply.MediaBuildFailed;
                }
            }
            var request = file.ToChoices(catalog, Loc.Instance.Language, system.TuneroExe, system.Theme).ToRequest();
            try
            {
                // The engine's own check, which its build repeats: an ArgumentException from inside the build is not a
                // refused request but an unexpected error.
                MediaBuild.Validate(request);
            }
            catch (ArgumentException ex)
            {
                return Invalid(lines, "request: " + ex.Message);
            }
            lines.Write($"Building {(request.Source.Download is { } esd ? esd.FileName : request.Source.IsoPath)}, edition {request.Edition}, {request.RemoveApps.Count} apps and {request.RemoveParts.Count} Windows parts removed, " +
                        $"setup {(request.Answers.SkipSetup ? "skipped for " + request.Answers.AccountName : "asked")}, profile {request.FirstSignIn?.Profile ?? "none"}, to {request.OutputIso}");
            var progress = new ProgressLines(lines);
            var result = await system.Build(request, new Sync<MediaProgress>(progress.Report), CancellationToken.None);
            lines.Write("Done: " + result.Iso);
            lines.Write("SHA-256 " + result.Sha256);
            return CommandLineApply.Done;
        }
        catch (MediaRequestFileException ex)
        {
            return Invalid(lines, ex.Message);
        }
        catch (MediaBuildException ex)
        {
            var code = ex.Code is { } c ? $", code {(c is < 0 or > 0xFFFF ? $"0x{c:X8}" : c.ToString(CultureInfo.InvariantCulture))}" : "";
            lines.Write($"Build failed at stage {ex.Stage}: key {ex.Key ?? (ex.InUse ? "Media_IsoInUse" : "none")}{code}: {ex.Message}");
            if (ex.LogPath is { } log) lines.Write("Build log: " + log);
            // The start cleanup deleted every saved build: one listed now is this run's.
            foreach (var saved in system.FindResumable())
                lines.Write($"Work folder kept for Resume: {saved.WorkFolder}, {saved.Bytes} bytes ({ByteSize.Format(saved.Bytes)}); the window offers Resume or Discard, the next --build-media deletes it");
            return CommandLineApply.MediaBuildFailed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("cli", "build media failed", ex);
            lines.Write($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return CommandLineApply.Error;
        }
    }

    private static int Invalid(Lines lines, string reason)
    {
        lines.Write("Request file invalid: " + reason);
        return CommandLineApply.InvalidFile;
    }

    /// <summary>A stage's start, then its percent when it reached the next multiple of 5 (with the detail, if any).</summary>
    private sealed class ProgressLines(Lines lines)
    {
        private MediaStage? _stage;
        private int _next;

        public void Report(MediaProgress p)
        {
            if (p.Stage != _stage)
            {
                _stage = p.Stage;
                _next = 0;
                lines.Write($"{p.Stage} started");
            }
            if (p.Percent is not { } percent || percent < _next) return;
            var whole = (int)Math.Clamp(percent, 0, 100);
            _next = whole / 5 * 5 + 5;
            lines.Write(p.Detail is { Length: > 0 } detail ? $"{p.Stage} {whole}% ({detail})" : $"{p.Stage} {whole}%");
        }
    }

    /// <summary>An IProgress that reports on the caller's thread, so the lines keep their order (Progress posts them).</summary>
    private sealed class Sync<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// The run's lines with the time, to the output and the --log file as they come (a long build shows where it is). An
    /// elevated run only creates a new log file, never overwrites one: whoever starts the exe names the path.
    /// </summary>
    private sealed class Lines : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly TextWriter _output;
        private readonly StreamWriter? _log;

        public Lines(TextWriter output, string? logPath)
        {
            _output = output;
            if (logPath is null) return;
            try
            {
                _log = new StreamWriter(new FileStream(logPath, DataPaths.ProcessIsElevated ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn("cli", $"log file not written: {ex.Message}");
                Write($"Log file {logPath} not written: {ex.Message}");
            }
        }

        public void Write(string text)
        {
            var line = $"{DateTime.Now:HH:mm:ss} {text}";
            lock (_gate)
            {
                try
                {
                    _output.WriteLine(line);
                    _output.Flush();
                    _log?.WriteLine(line);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // A closed output or a full disk must not end the build.
                }
            }
        }

        public void Dispose() => _log?.Dispose();
    }
}
