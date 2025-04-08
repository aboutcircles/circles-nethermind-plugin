using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class FlowGraphTests
{
    private PathfinderTestFixture _fixture;

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
    }

    [Test]
    public void FlowGraph_ConvertsCapacityGraphCorrectly()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000"));
        
        // Act
        var flowGraph = _fixture.GraphFactory.CreateFlowGraph(capacityGraph);
        
        // Assert
        // For each capacity edge, there should be a forward and reverse flow edge
        Assert.That(flowGraph.Edges.Count, Is.EqualTo(capacityGraph.Edges.Count * 2));
        
        // Check that reverse edges exist with zero flow
        foreach (var capacityEdge in capacityGraph.Edges)
        {
            // Find the corresponding flow edge
            var forwardEdge = flowGraph.Edges.FirstOrDefault(e => 
                e.From == capacityEdge.From && 
                e.To == capacityEdge.To && 
                e.Token == capacityEdge.Token);
            
            Assert.That(forwardEdge, Is.Not.Null);
            Assert.That(forwardEdge.Flow, Is.EqualTo(0));
            Assert.That(forwardEdge.CurrentCapacity, Is.EqualTo(capacityEdge.InitialCapacity));
            
            // Check reverse edge
            Assert.That(forwardEdge.ReverseEdge, Is.Not.Null);
            Assert.That(forwardEdge.ReverseEdge.From, Is.EqualTo(capacityEdge.To));
            Assert.That(forwardEdge.ReverseEdge.To, Is.EqualTo(capacityEdge.From));
        }
    }

    [Test]
    public void FlowGraph_MaxFlowComputation_ReturnsValidFlow()
    {
        // Arrange - Create a simple linear network
        _fixture.ReconfigureWithTestNetwork(5, linear: true);
        
        var sourceAddress = "0x0000000000000000000000000000000000000000";
        var sinkAddress = "0x0000000000000000000000000000000000000004";
        var targetFlow = "100000000";
        
        var request = TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow);
        
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            request);
        
        var flowGraph = _fixture.GraphFactory.CreateFlowGraph(capacityGraph);
        
        // Act
        long flow = flowGraph.ComputeMaxFlowWithPaths(sourceAddress, sinkAddress, long.Parse(targetFlow));
        
        // Assert
        Assert.That(flow, Is.GreaterThan(0));
        Assert.That(flow, Is.LessThanOrEqualTo(long.Parse(targetFlow)));
    }

    [Test]
    public void FlowGraph_ExtractPathsWithFlow_ReturnsValidPaths()
    {
        // Arrange - Create a simple linear network
        _fixture.ReconfigureWithTestNetwork(5, linear: true);
        
        var sourceAddress = "0x0000000000000000000000000000000000000000";
        var sinkAddress = "0x0000000000000000000000000000000000000004";
        var targetFlow = "100000000";
        
        var request = TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow);
        
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            request);
        
        var flowGraph = _fixture.GraphFactory.CreateFlowGraph(capacityGraph);
        
        // Run max flow
        flowGraph.ComputeMaxFlowWithPaths(sourceAddress, sinkAddress, long.Parse(targetFlow));
        
        // Act
        var paths = flowGraph.ExtractPathsWithFlow(sourceAddress, sinkAddress, 0);
        
        // Assert
        Assert.That(paths, Is.Not.Empty);
        
        foreach (var path in paths)
        {
            // Each path should start from source
            Assert.That(path[0].From, Is.EqualTo(sourceAddress));
            
            // Each path should end at sink
            Assert.That(path[^1].To, Is.EqualTo(sinkAddress));
            
            // Each path should have positive flow
            long pathFlow = path.Min(e => e.Flow);
            Assert.That(pathFlow, Is.GreaterThan(0));
            
            // Check path connectivity
            for (int i = 0; i < path.Count - 1; i++)
            {
                Assert.That(path[i].To, Is.EqualTo(path[i + 1].From));
            }
        }
    }

    [Test]
    public void FlowGraph_AggregateIdenticalEdges_CombinesFlows()
    {
        // Arrange - Create a flow graph with duplicate edges
        _fixture.ReconfigureWithTestNetwork(5);
        
        var sourceAddress = "0x0000000000000000000000000000000000000000";
        var sinkAddress = "0x0000000000000000000000000000000000000001";
        var targetFlow = "100000000";
        
        var request = TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow);
        
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            request);
        
        var flowGraph = _fixture.GraphFactory.CreateFlowGraph(capacityGraph);
        
        // Run max flow
        flowGraph.ComputeMaxFlowWithPaths(sourceAddress, sinkAddress, long.Parse(targetFlow));
        
        // Create a duplicate edge situation by adding an identical edge
        var existingEdge = flowGraph.Edges.First(e => e.Flow > 0);
        var duplicateEdge = new Circles.Pathfinder.Edges.FlowEdge(
            existingEdge.From, 
            existingEdge.To, 
            existingEdge.Token, 
            existingEdge.InitialCapacity);
        duplicateEdge.Flow = existingEdge.Flow / 2;
        existingEdge.Flow = existingEdge.Flow / 2;
        flowGraph.Edges.Add(duplicateEdge);
        
        // Act
        var aggregatedGraph = flowGraph.AggregateIdenticalEdges();
        
        // Assert
        // The aggregated graph should have fewer edges than the original
        Assert.That(aggregatedGraph.Edges.Count, Is.LessThan(flowGraph.Edges.Count));
        
        // Find the aggregated edge
        var aggregatedEdge = aggregatedGraph.Edges.FirstOrDefault(e => 
            e.From == existingEdge.From && 
            e.To == existingEdge.To && 
            e.Token == existingEdge.Token);
        
        // It should have the combined flow of both original edges
        Assert.That(aggregatedEdge, Is.Not.Null);
        Assert.That(aggregatedEdge.Flow, Is.EqualTo(existingEdge.Flow + duplicateEdge.Flow));
    }
}