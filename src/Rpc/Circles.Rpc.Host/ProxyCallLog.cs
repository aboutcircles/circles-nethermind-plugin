using System.Net;
using System.Text.Json;

namespace Circles.Rpc.Host;

/// <summary>
/// One log line per JSON-RPC call forwarded to Nethermind: method, target address,
/// 4-byte function selector, client IP and User-Agent. The forwarded path logged nothing
/// on success, so finding out which contracts a client was calling needed a packet
/// capture on the node.
///
/// Every logged value is either a fixed placeholder or checked against a strict format
/// (hex address, hex selector, parsed IP, printable User-Agent), so a crafted request
/// cannot write arbitrary text into the log.
/// </summary>
public static class ProxyCallLog
{
    private const int AddressLength = 42;   // 0x + 40 hex
    private const int SelectorLength = 10;  // 0x + 8 hex
    private const int MaxUserAgentLength = 120;

    /// <summary>
    /// The client IP for logging. Caddy replaces any client-sent X-Forwarded-For with the
    /// address it accepted the connection from (no trusted_proxies), and the rpc container
    /// is reachable only on the Docker network, so the first entry is the real client.
    /// In-network callers send no header and get the TCP peer.
    ///
    /// Not used for rate limiting: <see cref="RpcRateLimiter"/> keeps its own key.
    /// </summary>
    public static string ClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var ip))
                return ip.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>The User-Agent cut to 120 characters, non-printable characters replaced by '?', or "-".</summary>
    public static string UserAgent(HttpContext context)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ua))
            return "-";

        if (ua.Length > MaxUserAgentLength)
            ua = ua[..MaxUserAgentLength];

        return string.Create(ua.Length, ua, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = src[i] is >= ' ' and <= '~' ? src[i] : '?';
        });
    }

    /// <summary>
    /// Target address and selector from the params of a forwarded call, "-" where absent:
    /// <list type="bullet">
    /// <item>eth_call, eth_estimateGas, eth_createAccessList: params[0] is a transaction
    /// object with <c>to</c> and <c>data</c> (or <c>input</c>).</item>
    /// <item>eth_getCode, eth_getBalance, eth_getStorageAt, eth_getTransactionCount:
    /// params[0] is the address.</item>
    /// <item>eth_getLogs: params[0].address, a string or an array ("N addresses").</item>
    /// </list>
    /// A Multicall3 call shows Multicall3 and its own selector, not the calls inside it.
    /// </summary>
    public static (string To, string Selector) Describe(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Array || @params.GetArrayLength() == 0)
            return ("-", "-");

        var first = @params[0];
        if (first.ValueKind == JsonValueKind.String)
            return (HexOrDash(first.GetString(), AddressLength), "-");

        if (first.ValueKind != JsonValueKind.Object)
            return ("-", "-");

        var to = "-";
        if (first.TryGetProperty("to", out var toEl) && toEl.ValueKind == JsonValueKind.String)
        {
            to = HexOrDash(toEl.GetString(), AddressLength);
        }
        else if (first.TryGetProperty("address", out var addr))
        {
            to = addr.ValueKind switch
            {
                JsonValueKind.String => HexOrDash(addr.GetString(), AddressLength),
                JsonValueKind.Array => $"{addr.GetArrayLength()} addresses",
                _ => "-"
            };
        }

        var selector = "-";
        if ((first.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            || (first.TryGetProperty("input", out data) && data.ValueKind == JsonValueKind.String))
        {
            var hex = data.GetString();
            if (hex is { Length: >= SelectorLength })
                selector = HexOrDash(hex[..SelectorLength], SelectorLength);
        }

        return (to, selector);
    }

    /// <summary>
    /// Logs one forwarded call. <paramref name="method"/> must already be a safe label
    /// (<see cref="RpcMethodClassifier.SafeMetricLabel"/>). <paramref name="batchSize"/> is 1
    /// for a single request, else the number of items forwarded together.
    /// </summary>
    public static void Write(ILogger logger, string method, JsonElement @params, double elapsedMs,
        string clientIp, string userAgent, int batchSize)
    {
        var (to, selector) = Describe(@params);
        logger.LogInformation(
            "RPC proxy {Method} to={To} selector={Selector} batch={BatchSize} in {ElapsedMs} ms from {ClientIp} ua={UserAgent}",
            method, to, selector, batchSize, Math.Round(elapsedMs, 1), clientIp, userAgent);
    }

    private static string HexOrDash(string? value, int length)
    {
        if (value is null || value.Length != length || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return "-";

        for (var i = 2; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return "-";
        }

        return value.ToLowerInvariant();
    }
}
