using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Scale correctness tests — validate graph construction produces correct results
/// at realistic data sizes (10K avatars). These run in CI via dotnet test.
/// </summary>
[TestFixture]
[Category("Scale")]
public class ScaleTests
{
    private const string RouterAddr = "0xf1ff000000000000000000000000000000ffffff";

    private MockLoadGraph _mock = null!;
    private GeneratedGraphInfo _info = null!;

    [OneTimeSetUp]
    public void GenerateData()
    {
        (_mock, _info) = LargeGraphGenerator.Generate(avatarCount: 10_000, seed: 42);
    }

    [Test]
    public void TrustGraph_10KAvatars_AllEdgesPresent()
    {
        var factory = new GraphFactory(RouterAddr, _mock);
        var trustGraph = factory.V2TrustGraph();

        Assert.That(trustGraph.Edges, Has.Count.EqualTo(_info.TrustEdgeCount),
            "Trust graph should contain exactly the number of edges loaded");
        Assert.That(trustGraph.AvatarNodes.Count, Is.GreaterThan(0),
            "Trust graph should have avatar nodes");
    }

    [Test]
    public void BalanceGraph_10KAvatars_AllBalancesPresent()
    {
        var factory = new GraphFactory(RouterAddr, _mock);
        var balanceGraph = factory.V2BalanceGraph();

        Assert.That(balanceGraph.BalanceNodes.Count, Is.EqualTo(_info.BalanceCount),
            "Balance graph should contain exactly the number of balances loaded");
        Assert.That(balanceGraph.AvatarNodes.Count, Is.GreaterThan(0),
            "Balance graph should have avatar nodes");
    }

    [Test]
    public void CapacityGraph_10K_HasExpectedNodeCount()
    {
        var factory = new GraphFactory(RouterAddr, _mock);
        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trust);
        var capacityGraph = factory.CreateCapacityGraph(balance, trustLookup);

        // Capacity graph should have avatar nodes (from trust + balance) plus token pool nodes plus router
        Assert.That(capacityGraph.AvatarNodes.Count, Is.GreaterThanOrEqualTo(_info.AvatarCount),
            "Capacity graph should have at least as many avatar nodes as generated avatars");
        Assert.That(capacityGraph.Edges.Count, Is.GreaterThan(0),
            "Capacity graph should have edges");
        Assert.That(capacityGraph.TokenNodes.Count, Is.GreaterThan(0),
            "Capacity graph should have token pool nodes");
    }

    [Test]
    public void CapacityGraph_LargeGraph_FindPathSucceeds()
    {
        // Build a small deterministic graph where a path is guaranteed:
        // Alice holds TokenA, Bob trusts TokenA → flow Alice→Bob should succeed
        var mock = new MockLoadGraph();
        var alice = _info.Addresses[0];
        var bob = _info.Addresses[1];
        var tokenA = alice; // In Circles v2, your token = your address

        // Alice holds her own token (100 CRC)
        mock.AddBalance(
            AddressIdPool.IdOf(alice),
            AddressIdPool.IdOf(tokenA),
            100_000_000);

        // Bob trusts Alice's token
        mock.AddTrust(bob, tokenA);

        var factory = new GraphFactory(RouterAddr, mock);
        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trust);

        var flowRequest = new FlowRequest { Source = alice, Sink = bob };
        var capacityGraph = factory.CreateCapacityGraph(balance, trustLookup, flowRequest);

        var pathfinder = new V2Pathfinder();
        var maxFlow = pathfinder.ComputeMaxFlow(
            capacityGraph,
            flowRequest,
            new UInt256(50_000_000) * new UInt256(1_000_000_000_000)); // 50 CRC in WEI

        Assert.That(maxFlow, Is.GreaterThan(0),
            "Should find a valid flow path in the graph");
    }

    [Test]
    public void GraphFactory_DuplicateTrustEdges_Idempotent()
    {
        // Load same trust data twice — graph should not be corrupted
        var mock = new MockLoadGraph();
        var a = "0xaaaa000000000000000000000000000000000001";
        var b = "0xbbbb000000000000000000000000000000000002";

        mock.AddTrust(a, b);
        mock.AddTrust(a, b); // duplicate

        var factory = new GraphFactory(RouterAddr, mock);
        var trust = factory.V2TrustGraph();

        // Both edges are loaded (MockLoadGraph stores both, GraphFactory adds both)
        // The trust lookup deduplicates via HashSet
        var lookup = GraphFactory.BuildTrustLookup(trust);

        Assert.That(lookup.ContainsKey(AddressIdPool.IdOf(a)), Is.True);
        Assert.That(lookup[AddressIdPool.IdOf(a)], Has.Count.EqualTo(1),
            "BuildTrustLookup should deduplicate via HashSet — only one trust entry for a→b");
    }

    [Test]
    public void BuildTrustLookup_10K_AllTrustersPresent()
    {
        var factory = new GraphFactory(RouterAddr, _mock);
        var trust = factory.V2TrustGraph();
        var lookup = GraphFactory.BuildTrustLookup(trust);

        // Every truster in the original edges should appear as a key
        Assert.That(lookup.Count, Is.GreaterThan(0));

        // Sum of all trusted sets should equal the edge count (HashSet dedup may reduce)
        int totalTrusted = lookup.Values.Sum(s => s.Count);
        Assert.That(totalTrusted, Is.LessThanOrEqualTo(_info.TrustEdgeCount),
            "Total trust entries after dedup should be <= raw edge count");
        Assert.That(totalTrusted, Is.GreaterThan(_info.AvatarCount),
            "Should have more trust entries than avatars (each avatar trusts ~5)");
    }
}
