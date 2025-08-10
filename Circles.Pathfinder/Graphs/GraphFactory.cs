using System.Collections.Concurrent;
using Circles.Index.Common;
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
                CirclesConverter.TruncateToInt64(UInt256.Parse(balance.Balance)),
                balance.IsWrapped,
                balance.IsStatic);
        }

        return graph;
    }

    private static int _c = 0;

    /// <summary>
    /// Takes a balance graph and a trust graph and creates a capacity graph from them.
    /// Also sets up a "virtual sink" if source == sink and toTokens are specified.
    /// </summary>
    /// <param name="balanceGraph">The balance graph to use.</param>
    /// <param name="trustGraph">The trust graph to use.</param>
    /// <param name="request">Flow request parameters.</param>
    /// <returns>A capacity graph created from the balance and trust graphs.</returns>
    public CapacityGraph CreateCapacityGraph(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> trustLookup,
        FlowRequest? r)
    {
        Interlocked.Increment(ref _c);
        Console.WriteLine($"Creating capacity graph {_c}...");

        var capacityGraph = new CapacityGraph();

        // STEP 1: Add all avatar nodes from both graphs
        AddAllAvatarNodes(capacityGraph, balanceGraph, trustLookup);

        // STEP 1b: Add avatars referenced by simulated balances (holders + tokens)
        var simulated = NormalizeSimulatedBalances(r?.SimulatedBalances);
        foreach (var sb in simulated)
        {
            capacityGraph.AddAvatar(sb.HolderId);
            capacityGraph.AddAvatar(sb.TokenId);
        }

        // STEP 1c: Add simulated trust relations
        var simulatedTrust = NormalizeSimulatedTrusts(r?.SimulatedTrusts);
        foreach (var kv in simulatedTrust)
        {
            capacityGraph.AddAvatar(kv.Key);
            foreach (var trustee in kv.Value)
            {
                capacityGraph.AddAvatar(trustee);
            }
        }

        var mergedTrust = simulatedTrust.Count == 0 ? trustLookup : MergeTrust(trustLookup, simulatedTrust);

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
            var wrappedTokensInSim = simulated
                .Where(x => x.IsWrapped)
                .Select(x => x.TokenId)
                .ToHashSet();
            
            (virtualSinkAddress, virtualSinkTrustedTokens) = CreateVirtualSink(
                capacityGraph,
                sourceId.Value,
                toTokensFilter,
                balanceGraph,
                wrappedTokensInSim);
        }

        // STEP 3: Add balance nodes (applying filters)
        AddFilteredBalanceNodes(
            capacityGraph,
            balanceGraph,
            r!,
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            excludedFromTokensFilter);

        // STEP 3b: Add simulated balances (filtered) into capacityGraph + BN→holder capacity edges
        AddSimulatedBalances(
            capacityGraph,
            simulated,
            r!,
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
                capacityGraph,
                sourceId.Value,
                virtualSinkAddress.Value,
                virtualSinkTrustedTokens);

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
            capacityGraph,
            mergedTrust,
            virtualSinkAddress,
            sinkId,
            toTokensFilter,
            excludedToTokensFilter);

        return capacityGraph;
    }

    #region Helper Methods

    private readonly struct _SimBal
    {
        public int HolderId { get; init; }
        public int TokenId { get; init; }
        public long Amount { get; init; }
        public bool IsWrapped { get; init; }
        public bool IsStatic { get; init; }
    }

    private Dictionary<int, HashSet<int>> NormalizeSimulatedTrusts(List<SimulatedTrust>? raw)
    {
        var result = new Dictionary<int, HashSet<int>>();
        if (raw == null || raw.Count == 0)
        {
            return result;
        }

        for (int i = 0; i < raw.Count; i++)
        {
            var st = raw[i];
            bool missing = string.IsNullOrWhiteSpace(st.Truster) || string.IsNullOrWhiteSpace(st.Trustee);
            if (missing)
            {
                continue;
            }

            int trusterId = AddressIdPool.IdOf(st.Truster.ToLowerInvariant());
            int trusteeId = AddressIdPool.IdOf(st.Trustee.ToLowerInvariant());

            if (!result.TryGetValue(trusterId, out var set))
            {
                set = new HashSet<int>();
                result[trusterId] = set;
            }

            set.Add(trusteeId);
        }

        return result;
    }

    private IReadOnlyDictionary<int, HashSet<int>> MergeTrust(
        IReadOnlyDictionary<int, HashSet<int>> onchain,
        Dictionary<int, HashSet<int>> simulated)
    {
        var merged = new Dictionary<int, HashSet<int>>(onchain.Count + simulated.Count);

        foreach (var kv in onchain)
        {
            merged[kv.Key] = new HashSet<int>(kv.Value);
        }

        foreach (var kv in simulated)
        {
            if (!merged.TryGetValue(kv.Key, out var set))
            {
                set = new HashSet<int>();
                merged[kv.Key] = set;
            }

            foreach (var t in kv.Value)
            {
                set.Add(t);
            }
        }

        return merged;
    }

    private List<_SimBal> NormalizeSimulatedBalances(List<SimulatedBalance>? raw)
    {
        if (raw == null || raw.Count == 0)
        {
            return new List<_SimBal>(0);
        }

        var acc = new Dictionary<(int holder, int token, bool isWrapped, bool isStatic), long>();

        for (int i = 0; i < raw.Count; i++)
        {
            var sb = raw[i];
            bool holderMissing = string.IsNullOrWhiteSpace(sb.Holder);
            bool tokenMissing = string.IsNullOrWhiteSpace(sb.Token);
            bool amountMissing = string.IsNullOrWhiteSpace(sb.Amount);
            if (holderMissing || tokenMissing || amountMissing)
            {
                continue;
            }

            int holderId = AddressIdPool.IdOf(sb.Holder.ToLowerInvariant());
            int tokenId = AddressIdPool.IdOf(sb.Token.ToLowerInvariant());

            var amt = CirclesConverter.TruncateToInt64(UInt256.Parse(sb.Amount));
            if (amt <= 0)
            {
                continue;
            }

            bool isWrapped = sb.IsWrapped ?? false;
            bool isStatic = sb.IsStatic ?? false;

            var key = (holderId, tokenId, isWrapped, isStatic);
            if (acc.TryGetValue(key, out var existing))
            {
                long sum = existing + amt;
                acc[key] = sum < 0 ? long.MaxValue : sum; // saturate guard
            }
            else
            {
                acc[key] = amt;
            }
        }

        var list = new List<_SimBal>(acc.Count);
        foreach (var kvp in acc)
        {
            list.Add(new _SimBal
            {
                HolderId = kvp.Key.holder,
                TokenId = kvp.Key.token,
                IsWrapped = kvp.Key.isWrapped,
                IsStatic = kvp.Key.isStatic,
                Amount = kvp.Value
            });
        }

        return list;
    }

    private void AddAllAvatarNodes(
        CapacityGraph capacityGraph,
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> trustLookup)
    {
        // every avatar that shows up as a balance holder
        foreach (var avatarId in balanceGraph.AvatarNodes.Keys)
        {
            capacityGraph.AddAvatar(avatarId);
        }

        // every avatar that *trusts* something
        foreach (var truster in trustLookup.Keys)
        {
            capacityGraph.AddAvatar(truster);
        }

        // every token that is trusted by somebody
        foreach (var trustedSet in trustLookup.Values)
        {
            foreach (var tokenId in trustedSet)
            {
                capacityGraph.AddAvatar(tokenId);
            }
        }
    }

    private (int address, HashSet<int> trustedTokens) CreateVirtualSink(
        CapacityGraph capacityGraph,
        int sourceAddress,
        HashSet<int> toTokensFilter,
        BalanceGraph balanceGraph,
        HashSet<int> wrappedTokensInSim)
    {
        var virtualSinkAddress = sourceAddress + VIRTUAL_SINK_SUFFIX;
        var virtualSinkAddressId = AddressIdPool.IdOf(virtualSinkAddress);

        capacityGraph.AddAvatar(virtualSinkAddressId);
        capacityGraph.VirtualSinkAddress = virtualSinkAddressId;

        // Build a set of wrapped tokens once (snapshot) to avoid O(|toTokens|*|balances|)
        var snapshotWrappedTokens = new HashSet<int>();
        foreach (var bn in balanceGraph.BalanceNodes.Values)
        {
            if (bn.IsWrapped) snapshotWrappedTokens.Add(bn.Token);
        }

        // Collect tokens trusted by virtual sink (excluding wrapped tokens)
        var virtualSinkTrustedTokens = new HashSet<int>();
        foreach (var token in toTokensFilter)
        {
            bool tokenWrappedInSnapshot = snapshotWrappedTokens.Contains(token);
            bool tokenWrappedInSim = wrappedTokensInSim.Contains(token);

            bool shouldSkip = tokenWrappedInSnapshot || tokenWrappedInSim;
            if (shouldSkip)
            {
                // // Console.WriteLine($"Skipping wrapped token {token} for virtual sink trust");
                continue;
            }

            virtualSinkTrustedTokens.Add(token);
            // // Console.WriteLine($"Virtual sink trusts token: {token}");
        }

        return (virtualSinkAddressId, virtualSinkTrustedTokens);
    }

    private void AddSimulatedBalances(
        CapacityGraph capacityGraph,
        List<_SimBal> simulated,
        FlowRequest request,
        bool sourceEqualsSink,
        HashSet<int> fromTokensFilter,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedFromTokensFilter)
    {
        int? sourceId = !string.IsNullOrWhiteSpace(request.Source) ? AddressIdPool.IdOf(request.Source!) : null;

        foreach (var sb in simulated)
        {
            bool isSource = sourceId.HasValue && sb.HolderId == sourceId.Value;

            bool skipSelfToTokenWhenSourceEqualsSink =
                sourceEqualsSink &&
                isSource &&
                toTokensFilter.Count > 0 &&
                toTokensFilter.Contains(sb.TokenId);

            if (skipSelfToTokenWhenSourceEqualsSink)
            {
                continue;
            }

            bool sourceFromTokensMismatch =
                isSource &&
                fromTokensFilter.Count > 0 &&
                !fromTokensFilter.Contains(sb.TokenId);

            if (sourceFromTokensMismatch)
            {
                continue;
            }

            bool sourceExcludedFromToken =
                isSource &&
                excludedFromTokensFilter.Count > 0 &&
                excludedFromTokensFilter.Contains(sb.TokenId);

            if (sourceExcludedFromToken)
            {
                continue;
            }

            bool isWrapped = sb.IsWrapped;
            bool withWrapRequested = request.WithWrap == true;
            bool shouldSkipWrapped = isWrapped && !withWrapRequested;
            if (shouldSkipWrapped)
            {
                continue;
            }

            bool wrappedNotHeldBySource = isWrapped && !isSource;
            if (wrappedNotHeldBySource)
            {
                continue;
            }

            // Create balance node id separate from snapshot nodes to avoid collisions.
            int bnId = AddressIdPool.BalanceNodeIdOf($"{sb.HolderId}-{sb.TokenId}-sim");

            capacityGraph.AddBalanceNode(
                sb.HolderId,
                sb.TokenId,
                sb.Amount,
                sb.IsWrapped,
                sb.IsStatic,
                bnId);

            // Mirror snapshot behavior: add BN → holder capacity with the simulated amount.
            capacityGraph.AddCapacityEdge(sb.HolderId, bnId, sb.TokenId, sb.Amount);
        }
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
        for (int i = 0; i < balanceGraph.Edges.Count; i++)
        {
            var capacityEdge = balanceGraph.Edges[i];

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
    }

    private bool AddVirtualSinkEdges(
        CapacityGraph capacityGraph,
        int sourceAddress,
        int virtualSinkAddress,
        HashSet<int> virtualSinkTrustedTokenIds)
    {
        bool anyEdgeAdded = false;

        foreach (var bn in capacityGraph.BalanceNodes.Values)
        {
            bool isSourceOwnBalance = bn.Holder == sourceAddress;
            if (isSourceOwnBalance)
            {
                continue;
            }

            bool tokenAccepted = virtualSinkTrustedTokenIds.Contains(bn.Token);
            if (!tokenAccepted)
            {
                continue;
            }

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
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        int? virtualSinkAddress,
        int? sinkAddress,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter)
    {
        var tokenToAvatars = new Dictionary<int, List<int>>();

        foreach (var (avatar, trustedTokens) in accountTrusts)
        {
            foreach (var token in trustedTokens)
            {
                bool isSink = sinkAddress.HasValue && avatar == sinkAddress.Value;

                bool excludedByToTokensFilter =
                    isSink && toTokensFilter.Count > 0 && !toTokensFilter.Contains(token);
                if (excludedByToTokensFilter)
                {
                    continue;
                }

                bool excludedByExcludeToTokens = isSink && excludedToTokensFilter.Count > 0 &&
                                                 excludedToTokensFilter.Contains(token);
                if (excludedByExcludeToTokens)
                {
                    continue;
                }

                if (!tokenToAvatars.TryGetValue(token, out var list))
                {
                    list = tokenToAvatars[token] = new List<int>();
                }

                list.Add(avatar);
            }
        }

        /* create edges in parallel */
        var edgeBag = new ConcurrentBag<(int From, int To, int Token, long Amount)>();

        Parallel.ForEach(capacityGraph.BalanceNodes.Values, bn =>
        {
            if (!tokenToAvatars.TryGetValue(bn.Token, out var avatars))
            {
                return;
            }

            for (int i = 0; i < avatars.Count; i++)
            {
                var avatar = avatars[i];

                bool selfEdge = avatar == bn.Holder;
                if (selfEdge)
                {
                    continue;
                }

                bool isVirtualSink = virtualSinkAddress.HasValue && avatar == virtualSinkAddress.Value;
                if (isVirtualSink)
                {
                    continue;
                }

                bool nodesPresent =
                    capacityGraph.Nodes.ContainsKey(bn.Address) &&
                    capacityGraph.Nodes.ContainsKey(avatar);
                if (!nodesPresent)
                {
                    continue;
                }

                edgeBag.Add((bn.Address, avatar, bn.Token, bn.Amount));
            }
        });

        /* commit edges serially */
        foreach (var (from, to, token, amount) in edgeBag)
        {
            capacityGraph.AddCapacityEdge(from, to, token, amount);
        }
    }

    #endregion
}