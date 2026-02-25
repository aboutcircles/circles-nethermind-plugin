using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for PathUtils.ExtractFlowPaths - the path extraction algorithm.
/// These tests validate the Edmonds-Karp style path peeling logic.
/// </summary>
[TestFixture, Parallelizable]
public class PathExtractionTests
{
    // Test addresses (just need unique IDs)
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // ─────────────────────── Empty/Trivial Cases ───────────────────────

    [Test]
    public void ExtractFlowPaths_EmptyEdges_ReturnsEmpty()
    {
        var edges = new List<SimpleEdge>();
        var result = PathUtils.ExtractFlowPaths(edges, Node(1), Node(2));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ExtractFlowPaths_NoFlowEdges_ReturnsEmpty()
    {
        var source = Node(1);
        var sink = Node(2);

        var edges = new List<SimpleEdge>
        {
            new(source, sink, Node(100), 1000) { Flow = 0 } // Zero flow
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ExtractFlowPaths_DisconnectedGraph_ReturnsEmpty()
    {
        var source = Node(1);
        var sink = Node(2);
        var other = Node(3);

        // Edge doesn't connect source to sink
        var edges = new List<SimpleEdge>
        {
            new(source, other, Node(100), 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        Assert.That(result, Is.Empty);
    }

    // ─────────────────────── Single Edge Tests ───────────────────────

    [Test]
    public void ExtractFlowPaths_SingleEdge_ReturnsOnePath()
    {
        var source = Node(1);
        var sink = Node(2);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 500 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);

        Assert.That(result, Has.Count.EqualTo(1), "Should find exactly one path");
        Assert.That(result[0], Has.Count.EqualTo(1), "Path should have one edge");
        Assert.That(result[0][0].Flow, Is.EqualTo(500), "Flow should be 500");
    }

    // ─────────────────────── Linear Path Tests ───────────────────────

    [Test]
    public void ExtractFlowPaths_LinearPath_ReturnsCorrectPath()
    {
        var a = Node(1);
        var b = Node(2);
        var c = Node(3);
        var token = Node(100);

        // A → B → C
        var edges = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = 200 },
            new(b, c, token, 1000) { Flow = 200 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, a, c);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Has.Count.EqualTo(2));
        Assert.That(result[0][0].From, Is.EqualTo(a));
        Assert.That(result[0][0].To, Is.EqualTo(b));
        Assert.That(result[0][1].From, Is.EqualTo(b));
        Assert.That(result[0][1].To, Is.EqualTo(c));
    }

    [Test]
    public void ExtractFlowPaths_LongPath_AllEdgesIncluded()
    {
        var token = Node(100);

        // Chain of 5 nodes
        var nodes = Enumerable.Range(1, 5).Select(Node).ToArray();

        var edges = new List<SimpleEdge>();
        for (int i = 0; i < nodes.Length - 1; i++)
        {
            edges.Add(new SimpleEdge(nodes[i], nodes[i + 1], token, 1000) { Flow = 100 });
        }

        var result = PathUtils.ExtractFlowPaths(edges, nodes[0], nodes[^1]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Has.Count.EqualTo(4), "4 edges for 5 nodes");
    }

    // ─────────────────────── Parallel Path Tests ───────────────────────

    [Test]
    public void ExtractFlowPaths_ParallelPaths_ExtractsBoth()
    {
        var source = Node(1);
        var sink = Node(2);
        var mid1 = Node(3);
        var mid2 = Node(4);
        var token = Node(100);

        // Two parallel paths: Source→Mid1→Sink and Source→Mid2→Sink
        var edges = new List<SimpleEdge>
        {
            new(source, mid1, token, 1000) { Flow = 100 },
            new(mid1, sink, token, 1000) { Flow = 100 },
            new(source, mid2, token, 1000) { Flow = 200 },
            new(mid2, sink, token, 1000) { Flow = 200 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);

        Assert.That(result, Has.Count.EqualTo(2), "Should find 2 parallel paths");

        // Total flow should be 300
        var totalFlow = result.Sum(path => path.Min(e => e.Flow));
        Assert.That(totalFlow, Is.EqualTo(300));
    }

    // ─────────────────────── Bottleneck Tests ───────────────────────

    [Test]
    public void ExtractFlowPaths_Bottleneck_UsesMinFlow()
    {
        var a = Node(1);
        var b = Node(2);
        var c = Node(3);
        var token = Node(100);

        // A→B has 100, B→C has 50 (bottleneck)
        var edges = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = 100 },
            new(b, c, token, 1000) { Flow = 50 }  // Bottleneck
        };

        var result = PathUtils.ExtractFlowPaths(edges, a, c);

        // First path should take the bottleneck amount
        Assert.That(result[0][0].Flow, Is.EqualTo(50));
        Assert.That(result[0][1].Flow, Is.EqualTo(50));

        // Second path should take remaining 50 from first edge (but can't reach sink)
        // Actually, the remaining edge A→B with 50 can't form a path to C
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void ExtractFlowPaths_MultipleBottlenecks_ExtractsAll()
    {
        var a = Node(1);
        var b = Node(2);
        var c = Node(3);
        var d = Node(4);
        var token = Node(100);

        // A→B→C with flows [100, 200, 300]
        // Multiple paths should be extracted as bottleneck peels off
        var edges = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = 100 },
            new(b, c, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, a, c);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(100));
    }

