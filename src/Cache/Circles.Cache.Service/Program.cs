using Circles.Cache.Service;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Services;
using Prometheus;

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
builder.Services.AddHostedService<MetricsUpdateService>();

// Configure Kestrel to listen on configured port
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(settings.Port);
});

// Add health checks
builder.Services.AddHealthChecks();

// Add controllers for API endpoints
builder.Services.AddControllers();

// Add logging - use simple console format
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
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

// Enable Prometheus metrics collection and HTTP metrics
app.UseHttpMetrics();

// Map controllers
app.MapControllers();

// Map Prometheus metrics endpoint
app.MapMetrics();

// Health check endpoints
app.MapHealthChecks("/live");
app.MapGet("/ready", async (CacheServiceState state, CacheServiceSettings settings) =>
{
    // Query actual DB head from database
    long dbHead = state.LastProcessedBlock; // Default to current if query fails
    
    try
    {
        await using var conn = new Npgsql.NpgsqlConnection(settings.PostgresReadonlyConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT COALESCE(MAX(\"blockNumber\"), 0) FROM \"System_Block\"", conn);
        var result = await cmd.ExecuteScalarAsync();
        if (result != null && result != DBNull.Value)
        {
            dbHead = Convert.ToInt64(result);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to query DB head for readiness check");
    }

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
    status = "Running",
    endpoints = new
    {
        health = "/live",
        readiness = "/ready",
        stats = "/cache/stats",
        metrics = "/metrics",
        api = new
        {
            balances = "/api/balances/{address}",
            totalBalance = "/api/balances/{address}/total",
            totalBalanceV1 = "/api/balances/{address}/total/v1",
            totalBalanceV2 = "/api/balances/{address}/total/v2",
            avatarInfo = "/api/avatars/{address}",
            avatarInfoBatch = "/api/avatars/batch",
            profileCid = "/api/profiles/{address}/cid",
            profileCidBatch = "/api/profiles/cid/batch"
        }
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
