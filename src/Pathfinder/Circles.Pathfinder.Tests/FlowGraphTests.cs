using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for FlowGraph: node creation, edge addition, and AggregateIdenticalEdges
/// (including the B1 saturating addition fix).
/// </summary>
[TestFixture, Parallelizable]
public class FlowGraphTests
{
    #region Node Creation

    [Test]
    public void AddAvatar_CreatesAvatarAndNodeEntries()
    {
        var graph = new FlowGraph();
        int id = AddressIdPool.IdOf("0xfg10000000000000000000000000000000000001");

        graph.AddAvatar(id);

        Assert.That(graph.AvatarNodes.ContainsKey(id), Is.True);
        Assert.That(graph.Nodes.ContainsKey(id), Is.True);
        Assert.That(graph.Nodes[id], Is.InstanceOf<AvatarNode>());
    }

    [Test]
    public void AddAvatar_Duplicate_DoesNotThrow()
    {
        var graph = new FlowGraph();
        int id = AddressIdPool.IdOf("0xfg20000000000000000000000000000000000002");

        graph.AddAvatar(id);
        graph.AddAvatar(id); // duplicate

        Assert.That(graph.AvatarNodes.Count, Is.EqualTo(1));
    }

    [Test]
    public void AddBalanceNode_CreatesBalanceAndNodeEntries()
    {
        var graph = new FlowGraph();
        int holder = AddressIdPool.IdOf("0xfg30000000000000000000000000000000000003");
        int token = AddressIdPool.IdOf("0xfg40000000000000000000000000000000000004");

        graph.AddBalanceNode(holder, token, 1000, isWrapped: false, isStatic: true);

        Assert.That(graph.BalanceNodes.Count, Is.EqualTo(1));
        var bn = graph.BalanceNodes.Values.First();
        Assert.That(bn.Holder, Is.EqualTo(holder));
        Assert.That(bn.Token, Is.EqualTo(token));
        Assert.That(bn.Amount, Is.EqualTo(1000));
        Assert.That(bn.IsStatic, Is.True);
    }

    [Test]
    public void Constructor_WithCapacityHints_PreallocatesCollections()
    {
        var graph = new FlowGraph(nodeCount: 100, edgeCount: 200);

        // Should not throw and should be usable
        Assert.That(graph.Nodes, Is.Empty);
        Assert.That(graph.Edges, Is.Empty);
    }

    #endregion

    #region AddCapacityEdge

    [Test]
    public void AddCapacityEdge_CreatesFlowEdgeAndCopiesNodes()
    {
        // Build a capacity graph with two avatars and one edge
        var cap = new CapacityGraph();
        int a = AddressIdPool.IdOf("0xfg50000000000000000000000000000000000005");
        int b = AddressIdPool.IdOf("0xfg60000000000000000000000000000000000006");
        int tok = AddressIdPool.IdOf("0xfg70000000000000000000000000000000000007");
        cap.AddAvatar(a);
        cap.AddAvatar(b);
        var capEdge = new CapacityEdge(a, b, tok, 500);

        var flow = new FlowGraph();
        flow.AddCapacityEdge(cap, capEdge);

        Assert.That(flow.Edges.Count, Is.EqualTo(1));
        Assert.That(flow.Edges[0].From, Is.EqualTo(a));
        Assert.That(flow.Edges[0].To, Is.EqualTo(b));
        Assert.That(flow.Edges[0].Token, Is.EqualTo(tok));
        Assert.That(flow.Edges[0].InitialCapacity, Is.EqualTo(500));
        // Nodes should be copied from CapacityGraph
        Assert.That(flow.AvatarNodes.ContainsKey(a), Is.True);
        Assert.That(flow.AvatarNodes.ContainsKey(b), Is.True);
    }

    [Test]
    public void AddCapacity_CopiesAllEdgesFromCapacityGraph()
    {
        var cap = new CapacityGraph();
        int a = AddressIdPool.IdOf("0xfg80000000000000000000000000000000000008");
        int b = AddressIdPool.IdOf("0xfg90000000000000000000000000000000000009");
        int c = AddressIdPool.IdOf("0xfga0000000000000000000000000000000000010");
        int tok = AddressIdPool.IdOf("0xfgb0000000000000000000000000000000000011");
        cap.AddAvatar(a);
        cap.AddAvatar(b);
        cap.AddAvatar(c);
        cap.AddCapacityEdge(a, b, tok, 100);
        cap.AddCapacityEdge(b, c, tok, 200);

        var flow = new FlowGraph();
        flow.AddCapacity(cap);

        Assert.That(flow.Edges.Count, Is.EqualTo(2));
        Assert.That(flow.AvatarNodes.Count, Is.EqualTo(3));
    }

    #endregion

    #region AggregateIdenticalEdges

