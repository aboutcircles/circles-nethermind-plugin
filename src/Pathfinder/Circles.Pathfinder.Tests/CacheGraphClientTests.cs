using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for CacheGraphClient — HTTP client for fetching graph snapshots.
/// Covers: ETag conditional requests, 304 NotModified, 503 handling,
/// gzip/brotli decompression, and successful deserialization.
/// </summary>
[TestFixture]
public class CacheGraphClientTests
{
    private const string BaseUrl = "http://cache-service:3001";

    [Test]
    public async Task FetchGraphSnapshot_ShouldReturn304_WhenETagMatches()
    {
        var handler = new MockHttpHandler(HttpStatusCode.NotModified, "");
        var client = CreateClient(handler);

        var result = await client.FetchGraphSnapshotAsync("\"1234\"", CancellationToken.None);

        Assert.That(result.IsNotModified, Is.True);
        Assert.That(result.Snapshot, Is.Null);
    }

    [Test]
    public async Task FetchGraphSnapshot_ShouldSendIfNoneMatchHeader()
    {
        var handler = new MockHttpHandler(HttpStatusCode.NotModified, "");
        var client = CreateClient(handler);

        await client.FetchGraphSnapshotAsync("\"5000\"", CancellationToken.None);

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.That(handler.LastRequest!.Headers.IfNoneMatch.Count, Is.EqualTo(1));
        Assert.That(handler.LastRequest.Headers.IfNoneMatch.First().Tag, Is.EqualTo("\"5000\""));
    }

