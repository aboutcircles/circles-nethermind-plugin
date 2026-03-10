using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Comprehensive regression tests for PathUtils.ExtractFlowPaths.
/// This is mission-critical infrastructure — if path extraction is wrong,
/// the entire Circles transfer system halts.
///
/// Test categories:
/// 1. Invariant checks (flow conservation, path validity)
/// 2. Topology stress tests (deep chains, wide fan-out, complex DAGs)
/// 3. Adversarial inputs (cycles, self-loops, disconnected components)
/// 4. Reference implementation comparison (flow-conserving inputs)
/// 5. Determinism and mutation safety
/// 6. Scale / performance
/// 7. QuantizeSinkBoundFlows
/// </summary>
[TestFixture, Parallelizable]
public class PathExtractionRegressionTests
{
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // ═══════════════════════════════════════════════════════════════════
    // INVARIANT HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that every extracted path:
    /// 1. Starts at source, ends at sink
    /// 2. Is contiguous
    /// 3. Has positive, uniform flow across all edges
    /// 4. Contains no cycles
    /// And that total flow == expectedTotalFlow.
    /// </summary>
    private static void AssertPathInvariants(
        List<List<SimpleEdge>> paths,
        int source,
        int sink,
        long expectedTotalFlow,
        string context = "")
    {
        long totalFlow = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            Assert.That(path, Is.Not.Empty, $"{context} Path {i} is empty");
            Assert.That(path[0].From, Is.EqualTo(source), $"{context} Path {i} doesn't start at source");
            Assert.That(path[^1].To, Is.EqualTo(sink), $"{context} Path {i} doesn't end at sink");

            for (int j = 0; j < path.Count - 1; j++)
            {
                Assert.That(path[j].To, Is.EqualTo(path[j + 1].From),
                    $"{context} Path {i} not contiguous at edge {j}");
            }

            var pathFlow = path[0].Flow;
            Assert.That(pathFlow, Is.GreaterThan(0), $"{context} Path {i} has non-positive flow");

            foreach (var edge in path)
                Assert.That(edge.Flow, Is.EqualTo(pathFlow), $"{context} Path {i} inconsistent flow");

            var visited = new HashSet<int> { path[0].From };
            foreach (var edge in path)
                Assert.That(visited.Add(edge.To), $"{context} Path {i} has cycle (revisits node {edge.To})");

            totalFlow += pathFlow;
        }

