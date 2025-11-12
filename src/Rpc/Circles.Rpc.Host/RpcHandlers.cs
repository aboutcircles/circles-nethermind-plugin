using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query.Dto;
using Nethermind.Core;
using Circles.Pathfinder.DTOs;

namespace Circles.Rpc.Host;

public static partial class CirclesRpcHandlers
{
    // Balance & Token Methods
    public static async Task<object> HandleGetTotalBalance(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        var asTimeCircles = parameters.Length > 1 ? parameters[1].GetBoolean() : false;

        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = await rpcModule.circles_getTotalBalance(addressObject, asTimeCircles);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleCirclesV2GetTotalBalance(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        var asTimeCircles = parameters.Length > 1 ? parameters[1].GetBoolean() : true;

        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = await rpcModule.circlesV2_getTotalBalance(addressObject, asTimeCircles);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetTokenBalances(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = rpcModule.circles_getTokenBalances(addressObject);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetTokenInfo(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'tokenAddress' parameter.");

        var tokenAddress = parameters[0].GetString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(tokenAddress))
            throw new ArgumentException("Invalid 'tokenAddress' parameter.");

        var addressObject = new Address(tokenAddress);
        var resultWrapper = rpcModule.circles_getTokenInfo(addressObject);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetTokenInfoBatch(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'tokenAddresses' parameter.");

        var tokenAddressesArray = parameters[0].EnumerateArray().Select(p => new Address(p.GetString()?.ToLowerInvariant() ?? string.Empty)).ToArray();
        if (tokenAddressesArray.Length == 0)
            throw new ArgumentException("Invalid 'tokenAddresses' parameter.");

        var resultWrapper = await rpcModule.circles_getTokenInfoBatch(tokenAddressesArray);
        return resultWrapper.Result;
    }

    // Avatar & Profile Methods
    public static async Task<object> HandleGetAvatarInfo(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = rpcModule.circles_getAvatarInfo(addressObject);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetAvatarInfoBatch(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'addresses' parameter.");

        var addressesArray = parameters[0].EnumerateArray().Select(p => new Address(p.GetString()?.ToLowerInvariant() ?? string.Empty)).ToArray();
        if (addressesArray.Length == 0)
            throw new ArgumentException("Invalid 'addresses' parameter.");

        var resultWrapper = rpcModule.circles_getAvatarInfoBatch(addressesArray);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetProfileCid(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = rpcModule.circles_getProfileCid(addressObject);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetProfileCidBatch(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'cids' parameter.");

        var cidsArray = parameters[0].EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToArray();
        if (cidsArray.Length == 0)
            throw new ArgumentException("Invalid 'cids' parameter.");

        var resultWrapper = await rpcModule.circles_getProfileByCidBatch(cidsArray);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetProfileByAddress(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = await rpcModule.circles_getProfileByAddress(addressObject);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetProfileByAddressBatch(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'addresses' parameter.");

        var addressesArray = parameters[0].EnumerateArray()
            .Select(p => p.GetString()?.ToLowerInvariant())
            .Select(addr => addr == null ? null : new Address(addr))
            .ToArray();
        if (addressesArray.Length == 0)
            throw new ArgumentException("Invalid 'addresses' parameter.");

        var resultWrapper = await rpcModule.circles_getProfileByAddressBatch(addressesArray);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleSearchProfiles(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'text' parameter.");

        var text = parameters[0].GetString() ?? string.Empty;
        var limit = parameters.Length > 1 ? parameters[1].GetInt32() : 20;
        var offset = parameters.Length > 2 ? parameters[2].GetInt32() : 0;
        var types = parameters.Length > 3 ? parameters[3].EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToArray() : null;

        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Invalid 'text' parameter.");

        var resultWrapper = await rpcModule.circles_searchProfiles(text, limit, offset, types);
        return resultWrapper.Result;
    }

    // Trust & Network Methods
    public static async Task<object> HandleGetTrustRelations(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'address' parameter.");

        var address = parameters[0].GetString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Invalid 'address' parameter.");

        var addressObject = new Address(address);
        var resultWrapper = await rpcModule.circles_getTrustRelations(addressObject);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetCommonTrust(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 2)
            throw new ArgumentException("Missing 'address1' and 'address2' parameters.");

        var address1 = parameters[0].GetString()?.ToLowerInvariant();
        var address2 = parameters[1].GetString()?.ToLowerInvariant();
        var version = parameters.Length > 2 ? (int?)parameters[2].GetInt32() : null;

        if (string.IsNullOrEmpty(address1) || string.IsNullOrEmpty(address2))
            throw new ArgumentException("Invalid address parameters.");

        var address1Object = new Address(address1);
        var address2Object = new Address(address2);
        var resultWrapper = await rpcModule.circles_getCommonTrust(address1Object, address2Object, version);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleGetNetworkSnapshot(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var resultWrapper = await rpcModule.circles_getNetworkSnapshot();
        return resultWrapper.Result;
    }

    public static async Task<object> HandleV2FindPath(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'flowRequest' parameter.");

        var flowRequestJson = parameters[0].GetRawText();
        var flowRequest = JsonSerializer.Deserialize<FlowRequest>(flowRequestJson);

        if (flowRequest == null)
            throw new ArgumentException("Invalid 'flowRequest' parameter.");

        var resultWrapper = await rpcModule.circlesV2_findPath(flowRequest);
        return resultWrapper.Result;
    }

    // System & Query Methods
    public static async Task<object> HandleEvents(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();

        Address? address = null;
        long? fromBlock = null;
        long? toBlock = null;
        string[]? eventTypes = null;
        FilterPredicateDto[]? filterPredicates = null;
        bool? sortAscending = false;

        if (parameters.Length > 0 && parameters[0].ValueKind != JsonValueKind.Null)
        {
            var addressStr = parameters[0].GetString()?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(addressStr))
                address = new Address(addressStr);
        }

        if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
            fromBlock = parameters[1].GetInt64();

        if (parameters.Length > 2 && parameters[2].ValueKind != JsonValueKind.Null)
            toBlock = parameters[2].GetInt64();

        if (parameters.Length > 3 && parameters[3].ValueKind != JsonValueKind.Null)
            eventTypes = parameters[3].EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToArray();

        if (parameters.Length > 4 && parameters[4].ValueKind != JsonValueKind.Null)
        {
            var filterJson = parameters[4].GetRawText();
            filterPredicates = JsonSerializer.Deserialize<FilterPredicateDto[]>(filterJson);
        }

        if (parameters.Length > 5 && parameters[5].ValueKind != JsonValueKind.Null)
            sortAscending = parameters[5].GetBoolean();

        var resultWrapper = rpcModule.circles_events(address, fromBlock, toBlock, eventTypes, filterPredicates, sortAscending);
        return resultWrapper.Result;
    }

    public static async Task<object> HandleHealth(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var resultWrapper = rpcModule.circles_health();
        return resultWrapper.Result;
    }

    public static async Task<object> HandleTables(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var resultWrapper = await rpcModule.circles_tables();
        return resultWrapper.Result;
    }

    public static async Task<object> HandleQuery(JsonRpcRequest request, Settings settings, ILogger logger, CirclesRpcModule rpcModule)
    {
        var parameters = request.Params.EnumerateArray().ToArray();
        if (parameters.Length < 1)
            throw new ArgumentException("Missing 'query' parameter.");

        var queryJson = parameters[0].GetRawText();
        var query = JsonSerializer.Deserialize<SelectDto>(queryJson);

        if (query == null)
            throw new ArgumentException("Invalid 'query' parameter.");

        var resultWrapper = rpcModule.circles_query(query);
        return resultWrapper.Result;
    }
}