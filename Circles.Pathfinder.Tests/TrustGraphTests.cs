using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class TrustGraphTests
{
    private PathfinderTestFixture _fixture;

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
    }

    [Test]
    public void TrustGraph_LoadsAllTrustRelations()
    {
        // Arrange & Act - done in fixture setup
        var trustGraph = _fixture.TrustGraph;
        
        // Assert
        Assert.That(trustGraph.Edges.Count, Is.EqualTo(_fixture.TrustRelations.Count));
    }

    [Test]
    public void TrustGraph_ValidateTrustEdges()
    {
        // Arrange & Act - done in fixture setup
        var trustGraph = _fixture.TrustGraph;
        
        // Assert
        foreach (var relation in _fixture.TrustRelations)
        {
            bool found = false;
            foreach (var edge in trustGraph.Edges)
            {
                if (edge.From.Equals(relation.Truster.ToLower()) && 
                    edge.To.Equals(relation.Trustee.ToLower()))
                {
                    found = true;
                    break;
                }
            }
            
            Assert.That(found, Is.True, $"Trust relation from {relation.Truster} to {relation.Trustee} not found in graph");
        }
    }

    [Test]
    public void TrustGraph_FiltersByToTokensForSink()
    {
        // Arrange
        var sinkAddress = "0x1000000000000000000000000000000000000001";
        var toTokens = new List<string> { "0x3000000000000000000000000000000000000003" };
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest("0x2000000000000000000000000000000000000002", sinkAddress, "1000", toTokens: toTokens));
        
        // Assert - check if edges were properly filtered
        // In a capacity graph, we should see edges going to the sink only for tokens in toTokens list
        bool foundEdgesToSinkWithAllowedToken = false;
        bool foundEdgesToSinkWithDisallowedToken = false;
        
        foreach (var edge in capacityGraph.Edges)
        {
            if (edge.To.Equals(sinkAddress.ToLower()))
            {
                if (toTokens.Contains(edge.Token.ToUpper()))
                {
                    foundEdgesToSinkWithAllowedToken = true;
                }
                else
                {
                    foundEdgesToSinkWithDisallowedToken = true;
                }
            }
        }
        
        Assert.That(foundEdgesToSinkWithAllowedToken, Is.True, "Should find edges to sink with allowed token");
        Assert.That(foundEdgesToSinkWithDisallowedToken, Is.False, "Should not find edges to sink with disallowed token");
    }

    [Test]
    public void TrustGraph_VirtualSinkCreatedWhenSourceEqualsSink()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var toTokens = new List<string> { "0x3000000000000000000000000000000000000003" };
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sourceAddress, "1000", toTokens: toTokens));
        
        // Assert
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.EqualTo($"{sourceAddress.ToLower()}_virtual_sink"));
        
        // Verify that virtual sink node exists
        Assert.That(capacityGraph.AvatarNodes.ContainsKey(capacityGraph.VirtualSinkAddress), Is.True);
        
        // Verify edges to virtual sink exist
        bool foundEdgesToVirtualSink = false;
        foreach (var edge in capacityGraph.Edges)
        {
            if (edge.To.Equals(capacityGraph.VirtualSinkAddress))
            {
                foundEdgesToVirtualSink = true;
                break;
            }
        }
        
        Assert.That(foundEdgesToVirtualSink, Is.True);
    }

    [Test]
    public void TrustGraph_NoVirtualSinkCreatedForDifferentSourceAndSink()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000"));
        
        // Assert
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Null);
    }
}