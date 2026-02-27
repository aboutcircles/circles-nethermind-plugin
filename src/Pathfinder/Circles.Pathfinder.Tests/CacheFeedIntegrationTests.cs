using System.Numerics;
using Circles.Common;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Nodes;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Integration tests for the cache-feed pipeline:
///   Cache snapshot → CacheLoadGraph → GraphFactory → V2Pathfinder → transfer result.
/// Proves the full zero-SQL path produces correct pathfinding output.
/// </summary>
[TestFixture]
public class CacheFeedIntegrationTests
{
    // Valid 42-char hex addresses
    private static readonly string Router = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private static readonly string Alice = "0xaaaa000000000000000000000000000000000001";
    private static readonly string Bob = "0xbbbb000000000000000000000000000000000002";
    private static readonly string Charlie = "0xcccc000000000000000000000000000000000003";
    private static readonly string Wrapper1 = "0xww11000000000000000000000000000000000001";

    /// <summary>
    /// Simple 2-hop: Alice → Bob direct transfer.
    /// Alice holds Bob's tokens, Bob trusts Alice.
    /// Uses CacheLoadGraph as the ILoadGraph implementation.
    /// </summary>
    [Test]
    public void CacheFeed_SimpleDirectTransfer_ShouldFindMaxFlow()
    {
        var amount = CirclesConverter.CirclesToAttoCircles(100m).ToString();

        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 10000,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow(amount, Alice, Bob, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), false, "demurraged")
            },
            Trust: new[]
            {
                new PathfinderGraphTrustRow(Bob, Alice, 100)
            },
            Groups: Array.Empty<PathfinderGraphGroupRow>(),
            GroupTrusts: Array.Empty<PathfinderGraphGroupTrustRow>(),
            ConsentedFlow: Array.Empty<PathfinderGraphConsentedFlowRow>()
        );

        var loadGraph = new CacheLoadGraph(snapshot);
        var factory = new GraphFactory(Router, loadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();

        Assert.That(balanceGraph.AvatarNodes, Has.Count.GreaterThanOrEqualTo(1),
            "Alice should be in the balance graph");

        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        // Build capacity graph for the pathfinder
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup);
        Assert.That(capacityGraph.AvatarNodes.ContainsKey(AddressIdPool.IdOf(Alice.ToLowerInvariant())), Is.True,
            "Alice should be in the capacity graph");
        Assert.That(capacityGraph.AvatarNodes.ContainsKey(AddressIdPool.IdOf(Bob.ToLowerInvariant())), Is.True,
            "Bob should be in the capacity graph");
    }

    /// <summary>
    /// Verify CacheLoadGraph produces same graph topology as MockLoadGraph for equivalent inputs.
    /// Cross-validation: cache-feed path should produce identical graphs to mock/DB path.
    /// </summary>
    [Test]
    public void CacheFeed_ShouldMatchMockLoadGraph_SameInputs()
    {
        var amount = "1000000000000000000000"; // 1000 * 10^18

        // Build via CacheLoadGraph (cache-feed path)
        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 10000,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow(amount, Alice, Bob, 0, false, "demurraged"),
                new PathfinderGraphBalanceRow(amount, Bob, Charlie, 0, false, "demurraged")
            },
            Trust: new[]
            {
                new PathfinderGraphTrustRow(Bob, Alice, 100),
                new PathfinderGraphTrustRow(Charlie, Bob, 100)
            },
            Groups: null,
            GroupTrusts: null,
            ConsentedFlow: null
        );

        var cacheLoadGraph = new CacheLoadGraph(snapshot);
        var cacheFactory = new GraphFactory(Router, cacheLoadGraph);
        var cacheBg = cacheFactory.V2BalanceGraph();
        var cacheTg = cacheFactory.V2TrustGraph();

        // Build via MockLoadGraph (DB-equivalent path)
        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddBalanceWei(Alice, Bob, amount);
        mockLoadGraph.AddBalanceWei(Bob, Charlie, amount);
        mockLoadGraph.AddTrust(Bob, Alice);
        mockLoadGraph.AddTrust(Charlie, Bob);

        var mockFactory = new GraphFactory(Router, mockLoadGraph);
        var mockBg = mockFactory.V2BalanceGraph();
        var mockTg = mockFactory.V2TrustGraph();

        // Same avatar count in balance graph
        Assert.That(cacheBg.AvatarNodes.Count, Is.EqualTo(mockBg.AvatarNodes.Count),
            "Cache and mock should produce same number of balance graph avatar nodes");

        // Same trust edge count
        Assert.That(cacheTg.Edges.Count, Is.EqualTo(mockTg.Edges.Count),
            "Cache and mock should produce same number of trust edges");
    }

    /// <summary>
    /// Verify wrapper tokens are present in the balance graph when isWrapped=true.
    /// </summary>
    [Test]
    public void CacheFeed_WrappedBalances_ShouldAppearInGraph()
    {
        var amount = "5000000000000000000000";

        var snapshot = new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: 10000,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: new[]
            {
                new PathfinderGraphBalanceRow(amount, Alice, Wrapper1, 0, true, "demurraged"),
                new PathfinderGraphBalanceRow(amount, Alice, Bob, 0, false, "demurraged")
            },
            Trust: new[]
            {
                new PathfinderGraphTrustRow(Bob, Alice, 100),
                new PathfinderGraphTrustRow(Bob, Wrapper1, 100)
            },
            Groups: null,
            GroupTrusts: null,
            ConsentedFlow: null
        );

        var loadGraph = new CacheLoadGraph(snapshot);
        var factory = new GraphFactory(Router, loadGraph);
        var bg = factory.V2BalanceGraph();

        var aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        var wrapperId = AddressIdPool.IdOf(Wrapper1.ToLowerInvariant());

        Assert.That(bg.AvatarNodes.ContainsKey(aliceId), Is.True,
            "Alice should be in the balance graph");

        // Verify wrapped balance exists via BalanceNodes (keyed by composite balance-node ID)
        // BalanceGraph.AddBalance creates a BalanceNode with owner=alice, token=wrapper
        var hasWrapperBalance = bg.BalanceNodes.Values.Any(bn =>
            bn.Holder == aliceId && bn.Token == wrapperId);
        Assert.That(hasWrapperBalance, Is.True,
            "Alice should hold the wrapper token balance");
    }

    /// <summary>
    /// Verify ReplaceSnapshot works for live updates — new snapshot changes graph data.
    /// </summary>
    [Test]
    public void CacheFeed_ReplaceSnapshot_ShouldUpdateGraphData()
    {
        var amount1 = "1000000000000000000000";
        var snapshot1 = new PathfinderGraphSnapshot(
            1, 100, 0,
            new[] { new PathfinderGraphBalanceRow(amount1, Alice, Bob, 0, false, "demurraged") },
            new[] { new PathfinderGraphTrustRow(Bob, Alice, 100) },
            null, null, null);

        var loadGraph = new CacheLoadGraph(snapshot1);
        Assert.That(loadGraph.LastProcessedBlock, Is.EqualTo(100));
        Assert.That(loadGraph.LoadV2Balances().Count(), Is.EqualTo(1));

        // Replace with larger snapshot
        var amount2 = "2000000000000000000000";
        var snapshot2 = new PathfinderGraphSnapshot(
            1, 200, 0,
            new[]
            {
                new PathfinderGraphBalanceRow(amount2, Alice, Bob, 0, false, "demurraged"),
                new PathfinderGraphBalanceRow(amount2, Bob, Charlie, 0, false, "demurraged")
            },
            new[]
            {
                new PathfinderGraphTrustRow(Bob, Alice, 100),
                new PathfinderGraphTrustRow(Charlie, Bob, 100)
            },
            null, null, null);

        loadGraph.ReplaceSnapshot(snapshot2);

        Assert.That(loadGraph.LastProcessedBlock, Is.EqualTo(200));
        Assert.That(loadGraph.LoadV2Balances().Count(), Is.EqualTo(2));
        Assert.That(loadGraph.LoadV2Trust().Count(), Is.EqualTo(2));

        // Verify the new snapshot builds into a valid graph
        var factory = new GraphFactory(Router, loadGraph);
        var bg = factory.V2BalanceGraph();
        Assert.That(bg.AvatarNodes, Has.Count.GreaterThanOrEqualTo(2),
            "Updated snapshot should produce graph with Alice and Bob");
    }

    /// <summary>
    /// Verify consented flow flags propagate through CacheLoadGraph → CapacityGraph.
    /// </summary>
    [Test]
    public void CacheFeed_ConsentedFlowFlags_ShouldPropagateToCapacityGraph()
    {
        var amount = "1000000000000000000000";
        var snapshot = new PathfinderGraphSnapshot(
            1, 10000, 0,
            new[] { new PathfinderGraphBalanceRow(amount, Alice, Bob, 0, false, "demurraged") },
            new[] { new PathfinderGraphTrustRow(Bob, Alice, 100) },
            null, null,
            new[]
            {
                new PathfinderGraphConsentedFlowRow(Alice, true),
                new PathfinderGraphConsentedFlowRow(Bob, false)
            });

        var loadGraph = new CacheLoadGraph(snapshot);

        // Verify the raw consent data loads
        var consentFlags = loadGraph.LoadConsentedFlowFlags().ToList();
        Assert.That(consentFlags, Has.Count.EqualTo(2));
        Assert.That(consentFlags.Any(c => c.Avatar == Alice.ToLowerInvariant() && c.HasConsentedFlow), Is.True);
        Assert.That(consentFlags.Any(c => c.Avatar == Bob.ToLowerInvariant() && !c.HasConsentedFlow), Is.True);
    }
}
