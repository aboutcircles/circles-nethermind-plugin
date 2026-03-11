using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Phase 3d regression tests: Pre-built wrapped graph snapshot.
/// Guards the optimization that withWrap=true requests use a cached snapshot
/// instead of rebuilding the entire graph from scratch.
/// </summary>
[TestFixture, Parallelizable]
public class WrappedSnapshotRegressionTests
{
    private const string RouterAddr = "0xf000000000000000000000000000000000000001";
    private const string AliceAddr = "0xf100000000000000000000000000000000000001";
    private const string BobAddr = "0xf200000000000000000000000000000000000002";

    private static CapacityGraphPool CreatePool()
    {
        var mockLoadGraph = new MockLoadGraph();
        return new CapacityGraphPool(RouterAddr, mockLoadGraph);
    }

    private static CapacityGraph BuildMinimalGraph()
    {
        var graph = new CapacityGraph();
        int alice = AddressIdPool.IdOf(AliceAddr);
        int bob = AddressIdPool.IdOf(BobAddr);
        int router = AddressIdPool.IdOf(RouterAddr);
        graph.AddAvatar(alice);
        graph.AddAvatar(bob);
        graph.SetRouter(router);
        graph.AddTokenNode(alice);
        int pool = AddressIdPool.TokenPoolIdOf(alice);
        graph.AddCapacityEdge(alice, pool, alice, 100_000_000L);
        graph.AddCapacityEdge(pool, bob, alice, long.MaxValue);
        return graph;
    }

    private static (BalanceGraph balances, IReadOnlyDictionary<int, HashSet<int>> trust) BuildMinimalInputs()
    {
        var balances = new BalanceGraph();
        int alice = AddressIdPool.IdOf(AliceAddr);
        int bob = AddressIdPool.IdOf(BobAddr);
        balances.AddAvatar(alice);
        balances.AddAvatar(bob);
        balances.AddBalance(alice, alice, 100_000_000L, isWrapped: false, isStatic: false);
        var trust = new Dictionary<int, HashSet<int>>
        {
            [bob] = new HashSet<int> { alice }
        };
        return (balances, trust);
    }

    private static CachedGroupData BuildEmptyGroupData()
    {
        return new CachedGroupData(
            GroupNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>(),
            ConsentedAvatars: new HashSet<int>(),
            RegisteredAvatarIds: new HashSet<int>(),
            WrapperToAvatar: new Dictionary<int, int>());
    }

    // ----------------------------------------------------------------
    // IsWrapOnly classification
    // ----------------------------------------------------------------

