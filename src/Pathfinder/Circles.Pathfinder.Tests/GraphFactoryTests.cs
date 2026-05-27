using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using HelperMock = Circles.Pathfinder.Tests.Helpers.MockLoadGraph;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for GraphFactory: capacity graph construction, filtering,
/// simulated balances/trusts, virtual sink, group minting, and validation.
/// Uses a mock ILoadGraph to avoid DB dependency.
/// </summary>
[TestFixture, Parallelizable]
public class GraphFactoryTests
{
    // Valid 42-char hex addresses (0x + 40 hex) — required for IsValidEthereumAddress checks
    private static readonly string RouterAddr = "0xf1ff000000000000000000000000000000000001";
    private static readonly string ScoreRouterAddr = "0xf1ff000000000000000000000000000000000009";
    private static readonly string Alice = "0xf1aa000000000000000000000000000000000002";
    private static readonly string Bob = "0xf1bb000000000000000000000000000000000003";
    // Carol available for future multi-hop tests
    // private static readonly string Carol   = "0xf1cc000000000000000000000000000000000004";
    private static readonly string TokenA = "0xf10a000000000000000000000000000000000005";
    private static readonly string TokenB = "0xf10b000000000000000000000000000000000006";
    private static readonly string GroupG = "0xf199000000000000000000000000000000000007";
    private static readonly string GroupH = "0xf198000000000000000000000000000000000008";
    // Wrapper contract addresses for testing wrapper filter expansion
    private static readonly string WrapperA = "0xf1ea000000000000000000000000000000000011";
    private static readonly string WrapperB = "0xf1eb000000000000000000000000000000000012";

    /// <summary>Creates a factory with a mock ILoadGraph (no groups, no consent by default).</summary>
    private static GraphFactory MakeFactory(MockLoadGraph? mock = null)
    {
        mock ??= new MockLoadGraph();
        return new GraphFactory(RouterAddr, mock);
    }

    /// <summary>Builds a BalanceGraph with the given (holder, token, amount) triples.</summary>
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

    /// <summary>Builds a trust lookup: truster -> set of trusted tokens.</summary>
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

    #region BuildTrustLookup

    [Test]
    public void BuildTrustLookup_CreatesDictionaryFromTrustGraph()
    {
        var tg = new TrustGraph();
        int a = AddressIdPool.IdOf(Alice);
        int b = AddressIdPool.IdOf(Bob);
        tg.AddAvatar(a);
        tg.AddAvatar(b);
        tg.AddTrustEdge(a, b);

        var lookup = GraphFactory.BuildTrustLookup(tg);

        Assert.That(lookup.ContainsKey(a), Is.True);
        Assert.That(lookup[a], Does.Contain(b));
    }

    [Test]
    public void BuildTrustLookup_EmptyGraph_ReturnsEmpty()
    {
        var lookup = GraphFactory.BuildTrustLookup(new TrustGraph());
        Assert.That(lookup, Is.Empty);
    }

    #endregion

    #region IsValidEthereumAddress

    [TestCase("0x1234567890abcdef1234567890abcdef12345678", true)]
    [TestCase("0xABCDEF1234567890ABCDEF1234567890ABCDEF12", true)]
    [TestCase("1234567890abcdef1234567890abcdef12345678", false)]  // no 0x
    [TestCase("0x123", false)]                                      // too short
    [TestCase("0xGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", false)]  // non-hex
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValidEthereumAddress_VariousCases(string? address, bool expected)
    {
        Assert.That(GraphFactory.IsValidEthereumAddress(address!), Is.EqualTo(expected));
    }

    #endregion

    #region CreateCapacityGraph - Basic

    [Test]
    public void CreateCapacityGraph_AddsAllAvatarNodesFromBalanceAndTrust()
    {
        var mock = new MockLoadGraph { RegisteredAvatars = new List<string> { Alice, Bob, TokenA } };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph((Alice, TokenA, 1000));
        var trust = MakeTrust((Bob, TokenA));

        var cg = factory.CreateCapacityGraph(balance, trust);

        // Alice (holder), TokenA (balance token), Bob (truster), and TokenA (trusted) + Router
        Assert.That(cg.AvatarNodes.ContainsKey(AddressIdPool.IdOf(Alice)), Is.True);
        Assert.That(cg.AvatarNodes.ContainsKey(AddressIdPool.IdOf(Bob)), Is.True);
        Assert.That(cg.AvatarNodes.ContainsKey(AddressIdPool.IdOf(TokenA)), Is.True);
        Assert.That(cg.AvatarNodes.ContainsKey(AddressIdPool.IdOf(RouterAddr)), Is.True);
    }

    [Test]
    public void CreateCapacityGraph_CreatesHolderToTokenPoolEdge()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph((Alice, TokenA, 500));
        var trust = MakeTrust((Bob, TokenA));

        var cg = factory.CreateCapacityGraph(balance, trust);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        // H→TokenPool edge
        var h2pool = cg.Edges.Where(e => e.From == aliceId && e.To == poolId).ToList();
        Assert.That(h2pool.Count, Is.EqualTo(1));
        Assert.That(h2pool[0].InitialCapacity, Is.EqualTo(500));

        // TokenPool→Bob (trust-based) edge
        int bobId = AddressIdPool.IdOf(Bob);
        var pool2bob = cg.Edges.Where(e => e.From == poolId && e.To == bobId).ToList();
        Assert.That(pool2bob.Count, Is.EqualTo(1));
        Assert.That(pool2bob[0].InitialCapacity, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void CreateCapacityGraph_NoRequest_NullFilters_BuildsCleanGraph()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph((Alice, TokenA, 100));
        var trust = MakeTrust((Bob, TokenA));

        // request=null → no filters active
        var cg = factory.CreateCapacityGraph(balance, trust, request: null);

        Assert.That(cg.Edges.Count, Is.GreaterThan(0));
        Assert.That(cg.VirtualSinkAddress, Is.Null);
    }

    #endregion

    #region Simulated Balances

