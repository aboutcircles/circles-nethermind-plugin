using System.Globalization;
using System.Numerics;

namespace Circles.Pathfinder.Data;

/// <summary>
/// ILoadGraph implementation backed by a deserialized Cache Service snapshot.
/// The cache service provides already-demurraged balances; this class applies
/// the safety margin (matching the DB path's DemurrageCalculator.Apply behavior).
/// </summary>
public sealed class CacheLoadGraph : ILoadGraph
{
    private PathfinderGraphSnapshot _snapshot;
    private readonly Settings? _settings;

    public CacheLoadGraph(PathfinderGraphSnapshot snapshot, Settings? settings = null)
    {
        _snapshot = snapshot;
        _settings = settings;
    }

    public void ReplaceSnapshot(PathfinderGraphSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public long LastProcessedBlock => _snapshot.LastProcessedBlock;

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        var rows = _snapshot.Balances ?? [];

        // Apply safety margin to match DB path behavior (DemurrageCalculator.Apply).
        // The cache service already applies demurrage, but not the safety margin.
        var applyMargin = _settings != null
                          && _settings.TargetDemurrageTimestamp == null
                          && _settings.DemurrageSafetyMargin < 1.0;
        var safetyMargin = _settings?.DemurrageSafetyMargin ?? 1.0;

        foreach (var row in rows)
        {
            var balance = row.Balance;

            if (applyMargin)
            {
                var balanceValue = BigInteger.Parse(balance);
                if (balanceValue != BigInteger.Zero)
                {
                    balanceValue = (BigInteger)((double)balanceValue * safetyMargin);
                    if (balanceValue == BigInteger.Zero) continue;
                    balance = balanceValue.ToString(CultureInfo.InvariantCulture);
                }
            }

            yield return (
                balance,
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

    public IEnumerable<string> LoadRegisteredAvatars()
    {
        var rows = _snapshot.Avatars ?? [];
        foreach (var row in rows)
            yield return row.ToLowerInvariant();
    }

    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings()
    {
        var rows = _snapshot.WrapperMappings ?? [];
        foreach (var row in rows)
            yield return (row.WrapperAddress.ToLowerInvariant(), row.UnderlyingAvatar.ToLowerInvariant());
    }
}
