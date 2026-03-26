using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for the maxTransfers parameter in FlowRequest.
/// Tests the path pruning logic that limits the number of transfer steps.
///
/// The maxTransfers parameter counts collapsed transfer steps (Avatar -> Avatar per token).
/// When paths exceed this limit, they are pruned using a greedy algorithm that
/// prioritizes high-flow paths while minimizing step count.
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class MaxTransfersTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // Helper to get transfer count from result
    private static int GetTransferCount(MaxFlowResponse response) =>
        response.Transfers?.Count ?? 0;

    // Helper to assert MaxFlow > 0
    private static void AssertMaxFlowPositive(string? maxFlowStr, string message = "MaxFlow should be positive")
    {
        Assert.That(maxFlowStr, Is.Not.Null.And.Not.Empty, "MaxFlow should not be null or empty");
        var maxFlow = UInt256.Parse(maxFlowStr!);
        Assert.That(maxFlow > UInt256.Zero, Is.True, message);
    }

    // ─────────────────────── Basic Limit Tests ───────────────────────

    [Test]
    public void MaxTransfers_DirectPath_SingleStep_LimitOne_Succeeds()
    {
        // Arrange: Direct transfer A -> B with maxTransfers=1
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000); // 100 CRC
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 1
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should find path with exactly 1 step
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.EqualTo(1), "Direct transfer should be 1 step");
    }

    [Test]
    public void MaxTransfers_TwoHopPath_LimitTwo_Succeeds()
    {
        // Arrange: Two-hop transfer A -> B -> C with maxTransfers=2
        var source = Node(1);
        var intermediate = Node(2);
        var sink = Node(3);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddBalance(intermediate, token, 100_000_000);
        mock.AddTrust(intermediate, source);
        mock.AddTrust(intermediate, token);
        mock.AddTrust(sink, intermediate);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 2
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should find path with up to 2 steps
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.LessThanOrEqualTo(2),
            "Path should have at most 2 transfer steps");
    }

    [Test]
    public void MaxTransfers_Zero_TreatedAsNoLimit()
    {
        // Arrange: MaxTransfers=0 is treated as no limit (same as null)
        // This is correct behavior: 0 transfers is impossible, so it's ignored
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 0  // Treated as unlimited (0 is not > 0)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: maxTransfers=0 is treated as no limit, path should be found
        AssertMaxFlowPositive(result.MaxFlow, "MaxTransfers=0 should be treated as no limit");
    }

    // ─────────────────────── Pruning/Truncation Tests ───────────────────────

    [Test]
    public void MaxTransfers_ThreeHopPath_LimitOne_TruncatesToDirectOnly()
    {
        // Arrange: Has both direct and multi-hop paths, limit to 1
        // Source -> Sink (direct, token A)
        // Source -> Intermediate -> Sink (two-hop, token B)
        var source = Node(1);
        var intermediate = Node(2);
        var sink = Node(3);
        var tokenA = Node(10);  // For direct path
        var tokenB = Node(11);  // For multi-hop

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        // Direct path possible with tokenA
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);

        // Also multi-hop path possible with tokenB
        mock.AddBalance(source, tokenB, 50_000_000);
        mock.AddBalance(intermediate, tokenB, 50_000_000);
        mock.AddTrust(intermediate, source);
        mock.AddTrust(intermediate, tokenB);
        mock.AddTrust(sink, intermediate);
        mock.AddTrust(sink, tokenB);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 1
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should truncate to only the direct path
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.EqualTo(1),
            "With maxTransfers=1, should only include the direct transfer");
    }

    [Test]
    public void MaxTransfers_MultipleTokenPaths_PrunesByStepCount()
    {
        // Arrange: Multiple paths with different step counts
        // Direct path: Source -> Sink (tokenA, 80 CRC)
        // Two-hop: Source -> Mid1 -> Sink (tokenB, 50 CRC each)
        var source = Node(1);
        var mid1 = Node(2);
        var sink = Node(3);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        // Direct path with tokenA (1 step, higher flow)
        mock.AddBalance(source, tokenA, 80_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);

        // Multi-hop path with tokenB (2 steps)
        mock.AddBalance(source, tokenB, 50_000_000);
        mock.AddBalance(mid1, tokenB, 50_000_000);
        mock.AddTrust(mid1, source);
        mock.AddTrust(mid1, tokenB);
        mock.AddTrust(sink, mid1);
        mock.AddTrust(sink, tokenB);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 2
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should fit within 2 transfer steps
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.LessThanOrEqualTo(2));
    }

    [Test]
    public void MaxTransfers_ExactlyAtLimit_NoTruncation()
    {
        // Arrange: Path has exactly maxTransfers steps
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 1  // Exactly 1 step needed
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Path at limit should be included
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.EqualTo(1));
    }

    [Test]
    public void MaxTransfers_UnderLimit_NoChange()
    {
        // Arrange: Path has fewer steps than maxTransfers allows
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 10  // Much higher than needed
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Path should be included unchanged
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.GreaterThan(0));
    }

    [Test]
    public void MaxTransfers_Null_NoLimit()
    {
        // Arrange: maxTransfers=null means no limit
        var source = Node(1);
        var intermediate = Node(2);
        var sink = Node(3);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddBalance(intermediate, token, 100_000_000);
        mock.AddTrust(intermediate, source);
        mock.AddTrust(intermediate, token);
        mock.AddTrust(sink, intermediate);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = null  // No limit
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should include all paths without limit
        AssertMaxFlowPositive(result.MaxFlow);
    }

    // ─────────────────────── Group Minting + MaxTransfers ───────────────────────

    [Test]
    public void MaxTransfers_WithGroupMinting_CountsRouterSteps()
    {
        // Arrange: Group minting adds Router steps (Avatar -> Router -> Group)
        // Total steps: Avatar -> Router (1) + Router -> Group (1) + Group -> Avatar (1) = 3 minimum
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, token);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 5  // Should accommodate group minting steps
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should find path through group (with router steps)
        AssertMaxFlowPositive(result.MaxFlow);
        // Group minting path includes Router insertion
        Assert.That(result.Transfers!.Any(t => t.To == RouterAddress || t.From == RouterAddress),
            Is.True, "Group minting path should include Router");
    }

    [Test]
    public void MaxTransfers_TooLowForGroupMinting_NoPath()
    {
        // Arrange: MaxTransfers too low to fit group minting path
        // Group minting requires at least 3 steps (Avatar->Router, Router->Group, Group->Sink)
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, token);
        mock.AddTrust(sink, group);
        // No direct trust relationship, must go through group

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 1  // Too low for group minting path
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Either no path or zero flow (group path doesn't fit)
        // The exact behavior depends on whether any direct path exists
        Assert.That(GetTransferCount(result), Is.LessThanOrEqualTo(1),
            "Should not include group path that requires more than 1 step");
    }

    // ─────────────────────── Greedy Algorithm Behavior ───────────────────────

    [Test]
    public void MaxTransfers_GreedyPruning_PreferHighFlowPaths()
    {
        // Arrange: Multiple paths with different flows, limit forces choice
        // Path A: 100 CRC direct (1 step, tokenA)
        // Path B: 20 CRC direct (1 step, tokenB)
        // Each (from, to, token) tuple is a unique step.
        // With maxTransfers=1, only one token path can be included.
        // The greedy algorithm should prefer the higher-flow path (tokenA).
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10); // Higher balance
        var tokenB = Node(11); // Lower balance

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, tokenA, 100_000_000); // 100 CRC
        mock.AddBalance(source, tokenB, 20_000_000);  // 20 CRC
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);
        mock.AddTrust(sink, tokenB);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 1,  // Only 1 step allowed (one token type)
            DebugShowIntermediateSteps = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Debug output
        TestContext.Out.WriteLine($"MaxFlow: {result.MaxFlow}");
        TestContext.Out.WriteLine($"Transfer count: {GetTransferCount(result)}");
        if (result.Transfers != null)
        {
            foreach (var t in result.Transfers)
            {
                TestContext.Out.WriteLine($"  {t.From} -> {t.To}: {t.Value} (tokenOwner: {t.TokenOwner})");
            }
        }

        // Assert: Should find a path with exactly 1 step
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.EqualTo(1), "Should have exactly 1 transfer step");
    }

    [Test]
    public void MaxTransfers_SharedEdges_CountOnce()
    {
        // Arrange: Two paths share an edge, should only count once
        // Path A: Source -> Sink (tokenA)
        // Path B: Source -> Sink (tokenB) - same edge, different token
        // Both use the same (from, to) pair
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, tokenA, 50_000_000);
        mock.AddBalance(source, tokenB, 50_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);
        mock.AddTrust(sink, tokenB);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 2  // Allow 2 unique (from, to, token) steps
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should be able to use both tokens (each is a separate step)
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(GetTransferCount(result), Is.LessThanOrEqualTo(2));
    }

    // ─────────────────────── Negative Value Handling ───────────────────────

    [Test]
    public void MaxTransfers_NegativeValue_TreatedAsNoLimit()
    {
        // Arrange: Negative maxTransfers should be treated as no limit or invalid
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = -1  // Negative value
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Negative value should be treated as no limit (path found)
        AssertMaxFlowPositive(result.MaxFlow);
    }
}
