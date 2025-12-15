using Circles.Rpc.Host;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using Circles.Index.Query.Dto;
using Circles.Index.Common.Dto;

var builder = BuilderSetup.ConfigureBuilder(args);

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

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

app.Map("/ws/subscribe", async (HttpContext context, CirclesSubscriptionService subscriptionService, ILogger<Program> logger) =>
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
        logger.LogInformation("Subscription {SubscriptionId} closed for {RemoteIp}", subscriptionId, remoteIp);
    }
}).DisableAntiforgery();

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
            Id = request.Id,
            Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" }
        });
    }

    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var methodName = request.Method ?? "<unknown>";
    var startTimestamp = Stopwatch.GetTimestamp();
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
            "circles_getTokenHolders" => await ReflectionHandler(request, rpcModule),
            "circlesV2_findPath" => await HandleV2FindPath(request, rpcModule),
            // System & Query Methods
            "circles_events" => await HandleEvents(request, rpcModule),
            "circles_health" => await HandleHealth(request, rpcModule),
            "circles_tables" => await HandleTables(request, rpcModule),
            "circles_query" => await HandleQuery(request, rpcModule),
            // SDK Enablement Methods
            "circles_getProfileView" => await ReflectionHandler(request, rpcModule),
            "circles_getTrustNetworkSummary" => await ReflectionHandler(request, rpcModule),
            "circles_getAggregatedTrustRelationsEnriched" => await ReflectionHandler(request, rpcModule),
            "circles_getValidInviters" => await ReflectionHandler(request, rpcModule),
            "circles_getTransactionHistoryEnriched" => await ReflectionHandler(request, rpcModule),
            "circles_searchProfileByAddressOrName" => await ReflectionHandler(request, rpcModule),
            "circles_getInvitationOrigin" => await ReflectionHandler(request, rpcModule),
            "circles_getAllInvitations" => await ReflectionHandler(request, rpcModule),

            _ => throw new RpcMethodNotFoundException(request.Method)
        };

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        logger.LogInformation(
            "RPC request {Method} (id={Id}) succeeded in {ElapsedMs} ms (remote {RemoteIp})",
            methodName,
            request.Id,
            elapsed.TotalMilliseconds,
            remoteIp);

        return Results.Ok(new JsonRpcResponse
        {
            Id = request.Id,
            Result = rpcResult
        });
    }
    catch (RpcMethodNotFoundException ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        logger.LogWarning(ex,
            "RPC method not found: {Method} from {RemoteIp} after {ElapsedMs} ms",
            ex.MethodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {ex.MethodName}" }
        });
    }
    catch (ArgumentException ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        logger.LogWarning(ex,
            "Invalid params for method: {Method} from {RemoteIp} after {ElapsedMs} ms",
            methodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32602, Message = ex.Message }
        });
    }
    catch (JsonException ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        logger.LogWarning(ex,
            "Invalid JSON params for method: {Method} from {RemoteIp} after {ElapsedMs} ms",
            methodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32602, Message = "Invalid params" }
        });
    }
    catch (Exception ex)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        logger.LogError(ex,
            "Internal Server Error during RPC execution for method: {Method} from {RemoteIp} after {ElapsedMs} ms",
            methodName,
            remoteIp,
            elapsed.TotalMilliseconds);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
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
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    return JsonSerializer.Deserialize<SubscriptionRequest>(payload, options);
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

    // Configure JSON options to match Pathfinder DTOs
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    var flowRequest = JsonSerializer.Deserialize<FlowRequest>(parameters[0].GetRawText(), jsonOptions);
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
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new FilterPredicateArrayConverter());
        options.Converters.Add(new FilterPredicateDtoConverter());
        filterPredicates = JsonSerializer.Deserialize<IFilterPredicateDto[]>(parameters[4].GetRawText(), options);
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
                args[i] = parameters[i].GetString();
            }
            else if (underlyingType == typeof(int))
            {
                args[i] = parameters[i].GetInt32();
            }
            else if (underlyingType == typeof(long))
            {
                args[i] = parameters[i].GetInt64();
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

static async Task<object> HandleQuery(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    return await rpcModule.Query(query, cursor);
}

public static class JsonElementExtensions
{
    public static bool IsNullOrUndefined(this JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Null;
    }
}