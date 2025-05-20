using System.Collections.Immutable;
using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;

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

public record AggregationResult(
    NetTransfers StreamTransfers,
    List<IIndexedEventV2> StreamEvents,
    NetTransfers NonStreamTransfers,
    List<IIndexedEventV2> NonStreamEvents
);

/// <summary>
/// This class aggregates all TransferSingle, TransferBatch, and Erc20WrapperTransfer
/// events from one transaction. Then it outputs a series of "virtual" TransferSummary
/// records that reflect net flows between real addresses (or MINT/BURN if the system
/// net isn't zero).
/// </summary>
public static class TransferSummaryAggregator
{
    public static AggregationResult AggregateAll(IEnumerable<IIndexedEventV2> events,
        RollbackCache<string, (string, TokenValueRepresentation)> erc20WrapperAddresses)
    {
        var streamSums = new Dictionary<TransferKey, TransferTotal>();
        var nonStreamSums = new Dictionary<TransferKey, TransferTotal>();
        var streamEvents = new List<IIndexedEventV2>();
        var nonStreamEvents = new List<IIndexedEventV2>();
        bool inStream = false;

        foreach (var e in events)
        {
            if (e is FlowEdgesScopeSingleStarted)
            {
                inStream = true;
                nonStreamEvents.Add(e);
                continue;
            }

            if (e is StreamCompleted sc)
            {
                AddStreamCompleted(streamSums, sc);
                streamEvents.Add(e);
                inStream = false;
                continue;
            }

            if (inStream)
            {
                streamEvents.Add(e);
            }
            else
            {
                nonStreamEvents.Add(e);
                AddNonStreamTransfers(nonStreamSums, e, erc20WrapperAddresses);
            }
        }

        return new AggregationResult(
            new NetTransfers(streamSums.Values),
            streamEvents,
            new NetTransfers(nonStreamSums.Values),
            nonStreamEvents
        );
    }

    static void AddStreamCompleted(Dictionary<TransferKey, TransferTotal> sums, StreamCompleted e)
    {
        var key = new TransferKey(e.From, e.To);
        var amount = (BigInteger)e.Amount;
        var token = e.Id.ToHexString(true);
        if (!sums.TryGetValue(key, out var current))
        {
            sums[key] = new TransferTotal(key, amount, ImmutableHashSet.Create(token), 1);
        }
        else
        {
            sums[key] = current with
            {
                Value = current.Value + amount,
                Tokens = current.Tokens.Add(token),
                Transfers = current.Transfers + 1
            };
        }
    }

    static void AddNonStreamTransfers(Dictionary<TransferKey, TransferTotal> sums, IIndexedEventV2 e,
        RollbackCache<string, (string, TokenValueRepresentation)> erc20WrapperAddresses)
    {
        if (e is TransferSingle ts)
        {
            AddSum(sums, ts.From, ts.To, (BigInteger)ts.Value, ts.Id.ToHexString(true));
        }
        else if (e is TransferBatch tb)
        {
            AddSum(sums, tb.From, tb.To, (BigInteger)tb.Value, tb.Id.ToHexString(true));
        }
        else if (e is Erc20WrapperTransfer ewt)
        {
            var val = (BigInteger)ewt.Value;
            if (erc20WrapperAddresses.TryGetValue(ewt.TokenAddress, out var wrapperType) &&
                wrapperType.Item2 == TokenValueRepresentation.Inflationary)
            {
                val = CirclesConverter.AttoStaticCirclesToAttoCircles(val);
            }

            AddSum(sums, ewt.From, ewt.To, val, ewt.TokenAddress);
        }
    }

    static void AddSum(Dictionary<TransferKey, TransferTotal> sums, string from, string to, BigInteger value,
        string token)
    {
        var key = new TransferKey(from, to);
        if (!sums.TryGetValue(key, out var current))
        {
            sums[key] = new TransferTotal(key, value, ImmutableHashSet.Create(token), 1);
        }
        else
        {
            sums[key] = current with
            {
                Value = current.Value + value,
                Tokens = current.Tokens.Add(token),
                Transfers = current.Transfers + 1
            };
        }
    }
}