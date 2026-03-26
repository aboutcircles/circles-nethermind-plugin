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
using Circles.Rpc.Host.OpenRpc;
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

// ─── Browser root redirect ──────────────────────────────────────────────────
app.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription();

// ─── OpenRPC spec endpoint ───────────────────────────────────────────────────
var openRpcJson = JsonSerializer.SerializeToUtf8Bytes(
    OpenRpcGenerator.Generate(),
    new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true });

app.MapGet("/openrpc.json", () => Results.Bytes(openRpcJson, "application/json"))
    .ExcludeFromDescription();

// Redirect /openrpc to the CirclesTools RPC Query View (comprehensive interactive playground)
app.MapGet("/openrpc", () =>
    Results.Redirect("https://aboutcircles.github.io/CirclesTools/rpcQueryView.html"))
    .ExcludeFromDescription();


// ─── Unified API documentation portal ───────────────────────────────────────
app.MapGet("/docs", (HttpContext ctx) =>
{
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var html = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8"/>
      <meta name="viewport" content="width=device-width, initial-scale=1"/>
      <title>Circles API Documentation</title>
      <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif; background: #0a0e1a; color: #e0e0e0; min-height: 100vh; }
        .header { padding: 3rem 2rem 2rem; text-align: center; background: linear-gradient(135deg, #0d1117 0%, #161b22 100%); border-bottom: 1px solid #21262d; }
        .header h1 { font-size: 2rem; font-weight: 600; color: #f0f0f0; margin-bottom: 0.5rem; }
        .header p { color: #8b949e; font-size: 1.1rem; max-width: 600px; margin: 0 auto; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 1.5rem; padding: 2rem; max-width: 1200px; margin: 0 auto; }
        .card { background: #161b22; border: 1px solid #21262d; border-radius: 12px; padding: 1.5rem; transition: border-color 0.2s, transform 0.2s; }
        .card:hover { border-color: #388bfd; transform: translateY(-2px); }
        .card h2 { font-size: 1.2rem; color: #f0f0f0; margin-bottom: 0.5rem; display: flex; align-items: center; gap: 0.5rem; }
        .card p { color: #8b949e; font-size: 0.9rem; line-height: 1.5; margin-bottom: 1rem; }
        .badge { display: inline-block; font-size: 0.7rem; font-weight: 600; padding: 2px 8px; border-radius: 10px; text-transform: uppercase; }
        .badge-rpc { background: #1f3a5f; color: #58a6ff; }
        .badge-rest { background: #1a3f2a; color: #3fb950; }
        .badge-auth { background: #3d2e00; color: #d29922; }
        .links { display: flex; flex-wrap: wrap; gap: 0.5rem; }
        .links a { display: inline-flex; align-items: center; gap: 0.3rem; font-size: 0.85rem; color: #58a6ff; text-decoration: none; padding: 0.3rem 0.7rem; border: 1px solid #21262d; border-radius: 6px; transition: background 0.2s; }
        .links a:hover { background: #1f2937; }
        .footer { text-align: center; padding: 2rem; color: #484f58; font-size: 0.8rem; }
        .footer a { color: #58a6ff; text-decoration: none; }
      </style>
    </head>
    <body>
      <div class="header">
        <h1>Circles API Documentation</h1>
        <p>Machine-readable specs and interactive docs for all Circles protocol services</p>
      </div>
      <div class="grid">
        <div class="card">
          <h2><span class="badge badge-rpc">JSON-RPC</span> Circles RPC API</h2>
          <p>40+ JSON-RPC 2.0 methods for querying balances, avatars, profiles, trust relations, events, groups, invitations, and transitive transfer paths.</p>
          <div class="links">
            <a href="https://aboutcircles.github.io/CirclesTools/rpcQueryView.html">Interactive Playground</a>
            <a href="/openrpc.json">openrpc.json</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-rest">REST</span> Pathfinder API</h2>
          <p>Compute transitive transfer paths through the Circles trust network. Supports max flow, quantized mode, simulated balances, and debug stages.</p>
          <div class="links">
            <a href="/pathfinder/scalar/v1">Interactive Docs</a>
            <a href="/pathfinder/openapi/v1.json">openapi.json</a>
            <a href="https://aboutcircles.github.io/CirclesTools/rpcQueryView.html?method=circlesV2_findPath">Query Builder</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-auth">Auth</span> Authentication Service</h2>
          <p>Sign-In with Ethereum (SIWE), passkey registration, JWT issuance, JWKS endpoint, and service-to-service authentication.</p>
          <div class="links">
            <a href="/auth/docs">Swagger UI</a>
            <a href="/auth/openapi.json">openapi.json</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-rest">REST</span> Referrals API</h2>
          <p>Invitation links, referral distributions, and at-scale onboarding backend for the Circles protocol.</p>
          <div class="links">
            <a href="/referrals/docs">Swagger UI</a>
            <a href="/referrals/openapi.json">openapi.json</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-rest">REST</span> Profile Pinning Service</h2>
          <p>IPFS profile storage, content pinning, full-text search, and CID resolution. Indexes profiles from on-chain NameRegistry events.</p>
          <div class="links">
            <a href="/profiles/docs">Swagger UI</a>
            <a href="/profiles/openapi.json">openapi.json</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-rest">REST</span> Marketplace API</h2>
          <p>Product catalogs, shopping carts, checkout, order fulfillment, and seller management. Supports Odoo and CodeDispenser adapters.</p>
          <div class="links">
            <a href="/market/docs">Swagger UI</a>
            <a href="/market/docs/v1/swagger.json">openapi.json</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-rest">REST</span> Ethereum JSON-RPC</h2>
          <p>Standard Ethereum JSON-RPC (eth_*, net_*, web3_*) proxied to Nethermind. Use for block queries, transaction submission, and chain state.</p>
          <div class="links">
            <a href="/chain-rpc/">Nethermind Dashboard</a>
          </div>
        </div>
        <div class="card">
          <h2><span class="badge badge-rest">Monitoring</span> Grafana</h2>
          <p>Dashboards for indexer performance, RPC metrics, pathfinder load, cache hit rates, and infrastructure health.</p>
          <div class="links">
            <a href="/grafana">Grafana Dashboard</a>
          </div>
        </div>
      </div>
      <div class="footer">
        <p>All specs are machine-readable — use them for client codegen, SDK generation, or AI agent tool discovery.<br/>
        <a href="https://github.com/aboutcircles">github.com/aboutcircles</a></p>
      </div>
    </body>
    </html>
    """;
    return Results.Content(html, "text/html");
}).ExcludeFromDescription();

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

// ─── Concurrency & rate limiting ─────────────────────────────────────────────
// Concurrency semaphore: limits simultaneous in-flight requests (prevents DB pool exhaustion).
// Rate limiter: per-IP token bucket that counts batch items individually (prevents amplification).
// Both are non-blocking: semaphore returns 503, rate limiter returns 429.
var rpcSemaphore = app.Services.GetRequiredService<SemaphoreSlim>();
var rpcRateLimiter = app.Services.GetRequiredService<RpcRateLimiter>();

// ─── Batch JSON-RPC middleware ────────────────────────────────────────────────
// Routes batch requests: circles_*/circlesV2_* handled locally, eth_*/net_*/web3_* proxied to Nethermind.
// Each circles item acquires a semaphore slot; Nethermind items are batched in a single proxy call.
// Rate limit: entire batch costs N tokens (one per item) from the caller's per-IP bucket.
const int MaxBatchBodySize = 1_048_576; // 1 MB
const int MaxBatchSize = 50; // Max items per batch
var jsonArrayStart = "["u8.ToArray();
var jsonArraySep = ","u8.ToArray();
var jsonArrayEnd = "]"u8.ToArray();
// Match the case-insensitive options configured in BuilderSetup.cs for MapPost deserialization.
// CamelCase naming ensures batch responses use lowercase property names (jsonrpc, result, error)
// consistent with Nethermind's responses and the JSON-RPC 2.0 convention.
var batchJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

app.Use(async (context, next) =>
{
    if (context.Request.Method == "POST" && context.Request.Path == "/")
    {
        var contentType = context.Request.ContentType;
        if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        context.Request.EnableBuffering();
        var buf = new byte[1];
        var bytesRead = await context.Request.Body.ReadAsync(buf, 0, 1);
        context.Request.Body.Position = 0;
        var firstByte = bytesRead > 0 ? buf[0] : -1;

        if (firstByte == '[') // JSON array = batch request
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var nethermindClient = context.RequestServices.GetRequiredService<NethermindRpcClient>();
            var rpcModule = context.RequestServices.GetRequiredService<CirclesRpcModule>();
            var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var startTimestamp = Stopwatch.GetTimestamp();

            // Enforce body size limit
            if (context.Request.ContentLength > MaxBatchBodySize)
            {
                context.Response.StatusCode = 413;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                {
                    Error = new JsonRpcError { Code = -32600, Message = $"Batch request too large (max {MaxBatchBodySize / 1024}KB)" }
                }, batchJsonOptions);
                return;
            }

            try
            {
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);

                if (ms.Length > MaxBatchBodySize)
                {
                    context.Response.StatusCode = 413;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                    {
                        Error = new JsonRpcError { Code = -32600, Message = $"Batch request too large (max {MaxBatchBodySize / 1024}KB)" }
                    }, batchJsonOptions);
                    return;
                }

                ms.Position = 0;
                using var doc = await JsonDocument.ParseAsync(ms);
                var batchArray = doc.RootElement;

                if (batchArray.ValueKind != JsonValueKind.Array)
                {
                    await next();
                    return;
                }

                var batchLen = batchArray.GetArrayLength();

                // Enforce batch size limit
                if (batchLen > MaxBatchSize)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                    {
                        Error = new JsonRpcError { Code = -32600, Message = $"Batch too large: {batchLen} items (max {MaxBatchSize})" }
                    }, batchJsonOptions);
                    return;
                }

                // JSON-RPC 2.0 spec: empty array is invalid ("at least one value" required)
                if (batchLen == 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                    {
                        Error = new JsonRpcError { Code = -32600, Message = "Invalid Request: empty batch" }
                    }, batchJsonOptions);
                    return;
                }

                // Per-IP rate limit: batch costs N tokens (one per item).
                // Checked before any processing to fail fast on abusive callers.
                if (!rpcRateLimiter.TryAcquire(remoteIp, batchLen))
                {
                    RpcMetrics.RateLimitedTotal.Inc();
                    logger.LogWarning("Rate limited batch ({BatchSize} items) from {RemoteIp}", batchLen, remoteIp);
                    context.Response.StatusCode = 429;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                    {
                        Error = new JsonRpcError { Code = -32000, Message = "Rate limit exceeded" }
                    }, batchJsonOptions);
                    return;
                }

                RpcMetrics.BatchTotal.Inc();
                RpcMetrics.BatchSize.Observe(batchLen);
                logger.LogInformation("Batch JSON-RPC ({BatchSize} items) from {RemoteIp}", batchLen, remoteIp);

                // Classify each request
                var responses = new object?[batchLen];
                var circlesItems = new List<(int Index, JsonRpcRequest Request)>();
                var nethermindItems = new List<(int Index, JsonElement Raw)>();

                int idx = 0;
                foreach (var element in batchArray.EnumerateArray())
                {
                    var method = element.TryGetProperty("method", out var methodProp)
                        ? methodProp.GetString() : null;
                    var id = element.TryGetProperty("id", out var idProp) ? idProp : JsonRpcId.Null;
                    var jsonrpc = element.TryGetProperty("jsonrpc", out var jsonrpcProp)
                        ? jsonrpcProp.GetString() : null;

                    if (jsonrpc != "2.0" || string.IsNullOrEmpty(method))
                    {
                        responses[idx] = new JsonRpcErrorResponse
                        {
                            Id = id,
                            Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" }
                        };
                    }
                    else if (IsCirclesMethod(method))
                    {
                        try
                        {
                            var req = JsonSerializer.Deserialize<JsonRpcRequest>(
                                element.GetRawText(), batchJsonOptions);
                            if (req != null) circlesItems.Add((idx, req));
                            else responses[idx] = new JsonRpcErrorResponse
                            {
                                Id = id,
                                Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" }
                            };
                        }
                        catch (JsonException)
                        {
                            responses[idx] = new JsonRpcErrorResponse
                            {
                                Id = id,
                                Error = new JsonRpcError { Code = -32600, Message = "Invalid Request: malformed item" }
                            };
                        }
                    }
                    else if (IsProxyAllowed(method))
                    {
                        nethermindItems.Add((idx, element.Clone()));
                    }
                    else
                    {
                        responses[idx] = new JsonRpcErrorResponse
                        {
                            Id = id,
                            Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {method}" }
                        };
                    }
                    idx++;
                }

                // Process circles items sequentially with semaphore
                var circlesTask = Task.Run(async () =>
                {
                    foreach (var (i, req) in circlesItems)
                    {
                        if (!await rpcSemaphore.WaitAsync(0))
                        {
                            RpcMetrics.RejectedTotal.Inc();
                            responses[i] = new JsonRpcErrorResponse
                            {
                                Id = JsonRpcId.CoerceId(req.Id),
                                Error = new JsonRpcError { Code = -32000, Message = "Server busy" }
                            };
                            continue;
                        }
                        try
                        {
                            responses[i] = await DispatchSingleRequest(
                                req, rpcModule, nethermindClient, logger, remoteIp);
                        }
                        finally
                        {
                            rpcSemaphore.Release();
                        }
                    }
                });

                // Proxy Nethermind items in a single batch call.
                // JSON-RPC 2.0 batch responses are unordered — correlate by id, not position.
                var nethermindTask = Task.Run(async () =>
                {
                    if (nethermindItems.Count == 0) return;

                    try
                    {
                        var subBatch = nethermindItems.Select(x => x.Raw).ToArray();
                        var subBatchBytes = JsonSerializer.SerializeToUtf8Bytes(subBatch);
                        var result = await nethermindClient.ForwardRawRequest(subBatchBytes);

                        if (result.ValueKind == JsonValueKind.Array)
                        {
                            // Build id→response lookup from Nethermind's response array.
                            // JSON-RPC ids can be string, number, or null — use raw JSON text as key.
                            var responseById = new Dictionary<string, JsonElement>();
                            foreach (var resp in result.EnumerateArray())
                            {
                                var idKey = resp.TryGetProperty("id", out var respId)
                                    ? respId.GetRawText() : "null";
                                responseById.TryAdd(idKey, resp); // first wins on duplicate ids
                            }

                            // Match each sent request to its response by id
                            foreach (var (ni, raw) in nethermindItems)
                            {
                                var reqIdKey = raw.TryGetProperty("id", out var reqId)
                                    ? reqId.GetRawText() : "null";

                                if (responseById.TryGetValue(reqIdKey, out var matched))
                                    responses[ni] = matched;
                                else
                                {
                                    // No response for this id — notification (no id) or dropped
                                    var fallbackId = raw.TryGetProperty("id", out var fbId)
                                        ? fbId.Clone() : JsonRpcId.Null;
                                    responses[ni] = new JsonRpcErrorResponse
                                    {
                                        Id = fallbackId,
                                        Error = new JsonRpcError { Code = -32603, Message = "Missing proxy response" }
                                    };
                                }
                            }
                        }
                        else
                        {
                            // Nethermind returned a non-array (single error object)
                            logger.LogWarning("Nethermind returned non-array for batch of {Count} items", nethermindItems.Count);
                            foreach (var (ni, raw) in nethermindItems)
                            {
                                var errId = raw.TryGetProperty("id", out var errIdProp) ? errIdProp.Clone() : JsonRpcId.Null;
                                responses[ni] = new JsonRpcErrorResponse
                                {
                                    Id = errId,
                                    Error = new JsonRpcError { Code = -32603, Message = "Unexpected proxy response" }
                                };
                            }
                        }
                    }
                    catch (Exception proxyEx)
                    {
                        logger.LogError(proxyEx, "Failed to proxy Nethermind batch ({Count} items) from {RemoteIp}",
                            nethermindItems.Count, remoteIp);
                        foreach (var (i, raw) in nethermindItems)
                        {
                            var id = raw.TryGetProperty("id", out var idProp) ? idProp.Clone() : JsonRpcId.Null;
                            responses[i] = new JsonRpcErrorResponse
                            {
                                Id = id,
                                Error = new JsonRpcError { Code = -32603, Message = "Proxy error" }
                            };
                        }
                    }
                });

                await Task.WhenAll(circlesTask, nethermindTask);

                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                logger.LogInformation(
                    "Batch JSON-RPC completed in {ElapsedMs} ms ({CirclesCount} circles, {NethermindCount} proxied) from {RemoteIp}",
                    elapsed.TotalMilliseconds, circlesItems.Count, nethermindItems.Count, remoteIp);

                // Sweep for null slots (shouldn't happen, but safety net)
                for (int k = 0; k < responses.Length; k++)
                {
                    responses[k] ??= new JsonRpcErrorResponse
                    {
                        Error = new JsonRpcError { Code = -32603, Message = "Internal error: no response generated" }
                    };
                }

                // Serialize each element by runtime type (System.Text.Json serializes object[] by
                // declared type, which produces empty {} for JsonRpcResponse/JsonRpcErrorResponse)
                // Serialize batch response into a buffer, then flush once.
                // Each item is serialized by runtime type (System.Text.Json object[] bug workaround).
                context.Response.ContentType = "application/json";
                using var responseBuffer = new MemoryStream();
                responseBuffer.Write(jsonArrayStart);
                for (int k = 0; k < responses.Length; k++)
                {
                    if (k > 0) responseBuffer.Write(jsonArraySep);
                    var item = responses[k]!;
                    if (item is JsonElement je)
                        JsonSerializer.Serialize(responseBuffer, je);
                    else
                        JsonSerializer.Serialize(responseBuffer, item, item.GetType(), batchJsonOptions);
                }
                responseBuffer.Write(jsonArrayEnd);
                responseBuffer.Position = 0;
                await responseBuffer.CopyToAsync(context.Response.Body);
            }
            catch (JsonException jsonEx)
            {
                logger.LogWarning(jsonEx, "Malformed batch JSON-RPC request from {RemoteIp}", remoteIp);
                RpcMetrics.ErrorsTotal.WithLabels("batch", "parse_error").Inc();
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                {
                    Error = new JsonRpcError { Code = -32700, Message = "Parse error" }
                }, batchJsonOptions);
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                RpcMetrics.ErrorsTotal.WithLabels("batch", "internal_error").Inc();
                logger.LogError(ex, "Batch handler error from {RemoteIp} after {ElapsedMs} ms",
                    remoteIp, elapsed.TotalMilliseconds);

                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                {
                    Error = new JsonRpcError { Code = -32603, Message = "Internal batch error" }
                }, batchJsonOptions);
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

    // Per-IP rate limit: single request costs 1 token
    if (!rpcRateLimiter.TryAcquire(remoteIp))
    {
        RpcMetrics.RateLimitedTotal.Inc();
        return Results.Json(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32000, Message = "Rate limit exceeded" }
        }, statusCode: 429);
    }

    // Concurrency guard — reject immediately when at capacity
    if (!await rpcSemaphore.WaitAsync(0))
    {
        RpcMetrics.RejectedTotal.Inc();
        return Results.Json(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32000, Message = "Server busy" }
        }, statusCode: 503);
    }
    var nethermindClient = context.RequestServices.GetRequiredService<NethermindRpcClient>();

    try
    {
        var result = await DispatchSingleRequest(request, rpcModule, nethermindClient, logger, remoteIp);
        return Results.Json(result);
    }
    finally
    {
        rpcSemaphore.Release();
    }

}).DisableAntiforgery();

// ─── Helpers ─────────────────────────────────────────────────────────────────
// Method classification delegated to RpcMethodClassifier (testable public class).

static bool IsCirclesMethod(string? method) => RpcMethodClassifier.IsCirclesMethod(method);
static bool IsProxyAllowed(string? method) => RpcMethodClassifier.IsProxyAllowed(method);

/// <summary>
/// Dispatches a single JSON-RPC request. Handles circles_* locally, proxies eth_*/net_*/web3_* to Nethermind.
/// Returns JsonRpcResponse, JsonRpcErrorResponse, or JsonElement (for proxied responses).
/// Tracks per-method metrics. Does NOT manage the concurrency semaphore.
/// </summary>
static async Task<object> DispatchSingleRequest(
    JsonRpcRequest request,
    CirclesRpcModule rpcModule,
    NethermindRpcClient nethermindClient,
    ILogger logger,
    string remoteIp)
{
    var methodName = request.Method ?? "<unknown>";
    var metricLabel = RpcMethodClassifier.SafeMetricLabel(request.Method);
    var startTimestamp = Stopwatch.GetTimestamp();

    RpcMetrics.RequestsTotal.WithLabels(metricLabel).Inc();
    RpcMetrics.InFlightRequests.WithLabels(metricLabel).Inc();

    try
    {
        object rpcResult = request.Method switch
        {
            // OpenRPC discovery
            "rpc.discover" => OpenRpcGenerator.Generate(),
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
            "circles_events" => await HandleEventsLegacy(request, rpcModule),
            "circles_events_paginated" => await HandleEventsPaginated(request, rpcModule),
            "circles_health" => await HandleHealth(request, rpcModule),
            "circles_tables" => await HandleTables(request, rpcModule),
            "circles_query" => await HandleQuery(request, rpcModule),
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

            _ => throw new RpcMethodNotFoundException(request.Method ?? "<unknown>")
        };

        logger.LogInformation(
            "RPC {Method} (id={Id}) succeeded in {ElapsedMs} ms from {RemoteIp}",
            methodName, request.Id, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, remoteIp);

        return new JsonRpcResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Result = rpcResult
        };
    }
    catch (RpcMethodNotFoundException)
    {
        // Proxy safe read-only methods to Nethermind
        if (!IsProxyAllowed(request.Method))
        {
            RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "method_not_found").Inc();
            return new JsonRpcErrorResponse
            {
                Id = JsonRpcId.CoerceId(request.Id),
                Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {methodName}" }
            };
        }

        try
        {
            RpcMetrics.ProxiedTotal.WithLabels(metricLabel).Inc();
            var proxyResult = await nethermindClient.ForwardRpcRequest(
                request.Method!, request.Id, request.Params);
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            RpcMetrics.ProxyDuration.WithLabels(metricLabel).Observe(elapsed.TotalSeconds);
            return proxyResult; // JsonElement — already a complete JSON-RPC response
        }
        catch (Exception proxyEx)
        {
            RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "proxy_error").Inc();
            logger.LogError(proxyEx, "Failed to proxy {Method} from {RemoteIp}",
                methodName, remoteIp);
            return new JsonRpcErrorResponse
            {
                Id = JsonRpcId.CoerceId(request.Id),
                Error = new JsonRpcError { Code = -32603, Message = "Proxy error" }
            };
        }
    }
    catch (ArgumentException ex)
    {
        RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "invalid_params").Inc();
        return new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32602, Message = ex.Message }
        };
    }
    catch (JsonException)
    {
        RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "invalid_json").Inc();
        return new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32602, Message = "Invalid params" }
        };
    }
    catch (Exception ex)
    {
        RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "internal_error").Inc();
        logger.LogError(ex, "Internal error for {Method} from {RemoteIp}",
            methodName, remoteIp);
        return new JsonRpcErrorResponse
        {
            Id = JsonRpcId.CoerceId(request.Id),
            Error = new JsonRpcError { Code = -32603, Message = "Internal server error" }
        };
    }
    finally
    {
        RpcMetrics.InFlightRequests.WithLabels(metricLabel).Dec();
        RpcMetrics.RequestDuration.WithLabels(metricLabel).Observe(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
    }
}

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

static (string? Address, long? FromBlock, long? ToBlock, string[]? EventTypes,
    IFilterPredicateDto[]? FilterPredicates, bool? SortAscending, int? Limit, string? Cursor)
    ParseEventParameters(JsonRpcRequest request)
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
        return (null, null, null, null, null, false, null, null);
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

    if (parameters.Length > 6 && parameters[6].ValueKind != JsonValueKind.Null)
    {
        limit = parameters[6].GetInt32();
    }

    if (parameters.Length > 7 && parameters[7].ValueKind != JsonValueKind.Null)
    {
        cursor = parameters[7].GetString();
    }

    return (address, fromBlock, toBlock, eventTypes, filterPredicates, sortAscending, limit, cursor);
}

// Non-paginated events — returns plain array via EventsResponseJsonConverter for backwards compatibility.
static async Task<object> HandleEventsLegacy(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var p = ParseEventParameters(request);
    var pagedResult = await rpcModule.GetEvents(p.Address, p.FromBlock, p.ToBlock,
        p.EventTypes, p.FilterPredicates, p.SortAscending, p.Limit, p.Cursor);
    return new EventsResponse(pagedResult.Events);
}

// Paginated events — returns {events, hasMore, nextCursor}.
static async Task<object> HandleEventsPaginated(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var p = ParseEventParameters(request);
    return await rpcModule.GetEvents(p.Address, p.FromBlock, p.ToBlock,
        p.EventTypes, p.FilterPredicates, p.SortAscending, p.Limit, p.Cursor);
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
