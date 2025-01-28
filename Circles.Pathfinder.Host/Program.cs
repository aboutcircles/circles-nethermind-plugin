using System.Numerics;
using Circles.Pathfinder;
using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Add custom JSON config if needed
});

builder.Services.AddSingleton<NetworkState>();
builder.Services.AddHostedService<BackgroundUpdaterService>();

var app = builder.Build();

// Apply a global middleware that sets no-cache headers on all responses
app.Use((context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";

    return next(context);
});


// Basic status route
// The => arrow function is short for a single line returning a value
app.MapGet("/", () => "Alive and well");

// Route with query params: /findPath?from=A&to=B&amount=123
// In this route, we can also demonstrate reading the state
app.MapGet("/findPath", async (string from, string to, string amount, NetworkState stateContainer) =>
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.IsNullOrEmpty(amount))
        {
            return Results.BadRequest("from, to, and amount must be provided.");
        }

        if (!BigInteger.TryParse(amount, out _))
        {
            return Results.BadRequest("amount must be a valid integer.");
        }

        if (stateContainer.BalanceGraph == null || stateContainer.TrustGraph == null)
        {
            return Results.BadRequest("Graphs are not loaded yet.");
        }

        GraphFactory graphFactory = new();
        var pathfinder = new V2Pathfinder(graphFactory);
        var pathResult = pathfinder.ComputeMaxFlowWithData(
            stateContainer.BalanceGraph,
            stateContainer.TrustGraph,
            new FlowRequest
            {
                Source = from.ToLowerInvariant(),
                Sink = to.ToLowerInvariant(),
                TargetFlow = amount
            }, BigInteger.Parse(amount));

        return Results.Ok(pathResult);
    }
);

app.Run();