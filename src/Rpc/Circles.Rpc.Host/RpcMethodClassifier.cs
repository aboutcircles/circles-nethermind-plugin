namespace Circles.Rpc.Host;

/// <summary>
/// Classifies JSON-RPC method names for routing decisions.
///
/// Circles methods (circles_*, circlesV2_*, rpc.discover) are handled
/// locally by the RPC host. Proxy-allowed methods (eth_*, net_*, web3_*)
/// are forwarded to Nethermind. Everything else is rejected with -32601.
///
/// Used by both the single-request handler and the batch middleware to
/// ensure consistent routing decisions.
/// </summary>
public static class RpcMethodClassifier
{
    /// <summary>
    /// Returns true if the method should be handled locally by the Circles RPC host.
    /// Matches: circles_*, circlesV2_*, rpc.discover
    /// </summary>
    public static bool IsCirclesMethod(string? method) =>
        method != null && (method.StartsWith("circles_", StringComparison.Ordinal)
            || method.StartsWith("circlesV2_", StringComparison.Ordinal)
            || method == "rpc.discover");

    /// <summary>
    /// Returns a safe label for Prometheus metrics.
    /// Known circles methods and proxy-allowed prefixes pass through.
    /// Unknown methods are bucketed as "unknown_circles", "unknown_proxy", or "unknown"
    /// to prevent label cardinality explosion from crafted method names.
    /// </summary>
    public static string SafeMetricLabel(string? method)
    {
        if (method == null) return "unknown";
        if (KnownCirclesMethods.Contains(method)) return method;
        if (IsProxyAllowed(method)) return method;  // eth_*/net_*/web3_* — bounded by Nethermind's API
        if (IsCirclesMethod(method)) return "unknown_circles";  // matches prefix but not a real method
        return "unknown";
    }

    private static readonly HashSet<string> KnownCirclesMethods = new(StringComparer.Ordinal)
    {
        "rpc.discover",
        "circles_getTotalBalance", "circlesV2_getTotalBalance",
        "circles_getTokenBalances", "circles_getTokenInfo", "circles_getTokenInfoBatch",
        "circles_getAvatarInfo", "circles_getAvatarInfoBatch",
        "circles_getProfileCid", "circles_getProfileCidBatch",
        "circles_getProfileByCid", "circles_getProfileByCidBatch",
        "circles_getProfileByAddress", "circles_getProfileByAddressBatch",
        "circles_searchProfiles",
        "circles_getTrustRelations", "circles_getCommonTrust", "circles_getNetworkSnapshot",
        "circles_getAggregatedTrustRelations", "circles_findGroups",
        "circles_getGroupMembers", "circles_getGroupMemberships",
        "circles_getTransactionHistory", "circles_getTransferData", "circles_getTokenHolders",
        "circlesV2_findPath",
        "circles_getBlockByTimestamp",
        "circles_events", "circles_events_paginated",
        "circles_health", "circles_tables", "circles_query", "circles_paginated_query",
        "circles_getProfileView", "circles_getTrustNetworkSummary",
        "circles_getAggregatedTrustRelationsEnriched",
        "circles_getValidInviters", "circles_getTransactionHistoryEnriched",
        "circles_searchProfileByAddressOrName",
        "circles_getInvitationOrigin", "circles_getAllInvitations",
        "circles_getTrustInvitations", "circles_getEscrowInvitations",
        "circles_getAtScaleInvitations", "circles_getInvitationsFrom",
    };

    /// <summary>
    /// Returns true if the method is safe to proxy to Nethermind.
    /// Only read-only Ethereum JSON-RPC prefixes are allowed.
    /// Blocks admin_*, debug_*, personal_*, miner_*, etc. to prevent node compromise.
    /// </summary>
    public static bool IsProxyAllowed(string? method) =>
        method != null && (method.StartsWith("eth_", StringComparison.Ordinal)
            || method.StartsWith("net_", StringComparison.Ordinal)
            || method.StartsWith("web3_", StringComparison.Ordinal));

}
