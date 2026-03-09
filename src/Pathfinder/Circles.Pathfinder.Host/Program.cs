using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Common.Dto;
using Circles.Pathfinder;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Nodes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Nethermind.Int256;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Scalar.AspNetCore;
using static Circles.Pathfinder.Tracing;

var settings = new Circles.Pathfinder.Host.Settings();

Console.WriteLine("Starting Pathfinder service...");
Console.WriteLine($"* Max concurrent requests: {settings.MaxConcurrentRequests}");

// Build NpgsqlDataSource with explicit pool configuration
var dsBuilder = new NpgsqlDataSourceBuilder(settings.IndexReadonlyDbConnectionString);
dsBuilder.ConnectionStringBuilder.MinPoolSize = 2;
dsBuilder.ConnectionStringBuilder.MaxPoolSize = 20;
dsBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 300; // 5 min
var dataSource = dsBuilder.Build();

Console.WriteLine($"* DB Host: {dsBuilder.ConnectionStringBuilder.Host}");
Console.WriteLine($"* DB User: {dsBuilder.ConnectionStringBuilder.Username}");
Console.WriteLine($"* DB Name: {dsBuilder.ConnectionStringBuilder.Database}");
Console.WriteLine($"* DB Port: {dsBuilder.ConnectionStringBuilder.Port}");
Console.WriteLine($"* DB Pool: min={dsBuilder.ConnectionStringBuilder.MinPoolSize}, max={dsBuilder.ConnectionStringBuilder.MaxPoolSize}");
Console.WriteLine($"* Nethermind RPC URL: {settings.NethermindRpcUrl}");
Console.WriteLine($"* Consented flow: {(settings.ExcludeConsentedIntermediaries ? "DISABLED (intermediaries excluded)" : "enabled (validate rules)")}");
Console.WriteLine($"* Graph source: {(settings.UseCacheGraphSource ? $"CACHE ({settings.CacheServiceUrl})" : "DATABASE")}");
if (settings.UseCacheGraphSource)
{
    Console.WriteLine($"  - Fallback to DB: {settings.CacheGraphFallbackToDb}");
    Console.WriteLine($"  - Request timeout: {settings.CacheGraphRequestTimeoutSeconds}s");
}

var semaphore = new SemaphoreSlim(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);

var builder = WebApplication.CreateSlimBuilder(args);

// S2: Limit request body to 1 MB to prevent abuse via large simulatedBalances arrays
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576);

// Use default background service exception behavior to prevent silent failures
// Host will stop on background service exceptions, ensuring proper error handling

// Register the host settings instance and expose its common settings to components that need it
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Circles.Common.Settings>(provider => provider.GetRequiredService<Circles.Pathfinder.Host.Settings>().CommonSettings);
builder.Services.AddSingleton<Circles.Pathfinder.Settings>(provider => provider.GetRequiredService<Circles.Pathfinder.Host.Settings>());
builder.Services.AddSingleton(semaphore);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<NetworkState>();
builder.Services.AddSingleton<SnapshotCache>();
builder.Services.AddSingleton<LoadGraph>(provider =>
{
    var lg = new LoadGraph(dataSource, settings, provider.GetRequiredService<ILoggerFactory>().CreateLogger<LoadGraph>());
    // O4: Wire DB query duration metric
    lg.OnQueryCompleted = (queryName, elapsed) =>
        GraphUpdateMetrics.DbQueryDuration.WithLabels(queryName).Observe(elapsed.TotalSeconds);
    return lg;
});
builder.Services.AddSingleton<CapacityGraphPool>(provider =>
    new CapacityGraphPool(
        settings.RouterAddress,
        provider.GetRequiredService<LoadGraph>(),
        provider.GetRequiredService<ILoggerFactory>().CreateLogger<GraphFactory>()));
builder.Services.AddSingleton<V2Pathfinder>();
builder.Services.AddSingleton<FindPathHandler>();

builder.Services.AddHostedService<NetworkStateUpdaterService>();
builder.Services.AddHostedService<LogStatsService>();

