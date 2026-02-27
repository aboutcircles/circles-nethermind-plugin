using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Tests;

/// <summary>
/// Unit tests for TransferSummaryAggregator - the core transfer aggregation logic.
/// Uses HexHelper from Circles.Index.Common to avoid Nethermind.Core runtime dependency.
/// </summary>
[TestFixture]
public class TransferSummaryAggregatorTests
{
    private RollbackCache<string, (string, TokenValueRepresentation)> _erc20WrapperCache = null!;

    // Test addresses
    private const string Alice = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Charlie = "0xcccccccccccccccccccccccccccccccccccccccc";
    private const string Group = "0xdddddddddddddddddddddddddddddddddddddddd";
    private const string TokenAlice = "0x1111111111111111111111111111111111111111";
    private const string TokenBob = "0x2222222222222222222222222222222222222222";
    private const string WrapperDemurraged = "0x3333333333333333333333333333333333333333";
    private const string WrapperInflationary = "0x4444444444444444444444444444444444444444";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    private const string HubAddress = "0x5555555555555555555555555555555555555555";

    [SetUp]
    public void SetUp()
    {
        _erc20WrapperCache = new RollbackCache<string, (string, TokenValueRepresentation)>("TestCache");
        _erc20WrapperCache.Seed(new Dictionary<string, (string, TokenValueRepresentation)>
        {
            [WrapperDemurraged] = (Alice, TokenValueRepresentation.Demurraged),
            [WrapperInflationary] = (Bob, TokenValueRepresentation.Inflationary)
        });
    }

    // ─────────────────────── Basic Cases (No Nethermind dependency) ───────────────────────

