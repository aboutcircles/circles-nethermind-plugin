using System.Text.Json;
using Circles.Common.Dto;
using Circles.Index.Query.Dto;
using Circles.Rpc.Host.OpenRpc;

namespace Circles.Rpc.Host.Dispatch;

/// <summary>
/// Per-method handlers + reusable parameter parsers for the JSON-RPC dispatch table.
/// Each Handle{Method} deserializes the request params and forwards to <see cref="CirclesRpcModule"/>.
/// </summary>
public static class RpcHandlers
{
    public static async Task<object> HandleGetTotalBalance(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    public static async Task<object> HandleV2GetTotalBalance(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    public static async Task<object> HandleGetTokenBalances(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    public static async Task<object> HandleGetTokenInfo(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Token address parameter is required");
        }

        return (object?)await rpcModule.GetTokenInfo(parameters[0]) ?? new { };
    }

    public static async Task<object> HandleGetTokenInfoBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Token addresses array parameter is required");
        }

        return await rpcModule.GetTokenInfoBatch(parameters[0]);
    }

    public static async Task<object> HandleGetAvatarInfo(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    public static async Task<object> HandleGetAvatarInfoBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Addresses array parameter is required");
        }

        return await rpcModule.GetAvatarInfoBatch(parameters[0]);
    }

    public static async Task<object> HandleGetProfileCid(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Address parameter is required");
        }

        var cid = await rpcModule.GetProfileCid(parameters[0]);
        return cid ?? throw new ArgumentException("Profile CID not found");
    }

    public static async Task<object> HandleGetProfileCidBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Addresses array parameter is required");
        }

        return await rpcModule.GetProfileCidBatch(parameters[0]);
    }

    public static async Task<object> HandleGetProfileByCid(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("CID parameter is required");
        }

        var profile = await rpcModule.GetProfileByCid(parameters[0]);
        return profile ?? throw new ArgumentException("Profile not found for CID");
    }

    public static async Task<object> HandleGetProfileByCidBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("CIDs array parameter is required");
        }

        return await rpcModule.GetProfileByCidBatch(parameters[0]);
    }

    public static async Task<object> HandleGetProfileByAddress(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Address parameter is required");
        }

        return await rpcModule.GetProfileByAddress(parameters[0]);
    }

    public static async Task<object> HandleGetProfileByAddressBatch(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[][]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Addresses array parameter is required");
        }

        return await rpcModule.GetProfileByAddressBatch(parameters[0]);
    }

    public static async Task<object> HandleSearchProfiles(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());
        var parsed = Circles.Rpc.Host.Wire.SearchProfilesRequestParser.Parse(parameters);

        var searchResults = await rpcModule.SearchProfiles(
            parsed.Text, parsed.Limit, parsed.Offset, parsed.Types, parsed.GroupType);
        var transformedResults = searchResults.Results.Select(item =>
        {
            // Extract properties from AvatarInfo
            var avatarInfo = item.AvatarInfo;
            var address = avatarInfo.Avatar; // Remote expects "address" instead of "avatar"
            var cid = avatarInfo.CidV0; // Remote expects "cid"
            var avatarType = avatarInfo.Type; // Remote expects "avatarType"

            // Extract properties from Profile (JsonElement). Extended group-profile fields
            // are surfaced when the underlying proxy result populated them; null values are
            // dropped from the JSON output by the global DefaultIgnoreCondition=WhenWritingNull
            // serializer setting, so legacy profiles stay byte-identical to the pre-change baseline.
            var profile = item.Profile;
            string? GetStr(string key) =>
                profile?.TryGetProperty(key, out var el) == true && el.ValueKind != JsonValueKind.Null
                    ? el.GetString() : null;
            double? GetNum(string key) =>
                profile?.TryGetProperty(key, out var el) == true && el.ValueKind == JsonValueKind.Number
                    ? el.GetDouble() : null;
            string[]? GetStrArr(string key) =>
                profile?.TryGetProperty(key, out var el) == true && el.ValueKind == JsonValueKind.Array
                    ? el.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToArray()
                    : null;

            // Construct the flattened anonymous object
            return new
            {
                address = address,
                cid = cid,
                name = GetStr("name"),
                description = GetStr("description"),
                previewImageUrl = GetStr("previewImageUrl"),
                avatarType = avatarType,
                externalWebsite = GetStr("externalWebsite"),
                minRepScore = GetNum("minRepScore"),
                membershipFee = GetNum("membershipFee"),
                additionalCriteria = GetStrArr("additionalCriteria"),
                groupType = GetStr("groupType"),
                contactEmail = GetStr("contactEmail"),
                contactWebsite = GetStr("contactWebsite")
            };
        }).ToArray();

        return transformedResults;
    }

    public static async Task<object> HandleGetTrustRelations(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<string[]>(request.Params.GetRawText());
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Address parameter is required");
        }

        return await rpcModule.GetTrustRelations(parameters[0]);
    }

    public static async Task<object> HandleGetCommonTrust(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    public static async Task<object> HandleGetNetworkSnapshot(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        return await rpcModule.GetNetworkSnapshot();
    }

    public static async Task<object> HandleV2FindPath(JsonRpcRequest request, CirclesRpcModule rpcModule)
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

    public static (string? Address, long? FromBlock, long? ToBlock, string[]? EventTypes,
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
    public static async Task<object> HandleEventsLegacy(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var p = ParseEventParameters(request);
        var pagedResult = await rpcModule.GetEvents(p.Address, p.FromBlock, p.ToBlock,
            p.EventTypes, p.FilterPredicates, p.SortAscending, p.Limit, p.Cursor);
        return new EventsResponse(pagedResult.Events);
    }

    // Paginated events — returns {events, hasMore, nextCursor}.
    public static async Task<object> HandleEventsPaginated(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var p = ParseEventParameters(request);
        return await rpcModule.GetEvents(p.Address, p.FromBlock, p.ToBlock,
            p.EventTypes, p.FilterPredicates, p.SortAscending, p.Limit, p.Cursor);
    }

    public static async Task<object> ReflectionHandler(JsonRpcRequest request, CirclesRpcModule rpcModule)
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
            // Dispatch drift: switch arm exists but CirclesRpcModule has no matching member.
            // Not a user error — surfaces as -32603 via the generic catch in the dispatcher.
            throw new InvalidOperationException(
                $"Dispatched method '{request.Method}' has no matching public member " +
                $"'{methodName}' on CirclesRpcModule.");
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

    public static async Task<object> HandleGetBlockByTimestamp(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var parameters = JsonSerializer.Deserialize<JsonElement[]>(request.Params.GetRawText());

        if (parameters == null || parameters.Length == 0 || parameters[0].ValueKind == JsonValueKind.Null)
            throw new ArgumentException("timestamp parameter is required");

        if (parameters[0].ValueKind != JsonValueKind.Number || !parameters[0].TryGetInt64(out var timestamp))
            throw new ArgumentException("timestamp must be an integer");

        string? direction = null;
        if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
            direction = parameters[1].GetString();

        return await rpcModule.GetBlockByTimestamp(timestamp, direction);
    }

    public static async Task<object> HandleHealth(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var health = await rpcModule.GetHealth();
        return health.Status == "healthy" ? "Healthy" : $"Unhealthy: {health.Database}, {health.Index}";
    }

    public static async Task<object> HandleTables(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        return await rpcModule.GetTables();
    }

    // Non-paginated query — returns {columns, rows} only (no hasMore/nextCursor).
    // Used by invitation backends and one-shot queries that don't need pagination.
    public static async Task<object> HandleQuery(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var (query, cursor) = ParseQueryParameters(request);

        var pagedResult = await rpcModule.Query(query, cursor);
        return new QueryResponse(pagedResult.Columns, pagedResult.Rows);
    }

    public static async Task<object> HandleQuery2(JsonRpcRequest request, CirclesRpcModule rpcModule)
    {
        var (query, cursor) = ParseQueryParameters(request);
        return await rpcModule.Query(query, cursor);
    }

    public static (SelectDto Query, string? Cursor) ParseQueryParameters(JsonRpcRequest request)
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
}
