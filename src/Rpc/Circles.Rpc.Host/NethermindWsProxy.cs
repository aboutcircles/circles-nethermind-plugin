using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Circles.Rpc.Host;

/// <summary>
/// Manages a persistent WebSocket connection to Nethermind and multiplexes
/// eth_subscribe/eth_unsubscribe requests from multiple client sessions.
///
/// Clients receive stable proxy subscription IDs (UUIDs). Raw Nethermind
/// subscription IDs are internal — on reconnect, the proxy re-subscribes
/// transparently without disrupting clients.
/// </summary>
public sealed class NethermindWsProxy : IAsyncDisposable
{
    private readonly string _nethermindWsUrl;
    private readonly ILogger<NethermindWsProxy> _logger;
    private readonly CancellationTokenSource _cts = new();

    // Nethermind WebSocket connection + send serialization
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _wsLock = new(1, 1); // protects _ws lifecycle (connect/reconnect)
    private readonly SemaphoreSlim _sendLock = new(1, 1); // serializes writes to Nethermind WS
    private Task? _receiveLoop;

    // Request/response correlation for subscribe/unsubscribe calls
    private long _nextRequestId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    // Subscription routing: proxy ID ↔ Nethermind ID ↔ client
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _byProxyId = new();
    private readonly ConcurrentDictionary<string, string> _nethermindToProxy = new();

    // Reconnection settings
    private const int InitialReconnectDelayMs = 3000;
    private const int MaxReconnectDelayMs = 30000;
    private const int MaxNethermindMessageSize = 1_048_576; // 1MB — large blocks can produce big notifications

    public NethermindWsProxy(string nethermindWsUrl, ILogger<NethermindWsProxy> logger)
    {
        _nethermindWsUrl = nethermindWsUrl;
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to a Nethermind event on behalf of a client.
    /// Returns a stable proxy subscription ID (UUID).
    /// </summary>
    public async Task<string> SubscribeAsync(
        WebSocket clientWs,
        SemaphoreSlim clientSendLock,
        JsonElement @params,
        CancellationToken ct,
        Action<string>? onEvicted = null)
    {
        await EnsureConnectedAsync(ct);

        var proxyId = Guid.NewGuid().ToString("N");
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                method = "eth_subscribe",
                @params = CoerceParams(@params),
                id = requestId
            });

            await SendToNethermindAsync(request, ct);

            // Wait for Nethermind's response (with timeout)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await tcs.Task.WaitAsync(timeoutCts.Token);

            if (response.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(
                    $"Nethermind eth_subscribe failed: {error}");
            }

            var nethermindSubId = response.GetProperty("result").GetString()
                ?? throw new InvalidOperationException("Nethermind returned null subscription ID");

            // Register the mapping
            var entry = new SubscriptionEntry(
                ProxyId: proxyId,
                NethermindSubId: nethermindSubId,
                ClientWs: clientWs,
                ClientSendLock: clientSendLock,
                OriginalParams: @params.Clone(),
                OnEvicted: onEvicted);

            _byProxyId[proxyId] = entry;
            _nethermindToProxy[nethermindSubId] = proxyId;

            RpcMetrics.ActiveEthSubscriptions.Inc();
            RpcMetrics.EthSubscriptionsTotal.Inc();

            _logger.LogInformation(
                "eth_subscribe: proxy={ProxyId} nethermind={NethermindId}",
                proxyId, nethermindSubId);

            return proxyId;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Unsubscribe a single subscription by proxy ID.
    /// Sends eth_unsubscribe to Nethermind and removes all mappings.
    /// </summary>
    public async Task<bool> UnsubscribeAsync(string proxyId, CancellationToken ct)
    {
        if (!_byProxyId.TryRemove(proxyId, out var entry))
            return false;

        _nethermindToProxy.TryRemove(entry.NethermindSubId, out _);
        RpcMetrics.ActiveEthSubscriptions.Dec();

        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                var requestId = Interlocked.Increment(ref _nextRequestId);
                var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests[requestId] = tcs;

                try
                {
                    var request = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        jsonrpc = "2.0",
                        method = "eth_unsubscribe",
                        @params = new[] { entry.NethermindSubId },
                        id = requestId
                    });

