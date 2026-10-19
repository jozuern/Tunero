using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Optimizer.Core.Media;

/// <summary>A step of the media download failed. <see cref="Step"/> is "search", "cab" or "esd".</summary>
public sealed class MediaDownloadException(string step, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Step { get; } = step;
}

/// <summary>
/// Talks to Microsoft's media servers: the catalog search and the download of the catalog cab and the Windows image.
/// A file counts only when it has exactly the size and SHA-256 that the signed catalog names: it is written next to its
/// final name as ".part" and renamed only after both match, so a broken or half-finished file never has the real name.
/// </summary>
public sealed class MediaDownload(HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>A transfer that receives nothing for this long has stalled and counts as a dropped connection.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a dropped connection is resumed before the download fails.</summary>
    public const int MaxResumes = 3;

    private const long MaxSearchAnswerBytes = 1 << 20;
    private const string HostSuffix = ".delivery.mp.microsoft.com";

    // The Timeout of HttpClient would end a 6 GB transfer; stalls are handled per read instead.
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = MaxSearchAnswerBytes,
    };

    /// <summary>Posts the catalog search for this architecture ("amd64" or "arm64") and returns the JSON answer.</summary>
    public async Task<string> SearchAsync(string arch, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, MediaCatalog.SearchUrl)
            {
                Content = new StringContent(MediaCatalog.RequestBody(arch), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, stall.Token);
            if (!response.IsSuccessStatusCode) throw new MediaDownloadException("search", $"The catalog search answered {(int)response.StatusCode}.");
            return await response.Content.ReadAsStringAsync(stall.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new MediaDownloadException("search", "The catalog search failed: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="target"/> (which must not exist) and checks size and hash. A dropped
    /// connection is resumed with a Range request up to <see cref="MaxResumes"/> times; a server that answers 200 instead
    /// of 206 restarts the file from zero. On any failure or cancel nothing is left behind.
    /// </summary>
    /// <param name="step">"cab" or "esd": named in the <see cref="MediaDownloadException"/>.</param>
    public async Task DownloadAsync(string step, Uri url, string target, long size, byte[] sha256, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (!IsMicrosoftMediaUrl(url)) throw new MediaDownloadException(step, "The download address is not one of Microsoft's media servers.");
        if (File.Exists(target) || Directory.Exists(target)) throw new MediaDownloadException(step, $"{target} already exists.");
        var part = target + ".part";
        var completed = false;
        try
        {
            var state = new State();
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (state.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                for (var resumes = 0; ; resumes++)
                {
                    try
                    {
                        await TransferAsync(step, url, file, state, size, progress, ct);
                        break;
                    }
                    catch (Exception ex) when (resumes < MaxResumes && (ex is HttpRequestException or IOException ||
                                                                       (ex is OperationCanceledException && !ct.IsCancellationRequested)))
                    {
                        // A drop or a stall: the next round asks for the rest.
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException ||
                                               (ex is OperationCanceledException && !ct.IsCancellationRequested))
                    {
                        throw new MediaDownloadException(step, "The download failed: " + ex.Message, ex);
                    }
                }
                await file.FlushAsync(ct);
                if (state.Done != size) throw new MediaDownloadException(step, $"The file has {state.Done} bytes, not {size}.");
                if (!CryptographicOperations.FixedTimeEquals(state.Hash.GetHashAndReset(), sha256))
                    throw new MediaDownloadException(step, "The file does not match the SHA-256 of Microsoft's catalog.");
            }
            progress?.Report((size, size));
            File.Move(part, target);
            completed = true;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MediaDownloadException(step, "The file could not be written: " + ex.Message, ex);
        }
        finally
        {
            if (!completed) TryDelete(part);
        }
    }

    private sealed class State
    {
        public IncrementalHash Hash = null!;
        public long Done;
    }

    /// <summary>One request: from byte <c>Done</c> on, appended to <paramref name="file"/> and the hash.</summary>
    private async Task TransferAsync(string step, Uri url, FileStream file, State state, long size, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (state.Done > 0) request.Headers.Range = new RangeHeaderValue(state.Done, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        // The final address after redirects counts too: nothing is written from a server that is not Microsoft's.
        if (!IsMicrosoftMediaUrl(response.RequestMessage?.RequestUri ?? url))
            throw new MediaDownloadException(step, "The download was redirected away from Microsoft's media servers.");
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            Restart(file, state);
            throw new IOException("The server refused the range; starting again.");
        }
        if (!response.IsSuccessStatusCode) throw new MediaDownloadException(step, $"The server answered {(int)response.StatusCode}.");
        if (response.StatusCode == HttpStatusCode.PartialContent && state.Done > 0)
        {
            if (response.Content.Headers.ContentRange?.From != state.Done)
                throw new MediaDownloadException(step, "The server resumed at the wrong byte.");
        }
        else
        {
            // 200: the whole file from the start, whatever was asked.
            Restart(file, state);
        }

        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        var buffer = new byte[81920];
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        int read;
        while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
        {
            stall.CancelAfter(StallTimeout);
            if (state.Done + read > size) throw new MediaDownloadException(step, $"The file is larger than the {size} bytes the catalog names.");
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            state.Hash.AppendData(buffer, 0, read);
            state.Done += read;
            if (progress is not null && clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                lastReport = clock.Elapsed;
                progress.Report((state.Done, size));
            }
        }
        // Ended early without an error: a drop that looks like the end of the file.
        if (state.Done < size) throw new IOException("The connection ended before the whole file arrived.");
    }

    private static void Restart(FileStream file, State state)
    {
        if (state.Done == 0) return;
        file.SetLength(0);
        file.Position = 0;
        state.Hash.GetHashAndReset();
        state.Done = 0;
    }

    /// <summary>http or https to a host under delivery.mp.microsoft.com; integrity comes from the catalog's SHA-256, not from TLS.</summary>
    private static bool IsMicrosoftMediaUrl(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) &&
        url.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}
