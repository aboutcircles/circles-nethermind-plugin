using Circles.Rpc.Host.Dispatch;

namespace Circles.Rpc.Host.Endpoints;

/// <summary>
/// Single-request JSON-RPC route at <c>POST /</c>. Non-array bodies arrive here after the
/// batch middleware passes them through. Each request acquires one rate-limit token and
/// one semaphore slot.
/// </summary>
public static class JsonRpcEndpoint
{
    public static WebApplication MapSingleJsonRpcRoute(this WebApplication app)
    {
        var rpcSemaphore = app.Services.GetRequiredService<SemaphoreSlim>();
        var rpcRateLimiter = app.Services.GetRequiredService<RpcRateLimiter>();

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
                    Error = JsonRpcError.InvalidRequest()
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
                    Error = JsonRpcError.RateLimited()
                }, statusCode: 429);
            }

            // Concurrency guard — reject immediately when at capacity
            if (!await rpcSemaphore.WaitAsync(0))
            {
                RpcMetrics.RejectedTotal.Inc();
                return Results.Json(new JsonRpcErrorResponse
                {
                    Id = JsonRpcId.CoerceId(request.Id),
                    Error = JsonRpcError.ServerBusy()
                }, statusCode: 503);
            }
            var nethermindClient = context.RequestServices.GetRequiredService<NethermindRpcClient>();

            try
            {
                var result = await RpcDispatcher.DispatchSingleRequest(request, rpcModule, nethermindClient, logger, remoteIp);
                return Results.Json(result);
            }
            finally
            {
                rpcSemaphore.Release();
            }

        }).DisableAntiforgery();

        return app;
    }
}
