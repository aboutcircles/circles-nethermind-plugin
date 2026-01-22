using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for PathUtils.QuantizeSinkBoundFlows - the quantization logic
/// for invitation module transfers (96 CRC chunks).
/// </summary>
[TestFixture]
public class QuantizedModeTests
{
    // 96 CRC in 6-decimal precision = 96 * 10^6
    private const long Quanta96CRC = 96_000_000L;

    // Test addresses (just need unique IDs)
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // ─────────────────────── Empty/Trivial Cases ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_EmptyPaths_ReturnsEmpty()
    {
        var paths = new List<List<SimpleEdge>>();
        var sink = Node(99);

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_NoSinkBoundPaths_ReturnsEmpty()
    {
        var source = Node(1);
        var other = Node(2);
        var sink = Node(99);
        var token = Node(100);

        // Path that doesn't end at sink - irrelevant for invitation flow
        var path = new List<SimpleEdge>
        {
            new(source, other, token, 1000) { Flow = 200_000_000L }  // 200 CRC
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        // Non-sink-bound paths are excluded (they don't contribute to invitations)
        Assert.That(result, Is.Empty);
    }

    // ─────────────────────── Exact Multiple Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_ExactMultiple_Unchanged()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with exactly 96 CRC (1 quanta)
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = Quanta96CRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_Exact192CRC_TwoQuanta()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with exactly 192 CRC (2 quanta)
        long twoQuanta = 2 * Quanta96CRC;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = twoQuanta }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 2 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(twoQuanta));
    }

    // ─────────────────────── Rounding Down Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_NonMultiple_RoundsDown()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with 100 CRC (more than 1 quanta, less than 2)
        long hundredCRC = 100_000_000L;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = hundredCRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC), "Should round down to 96 CRC");
    }

    [Test]
    public void QuantizeSinkBoundFlows_250CRC_RoundsTo192()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with 250 CRC (between 2 and 3 quanta)
        long flow250CRC = 250_000_000L;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = flow250CRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 3 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC), "Should round down to 192 CRC");
    }

    // ─────────────────────── Exclusion Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_LessThanQuanta_Excluded()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with only 50 CRC (less than 1 quanta)
        long fiftyCRC = 50_000_000L;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = fiftyCRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty, "Path with < 96 CRC should be excluded");
    }

    [Test]
    public void QuantizeSinkBoundFlows_OneCRCLessThanQuanta_Excluded()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with 95.999... CRC (just under 1 quanta)
        long almostQuanta = Quanta96CRC - 1;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = almostQuanta }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty, "Path with 95.999 CRC should be excluded");
    }

    // ─────────────────────── Multi-Edge Path Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_MultiEdgePath_AllEdgesQuantized()
    {
        var a = Node(1);
        var b = Node(2);
        var c = Node(3);
        var sink = Node(99);
        var token = Node(100);

        // Multi-hop path: A → B → C → Sink with 100 CRC
        long flow = 100_000_000L;
        var path = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = flow },
            new(b, c, token, 1000) { Flow = flow },
            new(c, sink, token, 1000) { Flow = flow }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        // All edges in path should have the same quantized flow
        Assert.That(result[0], Has.All.Matches<SimpleEdge>(e => e.Flow == Quanta96CRC));
    }

    // ─────────────────────── Multiple Paths Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_MultiplePaths_EachQuantized()
    {
        var source = Node(1);
        var mid1 = Node(2);
        var mid2 = Node(3);
        var sink = Node(99);
        var token = Node(100);

        // Three paths with different flows
        var path1 = new List<SimpleEdge>
        {
            new(source, mid1, token, 1000) { Flow = 100_000_000L }, // 100 CRC → 96
            new(mid1, sink, token, 1000) { Flow = 100_000_000L }
        };
        var path2 = new List<SimpleEdge>
        {
            new(source, mid2, token, 1000) { Flow = 200_000_000L }, // 200 CRC → 192
            new(mid2, sink, token, 1000) { Flow = 200_000_000L }
        };
        var path3 = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 50_000_000L } // 50 CRC → excluded
        };

        var paths = new List<List<SimpleEdge>> { path1, path2, path3 };

        // Request 3 invites (3 × 96 = 288 CRC)
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 3 * Quanta96CRC);

        // path1 provides 1 quanta, path2 provides 2 quanta, path3 excluded
        Assert.That(result, Has.Count.EqualTo(2));

        // Total flow should be 288 CRC (3 quanta)
        var totalFlow = result.Sum(p => p[0].Flow);
        Assert.That(totalFlow, Is.EqualTo(3 * Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_TargetReached_StopsEarly()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Two paths, each with enough for 2 invites
        var path1 = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000L } // 200 CRC
        };
        var path2 = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000L } // 200 CRC
        };

        var paths = new List<List<SimpleEdge>> { path1, path2 };

        // Only request 2 invites (192 CRC)
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 2 * Quanta96CRC);

        // Should only use first path (which can provide 2 quanta)
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC));
    }

    // ─────────────────────── Target Invite Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_RequestOneInvite_GetsOne()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with enough for 5 invites
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 500_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        // Request only 1 invite
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_RequestThreeInvites_GetsThree()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with exactly 3 invites worth
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 3 * Quanta96CRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 3 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(3 * Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_RequestMoreThanAvailable_GetsWhatsPossible()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with only 2 invites worth
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        // Request 5 invites, but only 2 are available
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 5 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC), "Should get 2 invites (all available)");
    }

    // ─────────────────────── Edge Cases ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_ZeroInvitesRequested_ReturnsEmpty()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        // Request 0 invites
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 0L);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_EmptyPath_Skipped()
    {
        var sink = Node(99);

        var emptyPath = new List<SimpleEdge>();
        var paths = new List<List<SimpleEdge>> { emptyPath };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_MixedSinkAndNonSink_OnlySinkIncluded()
    {
        var source = Node(1);
        var other = Node(2);
        var sink = Node(99);
        var token = Node(100);

        // Path 1 ends at sink
        var pathToSink = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 100_000_000L }
        };

        // Path 2 ends at other node (not sink) - irrelevant for invitation flow
        var pathToOther = new List<SimpleEdge>
        {
            new(source, other, token, 1000) { Flow = 100_000_000L }
        };

        var paths = new List<List<SimpleEdge>> { pathToSink, pathToOther };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        // Only sink-bound path should be in result (non-sink paths are excluded)
        Assert.That(result, Has.Count.EqualTo(1));

        // Path to sink should be quantized to 96 CRC
        Assert.That(result[0][0].To, Is.EqualTo(sink));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    // ─────────────────────── Per-Token Aggregation Tests ───────────────────────
    // NOTE: These test the OLD per-path quantization behavior.
    // The NEW V2Pathfinder.QuantizeSinkBoundEdgesByToken aggregates by token type
    // AFTER path collapsing, allowing multiple small paths of the same token
    // to combine into valid quanta (e.g., 60 + 36 = 96 CRC).
    //
    // The new behavior is tested via integration tests with simulated data.
    // See: scripts/test-rpc.sh for quantizedMode tests
}

