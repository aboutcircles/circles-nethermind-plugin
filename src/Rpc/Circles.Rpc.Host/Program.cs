using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Common.Dto;
using Circles.Index.Query.Dto;
using Circles.Rpc.Host;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

var builder = BuilderSetup.ConfigureBuilder(args);

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseCors();
app.UseHttpMetrics();
app.UseResponseCompression();
app.MapMetrics();

app.MapHealthChecks("/live", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("live")
});

// readiness: nethermind sync status + pathfinder connectivity + database connectivity + indexer sync
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("nethermind-sync") || hc.Tags.Contains("pathfinder-connection") || hc.Tags.Contains("database-connection") || hc.Tags.Contains("indexer-sync"),
    AllowCachingResponses = false,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Degraded] = StatusCodes.Status200OK
    }
});

// nethermind connectivity
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("nethermind-connection"),
    AllowCachingResponses = false,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Degraded] = StatusCodes.Status200OK
    }
});

async Task HandleSubscriptionWebSocket(HttpContext context, CirclesSubscriptionService subscriptionService, ILogger<Program> logger)
{
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    logger.LogInformation("Incoming WebSocket subscription request from {RemoteIp}", remoteIp);

    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogWarning("Rejected non-WebSocket subscription request from {RemoteIp}", remoteIp);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket request expected.");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var request = await ReceiveSubscriptionRequestAsync(webSocket, context.RequestAborted);

    if (request == null)
    {
        logger.LogWarning("Subscription payload missing or invalid from {RemoteIp}", remoteIp);
        await SendSubscriptionErrorAsync(webSocket, null, "Invalid subscription payload", context.RequestAborted);
        await webSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Invalid subscription payload", context.RequestAborted);
        return;
    }

    if (!string.Equals(request.Jsonrpc, "2.0", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(request.Method, "circles_subscribe", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning("Unsupported subscription method '{Method}' from {RemoteIp}", request.Method, remoteIp);
        await SendSubscriptionErrorAsync(webSocket, request.Id, "Unsupported method", context.RequestAborted);
        await webSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Unsupported method", context.RequestAborted);
        return;
    }

    var subscriptionId = subscriptionService.Subscribe(webSocket, request.Params?.Address);
    RpcMetrics.ActiveSubscriptions.Inc();
    logger.LogInformation(
        "Subscription {SubscriptionId} established from {RemoteIp} (address filter: {Address})",
        subscriptionId,
        remoteIp,
        request.Params?.Address ?? "*"
    );

    try
    {
        await SendSubscriptionAckAsync(webSocket, request.Id, subscriptionId, context.RequestAborted);
        await PumpWebSocketAsync(webSocket, context.RequestAborted);
    }
    finally
    {
        subscriptionService.Unsubscribe(subscriptionId);
        RpcMetrics.ActiveSubscriptions.Dec();
        logger.LogInformation("Subscription {SubscriptionId} closed for {RemoteIp}", subscriptionId, remoteIp);
    }
}

app.Map("/ws/subscribe", HandleSubscriptionWebSocket).DisableAntiforgery();
app.Map("/ws", HandleSubscriptionWebSocket).DisableAntiforgery();

// ─── Batch JSON-RPC middleware ────────────────────────────────────────────────
// JSON-RPC batch requests (body is a JSON array) are forwarded entirely to Nethermind.
// Nethermind has the Circles module loaded, so it can handle both circles_* and eth_* methods.
const int MaxBatchBodySize = 1_048_576; // 1 MB
app.Use(async (context, next) =>
{
    if (context.Request.Method == "POST" && context.Request.Path == "/")
    {
        // Only intercept JSON content types
        var contentType = context.Request.ContentType;
        if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Peek first byte to detect batch (JSON array) — only EnableBuffering for this peek
        context.Request.EnableBuffering();
        var buf = new byte[1];
        var bytesRead = await context.Request.Body.ReadAsync(buf, 0, 1);
        context.Request.Body.Position = 0;
        var firstByte = bytesRead > 0 ? buf[0] : -1;

        if (firstByte == '[') // JSON array = batch request
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var nethermindClient = context.RequestServices.GetRequiredService<NethermindRpcClient>();
            var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var startTimestamp = Stopwatch.GetTimestamp();

            // Enforce body size limit for batch requests
            if (context.Request.ContentLength > MaxBatchBodySize)
            {
                context.Response.StatusCode = 413;
                context.Response.ContentType = "application/json";
                var tooLarge = new JsonRpcErrorResponse
                {
                    Error = new JsonRpcError { Code = -32600, Message = $"Batch request too large (max {MaxBatchBodySize / 1024}KB)" }
                };
                await JsonSerializer.SerializeAsync(context.Response.Body, tooLarge);
                return;
            }

            logger.LogInformation("Batch JSON-RPC request from {RemoteIp}, forwarding to Nethermind", remoteIp);
            RpcMetrics.ProxiedTotal.WithLabels("batch").Inc();

            try
            {
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);

                if (ms.Length > MaxBatchBodySize)
                {
                    context.Response.StatusCode = 413;
                    context.Response.ContentType = "application/json";
                    var tooLarge = new JsonRpcErrorResponse
                    {
                        Error = new JsonRpcError { Code = -32600, Message = $"Batch request too large (max {MaxBatchBodySize / 1024}KB)" }
                    };
                    await JsonSerializer.SerializeAsync(context.Response.Body, tooLarge);
                    return;
                }

                var body = ms.ToArray();
                var result = await nethermindClient.ForwardRawRequest(body);
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                RpcMetrics.ProxyDuration.WithLabels("batch").Observe(elapsed.TotalSeconds);

                logger.LogInformation("Batch JSON-RPC proxied in {ElapsedMs} ms from {RemoteIp}",
                    elapsed.TotalMilliseconds, remoteIp);

                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body, result);
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                RpcMetrics.ErrorsTotal.WithLabels("batch", "proxy_error").Inc();
                logger.LogError(ex, "Failed to proxy batch JSON-RPC from {RemoteIp} after {ElapsedMs} ms",
                    remoteIp, elapsed.TotalMilliseconds);

                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json";
                var errorResponse = new JsonRpcErrorResponse
                {
                    Error = new JsonRpcError { Code = -32603, Message = $"Batch proxy error: {ex.Message}" }
                };
                await JsonSerializer.SerializeAsync(context.Response.Body, errorResponse);
            }
            return;
        }
    }

    await next();
});

