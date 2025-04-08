using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;
using Nethermind.Int256;
using System.Globalization;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class PathfinderIntegrationTests
{
    private PathfinderTestFixture _fixture;

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
    }

    [Test]
    public void ComputeMaxFlow_ReturnsNonZeroFlow_ForConnectedNodes()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        var targetFlow = "1000000000000000000"; // 1 token
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow),
            UInt256.Parse(targetFlow));
        
        // Assert
        Assert.That(result.MaxFlow, Is.Not.EqualTo("0"));
        Assert.That(result.Transfers, Is.Not.Empty);
        
        // Validate the path
        Assert.That(TestUtils.ValidatePath(result.Transfers, sourceAddress, sinkAddress), Is.True);
        
        // Validate flow conservation
        Assert.That(TestUtils.ValidateFlowConservation(result.Transfers, sourceAddress, sinkAddress), Is.True);
    }

    [Test]
    public void ComputeMaxFlow_ReturnsZeroFlow_ForDisconnectedNodes()
    {
        // Arrange - Create a network where two nodes have no path between them
        var testBalances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>
        {
            ("1000000000000000000", "0xsource", "0xsource", false, false),
            ("1000000000000000000", "0xsink", "0xsink", false, false),
        };
        
        var testTrust = new List<(string Truster, string Trustee, int Limit)>();
        
        var fixture = new PathfinderTestFixture(testBalances, testTrust);
        var pathfinder = new V2Pathfinder(fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            fixture.BalanceGraph,
            fixture.TrustGraph,
            TestUtils.CreateFlowRequest("0xsource", "0xsink", "1000000000000000000"),
            UInt256.Parse("1000000000000000000"));
        
        // Assert
        Assert.That(result.MaxFlow, Is.EqualTo("0"));
        Assert.That(result.Transfers, Is.Empty);
    }

    [Test]
    public void ComputeMaxFlow_WithFromTokens_OnlyUsesSpecifiedTokens()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        var fromTokens = new List<string> { "0x2000000000000000000000000000000000000002" }; // Only use this token
        var targetFlow = "1000000000000000000"; // 1 token
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow, fromTokens: fromTokens),
            UInt256.Parse(targetFlow));
        
        // Assert
        Assert.That(result.Transfers, Is.Not.Empty);
        
        // Check that source only spends the specified token
        var sourceTransfers = result.Transfers.Where(t => t.From == sourceAddress).ToList();
        foreach (var transfer in sourceTransfers)
        {
            Assert.That(transfer.TokenOwner, Is.EqualTo(fromTokens[0]));
        }
    }

    [Test]
    public void ComputeMaxFlow_WithToTokens_OnlyAcceptsSpecifiedTokens()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        var toTokens = new List<string> { "0x3000000000000000000000000000000000000003" }; // Only accept this token
        var targetFlow = "1000000000000000000"; // 1 token
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow, toTokens: toTokens),
            UInt256.Parse(targetFlow));
        
        // Assert
        // If there are transfers to the sink, they should only be of the specified token
        var sinkTransfers = result.Transfers.Where(t => t.To == sinkAddress).ToList();
        foreach (var transfer in sinkTransfers)
        {
            Assert.That(transfer.TokenOwner, Is.EqualTo(toTokens[0]));
        }
    }

    [Test]
    public void ComputeMaxFlow_WithWrap_IncludesWrappedTokens()
    {
        // Arrange - Create network with wrapped tokens
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        var wrappedToken = "0x6000000000000000000000000000000000000006";
        var targetFlow = "1000000000000000000"; // 1 token
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow, withWrap: true),
            UInt256.Parse(targetFlow));
        
        // Assert - Check if wrapped tokens are used in the solution
        bool wrappedTokenUsed = result.Transfers.Any(t => t.TokenOwner == wrappedToken);
        
        // This assertion depends on having wrapped tokens in the sample data
        // It might need to be adjusted based on the actual network structure
        if (_fixture.Balances.Any(b => b.IsWrapped && b.Account == sourceAddress))
        {
            Assert.That(wrappedTokenUsed, Is.True);
        }
    }

    [Test]
    public void ComputeMaxFlow_WithVirtualSink_HandlesSourceEqualsSink()
    {
        // Arrange
        var address = "0x1000000000000000000000000000000000000001";
        var toTokens = new List<string> { "0x3000000000000000000000000000000000000003" };
        var targetFlow = "1000000000000000000"; // 1 token
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(address, address, targetFlow, toTokens: toTokens),
            UInt256.Parse(targetFlow));
        
        // Assert
        // Verify that we got a result with the same address as source and sink
        Assert.That(result.Transfers, Is.Not.Empty);
        
        foreach (var transfer in result.Transfers)
        {
            if (transfer.To == address)
            {
                // If this is a sink transfer, verify the token is in our toTokens list
                Assert.That(toTokens.Contains(transfer.TokenOwner), Is.True);
            }
        }
    }

    [Test]
    public void ComputeMaxFlow_RespectsMaxFlowLimit()
    {
        // Arrange
        var sourceAddress = "0x1000000000000000000000000000000000000001";
        var sinkAddress = "0x2000000000000000000000000000000000000002";
        var targetFlow = "500000000000000000"; // 0.5 token
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow),
            UInt256.Parse(targetFlow));
        
        // Assert
        // The returned max flow should be less than or equal to target flow
        UInt256 maxFlow = UInt256.Parse(result.MaxFlow);
        UInt256 targetFlowValue = UInt256.Parse(targetFlow);
        
        Assert.That(maxFlow, Is.LessThanOrEqualTo(targetFlowValue));
    }

    [Test]
    public void ComputeMaxFlow_LinearNetwork_FindsCorrectPath()
    {
        // Arrange - Create a linear trust network
        _fixture.ReconfigureWithTestNetwork(5, linear: true);
        
        var sourceAddress = "0x0000000000000000000000000000000000000000";
        var sinkAddress = "0x0000000000000000000000000000000000000004";
        var targetFlow = "100000000"; // A value within the capacity of our test network
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow),
            UInt256.Parse(targetFlow));
        
        // Assert
        Assert.That(result.MaxFlow, Is.Not.EqualTo("0"));
        Assert.That(result.Transfers.Count, Is.GreaterThan(0));
        
        // Verify that the path follows the linear trust chain
        // The path should be something like 0->1->2->3->4
        var sortedTransfers = result.Transfers.OrderBy(t => t.From).ToList();
        
        for (int i = 0; i < 4; i++)
        {
            bool foundLinkInChain = sortedTransfers.Any(t => 
                t.From == $"0x000000000000000000000000000000000000000{i}" && 
                t.To == $"0x000000000000000000000000000000000000000{i+1}");
            
            Assert.That(foundLinkInChain, Is.True, $"Could not find link from node {i} to node {i+1}");
        }
    }
}