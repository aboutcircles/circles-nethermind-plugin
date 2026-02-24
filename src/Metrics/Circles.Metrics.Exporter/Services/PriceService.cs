using System.Net.Http.Json;
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

    // Cached prices
    private double _cachedGnoPriceUsd;
    private DateTimeOffset _lastGnoPriceUpdate = DateTimeOffset.MinValue;
    private PriceSource _currentSource = PriceSource.None;

    // Rate limiting: 30 requests per minute = 1 request per 2 seconds minimum
    private DateTimeOffset _lastApiCall = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinTimeBetweenCalls = TimeSpan.FromSeconds(3);

    public enum PriceSource
    {
        None = 0,
        CoinGeckoLive = 1,
        Cached = 2,
        FallbackManual = 3
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
    /// </summary>
    public async Task<(double crcPriceUsd, double crcPriceGno, PriceSource source)> GetCrcPriceAsync(
        double tokenPriceInCrc,
        CancellationToken ct = default)
    {
        // tokenPriceInCrc = CRC per 1 GNO (e.g., 10000 CRC = 1 GNO)
        // So CRC/GNO = 1 / tokenPriceInCrc (e.g., 0.0001 GNO per CRC)

        if (tokenPriceInCrc <= 0)
        {
            // No offer price available, use fallback
            _logger.LogDebug("No token offer price available, using fallback CRC price: ${FallbackPrice}", _fallbackCrcPriceUsd);
            return (_fallbackCrcPriceUsd, 0, PriceSource.FallbackManual);
        }

        var crcPriceGno = 1.0 / tokenPriceInCrc;

        var (gnoPriceUsd, source) = await GetGnoPriceUsdAsync(ct);

        if (gnoPriceUsd <= 0)
        {
            // Can't get GNO price, use fallback for CRC
            return (_fallbackCrcPriceUsd, crcPriceGno, PriceSource.FallbackManual);
        }

        var crcPriceUsd = crcPriceGno * gnoPriceUsd;

        _logger.LogDebug(
            "CRC price calculated: ${CrcUsd:F6} (1 CRC = {CrcGno:F8} GNO, GNO = ${GnoUsd:F2})",
            crcPriceUsd, crcPriceGno, gnoPriceUsd);

        return (crcPriceUsd, crcPriceGno, source);
    }

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
