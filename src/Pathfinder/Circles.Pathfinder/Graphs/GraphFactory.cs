using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Int256;

namespace Circles.Pathfinder.Graphs;

public class GraphFactory(string routerAddress, ILoadGraph loadGraph, ILogger<GraphFactory>? logger = null)
{
    private const string VirtualSinkSuffix = "_virtual_sink";
    private static int _createdCount;
    private readonly ILogger _logger = logger ?? NullLogger<GraphFactory>.Instance;

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
    public TrustGraph V2TrustGraph()
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
    public BalanceGraph V2BalanceGraph()
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

    /// <summary>
    /// Creates a base capacity graph with NO per-request filtering.
    /// Contains only: avatar nodes, registered avatars, groups, router, consented flags,
    /// and unfiltered edges from all balances and trust relations.
    /// Used by the shared snapshot path (CapacityGraphPool.BuildFullGraph).
    /// </summary>
    public CapacityGraph CreateBaseCapacityGraph(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> trustLookup,
        CachedGroupData? cachedGroupData = null)
    {
        Interlocked.Increment(ref _createdCount);
        _logger.LogDebug("Creating BASE capacity graph {Count}...", _createdCount);

        var capacityGraph = new CapacityGraph();

        // Build registered avatar set FIRST (needed for filtering in AddAllAvatarNodes)
        foreach (var avatar in loadGraph.LoadRegisteredAvatars())
        {
            var id = AddressIdPool.IdOf(avatar.ToLowerInvariant());
            capacityGraph.AddAvatar(id);
            capacityGraph.RegisteredAvatarIds.Add(id);
        }

        // Add all avatar nodes from both graphs (filtered against registered set)
        AddAllAvatarNodes(capacityGraph, balanceGraph, trustLookup);

        // Load wrapper→avatar mappings (for DTO output resolution)
        LoadWrapperMappings(capacityGraph, cachedGroupData);

        // Load groups and track router node
        LoadGroupsAndTrackRouter(capacityGraph, cachedGroupData);

        // Load consented flow flags
        LoadConsentedFlowFlags(capacityGraph, cachedGroupData);

        // Store trust lookup (no merge — no simulated trust in base graph)
        capacityGraph.TrustLookup = trustLookup;

        // Add ALL holder→token pool edges (no filters)
        AddHolderToTokenEdges_Pooled(
            capacityGraph, balanceGraph,
            req: null, sourceEqualsSink: false,
            new HashSet<int>(), new HashSet<int>(), new HashSet<int>());

        // Add ALL trust-based out-edges (no filters)
        var (totalGroupTokenEdges, routerFilteredCount) = AddTokenPoolOutEdges(
            capacityGraph, trustLookup,
            virtualSink: null, virtualSinkTrustedTokens: new HashSet<int>(),
            sinkId: null, sourceId: null, new HashSet<int>(), new HashSet<int>());
        capacityGraph.TotalGroupTokenEdges = totalGroupTokenEdges;
        capacityGraph.RouterFilteredEdges = routerFilteredCount;

        // Add ALL group minting edges (no filters)
        AddGroupMintingEdges(
            capacityGraph, trustLookup,
            sinkId: null, new HashSet<int>(), new HashSet<int>());

        return capacityGraph;
    }

