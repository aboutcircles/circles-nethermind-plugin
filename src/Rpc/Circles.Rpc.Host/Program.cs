using Circles.Index.Common;
// using Circles.Rpc;
using Circles.Rpc.Host;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;


var builder = BuilderSetup.ConfigureBuilder(args);

var app = builder.Build();

app.UseHttpMetrics();
app.UseResponseCompression();
app.MapMetrics();

app.MapHealthChecks("/live", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("live")
});

// readiness: only healthy once the background loader has built the graphs
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("ready"),
    AllowCachingResponses = false,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Degraded] = StatusCodes.Status429TooManyRequests
    }
});

// ─── Routes ─────────────────────────────────────────────────────────────────

app.MapPost("/", async (
    JsonRpcRequest request,
    Settings settings,
    ILogger<Program> logger
    // CirclesRpcModule rpcModule
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
        // object rpcResult = request.Method switch
        // {
        //     "circles_getTotalBalance" => await CirclesRpcHandlers.HandleGetTotalBalance(request, settings, logger, rpcModule),
        //     // "circles_getTokenBalances" => await HandleGetTokenBalances(request, settings, logger),

        //     _ => throw new RpcMethodNotFoundException(request.Method)
        // };

        return Results.Ok(new JsonRpcResponse
        {
            Id = request.Id,
            // Result = rpcResult
        });
    }
    catch (RpcMethodNotFoundException ex)
    {
        logger.LogWarning("RPC Method not found: {Method}", ex.MethodName);
        return Results.NotFound(new JsonRpcErrorResponse
        {
            Id = request.Id,
            Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {ex.MethodName}" }
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Internal Server Error during RPC execution for method: {Method}", request.Method);
        return Results.Json(
            new JsonRpcErrorResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = -32603, Message = "Internal server error" }
            },
            statusCode: StatusCodes.Status500InternalServerError
        );
    }

}).DisableAntiforgery();

app.Run();