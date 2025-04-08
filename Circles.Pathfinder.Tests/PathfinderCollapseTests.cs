using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;
using Nethermind.Int256;
using System.Reflection;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class PathfinderCollapseTests
{
    private PathfinderTestFixture _fixture;

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
    }

    [Test]
    public void CollapseBalanceNodes_RemovesBalanceNodes()
    {
        // Arrange
        var sourceAddress = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var sinkAddress = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e";
        var graphFactory = _fixture.GraphFactory;
        
        var capacityGraph = graphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000000000000000000000"));
        
        var flowGraph = graphFactory.CreateFlowGraph(capacityGraph);
        
        // Run max flow
        flowGraph.ComputeMaxFlowWithPaths(sourceAddress, sinkAddress, 1000000000);
        
        // Get the paths
        var paths = flowGraph.ExtractPathsWithFlow(sourceAddress, sinkAddress, 0);
        
        // Create a pathfinder instance
        var pathfinder = new V2Pathfinder(graphFactory);
        
        // Get access to the private CollapseBalanceNodes method
        var methodInfo = typeof(V2Pathfinder).GetMethod("CollapseBalanceNodes", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        if (methodInfo == null)
        {
            Assert.Fail("CollapseBalanceNodes method not found");
            return;
        }
        
        // Act
        var collapsedGraph = (FlowGraph)methodInfo.Invoke(pathfinder, new object[] { paths })!;
        
        // Assert
        // Verify no balance nodes in the collapsed graph
        Assert.That(collapsedGraph.Nodes.Values.All(n => !n.Address.Contains("-")), Is.True);
        
        // Verify that all edges connect avatar nodes
        foreach (var edge in collapsedGraph.Edges)
        {
            Assert.That(edge.From.Contains("-"), Is.False);
            Assert.That(edge.To.Contains("-"), Is.False);
        }
    }

    [Test]
    public void CollapseBalanceNodes_MaintainsTotalFlow()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        var graphFactory = _fixture.GraphFactory;
        
        var capacityGraph = graphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000"));
        
        var flowGraph = graphFactory.CreateFlowGraph(capacityGraph);
        
        // Run max flow
        flowGraph.ComputeMaxFlowWithPaths(sourceAddress, sinkAddress, 1000);
        
        // Get the paths
        var paths = flowGraph.ExtractPathsWithFlow(sourceAddress, sinkAddress, 0);
        
        // Calculate total flow before collapse
        long totalFlowBefore = 0;
        foreach (var path in paths)
        {
            totalFlowBefore += path.Min(e => e.Flow);
        }
        
        // Create a pathfinder instance
        var pathfinder = new V2Pathfinder(graphFactory);
        
        // Get access to the private CollapseBalanceNodes method
        var methodInfo = typeof(V2Pathfinder).GetMethod("CollapseBalanceNodes", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        if (methodInfo == null)
        {
            Assert.Fail("CollapseBalanceNodes method not found");
            return;
        }
        
        // Act
        var collapsedGraph = (FlowGraph)methodInfo.Invoke(pathfinder, new object[] { paths })!;
        
        // Calculate total flow after collapse
        long totalFlowAfter = 0;
        foreach (var edge in collapsedGraph.Edges)
        {
            if (edge.From == sourceAddress)
            {
                totalFlowAfter += edge.Flow;
            }
        }
        
        // Assert
        Assert.That(totalFlowAfter, Is.EqualTo(totalFlowBefore));
    }

    [Test]
    public void CollapseBalanceNodes_ChainsOfBalancesCollapsed()
    {
        // Arrange - Create a test path with chains of balance nodes
        var path = new List<FlowEdge>();
        
        // Create a path: avatar1 -> balance1 -> balance2 -> avatar2
        var avatar1 = "0xavatar1";
        var balance1 = "0xavatar1-0xtoken1";
        var balance2 = "0xavatar2-0xtoken1";
        var avatar2 = "0xavatar2";
        var token = "0xtoken1";
        
        // Add edges
        path.Add(new FlowEdge(avatar1, balance1, token, 100) { Flow = 50 });
        path.Add(new FlowEdge(balance1, balance2, token, 100) { Flow = 40 });
        path.Add(new FlowEdge(balance2, avatar2, token, 100) { Flow = 30 });
        
        var paths = new List<List<FlowEdge>> { path };
        
        // Create a pathfinder instance
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Get access to the private CollapseBalanceNodes method
        var methodInfo = typeof(V2Pathfinder).GetMethod("CollapseBalanceNodes", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        if (methodInfo == null)
        {
            Assert.Fail("CollapseBalanceNodes method not found");
            return;
        }
        
        // Act
        var collapsedGraph = (FlowGraph)methodInfo.Invoke(pathfinder, new object[] { paths })!;
        
        // Assert
        // Should have a direct edge from avatar1 to avatar2
        var directEdge = collapsedGraph.Edges.FirstOrDefault(e => 
            e.From == avatar1 && e.To == avatar2 && e.Token == token);
        
        Assert.That(directEdge, Is.Not.Null);
        
        // The flow should be the minimum of the chain
        Assert.That(directEdge.Flow, Is.EqualTo(30));
    }
}