using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// High-order flag combination tests (3-5 flags simultaneously).
/// Covers gap G3: triple/quadruple flag combos.
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class KitchenSinkTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    private static void AssertMaxFlowPositive(string? maxFlowStr, string message = "MaxFlow should be positive")
    {
        Assert.That(maxFlowStr, Is.Not.Null.And.Not.Empty, "MaxFlow should not be null or empty");
        var maxFlow = UInt256.Parse(maxFlowStr!);
        Assert.That(maxFlow > UInt256.Zero, Is.True, message);
    }

    [Test]
    public void KS01_Quantized_Wrapped_Consent_TripleCombo()
    {
        // Triple: quantized + wrapped + consent
        var source = Node(1);
        var sink = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapper, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, avatar);
        mock.AddTrust(sink, wrapper);
        mock.AddTrust(source, sink); // Bidirectional: consent requires sender trusts receiver
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
            WithWrap = true,
            QuantizedMode = true,
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Quantized + wrapped + consent triple should work");
    }

    [Test]
    public void KS02_Quantized_Wrapped_Consent_Groups_QuadCombo()
    {
        // Quad: quantized + wrapped + consent + group minting
        var source = Node(1);
        var sink = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);
        var group = Node(100);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapper, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
        mock.AddGroup(group);
        mock.AddGroupTrust(group, wrapper); // Group trusts wrapper as collateral
        mock.AddTrust(sink, group); // Sink trusts group token
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
            WithWrap = true,
            QuantizedMode = true,
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Group minting with wrapped collateral, quantized, consented
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void KS03_Quantized_Wrapped_ToTokens_MaxTransfers_QuadCombo()
    {
        // Quad: quantized + wrapped + toTokens + maxTransfers
        var source = Node(1);
        var sink = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapper, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, avatar);
        mock.AddTrust(sink, wrapper);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(avatar) },
            MaxTransfers = 2
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(result.Transfers == null || result.Transfers.Count <= 3);
    }

    [Test]
    public void KS04_Consent_Groups_ExcludedFromTokens_TripleCombo()
    {
        // Triple: consent + groups + excludedFromTokens
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var collateralA = Node(10); // excluded
        var collateralB = Node(11); // available

        var mock = new MockLoadGraph();
        mock.AddBalance(source, collateralA, 200_000_000);
        mock.AddBalance(source, collateralB, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, collateralA);
        mock.AddGroupTrust(group, collateralB);
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
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(collateralA) },
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Should use only collateralB (100 CRC) via group minting with consent
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void KS05_Quantized_Groups_ToTokens_Consent_QuadCombo()
    {
        // Quad: quantized + groups + toTokens + consent
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

    [Test]
    public void KS06_AllParameters_NonConflicting_FullKitchenSink()
    {
        // All 14 parameters set (non-conflicting configuration)
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);
        var tokenC = Node(12);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 200_000_000);
        mock.AddBalance(source, wrapper, 100_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(tokenA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);
        mock.AddTrust(sink, wrapper);
        mock.AddTrust(source, sink); // Bidirectional for consent
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
            TargetFlow = "96000000000000000000",
            WithWrap = true,
            QuantizedMode = true,
            MaxTransfers = 5,
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA) },
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA) },
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(tokenC) }, // Not held anyway
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(tokenB) }, // Not trusted anyway
            DebugShowIntermediateSteps = true,
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            },
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(tokenA),
                    Amount = "300000000000000000000",
                    IsStatic = true
                }
            },
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new()
                {
                    Truster = AddressIdPool.StringOf(sink),
                    Trustee = AddressIdPool.StringOf(tokenA)
                }
            }
        };

        // Should NOT crash with all parameters set
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(result.Debug, Is.Not.Null, "Debug output should be present");
    }

    [Test]
    public void KS07_Debug_Quantized_Wrapped_Groups_DebugOutputComplete()
    {
        // Debug output for complex path: quantized + wrapped + groups
        var source = Node(1);
        var sink = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);
        var group = Node(100);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrapper, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
        mock.AddGroup(group);
        mock.AddGroupTrust(group, wrapper);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            QuantizedMode = true,
            DebugShowIntermediateSteps = true
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        Assert.That(result.Debug, Is.Not.Null, "Debug should be present for complex path");
    }

    [Test]
    public void KS08_Simulated_Wrapped_Quantized_Consent_QuadSimulation()
    {
        // Quad: simulated + wrapped + quantized + consent
        var source = Node(1);
        var sink = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
        // No real balances — everything simulated
        mock.AddWrapperMapping(AddressIdPool.StringOf(wrapper), AddressIdPool.StringOf(avatar));
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
            WithWrap = true,
            QuantizedMode = true,
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(wrapper),
                    Amount = "200000000000000000000",
                    IsWrapped = true,
                    IsStatic = true
                }
            },
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new()
                {
                    Truster = AddressIdPool.StringOf(sink),
                    Trustee = AddressIdPool.StringOf(avatar)
                },
                new()
                {
                    Truster = AddressIdPool.StringOf(sink),
                    Trustee = AddressIdPool.StringOf(wrapper)
                },
                new()
                {
                    Truster = AddressIdPool.StringOf(source),
                    Trustee = AddressIdPool.StringOf(sink) // Bidirectional for consent
                }
            },
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Fully simulated + wrapped + quantized + consent should work");
    }
}
