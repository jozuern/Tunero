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
/// A file counts only when it has exactly the size and SHA-256 that the signed catalog names, read back from the disk:
/// it is written next to its final name as ".part" and renamed only after both match, so a broken or half-finished file
/// never has the real name. Redirects are followed by hand so that no request goes to a host outside Microsoft's media servers.
/// </summary>
public sealed class MediaDownload(HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>A transfer that receives nothing for this long has stalled and counts as a dropped connection.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a dropped connection is resumed before the download fails.</summary>
    public const int MaxResumes = 3;

    /// <summary>How many redirects are followed (each one only to a Microsoft media host).</summary>
    public const int MaxRedirects = 5;

    private const long MaxSearchAnswerBytes = 1 << 20;
    private const string HostSuffix = ".delivery.mp.microsoft.com";

    // The Timeout of HttpClient would end a 6 GB transfer; stalls are handled per read instead.
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = MaxSearchAnswerBytes,
    };

    /// <summary>Creates the ".part" file; tests replace it to simulate a faulty disk.</summary>
    internal Func<string, FileStream> OpenPart { get; set; } =
        path => new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous);

    /// <summary>Posts the catalog search for this architecture ("amd64" or "arm64") and returns the JSON answer.</summary>
    public async Task<string> SearchAsync(string arch, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            // ResponseContentRead: the answer is buffered, which enforces the 1 MB limit.
            using var response = await SendFollowingAsync("search", new Uri(MediaCatalog.SearchUrl), url =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(MediaCatalog.RequestBody(arch), Encoding.UTF8, "application/json"),
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return request;
            }, HttpCompletionOption.ResponseContentRead, stall.Token);
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
        var part = target + ".part";
        var completed = false;
        try
        {
            if (File.Exists(target) || Directory.Exists(target)) throw new MediaDownloadException(step, $"{target} already exists.");
            var state = new State();
            await using (var file = OpenPart(part))
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
                        // A drop or a stall (disk errors are MediaDownloadExceptions and not retried): the next round asks for the rest.
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                               (ex is OperationCanceledException && !ct.IsCancellationRequested))
                    {
                        throw new MediaDownloadException(step, "The download failed: " + ex.Message, ex);
                    }
                }
                if (state.Done != size) throw new MediaDownloadException(step, $"The file has {state.Done} bytes, not {size}.");
                if (!CryptographicOperations.FixedTimeEquals(state.Hash.GetHashAndReset(), sha256))
                    throw new MediaDownloadException(step, "The file does not match the SHA-256 of Microsoft's catalog.");
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            // What counts is the file on the disk, not what was counted on the way in.
            await VerifyFileAsync(step, part, size, sha256, ct);
            File.Move(part, target);
            completed = true;
            progress?.Report((size, size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaDownloadException(step, "The file could not be written: " + ex.Message, ex);
        }
        finally
        {
            if (!completed) TryDelete(part);
        }
    }

    /// <summary>Reads the file back and checks its length and SHA-256.</summary>
    internal static async Task VerifyFileAsync(string step, string path, long size, byte[] sha256, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != size) throw new MediaDownloadException(step, $"The file on the disk has {stream.Length} bytes, not {size}.");
        var actual = await SHA256.HashDataAsync(stream, ct);
        if (!CryptographicOperations.FixedTimeEquals(actual, sha256))
            throw new MediaDownloadException(step, "The file on the disk does not match the SHA-256 of Microsoft's catalog.");
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
        var done = state.Done;
        using var response = await SendFollowingAsync(step, url, u =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, u);
            if (done > 0) request.Headers.Range = new RangeHeaderValue(done, null);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        // Every hop was checked before it was requested; this is a second look at where the answer came from.
        if (!IsMicrosoftMediaUrl(response.RequestMessage?.RequestUri ?? url))
            throw new MediaDownloadException(step, "The download was redirected away from Microsoft's media servers.");
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new MediaDownloadException(step, $"The server cannot serve the file from byte {done}: its size does not match the catalog's {size}.");
        if (!response.IsSuccessStatusCode) throw new MediaDownloadException(step, $"The server answered {(int)response.StatusCode}.");
        var whole = response.StatusCode != HttpStatusCode.PartialContent || done == 0;
        if (!whole && response.Content.Headers.ContentRange?.From != done)
            throw new MediaDownloadException(step, "The server resumed at the wrong byte.");
        if (whole && done > 0) Restart(state);

        // The file continues exactly where the counted bytes end, whatever an earlier attempt left behind.
        Disk(step, () =>
        {
            file.SetLength(state.Done);
            file.Position = state.Done;
        });

        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        var buffer = new byte[81920];
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        int read;
        while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
        {
            stall.CancelAfter(StallTimeout);
            if (state.Done + read > size) throw new MediaDownloadException(step, $"The file is larger than the {size} bytes the catalog names.");
            try
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new MediaDownloadException(step, "The file could not be written: " + ex.Message, ex);
            }
            state.Hash.AppendData(buffer, 0, read);
            state.Done += read;
            if (progress is not null && clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                lastReport = clock.Elapsed;
                progress.Report((state.Done, size));
            }
        }
        if (state.Done >= size) return;
        // A whole-file answer that ends cleanly short means the catalog's size is wrong: a second download would end the same way.
        if (whole) throw new MediaDownloadException(step, $"The server's file ends after {state.Done} bytes, not the catalog's {size}.");
        // A resumed answer that ends early: a drop that looks like the end.
        throw new IOException("The connection ended before the whole file arrived.");
    }

    private static void Disk(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaDownloadException(step, "The file could not be written: " + ex.Message, ex);
        }
    }

    private static void Restart(State state)
    {
        state.Hash.GetHashAndReset();
        state.Done = 0;
    }

    /// <summary>
    /// Sends the request and follows redirects by hand: every address, the first and each Location, must be a Microsoft
    /// media host BEFORE it is requested, so nothing is ever sent elsewhere.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(string step, Uri start, Func<Uri, HttpRequestMessage> make, HttpCompletionOption option, CancellationToken ct)
    {
        var url = start;
        for (var hop = 0; ; hop++)
        {
            if (!IsMicrosoftMediaUrl(url)) throw new MediaDownloadException(step, "The address is not one of Microsoft's media servers.");
            using var request = make(url);
            var response = await _http.SendAsync(request, option, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new MediaDownloadException(step, "A redirect named no address.");
            if (hop >= MaxRedirects) throw new MediaDownloadException(step, "Too many redirects.");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
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