// ─── Logging ────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => { o.FormatterName = ConsoleFormatterNames.Simple; });
builder.Logging.Configure(o =>
{
    o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId |
                                ActivityTrackingOptions.SpanId;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Circles.Pathfinder.Host", LogLevel.Debug);

// ─── OpenTelemetry tracing (opt-in via OTEL_EXPORTER_OTLP_ENDPOINT) ────────
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "circles-pathfinder",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"))
        .WithTracing(tracing => tracing
            .AddSource(Tracing.Name)
            .AddAspNetCoreInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter());

    Console.WriteLine($"* OTLP tracing enabled → {otlpEndpoint}");
}

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(o => { o.Level = CompressionLevel.Fastest; });

builder.Services.Configure<GzipCompressionProviderOptions>(o => { o.Level = CompressionLevel.Fastest; });

// Add configurable CORS (reads CORS_ALLOWED_ORIGINS from environment)
// CORS: read allowed origins from environment
var corsOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS") ?? "*";
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (corsOrigins == "*")
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    else
        policy.WithOrigins(corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .AllowAnyMethod().AllowAnyHeader().AllowCredentials();
}));

builder.Services
    .AddHealthChecks()
    // liveness – always healthy as long as the process answers HTTP
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    // readiness – needs the graphs, healthy background services, and DB connectivity
    .AddCheck<GraphReadinessHealthCheck>("graphs_loaded", tags: new[] { "ready" })
    .AddCheck<BackgroundServiceHealthCheck>("background_services", tags: new[] { "ready" })
    .AddCheck<DbConnectivityHealthCheck>("db_connectivity", tags: new[] { "ready" });

// OpenAPI documentation
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Circles Pathfinder API";
        document.Info.Version = "1.0.0";
        document.Info.Description = "REST API for computing transitive transfer paths through the Circles trust network using Google OR-Tools.";
        return Task.CompletedTask;
    });
});

// ─── Misc DI ────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});


var app = builder.Build();

app.UseCors();
app.UseHttpMetrics();
app.UseResponseCompression();
app.MapMetrics();
app.UseMiddleware<RequestBodyLoggingMiddleware>();
app.UseMiddleware<RequestTimingMiddleware>();

// OpenAPI + Scalar interactive docs
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "Circles Pathfinder API";
    options.Theme = ScalarTheme.DeepSpace;
});

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
app.MapGet("/findMaxFlow", async (
    string from,
    string to,
    string amount,
    string[]? fromTokens,
    string[]? toTokens,
    string[]? excludedFromTokens,
    string[]? excludedToTokens,
    bool? withWrap,
    string? simulatedBalances,
    string[]? simulatedConsentedAvatars,
    NetworkState state,
    CapacityGraphPool pool,
    V2Pathfinder pathfinder,
    FindPathHandler handler) =>
{
    using var act = Source.StartActivity("findMaxFlow", ActivityKind.Server);
    act?.SetTag("http.route", "/findMaxFlow");
    act?.SetTag("from", from);
    act?.SetTag("to", to);
    act?.SetTag("amount", amount);

    var addrErr = FindPathHandler.ValidateAddresses((from, "from"), (to, "to"));
    if (addrErr != null) return addrErr;

    if (!UInt256.TryParse(amount, out var targetFlow))
        return Results.BadRequest("amount must be a valid integer.");

    var (sim, simErr) = FindPathHandler.ParseSimulatedBalances(simulatedBalances);
    if (simErr != null) return simErr;

    var request = handler.BuildRequest(from, to, amount, fromTokens, toTokens,
        excludedFromTokens, excludedToTokens, withWrap, sim, simulatedConsentedAvatars);

    return await handler.ExecuteWithGuard("findMaxFlow", request, state, pool,
        graph => pathfinder.ComputeMaxFlow(graph, request, targetFlow));
})
.WithTags("Pathfinding")
.WithDescription("Compute the maximum transferable flow between two addresses without computing the full path.");

