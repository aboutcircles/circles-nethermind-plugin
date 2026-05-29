namespace Circles.Rpc.Host;

/// <summary>
/// Thrown when the dispatch switch falls through — the requested method is not registered
/// in any case arm. User-facing: surfaces as JSON-RPC <c>-32601 Method not found</c>, or
/// proxies to Nethermind when the name is in the proxy allowlist (e.g. <c>eth_*</c>).
/// </summary>
/// <remarks>
/// Do NOT throw this for internal dispatch drift (e.g. reflection lookup failures inside
/// a dispatched handler) — those are server bugs and should surface as <c>-32603</c>.
/// </remarks>
public class RpcMethodNotFoundException : Exception
{
    public string MethodName { get; }
    public RpcMethodNotFoundException(string methodName)
        : base($"RPC method '{methodName}' not found.")
    {
        MethodName = methodName;
    }
}
