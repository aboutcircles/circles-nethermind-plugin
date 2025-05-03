using System.Collections.Concurrent;
using Circles.Index.Utils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class GraphFactory
{
    private const string VIRTUAL_SINK_SUFFIX = "_virtual_sink";

    public static Dictionary<int, HashSet<int>> BuildTrustLookup(TrustGraph graph)
    {
        var dict = new Dictionary<int, HashSet<int>>();

        foreach (var edge in graph.Edges)
        {
            if (!dict.TryGetValue(edge.From, out var set))
            {
                set = new HashSet<int>();
                dict[edge.From] = set;
            }

            set.Add(edge.To);
        }

        return dict;
    }

    /// <summary>
    /// Loads all v2 trust edges from the database and creates a trust graph from them.
    /// </summary>
    /// <returns>A trust graph containing all v2 trust edges.</returns>
    public TrustGraph V2TrustGraph(LoadGraph loadGraph)
    {
        var graph = new TrustGraph();
        var trustEdges = loadGraph.LoadV2Trust().ToArray();

        foreach (var trustEdge in trustEdges)
        {
            var trusterId = AddressIdPool.IdOf(trustEdge.Truster);
            var trusteeId = AddressIdPool.IdOf(trustEdge.Trustee);

            if (!graph.AvatarNodes.ContainsKey(trusterId))
            {
                graph.AddAvatar(trusterId);
            }

            if (!graph.AvatarNodes.ContainsKey(trusteeId))
            {
                graph.AddAvatar(trusteeId);
            }

            graph.AddTrustEdge(trusterId, trusteeId);
        }

        return graph;
    }

    /// <summary>
    /// Loads all v2 balances from the database and creates a balance graph from them.
    /// </summary>
    /// <returns>A balance graph containing all v2 balances and holders.</returns>
    public BalanceGraph V2BalanceGraph(LoadGraph loadGraph)
    {
        var graph = new BalanceGraph();
        var balances = loadGraph.LoadV2Balances().ToArray();

        foreach (var balance in balances)
        {
            if (!graph.AvatarNodes.ContainsKey(balance.Account))
            {
                graph.AddAvatar(balance.Account);
            }

            graph.AddBalance(
                balance.Account,
                balance.TokenAddress,
                ConversionUtils.TruncateToInt64(UInt256.Parse(balance.Balance)),
                balance.IsWrapped,
                balance.IsStatic);
        }

        return graph;
    }

    public static int _c = 0;

    /// <summary>
    /// Takes a balance graph and a trust graph and creates a capacity graph from them.
    /// Also sets up a "virtual sink" if source == sink and toTokens are specified.
    /// </summary>
    /// <param name="balanceGraph">The balance graph to use.</param>
    /// <param name="trustGraph">The trust graph to use.</param>
    /// <param name="request">Flow request parameters.</param>
    /// <returns>A capacity graph created from the balance and trust graphs.</returns>
    public CapacityGraph CreateCapacityGraph(BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> trustLookup, FlowRequest? r)
    {
        Interlocked.Increment(ref _c);
        Console.WriteLine($"Creating capacity graph {_c}...");

        var capacityGraph = new CapacityGraph();

        // STEP 1: Add all avatar nodes from both graphs
        AddAllAvatarNodes(capacityGraph, balanceGraph, trustLookup);

        int? virtualSinkAddress = null;
        HashSet<int> virtualSinkTrustedTokens = new HashSet<int>();

        var sourceEqualsSink = r.Source?.Trim().ToLowerInvariant() == r.Sink?.Trim().ToLowerInvariant();

        // Setup key filters
        var toTokensFilter = r.ToTokens?
                                 .Select(AddressIdPool.IdOf)
                                 .ToHashSet()
                             ?? new HashSet<int>();

        var fromTokensFilter = r.FromTokens?
                                   .Select(AddressIdPool.IdOf)
                                   .ToHashSet()
                               ?? new HashSet<int>();

        var excludedFromTokensFilter = r.ExcludedFromTokens?
                                           .Select(AddressIdPool.IdOf)
                                           .ToHashSet()
                                       ?? new HashSet<int>();

        var excludedToTokensFilter = r.ExcludedToTokens?
                                         .Select(AddressIdPool.IdOf)
                                         .ToHashSet()
                                     ?? new HashSet<int>();

        // STEP 2: Create a virtual sink if needed
        int? sourceId = !string.IsNullOrWhiteSpace(r.Source) ? AddressIdPool.IdOf(r.Source) : null;
        int? sinkId = !string.IsNullOrWhiteSpace(r.Sink) ? AddressIdPool.IdOf(r.Sink) : null;

        if (sourceId != null && sourceEqualsSink && toTokensFilter.Count > 0)
        {
            (virtualSinkAddress, virtualSinkTrustedTokens) =
                CreateVirtualSink(capacityGraph, sourceId.Value, toTokensFilter, balanceGraph);
        }

        // STEP 3: Add balance nodes (applying filters)
        AddFilteredBalanceNodes(
            capacityGraph,
            balanceGraph,
            r,
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            excludedFromTokensFilter);

        // STEP 4: Add capacity edges from balance graph
        AddCapacityEdges(capacityGraph, balanceGraph);

        // STEP 5: Remove direct BN->source edges that form self-loops for source==sink
        if (sourceId != null && sourceEqualsSink && toTokensFilter.Count > 0)
        {
            RemoveSelfLoopEdges(capacityGraph, sourceId.Value, toTokensFilter);
        }

        // STEP 6: Create account trust dictionary for efficient lookups
        // STEP 7: Add virtual sink edges if using virtual sink
        if (sourceId != null && virtualSinkAddress != null)
        {
            var anyVirtualSinkEdgesAdded = AddVirtualSinkEdges(
                capacityGraph, balanceGraph, sourceId.Value, virtualSinkAddress.Value, virtualSinkTrustedTokens,
                trustLookup);

            // Remove virtual sink if no edges were added
            if (!anyVirtualSinkEdgesAdded)
            {
                capacityGraph.AvatarNodes.Remove(virtualSinkAddress.Value);
                capacityGraph.Nodes.Remove(virtualSinkAddress.Value);
                capacityGraph.VirtualSinkAddress = null;
                // // Console.WriteLine("Removed virtual sink due to no edges");
            }
        }

        // STEP 8: Add regular trust-based capacity edges
        AddTrustBasedCapacityEdges(capacityGraph, balanceGraph, trustLookup, virtualSinkAddress, sinkId, toTokensFilter,
            excludedToTokensFilter);

        return capacityGraph;
    }

    /// <summary>
    /// Creates a flow graph from a capacity graph.
    /// </summary>
    public FlowGraph CreateFlowGraph(CapacityGraph capacityGraph)
    {
        var flowGraph = new FlowGraph(capacityGraph.Nodes.Count + 1, capacityGraph.Edges.Count * 2 + 1);
        flowGraph.AddCapacity(capacityGraph);
        return flowGraph;
    }

    #region Helper Methods

    private void AddAllAvatarNodes(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> trustLookup)
    {
        // every avatar that shows up as a balance holder
        foreach (var avatarId in balanceGraph.AvatarNodes.Keys)
            capacityGraph.AddAvatar(avatarId);

        // every avatar that *trusts* something
        foreach (var truster in trustLookup.Keys)
            capacityGraph.AddAvatar(truster);

        // every token that is trusted by somebody
        foreach (var trustedSet in trustLookup.Values)
        foreach (var tokenId in trustedSet)
            capacityGraph.AddAvatar(tokenId);
    }

    private (int address, HashSet<int> trustedTokens) CreateVirtualSink(
        CapacityGraph capacityGraph,
        int sourceAddress,
        HashSet<int> toTokensFilter,
        BalanceGraph balanceGraph)
    {
        var virtualSinkAddress = sourceAddress + VIRTUAL_SINK_SUFFIX;
        var virtualSinkAddressId = AddressIdPool.IdOf(virtualSinkAddress);

        capacityGraph.AddAvatar(virtualSinkAddressId);
        capacityGraph.VirtualSinkAddress = virtualSinkAddressId;

        // // Console.WriteLine($"Created virtual sink: {virtualSinkAddress}");

        // Collect tokens trusted by virtual sink (excluding wrapped tokens)
        var virtualSinkTrustedTokens = new HashSet<int>();
        foreach (var token in toTokensFilter)
        {
            bool isWrapped = balanceGraph.BalanceNodes.Values
                .Any(bn => bn.Token == token && bn.IsWrapped);

            if (isWrapped)
            {
                // // Console.WriteLine($"Skipping wrapped token {token} for virtual sink trust");
                continue;
            }

            virtualSinkTrustedTokens.Add(token);
            // // Console.WriteLine($"Virtual sink trusts token: {token}");
        }

        return (virtualSinkAddressId, virtualSinkTrustedTokens);
    }

    private void AddFilteredBalanceNodes(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        FlowRequest request,
        bool sourceEqualsSink,
        HashSet<int> fromTokensFilter,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedFromTokensFilter)
    {
        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            int? sourceId = request.Source != null ? AddressIdPool.IdOf(request.Source) : null;
            var isSource = balanceNode.Holder == sourceId;

            // Case 1: When source and sink are the same, don't add balances for tokens that are in toTokens
            // This prevents trivial self-loops when attempting token conversion
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 &&
                toTokensFilter.Contains(balanceNode.Token))
            {
                continue;
            }

            // Case 2: If fromTokens is specified, only include those tokens for the source address
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(balanceNode.Token))
            {
                continue;
            }

            // Case 2b: If excludedFromTokens is specified, exclude those tokens for the source address
            if (isSource && excludedFromTokensFilter.Count > 0 &&
                excludedFromTokensFilter.Contains(balanceNode.Token))
            {
                continue;
            }

            // Case 3a: Filter out wrapped tokens if request.WithWrap = false
            if (balanceNode.IsWrapped && (request.WithWrap == null || request.WithWrap == false))
            {
                continue;
            }

            // Case 3b: Filter out wrapped tokens not held by the source
            if (balanceNode.IsWrapped && !isSource)
            {
                continue;
            }

            capacityGraph.AddBalanceNode(
                balanceNode.Holder,
                balanceNode.Token,
                balanceNode.Amount,
                balanceNode.IsWrapped,
                balanceNode.IsStatic,
                balanceNode.Address
            );
        }
    }

    private void AddCapacityEdges(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph)
    {
        foreach (var capacityEdge in balanceGraph.Edges)
        {
            if (!capacityGraph.Nodes.ContainsKey(capacityEdge.From)
                || !capacityGraph.Nodes.ContainsKey(capacityEdge.To))
            {
                continue;
            }

            capacityGraph.AddCapacityEdge(
                capacityEdge.From,
                capacityEdge.To,
                capacityEdge.Token,
                capacityEdge.InitialCapacity
            );
        }
    }

    private void RemoveSelfLoopEdges(
        CapacityGraph capacityGraph,
        int sourceAddress,
        HashSet<int> toTokensFilter)
    {
        for (int i = capacityGraph.Edges.Count - 1; i >= 0; i--)
        {
            var edge = capacityGraph.Edges[i];
            if (!AddressIdPool.IsBalanceNode(edge.From))
            {
                continue;
            }

            var holder = capacityGraph.BalanceNodes[edge.From].Holder;
            var holderSameAsSource = holder == sourceAddress;
            var toSameAsSource = edge.To == sourceAddress;
            var tokenInFilter = toTokensFilter.Contains(edge.Token);

            if (holderSameAsSource && toSameAsSource && tokenInFilter)
            {
                capacityGraph.Edges.RemoveAt(i);
            }
        }

        // capacityGraph.Edges.RemoveWhere(edge =>
        //     AddressIdPool.IsBalanceNode(edge.From) && capacityGraph.BalanceNodes[edge.From].Holder ==
        //                                            sourceAddress /*from is BalanceNode and source equals balance holder*/
        //                                            && edge.To == sourceAddress /*to equals source*/
        //                                            && toTokensFilter.Contains(edge.Token)
        // );
    }

    private bool AddVirtualSinkEdges(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        int sourceAddress,
        int virtualSinkAddress,
        HashSet<int> virtualSinkTrustedTokenIds,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts)
    {
        bool anyEdgeAdded = false;

        foreach (var bn in balanceGraph.BalanceNodes.Values)
        {
            // skip balances the capacity graph didn’t keep
            if (!capacityGraph.Nodes.ContainsKey(bn.Address))
                continue;

            // never draw from the source’s own balances
            if (bn.Holder == sourceAddress)
                continue;

            // does the holder trust this token?
            if (!accountTrusts.TryGetValue(bn.Holder, out var tokensTheyTrust) ||
                !tokensTheyTrust.Contains(bn.Token))
                continue;

            // does the virtual sink accept this token?
            if (!virtualSinkTrustedTokenIds.Contains(bn.Token))
                continue;

            capacityGraph.AddCapacityEdge(
                bn.Address,
                virtualSinkAddress,
                bn.Token,
                bn.Amount);

            anyEdgeAdded = true;
        }

        return anyEdgeAdded;
    }

    /// <summary>
    /// Adds capacity edges “BalanceNode ➜ trusting-avatar” for every avatar that
    /// accepts the BalanceNode’s token.  This version builds an index so that
    /// each BalanceNode only visits the *avatars that trust its token*, and runs
    /// the BalanceNode loop in parallel.  All mutations on shared state are
    /// funnelled through thread-safe helpers to avoid locking inside the hot loop.
    /// </summary>
    private void AddTrustBasedCapacityEdges(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        int? virtualSinkAddress,
        int? sinkAddress,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter)
    {
        /* build “token ➜ avatars that trust it”, applying sink filters inline */
        var tokenToAvatars = new Dictionary<int, List<int>>();

        foreach (var (avatar, trustedTokens) in accountTrusts)
        {
            foreach (var token in trustedTokens)
            {
                // If the avatar is the real sink, enforce toTokens / excludedToTokens
                if (avatar == sinkAddress)
                {
                    if (toTokensFilter.Count > 0 && !toTokensFilter.Contains(token)) continue;
                    if (excludedToTokensFilter.Count > 0 && excludedToTokensFilter.Contains(token)) continue;
                }

                if (!tokenToAvatars.TryGetValue(token, out var list))
                    list = tokenToAvatars[token] = new List<int>();

                list.Add(avatar);
            }
        }

        /* create edges in parallel */
        var edgeBag = new ConcurrentBag<(int From, int To, int Token, long Amount)>();

        Parallel.ForEach(balanceGraph.BalanceNodes.Values, bn =>
        {
            if (!tokenToAvatars.TryGetValue(bn.Token, out var avatars))
                return;

            foreach (var avatar in avatars)
            {
                if (avatar == bn.Holder) continue; // no self-edge
                if (virtualSinkAddress.HasValue && avatar == virtualSinkAddress) continue;
                if (!capacityGraph.Nodes.ContainsKey(bn.Address) ||
                    !capacityGraph.Nodes.ContainsKey(avatar)) continue;

                edgeBag.Add((bn.Address, avatar, bn.Token, bn.Amount));
            }
        });

        /* commit edges serially */
        foreach (var (from, to, token, amount) in edgeBag)
            capacityGraph.AddCapacityEdge(from, to, token, amount);
    }

    #endregion
}