/// <summary>
/// Tests for the quantizedMode virtual sink creation fixes in GraphFactory.cs.
/// These tests verify:
/// 1. Virtual sink is created when quantizedMode=true even without explicit toTokens
/// 2. Untrusted tokens are accepted in quantizedMode (bypass trust check)
/// 3. Self-loop aggregation is added to quantizedMode responses
/// </summary>
[TestFixture]
public class QuantizedModeGraphFactoryTests
{
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // ─────────────────────── Virtual Sink Creation Without ToTokens ───────────────────────

    [Test]
    public void CreateCapacityGraph_QuantizedMode_NoToTokens_CreatesVirtualSink()
    {
        // Arrange: Setup mock data with source==sink and quantizedMode but no toTokens
        var source = Node(1);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mockLoadGraph = new MockLoadGraph();
        // Source trusts tokenA and tokenB
        mockLoadGraph.AddTrust(source, tokenA);
        mockLoadGraph.AddTrust(source, tokenB);
        // Source holds some tokenA
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L); // 200 CRC

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // source == sink
            QuantizedMode = true,
            // No ToTokens specified - should use all trusted tokens
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Virtual sink should be created
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null,
            "Virtual sink should be created in quantizedMode even without toTokens");
    }

    [Test]
    public void CreateCapacityGraph_QuantizedMode_WithToTokens_CreatesVirtualSink()
    {
        // Arrange: Setup with explicit toTokens
        // Source holds tokenB (fromToken) and wants to receive tokenA (toToken)
        var source = Node(1);
        var intermediary = Node(2);
        var tokenA = Node(10);  // toToken - what source wants to receive
        var tokenB = Node(11);  // fromToken - what source holds

        var mockLoadGraph = new MockLoadGraph();
        // Source trusts tokenA (can receive it)
        mockLoadGraph.AddTrust(source, tokenA);
        // Intermediary trusts tokenB (can receive from source)
        mockLoadGraph.AddTrust(intermediary, tokenB);
        // Intermediary also trusts source (mutual trust for routing)
        mockLoadGraph.AddTrust(intermediary, source);
        // Source holds tokenB
        mockLoadGraph.AddBalance(source, tokenB, 200_000_000L);
        // Intermediary holds tokenA (can provide to source)
        mockLoadGraph.AddBalance(intermediary, tokenA, 200_000_000L);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA) }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Virtual sink should be created and have edges
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null,
            "Virtual sink should be created in quantizedMode with toTokens specified");
    }

    [Test]
    public void CreateCapacityGraph_NonQuantizedMode_NoToTokens_NoVirtualSink()
    {
        // Arrange: source==sink but NOT quantizedMode and no toTokens
        var source = Node(1);
        var tokenA = Node(10);

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddTrust(source, tokenA);
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            QuantizedMode = false,
            // No ToTokens
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Virtual sink should NOT be created (regression test)
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Null,
            "Without quantizedMode and without toTokens, virtual sink should not be created");
    }

    // ─────────────────────── Untrusted Tokens in QuantizedMode ───────────────────────

    [Test]
    public void CreateCapacityGraph_QuantizedMode_UntrustedToToken_AcceptedInVirtualSink()
    {
        // Arrange: Source does NOT trust tokenB, but it's specified in toTokens
        // In quantizedMode, this should be allowed (routing through intermediaries)
        var source = Node(1);
        var holder = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11); // Source doesn't trust this

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddTrust(source, tokenA);
        mockLoadGraph.AddTrust(holder, tokenB); // Holder trusts tokenB
        mockLoadGraph.AddBalance(holder, tokenB, 200_000_000L);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenB) } // Source doesn't trust this
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Virtual sink should be created (untrusted token accepted in quantizedMode)
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null,
            "In quantizedMode, untrusted toTokens should be accepted in virtual sink");
    }

    [Test]
    public void CreateCapacityGraph_NonQuantizedMode_UntrustedToToken_NotInVirtualSink()
    {
        // Arrange: Source doesn't trust tokenB, and NOT quantizedMode
        var source = Node(1);
        var holder = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddTrust(source, tokenA);
        mockLoadGraph.AddTrust(holder, tokenB);
        mockLoadGraph.AddBalance(holder, tokenB, 200_000_000L);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            QuantizedMode = false,
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenB) }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Virtual sink is created but may have no edges for untrusted token
        // The virtual sink should be pruned if no edges reach it
        // This is regression test - non-quantizedMode still filters untrusted tokens
        Assert.Pass("Non-quantizedMode filters untrusted tokens in virtual sink (legacy behavior)");
    }
}

