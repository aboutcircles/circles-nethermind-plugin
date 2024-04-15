using Nethermind.Core;

namespace Circles.Index;

public class Settings
{
    public readonly string IndexDbFileName = "circles-index.sqlite";

    public readonly Address CirclesV1HubAddress = Environment.GetEnvironmentVariable("V1_HUB_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V1_HUB_ADDRESS")!)
        : new("0x29b9a7fBb8995b2423a71cC17cf9810798F6C543");

    public readonly Address CirclesV2HubAddress = Environment.GetEnvironmentVariable("V2_HUB_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V2_HUB_ADDRESS")!)
        : new("0x29b9a7fBb8995b2423a71cC17cf9810798F6C543");

    public readonly string IndexDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? "Server=postgres;Port=5432;User Id=postgres;Password=postgres;Database=postgres;";

    public readonly long StartBlock = Environment.GetEnvironmentVariable("START_BLOCK") != null
        ? long.Parse(Environment.GetEnvironmentVariable("START_BLOCK")!)
        : 12541946L;

    public readonly string PathfinderRpcUrl = "http://localhost:8080";

    public readonly string PathfinderDbFileName = "pathfinder.db";

    public static readonly int InitialUserCacheSize = 150000;

    public static readonly int InitialOrgCacheSize = 15000;

    public ulong ChainId { get; set; } = Environment.GetEnvironmentVariable("CHAIN_ID") != null
        ? ulong.Parse(Environment.GetEnvironmentVariable("CHAIN_ID")!)
        : 100;

    public int MaxParallelism { get; set; } = 0;
}