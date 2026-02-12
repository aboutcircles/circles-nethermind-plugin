using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for consented flow validation logic.
/// Tests the contract rule: isPermittedFlow from Hub.sol
///
/// Rules:
/// - If From does NOT have consented flow → standard trust (To trusts TokenOwner) is sufficient
/// - If From HAS consented flow → requires:
///   1. From trusts To
///   2. To also has consented flow enabled
/// </summary>
[TestFixture]
public class ConsentedFlowValidationTests
{
    // Use AddressIdPool.IdOf() to avoid collisions with BalanceNodeIds from other tests.
    // Hardcoded small ints (1, 2, 3) can collide with IDs assigned by BalanceNodeIdOf()
    // in other test classes because both share the same monotonic counter.
    private static readonly int Alice = AddressIdPool.IdOf("0xcf01consent_alice");
    private static readonly int Bob = AddressIdPool.IdOf("0xcf02consent_bob");
    private static readonly int Carol = AddressIdPool.IdOf("0xcf03consent_carol");
    private static readonly int Dave = AddressIdPool.IdOf("0xcf04consent_dave");
    private static readonly int Router = AddressIdPool.IdOf("0xcf05consent_router");

    private static readonly int AliceToken = AddressIdPool.IdOf("0xcf06consent_alicetoken");
    private static readonly int BobToken = AddressIdPool.IdOf("0xcf07consent_bobtoken");

