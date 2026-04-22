using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Common;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Fetches sCRC/xDAI market price from the Balancer V3 SOR API.
/// Used as a fallback when CoinGecko is unavailable, and as the primary
/// source for the market-based CRC price (vs. the administered token offer rate).
/// </summary>
public class BalancerPriceService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BalancerPriceService> _logger;
    private readonly string _balancerApiUrl;
    private string _referenceScrcAddress;

    private const string WXDAI_ADDRESS = "0xe91D153E0b41518A2Ce8Dd3D7944Fa863463a97d";
    private const uint INFLATION_DAY_ZERO = 1_602_720_000;

    // Rate limiting: avoid hammering the Balancer API
    private DateTimeOffset _lastApiCall = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinTimeBetweenCalls = TimeSpan.FromSeconds(5);

    // Cache
    private double _cachedScrcXdaiPrice;
    private DateTimeOffset _lastPriceUpdate = DateTimeOffset.MinValue;
    private readonly TimeSpan _cacheExpiry;

    public BalancerPriceService(
        HttpClient httpClient,
        ILogger<BalancerPriceService> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;

        _balancerApiUrl = configuration["Balancer:ApiUrl"] ?? "https://api-v3.balancer.fi/graphql";
        _referenceScrcAddress = configuration["Balancer:ReferenceScrcAddress"] ?? "";
        _cacheExpiry = TimeSpan.FromMinutes(configuration.GetValue<int>("Balancer:CacheExpiryMinutes", 5));

        if (string.IsNullOrEmpty(_referenceScrcAddress))
        {
            _logger.LogWarning("Balancer:ReferenceScrcAddress not configured. Balancer pricing disabled.");
        }
        else if (!Regex.IsMatch(_referenceScrcAddress, @"^0x[0-9a-fA-F]{40}$"))
        {
            _logger.LogError("Balancer:ReferenceScrcAddress has invalid format: {Address}. Must be 0x + 40 hex chars. Balancer pricing disabled.",
                _referenceScrcAddress);
            _referenceScrcAddress = "";
        }
        else
        {
            _logger.LogInformation("Balancer pricing configured: sCRC={ScrcAddress}, API={ApiUrl}",
                _referenceScrcAddress, _balancerApiUrl);
        }
    }

    /// <summary>
    /// Whether the service is configured with a reference sCRC address.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_referenceScrcAddress);

    /// <summary>
    /// Gets the current sCRC/xDAI price from Balancer V3 SOR API.
    /// Returns 0 if unavailable or not configured.
    /// </summary>
    public async Task<double> GetScrcXdaiPriceAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;

        // Check cache
        if (_lastPriceUpdate + _cacheExpiry > DateTimeOffset.UtcNow && _cachedScrcXdaiPrice > 0)
            return _cachedScrcXdaiPrice;

        // Rate limiting
        if (DateTimeOffset.UtcNow - _lastApiCall < MinTimeBetweenCalls)
            return _cachedScrcXdaiPrice;

        try
        {
            var price = await FetchScrcXdaiPriceAsync(ct);
            if (price > 0)
            {
                _cachedScrcXdaiPrice = price;
                _lastPriceUpdate = DateTimeOffset.UtcNow;
            }
            return price;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch sCRC/xDAI price from Balancer V3");
            return _cachedScrcXdaiPrice; // return stale cache on error
        }
    }

    /// <summary>
    /// Converts an sCRC/xDAI price to dCRC/xDAI using pure C# demurrage math.
    /// No RPC calls needed — uses CirclesConverter from Circles.Common.
    /// </summary>
    public static double ConvertScrcToDcrcPrice(double scrcXdaiPrice, DateTimeOffset timestamp)
    {
        if (scrcXdaiPrice <= 0) return 0;

        var day = CirclesConverter.DayFromTimestamp(timestamp, INFLATION_DAY_ZERO);
        var oneUnit = BigInteger.Parse("1000000000000000000"); // 1e18
        var demurragedUnit = CirclesConverter.InflationaryToDemurrage(oneUnit, day);
        var convFactor = (double)demurragedUnit / 1e18;

        if (convFactor <= 0) return 0;

        return scrcXdaiPrice / convFactor;
    }

    /// <summary>
    /// Gets the demurrage conversion factor for a given timestamp.
    /// convFactor = InflationaryToDemurrage(1e18, day) / 1e18
    /// </summary>
    public static double GetConvFactor(DateTimeOffset timestamp)
    {
        var day = CirclesConverter.DayFromTimestamp(timestamp, INFLATION_DAY_ZERO);
        var oneUnit = BigInteger.Parse("1000000000000000000");
        var demurragedUnit = CirclesConverter.InflationaryToDemurrage(oneUnit, day);
        return (double)demurragedUnit / 1e18;
    }

    /// <summary>
    /// Gets the historic sCRC/xDAI price from Balancer V3 tokenGetHistoricalPrices API.
    /// Returns the closest price to the target date. Returns 0 if unavailable.
    /// </summary>
    public async Task<double> GetHistoricScrcXdaiPriceAsync(DateOnly targetDate, CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;

        try
        {
            // Use ALL range for maximum coverage (daily resolution, goes back to pool creation)
            var query = $$"""
            {
              tokenGetHistoricalPrices(
                addresses: ["{{_referenceScrcAddress}}"],
                chain: GNOSIS,
                range: ALL
              ) {
                prices { price timestamp }
              }
            }
            """;

            var request = new HttpRequestMessage(HttpMethod.Post, _balancerApiUrl)
            {
                Content = JsonContent.Create(new { query })
            };

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Balancer V3 historic prices API returned {StatusCode}", response.StatusCode);
                return 0;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            if (!json.TryGetProperty("data", out var data)
                || !data.TryGetProperty("tokenGetHistoricalPrices", out var historicalArr)
                || historicalArr.GetArrayLength() == 0)
                return 0;

            var prices = historicalArr[0].GetProperty("prices");
            var targetUnix = new DateTimeOffset(targetDate.ToDateTime(TimeOnly.Parse("12:00")), TimeSpan.Zero)
                .ToUnixTimeSeconds();

            // Find the closest price point to the target date
            double closestPrice = 0;
            long closestDist = long.MaxValue;

            foreach (var entry in prices.EnumerateArray())
            {
                if (!long.TryParse(entry.GetProperty("timestamp").GetString(), out var ts))
                    continue;

                var dist = Math.Abs(ts - targetUnix);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestPrice = entry.GetProperty("price").GetDouble();
                }
            }

            if (closestPrice > 0)
            {
                _logger.LogDebug("Historic Balancer price for {Date}: sCRC/xDAI={Price:F8} (dist={Dist}s)",
                    targetDate, closestPrice, closestDist);
            }

            return closestPrice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch historic sCRC/xDAI price for {Date}", targetDate);
            return 0;
        }
    }

    private async Task<double> FetchScrcXdaiPriceAsync(CancellationToken ct)
    {
        _lastApiCall = DateTimeOffset.UtcNow;

        // Query Balancer V3 SOR for sCRC → WXDAI swap price
        // Use 0.1 as swap amount (same as profileChecker.html), multiply result by 10
        var query = $$"""
        {
          sorGetSwapPaths(
            chain: GNOSIS,
            swapAmount: "0.1",
            swapType: EXACT_IN,
            tokenIn: "{{_referenceScrcAddress}}",
            tokenOut: "{{WXDAI_ADDRESS}}"
          ) {
            returnAmount
          }
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, _balancerApiUrl)
        {
            Content = JsonContent.Create(new { query })
        };

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Balancer V3 API returned {StatusCode}", response.StatusCode);
            return 0;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        if (json.TryGetProperty("data", out var data)
            && data.TryGetProperty("sorGetSwapPaths", out var sor)
            && sor.TryGetProperty("returnAmount", out var returnAmount))
        {
            var amountStr = returnAmount.GetString();
            if (double.TryParse(amountStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0)
            {
                // Query used 0.1 tokens, multiply by 10 to get price for 1 token
                var price = amount * 10;
                _logger.LogInformation("Balancer V3 sCRC/xDAI price: {Price:F8}", price);
                return price;
            }
        }

        _logger.LogWarning("Balancer V3 API returned unexpected response structure");
        return 0;
    }
}
