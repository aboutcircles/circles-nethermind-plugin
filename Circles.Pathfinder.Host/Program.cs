using System.Diagnostics;
using Circles.Pathfinder;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Microsoft.Extensions.Logging.Console;
using Nethermind.Int256;
using Npgsql;
using Prometheus;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static Circles.Pathfinder.Tracing;

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

// ─── OpenTelemetry Tracing ──────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "circles-pathfinder",
        serviceVersion: typeof(Program).Assembly.GetName().Version!.ToString()))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(Tracing.Name)
        .SetSampler(new ParentBasedSampler(
            new TraceIdRatioBasedSampler(0.05))) // sample 5 %
        .AddOtlpExporter() // ↗ to collector
        .AddConsoleExporter(o =>
        {
            o.Targets = ConsoleExporterOutputTargets.Console;
        }));

// ─── Misc DI ────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(_ => { });

var sem = new SemaphoreSlim(settings.MaxConcurrentRequests,
    settings.MaxConcurrentRequests);
builder.Services.AddSingleton(sem);

builder.Services.AddSingleton<NetworkState>();
builder.Services.AddSingleton(new FlowGraphPool(settings.IndexReadonlyDbConnectionString));
builder.Services.AddHostedService<NetworkStateUpdaterService>();
builder.Services.AddHostedService<LogStatsService>();

var app = builder.Build();

app.UseHttpMetrics();
app.MapMetrics();
app.UseMiddleware<RequestTimingMiddleware>();

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
    FlowGraphPool pool,
    ILogger<Program> log) =>
{
    using var act = Source.StartActivity("findPath", ActivityKind.Server);
    act?.SetTag("http.route", "/findPath");
    act?.SetTag("from", from);
    act?.SetTag("to", to);
    act?.SetTag("amount", amount);

    using var scope = log.BeginScope("traceId:{TraceId}", act?.TraceId.ToString());

    var sw = Stopwatch.StartNew();

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

        var parseMs = sw.ElapsedMilliseconds;
        var algoSw = Stopwatch.StartNew();

        var graphFactory = new GraphFactory();
        var pathfinder = new V2Pathfinder(graphFactory);

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
        
        using var rentedFlowGraph = await pool.Rent(request);
        MaxFlowResponse result = pathfinder.ComputeMaxFlowOnFlowGraph(rentedFlowGraph.Graph, request, targetFlow);
        
        // MaxFlowResponse result = pathfinder.ComputeMaxFlowWithData(
        //     balanceGraph, trustGraph, request, targetFlow);
        //
        // var algoMs = algoSw.ElapsedMilliseconds;
        // sw.Stop();
        // var totalMs = sw.ElapsedMilliseconds;
        //
        // log.LogDebug("findPath parsed={ParseMs}ms algo={AlgoMs}ms total={TotalMs}ms",
        //     parseMs, algoMs, totalMs);
        //
        // if (totalMs > Constants.FindPathSlaMs)
        // {
        //     log.LogWarning("findPath SLA breach — {TotalMs} ms (>{SlaMs})",
        //         totalMs, Constants.FindPathSlaMs);
        // }

        return Results.Ok(result);
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

app.Run();