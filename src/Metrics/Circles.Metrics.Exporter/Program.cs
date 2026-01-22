using Circles.Metrics.Exporter.Services;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Add configuration from environment variables
builder.Configuration.AddEnvironmentVariables();

// Get connection string from configuration
var connectionString = builder.Configuration.GetConnectionString("CirclesDb")
    ?? throw new InvalidOperationException("ConnectionStrings:CirclesDb is required");

// Register services
builder.Services.AddSingleton(sp =>
    new KpiRepository(connectionString, sp.GetRequiredService<ILogger<KpiRepository>>()));

// Register HttpClient and PriceService for CoinGecko integration
builder.Services.AddHttpClient<PriceService>();
builder.Services.AddSingleton<PriceService>();

builder.Services.AddHostedService<KpiCollectorService>();

// Liquidity monitoring services
builder.Services.AddSingleton(sp =>
    new LiquidityRepository(
        connectionString,
        sp.GetRequiredService<ILogger<LiquidityRepository>>()));

builder.Services.AddHostedService<LiquidityCollectorService>();

// Trust score monitoring services
builder.Services.AddSingleton(sp =>
    new TrustRepository(
        connectionString,
        sp.GetRequiredService<ILogger<TrustRepository>>()));

builder.Services.AddHostedService<TrustCollectorService>();

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Health check endpoints
app.MapHealthChecks("/health");
app.MapGet("/ready", () => Results.Ok("Ready"));

// Prometheus metrics endpoint
app.MapMetrics();

// Info endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "Circles Metrics Exporter",
    Version = "1.2.0",
    Endpoints = new[]
    {
        "/metrics - Prometheus metrics (KPIs + Liquidity + Trust Scores)",
        "/health - Health check",
        "/ready - Readiness check"
    },
    Features = new[]
    {
        "Business KPIs (users, trusts, transfers, groups)",
        "Liquidity monitoring (Balancer vault, group treasuries)",
        "Drain detection (z-score anomaly detection)",
        "Whale transfer tracking",
        "Trust score monitoring (distribution, anomalies, network health)"
    }
}));

app.Run();
