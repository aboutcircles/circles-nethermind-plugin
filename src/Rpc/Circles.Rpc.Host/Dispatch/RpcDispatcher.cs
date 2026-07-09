using System.Diagnostics;
using System.Text.Json;
using Circles.Rpc.Host.OpenRpc;

namespace Circles.Rpc.Host.Dispatch;

/// <summary>
/// JSON-RPC method router: maps <see cref="JsonRpcRequest.Method"/> to a handler in
/// <see cref="RpcHandlers"/>, proxies unsupported (but allowed) methods to Nethermind,
/// and emits per-method metrics. Does NOT manage the concurrency semaphore — that is the
/// caller's responsibility.
/// </summary>
public static class RpcDispatcher
{
    // Method classification delegated to RpcMethodClassifier (testable public class).
    public static bool IsCirclesMethod(string? method) => RpcMethodClassifier.IsCirclesMethod(method);
    public static bool IsProxyAllowed(string? method) => RpcMethodClassifier.IsProxyAllowed(method);

    /// <summary>
    /// Dispatches a single JSON-RPC request. Handles circles_* locally, proxies eth_*/net_*/web3_* to Nethermind.
    /// Returns JsonRpcResponse, JsonRpcErrorResponse, or JsonElement (for proxied responses).
    /// Tracks per-method metrics. Does NOT manage the concurrency semaphore.
    /// </summary>
    public static async Task<object> DispatchSingleRequest(
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
                "circles_getTotalBalance" => await RpcHandlers.HandleGetTotalBalance(request, rpcModule),
                "circlesV2_getTotalBalance" => await RpcHandlers.HandleV2GetTotalBalance(request, rpcModule),
                "circles_getTokenBalances" => await RpcHandlers.HandleGetTokenBalances(request, rpcModule),
                "circles_getTokenInfo" => await RpcHandlers.HandleGetTokenInfo(request, rpcModule),
                "circles_getTokenInfoBatch" => await RpcHandlers.HandleGetTokenInfoBatch(request, rpcModule),
                // Avatar & Profile Methods
                "circles_getAvatarInfo" => await RpcHandlers.HandleGetAvatarInfo(request, rpcModule),
                "circles_getAvatarInfoBatch" => await RpcHandlers.HandleGetAvatarInfoBatch(request, rpcModule),
                "circles_getProfileCid" => await RpcHandlers.HandleGetProfileCid(request, rpcModule),
                "circles_getProfileCidBatch" => await RpcHandlers.HandleGetProfileCidBatch(request, rpcModule),
                "circles_getProfileByCid" => await RpcHandlers.HandleGetProfileByCid(request, rpcModule),
                "circles_getProfileByCidBatch" => await RpcHandlers.HandleGetProfileByCidBatch(request, rpcModule),
                "circles_getProfileByAddress" => await RpcHandlers.HandleGetProfileByAddress(request, rpcModule),
                "circles_getProfileByAddressBatch" => await RpcHandlers.HandleGetProfileByAddressBatch(request, rpcModule),
                "circles_searchProfiles" => await RpcHandlers.HandleSearchProfiles(request, rpcModule),
                // Trust & Network Methods
                "circles_getTrustRelations" => await RpcHandlers.HandleGetTrustRelations(request, rpcModule),
                "circles_getCommonTrust" => await RpcHandlers.HandleGetCommonTrust(request, rpcModule),
                "circles_getNetworkSnapshot" => await RpcHandlers.HandleGetNetworkSnapshot(request, rpcModule),
                "circles_getAggregatedTrustRelations" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_findGroups" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getGroupMembers" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getGroupMemberships" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                // Multi-affiliate-group (community willingness) Methods
                "circles_getAffiliateGroupWishlist" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAffiliateGroups" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAffiliateGroupMembersWishlist" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAffiliateGroupMembers" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAffiliateGroupFeesPercentage" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getTransactionHistory" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getTransferData" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getTokenHolders" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circlesV2_findPath" => await RpcHandlers.HandleV2FindPath(request, rpcModule),
                "circles_getScoreGroupMintLimits" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                // System & Query Methods
                "circles_getBlockByTimestamp" => await RpcHandlers.HandleGetBlockByTimestamp(request, rpcModule),
                "circles_events" => await RpcHandlers.HandleEventsLegacy(request, rpcModule),
                "circles_events_paginated" => await RpcHandlers.HandleEventsPaginated(request, rpcModule),
                "circles_health" => await RpcHandlers.HandleHealth(request, rpcModule),
                "circles_tables" => await RpcHandlers.HandleTables(request, rpcModule),
                "circles_query" => await RpcHandlers.HandleQuery(request, rpcModule),
                "circles_paginated_query" => await RpcHandlers.HandleQuery2(request, rpcModule),
                // SDK Enablement Methods
                "circles_getProfileView" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getTrustNetworkSummary" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAggregatedTrustRelationsEnriched" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getValidInviters" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getTransactionHistoryEnriched" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_searchProfileByAddressOrName" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getInvitationOrigin" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAllInvitations" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getTrustInvitations" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getEscrowInvitations" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getAtScaleInvitations" => await RpcHandlers.ReflectionHandler(request, rpcModule),
                "circles_getInvitationsFrom" => await RpcHandlers.ReflectionHandler(request, rpcModule),

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
                    Error = JsonRpcError.MethodNotFound(methodName)
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
                    Error = JsonRpcError.Internal("Proxy error")
                };
            }
        }
        catch (ArgumentException ex)
        {
            RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "invalid_params").Inc();
            return new JsonRpcErrorResponse
            {
                Id = JsonRpcId.CoerceId(request.Id),
                Error = JsonRpcError.InvalidParams(ex.Message)
            };
        }
        catch (JsonException)
        {
            RpcMetrics.ErrorsTotal.WithLabels(metricLabel, "invalid_json").Inc();
            return new JsonRpcErrorResponse
            {
                Id = JsonRpcId.CoerceId(request.Id),
                Error = JsonRpcError.InvalidParams()
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
                Error = JsonRpcError.Internal()
            };
        }
        finally
        {
            RpcMetrics.InFlightRequests.WithLabels(metricLabel).Dec();
            RpcMetrics.RequestDuration.WithLabels(metricLabel).Observe(
                Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
        }
    }
}
