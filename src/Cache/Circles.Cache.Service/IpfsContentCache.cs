using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Circles.Cache.Service.Caches;

/// <summary>
/// LRU cache for IPFS profile content.
/// Unlike RollbackCache, IPFS content is immutable (content-addressed),
/// so we use a simple MemoryCache with size-based eviction.
/// </summary>
public sealed class IpfsContentCache
{
    private readonly MemoryCache _cache;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<IpfsContentCache> _logger;
    private readonly int _maxEntries;

    // Statistics
    private long _hits;
    private long _misses;

    public IpfsContentCache(NpgsqlDataSource dataSource, int maxEntries, ILogger<IpfsContentCache> logger)
    {
        _dataSource = dataSource;
        _maxEntries = maxEntries;
        _logger = logger;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = maxEntries
        });
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public int Count => _cache.Count;

    /// <summary>
    /// Gets profile content by CID. Returns the raw JSON string from ipfs_files table.
    /// </summary>
    public async Task<string?> GetAsync(string cid)
    {
        if (string.IsNullOrWhiteSpace(cid))
            return null;

        if (_cache.TryGetValue(cid, out string? cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _misses);

        // Fetch from database
        var content = await FetchFromDatabaseAsync(cid);
        if (content != null)
        {
            _cache.Set(cid, content, new MemoryCacheEntryOptions { Size = 1 });
        }

        return content;
    }

    /// <summary>
    /// Gets profile content for multiple CIDs in batch.
    /// Returns results in the same order as input CIDs.
    /// </summary>
    public async Task<string?[]> GetBatchAsync(string[] cids)
    {
        if (cids == null || cids.Length == 0)
            return Array.Empty<string?>();

        var results = new string?[cids.Length];
        var missingIndexes = new List<int>();
        var missingCids = new List<string>();

        // Check cache first
        for (int i = 0; i < cids.Length; i++)
        {
            var cid = cids[i];
            if (string.IsNullOrWhiteSpace(cid))
            {
                results[i] = null;
                continue;
            }

            if (_cache.TryGetValue(cid, out string? cached))
            {
                Interlocked.Increment(ref _hits);
                results[i] = cached;
            }
            else
            {
                missingIndexes.Add(i);
                missingCids.Add(cid);
            }
        }

        if (missingCids.Count == 0)
            return results;

        Interlocked.Add(ref _misses, missingCids.Count);

        // Fetch missing from database
        var fetched = await FetchBatchFromDatabaseAsync(missingCids.ToArray());

        // Map results back and populate cache
        for (int i = 0; i < missingCids.Count; i++)
        {
            var targetIndex = missingIndexes[i];
            var content = fetched[i];
            results[targetIndex] = content;

            if (content != null)
            {
                _cache.Set(missingCids[i], content, new MemoryCacheEntryOptions { Size = 1 });
            }
        }

        return results;
    }

    private async Task<string?> FetchFromDatabaseAsync(string cid)
    {
        const string sql = "SELECT payload FROM ipfs_files WHERE cid = @cid";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("cid", cid);

        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    private async Task<string?[]> FetchBatchFromDatabaseAsync(string[] cids)
    {
        var results = new string?[cids.Length];

        const string sql = @"
            SELECT f.cid, f.payload
            FROM unnest(@cids) WITH ORDINALITY as u(_cid, _index)
            LEFT JOIN ipfs_files f ON f.cid = u._cid
            ORDER BY u._index";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("cids", cids);

        await using var reader = await cmd.ExecuteReaderAsync();
        int index = 0;
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                results[index] = reader.GetString(1);
            }
            index++;
        }

        return results;
    }

    /// <summary>
    /// Strips JSON-LD fields from profile content for cleaner responses.
    /// </summary>
    public static string? StripJsonLdFields(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        try
        {
            var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return content;

            var cleaned = new Dictionary<string, JsonElement>();
            foreach (var prop in root.EnumerateObject())
            {
                // Skip JSON-LD fields
                if (prop.Name == "@context" || prop.Name == "namespaces" || prop.Name == "signingKeys")
                    continue;
                cleaned[prop.Name] = prop.Value;
            }

            return JsonSerializer.Serialize(cleaned);
        }
        catch
        {
            return content;
        }
    }
}
