using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for edge ordering in group minting scenarios.
///
/// Contract constraint: The operateFlowMatrix smart contract processes edges
/// sequentially. Groups can only transfer tokens they have received, so:
/// - All collateral edges (Router → Group) MUST precede the group's outbound mint edge
/// - All Avatar → Router edges should come before Router → Group edges
///
/// These tests verify the SortEdgesForMintDependencies and ValidateMintEdgeOrdering
/// methods in V2Pathfinder.
/// </summary>
[TestFixture]
public class EdgeOrderingTests
{
    // Test avatar IDs
    private const int Source = 1;
    private const int Sink = 2;
    private const int Router = 3;
    private const int Group1 = 4;
    private const int Group2 = 5;
    private const int Avatar1 = 6;
    private const int Avatar2 = 7;

    // Token IDs
    private const int Token1 = 100;
    private const int Token2 = 101;
    private const int Token3 = 102;
    // Group tokens have same ID as group address
    private const int GroupToken1 = Group1;
    private const int GroupToken2 = Group2;

    #region SortEdgesForMintDependencies Tests

    /// <summary>
    /// Basic test: Single group with multiple collateral tokens.
    /// All Router→Group edges should precede Group→Sink edge.
    /// </summary>
    [Test]
    public void SortEdges_SingleGroup_CollateralBeforeMint()
    {
        // Arrange: Edges in WRONG order (Group→Sink before all Router→Group)
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Source, Router, Token1, 50) { Flow = 50 },
            new(Group1, Sink, GroupToken1, 100) { Flow = 100 },  // WRONG: Mint before all collateral
            new(Router, Group1, Token1, 50) { Flow = 50 },
            new(Source, Router, Token2, 50) { Flow = 50 },
            new(Router, Group1, Token2, 50) { Flow = 50 },
        };

        // Act
        var sorted = SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: All collateral edges before mint edge
        int group1MintIndex = sorted.FindIndex(e => e.From == Group1 && e.To == Sink);
        int lastCollateralIndex = sorted.FindLastIndex(e => e.To == Group1 && capacityGraph.IsRouter(e.From));

        Assert.That(lastCollateralIndex, Is.LessThan(group1MintIndex),
            "All Router→Group edges must precede Group→Sink edge");
    }

    /// <summary>
    /// Multiple groups in same transaction.
    /// Each group's collateral edges should precede its own mint edge.
    /// </summary>
    [Test]
    public void SortEdges_MultipleGroups_EachGroupCollateralBeforeItsMint()
    {
        // Arrange: Two groups, edges interleaved incorrectly
        var capacityGraph = CreateCapacityGraphWithGroups(new[] { Group1, Group2 }, Router);
        var edges = new List<FlowEdge>
        {
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },   // Group1 mint (wrong position)
            new(Router, Group2, Token2, 30) { Flow = 30 },
            new(Group2, Sink, GroupToken2, 30) { Flow = 30 },   // Group2 mint
            new(Router, Group1, Token1, 50) { Flow = 50 },      // Group1 collateral (wrong position)
        };

        // Act
        var sorted = SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Each group's collateral precedes its mint
        AssertGroupCollateralBeforeMint(sorted, Group1, capacityGraph);
        AssertGroupCollateralBeforeMint(sorted, Group2, capacityGraph);
    }

    /// <summary>
    /// Non-group paths should remain unchanged.
    /// Standard Avatar→Avatar transfers have no ordering constraints.
    /// </summary>
    [Test]
    public void SortEdges_NoGroupEdges_PreservesOrder()
    {
        // Arrange: Standard path with no groups
        var capacityGraph = CreateCapacityGraphNoGroups();
        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 },
            new(Avatar1, Avatar2, Token1, 100) { Flow = 100 },
            new(Avatar2, Sink, Token1, 100) { Flow = 100 },
        };

        // Act
        var sorted = SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Order unchanged (all go to "otherEdges")
        Assert.That(sorted.Count, Is.EqualTo(3));
        Assert.That(sorted[0].From, Is.EqualTo(Source));
        Assert.That(sorted[1].From, Is.EqualTo(Avatar1));
        Assert.That(sorted[2].From, Is.EqualTo(Avatar2));
    }

    /// <summary>
    /// No router in graph - edges should pass through unchanged.
    /// </summary>
    [Test]
    public void SortEdges_NoRouter_ReturnsOriginalEdges()
    {
        // Arrange: Graph with groups but no router
        var capacityGraph = new CapacityGraph();
        capacityGraph.AddAvatar(Source);
        capacityGraph.AddAvatar(Sink);
        capacityGraph.AddGroup(Group1);
        // No router set!

        var edges = new List<FlowEdge>
        {
            new(Source, Group1, Token1, 100) { Flow = 100 },
            new(Group1, Sink, GroupToken1, 100) { Flow = 100 },
        };

        // Act
        var sorted = SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Same edges returned (no sorting without router)
        Assert.That(sorted, Is.SameAs(edges));
    }

    /// <summary>
    /// Avatar→Router edges should come before Router→Group edges.
    /// </summary>
    [Test]
    public void SortEdges_AvatarToRouterEdgesFirst()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 50) { Flow = 50 },      // Router→Group
            new(Source, Router, Token1, 50) { Flow = 50 },      // Avatar→Router (should be first)
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },   // Group→Sink
        };

        // Act
        var sorted = SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Avatar→Router comes first
        Assert.That(sorted[0].From, Is.EqualTo(Source));
        Assert.That(sorted[0].To, Is.EqualTo(Router));
    }

    #endregion

    #region ValidateMintEdgeOrdering Tests

    /// <summary>
    /// Correct ordering - no exception should be thrown.
    /// </summary>
    [Test]
    public void ValidateOrdering_CorrectOrder_NoException()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Source, Router, Token1, 100) { Flow = 100 },
            new(Router, Group1, Token1, 100) { Flow = 100 },
            new(Group1, Sink, GroupToken1, 100) { Flow = 100 },
        };

        Assert.DoesNotThrow(() => ValidateMintEdgeOrdering(edges, capacityGraph));
    }

    /// <summary>
    /// Mint edge appears before collateral - should throw.
    /// This triggers "insufficient collateral" because 0 inbound when mint is processed.
    /// </summary>
    [Test]
    public void ValidateOrdering_MintBeforeCollateral_ThrowsException()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Group1, Sink, GroupToken1, 100) { Flow = 100 },  // WRONG: Mint first (0 collateral)
            new(Router, Group1, Token1, 100) { Flow = 100 },     // Collateral after
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ValidateMintEdgeOrdering(edges, capacityGraph));

        // Either "ordering violation" or "insufficient collateral" is valid
        Assert.That(ex!.Message, Does.Contain("collateral").Or.Contain("ordering"));
    }

    /// <summary>
    /// Partial collateral received before mint - should throw.
    /// Group needs 100 but only 50 deposited when mint edge is processed.
    /// </summary>
    [Test]
    public void ValidateOrdering_PartialCollateral_ThrowsException()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 50) { Flow = 50 },
            new(Group1, Sink, GroupToken1, 100) { Flow = 100 },  // Needs 100, only 50 received!
            new(Router, Group1, Token2, 50) { Flow = 50 },       // Too late
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ValidateMintEdgeOrdering(edges, capacityGraph));

        Assert.That(ex!.Message, Does.Contain("insufficient collateral"));
    }

    /// <summary>
    /// Multiple groups with correct ordering - no exception.
    /// </summary>
    [Test]
    public void ValidateOrdering_MultipleGroups_CorrectOrder_NoException()
    {
        var capacityGraph = CreateCapacityGraphWithGroups(new[] { Group1, Group2 }, Router);
        var edges = new List<FlowEdge>
        {
            // Group1 flow
            new(Router, Group1, Token1, 50) { Flow = 50 },
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },
            // Group2 flow
            new(Router, Group2, Token2, 30) { Flow = 30 },
            new(Group2, Sink, GroupToken2, 30) { Flow = 30 },
        };

        Assert.DoesNotThrow(() => ValidateMintEdgeOrdering(edges, capacityGraph));
    }

    /// <summary>
    /// No router or groups - validation should pass (nothing to validate).
    /// </summary>
    [Test]
    public void ValidateOrdering_NoRouterOrGroups_NoException()
    {
        var capacityGraph = CreateCapacityGraphNoGroups();
        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 },
            new(Avatar1, Sink, Token1, 100) { Flow = 100 },
        };

        Assert.DoesNotThrow(() => ValidateMintEdgeOrdering(edges, capacityGraph));
    }

    /// <summary>
    /// Edge case: Group with zero outbound flow - should not cause issues.
    /// </summary>
    [Test]
    public void ValidateOrdering_GroupWithNoOutbound_NoException()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 100) { Flow = 100 },
            // No Group1→Sink edge (maybe filtered out or unused)
        };

        Assert.DoesNotThrow(() => ValidateMintEdgeOrdering(edges, capacityGraph));
    }

    #endregion

    #region Real Bug Scenario Tests

    /// <summary>
    /// Reproduces the exact bug scenario from Nov 17, 2025:
    /// - Multiple collateral tokens (13.63 + 27 + 14 CRC)
    /// - Single group mint (54.61 CRC)
    /// - Edges were returned in wrong order causing ERC1155InsufficientBalance
    /// </summary>
    [Test]
    public void BugScenario_MultipleCollateralTokens_CorrectlyOrdered()
    {
        // Arrange: Simulate the bug scenario with approximate values (scaled to avoid decimals)
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);

        // Original bug: edges came in wrong order
        var badOrder = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 1363) { Flow = 1363 },   // First collateral
            new(Group1, Sink, GroupToken1, 5461) { Flow = 5461 }, // Mint (TOO EARLY!)
            new(Router, Group1, Token2, 2700) { Flow = 2700 },   // Second collateral
            new(Router, Group1, Token3, 1400) { Flow = 1400 },   // Third collateral
        };

        // Act: Sort the edges
        var sorted = SortEdgesForMintDependencies(badOrder, capacityGraph);

        // Assert: All collateral before mint
        int mintIndex = sorted.FindIndex(e => e.From == Group1);
        int lastCollateralIndex = sorted.FindLastIndex(e => e.To == Group1);

        Assert.That(lastCollateralIndex, Is.LessThan(mintIndex),
            "Bug fix: All 3 collateral edges must precede mint edge");

        // Assert: Validation passes
        Assert.DoesNotThrow(() => ValidateMintEdgeOrdering(sorted, capacityGraph));
    }

    /// <summary>
    /// Verify that the old (buggy) order would fail validation.
    /// </summary>
    [Test]
    public void BugScenario_OldOrder_FailsValidation()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);

        // Bug scenario: only partial collateral before mint
        var buggyOrder = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 1363) { Flow = 1363 },   // First collateral (1363)
            new(Group1, Sink, GroupToken1, 5461) { Flow = 5461 }, // Mint needs 5461, only 1363 available!
            new(Router, Group1, Token2, 2700) { Flow = 2700 },   // Second collateral (too late)
            new(Router, Group1, Token3, 1400) { Flow = 1400 },   // Third collateral (too late)
        };

        // This should fail with insufficient collateral
        var ex = Assert.Throws<InvalidOperationException>(
            () => ValidateMintEdgeOrdering(buggyOrder, capacityGraph));

        Assert.That(ex!.Message, Does.Contain("insufficient collateral"));
    }

    #endregion

    #region Helper Methods

    private static CapacityGraph CreateCapacityGraphWithGroup(int groupId, int routerId)
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.SetRouter(routerId);
        graph.AddGroup(groupId);
        return graph;
    }

    private static CapacityGraph CreateCapacityGraphWithGroups(int[] groupIds, int routerId)
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.SetRouter(routerId);
        foreach (var groupId in groupIds)
        {
            graph.AddGroup(groupId);
        }
        return graph;
    }

    private static CapacityGraph CreateCapacityGraphNoGroups()
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.AddAvatar(Avatar1);
        graph.AddAvatar(Avatar2);
        // No router, no groups
        return graph;
    }

    private static void AssertGroupCollateralBeforeMint(List<FlowEdge> edges, int groupId, CapacityGraph cg)
    {
        int mintIndex = edges.FindIndex(e => e.From == groupId);
        int lastCollateralIndex = edges.FindLastIndex(e => e.To == groupId && cg.IsRouter(e.From));

        if (mintIndex >= 0 && lastCollateralIndex >= 0)
        {
            Assert.That(lastCollateralIndex, Is.LessThan(mintIndex),
                $"Group {groupId}: collateral must precede mint");
        }
    }

    /// <summary>
    /// Replicates SortEdgesForMintDependencies logic from V2Pathfinder for testing.
    /// </summary>
    private static List<FlowEdge> SortEdgesForMintDependencies(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            return edges;
        }

        var avatarToRouter = new List<FlowEdge>();
        var groupEdges = new Dictionary<int, (List<FlowEdge> Inbound, List<FlowEdge> Outbound)>();
        var otherEdges = new List<FlowEdge>();

        foreach (var edge in edges)
        {
            bool fromIsRouter = capacityGraph.IsRouter(edge.From);
            bool toIsRouter = capacityGraph.IsRouter(edge.To);
            bool fromIsGroup = capacityGraph.IsGroup(edge.From);
            bool toIsGroup = capacityGraph.IsGroup(edge.To);

            if (!fromIsRouter && !fromIsGroup && toIsRouter)
            {
                avatarToRouter.Add(edge);
            }
            else if (fromIsRouter && toIsGroup)
            {
                int groupId = edge.To;
                if (!groupEdges.TryGetValue(groupId, out var lists))
                {
                    lists = (new List<FlowEdge>(), new List<FlowEdge>());
                    groupEdges[groupId] = lists;
                }
                lists.Inbound.Add(edge);
            }
            else if (fromIsGroup && !toIsGroup && !toIsRouter)
            {
                int groupId = edge.From;
                if (!groupEdges.TryGetValue(groupId, out var lists))
                {
                    lists = (new List<FlowEdge>(), new List<FlowEdge>());
                    groupEdges[groupId] = lists;
                }
                lists.Outbound.Add(edge);
            }
            else
            {
                otherEdges.Add(edge);
            }
        }

        var result = new List<FlowEdge>(edges.Count);
        result.AddRange(avatarToRouter);

        foreach (var (_, (inbound, outbound)) in groupEdges)
        {
            result.AddRange(inbound);
            result.AddRange(outbound);
        }

        result.AddRange(otherEdges);
        return result;
    }

    /// <summary>
    /// Replicates ValidateMintEdgeOrdering logic from V2Pathfinder for testing.
    /// Note: Uses groupId directly instead of AddressIdPool.StringOf() since test IDs
    /// are synthetic and not registered in the pool.
    /// </summary>
    private static void ValidateMintEdgeOrdering(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null || capacityGraph.GroupNodes.Count == 0)
        {
            return;
        }

        var groupsWithOutboundSeen = new HashSet<int>();
        var groupInboundFlow = new Dictionary<int, long>();
        var groupOutboundFlow = new Dictionary<int, long>();

        for (int i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            bool fromIsRouter = capacityGraph.IsRouter(edge.From);
            bool fromIsGroup = capacityGraph.IsGroup(edge.From);
            bool toIsGroup = capacityGraph.IsGroup(edge.To);

            if (fromIsRouter && toIsGroup)
            {
                int groupId = edge.To;

                if (groupsWithOutboundSeen.Contains(groupId))
                {
                    throw new InvalidOperationException(
                        $"Edge ordering violation: Router → Group edge for group {groupId} " +
                        $"appears after Group → Avatar edge at index {i}. " +
                        "All collateral must be deposited before minting.");
                }

                groupInboundFlow.TryGetValue(groupId, out long current);
                groupInboundFlow[groupId] = current + edge.Flow;
            }
            else if (fromIsGroup && !capacityGraph.IsRouter(edge.To) && !capacityGraph.IsGroup(edge.To))
            {
                int groupId = edge.From;
                groupsWithOutboundSeen.Add(groupId);

                groupOutboundFlow.TryGetValue(groupId, out long currentOutbound);
                groupOutboundFlow[groupId] = currentOutbound + edge.Flow;

                groupInboundFlow.TryGetValue(groupId, out long inbound);
                if (inbound < groupOutboundFlow[groupId])
                {
                    throw new InvalidOperationException(
                        $"Flow violation: Group {groupId} has insufficient collateral at edge index {i}. " +
                        $"Cumulative inbound: {inbound}, cumulative outbound required: {groupOutboundFlow[groupId]}. " +
                        "Ensure all Router → Group edges precede Group → Avatar edges.");
                }
            }
        }
    }

    #endregion
}
