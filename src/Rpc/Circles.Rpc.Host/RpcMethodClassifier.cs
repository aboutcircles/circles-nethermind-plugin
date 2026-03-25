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
    /// Returns true if the method is safe to proxy to Nethermind.
    /// Only read-only Ethereum JSON-RPC prefixes are allowed.
    /// Blocks admin_*, debug_*, personal_*, miner_*, etc. to prevent node compromise.
    /// </summary>
    public static bool IsProxyAllowed(string? method) =>
        method != null && (method.StartsWith("eth_", StringComparison.Ordinal)
            || method.StartsWith("net_", StringComparison.Ordinal)
            || method.StartsWith("web3_", StringComparison.Ordinal));
}
