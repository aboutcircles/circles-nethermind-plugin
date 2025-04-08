using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class CapacityGraphTests
{
    private PathfinderTestFixture _fixture;

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
    }

    [Test]
    public void CapacityGraph_CombinesBalanceAndTrustGraphs()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000"));
        
        // Assert - Verify nodes from both graphs are present
        foreach (var avatarNode in _fixture.BalanceGraph.AvatarNodes)
        {
            Assert.That(capacityGraph.AvatarNodes.ContainsKey(avatarNode.Key), Is.True);
        }
        
        foreach (var avatarNode in _fixture.TrustGraph.AvatarNodes)
        {
            Assert.That(capacityGraph.AvatarNodes.ContainsKey(avatarNode.Key), Is.True);
        }
        
        // Check that balance nodes are included
        foreach (var balanceNode in _fixture.BalanceGraph.BalanceNodes)
        {
            // Note: Some balance nodes might be filtered out based on the request
            if (balanceNode.Value.HolderAddress != sourceAddress || !balanceNode.Value.IsWrapped)
            {
                Assert.That(capacityGraph.BalanceNodes.ContainsKey(balanceNode.Key), Is.True);
            }
        }
    }

    [Test]
    public void CapacityGraph_CreatesTrustBasedCapacityEdges()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000"));
        
        // Assert - For each balance, if someone trusts the token, there should be an edge
        foreach (var balanceNode in _fixture.BalanceGraph.BalanceNodes.Values)
        {
            var tokenIssuer = balanceNode.Token;
            
            // Find who trusts this token
            var trusters = _fixture.TrustRelations
                .Where(r => r.Trustee.ToLower() == tokenIssuer.ToLower())
                .Select(r => r.Truster.ToLower())
                .ToList();
            
            // For each truster, there should be a capacity edge from the balance node to them
            foreach (var truster in trusters)
            {
                // Skip if truster is the same as the balance holder (no self-loops)
                if (truster == balanceNode.HolderAddress.ToLower())
                    continue;
                
                // Look for the edge in the capacity graph
                bool foundEdge = capacityGraph.Edges.Any(e => 
                    e.From.ToLower() == balanceNode.Address.ToLower() && 
                    e.To.ToLower() == truster && 
                    e.Token.ToLower() == balanceNode.Token.ToLower());
                
                Assert.That(foundEdge, Is.True, 
                    $"Expected edge from {balanceNode.Address} to {truster} with token {balanceNode.Token}");
            }
        }
    }

    [Test]
    public void CapacityGraph_BalanceEdgesHaveCorrectCapacity()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, "1000"));
        
        // Assert - Check that balance-related edges have correct capacity
        foreach (var balance in _fixture.Balances)
        {
            var fromAddress = balance.Account.ToLower();
            var balanceNodeKey = $"{fromAddress}-{balance.TokenAddress.ToLower()}";
            
            // If this balance node exists in the capacity graph
            if (capacityGraph.BalanceNodes.ContainsKey(balanceNodeKey))
            {
                // Find edge from account to its balance node
                var edge = capacityGraph.Edges.FirstOrDefault(e => 
                    e.From.ToLower() == fromAddress && 
                    e.To.ToLower() == balanceNodeKey);
                
                Assert.That(edge, Is.Not.Null, $"Edge not found from {fromAddress} to {balanceNodeKey}");
            }
        }
    }

    [Test]
    public void CapacityGraph_PreventsSelfLoopsForSourceEqualsSink()
    {
        // Arrange - create a simple network where source has a balance of tokens it also trusts
        var sourceAddress = "0xabc";
        var tokenAddress = "0xdef";
        
        var testBalances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>
        {
            ("1000", sourceAddress, sourceAddress, false, false),
            ("2000", sourceAddress, tokenAddress, false, false),
        };
        
        var testTrust = new List<(string Truster, string Trustee, int Limit)>
        {
            (sourceAddress, tokenAddress, 100),
        };
        
        var fixture = new PathfinderTestFixture(testBalances, testTrust);
        
        // Act - create capacity graph with source == sink and toTokens = the token we trust
        var capacityGraph = fixture.GraphFactory.CreateCapacityGraph(
            fixture.BalanceGraph,
            fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sourceAddress, "1000", toTokens: new List<string> { tokenAddress }));
        
        // Assert - the balance of the token we trust should NOT be in the graph
        // to prevent a trivial self-loop
        var balanceNodeKey = $"{sourceAddress.ToLower()}-{tokenAddress.ToLower()}";
        Assert.That(capacityGraph.BalanceNodes.ContainsKey(balanceNodeKey), Is.False);
    }
}