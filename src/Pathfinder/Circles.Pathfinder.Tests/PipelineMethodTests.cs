using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Direct unit tests for V2Pathfinder internal pipeline methods:
/// CollapseSinglePathToEdges, PathHasConsentViolation, InsertRouterInTransfers.
/// </summary>
[TestFixture, Parallelizable]
public class PipelineMethodTests
{
    private V2Pathfinder _pathfinder = null!;

    // Stable addresses (hex-valid, won't collide with other test classes)
    private static readonly int A = AddressIdPool.IdOf("0xdd01pipeline_a");
    private static readonly int B = AddressIdPool.IdOf("0xdd02pipeline_b");
    private static readonly int C = AddressIdPool.IdOf("0xdd03pipeline_c");
    private static readonly int D = AddressIdPool.IdOf("0xdd04pipeline_d");
    private static readonly int Group1 = AddressIdPool.IdOf("0xdd05pipeline_group1");
    private static readonly int Router = AddressIdPool.IdOf("0xdd06pipeline_router");

    private static readonly int TokenA = AddressIdPool.IdOf("0xdd07pipeline_tokenA");
    private static readonly int TokenB = AddressIdPool.IdOf("0xdd08pipeline_tokenB");
    private static readonly int TokenGroup = AddressIdPool.IdOf("0xdd09pipeline_tokenGroup");

    [SetUp]
    public void SetUp()
    {
        _pathfinder = new V2Pathfinder();
    }

    #region CollapseSinglePathToEdges

    [Test]
    public void Collapse_AvatarPoolAvatar_CollapsesToDirectEdge()
    {
        var graph = MakeGraph();
        var pool = AddressIdPool.TokenPoolIdOf(TokenA);

        var path = new List<FlowEdge>
        {
            new(A, pool, TokenA, 1000) { Flow = 500 },
            new(pool, B, TokenA, 1000) { Flow = 500 },
        };

        var result = _pathfinder.CollapseSinglePathToEdges(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(A));
        Assert.That(result[0].To, Is.EqualTo(B));
        Assert.That(result[0].Token, Is.EqualTo(TokenA));
        Assert.That(result[0].Flow, Is.EqualTo(500));
    }

    [Test]
    public void Collapse_AvatarPoolGroup_CollapsesToAvatarGroup()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        var pool = AddressIdPool.TokenPoolIdOf(TokenA);

        var path = new List<FlowEdge>
        {
            new(A, pool, TokenA, 1000) { Flow = 300 },
            new(pool, Group1, TokenA, 1000) { Flow = 300 },
        };