    [Test]
    public void AggregateIdenticalEdges_MergesEdgesWithSameKey()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgc0000000000000000000000000000000000012");
        int b = AddressIdPool.IdOf("0xfgd0000000000000000000000000000000000013");
        int tok = AddressIdPool.IdOf("0xfge0000000000000000000000000000000000014");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        // Two edges with same (from, to, token) but different flows
        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = 60 });
        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = 40 });

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges.Count, Is.EqualTo(1));
        Assert.That(aggregated.Edges[0].Flow, Is.EqualTo(100)); // 60 + 40
    }

    [Test]
    public void AggregateIdenticalEdges_SkipsZeroFlowEdges()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgf0000000000000000000000000000000000015");
        int b = AddressIdPool.IdOf("0xfgg0000000000000000000000000000000000016");
        int tok = AddressIdPool.IdOf("0xfgh0000000000000000000000000000000000017");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = 50 });
        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = 0 }); // zero flow

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges.Count, Is.EqualTo(1));
        Assert.That(aggregated.Edges[0].Flow, Is.EqualTo(50));
    }

    [Test]
    public void AggregateIdenticalEdges_SkipsNegativeFlowEdges()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgi0000000000000000000000000000000000018");
        int b = AddressIdPool.IdOf("0xfgj0000000000000000000000000000000000019");
        int tok = AddressIdPool.IdOf("0xfgk0000000000000000000000000000000000020");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = 30 });
        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = -5 }); // negative

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges.Count, Is.EqualTo(1));
        Assert.That(aggregated.Edges[0].Flow, Is.EqualTo(30));
    }

    [Test]
    public void AggregateIdenticalEdges_DifferentTokens_NotMerged()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgl0000000000000000000000000000000000021");
        int b = AddressIdPool.IdOf("0xfgm0000000000000000000000000000000000022");
        int tok1 = AddressIdPool.IdOf("0xfgn0000000000000000000000000000000000023");
        int tok2 = AddressIdPool.IdOf("0xfgo0000000000000000000000000000000000024");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        graph.Edges.Add(new FlowEdge(a, b, tok1, 100) { Flow = 50 });
        graph.Edges.Add(new FlowEdge(a, b, tok2, 100) { Flow = 30 });

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges.Count, Is.EqualTo(2));
    }

    /// <summary>
    /// B1 fix: saturating addition prevents overflow when aggregating near-max flows.
    /// </summary>
    [Test]
    public void AggregateIdenticalEdges_SaturatingAddition_CapsAtLongMaxValue()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgp0000000000000000000000000000000000025");
        int b = AddressIdPool.IdOf("0xfgq0000000000000000000000000000000000026");
        int tok = AddressIdPool.IdOf("0xfgr0000000000000000000000000000000000027");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        // Two edges whose combined flow would overflow long.MaxValue
        graph.Edges.Add(new FlowEdge(a, b, tok, long.MaxValue) { Flow = long.MaxValue - 10 });
        graph.Edges.Add(new FlowEdge(a, b, tok, long.MaxValue) { Flow = 100 });

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges.Count, Is.EqualTo(1));
        Assert.That(aggregated.Edges[0].Flow, Is.EqualTo(long.MaxValue),
            "Saturating addition should cap at long.MaxValue, not overflow to negative");
    }

    [Test]
    public void AggregateIdenticalEdges_BothMaxValue_CapsWithoutOverflow()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgs0000000000000000000000000000000000028");
        int b = AddressIdPool.IdOf("0xfgt0000000000000000000000000000000000029");
        int tok = AddressIdPool.IdOf("0xfgu0000000000000000000000000000000000030");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        graph.Edges.Add(new FlowEdge(a, b, tok, long.MaxValue) { Flow = long.MaxValue });
        graph.Edges.Add(new FlowEdge(a, b, tok, long.MaxValue) { Flow = long.MaxValue });

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges[0].Flow, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void AggregateIdenticalEdges_TakesMaxCapacity()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgv0000000000000000000000000000000000031");
        int b = AddressIdPool.IdOf("0xfgw0000000000000000000000000000000000032");
        int tok = AddressIdPool.IdOf("0xfgx0000000000000000000000000000000000033");
        graph.AddAvatar(a);
        graph.AddAvatar(b);

        graph.Edges.Add(new FlowEdge(a, b, tok, 200) { Flow = 50, CurrentCapacity = 150 });
        graph.Edges.Add(new FlowEdge(a, b, tok, 300) { Flow = 30, CurrentCapacity = 270 });

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges[0].CurrentCapacity, Is.EqualTo(270),
            "Should take max capacity of merged edges");
    }

    [Test]
    public void AggregateIdenticalEdges_PreservesAvatarNodes()
    {
        var graph = new FlowGraph();
        int a = AddressIdPool.IdOf("0xfgy0000000000000000000000000000000000034");
        int b = AddressIdPool.IdOf("0xfgz0000000000000000000000000000000000035");
        int c = AddressIdPool.IdOf("0xfh10000000000000000000000000000000000036");
        int tok = AddressIdPool.IdOf("0xfh20000000000000000000000000000000000037");
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);
        graph.Edges.Add(new FlowEdge(a, b, tok, 100) { Flow = 50 });

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.AvatarNodes.Count, Is.EqualTo(3),
            "All avatar nodes should be preserved in aggregated graph");
    }

    [Test]
    public void AggregateIdenticalEdges_EmptyGraph_ReturnsEmpty()
    {
        var graph = new FlowGraph();

        var aggregated = graph.AggregateIdenticalEdges();

        Assert.That(aggregated.Edges, Is.Empty);
        Assert.That(aggregated.Nodes, Is.Empty);
    }

    #endregion
}