// ─── Routes ─────────────────────────────────────────────────────────────────

app.MapPost("/", async (
    HttpContext context,
    JsonRpcRequest request,
    Settings settings,
    ILogger<Program> logger,
    CirclesRpcModule rpcModule
    ) =>
{
    if (request.Jsonrpc != "2.0" || string.IsNullOrEmpty(request.Method))
    {
        return Results.BadRequest(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" }
        });
    }

    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var methodName = request.Method ?? "<unknown>";
    var startTimestamp = Stopwatch.GetTimestamp();

    // Track metrics
    RpcMetrics.RequestsTotal.WithLabels(methodName).Inc();
    RpcMetrics.InFlightRequests.WithLabels(methodName).Inc();

    logger.LogInformation(
        "RPC request {Method} (id={Id}) received from {RemoteIp}",
        methodName,
        request.Id,
        remoteIp);

    try
    {
        object rpcResult = request.Method switch
        {
            // Balance & Token Methods
            "circles_getTotalBalance" => await HandleGetTotalBalance(request, rpcModule),
            "circlesV2_getTotalBalance" => await HandleV2GetTotalBalance(request, rpcModule),
            "circles_getTokenBalances" => await HandleGetTokenBalances(request, rpcModule),
            "circles_getTokenInfo" => await HandleGetTokenInfo(request, rpcModule),
            "circles_getTokenInfoBatch" => await HandleGetTokenInfoBatch(request, rpcModule),
            // Avatar & Profile Methods
            "circles_getAvatarInfo" => await HandleGetAvatarInfo(request, rpcModule),
            "circles_getAvatarInfoBatch" => await HandleGetAvatarInfoBatch(request, rpcModule),
            "circles_getProfileCid" => await HandleGetProfileCid(request, rpcModule),
            "circles_getProfileCidBatch" => await HandleGetProfileCidBatch(request, rpcModule),
            "circles_getProfileByCid" => await HandleGetProfileByCid(request, rpcModule),
            "circles_getProfileByCidBatch" => await HandleGetProfileByCidBatch(request, rpcModule),
            "circles_getProfileByAddress" => await HandleGetProfileByAddress(request, rpcModule),
            "circles_getProfileByAddressBatch" => await HandleGetProfileByAddressBatch(request, rpcModule),
            "circles_searchProfiles" => await HandleSearchProfiles(request, rpcModule),
            // Trust & Network Methods
            "circles_getTrustRelations" => await HandleGetTrustRelations(request, rpcModule),
            "circles_getCommonTrust" => await HandleGetCommonTrust(request, rpcModule),
            "circles_getNetworkSnapshot" => await HandleGetNetworkSnapshot(request, rpcModule),
            "circles_getAggregatedTrustRelations" => await ReflectionHandler(request, rpcModule),
            "circles_findGroups" => await ReflectionHandler(request, rpcModule),
            "circles_getGroupMembers" => await ReflectionHandler(request, rpcModule),
            "circles_getGroupMemberships" => await ReflectionHandler(request, rpcModule),
            "circles_getTransactionHistory" => await ReflectionHandler(request, rpcModule),
            "circles_getTransferData" => await ReflectionHandler(request, rpcModule),
            "circles_getTokenHolders" => await ReflectionHandler(request, rpcModule),
            "circlesV2_findPath" => await HandleV2FindPath(request, rpcModule),
            // System & Query Methods
            "circles_events" => await HandleEvents(request, rpcModule),
            "circles_health" => await HandleHealth(request, rpcModule),
            "circles_tables" => await HandleTables(request, rpcModule),
            // Legacy non-paginated format ({columns, rows} only) — kept for non-paginating callers
            "circles_query" => await HandleQuery(request, rpcModule),
            // Server-side cursor pagination ({columns, rows, hasMore, nextCursor})
            "circles_paginated_query" => await HandleQuery2(request, rpcModule),
            // SDK Enablement Methods
            "circles_getProfileView" => await ReflectionHandler(request, rpcModule),
            "circles_getTrustNetworkSummary" => await ReflectionHandler(request, rpcModule),
            "circles_getAggregatedTrustRelationsEnriched" => await ReflectionHandler(request, rpcModule),
            "circles_getValidInviters" => await ReflectionHandler(request, rpcModule),
            "circles_getTransactionHistoryEnriched" => await ReflectionHandler(request, rpcModule),
            "circles_searchProfileByAddressOrName" => await ReflectionHandler(request, rpcModule),
            "circles_getInvitationOrigin" => await ReflectionHandler(request, rpcModule),
            "circles_getAllInvitations" => await ReflectionHandler(request, rpcModule),
            "circles_getTrustInvitations" => await ReflectionHandler(request, rpcModule),
            "circles_getEscrowInvitations" => await ReflectionHandler(request, rpcModule),
            "circles_getAtScaleInvitations" => await ReflectionHandler(request, rpcModule),
            "circles_getInvitationsFrom" => await ReflectionHandler(request, rpcModule),

            _ => throw new RpcMethodNotFoundException(request.Method)
        };

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        // Record successful request metrics
        RpcMetrics.RequestDuration.WithLabels(methodName).Observe(elapsed.TotalSeconds);
        RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();

        logger.LogInformation(
            "RPC request {Method} (id={Id}) succeeded in {ElapsedMs} ms (remote {RemoteIp})",
            methodName,
            request.Id,
            elapsed.TotalMilliseconds,
            remoteIp);

        return Results.Ok(new JsonRpcResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Result = rpcResult
        });
    }
    catch (RpcMethodNotFoundException)
    {
        // Only proxy safe read-only Ethereum JSON-RPC methods to Nethermind.
        // Block admin_*, debug_*, personal_*, miner_*, etc. to prevent node compromise.
        var isProxyAllowed = methodName.StartsWith("eth_", StringComparison.Ordinal)
            || methodName.StartsWith("net_", StringComparison.Ordinal)
            || methodName.StartsWith("web3_", StringComparison.Ordinal);

        if (!isProxyAllowed)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            RpcMetrics.RequestDuration.WithLabels(methodName).Observe(elapsed.TotalSeconds);
            RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();
            RpcMetrics.ErrorsTotal.WithLabels(methodName, "method_not_found").Inc();

            logger.LogWarning(
                "RPC method not found (not proxyable): {Method} from {RemoteIp} after {ElapsedMs} ms",
                methodName, remoteIp, elapsed.TotalMilliseconds);

            return Results.Ok(new JsonRpcErrorResponse
            {
                Id = JsonRpcId.CoerceId(request.Id),
                Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {methodName}" }
            });
        }

        var nethermindClient = context.RequestServices.GetRequiredService<NethermindRpcClient>();
        try
        {
            RpcMetrics.ProxiedTotal.WithLabels(methodName).Inc();

            logger.LogInformation(
                "Proxying RPC request {Method} (id={Id}) to Nethermind from {RemoteIp}",
                methodName, request.Id, remoteIp);

            var proxyResult = await nethermindClient.ForwardRpcRequest(
                request.Method!, request.Id, request.Params);

            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            RpcMetrics.ProxyDuration.WithLabels(methodName).Observe(elapsed.TotalSeconds);
            RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();

            logger.LogInformation(
                "Proxied RPC request {Method} (id={Id}) completed in {ElapsedMs} ms from {RemoteIp}",
                methodName, request.Id, elapsed.TotalMilliseconds, remoteIp);

            // Return Nethermind's response verbatim (preserves result or error)
            return Results.Json(proxyResult);
        }
        catch (Exception proxyEx)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();
            RpcMetrics.ErrorsTotal.WithLabels(methodName, "proxy_error").Inc();

            logger.LogError(proxyEx,
                "Failed to proxy RPC request {Method} to Nethermind from {RemoteIp} after {ElapsedMs} ms",
                methodName, remoteIp, elapsed.TotalMilliseconds);

            return Results.Ok(new JsonRpcErrorResponse
            {
                Id = JsonRpcId.CoerceId(request.Id),
                Error = new JsonRpcError { Code = -32603, Message = $"Failed to proxy request to Nethermind: {proxyEx.Message}" }
            });
        }
    }
    catch (ArgumentException ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        RpcMetrics.RequestDuration.WithLabels(methodName).Observe(elapsed.TotalSeconds);
        RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();
        RpcMetrics.ErrorsTotal.WithLabels(methodName, "invalid_params").Inc();

        logger.LogWarning(ex,
            "Invalid params for method: {Method} from {RemoteIp} after {ElapsedMs} ms",
            methodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32602, Message = ex.Message }
        });
    }
    catch (JsonException ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        RpcMetrics.RequestDuration.WithLabels(methodName).Observe(elapsed.TotalSeconds);
        RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();
        RpcMetrics.ErrorsTotal.WithLabels(methodName, "invalid_json").Inc();

        logger.LogWarning(ex,
            "Invalid JSON params for method: {Method} from {RemoteIp} after {ElapsedMs} ms",
            methodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32602, Message = "Invalid params" }
        });
    }
    catch (Exception ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        RpcMetrics.RequestDuration.WithLabels(methodName).Observe(elapsed.TotalSeconds);
        RpcMetrics.InFlightRequests.WithLabels(methodName).Dec();
        RpcMetrics.ErrorsTotal.WithLabels(methodName, "internal_error").Inc();

        logger.LogError(ex,
            "Internal Server Error during RPC execution for method: {Method} from {RemoteIp} after {ElapsedMs} ms",
            methodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32603, Message = $"Internal server error: {ex.Message}" }
        });
    }

}).DisableAntiforgery();

