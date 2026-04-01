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
        public IEnumerable<(string WrapperAddress, string UnderlyingAvatar)> LoadWrapperMappings()
            => Array.Empty<(string, string)>();
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
}
