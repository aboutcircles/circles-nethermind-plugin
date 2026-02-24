namespace Circles.Pathfinder.Data;

/// <summary>
/// In-memory accumulator for trust events.
/// Stores the latest trust event per (truster, trustee) pair.
/// Expiry filtering is done at read time, not at storage time.
/// </summary>
public class InMemoryTrustState
{
    private readonly Dictionary<(string Truster, string Trustee), (long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex)> _state = new();

    public int Count => _state.Count;

    public IEnumerable<KeyValuePair<(string Truster, string Trustee), (long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex)>> GetAll()
        => _state;

    public IEnumerable<(string Truster, string Trustee)> Keys => _state.Keys;

    public bool TryGet((string Truster, string Trustee) key,
        out (long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex) entry)
        => _state.TryGetValue(key, out entry);

    public void InitializeFromFullLoad(
        IEnumerable<(string Truster, string Trustee, long ExpiryTime, long BlockNumber, int TxIndex, int LogIndex)> rows)
    {
        _state.Clear();
        foreach (var row in rows)
        {
            var key = (row.Truster.ToLowerInvariant(), row.Trustee.ToLowerInvariant());
            _state[key] = (row.ExpiryTime, row.BlockNumber, row.TxIndex, row.LogIndex);
        }
    }

    /// <summary>
    /// Upsert a trust event. Only replaces if the new event is strictly later
    /// (blockNumber, then txIndex, then logIndex).
    /// </summary>
    public void ApplyTrustEvent(long blockNumber, int txIndex, int logIndex,
        string truster, string trustee, long expiryTime)
    {
        var key = (truster.ToLowerInvariant(), trustee.ToLowerInvariant());
        if (_state.TryGetValue(key, out var existing))
        {
            if (blockNumber > existing.BlockNumber
                || (blockNumber == existing.BlockNumber && txIndex > existing.TxIndex)
                || (blockNumber == existing.BlockNumber && txIndex == existing.TxIndex && logIndex > existing.LogIndex))
            {
                _state[key] = (expiryTime, blockNumber, txIndex, logIndex);
            }
            // else: older event, ignore
        }
        else
        {
            _state[key] = (expiryTime, blockNumber, txIndex, logIndex);
        }
    }

    /// <summary>
    /// Get active non-group trust edges (replicates trustQuery.sql logic).
    /// Filters: expiry > maxBlockTimestamp, both parties are avatars, truster is not a group.
    /// </summary>
    public IEnumerable<(string Truster, string Trustee, int Limit)> GetActiveTrusts(
        long maxBlockTimestamp, HashSet<string> avatarSet, HashSet<string> groupSet)
    {
        foreach (var kv in _state)
        {
            var (truster, trustee) = kv.Key;
            var expiryTime = kv.Value.ExpiryTime;

            // Filter expired trusts
            if (expiryTime <= maxBlockTimestamp) continue;

            // Both must be registered avatars
            if (!avatarSet.Contains(truster) || !avatarSet.Contains(trustee)) continue;

            // Truster must NOT be a group (replicates LEFT JOIN + IS NULL)
            if (groupSet.Contains(truster)) continue;

            yield return (truster, trustee, 100);
        }
    }

    /// <summary>
    /// Get group trust edges (replicates groupTrustQuery.sql logic).
    /// Returns trusts where the truster IS a group and trust hasn't expired.
    /// </summary>
    public IEnumerable<(string GroupAddress, string TrustedToken)> GetGroupTrusts(
        HashSet<string> groupSet, long maxBlockTimestamp)
    {
        foreach (var kv in _state)
        {
            var (truster, trustee) = kv.Key;
            var expiryTime = kv.Value.ExpiryTime;

            // Truster must be a group
            if (!groupSet.Contains(truster)) continue;

            // Filter expired
            if (expiryTime <= maxBlockTimestamp) continue;

            yield return (truster, trustee);
        }
    }

    /// <summary>
    /// Create a shallow copy for drift comparison.
    /// </summary>
    public InMemoryTrustState Snapshot()
    {
        var copy = new InMemoryTrustState();
        foreach (var kv in _state)
            copy._state[kv.Key] = kv.Value;
        return copy;
    }
}
