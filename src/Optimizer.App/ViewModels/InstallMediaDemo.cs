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

    /// <summary>The first run fails in Unpack with a sample DISM error, every later one succeeds.</summary>
    public sealed class Build
    {
        private int _runs;

        public async Task<string> RunAsync(MediaRequest request, IProgress<MediaProgress> progress, CancellationToken ct)
        {
            var fail = _runs++ == 0;
            foreach (var stage in new[] { MediaStage.Download, MediaStage.Check, MediaStage.Unpack, MediaStage.Apps, MediaStage.Files, MediaStage.Iso, MediaStage.Verify })
            {
                if (stage == MediaStage.Apps && request.RemoveApps.Count == 0) continue;
                progress.Report(new MediaProgress(stage, null, null));
                for (var percent = 0; percent <= 100; percent += 25)
                {
                    await Task.Delay(150, ct);
                    progress.Report(new MediaProgress(stage, percent, stage == MediaStage.Unpack ? "install.wim" : null));
                    if (fail && stage == MediaStage.Unpack && percent == 50) throw Failure();
                }
            }
            progress.Report(new MediaProgress(MediaStage.Cleanup, null, null));
            await Task.Delay(300, ct);
            return request.OutputIso;
        }
    }

    /// <summary>What a DISM failure looks like on the page: stage, reason, DISM's code and the log.</summary>
    public static MediaBuildException Failure() =>
        new(MediaStage.Unpack, "Demo: DISM failed.", unchecked((int)0x800F081F), "Media_ImageCheckFailed")
        {
            LogPath = Path.Combine(Optimizer.Core.Platform.DataPaths.Logs, "media-demo.log"),
        };
}
