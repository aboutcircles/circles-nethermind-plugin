using System.Text.Json;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Tests.Scenarios;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Converts MockLoadGraph / FixtureLoadGraph data into a PathfinderGraphSnapshot,
/// enabling direct comparison of DB-equivalent and cache-equivalent paths.
/// </summary>
public static class SnapshotBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Build a snapshot from MockLoadGraph data (already has int IDs for Account/Token).
    /// </summary>
    public static PathfinderGraphSnapshot FromMockLoadGraph(MockLoadGraph mock, long lastBlock = 1)
    {
        var balances = mock.LoadV2Balances().Select(b => new PathfinderGraphBalanceRow(
            Balance: b.Balance,
            Account: AddressIdPool.StringOf(b.Account),
            TokenAddress: AddressIdPool.StringOf(b.TokenAddress),
            LastActivity: 0,
            IsWrapped: b.IsWrapped,
            DemurrageMode: b.IsStatic ? "static" : "demurraged"
        )).ToList();

        var trust = mock.LoadV2Trust().Select(t => new PathfinderGraphTrustRow(
            Truster: t.Truster,
            Trustee: t.Trustee,
            Limit: t.Limit
        )).ToList();

        var groups = mock.LoadGroups().Select(g => new PathfinderGraphGroupRow(g)).ToList();

        var groupTrusts = mock.LoadGroupTrusts().Select(gt => new PathfinderGraphGroupTrustRow(
            GroupAddress: gt.GroupAddress,
            TrustedToken: gt.TrustedToken
        )).ToList();

        var consent = mock.LoadConsentedFlowFlags().Select(c => new PathfinderGraphConsentedFlowRow(
            Avatar: c.Avatar,
            HasConsentedFlow: c.HasConsentedFlow
        )).ToList();

        var avatars = mock.LoadRegisteredAvatars().ToList();

        var wrappers = mock.LoadWrapperMappings().Select(w => new PathfinderGraphWrapperMappingRow(
            WrapperAddress: w.WrapperAddress,
            UnderlyingAvatar: w.UnderlyingAvatar,
            CirclesType: w.CirclesType
        )).ToList();

        return new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: lastBlock,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: balances,
            Trust: trust,
            Groups: groups,
            GroupTrusts: groupTrusts,
            ConsentedFlow: consent,
            Avatars: avatars,
            WrapperMappings: wrappers
        );
    }

    /// <summary>
    /// Build a snapshot from FixtureSubgraph data (used for scenario-based flow tests).
    /// Mirrors what the cache service would return for the same data.
    /// </summary>
    public static PathfinderGraphSnapshot FromFixtureSubgraph(FixtureSubgraph subgraph, long lastBlock = 1)
    {
        var balances = (subgraph.Balances ?? []).Select(b => new PathfinderGraphBalanceRow(
            Balance: b.Amount,
            Account: b.Holder.ToLowerInvariant(),
            TokenAddress: b.Token.ToLowerInvariant(),
            LastActivity: 0,
            IsWrapped: b.IsWrapped,
            DemurrageMode: b.IsStatic ? "static" : "demurraged"
        )).ToList();

        var trust = (subgraph.Trust ?? [])
            .Where(t => t.Length >= 2)
            .Select(t => new PathfinderGraphTrustRow(
                Truster: t[0].ToLowerInvariant(),
                Trustee: t[1].ToLowerInvariant(),
                Limit: 100
            )).ToList();

        var groups = (subgraph.Groups ?? [])
            .Select(g => new PathfinderGraphGroupRow(g.ToLowerInvariant())).ToList();

        var groupTrusts = (subgraph.GroupTrusts ?? [])
            .Where(gt => gt.Length >= 2)
            .Select(gt => new PathfinderGraphGroupTrustRow(
                GroupAddress: gt[0].ToLowerInvariant(),
                TrustedToken: gt[1].ToLowerInvariant()
            )).ToList();

        var consent = (subgraph.ConsentedAvatars ?? [])
            .Select(a => new PathfinderGraphConsentedFlowRow(
                Avatar: a.ToLowerInvariant(),
                HasConsentedFlow: true
            )).ToList();

        // Derive all unique addresses as registered avatars (same logic as FixtureLoadGraph)
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in subgraph.Balances ?? [])
        {
            addresses.Add(b.Holder.ToLowerInvariant());
            addresses.Add(b.Token.ToLowerInvariant());
        }
        foreach (var t in subgraph.Trust ?? [])
        {
            if (t.Length >= 2)
            {
                addresses.Add(t[0].ToLowerInvariant());
                addresses.Add(t[1].ToLowerInvariant());
            }
        }
        foreach (var g in subgraph.Groups ?? [])
            addresses.Add(g.ToLowerInvariant());
        foreach (var a in subgraph.ConsentedAvatars ?? [])
            addresses.Add(a.ToLowerInvariant());

        return new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: lastBlock,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: balances,
            Trust: trust,
            Groups: groups,
            GroupTrusts: groupTrusts,
            ConsentedFlow: consent,
            Avatars: addresses.ToList(),
            WrapperMappings: []
        );
    }

    /// <summary>
    /// Parse a TransferScenario's Subgraph (object? / JsonElement) into FixtureSubgraph.
    /// Returns null if the scenario has no subgraph.
    /// </summary>
    public static FixtureSubgraph? ParseSubgraph(TransferScenario scenario)
    {
        if (scenario.Subgraph is JsonElement je)
            return je.Deserialize<FixtureSubgraph>(JsonOptions);
        return null;
    }
}
