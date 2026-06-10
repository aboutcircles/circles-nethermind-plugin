using Circles.Metrics.Exporter.Services;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Add configuration from environment variables
builder.Configuration.AddEnvironmentVariables();

// Get connection string from configuration
var connectionString = builder.Configuration.GetConnectionString("CirclesDb")
    ?? throw new InvalidOperationException("ConnectionStrings:CirclesDb is required");

// RepScoreDb is optional — exporter degrades gracefully without AA
var repScoreConnectionString = builder.Configuration.GetConnectionString("RepScoreDb");

// Register services
builder.Services.AddSingleton(sp =>
    new KpiRepository(connectionString, sp.GetRequiredService<ILogger<KpiRepository>>()));

// Register HttpClient and PriceService for CoinGecko integration
builder.Services.AddHttpClient<PriceService>();
builder.Services.AddSingleton<PriceService>();

// Balancer V3 pricing (market-based, fallback for CoinGecko)
builder.Services.AddHttpClient<BalancerPriceService>();
builder.Services.AddSingleton<BalancerPriceService>();

// Price cache in PostgreSQL (cache-through pattern)
builder.Services.AddSingleton(sp =>
    new PriceCacheRepository(connectionString, sp.GetRequiredService<ILogger<PriceCacheRepository>>()));

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

// Trust score history snapshot service (runs daily for anomaly detection)
builder.Services.AddHostedService<TrustHistorySnapshotService>();

// Rep score monitoring (AA circles_rep_score DB — bad actor radar + ScoreGroup distribution)
// Optional: skip if RepScoreDb is not configured (AA may not be deployed in all environments)
if (!string.IsNullOrWhiteSpace(repScoreConnectionString))
{
    builder.Services.AddSingleton(sp =>
        new RepScoreRepository(
            repScoreConnectionString,
            connectionString,
            builder.Configuration.GetValue<string>("RepScore:GroupId", "score_group")!,
            builder.Configuration.GetValue<double>("RepScore:HighScoreThreshold", 70.0),
            builder.Configuration.GetValue<double>("RepScore:ScoreDropThreshold", 20.0),
            sp.GetRequiredService<ILogger<RepScoreRepository>>()));

    builder.Services.AddHostedService<RepScoreCollectorService>();
}
else
{
    var startupLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
    startupLogger.LogWarning("ConnectionStrings:RepScoreDb not configured — rep score metrics disabled");
}

// Deployment status monitoring (probes RPC endpoints, no DB access needed)
builder.Services.AddHttpClient<DeploymentProber>();
builder.Services.AddSingleton<DeploymentProber>();
builder.Services.AddHostedService<DeploymentCollectorService>();

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Wire up Balancer fallback into PriceService (avoids circular DI)
var priceService = app.Services.GetRequiredService<PriceService>();
var balancerService = app.Services.GetRequiredService<BalancerPriceService>();
priceService.SetBalancerPriceService(balancerService);

// Health check endpoints
app.MapHealthChecks("/health");
app.MapGet("/ready", () => Results.Ok("Ready"));

// Prometheus metrics endpoint
app.MapMetrics();

// Pricing API endpoint (cache-through: PG → Balancer/CoinGecko → PG)
app.MapGet("/pricing", async (string? date, PriceCacheRepository priceCache, BalancerPriceService balancer, PriceService prices, ILogger<Program> logger, CancellationToken ct) =>
{
    DateOnly targetDate;
    if (string.IsNullOrEmpty(date))
    {
        targetDate = DateOnly.FromDateTime(DateTime.UtcNow);
    }
    else if (!DateOnly.TryParse(date, out targetDate))
    {
        return Results.BadRequest(new { error = "Invalid date format. Use YYYY-MM-DD." });
    }

    if (targetDate > DateOnly.FromDateTime(DateTime.UtcNow))
    {
        return Results.BadRequest(new { error = "Future dates are not supported." });
    }

    // Step 1: Check PG cache
    PriceCacheRepository.DailyPrice? cached = null;
    try { cached = await priceCache.GetAsync(targetDate, ct); }
    catch (Exception ex) { logger.LogWarning(ex, "PG cache lookup failed for {Date}", targetDate); }

    if (cached is not null)
    {
        return Results.Ok(new
        {
            date = cached.Date.ToString("yyyy-MM-dd"),
            scrc_xdai = cached.ScrcXdai,
            conv_factor = cached.ConvFactor,
            dcrc_xdai = cached.DcrcXdai,
            xdai_eur = cached.XdaiEur,
            dcrc_eur = cached.DcrcEur,
            source = cached.Source,
            cached = true
        });
    }

    // Step 2: Fetch price from Balancer
    var isToday = targetDate == DateOnly.FromDateTime(DateTime.UtcNow);
    var scrcXdai = isToday
        ? await balancer.GetScrcXdaiPriceAsync(ct)
        : await balancer.GetHistoricScrcXdaiPriceAsync(targetDate, ct);

    if (scrcXdai <= 0)
    {
        var ts = new DateTimeOffset(targetDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return Results.Ok(new
        {
            date = targetDate.ToString("yyyy-MM-dd"),
            scrc_xdai = 0.0,
            conv_factor = BalancerPriceService.GetConvFactor(ts),
            dcrc_xdai = 0.0,
            xdai_eur = 0.0,
            dcrc_eur = 0.0,
            source = "unavailable",
            cached = false,
            message = "Balancer price unavailable for this date"
        });
    }

    var timestamp = new DateTimeOffset(targetDate.ToDateTime(TimeOnly.Parse("12:00")), TimeSpan.Zero);
    var convFactor = BalancerPriceService.GetConvFactor(timestamp);
    var dcrcXdai = BalancerPriceService.ConvertScrcToDcrcPrice(scrcXdai, timestamp);

    // Step 3: Fetch xDAI/EUR from CoinGecko
    var xdaiEur = await prices.GetXdaiEurRateAsync(targetDate, ct);
    var dcrcEur = xdaiEur > 0 ? dcrcXdai * xdaiEur : 0;
    var source = isToday ? "balancer_live" : "balancer_historic";

    // Step 4: Store in PG
    try
    {
        await priceCache.UpsertAsync(targetDate, scrcXdai, convFactor, dcrcXdai, xdaiEur, dcrcEur, source, ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to cache price for {Date}", targetDate);
    }

    return Results.Ok(new
    {
        date = targetDate.ToString("yyyy-MM-dd"),
        scrc_xdai = scrcXdai,
        conv_factor = convFactor,
        dcrc_xdai = dcrcXdai,
        xdai_eur = xdaiEur,
        dcrc_eur = dcrcEur,
        source,
        cached = false
    });
});

// Info endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "Circles Metrics Exporter",
    Version = "1.5.0",
    Endpoints = new[]
    {
        "/metrics - Prometheus metrics (KPIs + Liquidity + Trust Scores + Deployment)",
        "/pricing?date=YYYY-MM-DD - CRC price lookup (cache-through: PG → Balancer/CoinGecko)",
        "/health - Health check",
        "/ready - Readiness check"
    },
    Features = new[]
    {
        "Business KPIs (users, trusts, transfers, groups)",
        "Liquidity monitoring (Balancer vault, group treasuries)",
        "Drain detection (z-score anomaly detection)",
        "Whale transfer tracking",
        "Trust score monitoring (distribution, anomalies, network health)",
        "Trust score history snapshots (daily, 90-day retention)",
        "Deployment status monitoring (multi-env RPC probing, no DB access needed)",
        "CRC pricing API (Balancer V3 market price, PG cache, EUR conversion)"
    }
}));

app.Run();
