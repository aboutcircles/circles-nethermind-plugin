using System.Numerics;
using Circles.Index.Utils;
using Circles.Pathfinder;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Nethermind.Int256;
using Npgsql;
using Prometheus;

var settings = new Settings();

Console.WriteLine("Starting Pathfinder service...");
Console.WriteLine($"* Max concurrent requests: {settings.MaxConcurrentRequests}");

var connectionStringBuilder = new NpgsqlConnectionStringBuilder(settings.IndexReadonlyDbConnectionString);
Console.WriteLine($"* DB Host: {connectionStringBuilder.Host}");
Console.WriteLine($"* DB User: {connectionStringBuilder.Username}");
Console.WriteLine($"* DB Name: {connectionStringBuilder.Database}");
Console.WriteLine($"* DB Port: {connectionStringBuilder.Port}");
Console.WriteLine($"* Circles RPC URL: {settings.CirclesRpcUrl}");

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Add custom JSON config if needed
});

var concurrencySemaphore = new SemaphoreSlim(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);
builder.Services.AddSingleton(concurrencySemaphore);

builder.Services.AddSingleton<NetworkState>();
builder.Services.AddHostedService<NetworkStateUpdaterService>();

var app = builder.Build();

// Middleware to disable caching for all responses
app.Use((context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    return next(context);
});

// Prometheus: add default HTTP metrics + /metrics endpoint
app.UseHttpMetrics();
app.MapMetrics();

// Route with query params: /findPath?from=A&to=B&amount=123&fromTokens=token1,token2&toTokens=token3,token4
app.MapGet("/findPath", async (
    string from,
    string to,
    string amount,
    string[]? fromTokens,
    string[]? toTokens,
    string[]? excludedFromTokens,
    string[]? excludedToTokens,
    bool? withWrap,
    NetworkState stateContainer,
    SemaphoreSlim semaphore
) =>
{
    if (!semaphore.Wait(0))
    {
        FindPathMetrics.RejectedRequestsCounter.Inc();
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // If we got here, we have a permit. We'll increment our gauge by +1.
    FindPathMetrics.InFlightRequestsGauge.Inc();
    try
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.IsNullOrEmpty(amount))
            return Results.BadRequest("from, to, and amount must be provided.");

        if (!UInt256.TryParse(amount, out var targetFlow))
            return Results.BadRequest("amount must be a valid integer.");

        // Get graphs from state container
        var balanceGraph = stateContainer.BalanceGraph;
        var trustGraph = stateContainer.TrustGraph;

        if (balanceGraph == null || trustGraph == null)
            return Results.BadRequest("Graphs are not loaded yet.");

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


        var pathResult = pathfinder.ComputeMaxFlowWithData(balanceGraph, trustGraph, request, targetFlow);

        return Results.Ok(pathResult);
    }
    finally
    {
        // Release the concurrency permit and decrement in-flight gauge
        semaphore.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});

app.Run();