app.Run();

static async Task<SubscriptionRequest?> ReceiveSubscriptionRequestAsync(WebSocket webSocket, CancellationToken cancellationToken)
{
    var buffer = new byte[4096];
    using var stream = new MemoryStream();

    while (true)
    {
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        if (result.MessageType != WebSocketMessageType.Text)
        {
            continue;
        }

        stream.Write(buffer, 0, result.Count);

        if (result.EndOfMessage)
        {
            break;
        }
    }

    if (stream.Length == 0)
    {
        return null;
    }

    var payload = Encoding.UTF8.GetString(stream.ToArray());
    return ParseSubscriptionRequest(payload);
}

static SubscriptionRequest? ParseSubscriptionRequest(string payload)
{
    using var document = JsonDocument.Parse(payload);
    var root = document.RootElement;

    var jsonrpc = root.TryGetProperty("jsonrpc", out var jsonrpcElement)
        ? jsonrpcElement.GetString()
        : null;

    var method = root.TryGetProperty("method", out var methodElement)
        ? methodElement.GetString()
        : null;

    JsonElement? id = root.TryGetProperty("id", out var idElement)
        ? idElement.Clone()
        : null;

    var parameters = ParseSubscriptionParams(method, root);

    // Compatibility: accept eth_subscribe payloads for circles subscriptions
    if (string.Equals(method, "eth_subscribe", StringComparison.OrdinalIgnoreCase) &&
        parameters != null)
    {
        method = "circles_subscribe";
    }

    return new SubscriptionRequest
    {
        Jsonrpc = jsonrpc,
        Method = method,
        Params = parameters,
        Id = id
    };
}

