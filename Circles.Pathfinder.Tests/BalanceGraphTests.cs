using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class BalanceGraphTests
{
    private PathfinderTestFixture _fixture;

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
    }

    [Test]
    public void BalanceGraph_LoadsAllBalances()
    {
        // Arrange & Act - done in the fixture setup
        var balanceGraph = _fixture.BalanceGraph;
        
        // Assert
        Assert.That(balanceGraph.BalanceNodes.Count, Is.EqualTo(_fixture.Balances.Count));
    }

    [Test]
    public void BalanceGraph_HasCorrectCapacityEdges()
    {
        // Arrange & Act - done in the fixture setup
        var balanceGraph = _fixture.BalanceGraph;
        
        // Assert
        Assert.That(balanceGraph.Edges.Count, Is.EqualTo(_fixture.Balances.Count));
        
        // Check a few specific balances
        foreach (var balance in _fixture.Balances)
        {
            var balanceNodeKey = $"{balance.Account.ToLower()}-{balance.TokenAddress.ToLower()}";
            Assert.That(balanceGraph.BalanceNodes.ContainsKey(balanceNodeKey), Is.True);
            
            var balanceNode = balanceGraph.BalanceNodes[balanceNodeKey];
            Assert.That(balanceNode.Token, Is.EqualTo(balance.TokenAddress.ToLower()));
            Assert.That(balanceNode.HolderAddress, Is.EqualTo(balance.Account.ToLower()));
            Assert.That(balanceNode.IsWrapped, Is.EqualTo(balance.IsWrapped));
            Assert.That(balanceNode.IsStatic, Is.EqualTo(balance.IsStatic));
        }
    }

    [Test]
    public void BalanceGraph_FilterWrappedTokens_WhenWithWrapIsFalse()
    {
        // Arrange - create a network with some wrapped tokens
        var testBalances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>
        {
            ("1000", "0xabc", "0xabc", false, false),
            ("2000", "0xdef", "0xdef", false, false),
            ("3000", "0xabc", "0xwrapped", true, false)
        };
        
        var testTrust = new List<(string Truster, string Trustee, int Limit)>
        {
            ("0xabc", "0xdef", 100),
            ("0xdef", "0xabc", 100)
        };
        
        var fixture = new PathfinderTestFixture(testBalances, testTrust);
        
        // Create capacityGraph with withWrap=false
        var capacityGraph = fixture.GraphFactory.CreateCapacityGraph(
            fixture.BalanceGraph, 
            fixture.TrustGraph, 
            TestUtils.CreateFlowRequest("0xabc", "0xdef", "1000", withWrap: false));
        
        // Assert - wrapped token shouldn't be in the capacity graph
        var wrappedNodeKey = "0xabc-0xwrapped";
        Assert.That(capacityGraph.BalanceNodes.ContainsKey(wrappedNodeKey), Is.False);
    }

    [Test]
    public void BalanceGraph_IncludeWrappedTokens_WhenWithWrapIsTrue()
    {
        // Arrange - create a network with some wrapped tokens
        var testBalances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>
        {
            ("1000", "0xabc", "0xabc", false, false),
            ("2000", "0xdef", "0xdef", false, false),
            ("3000", "0xabc", "0xwrapped", true, false)  // Wrapped token for source
        };
        
        var testTrust = new List<(string Truster, string Trustee, int Limit)>
        {
            ("0xabc", "0xdef", 100),
            ("0xdef", "0xabc", 100)
        };
        
        var fixture = new PathfinderTestFixture(testBalances, testTrust);
        
        // Create capacityGraph with withWrap=true
        var capacityGraph = fixture.GraphFactory.CreateCapacityGraph(
            fixture.BalanceGraph, 
            fixture.TrustGraph, 
            TestUtils.CreateFlowRequest("0xabc", "0xdef", "1000", withWrap: true));
        
        // Assert - wrapped token should be in the capacity graph (only for the source)
        var wrappedNodeKey = "0xabc-0xwrapped";
        Assert.That(fixture.BalanceGraph.BalanceNodes.ContainsKey(wrappedNodeKey), Is.True);
        
        // Check if it made it to the capacity graph
        Assert.That(capacityGraph.BalanceNodes.ContainsKey(wrappedNodeKey), Is.True);
    }

    [Test]
    public void BalanceGraph_FiltersByFromTokens()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var fromTokens = new List<string> { "0x2000000000000000000000000000000000000002" };
        
        // Act
        var capacityGraph = _fixture.GraphFactory.CreateCapacityGraph(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, "0x2000000000000000000000000000000000000002", "1000", fromTokens: fromTokens));
        
        // Assert
        // Only the specified token from the source should appear
        foreach (var balance in _fixture.Balances)
        {
            if (balance.Account == sourceAddress)
            {
                var balanceNodeKey = $"{balance.Account.ToLower()}-{balance.TokenAddress.ToLower()}";
                
                if (fromTokens.Contains(balance.TokenAddress))
                {
                    // Should be included
                    Assert.That(capacityGraph.BalanceNodes.ContainsKey(balanceNodeKey), Is.True);
                }
                else
                {
                    // Should be filtered out
                    Assert.That(capacityGraph.BalanceNodes.ContainsKey(balanceNodeKey), Is.False);
                }
            }
        }
    }
}