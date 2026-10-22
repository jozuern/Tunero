using System.IO;
using Optimizer.Core.Media;

namespace Optimizer.App.ViewModels;

/// <summary>
/// The install media page's developer switch --media-demo: a trimmed copy of Microsoft's catalog (embedded, read once)
/// and a build that only reports its stages. Nothing goes to the network, runs DISM or writes a file.
/// </summary>
internal static class InstallMediaDemo
{
    public sealed class Catalog : IMediaCatalogSource
    {
        public async Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct)
        {
            foreach (var step in new[] { "search", "cab", "unpack" })
            {
                p?.Report(step);
                await Task.Delay(100, ct);
            }
            using var stream = typeof(InstallMediaDemo).Assembly.GetManifestResourceStream("MediaDemo.products.xml")
                               ?? throw new FileNotFoundException("MediaDemo.products.xml");
            using var reader = new StreamReader(stream);
            return MediaCatalog.ParseProducts(await reader.ReadToEndAsync(ct));
        }
    }

    /// <summary>
    /// The first run fails in Unpack with a sample DISM error, every later one succeeds. With <see cref="Freeze"/> the run
    /// stops in the middle of Apps (at half, on a sample package) and waits for Stop.
    /// </summary>
    public sealed class Build
    {
        private int _runs;

        public bool Freeze { get; init; }

        public async Task<MediaBuildResult> RunAsync(MediaRequest request, IProgress<MediaProgress> progress, CancellationToken ct)
        {
            var fail = _runs++ == 0 && !Freeze;
            foreach (var stage in new[] { MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Apps, MediaStage.Files, MediaStage.Iso, MediaStage.Verify })
            {
                if (stage == MediaStage.Apps && request.RemoveApps.Count == 0 && request.RemoveUnreviewed.Count == 0) continue;
                progress.Report(new MediaProgress(stage, null, null));
                if (Freeze && stage == MediaStage.Apps)
                {
                    progress.Report(new MediaProgress(stage, 50, "Microsoft.BingNews"));
                    await Task.Delay(Timeout.Infinite, ct);
                }
                for (var percent = 0; percent <= 100; percent += 25)
                {
                    if (!Freeze) await Task.Delay(150, ct);
                    progress.Report(new MediaProgress(stage, percent, stage == MediaStage.Unpack ? "install.wim" : null));
                    if (fail && stage == MediaStage.Unpack && percent == 50) throw Failure();
                }
            }
            progress.Report(new MediaProgress(MediaStage.Cleanup, null, null));
            await Task.Delay(300, ct);
            return Result(request.OutputIso);
        }
    }

    /// <summary>A sample result: a size and a SHA-256 that belong to no file (the demo writes none).</summary>
    public static MediaBuildResult Result(string iso) =>
        new(iso, 5_712_000_000, "5E3A9B1C7D2F4086A1B3C5D7E9F0123456789ABCDEF0123456789ABCDEF01234")
        {
            LogPath = Path.Combine(Optimizer.Core.Platform.DataPaths.Logs, "media-demo.log"),
        };

    /// <summary>A German 26H2 ISO with Home, Pro and Education (what the page shows for an own ISO).</summary>
    public static IsoInfo Iso()
    {
        static WimImageInfo Image(int index, string edition, string name) =>
            new(index, name, edition, "x64", "10.0.26300.9711", 26300, "de-DE", ["de-DE"]);
        return new IsoInfo("install.wim", [Image(1, "Core", "Windows 11 Home"), Image(2, "Professional", "Windows 11 Pro"), Image(3, "Education", "Windows 11 Education")], HasBootWim: true);
    }

    /// <summary>
    /// The apps of the sample ISO's images: what the 26300 Pro image held (PCO-Test, 2026-10-21), without frameworks, as
    /// full package names.
    /// </summary>
    public static IReadOnlyList<string> IsoApps() =>
    [
        .. new[]
        {
            "Clipchamp.Clipchamp", "MSTeams", "Microsoft.BingNews", "Microsoft.BingSearch", "Microsoft.BingWeather", "Microsoft.GamingApp", "Microsoft.GetHelp",
            "Microsoft.MicrosoftOfficeHub", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.MicrosoftStickyNotes", "Microsoft.OutlookForWindows",
            "Microsoft.Paint", "Microsoft.PowerAutomateDesktop", "Microsoft.Todos", "Microsoft.Windows.DevHome", "Microsoft.Windows.Photos",
            "Microsoft.WindowsAlarms", "Microsoft.WindowsCalculator", "Microsoft.WindowsCamera", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsNotepad",
            "Microsoft.WindowsSoundRecorder", "Microsoft.WindowsStore", "Microsoft.Xbox.TCUI", "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider",
            "Microsoft.XboxSpeechToTextOverlay", "Microsoft.YourPhone", "Microsoft.ZuneMusic", "MicrosoftCorporationII.QuickAssist", "MicrosoftWindows.CrossDevice",
        }.Select(name => $"{name}_1.0.0.0_x64__8wekyb3d8bbwe"),
    ];

    /// <summary>What a DISM failure looks like on the page: stage, reason, DISM's code and the log.</summary>
    public static MediaBuildException Failure() =>
        new(MediaStage.Unpack, "Demo: DISM failed.", unchecked((int)0x800F081F), "Media_ImageCheckFailed")
        {
            LogPath = Path.Combine(Optimizer.Core.Platform.DataPaths.Logs, "media-demo.log"),
        };
}
