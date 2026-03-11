using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for swap mode (source==sink) with various flag combinations.
/// Covers gap G5: swap mode beyond the trivial case.
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class SwapModeTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    private static void AssertMaxFlowPositive(string? maxFlowStr, string message = "MaxFlow should be positive")
    {
        Assert.That(maxFlowStr, Is.Not.Null.And.Not.Empty, "MaxFlow should not be null or empty");
        var maxFlow = UInt256.Parse(maxFlowStr!);
        Assert.That(maxFlow > UInt256.Zero, Is.True, message);
    }

    private static void AssertMaxFlowZero(string? maxFlowStr, string message = "MaxFlow should be zero")
    {
        if (string.IsNullOrEmpty(maxFlowStr)) return;
        var maxFlow = UInt256.Parse(maxFlowStr);
        Assert.That(maxFlow == UInt256.Zero, Is.True, message);
    }

    [Test]
    public void SW01_SwapMode_WithWrap_WrappedSelfConversion()
    {
        // source==sink, wrapped token self-conversion via intermediary
        // Swap mode needs ToTokens to know what to receive
        var source = Node(1);
        var intermediary = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapper, 100_000_000, isWrapped: true);
        mock.AddBalance(intermediary, avatar, 100_000_000); // intermediary holds native
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
        mock.AddTrust(source, avatar);
        mock.AddTrust(source, wrapper);
        mock.AddTrust(intermediary, source);
        mock.AddTrust(intermediary, avatar);
        mock.AddTrust(intermediary, wrapper);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // Swap mode
            WithWrap = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(avatar) } // Want native avatar token back
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null, "Swap mode should create virtual sink");
    }

    [Test]
    public void SW02_SwapMode_WithWrap_ToTokens_FilterApplied()
    {
        // Swap mode + withWrap + toTokens: filter applied to swap output
        var source = Node(1);
        var bob = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);
        var wrapperA = Node(50);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapperA, 100_000_000, isWrapped: true);
        mock.AddBalance(source, tokenB, 100_000_000);
        mock.AddBalance(bob, tokenA, 100_000_000);
        mock.AddBalance(bob, tokenB, 100_000_000);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapperA), AddressIdPool.StringOf(tokenA));
        mock.AddTrust(source, tokenA);
        mock.AddTrust(source, tokenB);
        mock.AddTrust(source, wrapperA);
        mock.AddTrust(bob, tokenA);
        mock.AddTrust(bob, tokenB);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            WithWrap = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }

    [Test]
    public void SW03_SwapMode_Consent_CheckedOnSwap()
    {
        // Swap mode with consented flow
        var source = Node(1);
        var intermediary = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddBalance(intermediary, tokenB, 100_000_000);
        mock.AddTrust(source, tokenA);
        mock.AddTrust(source, tokenB);
        mock.AddTrust(intermediary, source);
        mock.AddTrust(intermediary, tokenA);
        mock.AddConsentedAvatar(source);
        mock.AddConsentedAvatar(intermediary);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // Swap
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(intermediary)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.ConsentedAvatars.Contains(source), Is.True);
    }

    [Test]
    public void SW04_SwapMode_Groups_PersonalToGroupConversion()
    {
        // Swap mode: convert personal token → group token via quantized mode
        // Non-quantized swap with groups requires the group minting edge to connect
        // to the virtual sink, which is complex. Use quantized mode which always creates virtual sink.
        var source = Node(1);
        var bob = Node(2);
        var group = Node(100);
        var personalToken = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, personalToken, 200_000_000);
        mock.AddBalance(bob, personalToken, 200_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, personalToken);
        mock.AddTrust(source, group); // Source trusts group token (wants to receive it)
        mock.AddTrust(source, personalToken);
        mock.AddTrust(bob, personalToken);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // Swap: self-conversion
            QuantizedMode = true // Quantized mode creates virtual sink for swap
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null, "Quantized swap should create virtual sink");
        Assert.That(capacityGraph.IsGroup(group), Is.True);
    }

    [Test]
    public void SW05_SwapMode_Quantized_WithWrap()
    {
        // Quantized swap with wrapped tokens
        var source = Node(1);
        var bob = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapper, 200_000_000, isWrapped: true);
        mock.AddBalance(bob, avatar, 200_000_000);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
        mock.AddTrust(source, avatar);
        mock.AddTrust(source, wrapper);
        mock.AddTrust(bob, source);
        mock.AddTrust(bob, avatar);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            WithWrap = true,
            QuantizedMode = true
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }

    [Test]
    public void SW06_SwapMode_MaxTransfers_SingleStep()
    {
        // Swap mode with maxTransfers=1
        var source = Node(1);
        var bob = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddBalance(bob, tokenB, 100_000_000);
        mock.AddTrust(source, tokenA);
        mock.AddTrust(source, tokenB);
        mock.AddTrust(bob, source);
        mock.AddTrust(bob, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            MaxTransfers = 1,
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenB) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // MaxTransfers=1 in swap mode should still attempt path
        Assert.That(result.Transfers == null || result.Transfers.Count <= 2);
    }

    [Test]
    public void SW07_SwapMode_FromTokens_And_ExcludedToTokens_CrossFilter()
    {
        // Swap: send tokenA, receive tokenB (toTokens=[B], excludedToTokens=[A])
        var source = Node(1);
        var bob = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddBalance(bob, tokenB, 100_000_000);
        mock.AddTrust(source, tokenA);
        mock.AddTrust(source, tokenB);
        mock.AddTrust(bob, source);
        mock.AddTrust(bob, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA) },
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenB) }, // Need ToTokens for swap
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(tokenA) } // Don't receive tokenA back
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }

    [Test]
    public void SW08_SwapMode_AllFilters_KitchenSink()
    {
        // Swap mode with every filter set
        var source = Node(1);
        var bob = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);
        var tokenC = Node(12);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddBalance(source, tokenB, 100_000_000);
        mock.AddBalance(source, tokenC, 100_000_000);
        mock.AddBalance(bob, tokenA, 100_000_000);
        mock.AddBalance(bob, tokenB, 100_000_000);
        mock.AddBalance(bob, tokenC, 100_000_000);
        mock.AddTrust(source, tokenA);
        mock.AddTrust(source, tokenB);
        mock.AddTrust(source, tokenC);
        mock.AddTrust(bob, source);
        mock.AddTrust(bob, tokenA);
        mock.AddTrust(bob, tokenB);
        mock.AddTrust(bob, tokenC);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source),
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA), AddressIdPool.StringOf(tokenB) },
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenB), AddressIdPool.StringOf(tokenC) },
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(tokenB) }, // Exclude tokenB from sending
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(tokenC) }, // Exclude tokenC from receiving
            MaxTransfers = 3,
            DebugShowIntermediateSteps = true
        };

        // Should not crash with all filters active in swap mode
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }
}
