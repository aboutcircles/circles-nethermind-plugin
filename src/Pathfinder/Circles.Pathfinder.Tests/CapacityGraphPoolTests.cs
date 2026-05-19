using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for CapacityGraphPool: snapshot management, Rent semantics,
/// and RequestNeedsFiltering logic.
/// </summary>
[TestFixture, Parallelizable]
public class CapacityGraphPoolTests
{
    // Deterministic addresses for test isolation
    private const string RouterAddr = "0xf000000000000000000000000000000000000001";
    private const string AliceAddr = "0xf100000000000000000000000000000000000001";
    private const string BobAddr = "0xf200000000000000000000000000000000000002";

    /// <summary>
    /// Creates a CapacityGraphPool backed by an in-memory MockLoadGraph — no DB needed.
    /// </summary>
    private static CapacityGraphPool CreatePool()
    {
        var mockLoadGraph = new MockLoadGraph();
        return new CapacityGraphPool(RouterAddr, mockLoadGraph);
    }

    /// <summary>
    /// Builds a minimal CapacityGraph with two avatars, one trust edge,
    /// and one balance edge (Alice holds 100 CRC of her own token, Bob trusts Alice).
    /// </summary>
    private static CapacityGraph BuildMinimalGraph()
    {
        var graph = new CapacityGraph();

        int alice = AddressIdPool.IdOf(AliceAddr);
        int bob = AddressIdPool.IdOf(BobAddr);
        int router = AddressIdPool.IdOf(RouterAddr);

        graph.AddAvatar(alice);
        graph.AddAvatar(bob);
        graph.SetRouter(router);

        // Alice holds her own token
        graph.AddTokenNode(alice);
        int pool = AddressIdPool.TokenPoolIdOf(alice);
        graph.AddCapacityEdge(alice, pool, alice, 100_000_000L);
        // Bob trusts Alice's token -> pool out-edge
        graph.AddCapacityEdge(pool, bob, alice, long.MaxValue);

        return graph;
    }

    /// <summary>
    /// Returns a minimal but valid BalanceGraph + trust lookup for Rent calls.
    /// </summary>
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

    // ----------------------------------------------------------------
    // 1. HasCurrentSnapshot before any update
    // ----------------------------------------------------------------

    [Test]
    public void HasCurrentSnapshot_BeforeUpdate_ReturnsFalse()
    {
        var pool = CreatePool();
        Assert.That(pool.HasCurrentSnapshot, Is.False);
    }

    // ----------------------------------------------------------------
    // 2. HasCurrentSnapshot after update
    // ----------------------------------------------------------------

    [Test]
    public void HasCurrentSnapshot_AfterUpdate_ReturnsTrue()
    {
        var pool = CreatePool();
        var graph = BuildMinimalGraph();
        var snap = new CapacityGraphSnapshot(1, graph);

        pool.UpdateSnapshot(snap);

        Assert.That(pool.HasCurrentSnapshot, Is.True);
    }

    // ----------------------------------------------------------------
    // 3. Rent before snapshot loaded throws GraphNotReadyException
    //    (a recoverable warmup signal, distinct from a solver fault)
    // ----------------------------------------------------------------

    [Test]
    public void Rent_BeforeSnapshotLoaded_ThrowsGraphNotReady()
    {
        var pool = CreatePool();
        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest();

        var ex = Assert.ThrowsAsync<GraphNotReadyException>(async () =>
        {
            await pool.Rent(request, balances, trust);
        });

        Assert.That(ex!.Message, Does.Contain("No capacity graph available yet"));
    }

    // ----------------------------------------------------------------
    // 4. Rent with unfiltered request returns shared snapshot base graph
    // ----------------------------------------------------------------

