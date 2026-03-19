using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Integration tests covering the full host pipeline:
///   MockLoadGraph → GraphFactory → CapacityGraphPool.Rent() → V2Pathfinder
///
/// These tests would have caught the IsWrapOnly pre-built snapshot bug (#243)
/// where {withWrap:true} without toTokens returned maxFlow=0 because the
/// pre-built snapshot lacked source-specific wrapped supply edges.
/// </summary>
[TestFixture, Parallelizable]
[Category("Integration")]
public class PoolToSolverIntegrationTests
{
    private const string RouterAddr = "0xee00000000000000000000000000000000000001";
    private const string SourceAddr = "0xee10000000000000000000000000000000000001";
    private const string SinkAddr = "0xee20000000000000000000000000000000000002";
    private const string TokenAddr = "0xee30000000000000000000000000000000000003";
    private const string WrapperAddr = "0xee40000000000000000000000000000000000004";
    private const string TokenBAddr = "0xee50000000000000000000000000000000000005";
    private const string GroupAddr = "0xee60000000000000000000000000000000000006";
    private const string BobAddr = "0xee70000000000000000000000000000000000007";

    private static readonly UInt256 HundredCrc = UInt256.Parse("100000000000000000000");

    /// <summary>
    /// End-to-end helper: builds MockLoadGraph, creates pool, rents graph, runs solver.
    /// Returns (maxFlow, transfers) so tests can assert on actual flow results.
    /// </summary>
    private static MaxFlowResponse RunFullPipeline(
        Action<MockLoadGraph> setupGraph,
        FlowRequest request,
        UInt256? targetFlow = null)
    {
        var mock = new MockLoadGraph();
        setupGraph(mock);

        var factory = new GraphFactory(RouterAddr, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var pool = new CapacityGraphPool(RouterAddr, mock);

        // Build and publish the base snapshot (same as NetworkStateUpdaterService does)
        var baseCap = CapacityGraphPool.BuildFullGraph(balanceGraph, trustLookup, mock, RouterAddr).Result;
        pool.UpdateSnapshot(new CapacityGraphSnapshot(1, baseCap));

        // Rent the graph for this specific request
        var handle = pool.Rent(request, balanceGraph, trustLookup).Result;

        var pathfinder = new V2Pathfinder();
        return pathfinder.ComputeMaxFlowWithPath(handle.Graph, request, targetFlow ?? HundredCrc);
    }

    // ----------------------------------------------------------------
    // WRAPPED TOKEN PIPELINE (the IsWrapOnly bug cases)
    // ----------------------------------------------------------------

    [Test]
    public void WithWrap_NoToTokens_WrappedBalanceFlows()
    {
        // THE BUG CASE: {withWrap:true} without toTokens was returning maxFlow=0
        // because IsWrapOnly routed to a pre-built snapshot without wrapped supply edges.
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, WrapperAddr, "200000000000000000000", isWrapped: true);
            mock.AddWrapperMapping(WrapperAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, WrapperAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = true
        });

