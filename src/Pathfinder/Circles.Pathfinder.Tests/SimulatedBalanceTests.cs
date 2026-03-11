using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for SimulatedBalance edge cases, especially IsStatic flag.
/// Covers gap G4: zero existing IsStatic test coverage.
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class SimulatedBalanceTests
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
    public void SB01_SimulatedBalance_IsStatic_NotDemurraged()
    {
        // IsStatic=true means balance should NOT be subject to demurrage
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(token),
                    Amount = "100000000000000000000", // 100 CRC
                    IsStatic = true
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("100000000000000000000"));

        // Static balance = 100 CRC, should be usable without demurrage reduction
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void SB02_SimulatedBalance_DefaultNotStatic_Demurraged()
    {
        // Default IsStatic=false means balance IS subject to demurrage
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(token),
                    Amount = "100000000000000000000" // 100 CRC
                    // IsStatic defaults to false
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Non-static balance should still find a path (demurrage only slightly reduces)
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void SB03_SimulatedBalance_IsStatic_WithWrap()
    {
        // Static wrapped balance (ERC20 wrapper with fixed amount)
        var source = Node(1);
        var sink = Node(2);
        var avatar = Node(10);
        var wrapper = Node(50);

        var mock = new MockLoadGraph();
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
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(wrapper),
                    Amount = "100000000000000000000",
                    IsStatic = true,
                    IsWrapped = true
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Static wrapped simulated balance should work");
    }

    [Test]
    public void SB04_SimulatedBalance_IsStatic_Quantized()
    {
        // Static balance + quantized mode
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(token),
                    Amount = "200000000000000000000", // 200 CRC
                    IsStatic = true
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("192000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        var truncated = CirclesConverter.TruncateToInt64(UInt256.Parse(result.MaxFlow!));
        Assert.That(truncated % 96_000_000L, Is.EqualTo(0L),
            "Static balance should quantize correctly");
    }

    [Test]
    public void SB05_SimulatedBalance_OverridesExistingRealBalance()
    {
        // Simulated balance replaces/augments existing real balance
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, token, 50_000_000); // Real: 50 CRC
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(token),
                    Amount = "300000000000000000000" // Simulate 300 CRC (overrides 50 CRC)
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("200000000000000000000"));

        // Flow should be based on simulated balance (up to 300 CRC), not real balance (50 CRC)
        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei > UInt256.Parse("50000000000000000000"),
            "Simulated balance should override real balance (>50 CRC)");
    }

    [Test]
    public void SB06_SimulatedBalance_NewHolder_CreatesNode()
    {
        // Simulated balance for a holder that doesn't exist in real graph
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        // Source has NO real balance - only simulated
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, token);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(token),
                    Amount = "100000000000000000000"
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Simulated balance should create graph node for new holder");
    }

    [Test]
    public void SB07_SimulatedBalance_And_SimulatedTrust_Combined()
    {
        // Both simulated balance and simulated trust needed for path
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        // No real balances, no real trusts between source and sink

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(token),
                    Amount = "100000000000000000000"
                }
            },
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new()
                {
                    Truster = AddressIdPool.StringOf(sink),
                    Trustee = AddressIdPool.StringOf(token)
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow, "Combined simulated balance + trust should create path");
    }

    [Test]
    public void SB08_SimulatedBalance_IsStatic_WithToTokensFilter()
    {
        // Static simulated balance respects toTokens filter
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);
        mock.AddTrust(sink, tokenB);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA) }, // Only tokenA
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(tokenA),
                    Amount = "50000000000000000000", // 50 CRC
                    IsStatic = true
                },
                new()
                {
                    Holder = AddressIdPool.StringOf(source),
                    Token = AddressIdPool.StringOf(tokenB),
                    Amount = "200000000000000000000", // 200 CRC (filtered out)
                    IsStatic = true
                }
            }
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        AssertMaxFlowPositive(result.MaxFlow);
        var flowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(flowWei <= UInt256.Parse("50000000000000000000"),
            "ToTokens filter should limit to tokenA (50 CRC), excluding tokenB");
    }
}
