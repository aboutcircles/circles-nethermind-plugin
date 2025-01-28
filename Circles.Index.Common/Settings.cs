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

    public readonly Address CirclesNameRegistryAddress =
        Environment.GetEnvironmentVariable("V2_NAME_REGISTRY_ADDRESS") != null
            ? new(Environment.GetEnvironmentVariable("V2_NAME_REGISTRY_ADDRESS")!)
            : throw new Exception("V2_NAME_REGISTRY_ADDRESS is not set.");

    public readonly Address CirclesErc20LiftAddress =
        Environment.GetEnvironmentVariable("V2_ERC20_LIFT_ADDRESS") != null
            ? new(Environment.GetEnvironmentVariable("V2_ERC20_LIFT_ADDRESS")!)
            : throw new Exception("V2_ERC20_LIFT_ADDRESS is not set.");

    public readonly Address CirclesStandardTreasuryAddress =
        Environment.GetEnvironmentVariable("V2_STANDARD_TREASURY_ADDRESS") != null
            ? new(Environment.GetEnvironmentVariable("V2_STANDARD_TREASURY_ADDRESS")!)
            : throw new Exception("V2_STANDARD_TREASURY_ADDRESS is not set.");

    public readonly Address? CirclesLBPFactoryAddress =
        Environment.GetEnvironmentVariable("V2_LBP_FACTORY_ADDRESS") != null
            ? new(Environment.GetEnvironmentVariable("V2_LBP_FACTORY_ADDRESS")!)
            : null;

    public readonly string IndexDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? throw new Exception("POSTGRES_CONNECTION_STRING is not set.");

    public readonly string? IndexReadonlyDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING");

    public readonly string? ExternalPathfinderUrl =
        Environment.GetEnvironmentVariable("EXTERNAL_PATHFINDER_URL");

    public readonly long StartBlock = Environment.GetEnvironmentVariable("START_BLOCK") != null
        ? long.Parse(Environment.GetEnvironmentVariable("START_BLOCK")!)
        : 0L;

    public readonly int MaxRetries = Environment.GetEnvironmentVariable("MAX_RETRIES") != null
        ? int.Parse(Environment.GetEnvironmentVariable("MAX_RETRIES")!)
        : 3;

    public readonly int MaxRetryDelayJitter = Environment.GetEnvironmentVariable("MAX_RETRY_DELAY_JITTER") != null
        ? int.Parse(Environment.GetEnvironmentVariable("MAX_RETRY_DELAY_JITTER")!)
        : 2500;

    public readonly int BlockBufferSize = 20000;
    public readonly int EventBufferSize = 100000;

    // public readonly long StartBlock = Environment.GetEnvironmentVariable("START_BLOCK") != null
    //     ? long.Parse(Environment.GetEnvironmentVariable("START_BLOCK")!)
    //     : 12541946L;
}