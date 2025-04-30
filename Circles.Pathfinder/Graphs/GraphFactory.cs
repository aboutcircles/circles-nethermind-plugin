using System.Collections.Concurrent;
using Circles.Index.Utils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class GraphFactory
{
    private const string VIRTUAL_SINK_SUFFIX = "_virtual_sink";

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

    /// <summary>
    /// Takes a balance graph and a trust graph and creates a capacity graph from them.
    /// Also sets up a "virtual sink" if source == sink and toTokens are specified.
    /// </summary>
    /// <param name="balanceGraph">The balance graph to use.</param>
    /// <param name="trustGraph">The trust graph to use.</param>
    /// <param name="request">Flow request parameters.</param>
    /// <returns>A capacity graph created from the balance and trust graphs.</returns>
    public CapacityGraph CreateCapacityGraph(BalanceGraph balanceGraph, TrustGraph trustGraph, FlowRequest r)
    {
        // Initialize counters for skipped nodes/edges
        int skipBalanceCase2 = 0; // fromTokens filter
        int skipBalanceCase2b = 0; // excludedFromTokens filter
        int skipBalanceCase3a = 0; // withWrap=false filter
        int skipBalanceCase3b = 0; // wrapped tokens not held by source
        int skipCapEdgeCase1 = 0; // nodes not in capacity graph
        int skipCapEdgeCase2 = 0; // trust-based edges missing nodes
        int skipTrustEdgeSinkTokens = 0; // sink->token trust filtered by toTokens
        int skipTrustEdgeSinkExcludedTokens = 0; // sink->token trust filtered by excludedToTokens

        var sourceId = AddressIdPool.IdOf(r.Source);
        var sinkId = AddressIdPool.IdOf(r.Sink);

        var capacityGraph = new CapacityGraph();

        // Setup key filters
        var sourceEqualsSink = (r.Source?.ToLowerInvariant() == r.Sink?.ToLowerInvariant());
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

        // STEP 1: Add all avatar nodes from both graphs
        AddAllAvatarNodes(capacityGraph, balanceGraph, trustGraph);

        // STEP 2: Create a virtual sink if needed
        int? virtualSinkAddress = null;
        HashSet<int> virtualSinkTrustedTokens = new HashSet<int>();

        if (sourceEqualsSink && !string.IsNullOrEmpty(r.Source) && toTokensFilter.Count > 0)
        {
            (virtualSinkAddress, virtualSinkTrustedTokens) =
                CreateVirtualSink(capacityGraph, sourceId, toTokensFilter, balanceGraph);
        }

        // STEP 3: Add balance nodes (applying filters)
        AddFilteredBalanceNodes(
            capacityGraph,
            balanceGraph,
            r,
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            excludedFromTokensFilter,
            ref skipBalanceCase2,
            ref skipBalanceCase2b,
            ref skipBalanceCase3a,
            ref skipBalanceCase3b);

        // STEP 4: Add capacity edges from balance graph
        AddCapacityEdges(capacityGraph, balanceGraph, ref skipCapEdgeCase1);

        // STEP 5: Remove direct BN->source edges that form self-loops for source==sink
        if (sourceEqualsSink && !string.IsNullOrEmpty(r.Source) && toTokensFilter.Count > 0)
        {
            RemoveSelfLoopEdges(capacityGraph, sourceId, toTokensFilter);
        }

        // STEP 6: Create account trust dictionary for efficient lookups
        var accountTrusts = BuildAccountTrustDictionary(
            trustGraph, sinkId, toTokensFilter, excludedToTokensFilter,
            ref skipTrustEdgeSinkTokens, ref skipTrustEdgeSinkExcludedTokens);

        // STEP 7: Add virtual sink edges if using virtual sink
        bool anyVirtualSinkEdgesAdded = false;
        if (virtualSinkAddress != null)
        {
            anyVirtualSinkEdgesAdded = AddVirtualSinkEdges(
                capacityGraph, balanceGraph, sourceId, virtualSinkAddress.Value, virtualSinkTrustedTokens,
                accountTrusts);

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
        AddTrustBasedCapacityEdges(
            capacityGraph, balanceGraph, accountTrusts, virtualSinkAddress, ref skipCapEdgeCase2);

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

    private void AddAllAvatarNodes(CapacityGraph capacityGraph, BalanceGraph balanceGraph, TrustGraph trustGraph)
    {
        foreach (var avatar in balanceGraph.AvatarNodes.Values)
        {
            capacityGraph.AddAvatar(avatar.Address);
        }

        foreach (var avatar in trustGraph.AvatarNodes.Values)
        {
            capacityGraph.AddAvatar(avatar.Address);
        }
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
        HashSet<int> excludedFromTokensFilter,
        ref int skipBalanceCase2,
        ref int skipBalanceCase2b,
        ref int skipBalanceCase3a,
        ref int skipBalanceCase3b)
    {
        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            var isSource = balanceNode.Holder == AddressIdPool.IdOf(request.Source);

            // Case 1: When source and sink are the same, don't add balances for tokens that are in toTokens
            // This prevents trivial self-loops when attempting token conversion
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 &&
                toTokensFilter.Contains(balanceNode.Token))
            {
                // // Console.WriteLine($"Skipping balance of token {balanceNode.Token} for source=sink to prevent self-loop");
                continue;
            }

            // Case 2: If fromTokens is specified, only include those tokens for the source address
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(balanceNode.Token))
            {
                skipBalanceCase2++;
                continue;
            }

            // Case 2b: If excludedFromTokens is specified, exclude those tokens for the source address
            if (isSource && excludedFromTokensFilter.Count > 0 &&
                excludedFromTokensFilter.Contains(balanceNode.Token))
            {
                skipBalanceCase2b++;
                continue;
            }

            // Case 3a: Filter out wrapped tokens if request.WithWrap = false
            if (balanceNode.IsWrapped && (request.WithWrap == null || request.WithWrap == false))
            {
                skipBalanceCase3a++;
                continue;
            }

            // Case 3b: Filter out wrapped tokens not held by the source
            if (balanceNode.IsWrapped && !isSource)
            {
                skipBalanceCase3b++;
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
        BalanceGraph balanceGraph,
        ref int skipCapEdgeCase1)
    {
        foreach (var capacityEdge in balanceGraph.Edges)
        {
            if (!capacityGraph.Nodes.ContainsKey(capacityEdge.From)
                || !capacityGraph.Nodes.ContainsKey(capacityEdge.To))
            {
                skipCapEdgeCase1++;
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
        capacityGraph.Edges.RemoveWhere(edge =>
            AddressIdPool.IsBalanceNode(edge.From) && capacityGraph.BalanceNodes[edge.From].Holder ==
                                                   sourceAddress /*from is BalanceNode and source equals balance holder*/
                                                   && edge.To == sourceAddress /*to equals source*/
                                                   && toTokensFilter.Contains(edge.Token)
        );
    }

    private Dictionary<int, HashSet<int>> BuildAccountTrustDictionary(
        TrustGraph trustGraph,
        int sinkAddress,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter,
        ref int skipTrustEdgeSinkTokens,
        ref int skipTrustEdgeSinkExcludedTokens)
    {
        var accountTrusts = new Dictionary<int, HashSet<int>>();

        foreach (var edge in trustGraph.Edges)
        {
            // Skip trust edges from sink -> tokens not in toTokens
            if (edge.From == sinkAddress
                && toTokensFilter.Count > 0
                && !toTokensFilter.Contains(edge.To))
            {
                skipTrustEdgeSinkTokens++;
                continue;
            }

            // Skip trust edges from sink -> tokens in excludedToTokens
            if (edge.From == sinkAddress
                && excludedToTokensFilter.Count > 0
                && excludedToTokensFilter.Contains(edge.To))
            {
                skipTrustEdgeSinkExcludedTokens++;
                continue;
            }

            if (!accountTrusts.TryGetValue(edge.From, out var trustedTokens))
            {
                trustedTokens = new HashSet<int>();
                accountTrusts[edge.From] = trustedTokens;
            }

            trustedTokens.Add(edge.To);
        }

        return accountTrusts;
    }

    private bool AddVirtualSinkEdges(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        int sourceAddress,
        int virtualSinkAddress,
        HashSet<int> virtualSinkTrustedTokenIds,
        Dictionary<int, HashSet<int>> accountTrusts)
    {
        bool anyVirtualSinkEdgesAdded = false;

        foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
        {
            // Skip if the node doesn't exist in capacityGraph
            if (!capacityGraph.Nodes.ContainsKey(balanceNode.Address))
                continue;

            // Skip source's own balance nodes
            if (balanceNode.Holder == sourceAddress)
                continue;

            // Check if holder trusts this token
            if (accountTrusts.TryGetValue(balanceNode.Holder, out var tokensTheyTrust))
            {
                // If holder trusts the token and virtual sink also trusts the token, add edge
                if (tokensTheyTrust.Contains(balanceNode.Token) &&
                    virtualSinkTrustedTokenIds.Contains(balanceNode.Token))
                {
                    if (capacityGraph.Nodes.ContainsKey(virtualSinkAddress))
                    {
                        capacityGraph.AddCapacityEdge(
                            balanceNode.Address,
                            virtualSinkAddress,
                            balanceNode.Token,
                            balanceNode.Amount
                        );
                        anyVirtualSinkEdgesAdded = true;
                    }
                }
            }
        }

        return anyVirtualSinkEdgesAdded;
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
        Dictionary<int, HashSet<int>> accountTrusts,
        int? virtualSinkAddress,
        ref int skipCapEdgeCase2)
    {
        // Build "token -> avatars that trust it" lookup
        var tokenToTrustingAvatars = new Dictionary<int, List<int>>();

        foreach (var (avatar, trustedTokens) in accountTrusts)
        {
            foreach (var token in trustedTokens)
            {
                if (!tokenToTrustingAvatars.TryGetValue(token, out var list))
                {
                    list = new List<int>();
                    tokenToTrustingAvatars[token] = list;
                }

                list.Add(avatar);
            }
        }

        // Prepare edge collection – we create edges locally in each thread
        // and add them to the graph *once* at the end to keep the graph’s
        // internal dictionaries single-threaded.
        var edgeBag = new ConcurrentBag<(int From, int To, int Token, long Amount)>();

        // Outer loop over BalanceNodes in parallel.
        Parallel.ForEach(
            balanceGraph.BalanceNodes.Values,
            balanceNode =>
            {
                // avatars that trust this BN’s token
                if (!tokenToTrustingAvatars.TryGetValue(balanceNode.Token, out var avatars))
                    return;

                foreach (var avatar in avatars)
                {
                    // Skip self, virtual sink and any node missing in capacityGraph
                    if (avatar == balanceNode.Holder ||
                        (virtualSinkAddress.HasValue && avatar == virtualSinkAddress) ||
                        !capacityGraph.Nodes.ContainsKey(balanceNode.Address) ||
                        !capacityGraph.Nodes.ContainsKey(avatar))
                    {
                        continue;
                    }

                    edgeBag.Add((
                        From: balanceNode.Address,
                        To: avatar,
                        Token: balanceNode.Token,
                        Amount: balanceNode.Amount));
                }
            });

        /* ---------------------------------------------------------------
         * 4. Commit edges to the graph serially – graph internals stay safe.
         * --------------------------------------------------------------- */
        foreach (var (from, to, token, amount) in edgeBag)
        {
            capacityGraph.AddCapacityEdge(from, to, token, amount);
        }
    }

    #endregion
}