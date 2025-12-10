using System.Collections.Immutable;
using System.Numerics;
using Circles.Index.Common;

namespace Circles.Index.CirclesV1;

/// <summary>
/// Key for tracking transfers between address pairs.
/// </summary>
public record TransferKey(string From, string To);

/// <summary>
/// Represents a net transfer total between two addresses.
/// </summary>
public record TransferTotal(TransferKey Key, BigInteger Value, ImmutableHashSet<string> Tokens, int Transfers);

/// <summary>
/// Result of aggregating V1 transfer events.
/// </summary>
/// <param name="HubTransferSummaries">Net transfers derived from HubTransfer events with their associated Transfer edges</param>
/// <param name="StandaloneTransfers">Transfer events not associated with any HubTransfer</param>
/// <param name="AllTransferEvents">All original Transfer events for reference</param>
public record AggregationResultV1(
    List<(HubTransfer Hub, HashSet<Transfer> Edges)> HubTransferSummaries,
    List<Transfer> StandaloneTransfers,
    List<Transfer> AllTransferEvents
);

/// <summary>
/// Aggregates V1 transfer events to produce net transfer summaries.
///
/// In V1, a multi-hop transfer emits:
/// - Multiple Transfer events (one per hop through different token contracts)
/// - One HubTransfer event representing the actual user-to-user transfer intent
///
/// This aggregator:
/// 1. Identifies HubTransfer events as the "real" transfers
/// 2. Uses DFS to trace which Transfer events belong to each HubTransfer
/// 3. Returns both the hub summaries and any standalone transfers
/// </summary>
public static class TransferSummaryAggregatorV1
{
    public static AggregationResultV1 Aggregate(IReadOnlyList<IIndexEvent> events)
    {
        var hubTransfers = new List<HubTransfer>();
        var allTransfers = new List<Transfer>();

        foreach (var e in events)
        {
            switch (e)
            {
                case HubTransfer ht:
                    hubTransfers.Add(ht);
                    break;
                case Transfer t:
                    allTransfers.Add(t);
                    break;
            }
        }

        // If no hub transfers, all transfers are standalone
        if (hubTransfers.Count == 0)
        {
            return new AggregationResultV1(
                new List<(HubTransfer, HashSet<Transfer>)>(),
                allTransfers,
                allTransfers
            );
        }

        // Build adjacency list: from -> list of transfers originating from that address
        var adjacency = new Dictionary<string, List<Transfer>>();
        foreach (var t in allTransfers)
        {
            var fromLower = t.From.ToLowerInvariant();
            if (!adjacency.TryGetValue(fromLower, out var list))
            {
                list = new List<Transfer>();
                adjacency[fromLower] = list;
            }
            list.Add(t);
        }

        var usedEdgesGlobal = new HashSet<Transfer>();
        var hubSummaries = new List<(HubTransfer Hub, HashSet<Transfer> Edges)>();

        // For each hub transfer, find all paths and collect edges
        foreach (var hubTransfer in hubTransfers)
        {
            var hubFrom = hubTransfer.From.ToLowerInvariant();
            var hubTo = hubTransfer.To.ToLowerInvariant();

            var usedEdges = new HashSet<Transfer>();
            var pathStack = new List<Transfer>();

            void Dfs(string current, HashSet<string> visited)
            {
                if (string.Equals(current, hubTo, StringComparison.OrdinalIgnoreCase))
                {
                    // Found a path - add all edges in the current path
                    foreach (var edge in pathStack)
                    {
                        usedEdges.Add(edge);
                    }
                    return;
                }

                if (!adjacency.TryGetValue(current, out var edges))
                {
                    return;
                }

                foreach (var edge in edges)
                {
                    var next = edge.To.ToLowerInvariant();
                    if (visited.Contains(next))
                    {
                        continue;
                    }

                    pathStack.Add(edge);
                    visited.Add(next);

                    Dfs(next, visited);

                    visited.Remove(next);
                    pathStack.RemoveAt(pathStack.Count - 1);
                }
            }

            // DFS from hub.From
            var visitedSet = new HashSet<string> { hubFrom };
            Dfs(hubFrom, visitedSet);

            // Mark edges as used globally
            foreach (var edge in usedEdges)
            {
                usedEdgesGlobal.Add(edge);
            }

            hubSummaries.Add((hubTransfer, usedEdges));
        }

        // Find standalone transfers (not used by any hub)
        var standaloneTransfers = allTransfers
            .Where(t => !usedEdgesGlobal.Contains(t))
            .ToList();

        return new AggregationResultV1(hubSummaries, standaloneTransfers, allTransfers);
    }
}
