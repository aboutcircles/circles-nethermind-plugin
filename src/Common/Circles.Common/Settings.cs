namespace Circles.Common;

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
    /// The block number to reindex from. Without REINDEX_TABLES this reindexes ALL tables only
    /// when REINDEX_ALL_TABLES=true is also set.
    /// With REINDEX_TABLES this deletes and backfills only the named physical tables.
    /// Example: REINDEX_FROM_BLOCK=38900000 REINDEX_TABLES=CrcV2_ScoreGroup_HistoricalSupply,CrcV2_ScoreGroup_PersonalMinted
    /// IMPORTANT: Remove these env vars after reindexing completes to avoid re-running on restart.
    /// </summary>
    public readonly long? ReindexFromBlock;

    public readonly bool ReindexAllTables =
        string.Equals(Environment.GetEnvironmentVariable("REINDEX_ALL_TABLES"), "true", StringComparison.OrdinalIgnoreCase);

    public readonly bool ReindexAllowPartialDependencies =
        string.Equals(Environment.GetEnvironmentVariable("REINDEX_ALLOW_PARTIAL_DEPENDENCIES"), "true", StringComparison.OrdinalIgnoreCase);

    public readonly string[] ReindexTables =
        Environment.GetEnvironmentVariable("REINDEX_TABLES")?.Split(',')
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray()
        ?? [];

    public Settings()
    {
        var reindexFromBlock = Environment.GetEnvironmentVariable("REINDEX_FROM_BLOCK");
        if (!string.IsNullOrWhiteSpace(reindexFromBlock) && long.TryParse(reindexFromBlock.Trim(), out var reindexBlock))
        {
            ReindexFromBlock = reindexBlock;
        }
    }

    #endregion

    #region Shared host configuration

    public readonly string? NethermindRpcUrl =
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL");

    public readonly int MaxConcurrentRequests =
        int.TryParse(Environment.GetEnvironmentVariable("PATHFINDER_MAX_CONCURRENT_REQUESTS"), out var maxRequests)
            ? maxRequests
            : Environment.ProcessorCount;

    public readonly int RpcMaxConcurrentRequests =
        int.TryParse(Environment.GetEnvironmentVariable("RPC_MAX_CONCURRENT_REQUESTS"), out var rpcMaxRequests)
            ? rpcMaxRequests
            : Math.Max(Environment.ProcessorCount * 4, 32);

    /// <summary>
    /// Per-IP rate limit: max RPC calls per second (batch items count individually).
    /// Set to 0 to disable rate limiting. Default: 100/s.
    /// </summary>
    public readonly int RpcRateLimitPerSecond =
        int.TryParse(Environment.GetEnvironmentVariable("RPC_RATE_LIMIT_PER_SECOND"), out var rateLimit)
            ? rateLimit
            : 100;

    /// <summary>
    /// Per-IP rate limit burst allowance. Permits short spikes above the sustained rate.
    /// Default: 200 (allows a burst of 200 then refills at RpcRateLimitPerSecond).
    /// </summary>
    public readonly int RpcRateLimitBurst =
        int.TryParse(Environment.GetEnvironmentVariable("RPC_RATE_LIMIT_BURST"), out var rateBurst)
            ? rateBurst
            : 200;

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

    public readonly string[] PaymentGatewayFactoryAddresses =
        Environment.GetEnvironmentVariable("V2_PAYMENT_GATEWAY_FACTORY_ADDRESS")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ??
        [
            "0x186725d8fe10a573dc73144f7a317fcae5314f19"
        ];

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
            "0x8f8b74fa13eaaff4176d061a0f98ad5c8e19c903"
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

    public readonly string[] ScoreGroupMintPolicies =
        Environment.GetEnvironmentVariable("V2_SCORE_GROUP_MINT_POLICIES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray()
        ?? [];

    /// <summary>
    /// Maps a score-group "treasury" (as recorded in <c>CrcV2_RegisterGroup.treasury</c>) to one
    /// or more sub-treasury addresses that actually hold collateral. Required when the on-chain
    /// treasury is a <c>ScoreTreasury</c> router/splitter that forwards tokens to score-keyed
    /// sub-treasuries rather than custody-ing them itself; without this mapping
    /// <see cref="Hub.balanceOf(treasury, collateral)"/> returns 0 and the mint-cap formula
    /// over-approves every router/migration mint.
    ///
    /// Format: semicolon-separated entries; each entry is <c>aggregator:sub1,sub2[,...]</c>.
    /// Example: <c>0xbee55b27...:0xe7dc5fae...,0x4b767d10...</c>. All addresses are normalized to
    /// lowercase. Aggregators not in this map fall back to single-treasury behavior (legacy
    /// base groups stay correct).
    /// </summary>
    public readonly Dictionary<string, string[]> ScoreTreasurySubTreasuries =
        EnvParsers.ParseAggregatorMap(
            "SCORE_TREASURY_SUBTREASURIES",
            Environment.GetEnvironmentVariable("SCORE_TREASURY_SUBTREASURIES"));

    public readonly string BaseGroupDeployer =
        Environment.GetEnvironmentVariable("BASE_GROUP_DEPLOYER")?.ToLowerInvariant()
        ?? "0xd0b5bd9962197beac4cba24244ec3587f19bd06d";

    // Invitations at Scale contracts
    public readonly string[] InvitationAtScaleInvitationFarmAddresses =
        Environment.GetEnvironmentVariable("V2_INVITATION_AT_SCALE_FARM_ADDRESSES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ??
        [
            "0xd28b7c4f148b1f1e190840a1f7a796c5525d8902"
        ];

    public readonly string[] InvitationAtScaleInvitationModuleAddresses =
        Environment.GetEnvironmentVariable("V2_INVITATION_AT_SCALE_MODULE_ADDRESSES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ??
        [
            "0x00738aca013b7b2e6cfe1690f0021c3182fa40b5"
        ];

    public readonly string[] InvitationAtScaleReferralsModuleAddresses =
        Environment.GetEnvironmentVariable("V2_INVITATION_AT_SCALE_REFERRALS_MODULE_ADDRESSES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ?? [
            "0x12105a9b291af2abb0591001155a75949b062ce5"
        ];

    public readonly string[] InvitationAtScaleQuotaGrantModuleAddresses =
        Environment.GetEnvironmentVariable("V2_INVITATION_AT_SCALE_QUOTA_GRANT_MODULE_ADDRESSES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray()
        ?? [];

    #endregion
}
