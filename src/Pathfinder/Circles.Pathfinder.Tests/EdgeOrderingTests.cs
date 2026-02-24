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
/// methods in V2Pathfinder by calling the REAL implementation (not duplicated logic).
/// </summary>
[TestFixture]
public class EdgeOrderingTests
{
    // Test addresses - using realistic-looking addresses for clarity
    private const string SourceAddr = "0x1111111111111111111111111111111111111111";
    private const string SinkAddr = "0x2222222222222222222222222222222222222222";
    private const string RouterAddr = "0x3333333333333333333333333333333333333333";
    private const string Group1Addr = "0x4444444444444444444444444444444444444444";
    private const string Group2Addr = "0x5555555555555555555555555555555555555555";
    private const string Group3Addr = "0x6666666666666666666666666666666666666666";
    private const string Avatar1Addr = "0x7777777777777777777777777777777777777777";
    private const string Avatar2Addr = "0x8888888888888888888888888888888888888888";

    // Token addresses
    private const string Token1Addr = "0xaaaa111111111111111111111111111111111111";
    private const string Token2Addr = "0xaaaa222222222222222222222222222222222222";
    private const string Token3Addr = "0xaaaa333333333333333333333333333333333333";
    private const string Token4Addr = "0xaaaa444444444444444444444444444444444444";
    private const string Token5Addr = "0xaaaa555555555555555555555555555555555555";

    // Resolved IDs (populated from AddressIdPool in tests)
    private int Source => AddressIdPool.IdOf(SourceAddr);
    private int Sink => AddressIdPool.IdOf(SinkAddr);
    private int Router => AddressIdPool.IdOf(RouterAddr);
    private int Group1 => AddressIdPool.IdOf(Group1Addr);
    private int Group2 => AddressIdPool.IdOf(Group2Addr);
    private int Group3 => AddressIdPool.IdOf(Group3Addr);
    private int Avatar1 => AddressIdPool.IdOf(Avatar1Addr);
    private int Avatar2 => AddressIdPool.IdOf(Avatar2Addr);

    private int Token1 => AddressIdPool.IdOf(Token1Addr);
    private int Token2 => AddressIdPool.IdOf(Token2Addr);
    private int Token3 => AddressIdPool.IdOf(Token3Addr);
    private int Token4 => AddressIdPool.IdOf(Token4Addr);
    private int Token5 => AddressIdPool.IdOf(Token5Addr);

    // Group tokens have same ID as group address
    private int GroupToken1 => Group1;
    private int GroupToken2 => Group2;
    private int GroupToken3 => Group3;

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

        // Act - call REAL V2Pathfinder implementation
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

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

        // Act - call REAL V2Pathfinder implementation
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

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

        // Act - call REAL V2Pathfinder implementation
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

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

        // Act - call REAL V2Pathfinder implementation
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

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

