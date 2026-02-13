using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for GraphFactory: capacity graph construction, filtering,
/// simulated balances/trusts, virtual sink, group minting, and validation.
/// Uses a mock ILoadGraph to avoid DB dependency.
/// </summary>
[TestFixture]
public class GraphFactoryTests
{
    // Valid 42-char hex addresses (0x + 40 hex) — required for IsValidEthereumAddress checks
    private static readonly string RouterAddr = "0xf1ff000000000000000000000000000000000001";
    private static readonly string Alice      = "0xf1aa000000000000000000000000000000000002";
    private static readonly string Bob        = "0xf1bb000000000000000000000000000000000003";
    // Carol available for future multi-hop tests
    // private static readonly string Carol   = "0xf1cc000000000000000000000000000000000004";
    private static readonly string TokenA     = "0xf10a000000000000000000000000000000000005";
    private static readonly string TokenB     = "0xf10b000000000000000000000000000000000006";
    private static readonly string GroupG     = "0xf199000000000000000000000000000000000007";
    private static readonly string GroupH     = "0xf198000000000000000000000000000000000008";

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
        var factory = MakeFactory();
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
            GroupTrustedTokens: new Dictionary<int, HashSet<int>>
            {
                { groupGId, new HashSet<int> { tokenAId } }
            },
            ConsentedAvatars: new HashSet<int>());

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

    #region Mock

    /// <summary>
    /// Minimal ILoadGraph mock that returns configurable in-memory data.
    /// </summary>
    private class MockLoadGraph : ILoadGraph
    {
        public List<string> Groups { get; set; } = new();
        public List<(string GroupAddress, string TrustedToken)> GroupTrusts { get; set; } = new();
        public List<(string Avatar, bool HasConsentedFlow)> ConsentedFlags { get; set; } = new();

        public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
            => Enumerable.Empty<(string, int, int, bool, bool)>();

        public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
            => Enumerable.Empty<(string, string, int)>();

        public IEnumerable<string> LoadGroups() => Groups;
        public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts() => GroupTrusts;
        public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags() => ConsentedFlags;
    }

    #endregion
}