        var result = _pathfinder.CollapseSinglePathToEdges(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(A));
        Assert.That(result[0].To, Is.EqualTo(Group1));
    }

    [Test]
    public void Collapse_GroupToAvatar_KeptAsIs()
    {
        var graph = MakeGraph(groups: new[] { Group1 });

        var path = new List<FlowEdge>
        {
            new(Group1, B, TokenGroup, 1000) { Flow = 200 },
        };

        var result = _pathfinder.CollapseSinglePathToEdges(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Group1));
        Assert.That(result[0].To, Is.EqualTo(B));
        Assert.That(result[0].Token, Is.EqualTo(TokenGroup));
    }

    [Test]
    public void Collapse_DirectAvatarToAvatar_KeptAsIs()
    {
        var graph = MakeGraph();

        var path = new List<FlowEdge>
        {
            new(A, B, TokenA, 1000) { Flow = 100 },
        };

        var result = _pathfinder.CollapseSinglePathToEdges(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(A));
        Assert.That(result[0].To, Is.EqualTo(B));
    }

    [Test]
    public void Collapse_EmptyPath_ReturnsEmpty()
    {
        var graph = MakeGraph();
        var result = _pathfinder.CollapseSinglePathToEdges(new List<FlowEdge>(), graph);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Collapse_MultiHop_CollapsesEachPoolSegment()
    {
        // A → Pool(TokenA) → B → Pool(TokenB) → C
        var graph = MakeGraph();
        var poolA = AddressIdPool.TokenPoolIdOf(TokenA);
        var poolB = AddressIdPool.TokenPoolIdOf(TokenB);

        var path = new List<FlowEdge>
        {
            new(A, poolA, TokenA, 1000) { Flow = 400 },
            new(poolA, B, TokenA, 1000) { Flow = 400 },
            new(B, poolB, TokenB, 1000) { Flow = 400 },
            new(poolB, C, TokenB, 1000) { Flow = 400 },
        };

        var result = _pathfinder.CollapseSinglePathToEdges(path, graph);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo((A, B, TokenA, 400L)));
        Assert.That(result[1], Is.EqualTo((B, C, TokenB, 400L)));
    }

    [Test]
    public void Collapse_MismatchedFlow_TakesMinimum()
    {
        var graph = MakeGraph();
        var pool = AddressIdPool.TokenPoolIdOf(TokenA);

        var path = new List<FlowEdge>
        {
            new(A, pool, TokenA, 1000) { Flow = 300 },
            new(pool, B, TokenA, 1000) { Flow = 200 }, // lower flow
        };

        var result = _pathfinder.CollapseSinglePathToEdges(path, graph);

        Assert.That(result[0].Flow, Is.EqualTo(200), "Should take Math.Min of both flows");
    }

    #endregion

    #region PathHasConsentViolation

    [Test]
    public void Consent_NoConsentData_NoViolation()
    {
        var graph = MakeGraph();
        // No ConsentedAvatars, no TrustLookup
        var edges = new List<(int From, int To, int Token, long Flow)> { (A, B, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.False);
    }

    [Test]
    public void Consent_FromNotConsented_NoViolation()
    {
        var graph = MakeGraph(consented: new[] { B }, trustLookup: new());
        var edges = new List<(int From, int To, int Token, long Flow)> { (A, B, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.False,
            "A is not consented → standard trust → no violation");
    }

    [Test]
    public void Consent_FromConsented_ToNotConsented_Violation()
    {
        var graph = MakeGraph(
            consented: new[] { A },
            trustLookup: new() { { A, new HashSet<int> { B } } });

        var edges = new List<(int From, int To, int Token, long Flow)> { (A, B, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.True,
            "A consented, B not consented → violation");
    }

    [Test]
    public void Consent_BothConsented_FromTrustsTo_NoViolation()
    {
        var graph = MakeGraph(
            consented: new[] { A, B },
            trustLookup: new() { { A, new HashSet<int> { B } } });

        var edges = new List<(int From, int To, int Token, long Flow)> { (A, B, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.False);
    }

    [Test]
    public void Consent_BothConsented_FromDoesNotTrustTo_Violation()
    {
        var graph = MakeGraph(
            consented: new[] { A, B },
            trustLookup: new() { { A, new HashSet<int>() } }); // A trusts nobody

        var edges = new List<(int From, int To, int Token, long Flow)> { (A, B, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.True);
    }

    [Test]
    public void Consent_GroupEdge_Skipped()
    {
        var graph = MakeGraph(
            groups: new[] { Group1 },
            consented: new[] { A },
            trustLookup: new() { { A, new HashSet<int>() } }); // A trusts nobody

        // A(consented) → Group: should be skipped (group becomes router edge later)
        var edges = new List<(int From, int To, int Token, long Flow)> { (A, Group1, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.False,
            "Group edges skipped — they become router edges");
    }

    [Test]
    public void Consent_GroupFromEdge_Skipped()
    {
        var graph = MakeGraph(
            groups: new[] { Group1 },
            consented: new[] { A },
            trustLookup: new());

        // Group → A: should be skipped
        var edges = new List<(int From, int To, int Token, long Flow)> { (Group1, A, TokenGroup, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.False);
    }

    [Test]
    public void Consent_MultiEdgePath_FirstViolation_ReturnsTrue()
    {
        var graph = MakeGraph(
            consented: new[] { A, B },
            trustLookup: new() { { A, new HashSet<int> { B } } }); // A→B valid

        // A→B is valid (both consented, A trusts B), but B→C violates (C not consented)
        var edges = new List<(int From, int To, int Token, long Flow)>
        {
            (A, B, TokenA, 100),
            (B, C, TokenA, 100),
        };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.True,
            "B→C: B consented, C not → violation");
    }

    [Test]
    public void Consent_PoolNodeEdge_Skipped()
    {
        var pool = AddressIdPool.TokenPoolIdOf(TokenA);
        var graph = MakeGraph(
            consented: new[] { A },
            trustLookup: new());

        var edges = new List<(int From, int To, int Token, long Flow)> { (A, pool, TokenA, 100) };

        Assert.That(_pathfinder.PathHasConsentViolation(edges, graph), Is.False,
            "Pool node edges skipped");
    }

    #endregion

    #region InsertRouterInTransfers

    [Test]
    public void Router_AvatarToGroup_SplitsIntoTwo()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        graph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new(A, Group1, TokenA, 1000) { Flow = 500 },
        };

        var result = _pathfinder.InsertRouterInTransfers(edges, graph);

        Assert.That(result, Has.Count.EqualTo(2));
        // Avatar → Router
        Assert.That(result[0].From, Is.EqualTo(A));
        Assert.That(result[0].To, Is.EqualTo(Router));
        Assert.That(result[0].Flow, Is.EqualTo(500));
        // Router → Group
        Assert.That(result[1].From, Is.EqualTo(Router));
        Assert.That(result[1].To, Is.EqualTo(Group1));
        Assert.That(result[1].Flow, Is.EqualTo(500));
    }

    [Test]
    public void Router_GroupToAvatar_KeptAsIs()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        graph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new(Group1, B, TokenGroup, 1000) { Flow = 200 },
        };

        var result = _pathfinder.InsertRouterInTransfers(edges, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Group1));
        Assert.That(result[0].To, Is.EqualTo(B));
    }

    [Test]
    public void Router_AvatarToAvatar_KeptAsIs()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        graph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new(A, B, TokenA, 1000) { Flow = 300 },
        };

        var result = _pathfinder.InsertRouterInTransfers(edges, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(A));
        Assert.That(result[0].To, Is.EqualTo(B));
    }

    [Test]
    public void Router_NoRouterConfigured_NoChange()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        // No router set

        var edges = new List<FlowEdge>
        {
            new(A, Group1, TokenA, 1000) { Flow = 500 },
        };

        var result = _pathfinder.InsertRouterInTransfers(edges, graph);

        Assert.That(result, Has.Count.EqualTo(1), "No router → no splitting");
    }

    [Test]
    public void Router_RouterToGroup_KeptAsIs()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        graph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new(Router, Group1, TokenA, 1000) { Flow = 100 },
        };

        var result = _pathfinder.InsertRouterInTransfers(edges, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].From, Is.EqualTo(Router));
    }

    [Test]
    public void Router_MultipleEdges_OnlyAvatarToGroupSplit()
    {
        var graph = MakeGraph(groups: new[] { Group1 });
        graph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new(A, B, TokenA, 1000) { Flow = 100 },       // A→B: keep
            new(B, Group1, TokenA, 1000) { Flow = 100 },   // B→Group: split
            new(Group1, C, TokenGroup, 1000) { Flow = 100 },// Group→C: keep
        };

        var result = _pathfinder.InsertRouterInTransfers(edges, graph);

        Assert.That(result, Has.Count.EqualTo(4)); // 1 + 2 + 1
        Assert.That(result[0].From, Is.EqualTo(A));   // A→B
        Assert.That(result[1].From, Is.EqualTo(B));    // B→Router
        Assert.That(result[1].To, Is.EqualTo(Router));
        Assert.That(result[2].From, Is.EqualTo(Router)); // Router→Group
        Assert.That(result[2].To, Is.EqualTo(Group1));
        Assert.That(result[3].From, Is.EqualTo(Group1)); // Group→C
    }

    #endregion

    #region Helpers

    private static CapacityGraph MakeGraph(
        int[]? groups = null,
        int[]? consented = null,
        Dictionary<int, HashSet<int>>? trustLookup = null)
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(A);
        graph.AddAvatar(B);
        graph.AddAvatar(C);
        graph.AddAvatar(D);

        if (groups != null)
        {
            foreach (var g in groups)
                graph.AddGroup(g);
        }

        if (consented != null)
            graph.ConsentedAvatars = new HashSet<int>(consented);

        if (trustLookup != null)
            graph.TrustLookup = trustLookup;

        return graph;
    }

    #endregion

    #region PropagateQuantizationBackwards

    [Test]
    public void PropagateBackwards_GroupMinting_FixesConservation()
    {
        // Scenario: Avatar→Router→Group→Sink
        // Group→Sink quantized from 300 to 288, upstream edges unchanged
        var router = AddressIdPool.IdOf("0xdd10quant_router");
        var group = AddressIdPool.IdOf("0xdd11quant_group");
        var sink = AddressIdPool.IdOf("0xdd12quant_sink");
        var source = AddressIdPool.IdOf("0xdd13quant_source");
        int tokenA = AddressIdPool.IdOf("0xdd14quant_tokenA");
        int groupToken = AddressIdPool.IdOf("0xdd15quant_grouptoken");

        var edges = new List<FlowEdge>
        {
            new(source, router, tokenA, 300) { Flow = 300 },    // Source→Router: original flow
            new(router, group, tokenA, 300) { Flow = 300 },      // Router→Group: original flow
            new(group, sink, groupToken, 288) { Flow = 288 },    // Group→Sink: QUANTIZED
        };

        V2Pathfinder.PropagateQuantizationBackwards(edges, sink, source);

        // All edges should now have flow=288 (propagated backwards)
        Assert.That(edges[0].Flow, Is.EqualTo(288), "Source→Router should be reduced");
        Assert.That(edges[1].Flow, Is.EqualTo(288), "Router→Group should be reduced");
        Assert.That(edges[2].Flow, Is.EqualTo(288), "Group→Sink should stay at 288");
    }

    [Test]
    public void PropagateBackwards_MultipleCollateral_ProportionalReduction()
    {
        // Two collateral edges feeding one group, quantized output
        var router = AddressIdPool.IdOf("0xdd20quant2_router");
        var group = AddressIdPool.IdOf("0xdd21quant2_group");
        var sink = AddressIdPool.IdOf("0xdd22quant2_sink");
        var src1 = AddressIdPool.IdOf("0xdd23quant2_src1");
        var src2 = AddressIdPool.IdOf("0xdd24quant2_src2");
        int tokenA = AddressIdPool.IdOf("0xdd25quant2_tokenA");
        int tokenB = AddressIdPool.IdOf("0xdd26quant2_tokenB");
        int groupToken = AddressIdPool.IdOf("0xdd27quant2_grouptoken");

        var edges = new List<FlowEdge>
        {
            new(src1, router, tokenA, 150) { Flow = 150 },
            new(src2, router, tokenB, 150) { Flow = 150 },
            new(router, group, tokenA, 150) { Flow = 150 },
            new(router, group, tokenB, 150) { Flow = 150 },
            new(group, sink, groupToken, 288) { Flow = 288 },  // Quantized from 300
        };

        V2Pathfinder.PropagateQuantizationBackwards(edges, sink, src1);

        // Group: inflow should match outflow (288)
        long groupIn = edges[2].Flow + edges[3].Flow;
        Assert.That(groupIn, Is.EqualTo(288), "Group inflow must equal outflow");

        // Router: inflow should match outflow
        long routerIn = edges[0].Flow + edges[1].Flow;
        long routerOut = edges[2].Flow + edges[3].Flow;
        Assert.That(routerIn, Is.EqualTo(routerOut), "Router inflow must equal outflow");
    }

    [Test]
    public void PropagateBackwards_AlreadyBalanced_NoChange()
    {
        // Direct transfer, no quantization mismatch
        var source = AddressIdPool.IdOf("0xdd30quant3_src");
        var sink = AddressIdPool.IdOf("0xdd31quant3_sink");
        int token = AddressIdPool.IdOf("0xdd32quant3_token");

        var edges = new List<FlowEdge>
        {
            new(source, sink, token, 96) { Flow = 96 },
        };

        V2Pathfinder.PropagateQuantizationBackwards(edges, sink, source);

        Assert.That(edges[0].Flow, Is.EqualTo(96), "Already balanced — no change");
    }

    [Test]
    public void PropagateBackwards_DeepChain_PropagatesAllTheWay()
    {
        // A→B→C→D→Sink, Sink edge quantized
        var a = AddressIdPool.IdOf("0xdd40quant4_a");
        var b = AddressIdPool.IdOf("0xdd41quant4_b");
        var c = AddressIdPool.IdOf("0xdd42quant4_c");
        var d = AddressIdPool.IdOf("0xdd43quant4_d");
        var sink = AddressIdPool.IdOf("0xdd44quant4_sink");
        int t = AddressIdPool.IdOf("0xdd45quant4_tok");

        var edges = new List<FlowEdge>
        {
            new(a, b, t, 200) { Flow = 200 },
            new(b, c, t, 200) { Flow = 200 },
            new(c, d, t, 200) { Flow = 200 },
            new(d, sink, t, 192) { Flow = 192 },  // Quantized from 200
        };

        V2Pathfinder.PropagateQuantizationBackwards(edges, sink, a);

        Assert.That(edges[0].Flow, Is.EqualTo(192), "A→B");
        Assert.That(edges[1].Flow, Is.EqualTo(192), "B→C");
        Assert.That(edges[2].Flow, Is.EqualTo(192), "C→D");
        Assert.That(edges[3].Flow, Is.EqualTo(192), "D→Sink unchanged");
    }

    #endregion
}