        AssertPositiveFlow(result, "withWrap=true without toTokens must find wrapped balance flow");
    }

    [Test]
    public void WithWrap_WithToTokens_WrappedBalanceFlows()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, WrapperAddr, "200000000000000000000", isWrapped: true);
            mock.AddWrapperMapping(WrapperAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, WrapperAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = true,
            ToTokens = new List<string> { TokenAddr }
        });

        AssertPositiveFlow(result, "withWrap=true with toTokens must find flow");
    }

    [Test]
    public void WithWrap_BothCases_SameMaxFlow()
    {
        Action<MockLoadGraph> setup = mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, WrapperAddr, "200000000000000000000", isWrapped: true);
            mock.AddWrapperMapping(WrapperAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, WrapperAddr);
        };

        var withoutToTokens = RunFullPipeline(setup, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = true
        });

        var withToTokens = RunFullPipeline(setup, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = true,
            ToTokens = new List<string> { TokenAddr }
        });

        Assert.That(withoutToTokens.MaxFlow, Is.EqualTo(withToTokens.MaxFlow),
            "With and without toTokens should produce identical maxFlow for same graph");
    }

    [Test]
    public void WithWrapFalse_WrappedBalance_NoFlow()
    {
        // Without withWrap, wrapped balances should NOT create supply edges.
        // Source has ONLY wrapped balance → no outgoing edges → returns zero flow.
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, WrapperAddr, "200000000000000000000", isWrapped: true);
            mock.AddWrapperMapping(WrapperAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, WrapperAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = false
        });
        Assert.That(result.MaxFlow, Is.EqualTo("0"));
        Assert.That(result.Transfers, Is.Empty);
    }

    // ----------------------------------------------------------------
    // BASE SNAPSHOT PATH (no filters → shared snapshot)
    // ----------------------------------------------------------------

    [Test]
    public void NoFilters_NativeBalance_FlowsThroughBaseSnapshot()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr
        });

        AssertPositiveFlow(result, "Unfiltered request through base snapshot must find native flow");
    }

    [Test]
    public void NoFilters_UsesSharedSnapshot_NotAdHoc()
    {
        var mock = new MockLoadGraph();
        mock.AddRegisteredAvatar(SourceAddr);
        mock.AddRegisteredAvatar(SinkAddr);
        mock.AddRegisteredAvatar(TokenAddr);
        mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
        mock.AddTrust(SinkAddr, TokenAddr);

        var factory = new GraphFactory(RouterAddr, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var pool = new CapacityGraphPool(RouterAddr, mock);
        var baseCap = CapacityGraphPool.BuildFullGraph(balanceGraph, trustLookup, mock, RouterAddr).Result;
        pool.UpdateSnapshot(new CapacityGraphSnapshot(1, baseCap));

        var request = new FlowRequest { Source = SourceAddr, Sink = SinkAddr };
        var handle1 = pool.Rent(request, balanceGraph, trustLookup).Result;
        var handle2 = pool.Rent(request, balanceGraph, trustLookup).Result;

        Assert.That(handle1.Graph, Is.SameAs(handle2.Graph),
            "Unfiltered requests should return the exact same shared snapshot object");
    }

    // ----------------------------------------------------------------
    // FILTER INTERACTIONS THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void FromTokensFilter_OnlyAllowedTokenFlows()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddRegisteredAvatar(TokenBAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddBalanceWei(SourceAddr, TokenBAddr, "200000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenBAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            FromTokens = new List<string> { TokenAddr } // Only tokenA
        });

        AssertPositiveFlow(result);
        var flow = UInt256.Parse(result.MaxFlow!);
        // Should be limited to tokenA's 100 CRC, not tokenA+tokenB=300
        Assert.That(flow <= UInt256.Parse("100000000000000000000"), Is.True,
            "FromTokens filter should limit to specified tokens only");
    }

    [Test]
    public void ExcludedFromTokens_BlocksSpecifiedToken()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddRegisteredAvatar(TokenBAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddBalanceWei(SourceAddr, TokenBAddr, "200000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenBAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            ExcludedFromTokens = new List<string> { TokenBAddr } // Block tokenB
        });

        AssertPositiveFlow(result);
        var flow = UInt256.Parse(result.MaxFlow!);
        Assert.That(flow <= UInt256.Parse("100000000000000000000"), Is.True,
            "ExcludedFromTokens should block tokenB, leaving only tokenA's 100 CRC");
    }

    [Test]
    public void ToTokensFilter_SinkOnlyReceivesSpecifiedToken()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddRegisteredAvatar(TokenBAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddBalanceWei(SourceAddr, TokenBAddr, "200000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenBAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            ToTokens = new List<string> { TokenAddr }
        });

        AssertPositiveFlow(result);
        var flow = UInt256.Parse(result.MaxFlow!);
        Assert.That(flow <= UInt256.Parse("100000000000000000000"), Is.True,
            "ToTokens filter should limit sink to receiving only tokenA");
    }

    // ----------------------------------------------------------------
    // QUANTIZED MODE THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void QuantizedMode_FlowIsMultipleOf96()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "200000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            QuantizedMode = true
        }, UInt256.Parse("192000000000000000000"));

        AssertPositiveFlow(result);
        var truncated = CirclesConverter.TruncateToInt64(UInt256.Parse(result.MaxFlow!));
        Assert.That(truncated % 96_000_000L, Is.EqualTo(0L),
            "Quantized mode flow must be a multiple of 96 CRC");
    }

    [Test]
    public void QuantizedMode_WithWrap_FlowIsQuantized()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, WrapperAddr, "200000000000000000000", isWrapped: true);
            mock.AddWrapperMapping(WrapperAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, WrapperAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = true,
            QuantizedMode = true
        }, UInt256.Parse("192000000000000000000"));

        AssertPositiveFlow(result, "Quantized + wrapped must find flow");
        var truncated = CirclesConverter.TruncateToInt64(UInt256.Parse(result.MaxFlow!));
        Assert.That(truncated % 96_000_000L, Is.EqualTo(0L));
    }

    // ----------------------------------------------------------------
    // SIMULATED BALANCES/TRUSTS THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void SimulatedBalance_CreatesFlowFromNothing()
    {
        // No real balances — only simulated. Should still find flow.
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            SimulatedBalances = new List<SimulatedBalance>
            {
                new()
                {
                    Holder = SourceAddr,
                    Token = TokenAddr,
                    Amount = "100000000000000000000"
                }
            }
        });

        AssertPositiveFlow(result, "Simulated balance should create flow from empty graph");
    }

    [Test]
    public void SimulatedTrust_CreatesPathFromNothing()
    {
        // No real trusts — only simulated. Should still find flow.
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new() { Truster = SinkAddr, Trustee = TokenAddr }
            }
        });

        AssertPositiveFlow(result, "Simulated trust should create path from empty trust graph");
    }

    // ----------------------------------------------------------------
    // SWAP MODE THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void SwapMode_WithToTokens_FindsFlow()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(BobAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddRegisteredAvatar(TokenBAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddBalanceWei(BobAddr, TokenBAddr, "100000000000000000000");
            mock.AddTrust(SourceAddr, TokenAddr);
            mock.AddTrust(SourceAddr, TokenBAddr);
            mock.AddTrust(BobAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SourceAddr, // Swap mode
            ToTokens = new List<string> { TokenBAddr }
        });

        // Swap mode with ToTokens creates virtual sink
        Assert.That(result.MaxFlow, Is.Not.Null);
    }

    // ----------------------------------------------------------------
    // GROUP MINTING THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void GroupMinting_FlowsThroughGroup()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddRegisteredAvatar(GroupAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "200000000000000000000");
            mock.AddGroup(GroupAddr);
            mock.AddGroupTrust(GroupAddr, TokenAddr);
            mock.AddTrust(SinkAddr, GroupAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr
        });

        AssertPositiveFlow(result, "Flow through group minting should work");
    }

    // ----------------------------------------------------------------
    // UNWRAP ENRICHMENT — tokenOwner preserves wrapper address
    // ----------------------------------------------------------------

    [Test]
    public void WithWrap_TokenOwner_IsWrapperAddress_NotUnderlyingAvatar()
    {
        // Verify that tokenOwner in transfer steps is the WRAPPER address,
        // not the underlying avatar. Callers need the wrapper address to
        // know which ERC20 to unwrap before submitting to Hub.sol.
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, WrapperAddr, "200000000000000000000", isWrapped: true);
            mock.AddWrapperMapping(WrapperAddr, TokenAddr);
            mock.AddTrust(SinkAddr, TokenAddr);
            mock.AddTrust(SinkAddr, WrapperAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            WithWrap = true
        });

        AssertPositiveFlow(result, "Wrapped flow must succeed");

        // At least one transfer should have tokenOwner == wrapper address
        var wrappedTransfers = result.Transfers!
            .Where(t => t.TokenOwner.Equals(WrapperAddr, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.That(wrappedTransfers.Count, Is.GreaterThan(0),
            $"Expected at least one transfer with tokenOwner={WrapperAddr} (wrapper address), " +
            $"but found tokenOwners: [{string.Join(", ", result.Transfers!.Select(t => t.TokenOwner))}]");
    }

    [Test]
    public void NativeToken_TokenOwner_IsAvatarAddress()
    {
        // For non-wrapped (native) tokens, tokenOwner should be the avatar address.
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr
        });

        AssertPositiveFlow(result);

        // All transfers should use the avatar address as tokenOwner (no wrappers)
        foreach (var t in result.Transfers!)
        {
            Assert.That(t.TokenOwner, Is.Not.EqualTo(WrapperAddr).IgnoreCase,
                "Native token transfers should not have wrapper as tokenOwner");
        }
    }

    // ----------------------------------------------------------------
    // DEBUG OUTPUT THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void DebugEnabled_ReturnsDebugStages()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            DebugShowIntermediateSteps = true
        });

        Assert.That(result.Debug, Is.Not.Null, "Debug stages should be present");
        Assert.That(result.Debug!.RawPaths, Is.Not.Null);
    }

    [Test]
    public void DebugDisabled_NoDebugStages()
    {
        var result = RunFullPipeline(mock =>
        {
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddTrust(SinkAddr, TokenAddr);
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr,
            DebugShowIntermediateSteps = false
        });

        Assert.That(result.Debug, Is.Null, "Debug stages should NOT be present when disabled");
    }

    // ----------------------------------------------------------------
    // MULTI-HOP THROUGH FULL PIPELINE
    // ----------------------------------------------------------------

    [Test]
    public void MultiHop_NativeTokens_FlowsThroughIntermediaries()
    {
        var result = RunFullPipeline(mock =>
        {
            // Source holds tokenA, Bob holds tokenB, sink trusts tokenB
            // Path: Source→Bob (via tokenA), Bob→Sink (via tokenB)
            mock.AddRegisteredAvatar(SourceAddr);
            mock.AddRegisteredAvatar(SinkAddr);
            mock.AddRegisteredAvatar(BobAddr);
            mock.AddRegisteredAvatar(TokenAddr);
            mock.AddRegisteredAvatar(TokenBAddr);
            mock.AddBalanceWei(SourceAddr, TokenAddr, "100000000000000000000");
            mock.AddBalanceWei(BobAddr, TokenBAddr, "100000000000000000000");
            mock.AddTrust(BobAddr, TokenAddr);   // Bob trusts source's token
            mock.AddTrust(SinkAddr, TokenBAddr);  // Sink trusts Bob's token
        }, new FlowRequest
        {
            Source = SourceAddr,
            Sink = SinkAddr
        });

        AssertPositiveFlow(result, "Multi-hop native flow should work");
        Assert.That(result.Transfers!.Count, Is.GreaterThanOrEqualTo(2),
            "Multi-hop should have at least 2 transfer steps");
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static void AssertPositiveFlow(MaxFlowResponse result, string? message = null)
    {
        Assert.That(result.MaxFlow, Is.Not.Null.And.Not.Empty, message ?? "MaxFlow should not be null");
        var flow = UInt256.Parse(result.MaxFlow!);
        Assert.That(flow > UInt256.Zero, Is.True, message ?? "MaxFlow should be positive");
    }

    private static void AssertZeroFlow(MaxFlowResponse result, string? message = null)
    {
        if (string.IsNullOrEmpty(result.MaxFlow)) return;
        var flow = UInt256.Parse(result.MaxFlow);
        Assert.That(flow == UInt256.Zero, Is.True, message ?? "MaxFlow should be zero");
    }
}
