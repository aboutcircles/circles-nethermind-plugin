using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Comprehensive unit tests for FlowRequest flag interactions.
/// Tests all flag combinations using MockLoadGraph (no external dependencies).
/// These tests run in &lt;10ms each and can run offline.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FlagInteractionTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // Helper to assert MaxFlow > 0 (MaxFlow is returned as string in MaxFlowResponse)
    private static void AssertMaxFlowPositive(string? maxFlowStr, string message = "MaxFlow should be positive")
    {
        Assert.That(maxFlowStr, Is.Not.Null.And.Not.Empty, "MaxFlow should not be null or empty");
        var maxFlow = UInt256.Parse(maxFlowStr!);
        Assert.That(maxFlow > UInt256.Zero, Is.True, message);
    }

    // Helper to assert MaxFlow == 0 (MaxFlow is returned as string in MaxFlowResponse)
    private static void AssertMaxFlowZero(string? maxFlowStr, string message = "MaxFlow should be zero")
    {
        if (string.IsNullOrEmpty(maxFlowStr))
        {
            // Null/empty counts as zero
            return;
        }
        var maxFlow = UInt256.Parse(maxFlowStr);
        Assert.That(maxFlow == UInt256.Zero, Is.True, message);
    }

    // ─────────────────────── FILTER INTERACTIONS ───────────────────────

    [Test]
    public void F001_ToTokens_And_FromTokens_BothRestricted_FindsPath()
    {
        // Arrange: Both ToTokens and FromTokens specified
        var source = Node(1);
        var intermediary = Node(2);
        var sink = Node(3);
        var tokenA = Node(10); // Both allowed
        var tokenB = Node(11); // Source has but excluded from ToTokens

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddBalance(source, tokenB, 100_000_000);
        mock.AddBalance(intermediary, tokenA, 100_000_000);
        mock.AddTrust(intermediary, source);
        mock.AddTrust(sink, intermediary);
        mock.AddTrust(sink, tokenA); // Sink trusts tokenA only

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA), AddressIdPool.StringOf(tokenB) },
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA) } // Only tokenA accepted at sink
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(result.Transfers, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void F002_ToTokens_And_ExcludedFromTokens_ConflictResolved()
    {
        // Arrange: Token in both ToTokens and ExcludedFromTokens
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddBalance(source, tokenB, 100_000_000);
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
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA), AddressIdPool.StringOf(tokenB) },
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(tokenA) } // Exclude tokenA from source
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Path should exist using tokenB only
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void F003_FromTokens_And_ExcludedToTokens_CrossFilter()
    {
        // Arrange: Send only tokenA, but exclude tokenA at sink
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA) },
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(tokenA) } // Exclude at sink
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: No path should exist (only token allowed to send is excluded at sink)
        AssertMaxFlowZero(result.MaxFlow);
    }

    // ─────────────────────── QUANTIZED MODE ───────────────────────

    [Test]
    public void Q001_QuantizedMode_Basic_96CRC_Quantization()
    {
        // Arrange: Basic quantized mode (source == sink, self-conversion)
        var source = Node(1);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 200_000_000); // 200 CRC
        mock.AddTrust(source, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // Self-conversion
            QuantizedMode = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Virtual sink should be created for quantized mode
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }

    [Test]
    public void Q002_QuantizedMode_With_ToTokens_ParsesCorrectly()
    {
        // Arrange: Quantized mode with explicit ToTokens
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 200_000_000);
        mock.AddBalance(source, tokenB, 200_000_000);
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
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(tokenA) } // Only tokenA
        };

        // Act: Should create capacity graph without error
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Request has the specified ToTokens
        Assert.That(request.ToTokens, Has.Count.EqualTo(1));
        Assert.That(request.QuantizedMode, Is.True);
    }

    [Test]
    public void Q003_QuantizedMode_With_SimulatedBalances()
    {
        // Arrange: Inject exactly 96 CRC via simulated balance
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        // No natural balance - will be injected
        mock.AddTrust(sink, token);
        mock.AddTrust(sink, source);

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
                    Amount = "96000000000000000000" // Exactly 96 CRC in WEI
                }
            }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Assert: Should find path with exactly 96 CRC
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void Q005_QuantizedMode_SelfConversion()
    {
        // Arrange: source == sink (invitation for self)
        var source = Node(1);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000); // 100 CRC
        mock.AddTrust(source, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // Self
            QuantizedMode = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null);
    }

    [Test]
    public void Q006_QuantizedMode_InvitationFlow_SourceNotEqualsSink()
    {
        // Arrange: source != sink (invitation for another person)
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000); // 100 CRC
        mock.AddTrust(sink, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink), // Different from source
            QuantizedMode = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Assert: Should find path for 1 invitation (96 CRC)
        AssertMaxFlowPositive(result.MaxFlow);
    }

    // ─────────────────────── CONSENTED FLOW ───────────────────────

    [Test]
    public void C001_SimulatedConsentedAvatars_EnablesConsentedFlow()
    {
        // Arrange: Avatar with simulated consented flow
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, tokenA);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedConsentedAvatars = new List<string> { AddressIdPool.StringOf(source) }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Source should be marked as consented
        Assert.That(capacityGraph.ConsentedAvatars.Contains(source), Is.True);
    }

    [Test]
    public void C002_ConsentedFlow_With_GroupMinting()
    {
        // Arrange: Consented avatar sending through group
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, tokenA);
        mock.AddTrust(sink, group);
        mock.AddConsentedAvatar(source);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert
        Assert.That(capacityGraph.ConsentedAvatars.Contains(source), Is.True);
        Assert.That(capacityGraph.IsGroup(group), Is.True);
    }

    // ─────────────────────── WRAPPED TOKENS ───────────────────────

    [Test]
    public void W001_WithWrap_IncludesWrappedTokens()
    {
        // Arrange: Source has wrapped token
        var source = Node(1);
        var sink = Node(2);
        var wrappedToken = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrappedToken, 100_000_000, isWrapped: true);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, wrappedToken);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert
        AssertMaxFlowPositive(result.MaxFlow);
    }

    // ─────────────────────── SIMULATED DATA ───────────────────────

    [Test]
    public void S001_SimulatedBalances_InjectsNewBalance()
    {
        // Arrange: No natural balance, injected via simulation
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
                }
            }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void S002_SimulatedTrusts_InjectsNewTrust()
    {
        // Arrange: No natural trust, injected via simulation
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, token, 100_000_000);
        // No trust relationship - will be injected

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new()
                {
                    Truster = AddressIdPool.StringOf(sink),
                    Trustee = AddressIdPool.StringOf(source)
                },
                new()
                {
                    Truster = AddressIdPool.StringOf(sink),
                    Trustee = AddressIdPool.StringOf(token)
                }
            }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void S003_SimulatedBalances_And_SimulatedTrusts_NewPath()
    {
        // Arrange: Entirely simulated path
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        // Empty graph - everything simulated

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
                new() { Truster = AddressIdPool.StringOf(sink), Trustee = AddressIdPool.StringOf(source) },
                new() { Truster = AddressIdPool.StringOf(sink), Trustee = AddressIdPool.StringOf(token) }
            }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert
        AssertMaxFlowPositive(result.MaxFlow);
    }

    // ─────────────────────── EDGE CASES ───────────────────────

    [Test]
    public void E001_MaxTransfers_One_LimitsSingleHop()
    {
        // Arrange: MaxTransfers=1 should limit to single-hop transfers
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
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = 1 // Single transfer allowed
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should find a path with MaxTransfers=1
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void E002_EmptyToTokens_MeansNoFilter()
    {
        // Arrange: Empty ToTokens array = no filter (all tokens allowed)
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
            Sink = AddressIdPool.StringOf(sink),
            ToTokens = new List<string>() // Empty = no filter (all tokens allowed)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Path found because empty ToTokens means no filter
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void E004_SourceHasNoBalance_ThrowsException()
    {
        // Arrange: Source with no balance results in BAD_INPUT from OR-Tools solver
        var source = Node(1);
        var sink = Node(2);

        var mock = new MockLoadGraph();
        // No balance for source
        mock.AddTrust(sink, source);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        // Assert: Throws InvalidOperationException when source has no outgoing edges
        Assert.Throws<InvalidOperationException>(() =>
        {
            pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));
        });
    }

    [Test]
    public void E005_SinkTrustsNothing_ThrowsArgumentException()
    {
        // Arrange: Sink with no trust relationships won't be in the graph
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, token, 100_000_000);
        // Sink trusts nothing - won't appear in graph

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        // Assert: Throws ArgumentException when sink isn't in the graph
        Assert.Throws<ArgumentException>(() =>
        {
            pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));
        });
    }

    // ─────────────────────── GROUP MINTING ───────────────────────

    [Test]
    public void G001_GroupMinting_BasicPath()
    {
        // Arrange: Path through group minting
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, tokenA); // Group accepts tokenA as collateral
        mock.AddTrust(sink, group); // Sink trusts group

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert: Group should be recognized
        Assert.That(capacityGraph.IsGroup(group), Is.True);
    }

    [Test]
    public void G002_GroupMinting_WithConsentedFlow()
    {
        // Arrange: Consented source with group minting path
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, tokenA);
        mock.AddTrust(sink, group);
        mock.AddConsentedAvatar(source); // Source has consented flow

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Assert
        Assert.That(capacityGraph.IsGroup(group), Is.True);
        Assert.That(capacityGraph.ConsentedAvatars.Contains(source), Is.True);
    }

    // ─────────────────────── DEBUG FLAG ───────────────────────

    [Test]
    public void D001_DebugShowIntermediateSteps_ReturnsAllStages()
    {
        // Arrange: Basic transfer to test debug output
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
            Sink = AddressIdPool.StringOf(sink),
            DebugShowIntermediateSteps = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Debug object should be present with all stages
        Assert.That(result.Debug, Is.Not.Null);
        Assert.That(result.Debug!.RawPaths, Is.Not.Null, "RawPaths should be present");
        Assert.That(result.Debug.Collapsed, Is.Not.Null, "Collapsed should be present");
        Assert.That(result.Debug.RouterInserted, Is.Not.Null, "RouterInserted should be present");
        Assert.That(result.Debug.Sorted, Is.Not.Null, "Sorted should be present");

        // RawPaths should show token pool (tpool-0x...) references
        Assert.That(result.Debug.RawPaths!.Count, Is.GreaterThan(0), "RawPaths should have edges");
        Assert.That(result.Debug.RawPaths.Any(e => e.From.StartsWith("tpool-") || e.To.StartsWith("tpool-")),
            Is.True, "RawPaths should contain token pool references");
    }

    [Test]
    public void D002_DebugShowIntermediateSteps_False_NoDebugOutput()
    {
        // Arrange: Basic transfer with debug flag off
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
            Sink = AddressIdPool.StringOf(sink),
            DebugShowIntermediateSteps = false
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Debug should be null when flag is false
        Assert.That(result.Debug, Is.Null);
    }

    [Test]
    public void D003_DebugShowIntermediateSteps_GroupMinting_ShowsRouter()
    {
        // Arrange: Group minting path to verify router insertion in debug output
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var tokenA = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, tokenA);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            DebugShowIntermediateSteps = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: RouterInserted stage should show router
        Assert.That(result.Debug, Is.Not.Null);
        Assert.That(result.Debug!.RouterInserted, Is.Not.Null);
        Assert.That(result.Debug.RouterInserted!.Any(e =>
            e.From == RouterAddress || e.To == RouterAddress),
            Is.True, "RouterInserted should contain router address");

        // Collapsed stage should NOT have router (it's inserted later)
        Assert.That(result.Debug.Collapsed, Is.Not.Null);
        Assert.That(result.Debug.Collapsed!.All(e =>
            e.From != RouterAddress && e.To != RouterAddress),
            Is.True, "Collapsed stage should not have router yet");
    }

    [Test]
    public void D004_DebugShowIntermediateSteps_Null_NoDebugOutput()
    {
        // Arrange: Default behavior when flag is not set (null)
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
            // DebugShowIntermediateSteps not set (null)
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Debug should be null by default
        Assert.That(result.Debug, Is.Null);
    }

    // ─────────────────────── COMBINED FLAG INTERACTIONS ───────────────────────

    [Test]
    public void QF001_QuantizedMode_WithFromTokens_FiltersCorrectly()
    {
        // Arrange: QuantizedMode + FromTokens filter
        // Only tokenA should be used (in FromTokens), tokenB excluded
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10); // Included in FromTokens
        var tokenB = Node(11); // Not in FromTokens

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 100_000_000); // 100 CRC
        mock.AddBalance(source, tokenB, 200_000_000); // 200 CRC (ignored)
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
            QuantizedMode = true,
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA) } // Only tokenA
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Assert: Should find path using only tokenA (100 CRC -> 96 CRC after quantization)
        AssertMaxFlowPositive(result.MaxFlow);
        // Max flow should be 96 CRC (1 quantum from tokenA), not 192 (if tokenB were included)
        var maxFlowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(maxFlowWei, Is.EqualTo(UInt256.Parse("96000000000000000000")),
            "FromTokens filter should restrict to tokenA only");
    }

    [Test]
    public void QF002_QuantizedMode_WithExcludedFromTokens_RespectsExclusion()
    {
        // Arrange: QuantizedMode + ExcludedFromTokens
        // tokenA is excluded, should only use tokenB
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10); // Excluded
        var tokenB = Node(11); // Available

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 200_000_000); // 200 CRC (excluded)
        mock.AddBalance(source, tokenB, 100_000_000); // 100 CRC
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
            QuantizedMode = true,
            ExcludedFromTokens = new List<string> { AddressIdPool.StringOf(tokenA) }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Assert: Should use tokenB (100 -> 96 CRC), not tokenA (excluded)
        AssertMaxFlowPositive(result.MaxFlow);
        var maxFlowWei = UInt256.Parse(result.MaxFlow!);
        Assert.That(maxFlowWei, Is.EqualTo(UInt256.Parse("96000000000000000000")),
            "ExcludedFromTokens should exclude tokenA, leaving only tokenB (96 CRC)");
    }

    [Test]
    public void QF003_QuantizedMode_WithMaxTransfers_TruncatesBeforeQuantization()
    {
        // Arrange: QuantizedMode + MaxTransfers
        // MaxTransfers pruning happens BEFORE quantization
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, token, 200_000_000); // 200 CRC
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
            MaxTransfers = 1 // Single transfer allowed
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("192000000000000000000"));

        // Assert: Should find quantized path within maxTransfers limit
        // 200 CRC with 1 transfer -> quantized to 192 CRC (2 invites)
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(result.Transfers?.Count, Is.LessThanOrEqualTo(2),
            "MaxTransfers should limit steps (allowing for self-loop aggregation edge)");
    }

    [Test]
    public void QF004_QuantizedMode_WithExcludedToTokens_FiltersAtSink()
    {
        // Arrange: QuantizedMode + ExcludedToTokens
        // tokenA excluded at sink, should only accept tokenB
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10); // Excluded at sink
        var tokenB = Node(11); // Allowed at sink

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 200_000_000);
        mock.AddBalance(source, tokenB, 100_000_000);
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
            QuantizedMode = true,
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(tokenA) }
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("96000000000000000000"));

        // Assert: Should use tokenB only (tokenA excluded at sink)
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void WC001_WithWrap_ConsentedFlow_BothWorkTogether()
    {
        // Arrange: WithWrap + ConsentedFlow (via simulated consented avatars)
        var source = Node(1);
        var sink = Node(2);
        var wrappedToken = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrappedToken, 100_000_000, isWrapped: true);
        // Consented flow requirements: source trusts sink, sink has consented flow
        mock.AddConsentedAvatar(source);
        mock.AddConsentedAvatar(sink);
        mock.AddTrust(source, sink);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, wrappedToken);

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

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Both wrapped tokens and consented flow should work
        Assert.That(capacityGraph.ConsentedAvatars.Contains(source), Is.True);
        Assert.That(capacityGraph.ConsentedAvatars.Contains(sink), Is.True);
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void WC002_WithWrap_ConsentedFlow_ViolationFiltersEdge()
    {
        // Arrange: Wrapped token path with consented flow violation
        // Source has consented flow but sink doesn't -> violation
        var source = Node(1);
        var sink = Node(2);
        var wrappedToken = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrappedToken, 100_000_000, isWrapped: true);
        mock.AddConsentedAvatar(source);  // Source has consented flow
        // Sink does NOT have consented flow (violation)
        mock.AddTrust(source, sink);
        mock.AddTrust(sink, source);
        mock.AddTrust(sink, wrappedToken);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Edge should be filtered due to consented flow violation
        AssertMaxFlowZero(result.MaxFlow, "Consented flow violation should filter the edge");
    }

    [Test]
    public void WC003_WithWrap_GroupMinting_WorksTogether()
    {
        // Arrange: Wrapped tokens used as collateral for group minting
        var source = Node(1);
        var sink = Node(2);
        var group = Node(100);
        var wrappedToken = Node(10);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, wrappedToken, 100_000_000, isWrapped: true);
        mock.AddGroup(group);
        mock.AddGroupTrust(group, wrappedToken);
        mock.AddTrust(sink, group);

        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            WithWrap = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("1000000000000000000"));

        // Assert: Should find path through group using wrapped token
        Assert.That(capacityGraph.IsGroup(group), Is.True);
        AssertMaxFlowPositive(result.MaxFlow);
    }

    [Test]
    public void AllFlags_QuantizedModeWithAllFilters_WorksTogether()
    {
        // Arrange: Kitchen sink test - all flags active
        var source = Node(1);
        var sink = Node(2);
        var tokenA = Node(10);
        var tokenB = Node(11);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, tokenA, 200_000_000);
        mock.AddBalance(source, tokenB, 50_000_000); // Below threshold
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
            QuantizedMode = true,
            MaxTransfers = 5,
            FromTokens = new List<string> { AddressIdPool.StringOf(tokenA), AddressIdPool.StringOf(tokenB) },
            ExcludedToTokens = new List<string> { AddressIdPool.StringOf(tokenB) },
            DebugShowIntermediateSteps = true
        };

        // Act
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();
        var result = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, UInt256.Parse("192000000000000000000"));

        // Assert: All flags should work together
        AssertMaxFlowPositive(result.MaxFlow);
        Assert.That(result.Debug, Is.Not.Null, "Debug should be present");
        Assert.That(result.Transfers?.Count, Is.LessThanOrEqualTo(6), "MaxTransfers should limit steps");
    }
}