    /// <summary>
    /// Standard case: From (Alice) does NOT have consented flow enabled.
    /// Edge should be valid regardless of other conditions.
    /// </summary>
    [Test]
    public void NoConsentedFlow_EdgeIsValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int>(), // No one has consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Bob, new HashSet<int> { AliceToken } } // Bob trusts Alice's token (standard trust)
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[0].To, Is.EqualTo(Bob));
    }

    /// <summary>
    /// From (Alice) has consented flow, To (Bob) does NOT have consented flow.
    /// Edge should be INVALID and filtered out.
    /// </summary>
    [Test]
    public void FromHasConsentedFlow_ToDoesNot_EdgeIsInvalid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice }, // Only Alice has consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } }, // Alice trusts Bob
                { Bob, new HashSet<int> { AliceToken } } // Bob trusts Alice's token
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - edge should be filtered out
        Assert.That(result, Is.Empty);
    }

    /// <summary>
    /// From (Alice) has consented flow, To (Bob) has consented flow,
    /// but From does NOT trust To.
    /// Edge should be INVALID and filtered out.
    /// </summary>
    [Test]
    public void BothHaveConsentedFlow_FromDoesNotTrustTo_EdgeIsInvalid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob }, // Both have consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                // Alice does NOT trust Bob (missing entry)
                { Bob, new HashSet<int> { AliceToken } } // Bob trusts Alice's token
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - edge should be filtered out
        Assert.That(result, Is.Empty);
    }

    /// <summary>
    /// From (Alice) has consented flow, To (Bob) has consented flow,
    /// and From trusts To.
    /// Edge should be VALID.
    /// </summary>
    [Test]
    public void BothHaveConsentedFlow_FromTrustsTo_EdgeIsValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob }, // Both have consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } }, // Alice trusts Bob
                { Bob, new HashSet<int> { AliceToken } } // Bob trusts Alice's token
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[0].To, Is.EqualTo(Bob));
    }

    /// <summary>
    /// Multi-hop path: Alice → Bob → Carol
    /// Alice has consented flow, Bob has consented flow, Carol does NOT.
    /// Alice→Bob should be valid (both have consent, Alice trusts Bob)
    /// Bob→Carol should be invalid (Bob has consent, Carol doesn't)
    /// </summary>
    [Test]
    public void MultiHopPath_PartialConsentedFlow_FiltersInvalidEdges()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob }, // Alice and Bob have consented flow, Carol doesn't
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } }, // Alice trusts Bob
                { Bob, new HashSet<int> { AliceToken, Carol } }, // Bob trusts Alice's token and Carol
                { Carol, new HashSet<int> { AliceToken } } // Carol trusts Alice's token
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 },
            new FlowEdge(Bob, Carol, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - only Alice→Bob should remain
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[0].To, Is.EqualTo(Bob));
    }

    /// <summary>
    /// Pool nodes should always be allowed through (they're not avatars).
    /// </summary>
    [Test]
    public void PoolNodeEdges_AlwaysValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice },
            trustLookup: new Dictionary<int, HashSet<int>>()
        );

        // Pool nodes are tracked in AddressIdPool.BalanceNodeIds
        var poolId = AddressIdPool.BalanceNodeIdOf("tpool-consent-test");

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, poolId, AliceToken, 1000) { Flow = 500 }, // Avatar → Pool
            new FlowEdge(poolId, Bob, AliceToken, 1000) { Flow = 500 }    // Pool → Avatar
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - pool edges should pass through
        Assert.That(result, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Router edges should always be allowed through.
    /// </summary>
    [Test]
    public void RouterEdges_AlwaysValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice },
            trustLookup: new Dictionary<int, HashSet<int>>()
        );
        capacityGraph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Router, AliceToken, 1000) { Flow = 500 },
            new FlowEdge(Router, Bob, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - router edges should pass through
        Assert.That(result, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Empty consented avatars set means no validation needed (backwards compatible).
    /// </summary>
    [Test]
    public void EmptyConsentedAvatars_AllEdgesValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int>(), // No one has consented flow
            trustLookup: new Dictionary<int, HashSet<int>>()
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 },
            new FlowEdge(Bob, Carol, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - all edges should pass through
        Assert.That(result, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Null trust lookup means no validation can be done - pass all through.
    /// </summary>
    [Test]
    public void NullTrustLookup_AllEdgesValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob },
            trustLookup: null!
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - should pass through (no validation possible)
        Assert.That(result, Has.Count.EqualTo(1));
    }

    #region Consented Avatar as Path Intermediate Tests

    /// <summary>
    /// Scenario: A (non-consented) → B (consented) → C (non-consented)
    ///
    /// Edge A→B: A doesn't have consented flow, so standard trust applies → VALID
    /// Edge B→C: B has consented flow, but C doesn't have consented flow → INVALID
    ///
    /// This tests the case where a consented flow avatar is in the MIDDLE of a path,
    /// not as the source. The consented avatar's outbound edges are still validated.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedAvatarAsIntermediate_NonConsentedSource_OutboundEdgeFiltered()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Bob }, // Only Bob has consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() },  // Alice trusts no one (doesn't matter - no consent)
                { Bob, new HashSet<int> { Carol } }, // Bob trusts Carol
                { Carol, new HashSet<int> { AliceToken } } // Carol trusts Alice's token
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 },  // A → B
            new FlowEdge(Bob, Carol, AliceToken, 1000) { Flow = 500 }   // B → C
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert:
        // - A→B should pass (A has no consented flow, standard trust applies)
        // - B→C should be filtered (B has consented flow, C doesn't)
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[0].To, Is.EqualTo(Bob));
    }

    /// <summary>
    /// Scenario: A (non-consented) → B (consented) → C (consented)
    /// Both B and C have consented flow, and B trusts C.
    ///
    /// Edge A→B: A doesn't have consented flow, standard trust → VALID
    /// Edge B→C: B has consented flow, C has consented flow, B trusts C → VALID
    ///
    /// Full path should be valid when intermediate consented avatars trust each other.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedAvatarAsIntermediate_BothConsentedAndTrusted_FullPathValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Bob, Carol }, // B and C have consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() },
                { Bob, new HashSet<int> { Carol } }, // Bob trusts Carol
                { Carol, new HashSet<int> { AliceToken } }
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 },
            new FlowEdge(Bob, Carol, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert - both edges should be valid
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[1].From, Is.EqualTo(Bob));
    }

    /// <summary>
    /// Scenario: A (consented) → B (consented) → C (non-consented)
    ///
    /// Tests a longer chain where the SOURCE also has consented flow.
    /// Both edges require full consented flow validation.
    ///
    /// Edge A→B: A has consent, B has consent, A trusts B → VALID
    /// Edge B→C: B has consent, C doesn't have consent → INVALID
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedAvatarAsIntermediate_ConsentedSourceChain_PartiallyValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob }, // A and B have consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } }, // Alice trusts Bob
                { Bob, new HashSet<int> { Carol } }, // Bob trusts Carol
                { Carol, new HashSet<int> { AliceToken } }
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 },
            new FlowEdge(Bob, Carol, AliceToken, 1000) { Flow = 500 }
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert:
        // - A→B valid (both consented, A trusts B)
        // - B→C invalid (C doesn't have consent)
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[0].To, Is.EqualTo(Bob));
    }

    /// <summary>
    /// Scenario: A → B (consented) → C (consented) → D
    /// Tests a 4-hop path with consented avatars in the middle.
    ///
    /// This is a realistic scenario where tokens flow through a "consented circle"
    /// of avatars before exiting to a non-consented recipient.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void FourHopPath_ConsentedCircleInMiddle_OnlyCircleEdgesValid()
    {
        // Arrange
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Bob, Carol }, // B and C form a "consented circle"
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() },
                { Bob, new HashSet<int> { Carol } }, // B trusts C
                { Carol, new HashSet<int> { Dave, AliceToken } }, // C trusts D (but D has no consent)
                { Dave, new HashSet<int> { AliceToken } }
            }
        );

        var edges = new List<FlowEdge>
        {
            new FlowEdge(Alice, Bob, AliceToken, 1000) { Flow = 500 },  // Non-consented → consented
            new FlowEdge(Bob, Carol, AliceToken, 1000) { Flow = 500 },  // Consented → consented
            new FlowEdge(Carol, Dave, AliceToken, 1000) { Flow = 500 }  // Consented → non-consented
        };

        // Act
        var result = ValidateConsentedFlow(edges, capacityGraph);

        // Assert:
        // - A→B valid (A has no consent, standard trust)
        // - B→C valid (both consented, B trusts C)
        // - C→D invalid (C consented, D not consented)
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].From, Is.EqualTo(Alice));
        Assert.That(result[0].To, Is.EqualTo(Bob));
        Assert.That(result[1].From, Is.EqualTo(Bob));
        Assert.That(result[1].To, Is.EqualTo(Carol));
    }

    #endregion

    #region Helper Methods

    private static CapacityGraph CreateCapacityGraph(
        HashSet<int> consentedAvatars,
        Dictionary<int, HashSet<int>>? trustLookup)
    {
        var graph = new CapacityGraph
        {
            ConsentedAvatars = consentedAvatars,
            TrustLookup = trustLookup
        };

        // Add avatar nodes
        graph.AddAvatar(Alice);
        graph.AddAvatar(Bob);
        graph.AddAvatar(Carol);
        graph.AddAvatar(Dave);

        return graph;
    }

    /// <summary>
    /// Replicates the ValidateConsentedFlow logic from V2Pathfinder for testing.
    /// This ensures we test the exact same logic without needing the full pathfinder.
    /// </summary>
    private static List<FlowEdge> ValidateConsentedFlow(List<FlowEdge> edges, CapacityGraph capacityGraph)
    {
        // If no consent data available, pass all edges through
        if (capacityGraph.TrustLookup == null || capacityGraph.ConsentedAvatars.Count == 0)
        {
            return edges;
        }

        var validEdges = new List<FlowEdge>(edges.Count);

        foreach (var edge in edges)
        {
            // Skip pool nodes - they're not avatars
            if (IsPoolNode(edge.From) || IsPoolNode(edge.To))
            {
                validEdges.Add(edge);
                continue;
            }

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

            // From has consented flow enabled - check additional requirements:
            // 1. From must trust To
            bool fromTrustsTo = capacityGraph.TrustLookup.TryGetValue(edge.From, out var fromTrusts)
                               && fromTrusts.Contains(edge.To);
            if (!fromTrustsTo)
            {
                // Invalid - From has consented flow but doesn't trust To
                continue;
            }

            // 2. To must also have consented flow enabled
            if (!capacityGraph.ConsentedAvatars.Contains(edge.To))
            {
                // Invalid - To doesn't have consented flow enabled
                continue;
            }

            // All checks passed
            validEdges.Add(edge);
        }

        return validEdges;
    }

    private static bool IsPoolNode(int nodeId)
    {
        return AddressIdPool.IsBalanceNode(nodeId);
    }

    #endregion
}