static SubscriptionParams? ParseSubscriptionParams(string? method, JsonElement root)
{
    if (!root.TryGetProperty("params", out var paramsElement) ||
        paramsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
        return new SubscriptionParams();
    }

    // Native payload shape:
    // { "method": "circles_subscribe", "params": { "address": "0x..." } }
    if (paramsElement.ValueKind == JsonValueKind.Object)
    {
        var address = paramsElement.TryGetProperty("address", out var addrElement)
            ? addrElement.GetString()
            : null;

        return new SubscriptionParams { Address = address };
    }

    // Compatibility payload shape:
    // { "method": "eth_subscribe", "params": ["circles", "{\"address\":\"0x...\"}"] }
    if (paramsElement.ValueKind == JsonValueKind.Array)
    {
        var parts = paramsElement.EnumerateArray().ToArray();
        if (parts.Length == 0)
        {
            return new SubscriptionParams();
        }

        if (string.Equals(method, "eth_subscribe", StringComparison.OrdinalIgnoreCase))
        {
            var topic = parts[0].ValueKind == JsonValueKind.String ? parts[0].GetString() : null;
            if (!string.Equals(topic, "circles", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (parts.Length < 2)
            {
                return new SubscriptionParams();
            }

            var second = parts[1];

            if (second.ValueKind == JsonValueKind.Object)
            {
                var addr = second.TryGetProperty("address", out var addrElement)
                    ? addrElement.GetString()
                    : null;
                return new SubscriptionParams { Address = addr };
            }

            if (second.ValueKind == JsonValueKind.String)
            {
                var raw = second.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(raw);
                        var nestedRoot = nested.RootElement;
                        if (nestedRoot.ValueKind == JsonValueKind.Object)
                        {
                            var addr = nestedRoot.TryGetProperty("address", out var addrElement)
                                ? addrElement.GetString()
                                : null;
                            return new SubscriptionParams { Address = addr };
                        }
                    }
                    catch (JsonException)
                    {
                        // Fallback: treat raw string itself as address
                        return new SubscriptionParams { Address = raw };
                    }
                }

                return new SubscriptionParams();
            }

            return new SubscriptionParams();
        }
    }

    return null;
}

