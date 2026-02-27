using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Circles.Pathfinder.Data;

/// <summary>
/// HTTP client for fetching graph snapshots from the Cache Service's /api/pathfinder/graph endpoint.
/// Supports ETag-based conditional requests and compressed responses.
/// </summary>
public sealed class CacheGraphClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CacheGraphClient(HttpClient httpClient, string baseUrl, TimeSpan timeout)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = timeout;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<CacheGraphFetchResult> FetchGraphSnapshotAsync(string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/pathfinder/graph");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return CacheGraphFetchResult.NotModified();

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new InvalidOperationException("Cache graph endpoint is not ready (503).");

        response.EnsureSuccessStatusCode();

        var newEtag = response.Headers.ETag?.Tag;
        var payloadBytes = response.Content.Headers.ContentLength ?? 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var decodedStream = await DecodeContentStream(response.Content.Headers.ContentEncoding, contentStream);

        var dto = await JsonSerializer.DeserializeAsync<PathfinderGraphSnapshotDto>(decodedStream, JsonOptions, ct)
                  ?? throw new InvalidOperationException("Failed to deserialize cache graph snapshot payload.");

        var snapshot = dto.ToModel();
        return CacheGraphFetchResult.Success(snapshot, newEtag, payloadBytes);
    }

    private static async Task<Stream> DecodeContentStream(ICollection<string> encodings, Stream raw)
    {
        if (encodings.Any(x => x.Equals("br", StringComparison.OrdinalIgnoreCase)))
        {
            var ms = new MemoryStream();
            await using var brotli = new BrotliStream(raw, CompressionMode.Decompress, leaveOpen: true);
            await brotli.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        if (encodings.Any(x => x.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
        {
            var ms = new MemoryStream();
            await using var gzip = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true);
            await gzip.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        var passthrough = new MemoryStream();
        await raw.CopyToAsync(passthrough);
        passthrough.Position = 0;
        return passthrough;
    }
}

public sealed record CacheGraphFetchResult
{
    public bool IsNotModified { get; init; }
    public PathfinderGraphSnapshot? Snapshot { get; init; }
    public string? Etag { get; init; }
    public long PayloadBytes { get; init; }

    public static CacheGraphFetchResult NotModified() => new()
    {
        IsNotModified = true
    };

    public static CacheGraphFetchResult Success(PathfinderGraphSnapshot snapshot, string? etag, long payloadBytes) => new()
    {
        IsNotModified = false,
        Snapshot = snapshot,
        Etag = etag,
        PayloadBytes = payloadBytes
    };
}
