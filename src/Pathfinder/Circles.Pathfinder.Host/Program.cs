using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Pathfinder;
using Circles.Pathfinder.Data;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Nodes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Nethermind.Int256;
using Npgsql;
using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static Circles.Pathfinder.Tracing;
using Microsoft.AspNetCore.ResponseCompression;

var settings = new Circles.Pathfinder.Host.Settings();

Console.WriteLine("Starting Pathfinder service...");
Console.WriteLine($"* Max concurrent requests: {settings.MaxConcurrentRequests}");

var csb = new NpgsqlConnectionStringBuilder(settings.IndexReadonlyDbConnectionString);
Console.WriteLine($"* DB Host: {csb.Host}");
Console.WriteLine($"* DB User: {csb.Username}");
Console.WriteLine($"* DB Name: {csb.Database}");
Console.WriteLine($"* DB Port: {csb.Port}");
Console.WriteLine($"* Nethermind RPC URL: {settings.NethermindRpcUrl}");

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
builder.Services.AddSingleton<NetworkState>();
builder.Services.AddSingleton<SnapshotCache>();
builder.Services.AddSingleton<LoadGraph>(provider =>
{
    var lg = new LoadGraph(settings, provider.GetRequiredService<ILoggerFactory>().CreateLogger<LoadGraph>());
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

// ─── Shared validation ───────────────────────────────────────────────────────
const int MaxArrayEntries = 1000;

static IResult? ValidateAddresses(params (string? addr, string name)[] pairs)
{
    foreach (var (addr, name) in pairs)
    {
        if (!string.IsNullOrWhiteSpace(addr) && !GraphFactory.IsValidEthereumAddress(addr.Trim().ToLowerInvariant()))
            return Results.BadRequest($"Invalid Ethereum address for '{name}': {addr}");
    }
    return null;
}

var sharedCaseInsensitiveJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

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
    string? simulatedBalances, // JSON array as query param
    string[]? simulatedConsentedAvatars, // Addresses with consented flow enabled (for testing)
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    V2Pathfinder pathfinder,
    ILogger<Program> log) =>
{
    // O8: Activity span for distributed tracing (mirrors findPath)
    using var act = Source.StartActivity("findMaxFlow", ActivityKind.Server);
    act?.SetTag("http.route", "/findMaxFlow");
    act?.SetTag("from", from);
    act?.SetTag("to", to);
    act?.SetTag("amount", amount);

    // S1: Validate address format
    var addrErr = ValidateAddresses((from, "from"), (to, "to"));
    if (addrErr != null) return addrErr;

    if (!UInt256.TryParse(amount, out var targetFlow))
    {
        log.LogWarning("Bad amount format");
        return Results.BadRequest("amount must be a valid integer.");
    }

    List<SimulatedBalance>? sim = null;
    if (!string.IsNullOrWhiteSpace(simulatedBalances))
    {
        try
        {
            sim = JsonSerializer.Deserialize<List<SimulatedBalance>>(
                simulatedBalances,
                sharedCaseInsensitiveJson);

            // S2: Cap array sizes
            if (sim != null && sim.Count > MaxArrayEntries)
                return Results.BadRequest($"simulatedBalances exceeds maximum of {MaxArrayEntries} entries.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Invalid simulatedBalances query JSON");
            return Results.BadRequest("simulatedBalances must be a JSON array of objects.");
        }
    }

    if (!sem.Wait(0))
    {
        FindPathMetrics.RejectedRequestsCounter.Inc();
        log.LogWarning("Concurrency limit hit — request rejected");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    FindPathMetrics.InFlightRequestsGauge.Inc();
    try
    {
        var balanceGraph = state.BalanceGraph;
        if (balanceGraph is null)
        {
            log.LogWarning("Graphs not ready");
            return Results.BadRequest("Graphs are not loaded yet.");
        }

        var trustGraph = state.AccountTrusts;

        var request = new FlowRequest
        {
            Source = from.ToLowerInvariant(),
            Sink = to.ToLowerInvariant(),
            TargetFlow = amount,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            ExcludedFromTokens = excludedFromTokens?.ToList(),
            ExcludedToTokens = excludedToTokens?.ToList(),
            WithWrap = withWrap,
            SimulatedBalances = sim,
            SimulatedConsentedAvatars = simulatedConsentedAvatars?.ToList()
        };

        using (var h = await pool.Rent(request, balanceGraph, trustGraph))
        {
            // S4: Solver timeout prevents stuck requests from blocking semaphore forever
            var solverTimeout = TimeSpan.FromSeconds(settings.SolverTimeoutSeconds);
            var result = await Task.Run(() => pathfinder.ComputeMaxFlow(h.Graph, request, targetFlow))
                .WaitAsync(solverTimeout);
            FindPathMetrics.SolverStatusTotal.WithLabels("success").Inc();
            return Results.Ok(result);
        }
    }
    catch (TimeoutException)
    {
        FindPathMetrics.SolverStatusTotal.WithLabels("timeout").Inc();
        log.LogError("findMaxFlow solver timed out after {Timeout}s for request: from={From}, to={To}, amount={Amount}",
            settings.SolverTimeoutSeconds, from, to, amount);
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        FindPathMetrics.SolverStatusTotal.WithLabels("error").Inc();
        log.LogError(ex, "findMaxFlow threw exception for request: from={From}, to={To}, amount={Amount}",
            from, to, amount);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
    finally
    {
        sem.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});


// ─── Routes ─────────────────────────────────────────────────────────────────
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
    string[]? simulatedConsentedAvatars, // Addresses with consented flow enabled (for testing)
    int? maxTransfers,
    bool? quantizedMode, // When true, enforces 96 CRC quantization for sink-bound transfers (invites = targetFlow / 96 CRC)
    bool? debugShowIntermediateSteps, // When true, includes all transformation stages in response
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    V2Pathfinder pathfinder,
    ILogger<Program> log) =>
{
    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", from);
    act?.SetTag("to", to);
    act?.SetTag("amount", amount);

    using var scope = log.BeginScope("traceId:{TraceId}", act?.TraceId.ToString());

    // S1: Validate address format
    var addrErr2 = ValidateAddresses((from, "from"), (to, "to"));
    if (addrErr2 != null) return addrErr2;

    if (!UInt256.TryParse(amount, out var targetFlow))
    {
        log.LogWarning("Bad amount format");
        return Results.BadRequest("amount must be a valid integer.");
    }

    List<SimulatedBalance>? sim = null;
    if (!string.IsNullOrWhiteSpace(simulatedBalances))
    {
        try
        {
            sim = JsonSerializer.Deserialize<List<SimulatedBalance>>(
                simulatedBalances,
                sharedCaseInsensitiveJson);

            // S2: Cap array sizes
            if (sim != null && sim.Count > MaxArrayEntries)
                return Results.BadRequest($"simulatedBalances exceeds maximum of {MaxArrayEntries} entries.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Invalid simulatedBalances query JSON");
            return Results.BadRequest("simulatedBalances must be a JSON array of objects.");
        }
    }

    if (!sem.Wait(0))
    {
        FindPathMetrics.RejectedRequestsCounter.Inc();
        log.LogWarning("Concurrency limit hit — request rejected");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    FindPathMetrics.InFlightRequestsGauge.Inc();
    try
    {
        var balanceGraph = state.BalanceGraph;
        var trustGraph = state.AccountTrusts;

        if (balanceGraph is null)
        {
            log.LogWarning("Graphs not ready");
            return Results.BadRequest("Graphs are not loaded yet.");
        }

        var request = new FlowRequest
        {
            Source = from.ToLowerInvariant(),
            Sink = to.ToLowerInvariant(),
            TargetFlow = amount,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            ExcludedFromTokens = excludedFromTokens?.ToList(),
            ExcludedToTokens = excludedToTokens?.ToList(),
            WithWrap = withWrap,
            SimulatedBalances = sim,
            SimulatedConsentedAvatars = simulatedConsentedAvatars?.ToList(),
            MaxTransfers = maxTransfers,
            QuantizedMode = quantizedMode,
            DebugShowIntermediateSteps = debugShowIntermediateSteps
        };

        using var h = await pool.Rent(request, balanceGraph, trustGraph);
        var solverTimeout = TimeSpan.FromSeconds(settings.SolverTimeoutSeconds);
        MaxFlowResponse result = await Task.Run(() => pathfinder.ComputeMaxFlowWithPath(h.Graph, request, targetFlow))
            .WaitAsync(solverTimeout);

        FindPathMetrics.SolverStatusTotal.WithLabels("success").Inc();
        return Results.Ok(result);
    }
    catch (TimeoutException)
    {
        FindPathMetrics.SolverStatusTotal.WithLabels("timeout").Inc();
        log.LogError("findPath solver timed out after {Timeout}s for request: from={From}, to={To}, amount={Amount}",
            settings.SolverTimeoutSeconds, from, to, amount);
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        FindPathMetrics.SolverStatusTotal.WithLabels("error").Inc();
        log.LogError(ex, "findPath threw exception for request: from={From}, to={To}, amount={Amount}",
            from, to, amount);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
    finally
    {
        sem.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});

// POST  /findPath  -----------------------------------------------------------
app.MapPost("/findPath", async (
    [FromBody] FlowRequest? request, // body-bound request DTO
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    V2Pathfinder pathfinder,
    ILogger<Program> log) =>
{
    // Handle JSON deserialization errors gracefully
    if (request == null)
    {
        log.LogWarning("Invalid JSON request body - could not deserialize FlowRequest");
        return Results.BadRequest("Invalid request body: Unable to deserialize FlowRequest. Check JSON format.");
    }

    // S1: Validate address format
    var addrErr3 = ValidateAddresses((request.Source, "source"), (request.Sink, "sink"));
    if (addrErr3 != null) return addrErr3;

    // S2: Cap array sizes
    if (request.SimulatedBalances?.Count > MaxArrayEntries)
        return Results.BadRequest($"simulatedBalances exceeds maximum of {MaxArrayEntries} entries.");
    if (request.SimulatedTrusts?.Count > MaxArrayEntries)
        return Results.BadRequest($"simulatedTrusts exceeds maximum of {MaxArrayEntries} entries.");
    if (request.SimulatedConsentedAvatars?.Count > MaxArrayEntries)
        return Results.BadRequest($"simulatedConsentedAvatars exceeds maximum of {MaxArrayEntries} entries.");

    log.LogInformation("Deserialized request - Source: {Source}, Sink: {Sink}, TargetFlow: {TargetFlow}",
        request.Source, request.Sink, request.TargetFlow);

    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", request.Source);
    act?.SetTag("to", request.Sink);
    act?.SetTag("amount", request.TargetFlow);

    using var scope = log.BeginScope("traceId:{TraceId}", act?.TraceId.ToString());

    // Normalise addresses so matching is case-insensitive
    request.Source = request.Source?.ToLowerInvariant();
    request.Sink = request.Sink?.ToLowerInvariant();

    if (string.IsNullOrEmpty(request.TargetFlow) || !UInt256.TryParse(request.TargetFlow, out var targetFlow))
    {
        log.LogWarning("Bad amount format - TargetFlow is {TargetFlow}", request.TargetFlow);
        return Results.BadRequest("amount must be a valid integer.");
    }

    if (!sem.Wait(0))
    {
        FindPathMetrics.RejectedRequestsCounter.Inc();
        log.LogWarning("Concurrency limit hit — request rejected");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    FindPathMetrics.InFlightRequestsGauge.Inc();
    try
    {
        var balanceGraph = state.BalanceGraph;
        var trustGraph = state.AccountTrusts;

        if (balanceGraph is null)
        {
            log.LogWarning("Graphs not ready");
            return Results.BadRequest("Graphs are not loaded yet.");
        }

        using var h = await pool.Rent(request, balanceGraph, trustGraph);
        var solverTimeout = TimeSpan.FromSeconds(settings.SolverTimeoutSeconds);
        MaxFlowResponse result = await Task.Run(
                () => pathfinder.ComputeMaxFlowWithPath(h.Graph, request, targetFlow))
            .WaitAsync(solverTimeout);

        FindPathMetrics.SolverStatusTotal.WithLabels("success").Inc();
        return Results.Ok(result);
    }
    catch (TimeoutException)
    {
        FindPathMetrics.SolverStatusTotal.WithLabels("timeout").Inc();
        log.LogError("findPath solver timed out after {Timeout}s for request: from={From}, to={To}, amount={Amount}",
            settings.SolverTimeoutSeconds, request.Source, request.Sink, request.TargetFlow);
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        FindPathMetrics.SolverStatusTotal.WithLabels("error").Inc();
        log.LogError(ex, "findPath threw exception for request: from={From}, to={To}, amount={Amount}",
            request.Source, request.Sink, request.TargetFlow);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
    finally
    {
        sem.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});

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
});

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