    [Test]
    public async Task Rent_UnfilteredRequest_ReturnsSharedSnapshot()
    {
        var pool = CreatePool();
        var graph = BuildMinimalGraph();
        var snap = new CapacityGraphSnapshot(42, graph);
        pool.UpdateSnapshot(snap);

        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest(); // empty => unfiltered

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.SameAs(graph),
            "Unfiltered Rent should return the exact same CapacityGraph instance from the snapshot");
    }

    // ----------------------------------------------------------------
    // 5. Rent with filtered request builds a new graph (needs cached group data to skip DB)
    // ----------------------------------------------------------------

    [Test]
    public async Task Rent_FilteredRequest_BuildsNewGraph()
    {
        var pool = CreatePool();
        var baseGraph = BuildMinimalGraph();
        var snap = new CapacityGraphSnapshot(42, baseGraph);

        // Provide cached group data so GraphFactory.CreateCapacityGraph skips DB queries
        var cachedGroups = new CachedGroupData(
            GroupNodes: new HashSet<int>(),
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>(),
            ConsentedAvatars: new HashSet<int>(),
            RegisteredAvatarIds: new HashSet<int>(),
            WrapperToAvatar: new Dictionary<int, int>());

        pool.UpdateSnapshot(snap, cachedGroups);

        var (balances, trust) = BuildMinimalInputs();

        // Request with FromTokens triggers filtering
        var request = new FlowRequest
        {
            Source = AliceAddr,
            Sink = BobAddr,
            FromTokens = new List<string> { AliceAddr }
        };

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.Not.SameAs(baseGraph),
            "Filtered Rent should build a new CapacityGraph, not return the shared snapshot");
    }

    // ----------------------------------------------------------------
    // 6. RequestNeedsFiltering: empty request
    // ----------------------------------------------------------------

    [Test]
    public void RequestNeedsFiltering_EmptyRequest_ReturnsFalse()
    {
        var request = new FlowRequest();
        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.False);
    }

    // ----------------------------------------------------------------
    // 7. RequestNeedsFiltering: with FromTokens
    // ----------------------------------------------------------------

    [Test]
    public void RequestNeedsFiltering_WithFromTokens_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            FromTokens = new List<string> { AliceAddr }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    // ----------------------------------------------------------------
    // 8. RequestNeedsFiltering: with WithWrap
    // ----------------------------------------------------------------

    [Test]
    public void RequestNeedsFiltering_WithWrap_ReturnsTrue()
    {
        var request = new FlowRequest { WithWrap = true };
        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    // ----------------------------------------------------------------
    // 9. RequestNeedsFiltering: with QuantizedMode
    // ----------------------------------------------------------------

    [Test]
    public void RequestNeedsFiltering_WithQuantizedMode_ReturnsTrue()
    {
        var request = new FlowRequest { QuantizedMode = true };
        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    // ----------------------------------------------------------------
    // Additional RequestNeedsFiltering coverage
    // ----------------------------------------------------------------

    [Test]
    public void RequestNeedsFiltering_WithToTokens_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            ToTokens = new List<string> { BobAddr }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    [Test]
    public void RequestNeedsFiltering_WithExcludedFromTokens_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            ExcludedFromTokens = new List<string> { AliceAddr }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    [Test]
    public void RequestNeedsFiltering_WithExcludedToTokens_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            ExcludedToTokens = new List<string> { BobAddr }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    [Test]
    public void RequestNeedsFiltering_WithSimulatedBalances_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = AliceAddr, Token = AliceAddr, Amount = "1000000000000000000" }
            }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    [Test]
    public void RequestNeedsFiltering_WithSimulatedTrusts_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new() { Truster = BobAddr, Trustee = AliceAddr }
            }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    [Test]
    public void RequestNeedsFiltering_WithSimulatedConsentedAvatars_ReturnsTrue()
    {
        var request = new FlowRequest
        {
            SimulatedConsentedAvatars = new List<string> { AliceAddr }
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.True);
    }

    [Test]
    public void RequestNeedsFiltering_WithNullLists_ReturnsFalse()
    {
        // Explicitly null lists should not trigger filtering
        var request = new FlowRequest
        {
            FromTokens = null,
            ToTokens = null,
            ExcludedFromTokens = null,
            ExcludedToTokens = null,
            SimulatedBalances = null,
            SimulatedTrusts = null,
            SimulatedConsentedAvatars = null,
            WithWrap = null,
            QuantizedMode = null
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.False);
    }

    [Test]
    public void RequestNeedsFiltering_WithEmptyLists_ReturnsFalse()
    {
        // Empty (but non-null) lists should not trigger filtering
        var request = new FlowRequest
        {
            FromTokens = new List<string>(),
            ToTokens = new List<string>(),
            ExcludedFromTokens = new List<string>(),
            ExcludedToTokens = new List<string>(),
            SimulatedBalances = new List<SimulatedBalance>(),
            SimulatedTrusts = new List<SimulatedTrust>(),
            SimulatedConsentedAvatars = new List<string>(),
            WithWrap = false,
            QuantizedMode = false
        };

        Assert.That(CapacityGraphPool.RequestNeedsFiltering(request), Is.False);
    }

    // ----------------------------------------------------------------
    // 10. UpdateSnapshot replaces old snapshot
    // ----------------------------------------------------------------

    [Test]
    public async Task UpdateSnapshot_ReplacesOldSnapshot()
    {
        var pool = CreatePool();

        var graphOld = BuildMinimalGraph();
        var snapOld = new CapacityGraphSnapshot(1, graphOld);
        pool.UpdateSnapshot(snapOld);

        var graphNew = BuildMinimalGraph();
        var snapNew = new CapacityGraphSnapshot(2, graphNew);
        pool.UpdateSnapshot(snapNew);

        var (balances, trust) = BuildMinimalInputs();
        var request = new FlowRequest(); // unfiltered => returns shared base

        using var handle = await pool.Rent(request, balances, trust);

        Assert.That(handle.Graph, Is.SameAs(graphNew),
            "After a second UpdateSnapshot, Rent should return the latest graph");
        Assert.That(handle.Graph, Is.Not.SameAs(graphOld),
            "Old graph should no longer be returned");
    }

    // ----------------------------------------------------------------
    // 11. CachedGroupData is passed to filtered builds
    // ----------------------------------------------------------------

    [Test]
    public async Task CachedGroupData_PassedToFilteredBuilds()
    {
        var pool = CreatePool();
        var baseGraph = BuildMinimalGraph();
        var snap = new CapacityGraphSnapshot(42, baseGraph);

        int alice = AddressIdPool.IdOf(AliceAddr);
        int bob = AddressIdPool.IdOf(BobAddr);

        // Create cached group data with a group node
        var groupAddr = "0xf300000000000000000000000000000000000003";
        int groupId = AddressIdPool.IdOf(groupAddr);

        var cachedGroups = new CachedGroupData(
            GroupNodes: new HashSet<int> { groupId },
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>
            {
                [groupId] = new HashSet<int> { alice }
            },
            ConsentedAvatars: new HashSet<int> { bob },
            RegisteredAvatarIds: new HashSet<int> { alice, bob, groupId },
            WrapperToAvatar: new Dictionary<int, int>());

        pool.UpdateSnapshot(snap, cachedGroups);

        var (balances, trust) = BuildMinimalInputs();

        // Filtered request to trigger ad-hoc graph build
        var request = new FlowRequest
        {
            Source = AliceAddr,
            Sink = BobAddr,
            FromTokens = new List<string> { AliceAddr }
        };

        using var handle = await pool.Rent(request, balances, trust);
        var filteredGraph = handle.Graph;

        // The filtered graph should contain the group from cached data
        Assert.That(filteredGraph.GroupNodes, Does.Contain(groupId),
            "Filtered graph should include group nodes from cached group data");
        Assert.That(filteredGraph.GroupTrustedTokens.ContainsKey(groupId), Is.True,
            "Filtered graph should include group trusted tokens from cached data");
        Assert.That(filteredGraph.ConsentedAvatars, Does.Contain(bob),
            "Filtered graph should include consented avatars from cached data");
    }

    // ----------------------------------------------------------------
    // CapacityGraphSnapshot property verification
    // ----------------------------------------------------------------

    [Test]
    public void CapacityGraphSnapshot_ExposesBlockAndBase()
    {
        var graph = BuildMinimalGraph();
        var snap = new CapacityGraphSnapshot(99, graph);

        Assert.That(snap.Block, Is.EqualTo(99));
        Assert.That(snap.Base, Is.SameAs(graph));
    }

    // ----------------------------------------------------------------
    // CapacityGraphHandle is disposable no-op
    // ----------------------------------------------------------------

    [Test]
    public void CapacityGraphHandle_DisposeIsNoOp()
    {
        var graph = BuildMinimalGraph();
        var handle = new CapacityGraphHandle(graph);

        // Dispose should not throw or invalidate the graph
        handle.Dispose();

        Assert.That(handle.Graph, Is.SameAs(graph),
            "Graph reference should still be valid after Dispose");
    }
}
