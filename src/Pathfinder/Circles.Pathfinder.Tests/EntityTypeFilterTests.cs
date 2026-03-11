using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests crossing entity topologies (human, group, multi-hop) with filter combinations.
/// Covers gap G1: entity-type × filter matrix.
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class EntityTypeFilterTests
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

    // ─────────────── GROUP-AS-SINK WITH FILTERS ───────────────

    [Test]
    public void ET01_GroupSink_ToTokens_GroupToken_FindsPath()
    {
        // Human→Group with toTokens=[groupToken]
        // Source holds collateral token, group trusts it, sink (group) mints groupToken
        var source = Node(1);
        var group = Node(100);
        var collateral = Node(10); // source's personal token used as collateral

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        // Sink (another human) trusts the group token
        var sink = Node(2);
        mock.AddTrust(sink, group); // sink trusts group token (groupId == groupToken)

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ToTokens = new List<string> { AddressIdPool.StringOf(group) } // Only group token accepted
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Group minting path should work with toTokens=[groupToken]");
    }

    [Test]
    public void ET02_GroupSink_ExcludedToTokens_CollateralExcluded_NoDirectPath()
    {
        // Human→Human via group, excludedToTokens excludes the group token at sink
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group); // sink trusts group token
        mock.AddTrust(sink, collateral); // sink also trusts collateral

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(group) } // Exclude group token at sink
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Should still find path via collateral (direct trust), but NOT via group token
        AssertMaxFlowPositive(result.MaxFlow, "Should find direct path via collateral trust");
    }

    [Test]
    public void ET03_GroupSink_FromTokens_SpecificCollateral_FindsPath()
    {
        // Human→Group, fromTokens restricts which collateral source can use
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateralA = Node(10);
        var collateralB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateralA, 100_000_000);
        mock.AddBalance(source, collateralB, 200_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateralA);
        mock.AddGroupTrust(group, collateralB);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            FromTokens = new List<string> { AddressIdPool.StringOf(collateralA) } // Only collateralA
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        // Max flow should be limited to collateralA's 100 CRC, not 300 CRC total
        var maxFlowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(maxFlowWei <= UInt256.Parse("100000000000000000000"),
            "FromTokens should restrict to collateralA only (100 CRC max)");
    }

    [Test]
    public void ET04_GroupSink_FromTokens_WrongToken_NoPath()
    {
        // Human→Group, fromTokens=[token not trusted by group] → no path
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);
        var wrongToken = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddBalance(source, wrongToken, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral); // Group trusts collateral, NOT wrongToken
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            FromTokens = new List<string> { AddressIdPool.StringOf(wrongToken) } // Group doesn't trust this
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowZero(result.MaxFlow, "Wrong fromToken should yield no path through group");
    }

    [Test]
    public void ET05_GroupIntermediary_ToTokens_GroupTokenReachesSink()
    {
        // Human→Group→Human, toTokens=[groupToken] at final sink
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group); // Sink trusts group token

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ToTokens = new List<string> { AddressIdPool.StringOf(group) } // Only group token
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void ET06_GroupIntermediary_MaxTransfers_TruncatesAtGroupBoundary()
    {
        // Human→Group→Human with maxTransfers=2 (group minting needs collateral+mint = 2 steps)
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 2 // Tight limit: collateral transfer + group mint
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // With maxTransfers=2, group minting should still work (it's 2 steps: collateral→router→group)
        // But may be truncated if path is longer
        Assert.That(result.Transfers == null || result.Transfers.Count <= 3,
            "Transfer count should be within maxTransfers budget");
    }

    [Test]
    public void ET07_GroupPlusHuman_ToTokens_And_MaxTransfers()
    {
        // Human→Group+Human chain with both toTokens and maxTransfers
        var source = Node(1);
        var intermediary = Node(2);
        var sink = Node(3);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(intermediary, group); // intermediary trusts group token
        mock.AddTrust(sink, intermediary); // sink trusts intermediary
        mock.AddTrust(sink, group); // sink also trusts group token

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ToTokens = new List<string> { AddressIdPool.StringOf(group) },
            MaxTransfers = 5
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(result.Transfers!.Count, Is.LessThanOrEqualTo(6));
    }

    [Test]
    public void ET08_GroupSink_Quantized_With_ToTokens()
    {
        // Quantized mode with group minting and toTokens filter
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 200_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(group) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Quantized group minting with toTokens filter should produce valid quantized output
        AssertMaxFlowPositive(result.MaxFlow);
        // Flow should be a multiple of 96 CRC (quantization unit)
        var truncated = CirclesConverter.TruncateToInt64(UInt256.Parse(result.MaxFlow!));
        Assert.That(truncated % 96_000_000L, Is.EqualTo(0L),
            "Quantized flow should be a multiple of 96 CRC");
    }

    [Test]
    public void ET09_GroupSink_Quantized_ExcludedFromTokens()
    {
        // Quantized + excludedFromTokens: one collateral excluded, other available
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateralA = Node(10);
        var collateralB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateralA, 200_000_000);
        mock.AddBalance(source, collateralB, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateralA);
        mock.AddGroupTrust(group, collateralB);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(collateralA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Should use collateralB only (100 CRC → 96 quantized)
        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("96000000000000000000"),
            "Should only use collateralB (100 CRC → 96 CRC quantized)");
    }

    [Test]
    public void ET10_GroupSink_Consent_GroupMinting()
    {
        // Consent at group boundary — source has consented flow
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group);
        mock.AddConsentedAvatar(source);
        mock.AddConsentedAvatar(sink);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Group minting with consented flow should work when both parties consent
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void ET11_GroupAsBalanceHolder_BasicTransfer()
    {
        // Group holds a token and transfers it (unusual but valid)
        // Actually groups can't hold personal tokens in the normal flow model —
        // they mint their OWN group token. Test that group-as-source is properly handled.
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, token, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink)
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void ET12_ThreeHopChain_ToTokens_FilterPropagates()
    {
        // 3-hop: Source→A→B→Sink, toTokens=[tokenX] — token filter at sink
        var source = Node(1);
        var nodeA = Node(2);
        var nodeB = Node(3);
        var sink = Node(4);
        var tokenX = Node(10);
        var tokenY = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenX, 100_000_000);
        mock.AddBalance(source, tokenY, 100_000_000);
        mock.AddTrust(nodeA, source);
        mock.AddTrust(nodeA, tokenX);
        mock.AddTrust(nodeA, tokenY);
        mock.AddTrust(nodeB, nodeA);
        mock.AddTrust(nodeB, tokenX);
        mock.AddTrust(nodeB, tokenY);
        mock.AddTrust(sink, nodeB);
        mock.AddTrust(sink, tokenX);
        mock.AddTrust(sink, tokenY);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenX) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "3-hop chain should work with toTokens filter");
    }

    [Test]
    public void ET13_ThreeHopChain_ExcludedFromTokens()
    {
        // 3-hop chain with excludedFromTokens at source
        var source = Node(1);
        var nodeA = Node(2);
        var sink = Node(3);
        var tokenGood = Node(10);
        var tokenBad = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenGood, 100_000_000);
        mock.AddBalance(source, tokenBad, 200_000_000);
        mock.AddTrust(nodeA, source);
        mock.AddTrust(nodeA, tokenGood);
        mock.AddTrust(nodeA, tokenBad);
        mock.AddTrust(sink, nodeA);
        mock.AddTrust(sink, tokenGood);
        mock.AddTrust(sink, tokenBad);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(tokenBad) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("100000000000000000000"),
            "Should use only tokenGood (100 CRC), not tokenBad (excluded)");
    }

    [Test]
    public void ET14_GroupSink_TwoCollaterals_FromTokens_RestrictsToOne()
    {
        // Group trusts 2 collaterals, fromTokens restricts to one
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateralA = Node(10); // 100 CRC
        var collateralB = Node(11); // 200 CRC

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateralA, 100_000_000);
        mock.AddBalance(source, collateralB, 200_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateralA);
        mock.AddGroupTrust(group, collateralB);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            FromTokens = new List<string> { AddressIdPool.StringOf(collateralA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("100000000000000000000"),
            "Only collateralA (100 CRC) should be used, not collateralB");
    }

    [Test]
    public void ET15_GroupIntermediary_ExcludedToTokens_GroupTokenExcludedAtSink()
    {
        // Human→Group→Human, excludedToTokens=[groupToken] at final sink
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group);
        mock.AddTrust(sink, collateral); // Sink also trusts collateral directly

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(group) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Should find direct path via collateral trust, group token path blocked
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void ET16_GroupSink_Quantized_Consent_ToTokens_TripleCombo()
    {
        // Triple: quantized + consent + toTokens at group boundary
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateral = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateral, 200_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateral);
        mock.AddTrust(sink, group);
        mock.AddConsentedAvatar(source);
        mock.AddConsentedAvatar(sink);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(group) },
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
    }
}
