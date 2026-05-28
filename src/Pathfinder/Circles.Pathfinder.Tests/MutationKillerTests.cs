using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Adversarial tests designed to kill specific surviving mutants.
/// Each test targets a precise condition that, if mutated (negated, boundary-shifted,
/// operator-swapped), would cause observable failure.
///
/// Naming convention: Kill_{File}_{Condition}_{MutationType}
/// </summary>
[TestFixture, Parallelizable]
public class MutationKillerTests
{
    // Valid 42-char hex addresses
    private static readonly string RouterAddr = "0xf2ff000000000000000000000000000000000001";
    private static readonly string Alice = "0xf2aa000000000000000000000000000000000002";
    private static readonly string Bob = "0xf2bb000000000000000000000000000000000003";
    private static readonly string Carol = "0xf2cc000000000000000000000000000000000004";
    private static readonly string TokenA = "0xf20a000000000000000000000000000000000006";
    private static readonly string TokenB = "0xf20b000000000000000000000000000000000007";
    private static readonly string TokenC = "0xf20c000000000000000000000000000000000008";
    private static readonly string GroupG = "0xf299000000000000000000000000000000000009";

    #region Helpers

    private static GraphFactory MakeFactory(InnerMockLoadGraph? mock = null)
    {
        mock ??= new InnerMockLoadGraph();
        return new GraphFactory(RouterAddr, mock);
    }

    private static BalanceGraph MakeBalanceGraph(params (string Holder, string Token, long Amount)[] entries)
    {
        var bg = new BalanceGraph();
        foreach (var (h, t, amt) in entries)
        {
            int hId = AddressIdPool.IdOf(h);
            int tId = AddressIdPool.IdOf(t);
            if (!bg.AvatarNodes.ContainsKey(hId))
                bg.AddAvatar(hId);
            bg.AddBalance(hId, tId, amt, isWrapped: false, isStatic: false);
        }
        return bg;
    }

    private static BalanceGraph MakeBalanceGraphEx(
        params (string Holder, string Token, long Amount, bool IsWrapped, bool IsStatic)[] entries)
    {
        var bg = new BalanceGraph();
        foreach (var (h, t, amt, wrapped, isStatic) in entries)
        {
            int hId = AddressIdPool.IdOf(h);
            int tId = AddressIdPool.IdOf(t);
            if (!bg.AvatarNodes.ContainsKey(hId))
                bg.AddAvatar(hId);
            bg.AddBalance(hId, tId, amt, isWrapped: wrapped, isStatic: isStatic);
        }
        return bg;
    }

    private static Dictionary<int, HashSet<int>> MakeTrust(params (string Truster, string Trustee)[] edges)
    {
        var dict = new Dictionary<int, HashSet<int>>();
        foreach (var (truster, trustee) in edges)
        {
            int terId = AddressIdPool.IdOf(truster);
            int teeId = AddressIdPool.IdOf(trustee);
            if (!dict.TryGetValue(terId, out var set))
            {
                set = new HashSet<int>();
                dict[terId] = set;
            }
            set.Add(teeId);
        }
        return dict;
    }

    #endregion

