using System.Collections.Immutable;
using System.Numerics;
using Circles.Index.Common;
using Circles.Index.Utils;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2;

/// <summary>
/// Sums up all individual transfers between two addresses in one direction.
/// </summary>
/// <param name="Key">The "from" and "to" addresses</param>
/// <param name="Value">The total value of all transfers between the pair</param>
/// <param name="Tokens">A set of all tokens involved in the sum</param>
/// <param name="Transfers">The total number of individual transfers that make up the sum</param>
public record TransferTotal(TransferKey Key, BigInteger Value, ImmutableHashSet<string> Tokens, int Transfers);

public record NetTransfers(IEnumerable<TransferTotal> Totals);

public record TransferKey(string From, string To);

/// <summary>
/// This class aggregates all TransferSingle, TransferBatch, and Erc20WrapperTransfer
/// events from one transaction. Then it outputs a series of "virtual" TransferSummary
/// records that reflect net flows between real addresses (or MINT/BURN if the system
/// net isn't zero).
/// </summary>
public static class TransferSummaryAggregator
{
    public static string ConvertUInt256ToHex(UInt256 value)
    {
        return value.ToHexString(true);
    }

    public static NetTransfers GetStreamSummary(IEnumerable<IIndexedEventV2> events)
    {
        var streamCompletedEvents = events
            .Where(o => o is StreamCompleted)
            .Cast<StreamCompleted>()
            .ToArray();

        Dictionary<TransferKey, TransferTotal> sums = new Dictionary<TransferKey, TransferTotal>();

        foreach (var streamCompletedEvent in streamCompletedEvents)
        {
            var streamKey = new TransferKey(streamCompletedEvent.From, streamCompletedEvent.To);
            if (!sums.TryGetValue(streamKey, out var streamSum))
            {
                sums[streamKey] = new TransferTotal(
                    streamKey,
                    (BigInteger)streamCompletedEvent.Amount,
                    ImmutableHashSet.Create(ConvertUInt256ToHex(streamCompletedEvent.Id)),
                    1);
            }
            else
            {
                sums[streamKey] = new TransferTotal(
                    streamKey,
                    streamSum.Value + (BigInteger)streamCompletedEvent.Amount,
                    streamSum.Tokens.Add(ConvertUInt256ToHex(streamCompletedEvent.Id)),
                    streamSum.Transfers + 1);
            }
        }

        return new NetTransfers(sums.Values);
    }

    public static IEnumerable<IIndexedEventV2> StreamEvents(IEnumerable<IIndexedEventV2> events)
    {
        // An event is a stream event if it's between a FlowEdgeScopeSingleStarted and StreamCompleted event.
        bool inStream = false;
        foreach (var e in events)
        {
            switch (e)
            {
                case FlowEdgesScopeSingleStarted _:
                    inStream = true;
                    break;
                case StreamCompleted _:
                    inStream = false;
                    break;
            }

            if (inStream)
            {
                yield return e;
            }
        }
    }

    public static IEnumerable<IIndexedEventV2> NonStreamEvents(IEnumerable<IIndexedEventV2> events)
    {
        // An event is a non-stream event if it's not between a FlowEdgeScopeSingleStarted and StreamCompleted event.
        bool inStream = false;
        foreach (var e in events)
        {
            switch (e)
            {
                case FlowEdgesScopeSingleStarted _:
                    inStream = true;
                    break;
                case StreamCompleted _:
                    inStream = false;
                    break;
            }

            if (!inStream)
            {
                yield return e;
            }
        }
    }

    public static NetTransfers CalculateNetTransfers(IEnumerable<IIndexEvent> events,
        IDictionary<Address, long> erc20WrapperAddresses)
    {
        Dictionary<TransferKey, TransferTotal> sums = new Dictionary<TransferKey, TransferTotal>();

        foreach (var e in events)
        {
            switch (e)
            {
                case TransferSingle ts:
                {
                    var tsKey = new TransferKey(ts.From, ts.To);
                    if (!sums.TryGetValue(tsKey, out var tsSum))
                    {
                        sums[tsKey] = new TransferTotal(
                            tsKey,
                            (BigInteger)ts.Value,
                            ImmutableHashSet.Create(ConvertUInt256ToHex(ts.Id)),
                            1);
                    }
                    else
                    {
                        sums[tsKey] = new TransferTotal(
                            tsKey,
                            tsSum.Value + (BigInteger)ts.Value,
                            tsSum.Tokens.Add(ConvertUInt256ToHex(ts.Id)),
                            tsSum.Transfers + 1);
                    }

                    break;
                }
                case TransferBatch tb:
                {
                    var tbKey = new TransferKey(tb.From, tb.To);
                    if (!sums.TryGetValue(tbKey, out var tbSum))
                    {
                        sums[tbKey] = new TransferTotal(
                            tbKey,
                            (BigInteger)tb.Value,
                            ImmutableHashSet.Create(ConvertUInt256ToHex(tb.Id)),
                            1);
                    }
                    else
                    {
                        sums[tbKey] = new TransferTotal(
                            tbKey,
                            tbSum.Value + (BigInteger)tb.Value,
                            tbSum.Tokens.Add(ConvertUInt256ToHex(tb.Id)),
                            tbSum.Transfers + 1);
                    }

                    break;
                }
                case Erc20WrapperTransfer ewt:
                {
                    BigInteger value = (BigInteger)ewt.Value;
                    if (erc20WrapperAddresses.TryGetValue(new Address(ewt.TokenAddress), out var erc20WrapperType) &&
                        erc20WrapperType == 1)
                    {
                        // static wrapper. Convert to demurraged.
                        value = (BigInteger)ConversionUtils.CirclesToAttoCircles(
                            ConversionUtils.StaticCirclesToCircles(ConversionUtils.AttoCirclesToCircles(ewt.Value)));
                    }

                    var ewtKey = new TransferKey(ewt.From, ewt.To);
                    if (!sums.TryGetValue(ewtKey, out var ewtSum))
                    {
                        sums[ewtKey] = new TransferTotal(
                            ewtKey,
                            value,
                            ImmutableHashSet.Create(ewt.TokenAddress),
                            1);
                    }
                    else
                    {
                        sums[ewtKey] = new TransferTotal(
                            ewtKey,
                            ewtSum.Value + value,
                            ewtSum.Tokens.Add(ewt.TokenAddress),
                            ewtSum.Transfers + 1);
                    }

                    break;
                }
            }
        }

        return new NetTransfers(sums.Values);
    }
}