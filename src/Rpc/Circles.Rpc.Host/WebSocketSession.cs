using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace Circles.Rpc.Host;

/// <summary>
/// Manages a single client WebSocket connection with support for multiple
/// concurrent subscriptions (both circles_subscribe and eth_subscribe).
///
/// Replaces the old single-message-then-pump pattern with a continuous
/// message loop that dispatches each incoming JSON-RPC message by method.
/// </summary>
public sealed class WebSocketSession : IAsyncDisposable
{
    private const int MaxClientMessageSize = 65_536; // 64KB — generous for subscription JSON-RPC
    private const int MaxSubscriptionsPerSession = 20; // combined circles + eth

    private readonly WebSocket _clientWs;
    private readonly CirclesSubscriptionService _circlesSvc;
    private readonly NethermindWsProxy _nethermindProxy;
    private readonly Settings _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Track subscriptions for cleanup
    private readonly List<string> _circlesSubIds = new();
    private readonly List<string> _ethSubIds = new();
    private readonly object _subListLock = new();

    public WebSocketSession(
        WebSocket clientWs,
        CirclesSubscriptionService circlesSvc,
        NethermindWsProxy nethermindProxy,
        Settings settings,
        ILogger logger)
    {
        _clientWs = clientWs;
        _circlesSvc = circlesSvc;
        _nethermindProxy = nethermindProxy;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Main message loop — reads JSON-RPC messages and dispatches by method.
    /// Runs until the client disconnects or cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        RpcMetrics.ActiveWsSessions.Inc();
        try
        {
            var buffer = new byte[4096];

            while (_clientWs.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? message;
                try
                {
                    message = await ReceiveMessageAsync(buffer, ct);
                }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "Client WebSocket error, ending session");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (message == null)
                    break; // close frame received

                try
                {
                    await DispatchMessageAsync(message, ct);
                }
                catch (WebSocketException)
                {
                    break; // client gone during response send
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error dispatching WebSocket message");
                    // Don't break — keep session alive for other subscriptions
                }
            }
        }
        finally
        {
            RpcMetrics.ActiveWsSessions.Dec();
            await CleanupAsync();
        }
    }

    private async Task<string?> ReceiveMessageAsync(byte[] buffer, CancellationToken ct)
    {
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await _clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (_clientWs.State == WebSocketState.CloseReceived)
                {
                    await _clientWs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            stream.Write(buffer, 0, result.Count);

            if (stream.Length > MaxClientMessageSize)
            {
                _logger.LogWarning("Client message exceeds {MaxSize} bytes, closing", MaxClientMessageSize);
                await _clientWs.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                return null;
            }

            if (result.EndOfMessage)
                break;
        }

        return stream.Length == 0 ? null : Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task DispatchMessageAsync(string message, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, -32700, "Parse error", ct);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;

            var jsonrpc = root.TryGetProperty("jsonrpc", out var jrpc) ? jrpc.GetString() : null;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = root.TryGetProperty("id", out var idEl) ? (JsonElement?)idEl.Clone() : null;

            if (jsonrpc != "2.0" || string.IsNullOrEmpty(method))
            {
                await SendErrorAsync(id, -32600, "Invalid Request", ct);
                return;
            }

            if (string.Equals(method, "circles_subscribe", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCirclesSubscribeAsync(root, id, ct);
            }
            else if (string.Equals(method, "circles_unsubscribe", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCirclesUnsubscribeAsync(root, id, ct);
            }
            else if (string.Equals(method, "eth_subscribe", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEthSubscribeAsync(root, id, ct);
            }
            else if (string.Equals(method, "eth_unsubscribe", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEthUnsubscribeAsync(root, id, ct);
            }
            else
            {
                await SendErrorAsync(id, -32601,
                    $"Method not found: {method}. Only subscribe/unsubscribe methods are supported over WebSocket.",
                    ct);
            }
        }
    }

    private bool TryCheckSubscriptionLimit(out int currentCount)
    {
        lock (_subListLock)
            currentCount = _circlesSubIds.Count + _ethSubIds.Count;
        return currentCount < MaxSubscriptionsPerSession;
    }

    private async Task HandleCirclesSubscribeAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        if (!TryCheckSubscriptionLimit(out _))
        {
            await SendErrorAsync(id, -32000, $"Subscription limit reached ({MaxSubscriptionsPerSession})", ct);
            return;
        }

        var address = ExtractCirclesAddress(root);
        var subId = _circlesSvc.Subscribe(_clientWs, address, _sendLock);

        lock (_subListLock)
            _circlesSubIds.Add(subId);

        RpcMetrics.ActiveCirclesSubscriptions.Inc();
        _logger.LogInformation("circles_subscribe: {SubId} (filter: {Address})", subId, address ?? "*");

        await SendResultAsync(id, subId, ct);
    }

    private async Task HandleCirclesUnsubscribeAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        var subId = ExtractUnsubscribeId(root);
        if (subId == null)
        {
            await SendErrorAsync(id, -32602, "Invalid params: subscription ID required", ct);
            return;
        }

        bool found;
        lock (_subListLock)
            found = _circlesSubIds.Remove(subId);

        if (found)
        {
            _circlesSvc.Unsubscribe(subId);
            RpcMetrics.ActiveCirclesSubscriptions.Dec();
        }

        await SendResultAsync(id, found, ct);
    }

    private async Task HandleEthSubscribeAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        if (!TryCheckSubscriptionLimit(out _))
        {
            await SendErrorAsync(id, -32000, $"Subscription limit reached ({MaxSubscriptionsPerSession})", ct);
            return;
        }

        // Backward compat: eth_subscribe(["circles", ...]) → route to circles (works even when eth proxy is disabled)
        if (IsCirclesCompatSubscription(root))
        {
            _logger.LogWarning("Deprecated: eth_subscribe with 'circles' topic — use circles_subscribe instead");
            await HandleCirclesSubscribeFromEthCompatAsync(root, id, ct);
            return;
        }

        if (!_settings.EthSubscribeEnabled)
        {
            await SendErrorAsync(id, -32601, "eth_subscribe is disabled on this node", ct);
            return;
        }

        if (!root.TryGetProperty("params", out var @params))
        {
            await SendErrorAsync(id, -32602, "Invalid params: eth_subscribe requires params", ct);
            return;
        }

        try
        {
            var proxyId = await _nethermindProxy.SubscribeAsync(_clientWs, _sendLock, @params, ct);

            lock (_subListLock)
                _ethSubIds.Add(proxyId);

            await SendResultAsync(id, proxyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "eth_subscribe failed");
            await SendErrorAsync(id, -32000, "eth_subscribe failed", ct);
        }
    }

    private async Task HandleEthUnsubscribeAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        var subId = ExtractUnsubscribeId(root);
        if (subId == null)
        {
            await SendErrorAsync(id, -32602, "Invalid params: subscription ID required", ct);
            return;
        }

        bool found;
        lock (_subListLock)
            found = _ethSubIds.Remove(subId);

        if (found)
        {
            await _nethermindProxy.UnsubscribeAsync(subId, ct);
        }
        else
        {
            // Fallthrough: compat path adds circles subs via eth_subscribe("circles",...)
            // so eth_unsubscribe should also check the circles list
            lock (_subListLock)
                found = _circlesSubIds.Remove(subId);

            if (found)
            {
                _circlesSvc.Unsubscribe(subId);
                RpcMetrics.ActiveCirclesSubscriptions.Dec();
            }
        }

        await SendResultAsync(id, found, ct);
    }

    /// <summary>
    /// Backward compat: eth_subscribe(["circles", ...]) → route to CirclesSubscriptionService.
    /// </summary>
    private async Task HandleCirclesSubscribeFromEthCompatAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        var address = ExtractCirclesAddressFromEthCompat(root);
        var subId = _circlesSvc.Subscribe(_clientWs, address, _sendLock);

        lock (_subListLock)
            _circlesSubIds.Add(subId);

        RpcMetrics.ActiveCirclesSubscriptions.Inc();
        await SendResultAsync(id, subId, ct);
    }

    private async Task CleanupAsync()
    {
        List<string> circlesIds, ethIds;
        lock (_subListLock)
        {
            circlesIds = new List<string>(_circlesSubIds);
            ethIds = new List<string>(_ethSubIds);
            _circlesSubIds.Clear();
            _ethSubIds.Clear();
        }

        foreach (var subId in circlesIds)
        {
            try
            {
                _circlesSvc.Unsubscribe(subId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unsubscribe circles sub {SubId} during cleanup", subId);
            }
            RpcMetrics.ActiveCirclesSubscriptions.Dec();
        }

        if (ethIds.Count > 0)
        {
            try
            {
                await _nethermindProxy.RemoveClientSubscriptionsAsync(_clientWs, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup eth subscriptions on disconnect");
            }
        }

        _logger.LogInformation("Session cleaned up: {CirclesCount} circles, {EthCount} eth subscriptions removed",
            circlesIds.Count, ethIds.Count);
    }

    #region JSON helpers

    private async Task SendResultAsync(JsonElement? id, object result, CancellationToken ct)
    {
        var response = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["result"] = result };
        if (id is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
            response["id"] = id.Value;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_clientWs.State == WebSocketState.Open)
                await _clientWs.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendErrorAsync(JsonElement? id, int code, string message, CancellationToken ct)
    {
        var response = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new { code, message }
        };
        if (id is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
            response["id"] = id.Value;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_clientWs.State == WebSocketState.Open)
                await _clientWs.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    #endregion

    #region Param extraction

    private static string? ExtractCirclesAddress(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsEl))
            return null;

        // { "params": { "address": "0x..." } }
        if (paramsEl.ValueKind == JsonValueKind.Object
            && paramsEl.TryGetProperty("address", out var addrEl))
            return addrEl.GetString();

        return null;
    }

    private static string? ExtractUnsubscribeId(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsEl))
            return null;

        // { "params": ["sub-id"] }
        if (paramsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in paramsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
            }
        }

        // { "params": "sub-id" }
        if (paramsEl.ValueKind == JsonValueKind.String)
            return paramsEl.GetString();

        return null;
    }

    /// <summary>
    /// Detects the backward-compat pattern: eth_subscribe(["circles", ...])
    /// </summary>
    private static bool IsCirclesCompatSubscription(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsEl)
            || paramsEl.ValueKind != JsonValueKind.Array)
            return false;

        var enumerator = paramsEl.EnumerateArray();
        if (!enumerator.MoveNext())
            return false;

        var first = enumerator.Current;
        return first.ValueKind == JsonValueKind.String
            && string.Equals(first.GetString(), "circles", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extract address from eth_subscribe(["circles", {address: "0x..."}]) or
    /// eth_subscribe(["circles", "{\"address\":\"0x...\"}"])
    /// </summary>
    private static string? ExtractCirclesAddressFromEthCompat(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsEl)
            || paramsEl.ValueKind != JsonValueKind.Array)
            return null;

        var parts = paramsEl.EnumerateArray().ToArray();
        if (parts.Length < 2)
            return null;

        var second = parts[1];

        if (second.ValueKind == JsonValueKind.Object
            && second.TryGetProperty("address", out var addrEl))
            return addrEl.GetString();

        if (second.ValueKind == JsonValueKind.String)
        {
            var raw = second.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                using var nested = JsonDocument.Parse(raw);
                if (nested.RootElement.ValueKind == JsonValueKind.Object
                    && nested.RootElement.TryGetProperty("address", out var nestedAddr))
                    return nestedAddr.GetString();
            }
            catch (JsonException)
            {
                return raw; // treat as address directly
            }
        }

        return null;
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        _sendLock.Dispose();
    }
}
