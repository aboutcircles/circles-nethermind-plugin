using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Npgsql;

namespace Circles.Rpc.Host;

public static class BuilderSetup
{
    public static WebApplicationBuilder ConfigureBuilder(string[] args)
    {
        var settings = new Settings();

        Console.WriteLine("Starting Circles.Rpc service...");
        Console.WriteLine($"* Max concurrent requests: {settings.MaxConcurrentRequests}");

        var csb = new NpgsqlConnectionStringBuilder(settings.IndexReadonlyDbConnectionString);
        Console.WriteLine($"* DB Host: {csb.Host}");
        Console.WriteLine($"* DB User: {csb.Username}");
        Console.WriteLine($"* DB Name: {csb.Database}");
        Console.WriteLine($"* DB Port: {csb.Port}");
        Console.WriteLine($"* Nethermind RPC URL: {settings.NethermindRpcUrl}");
        Console.WriteLine($"* Balance Mode: {settings.BalanceMode}");
        Console.WriteLine($"* Cache Service URL: {settings.CacheServiceUrl ?? "Not configured"}");
        Console.WriteLine($"* Use Cache Service: {settings.UseCacheService}");

        var semaphore = new SemaphoreSlim(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(semaphore);

        // HTTP client factory for health checks and external API calls
        builder.Services.AddHttpClient();

        // HTTP context accessor for per-request block filtering
        builder.Services.AddHttpContextAccessor();

        // HTTP client factory for Nethermind RPC client with timeout configuration
        builder.Services.AddHttpClient<NethermindRpcClient>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        // Nethermind RPC client for health checks and balance queries
        builder.Services.AddSingleton(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var settings = sp.GetRequiredService<Settings>();
            return new NethermindRpcClient(httpClientFactory, settings.NethermindRpcUrl ?? "http://localhost:8545");
        });

        // Cache Service Client (optional - only if configured)
        if (settings.UseCacheService && !string.IsNullOrWhiteSpace(settings.CacheServiceUrl))
        {
            builder.Services.AddSingleton<CacheServiceClient.CacheServiceClient>(sp =>
            {
                var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient();
                var logger = sp.GetService<ILogger<CacheServiceClient.CacheServiceClient>>();
                return new CacheServiceClient.CacheServiceClient(httpClient, settings.CacheServiceUrl, logger);
            });
        }

        // Use the existing CirclesRpcModule with IHttpClientFactory
        builder.Services.AddSingleton<CirclesRpcModule>(sp =>
        {
            var settings = sp.GetRequiredService<Settings>();
            var httpClientFactory = sp.GetService<IHttpClientFactory>();
            var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
            var logger = sp.GetService<ILogger<CirclesRpcModule>>();
            var cacheServiceClient = settings.UseCacheService ? sp.GetService<CacheServiceClient.CacheServiceClient>() : null;
            return new CirclesRpcModule(settings, httpClientFactory, httpContextAccessor, logger, cacheServiceClient);
        });

        builder.Services.AddSingleton<CirclesSubscriptionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CirclesSubscriptionService>());

        // ─── Logging ────────────────────────────────────────────────────────────────
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => { o.FormatterName = ConsoleFormatterNames.Simple; });
        builder.Logging.Configure(o =>
        {
            o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId |
                                        ActivityTrackingOptions.SpanId;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Circles.Rpc.Host", LogLevel.Debug);

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
            // nethermind connectivity
            .AddCheck<NethermindConnectionHealthCheck>("nethermind-connection", tags: new[] { "nethermind-connection" })
            // nethermind sync status
            .AddCheck<NethermindSyncHealthCheck>("nethermind-sync", tags: new[] { "nethermind-sync" })
            // pathfinder connectivity (optional dependency - degrades gracefully)
            .AddCheck<PathfinderConnectionHealthCheck>("pathfinder-connection", tags: new[] { "pathfinder-connection" })
            // database connectivity
            .AddCheck<DatabaseConnectionHealthCheck>("database-connection", tags: new[] { "database-connection" })
            // indexer sync status (checks if indexer is caught up with chain head)
            .AddCheck<IndexerSyncHealthCheck>("indexer-sync", tags: new[] { "indexer-sync" });

        // ─── Misc DI ────────────────────────────────────────────────────────────────
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        return builder;
    }
}