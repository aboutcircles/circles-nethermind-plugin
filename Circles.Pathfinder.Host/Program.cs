using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Pathfinder;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.LogDb;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Nodes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Nethermind.Int256;
using Npgsql;
using Prometheus;
using static Circles.Pathfinder.Tracing;
using Microsoft.AspNetCore.ResponseCompression;

var settings = new Settings();

Console.WriteLine("Starting Pathfinder service...");
Console.WriteLine($"* Max concurrent requests: {settings.MaxConcurrentRequests}");

var csb = new NpgsqlConnectionStringBuilder(settings.IndexReadonlyDbConnectionString);
Console.WriteLine($"* DB Host: {csb.Host}");
Console.WriteLine($"* DB User: {csb.Username}");
Console.WriteLine($"* DB Name: {csb.Database}");
Console.WriteLine($"* DB Port: {csb.Port}");
Console.WriteLine($"* Circles RPC URL: {settings.CirclesRpcUrl}");

var builder = WebApplication.CreateSlimBuilder(args);

// ─── Logging ────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o =>
{
    o.FormatterName = ConsoleFormatterNames.Simple;
    o.IncludeScopes = true;
});
builder.Logging.Configure(o =>
{
    o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId |
                                ActivityTrackingOptions.SpanId;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Circles.Pathfinder.Host", LogLevel.Debug);

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(o => { o.Level = CompressionLevel.Fastest; });

builder.Services.Configure<GzipCompressionProviderOptions>(o => { o.Level = CompressionLevel.Fastest; });

builder.Services
    .AddHealthChecks()
    // liveness – always healthy as long as the process answers HTTP
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    // readiness – needs the graphs
    .AddCheck<GraphReadinessHealthCheck>("graphs_loaded", tags: new[] { "ready" });

// ─── Misc DI ────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(_ => { });

var sem = new SemaphoreSlim(settings.MaxConcurrentRequests,
    settings.MaxConcurrentRequests);
builder.Services.AddSingleton(sem);

builder.Services.AddSingleton<NetworkState>();
builder.Services.AddSingleton(new PathLogDb());
builder.Services.AddSingleton(new CapacityGraphPool());
builder.Services.AddHostedService<NetworkStateUpdaterService>();
builder.Services.AddHostedService<LogStatsService>();

var app = builder.Build();

app.UseHttpMetrics();
app.UseResponseCompression();
app.MapMetrics();
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
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    ILogger<Program> log) =>
{
    if (!UInt256.TryParse(amount, out var targetFlow))
    {
        log.LogWarning("Bad amount format");
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
        if (balanceGraph is null)
        {
            log.LogWarning("Graphs not ready");
            return Results.BadRequest("Graphs are not loaded yet.");
        }

        var trustGraph = state.AccountTrusts;

        var pathfinder = new V2Pathfinder();

        var request = new FlowRequest
        {
            Source = from.ToLower(),
            Sink = to.ToLower(),
            TargetFlow = amount,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            ExcludedFromTokens = excludedFromTokens?.ToList(),
            ExcludedToTokens = excludedToTokens?.ToList(),
            WithWrap = withWrap
        };

        using (var h = await pool.Rent(request, balanceGraph, trustGraph))
        {
            var result = pathfinder.ComputeMaxFlow(h.Graph, request, targetFlow);
            return Results.Ok(result);
        }
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        log.LogWarning(ex, "findPath threw non-fatal exception");
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
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    ILogger<Program> log,
    PathLogDb logDb) =>
{
    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", from);
    act?.SetTag("to", to);
    act?.SetTag("amount", amount);

    using var scope = log.BeginScope("traceId:{TraceId}", act?.TraceId.ToString());

    if (!UInt256.TryParse(amount, out var targetFlow))
    {
        log.LogWarning("Bad amount format");
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

        var pathfinder = new V2Pathfinder();

        var request = new FlowRequest
        {
            Source = from.ToLower(),
            Sink = to.ToLower(),
            TargetFlow = amount,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            ExcludedFromTokens = excludedFromTokens?.ToList(),
            ExcludedToTokens = excludedToTokens?.ToList(),
            WithWrap = withWrap
        };

        var requestId = Guid.NewGuid();
        _ = logDb.LogRequest(requestId, state.LastKnownBlockNumber, request);

        try
        {
            using var h = await pool.Rent(request, balanceGraph, trustGraph);
            MaxFlowResponse result = pathfinder.ComputeMaxFlowWithPath(h.Graph, request, targetFlow);

            _ = logDb.LogResponse(requestId, result, true);

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            _ = logDb.LogResponse(requestId, null, false, ex.Message + "\n" + ex.StackTrace);
            throw;
        }
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        log.LogWarning(ex, "findPath threw non-fatal exception");
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
    [FromBody] FlowRequest request, // body-bound request DTO
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    ILogger<Program> log,
    PathLogDb logDb) =>
{
    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", request.Source);
    act?.SetTag("to", request.Sink);
    act?.SetTag("amount", request.TargetFlow);

    // Normalise addresses so matching is case-insensitive
    request.Source = request.Source?.ToLowerInvariant();
    request.Sink = request.Sink?.ToLowerInvariant();

    if (!UInt256.TryParse(request.TargetFlow, out var targetFlow))
    {
        log.LogWarning("Bad amount format");
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

        var pathfinder = new V2Pathfinder();
        var requestId = Guid.NewGuid();
        _ = logDb.LogRequest(requestId, state.LastKnownBlockNumber, request);

        try
        {
            using var h = await pool.Rent(request, balanceGraph, trustGraph);
            MaxFlowResponse result =
                pathfinder.ComputeMaxFlowWithPath(h.Graph, request, targetFlow);

            _ = logDb.LogResponse(requestId, result, true);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            _ = logDb.LogResponse(requestId, null, false,
                ex.Message + "\n" + ex.StackTrace);
            throw;
        }
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        log.LogWarning(ex, "findPath threw non-fatal exception");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
    finally
    {
        sem.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});

// POST  /rankByGraphProximity -----------------------------------------------
app.MapPost("/rankByGraphProximity", async (
    [FromBody] ProximityRequest request,
    NetworkState state,
    SemaphoreSlim sem,
    CapacityGraphPool pool,
    ILogger<Program> log) =>
{
    if (request.Reference is null || request.Addresses is null)
        return Results.BadRequest("reference and addresses are required.");
    if (request.Addresses.Count > 100)
        return Results.BadRequest("maximum 100 addresses allowed.");

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

        using var h = await pool.Rent(new FlowRequest(), balanceGraph, state.AccountTrusts);
        var pf = new V2Pathfinder();
        var ranks = pf.RankByGraphProximity(
            h.Graph,
            request.Reference.ToLowerInvariant(),
            request.Addresses.Select(a => a.ToLowerInvariant()));
        return Results.Ok(ranks);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        log.LogWarning(ex, "rankByGraphProximity threw non-fatal exception");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
    finally
    {
        sem.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});

app.MapGet("/snapshot", async (NetworkState state) =>
{
    var graphsReady = state.BalanceGraph is not null &&
                      state.AccountTrusts.Count > 0;

    if (!graphsReady)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var balancesByHolder = new Dictionary<int, List<BalanceNode>>(state.BalanceGraph!.BalanceNodes.Count);
    foreach (var node in state.BalanceGraph.BalanceNodes.Values)
    {
        if (!balancesByHolder.TryGetValue(node.Holder, out var list))
        {
            list = new List<BalanceNode>();
            balancesByHolder.Add(node.Holder, list);
        }

        list.Add(node);
    }

    var snapshot = new NetworkSnapshot
    {
        BlockNumber = state.LastKnownBlockNumber,
        Addresses = AddressIdPool.GetAvatarSnapshot(),
        Trust = state.AccountTrusts.ToDictionary(kvp => kvp.Key, kvp => new HashSet<int>(kvp.Value)),
        Balance = balancesByHolder
    };

    // Let ASP.NET Core handle JSON + transport compression
    return Results.Json(
        snapshot,
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
});

app.Run();