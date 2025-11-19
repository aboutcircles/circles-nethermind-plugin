namespace Circles.Pathfinder.Host;

public class Settings : Pathfinder.Settings
{
    private readonly Index.Common.Settings _commonSettings;

    public Settings()
    {
        _commonSettings = new Index.Common.Settings();
    }

    // Expose the common settings instance for dependency injection
    internal Index.Common.Settings CommonSettings => _commonSettings;

    public string NethermindRpcUrl =>
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL")
        ?? throw new ArgumentException("NETHERMIND_RPC_URL is not set.");

    public int MaxConcurrentRequests = Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS")!)
        : Environment.ProcessorCount * 2;

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
