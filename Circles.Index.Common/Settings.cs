using Nethermind.Core;

namespace Circles.Index.Common;

public class Settings
{
    public readonly Address CirclesV1HubAddress = Environment.GetEnvironmentVariable("V1_HUB_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V1_HUB_ADDRESS")!)
        : throw new Exception("V1_HUB_ADDRESS is not set.");

    public readonly Address CirclesV2HubAddress = Environment.GetEnvironmentVariable("V2_HUB_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V2_HUB_ADDRESS")!)
        : throw new Exception("V2_HUB_ADDRESS is not set.");
    
    public readonly Address CirclesNameRegistryAddress = Environment.GetEnvironmentVariable("V2_NAME_REGISTRY_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V2_NAME_REGISTRY_ADDRESS")!)
        : throw new Exception("V2_NAME_REGISTRY_ADDRESS is not set.");

    public readonly string IndexDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? throw new Exception("POSTGRES_CONNECTION_STRING is not set.");

    public readonly int BlockBufferSize = 20000;
    public readonly int EventBufferSize = 100000;

    // public readonly long StartBlock = Environment.GetEnvironmentVariable("START_BLOCK") != null
    //     ? long.Parse(Environment.GetEnvironmentVariable("START_BLOCK")!)
    //     : 12541946L;
}