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

        // STEP 3: Add pooled H→TokenPool edges from snapshot balances (applying filters)
        AddHolderToTokenEdges_Pooled(
            capacityGraph,
            balanceGraph,
            r!,
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            excludedFromTokensFilter);

        // STEP 3b: Add pooled H→TokenPool edges from simulated balances (filtered)
        AddSimulatedBalances_Pooled(
            capacityGraph,
            simulated,
            r!,
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            excludedFromTokensFilter);

        // STEP 4: (pooled model) no BN edges to add

        // STEP 5: Remove TokenPool→source edges that form self-loops for source==sink (swap)
        if (sourceId != null && sourceEqualsSink && toTokensFilter.Count > 0)
        {
            RemoveTokenSelfLoopsForSwap(capacityGraph, sourceId.Value, toTokensFilter);
        }

        // STEP 6/7/8: Add trust-based out-edges from TokenPool→avatars (+ virtual sink in swap mode)
        AddTokenPoolOutEdges(
            capacityGraph,
            mergedTrust,
            virtualSinkAddress,
            sinkId,
            toTokensFilter,
            excludedToTokensFilter);

        // If a virtual sink was created but received no edges, prune it (mirror legacy behaviour)
        if (virtualSinkAddress != null)
        {
            bool anyVirtualSinkEdgesAdded = capacityGraph.Edges.Any(e => e.To == virtualSinkAddress.Value);
            if (!anyVirtualSinkEdgesAdded)
            {
                capacityGraph.AvatarNodes.Remove(virtualSinkAddress.Value);
                capacityGraph.Nodes.Remove(virtualSinkAddress.Value);
                capacityGraph.VirtualSinkAddress = null;
            }
        }

        return capacityGraph;
    }


    #region Helper Methods

    private readonly struct SimulatedBalance
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

    private List<SimulatedBalance> NormalizeSimulatedBalances(List<DTOs.SimulatedBalance>? raw)
    {
        if (raw == null || raw.Count == 0)
        {
            return new List<SimulatedBalance>(0);
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

        var list = new List<SimulatedBalance>(acc.Count);
        foreach (var kvp in acc)
        {
            list.Add(new SimulatedBalance
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

    private void AddHolderToTokenEdges_Pooled(
        CapacityGraph g,
        BalanceGraph snapshot,
        FlowRequest req,
        bool sourceEqualsSink,
        HashSet<int> fromTokensFilter,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedFromTokensFilter)
    {
        int? sourceId = !string.IsNullOrWhiteSpace(req.Source) ? AddressIdPool.IdOf(req.Source!) : null;

        foreach (var bn in snapshot.BalanceNodes.Values)
        {
            bool isSource = sourceId.HasValue && bn.Holder == sourceId.Value;

            // keep all your existing filters verbatim:
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && toTokensFilter.Contains(bn.Token)) continue;
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(bn.Token)) continue;
            if (isSource && excludedFromTokensFilter.Count > 0 && excludedFromTokensFilter.Contains(bn.Token)) continue;
            if (bn.IsWrapped && (req.WithWrap == null || req.WithWrap == false)) continue;
            if (bn.IsWrapped && !isSource) continue;

            // ensure the pool node exists
            g.AddTokenNode(bn.Token);
            int pool = AddressIdPool.TokenPoolIdOf(bn.Token);

            // H -> TokenPool(T), capacity = balance
            g.AddCapacityEdge(bn.Holder, pool, bn.Token, bn.Amount);
        }
    }

    private void AddSimulatedBalances_Pooled(
        CapacityGraph g,
        List<SimulatedBalance> simulated,
        FlowRequest req,
        bool sourceEqualsSink,
        HashSet<int> fromTokensFilter,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedFromTokensFilter)
    {
        int? sourceId = !string.IsNullOrWhiteSpace(req.Source) ? AddressIdPool.IdOf(req.Source!) : null;

        foreach (var sb in simulated)
        {
            bool isSource = sourceId.HasValue && sb.HolderId == sourceId.Value;

            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 &&
                toTokensFilter.Contains(sb.TokenId)) continue;
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(sb.TokenId)) continue;
            if (isSource && excludedFromTokensFilter.Count > 0 &&
                excludedFromTokensFilter.Contains(sb.TokenId)) continue;

            if (sb.IsWrapped && !(req.WithWrap ?? false)) continue;
            if (sb.IsWrapped && !isSource) continue;

            g.AddTokenNode(sb.TokenId);
            int pool = AddressIdPool.TokenPoolIdOf(sb.TokenId);

            g.AddCapacityEdge(sb.HolderId, pool, sb.TokenId, sb.Amount);
        }
    }

    private void AddTokenPoolOutEdges(
        CapacityGraph g,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        int? virtualSink,
        int? sinkId,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter)
    {
        // Build token -> list of avatars who trust that token (same as before)
        var tokenToAvatars = new Dictionary<int, List<int>>();
        foreach (var (truster, trustedTokens) in accountTrusts)
        {
            foreach (var t in trustedTokens)
            {
                bool isSink = sinkId.HasValue && truster == sinkId.Value;
                if (isSink && toTokensFilter.Count > 0 && !toTokensFilter.Contains(t)) continue;
                if (isSink && excludedToTokensFilter.Count > 0 && excludedToTokensFilter.Contains(t)) continue;

                if (!tokenToAvatars.TryGetValue(t, out var list))
                    tokenToAvatars[t] = list = new List<int>();
                list.Add(truster);
            }
        }

        foreach (var (token, acceptors) in tokenToAvatars)
        {
            int pool = AddressIdPool.TokenPoolIdOf(token);
            if (!g.Nodes.ContainsKey(pool)) continue; // no supply -> no out-edges

            // Capacity tip: bound each out-edge by the total token supply to help the solver.
            foreach (var a in acceptors)
            {
                // we deliberately keep 'self' edges (pool->holder) out; holders already contribute via H->pool
                // but there is no concept of "holder" on pool edges; skip avatar==source in certain swap cases below
                g.AddCapacityEdge(pool, a, token, long.MaxValue);
            }
        }

        // Virtual sink edges (swap mode): TokenPool(token) -> virtualSink
        if (virtualSink is int vs)
        {
            foreach (var t in toTokensFilter)
            {
                int pool = AddressIdPool.TokenPoolIdOf(t);
                if (!g.Nodes.ContainsKey(pool)) continue;
                g.AddCapacityEdge(pool, vs, t, long.MaxValue);
            }
        }
    }

    private void RemoveTokenSelfLoopsForSwap(
        CapacityGraph g,
        int sourceId,
        HashSet<int> toTokensFilter)
    {
        for (int i = g.Edges.Count - 1; i >= 0; i--)
        {
            var e = g.Edges[i];
            bool isPool = AddressIdPool.IsBalanceNode(e.From) && AddressIdPool.StringOf(e.From).StartsWith("tpool-");
            if (!isPool) continue;
            if (e.To == sourceId && toTokensFilter.Contains(e.Token))
                g.Edges.RemoveAt(i);
        }
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

    #endregion
}