static async Task SendSubscriptionAckAsync(WebSocket socket, JsonElement? id, string subscriptionId, CancellationToken cancellationToken)
{
    var envelope = BuildSubscriptionResponse(id, subscriptionId, null);
    await socket.SendAsync(envelope, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task SendSubscriptionErrorAsync(WebSocket socket, JsonElement? id, string message, CancellationToken cancellationToken)
{
    var error = new { code = -32600, message };
    var envelope = BuildSubscriptionResponse(id, null, error);
    await socket.SendAsync(envelope, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task PumpWebSocketAsync(WebSocket socket, CancellationToken cancellationToken)
{
    var buffer = new byte[1024];
    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
            break;
        }
    }
}

static ArraySegment<byte> BuildSubscriptionResponse(JsonElement? id, object? result, object? error)
{
    var payload = new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0"
    };

    if (result != null)
    {
        payload["result"] = result;
    }

    if (error != null)
    {
        payload["error"] = error;
    }

    if (id is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
    {
        payload["id"] = id.Value;
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    return new ArraySegment<byte>(bytes);
}

// ─── RPC Handler Methods ──────────────────────────────────────────────────

static async Task<object> HandleGetTotalBalance(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    string address = parameters[0].GetString() ?? throw new ArgumentException("Address parameter must be a string");
    bool asTimeCircles = true;
    if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
    {
        if (parameters[1].ValueKind == JsonValueKind.True || parameters[1].ValueKind == JsonValueKind.False)
        {
            asTimeCircles = parameters[1].GetBoolean();
        }
        else if (parameters[1].ValueKind == JsonValueKind.String)
        {
            if (bool.TryParse(parameters[1].GetString(), out var parsedValue))
            {
                asTimeCircles = parsedValue;
            }
        }
    }

    var result = await rpcModule.GetTotalBalance(address, 1, asTimeCircles);
    return result;
}

static async Task<object> HandleV2GetTotalBalance(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    string address = parameters[0].GetString() ?? throw new ArgumentException("Address parameter must be a string");
    bool asTimeCircles = true;
    if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
    {
        if (parameters[1].ValueKind == JsonValueKind.True || parameters[1].ValueKind == JsonValueKind.False)
        {
            asTimeCircles = parameters[1].GetBoolean();
        }
        else if (parameters[1].ValueKind == JsonValueKind.String)
        {
            if (bool.TryParse(parameters[1].GetString(), out var parsedValue))
            {
                asTimeCircles = parsedValue;
            }
        }
    }

    var result = await rpcModule.GetTotalBalance(address, 2, asTimeCircles);
    return result;
}

static async Task<object> HandleGetTokenBalances(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    string address = parameters[0];
    var result = await rpcModule.GetTokenBalances(address);
    return result;
}

static async Task<object> HandleGetTokenInfo(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Token address parameter is required");
    }

    return (object?)await rpcModule.GetTokenInfo(parameters[0]) ?? new { };
}

static async Task<object> HandleGetTokenInfoBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Token addresses array parameter is required");
    }

    return await rpcModule.GetTokenInfoBatch(parameters[0]);
}