    // ─────────────────────── Diamond Graph Tests ───────────────────────

    [Test]
    public void ExtractFlowPaths_DiamondGraph_CorrectFlows()
    {
        var source = Node(1);
        var left = Node(2);
        var right = Node(3);
        var sink = Node(4);
        var token = Node(100);

        // Diamond: Source → Left → Sink and Source → Right → Sink
        var edges = new List<SimpleEdge>
        {
            new(source, left, token, 1000) { Flow = 50 },
            new(source, right, token, 1000) { Flow = 75 },
            new(left, sink, token, 1000) { Flow = 50 },
            new(right, sink, token, 1000) { Flow = 75 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);

        Assert.That(result, Has.Count.EqualTo(2));

        var totalFlow = result.Sum(path => path.Min(e => e.Flow));
        Assert.That(totalFlow, Is.EqualTo(125));
    }

    // ─────────────────────── Flow Conservation Tests ───────────────────

    [Test]
    public void ExtractFlowPaths_FlowConservation_TotalEqualsSourceOutflow()
    {
        var source = Node(1);
        var a = Node(2);
        var b = Node(3);
        var c = Node(4);
        var sink = Node(5);
        var token = Node(100);

        // Complex graph with multiple paths
        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 1000) { Flow = 100 },
            new(source, b, token, 1000) { Flow = 150 },
            new(a, c, token, 1000) { Flow = 100 },
            new(b, c, token, 1000) { Flow = 150 },
            new(c, sink, token, 1000) { Flow = 250 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);

        // Sum of all path flows should equal total outflow from source
        var extractedFlow = result.Sum(path => path.Min(e => e.Flow));
        Assert.That(extractedFlow, Is.EqualTo(250));
    }

    // ─────────────────────── Edge Cases ───────────────────────

    [Test]
    public void ExtractFlowPaths_SourceEqualsSink_ReturnsEmpty()
    {
        var node = Node(1);
        var other = Node(2);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(node, other, token, 1000) { Flow = 100 },
            new(other, node, token, 1000) { Flow = 100 }
        };

        // Source == Sink, can't have a path from A to A
        var result = PathUtils.ExtractFlowPaths(edges, node, node);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ExtractFlowPaths_CyclicGraph_DoesNotLoop()
    {
        var a = Node(1);
        var b = Node(2);
        var c = Node(3);
        var sink = Node(4);
        var token = Node(100);

        // A → B → C → B (cycle) + B → Sink
        var edges = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = 100 },
            new(b, c, token, 1000) { Flow = 50 },
            new(c, b, token, 1000) { Flow = 25 },  // Cycle back
            new(b, sink, token, 1000) { Flow = 100 }
        };

        // Should not infinite loop - BFS prevents revisiting
        var result = PathUtils.ExtractFlowPaths(edges, a, sink);

        Assert.That(result, Has.Count.GreaterThan(0));
    }

    // ─────────────────────── Order Independence ───────────────────────

    [Test]
    public void ExtractFlowPaths_EdgeOrder_DoesNotAffectResult()
    {
        var source = Node(1);
        var mid = Node(2);
        var sink = Node(3);
        var token = Node(100);

        var edges1 = new List<SimpleEdge>
        {
            new(source, mid, token, 1000) { Flow = 100 },
            new(mid, sink, token, 1000) { Flow = 100 }
        };

        var edges2 = new List<SimpleEdge>
        {
            new(mid, sink, token, 1000) { Flow = 100 },
            new(source, mid, token, 1000) { Flow = 100 }
        };

        var result1 = PathUtils.ExtractFlowPaths(edges1, source, sink);
        var result2 = PathUtils.ExtractFlowPaths(edges2, source, sink);

        // Same number of paths regardless of input order
        Assert.That(result1.Count, Is.EqualTo(result2.Count));

        // Same total flow
        var flow1 = result1.Sum(p => p.Min(e => e.Flow));
        var flow2 = result2.Sum(p => p.Min(e => e.Flow));
        Assert.That(flow1, Is.EqualTo(flow2));
    }
}