        Assert.That(totalFlow, Is.EqualTo(expectedTotalFlow),
            $"{context} Total flow {totalFlow} != expected {expectedTotalFlow}");
    }

    /// <summary>
    /// Builds a flow-conserving network on a DAG (forward edges only, flow in == flow out at each node).
    /// Uses a push-forward approach: assign random capacity to each edge, then compute actual flow
    /// by pushing from source and capping at each node's available outgoing capacity.
    /// </summary>
    private static List<SimpleEdge> BuildFlowConservingDAG(int[] nodes, int token, Random rng)
    {
        var source = nodes[0];
        var sink = nodes[^1];
        int n = nodes.Length;

        // Generate random forward edges
        var edgeDefs = new List<(int from, int to, long capacity)>();
        for (int i = 0; i < n - 1; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (rng.NextDouble() < 0.5)
                    edgeDefs.Add((nodes[i], nodes[j], rng.Next(1, 100)));
            }
        }

        // Ensure at least source→sink
        if (!edgeDefs.Any(e => e.from == source))
            edgeDefs.Add((source, sink, rng.Next(10, 50)));

        // Build adjacency for topological flow assignment
        var outEdges = new Dictionary<int, List<int>>(); // node → edge indices
        for (int i = 0; i < edgeDefs.Count; i++)
        {
            if (!outEdges.ContainsKey(edgeDefs[i].from))
                outEdges[edgeDefs[i].from] = new List<int>();
            outEdges[edgeDefs[i].from].Add(i);
        }

        // Push flow forward: assign flow to each edge such that flow in == flow out at each node
        var flows = new long[edgeDefs.Count];
        var nodeInflow = new Dictionary<int, long>();
        nodeInflow[source] = long.MaxValue; // source has unlimited supply

        // Process nodes in topological order (forward indices = topological in a DAG)
        for (int i = 0; i < n; i++)
        {
            var node = nodes[i];
            if (!outEdges.ContainsKey(node)) continue;
            if (!nodeInflow.ContainsKey(node) || nodeInflow[node] <= 0) continue;

            long available = nodeInflow[node];
            var outs = outEdges[node];

            // Distribute available flow proportionally to edge capacities
            long totalCap = outs.Sum(idx => edgeDefs[idx].capacity);
            long assigned = 0;

            for (int k = 0; k < outs.Count; k++)
            {
                int idx = outs[k];
                long cap = edgeDefs[idx].capacity;
                long flow;

                if (k == outs.Count - 1)
                    flow = Math.Min(cap, available - assigned); // Last edge gets remainder
                else
                    flow = Math.Min(cap, (long)(available * ((double)cap / totalCap)));

                if (flow <= 0) continue;

                flows[idx] = flow;
                assigned += flow;

                int to = edgeDefs[idx].to;
                if (!nodeInflow.ContainsKey(to)) nodeInflow[to] = 0;
                nodeInflow[to] += flow;
            }
        }

        // Build edges with computed flows (only include edges with positive flow)
        var result = new List<SimpleEdge>();
        for (int i = 0; i < edgeDefs.Count; i++)
        {
            if (flows[i] > 0)
            {
                result.Add(new SimpleEdge(edgeDefs[i].from, edgeDefs[i].to, token, edgeDefs[i].capacity)
                { Flow = flows[i] });
            }
        }

        return result;
    }

    /// <summary>
    /// Calculate total source outflow (expected total for flow-conserving network).
    /// </summary>
    private static long SourceOutflow(IEnumerable<SimpleEdge> edges, int source)
        => edges.Where(e => e.From == source && e.Flow > 0).Sum(e => e.Flow);

    // ═══════════════════════════════════════════════════════════════════
    // 1. EDGE CASES & BOUNDARY CONDITIONS
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void SourceEqualsSink_ReturnsEmpty_NoOOM()
    {
        var node = Node(1);
        var other = Node(2);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(node, other, token, 1000) { Flow = 100 },
            new(other, node, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, node, node);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AllEdgesZeroFlow_ReturnsEmpty()
    {
        var s = Node(1);
        var t = Node(2);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(s, t, token, 1000) { Flow = 0 },
            new(s, Node(3), token, 1000) { Flow = 0 }
        };

        Assert.That(PathUtils.ExtractFlowPaths(edges, s, t), Is.Empty);
    }

    [Test]
    public void NegativeFlow_Ignored()
    {
        var s = Node(1);
        var t = Node(2);
        var edges = new List<SimpleEdge> { new(s, t, Node(100), 1000) { Flow = -50 } };
        Assert.That(PathUtils.ExtractFlowPaths(edges, s, t), Is.Empty);
    }

    [Test]
    public void SingleUnitFlow_ExtractsCorrectly()
    {
        var s = Node(1);
        var t = Node(2);
        var edges = new List<SimpleEdge> { new(s, t, Node(100), 1) { Flow = 1 } };
        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        AssertPathInvariants(result, s, t, 1);
    }

    [Test]
    public void VeryLargeFlow_NoOverflow()
    {
        var s = Node(1);
        var t = Node(2);
        long bigFlow = long.MaxValue / 2;
        var edges = new List<SimpleEdge> { new(s, t, Node(100), bigFlow) { Flow = bigFlow } };
        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        AssertPathInvariants(result, s, t, bigFlow);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. TOPOLOGY STRESS TESTS
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void DeepChain_50Nodes()
    {
        var token = Node(100);
        var nodes = Enumerable.Range(1, 50).Select(Node).ToArray();

        var edges = new List<SimpleEdge>();
        for (int i = 0; i < 49; i++)
            edges.Add(new SimpleEdge(nodes[i], nodes[i + 1], token, 1000) { Flow = 42 });

        var result = PathUtils.ExtractFlowPaths(edges, nodes[0], nodes[^1]);
        AssertPathInvariants(result, nodes[0], nodes[^1], 42);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Has.Count.EqualTo(49));
    }

    [Test]
    public void WideFanOut_20ParallelPaths()
    {
        var source = Node(1);
        var sink = Node(2);
        var token = Node(100);

        var edges = new List<SimpleEdge>();
        for (int i = 0; i < 20; i++)
        {
            var mid = Node(10 + i);
            long flow = (i + 1) * 10;
            edges.Add(new SimpleEdge(source, mid, token, 1000) { Flow = flow });
            edges.Add(new SimpleEdge(mid, sink, token, 1000) { Flow = flow });
        }

        long expected = Enumerable.Range(1, 20).Sum() * 10L; // 2100
        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, expected);
        Assert.That(result, Has.Count.EqualTo(20));
    }

    [Test]
    public void ComplexDAG_SharedMiddle()
    {
        // Source → A → M → Sink (100)
        // Source → B → M → Sink (150)
        // M→Sink carries 250
        var source = Node(1);
        var a = Node(2);
        var b = Node(3);
        var m = Node(4);
        var sink = Node(5);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 1000) { Flow = 100 },
            new(source, b, token, 1000) { Flow = 150 },
            new(a, m, token, 1000) { Flow = 100 },
            new(b, m, token, 1000) { Flow = 150 },
            new(m, sink, token, 1000) { Flow = 250 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, 250);
    }

    [Test]
    public void DiamondWithAsymmetricFlows()
    {
        var source = Node(1);
        var a = Node(2);
        var b = Node(3);
        var sink = Node(4);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 1000) { Flow = 30 },
            new(source, b, token, 1000) { Flow = 70 },
            new(a, sink, token, 1000) { Flow = 30 },
            new(b, sink, token, 1000) { Flow = 70 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, 100);
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void CascadingBottlenecks()
    {
        // S→A(300)→B(200)→C(100)→T(100) — only 100 can flow
        var s = Node(1); var a = Node(2); var b = Node(3); var c = Node(4); var t = Node(5);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(s, a, token, 1000) { Flow = 300 },
            new(a, b, token, 1000) { Flow = 200 },
            new(b, c, token, 1000) { Flow = 100 },
            new(c, t, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        AssertPathInvariants(result, s, t, 100);
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void ComplexFlowConservingNetwork()
    {
        // Verified flow-conserving at every intermediate node
        var source = Node(1);
        var a = Node(2); var b = Node(3); var c = Node(4); var d = Node(5);
        var sink = Node(6);
        var token = Node(100);

        // S→A:100, S→B:80
        // A→C:60, A→D:40
        // B→C:30, B→D:50
        // C→T:90, D→T:90
        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 200) { Flow = 100 },
            new(source, b, token, 200) { Flow = 80 },
            new(a, c, token, 200) { Flow = 60 },
            new(a, d, token, 200) { Flow = 40 },
            new(b, c, token, 200) { Flow = 30 },
            new(b, d, token, 200) { Flow = 50 },
            new(c, sink, token, 200) { Flow = 90 },
            new(d, sink, token, 200) { Flow = 90 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, 180);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. CYCLE & ADVERSARIAL TOPOLOGY TESTS
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void SelfLoop_Ignored()
    {
        var s = Node(1); var t = Node(2); var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(s, s, token, 1000) { Flow = 100 },  // Self-loop
            new(s, t, token, 1000) { Flow = 50 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(50));
    }

    [Test]
    public void MutualCycle_FindsValidPath()
    {
        var a = Node(1); var b = Node(2); var sink = Node(3); var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = 100 },
            new(b, a, token, 1000) { Flow = 50 },
            new(a, sink, token, 1000) { Flow = 75 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, a, sink);
        Assert.That(result, Has.Count.GreaterThan(0));
        foreach (var path in result)
        {
            Assert.That(path[0].From, Is.EqualTo(a));
            Assert.That(path[^1].To, Is.EqualTo(sink));
        }
    }

    [Test]
    public void TriangleCycle_WithExitToSink()
    {
        var a = Node(1); var b = Node(2); var c = Node(3); var sink = Node(4); var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = 100 },
            new(b, c, token, 1000) { Flow = 100 },
            new(c, a, token, 1000) { Flow = 50 },
            new(c, sink, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, a, sink);
        Assert.That(result, Has.Count.GreaterThan(0));
        Assert.That(result.Any(p => p.Count == 3), "Should find A→B→C→Sink path");
    }

    [Test]
    public void DisconnectedComponents_OnlyReachable()
    {
        var s = Node(1); var t = Node(2); var x = Node(3); var y = Node(4); var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(s, t, token, 1000) { Flow = 100 },
            new(x, y, token, 1000) { Flow = 200 }  // Disconnected
        };

        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        AssertPathInvariants(result, s, t, 100);
    }

    [Test]
    public void ReverseEdge_NotUsedForForwardPath()
    {
        var s = Node(1); var t = Node(2); var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(t, s, token, 1000) { Flow = 100 },  // Wrong direction
            new(s, t, token, 1000) { Flow = 50 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        AssertPathInvariants(result, s, t, 50);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3b. DEAD-END BRANCH RECOVERY (reviewer-flagged, verified correct)
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void DeadEndBranch_ThenValidPath_FlowNotLost()
    {
        var source = Node(1); var a = Node(2); var b = Node(3); var sink = Node(4);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 1000) { Flow = 100 },
            new(a, b, token, 1000) { Flow = 50 },   // dead end
            new(a, sink, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, 100);
    }

    [Test]
    public void MultipleDeadEnds_ThenValidPath()
    {
        var source = Node(1); var a = Node(2); var b = Node(3); var c = Node(4); var sink = Node(5);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 1000) { Flow = 100 },
            new(a, b, token, 1000) { Flow = 50 },
            new(a, c, token, 1000) { Flow = 30 },
            new(a, sink, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, 100);
    }

    [Test]
    public void NestedDeadEnd_ThenValidPath()
    {
        var source = Node(1); var a = Node(2); var b = Node(3); var c = Node(4); var sink = Node(5);
        var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(source, a, token, 1000) { Flow = 100 },
            new(a, b, token, 1000) { Flow = 50 },
            new(b, c, token, 1000) { Flow = 30 },
            new(a, sink, token, 1000) { Flow = 100 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        AssertPathInvariants(result, source, sink, 100);
    }

    [Test]
    public void ParallelEdges_SameStructure_DifferentFlow()
    {
        // Two edges S→M with same Token/Capacity, different Flow.
        // Verifies record equality correctly distinguishes them during removal.
        var s = Node(1); var mid = Node(2); var t = Node(3); var token = Node(100);

        var edges = new List<SimpleEdge>
        {
            new(s, mid, token, 1000) { Flow = 50 },
            new(s, mid, token, 1000) { Flow = 30 },
            new(mid, t, token, 1000) { Flow = 80 }
        };

        var result = PathUtils.ExtractFlowPaths(edges, s, t);
        Assert.That(result.Sum(p => p[0].Flow), Is.EqualTo(80));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. REFERENCE IMPLEMENTATION COMPARISON (flow-conserving only)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Naive BFS-based path extraction for comparison.
    /// Both DFS and BFS must produce the same total flow on flow-conserving inputs.
    /// </summary>
    private static List<List<SimpleEdge>> ReferenceExtractFlowPaths(
        IReadOnlyList<SimpleEdge> edges, int source, int sink)
    {
        if (source == sink) return new List<List<SimpleEdge>>();

        var edgeCopies = edges.Select(e =>
            new SimpleEdge(e.From, e.To, e.Token, e.Capacity) { Flow = e.Flow }).ToList();
        var result = new List<List<SimpleEdge>>();

        while (true)
        {
            var adj = new Dictionary<int, List<SimpleEdge>>();
            foreach (var e in edgeCopies.Where(e => e.Flow > 0))
            {
                if (!adj.ContainsKey(e.From)) adj[e.From] = new List<SimpleEdge>();
                adj[e.From].Add(e);
            }

            var parent = new Dictionary<int, SimpleEdge>();
            var visited = new HashSet<int> { source };
            var queue = new Queue<int>();
            queue.Enqueue(source);
            bool found = false;

            while (queue.Count > 0 && !found)
            {
                var node = queue.Dequeue();
                if (!adj.ContainsKey(node)) continue;
                foreach (var e in adj[node])
                {
                    if (e.Flow > 0 && visited.Add(e.To))
                    {
                        parent[e.To] = e;
                        if (e.To == sink) { found = true; break; }
                        queue.Enqueue(e.To);
                    }
                }
            }

            if (!found) break;

            var path = new List<SimpleEdge>();
            var cur = sink;
            while (cur != source)
            {
                path.Insert(0, parent[cur]);
                cur = parent[cur].From;
            }

            long minFlow = path.Min(e => e.Flow);
            result.Add(path.Select(e =>
                new SimpleEdge(e.From, e.To, e.Token, e.Capacity) { Flow = minFlow }).ToList());

            foreach (var e in path) e.Flow -= minFlow;
        }

        return result;
    }

    [Test]
    public void ReferenceComparison_Diamond_FlowConserving()
    {
        var source = Node(1);
        var left = Node(2); var right = Node(3); var sink = Node(4);
        var token = Node(100);

        var edges1 = new List<SimpleEdge>
        {
            new(source, left, token, 1000) { Flow = 50 },
            new(source, right, token, 1000) { Flow = 75 },
            new(left, sink, token, 1000) { Flow = 50 },
            new(right, sink, token, 1000) { Flow = 75 }
        };

        var edges2 = edges1.Select(e =>
            new SimpleEdge(e.From, e.To, e.Token, e.Capacity) { Flow = e.Flow }).ToList();

        var totalDFS = PathUtils.ExtractFlowPaths(edges1, source, sink).Sum(p => p[0].Flow);
        var totalRef = ReferenceExtractFlowPaths(edges2, source, sink).Sum(p => p[0].Flow);

        Assert.That(totalDFS, Is.EqualTo(totalRef));
    }

    [Test, Repeat(50)]
    public void ReferenceComparison_RandomFlowConservingDAG()
    {
        var rng = new Random(42 + TestContext.CurrentContext.CurrentRepeatCount);
        var token = Node(100);

        int n = rng.Next(4, 12);
        var nodes = Enumerable.Range(1, n).Select(Node).ToArray();
        var source = nodes[0];
        var sink = nodes[^1];

        var edges = BuildFlowConservingDAG(nodes, token, rng);

        if (edges.Count == 0) return; // Degenerate case

        long expected = SourceOutflow(edges, source);
        if (expected <= 0) return;

        // Clone for reference
        var edges2 = edges.Select(e =>
            new SimpleEdge(e.From, e.To, e.Token, e.Capacity) { Flow = e.Flow }).ToList();

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        var reference = ReferenceExtractFlowPaths(edges2, source, sink);

        var totalDFS = result.Sum(p => p[0].Flow);
        var totalRef = reference.Sum(p => p[0].Flow);

        Assert.That(totalDFS, Is.EqualTo(totalRef),
            $"Repeat {TestContext.CurrentContext.CurrentRepeatCount} (n={n}): DFS={totalDFS} != Ref={totalRef}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. INPUT MUTATION SAFETY
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void OriginalEdges_FlowMutatedToZero()
    {
        var s = Node(1); var t = Node(2); var token = Node(100);
        var edge = new SimpleEdge(s, t, token, 1000) { Flow = 100 };
        PathUtils.ExtractFlowPaths(new List<SimpleEdge> { edge }, s, t);
        Assert.That(edge.Flow, Is.EqualTo(0));
    }

    [Test]
    public void OriginalEdges_PartialConsumption()
    {
        var s = Node(1); var m = Node(2); var t = Node(3); var token = Node(100);
        var e1 = new SimpleEdge(s, m, token, 1000) { Flow = 100 };
        var e2 = new SimpleEdge(m, t, token, 1000) { Flow = 50 };

        PathUtils.ExtractFlowPaths(new List<SimpleEdge> { e1, e2 }, s, t);
        Assert.That(e2.Flow, Is.EqualTo(0));
        Assert.That(e1.Flow, Is.EqualTo(50));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. DETERMINISM
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void SameInput_SameOutput_Deterministic()
    {
        var source = Node(1); var a = Node(2); var b = Node(3); var sink = Node(4);
        var token = Node(100);

        List<SimpleEdge> MakeEdges() => new()
        {
            new(source, a, token, 1000) { Flow = 100 },
            new(source, b, token, 1000) { Flow = 200 },
            new(a, sink, token, 1000) { Flow = 100 },
            new(b, sink, token, 1000) { Flow = 200 }
        };

        var r1 = PathUtils.ExtractFlowPaths(MakeEdges(), source, sink);
        var r2 = PathUtils.ExtractFlowPaths(MakeEdges(), source, sink);

        Assert.That(r1.Count, Is.EqualTo(r2.Count));
        for (int i = 0; i < r1.Count; i++)
        {
            Assert.That(r1[i].Count, Is.EqualTo(r2[i].Count));
            for (int j = 0; j < r1[i].Count; j++)
            {
                Assert.That(r1[i][j].From, Is.EqualTo(r2[i][j].From));
                Assert.That(r1[i][j].To, Is.EqualTo(r2[i][j].To));
                Assert.That(r1[i][j].Flow, Is.EqualTo(r2[i][j].Flow));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. SCALE / PERFORMANCE
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Scale_100Nodes_50Paths()
    {
        var token = Node(500);
        var source = Node(1); var sink = Node(200);

        var edges = new List<SimpleEdge>();
        for (int i = 0; i < 50; i++)
        {
            var mid = Node(300 + i);
            long flow = (i + 1) * 10;
            edges.Add(new SimpleEdge(source, mid, token, 10000) { Flow = flow });
            edges.Add(new SimpleEdge(mid, sink, token, 10000) { Flow = flow });
        }

        var result = PathUtils.ExtractFlowPaths(edges, source, sink);
        Assert.That(result, Has.Count.EqualTo(50));
        long total = result.Sum(p => p[0].Flow);
        Assert.That(total, Is.EqualTo(Enumerable.Range(1, 50).Sum() * 10L));
    }

    [Test]
    public void Scale_DeepChain200()
    {
        var token = Node(500);
        var nodes = Enumerable.Range(1, 200).Select(Node).ToArray();

        var edges = new List<SimpleEdge>();
        for (int i = 0; i < 199; i++)
            edges.Add(new SimpleEdge(nodes[i], nodes[i + 1], token, 1000) { Flow = 77 });

        var result = PathUtils.ExtractFlowPaths(edges, nodes[0], nodes[^1]);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Has.Count.EqualTo(199));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. QUANTIZE SINK BOUND FLOWS
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Quantize_EmptyPaths_ReturnsEmpty()
    {
        Assert.That(PathUtils.QuantizeSinkBoundFlows(new(), Node(1), 96, 960), Is.Empty);
    }

    [Test]
    public void Quantize_ExactMultiple()
    {
        var sink = Node(2); var token = Node(100);
        var path = new List<SimpleEdge> { new(Node(1), sink, token, 1000) { Flow = 288 } }; // 3*96

        var result = PathUtils.QuantizeSinkBoundFlows(new() { path }, sink, 96, 960);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(288));
    }

    [Test]
    public void Quantize_FlowLessThanQuanta_Excluded()
    {
        var sink = Node(2); var token = Node(100);
        var path = new List<SimpleEdge> { new(Node(1), sink, token, 1000) { Flow = 50 } };

        Assert.That(PathUtils.QuantizeSinkBoundFlows(new() { path }, sink, 96, 960), Is.Empty);
    }

    [Test]
    public void Quantize_RoundsDown()
    {
        var sink = Node(2); var token = Node(100);
        var path = new List<SimpleEdge> { new(Node(1), sink, token, 1000) { Flow = 250 } };

        var result = PathUtils.QuantizeSinkBoundFlows(new() { path }, sink, 96, 960);
        Assert.That(result[0][0].Flow, Is.EqualTo(192)); // 2*96
    }

    [Test]
    public void Quantize_CapsAtTargetFlow()
    {
        var sink = Node(2); var token = Node(100);
        var paths = new List<List<SimpleEdge>>
        {
            new() { new(Node(1), sink, token, 1000) { Flow = 960 } },
            new() { new(Node(3), sink, token, 1000) { Flow = 960 } }
        };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, 96, 960);
        Assert.That(result.Sum(p => p[0].Flow), Is.LessThanOrEqualTo(960));
    }

    [Test]
    public void Quantize_NonSinkPath_Skipped()
    {
        var sink = Node(2); var token = Node(100);
        var path = new List<SimpleEdge> { new(Node(1), Node(3), token, 1000) { Flow = 500 } };

        Assert.That(PathUtils.QuantizeSinkBoundFlows(new() { path }, sink, 96, 960), Is.Empty);
    }
}
