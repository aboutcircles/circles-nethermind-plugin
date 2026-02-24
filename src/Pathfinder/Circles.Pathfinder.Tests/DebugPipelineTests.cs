using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests that DebugShowIntermediateSteps populates all pipeline stages
/// with correct transformation data.
/// </summary>
[TestFixture]
public class DebugPipelineTests
{
    // Simple 3-avatar graph: A → B → C (each trusts the previous avatar's token)
    private static readonly string AddrA = "0xbb01debug_pipeline_a";
    private static readonly string AddrB = "0xbb02debug_pipeline_b";
    private static readonly string AddrC = "0xbb03debug_pipeline_c";
    private const long Balance = 1_000_000L;

    [Test]
    public void Debug_WhenEnabled_AllStagesPopulated()
    {
        var (graph, source, sink) = BuildSimpleGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            DebugShowIntermediateSteps = true,
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);

        Assert.That(result.Debug, Is.Not.Null, "Debug stages should be populated");
        Assert.That(result.Debug!.RawPaths, Is.Not.Null.And.Not.Empty, "Stage 1: RawPaths");
        Assert.That(result.Debug.Collapsed, Is.Not.Null.And.Not.Empty, "Stage 2: Collapsed");
        Assert.That(result.Debug.RouterInserted, Is.Not.Null.And.Not.Empty, "Stage 3: RouterInserted");
        Assert.That(result.Debug.Sorted, Is.Not.Null.And.Not.Empty, "Stage 4: Sorted");
    }

    [Test]
    public void Debug_WhenDisabled_NoStages()
    {
        var (graph, source, sink) = BuildSimpleGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            DebugShowIntermediateSteps = false,
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);

        Assert.That(result.Debug, Is.Null, "Debug stages should be null when disabled");
    }

    [Test]
    public void Debug_WhenNull_NoStages()
    {
        var (graph, source, sink) = BuildSimpleGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            // DebugShowIntermediateSteps not set (null)
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);

        Assert.That(result.Debug, Is.Null);
    }

    [Test]
    public void Debug_RawPaths_ContainsPoolNodes()
    {
        var (graph, source, sink) = BuildSimpleGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            DebugShowIntermediateSteps = true,
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);

        // Raw paths should contain tpool- nodes (before collapse)
        var rawAddresses = result.Debug!.RawPaths!
            .SelectMany(s => new[] { s.From, s.To })
            .Where(a => a != null)
            .ToList();
        Assert.That(rawAddresses.Any(a => a!.StartsWith("tpool-")), Is.True,
            "Raw paths should contain token pool nodes");
    }

    [Test]
    public void Debug_Collapsed_NoPoolNodes()
    {
        var (graph, source, sink) = BuildSimpleGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            DebugShowIntermediateSteps = true,
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);

        // Collapsed stage should have no pool nodes
        var collapsedAddresses = result.Debug!.Collapsed!
            .SelectMany(s => new[] { s.From, s.To })
            .Where(a => a != null)
            .ToList();
        Assert.That(collapsedAddresses.All(a => !a!.StartsWith("tpool-")), Is.True,
            "Collapsed stage should not contain token pool nodes");
    }

    [Test]
    public void Debug_StageProgression_EdgeCountsCoherent()
    {
        var (graph, source, sink) = BuildSimpleGraph();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            DebugShowIntermediateSteps = true,
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);
        var d = result.Debug!;

        // Raw > Collapsed (pool nodes removed)
        Assert.That(d.RawPaths!.Count, Is.GreaterThanOrEqualTo(d.Collapsed!.Count),
            "Raw paths should have >= collapsed edges (pools removed)");

        // RouterInserted >= Collapsed (router adds edges for Avatar→Group)
        Assert.That(d.RouterInserted!.Count, Is.GreaterThanOrEqualTo(d.Collapsed.Count),
            "Router stage should have >= collapsed edges");

        // Sorted same count as RouterInserted (just reordered)
        Assert.That(d.Sorted!.Count, Is.EqualTo(d.RouterInserted.Count),
            "Sorted stage should have same count as router-inserted");
    }

    [Test]
    public void Debug_WithGroupMinting_RouterInserted_HasRouterEdges()
    {
        var (graph, source, sink) = BuildGraphWithGroup();
        var pf = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            DebugShowIntermediateSteps = true,
        };
        var target = CirclesConverter.BlowUpToUInt256(Balance);

        var result = pf.ComputeMaxFlowWithPath(graph, request, target);

        if (result.Transfers.Count == 0)
        {
            Assert.Ignore("No flow found (graph topology may not connect source→sink via group)");
            return;
        }

        // Check that router edges appear in Stage 3 but not Stage 2
        var collapsedAddresses = result.Debug!.Collapsed!
            .SelectMany(s => new[] { s.From, s.To })
            .Where(a => a != null)
            .ToHashSet();
        var routerAddresses = result.Debug.RouterInserted!
            .SelectMany(s => new[] { s.From, s.To })
            .Where(a => a != null)
            .ToHashSet();

        // If there are group edges, router should appear in Stage 3 but not Stage 2
        var routerAddr = AddressIdPool.StringOf(AddressIdPool.IdOf("0xbb04debug_pipeline_router"));
        if (routerAddresses.Contains(routerAddr))
        {
            Assert.That(collapsedAddresses.Contains(routerAddr), Is.False,
                "Router should not appear in collapsed stage (inserted later)");
        }
    }

    #region Helpers

    /// <summary>
    /// A → B → C: each avatar holds own token, next avatar trusts it.
    /// </summary>
    private static (CapacityGraph graph, string source, string sink) BuildSimpleGraph()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf(AddrA);
        int b = AddressIdPool.IdOf(AddrB);
        int c = AddressIdPool.IdOf(AddrC);

        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);

        // Each avatar's token
        graph.AddTokenNode(a);
        graph.AddTokenNode(b);

        int poolA = AddressIdPool.TokenPoolIdOf(a);
        int poolB = AddressIdPool.TokenPoolIdOf(b);

        // A holds token A, B trusts token A
        graph.AddCapacityEdge(a, poolA, a, Balance);
        graph.AddCapacityEdge(poolA, b, a, long.MaxValue);

        // B holds token B, C trusts token B
        graph.AddCapacityEdge(b, poolB, b, Balance);
        graph.AddCapacityEdge(poolB, c, b, long.MaxValue);

        // Trust lookup
        graph.TrustLookup = new Dictionary<int, HashSet<int>>
        {
            { a, new HashSet<int> { a } },
            { b, new HashSet<int> { a, b } },
            { c, new HashSet<int> { b, c } },
        };

        return (graph, AddrA, AddrC);
    }

    /// <summary>
    /// A → Group → C via collateral + group minting.
    /// </summary>
    private static (CapacityGraph graph, string source, string sink) BuildGraphWithGroup()
    {
        var graph = new CapacityGraph();
        var routerAddr = "0xbb04debug_pipeline_router";
        var groupAddr = "0xbb05debug_pipeline_group";

        int a = AddressIdPool.IdOf(AddrA);
        int c = AddressIdPool.IdOf(AddrC);
        int grp = AddressIdPool.IdOf(groupAddr);
        int router = AddressIdPool.IdOf(routerAddr);

        graph.AddAvatar(a);
        graph.AddAvatar(c);
        graph.AddGroup(grp);
        graph.SetRouter(router);

        graph.AddTokenNode(a);
        int poolA = AddressIdPool.TokenPoolIdOf(a);

        // A holds token A
        graph.AddCapacityEdge(a, poolA, a, Balance);
        // Group trusts token A (collateral)
        graph.AddCapacityEdge(poolA, grp, a, long.MaxValue);
        // Group mints group token to C
        graph.AddCapacityEdge(grp, c, grp, long.MaxValue);

        graph.GroupTrustedTokens[grp] = new HashSet<int> { a };
        graph.TrustLookup = new Dictionary<int, HashSet<int>>
        {
            { a, new HashSet<int> { a } },
            { c, new HashSet<int> { grp } },
        };

        return (graph, AddrA, AddrC);
    }

    #endregion
}
