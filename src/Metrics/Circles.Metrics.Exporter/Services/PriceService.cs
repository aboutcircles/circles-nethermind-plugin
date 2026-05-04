using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Service for fetching cryptocurrency prices from CoinGecko.
/// Derives CRC price from GNO offer price + external GNO/USD price.
/// </summary>
public class PriceService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PriceService> _logger;
    private readonly string? _apiKey;
    private readonly double _fallbackCrcPriceUsd;
    private readonly TimeSpan _cacheExpiry;
    private volatile BalancerPriceService? _balancerPriceService;

    // Cached prices
    private double _cachedGnoPriceUsd;
    private DateTimeOffset _lastGnoPriceUpdate = DateTimeOffset.MinValue;
    private PriceSource _currentSource = PriceSource.None;

    // Balancer-derived price (market-based, independent of token offer rate)
    private double _lastBalancerDcrcXdai;

    // Rate limiting: 30 requests per minute = 1 request per 2 seconds minimum
    private DateTimeOffset _lastApiCall = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinTimeBetweenCalls = TimeSpan.FromSeconds(3);

    public enum PriceSource
    {
        None = 0,
        CoinGeckoLive = 1,
        Cached = 2,
        FallbackManual = 3,
        BalancerLive = 4
    }

    public PriceService(
        HttpClient httpClient,
        ILogger<PriceService> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;

        _apiKey = configuration["CoinGecko:ApiKey"];
        _fallbackCrcPriceUsd = configuration.GetValue<double>("Pricing:FallbackCrcPriceUsd", 0.01);
        _cacheExpiry = TimeSpan.FromMinutes(configuration.GetValue<int>("Pricing:CacheExpiryMinutes", 5));

        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("CoinGecko API key not configured. Price fetching will use fallback values.");
        }
        else
        {
            var tier = _apiKey.StartsWith("CG-Pro-", StringComparison.OrdinalIgnoreCase) ? "Pro" : "Demo/Free";
            _logger.LogInformation("CoinGecko API configured with {Tier} tier key", tier);
        }
    }

    /// <summary>
    /// Gets current GNO price in USD from CoinGecko (with caching and rate limiting).
    /// </summary>
    public async Task<(double price, PriceSource source)> GetGnoPriceUsdAsync(CancellationToken ct = default)
    {
        // Check if cache is still valid
        if (_lastGnoPriceUpdate + _cacheExpiry > DateTimeOffset.UtcNow && _cachedGnoPriceUsd > 0)
        {
            return (_cachedGnoPriceUsd, PriceSource.Cached);
        }

        // Rate limiting check
        if (DateTimeOffset.UtcNow - _lastApiCall < MinTimeBetweenCalls)
        {
            if (_cachedGnoPriceUsd > 0)
            {
                return (_cachedGnoPriceUsd, PriceSource.Cached);
            }
            // No cache, can't call API yet - return 0
            return (0, PriceSource.None);
        }

        // Try to fetch from CoinGecko
        if (!string.IsNullOrEmpty(_apiKey))
        {
            try
            {
                var price = await FetchGnoPriceFromCoinGeckoAsync(ct);
                if (price > 0)
                {
                    _cachedGnoPriceUsd = price;
                    _lastGnoPriceUpdate = DateTimeOffset.UtcNow;
                    _currentSource = PriceSource.CoinGeckoLive;
                    return (price, PriceSource.CoinGeckoLive);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch GNO price from CoinGecko");
            }
        }

        // Return cached if available
        if (_cachedGnoPriceUsd > 0)
        {
            return (_cachedGnoPriceUsd, PriceSource.Cached);
        }

        // No price available
        return (0, PriceSource.None);
    }

    /// <summary>
    /// Calculates CRC price in USD based on GNO offer price.
    /// Formula: CRC/USD = (1 / tokenPriceInCrc) × GNO/USD
    /// Where tokenPriceInCrc = how much CRC to get 1 GNO
    ///
    /// Fallback chain: CoinGecko → Balancer → Cache → FallbackManual
    /// Also fetches Balancer market price independently for the circles_crc_price_balancer_xdai gauge.
    /// </summary>
    public async Task<(double crcPriceUsd, double crcPriceGno, PriceSource source)> GetCrcPriceAsync(
        double tokenPriceInCrc,
        CancellationToken ct = default)
    {
        // Always try to fetch Balancer market price (independent of CoinGecko)
        await TryUpdateBalancerPriceAsync(ct);

        // tokenPriceInCrc = CRC per 1 GNO (e.g., 10000 CRC = 1 GNO)
        // So CRC/GNO = 1 / tokenPriceInCrc (e.g., 0.0001 GNO per CRC)
        var crcPriceGno = tokenPriceInCrc > 0 ? 1.0 / tokenPriceInCrc : 0;

        // Step 1: Try CoinGecko (primary)
        var (gnoPriceUsd, source) = await GetGnoPriceUsdAsync(ct);

        if (gnoPriceUsd > 0 && crcPriceGno > 0)
        {
            var crcPriceUsd = crcPriceGno * gnoPriceUsd;
            _logger.LogDebug(
                "CRC price calculated: ${CrcUsd:F6} (1 CRC = {CrcGno:F8} GNO, GNO = ${GnoUsd:F2})",
                crcPriceUsd, crcPriceGno, gnoPriceUsd);
            return (crcPriceUsd, crcPriceGno, source);
        }

        // Step 2: Try Balancer (fallback) — gives dCRC/xDAI ≈ USD directly
        if (_lastBalancerDcrcXdai > 0)
        {
            _logger.LogInformation("Using Balancer market price as fallback: dCRC/xDAI={Price:F8}", _lastBalancerDcrcXdai);
            _currentSource = PriceSource.BalancerLive;
            return (_lastBalancerDcrcXdai, crcPriceGno, PriceSource.BalancerLive);
        }

        // Step 3: Fallback manual
        if (tokenPriceInCrc <= 0)
        {
            _logger.LogDebug("No price sources available, using fallback CRC price: ${FallbackPrice}", _fallbackCrcPriceUsd);
            return (_fallbackCrcPriceUsd, 0, PriceSource.FallbackManual);
        }

        return (_fallbackCrcPriceUsd, crcPriceGno, PriceSource.FallbackManual);
    }

    private async Task TryUpdateBalancerPriceAsync(CancellationToken ct)
    {
        if (_balancerPriceService is null || !_balancerPriceService.IsConfigured)
            return;

        try
        {
            var scrcXdai = await _balancerPriceService.GetScrcXdaiPriceAsync(ct);
            if (scrcXdai > 0)
            {
                _lastBalancerDcrcXdai = BalancerPriceService.ConvertScrcToDcrcPrice(scrcXdai, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Balancer price fetch failed (non-critical)");
        }
    }

    /// <summary>
    /// Sets the Balancer price service for fallback pricing.
    /// Called after DI construction to avoid circular dependency.
    /// </summary>
    public void SetBalancerPriceService(BalancerPriceService balancerPriceService)
    {
        _balancerPriceService = balancerPriceService;
    }

    /// <summary>
    /// Gets the last Balancer-derived dCRC/xDAI price (market-based).
    /// This is independent of the administered token offer rate.
    /// </summary>
    public double LastBalancerDcrcXdai => _lastBalancerDcrcXdai;

    /// <summary>
    /// Gets the current price source indicator.
    /// </summary>
    public PriceSource CurrentSource => _currentSource;

    /// <summary>
    /// Gets the timestamp of the last successful price update.
    /// </summary>
    public DateTimeOffset LastUpdate => _lastGnoPriceUpdate;

    private async Task<double> FetchGnoPriceFromCoinGeckoAsync(CancellationToken ct)
    {
        _lastApiCall = DateTimeOffset.UtcNow;

        // GNO token ID on CoinGecko: "gnosis"
        // Determine API tier based on key prefix:
        // - "CG-Pro-" = Pro API (pro-api.coingecko.com, x-cg-pro-api-key)
        // - "CG-" = Demo/Free API (api.coingecko.com, x-cg-demo-api-key)
        var isProKey = _apiKey?.StartsWith("CG-Pro-", StringComparison.OrdinalIgnoreCase) ?? false;
        var baseUrl = isProKey ? "https://pro-api.coingecko.com" : "https://api.coingecko.com";
        var headerName = isProKey ? "x-cg-pro-api-key" : "x-cg-demo-api-key";

        var url = $"{baseUrl}/api/v3/simple/price?ids=gnosis&vs_currencies=usd";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(headerName, _apiKey);
        request.Headers.Add("Accept", "application/json");

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "CoinGecko API returned {StatusCode}: {ReasonPhrase}",
                response.StatusCode, response.ReasonPhrase);
            return 0;
        }

        var data = await response.Content.ReadFromJsonAsync<CoinGeckoResponse>(ct);

        if (data?.Gnosis?.Usd > 0)
        {
            _logger.LogInformation("Fetched GNO price from CoinGecko: ${Price:F2}", data.Gnosis.Usd);
            return data.Gnosis.Usd;
        }

        return 0;
    }

    /// <summary>
    /// Fetches xDAI/EUR rate from CoinGecko for a given date range.
    /// Uses DAI as proxy (xDAI is bridged DAI on Gnosis Chain, 1:1 peg).
    /// </summary>
    public async Task<double> GetXdaiEurRateAsync(DateOnly targetDate, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
            return 0;

        try
        {
            var targetTs = new DateTimeOffset(targetDate.ToDateTime(TimeOnly.Parse("12:00")), TimeSpan.Zero)
                .ToUnixTimeSeconds();
            var from = targetTs - 86400;
            var to = targetTs + 86400;

            var isProKey = _apiKey.StartsWith("CG-Pro-", StringComparison.OrdinalIgnoreCase);
            var baseUrl = isProKey ? "https://pro-api.coingecko.com" : "https://api.coingecko.com";
            var headerName = isProKey ? "x-cg-pro-api-key" : "x-cg-demo-api-key";

            var url = $"{baseUrl}/api/v3/coins/dai/market_chart/range?vs_currency=eur&from={from}&to={to}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(headerName, _apiKey);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CoinGecko DAI/EUR API returned {StatusCode}", response.StatusCode);
                return 0;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!json.TryGetProperty("prices", out var prices))
                return 0;

            // Find the closest price point to target timestamp
            double closestPrice = 0;
            long closestDist = long.MaxValue;
            var targetMs = targetTs * 1000;

            foreach (var entry in prices.EnumerateArray())
            {
                if (entry.GetArrayLength() < 2) continue;
                var ts = entry[0].GetInt64();
                var price = entry[1].GetDouble();
                var dist = Math.Abs(ts - targetMs);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestPrice = price;
                }
            }

            if (closestPrice > 0)
            {
                _logger.LogDebug("CoinGecko DAI/EUR for {Date}: {Rate:F6}", targetDate, closestPrice);
            }

            return closestPrice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch DAI/EUR rate for {Date}", targetDate);
            return 0;
        }
    }

    // CoinGecko response DTOs
    private class CoinGeckoResponse
    {
        [JsonPropertyName("gnosis")]
        public GnosisPrice? Gnosis { get; set; }
    }

    private class GnosisPrice
    {
        [JsonPropertyName("usd")]
        public double Usd { get; set; }
    }
}