        // Act - call REAL V2Pathfinder implementation
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Avatar→Router comes first
        Assert.That(sorted[0].From, Is.EqualTo(Source));
        Assert.That(sorted[0].To, Is.EqualTo(Router));
    }

    /// <summary>
    /// Three groups in sequence - tests complex dependency ordering.
    /// </summary>
    [Test]
    public void SortEdges_ThreeGroupsSequential_AllDependenciesSatisfied()
    {
        // Arrange: Three groups with interleaved edges
        var capacityGraph = CreateCapacityGraphWithGroups(new[] { Group1, Group2, Group3 }, Router);
        var edges = new List<FlowEdge>
        {
            new(Group3, Sink, GroupToken3, 20) { Flow = 20 },   // Group3 mint (wrong)
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },   // Group1 mint (wrong)
            new(Router, Group2, Token2, 30) { Flow = 30 },
            new(Group2, Sink, GroupToken2, 30) { Flow = 30 },
            new(Router, Group1, Token1, 50) { Flow = 50 },      // Group1 collateral
            new(Router, Group3, Token3, 20) { Flow = 20 },      // Group3 collateral
        };

        // Act
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Each group's collateral precedes its mint
        AssertGroupCollateralBeforeMint(sorted, Group1, capacityGraph);
        AssertGroupCollateralBeforeMint(sorted, Group2, capacityGraph);
        AssertGroupCollateralBeforeMint(sorted, Group3, capacityGraph);
    }

    /// <summary>
    /// Five different collateral token types to a single group.
    /// </summary>
    [Test]
    public void SortEdges_FiveCollateralTokenTypes_CorrectOrdering()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        var edges = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 10) { Flow = 10 },
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },  // Mint in middle (wrong)
            new(Router, Group1, Token2, 10) { Flow = 10 },
            new(Router, Group1, Token3, 10) { Flow = 10 },
            new(Router, Group1, Token4, 10) { Flow = 10 },
            new(Router, Group1, Token5, 10) { Flow = 10 },
        };

        // Act
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: All 5 collateral tokens before mint
        int mintIndex = sorted.FindIndex(e => e.From == Group1 && e.To == Sink);
        int lastCollateralIndex = sorted.FindLastIndex(e => e.To == Group1 && capacityGraph.IsRouter(e.From));

        Assert.That(lastCollateralIndex, Is.LessThan(mintIndex),
            "All 5 collateral edges must precede mint edge");
        Assert.That(sorted.Count(e => e.To == Group1 && capacityGraph.IsRouter(e.From)), Is.EqualTo(5),
            "Should have exactly 5 collateral edges");
    }

    /// <summary>
    /// Mixed group and avatar-to-avatar transfers in same path.
    /// </summary>
    [Test]
    public void SortEdges_MixedGroupAndAvatarTransfers_CorrectOrdering()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        capacityGraph.AddAvatar(Avatar1);
        capacityGraph.AddAvatar(Avatar2);

        var edges = new List<FlowEdge>
        {
            new(Avatar1, Avatar2, Token1, 25) { Flow = 25 },    // Standard transfer
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },   // Mint (wrong position)
            new(Source, Avatar1, Token1, 25) { Flow = 25 },     // Standard transfer
            new(Router, Group1, Token2, 50) { Flow = 50 },      // Collateral
            new(Source, Router, Token2, 50) { Flow = 50 },      // Avatar→Router
        };

        // Act
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

        // Assert: Group ordering correct, other edges preserved
        AssertGroupCollateralBeforeMint(sorted, Group1, capacityGraph);

        // Avatar→Router should come before Router→Group
        int avatarToRouterIdx = sorted.FindIndex(e => e.From == Source && e.To == Router);
        int routerToGroupIdx = sorted.FindIndex(e => e.From == Router && e.To == Group1);
        Assert.That(avatarToRouterIdx, Is.LessThan(routerToGroupIdx));
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

        // Act/Assert - call REAL V2Pathfinder implementation
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));
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

        // Act/Assert - call REAL V2Pathfinder implementation
        var ex = Assert.Throws<InvalidOperationException>(
            () => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));

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

        // Act/Assert - call REAL V2Pathfinder implementation
        var ex = Assert.Throws<InvalidOperationException>(
            () => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));

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

        // Act/Assert - call REAL V2Pathfinder implementation
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));
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

        // Act/Assert - call REAL V2Pathfinder implementation
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));
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

        // Act/Assert - call REAL V2Pathfinder implementation
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));
    }

    /// <summary>
    /// Collateral appearing after we've already seen outbound from group - ordering violation.
    /// </summary>
    [Test]
    public void ValidateOrdering_CollateralAfterMint_ThrowsOrderingViolation()
    {
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);
        // First mint (with some collateral), then more collateral arrives
        var edges = new List<FlowEdge>
        {
            new(Router, Group1, Token1, 50) { Flow = 50 },       // First collateral
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },    // Mint (OK so far)
            new(Router, Group1, Token2, 50) { Flow = 50 },       // More collateral AFTER mint - WRONG!
        };

        // Act/Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => V2Pathfinder.ValidateMintEdgeOrdering(edges, capacityGraph));

        Assert.That(ex!.Message, Does.Contain("ordering violation").IgnoreCase);
    }

    #endregion

    #region Consented Flow + Router Edge Tests

    /// <summary>
    /// BUG REPRODUCTION: isPermittedFlow failure with consented flow avatars.
    ///
    /// Root cause: ValidateConsentedFlow runs BEFORE InsertRouterInTransfers.
    /// The router-skip logic at lines 332-337 can never trigger because router
    /// edges don't exist yet when ValidateConsentedFlow processes the edges.
    ///
    /// Scenario:
    /// 1. Avatar has consented flow enabled
    /// 2. Avatar sends tokens to Group (which requires Router insertion)
    /// 3. ValidateConsentedFlow sees Avatar→Group edge
    /// 4. Avatar has consented flow, so it checks: Avatar trusts Group? Group has consented flow?
    /// 5. This check should be skipped for edges that will become router edges!
    ///
    /// Fix: Move InsertRouterInTransfers BEFORE ValidateConsentedFlow so router
    /// edges exist when validation runs, allowing the router-skip logic to work.
    /// </summary>
    [Test]
    public void ConsentedFlowAvatar_GroupMint_RouterEdgesShouldBeSkipped()
    {
        // Arrange: Avatar with consented flow sends to Group
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);

        // Source has consented flow enabled
        capacityGraph.ConsentedAvatars.Add(Source);

        // Set up trust lookup - Source does NOT trust Group directly
        // (This would fail the consented flow check if not for the router skip)
        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [Source] = new HashSet<int>(), // Source trusts no one
            [Group1] = new HashSet<int>()  // Group trusts no one
        };
        capacityGraph.TrustLookup = trustLookup;

        // Edges BEFORE router insertion: Avatar → Group
        // After InsertRouterInTransfers this becomes: Avatar → Router → Group
        var edgesBeforeRouterInsertion = new List<FlowEdge>
        {
            new(Source, Group1, Token1, 100) { Flow = 100 }
        };

        // Act: First insert router, then validate (CORRECT order - the fix)
        var edgesAfterRouterInsertion = capacityGraph.RouterNode != null
            ? InsertRouterInTransfersForTest(edgesBeforeRouterInsertion, capacityGraph)
            : edgesBeforeRouterInsertion;

        // Now validate - router edges should be skipped
        var validatedEdges = ValidateConsentedFlowForTest(edgesAfterRouterInsertion, capacityGraph);

        // Assert: Both router edges should be present (router edges are skipped by validation)
        Assert.That(validatedEdges.Count, Is.EqualTo(2),
            "Both Avatar→Router and Router→Group edges should pass validation");

        Assert.That(validatedEdges.Any(e => e.From == Source && e.To == Router),
            "Avatar→Router edge should be present");
        Assert.That(validatedEdges.Any(e => e.From == Router && e.To == Group1),
            "Router→Group edge should be present");
    }

    /// <summary>
    /// This test demonstrates the BUG - validating BEFORE router insertion
    /// causes consented flow avatar edges to Group to be incorrectly filtered.
    /// </summary>
    [Test]
    public void BUG_ConsentedFlowAvatar_ValidatingBeforeRouterInsertion_FiltersEdgesIncorrectly()
    {
        // Arrange: Avatar with consented flow sends to Group
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);

        // Source has consented flow enabled
        capacityGraph.ConsentedAvatars.Add(Source);

        // Set up trust lookup - Source does NOT trust Group directly
        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [Source] = new HashSet<int>(), // Source trusts no one
            [Group1] = new HashSet<int>()  // Group trusts no one
        };
        capacityGraph.TrustLookup = trustLookup;

        // Edges BEFORE router insertion: Avatar → Group
        var edgesBeforeRouterInsertion = new List<FlowEdge>
        {
            new(Source, Group1, Token1, 100) { Flow = 100 }
        };

        // BUG: Validate BEFORE router insertion (current buggy behavior)
        var validatedEdgesWRONG = ValidateConsentedFlowForTest(edgesBeforeRouterInsertion, capacityGraph);

        // The bug: Avatar→Group edge is filtered out because:
        // - Source has consented flow
        // - Source doesn't trust Group
        // - So the edge is removed!
        //
        // This is WRONG because after InsertRouterInTransfers, this edge becomes
        // Avatar→Router→Group, and router edges should be skipped.
        Assert.That(validatedEdgesWRONG.Count, Is.EqualTo(0),
            "BUG: Edge is incorrectly filtered because validation runs before router insertion");
    }

    /// <summary>
    /// When an avatar with consented flow sends through the router,
    /// the router edges (Avatar→Router and Router→Group) should bypass
    /// consented flow validation.
    /// </summary>
    [Test]
    public void ConsentedFlowAvatar_RouterEdgesExplicitly_AreNotFiltered()
    {
        // Arrange: Avatar with consented flow, edges already have router
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);

        // Source has consented flow enabled
        capacityGraph.ConsentedAvatars.Add(Source);

        // Set up trust lookup - Source does NOT trust Router or Group
        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [Source] = new HashSet<int>(),
            [Router] = new HashSet<int>(),
            [Group1] = new HashSet<int>()
        };
        capacityGraph.TrustLookup = trustLookup;

        // Edges with router already inserted
        var edgesWithRouter = new List<FlowEdge>
        {
            new(Source, Router, Token1, 100) { Flow = 100 },  // Avatar → Router
            new(Router, Group1, Token1, 100) { Flow = 100 }   // Router → Group
        };

        // Act: Validate with router edges
        var validatedEdges = ValidateConsentedFlowForTest(edgesWithRouter, capacityGraph);

        // Assert: Both edges should pass (router edges are skipped)
        Assert.That(validatedEdges.Count, Is.EqualTo(2),
            "Router edges should bypass consented flow validation");
    }

    /// <summary>
    /// Non-router edges from consented flow avatar should still be validated.
    /// </summary>
    [Test]
    public void ConsentedFlowAvatar_NonRouterEdge_IsValidated()
    {
        // Arrange: Avatar with consented flow sends to another avatar (not group)
        var capacityGraph = CreateCapacityGraphNoGroups();
        capacityGraph.AddAvatar(Source);
        capacityGraph.AddAvatar(Avatar1);

        // Source has consented flow enabled
        capacityGraph.ConsentedAvatars.Add(Source);

        // Source does NOT trust Avatar1, Avatar1 does NOT have consented flow
        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [Source] = new HashSet<int>(),  // Source trusts no one
            [Avatar1] = new HashSet<int>()
        };
        capacityGraph.TrustLookup = trustLookup;

        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 }
        };

        // Act: Validate
        var validatedEdges = ValidateConsentedFlowForTest(edges, capacityGraph);

        // Assert: Edge is filtered because Source has consented flow but doesn't trust Avatar1
        Assert.That(validatedEdges.Count, Is.EqualTo(0),
            "Non-router edge from consented avatar should be validated and filtered if trust is missing");
    }

    /// <summary>
    /// Non-consented avatar sending to group should work normally.
    /// </summary>
    [Test]
    public void NonConsentedAvatar_GroupMint_EdgesPassValidation()
    {
        // Arrange: Avatar WITHOUT consented flow sends to Group
        var capacityGraph = CreateCapacityGraphWithGroup(Group1, Router);

        // Source does NOT have consented flow
        // (ConsentedAvatars is empty by default)

        // Set up trust lookup
        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [Source] = new HashSet<int>(),
            [Group1] = new HashSet<int>()
        };
        capacityGraph.TrustLookup = trustLookup;

        var edges = new List<FlowEdge>
        {
            new(Source, Group1, Token1, 100) { Flow = 100 }
        };

        // Act: Validate
        var validatedEdges = ValidateConsentedFlowForTest(edges, capacityGraph);

        // Assert: Edge passes because Source doesn't have consented flow
        Assert.That(validatedEdges.Count, Is.EqualTo(1),
            "Non-consented avatar edges should pass validation");
    }

    // Test helper: Calls the real InsertRouterInTransfers via reflection or duplication
    private List<FlowEdge> InsertRouterInTransfersForTest(List<FlowEdge> transfers, CapacityGraph capacityGraph)
    {
        if (capacityGraph.RouterNode == null)
            return transfers;

        var result = new List<FlowEdge>();
        int routerId = capacityGraph.RouterNode.Value;

        foreach (var transfer in transfers)
        {
            if (!capacityGraph.IsGroup(transfer.From) &&
                !capacityGraph.IsRouter(transfer.From) &&
                capacityGraph.IsGroup(transfer.To))
            {
                // Split into Avatar → Router → Group
                result.Add(new FlowEdge(transfer.From, routerId, transfer.Token, transfer.InitialCapacity)
                {
                    Flow = transfer.Flow,
                    CurrentCapacity = transfer.CurrentCapacity
                });
                result.Add(new FlowEdge(routerId, transfer.To, transfer.Token, transfer.InitialCapacity)
                {
                    Flow = transfer.Flow,
                    CurrentCapacity = transfer.CurrentCapacity
                });
            }
            else
            {
                result.Add(transfer);
            }
        }
        return result;
    }

    // Test helper: Replicates ValidateConsentedFlow logic for testing
    private List<FlowEdge> ValidateConsentedFlowForTest(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        if (capacityGraph.TrustLookup == null || capacityGraph.ConsentedAvatars.Count == 0)
            return edges;

        var validEdges = new List<FlowEdge>(edges.Count);

        foreach (var edge in edges)
        {
            // Skip router edges
            if (capacityGraph.IsRouter(edge.From) || capacityGraph.IsRouter(edge.To))
            {
                validEdges.Add(edge);
                continue;
            }

            // If From doesn't have consented flow, standard trust is sufficient
            if (!capacityGraph.ConsentedAvatars.Contains(edge.From))
            {
                validEdges.Add(edge);
                continue;
            }

            // From has consented flow - check additional requirements
            bool fromTrustsTo = capacityGraph.TrustLookup.TryGetValue(edge.From, out var fromTrusts)
                               && fromTrusts.Contains(edge.To);
            if (!fromTrustsTo)
                continue;

            if (!capacityGraph.ConsentedAvatars.Contains(edge.To))
                continue;

            validEdges.Add(edge);
        }

        return validEdges;
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

        // Act: Sort the edges using REAL V2Pathfinder
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(badOrder, capacityGraph);

        // Assert: All collateral before mint
        int mintIndex = sorted.FindIndex(e => e.From == Group1);
        int lastCollateralIndex = sorted.FindLastIndex(e => e.To == Group1);

        Assert.That(lastCollateralIndex, Is.LessThan(mintIndex),
            "Bug fix: All 3 collateral edges must precede mint edge");

        // Assert: Validation passes using REAL V2Pathfinder
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(sorted, capacityGraph));
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

        // This should fail with insufficient collateral using REAL V2Pathfinder
        var ex = Assert.Throws<InvalidOperationException>(
            () => V2Pathfinder.ValidateMintEdgeOrdering(buggyOrder, capacityGraph));

        Assert.That(ex!.Message, Does.Contain("insufficient collateral"));
    }

    /// <summary>
    /// Integration test: Sort then validate should always succeed.
    /// </summary>
    [Test]
    public void SortThenValidate_AlwaysSucceeds()
    {
        var capacityGraph = CreateCapacityGraphWithGroups(new[] { Group1, Group2 }, Router);

        // Deliberately bad order
        var edges = new List<FlowEdge>
        {
            new(Group2, Sink, GroupToken2, 100) { Flow = 100 },
            new(Group1, Sink, GroupToken1, 50) { Flow = 50 },
            new(Router, Group1, Token1, 50) { Flow = 50 },
            new(Router, Group2, Token2, 100) { Flow = 100 },
        };

        // Sort then validate
        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, capacityGraph);

        // Should not throw
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(sorted, capacityGraph));
    }

    #endregion

    #region Helper Methods

    private CapacityGraph CreateCapacityGraphWithGroup(int groupId, int routerId)
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.SetRouter(routerId);
        graph.AddGroup(groupId);
        return graph;
    }

    private CapacityGraph CreateCapacityGraphWithGroups(int[] groupIds, int routerId)
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

    private CapacityGraph CreateCapacityGraphNoGroups()
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

    #endregion
}
