namespace Circles.Pathfinder.Host;

public class Settings : Circles.Index.Common.Settings
{
    public new readonly string IndexReadonlyDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING")
        ?? throw new ArgumentException("POSTGRES_READONLY_CONNECTION_STRING is not set.");

    public readonly string NethermindRpcUrl =
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL")
        ?? throw new ArgumentException("NETHERMIND_RPC_URL is not set.");

    public new readonly int MaxConcurrentRequests = Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS")!)
        : Environment.ProcessorCount * 2;

    // Router address used for post-processing Avatar→Group transfers.
    // The router node is tracked but not part of the capacity graph during pathfinding.
    public readonly string RouterAddress =
        Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")
        ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
}