namespace Circles.Pathfinder.Data;

/// <summary>
/// ILoadGraph implementation backed by a deserialized Cache Service snapshot.
/// No DB connection, no demurrage calculation — the cache service provides
/// raw balances that LoadGraph/IncrementalLoadGraph would normally compute.
/// </summary>
public sealed class CacheLoadGraph : ILoadGraph
{
    private PathfinderGraphSnapshot _snapshot;

    public CacheLoadGraph(PathfinderGraphSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public void ReplaceSnapshot(PathfinderGraphSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public long LastProcessedBlock => _snapshot.LastProcessedBlock;

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        var rows = _snapshot.Balances ?? [];
        foreach (var row in rows)
        {
            yield return (
                row.Balance,
                AddressIdPool.IdOf(row.Account.ToLowerInvariant()),
                AddressIdPool.IdOf(row.TokenAddress.ToLowerInvariant()),
                row.IsWrapped,
                string.Equals(row.CirclesType, "static", StringComparison.OrdinalIgnoreCase));
        }
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        var rows = _snapshot.Trust ?? [];
        foreach (var row in rows)
            yield return (row.Truster.ToLowerInvariant(), row.Trustee.ToLowerInvariant(), row.Limit);
    }

    public IEnumerable<string> LoadGroups()
    {
        var rows = _snapshot.Groups ?? [];
        foreach (var row in rows)
            yield return row.GroupAddress.ToLowerInvariant();
    }

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        var rows = _snapshot.GroupTrusts ?? [];
        foreach (var row in rows)
            yield return (row.GroupAddress.ToLowerInvariant(), row.TrustedToken.ToLowerInvariant());
    }

    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
    {
        var rows = _snapshot.ConsentedFlow ?? [];
        foreach (var row in rows)
            yield return (row.Avatar.ToLowerInvariant(), row.HasConsentedFlow);
    }
}
