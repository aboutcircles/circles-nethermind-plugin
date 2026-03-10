namespace Circles.Pathfinder.Host;

public class Settings : Pathfinder.Settings
{
    private readonly Common.Settings _commonSettings;

    public Settings()
    {
        _commonSettings = new Common.Settings();
    }

    // Expose the common settings instance for dependency injection
    internal Common.Settings CommonSettings => _commonSettings;

    public string NethermindRpcUrl =>
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL")
        ?? throw new ArgumentException("NETHERMIND_RPC_URL is not set.");

    public int MaxConcurrentRequests = Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS")!)
        : Math.Max(Environment.ProcessorCount * 2, 8);

    /// <summary>
    /// When true, the pathfinder fetches graph data from the Cache Service instead of DB.
    /// Defaults to ON when CACHE_SERVICE_URL is configured — set USE_CACHE_GRAPH_SOURCE=false
    /// to explicitly opt out. Falls back to DB if cache is unavailable
    /// (unless CACHE_GRAPH_FALLBACK_TO_DB=false).
    /// </summary>
    public bool UseCacheGraphSource =>
        Environment.GetEnvironmentVariable("USE_CACHE_GRAPH_SOURCE")?.ToLowerInvariant() switch
        {
            "false" or "0" => false,
            "true" or "1" => true,
            _ => !string.IsNullOrWhiteSpace(CacheServiceUrl)
        };

    public string? CacheServiceUrl =>
        Environment.GetEnvironmentVariable("CACHE_SERVICE_URL");

    public int CacheGraphRequestTimeoutSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("CACHE_GRAPH_REQUEST_TIMEOUT_SECONDS"), out var timeout)
            ? timeout
            : 60;

    public bool CacheGraphFallbackToDb =>
        Environment.GetEnvironmentVariable("CACHE_GRAPH_FALLBACK_TO_DB")?.ToLowerInvariant() switch
        {
            "false" => false,
            "0" => false,
            _ => true
        };

    // Access BaseGroupRouter from common settings
    public string BaseGroupRouter => _commonSettings.BaseGroupRouter;

    // Provide IndexReadonlyDbConnectionString for Program.cs compatibility
    public string IndexReadonlyDbConnectionString => _commonSettings.IndexReadonlyDbConnectionString;

    // Router address used for post-processing Avatar→Group transfers.
    // The router node is tracked but not part of the capacity graph during pathfinding.
    public string RouterAddress =>
        Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")
        ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
}
