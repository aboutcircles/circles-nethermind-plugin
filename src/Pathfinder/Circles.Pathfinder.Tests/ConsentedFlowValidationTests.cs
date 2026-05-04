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
    /// Non-consented Avatar→Group edges are skipped (become Avatar→Router→Group, standard trust applies).
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_NonConsentedAvatarToGroupEdge_Skipped()
    {
        var groupId = AddressIdPool.IdOf("0xcf08consent_group");

        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Bob }, // Alice is NOT consented
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() }
            }
        );
        capacityGraph.AddGroup(groupId);

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, groupId, AliceToken, 500)
        };

        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        Assert.That(hasViolation, Is.False,
            "Non-consented Avatar→Group edges should be skipped");
    }

    /// <summary>
    /// Consented Avatar→Group edges are NOT skipped — after Router insertion,
    /// Avatar(consented)→Router fails isPermittedFlow (Router lacks advancedUsageFlags).
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_ConsentedAvatarToGroupEdge_DetectsViolation()
    {
        var groupId = AddressIdPool.IdOf("0xcf08consent_group2");

        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice },
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int>() }
            }
        );
        capacityGraph.AddGroup(groupId);

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, groupId, AliceToken, 500) // Consented→Group → will become Consented→Router → fails
        };

        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        Assert.That(hasViolation, Is.True,
            "Consented Avatar→Group should be flagged — Router lacks advancedUsageFlags");
    }

    /// <summary>
    /// Regression: consented Avatar → consented Group with mutual trust.
    /// Even though the Group is consented and trusted by the Avatar, this
    /// MUST be a violation because InsertRouterInTransfers converts this to
    /// Avatar(consented) → Router → Group. Hub.sol's isPermittedFlow(Avatar, Router, token)
    /// always fails: Router never calls setAdvancedUsageFlag.
    /// Before the fix, PathHasConsentViolation would allow this path.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_ConsentedAvatarToConsentedTrustedGroup_StillViolation()
    {
        var groupId = AddressIdPool.IdOf("0xcf10consent_group_trusted");

        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice, groupId }, // Both consented
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { groupId } }, // Alice trusts Group
                { groupId, new HashSet<int> { AliceToken } } // Group trusts Alice's token
            }
        );
        capacityGraph.AddGroup(groupId);

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, groupId, AliceToken, 500)
        };

        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        Assert.That(hasViolation, Is.True,
            "Consented Avatar→Consented Group MUST be violation — Router lacks advancedUsageFlags regardless of Group's consent status");
    }

    /// <summary>
    /// Group→Avatar (mint) edges stay as-is after InsertRouterInTransfers and
    /// must be consent-checked. Groups don't have advancedUsageFlags, so the
    /// check passes — but the edge must NOT be skipped.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_GroupToAvatarEdge_IsConsentChecked_PassesWhenGroupNotConsented()
    {
        var groupId = AddressIdPool.IdOf("0xcf09consent_group_mint");

        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice }, // Only Alice consented, not the group
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { groupId, new HashSet<int> { AliceToken } }
            }
        );
        capacityGraph.AddGroup(groupId);

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (groupId, Alice, AliceToken, 500) // Group→Avatar mint edge (stays as-is)
        };

        // Act
        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        // Assert — Group is not consented, so standard trust is sufficient → no violation
        Assert.That(hasViolation, Is.False,
            "Group→Avatar mint edge should pass: group has no advancedUsageFlags");
    }

    /// <summary>
    /// Hypothetical: if a group DID have consented flow and didn't trust the recipient,
    /// the consent check should catch it. This verifies the edge is NOT skipped.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void PathLevel_GroupToAvatarEdge_DetectsViolation_WhenGroupIsConsented()
    {
        var groupId = AddressIdPool.IdOf("0xcf10consent_group_consented");

        var capacityGraph = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { groupId }, // Group has consented flow
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                // Group does NOT trust Alice
            }
        );
        capacityGraph.AddGroup(groupId);

        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (groupId, Alice, AliceToken, 500) // Group(consented)→Avatar — group doesn't trust Alice
        };

        // Act
        bool hasViolation = PathHasConsentViolation(pathEdges, capacityGraph);

        // Assert — group is consented but doesn't trust recipient → violation detected
        Assert.That(hasViolation, Is.True,
            "Group→Avatar edge with consented group that doesn't trust recipient should be caught");
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
    /// Consented source and sink with a non-consented intermediary — path is still excluded.
    /// Pre-2026-04-28 the filter exempted endpoints, missing that the edge Alice(consented)→Bob(non-consented)
    /// itself violates Hub.sol's isPermittedFlow regardless of Bob's intermediary status.
    /// Hub.sol checks every edge: if from is consented, to must be consented.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_ConsentedEndpoints_NonConsentedHop_StillExcluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice, Carol },
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),  // consented → non-consented: rejected by Hub.sol
            (Bob, Carol, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Carol);
        Assert.That(result, Is.True,
            "Even if both endpoints are consented, a non-consented hop in between is rejected by Hub.sol " +
            "isPermittedFlow on the consented-source→non-consented-hop edge");
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
    /// Direct transfer with no consent involvement at all — path is not excluded.
    /// (Originally tested consented-source → non-consented-sink as "no intermediaries to exclude",
    /// but Hub.sol rejects that edge directly. This test now verifies the genuine no-consent case.)
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_DirectTransfer_NoConsentInvolved_NotExcluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Carol }, // Carol exists in consented set but is not on the path
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Bob);
        Assert.That(result, Is.False,
            "Direct transfer between non-consented avatars is not affected by exclusion mode");
    }

    /// <summary>
    /// Direct transfer from consented source to non-consented sink — Hub.sol rejects.
    /// Even though there are no intermediaries, the direct edge itself violates isPermittedFlow.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_DirectTransfer_ConsentedSource_NonConsentedSink_Excluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice }, // Alice is consented, Bob is not
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Bob);
        Assert.That(result, Is.True,
            "Direct edge consented → non-consented violates Hub.sol isPermittedFlow regardless of position");
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
    /// Regression for the source-via-group-mint loophole closed on 2026-04-28.
    /// Pre-fix: PathHasConsentedIntermediary exempted source/sink from the consent
    /// check, so a consented SOURCE could route through a group mint — but on-chain,
    /// after Router insertion, Avatar(consented)→Router always reverts.
    /// Post-fix: consented sender → Group is unconditionally rejected even if sender
    /// is the source vertex.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_ConsentedSource_ToGroup_StillExcluded()
    {
        var groupId = AddressIdPool.IdOf("0xcf28consent_excl_source_group");
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice }, // Alice (the source) is consented
            new Dictionary<int, HashSet<int>>());
        graph.AddGroup(groupId);

        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        // Source(consented) → Group → Sink — pre-fix this slipped through because Alice
        // was source, post-fix it's caught at the IsGroup(to) edge regardless of position.
        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, groupId, AliceToken, 500),
            (groupId, Bob, groupId, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Bob);
        Assert.That(result, Is.True,
            "Consented source → Group must be excluded even when sender is source — Avatar(consented)→Router fails on-chain");
    }

    /// <summary>
    /// Regression for the prod 2026-04-28 violation (PathfinderPathAuditViolation alert).
    /// Consented source → non-consented human intermediary slipped past the exclusion filter
    /// because the existing rules (consented avatar in intermediary slot) didn't fire when
    /// the intermediary was a regular human and the consented party was the source vertex.
    /// Hub.sol's isPermittedFlow rejects this — advancedUsageFlags[Bob] is required but unset.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_ConsentedSource_NonConsentedHuman_PathExcluded()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice }, // Only Alice (source) is consented
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),  // consented → non-consented: Hub.sol rejects
            (Bob, Carol, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Carol);
        Assert.That(result, Is.True,
            "Consented source → non-consented human intermediary must be excluded — " +
            "Hub.sol isPermittedFlow requires advancedUsageFlags[to] when from is consented");
    }

    /// <summary>
    /// Symmetric case to the prod regression: consented source → consented sink with
    /// only consented avatars in between would be valid per Hub.sol — but the existing
    /// "exclude-intermediaries" mode is intentionally pessimistic and drops ALL paths that
    /// touch consented avatars in non-endpoint positions. Documents that behavior.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentedIntermediary_AllConsentedChain_StillExcludedInPessimisticMode()
    {
        var graph = CreateCapacityGraph(
            new HashSet<int> { Alice, Bob, Carol }, // entire chain consented
            new Dictionary<int, HashSet<int>>());
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = true });

        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Bob, AliceToken, 500),
            (Bob, Carol, AliceToken, 500)
        };

        bool result = pathfinder.PathHasConsentedIntermediary(edges, graph, Alice, Carol);
        Assert.That(result, Is.True,
            "Exclude-intermediaries mode is intentionally conservative — " +
            "paths through consented intermediaries are dropped even if all-consented and Hub-valid");
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

    #region Consent Flag Removal

    /// <summary>
    /// Verifies that removing the consented flag (avatar calls setAdvancedUsageFlag(0))
    /// correctly transitions behavior: with consent → violation, without → no violation.
    /// Simulates the SQL query returning flag=0 (latest state) for a previously consented avatar.
    /// </summary>
    [Test]
    [Category("consented-flow")]
    public void ConsentFlagRemoval_SameAvatar_TransitionsBehavior()
    {
        // Phase 1: Alice has consent enabled
        var graphWithConsent = CreateCapacityGraph(
            consentedAvatars: new HashSet<int> { Alice },
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } },
                { Bob, new HashSet<int> { AliceToken } }
            }
        );

        // Alice→Carol: Carol is NOT consented → should be violation
        var pathEdges = new List<(int From, int To, int Token, long Flow)>
        {
            (Alice, Carol, AliceToken, 500)
        };

        bool hasViolationWithConsent = PathHasConsentViolation(pathEdges, graphWithConsent);
        Assert.That(hasViolationWithConsent, Is.True,
            "With consent enabled, Alice→Carol should be a violation (Carol not consented)");

        // Phase 2: Alice removes consent (setAdvancedUsageFlag(0))
        // SQL DISTINCT ON returns flag=0 → Alice NOT in consentedAvatars
        var graphWithoutConsent = CreateCapacityGraph(
            consentedAvatars: new HashSet<int>(), // Alice no longer consented
            trustLookup: new Dictionary<int, HashSet<int>>
            {
                { Alice, new HashSet<int> { Bob } },
                { Bob, new HashSet<int> { AliceToken } }
            }
        );

        bool hasViolationWithoutConsent = PathHasConsentViolation(pathEdges, graphWithoutConsent);
        Assert.That(hasViolationWithoutConsent, Is.False,
            "With consent removed, Alice→Carol should NOT be a violation (standard trust applies)");
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

    // Delegate to production V2Pathfinder method (internal, accessible via InternalsVisibleTo)
    // to avoid logic drift between test copies and production code.
    private static readonly V2Pathfinder _consentValidator = new();

    private static bool PathHasConsentViolation(
        List<(int From, int To, int Token, long Flow)> collapsedEdges,
        CapacityGraph capacityGraph)
        => _consentValidator.PathHasConsentViolation(collapsedEdges, capacityGraph);

    #endregion
}
