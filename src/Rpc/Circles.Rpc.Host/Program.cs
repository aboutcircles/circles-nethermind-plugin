
using Circles.Rpc.Host;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using Circles.Index.Query.Dto;
using Circles.Pathfinder.DTOs;

var builder = BuilderSetup.ConfigureBuilder(args);

var app = builder.Build();

app.UseHttpMetrics();
app.UseResponseCompression();
app.MapMetrics();

app.MapHealthChecks("/live", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("live")
});

// readiness: nethermind sync status + pathfinder connectivity
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("nethermind-sync") || hc.Tags.Contains("pathfinder-connection"),
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

// ─── Routes ─────────────────────────────────────────────────────────────────

app.MapPost("/", async (
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
            "circlesV2_findPath" => await HandleV2FindPath(request, rpcModule),
            // System & Query Methods
            "circles_events" => await HandleEvents(request, rpcModule),
            "circles_health" => await HandleHealth(request, rpcModule),
            "circles_tables" => await HandleTables(request, rpcModule),
            "circles_query" => await HandleQuery(request, rpcModule),

            _ => throw new RpcMethodNotFoundException(request.Method)
        };

        return Results.Ok(new JsonRpcResponse
        {
            Id = request.Id,
            Result = rpcResult
        });
    }
    catch (RpcMethodNotFoundException ex)
    {
        logger.LogWarning("RPC Method not found: {Method}", ex.MethodName);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {ex.MethodName}" }
        });
    }
    catch (ArgumentException ex)
    {
        logger.LogWarning(ex, "Invalid params for method: {Method}", request.Method);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32602, Message = ex.Message }
        });
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "Invalid JSON params for method: {Method}", request.Method);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32602, Message = "Invalid params" }
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Internal Server Error during RPC execution for method: {Method}", request.Method);
        return Results.Ok(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32603, Message = "Internal server error" }
        });
    }

}).DisableAntiforgery();

app.Run();

// ─── RPC Handler Methods ──────────────────────────────────────────────────

static async Task<object> HandleGetTotalBalance(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    string address = parameters[0];
    bool asTimeCircles = true;
    if (parameters.Length > 1 && parameters[1] != null)
    {
        if (bool.TryParse(parameters[1], out var parsedValue))
        {
            asTimeCircles = parsedValue;
        }
    }

    var result = await rpcModule.GetTotalBalance(address, 1, asTimeCircles);
    return result;
}

static async Task<object> HandleV2GetTotalBalance(JsonRpcRequest request, CirclesRpcModule rpcModule)
{
    var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
    if (parameters == null || parameters.Length == 0)
    {
        throw new ArgumentException("Address parameter is required");
    }

    string address = parameters[0];
    bool asTimeCircles = true;
    if (parameters.Length > 1 && parameters[1] != null)
    {
        if (bool.TryParse(parameters[1], out var parsedValue))
        {
            asTimeCircles = parsedValue;
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

    return await rpcModule.GetTokenInfo(parameters[0]);
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

    return await rpcModule.SearchProfiles(text, limit, offset, types);
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

    if (parameters == null || parameters.Length == 0)
    {
        return await rpcModule.GetEvents(null, null, null, null, null, false);
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

    return await rpcModule.GetEvents(address, fromBlock, toBlock, eventTypes, filterPredicates, sortAscending);
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
    return await rpcModule.Query(query);
}

public static class JsonElementExtensions
{
    public static bool IsNullOrUndefined(this JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Null;
    }
}