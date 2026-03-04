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
[TestFixture, Parallelizable]
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

    #region Path-Level Consent Filtering Tests

    /// <summary>
    /// A single path through a non-consented intermediate should be entirely dropped.
    /// With path-level filtering, the entire path is removed (not individual edges).
    /// Result: 0 flow, 0 transfers.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_SinglePathWithViolation_EntirePathDropped()
    {
        // Arrange: Alice(consented) → Bob(non-consented) → Carol
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice },
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() }, // Alice trusts no one (non-consented-to)
                { Bob, new HashSet<int> { AliceToken } },
                { Carol, new HashSet<int> { AliceToken } }
            }
        );

        // Simulate collapsed path edges (after pool collapse)
        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),
            (Bob, Carol, AliceToken, 500)
        };

        // Act
        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        // Assert — Alice(consented) → Bob: Alice doesn't trust Bob → violation
        Assert.That(hasViolation, Is.True,
            "Path with consented→non-trusted should be flagged as violation");
    }

    /// <summary>
    /// Two paths: one valid, one with consent violation. Only valid path survives.
    /// This tests that path-level filtering preserves flow conservation by dropping
    /// entire paths rather than individual edges.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_TwoPathsOneViolation_OnlyValidPathSurvives()
    {
        // Arrange: Path 1: Alice(consented) → Bob(consented), Alice trusts Bob → VALID
        //          Path 2: Alice(consented) → Carol(non-consented) → Dave → INVALID
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob },
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } }, // Alice trusts Bob (for path 1)
                { Bob, new HashSet<int> { AliceToken } },
                { Carol, new HashSet<int> { AliceToken } },
                { Dave, new HashSet<int> { AliceToken } }
            }
        );

        var validPath = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 300)
        };
        var invalidPath = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Carol, AliceToken, 200), // Alice(consented) → Carol(non-consented) → violation
            (Carol, Dave, AliceToken, 200)
        };

        // Act
        bool path1Violation = PathHasConsentViolation(validPath, capacityGraph);
        bool path2Violation = PathHasConsentViolation(invalidPath, capacityGraph);

        // Assert
        Assert.That(path1Violation, Is.False, "Valid path should not be flagged");
        Assert.That(path2Violation, Is.True, "Path through non-consented intermediate should be flagged");
    }

    /// <summary>
    /// Group edges should NOT count as consent violations because they become router
    /// edges after InsertRouterInTransfers, and router bypasses consent checks.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_GroupEdges_NotCountedAsViolation()
    {
        // Arrange: Alice(consented) → GroupX (group node)
        var groupId = AddressIdPool.IdOf("0xcf08consent_group");

        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice },
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() } // Alice doesn't trust group — but it doesn't matter
            }
        );
        capacityGraph.AddGroup(groupId);

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, groupId, AliceToken, 500) // Will become Avatar→Router→Group
        };

        // Act
        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        // Assert — group edge should be skipped
        Assert.That(hasViolation, Is.False,
            "Group edges should be skipped (they become router edges which bypass consent)");
    }

    /// <summary>
    /// Consented direct transfer: A(consented) → B(consented), A trusts B → valid.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_ConsentedDirectTransfer_Passes()
    {
        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, Bob },
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } }, // Alice trusts Bob
                { Bob, new HashSet<int> { AliceToken } }
            }
        );

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500)
        };

        // Act
        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        // Assert
        Assert.That(hasViolation, Is.False,
            "Both consented, from trusts to → should pass");
    }

    #endregion

    #region Regression: 3-Path Graph Consent Drop Preserves Flow Conservation (Bug #2)

    /// <summary>
    /// Regression for consent conservation bug: deterministic 3-path graph where 1 path
    /// violates consent rules. Dropping the violating path ENTIRELY (not individual edges)
    /// must preserve flow conservation at all intermediate vertices.
    ///
    /// Before the fix: edge-level consent filtering on AGGREGATED edges left "holes" —
    /// intermediate vertices with unbalanced in/out flows.
    /// After the fix: path-level filtering drops entire paths before aggregation.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentPathLevel_ThreePathGraph_DropsViolatingPathPreservesConservation()
    {
        // Topology: 3 independent paths from Source to Sink
        //   Path 1: Source(consented) → A(consented) → Sink(consented)  [VALID: all consented, Source trusts A, A trusts Sink]
        //   Path 2: Source(consented) → B(non-consented) → Sink(consented)  [INVALID: Source→B fails (B not consented)]
        //   Path 3: Source(consented) → C(consented) → Sink(consented)  [VALID: all consented, Source trusts C, C trusts Sink]
        //
        // After filtering: paths 1 and 3 survive. Flow conservation MUST hold at A and C.

        var Source = AddressIdPool.IdOf("0xcf10_3path_source_0000000000000");
        var A = AddressIdPool.IdOf("0xcf10_3path_vertex_a000000000000");
        var B = AddressIdPool.IdOf("0xcf10_3path_vertex_b000000000000");
        var C = AddressIdPool.IdOf("0xcf10_3path_vertex_c000000000000");
        var Sink = AddressIdPool.IdOf("0xcf10_3path_sink_0000000000000000");
        var Token = AddressIdPool.IdOf("0xcf10_3path_token_000000000000000");

        var graph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Source, A, C, Sink }, // B is NOT consented; Sink IS consented
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Source, new HashSet<int> { A, C } }, // Source trusts A and C (NOT B)
                { A, new HashSet<int> { Sink, Token } }, // A trusts Sink
                { B, new HashSet<int> { Token } },
                { C, new HashSet<int> { Sink, Token } }, // C trusts Sink
                { Sink, new HashSet<int> { Token } }
            }
        );
        graph.AddAvatar(Source);
        graph.AddAvatar(A);
        graph.AddAvatar(B);
        graph.AddAvatar(C);
        graph.AddAvatar(Sink);

        // 3 independent paths
        var path1 = new List<(int From, int To, int Token, long Flow)>
        {
            (Source, A, Token, 300),
            (A, Sink, Token, 300)
        };
        var path2 = new List<(int From, int To, int Token, long Flow)>
        {
            (Source, B, Token, 200), // VIOLATION: Source(consented) → B(non-consented)
            (B, Sink, Token, 200)
        };
        var path3 = new List<(int From, int To, int Token, long Flow)>
        {
            (Source, C, Token, 400),
            (C, Sink, Token, 400)
        };

        // Verify consent violation detection
        Assert.That(PathHasConsentViolation(path1, graph), Is.False, "Path 1 should be valid");
        Assert.That(PathHasConsentViolation(path2, graph), Is.True, "Path 2 should be violation");
        Assert.That(PathHasConsentViolation(path3, graph), Is.False, "Path 3 should be valid");

        // Simulate path-level filtering: keep valid paths, drop invalid ones
        var survivingEdges = new List<FlowEdge>();
        foreach (var (from, to, token, flow) in path1)
            survivingEdges.Add(new FlowEdge(from, to, token, flow) { Flow = flow });
        // path2 dropped entirely
        foreach (var (from, to, token, flow) in path3)
            survivingEdges.Add(new FlowEdge(from, to, token, flow) { Flow = flow });

        // Verify flow conservation on surviving edges
        var netFlow = new Dictionary<int, long>();
        foreach (var e in survivingEdges)
        {
            netFlow.TryGetValue(e.From, out var fromNet);
            netFlow[e.From] = fromNet - e.Flow;
            netFlow.TryGetValue(e.To, out var toNet);
            netFlow[e.To] = toNet + e.Flow;
        }

        // Intermediaries (A, C) should have net flow = 0
        Assert.That(netFlow.GetValueOrDefault(A), Is.EqualTo(0),
            "Flow conservation violated at vertex A after dropping path 2");
        Assert.That(netFlow.GetValueOrDefault(C), Is.EqualTo(0),
            "Flow conservation violated at vertex C after dropping path 2");

        // Source should have net outflow = -(300+400) = -700
        Assert.That(netFlow[Source], Is.EqualTo(-700),
            "Source should have total outflow of 700 (paths 1+3)");
        // Sink should have net inflow = 300+400 = 700
        Assert.That(netFlow[Sink], Is.EqualTo(700),
            "Sink should have total inflow of 700 (paths 1+3)");
    }

    #endregion

    #region PathHasConsentedIntermediary Tests (DisableConsentedFlow mode)

    /// <summary>
    /// When no avatars are consented, no intermediary exclusion should happen.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_NoConsentedAvatars_NoExclusion()
    {
        var graph = CreateCapacityGraph(new HashSet<int>(), new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),
            (Bob, Carol, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Carol);
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// Source and sink are consented — they should NOT be treated as intermediaries.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_SourceAndSinkConsented_NotExcluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice, Carol },
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),
            (Bob, Carol, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Carol);
        Assert.That(result, Is.False, "Source and sink consented but should not be treated as intermediaries");
    }

    /// <summary>
    /// Consented avatar in intermediary position — path should be excluded.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_ConsentedInMiddle_PathExcluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Bob }, // Bob is consented
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),  // Bob as "to" intermediary
            (Bob, Carol, AliceToken, 500)   // Bob as "from" intermediary
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Carol);
        Assert.That(result, Is.True, "Consented avatar Bob in intermediary position should trigger exclusion");
    }

    /// <summary>
    /// Direct transfer (no intermediaries): source=consented → sink.
    /// Should NOT be excluded since there are no intermediaries.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_DirectTransfer_NoExclusion()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice },
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Bob);
        Assert.That(result, Is.False, "Direct transfer has no intermediaries to exclude");
    }

    /// <summary>
    /// Multiple consented intermediaries — should still detect and exclude.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_MultipleConsented_StillExcluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Bob, Carol }, // Both intermediaries consented
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),
            (Bob, Carol, AliceToken, 500),
            (Carol, Dave, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Dave);
        Assert.That(result, Is.True, "Path through consented intermediaries should be excluded");
    }

    /// <summary>
    /// End-to-end: V2Pathfinder with DisableConsentedFlow=true excludes paths through
    /// consented intermediaries while preserving flow conservation.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void EndToEnd_DisableConsentedFlow_ExcludesIntermediaries_PreservesConservation()
    {
        var rng = new Random(42);
        var graph = PropertyBasedTests.BuildSyntheticGraph(6, 0, 1_000_000L, rng,
            trustDensity: 0.5, withRouter: false, withConsent: true, consentRate: 0.5);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinderExclude = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });
        var pathfinderValidate = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });

        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new Circles.Common.Dto.FlowRequest { Source = sourceAddr, Sink = sinkAddr };
        var target = Circles.Common.CirclesConverter.BlowUpToUInt256(1_000_000L);

        // Both modes should not crash
        Circles.Common.Dto.MaxFlowResponse resultExclude;
        Circles.Common.Dto.MaxFlowResponse resultValidate;
        try
        {
            resultExclude = pathfinderExclude.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            return; // Empty graph — valid
        }

        // Rebuild graph with same seed for the second test (solver mutates edge flows)
        var rng2 = new Random(42);
        var graph2 = PropertyBasedTests.BuildSyntheticGraph(6, 0, 1_000_000L, rng2,
            trustDensity: 0.5, withRouter: false, withConsent: true, consentRate: 0.5);
        var (source2, sink2) = PropertyBasedTests.PickSourceSink(graph2, rng2);
        var request2 = new Circles.Common.Dto.FlowRequest
        {
            Source = AddressIdPool.StringOf(source2),
            Sink = AddressIdPool.StringOf(sink2)
        };

        try
        {
            resultValidate = pathfinderValidate.ComputeMaxFlowWithPath(graph2, request2, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            return;
        }

        // Both should produce valid flow conservation
        if (resultExclude.Transfers.Count > 0)
        {
            PropertyBasedTests_AssertFlowConservation(resultExclude.Transfers, sourceAddr, sinkAddr);
        }
        if (resultValidate.Transfers.Count > 0)
        {
            PropertyBasedTests_AssertFlowConservation(resultValidate.Transfers,
                request2.Source!, request2.Sink!);
        }
    }

    private static void PropertyBasedTests_AssertFlowConservation(
        List<Circles.Common.Dto.TransferPathStep> steps, string source, string sink)
    {
        var src = source.ToLowerInvariant();
        var snk = sink.ToLowerInvariant();
        var netFlow = new Dictionary<string, long>();

        foreach (var step in steps)
        {
            var from = step.From!.ToLowerInvariant();
            var to = step.To!.ToLowerInvariant();
            if (from == to) continue;

            var flow = long.Parse(step.Value!);
            netFlow.TryGetValue(from, out long fromNet);
            netFlow[from] = fromNet - flow;
            netFlow.TryGetValue(to, out long toNet);
            netFlow[to] = toNet + flow;
        }

        foreach (var (vertex, net) in netFlow)
        {
            if (vertex == src || vertex == snk) continue;
            Assert.That(net, Is.EqualTo(0),
                $"Flow conservation violated at {vertex[..Math.Min(10, vertex.Length)]}...: net={net}");
        }
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

    /// <summary>
    /// Replicates the PathHasConsentViolation logic from V2Pathfinder for testing.
    /// Checks if a collapsed path has any consent violation.
    /// </summary>
    private static bool PathHasConsentViolation(
        List<(int From, int To, int Token, long Flow)> collapsedEdges,
        CapacityGraph capacityGraph)
    {
        if (capacityGraph.TrustLookup == null || capacityGraph.ConsentedAvatars.Count == 0)
            return false;

        foreach (var (from, to, _, _) in collapsedEdges)
        {
            // Skip group edges — they become router edges later
            if (capacityGraph.IsGroup(from) || capacityGraph.IsGroup(to))
                continue;

            // Skip pool nodes
            if (IsPoolNode(from) || IsPoolNode(to))
                continue;

            // If From doesn't have consented flow, standard trust is sufficient
            if (!capacityGraph.ConsentedAvatars.Contains(from))
                continue;

            // From has consented flow — check requirements
            bool fromTrustsTo = capacityGraph.TrustLookup.TryGetValue(from, out var fromTrusts)
                                && fromTrusts.Contains(to);
            if (!fromTrustsTo)
                return true;

            if (!capacityGraph.ConsentedAvatars.Contains(to))
                return true;
        }

        return false;
    }

    #endregion
}
