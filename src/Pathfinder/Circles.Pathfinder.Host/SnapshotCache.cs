using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Host;

/// <summary>
/// Caches the serialized network snapshot to avoid repeated JSON serialization.
/// Invalidates when the block number changes.
/// Uses a lock-free fast path for cache hits (~99% of calls).
/// </summary>
public sealed class SnapshotCache
{
    private readonly object _lock = new();
    private long _cachedBlockNumber = -1;
    private byte[]? _cachedJson;
    private string? _etag;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Gets the cached snapshot JSON, rebuilding if necessary.
    /// Returns null if the graphs are not ready.
    /// </summary>
    public (byte[]? Json, string? ETag) GetOrBuildSnapshot(NetworkState state)
    {
        var graphsReady = state.BalanceGraph is not null &&
                          state.AccountTrusts.Count > 0;

        if (!graphsReady)
        {
            return (null, null);
        }

        var currentBlock = state.LastKnownBlockNumber;

        // Lock-free fast path: volatile reads are safe because writes happen
        // inside the lock below, and we only need a consistent pair (json + block).
        // Worst case on stale read: we fall through to the locked rebuild.
        var cachedJson = Volatile.Read(ref _cachedJson);
        var cachedBlock = Volatile.Read(ref _cachedBlockNumber);
        if (cachedJson != null && cachedBlock == currentBlock)
        {
            return (cachedJson, Volatile.Read(ref _etag));
        }

        lock (_lock)
        {
            // Double-check after acquiring lock
            if (_cachedJson != null && _cachedBlockNumber == currentBlock)
            {
                return (_cachedJson, _etag);
            }

            // Rebuild the snapshot
            var balancesByHolder = new Dictionary<int, List<BalanceNode>>(state.BalanceGraph!.BalanceNodes.Count);
            foreach (var node in state.BalanceGraph.BalanceNodes.Values)
            {
                if (!balancesByHolder.TryGetValue(node.Holder, out var list))
                {
                    list = new List<BalanceNode>();
                    balancesByHolder.Add(node.Holder, list);
                }
                list.Add(node);
            }

            var snapshot = new NetworkSnapshot
            {
                BlockNumber = currentBlock,
                Addresses = AddressIdPool.GetAvatarSnapshot(),
                Trust = state.AccountTrusts.ToDictionary(kvp => kvp.Key, kvp => new HashSet<int>(kvp.Value)),
                Balance = balancesByHolder
            };

            // Serialize to JSON bytes — write order: json → block → etag
            // ensures readers see _cachedJson before _cachedBlockNumber,
            // and _etag (the public signal) is written last
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            var etag = $"\"{currentBlock}\"";

            Volatile.Write(ref _cachedJson, json);
            Volatile.Write(ref _cachedBlockNumber, currentBlock);
            Volatile.Write(ref _etag, etag); // last: etag is the public signal

            return (json, etag);
        }
    }

    /// <summary>
    /// Gets the current ETag without rebuilding the cache.
    /// </summary>
    public string? CurrentETag => Volatile.Read(ref _etag);

    /// <summary>
    /// Gets the cached block number.
    /// </summary>
    public long CachedBlockNumber => Volatile.Read(ref _cachedBlockNumber);
}
