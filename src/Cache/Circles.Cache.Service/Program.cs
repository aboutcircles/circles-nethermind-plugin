using Circles.Cache.Service;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Services;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Scalar.AspNetCore;

static void AddConfigurableCors(IServiceCollection services)
{
    var origins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS") ?? "*";
    services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        if (origins == "*") p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        else p.WithOrigins(origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .AllowAnyMethod().AllowAnyHeader().AllowCredentials();
    }));
}

var builder = WebApplication.CreateBuilder(args);

// If a background service (warmup/listener) throws an unhandled exception past its
// internal retry loops, stop the host so Docker restarts the container. The previous
// Ignore behavior silently stopped the service, leaving the cache permanently stale
// with no log or metric to diagnose why.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

// Load settings from environment
var settings = CacheServiceSettings.FromEnvironment();
builder.Services.AddSingleton(settings);

// Build NpgsqlDataSource for readonly connection pooling
var readonlyDsBuilder = new NpgsqlDataSourceBuilder(settings.EffectiveReadonlyConnectionString);
readonlyDsBuilder.ConnectionStringBuilder.MinPoolSize = 2;
readonlyDsBuilder.ConnectionStringBuilder.MaxPoolSize = 20;
readonlyDsBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 300; // 5 min
var readonlyDataSource = readonlyDsBuilder.Build();
builder.Services.AddSingleton(readonlyDataSource);

// Register cache infrastructure
builder.Services.AddSingleton(sp => new CacheServiceState(settings.RollbackCapacity, settings.ReorgDetectionWindow));
builder.Services.AddSingleton(sp => new CacheContainer(settings.RollbackCapacity));
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IpfsContentCache>>();
    return new IpfsContentCache(readonlyDataSource, settings.IpfsCacheMaxEntries, logger);
});

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

// OpenAPI documentation
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Circles Cache Service API";
        document.Info.Version = "1.0.0";
        document.Info.Description = "Low-latency read cache for Circles protocol data — balances, avatars, tokens, profiles, trust relations, and group memberships.";
        return Task.CompletedTask;
    });
});

AddConfigurableCors(builder.Services);

// Add logging - use simple console format
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
});
builder.Logging.Configure(o =>
{
    o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId |
                                ActivityTrackingOptions.SpanId;
});

// ─── OpenTelemetry tracing (opt-in via OTEL_EXPORTER_OTLP_ENDPOINT) ────────
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "circles-cache-service",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"))
        .WithTracing(tracing => tracing
            .AddSource(Tracing.Name)
            .AddAspNetCoreInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter());

    Console.WriteLine($"* OTLP tracing enabled → {otlpEndpoint}");
}

// Build the app
var app = builder.Build();

// Log startup configuration
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== Circles Cache Service Starting ===");
logger.LogInformation("PostgreSQL Connection: {ConnectionString}",
    MaskConnectionString(settings.PostgresConnectionString));
logger.LogInformation("PostgreSQL Readonly Connection: {ConnectionString}",
    MaskConnectionString(settings.PostgresReadonlyConnectionString));
logger.LogInformation("PostgreSQL Pool: min={MinPool}, max={MaxPool}",
    readonlyDsBuilder.ConnectionStringBuilder.MinPoolSize,
    readonlyDsBuilder.ConnectionStringBuilder.MaxPoolSize);
logger.LogInformation("PG Notify Channel: {Channel}", settings.PgNotifyChannel);
logger.LogInformation("Rollback Capacity: {Capacity} blocks", settings.RollbackCapacity);
logger.LogInformation("Reorg Detection Window: {Window} blocks", settings.ReorgDetectionWindow);
logger.LogInformation("Max Catchup Lag: {Lag} blocks", settings.MaxCatchupLag);
logger.LogInformation("Tail Reconciliation: {Enabled} (window={Window} blocks, every {Interval} blocks)",
    settings.ReconciliationEnabled ? "enabled" : "disabled",
    settings.ReconciliationWindowBlocks, settings.ReconciliationIntervalBlocks);
logger.LogInformation("HTTP Port: {Port}", settings.Port);
logger.LogInformation("=======================================");

// Configure middleware
app.UseCors();
app.UseRouting();

// OpenAPI + Scalar interactive docs
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "Circles Cache Service API";
    options.Theme = ScalarTheme.DeepSpace;
});

// Enable Prometheus metrics collection and HTTP metrics
app.UseHttpMetrics();

// Map controllers
app.MapControllers();

// Map Prometheus metrics endpoint
app.MapMetrics();

// Health check endpoints
app.MapHealthChecks("/live");
app.MapGet("/ready", async (CacheServiceState state, NpgsqlDataSource dataSource) =>
{
    // Query actual DB head from database — if this fails, the service is not ready
    long dbHead;

    try
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT COALESCE(MAX(\"blockNumber\"), 0) FROM \"System_Block\"", conn);
        var result = await cmd.ExecuteScalarAsync();
        dbHead = result switch
        {
            long l => l,
            int i => i,
            null or DBNull => 0,
            _ => Convert.ToInt64(result)
        };
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to query DB head for readiness check");
        return Results.Json(new { status = "not_ready", error = "database unreachable" }, statusCode: 503);
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
app.MapGet("/cache/stats", (CacheContainer caches, IpfsContentCache ipfsCache, CacheServiceState state) =>
{
    var stats = caches.GetStatistics();
    stats["lastProcessedBlock"] = state.LastProcessedBlock;
    stats["warmupComplete"] = state.WarmupComplete;
    stats["listenerConnected"] = state.ListenerConnected;
    stats["ipfs_cache_entries"] = ipfsCache.Count;
    stats["ipfs_cache_hits"] = ipfsCache.Hits;
    stats["ipfs_cache_misses"] = ipfsCache.Misses;

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
        docs = new
        {
            openapi = "/openapi/v1.json",
            scalar = "/scalar/v1"
        },
        api = new
        {
            balances = "/api/balances/{address}",
            totalBalance = "/api/balances/{address}/total",
            totalBalanceV1 = "/api/balances/{address}/total/v1",
            totalBalanceV2 = "/api/balances/{address}/total/v2",
            avatarInfo = "/api/avatars/{address}",
            avatarInfoBatch = "/api/avatars/batch",
            profileCid = "/api/profiles/{address}/cid",
            profileCidBatch = "/api/profiles/cid/batch",
            profileContent = "/api/profiles/content/{cid}",
            profileContentBatch = "/api/profiles/content/batch"
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