    [Test]
    public void FetchGraphSnapshot_ShouldThrow_On503ServiceUnavailable()
    {
        var handler = new MockHttpHandler(HttpStatusCode.ServiceUnavailable, "");
        var client = CreateClient(handler);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchGraphSnapshotAsync(null, CancellationToken.None));
    }

    [Test]
    public void FetchGraphSnapshot_ShouldThrow_On500InternalServerError()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "");
        var client = CreateClient(handler);

        Assert.ThrowsAsync<HttpRequestException>(
            () => client.FetchGraphSnapshotAsync(null, CancellationToken.None));
    }

    [Test]
    public async Task FetchGraphSnapshot_ShouldDeserializeUncompressedJson()
    {
        var json = CreateMinimalSnapshotJson(42000);
        var handler = new MockHttpHandler(HttpStatusCode.OK, json, etag: "\"42000\"");
        var client = CreateClient(handler);

        var result = await client.FetchGraphSnapshotAsync(null, CancellationToken.None);

        Assert.That(result.IsNotModified, Is.False);
        Assert.That(result.Snapshot, Is.Not.Null);
        Assert.That(result.Snapshot!.LastProcessedBlock, Is.EqualTo(42000));
        Assert.That(result.Snapshot.SchemaVersion, Is.EqualTo(1));
        Assert.That(result.Etag, Is.EqualTo("\"42000\""));
    }

    [Test]
    public async Task FetchGraphSnapshot_ShouldDecompressGzip()
    {
        var json = CreateMinimalSnapshotJson(99000);
        var compressed = GzipCompress(json);
        var handler = new MockHttpHandler(
            HttpStatusCode.OK, compressed, "gzip", etag: "\"99000\"");
        var client = CreateClient(handler);

        var result = await client.FetchGraphSnapshotAsync(null, CancellationToken.None);

        Assert.That(result.Snapshot, Is.Not.Null);
        Assert.That(result.Snapshot!.LastProcessedBlock, Is.EqualTo(99000));
    }

    [Test]
    public async Task FetchGraphSnapshot_ShouldDecompressBrotli()
    {
        var json = CreateMinimalSnapshotJson(88000);
        var compressed = BrotliCompress(json);
        var handler = new MockHttpHandler(
            HttpStatusCode.OK, compressed, "br", etag: "\"88000\"");
        var client = CreateClient(handler);

        var result = await client.FetchGraphSnapshotAsync(null, CancellationToken.None);

        Assert.That(result.Snapshot, Is.Not.Null);
        Assert.That(result.Snapshot!.LastProcessedBlock, Is.EqualTo(88000));
    }

    [Test]
    public async Task FetchGraphSnapshot_ShouldNotSendIfNoneMatch_WhenEtagNull()
    {
        var json = CreateMinimalSnapshotJson(1000);
        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        await client.FetchGraphSnapshotAsync(null, CancellationToken.None);

        Assert.That(handler.LastRequest!.Headers.IfNoneMatch, Is.Empty);
    }

    [Test]
    public async Task FetchGraphSnapshot_ShouldDeserializeFullSnapshot()
    {
        var dto = new
        {
            schemaVersion = 1,
            lastProcessedBlock = 50000L,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            balances = new[]
            {
                new { balance = "1000000000000000000", account = "0xalice", tokenAddress = "0xtoken", lastActivity = 12345L, isWrapped = false, circlesType = "demurraged" },
                new { balance = "500000000000000000", account = "0xbob", tokenAddress = "0xwrapper", lastActivity = 0L, isWrapped = true, circlesType = "static" }
            },
            trust = new[]
            {
                new { truster = "0xalice", trustee = "0xbob", limit = 100 }
            },
            groups = new[]
            {
                new { groupAddress = "0xgroup1" }
            },
            groupTrusts = new[]
            {
                new { groupAddress = "0xgroup1", trustedToken = "0xalice" }
            },
            consentedFlow = new[]
            {
                new { avatar = "0xalice", hasConsentedFlow = true }
            }
        };

        var json = JsonSerializer.Serialize(dto);
        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var result = await client.FetchGraphSnapshotAsync(null, CancellationToken.None);

        Assert.That(result.Snapshot!.Balances, Has.Count.EqualTo(2));
        Assert.That(result.Snapshot.Trust, Has.Count.EqualTo(1));
        Assert.That(result.Snapshot.Groups, Has.Count.EqualTo(1));
        Assert.That(result.Snapshot.GroupTrusts, Has.Count.EqualTo(1));
        Assert.That(result.Snapshot.ConsentedFlow, Has.Count.EqualTo(1));
        Assert.That(result.Snapshot.ConsentedFlow![0].HasConsentedFlow, Is.True);
    }

    // ── CacheGraphFetchResult Tests ────────────────────────────────────

    [Test]
    public void CacheGraphFetchResult_NotModified_ShouldHaveCorrectState()
    {
        var result = CacheGraphFetchResult.NotModified();

        Assert.That(result.IsNotModified, Is.True);
        Assert.That(result.Snapshot, Is.Null);
        Assert.That(result.Etag, Is.Null);
        Assert.That(result.PayloadBytes, Is.EqualTo(0));
    }

    [Test]
    public void CacheGraphFetchResult_Success_ShouldHaveCorrectState()
    {
        var snapshot = new PathfinderGraphSnapshot(1, 5000, 0, null, null, null, null, null);
        var result = CacheGraphFetchResult.Success(snapshot, "\"5000\"", 1024);

        Assert.That(result.IsNotModified, Is.False);
        Assert.That(result.Snapshot, Is.SameAs(snapshot));
        Assert.That(result.Etag, Is.EqualTo("\"5000\""));
        Assert.That(result.PayloadBytes, Is.EqualTo(1024));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static CacheGraphClient CreateClient(MockHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new CacheGraphClient(httpClient, BaseUrl, TimeSpan.FromSeconds(30));
    }

    private static string CreateMinimalSnapshotJson(long lastBlock)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            lastProcessedBlock = lastBlock,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    private static byte[] GzipCompress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(bytes);
        return ms.ToArray();
    }

    private static byte[] BrotliCompress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(bytes);
        return ms.ToArray();
    }

    /// <summary>
    /// Simple HttpMessageHandler mock for testing CacheGraphClient without real network calls.
    /// </summary>
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly byte[] _content;
        private readonly string? _contentEncoding;
        private readonly string? _etag;

        public HttpRequestMessage? LastRequest { get; private set; }

        public MockHttpHandler(HttpStatusCode statusCode, string textContent, string? etag = null)
        {
            _statusCode = statusCode;
            _content = Encoding.UTF8.GetBytes(textContent);
            _contentEncoding = null;
            _etag = etag;
        }

        public MockHttpHandler(HttpStatusCode statusCode, byte[] compressedContent, string contentEncoding, string? etag = null)
        {
            _statusCode = statusCode;
            _content = compressedContent;
            _contentEncoding = contentEncoding;
            _etag = etag;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            var response = new HttpResponseMessage(_statusCode);

            if (_statusCode != HttpStatusCode.NotModified && _content.Length > 0)
            {
                response.Content = new ByteArrayContent(_content);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                response.Content.Headers.ContentLength = _content.Length;

                if (_contentEncoding != null)
                    response.Content.Headers.ContentEncoding.Add(_contentEncoding);
            }

            if (_etag != null)
                response.Headers.ETag = new EntityTagHeaderValue(_etag);

            return Task.FromResult(response);
        }
    }
}