                    await SendToNethermindAsync(request, ct);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await tcs.Task.WaitAsync(timeoutCts.Token);
                }
                finally
                {
                    _pendingRequests.TryRemove(requestId, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send eth_unsubscribe for proxy={ProxyId}", proxyId);
        }

        _logger.LogInformation("eth_unsubscribe: proxy={ProxyId}", proxyId);
        return true;
    }

    /// <summary>
    /// Remove all subscriptions belonging to a specific client WebSocket.
    /// Called when the client disconnects.
    /// </summary>
    public async Task RemoveClientSubscriptionsAsync(WebSocket clientWs, CancellationToken ct)
    {
        var proxyIds = _byProxyId
            .Where(kvp => ReferenceEquals(kvp.Value.ClientWs, clientWs))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var proxyId in proxyIds)
        {
            await UnsubscribeAsync(proxyId, ct);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ws?.State == WebSocketState.Open)
            return;

        await _wsLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_ws?.State == WebSocketState.Open)
                return;

            _ws?.Dispose();
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            _logger.LogInformation("Connecting to Nethermind WebSocket at {Url}", _nethermindWsUrl);
            await _ws.ConnectAsync(new Uri(_nethermindWsUrl), ct);
            _logger.LogInformation("Connected to Nethermind WebSocket");

            // Start the receive loop (only if not already running)
            if (_receiveLoop == null || _receiveLoop.IsCompleted)
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        finally
        {
            _wsLock.Release();
        }
    }

    private async Task SendToNethermindAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws?.State != WebSocketState.Open)
                throw new InvalidOperationException("Nethermind WebSocket is not connected");

            await _ws.SendAsync(data, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Continuously reads from the Nethermind WebSocket and routes messages:
    /// - Responses (with "id") → complete pending TaskCompletionSource
    /// - Notifications (with "method":"eth_subscription") → forward to client WS
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var needsReconnect = false;

                using (var stream = new MemoryStream())
                {
                    while (true)
                    {
                        if (_ws?.State != WebSocketState.Open)
                        {
                            _logger.LogWarning("Nethermind WS not open in receive loop, triggering reconnect");
                            needsReconnect = true;
                            break;
                        }

                        var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogWarning("Nethermind WS sent close frame");
                            needsReconnect = true;
                            break;
                        }

                        stream.Write(buffer, 0, result.Count);

                        if (stream.Length > MaxNethermindMessageSize)
                        {
                            _logger.LogWarning("Nethermind message exceeds {MaxSize} bytes, skipping", MaxNethermindMessageSize);
                            while (!result.EndOfMessage)
                                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            stream.SetLength(0);
                            break;
                        }

                        if (result.EndOfMessage)
                            break;
                    }

                    if (!needsReconnect && stream.Length > 0)
                    {
                        using var json = JsonDocument.Parse(stream.ToArray());
                        RouteMessage(json.RootElement);
                    }
                }

                if (needsReconnect)
                    await ReconnectAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, "Nethermind WS error, reconnecting");
                await ReconnectAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in Nethermind WS receive loop");
                await Task.Delay(1000, ct);
            }
        }
    }

    private void RouteMessage(JsonElement root)
    {
        // Response to a request we made (has "id" field)
        if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
        {
            var id = idElement.GetInt64();
            if (_pendingRequests.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(root.Clone());
            }
            return;
        }

        // Subscription notification: {"jsonrpc":"2.0","method":"eth_subscription","params":{"subscription":"0x...","result":{...}}}
        if (root.TryGetProperty("method", out var methodElement)
            && methodElement.GetString() == "eth_subscription"
            && root.TryGetProperty("params", out var paramsElement)
            && paramsElement.TryGetProperty("subscription", out var subIdElement))
        {
            var nethermindSubId = subIdElement.GetString();
            if (nethermindSubId != null && _nethermindToProxy.TryGetValue(nethermindSubId, out var proxyId)
                && _byProxyId.TryGetValue(proxyId, out var entry))
            {
                // Clone before fire-and-forget — the JsonDocument is disposed after RouteMessage returns
                var clonedParams = paramsElement.Clone();
                _ = ForwardNotificationAsync(entry, proxyId, clonedParams);
            }
            else
            {
                _logger.LogDebug("Received notification for unknown subscription {SubId}", nethermindSubId);
            }
        }
    }

    private async Task ForwardNotificationAsync(SubscriptionEntry entry, string proxyId, JsonElement paramsElement)
    {
        if (entry.ClientWs.State != WebSocketState.Open)
        {
            RemoveStaleSubscription(proxyId);
            return;
        }

        try
        {
            // Rewrite the subscription ID to the proxy ID
            var notification = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                method = "eth_subscription",
                @params = new
                {
                    subscription = proxyId,
                    result = paramsElement.GetProperty("result")
                }
            });

            // Timeout prevents a dead client from holding the send lock indefinitely
            if (!await entry.ClientSendLock.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Send lock timeout for proxy={ProxyId}, removing stale subscription", proxyId);
                RemoveStaleSubscription(proxyId);
                return;
            }

            try
            {
                if (entry.ClientWs.State == WebSocketState.Open)
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    sendCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await entry.ClientWs.SendAsync(notification, WebSocketMessageType.Text, true, sendCts.Token);
                }
            }
            finally
            {
                entry.ClientSendLock.Release();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Server shutting down — leave subscription in place; DisposeAsync handles cleanup.
        }
        catch (OperationCanceledException)
        {
            // 5-second SendAsync timeout — client is unresponsive. Drop the subscription
            // so we don't leak proxy mappings or keep the upstream eth_subscribe alive
            // for a dead client.
            _logger.LogWarning(
                "Notification send timed out for proxy={ProxyId}, removing stale subscription",
                proxyId);
            RemoveStaleSubscription(proxyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward notification, removing stale subscription proxy={ProxyId}", proxyId);
            RemoveStaleSubscription(proxyId);
        }
    }

    private void RemoveStaleSubscription(string proxyId)
    {
        if (_byProxyId.TryRemove(proxyId, out var removed))
        {
            _nethermindToProxy.TryRemove(removed.NethermindSubId, out _);

            // Best-effort upstream unsubscribe — stop Nethermind from sending orphaned notifications
            _ = SendBestEffortUnsubscribeAsync(removed.NethermindSubId);

            RpcMetrics.ActiveEthSubscriptions.Dec();

            // Notify the owning session so its per-session state (e.g. _ethSubIds) stays in
            // sync with the proxy's view; otherwise the session's subscription-limit count
            // and eth_unsubscribe handling would diverge from reality.
            NotifyEvicted(removed);

            _logger.LogInformation("Removed stale eth subscription proxy={ProxyId}", proxyId);
        }
    }

    private async Task SendBestEffortUnsubscribeAsync(string nethermindSubId)
    {
        try
        {
            if (_ws?.State != WebSocketState.Open) return;

            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                method = "eth_unsubscribe",
                @params = new[] { nethermindSubId },
                id = requestId
            });
            await SendToNethermindAsync(request, _cts.Token);
        }
        catch (ObjectDisposedException) { /* shutdown race — expected */ }
        catch (OperationCanceledException) { /* shutdown — expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort eth_unsubscribe failed for {SubId}", nethermindSubId);
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        RpcMetrics.NethermindWsReconnects.Inc();
        var delay = InitialReconnectDelayMs;

        // Fail all pending requests
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wsLock.WaitAsync(ct);
                try
                {
                    _ws?.Dispose();
                    _ws = new ClientWebSocket();
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                    _logger.LogInformation("Reconnecting to Nethermind WebSocket");
                    await _ws.ConnectAsync(new Uri(_nethermindWsUrl), ct);
                    _logger.LogInformation("Reconnected to Nethermind WebSocket");
                }
                finally
                {
                    _wsLock.Release();
                }

                // Re-subscribe all active subscriptions (outside lock — uses _sendLock for writes)
                await ResubscribeAllAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect failed, retrying in {Delay}ms", delay);
                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, MaxReconnectDelayMs);
            }
        }
    }

    /// <summary>
    /// After reconnection, re-subscribe all active subscriptions with Nethermind.
    /// Updates the Nethermind ID mapping while keeping proxy IDs stable.
    /// </summary>
    private async Task ResubscribeAllAsync(CancellationToken ct)
    {
        var entries = _byProxyId.Values.ToList();
        if (entries.Count == 0) return;

        _logger.LogInformation("Re-subscribing {Count} active eth subscriptions after reconnect", entries.Count);

        // Clear old Nethermind→proxy mappings (Nethermind assigned new IDs)
        _nethermindToProxy.Clear();

        foreach (var entry in entries)
        {
            try
            {
                var requestId = Interlocked.Increment(ref _nextRequestId);
                var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests[requestId] = tcs;

                try
                {
                    var request = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        jsonrpc = "2.0",
                        method = "eth_subscribe",
                        @params = CoerceParams(entry.OriginalParams),
                        id = requestId
                    });

                    await SendToNethermindAsync(request, ct);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                    var response = await tcs.Task.WaitAsync(timeoutCts.Token);

                    if (response.TryGetProperty("result", out var resultElement))
                    {
                        var newNethermindId = resultElement.GetString();
                        if (newNethermindId != null)
                        {
                            // Update the entry with new Nethermind ID
                            var updated = entry with { NethermindSubId = newNethermindId };
                            _byProxyId[entry.ProxyId] = updated;
                            _nethermindToProxy[newNethermindId] = entry.ProxyId;

                            _logger.LogDebug(
                                "Re-subscribed proxy={ProxyId} old={OldId} new={NewId}",
                                entry.ProxyId, entry.NethermindSubId, newNethermindId);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Re-subscribe failed for proxy={ProxyId}: {Response}",
                            entry.ProxyId, response);
                        _byProxyId.TryRemove(entry.ProxyId, out _);
                        RpcMetrics.ActiveEthSubscriptions.Dec();
                        NotifyEvicted(entry);
                    }
                }
                finally
                {
                    _pendingRequests.TryRemove(requestId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-subscribe proxy={ProxyId}", entry.ProxyId);
                _byProxyId.TryRemove(entry.ProxyId, out _);
                RpcMetrics.ActiveEthSubscriptions.Dec();
                NotifyEvicted(entry);
            }
        }
    }

    /// <summary>
    /// Coerce the params JsonElement into a proper array for Nethermind.
    /// eth_subscribe expects: ["eventType", {options}] or ["eventType"]
    /// </summary>
    private static object CoerceParams(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object?>();
            foreach (var item in @params.EnumerateArray())
            {
                list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item);
            }
            return list;
        }

        // Single value — wrap in array
        if (@params.ValueKind == JsonValueKind.String)
            return new[] { @params.GetString() };

        return @params;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_receiveLoop != null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Receive loop terminated with error during shutdown");
            }
        }

        if (_ws != null)
        {
            if (_ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Best-effort WS close failed during shutdown");
                }
            }
            _ws.Dispose();
        }

        _cts.Dispose();
        _wsLock.Dispose();
        _sendLock.Dispose();
    }

    private sealed record SubscriptionEntry(
        string ProxyId,
        string NethermindSubId,
        WebSocket ClientWs,
        SemaphoreSlim ClientSendLock,
        JsonElement OriginalParams,
        Action<string>? OnEvicted = null);

    private void NotifyEvicted(SubscriptionEntry entry)
    {
        if (entry.OnEvicted is null) return;
        try
        {
            entry.OnEvicted(entry.ProxyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnEvicted callback threw for proxy={ProxyId}", entry.ProxyId);
        }
    }
}
