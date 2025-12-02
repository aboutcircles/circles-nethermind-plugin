namespace Circles.Index.Common;

/// <summary>
/// This config is commonly used by the Circles.Index plugin and the Circles.Pathfinder.Host application.
/// </summary>
public class Settings
{
    public readonly string IndexDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? throw new Exception("POSTGRES_CONNECTION_STRING is not set.");

    public readonly string IndexReadonlyDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING")
        ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? throw new Exception("POSTGRES_CONNECTION_STRING is not set.");

    public readonly string PgNotifyChannel =
        Environment.GetEnvironmentVariable("CIRCLES_PG_NOTIFY_CHANNEL")
        ?? "circles_index_events";

    #region Nethermind plug-in only configuration

    public readonly string? ExternalPathfinderUrl =
        Environment.GetEnvironmentVariable("EXTERNAL_PATHFINDER_URL");

    public readonly long StartBlock =
        long.TryParse(Environment.GetEnvironmentVariable("START_BLOCK"), out var startBlock)
            ? startBlock
            : 0L;

    public readonly int BlockBufferSize = 20000;
    public readonly int EventBufferSize = 100000;

    #endregion

    #region Shared host configuration

    public readonly string? NethermindRpcUrl =
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL");

    public readonly int MaxConcurrentRequests =
        int.TryParse(Environment.GetEnvironmentVariable("PATHFINDER_MAX_CONCURRENT_REQUESTS"), out var maxRequests)
            ? maxRequests
            : Environment.ProcessorCount;

    #endregion

    #region Indexed contract addresses

    public readonly string CirclesV1HubAddress =
        Environment.GetEnvironmentVariable("V1_HUB_ADDRESS")?.ToLowerInvariant()
        ?? "0x29b9a7fbb8995b2423a71cc17cf9810798f6c543";

    public readonly string CirclesV2HubAddress =
        Environment.GetEnvironmentVariable("V2_HUB_ADDRESS")?.ToLowerInvariant()
        ?? "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8";

    public readonly string CirclesV1NameRegistry =
        Environment.GetEnvironmentVariable("V1_NAME_REGISTRY_ADDRESS")?.ToLowerInvariant()
        ?? "0x1ead7f904f6ffc619c58b85e04f890b394e08172";

    public readonly string CirclesNameRegistryAddress =
        Environment.GetEnvironmentVariable("V2_NAME_REGISTRY_ADDRESS")?.ToLowerInvariant()
        ?? "0xa27566fd89162cc3d40cb59c87aaaa49b85f3474";

    public readonly string CirclesErc20LiftAddress =
        Environment.GetEnvironmentVariable("V2_ERC20_LIFT_ADDRESS")?.ToLowerInvariant()
        ?? "0x5f99a795dd2743c36d63511f0d4bc667e6d3cdb5";

    public readonly string CirclesStandardTreasuryAddress =
        Environment.GetEnvironmentVariable("V2_STANDARD_TREASURY_ADDRESS")?.ToLowerInvariant()
        ?? "0x08f90ab73a515308f03a718257ff9887ed330c6e";

    public readonly string[] CirclesLBPFactoryAddress =
        Environment.GetEnvironmentVariable("V2_LBP_FACTORY_ADDRESS")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ??
        [
            "0xd10d53ec77ce25829b7d270d736403218af22ad9",
            "0x4bb5a425a68ed73cf0b26ce79f5eead9103c30fc",
            "0xeced91232c609a42f6016860e8223b8aecaa7bd0"
        ];

    public readonly string[] CirclesTokenOfferFactoryAddress =
        Environment.GetEnvironmentVariable("V2_TOKEN_OFFER_FACTORY_ADDRESS")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ?? ["0x43c8e7cb2fea3a55b52867bb521ebf8cb072feca"];

    public readonly string[] CMGroupDeployer =
        Environment.GetEnvironmentVariable("V2_CMGROUP_DEPLOYER")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ??
        [
            "0x55785b41703728f1f1f05e77e22b13c3fcc9ce65",
            "0xfeca40eb02fb1f4f5f795fc7a03c1a27819b1ded"
        ];

    public readonly string[] SafeProxyFactoryAddresses =
        Environment.GetEnvironmentVariable("SAFE_PROXY_FACTORY_ADDRESSES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ??
        [
            "0x8b4404de0caece4b966a9959f134f0efda636156",
            "0x12302fe9c02ff50939baaaaf415fc226c078613c",
            "0x76e2cfc1f5fa8f6a5b3fc4c8f4788f0116861f9b",
            "0xa6b71e26c5e0845f74c812102ca7114b6a896ab2",
            "0x4e1dcf7ad4e460cfd30791ccc4f9c8a4f820ec67"
        ];

    public readonly string InvitationEscrowContract =
        Environment.GetEnvironmentVariable("V2_INVITATION_ESCROW_ADDRESS")?.ToLowerInvariant()
        ?? "0x0956c08ad2dcc6f4a1e0cc5ffa3a08d2a6d85f29";

    public readonly string AffiliateGroupRegistry =
        Environment.GetEnvironmentVariable("V2_AFFILIATE_GROUP_REGISTRY_ADDRESS")?.ToLowerInvariant()
        ?? "0xca8222e780d046707083f51377b5fd85e2866014";

    public readonly string OICContractAddress =
        Environment.GetEnvironmentVariable("V2_OIC_ADDRESS")?.ToLowerInvariant()
        ?? "0x6fff09332ae273ba7095a2a949a7f4b89eb37c52";

    public readonly string BaseGroupRouter =
        Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")?.ToLowerInvariant()
        ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    public readonly string BaseGroupDeployer = 
        Environment.GetEnvironmentVariable("BASE_GROUP_DEPLOYER")?.ToLowerInvariant()
        ?? "0xd0b5bd9962197beac4cba24244ec3587f19bd06d";

    #endregion
}