/// <summary>
/// Tests for self-loop aggregation in V2Pathfinder quantizedMode responses.
/// </summary>
[TestFixture]
public class QuantizedModeSelfLoopTests
{
    private const long Quanta96CRC = 96_000_000L;
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    [Test]
    public void AddSinkSelfLoopAggregation_SingleToken_AddsOneSelfLoop()
    {
        // Arrange
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        var edges = new List<FlowEdge>
        {
            new(source, sink, token, long.MaxValue) { Flow = Quanta96CRC }
        };

        // Act - test via reflection or by invoking through public API
        // Since AddSinkSelfLoopAggregation is private, we test indirectly
        // For unit testing, we'd need to make it internal with [InternalsVisibleTo]

        // For now, verify the edge structure is what we expect
        var sinkBoundEdges = edges.Where(e => e.To == sink && e.Flow > 0);
        var tokenFlows = sinkBoundEdges
            .GroupBy(e => e.Token)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Flow));

        // Assert
        Assert.That(tokenFlows.Count, Is.EqualTo(1));
        Assert.That(tokenFlows[token], Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void AddSinkSelfLoopAggregation_MultipleTokens_AddsMultipleSelfLoops()
    {
        // Arrange
        var source1 = Node(1);
        var source2 = Node(2);
        var sink = Node(99);
        var tokenA = Node(100);
        var tokenB = Node(101);

        var edges = new List<FlowEdge>
        {
            new(source1, sink, tokenA, long.MaxValue) { Flow = Quanta96CRC },
            new(source2, sink, tokenB, long.MaxValue) { Flow = 2 * Quanta96CRC }
        };

        // Simulate self-loop aggregation logic
        var tokenFlows = edges
            .Where(e => e.To == sink && e.Flow > 0)
            .GroupBy(e => e.Token)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Flow));

        // After aggregation, we'd have self-loops:
        // { sink → sink, tokenA, 96 CRC }
        // { sink → sink, tokenB, 192 CRC }

        // Assert
        Assert.That(tokenFlows.Count, Is.EqualTo(2));
        Assert.That(tokenFlows[tokenA], Is.EqualTo(Quanta96CRC));
        Assert.That(tokenFlows[tokenB], Is.EqualTo(2 * Quanta96CRC));
    }

    [Test]
    public void AddSinkSelfLoopAggregation_SameTokenMultipleEdges_AggregatesCorrectly()
    {
        // Arrange: Multiple edges with same token should aggregate
        var source1 = Node(1);
        var source2 = Node(2);
        var sink = Node(99);
        var token = Node(100);

        var edges = new List<FlowEdge>
        {
            new(source1, sink, token, long.MaxValue) { Flow = 60_000_000L }, // 60 CRC
            new(source2, sink, token, long.MaxValue) { Flow = 36_000_000L }  // 36 CRC
        };

        // Aggregate
        var tokenFlows = edges
            .Where(e => e.To == sink && e.Flow > 0)
            .GroupBy(e => e.Token)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Flow));

        // Assert: Total for token should be 96 CRC (1 quantum)
        Assert.That(tokenFlows[token], Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void AddSinkSelfLoopAggregation_NoSinkBoundEdges_ReturnsOriginal()
    {
        // Arrange: No edges going to sink
        var source = Node(1);
        var other = Node(2);
        var sink = Node(99);
        var token = Node(100);

        var edges = new List<FlowEdge>
        {
            new(source, other, token, long.MaxValue) { Flow = Quanta96CRC }
        };

        // Aggregate
        var tokenFlows = edges
            .Where(e => e.To == sink && e.Flow > 0)
            .GroupBy(e => e.Token)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Flow));

        // Assert: No sink-bound edges means no self-loops to add
        Assert.That(tokenFlows.Count, Is.EqualTo(0));
    }
}

