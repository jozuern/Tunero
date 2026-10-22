using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

public class MediaDownloadTests
{
    private const string EsdUrl = "http://dl.delivery.mp.microsoft.com/x/test.esd";

    private static byte[] Body(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            var response = respond(request, Requests.Count);
            response.RequestMessage ??= request;
            return response;
        }
    }

    /// <summary>Gives <paramref name="data"/> up to <paramref name="failAfter"/> bytes, then runs <paramref name="atEnd"/> (throws or blocks).</summary>
    private sealed class BrokenStream(byte[] data, int failAfter, Func<CancellationToken, ValueTask<int>> atEnd) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= failAfter) return atEnd(ct);
            var n = Math.Min(Math.Min(buffer.Length, 16384), failAfter - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return ValueTask.FromResult(n);
        }
    }

    private static HttpResponseMessage Ok(byte[] data, HttpStatusCode status = HttpStatusCode.OK, int offset = 0)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data, offset, data.Length - offset) };
        if (status == HttpStatusCode.PartialContent)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
        return response;
    }

    private static HttpResponseMessage Dropping(byte[] data, int failAfter) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(data, failAfter, _ => throw new IOException("connection dropped"))) };

    private static async Task<(string Folder, string Target)> Run(FakeHandler handler, byte[] data)
    {
        var folder = TestFolders.Create("mediadl");
        var target = Path.Combine(folder, "test.esd");
        using var download = new MediaDownload(handler);
        await download.DownloadAsync("esd", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, CancellationToken.None);
        return (folder, target);
    }

    private static async Task<(string Folder, string Target, Exception Error)> RunFailing(FakeHandler handler, byte[] data,
        byte[]? hash = null, string url = EsdUrl)
    {
        var folder = TestFolders.Create("mediadl");
        var target = Path.Combine(folder, "test.esd");
        using var download = new MediaDownload(handler);
        var error = await Record.ExceptionAsync(() =>
            download.DownloadAsync("esd", new Uri(url), target, data.Length, hash ?? SHA256.HashData(data), null, CancellationToken.None));
        Assert.NotNull(error);
        return (folder, target, error);
    }

    [Fact]
    public async Task DownloadChecksTheHash()
    {
        var data = Body(1 << 20);
        var (folder, target) = await Run(new FakeHandler((_, _) => Ok(data)), data);
        try
        {
            Assert.Equal(1 << 20, new FileInfo(target).Length);
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task WrongHashDeletesTheFile()
    {
        var data = Body(1 << 20);
        var (folder, target, error) = await RunFailing(new FakeHandler((_, _) => Ok(data)), data, hash: new byte[32]);
        try
        {
            Assert.Equal("esd", Assert.IsType<MediaDownloadException>(error).Step);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task WrongSizeDeletesTheFile()
    {
        var data = Body(1 << 20);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "test.esd");
            using var download = new MediaDownload(new FakeHandler((_, _) => Ok(data)));
            var error = await Assert.ThrowsAsync<MediaDownloadException>(() =>
                download.DownloadAsync("esd", new Uri(EsdUrl), target, data.Length + 1, SHA256.HashData(data), null, CancellationToken.None));
            Assert.Equal("esd", error.Step);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task DroppedConnectionResumesWithRange()
    {
        var data = Body(1 << 20);
        var handler = new FakeHandler((_, n) => n == 1 ? Dropping(data, 300 * 1024) : Ok(data, HttpStatusCode.PartialContent, 300 * 1024));
        var (folder, target) = await Run(handler, data);
        try
        {
            Assert.Equal(2, handler.Requests.Count);
            Assert.Null(handler.Requests[0].Headers.Range);
            Assert.Equal(300L * 1024, handler.Requests[1].Headers.Range!.Ranges.Single().From);
            Assert.Equal(data, File.ReadAllBytes(target));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ServerIgnoringRangeRestarts()
    {
        var data = Body(1 << 20);
        var handler = new FakeHandler((_, n) => n == 1 ? Dropping(data, 300 * 1024) : Ok(data));
        var (folder, target) = await Run(handler, data);
        try
        {
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(data, File.ReadAllBytes(target));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task GivesUpAfterThreeResumes()
    {
        var data = Body(1 << 20);
        var handler = new FakeHandler((_, _) => Dropping(data, 1000));
        var (folder, target, error) = await RunFailing(handler, data);
        try
        {
            Assert.Equal("esd", Assert.IsType<MediaDownloadException>(error).Step);
            Assert.Equal(4, handler.Requests.Count);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ResumeThatStartsElsewhereIsRefused()
    {
        var data = Body(1 << 20);
        // The answer claims to be a range but starts at the wrong byte: appending it would corrupt the file.
        var handler = new FakeHandler((_, n) => n == 1 ? Dropping(data, 300 * 1024) : Ok(data, HttpStatusCode.PartialContent, 100));
        var (folder, target, error) = await RunFailing(handler, data);
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ForeignHostIsRefused()
    {
        var data = Body(1024);
        var handler = new FakeHandler((_, _) => Ok(data));
        var (folder, target, error) = await RunFailing(handler, data, url: "https://example.com/x.esd");
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.Empty(handler.Requests);
            Assert.False(File.Exists(target));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Theory]
    [InlineData("ftp://dl.delivery.mp.microsoft.com/x.esd")]
    [InlineData("https://delivery.mp.microsoft.com.evil.example/x.esd")]
    [InlineData("https://evildelivery.mp.microsoft.com/x.esd")]
    public async Task OtherUrlsAreRefused(string url)
    {
        var data = Body(1024);
        var handler = new FakeHandler((_, _) => Ok(data));
        var (folder, _, error) = await RunFailing(handler, data, url: url);
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.Empty(handler.Requests);
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task RedirectToAnotherHostIsRefused()
    {
        var data = Body(1024);
        var handler = new FakeHandler((_, _) =>
        {
            var response = Ok(data);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://evil.example.com/x.esd");
            return response;
        });
        var (folder, target, error) = await RunFailing(handler, data);
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ExistingTargetIsAnError()
    {
        var data = Body(1024);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "test.esd");
            File.WriteAllText(target, "old");
            var handler = new FakeHandler((_, _) => Ok(data));
            using var download = new MediaDownload(handler);
            await Assert.ThrowsAsync<MediaDownloadException>(() =>
                download.DownloadAsync("cab", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, CancellationToken.None));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.Empty(handler.Requests);
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ServerErrorFailsWithTheStep()
    {
        var data = Body(1024);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "test.cab");
            using var download = new MediaDownload(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)));
            var error = await Assert.ThrowsAsync<MediaDownloadException>(() =>
                download.DownloadAsync("cab", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, CancellationToken.None));
            Assert.Equal("cab", error.Step);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task StopDeletesThePartialFile()
    {
        var data = Body(1 << 20);
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BrokenStream(data, 100 * 1024, async ct =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            })),
        });
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "test.esd");
            using var download = new MediaDownload(handler);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                download.DownloadAsync("esd", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, cts.Token));
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task StalledConnectionCountsAsDropped()
    {
        var data = Body(1 << 20);
        var old = MediaDownload.StallTimeout;
        // The broken connection stalls for good, so any limit ends it; one the healthy second connection cannot reach on a
        // busy CI runner (200 ms did, and a third request followed).
        MediaDownload.StallTimeout = TimeSpan.FromSeconds(1);
        try
        {
            var handler = new FakeHandler((_, n) => n == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new BrokenStream(data, 300 * 1024, async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; })),
                }
                : Ok(data, HttpStatusCode.PartialContent, 300 * 1024));
            var (folder, target) = await Run(handler, data);
            try
            {
                Assert.Equal(2, handler.Requests.Count);
                Assert.Equal(data, File.ReadAllBytes(target));
            }
            finally { TestFolders.Delete(folder); }
        }
        finally { MediaDownload.StallTimeout = old; }
    }

    [Fact]
    public async Task ProgressEndsAtTheTotal()
    {
        var data = Body(1 << 20);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var last = (Done: 0L, Total: 0L);
            using var download = new MediaDownload(new FakeHandler((_, _) => Ok(data)));
            await download.DownloadAsync("esd", new Uri(EsdUrl), Path.Combine(folder, "a.esd"), data.Length, SHA256.HashData(data),
                new SynchronousProgress(p => last = p), CancellationToken.None);
            Assert.Equal((data.Length, data.Length), last);
        }
        finally { TestFolders.Delete(folder); }
    }

    private sealed class SynchronousProgress(Action<(long Done, long Total)> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => report(value);
    }

    [Fact]
    public async Task SearchPostsTheBody()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        using var download = new MediaDownload(handler);
        var json = await download.SearchAsync("amd64", CancellationToken.None);
        Assert.Equal("[]", json);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(MediaCatalog.SearchUrl, request.RequestUri!.ToString());
        Assert.Equal(MediaCatalog.RequestBody("amd64"), handler.Bodies[0]);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
    }

    [Fact]
    public async Task SearchErrorStatusFailsWithTheStep()
    {
        using var download = new MediaDownload(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var error = await Assert.ThrowsAsync<MediaDownloadException>(() => download.SearchAsync("amd64", CancellationToken.None));
        Assert.Equal("search", error.Step);
    }

    [Fact]
    public async Task SearchAnswerOverOneMegabyteIsRefused()
    {
        var big = new string('x', (1 << 20) + 10);
        using var download = new MediaDownload(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big) }));
        var error = await Assert.ThrowsAsync<MediaDownloadException>(() => download.SearchAsync("amd64", CancellationToken.None));
        Assert.Equal("search", error.Step);
    }

    private static HttpResponseMessage Redirect(string to, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task RedirectToAForeignHostIsNeverRequested()
    {
        var data = Body(1024);
        var handler = new FakeHandler((_, _) => Redirect("https://evil.example/x"));
        var (folder, target, error) = await RunFailing(handler, data);
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.Single(handler.Requests);
            Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "evil.example");
            Assert.False(File.Exists(target));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task RedirectBetweenDeliveryHostsIsFollowed()
    {
        var data = Body(1024);
        var handler = new FakeHandler((request, n) =>
            n == 1 ? Redirect("http://tlu.dl.delivery.mp.microsoft.com/y.esd") : n == 2 ? Redirect("/z.esd") : Ok(data));
        var (folder, target) = await Run(handler, data);
        try
        {
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal("http://tlu.dl.delivery.mp.microsoft.com/z.esd", handler.Requests[2].RequestUri!.ToString());
            Assert.Equal(data, File.ReadAllBytes(target));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task SixRedirectsFail()
    {
        var data = Body(1024);
        var handler = new FakeHandler((_, n) => Redirect($"http://dl.delivery.mp.microsoft.com/{n}.esd"));
        var (folder, _, error) = await RunFailing(handler, data);
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.Equal(6, handler.Requests.Count);
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task SearchDoesNotFollowARedirectToAForeignHost()
    {
        var handler = new FakeHandler((_, _) => Redirect("https://evil.example/x"));
        using var download = new MediaDownload(handler);
        var error = await Assert.ThrowsAsync<MediaDownloadException>(() => download.SearchAsync("amd64", CancellationToken.None));
        Assert.Equal("search", error.Step);
        Assert.Single(handler.Requests);
    }

    /// <summary>Writes through, but the second call fails or flips a bit, like a bad disk.</summary>
    private sealed class FaultyFile(string path, bool fail) : FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous)
    {
        private int _writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (++_writes == 2)
            {
                if (fail) throw new IOException("disk full");
                var copy = buffer.ToArray();
                copy[0] ^= 0xFF;
                return base.WriteAsync(copy, ct);
            }
            return base.WriteAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task DiskWriteFailureIsNotRetried()
    {
        var data = Body(1 << 20);
        var handler = new FakeHandler((_, _) => Ok(data));
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "test.esd");
            using var download = new MediaDownload(handler) { OpenPart = p => new FaultyFile(p, fail: true) };
            var error = await Assert.ThrowsAsync<MediaDownloadException>(() =>
                download.DownloadAsync("esd", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, CancellationToken.None));
            Assert.Equal("esd", error.Step);
            Assert.Single(handler.Requests);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task TheFileOnDiskDecides()
    {
        // The bytes counted and hashed in memory are right; the ones that reached the disk are not.
        var data = Body(1 << 20);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "test.esd");
            using var download = new MediaDownload(new FakeHandler((_, _) => Ok(data))) { OpenPart = p => new FaultyFile(p, fail: false) };
            var error = await Assert.ThrowsAsync<MediaDownloadException>(() =>
                download.DownloadAsync("esd", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, CancellationToken.None));
            Assert.Equal("esd", error.Step);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task VerifyFileChecksSizeAndHash()
    {
        var data = Body(5000);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var path = Path.Combine(folder, "f.part");
            File.WriteAllBytes(path, data);
            await MediaDownload.VerifyFileAsync("esd", path, data.Length, SHA256.HashData(data), CancellationToken.None);
            var good = SHA256.HashData(data);
            data[10] ^= 1;
            File.WriteAllBytes(path, data);
            await Assert.ThrowsAsync<MediaDownloadException>(() => MediaDownload.VerifyFileAsync("esd", path, data.Length, good, CancellationToken.None));
            await Assert.ThrowsAsync<MediaDownloadException>(() => MediaDownload.VerifyFileAsync("esd", path, data.Length + 1, SHA256.HashData(data), CancellationToken.None));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task MissingFolderFailsWithTheStep()
    {
        var data = Body(1024);
        var target = Path.Combine(Path.GetTempPath(), "pco-nonexistent-" + Guid.NewGuid().ToString("N"), "x.esd");
        using var download = new MediaDownload(new FakeHandler((_, _) => Ok(data)));
        var error = await Assert.ThrowsAsync<MediaDownloadException>(() =>
            download.DownloadAsync("cab", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data), null, CancellationToken.None));
        Assert.Equal("cab", error.Step);
    }

    [Fact]
    public async Task RangeNotSatisfiableFailsAtOnce()
    {
        var data = Body(1 << 20);
        var handler = new FakeHandler((_, n) => n == 1 ? Dropping(data, 300 * 1024) : new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        var (folder, target, error) = await RunFailing(handler, data);
        try
        {
            Assert.IsType<MediaDownloadException>(error);
            Assert.Equal(2, handler.Requests.Count);
            Assert.False(File.Exists(target));
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ProgressEndsOnlyAfterTheMove()
    {
        var data = Body(1 << 20);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var target = Path.Combine(folder, "a.esd");
            var existedAtFinal = false;
            using var download = new MediaDownload(new FakeHandler((_, _) => Ok(data)));
            await download.DownloadAsync("esd", new Uri(EsdUrl), target, data.Length, SHA256.HashData(data),
                new SynchronousProgress(p => { if (p.Done == p.Total) existedAtFinal = File.Exists(target); }), CancellationToken.None);
            Assert.True(existedAtFinal);
        }
        finally { TestFolders.Delete(folder); }
    }

    [Fact]
    public async Task ShortCleanEndOfAFullAnswerFailsAtOnce()
    {
        var data = Body(1 << 20);
        var folder = TestFolders.Create("mediadl");
        try
        {
            var handler = new FakeHandler((_, _) => Ok(data));
            using var download = new MediaDownload(handler);
            await Assert.ThrowsAsync<MediaDownloadException>(() =>
                download.DownloadAsync("esd", new Uri(EsdUrl), Path.Combine(folder, "a.esd"), data.Length + 5, SHA256.HashData(data), null, CancellationToken.None));
            Assert.Single(handler.Requests);
        }
        finally { TestFolders.Delete(folder); }
    }
}
