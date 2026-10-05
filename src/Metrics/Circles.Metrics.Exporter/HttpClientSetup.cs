namespace Circles.Metrics.Exporter;

public static class HttpClientSetup
{
    /// <summary>
    /// Sent on every outgoing request. CoinGecko answers 403 to a request without a
    /// User-Agent header, and HttpClient sends none by default.
    /// </summary>
    public const string UserAgent = "circles-metrics-exporter";

    /// <summary>
    /// Sets the User-Agent on every client the factory creates. The price services are
    /// registered as singletons after their typed clients, so they receive the unnamed
    /// default client; configuring only the typed clients would not reach them.
    /// </summary>
    public static IServiceCollection AddHttpClientUserAgent(this IServiceCollection services) =>
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)));
}
