namespace Circles.Index.Common;

/// <summary>
/// Specifies how the indexer writes batches to the database.
/// </summary>
public enum WriteMode
{
    /// <summary>
    /// Use PostgreSQL COPY for maximum performance. Fails on duplicate keys.
    /// </summary>
    Copy,

    /// <summary>
    /// Use INSERT ... ON CONFLICT DO NOTHING. Handles duplicates gracefully but slower.
    /// </summary>
    Upsert,

    /// <summary>
    /// Use COPY normally, automatically fall back to Upsert on duplicate key errors.
    /// This is the recommended mode for production as it provides both performance and resilience.
    /// </summary>
    Auto
}

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

    public readonly int BlockBufferSize =
        int.TryParse(Environment.GetEnvironmentVariable("BLOCK_BUFFER_SIZE"), out var blockBufferSize)
            ? blockBufferSize
            : 20000;

    public readonly int EventBufferSize =
        int.TryParse(Environment.GetEnvironmentVariable("EVENT_BUFFER_SIZE"), out var eventBufferSize)
            ? eventBufferSize
            : 100000;

    /// <summary>
    /// Controls how the indexer writes batches to the database.
    /// - Copy: Use PostgreSQL COPY for maximum performance (fails on duplicate keys)
    /// - Upsert: Use INSERT ... ON CONFLICT DO NOTHING (handles duplicates, slower)
    /// - Auto: Use COPY normally, fall back to Upsert on duplicate key errors (default, recommended)
    /// </summary>
    public readonly WriteMode WriteMode =
        Enum.TryParse<WriteMode>(Environment.GetEnvironmentVariable("INDEXER_WRITE_MODE"), ignoreCase: true, out var writeMode)
            ? writeMode
            : WriteMode.Auto;

    /// <summary>
    /// If set, delete all indexed data from this block number onwards and re-sync from there.
    /// This is useful for fixing indexing issues or re-indexing after a bug fix.
    /// Set to 0 or unset to disable re-indexing.
    /// </summary>
    public readonly long? ReindexFromBlock =
        long.TryParse(Environment.GetEnvironmentVariable("REINDEX_FROM_BLOCK"), out var reindexBlock) && reindexBlock > 0
            ? reindexBlock
            : null;

    /// <summary>
    /// Comma-separated list of table names to re-index (delete and re-sync).
    /// If set to "all" or not specified when REINDEX_FROM_BLOCK is set, all tables will be re-indexed.
    /// Example: "CrcV2_InvitationsAtScale_RegisterHuman,CrcV2_InvitationsAtScale_AccountClaimed"
    /// </summary>
    public readonly string[] ReindexTables =
        Environment.GetEnvironmentVariable("REINDEX_TABLES")?.Split(',')
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray()
        ?? [];

    /// <summary>
    /// Per-table start blocks for catching up specific event tables.
    /// Format: "TableName1:StartBlock1,TableName2:StartBlock2"
    /// This allows syncing newly added LogParsers from their deployment block while keeping other tables up-to-date.
    /// Example: "CrcV2_InvitationsAtScale_RegisterHuman:37500000,CrcV2_InvitationsAtScale_AccountClaimed:37500000"
    /// </summary>
    public readonly Dictionary<string, long> TableStartBlocks =
        ParseTableStartBlocks(Environment.GetEnvironmentVariable("TABLE_START_BLOCKS"));

    private static Dictionary<string, long> ParseTableStartBlocks(string? value)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var pair in value.Split(','))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[1].Trim(), out var block))
            {
                result[parts[0].Trim()] = block;
            }
        }
        return result;
    }

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

    public readonly string[] InvitationEscrowContract =
        Environment.GetEnvironmentVariable("V2_INVITATION_ESCROW_ADDRESS")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ?? [
            "0x0956c08ad2dcc6f4a1e0cc5ffa3a08d2a6d85f29",
            "0x8F8B74fa13eaaff4176D061a0F98ad5c8E19c903"
        ];

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

    // Invitations at Scale contracts
    public readonly string InvitationModuleAddress =
        Environment.GetEnvironmentVariable("V2_INVITATION_MODULE_ADDRESS")?.ToLowerInvariant()
        ?? "0x00738aca013b7b2e6cfe1690f0021c3182fa40b5";

    public readonly string ReferralsModuleAddress =
        Environment.GetEnvironmentVariable("V2_REFERRALS_MODULE_ADDRESS")?.ToLowerInvariant()
        ?? "0x12105a9b291af2abb0591001155a75949b062ce5";

    public readonly string InvitationFarmAddress =
        Environment.GetEnvironmentVariable("V2_INVITATION_FARM_ADDRESS")?.ToLowerInvariant()
        ?? "0xd28b7c4f148b1f1e190840a1f7a796c5525d8902";

    #endregion
}