using System.Net.WebSockets;

namespace Circles.Rpc.Host.Endpoints;

/// <summary>
/// Unified WebSocket entry points (<c>/ws</c> and <c>/ws/subscribe</c>) that multiplex
/// Circles subscriptions with Nethermind proxy traffic inside a single session.
/// </summary>
public static class WebSocketEndpoints
{
    private static int _activeSessions;

    /// <summary>Current number of active WebSocket sessions.</summary>
    internal static int ActiveSessions => Volatile.Read(ref _activeSessions);

    public static WebApplication MapCirclesWebSockets(this WebApplication app)
    {
        app.Map("/ws/subscribe", HandleUnifiedWebSocket).DisableAntiforgery();
        app.Map("/ws", HandleUnifiedWebSocket).DisableAntiforgery();
        return app;
    }

    private static async Task HandleUnifiedWebSocket(
        HttpContext context,
        CirclesSubscriptionService circlesSvc,
        NethermindWsProxy nethermindProxy,
        Settings settings,
        ILogger<Program> logger)
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!context.WebSockets.IsWebSocketRequest)
        {
            logger.LogWarning("Rejected non-WebSocket request from {RemoteIp}", remoteIp);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket request expected.");
            return;
        }

        var active = Interlocked.Increment(ref _activeSessions);
        try
        {
            var cap = settings.MaxConcurrentWsSessions;
            if (cap > 0 && active > cap)
            {
                logger.LogWarning(
                    "Rejected WebSocket session from {RemoteIp}: concurrent session cap of {Cap} reached",
                    remoteIp, cap);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Too many concurrent WebSocket sessions.");
                return;
            }

            logger.LogInformation("WebSocket session started from {RemoteIp}", remoteIp);
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

            await using var session = new WebSocketSession(webSocket, circlesSvc, nethermindProxy, settings, logger);
            await session.RunAsync(context.RequestAborted);

            logger.LogInformation("WebSocket session ended for {RemoteIp}", remoteIp);
        }
        finally
        {
            Interlocked.Decrement(ref _activeSessions);
        }
    }
}
