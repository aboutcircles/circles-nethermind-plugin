using Circles.Cache.Service;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Services;

var builder = WebApplication.CreateBuilder(args);

// Load settings from environment
var settings = CacheServiceSettings.FromEnvironment();
builder.Services.AddSingleton(settings);

// Register cache infrastructure
builder.Services.AddSingleton<CacheServiceState>();
builder.Services.AddSingleton(sp => new CacheContainer(settings.RollbackCapacity));

// Register background services
builder.Services.AddHostedService<CacheWarmupService>();
builder.Services.AddHostedService<NotificationListenerService>();

// Configure Kestrel to listen on configured port
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(settings.Port);
});

// Add health checks
builder.Services.AddHealthChecks();

// Add logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Build the app
var app = builder.Build();

// Log startup configuration
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== Circles Cache Service Starting ===");
logger.LogInformation("PostgreSQL Connection: {ConnectionString}",
    MaskConnectionString(settings.PostgresConnectionString));
logger.LogInformation("PostgreSQL Readonly Connection: {ConnectionString}",
    MaskConnectionString(settings.PostgresReadonlyConnectionString));
logger.LogInformation("PG Notify Channel: {Channel}", settings.PgNotifyChannel);
logger.LogInformation("Rollback Capacity: {Capacity} blocks", settings.RollbackCapacity);
logger.LogInformation("Max Catchup Lag: {Lag} blocks", settings.MaxCatchupLag);
logger.LogInformation("HTTP Port: {Port}", settings.Port);
logger.LogInformation("=======================================");

// Configure middleware
app.UseRouting();

// Health check endpoints
app.MapHealthChecks("/live");
app.MapGet("/ready", (CacheServiceState state, CacheServiceSettings settings) =>
{
    // TODO: Get actual DB head from database query
    // For now, use a placeholder value
    var dbHead = state.LastProcessedBlock; // Will be updated when warmup is implemented

    var isReady = state.IsReady(dbHead, settings.MaxCatchupLag);

    var response = new
    {
        status = isReady ? "ready" : "not_ready",
        lastProcessedBlock = state.LastProcessedBlock,
        dbHead = dbHead,
        lag = state.GetLag(dbHead),
        warmupComplete = state.WarmupComplete,
        listenerConnected = state.ListenerConnected
    };

    return isReady
        ? Results.Ok(response)
        : Results.Json(response, statusCode: 503);
});

// Cache statistics endpoint
app.MapGet("/cache/stats", (CacheContainer caches, CacheServiceState state) =>
{
    var stats = caches.GetStatistics();
    stats["lastProcessedBlock"] = state.LastProcessedBlock;
    stats["warmupComplete"] = state.WarmupComplete;
    stats["listenerConnected"] = state.ListenerConnected;

    return Results.Ok(stats);
});

// Root endpoint
app.MapGet("/", () => new
{
    service = "Circles Cache Service",
    version = "1.0.0",
    status = "Starting",
    endpoints = new
    {
        health = "/live",
        readiness = "/ready"
    }
});

// Run the application
app.Run();

// Helper method to mask sensitive connection string data
static string MaskConnectionString(string connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return "(not set)";
    }

    // Mask password in connection string
    var parts = connectionString.Split(';');
    var masked = new List<string>();

    foreach (var part in parts)
    {
        if (part.TrimStart().StartsWith("Password=", StringComparison.OrdinalIgnoreCase) ||
            part.TrimStart().StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
        {
            masked.Add("Password=***");
        }
        else
        {
            masked.Add(part);
        }
    }

    return string.Join(';', masked);
}
