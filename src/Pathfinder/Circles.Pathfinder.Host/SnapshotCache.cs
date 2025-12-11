using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Host;

/// <summary>
/// Caches the serialized network snapshot to avoid repeated JSON serialization.
/// Invalidates when the block number changes.
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

        lock (_lock)
        {
            // Return cached version if block number hasn't changed
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

            // Serialize to JSON bytes
            _cachedJson = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            _cachedBlockNumber = currentBlock;
            _etag = $"\"{currentBlock}\"";

            return (_cachedJson, _etag);
        }
    }

    /// <summary>
    /// Gets the current ETag without rebuilding the cache.
    /// </summary>
    public string? CurrentETag
    {
        get
        {
            lock (_lock)
            {
                return _etag;
            }
        }
    }

    /// <summary>
    /// Gets the cached block number.
    /// </summary>
    public long CachedBlockNumber
    {
        get
        {
            lock (_lock)
            {
                return _cachedBlockNumber;
            }
        }
    }
}