app.MapGet("/findPath", async (
    string from,
    string to,
    string amount,
    string[]? fromTokens,
    string[]? toTokens,
    string[]? excludedFromTokens,
    string[]? excludedToTokens,
    bool? withWrap,
    string? simulatedBalances,
    string[]? simulatedConsentedAvatars,
    int? maxTransfers,
    bool? quantizedMode,
    bool? debugShowIntermediateSteps,
    NetworkState state,
    CapacityGraphPool pool,
    V2Pathfinder pathfinder,
    FindPathHandler handler,
    ILogger<Program> log) =>
{
    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", from);
    act?.SetTag("to", to);
    act?.SetTag("amount", amount);
    using var scope = log.BeginScope("traceId:{TraceId}", act?.TraceId.ToString());

    var addrErr = FindPathHandler.ValidateAddresses((from, "from"), (to, "to"));
    if (addrErr != null) return addrErr;

    if (!UInt256.TryParse(amount, out var targetFlow))
        return Results.BadRequest("amount must be a valid integer.");

    var (sim, simErr) = FindPathHandler.ParseSimulatedBalances(simulatedBalances);
    if (simErr != null) return simErr;

    var request = handler.BuildRequest(from, to, amount, fromTokens, toTokens,
        excludedFromTokens, excludedToTokens, withWrap, sim, simulatedConsentedAvatars,
        maxTransfers, quantizedMode, debugShowIntermediateSteps);

    return await handler.ExecuteWithGuard("findPath", request, state, pool,
        graph => pathfinder.ComputeMaxFlowWithPath(graph, request, targetFlow));
})
.WithTags("Pathfinding")
.WithDescription("Compute a transitive transfer path between two addresses (GET with query parameters).");

// POST  /findPath  -----------------------------------------------------------
app.MapPost("/findPath", async (
    [FromBody] FlowRequest? request,
    NetworkState state,
    CapacityGraphPool pool,
    V2Pathfinder pathfinder,
    FindPathHandler handler,
    ILogger<Program> log) =>
{
    if (request == null)
        return Results.BadRequest("Invalid request body: Unable to deserialize FlowRequest. Check JSON format.");

    var addrErr = FindPathHandler.ValidateAddresses((request.Source, "source"), (request.Sink, "sink"));
    if (addrErr != null) return addrErr;

    var arraySizeErr = FindPathHandler.ValidateArraySizes(request);
    if (arraySizeErr != null) return arraySizeErr;

    log.LogInformation("Deserialized request - Source: {Source}, Sink: {Sink}, TargetFlow: {TargetFlow}",
        request.Source, request.Sink, request.TargetFlow);

    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", request.Source);
    act?.SetTag("to", request.Sink);
    act?.SetTag("amount", request.TargetFlow);
    using var scope = log.BeginScope("traceId:{TraceId}", act?.TraceId.ToString());

    request.Source = request.Source?.ToLowerInvariant();
    request.Sink = request.Sink?.ToLowerInvariant();

    if (settings.ExcludeConsentedIntermediaries)
        request.SimulatedConsentedAvatars = null;

    if (string.IsNullOrEmpty(request.TargetFlow) || !UInt256.TryParse(request.TargetFlow, out var targetFlow))
        return Results.BadRequest("amount must be a valid integer.");

    return await handler.ExecuteWithGuard("findPath", request, state, pool,
        graph => pathfinder.ComputeMaxFlowWithPath(graph, request, targetFlow));
})
.WithTags("Pathfinding")
.WithDescription("Compute a transitive transfer path between two addresses (POST with JSON body).");

app.MapGet("/snapshot", (NetworkState state, SnapshotCache snapshotCache, HttpContext httpContext) =>
{
    // Check If-None-Match header for conditional request
    var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch.FirstOrDefault();
    var currentETag = snapshotCache.CurrentETag;

    // If client has current version, return 304 Not Modified
    if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == currentETag)
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    // Get or build the cached snapshot
    var (json, etag) = snapshotCache.GetOrBuildSnapshot(state);

    if (json == null)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // Set cache headers
    httpContext.Response.Headers.ETag = etag;
    httpContext.Response.Headers.CacheControl = "public, max-age=5"; // 5 seconds - allows clients to cache briefly

    // Return pre-serialized JSON bytes directly
    return Results.Bytes(json, "application/json");
})
.WithTags("Graph")
.WithDescription("Get a snapshot of the full trust graph (balances, trusts, avatars). Supports ETag-based conditional requests.");

app.Run();

/// <summary>
/// CORS configuration helper. Reads CORS_ALLOWED_ORIGINS from environment (comma-separated list or "*" for all).
/// </summary>
static class CorsConfiguration
{
    public static void AddConfigurableCors(this IServiceCollection services)
    {
        var corsOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS") ?? "*";

        services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            if (corsOrigins == "*")
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            }
            else
            {
                policy.WithOrigins(corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            }
        }));
    }
}
