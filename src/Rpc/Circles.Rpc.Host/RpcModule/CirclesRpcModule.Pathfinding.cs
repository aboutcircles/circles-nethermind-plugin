using System.Text;
using System.Text.Json;
using Circles.Common.Dto;

namespace Circles.Rpc.Host;

/// <summary>
/// Pathfinding methods for CirclesRpcModule.
/// </summary>
public partial class CirclesRpcModule
{
    public async Task<JsonElement> GetNetworkSnapshot()
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            throw new InvalidOperationException("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/snapshot";

        // Build request with conditional ETag header if we have a cached version
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Note: X-Max-Block-Number is NOT forwarded here because the pathfinder's /snapshot
        // endpoint always returns the live graph. Historical snapshots are not supported.

        string? cachedETag;
        JsonElement? cached;
        lock (_snapshotLock)
        {
            cachedETag = _snapshotETag;
            cached = _cachedSnapshot;
        }

        if (!string.IsNullOrEmpty(cachedETag))
        {
            request.Headers.IfNoneMatch.ParseAdd(cachedETag);
        }

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // If 304 Not Modified, return cached version
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached.HasValue)
        {
            _logger?.LogDebug("Network snapshot returned from cache (ETag: {ETag})", cachedETag);
            return cached.Value;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        // Parse to JsonDocument and clone the root element to detach from the document
        using var doc = await JsonDocument.ParseAsync(stream);
        var snapshot = doc.RootElement.Clone();

        // Cache the response with its ETag
        var newETag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrEmpty(newETag))
        {
            lock (_snapshotLock)
            {
                _snapshotETag = newETag;
                _cachedSnapshot = snapshot;
            }
            _logger?.LogDebug("Network snapshot cached (ETag: {ETag})", newETag);
        }

        return snapshot;
    }

    public async Task<JsonElement> FindPathV2(FlowRequest flowRequest)
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            throw new InvalidOperationException("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/findPath";

        var jsonContent = JsonSerializer.Serialize(flowRequest, SharedJsonOptions.CamelCase);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        // Forward X-Max-Block-Number header to pathfinder for historical queries
        var maxBlockNumber = GetMaxBlockNumberFromHeader();
        if (maxBlockNumber.HasValue)
        {
            request.Headers.Add(MaxBlockNumberHeader, maxBlockNumber.Value.ToString());
        }

        using var response = await HttpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger?.LogError("Pathfinder returned {StatusCode}: {ErrorBody}", response.StatusCode, errorContent);

            if ((int)response.StatusCode == 400)
                throw new ArgumentException(errorContent);

            throw new InvalidOperationException("Pathfinder service error");
        }

        var responseString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(responseString);
    }
}