    // ════════════════════════════════════════════════════════════════════════
    // MK-01: FromTokensFilter negation — verify excluded tokens are ABSENT
    //
    // Target: GraphFactory.cs line 601
    //   if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(bn.Token)) continue;
    // Mutant: remove the `!` → keeps ONLY non-matching tokens (inverts filter)
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_FromTokensNegation_VerifyIncludedAndExcluded()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Alice, TokenB, 200));
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            FromTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Positive assertion: TokenA edge from source MUST exist
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA && e.Token == tokenAId), Is.True,
            "TokenA should be INCLUDED (it's in fromTokens)");

        // Negative assertion: TokenB edge from source MUST NOT exist
        // If `!` is removed from the condition, TokenB would be included and TokenA excluded
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB && e.Token == tokenBId), Is.False,
            "TokenB should be EXCLUDED (it's not in fromTokens)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-02: Swap mode source→pool exclusion
    //
    // Target: GraphFactory.cs line 600
    //   if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && toTokensFilter.Contains(bn.Token)) continue;
    // Mutant: remove `sourceEqualsSink` → also excludes in non-swap mode
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_SwapModeCheck_OnlyExcludesWhenSourceEqualsSink()
    {
        var factory = MakeFactory();
        // Alice holds TokenA; Bob trusts TokenA
        var balance = MakeBalanceGraph((Alice, TokenA, 500));
        var trust = MakeTrust((Bob, TokenA));

        // NON-swap mode: source != sink, but toTokens is set
        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);

        // In non-swap mode, Alice's TokenA balance MUST create an edge (not excluded)
        // If `sourceEqualsSink` check is removed, this edge would be wrongly excluded
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA && e.Token == tokenAId), Is.True,
            "Source's toTokens balance must NOT be excluded when source != sink");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-03: Wrapped token non-source exclusion
    //
    // Target: GraphFactory.cs line 604
    //   if (bn.IsWrapped && !isSource) continue;
    // Mutant: remove `!isSource` → excludes wrapped tokens even for source
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_WrappedNonSource_SourceIncluded_OthersExcluded()
    {
        var factory = MakeFactory();
        // Alice (source) holds wrapped TokenA; Bob (non-source) also holds wrapped TokenA
        var bg = MakeBalanceGraphEx(
            (Alice, TokenA, 300, true, false),
            (Bob, TokenA, 200, true, false));
        var trust = MakeTrust((Carol, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Carol,
            WithWrap = true // Enable wrapped token support
        };

        var cg = factory.CreateCapacityGraph(bg, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int bobId = AddressIdPool.IdOf(Bob);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);

        // Alice (source) with wrapped token MUST have edge
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.True,
            "Source's wrapped balance should be included when withWrap=true");

        // Bob (non-source) with wrapped token MUST NOT have edge
        // If `!isSource` is removed from condition, Bob's wrapped edge would also appear
        Assert.That(cg.Edges.Any(e => e.From == bobId && e.To == poolA), Is.False,
            "Non-source's wrapped balance must be excluded even with withWrap=true");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-04: Sink toTokensFilter — exact token verification at sink
    //
    // Target: GraphFactory.cs line 674
    //   if (isSink && toTokensFilter.Count > 0 && !toTokensFilter.Contains(t)) continue;
    // Mutant: remove negation → only allows non-matching tokens
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_SinkToTokensFilter_NegationFlip()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Alice, TokenB, 100));
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Pool(A)→Bob: TokenA IS in toTokens → MUST exist
        Assert.That(cg.Edges.Any(e => e.From == poolA && e.To == bobId && e.Token == tokenAId), Is.True,
            "TokenA pool→sink should exist (in toTokens)");

        // Pool(B)→Bob: TokenB is NOT in toTokens → MUST NOT exist
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == bobId && e.Token == tokenBId), Is.False,
            "TokenB pool→sink should be filtered (not in toTokens)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-05: excludedToTokensFilter at sink
    //
    // Target: GraphFactory.cs line 675
    //   if (isSink && excludedToTokensFilter.Count > 0 && excludedToTokensFilter.Contains(t)) continue;
    // Mutant: remove Contains check → excludes all tokens when filter is non-empty
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_ExcludedToTokens_OnlyExcludesSpecifiedToken()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Alice, TokenB, 100));
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ExcludedToTokens = new List<string> { TokenB }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // TokenA is NOT excluded → pool→sink edge MUST exist
        Assert.That(cg.Edges.Any(e => e.From == poolA && e.To == bobId && e.Token == tokenAId), Is.True,
            "TokenA should pass through (not in excludedToTokens)");

        // TokenB IS excluded → pool→sink edge MUST NOT exist
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == bobId && e.Token == tokenBId), Is.False,
            "TokenB should be excluded from sink edges");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-06: Virtual sink pruning when no edges reach it
    //
    // Target: GraphFactory.cs lines 371-377
    //   if (!anyVirtualSinkEdgesAdded) { ... remove virtual sink ... }
    // Mutant: remove the pruning → orphaned virtual sink stays in graph
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_VirtualSinkPruning_NoEdges_RemovedFromGraph()
    {
        var factory = MakeFactory();
        // Source==sink with toTokens filter pointing at a token nobody holds
        // The virtual sink will be created, but no edges will reach it
        var balance = MakeBalanceGraph((Alice, TokenA, 500));
        // Alice trusts TokenB (needed for virtual sink creation) but nobody has TokenB
        var trust = MakeTrust((Alice, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { TokenB }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        // Virtual sink should be pruned — no edge delivers TokenB (nobody holds it)
        Assert.That(cg.VirtualSinkAddress, Is.Null,
            "Virtual sink should be pruned when no edges reach it");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-07: QuantizedMinBalance exact boundary — >= vs >
    //
    // Target: GraphFactory.cs line 267
    //   if (sinkTrusts.Contains(balanceNode.Token) && balanceNode.Amount >= QuantizedMinBalance)
    // Mutant: >= → > excludes exactly-96-CRC balances
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_QuantizedMinBalance_ExactBoundaryIncluded()
    {
        var factory = MakeFactory();
        const long Exactly96CRC = 96_000_000L;

        // Alice has EXACTLY 96 CRC of TokenA; Bob (sink) trusts TokenA
        var balance = MakeBalanceGraph((Alice, TokenA, Exactly96CRC));
        var trust = MakeTrust(
            (Bob, TokenA),   // Sink trusts this token
            (Alice, TokenA)  // Source has trust relationship
        );

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            QuantizedMode = true
            // No ToTokens specified → auto-discovery should find TokenA at exactly 96 CRC
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);

        // With >= boundary: exactly 96 CRC qualifies for auto-discovery
        // With > boundary (mutant): 96 CRC would NOT qualify
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA && e.Token == tokenAId), Is.True,
            "Exactly 96 CRC balance should qualify for quantized auto-discovery (>= boundary)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-08: needsVirtualSink || check — isQuantizedSwapMode without toTokens
    //
    // Target: GraphFactory.cs line 294
    //   bool needsVirtualSink = sourceId != null && sourceEqualsSink && (toTokensFilter.Count > 0 || isQuantizedSwapMode);
    // Mutant: || → && breaks quantized swap mode without explicit toTokens
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_NeedsVirtualSink_QuantizedSwapWithoutToTokens()
    {
        var factory = MakeFactory();
        // Alice has TokenA; she trusts it; swap mode (source==sink)
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 200_000_000),
            (Bob, TokenA, 100_000_000));  // Someone else also holds it
        var trust = MakeTrust((Alice, TokenA), (Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            QuantizedMode = true
            // No ToTokens — the || isQuantizedSwapMode should still create virtual sink
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        // With ||: virtual sink is created (isQuantizedSwapMode is true)
        // With && (mutant): would require BOTH toTokensFilter.Count > 0 AND isQuantizedSwapMode
        Assert.That(cg.VirtualSinkAddress, Is.Not.Null,
            "Virtual sink should be created in quantized swap mode even without explicit toTokens");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-09: MaxTransfers boundary — Value > 0 vs Value >= 0
    //
    // Target: V2Pathfinder.cs line 142
    //   bool hasStepCap = request.MaxTransfers.HasValue && request.MaxTransfers.Value > 0;
    // Mutant: > 0 → >= 0 would activate pruning with maxTransfers=0
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_V2Pathfinder_MaxTransfersZero_NoPruning()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            MaxTransfers = 0 // Zero means "no pruning" (> 0 is false)
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        // With > 0: hasStepCap = false → no pruning → flow found
        // With >= 0 (mutant): hasStepCap = true, stepCap = 0 → prunes everything
        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan(UInt256.Zero),
            "MaxTransfers=0 should not activate pruning (no paths removed)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-10: FlowGraph.AggregateIdenticalEdges — zero-flow skip
    //
    // Target: FlowGraph.cs line 117
    //   if (edge.Flow <= 0) { continue; }
    // Mutant: remove the skip → zero-flow edges appear in aggregated graph
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_FlowGraph_AggregateSkipsZeroFlow()
    {
        var fg = new FlowGraph();
        int a = AddressIdPool.IdOf(Alice);
        int b = AddressIdPool.IdOf(Bob);
        int t = AddressIdPool.IdOf(TokenA);
        fg.AddAvatar(a);
        fg.AddAvatar(b);

        // Add one edge with flow, one with zero flow (same key)
        fg.Edges.Add(new FlowEdge(a, b, t, 100) { Flow = 50, CurrentCapacity = 50 });
        fg.Edges.Add(new FlowEdge(a, b, t, 100) { Flow = 0, CurrentCapacity = 100 });

        var agg = fg.AggregateIdenticalEdges();

        // Only the non-zero edge should survive
        Assert.That(agg.Edges.Count, Is.EqualTo(1), "Zero-flow edge should not appear in aggregation");
        Assert.That(agg.Edges[0].Flow, Is.EqualTo(50));
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-11: FlowGraph saturating addition — overflow protection
    //
    // Target: FlowGraph.cs line 127-128
    //   existingEdge.Flow = existingEdge.Flow > long.MaxValue - edge.Flow
    //       ? long.MaxValue : existingEdge.Flow + edge.Flow;
    // Mutant: remove overflow check → integer overflow wraps to negative
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_FlowGraph_SaturatingAddition_NoOverflow()
    {
        var fg = new FlowGraph();
        int a = AddressIdPool.IdOf(Alice);
        int b = AddressIdPool.IdOf(Bob);
        int t = AddressIdPool.IdOf(TokenA);
        fg.AddAvatar(a);
        fg.AddAvatar(b);

        // Two edges that together exceed long.MaxValue
        long nearMax = long.MaxValue / 2 + 1;
        fg.Edges.Add(new FlowEdge(a, b, t, nearMax) { Flow = nearMax, CurrentCapacity = 0 });
        fg.Edges.Add(new FlowEdge(a, b, t, nearMax) { Flow = nearMax, CurrentCapacity = 0 });

        var agg = fg.AggregateIdenticalEdges();

        // Aggregated flow MUST be positive (saturated at long.MaxValue)
        Assert.That(agg.Edges.Count, Is.EqualTo(1));
        Assert.That(agg.Edges[0].Flow, Is.EqualTo(long.MaxValue),
            "Aggregated flow should saturate at long.MaxValue, not overflow to negative");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-12: Group minting — sink toTokens filter on group token
    //
    // Target: GraphFactory.cs line 745
    //   if (isSink && toTokensFilter.Count > 0 && !toTokensFilter.Contains(groupToken)) continue;
    // Mutant: removing negation → only allows non-matching group tokens
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_GroupMintToTokensFilter_InclusionAndExclusion()
    {
        var mock = new InnerMockLoadGraph
        {
            Groups = new List<string> { GroupG },
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph((Alice, TokenA, 300));
        // Bob trusts both GroupG and TokenA, but toTokens only allows GroupG
        var trust = MakeTrust((Alice, TokenA), (Bob, TokenA), (Bob, GroupG));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { GroupG } // Only group token allowed at sink
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int groupId = AddressIdPool.IdOf(GroupG);

        // Group→Bob mint edge with group token SHOULD exist (group token IS in toTokens)
        Assert.That(cg.Edges.Any(e => e.From == groupId && e.To == bobId && e.Token == groupId), Is.True,
            "Group minting to sink should work when group token is in toTokens");
    }

    [Test]
    public void Kill_GraphFactory_GroupMintToTokensFilter_ExcludesNonMatching()
    {
        var mock = new InnerMockLoadGraph
        {
            Groups = new List<string> { GroupG },
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph((Alice, TokenA, 300), (Alice, TokenB, 300));
        // Bob trusts GroupG AND TokenB; toTokens specifies TokenB (which sink trusts)
        // but NOT GroupG. So the group minting edge should be blocked.
        // Note: Bob must trust the toTokens token for the pre-filter (Step 2a) to keep it.
        var trust = MakeTrust((Alice, TokenA), (Bob, TokenA), (Bob, GroupG), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { TokenB } // Group token NOT in this list, but sink trusts TokenB
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int groupId = AddressIdPool.IdOf(GroupG);

        // Group→Bob should NOT exist — group token (GroupG) not in toTokens ({TokenB})
        Assert.That(cg.Edges.Any(e => e.From == groupId && e.To == bobId && e.Token == groupId), Is.False,
            "Group minting to sink should be blocked when group token not in toTokens");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-13: excludedFromTokens — verify it only affects source, not others
    //
    // Target: GraphFactory.cs line 602
    //   if (isSource && excludedFromTokensFilter.Count > 0 && excludedFromTokensFilter.Contains(bn.Token)) continue;
    // Mutant: remove `isSource` → excludes token from ALL holders
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_ExcludedFromTokens_OnlyAffectsSource()
    {
        var factory = MakeFactory();
        // Both Alice (source) and Carol (non-source) hold TokenA
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Carol, TokenA, 200));
        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ExcludedFromTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int carolId = AddressIdPool.IdOf(Carol);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);

        // Alice (source) excluded from sending TokenA
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.False,
            "Source should be excluded from sending excluded token");

        // Carol (non-source) NOT affected by excludedFromTokens
        Assert.That(cg.Edges.Any(e => e.From == carolId && e.To == poolA), Is.True,
            "Non-source holders should not be affected by excludedFromTokens");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-14: Empty filter semantics — fromTokens=[] means "allow all"
    //
    // Target: GraphFactory.cs line 601
    //   if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(bn.Token)) continue;
    // Mutant: remove `Count > 0` → empty filter blocks everything
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_EmptyFromTokensFilter_AllowsAllTokens()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Alice, TokenB, 200));
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            FromTokens = new List<string>() // Empty list = no filter
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Empty fromTokens = allow all → both edges must exist
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.True,
            "Empty fromTokens should allow TokenA");
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB), Is.True,
            "Empty fromTokens should allow TokenB");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-15: Combined 4-filter interaction — all filters active simultaneously
    //
    // Tests that fromTokens, excludedFromTokens, toTokens, and excludedToTokens
    // all apply independently and correctly when active together.
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_AllFourFiltersActive_CorrectInteraction()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Alice, TokenB, 200),
            (Alice, TokenC, 300));
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB), (Bob, TokenC));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            FromTokens = new List<string> { TokenA, TokenB }, // Only A and B from source
            ExcludedFromTokens = new List<string> { TokenB }, // But not B
            ToTokens = new List<string> { TokenA, TokenC },   // Only A and C at sink
            // Net result: only TokenA should flow (from source: A allowed; at sink: A allowed)
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int bobId = AddressIdPool.IdOf(Bob);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int tokenCId = AddressIdPool.IdOf(TokenC);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);
        int poolC = AddressIdPool.TokenPoolIdOf(tokenCId);

        // Source side: TokenA passes fromTokens AND not in excludedFromTokens
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA && e.Token == tokenAId), Is.True,
            "TokenA should pass source filters");

        // Source side: TokenB passes fromTokens but IS in excludedFromTokens
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB && e.Token == tokenBId), Is.False,
            "TokenB should be excluded by excludedFromTokens");

        // Source side: TokenC NOT in fromTokens → excluded
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolC && e.Token == tokenCId), Is.False,
            "TokenC should be excluded by fromTokens");

        // Sink side: TokenA in toTokens → pool→sink edge allowed
        Assert.That(cg.Edges.Any(e => e.From == poolA && e.To == bobId && e.Token == tokenAId), Is.True,
            "TokenA should reach sink (in toTokens)");

        // Sink side: TokenB not in toTokens → pool→sink edge blocked
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == bobId && e.Token == tokenBId), Is.False,
            "TokenB pool→sink should be blocked by toTokens");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-16: MaxFlowSolver — flow > 0 skip check
    //
    // Target: MaxFlowSolver.cs line 59-62
    //   bool hasFlow = flow > 0; if (!hasFlow) continue;
    // Mutant: remove skip → zero-flow edges appear in result
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_MaxFlowSolver_ZeroFlowEdgesExcluded()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        // Alice has limited balance, Bob trusts
        mock.AddBalance(source, token, 50_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        // Use very large target — solver will find some paths but capacity-limited edges
        // will have zero flow
        long targetFlow = 1_000_000_000L;
        var solved = MaxFlowSolver.Solve(cg.Edges, source, AddressIdPool.IdOf(Bob), targetFlow);

        // Every edge in result must have positive flow
        Assert.That(solved.All(e => e.Flow > 0), Is.True,
            "MaxFlowSolver result should only contain edges with positive flow");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-17: AddressIdPool — IdOf case insensitivity
    //
    // Target: AddressIdPool.cs line 51
    //   string lower = _address.ToLowerInvariant();
    // Mutant: remove toLower → case-sensitive lookup creates duplicate IDs
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_AddressIdPool_CaseInsensitive_SameId()
    {
        string upper = "0xABCDEF1234567890ABCDEF1234567890ABCDEF12";
        string lower = "0xabcdef1234567890abcdef1234567890abcdef12";
        string mixed = "0xAbCdEf1234567890AbCdEf1234567890AbCdEf12";

        int id1 = AddressIdPool.IdOf(upper);
        int id2 = AddressIdPool.IdOf(lower);
        int id3 = AddressIdPool.IdOf(mixed);

        // All three case variants must map to the same ID
        Assert.That(id2, Is.EqualTo(id1), "Lowercase should match uppercase");
        Assert.That(id3, Is.EqualTo(id1), "Mixed case should match uppercase");

        // StringOf should return lowercase
        Assert.That(AddressIdPool.StringOf(id1), Is.EqualTo(lower));
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-18: AddressIdPool — StringOf unknown ID throws
    //
    // Target: AddressIdPool.cs line 76
    //   throw new KeyNotFoundException(...)
    // Mutant: remove throw → returns null/empty instead of throwing
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_AddressIdPool_StringOfUnknown_Throws()
    {
        // Use a very high ID that's extremely unlikely to exist
        Assert.Throws<KeyNotFoundException>(() => AddressIdPool.StringOf(int.MaxValue - 1));
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-19: Router/Group balance skip — both must be excluded
    //
    // Target: GraphFactory.cs line 594
    //   if (g.IsRouter(bn.Holder) || g.IsGroup(bn.Holder)) continue;
    // Mutant: || → && only skips if BOTH router AND group (impossible)
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_RouterAndGroupBalancesExcluded()
    {
        var mock = new InnerMockLoadGraph
        {
            Groups = new List<string> { GroupG }
        };
        var factory = MakeFactory(mock);

        // Router and Group both hold TokenA — neither should generate edges
        var bg = new BalanceGraph();
        int routerId = AddressIdPool.IdOf(RouterAddr);
        int groupId = AddressIdPool.IdOf(GroupG);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        bg.AddAvatar(routerId);
        bg.AddAvatar(groupId);
        bg.AddBalance(routerId, tokenAId, 999, isWrapped: false, isStatic: false);
        bg.AddBalance(groupId, tokenAId, 888, isWrapped: false, isStatic: false);

        var trust = MakeTrust((Bob, TokenA));
        var cg = factory.CreateCapacityGraph(bg, trust);

        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);

        // Neither router nor group should have outbound pool edges
        Assert.That(cg.Edges.Any(e => e.From == routerId && e.To == poolA), Is.False,
            "Router should not generate holder→pool edges");
        Assert.That(cg.Edges.Any(e => e.From == groupId && e.To == poolA), Is.False,
            "Group should not generate holder→pool edges");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-20: V2Pathfinder — transfer output filters zero-flow edges
    //
    // Target: V2Pathfinder.cs line 314
    //   if (e.Flow <= 0) { continue; }
    // Mutant: remove → zero-flow edges appear in transfer output
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_V2Pathfinder_OutputExcludesZeroFlowEdges()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        // Every transfer step must have a positive value
        Assert.That(result.Transfers, Is.Not.Null);
        foreach (var step in result.Transfers!)
        {
            var value = UInt256.Parse(step.Value);
            Assert.That(value, Is.GreaterThan(UInt256.Zero),
                $"Transfer {step.From}→{step.To} has non-positive value {step.Value}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-21: CapacityGraph.IsGroup vs IsRouter — independent checks
    //
    // Target: CapacityGraph.cs lines 76-77
    //   public bool IsGroup(int nodeAddress) => GroupNodes.Contains(nodeAddress);
    //   public bool IsRouter(int nodeAddress) => RouterNode.HasValue && RouterNode.Value == nodeAddress;
    // Mutant: swap IsGroup/IsRouter → wrong type identification
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_CapacityGraph_IsGroupAndIsRouter_Independent()
    {
        var cg = new CapacityGraph();
        int routerNode = AddressIdPool.IdOf(RouterAddr);
        int groupNode = AddressIdPool.IdOf(GroupG);
        int regularNode = AddressIdPool.IdOf(Alice);

        cg.SetRouter(routerNode);
        cg.AddGroup(groupNode);
        cg.AddAvatar(regularNode);

        // Router checks
        Assert.That(cg.IsRouter(routerNode), Is.True);
        Assert.That(cg.IsRouter(groupNode), Is.False);
        Assert.That(cg.IsRouter(regularNode), Is.False);

        // Group checks
        Assert.That(cg.IsGroup(groupNode), Is.True);
        Assert.That(cg.IsGroup(routerNode), Is.False);
        Assert.That(cg.IsGroup(regularNode), Is.False);
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-22: Simulated balance filters — same filter logic as snapshot balances
    //
    // Target: GraphFactory.cs lines 634-641 (AddSimulatedBalances_Pooled)
    // These are NoCoverage mutants — simulated balances with filters need a test
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_SimulatedBalance_FromTokensFilter()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(); // empty snapshot
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            FromTokens = new List<string> { TokenA },
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = Alice, Token = TokenA, Amount = "100000000000000000000" }, // 100 CRC
                new() { Holder = Alice, Token = TokenB, Amount = "200000000000000000000" }  // 200 CRC
            }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // TokenA allowed by fromTokens → edge exists
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.True,
            "Simulated TokenA should pass fromTokens filter");

        // TokenB not in fromTokens → edge excluded for source
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB), Is.False,
            "Simulated TokenB should be excluded by fromTokens filter");
    }

    [Test]
    public void Kill_GraphFactory_SimulatedBalance_WrappedNonSource_Excluded()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph();
        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = true,
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = Alice, Token = TokenA, Amount = "100000000000000000000", IsWrapped = true },
                new() { Holder = Carol, Token = TokenA, Amount = "100000000000000000000", IsWrapped = true }
            }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int carolId = AddressIdPool.IdOf(Carol);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);

        // Source's wrapped sim balance included
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.True,
            "Source wrapped simulated balance should be included");

        // Non-source's wrapped sim balance excluded
        Assert.That(cg.Edges.Any(e => e.From == carolId && e.To == poolA), Is.False,
            "Non-source wrapped simulated balance should be excluded");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-23: Swap mode — source's toTokens balance excluded from source→pool
    //
    // Target: GraphFactory.cs line 600
    //   if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && toTokensFilter.Contains(bn.Token)) continue;
    // This filter prevents the source from sending toTokens into the pool
    // in swap mode (would create a pointless loop: source sends token, gets it back)
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_SwapMode_SourceToTokensExcludedFromPool()
    {
        var factory = MakeFactory();
        // Alice holds TokenA and TokenB; swap mode with toTokens={TokenA}
        // Bob also holds TokenA so the pool gets populated from elsewhere
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 500),
            (Alice, TokenB, 300),
            (Bob, TokenA, 300));
        var trust = MakeTrust((Alice, TokenA), (Alice, TokenB), (Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Alice→Pool(TokenA) should be EXCLUDED (swap mode, TokenA is in toTokens)
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA && e.Token == tokenAId), Is.False,
            "Source's toTokens balance should be excluded in swap mode");

        // Alice→Pool(TokenB) should still exist (TokenB not in toTokens)
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB && e.Token == tokenBId), Is.True,
            "Source's non-toTokens balance should still be included in swap mode");

        // Bob→Pool(TokenA) should exist (Bob is not source)
        int bobId = AddressIdPool.IdOf(Bob);
        Assert.That(cg.Edges.Any(e => e.From == bobId && e.To == poolA && e.Token == tokenAId), Is.True,
            "Non-source holder's balance should still create pool edge");

        // Virtual sink should exist (swap mode with toTokens)
        Assert.That(cg.VirtualSinkAddress, Is.Not.Null,
            "Virtual sink should be created in swap mode");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-24: Consented flow validation — all 3 outcomes tested
    //
    // Target: V2Pathfinder.cs lines 458-510
    // Survivors at lines 458, 468, 475, 490, 497
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_V2Pathfinder_ConsentedFlow_NonConsentedPasses()
    {
        // An unconsented sender should always pass validation
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);
        // Alice NOT in consented avatars

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        // Flow should succeed (Alice is not consented → standard trust applies)
        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan(UInt256.Zero),
            "Non-consented sender should pass validation");
    }

    [Test]
    public void Kill_V2Pathfinder_ConsentedFlow_ConsentedSenderTrustsReceiver_Passes()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);
        mock.AddTrust(source, sink); // Alice trusts Bob (needed for consented flow)
        mock.AddConsentedAvatar(source); // Alice has consented flow
        mock.AddConsentedAvatar(sink);   // Bob also has consented flow

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        // Flow should succeed: From(consented) trusts To, and To is also consented
        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan(UInt256.Zero),
            "Consented sender that trusts consented receiver should pass");
    }

    [Test]
    public void Kill_V2Pathfinder_ConsentedFlow_ConsentedSenderDoesNotTrustReceiver_Blocked()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);
        // Alice does NOT trust Bob
        mock.AddConsentedAvatar(source); // Alice has consented flow
        mock.AddConsentedAvatar(sink);   // Bob also has consented flow

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        // Flow should be blocked: From(consented) does NOT trust To
        Assert.That(UInt256.Parse(result.MaxFlow), Is.EqualTo(UInt256.Zero),
            "Consented sender that doesn't trust receiver should block flow");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-25: toTokensFilter intersection at step 2a — filter preserves only trusted
    //
    // Target: GraphFactory.cs lines 226-239 (step 2a)
    // Survivor at line 226 (Equality mutation)
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_ToTokensIntersection_KeepsOnlyTrusted()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(
            (Alice, TokenA, 100),
            (Alice, TokenB, 100));
        // Bob trusts only TokenA, not TokenB
        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { TokenA, TokenB } // Request both, but sink only trusts A
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Pool→Bob for TokenA should exist (trusted)
        Assert.That(cg.Edges.Any(e => e.From == poolA && e.To == bobId && e.Token == tokenAId), Is.True,
            "Trusted token should pass the Step 2a intersection filter");

        // Pool→Bob for TokenB should NOT exist (not trusted by sink → removed by intersection)
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == bobId && e.Token == tokenBId), Is.False,
            "Untrusted token should be removed by Step 2a intersection filter");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-26: PrunePathsByStepLimit — boundary at delta <= stepsLeft
    //
    // Target: V2Pathfinder.cs line 728
    //   bool fitsBudget = delta <= stepsLeft;
    // Mutant: <= → < would be too conservative (reject paths that exactly fit)
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_V2Pathfinder_PrunePathBudget_ExactFit()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        // MaxTransfers=1 → exactly one collapsed transfer step allowed
        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            MaxTransfers = 1
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        // With <=: path with exactly 1 step fits budget → flow found
        // With < (mutant): 1 < 1 is false → path rejected, no flow
        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan(UInt256.Zero),
            "Path that exactly fits the step budget should be included");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-27: Group minting — excludedToTokens filter on group token
    //
    // Target: GraphFactory.cs line 747
    //   if (isSink && excludedToTokensFilter.Count > 0 && excludedToTokensFilter.Contains(groupToken)) continue;
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_GraphFactory_GroupMintExcludedToTokensFilter()
    {
        var mock = new InnerMockLoadGraph
        {
            Groups = new List<string> { GroupG },
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph((Alice, TokenA, 300));
        var trust = MakeTrust((Alice, TokenA), (Bob, TokenA), (Bob, GroupG));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ExcludedToTokens = new List<string> { GroupG } // Exclude the group's own token at sink
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int groupId = AddressIdPool.IdOf(GroupG);

        // Group→Bob mint edge should be excluded by excludedToTokens
        Assert.That(cg.Edges.Any(e => e.From == groupId && e.To == bobId && e.Token == groupId), Is.False,
            "Group minting to sink should be blocked by excludedToTokens");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-28: AddressIdPool — BalanceNodeIdOf tags the ID correctly
    //
    // Target: AddressIdPool.cs lines 85, 96
    //   BalanceNodeIds.TryAdd(existing, 0);
    // Mutant: remove → IsBalanceNode returns false for known balance nodes
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_AddressIdPool_BalanceNodeIdOf_TagsCorrectly()
    {
        string balNode = "0xf2ba1ance0000000000000000000000000000001";
        int id = AddressIdPool.BalanceNodeIdOf(balNode);

        // Should be tagged as balance node
        Assert.That(AddressIdPool.IsBalanceNode(id), Is.True,
            "BalanceNodeIdOf should tag the ID as a balance node");

        // Second call with same string should still be tagged
        int id2 = AddressIdPool.BalanceNodeIdOf(balNode);
        Assert.That(id2, Is.EqualTo(id), "Same string should return same ID");
        Assert.That(AddressIdPool.IsBalanceNode(id2), Is.True,
            "Re-fetched balance node should still be tagged");

        // Regular IdOf should NOT be a balance node
        string regularAddr = "0xf2re9u1ar0000000000000000000000000000001";
        int regularId = AddressIdPool.IdOf(regularAddr);
        Assert.That(AddressIdPool.IsBalanceNode(regularId), Is.False,
            "Regular address should NOT be tagged as balance node");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-29: Quantized mode — full E2E with 96 CRC quantum
    //
    // Target: V2Pathfinder.cs lines 1057-1150 (quantization engine)
    // Survivors at 1057, 1080, 1087, 1094, 1102, 1122, 1133
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_V2Pathfinder_QuantizedMode_OutputIsMultipleOf96CRC()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);

        // Give Alice 200 CRC — enough for 2 quanta (192 CRC), 8 CRC left over
        mock.AddBalance(source, token, 200_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            QuantizedMode = true
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        // Request 200 CRC worth (will be quantized down to 192)
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("200000000000000000000"));

        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan(UInt256.Zero),
            "Quantized mode should find flow");

        // Each sink-bound transfer should be a multiple of 96 CRC
        // 96 CRC in Wei = 96 * 10^18 = 96000000000000000000
        var quantaWei = UInt256.Parse("96000000000000000000");
        foreach (var step in result.Transfers!)
        {
            var toId = AddressIdPool.IdOf(step.To);
            var fromId = AddressIdPool.IdOf(step.From);
            bool isSinkBound = toId == sink && fromId != sink; // Exclude self-loops

            if (isSinkBound)
            {
                var value = UInt256.Parse(step.Value);
                // Check divisibility: value / quantaWei * quantaWei == value
                UInt256.Divide(value, quantaWei, out var quotient);
                var reconstructed = quotient * quantaWei;
                Assert.That(reconstructed, Is.EqualTo(value),
                    $"Sink-bound transfer {step.From}→{step.To} value {step.Value} should be multiple of 96 CRC");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // MK-30: ValidateMintEdgeOrdering — inbound == outbound is valid
    //
    // Target: V2Pathfinder.cs line 1018
    //   if (inbound < groupOutboundFlow[groupId])
    // Mutant: < → <= rejects balanced (equal) flows
    // ════════════════════════════════════════════════════════════════════════
    [Test]
    public void Kill_V2Pathfinder_MintOrdering_ExactBalanceValid()
    {
        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddr);
        int source = AddressIdPool.IdOf(Alice);
        int sink = AddressIdPool.IdOf(Bob);
        int token = AddressIdPool.IdOf(TokenA);
        int group = AddressIdPool.IdOf(GroupG);

        mock.AddBalance(source, token, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, token);
        mock.AddTrust(sink, group); // Bob trusts group token

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            MaxTransfers = 10
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        // This will produce: Alice→Router→Group (collateral) + Group→Bob (mint)
        // The collateral exactly matches the mint — should be valid with < but not <=
        var result = pathfinder.ComputeMaxFlowWithPath(cg, request, UInt256.Parse("100000000000000000000"));

        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan(UInt256.Zero),
            "Group minting with exactly balanced inbound/outbound should succeed");
    }

    #region Inner Mock

    private class InnerMockLoadGraph : ILoadGraph
    {
        public List<string> Groups { get; set; } = new();
        public List<(string GroupAddress, string TrustedToken)> GroupTrusts { get; set; } = new();
        public List<(string Avatar, bool HasConsentedFlow)> ConsentedFlags { get; set; } = new();

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
            => Enumerable.Empty<(string, int, int, bool, bool)>();

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
            => Enumerable.Empty<(string, string, int)>();

        public IEnumerable<string> LoadGroups() => Groups;
        public IEnumerable<string> LoadOrganizations() => [];
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts() => GroupTrusts;
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags() => ConsentedFlags;
        public IEnumerable<string> LoadRegisteredAvatars() => Enumerable.Empty<string>();
        public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, CirclesType CirclesType)> LoadWrapperMappings()
            => Array.Empty<(string, string, CirclesType)>();
    }

    #endregion
}
