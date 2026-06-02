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

    // Single-slot cache for the most-recently-requested historical (block-pinned) snapshot.
    // Independent of the live slot above; keyed by the requested block number.
    private readonly object _histLock = new();
    private long _histBlockNumber = -1;
    private byte[]? _histJson;
    private string? _histEtag;

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

            var json = SerializeSnapshot(state.BalanceGraph!, state.AccountTrusts, currentBlock);
            var etag = $"\"{currentBlock}\"";

            // Write order: json → block → etag ensures readers see _cachedJson before
            // _cachedBlockNumber, and _etag (the public signal) is written last.
            Volatile.Write(ref _cachedJson, json);
            Volatile.Write(ref _cachedBlockNumber, currentBlock);
            Volatile.Write(ref _etag, etag); // last: etag is the public signal

            return (json, etag);
        }
    }

    /// <summary>
    /// The ETag for a historical (block-pinned) snapshot. A per-block validator (state at block N
    /// is deterministic), namespaced so it can never alias the live snapshot's ETag (which is just
    /// the block number) — the two responses can differ slightly at the same block (demurrage
    /// anchor). Single source of truth: used by both this cache and the /snapshot endpoint's
    /// If-None-Match short-circuit.
    /// </summary>
    public static string HistoricalETag(long blockNumber) => $"\"historical-{blockNumber}\"";

    /// <summary>
    /// Builds (or returns a cached) snapshot for a historical block, reconstructed from a
    /// block-pinned GraphFactory (loaded via HistoricalGraphCache) rather than the live
    /// NetworkState. Single-slot cache keyed by block: repeated polls of the same historical
    /// block reuse the serialized bytes, so the graph rebuild + multi-MB serialize only runs
    /// when the requested block changes — which also bounds the cost of repeated
    /// /snapshot + X-Max-Block-Number=N requests. The ETag (see HistoricalETag) is per-block,
    /// so a client polling the same block gets 304s.
    /// </summary>
    public (byte[] Json, string ETag) GetOrBuildHistoricalSnapshot(GraphFactory factory, long blockNumber)
    {
        // Lock-free fast path (mirrors the live path): a consistent (json, block) pair; a stale
        // read just falls through to the locked rebuild.
        var cachedJson = Volatile.Read(ref _histJson);
        var cachedBlock = Volatile.Read(ref _histBlockNumber);
        if (cachedJson != null && cachedBlock == blockNumber)
        {
            return (cachedJson, Volatile.Read(ref _histEtag)!);
        }

        lock (_histLock)
        {
            if (_histJson != null && _histBlockNumber == blockNumber)
            {
                return (_histJson, _histEtag!);
            }

            // Build the same graph projections the live updater uses (NetworkStateUpdaterService),
            // but from the block-pinned factory so the snapshot reflects state as of blockNumber.
            var trustGraph = factory.V2TrustGraph();
            var accountTrusts = GraphFactory.BuildTrustLookup(trustGraph);
            var balanceGraph = factory.V2BalanceGraph();

            var json = SerializeSnapshot(balanceGraph, accountTrusts, blockNumber);
            var etag = HistoricalETag(blockNumber);

            Volatile.Write(ref _histJson, json);
            Volatile.Write(ref _histBlockNumber, blockNumber);
            Volatile.Write(ref _histEtag, etag);

            return (json, etag);
        }
    }

    /// <summary>
    /// Shared snapshot construction + serialization used by both the live and historical paths,
    /// so the wire format (NetworkSnapshot shape, JSON options) lives in exactly one place. Each
    /// caller computes its own ETag (live = block number; historical = HistoricalETag).
    /// Addresses is the global AddressIdPool snapshot (a superset id→address lookup; the ids that
    /// appear in Trust/Balance index into it, extra entries are harmless).
    /// </summary>
    private static byte[] SerializeSnapshot(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        long blockNumber)
    {
        var balancesByHolder = new Dictionary<int, List<BalanceNode>>(balanceGraph.BalanceNodes.Count);
        foreach (var node in balanceGraph.BalanceNodes.Values)
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
            BlockNumber = blockNumber,
            Addresses = AddressIdPool.GetAvatarSnapshot(),
            Trust = accountTrusts.ToDictionary(kvp => kvp.Key, kvp => new HashSet<int>(kvp.Value)),
            Balance = balancesByHolder
        };

        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
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
