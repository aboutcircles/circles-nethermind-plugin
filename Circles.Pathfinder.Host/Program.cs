using System.Numerics;
using Circles.Pathfinder;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
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
    bool? withWrap,
    NetworkState stateContainer,
    SemaphoreSlim semaphore
) =>
{
    // Auxiliary method to create a filtered balance graph
    static BalanceGraph CreateFilteredBalanceGraph(BalanceGraph originalGraph, List<string> fromTokens, string source, List<string>? toTokens = null, string? sink = null)
    {
        var filteredGraph = new BalanceGraph(fromTokens, source, toTokens, sink);
        foreach (var avatarNode in originalGraph.AvatarNodes.Values)
        {
            filteredGraph.AddAvatar(avatarNode.Address);
        }
        foreach (var balanceNode in originalGraph.BalanceNodes.Values)
        {
            filteredGraph.AddBalance(
                balanceNode.HolderAddress,
                balanceNode.Token,
                balanceNode.Amount
            );
        }
        return filteredGraph;
    }

    // Auxiliary method to create a filtered trust graph
    static TrustGraph CreateFilteredTrustGraph(TrustGraph originalGraph, List<string> toTokens, string sink)
    {
        var filteredGraph = new TrustGraph(toTokens, sink);
        foreach (var avatarNode in originalGraph.AvatarNodes.Values)
        {
            filteredGraph.AddAvatar(avatarNode.Address);
        }
        foreach (var edge in originalGraph.Edges)
        {
            filteredGraph.AddTrustEdge(edge.From, edge.To);
        }
        return filteredGraph;
    }

    if (!semaphore.Wait(0))
    {
        FindPathMetrics.RejectedRequestsCounter.Inc();
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    FindPathMetrics.InFlightRequestsGauge.Inc();
    try
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.IsNullOrEmpty(amount))
            return Results.BadRequest("from, to, and amount must be provided.");

        if (!BigInteger.TryParse(amount, out var targetFlow))
            return Results.BadRequest("amount must be a valid integer.");

        // Use withWrap parameter to select the appropriate graph
        bool useWrappedTokens = withWrap ?? false; // Default to false (non-wrapped) if not specified
        
        // Select the appropriate graphs based on withWrap parameter
        var balanceGraph = useWrappedTokens 
            ? stateContainer.WrappedBalanceGraph 
            : stateContainer.BalanceGraph;
        
        var trustGraph = useWrappedTokens 
            ? stateContainer.WrappedTrustGraph 
            : stateContainer.TrustGraph;

        if (balanceGraph == null || trustGraph == null)
            return Results.BadRequest($"Requested graphs (withWrap={useWrappedTokens}) are not loaded yet.");

        var graphFactory = new GraphFactory();
        var pathfinder = new V2Pathfinder(graphFactory);
        
        var request = new FlowRequest
        {
            Source = from.ToLower(),
            Sink = to.ToLower(),
            TargetFlow = amount,
            FromTokens = fromTokens?.ToList(),
            ToTokens = toTokens?.ToList(),
            WithWrap = withWrap
        };

        // Only create filtered graphs if filters are specified
        var filteredBalanceGraph = ((fromTokens != null && fromTokens.Length > 0) || (request.Source == request.Sink))
            ? CreateFilteredBalanceGraph(
                balanceGraph, 
                request.FromTokens!, 
                request.Source, 
                request.ToTokens, 
                request.Sink)
            : balanceGraph;

        var filteredTrustGraph = (toTokens != null && toTokens.Length > 0)
            ? CreateFilteredTrustGraph(trustGraph, request.ToTokens!, request.Sink)
            : trustGraph;

        var pathResult = pathfinder.ComputeMaxFlowWithData(filteredBalanceGraph, filteredTrustGraph, request, targetFlow);

        return Results.Ok(pathResult);
    }
    finally
    {
        semaphore.Release();
        FindPathMetrics.InFlightRequestsGauge.Dec();
    }
});

app.Run();