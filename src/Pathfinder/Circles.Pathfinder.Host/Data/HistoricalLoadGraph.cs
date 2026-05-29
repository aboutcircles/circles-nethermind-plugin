using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Circles.Common;
using Circles.Pathfinder.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Pathfinder.Host.Data;

/// <summary>
/// ILoadGraph implementation that loads graph data at a specific historical block number
/// by querying PostgreSQL directly with block-ceiling filters.
///
/// Promoted from the test-only ProxyLoadGraph — same SQL templates but reads directly
/// from NpgsqlDataSource instead of going through the test-env HTTP proxy.
///
/// Key differences from ProxyLoadGraph:
/// - Uses block-filtered registered_avatars CTE (not V_CrcV2_Avatars view) for correct historical filtering
/// - Includes tokenAddress registration filter (production fix 2026-02-14)
/// - Reads NpgsqlDataReader directly instead of JSON deserialization
/// </summary>
public sealed class HistoricalLoadGraph : ILoadGraph
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly long _blockNumber;
    private readonly Settings _settings;
    private readonly ILogger _logger;

    // V2 Hub epoch on gnosis mainnet: Hub(0x5524...).inflationDayZero() == 1_602_720_000
    private const uint InflationDayZeroUnix = 1_602_720_000;
    private const ulong SecondsPerDay = 86_400;

    // Block-filtered registered avatars CTE — used in balance and trust queries
    // to ensure only avatars registered at or before the target block are included.
    private const string RegisteredAvatarsCte = """
        registered_avatars AS (
            SELECT avatar FROM "CrcV2_RegisterHuman" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "group" AS avatar FROM "CrcV2_RegisterGroup" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT organization AS avatar FROM "CrcV2_RegisterOrganization" WHERE "blockNumber" <= {0}
        )
        """;

    // Balance query with block-filtered registered_avatars CTE.
    // Filters BOTH account AND tokenAddress against registered avatars
    // (production fix 2026-02-14: prevents AvatarMustBeRegistered reverts).
    private const string BalanceQueryTemplate = """
        WITH {2},
        tx AS (
            SELECT "timestamp", "from" AS account, "tokenAddress", -value AS delta
            FROM "CrcV2_TransferSingle" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", value AS delta
            FROM "CrcV2_TransferSingle" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "timestamp", "from" AS account, "tokenAddress", -value AS delta
            FROM "CrcV2_TransferBatch" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", value AS delta
            FROM "CrcV2_TransferBatch" WHERE "blockNumber" <= {0}
        ), agg AS (
            SELECT account, "tokenAddress", sum(delta) AS balance, max("timestamp") AS last_ts
            FROM tx
            GROUP BY account, "tokenAddress"
        )
        SELECT balance::text, account, "tokenAddress", last_ts AS "lastActivity",
               false AS "isWrapped", 'demurraged' AS "circlesType"
        FROM agg
        INNER JOIN registered_avatars ra ON ra.avatar = agg.account
        INNER JOIN registered_avatars ra_token ON ra_token.avatar = agg."tokenAddress"
        WHERE account <> '0x0000000000000000000000000000000000000000' AND balance > 0
        ORDER BY balance, account, "tokenAddress"
        """;

    // Trust query with block-filtered registered_avatars CTE.
    private const string TrustQueryTemplate = """
        WITH {2},
        trust_state AS (
            SELECT truster, trustee, "expiryTime",
                   row_number() OVER (PARTITION BY truster, trustee
                       ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC) AS rn
            FROM "CrcV2_Trust"
            WHERE "blockNumber" <= {0}
        ), active_trust AS (
            SELECT truster, trustee FROM trust_state
            WHERE rn = 1 AND "expiryTime" > (SELECT max("timestamp") FROM "System_Block" WHERE "blockNumber" <= {0})::numeric
        )
        SELECT t1.truster, t1.trustee FROM active_trust t1
        INNER JOIN registered_avatars a1 ON a1.avatar = t1.truster
        INNER JOIN registered_avatars a2 ON a2.avatar = t1.trustee
        LEFT JOIN "CrcV2_RegisterGroup" t2
            ON t2."group" = t1.truster
           AND t2."blockNumber" <= {0}
        WHERE t2."group" IS NULL
        """;

    // Block-aware group enumeration that mirrors live groupQuery.sql:
    // a group is "supported" if it's either a BaseGroup (mint=@mintPolicy) OR
    // a ScoreGroup (mint in @scoreMintPolicies AND has a GroupInitialized event).
    // The ScoreGroup branch is no-op when @scoreMintPolicies is empty (ANY('{}') is false),
    // preserving BaseGroup-only behaviour for callers without ScoreGroup configuration.
    private const string GroupQueryTemplate = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
              AND "blockNumber" <= {0}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        supported_groups AS (
            SELECT g."group" AS group_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {0}
              AND (
                g."mint" = LOWER(@mintPolicy)
                OR (LOWER(g."mint") = ANY(@scoreMintPolicies) AND sg.router_address IS NOT NULL)
              )
        )
        SELECT group_address FROM supported_groups
        """;

    // Block-aware group→trustee trust query, mirrors live groupTrustQuery.sql.
    // Filters trustees through a block-filtered registered_avatars CTE (matches the
    // production fix from 2026-02-14 that prevents AvatarMustBeRegistered reverts).
    // Includes both BaseGroup and ScoreGroup-managed groups via the same
    // supported_groups CTE as GroupQueryTemplate above.
    private const string GroupTrustQueryTemplate = """
        WITH trust_state AS (
            SELECT truster, trustee, "expiryTime",
                   row_number() OVER (PARTITION BY truster, trustee
                       ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC) AS rn
            FROM "CrcV2_Trust"
            WHERE "blockNumber" <= {0}
        ), active_trust AS (
            SELECT truster, trustee FROM trust_state
            WHERE rn = 1 AND "expiryTime" > (SELECT max("timestamp") FROM "System_Block" WHERE "blockNumber" <= {0})::numeric
        ), latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
              AND "blockNumber" <= {0}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ), supported_groups AS (
            SELECT g."group" AS group_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {0}
              AND (
                g."mint" = LOWER(@mintPolicy)
                OR (LOWER(g."mint") = ANY(@scoreMintPolicies) AND sg.router_address IS NOT NULL)
              )
        ), registered_avatars AS (
            SELECT avatar FROM "CrcV2_RegisterHuman" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "group" AS avatar FROM "CrcV2_RegisterGroup" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT organization AS avatar FROM "CrcV2_RegisterOrganization" WHERE "blockNumber" <= {0}
        )
        SELECT t.truster AS group_address, t.trustee AS trusted_token
        FROM active_trust t
        INNER JOIN supported_groups g ON g.group_address = t.truster
        INNER JOIN registered_avatars ra ON ra.avatar = t.trustee
        """;

    private const string RegisteredAvatarsQueryTemplate = """
        SELECT avatar FROM "CrcV2_RegisterHuman" WHERE "blockNumber" <= {0}
        UNION ALL
        SELECT "group" AS avatar FROM "CrcV2_RegisterGroup" WHERE "blockNumber" <= {0}
        UNION ALL
        SELECT organization AS avatar FROM "CrcV2_RegisterOrganization" WHERE "blockNumber" <= {0}
        """;

    private const string ConsentedFlowQueryTemplate = """
        SELECT DISTINCT ON (avatar) avatar, flag
        FROM "CrcV2_SetAdvancedUsageFlag"
        WHERE "blockNumber" <= {0}
        ORDER BY avatar, "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        """;

    private const string BlockTimestampQuery = """
        SELECT "timestamp" FROM "System_Block" WHERE "blockNumber" <= {0}
        ORDER BY "blockNumber" DESC LIMIT 1
        """;

    // Block-aware group→router resolution. Mirrors live groupRouterQuery.sql:
    // ScoreGroups get their per-group pathMintRouter from the GroupInitialized event;
    // BaseGroups (and ScoreGroups without an init event) fall back to the standard router.
    private const string GroupRouterQueryTemplate = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
              AND "blockNumber" <= {0}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        supported_groups AS (
            SELECT
                g."group" AS group_address,
                g."mint",
                sg.router_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {0}
              AND (g."mint" = LOWER(@mintPolicy)
                   OR (LOWER(g."mint") = ANY(@scoreMintPolicies) AND sg.router_address IS NOT NULL))
        )
        SELECT
            group_address,
            CASE
                WHEN router_address IS NOT NULL THEN router_address
                ELSE LOWER(@standardRouter)
            END AS router_address
        FROM supported_groups
        """;

    // Distinct ScoreGroup pathMintRouter addresses, used by GraphFactory to recognise
    // score-router membership for the OperatorApprovals gating logic.
    private const string ScoreRoutersQueryTemplate = """
        SELECT DISTINCT LOWER("pathMintRouter") AS router_address
        FROM "CrcV2_ScoreGroup_GroupInitialized"
        WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
          AND "blockNumber" <= {0}
        """;

    // Block-aware operator approvals (latest-per-pair = true), filtered to the
    // requested account list. Mirrors the inline SQL in LoadGraph.LoadOperatorApprovals
    // with the addition of the blockNumber ceiling.
    private const string OperatorApprovalsQueryTemplate = """
        SELECT account, operator FROM (
            SELECT DISTINCT ON (LOWER("account"), LOWER("operator"))
                LOWER("account") AS account,
                LOWER("operator") AS operator,
                "approved" AS approved
            FROM "CrcV2_ApprovalForAll"
            WHERE LOWER("account") = ANY(@accounts)
              AND "blockNumber" <= {0}
            ORDER BY LOWER("account"), LOWER("operator"),
                     "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ) latest
        WHERE approved = true
        """;

    public HistoricalLoadGraph(NpgsqlDataSource dataSource, long blockNumber, Settings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (blockNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockNumber), blockNumber, "Block number must be positive");

        _dataSource = dataSource;
        _blockNumber = blockNumber;
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets the timestamp of the target block (or nearest prior block) for demurrage calculation.
    /// Returns 0 if no block exists at or before the requested block number.
    /// </summary>
    public long GetBlockTimestamp()
    {
        var sql = string.Format(BlockTimestampQuery, _blockNumber);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        var result = cmd.ExecuteScalar();
        // System_Block.timestamp is bigint; also handle int/decimal for safety
        return result switch
        {
            long l => l,
            int i => i,
            decimal d => (long)d,
            _ => 0
        };
    }

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        var registeredAvatarsCte = string.Format(RegisteredAvatarsCte, _blockNumber);
        var sql = string.Format(BalanceQueryTemplate, _blockNumber, "", registeredAvatarsCte);
        var results = new List<(string, int, int, bool, bool)>(50_000);

        // Calculate target day for demurrage
        var targetTimestamp = _settings.TargetDemurrageTimestamp ?? DateTimeOffset.UtcNow;
        var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp, InflationDayZeroUnix);

        var sw = Stopwatch.StartNew();
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var balance = reader.GetString(0);
            var account = reader.GetString(1);
            var tokenAddress = reader.GetString(2);
            var lastActivity = reader.GetInt64(3);
            var isWrapped = reader.GetBoolean(4);
            var circlesType = reader.GetString(5);

            if (circlesType == "static")
            {
                var staticAttoCircles = BigInteger.Parse(balance);
                var demurragedAttoCircles = CirclesConverter.InflationaryToDemurrage(staticAttoCircles, targetDay);
                if (demurragedAttoCircles == 0) continue;
                balance = demurragedAttoCircles.ToString(CultureInfo.InvariantCulture);
            }
            else if (circlesType == "demurraged")
            {
                var inflationaryBalance = BigInteger.Parse(balance);

                // Guard: lastActivity before V2 epoch means data inconsistency — skip
                if (lastActivity < InflationDayZeroUnix)
                {
                    _logger.LogWarning(
                        "Balance row has lastActivity {LastActivity} before V2 epoch {Epoch} — skipping (account={Account}, token={Token})",
                        lastActivity, InflationDayZeroUnix, account, tokenAddress);
                    continue;
                }

                var lastActivityDay = (ulong)(lastActivity - InflationDayZeroUnix) / SecondsPerDay;
                var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;

                if (daysDelta > 0)
                {
                    var demurragedBalance = CirclesConverter.InflationaryToDemurrage(inflationaryBalance, daysDelta);
                    balance = demurragedBalance.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (balance == "0") continue;

            results.Add((balance,
                AddressIdPool.IdOf(account.ToLowerInvariant()),
                AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                isWrapped,
                circlesType == "static"));
        }

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} balances loaded in {Elapsed}ms",
            _blockNumber, results.Count, sw.ElapsedMilliseconds);
        return results;
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        var registeredAvatarsCte = string.Format(RegisteredAvatarsCte, _blockNumber);
        var sql = string.Format(TrustQueryTemplate, _blockNumber, "", registeredAvatarsCte);
        var results = new List<(string, string, int)>(100_000);

        var sw = Stopwatch.StartNew();
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), 100));
        }

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} trust edges loaded in {Elapsed}ms",
            _blockNumber, results.Count, sw.ElapsedMilliseconds);
        return results;
    }

    public IEnumerable<string> LoadGroups()
    {
        var sql = string.Format(GroupQueryTemplate, _blockNumber);
        var results = new List<string>();

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("mintPolicy", _settings.StandardMintPolicyAddress ?? "");
        cmd.Parameters.AddWithValue("scoreMintPolicies", _settings.ScoreGroupMintPolicies);
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} groups",
            _blockNumber, results.Count);
        return results;
    }

    public IEnumerable<string> LoadOrganizations() => [];

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        var sql = string.Format(GroupTrustQueryTemplate, _blockNumber);
        var results = new List<(string, string)>();

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("mintPolicy", _settings.StandardMintPolicyAddress ?? "");
        cmd.Parameters.AddWithValue("scoreMintPolicies", _settings.ScoreGroupMintPolicies);
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
    {
        var sql = string.Format(ConsentedFlowQueryTemplate, _blockNumber);
        var results = new List<(string, bool)>();

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var avatar = reader.GetString(0);
            var flag = (byte[])reader[1];
            bool hasConsented = flag.Length >= 32 && (flag[31] & 0x01) != 0;
            results.Add((avatar, hasConsented));
        }

        return results;
    }

    public IEnumerable<string> LoadRegisteredAvatars()
    {
        var sql = string.Format(RegisteredAvatarsQueryTemplate, _blockNumber);
        var results = ExecuteStringListQuery(sql, "registered avatars");
        return results.Select(a => a.ToLowerInvariant()).ToList();
    }

    // Historical canary path: wrapper mappings are not yet block-filtered, so the canary
    // is effectively skipped for historical requests. Acceptable today because the canary's
    // job is detecting LIVE graph drift; historical traffic doesn't broadcast on-chain.
    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, CirclesType CirclesType)> LoadWrapperMappings()
        => Array.Empty<(string, string, CirclesType)>();

    public IEnumerable<(string GroupAddress, string RouterAddress)> LoadGroupRouters()
    {
        var sql = string.Format(GroupRouterQueryTemplate, _blockNumber);
        var results = new List<(string, string)>();

        var sw = Stopwatch.StartNew();
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("mintPolicy", _settings.StandardMintPolicyAddress ?? "");
        cmd.Parameters.AddWithValue("scoreMintPolicies", _settings.ScoreGroupMintPolicies);
        cmd.Parameters.AddWithValue("standardRouter", _settings.GroupRouterAddress);
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} group routers in {Elapsed}ms",
            _blockNumber, results.Count, sw.ElapsedMilliseconds);
        return results;
    }

    public IEnumerable<string> LoadScoreRouters()
    {
        // Match the live LoadGraph short-circuit: when no score-mint policies are
        // configured the table query is a no-op anyway, so skip the round-trip.
        if (_settings.ScoreGroupMintPolicies.Length == 0)
            return [];

        var sql = string.Format(ScoreRoutersQueryTemplate, _blockNumber);
        var results = new List<string>();

        var sw = Stopwatch.StartNew();
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("scoreMintPolicies", _settings.ScoreGroupMintPolicies);
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} score routers in {Elapsed}ms",
            _blockNumber, results.Count, sw.ElapsedMilliseconds);
        return results;
    }

    public IEnumerable<(string GroupAddress, string CollateralToken, string AvailableLimit)> LoadScoreGroupMintLimits()
    {
        if (_settings.ScoreGroupMintPolicies.Length == 0)
            return [];

        var sw = Stopwatch.StartNew();
        using var connection = _dataSource.OpenConnection();

        // ScoreGroupMintLimitReader already supports maxBlock end-to-end across its
        // three sub-queries (base rows, historical supply, personal minted).
        // Pin demurrage to the block timestamp so historical computations are
        // deterministic — TargetDemurrageTimestamp is set in HistoricalGraphCache.LoadGraph.
        var rows = ScoreGroupMintLimitReader.Read(
            connection,
            _settings.ScoreGroupMintPolicies,
            _settings.TargetDemurrageTimestamp,
            _settings.DemurrageSafetyMargin,
            commandTimeoutSeconds: 60,
            maxBlock: _blockNumber,
            subTreasuryOverrides: _settings.ScoreTreasurySubTreasuries);

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} score group mint limits in {Elapsed}ms",
            _blockNumber, rows.Count, sw.ElapsedMilliseconds);
        return rows.Select(row => (row.GroupAddress, row.CollateralToken, row.AvailableLimit)).ToList();
    }

    public IEnumerable<(string Account, string Operator)> LoadOperatorApprovals(IEnumerable<string> accounts)
    {
        var accountSet = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (accountSet.Length == 0)
            return [];

        var sql = string.Format(OperatorApprovalsQueryTemplate, _blockNumber);
        var results = new List<(string, string)>();

        var sw = Stopwatch.StartNew();
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("accounts", accountSet);
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} operator approvals across {Accounts} accounts in {Elapsed}ms",
            _blockNumber, results.Count, accountSet.Length, sw.ElapsedMilliseconds);
        return results;
    }

    private List<string> ExecuteStringListQuery(string sql, string label)
    {
        var results = new List<string>();

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        _logger.LogInformation(
            "[HistoricalLoadGraph] Block {Block}: {Count} {Label}",
            _blockNumber, results.Count, label);
        return results;
    }
}