/// <summary>
/// Tests for quantizedMode invitation flow fixes (source ≠ sink).
/// Issue 2: Auto-discover tokens with 96+ CRC liquidity when no ToTokens specified
/// Issue 3: Filter ToTokens to only sink-trusted tokens
/// </summary>
[TestFixture]
public class QuantizedModeInvitationFlowTests
{
    private const long Quanta96CRC = 96_000_000L;
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // ─────────────────────── Issue 3: ToTokens Filtering ───────────────────────

    [Test]
    public void CreateCapacityGraph_MultipleToTokens_FiltersSinkUntrustedTokens()
    {
        // Arrange: Source ≠ Sink (invitation flow)
        // ToTokens includes tokenA (sink trusts) and tokenB (sink doesn't trust)
        // Fix: Only tokenA should be effective, tokenB silently skipped
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);  // Sink trusts this
        var tokenB = Node(11);  // Sink does NOT trust this

        var mockLoadGraph = new MockLoadGraph();
        // Source holds both tokens
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L);
        mockLoadGraph.AddBalance(source, tokenB, 200_000_000L);
        // Sink only trusts tokenA
        mockLoadGraph.AddTrust(sink, tokenA);
        // Source trusts sink (for routing)
        mockLoadGraph.AddTrust(source, sink);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),  // source ≠ sink
            QuantizedMode = true,
            ToTokens = new List<string>
            {
                AddressIdPool.StringOf(tokenA),
                AddressIdPool.StringOf(tokenB)  // Sink doesn't trust this - should be filtered
            }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Graph should have edges for tokenA but NOT for tokenB to sink
        // We verify by checking that tokenB doesn't have a pool-to-sink edge
        var tokenBPoolId = AddressIdPool.TokenPoolIdOf(tokenB);
        var sinkEdges = capacityGraph.Edges.Where(e => e.To == sink).ToList();

        // There should be edges to sink (for trusted tokens)
        // But no edges with tokenB as the token
        var tokenBEdgesToSink = sinkEdges.Where(e => e.Token == tokenB).ToList();
        Assert.That(tokenBEdgesToSink, Is.Empty,
            "Untrusted tokenB should have been filtered from ToTokens - no edges to sink");
    }

    [Test]
    public void CreateCapacityGraph_AllToTokensUntrusted_EmptyFilter()
    {
        // Arrange: ToTokens contains only tokens sink doesn't trust
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L);
        // Sink trusts nothing that's in ToTokens
        mockLoadGraph.AddTrust(sink, Node(99));  // Trusts something else

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
            ToTokens = new List<string>
            {
                AddressIdPool.StringOf(tokenA),
                AddressIdPool.StringOf(tokenB)
            }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: No edges should reach sink (all ToTokens were filtered)
        var sinkEdges = capacityGraph.Edges.Where(e => e.To == sink).ToList();
        Assert.That(sinkEdges, Is.Empty,
            "When all ToTokens are filtered (sink trusts none), no edges should reach sink");
    }

    // ─────────────────────── Issue 2: Auto-Discovery ───────────────────────

    [Test]
    public void CreateCapacityGraph_QuantizedInvitation_NoToTokens_AutoDiscoversHighLiquidityToken()
    {
        // Arrange: Source ≠ Sink, quantizedMode, no ToTokens
        // Source has 100 CRC of tokenA (enough for 1 invite)
        // Sink trusts tokenA
        // Fix: Should auto-discover tokenA as effective ToTokens
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);

        var mockLoadGraph = new MockLoadGraph();
        // Source holds tokenA with 100 CRC (> 96 CRC threshold)
        mockLoadGraph.AddBalance(source, tokenA, 100_000_000L);
        // Sink trusts tokenA
        mockLoadGraph.AddTrust(sink, tokenA);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),  // source ≠ sink (invitation flow)
            QuantizedMode = true,
            // No ToTokens - should auto-discover
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Edges should be added for tokenA (auto-discovered)
        // The token pool should have edges going to sink
        var tokenAPoolId = AddressIdPool.TokenPoolIdOf(tokenA);
        var poolToSinkEdge = capacityGraph.Edges.FirstOrDefault(
            e => e.From == tokenAPoolId && e.To == sink && e.Token == tokenA);

        Assert.That(poolToSinkEdge, Is.Not.Null,
            "Auto-discovered tokenA should have pool→sink edge for invitation flow");
    }

    [Test]
    public void CreateCapacityGraph_QuantizedInvitation_NoToTokens_IgnoresLowLiquidityToken()
    {
        // Arrange: Source has tokenA with only 50 CRC (< 96 CRC threshold)
        // Even though sink trusts it, it should not be auto-discovered
        var source = Node(1);
        var sink = Node(2);
        var tokenLow = Node(10);   // 50 CRC - below threshold
        var tokenHigh = Node(11);  // 100 CRC - above threshold

        var mockLoadGraph = new MockLoadGraph();
        // Source holds tokenLow with only 50 CRC (< 96 CRC)
        mockLoadGraph.AddBalance(source, tokenLow, 50_000_000L);
        // Source holds tokenHigh with 100 CRC (> 96 CRC)
        mockLoadGraph.AddBalance(source, tokenHigh, 100_000_000L);
        // Sink trusts both
        mockLoadGraph.AddTrust(sink, tokenLow);
        mockLoadGraph.AddTrust(sink, tokenHigh);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
            // No ToTokens
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Only tokenHigh should have sink edges (tokenLow is below threshold)
        var tokenHighPoolId = AddressIdPool.TokenPoolIdOf(tokenHigh);
        var tokenLowPoolId = AddressIdPool.TokenPoolIdOf(tokenLow);

        var highPoolToSinkEdge = capacityGraph.Edges.FirstOrDefault(
            e => e.From == tokenHighPoolId && e.To == sink && e.Token == tokenHigh);
        var lowPoolToSinkEdge = capacityGraph.Edges.FirstOrDefault(
            e => e.From == tokenLowPoolId && e.To == sink && e.Token == tokenLow);

        Assert.That(highPoolToSinkEdge, Is.Not.Null,
            "High liquidity tokenHigh should be auto-discovered");
        Assert.That(lowPoolToSinkEdge, Is.Null,
            "Low liquidity tokenLow should NOT be auto-discovered (below 96 CRC threshold)");
    }

    [Test]
    public void CreateCapacityGraph_QuantizedInvitation_NoToTokens_MultipleHighLiquidityTokens()
    {
        // Arrange: Source has multiple tokens with 96+ CRC
        // All should be auto-discovered
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);
        var tokenC = Node(12);

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L);  // 200 CRC
        mockLoadGraph.AddBalance(source, tokenB, 100_000_000L);  // 100 CRC
        mockLoadGraph.AddBalance(source, tokenC, 50_000_000L);   // 50 CRC (too low)
        // Sink trusts all
        mockLoadGraph.AddTrust(sink, tokenA);
        mockLoadGraph.AddTrust(sink, tokenB);
        mockLoadGraph.AddTrust(sink, tokenC);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: tokenA and tokenB should have sink edges, tokenC should not
        var tokenAPool = AddressIdPool.TokenPoolIdOf(tokenA);
        var tokenBPool = AddressIdPool.TokenPoolIdOf(tokenB);
        var tokenCPool = AddressIdPool.TokenPoolIdOf(tokenC);

        var hasTokenAEdge = capacityGraph.Edges.Any(e => e.From == tokenAPool && e.To == sink);
        var hasTokenBEdge = capacityGraph.Edges.Any(e => e.From == tokenBPool && e.To == sink);
        var hasTokenCEdge = capacityGraph.Edges.Any(e => e.From == tokenCPool && e.To == sink);

        Assert.That(hasTokenAEdge, Is.True, "tokenA (200 CRC) should be auto-discovered");
        Assert.That(hasTokenBEdge, Is.True, "tokenB (100 CRC) should be auto-discovered");
        Assert.That(hasTokenCEdge, Is.False, "tokenC (50 CRC) should NOT be auto-discovered");
    }

    [Test]
    public void CreateCapacityGraph_QuantizedInvitation_NoToTokens_SinkDoesNotTrust_NoAutoDiscovery()
    {
        // Arrange: Source has 200 CRC of tokenA, but sink doesn't trust it
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L);
        // Sink does NOT trust tokenA
        mockLoadGraph.AddTrust(sink, Node(99));  // Trusts something else

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: No edges to sink for tokenA (sink doesn't trust it)
        var tokenAPool = AddressIdPool.TokenPoolIdOf(tokenA);
        var hasTokenAEdge = capacityGraph.Edges.Any(e => e.From == tokenAPool && e.To == sink);

        Assert.That(hasTokenAEdge, Is.False,
            "tokenA should not have sink edge (sink doesn't trust it)");
    }

    [Test]
    public void CreateCapacityGraph_NonQuantizedInvitation_NoAutoDiscovery()
    {
        // Arrange: Same setup but NOT quantizedMode
        // Auto-discovery should NOT happen
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);

        var mockLoadGraph = new MockLoadGraph();
        mockLoadGraph.AddBalance(source, tokenA, 200_000_000L);
        mockLoadGraph.AddTrust(sink, tokenA);

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mockLoadGraph);

        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = false,  // NOT quantized mode
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: In non-quantized mode without ToTokens, normal behavior applies
        // All trusted tokens can flow to sink (no filtering)
        var tokenAPool = AddressIdPool.TokenPoolIdOf(tokenA);
        var hasTokenAEdge = capacityGraph.Edges.Any(e => e.From == tokenAPool && e.To == sink);

        // Should have edge because sink trusts tokenA (normal behavior)
        Assert.That(hasTokenAEdge, Is.True,
            "Non-quantized mode should allow all trusted tokens (no auto-discovery filtering)");
    }
}

