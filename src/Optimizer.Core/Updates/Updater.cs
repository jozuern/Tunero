using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Updates;

public enum UpdateOutcome { Ready, ChecksumMismatch, SignatureInvalid, DownloadFailed, NoAssets }

/// <param name="Sha256">Hash of the downloaded exe, checked against the release's signed SHA-256 file.</param>
public sealed record UpdateDownload(UpdateOutcome Outcome, string? FilePath = null, string? Sha256 = null);

/// <summary>
/// Self-update after the user confirms it: downloads the release exe, its SHA-256 file and the signature from this
/// repository's release (links checked by <see cref="ReleaseCheck"/>). The checksum counts only when the project's key
/// signed it for this version (<see cref="UpdateSignature"/>), and the exe only when it has that checksum; then it is
/// swapped for the running exe.
/// </summary>
/// <param name="publicKeyPem">The key that must have signed the release; null uses the key built into the app.</param>
public sealed partial class Updater(HttpMessageHandler? handler = null, string? publicKeyPem = null) : IDisposable
{
    /// <summary>The exe is about 80 MB; anything far larger is not a release of this app and is not written to the disk.</summary>
    public const long MaxExeBytes = 300L << 20;

    /// <summary>A download that receives nothing for this long has stalled (HttpClient.Timeout ends once the headers arrive).</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // The checksum and signature files are a few hundred bytes: a larger buffered answer is refused. The exe is streamed
    // (not buffered), so this limit does not apply to it.
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10), MaxResponseContentBufferSize = 64 << 10 };
    // Null: any of the embedded keys (the release key and the offline backup key) may have signed the release.
    private readonly string? _key = publicKeyPem;

    /// <param name="folder">Where the download goes: the app's protected data folder (updates).</param>
    /// <param name="progress">Percent, when the server sends the size.</param>
    public async Task<UpdateDownload> DownloadAsync(ReleaseCheckResult release, string folder, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (release.Status != ReleaseCheckStatus.NewerAvailable || release.Assets is not { } assets || release.Latest is not { } version ||
            (_key is null && !UpdateSignature.IsConfigured))
            return new(UpdateOutcome.NoAssets);
        var target = Path.Combine(folder, $"Tunero-{version.ToString(3)}.exe");
        var partial = target + ".partial";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (ParseChecksum(await GetStringAsync(assets.ChecksumUrl, ct)) is not { } expected) return new(UpdateOutcome.DownloadFailed);
            if (!UpdateSignature.Verify(version, expected, await GetStringAsync(assets.SignatureUrl, ct), _key))
                return new(UpdateOutcome.SignatureInvalid);
            Directory.CreateDirectory(folder);
            string actual;
            using (var request = Request(assets.ExeUrl))
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (total > MaxExeBytes) return new(UpdateOutcome.DownloadFailed);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(partial);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                stall.CancelAfter(StallTimeout);
                while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    done += read;
                    if (done > MaxExeBytes) return new(UpdateOutcome.DownloadFailed);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    if (total > 0) progress?.Report((int)(done * 100 / total.Value));
                }
                actual = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new(UpdateOutcome.ChecksumMismatch);
            }
            File.Move(partial, target, overwrite: true);
            return new(UpdateOutcome.Ready, target, actual);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // OperationCanceledException without the caller's cancel: HttpClient's timeout or a stalled download.
            return new(UpdateOutcome.DownloadFailed);
        }
        finally
        {
            // Moved to its final name on success; anything left (too large, failed, cancelled) goes.
            TryDelete(partial);
        }
    }

    /// <summary>"&lt;64 hex&gt;  Tunero.exe" (sha256sum format) or just the hash.</summary>
    public static string? ParseChecksum(string text) =>
        ChecksumRegex().Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// Puts the new exe in place of the running one. It is first copied next to it as ".new" (a failed copy, a full disk
    /// for example, leaves the running exe untouched); Windows lets a running exe be renamed, so the running one then
    /// moves to ".old" (deleted on the next start) and ".new" takes its name; on failure the old exe is put back. The
    /// folder is usually one the signed-in user can write to (Downloads): it stays held while the files move
    /// (<see cref="SafeDelete.Pin"/>), leftovers are deleted through <see cref="SafeDelete"/>, and ".new" is created new
    /// and checked for its real path, so no step follows a link the user put there.
    /// </summary>
    public static void Install(string newExe, string runningExe)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(runningExe))!;
        // A drive root (the exe on a USB stick) is held too: it cannot be renamed or replaced.
        using var pin = SafeDelete.Pin(folder, allowRoot: true) ?? throw new IOException($"{folder} is a link or cannot be held: not updated");
        var old = runningExe + ".old";
        var staged = runningExe + ".new";
        DeleteIfThere(old);
        DeleteIfThere(staged);
        try
        {
            using (var source = File.OpenRead(newExe))
            using (var target = SafeDelete.CreateNew(staged))
                source.CopyTo(target);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
        try
        {
            File.Move(runningExe, old);
        }
        catch
        {
            // Another program holds the exe (an antivirus scan, a sync tool): the running exe stays, the copy goes.
            TryDelete(staged);
            throw;
        }
        try
        {
            File.Move(staged, runningExe);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(runningExe)) SafeDelete.DeleteFile(runningExe);
                File.Move(old, runningExe);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                Logging.Log.Error("update", $"the previous exe could not be put back, it is {old}", restore);
            }
            TryDelete(staged);
            Logging.Log.Error("update", "install failed", ex);
            throw;
        }
    }

    private static void DeleteIfThere(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) SafeDelete.DeleteFile(path);
    }

    /// <summary>
    /// Starts the installed exe only if it still has the checked hash. The exe usually sits in a folder the signed-in
    /// user can write to (Downloads), and the new process inherits this process's administrator rights without a UAC
    /// prompt: the file stays open without write or delete sharing from the hash check until the process has started,
    /// so it cannot be swapped in between. Returns false (nothing started) when the hash differs.
    /// </summary>
    public static bool StartVerified(string exe, string sha256, Action<string> start)
    {
        using var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(hold));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
        start(exe);
        return true;
    }

    /// <summary>
    /// Puts the previous exe back when the installed one cannot be trusted (<see cref="StartVerified"/> found another hash):
    /// the file in its place is deleted without following links and ".old" takes the name again. False when it failed.
    /// </summary>
    public static bool RollBack(string runningExe)
    {
        var old = runningExe + ".old";
        try
        {
            if (!File.Exists(old)) return false;
            using var pin = SafeDelete.Pin(Path.GetDirectoryName(Path.GetFullPath(runningExe))!, allowRoot: true);
            if (pin is null) return false;
            DeleteIfThere(runningExe);
            File.Move(old, runningExe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Error("update", $"the previous exe could not be put back, it is {old}", ex);
            return false;
        }
    }

    /// <summary>After an update: removes the previous exe and the downloaded files.</summary>
    public static void CleanUp(string runningExe, string folder)
    {
        TryDelete(runningExe + ".old");
        try
        {
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "Tunero-*.exe*").Concat(Directory.EnumerateFiles(folder, "PCOptimizer-*.exe*"))) TryDelete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = Request(url);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Tunero", "update"));
        return request;
    }

    /// <summary>Deletes without following links (the exe's folder is often one the signed-in user can write to).</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) SafeDelete.DeleteFile(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^\s*([0-9a-fA-F]{64})\b")]
    private static partial Regex ChecksumRegex();

    public void Dispose() => _http.Dispose();
}
