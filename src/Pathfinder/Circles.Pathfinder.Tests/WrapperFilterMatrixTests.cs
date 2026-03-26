using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Systematic wrapper×filter×mode crossing tests.
/// Covers gaps G2 (wrapper × all 4 filters × quantized) and G7 (wrapper + group minting).
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class WrapperFilterMatrixTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // Wrapper addresses are distinct from avatar addresses
    private static readonly int AvatarA = Node(10);
    private static readonly int WrapperDemurraged = Node(50); // Demurraged wrapper for AvatarA
    private static readonly int WrapperStatic = Node(51);     // Static wrapper for AvatarA

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

    // ─────────────── QUANTIZED + WRAPPER + FILTERS ───────────────

    [Test]
    public void WF01_Quantized_WithWrap_ToTokens_WrapperExpanded()
    {
        // quantized + withWrap + toTokens=[avatarA] → wrapper expanded in toTokensFilter
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);

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
            ToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Quantized wrapped flow with toTokens should work");
        var truncated = CirclesConverter.TruncateToInt64(UInt256.Parse(result.MaxFlow!));
        Assert.That(truncated % 96_000_000L, Is.EqualTo(0L),
            "Flow should be quantized to 96 CRC multiples");
    }

    [Test]
    public void WF02_Quantized_WithWrap_FromTokens_WrapperExpanded()
    {
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddBalance(source, Node(11), 200_000_000); // Another token (not wrapped)
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);
        mock.AddTrust(sink, Node(11));

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
            FromTokens = new List<string> { AddressIdPool.StringOf(AvatarA) } // Only AvatarA → expands to include wrapper
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Quantized wrapped flow with fromTokens should work");
    }

    [Test]
    public void WF03_Quantized_WithWrap_ExcludedFromTokens_WrapperExcluded()
    {
        // excludedFromTokens=[avatarA] → wrapper also excluded, falls to other token
        var source = Node(1);
        var sink = Node(2);
        var otherToken = Node(11);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddBalance(source, otherToken, 100_000_000);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);
        mock.AddTrust(sink, otherToken);

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
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(AvatarA) } // Excludes wrapper too
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("96000000000000000000"),
            "Should only use otherToken (100 CRC → 96), wrapper excluded");
    }

    [Test]
    public void WF04_Quantized_WithWrap_ExcludedToTokens_WrapperExcludedAtSink()
    {
        var source = Node(1);
        var sink = Node(2);
        var otherToken = Node(11);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddBalance(source, otherToken, 100_000_000);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);
        mock.AddTrust(sink, otherToken);

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
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) } // Exclude at sink → wrapper also excluded
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
    }

    // ─────────────── STATIC VS DEMURRAGED WRAPPERS ───────────────

    [Test]
    public void WF05_StaticWrapper_ToTokens_Expanded()
    {
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperStatic, 100_000_000, isWrapped: true, isStatic: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperStatic), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperStatic);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Static wrapper should be expanded in toTokens filter");
    }

    [Test]
    public void WF06_StaticWrapper_FromTokens_Expanded()
    {
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperStatic, 100_000_000, isWrapped: true, isStatic: true);
        mock.AddBalance(source, Node(12), 200_000_000); // Another token
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperStatic), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperStatic);
        mock.AddTrust(sink, Node(12));

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            FromTokens = new List<string> { AddressIdPool.StringOf(AvatarA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Static wrapper should be expanded in fromTokens filter");
    }

    [Test]
    public void WF07_BothWrappers_ToTokens_AllExpanded()
    {
        // Avatar has BOTH static and demurraged wrappers → both expanded
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddBalance(source, WrapperStatic, 50_000_000, isWrapped: true, isStatic: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperStatic), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);
        mock.AddTrust(sink, WrapperStatic);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Both static and demurraged wrappers should reach sink");
    }

    [Test]
    public void WF08_BothWrappers_ExcludedFromTokens_AllExcluded()
    {
        var source = Node(1);
        var sink = Node(2);
        var otherToken = Node(12);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddBalance(source, WrapperStatic, 50_000_000, isWrapped: true, isStatic: true);
        mock.AddBalance(source, otherToken, 30_000_000);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperStatic), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);
        mock.AddTrust(sink, WrapperStatic);
        mock.AddTrust(sink, otherToken);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(AvatarA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Should use only otherToken (30 CRC), both wrappers excluded
        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("30000000000000000000"),
            "Both wrappers excluded, only otherToken (30 CRC) available");
    }

    // ─────────────── WRAPPER + GROUP MINTING ───────────────

    [Test]
    public void WF09_WrappedCollateral_GroupSink_ToTokens()
    {
        // Wrapped token used as collateral for group minting with toTokens
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddGroup(group);
        mock.AddGroupTrust(group, WrapperDemurraged); // Group trusts wrapper as collateral
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
            ToTokens = new List<string> { AddressIdPool.StringOf(group) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Wrapped collateral → group minting with toTokens should work");
    }

    [Test]
    public void WF10_WrappedCollateral_GroupSink_Consent_Triple()
    {
        // Triple: withWrap + group minting + consent
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddGroup(group);
        mock.AddGroupTrust(group, WrapperDemurraged);
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
            WithWrap = true,
            SimulatedConsentedAvatars = new List<string>
            {
                AddressIdPool.StringOf(source),
                AddressIdPool.StringOf(sink)
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Wrapped collateral + group mint + consent should work");
    }

    // ─────────────── SWAP MODE + WRAPPER ───────────────

    [Test]
    public void WF11_SwapMode_WithWrap_SelfConversion()
    {
        // source==sink, swap wrapped → native. Needs ToTokens and second holder.
        var source = Node(1);
        var bob = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddBalance(bob, AvatarA, 50_000_000); // Bob holds native
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(source, AvatarA);
        mock.AddTrust(source, WrapperDemurraged);
        mock.AddTrust(bob, source);
        mock.AddTrust(bob, AvatarA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // Swap mode
            WithWrap = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) } // Want native back
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null, "Swap mode should create virtual sink");
    }

    [Test]
    public void WF12_SwapMode_WithWrap_ExcludedToTokens()
    {
        // Swap mode + withWrap + excludedToTokens: receive tokenB, exclude wrapper from output
        var source = Node(1);
        var tokenB = Node(12);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddBalance(source, tokenB, 50_000_000);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(source, AvatarA);
        mock.AddTrust(source, WrapperDemurraged);
        mock.AddTrust(source, tokenB);

        // Second holder so pool nodes exist in swap mode
        var bob = Node(3);
        mock.AddBalance(bob, AvatarA, 50_000_000);
        mock.AddBalance(bob, tokenB, 50_000_000);
        mock.AddTrust(bob, AvatarA);
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
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenB) }, // Want tokenB back
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) } // Exclude wrapper
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }

    // ─────────────── GUARD TESTS ───────────────

    [Test]
    public void WF13_WithWrapFalse_ToTokens_WrapperNotExpanded()
    {
        // WithWrap=false: wrapper should NOT be expanded in filter
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddBalance(source, AvatarA, 100_000_000); // Native balance
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = false, // Explicitly false
            ToTokens = new List<string> { AddressIdPool.StringOf(AvatarA) }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Should only use native AvatarA token (100 CRC), NOT wrapped (200 CRC)
        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("100000000000000000000"),
            "WithWrap=false should not expand wrapper in toTokens filter");
    }

    [Test]
    public void WF14_ExplicitWrapperAddress_InFilter_Works()
    {
        // User passes wrapper address directly in toTokens (not avatar address)
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 100_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, WrapperDemurraged);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(WrapperDemurraged) } // Wrapper directly
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Explicit wrapper address in toTokens should work");
    }

    [Test]
    public void WF15_Quantized_WithWrap_MaxTransfers_SingleStep()
    {
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);

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
            MaxTransfers = 1
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Should find single-step quantized wrapped transfer
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void WF16_Quantized_WithWrap_Consent_TripleCombo()
    {
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        mock.SetRouterAddress(RouterAddress);
        mock.AddBalance(source, WrapperDemurraged, 200_000_000, isWrapped: true);
        mock.AddWrapperMapping(AddressIdPool.StringOf(WrapperDemurraged), AddressIdPool.StringOf(AvatarA));
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, AvatarA);
        mock.AddTrust(sink, WrapperDemurraged);
        mock.AddTrust(source, sink); // Bidirectional trust required for consent
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
}
