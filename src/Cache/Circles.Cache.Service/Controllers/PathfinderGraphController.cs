using System.Globalization;
using System.Numerics;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
using Circles.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// Serves the full pathfinder graph snapshot from in-memory caches (zero SQL).
/// The pathfinder fetches this periodically to build its capacity graph.
/// Supports ETag-based conditional requests and selective section inclusion.
/// </summary>
[ApiController]
[Route("api/pathfinder")]
public class PathfinderGraphController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<PathfinderGraphController> _logger;

    /// <summary>
    /// Standard treasury mint address — only groups with this mint policy
    /// participate in the pathfinder's transitive transfer routing.
    /// Reads V2_STANDARD_MINT_POLICY env var (same as Pathfinder Settings.StandardMintPolicyAddress).
    /// </summary>
    private static readonly string StandardTreasuryMint =
        Environment.GetEnvironmentVariable("V2_STANDARD_MINT_POLICY")?.Trim().ToLowerInvariant()
        ?? "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";

    private static readonly HashSet<string> ScoreGroupMintPolicies =
        (Environment.GetEnvironmentVariable("V2_SCORE_GROUP_MINT_POLICIES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();

    /// <summary>
    /// Score-group treasury "aggregator" → sub-treasury address list. Same env
    /// (<c>SCORE_TREASURY_SUBTREASURIES</c>) and validated parser as the pathfinder
    /// and indexer; the Cache service reads it directly because it does not link
    /// <c>Circles.Common.Settings</c>, but routes through the same validator so
    /// malformed-env operator-typos fail-fast everywhere uniformly.
    /// </summary>
    private static readonly Dictionary<string, string[]> ScoreTreasurySubTreasuries =
        Circles.Common.EnvParsers.ParseAggregatorMap(
            "SCORE_TREASURY_SUBTREASURIES",
            Environment.GetEnvironmentVariable("SCORE_TREASURY_SUBTREASURIES"));

    private static readonly string BaseGroupRouter =
        Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")?.Trim().ToLowerInvariant()
        ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static readonly double DemurrageSafetyMargin =
        Environment.GetEnvironmentVariable("PATHFINDER_DEMURRAGE_SAFETY_MARGIN") != null
            ? double.Parse(Environment.GetEnvironmentVariable("PATHFINDER_DEMURRAGE_SAFETY_MARGIN")!, CultureInfo.InvariantCulture)
            : 0.999999;

    private const int SchemaVersion = 1;

    /// <summary>V2 Hub epoch on gnosis mainnet: 2020-10-15 00:00 UTC (same as V1).</summary>
    private const uint V2InflationDayZero = 1_602_720_000;
    private const long SecondsPerDay = 86_400;

    public PathfinderGraphController(
        CacheContainer caches,
        CacheServiceState state,
        ILogger<PathfinderGraphController> logger)
        : this(caches, state, null, logger)
    {
    }

    [ActivatorUtilitiesConstructor]
    public PathfinderGraphController(
        CacheContainer caches,
        CacheServiceState state,
        NpgsqlDataSource? dataSource,
        ILogger<PathfinderGraphController> logger)
    {
        _caches = caches;
        _state = state;
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Returns the full pathfinder graph snapshot from in-memory caches.
    /// Supports ?include=balances,trust,groups,groupTrusts,consentedFlow,avatars,wrapperMappings for selective loading.
    /// ETag is based on LastProcessedBlock for conditional 304 responses.
    /// </summary>
    [HttpGet("graph")]
    public ActionResult<PathfinderGraphResponse> GetGraph([FromQuery] string? include = null)
    {
        if (!_state.WarmupComplete)
        {
            return StatusCode(503, new { error = "Cache warmup in progress" });
        }

        var lastBlock = _state.LastProcessedBlock;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var etag = $"\"{lastBlock}\"";

        // ETag-based conditional request
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(304);
        }

        // Parse include filter (default: all sections)
        var sections = ParseInclude(include);

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache";

        try
        {
            // Pre-compute shared lookups once per request
            var registrations = new CacheRegistrationSet(_caches);
            var wrapperLookup = new CacheWrapperLookup(_caches);
            var needsRouterGroups = sections.Contains("trust") ||
                                    sections.Contains("groups") ||
                                    sections.Contains("grouprouters") ||
                                    sections.Contains("grouptrusts") ||
                                    sections.Contains("scoregroupmintlimits");
            var indexedScoreGroupRouters = needsRouterGroups ? LoadIndexedScoreGroupRouters(lastBlock) : [];
            var routerGroups = needsRouterGroups
                ? GetRouterFilteredGroups(indexedScoreGroupRouters)
                : null;
            var avatarToWrappers = sections.Contains("trust")
                ? BuildAvatarToWrappersIndex()
                : null;
            // All historical distinct routers across all score-group policies (matches DB-source
            // scoreRoutersQuery.sql, NOT just the per-group-latest in indexedScoreGroupRouters).
            // If a group re-initialises with a new router, both old and new must appear so the
            // C3 gate can resolve approvals for either; deriving from indexedScoreGroupRouters
            // alone would drop retired routers and silently fail-OPEN their approvals.
            var allScoreRouters = sections.Contains("scorerouters") || sections.Contains("operatorapprovals")
                ? LoadAllScoreRouters(lastBlock)
                : [];

            var response = new PathfinderGraphResponse(
                SchemaVersion: SchemaVersion,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp,
                Balances: sections.Contains("balances") ? BuildBalances(registrations, wrapperLookup) : null,
                Trust: sections.Contains("trust") ? BuildTrust(registrations, routerGroups!, avatarToWrappers!) : null,
                Groups: sections.Contains("groups") ? BuildGroups(routerGroups!) : null,
                GroupRouters: sections.Contains("grouprouters") ? BuildGroupRouters(routerGroups!, indexedScoreGroupRouters) : null,
                GroupTrusts: sections.Contains("grouptrusts") ? BuildGroupTrusts(registrations, routerGroups!) : null,
                ConsentedFlow: sections.Contains("consentedflow") ? BuildConsentedFlow(registrations) : null,
                Avatars: sections.Contains("avatars") ? BuildAvatars() : null,
                Organizations: sections.Contains("organizations") ? BuildOrganizations() : null,
                WrapperMappings: sections.Contains("wrappermappings") ? BuildWrapperMappings(registrations) : null,
                ScoreGroupMintLimits: sections.Contains("scoregroupmintlimits")
                    ? BuildScoreGroupMintLimits(registrations, routerGroups!, indexedScoreGroupRouters, lastBlock)
                    : null,
                ScoreRouters: sections.Contains("scorerouters")
                    ? allScoreRouters
                    : null,
                OperatorApprovals: sections.Contains("operatorapprovals")
                    ? BuildOperatorApprovals(allScoreRouters, lastBlock)
                    : null
            );

            _logger.LogDebug(
                "Pathfinder graph snapshot: block={Block}, balances={Balances}, trust={Trust}, groups={Groups}, groupTrusts={GroupTrusts}, consent={Consent}, avatars={Avatars}, orgs={Orgs}, wrappers={Wrappers}, scoreRouters={ScoreRouters}, operatorApprovals={OperatorApprovals}",
                lastBlock,
                response.Balances?.Count ?? 0,
                response.Trust?.Count ?? 0,
                response.Groups?.Count ?? 0,
                response.GroupTrusts?.Count ?? 0,
                response.ConsentedFlow?.Count ?? 0,
                response.Avatars?.Count ?? 0,
                response.Organizations?.Count ?? 0,
                response.WrapperMappings?.Count ?? 0,
                response.ScoreRouters?.Count ?? 0,
                response.OperatorApprovals?.Count ?? 0);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building pathfinder graph snapshot");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    private static HashSet<string> ParseInclude(string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
            return new HashSet<string> { "balances", "trust", "groups", "grouprouters", "grouptrusts", "scoregroupmintlimits", "consentedflow", "avatars", "organizations", "wrappermappings", "scorerouters", "operatorapprovals" };

        return new HashSet<string>(
            include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()));
    }

    /// <summary>
    /// Builds balance rows from V2BalancesByAccountAndToken.
    /// Converts cached decimal balances to attoCircles integer strings (matching LoadGraph output).
    /// Applies demurrage from lastActivity → now for demurraged balances.
    /// Converts static (inflationary) balances to demurraged equivalent at target day.
    /// </summary>
    private List<PathfinderBalanceRow> BuildBalances(IRegistrationSet registrations, IWrapperLookup wrapperLookup)
    {
        var balances = new List<PathfinderBalanceRow>();
        var targetDay = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, V2InflationDayZero);

        foreach (var kvp in _caches.V2BalancesByAccountAndToken.ReadOnlyDictionary)
        {
            if (kvp.Value <= 0)
                continue;

            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var account = kvp.Key[..separatorIndex];
            var tokenAddress = kvp.Key[(separatorIndex + 1)..];

            // Shared invariant: account + token must be registered
            if (!CirclesInvariants.IsValidBalance(account, tokenAddress, registrations, wrapperLookup))
                continue;

            // Determine if this is a wrapper token (for demurrage handling)
            var isWrapped = false;
            var isStatic = false;

            if (_caches.Erc20WrapperAddresses.TryGetValue(tokenAddress, out var wrapperInfo))
            {
                isWrapped = true;
                isStatic = wrapperInfo.CirclesType == CirclesType.InflationaryCircles;
            }

            // Convert decimal Circles → attoCircles BigInteger
            var attoBalance = CirclesConverter.CirclesToAttoCircles(kvp.Value);
            if (attoBalance == BigInteger.Zero)
                continue;

            // Get last activity timestamp.
            // Defensive: warmup and incremental paths always write lastActivity alongside
            // balance from the same source. This guard protects against hypothetical cache
            // inconsistency — skip rather than emit an undecayed balance.
            var hasLastActivity = _caches.V2LastActivity.TryGetValue(kvp.Key, out var lastActivity);

            if (isStatic)
            {
                // Static (inflationary) → convert to demurraged equivalent at target day
                attoBalance = CirclesConverter.InflationaryToDemurrage(attoBalance, targetDay);
            }
            else if (!hasLastActivity || lastActivity < V2InflationDayZero)
            {
                // Missing or pre-epoch timestamp → cannot compute demurrage → skip
                continue;
            }
            else
            {
                // Demurraged: apply demurrage from lastActivity → now
                var lastActivityDay = (ulong)(lastActivity - V2InflationDayZero) / (ulong)SecondsPerDay;
                var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;
                if (daysDelta > 0)
                {
                    attoBalance = CirclesConverter.InflationaryToDemurrage(attoBalance, daysDelta);
                }
            }

            if (attoBalance == BigInteger.Zero)
                continue;

            balances.Add(new PathfinderBalanceRow(
                Balance: attoBalance.ToString(CultureInfo.InvariantCulture),
                Account: account,
                TokenAddress: tokenAddress,
                LastActivity: lastActivity,
                IsWrapped: isWrapped,
                CirclesType: isStatic ? "static" : "demurraged"
            ));
        }

        return balances;
    }

    /// <summary>
    /// Builds trust rows from V2TrustRelations.
    /// Filters: registered avatars only, non-revoked, non-group trusters.
    /// Derives wrapper trust edges: if trustee has a wrapper, emit (truster, wrapperAddress) too.
    /// </summary>
    private List<PathfinderTrustRow> BuildTrust(
        IRegistrationSet registrations,
        HashSet<string> routerGroups,
        Dictionary<string, List<string>> avatarToWrappers)
    {
        var trust = new List<PathfinderTrustRow>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var kvp in _caches.V2TrustRelations.ReadOnlyDictionary)
        {
            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var truster = kvp.Key[..separatorIndex];
            var trustee = kvp.Key[(separatorIndex + 1)..];
            var expiryTime = kvp.Value;

            // Shared invariant: both registered, not expired, non-group truster
            if (!CirclesInvariants.IsValidTrustEdge(truster, trustee, expiryTime, now, registrations))
                continue;

            // Native trust edge
            trust.Add(new PathfinderTrustRow(
                Truster: truster,
                Trustee: trustee,
                Limit: 100
            ));

            // Derive wrapper trust edges: O(1) lookup via pre-built reverse index
            if (avatarToWrappers.TryGetValue(trustee, out var wrapperAddresses))
            {
                foreach (var wrapperAddr in wrapperAddresses)
                {
                    trust.Add(new PathfinderTrustRow(
                        Truster: truster,
                        Trustee: wrapperAddr,
                        Limit: 100
                    ));
                }
            }
        }

        return trust;
    }

    /// <summary>
    /// Builds group rows — only groups using the standard treasury (router).
    /// </summary>
    private static List<PathfinderGroupRow> BuildGroups(HashSet<string> routerGroups)
    {
        var groups = new List<PathfinderGroupRow>(routerGroups.Count);
        foreach (var groupAddr in routerGroups)
        {
            groups.Add(new PathfinderGroupRow(GroupAddress: groupAddr));
        }
        return groups;
    }

    private List<PathfinderGroupRouterRow> BuildGroupRouters(
        HashSet<string> routerGroups,
        IReadOnlyDictionary<string, string> indexedScoreGroupRouters)
    {
        var groupRouters = new List<PathfinderGroupRouterRow>(routerGroups.Count);
        foreach (var groupAddr in routerGroups)
        {
            if (!_caches.Groups.ReadOnlyDictionary.TryGetValue(groupAddr, out var group))
                continue;

            var isScoreGroup = ScoreGroupMintPolicies.Contains(group.Mint.ToLowerInvariant());
            string routerAddress;
            if (isScoreGroup)
            {
                if (!indexedScoreGroupRouters.TryGetValue(groupAddr, out routerAddress!))
                    continue;
            }
            else
            {
                routerAddress = BaseGroupRouter;
            }

            groupRouters.Add(new PathfinderGroupRouterRow(
                GroupAddress: groupAddr,
                RouterAddress: routerAddress
            ));
        }

        return groupRouters;
    }

    private List<PathfinderScoreGroupMintLimitRow> BuildScoreGroupMintLimits(
        IRegistrationSet registrations,
        HashSet<string> routerGroups,
        IReadOnlyDictionary<string, string> indexedScoreGroupRouters,
        long lastBlock)
    {
        if (ScoreGroupMintPolicies.Count == 0)
            return [];
        if (_dataSource == null)
            return [];

        var scoreRouterGroups = new HashSet<string>(
            routerGroups.Where(groupAddr =>
                indexedScoreGroupRouters.ContainsKey(groupAddr) &&
                _caches.Groups.ReadOnlyDictionary.TryGetValue(groupAddr, out var group) &&
                ScoreGroupMintPolicies.Contains(group.Mint.ToLowerInvariant())));
        if (scoreRouterGroups.Count == 0)
            return [];

        IReadOnlyList<ScoreGroupMintLimitRow> rows;
        try
        {
            using var connection = _dataSource.OpenConnection();
            var groupMetadata = LoadScoreGroupMetadata(connection, scoreRouterGroups, lastBlock);
            var baseRows = BuildScoreGroupMintLimitBaseRows(registrations, scoreRouterGroups, groupMetadata);
            rows = ScoreGroupMintLimitReader.ReadFromBaseRows(
                connection,
                ScoreGroupMintPolicies.ToArray(),
                baseRows,
                targetTimestamp: null,
                safetyMargin: DemurrageSafetyMargin,
                commandTimeoutSeconds: 60,
                maxBlock: lastBlock);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(
                ex,
                "Score-group mint limit tables are not available yet; omitting score-group mint limits from pathfinder graph snapshot");
            return [];
        }

        return rows
            .Select(row => new PathfinderScoreGroupMintLimitRow(
                row.GroupAddress,
                row.CollateralToken,
                row.AvailableLimit))
            .ToList();
    }

    /// <summary>
    /// All distinct score-router addresses ever emitted by CrcV2_ScoreGroup_GroupInitialized
    /// across the configured score-group mint policies, bounded by lastBlock for snapshot reproducibility.
    /// Mirrors the DB-source scoreRoutersQuery.sql so cache-source and DB-source feed the same
    /// router universe into the C3 fail-closed gate, including retired routers from re-initialisations.
    /// </summary>
    private List<string> LoadAllScoreRouters(long lastBlock)
    {
        if (_dataSource == null || ScoreGroupMintPolicies.Count == 0)
            return [];

        const string sql = """
            SELECT DISTINCT LOWER("pathMintRouter") AS router
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE "blockNumber" <= @lastBlock
              AND LOWER("emitter") = ANY(@scoreMintPolicies);
            """;

        try
        {
            using var connection = _dataSource.OpenConnection();
            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            command.Parameters.AddWithValue("scoreMintPolicies", ScoreGroupMintPolicies.ToArray());
            command.CommandTimeout = 60;

            var result = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var router = reader.GetString(0);
                if (!string.IsNullOrEmpty(router))
                    result.Add(router);
            }
            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(
                ex,
                "Score-group router table is not available yet; score routers omitted from the pathfinder graph snapshot");
            return [];
        }
    }

    /// <summary>
    /// Operator-approval pairs filtered to score-router accounts only. Matches the DB-source
    /// CrcV2_ApprovalForAll DISTINCT-ON-(account, operator) projection used by LoadGraph.LoadOperatorApprovals,
    /// bounded by lastBlock to keep snapshots reproducible.
    /// </summary>
    private List<PathfinderOperatorApprovalRow> BuildOperatorApprovals(
        IReadOnlyList<string> scoreRouters,
        long lastBlock)
    {
        if (_dataSource == null || scoreRouters.Count == 0)
            return [];

        const string sql = """
            SELECT account, operator FROM (
                SELECT DISTINCT ON (LOWER("account"), LOWER("operator"))
                    LOWER("account") AS account,
                    LOWER("operator") AS operator,
                    "approved" AS approved
                FROM "CrcV2_ApprovalForAll"
                WHERE "blockNumber" <= @lastBlock
                  AND LOWER("account") = ANY(@accounts)
                ORDER BY LOWER("account"), LOWER("operator"),
                         "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
            ) latest
            WHERE approved = true;
            """;

        try
        {
            using var connection = _dataSource.OpenConnection();
            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            command.Parameters.AddWithValue("accounts", scoreRouters.ToArray());
            command.CommandTimeout = 60;

            var result = new List<PathfinderOperatorApprovalRow>(64);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new PathfinderOperatorApprovalRow(
                    Account: reader.GetString(0),
                    Operator: reader.GetString(1)));
            }

            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(
                ex,
                "CrcV2_ApprovalForAll table is not available yet; operator approvals omitted from the pathfinder graph snapshot");
            return [];
        }
    }

    private Dictionary<string, string> LoadIndexedScoreGroupRouters(long lastBlock)
    {
        if (_dataSource == null || ScoreGroupMintPolicies.Count == 0)
            return [];

        const string sql = """
            SELECT DISTINCT ON ("group")
                "group",
                LOWER("pathMintRouter")
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE "blockNumber" <= @lastBlock
              AND LOWER("emitter") = ANY(@scoreMintPolicies)
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;
            """;

        try
        {
            using var connection = _dataSource.OpenConnection();
            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("lastBlock", lastBlock);
            command.Parameters.AddWithValue("scoreMintPolicies", ScoreGroupMintPolicies.ToArray());
            command.CommandTimeout = 60;

            var result = new Dictionary<string, string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0).ToLowerInvariant()] = reader.GetString(1).ToLowerInvariant();
            }

            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(
                ex,
                "Score-group router table is not available yet; score groups will be omitted from the pathfinder graph snapshot");
            return [];
        }
    }

    private static Dictionary<string, (string Treasury, string Policy)> LoadScoreGroupMetadata(
        NpgsqlConnection connection,
        HashSet<string> routerGroups,
        long lastBlock)
    {
        var scoreGroups = routerGroups.ToArray();
        if (scoreGroups.Length == 0)
            return [];

        const string sql = """
            WITH latest_score_group AS (
                SELECT DISTINCT ON ("group")
                    "group" AS group_address,
                    LOWER("emitter") AS policy
                FROM "CrcV2_ScoreGroup_GroupInitialized"
                WHERE "blockNumber" <= @lastBlock
                  AND LOWER("emitter") = ANY(@scoreMintPolicies)
                ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
            ),
            latest_register_group AS (
                SELECT DISTINCT ON ("group")
                    "group" AS group_address,
                    LOWER("treasury") AS treasury,
                    LOWER("mint") AS mint
                FROM "CrcV2_RegisterGroup"
                WHERE "blockNumber" <= @lastBlock
                  AND "group" = ANY(@groups)
                ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
            )
            SELECT
                rg.group_address,
                rg.treasury,
                COALESCE(lsg.policy, rg.mint) AS policy
            FROM latest_register_group rg
            LEFT JOIN latest_score_group lsg ON lsg.group_address = rg.group_address;
            """;

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("lastBlock", lastBlock);
        command.Parameters.AddWithValue("groups", scoreGroups);
        command.Parameters.AddWithValue("scoreMintPolicies", ScoreGroupMintPolicies.ToArray());
        command.CommandTimeout = 60;

        var result = new Dictionary<string, (string Treasury, string Policy)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0).ToLowerInvariant()] = (
                reader.GetString(1).ToLowerInvariant(),
                reader.GetString(2).ToLowerInvariant());
        }

        return result;
    }

    private List<ScoreGroupMintLimitBaseRow> BuildScoreGroupMintLimitBaseRows(
        IRegistrationSet registrations,
        HashSet<string> routerGroups,
        Dictionary<string, (string Treasury, string Policy)> groupMetadata)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var groupTokens = new Dictionary<string, HashSet<string>>();
        var trustedTokens = new HashSet<string>();

        foreach (var kvp in _caches.V2TrustRelations.ReadOnlyDictionary)
        {
            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var truster = kvp.Key[..separatorIndex];
            var trustee = kvp.Key[(separatorIndex + 1)..];
            if (!groupMetadata.ContainsKey(truster))
                continue;

            if (!CirclesInvariants.IsValidGroupTrustEdge(truster, trustee, kvp.Value, now, registrations, routerGroups))
                continue;

            if (!groupTokens.TryGetValue(truster, out var tokens))
            {
                tokens = new HashSet<string>();
                groupTokens[truster] = tokens;
            }

            tokens.Add(trustee);
            trustedTokens.Add(trustee);
        }

        var tokenSupply = new Dictionary<string, BigInteger>();
        var accountTokenBalances = new Dictionary<(string Account, string Token), BigInteger>();

        foreach (var kvp in _caches.V2BalancesByAccountAndToken.ReadOnlyDictionary)
        {
            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var account = kvp.Key[..separatorIndex];
            var token = kvp.Key[(separatorIndex + 1)..];
            if (!trustedTokens.Contains(token))
                continue;

            var demurragedBalance = DemurrageCachedV2Balance(kvp.Key, kvp.Value);
            if (demurragedBalance <= BigInteger.Zero)
                continue;

            tokenSupply[token] = tokenSupply.GetValueOrDefault(token) + demurragedBalance;
            accountTokenBalances[(account, token)] = demurragedBalance;
        }

        var rows = new List<ScoreGroupMintLimitBaseRow>();
        foreach (var (group, tokens) in groupTokens)
        {
            if (!groupMetadata.TryGetValue(group, out var metadata))
                continue;

            // When the on-chain treasury is a ScoreTreasury aggregator that doesn't
            // custody tokens (forwards them to score-keyed sub-treasuries), the
            // override list provides the actual holders. Sum balances across them;
            // legacy single-treasury groups fall through to the original lookup.
            var effectiveTreasuries =
                ScoreTreasurySubTreasuries.TryGetValue(metadata.Treasury, out var subs) && subs.Length > 0
                    ? subs
                    : [metadata.Treasury];

            foreach (var token in tokens)
            {
                BigInteger treasuryBalance = BigInteger.Zero;
                foreach (var treasury in effectiveTreasuries)
                {
                    if (accountTokenBalances.TryGetValue((treasury, token), out var bal))
                        treasuryBalance += bal;
                }
                tokenSupply.TryGetValue(token, out var currentSupply);

                rows.Add(new ScoreGroupMintLimitBaseRow(
                    group,
                    token,
                    metadata.Policy,
                    treasuryBalance,
                    currentSupply));
            }
        }

        return rows;
    }

    private BigInteger DemurrageCachedV2Balance(string balanceKey, decimal balance)
    {
        if (balance <= 0)
            return BigInteger.Zero;

        var attoBalance = CirclesConverter.CirclesToAttoCircles(balance);

        if (!_caches.V2LastActivity.TryGetValue(balanceKey, out var lastActivity) ||
            lastActivity < V2InflationDayZero)
        {
            // Cache anomaly: a positive balance is recorded but the
            // last-activity cache has no entry. Returning zero here silently
            // erases the balance from any downstream sum — including the
            // score-group treasury sum below, where missing balance means
            // the mint-cap formula over-approves. Fall back to the
            // un-demurraged inflationary balance (an upper bound, since
            // demurrage only ever decreases the value); biases treasury
            // sums HIGH, biases the mint cap LOW, which is the safer side.
            _logger.LogWarning(
                "DemurrageCachedV2Balance: V2LastActivity missing for key {Key}; using un-demurraged inflationary balance as conservative upper bound",
                balanceKey);
            return attoBalance;
        }

        var targetDay = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, V2InflationDayZero);
        var lastActivityDay = (ulong)(lastActivity - V2InflationDayZero) / (ulong)SecondsPerDay;
        var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;
        return daysDelta == 0
            ? attoBalance
            : CirclesConverter.InflationaryToDemurrage(attoBalance, daysDelta);
    }

    /// <summary>
    /// Builds group trust rows — trust edges where truster is a router-filtered group.
    /// </summary>
    private List<PathfinderGroupTrustRow> BuildGroupTrusts(IRegistrationSet registrations, HashSet<string> routerGroups)
    {
        var groupTrusts = new List<PathfinderGroupTrustRow>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var kvp in _caches.V2TrustRelations.ReadOnlyDictionary)
        {
            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var truster = kvp.Key[..separatorIndex];
            var trustee = kvp.Key[(separatorIndex + 1)..];
            var expiryTime = kvp.Value;

            // Shared invariant: router group truster, registered trustee, not expired
            if (!CirclesInvariants.IsValidGroupTrustEdge(truster, trustee, expiryTime, now, registrations, routerGroups))
                continue;

            groupTrusts.Add(new PathfinderGroupTrustRow(
                GroupAddress: truster,
                TrustedToken: trustee
            ));
        }

        return groupTrusts;
    }

    /// <summary>
    /// Returns all registered V2 avatar addresses from the cache.
    /// Hub.sol considers humans, organizations, AND groups as registered avatars
    /// (avatars[addr] != address(0) for all three types). The pathfinder uses this
    /// list to populate RegisteredAvatarIds for graph construction filtering.
    /// </summary>
    private List<string> BuildAvatars()
    {
        var avatars = new List<string>(_caches.V2Avatars.Count + _caches.Groups.Count);
        foreach (var address in _caches.V2Avatars.ReadOnlyDictionary.Keys)
        {
            avatars.Add(address);
        }
        // Groups are registered avatars in Hub.sol — must be included
        foreach (var address in _caches.Groups.ReadOnlyDictionary.Keys)
        {
            avatars.Add(address);
        }
        return avatars;
    }

    /// <summary>
    /// Returns all V2 organization addresses from the cache.
    /// Organizations are stored in V2Avatars with type "CrcV2_RegisterOrganization".
    /// The pathfinder uses this to populate CapacityGraph.OrganizationNodes for
    /// canary simulation gating (orgs can't be simulated — Hub.sol requires operator approval).
    /// </summary>
    private List<string> BuildOrganizations()
    {
        var organizations = new List<string>();
        foreach (var kvp in _caches.V2Avatars.ReadOnlyDictionary)
        {
            if (kvp.Value.Type == "CrcV2_RegisterOrganization")
                organizations.Add(kvp.Key);
        }
        return organizations;
    }

    /// <summary>
    /// Builds wrapper→avatar mapping rows from the Erc20WrapperAddresses cache.
    /// Only includes wrappers whose underlying avatar is registered.
    /// </summary>
    private List<PathfinderWrapperMappingRow> BuildWrapperMappings(IRegistrationSet registrations)
    {
        var mappings = new List<PathfinderWrapperMappingRow>();
        foreach (var kvp in _caches.Erc20WrapperAddresses.ReadOnlyDictionary)
        {
            // Shared invariant: underlying avatar must be registered
            if (!CirclesInvariants.IsValidWrapperMapping(kvp.Value.Avatar, registrations))
                continue;

            mappings.Add(new PathfinderWrapperMappingRow(
                WrapperAddress: kvp.Key,
                UnderlyingAvatar: kvp.Value.Avatar,
                CirclesType: kvp.Value.CirclesType
            ));
        }
        return mappings;
    }

    /// <summary>
    /// Builds consented flow rows from the ConsentedFlowFlags cache.
    /// Extracts bit 0 of byte[31] to determine consent status.
    /// </summary>
    private List<PathfinderConsentedFlowRow> BuildConsentedFlow(IRegistrationSet registrations)
    {
        var consent = new List<PathfinderConsentedFlowRow>();

        foreach (var kvp in _caches.ConsentedFlowFlags.ReadOnlyDictionary)
        {
            // Shared invariant: avatar must be registered
            if (!CirclesInvariants.IsValidConsentedFlowFlag(kvp.Key, registrations))
                continue;

            var flagBytes = kvp.Value;

            if (flagBytes.Length < 32)
            {
                _logger.LogWarning("ConsentedFlowFlags for avatar {Avatar} has unexpected length {Length}", kvp.Key, flagBytes.Length);
                continue;
            }

            // bytes32 flag — bit 0 of the last byte (index 31) indicates consented flow
            var hasConsent = (flagBytes[31] & 0x01) != 0;

            consent.Add(new PathfinderConsentedFlowRow(
                Avatar: kvp.Key,
                HasConsentedFlow: hasConsent
            ));
        }

        return consent;
    }

    /// <summary>
    /// Builds a reverse index: avatar address → list of wrapper addresses.
    /// Used for O(1) wrapper trust edge derivation instead of O(N) scan.
    /// </summary>
    private Dictionary<string, List<string>> BuildAvatarToWrappersIndex()
    {
        var index = new Dictionary<string, List<string>>();
        foreach (var kvp in _caches.Erc20WrapperAddresses.ReadOnlyDictionary)
        {
            var avatar = kvp.Value.Avatar;
            if (!index.TryGetValue(avatar, out var wrappers))
            {
                wrappers = new List<string>(1);
                index[avatar] = wrappers;
            }
            wrappers.Add(kvp.Key); // wrapper address
        }
        return index;
    }

    /// <summary>
    /// Returns the set of group addresses supported by the pathfinder.
    /// </summary>
    private HashSet<string> GetRouterFilteredGroups(IReadOnlyDictionary<string, string> indexedScoreGroupRouters)
    {
        var result = new HashSet<string>();
        foreach (var kvp in _caches.Groups.ReadOnlyDictionary)
        {
            var mint = kvp.Value.Mint.ToLowerInvariant();
            if (mint == StandardTreasuryMint ||
                (ScoreGroupMintPolicies.Contains(mint) && indexedScoreGroupRouters.ContainsKey(kvp.Key)))
            {
                result.Add(kvp.Key);
            }
        }
        return result;
    }
}