    [Test]
    public void CreateCapacityGraph_SimulatedBalances_AddsEdges()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(); // empty
        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            SimulatedBalances = new List<SimulatedBalance>
            {
                // Amount in attoCircles (Wei) — TruncateToInt64 divides by 10^12
                new() { Holder = Alice, Token = TokenA, Amount = "2000000000000000000" } // 2 CRC
            }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        var simEdges = cg.Edges.Where(e => e.From == aliceId && e.To == poolId).ToList();
        Assert.That(simEdges.Count, Is.GreaterThanOrEqualTo(1));
    }

    #endregion

    #region Simulated Trusts

    [Test]
    public void CreateCapacityGraph_SimulatedTrusts_MergedWithOnchain()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph((Alice, TokenA, 100));
        var trust = MakeTrust(); // empty on-chain trust

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new() { Truster = Bob, Trustee = TokenA }
            }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        // Bob should receive TokenA via pool→Bob edge (simulated trust)
        var pool2bob = cg.Edges.Where(e => e.From == poolId && e.To == bobId && e.Token == tokenAId).ToList();
        Assert.That(pool2bob.Count, Is.EqualTo(1));
    }

    #endregion

    #region Source/Sink Validation

    [Test]
    public void CreateCapacityGraph_GroupAsSource_Throws()
    {
        var mock = new MockLoadGraph { Groups = new List<string> { GroupG } };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph();
        var trust = MakeTrust();

        var request = new FlowRequest { Source = GroupG, Sink = Bob };

        Assert.Throws<ArgumentException>(() =>
            factory.CreateCapacityGraph(balance, trust, request));
    }

    [Test]
    public void CreateCapacityGraph_GroupAsSink_Throws()
    {
        var mock = new MockLoadGraph { Groups = new List<string> { GroupG } };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph();
        var trust = MakeTrust();

        var request = new FlowRequest { Source = Alice, Sink = GroupG };

        Assert.Throws<ArgumentException>(() =>
            factory.CreateCapacityGraph(balance, trust, request));
    }

    [Test]
    public void CreateCapacityGraph_RouterAsSource_Throws()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph();
        var trust = MakeTrust();

        var request = new FlowRequest { Source = RouterAddr, Sink = Bob };

        Assert.Throws<ArgumentException>(() =>
            factory.CreateCapacityGraph(balance, trust, request));
    }

    [Test]
    public void CreateCapacityGraph_RouterAsSink_Throws()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph();
        var trust = MakeTrust();

        var request = new FlowRequest { Source = Alice, Sink = RouterAddr };

        Assert.Throws<ArgumentException>(() =>
            factory.CreateCapacityGraph(balance, trust, request));
    }

    #endregion

    #region Filters

    [Test]
    public void CreateCapacityGraph_FromTokensFilter_OnlyIncludesMatchingSourceEdges()
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
            FromTokens = new List<string> { TokenA } // only TokenA allowed from source
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Alice→PoolA should exist
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.True,
            "TokenA edge from source should exist");

        // Alice→PoolB should NOT exist (filtered out)
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB), Is.False,
            "TokenB edge from source should be filtered by fromTokens");
    }

    [Test]
    public void CreateCapacityGraph_ExcludedFromTokensFilter_ExcludesMatchingSourceEdges()
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
            ExcludedFromTokens = new List<string> { TokenB }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolB), Is.False,
            "Excluded token should not have source edge");
    }

    [Test]
    public void CreateCapacityGraph_ToTokensFilter_FiltersPoolToSinkEdges()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph((Alice, TokenA, 100), (Alice, TokenB, 100));
        // Bob trusts both tokens, but only TokenA is in toTokens
        var trust = MakeTrust((Bob, TokenA), (Bob, TokenB));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        int tokenBId = AddressIdPool.IdOf(TokenB);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Pool(B)→Bob should be filtered by ToTokens since Bob is the sink
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == bobId && e.Token == tokenBId), Is.False,
            "TokenB pool→sink edge should be filtered by toTokens");
    }

    [Test]
    public void CreateCapacityGraph_WithWrapFalse_SkipsWrappedBalances()
    {
        var factory = MakeFactory();
        var bg = new BalanceGraph();
        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        bg.AddAvatar(aliceId);
        bg.AddBalance(aliceId, tokenAId, 300, isWrapped: true, isStatic: false);

        var trust = MakeTrust((Bob, TokenA));

        // withWrap = false (default)
        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trust, request);

        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.False,
            "Wrapped balance should be skipped when withWrap is not set");
    }

    [Test]
    public void CreateCapacityGraph_WithWrapTrue_IncludesWrappedSourceBalance()
    {
        var factory = MakeFactory();
        var bg = new BalanceGraph();
        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        bg.AddAvatar(aliceId);
        bg.AddBalance(aliceId, tokenAId, 300, isWrapped: true, isStatic: false);

        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest { Source = Alice, Sink = Bob, WithWrap = true };
        var cg = factory.CreateCapacityGraph(bg, trust, request);

        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolA), Is.True,
            "Wrapped source balance should be included when withWrap=true");
    }

    #endregion

    #region Virtual Sink

    [Test]
    public void CreateCapacityGraph_SourceEqualsSink_WithToTokens_CreatesVirtualSink()
    {
        var factory = MakeFactory();
        // In swap mode, source's balance for toTokens is excluded from the graph.
        // A non-source holder (Bob) must also hold TokenA so the pool gets created.
        var balance = MakeBalanceGraph((Alice, TokenA, 500), (Bob, TokenA, 300));
        // Alice trusts TokenA (needed for virtual sink to accept)
        var trust = MakeTrust((Alice, TokenA), (Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        Assert.That(cg.VirtualSinkAddress, Is.Not.Null,
            "Virtual sink should be created for source==sink with toTokens");
    }

    [Test]
    public void CreateCapacityGraph_SourceDifferentFromSink_NoVirtualSink()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph((Alice, TokenA, 500));
        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        Assert.That(cg.VirtualSinkAddress, Is.Null,
            "No virtual sink when source != sink");
    }

    #endregion

    #region Group Minting

    [Test]
    public void CreateCapacityGraph_GroupMintingEdges_AddedWhenTrustExists()
    {
        var mock = new MockLoadGraph
        {
            Groups = new List<string> { GroupG },
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph((Alice, TokenA, 300));
        // Bob trusts the group's own token (GroupG as token = GroupG mints)
        var trust = MakeTrust((Alice, TokenA), (Bob, TokenA), (Bob, GroupG));

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int groupId = AddressIdPool.IdOf(GroupG);
        int bobId = AddressIdPool.IdOf(Bob);

        // Group→Bob edge for group token minting
        var mintEdges = cg.Edges.Where(e => e.From == groupId && e.To == bobId && e.Token == groupId).ToList();
        Assert.That(mintEdges.Count, Is.EqualTo(1), "Group minting edge should exist");
        Assert.That(mintEdges[0].InitialCapacity, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void SelfMintViaPath_GroupTokenWithNoSupply_VirtualSinkSurvivesViaGroupEdge()
    {
        // Regression for self-mint via path returning maxFlow=0 against prod
        // ScoreGroup 0x7CadB2E9… (2026-05-19). Reproduces shape: source==sink,
        // toTokens=[group whose CRC nobody holds yet]. Pre-fix the virtualSink
        // had zero inbound edges (TokenPool(groupToken) doesn't exist pre-mint)
        // and got pruned, yielding maxFlow=0 for legitimate self-mint paths.
        // Fix: Group → virtualSink fallback when pool is absent and t is a group.
        var mock = new MockLoadGraph
        {
            Groups = new List<string> { GroupG },
            // GroupG trusts TokenA as collateral
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);

        // Alice holds TokenA (collateral). Nobody holds GroupG's CRC yet.
        var balance = MakeBalanceGraph((Alice, TokenA, 1000));
        // Alice trusts both TokenA (for her own holdings) and GroupG (to receive
        // the group's CRC after mint).
        var trust = MakeTrust((Alice, TokenA), (Alice, GroupG));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { GroupG }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        Assert.That(cg.VirtualSinkAddress, Is.Not.Null,
            "Virtual sink must survive — Group → virtualSink edge should be added " +
            "even when TokenPool(groupToken) is absent (no existing supply).");

        int groupId = AddressIdPool.IdOf(GroupG);
        int virtualSinkId = cg.VirtualSinkAddress!.Value;
        var groupToVirtualSink = cg.Edges
            .Where(e => e.From == groupId && e.To == virtualSinkId && e.Token == groupId)
            .ToList();
        Assert.That(groupToVirtualSink.Count, Is.EqualTo(1),
            "Exactly one Group → virtualSink edge should exist for the group token.");
        Assert.That(groupToVirtualSink[0].InitialCapacity, Is.EqualTo(long.MaxValue),
            "Group → virtualSink capacity is uncapped; mint cap is enforced upstream " +
            "by the pool_collateral → group edge.");
    }

    [Test]
    public void SelfMintViaPath_GroupTokenWithExistingSupply_UsesPoolEdge()
    {
        // Counterpart to the above: when TokenPool(groupToken) DOES exist (some
        // avatar holds the group's CRC), the existing pool → virtualSink path
        // must still be used. The Group → virtualSink fallback must not fire.
        var mock = new MockLoadGraph
        {
            Groups = new List<string> { GroupG },
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);

        // Bob already holds GroupG's CRC, so TokenPool(GroupG) will be created.
        var balance = MakeBalanceGraph((Alice, TokenA, 1000), (Bob, GroupG, 500));
        var trust = MakeTrust((Alice, TokenA), (Alice, GroupG), (Bob, GroupG));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { GroupG }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int groupId = AddressIdPool.IdOf(GroupG);
        int virtualSinkId = cg.VirtualSinkAddress!.Value;

        // Group → virtualSink fallback edge should NOT exist when pool path is viable.
        var fallbackEdges = cg.Edges
            .Where(e => e.From == groupId && e.To == virtualSinkId)
            .ToList();
        Assert.That(fallbackEdges.Count, Is.EqualTo(0),
            "Group → virtualSink fallback must not fire when TokenPool(groupToken) exists.");
    }

    [Test]
    public void SelfMintViaPath_FallbackActive_MintCapBindsThroughCollateralEdge()
    {
        // Review-gated regression: when the Group → virtualSink fallback fires
        // (group token has no pool pre-mint), the mint cap must still bind the
        // flow through the upstream pool_collateral → group edge. Verifies the
        // fallback's `long.MaxValue` capacity is not the effective bottleneck.
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddScoreRouter(ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        // Alice trusts GroupG so it survives the sink-side toTokens filter and
        // ends up in virtualSinkTrustedTokens.
        mock.AddTrust(Alice, GroupG);
        mock.AddScoreGroupMintLimit(GroupG, TokenA, "25000000000000000000");
        mock.AddOperatorApproval(ScoreRouterAddr, Alice);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { GroupG }
        };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        int groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        // Cap edge binds at the cached limit (25_000_000 from AddScoreGroupMintLimit).
        var capEdge = cg.Edges.Single(e => e.From == poolId && e.To == groupId && e.Token == tokenAId);
        Assert.That(capEdge.InitialCapacity, Is.EqualTo(25_000_000),
            "pool_collateral → group capacity must equal ScoreGroupMintLimits cap.");

        // Fallback edge exists and is uncapped (cap is enforced by the upstream collateral edge).
        Assert.That(cg.VirtualSinkAddress, Is.Not.Null);
        int virtualSinkId = cg.VirtualSinkAddress!.Value;
        var fallbackEdge = cg.Edges.Single(e => e.From == groupId && e.To == virtualSinkId && e.Token == groupId);
        Assert.That(fallbackEdge.InitialCapacity, Is.EqualTo(long.MaxValue),
            "Group → virtualSink fallback edge is intentionally uncapped; cap is enforced by the upstream collateral edge.");
    }

    [Test]
    public void SelfMintViaPath_FallbackActive_UnapprovedOperator_HasNoCollateralEdge()
    {
        // Review-gated regression: when source is NOT an approved operator of
        // a score router, the per-request build filters out the
        // pool_collateral → group edge. The fallback edge Group → virtualSink
        // may still get created (it only checks pool absence + IsGroup), but
        // the group node has no inbound edges, so max-flow returns 0.
        // This proves the fallback does not enable false positives.
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddScoreRouter(ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        mock.AddTrust(Alice, GroupG);
        mock.AddScoreGroupMintLimit(GroupG, TokenA, "25000000000000000000");
        // INTENTIONALLY OMIT mock.AddOperatorApproval(ScoreRouterAddr, Alice);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { GroupG }
        };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        int groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        // pool_collateral → group edge must be filtered out when operator is unapproved.
        var collateralEdges = cg.Edges
            .Where(e => e.From == poolId && e.To == groupId && e.Token == tokenAId)
            .ToList();
        Assert.That(collateralEdges.Count, Is.EqualTo(0),
            "pool_collateral → group must NOT be added when source is not an approved operator of the score router.");

        // Group has zero inbound edges → no path source → group → virtualSink exists.
        // Fallback edge (group → virtualSink) might be added, but it's unreachable.
        var groupInbound = cg.Edges.Where(e => e.To == groupId).ToList();
        Assert.That(groupInbound.Count, Is.EqualTo(0),
            "Group node must have no inbound edges when source is unapproved — proves no false positive even if Group → virtualSink exists.");
    }

    [Test]
    public void GroupTrustedToken_IsAvatarNode_EvenWithNoUserTrust_DbPath()
    {
        // Token T is ONLY reachable via group trust (G trusts T).
        // No user-to-user trust mentions T, no balance holder is T.
        // Before fix: T was in GroupTrustedTokens but NOT in AvatarNodes → "Sink isn't in the graph snapshot"
        var mock = new MockLoadGraph
        {
            Groups = new List<string> { GroupG },
            GroupTrusts = new List<(string, string)> { (GroupG, TokenA) }
        };
        var factory = MakeFactory(mock);
        var balance = MakeBalanceGraph((Alice, TokenA, 300));
        // Only Alice→TokenA trust; TokenA appears as trustee, but the key test is
        // that group-trusted tokens also get AddAvatar() called
        var trust = MakeTrust((Alice, TokenA));

        var cg = factory.CreateCapacityGraph(balance, trust);

        int tokenAId = AddressIdPool.IdOf(TokenA);
        Assert.That(cg.AvatarNodes.ContainsKey(tokenAId), Is.True,
            "Group-trusted token should be a valid avatar node (DB path)");
    }

    [Test]
    public void GroupTrustedToken_IsAvatarNode_EvenWithNoUserTrust_CachedPath()
    {
        // Same scenario but via cached group data path
        var factory = MakeFactory(); // no DB groups

        int groupGId = AddressIdPool.IdOf(GroupG);
        int tokenBId = AddressIdPool.IdOf(TokenB);

        var cached = new CachedGroupData(
            GroupNodes: new HashSet<int> { groupGId },
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>
            {
                { groupGId, new HashSet<int> { tokenBId } }
            },
            ConsentedAvatars: new HashSet<int>(),
            RegisteredAvatarIds: new HashSet<int>(),
            WrapperToAvatar: new Dictionary<int, int>());

        var balance = MakeBalanceGraph((Alice, TokenA, 100));
        // TokenB has NO user trust edges and NO balance holders
        var trust = MakeTrust((Alice, TokenA));

        var cg = factory.CreateCapacityGraph(balance, trust, request: null, cachedGroupData: cached);

        Assert.That(cg.AvatarNodes.ContainsKey(tokenBId), Is.True,
            "Group-trusted token should be a valid avatar node (cached path)");
    }

    #endregion

    #region CachedGroupData

    [Test]
    public void CreateCapacityGraph_WithCachedGroupData_SkipsDBLoad()
    {
        var mock = new MockLoadGraph
        {
            // DB returns different data — should NOT be used when cache is present
            Groups = new List<string> { GroupH },
            GroupTrusts = new List<(string, string)>()
        };
        var factory = MakeFactory(mock);

        int groupGId = AddressIdPool.IdOf(GroupG);
        int tokenAId = AddressIdPool.IdOf(TokenA);

        var cached = new CachedGroupData(
            GroupNodes: new HashSet<int> { groupGId },
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>
            {
                { groupGId, new HashSet<int> { tokenAId } }
            },
            ConsentedAvatars: new HashSet<int>(),
            RegisteredAvatarIds: new HashSet<int>(),
            WrapperToAvatar: new Dictionary<int, int>());

        var balance = MakeBalanceGraph((Alice, TokenA, 100));
        var trust = MakeTrust((Bob, TokenA));

        var cg = factory.CreateCapacityGraph(balance, trust, request: null, cachedGroupData: cached);

        // GroupG should be in graph (from cache), not GroupH (from DB)
        Assert.That(cg.IsGroup(groupGId), Is.True, "Cached group should be present");
        Assert.That(cg.GroupNodes.Contains(AddressIdPool.IdOf(GroupH)), Is.False,
            "DB group should not be loaded when cache is present");
    }

    #endregion

    #region Consented Flow

    [Test]
    public void CreateCapacityGraph_SimulatedConsentedAvatars_AddedToGraph()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph((Alice, TokenA, 100));
        var trust = MakeTrust((Bob, TokenA));

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            SimulatedConsentedAvatars = new List<string> { Alice }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        Assert.That(cg.ConsentedAvatars.Contains(aliceId), Is.True);
    }

    [Test]
    public void CreateCapacityGraph_InvalidSimulatedConsentedAvatar_Skipped()
    {
        var factory = MakeFactory();
        var balance = MakeBalanceGraph();
        var trust = MakeTrust();

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            SimulatedConsentedAvatars = new List<string>
            {
                "not-a-valid-address",
                "",
                Alice // valid
            }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int aliceId = AddressIdPool.IdOf(Alice);
        Assert.That(cg.ConsentedAvatars.Contains(aliceId), Is.True);
        // Only the valid address should be added (1 entry)
        Assert.That(cg.ConsentedAvatars.Count, Is.EqualTo(1));
    }

    [Test]
    public void SimulatedConsentedAvatar_IsAvatarNode_EvenWithNoBalanceOrTrust()
    {
        // A simulated consented avatar that has no balance and no trust edges
        // should still be registered as an avatar node (consistency with STEP 1b/1c)
        var factory = MakeFactory();
        var balance = MakeBalanceGraph(); // empty
        var trust = MakeTrust();          // empty

        // Bob has no economic activity but is injected as consented
        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            SimulatedConsentedAvatars = new List<string> { Bob }
        };

        var cg = factory.CreateCapacityGraph(balance, trust, request);

        int bobId = AddressIdPool.IdOf(Bob);
        Assert.That(cg.AvatarNodes.ContainsKey(bobId), Is.True,
            "Simulated consented avatar should be registered as avatar node");
        Assert.That(cg.ConsentedAvatars.Contains(bobId), Is.True,
            "Simulated consented avatar should be in ConsentedAvatars set");
    }

    #endregion

    #region Regression: Unregistered Wrapper Trustee (Bug #1 + #6)

    /// <summary>
    /// Regression: a wrapper address for an unregistered avatar that enters through trust
    /// must NOT appear in AvatarNodes at all (fail-closed registration filter).
    /// Previously the empty RegisteredAvatarIds bypass allowed unregistered wrappers in,
    /// which caused Hub.sol AvatarMustBeRegistered reverts.
    /// </summary>
    [Test]
    public void GraphFactory_UnregisteredWrapperTrustee_CannotBeFlowIntermediary()
    {
        // WrapperAddr is trusted by Bob but is unregistered
        var WrapperAddr = "0xf1wr000000000000000000000000000000000009";
        // Only register Alice, Bob, TokenA — NOT WrapperAddr
        var mock = new MockLoadGraph { RegisteredAvatars = new List<string> { Alice, Bob, TokenA } };
        var factory = MakeFactory(mock);
        var bg = new BalanceGraph();
        int aliceId = AddressIdPool.IdOf(Alice);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        bg.AddAvatar(aliceId);
        bg.AddBalance(aliceId, tokenAId, 500, isWrapped: false, isStatic: false);

        // Bob trusts WrapperAddr token AND TokenA
        var trust = MakeTrust((Bob, WrapperAddr), (Bob, TokenA));

        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trust, request);

        int wrapperId = AddressIdPool.IdOf(WrapperAddr);
        // Unregistered wrapper must NOT be in AvatarNodes (fail-closed filter)
        Assert.That(cg.AvatarNodes.ContainsKey(wrapperId), Is.False,
            "Unregistered wrapper should be excluded from AvatarNodes");

        // Registered TokenA should still be in AvatarNodes
        Assert.That(cg.AvatarNodes.ContainsKey(tokenAId), Is.True,
            "Registered token should be in AvatarNodes");
    }

    #endregion

    #region Regression: Consent Flag False (Bug #2)

    /// <summary>
    /// Regression: avatars with HasConsentedFlow=false should NOT appear in ConsentedAvatars.
    /// If they leaked in, consent validation would incorrectly apply consented flow rules
    /// to non-consented avatars, breaking flow paths.
    /// </summary>
    [Test]
    public void GraphFactory_ConsentFlagFalse_ExcludedFromConsentedAvatars()
    {
        var mock = new HelperMock();
        mock.AddConsentedAvatar(Alice, hasConsentedFlow: true);
        mock.AddConsentedAvatar(Bob, hasConsentedFlow: false);
        mock.AddTrust(Bob, Alice);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice),
            AddressIdPool.IdOf(TokenA),
            200_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);
        var cg = factory.CreateCapacityGraph(bg, trustLookup);

        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        int bobId = AddressIdPool.IdOf(Bob.ToLowerInvariant());

        Assert.That(cg.ConsentedAvatars.Contains(aliceId), Is.True,
            "Alice (consent=true) should be in ConsentedAvatars");
        Assert.That(cg.ConsentedAvatars.Contains(bobId), Is.False,
            "Bob (consent=false) should NOT be in ConsentedAvatars");
    }

    #endregion

    #region Regression: Registered Avatar With No Edges (PR #191)

    /// <summary>
    /// Regression for PR #191: a registered avatar with zero balance and zero trust edges
    /// should still be a valid source/sink candidate. Before the fix, requesting maxFlow
    /// with such an avatar as source/sink would crash with "not in graph".
    /// </summary>
    [Test]
    public void GraphFactory_RegisteredAvatarWithNoEdges_IsValidSourceSink()
    {
        var lonely = "0xf1lo000000000000000000000000000000000010";
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(lonely);
        // Add some normal graph data so the graph isn't empty
        mock.AddTrust(Alice, Bob);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(Bob.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);
        var cg = factory.CreateCapacityGraph(bg, trustLookup);

        int lonelyId = AddressIdPool.IdOf(lonely.ToLowerInvariant());
        Assert.That(cg.AvatarNodes.ContainsKey(lonelyId), Is.True,
            "Registered avatar with no edges should be in AvatarNodes");
    }

    #endregion

    #region Regression: Group-Trusted Tokens as Avatar Nodes (PR #189)

    /// <summary>
    /// Regression for PR #189: tokens trusted by groups must be added as avatar nodes
    /// via AddAvatar(). Before the fix, group-trusted tokens that had no user trust
    /// or balance edges were missing from AvatarNodes, causing "Sink isn't in the graph
    /// snapshot" errors.
    /// </summary>
    [Test]
    public void GraphFactory_GroupTrustedTokens_RegisteredAsAvatarNodes()
    {
        // TokenB is ONLY reachable via group trust (GroupG trusts TokenB)
        // No user-to-user trust mentions TokenB, no balance holder is TokenB
        var mock = new HelperMock();
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenB);
        // Some normal data so graph isn't empty
        mock.AddTrust(Alice, TokenA);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);
        var cg = factory.CreateCapacityGraph(bg, trustLookup);

        int tokenBId = AddressIdPool.IdOf(TokenB.ToLowerInvariant());
        Assert.That(cg.AvatarNodes.ContainsKey(tokenBId), Is.True,
            "Group-trusted token should be registered as avatar node (DB path)");
    }

    [Test]
    public void GraphFactory_GroupSpecificRouterTrust_AllowsScoreGroupCollateral()
    {
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);
        var cg = factory.CreateCapacityGraph(bg, trustLookup);

        var tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        var groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var scoreRouterId = AddressIdPool.IdOf(ScoreRouterAddr.ToLowerInvariant());
        var poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        Assert.That(cg.RouterForGroup(groupId), Is.EqualTo(scoreRouterId));
        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.True,
            "Score group collateral should be gated by the score router trust, not the base router trust.");
    }

    [Test]
    public void GraphFactory_ScoreGroupMintLimit_CapsCollateralEdge()
    {
        // Score-group collateral edges are source-dependent (gated by Hub.isApprovedForAll
        // on the score router); they are reconstructed per-request from the filtered path,
        // not from the base snapshot. The test exercises the production path with Alice
        // as an approved operator of the score router.
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddScoreRouter(ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        mock.AddScoreGroupMintLimit(GroupG, TokenA, "25000000000000000000");
        mock.AddOperatorApproval(ScoreRouterAddr, Alice);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);
        var cg = factory.CreateCapacityGraph(bg, trustLookup,
            new FlowRequest { Source = Alice, Sink = Bob });

        var tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        var groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        var edge = cg.Edges.Single(e => e.From == poolId && e.To == groupId && e.Token == tokenAId);
        Assert.That(edge.InitialCapacity, Is.EqualTo(25_000_000));
    }

    private static HelperMock BuildScoreGroupMock(bool approveAlice)
    {
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddScoreRouter(ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        mock.AddScoreGroupMintLimit(GroupG, TokenA, "25000000000000000000");
        if (approveAlice)
            mock.AddOperatorApproval(ScoreRouterAddr, Alice);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);
        return mock;
    }

    /// <summary>
    /// Base snapshot is source-agnostic; score-router collateral edges are inherently
    /// source-dependent (gated by Hub.isApprovedForAll on the router). Including them in
    /// the shared snapshot causes pathfinder to emit reverting paths when the snapshot is
    /// served directly to a request with a source. Strip them unconditionally.
    /// </summary>
    [Test]
    public void GraphFactory_BaseSnapshot_StripsScoreRouterEdges_WhenSourceUnknown()
    {
        var mock = BuildScoreGroupMock(approveAlice: true);
        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        var cg = factory.CreateBaseCapacityGraph(bg, trustLookup);

        var tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        var groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.False,
            "Score-group collateral edges must not appear in the source-agnostic base snapshot.");
    }

    /// <summary>
    /// Filtered request with a source that the score router has NOT approved must drop the
    /// score-group collateral edge — emitting it would produce a path that reverts at
    /// Hub.operateFlowMatrix's isApprovedForAll check.
    /// </summary>
    [Test]
    public void GraphFactory_FilteredRequest_StripsScoreRouterEdges_ForUnapprovedSource()
    {
        var mock = BuildScoreGroupMock(approveAlice: false);
        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        var cg = factory.CreateCapacityGraph(bg, trustLookup,
            new FlowRequest { Source = Alice, Sink = Bob });

        var tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        var groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.False,
            "Unapproved source must not see score-group collateral edges.");
    }

    /// <summary>
    /// C1 regression: when CachedGroupData carries a non-null OperatorApprovals dict, the
    /// cached-rehydration path in LoadGroupsAndTrackRouter must hydrate the per-request
    /// graph's OperatorApprovals so the score-router gate at AddTokenPoolOutEdges sees the
    /// approval. Failure mode if the cache plumbing breaks: filtered requests fail-CLOSED
    /// → maxFlow=0 for approved users.
    /// </summary>
    [Test]
    public void GraphFactory_CachedGroupData_OperatorApprovals_Hydrated_PreservesApprovedRouterEdges()
    {
        var mock = BuildScoreGroupMock(approveAlice: true);
        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        int tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        int groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        int routerId = AddressIdPool.IdOf(ScoreRouterAddr.ToLowerInvariant());
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        var cached = new CachedGroupData(
            GroupNodes: new HashSet<int> { groupId },
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>> { [groupId] = new() { tokenAId } },
            ConsentedAvatars: new HashSet<int>(),
            RegisteredAvatarIds: new HashSet<int> { aliceId, tokenAId, groupId },
            WrapperToAvatar: new Dictionary<int, int>(),
            GroupRouters: new Dictionary<int, int> { [groupId] = routerId },
            ScoreGroupMintLimits: new Dictionary<(int, int), long> { [(groupId, tokenAId)] = 25_000_000L },
            OperatorApprovals: new Dictionary<int, HashSet<int>> { [routerId] = new() { aliceId } },
            ScoreRouterIds: new HashSet<int> { routerId });

        var cg = factory.CreateCapacityGraph(bg, trustLookup,
            new FlowRequest { Source = Alice, Sink = Bob }, cached);

        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.True,
            "Cached OperatorApprovals must hydrate the per-request graph so approved sources retain the score-router edge.");
    }

    /// <summary>
    /// C1 regression: when CachedGroupData.OperatorApprovals is null (the pre-fix bug shape
    /// at NetworkStateUpdaterService.cs lines 221/471/702), the cached path returns early
    /// without loading approvals from DB. Approved users then lose their score-router edges
    /// → maxFlow=0. This test pins the failure mode so it can't silently regress.
    /// </summary>
    [Test]
    public void GraphFactory_CachedGroupData_NullOperatorApprovals_DropsApprovedRouterEdges_C1Regression()
    {
        var mock = BuildScoreGroupMock(approveAlice: true);
        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        int tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        int groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        int routerId = AddressIdPool.IdOf(ScoreRouterAddr.ToLowerInvariant());
        int poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        var cached = new CachedGroupData(
            GroupNodes: new HashSet<int> { groupId },
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>> { [groupId] = new() { tokenAId } },
            ConsentedAvatars: new HashSet<int>(),
            RegisteredAvatarIds: new HashSet<int> { aliceId, tokenAId, groupId },
            WrapperToAvatar: new Dictionary<int, int>(),
            GroupRouters: new Dictionary<int, int> { [groupId] = routerId },
            ScoreGroupMintLimits: new Dictionary<(int, int), long> { [(groupId, tokenAId)] = 25_000_000L },
            OperatorApprovals: null,
            ScoreRouterIds: new HashSet<int> { routerId });

        var cg = factory.CreateCapacityGraph(bg, trustLookup,
            new FlowRequest { Source = Alice, Sink = Bob }, cached);

        Assert.That(cg.OperatorApprovals.Count, Is.EqualTo(0),
            "Null OperatorApprovals in cache leaves the per-request graph empty — production must populate the field at NetworkStateUpdaterService.cs lines 221/471/702.");
        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.False,
            "With null cached approvals, even approved users lose score-router edges (C1 fail-CLOSED).");
    }

    /// <summary>
    /// C2 regression: freshly-initialized score groups have an indexed GroupInitialized
    /// event (and therefore a pathMintRouter) BEFORE any mint-limit row or operator
    /// approval exists. The router must already be recognized as a score router so the
    /// gate fires fail-CLOSED on unapproved sources; otherwise the user attempts a mint
    /// path the contract immediately reverts.
    /// </summary>
    [Test]
    public void GraphFactory_FreshlyInitializedScoreGroup_NoMintLimitYet_GateFiresClosed()
    {
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddScoreRouter(ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        // intentionally NO AddScoreGroupMintLimit, NO AddOperatorApproval — mirrors
        // the post-initialize / pre-first-mint window the C2 fix targets.
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        var cg = factory.CreateCapacityGraph(bg, trustLookup,
            new FlowRequest { Source = Alice, Sink = Bob });

        var tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        var groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.False,
            "Freshly-initialized score router must be recognized (via ScoreRouterIds) and the gate must fail-CLOSED when no approval is present.");
    }

    /// <summary>
    /// C2: a group with a custom router that is NOT a score-group router (no entry in
    /// GroupInitialized) keeps its collateral edge — the operator-approval gate doesn't
    /// apply. Pins the boundary between score and non-score groups.
    /// </summary>
    [Test]
    public void GraphFactory_NonScoreGroupWithCustomRouter_EdgeKept()
    {
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        // No AddScoreRouter: this router is not in CrcV2_ScoreGroup_GroupInitialized.
        mock.AddTrust(ScoreRouterAddr, TokenA);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());

        var cg = factory.CreateCapacityGraph(bg, trustLookup,
            new FlowRequest { Source = Alice, Sink = Bob });

        var tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        var groupId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var poolId = AddressIdPool.TokenPoolIdOf(tokenAId);

        Assert.That(cg.Edges.Any(e => e.From == poolId && e.To == groupId && e.Token == tokenAId), Is.True,
            "Non-score groups must not be subject to the score-router operator-approval gate.");
    }

    [Test]
    public void GraphFactory_GroupRouterRows_DoNotAddUnsupportedGroups()
    {
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(TokenA);
        mock.AddGroup(GroupG);
        mock.AddGroupTrust(GroupG, TokenA);
        mock.AddGroupRouter(GroupG, ScoreRouterAddr);
        mock.AddGroupRouter(GroupH, ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);
        var cg = factory.CreateCapacityGraph(bg, trustLookup);

        var groupGId = AddressIdPool.IdOf(GroupG.ToLowerInvariant());
        var groupHId = AddressIdPool.IdOf(GroupH.ToLowerInvariant());

        Assert.That(cg.GroupRouters.ContainsKey(groupGId), Is.True);
        Assert.That(cg.GroupRouters.ContainsKey(groupHId), Is.False,
            "A router row must not create a group that was filtered out by LoadGroups.");
        Assert.That(cg.GroupNodes.Contains(groupHId), Is.False);
    }

    #endregion

    #region Regression: Cached vs DB Consent Consistency

    /// <summary>
    /// The cached group data path should produce identical ConsentedAvatars as the DB path.
    /// If caching logic diverges, consent validation would differ between fresh and cached
    /// requests — causing intermittent flow failures.
    /// </summary>
    [Test]
    public void CachedGroupData_ConsentSet_MatchesDBPath()
    {
        // DB path: consent loaded from LoadConsentedFlowFlags
        var mock = new HelperMock();
        mock.AddConsentedAvatar(Alice, true);
        mock.AddConsentedAvatar(Bob, false);
        mock.AddTrust(Bob, Alice);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factoryDb = new GraphFactory(RouterAddr, mock);
        var bgDb = factoryDb.V2BalanceGraph();
        var tgDb = factoryDb.V2TrustGraph();
        var trustDb = GraphFactory.BuildTrustLookup(tgDb);
        var cgDb = factoryDb.CreateCapacityGraph(bgDb, trustDb);

        // Cached path: consent provided via CachedGroupData
        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        var cached = new CachedGroupData(
            GroupNodes: new HashSet<int>(),
            OrganizationNodes: new HashSet<int>(),
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>(),
            ConsentedAvatars: new HashSet<int> { aliceId },
            RegisteredAvatarIds: new HashSet<int>(),
            WrapperToAvatar: new Dictionary<int, int>()
        );

        var mock2 = new HelperMock();
        mock2.AddTrust(Bob, Alice);
        mock2.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            100_000_000);

        var factoryCached = new GraphFactory(RouterAddr, mock2);
        var bgCached = factoryCached.V2BalanceGraph();
        var tgCached = factoryCached.V2TrustGraph();
        var trustCached = GraphFactory.BuildTrustLookup(tgCached);
        var cgCached = factoryCached.CreateCapacityGraph(bgCached, trustCached, cachedGroupData: cached);

        // Both should agree on who is consented
        Assert.That(cgDb.ConsentedAvatars.Contains(aliceId), Is.EqualTo(cgCached.ConsentedAvatars.Contains(aliceId)),
            "DB and cached paths should agree on Alice's consent status");
        int bobId = AddressIdPool.IdOf(Bob.ToLowerInvariant());
        Assert.That(cgDb.ConsentedAvatars.Contains(bobId), Is.EqualTo(cgCached.ConsentedAvatars.Contains(bobId)),
            "DB and cached paths should agree on Bob's consent status (both false)");
    }

    #endregion

    #region Regression: IsWrapped Balance Filtering (Bug #6)

    /// <summary>
    /// Regression: wrapped balances must produce zero capacity edges when WithWrap=false.
    /// If wrapping filter is broken, wrapper addresses (which Hub.sol rejects as flow
    /// vertices) would leak into the transfer path → ERC1155InvalidReceiver revert.
    /// </summary>
    [Test]
    public void GraphFactory_IsWrappedBalance_SkippedWithoutWithWrapFlag()
    {
        var mock = new HelperMock();
        var wrapperId = AddressIdPool.IdOf("0xf1wr000000000000000000000000000000000020".ToLowerInvariant());
        var aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());

        // Alice holds a WRAPPED token
        mock.AddBalance(aliceId, wrapperId, 500_000_000, isWrapped: true);
        mock.AddTrust(Bob, Alice);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        // WithWrap=false (default)
        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        // Alice should have no outbound edges to the wrapper's pool
        int wrapperPoolId = AddressIdPool.TokenPoolIdOf(wrapperId);
        var wrapperEdges = cg.Edges.Where(e => e.From == aliceId && e.To == wrapperPoolId).ToList();
        Assert.That(wrapperEdges, Is.Empty,
            "Wrapped balance should not produce capacity edges when WithWrap=false");
    }

    #endregion

    #region Router Exclusion

    [Test]
    public void CreateCapacityGraph_RouterBalance_SkippedInEdges()
    {
        var factory = MakeFactory();
        // Router holds TokenA — should NOT generate H→Pool edge
        var balance = MakeBalanceGraph((RouterAddr, TokenA, 999));
        var trust = MakeTrust((Bob, TokenA));

        var cg = factory.CreateCapacityGraph(balance, trust);

        int routerId = AddressIdPool.IdOf(RouterAddr);
        // No outbound edges from router to token pools
        Assert.That(cg.Edges.Any(e => e.From == routerId), Is.False,
            "Router should have no outbound token pool edges");
    }

    #endregion

    #region Wrapper Filter Expansion (withWrap + toTokens/fromTokens/excluded)

    /// <summary>
    /// Main bug: withWrap=true + toTokens=[avatarAddr] should expand toTokensFilter
    /// to include wrapper IDs, allowing wrapped token flow to reach the sink.
    /// Without expansion, TokenPool(wrapper)→Sink edge is blocked.
    /// </summary>
    [Test]
    public void WithWrap_ToTokens_ExpandsFilterWithWrapperIds()
    {
        var mock = new HelperMock();
        // WrapperA wraps TokenA's underlying avatar
        mock.AddWrapperMapping(WrapperA, TokenA);
        // Alice holds wrapped balance (token = wrapper contract addr)
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperA.ToLowerInvariant()),
            500_000_000, isWrapped: true);
        // Bob trusts both TokenA (native) and WrapperA (via trust query UNION 2)
        mock.AddTrust(Bob, TokenA);
        mock.AddTrust(Bob, WrapperA);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = true,
            ToTokens = new List<string> { TokenA } // avatar address, not wrapper
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int bobId = AddressIdPool.IdOf(Bob.ToLowerInvariant());
        int wrapperId = AddressIdPool.IdOf(WrapperA.ToLowerInvariant());
        int wrapperPool = AddressIdPool.TokenPoolIdOf(wrapperId);

        // TokenPool(wrapper)→Bob edge should exist because toTokensFilter was expanded
        Assert.That(cg.Edges.Any(e => e.From == wrapperPool && e.To == bobId && e.Token == wrapperId), Is.True,
            "Wrapper token pool→sink edge should exist when toTokensFilter is expanded with wrapper IDs");
    }

    /// <summary>
    /// withWrap=false should NOT expand filters — wrapped balances are excluded entirely.
    /// </summary>
    [Test]
    public void WithWrapFalse_ToTokens_NoExpansion()
    {
        var mock = new HelperMock();
        mock.AddWrapperMapping(WrapperA, TokenA);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperA.ToLowerInvariant()),
            500_000_000, isWrapped: true);
        mock.AddTrust(Bob, TokenA);
        mock.AddTrust(Bob, WrapperA);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = false,
            ToTokens = new List<string> { TokenA }
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        int wrapperId = AddressIdPool.IdOf(WrapperA.ToLowerInvariant());
        int wrapperPool = AddressIdPool.TokenPoolIdOf(wrapperId);

        // No wrapper edges at all — wrapped balances skipped
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == wrapperPool), Is.False,
            "Wrapped balance should not produce source edge when withWrap=false");
    }

    /// <summary>
    /// Multiple wrappers for different avatars should all be expanded.
    /// </summary>
    [Test]
    public void WithWrap_ToTokens_MultipleWrappers_AllExpanded()
    {
        var mock = new HelperMock();
        mock.AddWrapperMapping(WrapperA, TokenA);
        mock.AddWrapperMapping(WrapperB, TokenB);
        // Alice holds both wrapped tokens
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperA.ToLowerInvariant()),
            300_000_000, isWrapped: true);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperB.ToLowerInvariant()),
            400_000_000, isWrapped: true);
        // Bob trusts everything
        mock.AddTrust(Bob, TokenA);
        mock.AddTrust(Bob, TokenB);
        mock.AddTrust(Bob, WrapperA);
        mock.AddTrust(Bob, WrapperB);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = true,
            ToTokens = new List<string> { TokenA, TokenB }
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int bobId = AddressIdPool.IdOf(Bob.ToLowerInvariant());
        int wrapperAId = AddressIdPool.IdOf(WrapperA.ToLowerInvariant());
        int wrapperBId = AddressIdPool.IdOf(WrapperB.ToLowerInvariant());
        int poolA = AddressIdPool.TokenPoolIdOf(wrapperAId);
        int poolB = AddressIdPool.TokenPoolIdOf(wrapperBId);

        Assert.That(cg.Edges.Any(e => e.From == poolA && e.To == bobId), Is.True,
            "WrapperA pool→sink should exist");
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == bobId), Is.True,
            "WrapperB pool→sink should exist");
    }

    /// <summary>
    /// withWrap=true but empty toTokens → no expansion needed, no crash.
    /// </summary>
    [Test]
    public void WithWrap_EmptyToTokens_NoExpansion_NoCrash()
    {
        var mock = new HelperMock();
        mock.AddWrapperMapping(WrapperA, TokenA);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperA.ToLowerInvariant()),
            500_000_000, isWrapped: true);
        mock.AddTrust(Bob, WrapperA);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = true
            // No ToTokens — no whitelist active
        };

        Assert.DoesNotThrow(() => factory.CreateCapacityGraph(bg, trustLookup, request));
    }

    /// <summary>
    /// fromTokens=[avatarAddr] with withWrap=true should also allow the wrapper balance.
    /// Without expansion, the wrapper is blocked at source because fromTokensFilter
    /// doesn't contain the wrapper ID.
    /// </summary>
    [Test]
    public void WithWrap_FromTokens_ExpandsFilterWithWrapperIds()
    {
        var mock = new HelperMock();
        mock.AddWrapperMapping(WrapperA, TokenA);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperA.ToLowerInvariant()),
            500_000_000, isWrapped: true);
        mock.AddTrust(Bob, WrapperA);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = true,
            FromTokens = new List<string> { TokenA } // avatar address
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        int wrapperId = AddressIdPool.IdOf(WrapperA.ToLowerInvariant());
        int wrapperPool = AddressIdPool.TokenPoolIdOf(wrapperId);

        // Source→TokenPool(wrapper) edge should exist
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == wrapperPool), Is.True,
            "Wrapped source balance should be allowed when fromTokensFilter is expanded");
    }

    /// <summary>
    /// excludedFromTokens=[avatarAddr] with withWrap=true should also exclude the wrapper.
    /// Without expansion, the wrapper leaks through as a source edge.
    /// </summary>
    [Test]
    public void WithWrap_ExcludedFromTokens_ExpandsFilterWithWrapperIds()
    {
        var mock = new HelperMock();
        mock.AddWrapperMapping(WrapperA, TokenA);
        // Alice holds both native AND wrapped token
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            300_000_000, isWrapped: false);
        mock.AddBalance(
            AddressIdPool.IdOf(Alice.ToLowerInvariant()),
            AddressIdPool.IdOf(WrapperA.ToLowerInvariant()),
            500_000_000, isWrapped: true);
        mock.AddTrust(Bob, TokenA);
        mock.AddTrust(Bob, WrapperA);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            WithWrap = true,
            ExcludedFromTokens = new List<string> { TokenA } // exclude avatar → should also exclude wrapper
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int aliceId = AddressIdPool.IdOf(Alice.ToLowerInvariant());
        int tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        int wrapperId = AddressIdPool.IdOf(WrapperA.ToLowerInvariant());
        int poolNative = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolWrapper = AddressIdPool.TokenPoolIdOf(wrapperId);

        // Both native and wrapped should be excluded from source
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolNative), Is.False,
            "Native token should be excluded by excludedFromTokens");
        Assert.That(cg.Edges.Any(e => e.From == aliceId && e.To == poolWrapper), Is.False,
            "Wrapped token should also be excluded when excludedFromTokens is expanded");
    }

    /// <summary>
    /// B7: excludedToTokens must also be applied to the virtual sink in swap mode.
    /// Without this fix, excludedToTokens had no effect on what the virtual sink accepted.
    /// </summary>
    [Test]
    public void SwapMode_ExcludedToTokens_AppliedToVirtualSink()
    {
        var mock = new HelperMock();
        // Alice holds some other token (not in toTokens) to generate outbound supply
        // Bob holds TokenA and TokenB so pool nodes get created (Alice's are excluded in swap mode)
        mock.AddBalance(
            AddressIdPool.IdOf(Bob.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenA.ToLowerInvariant()),
            500_000_000);
        mock.AddBalance(
            AddressIdPool.IdOf(Bob.ToLowerInvariant()),
            AddressIdPool.IdOf(TokenB.ToLowerInvariant()),
            300_000_000);
        // Alice trusts both (needed for swap/virtual sink to accept)
        mock.AddTrust(Alice, TokenA);
        mock.AddTrust(Alice, TokenB);
        // Bob trusts Alice's tokens too (needed for pool outbound edges)
        mock.AddTrust(Bob, TokenA);
        mock.AddTrust(Bob, TokenB);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var tg = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(tg);

        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { TokenA, TokenB },
            ExcludedToTokens = new List<string> { TokenB } // exclude TokenB from receiving
        };

        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        Assert.That(cg.VirtualSinkAddress, Is.Not.Null, "Virtual sink should be created for swap mode");

        int virtualSinkId = cg.VirtualSinkAddress!.Value;
        int tokenAId = AddressIdPool.IdOf(TokenA.ToLowerInvariant());
        int tokenBId = AddressIdPool.IdOf(TokenB.ToLowerInvariant());
        int poolA = AddressIdPool.TokenPoolIdOf(tokenAId);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenBId);

        // Virtual sink should accept TokenA but NOT TokenB
        Assert.That(cg.Edges.Any(e => e.From == poolA && e.To == virtualSinkId), Is.True,
            "TokenA pool→virtualSink edge should exist");
        Assert.That(cg.Edges.Any(e => e.From == poolB && e.To == virtualSinkId), Is.False,
            "TokenB pool→virtualSink should be excluded by excludedToTokens");
    }

    #endregion

    #region Mock

    /// <summary>
    /// Minimal ILoadGraph mock that returns configurable in-memory data.
    /// </summary>
    private class MockLoadGraph : ILoadGraph
    {
        public List<string> Groups { get; set; } = new();
        public List<(string GroupAddress, string TrustedToken)> GroupTrusts { get; set; } = new();
        public List<(string Avatar, bool HasConsentedFlow)> ConsentedFlags { get; set; } = new();
        public List<string> RegisteredAvatars { get; set; } = new();

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
            => Enumerable.Empty<(string, int, int, bool, bool)>();

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
            => Enumerable.Empty<(string, string, int)>();

        public IEnumerable<string> LoadGroups() => Groups;
        public IEnumerable<string> LoadOrganizations() => [];
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts() => GroupTrusts;
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags() => ConsentedFlags;
        public IEnumerable<string> LoadRegisteredAvatars() => RegisteredAvatars;
        public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, int CirclesType)> LoadWrapperMappings()
            => Array.Empty<(string, string, int)>();
    }

    #endregion

    #region Registration Filter Tests

    // Unregistered token address used for filter tests
    private static readonly string UnregisteredToken = "0xf1dd000000000000000000000000000000000099";

    [Test]
    public void AddAllAvatarNodes_UnregisteredTokenId_ExcludedFromGraph()
    {
        // Arrange: Alice trusts both TokenA (registered) and UnregisteredToken
        var mock = new MockLoadGraph
        {
            RegisteredAvatars = new List<string> { Alice, Bob, TokenA }
        };
        var factory = MakeFactory(mock);
        var bg = MakeBalanceGraph((Alice, TokenA, 1000));

        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [AddressIdPool.IdOf(Alice)] = new HashSet<int>
            {
                AddressIdPool.IdOf(TokenA),
                AddressIdPool.IdOf(UnregisteredToken)
            }
        };

        // Act
        var cg = factory.CreateBaseCapacityGraph(bg, trustLookup);

        // Assert: registered token is an avatar node, unregistered is NOT
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int unregId = AddressIdPool.IdOf(UnregisteredToken);

        Assert.That(cg.AvatarNodes.ContainsKey(tokenAId), Is.True,
            "Registered token should be an avatar node");
        Assert.That(cg.AvatarNodes.ContainsKey(unregId), Is.False,
            "Unregistered token must NOT be an avatar node");
    }

    [Test]
    public void CreateBaseCapacityGraph_RegisteredSetPopulatedBeforeAvatarNodes()
    {
        // Arrange: set up registered avatars — ensure they appear in RegisteredAvatarIds
        var mock = new MockLoadGraph
        {
            RegisteredAvatars = new List<string> { Alice, Bob, TokenA, TokenB }
        };
        var factory = MakeFactory(mock);
        var bg = MakeBalanceGraph((Alice, TokenA, 500));
        var trustLookup = new Dictionary<int, HashSet<int>>();

        // Act
        var cg = factory.CreateBaseCapacityGraph(bg, trustLookup);

        // Assert: all registered avatars are in RegisteredAvatarIds
        Assert.That(cg.RegisteredAvatarIds.Contains(AddressIdPool.IdOf(Alice)), Is.True);
        Assert.That(cg.RegisteredAvatarIds.Contains(AddressIdPool.IdOf(Bob)), Is.True);
        Assert.That(cg.RegisteredAvatarIds.Contains(AddressIdPool.IdOf(TokenA)), Is.True);
        Assert.That(cg.RegisteredAvatarIds.Contains(AddressIdPool.IdOf(TokenB)), Is.True);
    }

    [Test]
    public void CreateCapacityGraph_Filtered_UnregisteredTokenExcluded()
    {
        // Arrange: filtered path with cached group data
        var mock = new MockLoadGraph
        {
            RegisteredAvatars = new List<string> { Alice, TokenA }
        };
        var factory = MakeFactory(mock);
        var bg = MakeBalanceGraph((Alice, TokenA, 1000));

        var trustLookup = new Dictionary<int, HashSet<int>>
        {
            [AddressIdPool.IdOf(Alice)] = new HashSet<int>
            {
                AddressIdPool.IdOf(TokenA),
                AddressIdPool.IdOf(UnregisteredToken)
            }
        };

        var request = new FlowRequest { Source = Alice, Sink = TokenA, TargetFlow = "1000" };

        // Act
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        // Assert
        int unregId = AddressIdPool.IdOf(UnregisteredToken);
        Assert.That(cg.AvatarNodes.ContainsKey(unregId), Is.False,
            "Unregistered token must NOT appear in filtered capacity graph");
    }

    #endregion

    #region ScoreGroup wrapped-only guards

    // ScoreGroup CRC is wrapped-only by design: the unwrapped ERC1155 may appear
    // ONLY on the terminal Group→sink mint edge (recipient wraps off-graph). It
    // must never be delivered to a non-sink intermediary, never sit as a holder
    // balance, and never be forwarded transitively. A group is a "score group"
    // iff its router is in ScoreRouterIds (canonical, immutable post-init).

    private static readonly string ScoreGroupSg = "0xf15c000000000000000000000000000000000021";
    private static readonly string Carol = "0xf1cc000000000000000000000000000000000004";

    /// <summary>Builds a HelperMock whose GroupSg is a score group collateralised by TokenA.</summary>
    private static HelperMock MakeScoreGroupMock()
    {
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(Bob);
        mock.AddRegisteredAvatar(Carol);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddRegisteredAvatar(ScoreGroupSg);
        mock.AddGroup(ScoreGroupSg);
        mock.AddGroupTrust(ScoreGroupSg, TokenA);
        mock.AddGroupRouter(ScoreGroupSg, ScoreRouterAddr);
        mock.AddScoreRouter(ScoreRouterAddr);
        mock.AddTrust(ScoreRouterAddr, TokenA);
        mock.AddScoreGroupMintLimit(ScoreGroupSg, TokenA, "25000000000000000000");
        mock.AddOperatorApproval(ScoreRouterAddr, Alice);
        return mock;
    }

    [Test]
    public void ScoreGroupCrc_MintedOnlyToSink_NotToNonSinkTruster()
    {
        // Alice (collateral holder) → Bob (sink). Bob and Carol both trust the
        // score group's CRC, but only the sink may receive the freshly minted
        // unwrapped ERC1155 — Carol (a non-sink intermediary) must not.
        var mock = MakeScoreGroupMock();
        mock.AddTrust(Bob, ScoreGroupSg);
        mock.AddTrust(Carol, ScoreGroupSg);
        mock.AddBalance(AddressIdPool.IdOf(Alice), AddressIdPool.IdOf(TokenA), 100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Bob,
            ToTokens = new List<string> { ScoreGroupSg }
        };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int sgId = AddressIdPool.IdOf(ScoreGroupSg);
        int bobId = AddressIdPool.IdOf(Bob);
        int carolId = AddressIdPool.IdOf(Carol);

        Assert.That(cg.IsScoreGroup(sgId), Is.True,
            "GroupSg's router is a score router → it must be recognised as a score group.");

        var toSink = cg.Edges.Where(e => e.From == sgId && e.To == bobId && e.Token == sgId).ToList();
        Assert.That(toSink.Count, Is.EqualTo(1),
            "The terminal Group→sink mint edge for the score-group CRC must exist.");

        var toCarol = cg.Edges.Where(e => e.From == sgId && e.To == carolId && e.Token == sgId).ToList();
        Assert.That(toCarol.Count, Is.EqualTo(0),
            "Score-group CRC must NOT be minted to a non-sink intermediary.");
    }

    [Test]
    public void RegularGroupCrc_StillMintedToNonSinkTruster_NegativeControl()
    {
        // Identical topology but GroupG is a REGULAR group (no score router).
        // The guard must not affect it: Group→Carol (non-sink) still exists.
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(Bob);
        mock.AddRegisteredAvatar(Carol);
        mock.AddRegisteredAvatar(TokenA);
        mock.AddRegisteredAvatar(GroupG);
        mock.AddGroup(GroupG);
        mock.AddTrust(Bob, GroupG);
        mock.AddTrust(Carol, GroupG);
        mock.AddBalance(AddressIdPool.IdOf(Alice), AddressIdPool.IdOf(TokenA), 100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int groupId = AddressIdPool.IdOf(GroupG);
        int carolId = AddressIdPool.IdOf(Carol);

        Assert.That(cg.IsScoreGroup(groupId), Is.False,
            "A group with no score router must not be treated as a score group.");
        var toCarol = cg.Edges.Where(e => e.From == groupId && e.To == carolId && e.Token == groupId).ToList();
        Assert.That(toCarol.Count, Is.EqualTo(1),
            "Regular group CRC remains transitively mintable to non-sink trusters (unchanged behaviour).");
    }

    [Test]
    public void ScoreGroupCrc_HolderBalance_NotRoutable()
    {
        // Bob already holds the score group's unwrapped CRC. It must not become
        // routing liquidity: no Holder→TokenPool edge, no pool node at all.
        var mock = MakeScoreGroupMock();
        mock.AddBalance(AddressIdPool.IdOf(Bob), AddressIdPool.IdOf(ScoreGroupSg), 5_000_000);
        mock.AddBalance(AddressIdPool.IdOf(Alice), AddressIdPool.IdOf(TokenA), 100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        var request = new FlowRequest { Source = Alice, Sink = Carol };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int sgId = AddressIdPool.IdOf(ScoreGroupSg);
        int bobId = AddressIdPool.IdOf(Bob);
        int sgPool = AddressIdPool.TokenPoolIdOf(sgId);

        Assert.That(cg.Nodes.ContainsKey(sgPool), Is.False,
            "No TokenPool node may be created for an unwrapped score-group CRC balance.");
        var holderEdges = cg.Edges.Where(e => e.From == bobId && e.Token == sgId).ToList();
        Assert.That(holderEdges.Count, Is.EqualTo(0),
            "A holder of unwrapped score-group CRC must have no outgoing routable edge for it.");
    }

    [Test]
    public void RegularGroupCrc_HolderBalance_Routable_NegativeControl()
    {
        // Counterpart: a regular group's CRC held by Bob IS routable (pool node
        // + holder edge present), proving the holder guard is score-group-scoped.
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(Bob);
        mock.AddRegisteredAvatar(GroupG);
        mock.AddGroup(GroupG);
        mock.AddBalance(AddressIdPool.IdOf(Bob), AddressIdPool.IdOf(GroupG), 5_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        var request = new FlowRequest { Source = Alice, Sink = Bob };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int groupId = AddressIdPool.IdOf(GroupG);
        int bobId = AddressIdPool.IdOf(Bob);
        int gPool = AddressIdPool.TokenPoolIdOf(groupId);

        Assert.That(cg.Nodes.ContainsKey(gPool), Is.True,
            "Regular group CRC balances still create a TokenPool node.");
        var holderEdges = cg.Edges.Where(e => e.From == bobId && e.To == gPool && e.Token == groupId).ToList();
        Assert.That(holderEdges.Count, Is.EqualTo(1),
            "Regular group CRC remains routable from a holder (unchanged behaviour).");
    }

    [Test]
    public void SelfMintViaPath_ScoreGroup_VirtualSinkFallbackSurvivesGuards()
    {
        // The wrapped-only guards must NOT break legitimate self-mint
        // (source==sink, toTokens=[scoreGroup]). The Group→virtualSink fallback
        // (PR #397) resolves to Group→sink, the one permitted terminal edge.
        var mock = MakeScoreGroupMock();
        mock.AddTrust(Alice, ScoreGroupSg); // recipient trusts SG so it survives the sink-side filter
        mock.AddBalance(AddressIdPool.IdOf(Alice), AddressIdPool.IdOf(TokenA), 100_000_000);

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        var request = new FlowRequest
        {
            Source = Alice,
            Sink = Alice,
            ToTokens = new List<string> { ScoreGroupSg }
        };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int sgId = AddressIdPool.IdOf(ScoreGroupSg);
        int tokenAId = AddressIdPool.IdOf(TokenA);
        int sgPool = AddressIdPool.TokenPoolIdOf(tokenAId);

        Assert.That(cg.VirtualSinkAddress, Is.Not.Null,
            "Self-mint must still produce a surviving virtual sink under the guards.");
        int vSink = cg.VirtualSinkAddress!.Value;

        var fallback = cg.Edges.Where(e => e.From == sgId && e.To == vSink && e.Token == sgId).ToList();
        Assert.That(fallback.Count, Is.EqualTo(1),
            "Group→virtualSink self-mint fallback must still fire for a score group.");

        var capEdge = cg.Edges.Single(e => e.From == sgPool && e.To == sgId && e.Token == tokenAId);
        Assert.That(capEdge.InitialCapacity, Is.EqualTo(25_000_000),
            "Collateral mint inflow (pool(collateral)→group) is unaffected by the guards.");
    }

    [Test]
    public void ScoreGroupReusingStandardRouter_DoesNotMisflagRegularGroups()
    {
        // Catastrophic-misfire guard: if a score group is ever initialized with
        // the STANDARD group router as its pathMintRouter, ScoreRouterIds would
        // contain the standard router id. Every regular group is assigned that
        // same router, so without the RouterNode exclusion in IsScoreGroup the
        // wrap-only guard would strip ALL regular-group routing. Verify the
        // exclusion holds: a regular group on the standard router stays routable.
        var mock = new HelperMock();
        mock.AddRegisteredAvatar(Alice);
        mock.AddRegisteredAvatar(Bob);
        mock.AddRegisteredAvatar(GroupG);
        mock.AddGroup(GroupG);
        // Regular group explicitly on the standard router (mirrors prod
        // groupRouterQuery.sql ELSE @standardRouter behaviour).
        mock.AddGroupRouter(GroupG, RouterAddr);
        // Simulate the adverse on-chain fact: standard router shows up as a score
        // router (a score group somewhere used it as pathMintRouter).
        mock.AddScoreRouter(RouterAddr);
        mock.AddTrust(Bob, GroupG); // Bob is a non-sink truster

        var factory = new GraphFactory(RouterAddr, mock);
        var bg = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        var request = new FlowRequest { Source = Alice, Sink = Carol };
        var cg = factory.CreateCapacityGraph(bg, trustLookup, request);

        int groupId = AddressIdPool.IdOf(GroupG);
        int bobId = AddressIdPool.IdOf(Bob);

        Assert.That(cg.IsScoreGroup(groupId), Is.False,
            "A group on the standard router must NEVER be treated as a score group, " +
            "even if the standard router id leaked into ScoreRouterIds.");
        var toBob = cg.Edges.Where(e => e.From == groupId && e.To == bobId && e.Token == groupId).ToList();
        Assert.That(toBob.Count, Is.EqualTo(1),
            "Regular-group routing must survive a standard-router/score-router collision.");
    }

    #endregion
}