static async Task<object> HandleGetAvatarInfo(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    try
    {
        return await rpcModule.GetAvatarInfo(parameters[0]);
    }
    catch (InvalidOperationException ex)
    {
        // Return null for non-existent avatars to match reference implementation behavior
        throw new ArgumentException(ex.Message);
    }
}

static async Task<object> HandleGetAvatarInfoBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Addresses array parameter is required");
    }

    return await rpcModule.GetAvatarInfoBatch(parameters[0]);
}

static async Task<object> HandleGetProfileCid(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    var cid = await rpcModule.GetProfileCid(parameters[0]);
    return cid ?? throw new ArgumentException("Profile CID not found");
}

static async Task<object> HandleGetProfileCidBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Addresses array parameter is required");
    }

    return await rpcModule.GetProfileCidBatch(parameters[0]);
}

static async Task<object> HandleGetProfileByCid(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("CID parameter is required");
    }

    var profile = await rpcModule.GetProfileByCid(parameters[0]);
    return profile ?? throw new ArgumentException("Profile not found for CID");
}

static async Task<object> HandleGetProfileByCidBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("CIDs array parameter is required");
    }

    return await rpcModule.GetProfileByCidBatch(parameters[0]);
}

static async Task<object> HandleGetProfileByAddress(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    return await rpcModule.GetProfileByAddress(parameters[0]);
}

static async Task<object> HandleGetProfileByAddressBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Addresses array parameter is required");
    }

    return await rpcModule.GetProfileByAddressBatch(parameters[0]);
}

