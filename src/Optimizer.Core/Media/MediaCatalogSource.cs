using Optimizer.Core.Actions;
using Optimizer.Core.Startup;
using AppLog = Optimizer.Core.Logging.Log;

namespace Optimizer.Core.Media;

/// <summary>Where the install media page gets Microsoft's list of Windows images from. Tests and the demo pass their own.</summary>
public interface IMediaCatalogSource
{
    /// <summary>
    /// Every image file of the catalog for <paramref name="arch"/> ("amd64"). <paramref name="p"/> gets the step that
    /// starts ("search", "cab", "unpack"). Throws <see cref="MediaDownloadException"/> naming the step that failed.
    /// </summary>
    Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct);
}

/// <summary>
/// Microsoft's catalog, the way the Media Creation Tool reads it: the search names products.cab with its size and SHA-256,
/// the cab is downloaded into a fresh work folder locked to Administrators and SYSTEM, checked (size, SHA-256, Microsoft's
/// signature) and unpacked with expand.exe from System32; the single file inside is products.xml. The work folder is
/// deleted afterwards, also after a failure or Stop. The search endpoint is not documented by Microsoft.
/// </summary>
public sealed class MediaCatalogSource(IMediaSteps steps, IProcessRunner processes, string drive, HttpMessageHandler? handler = null,
    Func<string, bool>? signedByMicrosoft = null) : IMediaCatalogSource
{
    /// <summary>products.xml is a few megabytes; anything far larger is not the catalog.</summary>
    private const long MaxCatalogBytes = 64L << 20;

    private readonly Func<string, bool> _signed = signedByMicrosoft ?? (path => SignatureVerifier.Verify(path).IsMicrosoft);

    public async Task<IReadOnlyList<EsdFile>> LoadAsync(string arch, IProgress<string>? p, CancellationToken ct)
    {
        using var download = new MediaDownload(handler);
        p?.Report("search");
        var answer = await download.SearchAsync(arch, ct);
        var cab = MediaCatalog.ParseSearchAnswer(answer) ?? throw new MediaDownloadException("search", "The catalog search named no products.cab.");

        p?.Report("cab");
        WorkFolder work;
        try
        {
            // NewWorkFolder only takes the drive from the path: the folder lands at its root.
            work = steps.NewWorkFolder(Path.Combine(drive, "catalog.iso"));
        }
        catch (MediaBuildException ex)
        {
            throw new MediaDownloadException("cab", "No work folder for the catalog: " + ex.Message, ex);
        }
        var folder = work.Path;
        // A disk error names the step it happened in.
        var step = "cab";
        try
        {
            var cabFile = Path.Combine(folder, "catalog.cab");
            await download.DownloadAsync("cab", cab.Url, cabFile, cab.Size, cab.Sha256, null, ct);
            if (!_signed(cabFile)) throw new MediaDownloadException("cab", "products.cab is not signed by Microsoft.");

            step = "unpack";
            p?.Report(step);
            var target = Path.Combine(folder, "catalog");
            Directory.CreateDirectory(target);
            var (code, output) = await Task.Run(() => processes.Run("expand.exe", $"\"{cabFile}\" -F:* \"{target}\"", TimeSpan.FromMinutes(2)), ct);
            ct.ThrowIfCancellationRequested();
            if (code != 0) throw new MediaDownloadException("unpack", $"expand.exe ended with {code}: {output.Trim()}");
            var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
            if (files.Length != 1) throw new MediaDownloadException("unpack", $"products.cab holds {files.Length} files, not 1.");
            var info = new FileInfo(files[0]);
            if (info.Length > MaxCatalogBytes) throw new MediaDownloadException("unpack", $"products.xml has {info.Length} bytes.");
            try
            {
                return MediaCatalog.ParseProducts(await File.ReadAllTextAsync(info.FullName, ct));
            }
            catch (FormatException ex)
            {
                throw new MediaDownloadException("unpack", "products.xml cannot be read: " + ex.Message, ex);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaDownloadException(step, $"The catalog {(step == "cab" ? "download could not be checked" : "could not be unpacked")}: {ex.Message}", ex);
        }
        finally
        {
            work.Dispose();
            try
            {
                steps.DeleteWorkFolder(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The next start's cleanup removes it.
                AppLog.Warn("media", $"catalog work folder {folder} left in place: {ex.Message}");
            }
        }
    }
}
