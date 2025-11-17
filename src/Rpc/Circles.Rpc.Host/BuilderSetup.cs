using System.IO.Compression;
using Circles.Index.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Logging;

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

        var semaphore = new SemaphoreSlim(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(semaphore);

        // Nethermind RPC client for health checks and balance queries
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<Settings>();
            return new NethermindRpcClient(settings.NethermindRpcUrl ?? "http://localhost:8545");
        });

        // Use the existing CirclesRpcModule
        builder.Services.AddSingleton<CirclesRpcModule>();

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
            .AddCheck<NethermindSyncHealthCheck>("nethermind-sync", tags: new[] { "nethermind-sync" });

        // ─── Misc DI ────────────────────────────────────────────────────────────────
        builder.Services.ConfigureHttpJsonOptions(_ => { });

        return builder;
    }
}