static async Task<object> HandleSearchProfiles(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Search text parameter is required");
    }

    string text = parameters[0].GetString() ?? "";
    int limit = 20;
    int offset = 0;
    string[]? types = null;

    if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
    {
        limit = parameters[1].GetInt32();
    }

    if (parameters.Length > 2 && parameters[2].ValueKind != JsonValueKind.Null)
    {
        offset = parameters[2].GetInt32();
    }

    if (parameters.Length > 3 && parameters[3].ValueKind != JsonValueKind.Null)
    {
        types = parameters[3].Deserialize<string[]>();
    }

    var searchResults = await rpcModule.SearchProfiles(text, limit, offset, types);
    var transformedResults = searchResults.Results.Select(item =>
    {
        // Extract properties from AvatarInfo
        var avatarInfo = item.AvatarInfo;
        var address = avatarInfo.Avatar; // Remote expects "address" instead of "avatar"
        var cid = avatarInfo.CidV0; // Remote expects "cid"
        var avatarType = avatarInfo.Type; // Remote expects "avatarType"

        // Extract properties from Profile (JsonElement)
        var profileName = item.Profile?.TryGetProperty("name", out var nameElement) == true ? nameElement.GetString() : null;
        var profileDescription = item.Profile?.TryGetProperty("description", out var descElement) == true ? descElement.GetString() : null;
        var profilePreviewImageUrl = item.Profile?.TryGetProperty("previewImageUrl", out var imageUrlElement) == true ? imageUrlElement.GetString() : null;

        // Construct the flattened anonymous object
        return new
        {
            address = address,
            cid = cid,
            name = profileName,
            description = profileDescription,
            previewImageUrl = profilePreviewImageUrl,
            avatarType = avatarType
        };
    }).ToArray();

    return transformedResults;
}

static async Task<object> HandleGetTrustRelations(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    return await rpcModule.GetTrustRelations(parameters[0]);
}

static async Task<object> HandleGetCommonTrust(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<object[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length < 2)
    {
        throw new ArgumentException("Two address parameters are required");
    }

    var address1 = parameters[0].ToString();
    var address2 = parameters[1].ToString();
    if (address1 == null || address2 == null)
    {
        throw new ArgumentException("Address parameters cannot be null");
    }
    int? version = null;

    if (parameters.Length > 2 && parameters[2] != null)
    {
        if (int.TryParse(parameters[2].ToString(), out var v))
        {
            version = v;
        }
    }

    var result = await rpcModule.GetCommonTrust(address1, address2, version);
    return result.CommonTrusts;
}

static async Task<object> HandleGetNetworkSnapshot(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    return await rpcModule.GetNetworkSnapshot();
}

static async Task<object> HandleV2FindPath(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("FlowRequest parameter is required");
    }

    var flowRequest = JsonSerializer.Deserialize<FlowRequest>(parameters[0].GetRawText(), SharedJsonOptions.CamelCase);
    if (flowRequest == null)
    {
        throw new ArgumentException("Invalid FlowRequest parameter");
    }

    return await rpcModule.FindPathV2(flowRequest);
}

static async Task<object> HandleEvents(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());

    string? address = null;
    long? fromBlock = null;
    long? toBlock = null;
    string[]? eventTypes = null;
    bool? sortAscending = false;
    int? limit = null;
    string? cursor = null;

    if (parameters == null || parameters.Length == 0)
    {
        return await rpcModule.GetEvents(null, null, null, null, null, false, null, null);
    }

    if (parameters.Length > 0 && parameters[0].ValueKind != JsonValueKind.Null)
    {
        address = parameters[0].GetString();
    }
    if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
    {
        fromBlock = parameters[1].GetInt64();
    }
    if (parameters.Length > 2 && parameters[2].ValueKind != JsonValueKind.Null)
    {
        toBlock = parameters[2].GetInt64();
    }
    if (parameters.Length > 3 && parameters[3].ValueKind != JsonValueKind.Null)
    {
        eventTypes = parameters[3].Deserialize<string[]>();
    }

    IFilterPredicateDto[]? filterPredicates = null;
    if (parameters.Length > 4 && parameters[4].ValueKind != JsonValueKind.Null)
    {
        filterPredicates = JsonSerializer.Deserialize<IFilterPredicateDto[]>(parameters[4].GetRawText(), SharedJsonOptions.FilterPredicate);
    }

    if (parameters.Length > 5 && parameters[5].ValueKind != JsonValueKind.Null)
    {
        sortAscending = parameters[5].GetBoolean();
    }

    // New pagination parameters
    if (parameters.Length > 6 && parameters[6].ValueKind != JsonValueKind.Null)
    {
        limit = parameters[6].GetInt32();
    }

    if (parameters.Length > 7 && parameters[7].ValueKind != JsonValueKind.Null)
    {
        cursor = parameters[7].GetString();
    }

    return await rpcModule.GetEvents(address, fromBlock, toBlock, eventTypes, filterPredicates, sortAscending, limit, cursor);
}

