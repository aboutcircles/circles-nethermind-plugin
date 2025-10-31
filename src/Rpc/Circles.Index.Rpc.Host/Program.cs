using System.IO.Compression;
using Circles.Index.Common;
using Circles.Index.Rpc.Host;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using Prometheus;
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

var semaphore = new SemaphoreSlim(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(semaphore);

// ─── Logging ────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => { o.FormatterName = ConsoleFormatterNames.Simple; });
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
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" });

// ─── Misc DI ────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(_ => { });


var app = builder.Build();

app.UseHttpMetrics();
app.UseResponseCompression();
app.MapMetrics();

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

app.Run();