    [Test]
    public void AggregateAll_EmptyEvents_ReturnsEmptyResult()
    {
        var events = Array.Empty<IIndexedEventV2>();

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(0));
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
            Assert.That(result.StreamEvents, Is.Empty);
            Assert.That(result.NonStreamEvents, Is.Empty);
        });
    }

    [Test]
    public void AggregateAll_FlowEdgesScopeSingleStarted_AddedToStreamEvents()
    {
        // FlowEdgesScopeSingleStarted is a stream boundary event and belongs in streamEvents
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.That(result.StreamEvents, Has.Count.EqualTo(1));
        Assert.That(result.StreamEvents[0], Is.InstanceOf<FlowEdgesScopeSingleStarted>());
        Assert.That(result.NonStreamEvents, Is.Empty);
    }

    [Test]
    public void AggregateAll_GroupMintEvents_NotAggregated()
    {
        // GroupMint events are added to nonStreamEvents but don't trigger transfer aggregation
        var events = new IIndexedEventV2[]
        {
            CreateGroupMint(Alice, Bob, Group, 1_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.That(result.NonStreamEvents, Has.Count.EqualTo(1));
        Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
    }

    [Test]
    public void AggregateAll_TrustEvents_NotAggregated()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTrust(Alice, Bob)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.That(result.NonStreamEvents, Has.Count.EqualTo(1));
        Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
    }

    [Test]
    public void AggregateAll_EventsAfterScopeLastEnded_StayInStream()
    {
        // Hub.sol emits: ScopeStart → transfers → ScopeLastEnded → GroupMint/TransferBatch → StreamCompleted
        // Events between ScopeLastEnded and StreamCompleted are still part of the stream.
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateFlowEdgesScopeLastEnded(),
            CreateTransferBatch(ZeroAddress, Alice, 0, 200UL),
            CreateGroupMint(Alice, Bob, Group, 300UL),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            // All 6 events must be in stream
            Assert.That(result.StreamEvents, Has.Count.EqualTo(6));
            Assert.That(result.StreamEvents.OfType<FlowEdgesScopeLastEnded>().Count(), Is.EqualTo(1));
            Assert.That(result.NonStreamEvents, Is.Empty);
            // TransferBatch/GroupMint between ScopeLastEnded and StreamCompleted must NOT create non-stream totals
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public void AggregateAll_MultipleStreamsInOneTransaction_EachStreamIsolated()
    {
        var events = new IIndexedEventV2[]
        {
            // Stream 1
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateFlowEdgesScopeLastEnded(),
            CreateStreamCompleted(Alice, Bob, 100UL),
            // Inter-stream non-stream event
            CreateTransferSingle(Charlie, Alice, 50UL),
            // Stream 2
            CreateFlowEdgesScopeSingleStarted(2, 1),
            CreateTransferSingle(Bob, Charlie, 200UL),
            CreateFlowEdgesScopeLastEnded(),
            CreateGroupMint(Bob, Charlie, Group, 200UL),
            CreateStreamCompleted(Bob, Charlie, 200UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(2)); // Alice→Bob, Bob→Charlie
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1)); // Charlie→Alice
            Assert.That(result.NonStreamEvents, Has.Count.EqualTo(1));
        });
    }

    // ─────────────────────── Stream-Only Tests (No Nethermind dependency) ───────────────────────
    // StreamCompleted aggregation uses ToHexString but only on stream transfers

    [Test]
    public void AggregateAll_MultipleStreamCompleted_AggregatesCorrectly()
    {
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateStreamCompleted(Alice, Bob, 1_000_000_000_000_000_000UL),
            CreateStreamCompleted(Alice, Bob, 500_000_000_000_000_000UL),
            CreateStreamCompleted(Alice, Charlie, 250_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(2));
            var aliceToBob = result.StreamTransfers.Totals.First(t => t.Key.To == Bob);
            var aliceToCharlie = result.StreamTransfers.Totals.First(t => t.Key.To == Charlie);
            Assert.That(aliceToBob.Value.ToString(), Is.EqualTo("1500000000000000000"));
            Assert.That(aliceToBob.Transfers, Is.EqualTo(2));
            Assert.That(aliceToCharlie.Value.ToString(), Is.EqualTo("250000000000000000"));
        });
    }

    // ─────────────────────── TransferSingle Tests (Require Nethermind) ───────────────────────

    [Test]
    public void AggregateAll_SingleTransfer_CreatesOneSummary()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1));
            var total = result.NonStreamTransfers.Totals.First();
            Assert.That(total.Key.From, Is.EqualTo(Alice));
            Assert.That(total.Key.To, Is.EqualTo(Bob));
            Assert.That(total.Value.ToString(), Is.EqualTo("1000000000000000000"));
            Assert.That(total.Transfers, Is.EqualTo(1));
        });
    }

    [Test]
    public void AggregateAll_MultipleTransfersSameDirection_SumsValues()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL),
            CreateTransferSingle(Alice, Bob, 2_000_000_000_000_000_000UL),
            CreateTransferSingle(Alice, Bob, 500_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1));
            var total = result.NonStreamTransfers.Totals.First();
            Assert.That(total.Value.ToString(), Is.EqualTo("3500000000000000000"));
            Assert.That(total.Transfers, Is.EqualTo(3));
        });
    }

    [Test]
    public void AggregateAll_BidirectionalTransfers_CreatesSeparateSummaries()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL),
            CreateTransferSingle(Bob, Alice, 500_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(2));
            var aliceToBob = result.NonStreamTransfers.Totals.First(t => t.Key.From == Alice);
            var bobToAlice = result.NonStreamTransfers.Totals.First(t => t.Key.From == Bob);
            Assert.That(aliceToBob.Value.ToString(), Is.EqualTo("1000000000000000000"));
            Assert.That(bobToAlice.Value.ToString(), Is.EqualTo("500000000000000000"));
        });
    }

    [Test]
    public void AggregateAll_MultipleUniqueTokens_TracksAllTokens()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL, TokenAlice),
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL, TokenBob)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Tokens.Count, Is.EqualTo(2));
    }

    // ─────────────────────── TransferBatch Tests (Require Nethermind) ───────────────────────

    [Test]
    public void AggregateAll_TransferBatch_AggregatesLikeSingle()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferBatch(Alice, Bob, 0, 1_000_000_000_000_000_000UL),
            CreateTransferBatch(Alice, Bob, 1, 2_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Value.ToString(), Is.EqualTo("3000000000000000000"));
        Assert.That(total.Transfers, Is.EqualTo(2));
    }

    // ─────────────────────── ERC20 Wrapper Transfer Tests (Require Nethermind) ───────────────────────

    [Test]
    public void AggregateAll_Erc20DemurragedTransfer_UsesValueAsIs()
    {
        var events = new IIndexedEventV2[]
        {
            CreateErc20WrapperTransfer(Alice, Bob, 1_000_000_000_000_000_000UL, WrapperDemurraged)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Value.ToString(), Is.EqualTo("1000000000000000000"));
    }

    [Test]
    public void AggregateAll_Erc20InflationaryTransfer_AppliesConversion()
    {
        var events = new IIndexedEventV2[]
        {
            CreateErc20WrapperTransfer(Alice, Bob, 1_000_000_000_000_000_000UL, WrapperInflationary)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Value.ToString(), Is.Not.EqualTo("1000000000000000000"));
    }

    [Test]
    public void AggregateAll_Erc20UnknownWrapper_UsesValueAsIs()
    {
        const string unknownWrapper = "0x9999999999999999999999999999999999999999";
        var events = new IIndexedEventV2[]
        {
            CreateErc20WrapperTransfer(Alice, Bob, 1_000_000_000_000_000_000UL, unknownWrapper)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Value.ToString(), Is.EqualTo("1000000000000000000"));
    }

    // ─────────────────────── Stream Transfer Tests (Require Nethermind) ───────────────────────

    [Test]
    public void AggregateAll_StreamFlow_SeparatesFromNonStream()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL),
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Charlie, 500_000_000_000_000_000UL),
            CreateStreamCompleted(Alice, Charlie, 500_000_000_000_000_000UL),
            CreateTransferSingle(Bob, Charlie, 250_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(1));
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(2));
        });
    }

    // ─────────────────────── Mint/Burn Tests (Require Nethermind) ───────────────────────

    [Test]
    public void AggregateAll_MintFromZeroAddress_CreatesTransferFromZero()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(ZeroAddress, Alice, 1_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Key.From, Is.EqualTo(ZeroAddress));
        Assert.That(total.Key.To, Is.EqualTo(Alice));
    }

    [Test]
    public void AggregateAll_BurnToZeroAddress_CreatesTransferToZero()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, ZeroAddress, 1_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Key.From, Is.EqualTo(Alice));
        Assert.That(total.Key.To, Is.EqualTo(ZeroAddress));
    }

    // ─────────────────────── Complex Transaction Tests (Require Nethermind) ───────────────────────

    [Test]
    public void AggregateAll_MixedEventTypes_AggregatesAllCorrectly()
    {
        var events = new IIndexedEventV2[]
        {
            CreateTransferSingle(Alice, Bob, 1_000_000_000_000_000_000UL),
            CreateTransferBatch(Alice, Bob, 0, 500_000_000_000_000_000UL),
            CreateErc20WrapperTransfer(Alice, Bob, 250_000_000_000_000_000UL, WrapperDemurraged),
            CreateTransferSingle(Bob, Alice, 100_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var aliceToBob = result.NonStreamTransfers.Totals.First(t => t.Key.From == Alice);
        var bobToAlice = result.NonStreamTransfers.Totals.First(t => t.Key.From == Bob);

        Assert.Multiple(() =>
        {
            Assert.That(aliceToBob.Value.ToString(), Is.EqualTo("1750000000000000000"));
            Assert.That(aliceToBob.Transfers, Is.EqualTo(3));
            Assert.That(bobToAlice.Value.ToString(), Is.EqualTo("100000000000000000"));
        });
    }

    [Test]
    public void AggregateAll_VeryLargeValues_HandlesWithoutOverflow()
    {
        var largeValue = UInt256.MaxValue / 2;
        var events = new IIndexedEventV2[]
        {
            new TransferSingle(1, 0, 0, 0, "0x", HubAddress, Alice, Alice, Bob, new UInt256(1), largeValue),
            new TransferSingle(1, 0, 0, 1, "0x", HubAddress, Alice, Alice, Bob, new UInt256(2), largeValue)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        Assert.That(total.Transfers, Is.EqualTo(2));
    }

    [Test]
    public void AggregateAll_StreamEvents_ContainsAllEventsInStream()
    {
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateTransferSingle(Bob, Charlie, 100UL),
            CreateStreamCompleted(Alice, Charlie, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        // 4 events: FlowEdgesScopeSingleStarted + 2 TransferSingle + StreamCompleted
        Assert.That(result.StreamEvents, Has.Count.EqualTo(4));
        Assert.That(result.StreamEvents.OfType<FlowEdgesScopeSingleStarted>().Count(), Is.EqualTo(1));
        Assert.That(result.StreamEvents.OfType<TransferSingle>().Count(), Is.EqualTo(2));
        Assert.That(result.StreamEvents.OfType<StreamCompleted>().Count(), Is.EqualTo(1));
    }

    // ─────────────────────── Erc20WrapperTransfer in Stream Tests ───────────────────────
    // These tests verify the fix for wrapper transfers being swallowed inside streams.
    // StreamCompleted only reflects Hub ERC1155 flows; wrapper transfers must be aggregated separately.

    [Test]
    public void AggregateAll_Erc20WrapperTransferInStream_AggregatedToNonStreamSums()
    {
        // Core bug fix: Erc20WrapperTransfer inside a stream must produce a non-stream summary row
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateFlowEdgesScopeLastEnded(),
            CreateErc20WrapperTransfer(Bob, Charlie, 200UL, WrapperDemurraged),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            // StreamCompleted creates the stream summary (Alice→Bob)
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(1));
            var streamTotal = result.StreamTransfers.Totals.First();
            Assert.That(streamTotal.Key.From, Is.EqualTo(Alice));
            Assert.That(streamTotal.Key.To, Is.EqualTo(Bob));

            // Erc20WrapperTransfer creates the non-stream summary (Bob→Charlie)
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1));
            var nonStreamTotal = result.NonStreamTransfers.Totals.First();
            Assert.That(nonStreamTotal.Key.From, Is.EqualTo(Bob));
            Assert.That(nonStreamTotal.Key.To, Is.EqualTo(Charlie));
            Assert.That(nonStreamTotal.Value.ToString(), Is.EqualTo("200"));
        });
    }

    [Test]
    public void AggregateAll_Erc20WrapperTransferInStream_AppearsInBothEventLists()
    {
        // Erc20WrapperTransfer in stream must appear in BOTH streamEvents (for ordering)
        // AND nonStreamEvents (for aggregation visibility)
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateErc20WrapperTransfer(Alice, Bob, 100UL, WrapperDemurraged),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamEvents.OfType<Erc20WrapperTransfer>().Count(), Is.EqualTo(1));
            Assert.That(result.NonStreamEvents.OfType<Erc20WrapperTransfer>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public void AggregateAll_Erc20InflationaryWrapperInStream_AppliesConversion()
    {
        // Inflationary wrapper conversion must work even when inside a stream
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateErc20WrapperTransfer(Alice, Bob, 1_000_000_000_000_000_000UL, WrapperInflationary),
            CreateStreamCompleted(Alice, Bob, 1_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        var total = result.NonStreamTransfers.Totals.First();
        // Inflationary → AttoStaticCircles conversion should change the value
        Assert.That(total.Value.ToString(), Is.Not.EqualTo("1000000000000000000"));
    }

    [Test]
    public void AggregateAll_MultipleWrapperTransfersInStream_AggregatesBySenderReceiver()
    {
        // Multiple Erc20WrapperTransfer events with same from/to should sum
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateFlowEdgesScopeLastEnded(),
            CreateErc20WrapperTransfer(Bob, Charlie, 200UL, WrapperDemurraged),
            CreateErc20WrapperTransfer(Bob, Charlie, 300UL, WrapperDemurraged),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1));
            var total = result.NonStreamTransfers.Totals.First();
            Assert.That(total.Key.From, Is.EqualTo(Bob));
            Assert.That(total.Key.To, Is.EqualTo(Charlie));
            Assert.That(total.Value.ToString(), Is.EqualTo("500"));
            Assert.That(total.Transfers, Is.EqualTo(2));
        });
    }

    [Test]
    public void AggregateAll_TransferSingleInStream_NotAggregatedToNonStream()
    {
        // Regression: TransferSingle in stream must NOT leak to non-stream (captured by StreamCompleted)
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(1));
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
            Assert.That(result.NonStreamEvents, Is.Empty);
        });
    }

    [Test]
    public void AggregateAll_TransferBatchInStream_NotAggregatedToNonStream()
    {
        // Regression: TransferBatch in stream must NOT leak to non-stream
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferBatch(ZeroAddress, Alice, 0, 200UL),
            CreateStreamCompleted(Alice, Bob, 200UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(1));
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
            Assert.That(result.NonStreamEvents, Is.Empty);
        });
    }

    [Test]
    public void AggregateAll_GroupMintInStream_NotAggregatedToNonStream()
    {
        // Regression: GroupMint in stream must NOT leak to non-stream
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateFlowEdgesScopeLastEnded(),
            CreateGroupMint(Alice, Bob, Group, 100UL),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            // GroupMint is in stream events
            Assert.That(result.StreamEvents.OfType<GroupMint>().Count(), Is.EqualTo(1));
            // But NOT in non-stream events
            Assert.That(result.NonStreamEvents.OfType<GroupMint>().Count(), Is.EqualTo(0));
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public void AggregateAll_RealWorldGroupMintFlow_CapturesAllLegs()
    {
        // Mirrors the real transaction (0xe0cfdf0f...) that triggered this bug:
        // Stream: 0xc7d3df → org (personal CRC via Hub)
        // Wrapper: org → 0xf48554 (wrapped ERC20 forwarding)
        // GroupMint: org mints group tokens (not a transfer)
        const string Org = "0xd4dd7ba300000000000000000000000000000001";
        const string Sender = "0xc7d3df0000000000000000000000000000000001";
        const string FinalRecipient = "0xf4855400000000000000000000000000000001";
        const string Router = "0x8586b40000000000000000000000000000000001";

        var events = new IIndexedEventV2[]
        {
            // Stream starts
            CreateFlowEdgesScopeSingleStarted(1, 0),
            // Hub ERC1155 transfers (captured by StreamCompleted)
            CreateTransferSingle(ZeroAddress, Sender, 50UL), // demurrage burn
            CreateTransferSingle(Sender, Org, 1_000_000_000_000_000_000UL),
            CreateFlowEdgesScopeLastEnded(),
            // Post-scope: more Hub transfers + wrapper + group mint
            CreateTransferSingle(Org, Router, 500_000_000_000_000_000UL),
            CreateTransferSingle(Router, Group, 500_000_000_000_000_000UL), // collateral
            CreateTransferSingle(ZeroAddress, Org, 500_000_000_000_000_000UL), // group mint
            CreateTransferSingle(Org, WrapperDemurraged, 500_000_000_000_000_000UL), // wrap
            // ERC20 wrapper transfers (NOT captured by StreamCompleted!)
            CreateErc20WrapperTransfer(ZeroAddress, Router, 500_000_000_000_000_000UL, WrapperDemurraged),
            CreateErc20WrapperTransfer(Router, Org, 500_000_000_000_000_000UL, WrapperDemurraged),
            CreateErc20WrapperTransfer(Org, FinalRecipient, 500_000_000_000_000_000UL, WrapperDemurraged),
            // Group mint event (not a transfer type)
            CreateGroupMint(Sender, Org, Group, 500_000_000_000_000_000UL),
            // StreamCompleted: only the Hub-level summary
            CreateStreamCompleted(Sender, Org, 1_000_000_000_000_000_000UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            // Stream summary: Sender → Org (from StreamCompleted)
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(1));
            var streamTotal = result.StreamTransfers.Totals.First();
            Assert.That(streamTotal.Key.From, Is.EqualTo(Sender));
            Assert.That(streamTotal.Key.To, Is.EqualTo(Org));
            Assert.That(streamTotal.Value.ToString(), Is.EqualTo("1000000000000000000"));

            // Non-stream summary: 3 Erc20WrapperTransfer rows
            var nonStreamTotals = result.NonStreamTransfers.Totals.ToList();
            Assert.That(nonStreamTotals.Count, Is.EqualTo(3));

            // Verify the critical outgoing leg: Org → FinalRecipient
            var orgToRecipient = nonStreamTotals.FirstOrDefault(t =>
                t.Key.From == Org && t.Key.To == FinalRecipient);
            Assert.That(orgToRecipient, Is.Not.Null, "Org → FinalRecipient wrapper transfer must be visible");
            Assert.That(orgToRecipient!.Value.ToString(), Is.EqualTo("500000000000000000"));

            // GroupMint must NOT create a non-stream total
            Assert.That(result.NonStreamEvents.OfType<GroupMint>().Count(), Is.EqualTo(0));

            // All events in stream
            Assert.That(result.StreamEvents, Has.Count.EqualTo(13));
            // Only Erc20WrapperTransfer in non-stream events
            Assert.That(result.NonStreamEvents.Count, Is.EqualTo(3));
            Assert.That(result.NonStreamEvents.All(e => e is Erc20WrapperTransfer), Is.True);
        });
    }

    [Test]
    public void AggregateAll_WrapperTransferBetweenStreams_StaysNonStream()
    {
        // Erc20WrapperTransfer between two streams should still be non-stream (not affected by fix)
        var events = new IIndexedEventV2[]
        {
            // Stream 1
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateStreamCompleted(Alice, Bob, 100UL),
            // Non-stream wrapper transfer
            CreateErc20WrapperTransfer(Bob, Charlie, 200UL, WrapperDemurraged),
            // Stream 2
            CreateFlowEdgesScopeSingleStarted(2, 1),
            CreateTransferSingle(Charlie, Alice, 50UL),
            CreateStreamCompleted(Charlie, Alice, 50UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(2));
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1));
            var wrapperTotal = result.NonStreamTransfers.Totals.First();
            Assert.That(wrapperTotal.Key.From, Is.EqualTo(Bob));
            Assert.That(wrapperTotal.Key.To, Is.EqualTo(Charlie));
        });
    }

    [Test]
    public void AggregateAll_WrapperTransferBeforeScopeStart_StaysNonStream()
    {
        // Wrapper transfer before any stream starts — baseline non-stream behavior
        var events = new IIndexedEventV2[]
        {
            CreateErc20WrapperTransfer(Alice, Bob, 100UL, WrapperDemurraged),
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Charlie, 50UL),
            CreateStreamCompleted(Alice, Charlie, 50UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            Assert.That(result.NonStreamTransfers.Totals.Count(), Is.EqualTo(1));
            Assert.That(result.NonStreamEvents.OfType<Erc20WrapperTransfer>().Count(), Is.EqualTo(1));
            Assert.That(result.StreamTransfers.Totals.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public void AggregateAll_WrapperAndHubTransferSameDirectionInStream_NotDoubleCounted()
    {
        // When both a TransferSingle and Erc20WrapperTransfer go Alice→Bob in the same stream,
        // only the wrapper should appear in nonStreamSums (TransferSingle covered by StreamCompleted)
        var events = new IIndexedEventV2[]
        {
            CreateFlowEdgesScopeSingleStarted(1, 0),
            CreateTransferSingle(Alice, Bob, 100UL),
            CreateErc20WrapperTransfer(Alice, Bob, 200UL, WrapperDemurraged),
            CreateStreamCompleted(Alice, Bob, 100UL)
        };

        var result = TransferSummaryAggregator.AggregateAll(events, _erc20WrapperCache);

        Assert.Multiple(() =>
        {
            // Stream: 100 (from StreamCompleted)
            var streamTotal = result.StreamTransfers.Totals.First();
            Assert.That(streamTotal.Value.ToString(), Is.EqualTo("100"));

            // Non-stream: 200 (from Erc20WrapperTransfer only — NOT 300)
            var nonStreamTotal = result.NonStreamTransfers.Totals.First();
            Assert.That(nonStreamTotal.Value.ToString(), Is.EqualTo("200"));
        });
    }

    // ─────────────────────── E2E Tests (Require DB) ───────────────────────

    [Test]
    [Explicit("Requires staging database — set CIRCLES_CONNECTION_STRING or source docker/.env")]
    public void E2E_WrapperTransferSummary_Transaction0xe0cfdf0f()
    {
        // Verify the specific transaction that triggered the bug.
        // After reindex, TransferSummary for tx 0xe0cfdf0f... should contain both:
        //   1. Stream leg: 0xc7d3df → org
        //   2. Wrapper leg: org → 0xf48554
        var connectionString = Environment.GetEnvironmentVariable("CIRCLES_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
        {
            Assert.Ignore("CIRCLES_CONNECTION_STRING not set");
            return;
        }

        Console.WriteLine("To verify after reindex, run:");
        Console.WriteLine(@"  SELECT * FROM ""V_CrcV2_TransferSummary"" WHERE ""transactionHash"" = '0xe0cfdf0f0970a104aaa79b157b240ff95e02807a40a84b2ef6ff94d7dbc761b1';");
        Console.WriteLine("Expected: at least 2 rows (stream + wrapper transfer)");

        Assert.Pass("Manual verification — see console output for query");
    }

    // ─────────────────────── Helper Methods ───────────────────────

    private static TransferSingle CreateTransferSingle(string from, string to, ulong value, string? tokenId = null)
    {
        var id = tokenId != null
            ? new UInt256(Convert.FromHexString(tokenId.Substring(2).PadLeft(64, '0')), true)
            : new UInt256(1);

        return new TransferSingle(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress,
            Operator: from,
            From: from,
            To: to,
            Id: id,
            Value: new UInt256(value)
        );
    }

    private static TransferBatch CreateTransferBatch(string from, string to, int batchIndex, ulong value)
    {
        return new TransferBatch(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress,
            BatchIndex: batchIndex,
            Operator: from,
            From: from,
            To: to,
            Id: new UInt256((ulong)batchIndex + 1),
            Value: new UInt256(value)
        );
    }

    private static Erc20WrapperTransfer CreateErc20WrapperTransfer(string from, string to, ulong value, string wrapper)
    {
        return new Erc20WrapperTransfer(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            TransactionHash: "0x1234",
            Emitter: wrapper,
            TokenAddress: wrapper,
            From: from,
            To: to,
            Value: new UInt256(value)
        );
    }

    private static FlowEdgesScopeSingleStarted CreateFlowEdgesScopeSingleStarted(ulong flowEdgeId, ushort streamId)
    {
        return new FlowEdgesScopeSingleStarted(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress,
            FlowEdgeId: new UInt256(flowEdgeId),
            StreamId: streamId
        );
    }

    private static StreamCompleted CreateStreamCompleted(string from, string to, ulong amount)
    {
        return new StreamCompleted(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            BatchIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress,
            Operator: from,
            From: from,
            To: to,
            Id: new UInt256(1),
            Amount: new UInt256(amount)
        );
    }

    private static GroupMint CreateGroupMint(string sender, string receiver, string group, ulong amount)
    {
        return new GroupMint(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            BatchIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress,
            Sender: sender,
            Receiver: receiver,
            Group: group,
            Collateral: new UInt256(amount),
            Amount: new UInt256(amount)
        );
    }

    private static FlowEdgesScopeLastEnded CreateFlowEdgesScopeLastEnded()
    {
        return new FlowEdgesScopeLastEnded(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress
        );
    }

    private static Trust CreateTrust(string truster, string trustee)
    {
        return new Trust(
            BlockNumber: 1,
            Timestamp: 1700000000,
            TransactionIndex: 0,
            LogIndex: 0,
            TransactionHash: "0x1234",
            Emitter: HubAddress,
            Truster: truster,
            Trustee: trustee,
            ExpiryTime: UInt256.MaxValue
        );
    }
}