static async Task<object> ReflectionHandler(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    // Generic handler that uses reflection to call methods with standard parameter patterns
    // Handles methods with signatures like: Task<PagedResponse<T>> Method(string param1, int limit = X, string? cursor = null)
    if (string.IsNullOrEmpty(request.Method))
    {
        throw new ArgumentException("Method parameter is required and cannot be null or empty.");
    }

    var methodName = request.Method.Replace("circles_", "").Replace("circlesV2_", "");
    methodName = char.ToUpper(methodName[0]) + methodName.Substring(1);

    var method = typeof(CirclesRpcModule).GetMethod(methodName);
    if (method == null)
    {
        throw new RpcMethodNotFoundException(request.Method);
    }

    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
    var methodParams = method.GetParameters();
    var args = new object?[methodParams.Length];

    for (int i = 0; i < methodParams.Length; i++)
    {
        if (parameters != null && i < parameters.Length && parameters[i].ValueKind != JsonValueKind.Null)
        {
            var paramType = methodParams[i].ParameterType;
            var underlyingType = Nullable.GetUnderlyingType(paramType) ?? paramType;

            if (underlyingType == typeof(string))
            {
                // Handle both string values and numbers passed as strings
                args[i] = parameters[i].ValueKind == JsonValueKind.String
                    ? parameters[i].GetString()
                    : parameters[i].ToString();
            }
            else if (underlyingType == typeof(int))
            {
                args[i] = parameters[i].ValueKind == JsonValueKind.Number
                    ? parameters[i].GetInt32()
                    : int.TryParse(parameters[i].GetString(), out var n) ? n
                    : throw new ArgumentException($"Parameter '{methodParams[i].Name}' must be a number, got: {parameters[i]}");
            }
            else if (underlyingType == typeof(long))
            {
                // Handle both numeric JSON values and string representations
                args[i] = parameters[i].ValueKind == JsonValueKind.Number
                    ? parameters[i].GetInt64()
                    : long.TryParse(parameters[i].GetString(), out var l) ? l
                    : throw new ArgumentException($"Parameter '{methodParams[i].Name}' must be a number, got: {parameters[i]}");
            }
            else if (underlyingType == typeof(bool))
            {
                args[i] = parameters[i].GetBoolean();
            }
            else if (paramType == typeof(string[]))
            {
                args[i] = parameters[i].Deserialize<string[]>();
            }
            else
            {
                args[i] = JsonSerializer.Deserialize(parameters[i].GetRawText(), paramType);
            }
        }
        else if (methodParams[i].HasDefaultValue)
        {
            args[i] = methodParams[i].DefaultValue;
        }
        else
        {
            args[i] = null;
        }
    }

    var result = method.Invoke(rpcModule, args);
    if (result is Task task)
    {
        await task;
        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task) ?? new object();
    }

    return result ?? new object();
}

static async Task<object> HandleHealth(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var health = await rpcModule.GetHealth();
    return health.Status == "healthy" ? "Healthy" : $"Unhealthy: {health.Database}, {health.Index}";
}

static async Task<object> HandleTables(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    return await rpcModule.GetTables();
}

// Non-paginated query — returns {columns, rows} only (no hasMore/nextCursor).
// Used by invitation backends and one-shot queries that don't need pagination.
static async Task<object> HandleQuery(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var (query, cursor) = ParseQueryParameters(request);

    var pagedResult = await rpcModule.Query(query, cursor);
    return new QueryResponse(pagedResult.Columns, pagedResult.Rows);
}

static async Task<object> HandleQuery2(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var (query, cursor) = ParseQueryParameters(request);
    return await rpcModule.Query(query, cursor);
}

static (SelectDto Query, string? Cursor) ParseQueryParameters(JsonRpcRequest request)
{
    var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("SelectDto parameter is required");
    }

    var query = JsonSerializer.Deserialize<SelectDto>(parameters[0].GetRawText());
    if (query == null)
    {
        throw new ArgumentException("Invalid SelectDto parameter");
    }

    // Optional cursor parameter for pagination
    string? cursor = null;
    if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
    {
        cursor = parameters[1].GetString();
    }

    return (query, cursor);
}

public static class JsonElementExtensions
{
    public static bool IsNullOrUndefined(this JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }
}