    /// <summary>
    /// Creates a filtered capacity graph for a specific <see cref="FlowRequest"/>.
    /// Applies token filters, simulated balances/trusts, virtual sink construction,
    /// and quantized mode logic. Used by CapacityGraphPool.Rent for ad-hoc requests.
    /// </summary>
    public CapacityGraph CreateCapacityGraph(
        BalanceGraph balanceGraph,
        IReadOnlyDictionary<int, HashSet<int>> trustLookup,
        FlowRequest? request = null,
        CachedGroupData? cachedGroupData = null)
    {
        // Fast path: no request means base graph (delegate to clean path)
        if (request == null)
            return CreateBaseCapacityGraph(balanceGraph, trustLookup, cachedGroupData);

        Interlocked.Increment(ref _createdCount);
        _logger.LogDebug("Creating FILTERED capacity graph {Count}...", _createdCount);

        var capacityGraph = new CapacityGraph();

        // STEP 1: Build registered avatar set FIRST (needed for filtering)
        if (cachedGroupData?.RegisteredAvatarIds != null)
        {
            foreach (var avatarId in cachedGroupData.RegisteredAvatarIds)
            {
                capacityGraph.AddAvatar(avatarId);
                capacityGraph.RegisteredAvatarIds.Add(avatarId);
            }
        }
        else
        {
            foreach (var avatar in loadGraph.LoadRegisteredAvatars())
            {
                var id = AddressIdPool.IdOf(avatar.ToLowerInvariant());
                capacityGraph.AddAvatar(id);
                capacityGraph.RegisteredAvatarIds.Add(id);
            }
        }

        // STEP 1a: Add all avatar nodes from both graphs (filtered against registered set)
        AddAllAvatarNodes(capacityGraph, balanceGraph, trustLookup);

        // STEP 1b: Add avatars referenced by simulated balances (holders + tokens)
        var simulated = NormalizeSimulatedBalances(request.SimulatedBalances);
        foreach (var sb in simulated)
        {
            capacityGraph.AddAvatar(sb.HolderId);
            capacityGraph.AddAvatar(sb.TokenId);
        }

        // STEP 1c: Add simulated trust relations
        var simulatedTrust = NormalizeSimulatedTrusts(request.SimulatedTrusts);
        foreach (var kv in simulatedTrust)
        {
            capacityGraph.AddAvatar(kv.Key);
            foreach (var trustee in kv.Value)
            {
                capacityGraph.AddAvatar(trustee);
            }
        }

        // STEP 1d: Load wrapper→avatar mappings
        LoadWrapperMappings(capacityGraph, cachedGroupData);

        // STEP 1e: Load groups and track router node for post-processing
        LoadGroupsAndTrackRouter(capacityGraph, cachedGroupData);

        // STEP 1f: Load consented flow flags
        LoadConsentedFlowFlags(capacityGraph, cachedGroupData);

        // STEP 1g: Add simulated consented avatars (for testing)
        // Capped at 100 to limit AddressIdPool growth from user input
        if (request?.SimulatedConsentedAvatars != null && request.SimulatedConsentedAvatars.Count > 0)
        {
            const int maxSimulatedConsentedAvatars = 100;
            if (request.SimulatedConsentedAvatars.Count > maxSimulatedConsentedAvatars)
            {
                _logger.LogWarning("SimulatedConsentedAvatars count {Count} exceeds limit {Limit}, truncating",
                    request.SimulatedConsentedAvatars.Count, maxSimulatedConsentedAvatars);
            }

            int addedCount = 0;
            foreach (var avatar in request.SimulatedConsentedAvatars.Take(maxSimulatedConsentedAvatars))
            {
                if (string.IsNullOrWhiteSpace(avatar))
                    continue;

                var normalized = avatar.Trim().ToLowerInvariant();

                // Validate address format (must be 0x + 40 hex chars)
                if (!IsValidEthereumAddress(normalized))
                {
                    _logger.LogWarning("Invalid address format for simulated consented avatar: '{Avatar}' (skipped)", avatar);
                    continue;
                }

                var avatarId = AddressIdPool.IdOf(normalized);
                capacityGraph.AddAvatar(avatarId);
                capacityGraph.ConsentedAvatars.Add(avatarId);
                addedCount++;
            }

            if (addedCount > 0)
            {
                _logger.LogDebug("Added {Count} simulated consented avatars", addedCount);
            }
        }

        var mergedTrust = simulatedTrust.Count == 0 ? trustLookup : MergeTrust(trustLookup, simulatedTrust);

        // Store trust lookup in capacity graph for consented flow validation
        capacityGraph.TrustLookup = mergedTrust;

        int? virtualSinkAddress = null;
        HashSet<int> virtualSinkTrustedTokens = new HashSet<int>();

        var sourceEqualsSink = request?.Source?.Trim().ToLowerInvariant() == request?.Sink?.Trim().ToLowerInvariant();

        // Setup key filters — use TryIdOf to avoid permanently allocating unknown
        // addresses in the global AddressIdPool (M2: unbounded memory growth).
        // Addresses not already in the pool can't match any graph node.
        var toTokensFilter = ResolveFilterAddresses(request?.ToTokens);
        var fromTokensFilter = ResolveFilterAddresses(request?.FromTokens);
        var excludedFromTokensFilter = ResolveFilterAddresses(request?.ExcludedFromTokens);
        var excludedToTokensFilter = ResolveFilterAddresses(request?.ExcludedToTokens);

        // Whether the caller explicitly asked to constrain delivery to a set of tokens. Read from the
        // raw request, NOT from toTokensFilter — so it stays true even when every requested address is
        // unresolvable at this block (e.g. a token that does not exist yet). This distinguishes
        // "no toTokens given" (unconstrained) from "toTokens given but unsatisfiable" (must be no path).
        var toTokensExplicit = (request?.ToTokens?.Count ?? 0) > 0;

        // STEP 1h: Expand filters with wrapper IDs when withWrap is enabled.
        // User-provided filters contain avatar addresses, but wrapped balances use wrapper
        // contract addresses as token IDs. Without expansion, inclusion filters (toTokens,
        // fromTokens) block wrapped tokens, and exclusion filters (excludedFromTokens,
        // excludedToTokens) fail to exclude them.
        if (request?.WithWrap ?? false)
        {
            var wrapperMap = capacityGraph.WrapperToAvatar;
            if (wrapperMap.Count > 0)
            {
                ExpandFilterWithWrapperIds(toTokensFilter, wrapperMap, "toTokensFilter");
                ExpandFilterWithWrapperIds(fromTokensFilter, wrapperMap, "fromTokensFilter");
                ExpandFilterWithWrapperIds(excludedFromTokensFilter, wrapperMap, "excludedFromTokensFilter");
                ExpandFilterWithWrapperIds(excludedToTokensFilter, wrapperMap, "excludedToTokensFilter");
            }
        }

        // STEP 2: Validate source and sink are not groups or router
        int? sourceId = !string.IsNullOrWhiteSpace(request?.Source) ? AddressIdPool.IdOf(request.Source) : null;
        int? sinkId = !string.IsNullOrWhiteSpace(request?.Sink) ? AddressIdPool.IdOf(request.Sink) : null;

        // Defense-in-depth: sanitize for logging to prevent log forging via embedded newlines.
        // FlowRequest.Source/Sink are already sanitized at construction in FindPathHandler.BuildRequest,
        // but guard here too since GraphFactory is a public API.
        var logSource = request?.Source?.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var logSink = request?.Sink?.Replace("\r", string.Empty).Replace("\n", string.Empty);

        if (sourceId != null && capacityGraph.IsGroup(sourceId.Value))
        {
            _logger.LogWarning("Rejected source '{Source}': address is a group", logSource);
            throw new ArgumentException("Invalid source address.");
        }

        if (sinkId != null && capacityGraph.IsGroup(sinkId.Value))
        {
            _logger.LogWarning("Rejected sink '{Sink}': address is a group", logSink);
            throw new ArgumentException("Invalid sink address.");
        }

        if (sourceId != null && capacityGraph.IsRouter(sourceId.Value))
        {
            _logger.LogWarning("Rejected source '{Source}': address is the router", logSource);
            throw new ArgumentException("Invalid source address.");
        }

        if (sinkId != null && capacityGraph.IsRouter(sinkId.Value))
        {
            _logger.LogWarning("Rejected sink '{Sink}': address is the router", logSink);
            throw new ArgumentException("Invalid sink address.");
        }

        // STEP 2a: Filter ToTokens to only include tokens the sink actually trusts
        // This fixes Issue 3: Multiple ToTokens where sink doesn't trust all of them
        // Previously, flow would go to intermediaries for untrusted tokens, resulting in maxFlow=0
        // NOTE: Only apply in invitation mode (source ≠ sink), not swap mode where virtual sink handles routing
        if (!sourceEqualsSink && toTokensFilter.Count > 0 && sinkId.HasValue &&
            mergedTrust.TryGetValue(sinkId.Value, out var sinkTrustedTokens))
        {
            var originalCount = toTokensFilter.Count;
            var effectiveToTokens = toTokensFilter
                .Where(token => IsTokenTrustedBy(
                    sinkTrustedTokens,
                    token,
                    request?.WithWrap ?? false,
                    capacityGraph.WrapperToAvatar))
                .ToHashSet();

            if (effectiveToTokens.Count < originalCount)
            {
                var skippedCount = originalCount - effectiveToTokens.Count;
                _logger.LogDebug("[quantizedMode] Filtered {Skipped} ToTokens not trusted by sink (kept {Kept} of {Total})",
                    skippedCount, effectiveToTokens.Count, originalCount);
            }

            toTokensFilter = effectiveToTokens;
        }
        else if (!sourceEqualsSink && toTokensFilter.Count > 0 && sinkId.HasValue)
        {
            // Sink doesn't trust ANY tokens - clear the filter (will result in no path)
            _logger.LogWarning("[quantizedMode] Sink trusts no tokens, clearing ToTokens filter");
            toTokensFilter = new HashSet<int>();
        }

        // STEP 2a1: An explicit toTokens that narrowed to empty is UNSATISFIABLE — the sink can
        // receive none of the requested tokens (or none resolve at this block). Downstream, an empty
        // filter means "no constraint", so without this the pathfinder would silently fall back to
        // delivering an unrequested token (e.g. personal CRC for a "pay only in this group token"
        // request) — a path that can revert on-chain. Insert a sentinel id that matches no real token
        // (AddressIdPool ids are non-negative) so the sink-token edge filters drop every real edge to
        // the sink, yielding no path. Scoped to invitation flow (source ≠ sink); swap mode (source ==
        // sink) routes toTokens through the virtual sink and is left untouched.
        if (toTokensExplicit && toTokensFilter.Count == 0 && !sourceEqualsSink && sinkId.HasValue)
        {
            const int unsatisfiableToTokenSentinel = -1;
            _logger.LogInformation(
                "toTokens ({Count}) all unsatisfiable for sink {Sink}; returning no path",
                request?.ToTokens?.Count ?? 0, logSink);
            toTokensFilter = new HashSet<int> { unsatisfiableToTokenSentinel };
        }

        // STEP 2a2: Auto-discover tokens for quantizedMode when no ToTokens specified
        // This fixes Issue 2: Without ToTokens, flow spreads across many tokens, each < 96 CRC
        // Solution: Find tokens where source has 96+ CRC balance AND sink trusts the token
        const long QuantizedMinBalance = 96_000_000L; // 96 CRC in truncated form
        bool isQuantizedInvitationMode = (request?.QuantizedMode ?? false) && !sourceEqualsSink;

        if (isQuantizedInvitationMode && toTokensFilter.Count == 0 && sourceId.HasValue && sinkId.HasValue &&
            mergedTrust.TryGetValue(sinkId.Value, out var sinkTrusts))
        {
            // Find source's token balances that: (1) sink trusts AND (2) have 96+ CRC
            var candidateTokens = new List<(int Token, long Balance)>();

            foreach (var balanceNode in balanceGraph.BalanceNodes.Values)
            {
                // Only consider source's balances
                if (balanceNode.Holder != sourceId.Value)
                    continue;

                // Skip wrapped balances unless WithWrap is enabled — their supply edges
                // won't be created (line 708), so adding them to toTokensFilter would
                // produce silent zero-flow
                if (balanceNode.IsWrapped && !(request?.WithWrap ?? false))
                    continue;

                // Check if sink trusts this token AND balance is sufficient
                if (IsTokenTrustedBy(sinkTrusts, balanceNode.Token, request?.WithWrap ?? false, capacityGraph.WrapperToAvatar)
                    && balanceNode.Amount >= QuantizedMinBalance)
                {
                    candidateTokens.Add((balanceNode.Token, balanceNode.Amount));
                }
            }

            if (candidateTokens.Count > 0)
            {
                // Sort by balance descending, pick tokens with highest balances
                // This focuses flow through tokens most likely to deliver 96+ CRC
                var autoDiscoveredTokens = candidateTokens
                    .OrderByDescending(t => t.Balance)
                    .Select(t => t.Token)
                    .ToHashSet();

                toTokensFilter = autoDiscoveredTokens;
                _logger.LogDebug("[quantizedMode] Auto-discovered {Count} tokens with 96+ CRC liquidity for invitation flow", autoDiscoveredTokens.Count);
            }
            else
            {
                _logger.LogWarning("[quantizedMode] No tokens found with 96+ CRC where source has balance and sink trusts");
            }
        }

        // STEP 2b: Create a virtual sink if needed
        // In quantizedMode with source==sink, we also need a virtual sink even without explicit toTokens
        bool isQuantizedSwapMode = (request?.QuantizedMode ?? false) && sourceEqualsSink;
        bool needsVirtualSink = sourceId != null && sourceEqualsSink && (toTokensFilter.Count > 0 || isQuantizedSwapMode);

        if (needsVirtualSink)
        {
            // Wrapped tokens from simulated balances — currently always empty in production
            // (wrapper data excluded from queries). Kept for future support of wrapped token
            // simulation, where callers inject ERC20-wrapped balances into the graph.
            var wrappedTokensInSim = simulated
                .Where(x => x.IsWrapped)
                .Select(x => x.TokenId)
                .ToHashSet();

            // If quantizedMode but no toTokens specified, use all tokens trusted by source
            var effectiveToTokensFilter = toTokensFilter;
            if (isQuantizedSwapMode && toTokensFilter.Count == 0 &&
                mergedTrust.TryGetValue(sourceId!.Value, out var trustedBySource))
            {
                effectiveToTokensFilter = trustedBySource.ToHashSet();
            }

            (virtualSinkAddress, virtualSinkTrustedTokens) = CreateVirtualSink(
                capacityGraph,
                sourceId!.Value,
                effectiveToTokensFilter,
                excludedToTokensFilter,
                balanceGraph,
                wrappedTokensInSim,
                mergedTrust,
                request?.WithWrap ?? false,
                isQuantizedSwapMode);
        }

        // STEP 3: Add pooled H→TokenPool edges from snapshot balances (applying filters)
        AddHolderToTokenEdges_Pooled(
            capacityGraph,
            balanceGraph,
            request,
            sourceEqualsSink,
            fromTokensFilter,
            toTokensFilter,
            excludedFromTokensFilter);

        // STEP 3b: Add pooled H→TokenPool edges from simulated balances (filtered)
        AddSimulatedBalances_Pooled(
            capacityGraph,
            simulated,
            request,
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
        // Groups can now receive tokens directly
        var (totalGroupTokenEdges, routerFilteredCount) = AddTokenPoolOutEdges(
            capacityGraph,
            mergedTrust,
            virtualSinkAddress,
            virtualSinkTrustedTokens,
            sinkId,
            sourceId,
            toTokensFilter,
            excludedToTokensFilter);
        capacityGraph.TotalGroupTokenEdges = totalGroupTokenEdges;
        capacityGraph.RouterFilteredEdges = routerFilteredCount;

        // STEP 8: Add Group minting edges (Group → Avatar with group token)
        AddGroupMintingEdges(
            capacityGraph,
            mergedTrust,
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

            var trusterNorm = st.Truster.Trim().ToLowerInvariant();
            var trusteeNorm = st.Trustee.Trim().ToLowerInvariant();
            if (!IsValidEthereumAddress(trusterNorm) || !IsValidEthereumAddress(trusteeNorm))
            {
                _logger.LogWarning("Invalid address in simulated trust: truster='{Truster}' trustee='{Trustee}' (skipped)", st.Truster, st.Trustee);
                continue;
            }

            int trusterId = AddressIdPool.IdOf(trusterNorm);
            int trusteeId = AddressIdPool.IdOf(trusteeNorm);

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

    private List<SimulatedBalance> NormalizeSimulatedBalances(List<Circles.Common.Dto.SimulatedBalance>? raw)
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

            var holderNorm = sb.Holder.Trim().ToLowerInvariant();
            var tokenNorm = sb.Token.Trim().ToLowerInvariant();
            if (!IsValidEthereumAddress(holderNorm) || !IsValidEthereumAddress(tokenNorm))
            {
                _logger.LogWarning("Invalid address in simulated balance: holder='{Holder}' token='{Token}' (skipped)", sb.Holder, sb.Token);
                continue;
            }

            int holderId = AddressIdPool.IdOf(holderNorm);
            int tokenId = AddressIdPool.IdOf(tokenNorm);

            if (!UInt256.TryParse(sb.Amount, out var parsedAmount))
            {
                _logger.LogWarning("Invalid amount in simulated balance: '{Amount}' (skipped)", sb.Amount);
                continue;
            }
            var amt = CirclesConverter.TruncateToInt64(parsedAmount);
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

    // Load groups and router — exceptions propagate to caller (CreateCapacityGraph)
    private void LoadGroupsAndTrackRouter(CapacityGraph capacityGraph, CachedGroupData? cached = null)
    {
        // Track router node ID for post-processing (inserting router between Avatar->Group transfers)
        // Note: Router node is added to graph but has no edges during graph construction
        int routerId = AddressIdPool.IdOf(routerAddress);
        capacityGraph.SetRouter(routerId);

        // Use cached data if available (avoids DB queries per filtered request)
        if (cached != null)
        {
            foreach (var groupId in cached.GroupNodes)
                capacityGraph.AddGroup(groupId);
            if (cached.GroupRouters != null)
            {
                foreach (var (groupId, groupRouterId) in cached.GroupRouters)
                {
                    if (cached.GroupNodes.Contains(groupId))
                        capacityGraph.SetGroupRouter(groupId, groupRouterId);
                }
            }
            if (cached.ScoreGroupMintLimits != null)
            {
                foreach (var (key, limit) in cached.ScoreGroupMintLimits)
                    capacityGraph.ScoreGroupMintLimits[key] = limit;
            }
            if (cached.OperatorApprovals != null)
            {
                foreach (var (cachedRouterId, operators) in cached.OperatorApprovals)
                    capacityGraph.OperatorApprovals[cachedRouterId] = new HashSet<int>(operators);
            }
            if (cached.ScoreRouterIds != null)
            {
                foreach (var scoreRouterId in cached.ScoreRouterIds)
                    capacityGraph.ScoreRouterIds.Add(scoreRouterId);
            }
            foreach (var orgId in cached.OrganizationNodes)
                capacityGraph.OrganizationNodes.Add(orgId);
            foreach (var (groupId, tokens) in cached.GroupTrustedTokens)
            {
                capacityGraph.GroupTrustedTokens[groupId] = new HashSet<int>(tokens);
                foreach (var tokenId in tokens)
                    capacityGraph.AddAvatar(tokenId);  // Ensure group-trusted tokens are valid graph nodes
            }
            _logger.LogDebug("Used cached group data: {GroupCount} groups, {OrgCount} orgs, {TrustCount} group-trust entries",
                cached.GroupNodes.Count, cached.OrganizationNodes.Count, cached.GroupTrustedTokens.Count);
            return;
        }

        // Load groups from DB
        var groups = loadGraph.LoadGroups().ToList();
        var loadedGroupIds = new HashSet<int>();
        foreach (var groupAddress in groups)
        {
            int groupId = AddressIdPool.IdOf(groupAddress.ToLowerInvariant());
            capacityGraph.AddGroup(groupId);
            loadedGroupIds.Add(groupId);
        }

        foreach (var (groupAddress, groupRouterAddress) in loadGraph.LoadGroupRouters())
        {
            int groupId = AddressIdPool.IdOf(groupAddress.ToLowerInvariant());
            if (!loadedGroupIds.Contains(groupId))
                continue;

            int groupRouterId = AddressIdPool.IdOf(groupRouterAddress.ToLowerInvariant());
            capacityGraph.SetGroupRouter(groupId, groupRouterId);
        }

        foreach (var (groupAddress, collateralToken, availableLimit) in loadGraph.LoadScoreGroupMintLimits())
        {
            if (!UInt256.TryParse(availableLimit, out var parsedLimit))
                continue;

            int groupId = AddressIdPool.IdOf(groupAddress.ToLowerInvariant());
            int tokenId = AddressIdPool.IdOf(collateralToken.ToLowerInvariant());
            capacityGraph.ScoreGroupMintLimits[(groupId, tokenId)] = CirclesConverter.TruncateToInt64(parsedLimit);
        }

        // Load the canonical score-router set from CrcV2_ScoreGroup.GroupInitialized.
        // The pathMintRouter field is immutable post-init, so this is the source of truth
        // for "is this address a score router" — independent of whether mint-limit rows
        // or operator approvals exist yet (both lag the initialize event).
        foreach (var scoreRouterAddress in loadGraph.LoadScoreRouters())
        {
            if (string.IsNullOrWhiteSpace(scoreRouterAddress))
                continue;
            int scoreRouterId = AddressIdPool.IdOf(scoreRouterAddress.ToLowerInvariant());
            capacityGraph.ScoreRouterIds.Add(scoreRouterId);
        }

        // A score group must deploy its own pathMintRouter. If one is ever
        // initialized with the standard group router, IsScoreGroup excludes it
        // (otherwise every regular group would be misflagged and silently
        // stripped). Surface the misconfiguration loudly — the wrap-only guard
        // is inert for that group until it is redeployed with a dedicated router.
        if (capacityGraph.RouterNode is { } stdRouterId
            && capacityGraph.ScoreRouterIds.Contains(stdRouterId))
        {
            _logger.LogError(
                "Score router collision: a ScoreGroup.GroupInitialized event uses the standard " +
                "group router {Router} as pathMintRouter. IsScoreGroup excludes the standard " +
                "router, so the wrapped-only guard is INERT for any such group. Redeploy the " +
                "score group with a dedicated ScoreGroupMintRouter.",
                AddressIdPool.StringOf(stdRouterId));
        }

        // Load ERC-1155 operator approvals granted BY the known group routers.
        // These gate Avatar→Router edges: Hub.operateFlowMatrix reverts if the caller
        // (operator/msg.sender) is not approved by the Router that holds tokens on the path.
        var routerAddresses = capacityGraph.GroupRouters.Values
            .Select(AddressIdPool.StringOf)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        if (routerAddresses.Count > 0)
        {
            foreach (var (account, op) in loadGraph.LoadOperatorApprovals(routerAddresses))
            {
                int approvingRouterId = AddressIdPool.IdOf(account.ToLowerInvariant());
                int operatorId = AddressIdPool.IdOf(op.ToLowerInvariant());
                if (!capacityGraph.OperatorApprovals.TryGetValue(approvingRouterId, out var approved))
                {
                    approved = new HashSet<int>();
                    capacityGraph.OperatorApprovals[approvingRouterId] = approved;
                }
                approved.Add(operatorId);
            }
        }

        // Load organizations from DB (needed for canary source-type filtering)
        foreach (var orgAddress in loadGraph.LoadOrganizations())
        {
            int orgId = AddressIdPool.IdOf(orgAddress.ToLowerInvariant());
            capacityGraph.OrganizationNodes.Add(orgId);
        }

        // Load group trust relationships from DB
        var groupTrusts = loadGraph.LoadGroupTrusts().ToList();
        foreach (var (groupAddress, trustedToken) in groupTrusts)
        {
            int groupId = AddressIdPool.IdOf(groupAddress.ToLowerInvariant());
            int tokenId = AddressIdPool.IdOf(trustedToken.ToLowerInvariant());

            if (!capacityGraph.GroupTrustedTokens.TryGetValue(groupId, out var trustedSet))
            {
                trustedSet = new HashSet<int>();
                capacityGraph.GroupTrustedTokens[groupId] = trustedSet;
            }

            trustedSet.Add(tokenId);
            capacityGraph.AddAvatar(tokenId);  // Ensure group-trusted tokens are valid graph nodes
        }
    }

    // Load ERC20 wrapper→avatar reverse mappings for DTO output resolution.
    // Also populates InflationaryWrappers for the canary's unit-discrimination logic.
    private void LoadWrapperMappings(CapacityGraph capacityGraph, CachedGroupData? cached = null)
    {
        if (cached != null)
        {
            foreach (var (wrapperId, avatarId) in cached.WrapperToAvatar)
                capacityGraph.WrapperToAvatar[wrapperId] = avatarId;
            if (cached.InflationaryWrappers != null)
            {
                foreach (var wrapperId in cached.InflationaryWrappers)
                    capacityGraph.InflationaryWrappers.Add(wrapperId);
            }
            _logger.LogDebug("Used cached wrapper mappings: {Count} entries ({Inflationary} inflationary)",
                cached.WrapperToAvatar.Count, capacityGraph.InflationaryWrappers.Count);
            return;
        }

        foreach (var (wrapperAddr, avatarAddr, circlesType) in loadGraph.LoadWrapperMappings())
        {
            int wrapperId = AddressIdPool.IdOf(wrapperAddr.ToLowerInvariant());
            int avatarId = AddressIdPool.IdOf(avatarAddr.ToLowerInvariant());
            capacityGraph.WrapperToAvatar[wrapperId] = avatarId;
            if (circlesType == CirclesType.InflationaryCircles)
                capacityGraph.InflationaryWrappers.Add(wrapperId);
        }

        _logger.LogDebug("Loaded {Count} wrapper→avatar mappings from DB ({Inflationary} inflationary)",
            capacityGraph.WrapperToAvatar.Count, capacityGraph.InflationaryWrappers.Count);
    }

    // Load consented flow flags — exceptions propagate to caller (CreateCapacityGraph)
    private void LoadConsentedFlowFlags(CapacityGraph capacityGraph, CachedGroupData? cached = null)
    {
        // Use cached data if available (avoids 1 DB query per filtered request)
        if (cached != null)
        {
            capacityGraph.ConsentedAvatars = new HashSet<int>(cached.ConsentedAvatars);
            _logger.LogDebug("Used cached {Count} avatars with consented flow enabled", cached.ConsentedAvatars.Count);
            return;
        }

        var consentedFlags = loadGraph.LoadConsentedFlowFlags()
            .Where(x => x.HasConsentedFlow)
            .Select(x => AddressIdPool.IdOf(x.Avatar.ToLowerInvariant()))
            .ToHashSet();

        capacityGraph.ConsentedAvatars = consentedFlags;

        _logger.LogDebug("Loaded {Count} avatars with consented flow enabled", consentedFlags.Count);
    }

    private void AddHolderToTokenEdges_Pooled(
        CapacityGraph g,
        BalanceGraph snapshot,
        FlowRequest? req,
        bool sourceEqualsSink,
        HashSet<int> fromTokensFilter,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedFromTokensFilter)
    {
        int? sourceId = !string.IsNullOrWhiteSpace(req?.Source) ? AddressIdPool.IdOf(req.Source!) : null;

        foreach (var bn in snapshot.BalanceNodes.Values)
        {
            // Skip if holder is Router or Group (they don't use token pools)
            if (g.IsRouter(bn.Holder) || g.IsGroup(bn.Holder))
                continue;

            bool isSource = sourceId.HasValue && bn.Holder == sourceId.Value;

            // keep all your existing filters verbatim:
            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 && toTokensFilter.Contains(bn.Token)) continue;
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(bn.Token)) continue;
            if (isSource && excludedFromTokensFilter.Count > 0 && excludedFromTokensFilter.Contains(bn.Token)) continue;
            if (bn.IsWrapped && !(req?.WithWrap ?? false)) continue;
            if (bn.IsWrapped && !isSource) continue;
            // ScoreGroup CRC is wrapped-only: the unwrapped ERC1155 must never be a
            // routable holder balance (no source spend, no transient forwarding). It
            // may exist only on the terminal Group→sink mint edge.
            if (g.IsScoreGroup(bn.Token)) continue;

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
        FlowRequest? req,
        bool sourceEqualsSink,
        HashSet<int> fromTokensFilter,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedFromTokensFilter)
    {
        int? sourceId = !string.IsNullOrWhiteSpace(req?.Source) ? AddressIdPool.IdOf(req.Source!) : null;

        foreach (var sb in simulated)
        {
            // Skip if holder is Router or Group
            if (g.IsRouter(sb.HolderId) || g.IsGroup(sb.HolderId))
                continue;

            bool isSource = sourceId.HasValue && sb.HolderId == sourceId.Value;

            if (sourceEqualsSink && isSource && toTokensFilter.Count > 0 &&
                toTokensFilter.Contains(sb.TokenId)) continue;
            if (isSource && fromTokensFilter.Count > 0 && !fromTokensFilter.Contains(sb.TokenId)) continue;
            if (isSource && excludedFromTokensFilter.Count > 0 &&
                excludedFromTokensFilter.Contains(sb.TokenId)) continue;

            if (sb.IsWrapped && !(req?.WithWrap ?? false)) continue;
            if (sb.IsWrapped && !isSource) continue;
            // ScoreGroup CRC is wrapped-only — never a routable (simulated) balance.
            if (g.IsScoreGroup(sb.TokenId)) continue;

            g.AddTokenNode(sb.TokenId);
            int pool = AddressIdPool.TokenPoolIdOf(sb.TokenId);

            g.AddCapacityEdge(sb.HolderId, pool, sb.TokenId, sb.Amount);
        }
    }

    // Modified version of AddTokenPoolOutEdges that allows Groups to receive tokens directly.
    // Returns (totalGroupTokenEdges, routerFilteredCount) for metrics.
    private (int TotalGroupTokenEdges, int RouterFilteredCount) AddTokenPoolOutEdges(
        CapacityGraph g,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        int? virtualSink,
        HashSet<int> virtualSinkTrustedTokens,
        int? sinkId,
        int? sourceId,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter)
    {
        // Build token -> list of avatars who trust that token
        var tokenToAvatars = new Dictionary<int, List<int>>();

        foreach (var (truster, trustedTokens) in accountTrusts)
        {
            // Skip Router - it doesn't directly receive from pools
            if (g.IsRouter(truster))
                continue;

            // Groups CAN be trusters now - they receive tokens directly

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

        // Groups can receive tokens they trust directly from token pools,
        // but ONLY if the Router also trusts that token. Hub.sol line 665
        // checks trustMarkers[router][token] for every Avatar→Router edge
        // in operateFlowMatrix, so tokens not trusted by the Router would
        // revert on-chain even though the pathfinder found a valid flow.
        int routerFilteredCount = 0;
        int totalGroupTokenEdges = 0;
        int approvalFilteredCount = 0;
        foreach (var groupId in g.GroupNodes)
        {
            if (g.GroupTrustedTokens.TryGetValue(groupId, out var trustedTokens))
            {
                var groupRouter = g.RouterForGroup(groupId);
                accountTrusts.TryGetValue(groupRouter, out var routerTrusts);
                if (routerTrusts == null)
                    _logger.LogWarning("Router node {Router} has no trust entries in accountTrusts — group collateral edges for {Group} will be blocked",
                        AddressIdPool.StringOf(groupRouter), AddressIdPool.StringOf(groupId));

                // Score-group routers require ERC-1155 approval from the Router for the
                // operator (msg.sender of operateFlowMatrix) before Hub will execute the
                // Router→Group hop. The pathfinder treats the path source as that operator.
                // When sourceId is provided but unapproved, skip every collateral edge
                // through this group's router — the resulting path would revert on-chain.
                // When sourceId is null (base snapshot), the gate cannot be evaluated and
                // the edges are inherently source-dependent — strip them unconditionally so
                // the shared snapshot can't emit reverting paths. Per-request filtered
                // builds reconstruct these edges with proper source context.
                // Membership in ScoreRouterIds is the canonical signal — derived from
                // CrcV2_ScoreGroup.GroupInitialized.pathMintRouter — so freshly-initialized
                // groups with no mint-limit rows yet are still recognized and gated.
                bool isScoreRouter = g.ScoreRouterIds.Contains(groupRouter);
                bool operatorApproved =
                    !isScoreRouter
                    || (sourceId.HasValue
                        && g.OperatorApprovals.TryGetValue(groupRouter, out var approvedOps)
                        && approvedOps.Contains(sourceId.Value));

                foreach (var token in trustedTokens)
                {
                    totalGroupTokenEdges++;

                    // Fail-closed: if router trusts are unknown, block the edge.
                    // Missing router trust data would otherwise allow invalid paths
                    // that revert on-chain (the same bug this filter prevents).
                    if (routerTrusts == null || !routerTrusts.Contains(token))
                    {
                        routerFilteredCount++;
                        continue;
                    }

                    if (g.ScoreGroupMintLimits.TryGetValue((groupId, token), out var availableLimit) &&
                        availableLimit <= 0)
                    {
                        continue;
                    }

                    // approveCRC gate (ERC-1155 operator approval Router→source).
                    if (!operatorApproved)
                    {
                        approvalFilteredCount++;
                        continue;
                    }

                    if (!tokenToAvatars.TryGetValue(token, out var list))
                        tokenToAvatars[token] = list = new List<int>();
                    if (!list.Contains(groupId))
                        list.Add(groupId);
                }
            }
        }

        if (routerFilteredCount > 0)
            _logger.LogInformation("Filtered {Count} group collateral edges where Router does not trust the token", routerFilteredCount);
        if (approvalFilteredCount > 0)
            _logger.LogInformation("Filtered {Count} group collateral edges where source is not an approved operator of the score router", approvalFilteredCount);

        foreach (var (token, acceptors) in tokenToAvatars)
        {
            int pool = AddressIdPool.TokenPoolIdOf(token);
            if (!g.Nodes.ContainsKey(pool)) continue; // no supply -> no out-edges

            // Defense-in-depth safety net (normally a no-op): a score-group CRC
            // pool node is never created in the first place (guards in the
            // holder/simulated balance loops skip it), so this branch is not the
            // primary enforcement — it only matters if those upstream guards are
            // ever weakened. Collateral mint inflow (pool(collateral)→group) is
            // unaffected: its token is member collateral, never a score-group id.
            if (g.IsScoreGroup(token)) continue;

            foreach (var a in acceptors)
            {
                var capacity = g.ScoreGroupMintLimits.TryGetValue((a, token), out var availableLimit)
                    ? availableLimit
                    : long.MaxValue;
                if (capacity <= 0)
                    continue;

                g.AddCapacityEdge(pool, a, token, capacity);
            }
        }

        // Virtual sink edges (swap mode): TokenPool(token) -> virtualSink, OR
        // Group → virtualSink for group tokens that have no existing supply.
        //
        // When the target token in a source==sink swap is a group's own CRC and
        // no avatar holds it yet (= no TokenPool node), the group itself is the
        // implicit issuer via Hub.groupMint inside operateFlowMatrix. Without a
        // direct Group → virtualSink edge the virtual sink has zero inbound
        // edges and gets pruned at line ~502, yielding maxFlow=0 for legitimate
        // self-mint paths. The mint cap is still enforced upstream by the
        // pool_collateral → group edges (capacity = ScoreGroupMintLimits[(group,
        // collateral)]) added above, so this fallback never inflates allowable
        // flow beyond the cached cap.
        if (virtualSink != null)
        {
            foreach (var t in virtualSinkTrustedTokens)
            {
                int pool = AddressIdPool.TokenPoolIdOf(t);
                if (g.Nodes.ContainsKey(pool))
                {
                    // Defense-in-depth: a score-group CRC pool node must never
                    // exist (upstream guards prevent it). If one ever did, do NOT
                    // route it transiently via pool→virtualSink — fall through to
                    // the Group→virtualSink self-mint terminal, the only permitted
                    // appearance of the unwrapped score-group ERC1155.
                    if (!g.IsScoreGroup(t))
                    {
                        g.AddCapacityEdge(pool, virtualSink.Value, t, long.MaxValue);
                        continue;
                    }
                }

                if (g.IsGroup(t))
                {
                    g.AddCapacityEdge(t, virtualSink.Value, t, long.MaxValue);
                }
            }
        }

        return (totalGroupTokenEdges, routerFilteredCount);
    }

    // Add Group → Avatar edges for group token minting
    private void AddGroupMintingEdges(
        CapacityGraph g,
        IReadOnlyDictionary<int, HashSet<int>> accountTrusts,
        int? sinkId,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter)
    {
        // Observability for the hard-wired (no kill-switch) wrap-only guard: a
        // sudden spike here vs. the known score-group count signals a misfire
        // (e.g. the standard-router collision IsScoreGroup guards against).
        int scoreGroupNonSinkSkipped = 0;
        var scoreGroupsGuarded = new HashSet<int>();

        foreach (var groupId in g.GroupNodes)
        {
            // Each group mints its own token (the group address IS the token address)
            int groupToken = groupId;

            // Find avatars that trust this group's token
            var trustingAvatars = new List<int>();
            foreach (var (truster, trustedTokens) in accountTrusts)
            {
                // Skip other groups and router
                if (g.IsGroup(truster) || g.IsRouter(truster))
                    continue;

                if (trustedTokens.Contains(groupToken))
                {
                    bool isSink = sinkId.HasValue && truster == sinkId.Value;
                    // ScoreGroup CRC is wrapped-only: the unwrapped ERC1155 may be
                    // minted ONLY directly to the final recipient (sink), who wraps
                    // it off-graph. No Group→intermediary delivery, no transient
                    // forwarding. In the sink-less shared snapshot this fails closed
                    // (no score-group mint edges); per-request filtered builds
                    // reconstruct them with proper sink context.
                    if (g.IsScoreGroup(groupId) && !isSink)
                    {
                        scoreGroupNonSinkSkipped++;
                        scoreGroupsGuarded.Add(groupId);
                        continue;
                    }
                    if (isSink && toTokensFilter.Count > 0 && !toTokensFilter.Contains(groupToken))
                        continue;
                    if (isSink && excludedToTokensFilter.Count > 0 && excludedToTokensFilter.Contains(groupToken))
                        continue;

                    trustingAvatars.Add(truster);
                }
            }

            // Add Group → Avatar edges for group token
            foreach (var avatar in trustingAvatars)
            {
                g.AddCapacityEdge(groupId, avatar, groupToken, long.MaxValue);
            }
        }

        if (scoreGroupNonSinkSkipped > 0)
            _logger.LogInformation(
                "Wrapped-only guard: dropped {Count} non-sink Group→avatar mint edges across {Groups} score group(s)",
                scoreGroupNonSinkSkipped, scoreGroupsGuarded.Count);
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

        // every token that is trusted by somebody — only if registered
        // (defense-in-depth: SQL queries should already filter, but guard against leaks)
        // Fail-closed: if RegisteredAvatarIds is empty, no trusted tokens are added.
        var registered = capacityGraph.RegisteredAvatarIds;
        if (registered.Count == 0)
        {
            _logger.LogError("RegisteredAvatarIds is empty — no trusted tokens will be added to graph");
        }

        foreach (var trustedSet in trustLookup.Values)
        {
            foreach (var tokenId in trustedSet)
            {
                if (registered.Contains(tokenId))
                {
                    capacityGraph.AddAvatar(tokenId);
                }
            }
        }
    }

    /// <summary>
    /// Resolves user-supplied filter addresses to AddressIdPool IDs without allocating
    /// new entries for unknown addresses (prevents unbounded memory growth from attacker input).
    /// </summary>
    private static HashSet<int> ResolveFilterAddresses(IReadOnlyList<string>? addresses)
    {
        if (addresses == null || addresses.Count == 0)
            return new HashSet<int>();

        var result = new HashSet<int>(addresses.Count);
        foreach (var addr in addresses)
        {
            if (AddressIdPool.TryIdOf(addr, out var id))
                result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Validates that a string is a valid Ethereum address format.
    /// Expects: 0x followed by exactly 40 hexadecimal characters.
    /// </summary>
    public static bool IsValidEthereumAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
            return false;

        if (!address.StartsWith("0x") || address.Length != 42)
            return false;

        // Check remaining 40 characters are valid hex
        for (int i = 2; i < address.Length; i++)
        {
            char c = address[i];
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    private (int address, HashSet<int> trustedTokens) CreateVirtualSink(
        CapacityGraph capacityGraph,
        int sourceAddress,
        HashSet<int> toTokensFilter,
        HashSet<int> excludedToTokensFilter,
        BalanceGraph balanceGraph,
        HashSet<int> wrappedTokensInSim,
        IReadOnlyDictionary<int, HashSet<int>> mergedTrust,
        bool withWrap,
        bool quantizedMode = false)
    {
        var virtualSinkAddress = sourceAddress + VirtualSinkSuffix;
        var virtualSinkAddressId = AddressIdPool.IdOf(virtualSinkAddress);

        capacityGraph.AddAvatar(virtualSinkAddressId);
        capacityGraph.VirtualSinkAddress = virtualSinkAddressId;

        // Build a set of wrapped tokens once (snapshot) to avoid O(|toTokens|*|balances|)
        var snapshotWrappedTokens = new HashSet<int>();
        foreach (var bn in balanceGraph.BalanceNodes.Values)
        {
            if (bn.IsWrapped) snapshotWrappedTokens.Add(bn.Token);
        }

        // Get tokens that the source actually trusts
        HashSet<int> sourceTrustedTokens = new HashSet<int>();
        if (mergedTrust.TryGetValue(sourceAddress, out var trustedBySource))
        {
            sourceTrustedTokens = trustedBySource;
        }

        // Collect tokens trusted by virtual sink
        // In regular swap mode: must be in toTokensFilter AND trusted by source
        // In quantizedMode: accept ALL specified tokens (bypass trust check)
        //   because untrusted tokens can still route through intermediaries
        var virtualSinkTrustedTokens = new HashSet<int>();
        foreach (var token in toTokensFilter)
        {
            // Apply excludedToTokensFilter — previously only applied at real sink (AddTokenPoolOutEdges)
            if (excludedToTokensFilter.Count > 0 && excludedToTokensFilter.Contains(token))
                continue;

            // In quantizedMode: accept all specified tokens (trust validation happens post-path)
            // In regular mode: require source trust
            if (!quantizedMode && !IsTokenTrustedBy(sourceTrustedTokens, token, withWrap, capacityGraph.WrapperToAvatar))
            {
                // Source doesn't trust this token and we're not in quantized mode
                continue;
            }

            bool tokenWrappedInSnapshot = snapshotWrappedTokens.Contains(token);
            bool tokenWrappedInSim = wrappedTokensInSim.Contains(token);

            bool shouldSkip = tokenWrappedInSnapshot || tokenWrappedInSim;
            if (shouldSkip)
            {
                continue;
            }

            virtualSinkTrustedTokens.Add(token);
        }

        return (virtualSinkAddressId, virtualSinkTrustedTokens);
    }

    /// <summary>
    /// Expands a token filter to include wrapper IDs for any avatar IDs already in the filter.
    /// This bridges the avatar-address / wrapper-contract-address namespace gap so that
    /// user-provided filters (which use avatar addresses) also match wrapped token flows
    /// (which use wrapper contract addresses as token IDs in the graph).
    /// </summary>
    private void ExpandFilterWithWrapperIds(
        HashSet<int> filter,
        Dictionary<int, int> wrapperToAvatar,
        string filterName)
    {
        if (filter.Count == 0)
            return;

        int added = 0;
        foreach (var (wrapperId, avatarId) in wrapperToAvatar)
        {
            if (filter.Contains(avatarId) && filter.Add(wrapperId))
                added++;
        }

        if (added > 0)
            _logger.LogDebug("Expanded {FilterName} with {Count} wrapper ID(s) for withWrap=true",
                filterName, added);
    }

    private static bool IsTokenTrustedBy(
        HashSet<int> trustedTokens,
        int token,
        bool withWrap,
        IReadOnlyDictionary<int, int> wrapperToAvatar)
    {
        if (trustedTokens.Contains(token))
            return true;

        if (!withWrap)
            return false;

        return wrapperToAvatar.TryGetValue(token, out var underlyingAvatarToken)
            && trustedTokens.Contains(underlyingAvatarToken);
    }

    #endregion
}
