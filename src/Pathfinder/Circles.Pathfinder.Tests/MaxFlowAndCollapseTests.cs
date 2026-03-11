using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for ComputeMaxFlow (flow-only, no path extraction) and
/// CountCollapsedTransferSteps / CollapsePathToTransfers (step counting).
/// </summary>
[TestFixture, Parallelizable]
public class MaxFlowAndCollapseTests
{
    // Unique prefixed addresses to avoid AddressIdPool collisions
    private static readonly string AddrA = "0xee01maxflow_a";
    private static readonly string AddrB = "0xee02maxflow_b";
    private static readonly string AddrC = "0xee03maxflow_c";
    private static readonly string AddrD = "0xee04maxflow_d";

    #region ComputeMaxFlow (flow-only)

    [Test]
    public void ComputeMaxFlow_SimpleLinear_ReturnsFlow()
    {
        // A → pool(A) → B, balance 1000
        var (graph, source, sink) = BuildLinearGraph(1_000_000L);
        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = source, Sink = sink };
        var target = CirclesConverter.BlowUpToUInt256(1_000_000L);

        long flow = pf.ComputeMaxFlow(graph, request, target);

        Assert.That(flow, Is.GreaterThan(0));
        Assert.That(flow, Is.LessThanOrEqualTo(1_000_000L));
    }

    [Test]
    public void ComputeMaxFlow_ExactTarget_CapsAtTarget()
    {
        var (graph, source, sink) = BuildLinearGraph(5_000_000L);
        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = source, Sink = sink };
        // Request less than available
        var target = CirclesConverter.BlowUpToUInt256(1_000_000L);

        long flow = pf.ComputeMaxFlow(graph, request, target);

        Assert.That(flow, Is.EqualTo(1_000_000L),
            "Should cap at target when capacity > target");
    }

    [Test]
    public void ComputeMaxFlow_MissingSource_Throws()
    {
        var graph = new CapacityGraph();
        int b = AddressIdPool.IdOf(AddrB);
        graph.AddAvatar(b);

        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = AddrA, Sink = AddrB };
        var target = CirclesConverter.BlowUpToUInt256(100L);

        Assert.Throws<ArgumentException>(() => pf.ComputeMaxFlow(graph, request, target));
    }

    [Test]
    public void ComputeMaxFlow_MissingSink_Throws()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        graph.AddAvatar(a);

        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = AddrA, Sink = AddrB };
        var target = CirclesConverter.BlowUpToUInt256(100L);

        Assert.Throws<ArgumentException>(() => pf.ComputeMaxFlow(graph, request, target));
    }

    [Test]
    public void ComputeMaxFlow_DisconnectedGraph_ThrowsNoOutgoingEdges()
    {
        // A and B exist but no edges from source → throws before OR-Tools
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddTokenNode(a);

        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = AddrA, Sink = AddrB };
        var target = CirclesConverter.BlowUpToUInt256(100L);

        var ex = Assert.Throws<InvalidOperationException>(
            () => pf.ComputeMaxFlow(graph, request, target));
        Assert.That(ex!.Message, Does.Contain("no outgoing edges"));
    }

    [Test]
    public void ComputeMaxFlow_MultiHop_FlowPropagates()
    {
        // A → B → C, each leg has 500k capacity
        var (graph, source, sink) = BuildChainGraph(500_000L);
        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = source, Sink = sink };
        var target = CirclesConverter.BlowUpToUInt256(500_000L);

        long flow = pf.ComputeMaxFlow(graph, request, target);

        Assert.That(flow, Is.GreaterThan(0), "Flow should propagate through B");
        Assert.That(flow, Is.LessThanOrEqualTo(500_000L));
    }

    [Test]
    public void ComputeMaxFlow_NullSource_Throws()
    {
        var graph = new CapacityGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = null, Sink = AddrB };
        var target = CirclesConverter.BlowUpToUInt256(100L);

        Assert.Throws<ArgumentNullException>(() => pf.ComputeMaxFlow(graph, request, target));
    }

    [Test]
    public void ComputeMaxFlow_NullSink_Throws()
    {
        var graph = new CapacityGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest { Source = AddrA, Sink = null };
        var target = CirclesConverter.BlowUpToUInt256(100L);

        Assert.Throws<ArgumentNullException>(() => pf.ComputeMaxFlow(graph, request, target));
    }

    #endregion

    #region CollapsePathToTransfers

    [Test]
    public void CollapsePathToTransfers_PoolCollapse_ProducesSingleTriple()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int tokenA = a; // token = avatar's own token
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddTokenNode(tokenA);
        int pool = AddressIdPool.TokenPoolIdOf(tokenA);

        var path = new List<SimpleEdge>
        {
            new(a, pool, tokenA, 1000) { Flow = 500 },
            new(pool, b, tokenA, 1000) { Flow = 500 },
        };

        var result = V2Pathfinder.CollapsePathToTransfers(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo((a, b, tokenA)));
    }

    [Test]
    public void CollapsePathToTransfers_DirectEdge_KeptAsIs()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int tokenA = a;
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        var path = new List<SimpleEdge>
        {
            new(a, b, tokenA, 1000) { Flow = 300 },
        };

        var result = V2Pathfinder.CollapsePathToTransfers(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo((a, b, tokenA)));
    }

    [Test]
    public void CollapsePathToTransfers_MultiHop_CollapsesEachPool()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int c = AddressIdPool.IdOf(AddrC);
        int tokenA = a;
        int tokenB = b;
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);
        graph.AddTokenNode(tokenA);
        graph.AddTokenNode(tokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenA);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenB);

        // A → Pool(A) → B → Pool(B) → C
        var path = new List<SimpleEdge>
        {
            new(a, poolA, tokenA, 1000) { Flow = 400 },
            new(poolA, b, tokenA, 1000) { Flow = 400 },
            new(b, poolB, tokenB, 1000) { Flow = 400 },
            new(poolB, c, tokenB, 1000) { Flow = 400 },
        };

        var result = V2Pathfinder.CollapsePathToTransfers(path, graph);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo((a, b, tokenA)));
        Assert.That(result[1], Is.EqualTo((b, c, tokenB)));
    }

    [Test]
    public void CollapsePathToTransfers_EmptyPath_ReturnsEmpty()
    {
        var graph = new CapacityGraph();
        var result = V2Pathfinder.CollapsePathToTransfers(new List<SimpleEdge>(), graph);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void CollapsePathToTransfers_GroupEdge_KeptAsIs()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int grp = AddressIdPool.IdOf("0xee05maxflow_group");
        graph.AddAvatar(a);
        graph.AddGroup(grp);

        var path = new List<SimpleEdge>
        {
            new(grp, a, grp, 1000) { Flow = 200 },
        };

        var result = V2Pathfinder.CollapsePathToTransfers(path, graph);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo((grp, a, grp)));
    }

    #endregion

    #region CountCollapsedTransferSteps

    [Test]
    public void CountSteps_SinglePath_CountsCollapsedEdges()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int tokenA = a;
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddTokenNode(tokenA);
        int pool = AddressIdPool.TokenPoolIdOf(tokenA);

        var paths = new List<List<SimpleEdge>>
        {
            new()
            {
                new(a, pool, tokenA, 1000) { Flow = 500 },
                new(pool, b, tokenA, 1000) { Flow = 500 },
            }
        };

        int steps = V2Pathfinder.CountCollapsedTransferSteps(paths, graph);
        Assert.That(steps, Is.EqualTo(1), "A→pool→B collapses to 1 step");
    }

    [Test]
    public void CountSteps_TwoPathsSameEdge_Deduplicated()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int tokenA = a;
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddTokenNode(tokenA);
        int pool = AddressIdPool.TokenPoolIdOf(tokenA);

        // Two paths with the same collapsed edge (A→B with tokenA)
        var paths = new List<List<SimpleEdge>>
        {
            new()
            {
                new(a, pool, tokenA, 1000) { Flow = 300 },
                new(pool, b, tokenA, 1000) { Flow = 300 },
            },
            new()
            {
                new(a, pool, tokenA, 1000) { Flow = 200 },
                new(pool, b, tokenA, 1000) { Flow = 200 },
            },
        };

        int steps = V2Pathfinder.CountCollapsedTransferSteps(paths, graph);
        Assert.That(steps, Is.EqualTo(1), "Same (From,To,Token) deduplicates to 1");
    }

    [Test]
    public void CountSteps_TwoPathsDifferentEdges_SumsUnique()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int c = AddressIdPool.IdOf(AddrC);
        int tokenA = a;
        int tokenB = b;
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);
        graph.AddTokenNode(tokenA);
        graph.AddTokenNode(tokenB);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenA);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenB);

        // Path 1: A→B via tokenA, Path 2: B→C via tokenB
        var paths = new List<List<SimpleEdge>>
        {
            new()
            {
                new(a, poolA, tokenA, 1000) { Flow = 300 },
                new(poolA, b, tokenA, 1000) { Flow = 300 },
            },
            new()
            {
                new(b, poolB, tokenB, 1000) { Flow = 200 },
                new(poolB, c, tokenB, 1000) { Flow = 200 },
            },
        };

        int steps = V2Pathfinder.CountCollapsedTransferSteps(paths, graph);
        Assert.That(steps, Is.EqualTo(2), "Two unique (From,To,Token) triples = 2 steps");
    }

    [Test]
    public void CountSteps_EmptyPaths_Zero()
    {
        var graph = new CapacityGraph();
        var paths = new List<List<SimpleEdge>>();
        int steps = V2Pathfinder.CountCollapsedTransferSteps(paths, graph);
        Assert.That(steps, Is.EqualTo(0));
    }

    [Test]
    public void CountSteps_MultiHopPath_CountsAllCollapsedEdges()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int c = AddressIdPool.IdOf(AddrC);
        int d = AddressIdPool.IdOf(AddrD);
        int tokenA = a;
        int tokenB = b;
        int tokenC = c;
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);
        graph.AddAvatar(d);
        graph.AddTokenNode(tokenA);
        graph.AddTokenNode(tokenB);
        graph.AddTokenNode(tokenC);
        int poolA = AddressIdPool.TokenPoolIdOf(tokenA);
        int poolB = AddressIdPool.TokenPoolIdOf(tokenB);
        int poolC = AddressIdPool.TokenPoolIdOf(tokenC);

        // A → B → C → D (3 hops via pools)
        var paths = new List<List<SimpleEdge>>
        {
            new()
            {
                new(a, poolA, tokenA, 1000) { Flow = 100 },
                new(poolA, b, tokenA, 1000) { Flow = 100 },
                new(b, poolB, tokenB, 1000) { Flow = 100 },
                new(poolB, c, tokenB, 1000) { Flow = 100 },
                new(c, poolC, tokenC, 1000) { Flow = 100 },
                new(poolC, d, tokenC, 1000) { Flow = 100 },
            }
        };

        int steps = V2Pathfinder.CountCollapsedTransferSteps(paths, graph);
        Assert.That(steps, Is.EqualTo(3), "3-hop path = 3 transfer steps");
    }

    #endregion

    #region Helpers

    private static (CapacityGraph graph, string source, string sink) BuildLinearGraph(long balance)
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);

        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddTokenNode(a);

        int pool = AddressIdPool.TokenPoolIdOf(a);
        graph.AddCapacityEdge(a, pool, a, balance);
        graph.AddCapacityEdge(pool, b, a, long.MaxValue);

        graph.TrustLookup = new Dictionary<int, HashSet<int>>
        {
            { a, new HashSet<int> { a } },
            { b, new HashSet<int> { a, b } },
        };

        return (graph, AddrA, AddrB);
    }

    private static (CapacityGraph graph, string source, string sink) BuildChainGraph(long balance)
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int c = AddressIdPool.IdOf(AddrC);

        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);
        graph.AddTokenNode(a);
        graph.AddTokenNode(b);

        int poolA = AddressIdPool.TokenPoolIdOf(a);
        int poolB = AddressIdPool.TokenPoolIdOf(b);

        // A holds token A, B trusts A's token
        graph.AddCapacityEdge(a, poolA, a, balance);
        graph.AddCapacityEdge(poolA, b, a, long.MaxValue);

        // B holds token B, C trusts B's token
        graph.AddCapacityEdge(b, poolB, b, balance);
        graph.AddCapacityEdge(poolB, c, b, long.MaxValue);

        graph.TrustLookup = new Dictionary<int, HashSet<int>>
        {
            { a, new HashSet<int> { a } },
            { b, new HashSet<int> { a, b } },
            { c, new HashSet<int> { b, c } },
        };

        return (graph, AddrA, AddrC);
    }

    #endregion
}
