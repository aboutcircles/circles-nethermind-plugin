using Circles.Pathfinder;
using Circles.Pathfinder.Edges;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for the internal MaxFlowSolver that wraps Google OR-Tools max-flow.
/// Tests cover: empty graph, single edge, diamond topology, chain bottleneck,
/// parallel edges, disconnected components, and large capacities.
/// </summary>
[TestFixture, Parallelizable]
public class MaxFlowSolverTests
{
    private const int TokenId = 999; // arbitrary — Token is metadata, doesn't affect solver

    #region Empty / degenerate graphs

    [Test]
    public void Solve_EmptyEdges_ThrowsBadInput()
    {
        // No edges at all. The only arc is super-source -> source.
        // Sink node has no arcs, so OR-Tools returns BAD_INPUT.
        var edges = new List<CapacityEdge>();
        int source = 0;
        int sink = 1;
        long target = 100;

        var ex = Assert.Throws<InvalidOperationException>(
            () => MaxFlowSolver.Solve(edges, source, sink, target));

        Assert.That(ex!.Message, Does.Contain("BAD_INPUT"));
    }

    #endregion

    #region Single edge

    [Test]
    public void Solve_SingleDirectEdge_FlowMatchesTarget()
    {
        // S --100--> T, target=50 => flow=50
        int source = 0;
        int sink = 1;
        var edges = new List<CapacityEdge>
        {
            new(source, sink, TokenId, 100)
        };

        var result = MaxFlowSolver.Solve(edges, source, sink, targetFlow: 50);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].From, Is.EqualTo(source));
            Assert.That(result[0].To, Is.EqualTo(sink));
            Assert.That(result[0].Flow, Is.EqualTo(50));
            // Capacity = InitialCapacity - Flow = 100 - 50 = 50
            Assert.That(result[0].Capacity, Is.EqualTo(50));
        });
    }

    [Test]
    public void Solve_SingleDirectEdge_FlowCappedByCapacity()
    {
        // S --30--> T, target=100 => flow=30 (capacity is the bottleneck)
        int source = 0;
        int sink = 1;
        var edges = new List<CapacityEdge>
        {
            new(source, sink, TokenId, 30)
        };

        var result = MaxFlowSolver.Solve(edges, source, sink, targetFlow: 100);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].Flow, Is.EqualTo(30));
            Assert.That(result[0].Capacity, Is.EqualTo(0)); // 30 - 30 = 0
        });
    }

    [Test]
    public void Solve_SingleDirectEdge_ZeroTarget_ReturnsEmpty()
    {
        // S --100--> T, target=0 => no flow needed, result empty
        int source = 0;
        int sink = 1;
        var edges = new List<CapacityEdge>
        {
            new(source, sink, TokenId, 100)
        };

        var result = MaxFlowSolver.Solve(edges, source, sink, targetFlow: 0);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Diamond graph

    [Test]
    public void Solve_DiamondGraph_FindsMaxFlow()
    {
        // Classic diamond:
        //   S -10-> A -15-> T
        //   S -20-> B -25-> T
        // Max flow = min(10+20, 15+25) = min(30, 40) = 30
        int s = 0, a = 1, b = 2, t = 3;
        var edges = new List<CapacityEdge>
        {
            new(s, a, TokenId, 10),
            new(s, b, TokenId, 20),
            new(a, t, TokenId, 15),
            new(b, t, TokenId, 25)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 30);

        long totalFlow = result.Sum(e => e.From == s ? e.Flow : 0);
        Assert.That(totalFlow, Is.EqualTo(30));

        // Verify flow conservation at intermediate nodes A and B
        AssertFlowConservation(result, new[] { a, b });
    }

    [Test]
    public void Solve_DiamondGraph_TargetExceedsMaxFlow_FlowCapped()
    {
        // Same diamond, but target = 100 (exceeds max possible flow of 30)
        int s = 0, a = 1, b = 2, t = 3;
        var edges = new List<CapacityEdge>
        {
            new(s, a, TokenId, 10),
            new(s, b, TokenId, 20),
            new(a, t, TokenId, 15),
            new(b, t, TokenId, 25)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 100);

        long totalFlow = result.Sum(e => e.From == s ? e.Flow : 0);
        Assert.That(totalFlow, Is.EqualTo(30));
    }

    #endregion

    #region Chain graph

    [Test]
    public void Solve_ChainGraph_FindsBottleneck()
    {
        // A --100--> B --5--> C --100--> D
        // Bottleneck = 5
        int a = 0, b = 1, c = 2, d = 3;
        var edges = new List<CapacityEdge>
        {
            new(a, b, TokenId, 100),
            new(b, c, TokenId, 5),
            new(c, d, TokenId, 100)
        };

        var result = MaxFlowSolver.Solve(edges, a, d, targetFlow: 50);

        long totalFlow = result.Sum(e => e.From == a ? e.Flow : 0);
        Assert.That(totalFlow, Is.EqualTo(5));

        // Each edge in the chain should carry exactly 5
        foreach (var edge in result)
        {
            Assert.That(edge.Flow, Is.EqualTo(5));
        }
    }

    #endregion

    #region Parallel edges

    [Test]
    public void Solve_ParallelEdges_SumsCapacity()
    {
        // Two parallel edges S -> T with caps 10 and 20 => max flow = 30
        int s = 0, t = 1;
        int tokenA = 100;
        int tokenB = 200;
        var edges = new List<CapacityEdge>
        {
            new(s, t, tokenA, 10),
            new(s, t, tokenB, 20)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 30);

        long totalFlow = result.Sum(e => e.Flow);
        Assert.That(totalFlow, Is.EqualTo(30));
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void Solve_ParallelEdges_TargetLessThanTotal()
    {
        // Two parallel edges S -> T with caps 10 and 20, target=15
        // Solver should find exactly 15 (distributed across edges)
        int s = 0, t = 1;
        var edges = new List<CapacityEdge>
        {
            new(s, t, 100, 10),
            new(s, t, 200, 20)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 15);

        long totalFlow = result.Sum(e => e.Flow);
        Assert.That(totalFlow, Is.EqualTo(15));
    }

    #endregion

    #region Disconnected graph

    [Test]
    public void Solve_DisconnectedComponents_ReturnsEmptyFlows()
    {
        // Source in one component, sink in another. Both have edges but no path connects them.
        // Component 1: S(0) -> A(1)
        // Component 2: B(2) -> T(3)
        // No path from S to T => max flow = 0 => empty result
        int s = 0, a = 1, b = 2, t = 3;
        var edges = new List<CapacityEdge>
        {
            new(s, a, TokenId, 100),
            new(b, t, TokenId, 100)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 100);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Large capacity

    [Test]
    public void Solve_LargeCapacity_HandlesCorrectly()
    {
        // Edges with capacity near long.MaxValue / 2 to check for overflow safety
        int s = 0, t = 1;
        long largeCap = long.MaxValue / 2;
        var edges = new List<CapacityEdge>
        {
            new(s, t, TokenId, largeCap)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 1000);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].Flow, Is.EqualTo(1000));
            Assert.That(result[0].Capacity, Is.EqualTo(largeCap - 1000));
        });
    }

    #endregion

    #region Token metadata preservation

    [Test]
    public void Solve_PreservesTokenId()
    {
        // Verify that Token ids are carried through correctly in the output
        int s = 0, a = 1, t = 2;
        int token1 = 42;
        int token2 = 77;
        var edges = new List<CapacityEdge>
        {
            new(s, a, token1, 50),
            new(a, t, token2, 50)
        };

        var result = MaxFlowSolver.Solve(edges, s, t, targetFlow: 30);

        Assert.That(result, Has.Count.EqualTo(2));

        var edgeSA = result.First(e => e.From == s && e.To == a);
        var edgeAT = result.First(e => e.From == a && e.To == t);

        Assert.Multiple(() =>
        {
            Assert.That(edgeSA.Token, Is.EqualTo(token1));
            Assert.That(edgeAT.Token, Is.EqualTo(token2));
        });
    }

    #endregion

    #region Flow conservation helper

    /// <summary>
    /// Verifies flow conservation: for each intermediate node, total inflow == total outflow.
    /// </summary>
    private static void AssertFlowConservation(IReadOnlyList<SimpleEdge> result, int[] intermediateNodes)
    {
        foreach (int node in intermediateNodes)
        {
            long inflow = result.Where(e => e.To == node).Sum(e => e.Flow);
            long outflow = result.Where(e => e.From == node).Sum(e => e.Flow);
            Assert.That(outflow, Is.EqualTo(inflow),
                $"Flow conservation violated at node {node}: inflow={inflow}, outflow={outflow}");
        }
    }

    #endregion
}