    [Test]
    public void IsWrapOnly_AlwaysReturnsFalse_DueToMissingSourceContext()
    {
        // IsWrapOnly is disabled because the pre-built wrapped snapshot lacks
        // source-specific wrapped supply edges (line 733 of AddHolderToTokenEdges_Pooled).
        var request = new FlowRequest { WithWrap = true };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False,
            "IsWrapOnly always returns false — pre-built snapshot can't have source-specific wrapped supply edges.");
    }

    [Test]
    public void IsWrapOnly_WithWrapAndFromTokens_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            FromTokens = new List<string> { AliceAddr }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False,
            "WithWrap + FromTokens should NOT be wrap-only (needs custom filtering).");
    }

    [Test]
    public void IsWrapOnly_WithWrapAndToTokens_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            ToTokens = new List<string> { BobAddr }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndExcludedFromTokens_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            ExcludedFromTokens = new List<string> { AliceAddr }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndExcludedToTokens_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            ExcludedToTokens = new List<string> { BobAddr }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndQuantizedMode_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            QuantizedMode = true
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndSimulatedBalances_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = AliceAddr, Token = AliceAddr, Amount = "1000" }
            }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndSimulatedTrusts_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new() { Truster = BobAddr, Trustee = AliceAddr }
            }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndSimulatedConsentedAvatars_ReturnsFalse()
    {
        var request = new FlowRequest
        {
            WithWrap = true,
            SimulatedConsentedAvatars = new List<string> { AliceAddr }
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithoutWrap_ReturnsFalse()
    {
        var request = new FlowRequest { WithWrap = false };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithNullWrap_ReturnsFalse()
    {
        var request = new FlowRequest { WithWrap = null };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    [Test]
    public void IsWrapOnly_WithWrapAndEmptyLists_ReturnsFalse()
    {
        // IsWrapOnly is disabled — always returns false regardless of input
        var request = new FlowRequest
        {
            WithWrap = true,
            FromTokens = new List<string>(),
            ToTokens = new List<string>(),
            ExcludedFromTokens = new List<string>(),
            ExcludedToTokens = new List<string>(),
            SimulatedBalances = new List<SimulatedBalance>(),
            SimulatedTrusts = new List<SimulatedTrust>(),
            SimulatedConsentedAvatars = new List<string>(),
            QuantizedMode = false
        };
        Assert.That(CapacityGraphPool.IsWrapOnly(request), Is.False);
    }

    // ----------------------------------------------------------------
    // Wrapped snapshot lifecycle
    // ----------------------------------------------------------------

    [Test]
    public void CurrentWrappedSnapshot_InitiallyNull()
    {
        var pool = CreatePool();
        Assert.That(pool.CurrentWrappedSnapshot, Is.Null);
    }

    [Test]
    public void UpdateWrappedSnapshot_SetsCurrentWrappedSnapshot()
    {
        var pool = CreatePool();
        var graph = BuildMinimalGraph();
        var snap = new CapacityGraphSnapshot(42, graph);

        pool.UpdateWrappedSnapshot(snap);

        Assert.That(pool.CurrentWrappedSnapshot, Is.Not.Null);
        Assert.That(pool.CurrentWrappedSnapshot!.Block, Is.EqualTo(42));
        Assert.That(pool.CurrentWrappedSnapshot.Base, Is.SameAs(graph));
    }

    [Test]
    public void UpdateWrappedSnapshot_ReplacesOldSnapshot()
    {
        var pool = CreatePool();
        var graph1 = BuildMinimalGraph();
        var graph2 = BuildMinimalGraph();

        pool.UpdateWrappedSnapshot(new CapacityGraphSnapshot(1, graph1));
        pool.UpdateWrappedSnapshot(new CapacityGraphSnapshot(2, graph2));

        Assert.That(pool.CurrentWrappedSnapshot!.Block, Is.EqualTo(2));
        Assert.That(pool.CurrentWrappedSnapshot.Base, Is.SameAs(graph2));
    }

    // ----------------------------------------------------------------
    // Rent with wrap-only requests uses wrapped snapshot
    // ----------------------------------------------------------------

    [Test]
    public async Task Rent_WrapOnly_BuildsAdHocGraph()
    {
        // After the IsWrapOnly fix, wrap-only requests build ad-hoc graphs
        // (not the pre-built snapshot which lacks wrapped supply edges).
        var pool = CreatePool();
        var baseGraph = BuildMinimalGraph();
        var wrappedGraph = BuildMinimalGraph();

        pool.UpdateSnapshot(new CapacityGraphSnapshot(42, baseGraph), BuildEmptyGroupData());
        pool.UpdateWrappedSnapshot(new CapacityGraphSnapshot(42, wrappedGraph));

        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest { WithWrap = true };

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.Not.SameAs(wrappedGraph),
            "Wrap-only request should NOT use pre-built snapshot (lacks source-specific wrapped edges).");
        Assert.That(handle.Graph, Is.Not.SameAs(baseGraph),
            "Should build ad-hoc graph, not return base graph.");
    }

    [Test]
    public async Task Rent_WrapOnly_NoWrappedSnapshot_FallsBackToAdHoc()
    {
        var pool = CreatePool();
        var baseGraph = BuildMinimalGraph();

        pool.UpdateSnapshot(new CapacityGraphSnapshot(42, baseGraph), BuildEmptyGroupData());
        // Deliberately NOT setting wrapped snapshot

        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest { WithWrap = true };

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.Not.SameAs(baseGraph),
            "Without wrapped snapshot, should build ad-hoc (not return base graph).");
    }

    [Test]
    public async Task Rent_WrapPlusFilters_SkipsWrappedSnapshot()
    {
        var pool = CreatePool();
        var baseGraph = BuildMinimalGraph();
        var wrappedGraph = BuildMinimalGraph();

        pool.UpdateSnapshot(new CapacityGraphSnapshot(42, baseGraph), BuildEmptyGroupData());
        pool.UpdateWrappedSnapshot(new CapacityGraphSnapshot(42, wrappedGraph));

        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest
        {
            Source = AliceAddr,
            Sink = BobAddr,
            WithWrap = true,
            FromTokens = new List<string> { AliceAddr }
        };

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.Not.SameAs(wrappedGraph),
            "WithWrap + FromTokens should build ad-hoc, not use wrapped snapshot.");
        Assert.That(handle.Graph, Is.Not.SameAs(baseGraph),
            "Should build fresh ad-hoc graph.");
    }

    [Test]
    public async Task Rent_NoWrap_ReturnsBaseSnapshot()
    {
        var pool = CreatePool();
        var baseGraph = BuildMinimalGraph();
        var wrappedGraph = BuildMinimalGraph();

        pool.UpdateSnapshot(new CapacityGraphSnapshot(42, baseGraph), BuildEmptyGroupData());
        pool.UpdateWrappedSnapshot(new CapacityGraphSnapshot(42, wrappedGraph));

        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest(); // no wrap

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.SameAs(baseGraph),
            "Non-wrapped request should return base snapshot.");
        Assert.That(handle.Graph, Is.Not.SameAs(wrappedGraph),
            "Should not return wrapped graph for non-wrapped request.");
    }

    // ----------------------------------------------------------------
    // BuildFullWrappedGraph static method
    // ----------------------------------------------------------------

    [Test]
    public async Task BuildFullWrappedGraph_ReturnsValidGraph()
    {
        var mockLoadGraph = new MockLoadGraph();
        var balances = new BalanceGraph();
        int alice = AddressIdPool.IdOf(AliceAddr);
        balances.AddAvatar(alice);
        balances.AddBalance(alice, alice, 100_000_000L, isWrapped: false, isStatic: false);

        var trust = new Dictionary<int, HashSet<int>>
        {
            [AddressIdPool.IdOf(BobAddr)] = new HashSet<int> { alice }
        };

        var graph = await CapacityGraphPool.BuildFullWrappedGraph(
            balances, trust, mockLoadGraph, RouterAddr);

        Assert.That(graph, Is.Not.Null);
        Assert.That(graph.Nodes.Count, Is.GreaterThan(0),
            "Wrapped graph should have nodes.");
    }
}