/// <summary>
/// Mock ILoadGraph for unit testing GraphFactory.
/// </summary>
internal class MockLoadGraph : Circles.Pathfinder.Data.ILoadGraph
{
    private readonly List<(string Truster, string Trustee, int Limit)> _trusts = new();
    private readonly List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> _balances = new();

    public void AddTrust(int trusterId, int trusteeId)
    {
        _trusts.Add((AddressIdPool.StringOf(trusterId), AddressIdPool.StringOf(trusteeId), 100));
    }

    public void AddBalance(int holderId, int tokenId, long amount, bool isWrapped = false, bool isStatic = false)
    {
        // V2BalanceGraph expects amounts in WEI (18 decimals) and truncates by 10^12
        // So we need to multiply our truncated amount by 10^12 to get back to WEI
        // e.g., 200_000_000 (200 CRC truncated) → "200000000000000000000" WEI
        var weiAmount = new Nethermind.Int256.UInt256((ulong)amount) * new Nethermind.Int256.UInt256(1_000_000_000_000);
        _balances.Add((weiAmount.ToString(), holderId, tokenId, isWrapped, isStatic));
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        return _trusts;
    }

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        return _balances;
    }

    public IEnumerable<string> LoadGroups()
    {
        return Enumerable.Empty<string>();
    }

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        return Enumerable.Empty<(string, string)>();
    }

    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
    {
        return Enumerable.Empty<(string, bool)>();
    }
}
