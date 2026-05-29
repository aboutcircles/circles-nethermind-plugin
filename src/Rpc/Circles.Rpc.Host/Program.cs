using Circles.Rpc.Host;
using Circles.Rpc.Host.Endpoints;
using Circles.Rpc.Host.Middleware;
using Prometheus;

// The HTTP pipeline + JSON-RPC dispatch implementation is split across several files for
// maintainability. Each helper file is wired in here in middleware-then-routes order:
// - BuilderSetup.cs                        — DI registration, JSON options, health-check probes
// - Middleware/BatchRpcMiddleware.cs       — POST / JSON-array (batch) middleware
// - Endpoints/HealthCheckEndpoints.cs      — /live, /ready, /health
// - Endpoints/DocsEndpoints.cs             — /, /docs, /openrpc.json, /openrpc
// - Endpoints/WebSocketEndpoints.cs        — /ws, /ws/subscribe
// - Endpoints/JsonRpcEndpoint.cs           — POST / single-request route
// - Dispatch/RpcDispatcher.cs              — method → handler routing, proxy fallback, metrics
// - Dispatch/RpcHandlers.cs                — per-method Handle* implementations
// - Json/JsonElementExtensions.cs          — utility extension (global namespace)

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

app.MapCirclesHealthChecks();
app.MapDocsAndOpenRpc();
app.MapCirclesWebSockets();
app.UseBatchJsonRpc();
app.MapSingleJsonRpcRoute();

app.Run();

public partial class Program { }
