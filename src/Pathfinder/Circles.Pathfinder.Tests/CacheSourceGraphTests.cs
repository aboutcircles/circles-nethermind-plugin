using Circles.Pathfinder.Data;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Layer 2: Graph Structure Equivalence — both data sources → GraphFactory → CapacityGraph,
/// compare resulting graph structure (node counts, edge counts, total capacity, flags).
/// </summary>
[TestFixture]
public class CacheSourceGraphTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    [Test]
    public void SameInput_BothSources_SameAvatarNodeCount()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        Assert.That(cacheCap.AvatarNodes.Count, Is.EqualTo(mockCap.AvatarNodes.Count),
            "Avatar node count should match");
    }

    [Test]
    public void SameInput_BothSources_SameGroupNodeCount()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        Assert.That(cacheCap.GroupNodes.Count, Is.EqualTo(mockCap.GroupNodes.Count),
            "Group node count should match");
    }

    [Test]
    public void SameInput_BothSources_SameEdgeCount()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        Assert.That(cacheCap.Edges.Count, Is.EqualTo(mockCap.Edges.Count),
            "Edge count should match");
    }

    [Test]
    public void SameInput_BothSources_SameTotalCapacity()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        // Use BigInteger to avoid long overflow on large capacity sums
        var mockTotal = mockCap.Edges.Aggregate(
            System.Numerics.BigInteger.Zero,
            (sum, e) => sum + e.InitialCapacity);
        var cacheTotal = cacheCap.Edges.Aggregate(
            System.Numerics.BigInteger.Zero,
            (sum, e) => sum + e.InitialCapacity);

        Assert.That(cacheTotal, Is.EqualTo(mockTotal),
            "Total capacity should match");
    }

    [Test]
    public void SameInput_BothSources_SameConsentedAvatarSet()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        Assert.That(cacheCap.ConsentedAvatars.SetEquals(mockCap.ConsentedAvatars), Is.True,
            "Consented avatar sets should match");
    }

    [Test]
    public void SameInput_BothSources_SameWrapperMappingSet()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        Assert.That(cacheCap.WrapperToAvatar.Count, Is.EqualTo(mockCap.WrapperToAvatar.Count));

        foreach (var kv in mockCap.WrapperToAvatar)
        {
            Assert.That(cacheCap.WrapperToAvatar.ContainsKey(kv.Key), Is.True,
                $"Missing wrapper mapping for {kv.Key}");
            Assert.That(cacheCap.WrapperToAvatar[kv.Key], Is.EqualTo(kv.Value),
                $"Wrapper mapping mismatch for {kv.Key}");
        }
    }

    [Test]
    public void WithGroups_BothSources_SameGroupTrustEdges()
    {
        var mock = BuildGraphWithGroups();
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(mock);

        // Group trust edges appear as edges FROM token pool TO group
        var mockGroupEdges = mockCap.Edges
            .Where(e => mockCap.GroupNodes.Contains(e.To))
            .ToHashSet();
        var cacheGroupEdges = cacheCap.Edges
            .Where(e => cacheCap.GroupNodes.Contains(e.To))
            .ToHashSet();

        Assert.That(cacheGroupEdges.Count, Is.EqualTo(mockGroupEdges.Count),
            "Group trust edge count should match");
    }

    [Test]
    public void WithConsent_BothSources_SameConsentFlags()
    {
        var mock = BuildTestGraph();
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(mock);

        Assert.That(cacheCap.ConsentedAvatars, Is.EquivalentTo(mockCap.ConsentedAvatars));
    }

    [Test]
    public void SameInput_BothSources_SameRegisteredAvatarIds()
    {
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(BuildTestGraph());

        Assert.That(cacheCap.RegisteredAvatarIds.Count, Is.EqualTo(mockCap.RegisteredAvatarIds.Count),
            "Registered avatar count should match");
        Assert.That(cacheCap.RegisteredAvatarIds.SetEquals(mockCap.RegisteredAvatarIds), Is.True,
            "Registered avatar sets should match");
    }

    [Test]
    public void EmptyGraph_BothSources_ZeroNodes()
    {
        var mock = new MockLoadGraph();
        var (mockCap, cacheCap) = BuildBothCapacityGraphs(mock);

        Assert.That(cacheCap.AvatarNodes.Count, Is.EqualTo(mockCap.AvatarNodes.Count));
        Assert.That(cacheCap.Edges.Count, Is.EqualTo(mockCap.Edges.Count));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static (CapacityGraph mock, CapacityGraph cache) BuildBothCapacityGraphs(MockLoadGraph mock)
    {
        // Path 1: MockLoadGraph → GraphFactory → CapacityGraph
        var mockFactory = new GraphFactory(RouterAddress, mock);
        var mockTrust = mockFactory.V2TrustGraph();
        var mockBalance = mockFactory.V2BalanceGraph();
        var mockTrustLookup = GraphFactory.BuildTrustLookup(mockTrust);
        var mockCap = mockFactory.CreateBaseCapacityGraph(mockBalance, mockTrustLookup);

        // Path 2: MockLoadGraph → Snapshot → CacheLoadGraph → GraphFactory → CapacityGraph
        var snapshot = SnapshotBuilder.FromMockLoadGraph(mock);
        var cacheLoadGraph = new CacheLoadGraph(snapshot);
        var cacheFactory = new GraphFactory(RouterAddress, cacheLoadGraph);
        var cacheTrust = cacheFactory.V2TrustGraph();
        var cacheBalance = cacheFactory.V2BalanceGraph();
        var cacheTrustLookup = GraphFactory.BuildTrustLookup(cacheTrust);
        var cacheCap = cacheFactory.CreateBaseCapacityGraph(cacheBalance, cacheTrustLookup);

        return (mockCap, cacheCap);
    }

    private static MockLoadGraph BuildTestGraph()
    {
        var mock = new MockLoadGraph();

        var a1 = "0xee00000000000000000000000000000000000001";
        var a2 = "0xee00000000000000000000000000000000000002";
        var a3 = "0xee00000000000000000000000000000000000003";
        var g1 = "0xee00000000000000000000000000000000000010";

        mock.AddRegisteredAvatar(a1);
        mock.AddRegisteredAvatar(a2);
        mock.AddRegisteredAvatar(a3);

        mock.AddBalanceWei(a1, a1, "500000000000000000000");
        mock.AddBalanceWei(a2, a2, "300000000000000000000");
        mock.AddBalanceWei(a3, a3, "200000000000000000000");

        mock.AddTrust(a1, a2);
        mock.AddTrust(a2, a3);
        mock.AddTrust(a3, a1);

        mock.AddGroup(g1);
        mock.AddGroupTrust(g1, a1);
        mock.AddGroupTrust(g1, a2);

        mock.AddConsentedAvatar(a3, true);

        mock.AddWrapperMapping("0xee00000000000000000000000000000000000020", a1);

        return mock;
    }

    private static MockLoadGraph BuildGraphWithGroups()
    {
        var mock = new MockLoadGraph();

        var a1 = "0xef00000000000000000000000000000000000001";
        var a2 = "0xef00000000000000000000000000000000000002";
        var g1 = "0xef00000000000000000000000000000000000010";
        var g2 = "0xef00000000000000000000000000000000000011";

        mock.AddRegisteredAvatar(a1);
        mock.AddRegisteredAvatar(a2);

        mock.AddBalanceWei(a1, a1, "400000000000000000000");
        mock.AddBalanceWei(a2, a2, "600000000000000000000");

        mock.AddTrust(a1, a2);
        mock.AddTrust(a2, a1);

        mock.AddGroup(g1);
        mock.AddGroup(g2);
        mock.AddGroupTrust(g1, a1);
        mock.AddGroupTrust(g1, a2);
        mock.AddGroupTrust(g2, a2);

        return mock;
    }
}
