using System.Globalization;
using System.Numerics;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Common;

public sealed record ScoreGroupMintLimitRow(
    string GroupAddress,
    string CollateralToken,
    string AvailableLimit);

public sealed record ScoreGroupMintLimitBaseRow(
    string GroupAddress,
    string CollateralToken,
    string Policy,
    BigInteger TreasuryBalance,
    BigInteger CurrentSupply);

public static class ScoreGroupMintLimitReader
{
    private const uint InflationDayZeroUnix = 1_602_720_000;

    /// <summary>
    /// Base-rows SQL. Each score group's "treasury" (as recorded in
    /// <c>CrcV2_RegisterGroup.treasury</c>) is expanded into one or more
    /// effective treasury addresses via the <c>treasury_overrides</c> CTE.
    /// When the on-chain treasury is a <c>ScoreTreasury</c> router/splitter
    /// (does not custody collateral itself but forwards to score-keyed
    /// sub-treasuries), the override list contains the sub-treasuries that
    /// actually hold tokens. Groups not in the override map fall back to
    /// single-treasury behavior — the override list defaults to
    /// <c>ARRAY[treasury]</c>.
    ///
    /// Balance computation: instead of querying the <c>V_CrcV2_BalancesByAccountAndToken</c>
    /// view (which FULL JOINs all 140K rows in the materialized table before filtering),
    /// this query inlines the view's logic with the tokenAddress filter applied before the
    /// FULL JOIN. The materialized table has an index on tokenAddress so only the handful of
    /// relevant score-group token rows are scanned.
    /// </summary>
    private const string BaseRowsSql = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                LOWER("emitter") AS policy
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
              AND LOWER("emitter") = ANY(@scoreMintPolicies)
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        score_groups AS (
            SELECT
                LOWER(g."group") AS group_address,
                LOWER(g."treasury") AS treasury,
                LOWER(COALESCE(lsg.policy, g."mint")) AS policy
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group lsg ON LOWER(lsg.group_address) = LOWER(g."group")
            WHERE (@maxBlock::bigint IS NULL OR g."blockNumber" <= @maxBlock)
              AND LOWER(g."mint") = ANY(@scoreMintPolicies)
              AND lsg.group_address IS NOT NULL
              AND (@groupAddressFilter::text IS NULL OR LOWER(g."group") = @groupAddressFilter)
        ),
        treasury_overrides AS (
            SELECT
                LOWER(agg) AS aggregator,
                string_to_array(LOWER(subs_csv), ',') AS subs
            FROM unnest(
                @subTreasuryAggregators::text[],
                @subTreasuryLists::text[]
            ) AS u(agg, subs_csv)
        ),
        effective_treasuries AS (
            SELECT
                sg.group_address,
                sg.policy,
                COALESCE(o.subs, ARRAY[sg.treasury]::text[]) AS treasuries
            FROM score_groups sg
            LEFT JOIN treasury_overrides o ON o.aggregator = sg.treasury
        ),
        group_tokens AS (
            SELECT
                et.group_address,
                et.treasuries,
                et.policy,
                t.trustee AS trusted_token
            FROM effective_treasuries et
            INNER JOIN "V_CrcV2_TrustRelations" t ON t.truster = et.group_address
            INNER JOIN "V_CrcV2_Avatars" a ON a.avatar = t.trustee
            WHERE (@collateralTokenFilter::text IS NULL OR t.trustee = @collateralTokenFilter)
        ),
        -- Inline the V_CrcV2_BalancesByAccountAndToken view logic with the tokenAddress
        -- filter applied before the FULL JOIN so the planner uses the tokenAddress index
        -- on M_CrcV2_BalancesByAccountAndToken instead of scanning all 140K rows.
        watermark AS (
            SELECT COALESCE(MAX("_maxBlock"), 0) AS wm FROM "M_CrcV2_BalancesByAccountAndToken"
        ),
        delta_tx AS (
            SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
            FROM "CrcV2_TransferSingle"
            WHERE "blockNumber" > (SELECT wm FROM watermark)
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
            FROM "CrcV2_TransferSingle"
            WHERE "blockNumber" > (SELECT wm FROM watermark)
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
            UNION ALL
            SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
            FROM "CrcV2_TransferBatch"
            WHERE "blockNumber" > (SELECT wm FROM watermark)
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
            FROM "CrcV2_TransferBatch"
            WHERE "blockNumber" > (SELECT wm FROM watermark)
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
        ),
        delta_agg AS (
            SELECT account, id::text AS "tokenId", "tokenAddress",
                   MAX("timestamp") AS "lastActivity", SUM(delta) AS "totalBalance"
            FROM delta_tx
            GROUP BY account, id, "tokenAddress"
        ),
        filtered_mat AS (
            SELECT account, "tokenId", "tokenAddress", "lastActivity", "totalBalance"
            FROM "M_CrcV2_BalancesByAccountAndToken"
            WHERE "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
              AND account <> '0x0000000000000000000000000000000000000000'
        ),
        merged AS (
            SELECT
                COALESCE(m.account, d.account) AS account,
                COALESCE(m."tokenId", d."tokenId") AS "tokenId",
                COALESCE(m."tokenAddress", d."tokenAddress") AS "tokenAddress",
                GREATEST(COALESCE(m."lastActivity", 0), COALESCE(d."lastActivity", 0)) AS "lastActivity",
                COALESCE(m."totalBalance", 0) + COALESCE(d."totalBalance", 0) AS "totalBalance"
            FROM filtered_mat m
            FULL JOIN delta_agg d
                ON m.account = d.account
               AND m."tokenId" = d."tokenId"
               AND m."tokenAddress" = d."tokenAddress"
        ),
        balances AS (
            SELECT account, "tokenId", "tokenAddress",
                   floor("totalBalance" * power(
                       0.9998013320085989574306481700129226782902039065082930593676448873,
                       (extract(epoch from now())::bigint - 1602720000) / 86400
                       - ("lastActivity" - 1602720000) / 86400
                   )) AS "demurragedTotalBalance"
            FROM merged
            WHERE account <> '0x0000000000000000000000000000000000000000'
              AND "totalBalance" > 0
        ),
        token_supply AS (
            SELECT
                b."tokenAddress",
                SUM(b."demurragedTotalBalance")::text AS current_supply
            FROM balances b
            INNER JOIN (SELECT DISTINCT trusted_token FROM group_tokens) gt
                ON gt.trusted_token = b."tokenAddress"
            GROUP BY b."tokenAddress"
        )
        SELECT
            gt.group_address,
            gt.trusted_token,
            gt.policy,
            COALESCE((
                SELECT SUM(b."demurragedTotalBalance")
                FROM balances b
                WHERE b.account = ANY(gt.treasuries)
                  AND b."tokenAddress" = gt.trusted_token
            ), 0)::text AS treasury_balance,
            COALESCE(ts.current_supply, '0') AS current_supply
        FROM group_tokens gt
        LEFT JOIN token_supply ts
            ON ts."tokenAddress" = gt.trusted_token;
        """;

    /// <summary>
    /// Historical variant of <see cref="BaseRowsSql"/> used when <c>maxBlock</c> is non-null.
    /// Bypasses <c>M_CrcV2_BalancesByAccountAndToken</c> (the matview, which only holds HEAD
    /// state and has no block-pinned twin in the test environment) and instead aggregates
    /// directly from raw <c>CrcV2_TransferSingle/Batch</c> tables with
    /// <c>WHERE "blockNumber" &lt;= @maxBlock</c>. Demurrage is anchored to the block's own
    /// timestamp from <c>System_Block</c>, mirroring the <c>circles_at_block</c> schema's
    /// <c>pin_timestamp()</c> function. This makes parity tests and any future historical
    /// point-in-time queries produce correct results.
    /// </summary>
    internal const string BaseRowsSqlHistorical = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                LOWER("emitter") AS policy
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
              AND LOWER("emitter") = ANY(@scoreMintPolicies)
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        score_groups AS (
            SELECT
                LOWER(g."group") AS group_address,
                LOWER(g."treasury") AS treasury,
                LOWER(COALESCE(lsg.policy, g."mint")) AS policy
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group lsg ON LOWER(lsg.group_address) = LOWER(g."group")
            WHERE (@maxBlock::bigint IS NULL OR g."blockNumber" <= @maxBlock)
              AND LOWER(g."mint") = ANY(@scoreMintPolicies)
              AND lsg.group_address IS NOT NULL
              AND (@groupAddressFilter::text IS NULL OR LOWER(g."group") = @groupAddressFilter)
        ),
        treasury_overrides AS (
            SELECT
                LOWER(agg) AS aggregator,
                string_to_array(LOWER(subs_csv), ',') AS subs
            FROM unnest(
                @subTreasuryAggregators::text[],
                @subTreasuryLists::text[]
            ) AS u(agg, subs_csv)
        ),
        effective_treasuries AS (
            SELECT
                sg.group_address,
                sg.policy,
                COALESCE(o.subs, ARRAY[sg.treasury]::text[]) AS treasuries
            FROM score_groups sg
            LEFT JOIN treasury_overrides o ON o.aggregator = sg.treasury
        ),
        group_tokens AS (
            SELECT
                et.group_address,
                et.treasuries,
                et.policy,
                t.trustee AS trusted_token
            FROM effective_treasuries et
            INNER JOIN "V_CrcV2_TrustRelations" t ON t.truster = et.group_address
            INNER JOIN "V_CrcV2_Avatars" a ON a.avatar = t.trustee
            WHERE (@collateralTokenFilter::text IS NULL OR t.trustee = @collateralTokenFilter)
        ),
        block_ts AS (
            SELECT MAX("timestamp") AS ts
            FROM "System_Block"
            WHERE "blockNumber" <= @maxBlock
        ),
        raw_tx AS (
            SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
            FROM "CrcV2_TransferSingle"
            WHERE "blockNumber" <= @maxBlock
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
            FROM "CrcV2_TransferSingle"
            WHERE "blockNumber" <= @maxBlock
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
            UNION ALL
            SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
            FROM "CrcV2_TransferBatch"
            WHERE "blockNumber" <= @maxBlock
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
            FROM "CrcV2_TransferBatch"
            WHERE "blockNumber" <= @maxBlock
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM group_tokens))
        ),
        raw_agg AS (
            SELECT account, id::text AS "tokenId", "tokenAddress",
                   MAX("timestamp") AS "lastActivity", SUM(delta) AS "totalBalance"
            FROM raw_tx
            GROUP BY account, id, "tokenAddress"
        ),
        balances AS (
            SELECT account, "tokenId", "tokenAddress",
                   floor("totalBalance" * power(
                       0.9998013320085989574306481700129226782902039065082930593676448873,
                       ((SELECT ts FROM block_ts) - 1602720000) / 86400
                       - ("lastActivity" - 1602720000) / 86400
                   )) AS "demurragedTotalBalance"
            FROM raw_agg
            WHERE account <> '0x0000000000000000000000000000000000000000'
              AND "totalBalance" > 0
        ),
        token_supply AS (
            SELECT
                b."tokenAddress",
                SUM(b."demurragedTotalBalance")::text AS current_supply
            FROM balances b
            INNER JOIN (SELECT DISTINCT trusted_token FROM group_tokens) gt
                ON gt.trusted_token = b."tokenAddress"
            GROUP BY b."tokenAddress"
        )
        SELECT
            gt.group_address,
            gt.trusted_token,
            gt.policy,
            COALESCE((
                SELECT SUM(b."demurragedTotalBalance")
                FROM balances b
                WHERE b.account = ANY(gt.treasuries)
                  AND b."tokenAddress" = gt.trusted_token
            ), 0)::text AS treasury_balance,
            COALESCE(ts.current_supply, '0') AS current_supply
        FROM group_tokens gt
        LEFT JOIN token_supply ts
            ON ts."tokenAddress" = gt.trusted_token;
        """;

    // HistoricalSupply is now per-(group, collateral) in the prod contract — the
    // event added `address indexed group`, so a single policy serving multiple
    // groups (via initializeGroup) tracks supply scoped to each group rather
    // than conflating them. DISTINCT ON / ORDER BY must include "group".
    private const string HistoricalSql = """
        SELECT DISTINCT ON (LOWER("emitter"), "group", "collateral")
            LOWER("emitter") AS policy,
            LOWER("group") AS group_address,
            "collateral"::text AS collateral,
            "supply"::text AS supply,
            "day"::text AS day
        FROM "CrcV2_ScoreGroup_HistoricalSupply"
        WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
          AND (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
        ORDER BY LOWER("emitter"), "group", "collateral", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;
        """;

    private const string PersonalSql = """
        SELECT DISTINCT ON (LOWER("emitter"), LOWER("group"), "collateral")
            LOWER("emitter") AS policy,
            LOWER("group") AS group_address,
            "collateral"::text AS collateral,
            "mintedAmountOnToday"::text AS minted_amount,
            "day"::text AS day
        FROM "CrcV2_ScoreGroup_PersonalMinted"
        WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
          AND (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
        ORDER BY LOWER("emitter"), LOWER("group"), "collateral", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;
        """;

    public static IReadOnlyList<ScoreGroupMintLimitRow> Read(
        NpgsqlConnection connection,
        string[] scoreMintPolicies,
        DateTimeOffset? targetTimestamp,
        double safetyMargin,
        int commandTimeoutSeconds,
        long? maxBlock = null,
        NpgsqlTransaction? transaction = null,
        string? groupAddressFilter = null,
        string? collateralTokenFilter = null,
        IReadOnlyDictionary<string, string[]>? subTreasuryOverrides = null)
    {
        var policies = scoreMintPolicies
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        if (policies.Length == 0)
            return [];

        var baseRows = ReadBaseRows(
            connection,
            policies,
            commandTimeoutSeconds,
            maxBlock,
            transaction,
            NormalizeFilter(groupAddressFilter),
            NormalizeFilter(collateralTokenFilter),
            subTreasuryOverrides);
        return ReadFromBaseRows(
            connection,
            policies,
            baseRows,
            targetTimestamp,
            safetyMargin,
            commandTimeoutSeconds,
            maxBlock,
            transaction);
    }

    internal static string? NormalizeFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant();
    }

    public static IReadOnlyList<ScoreGroupMintLimitRow> ReadFromBaseRows(
        NpgsqlConnection connection,
        string[] scoreMintPolicies,
        IEnumerable<ScoreGroupMintLimitBaseRow> baseRows,
        DateTimeOffset? targetTimestamp,
        double safetyMargin,
        int commandTimeoutSeconds,
        long? maxBlock = null,
        NpgsqlTransaction? transaction = null)
    {
        var policies = scoreMintPolicies
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        if (policies.Length == 0)
            return [];

        var historical = ReadHistorical(connection, policies, commandTimeoutSeconds, maxBlock, transaction);
        var personal = ReadPersonal(connection, policies, commandTimeoutSeconds, maxBlock, transaction);
        var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp ?? DateTimeOffset.UtcNow, InflationDayZeroUnix);
        var applyMargin = targetTimestamp == null && safetyMargin < 1.0;

        var rows = new List<ScoreGroupMintLimitRow>();
        foreach (var row in baseRows)
        {
            var group = row.GroupAddress.ToLowerInvariant();
            var collateralToken = row.CollateralToken.ToLowerInvariant();
            var policy = row.Policy.ToLowerInvariant();
            var treasuryBalance = row.TreasuryBalance;
            var currentSupply = row.CurrentSupply;
            var collateralId = AddressToTokenId(collateralToken);

            var historicalSupply = historical.TryGetValue((policy, group, collateralId), out var historicalState)
                ? DiscountToTargetDay(historicalState.Amount, historicalState.Day, targetDay)
                : currentSupply;

            var personalMinted = personal.TryGetValue((policy, group, collateralId), out var personalState)
                ? DiscountToTargetDay(personalState.Amount, personalState.Day, targetDay)
                : BigInteger.Zero;

            var available = historicalSupply + personalMinted - treasuryBalance;
            if (available < BigInteger.Zero)
                available = BigInteger.Zero;

            if (applyMargin && available > BigInteger.Zero)
                available = (BigInteger)((double)available * safetyMargin);

            rows.Add(new ScoreGroupMintLimitRow(
                group,
                collateralToken,
                available.ToString(CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    // Two SQL paths selected at runtime:
    //   maxBlock IS NULL  → BaseRowsSql: fast matview + delta tail, demurrage at NOW().
    //                       Used by the live pathfinder (HEAD state queries).
    //   maxBlock IS NOT NULL → BaseRowsSqlHistorical: aggregates raw transfer tables with
    //                       blockNumber <= @maxBlock, demurrage anchored to the block's
    //                       own timestamp. Used by HistoricalLoadGraph (snapshot/historical-graph
    //                       endpoint) and parity tests. NOTE: raw-table scan cost scales with
    //                       transfer volume at the pinned block; acceptable for snapshot use-case
    //                       but avoid using for tight latency paths.
    internal static IReadOnlyList<ScoreGroupMintLimitBaseRow> ReadBaseRows(
        NpgsqlConnection connection,
        string[] policies,
        int commandTimeoutSeconds,
        long? maxBlock,
        NpgsqlTransaction? transaction,
        string? groupAddressFilter = null,
        string? collateralTokenFilter = null,
        IReadOnlyDictionary<string, string[]>? subTreasuryOverrides = null)
    {
        // Flatten the override dict into two parallel text[] arrays so the SQL
        // CTE can unnest them. Empty input keeps every group on its single
        // RegisterGroup-recorded treasury via the COALESCE fallback inside the
        // SQL. All addresses lowercased to match the score_groups CTE keys.
        var overridePairs = (subTreasuryOverrides ?? new Dictionary<string, string[]>())
            .Where(kv => kv.Value.Length > 0)
            .ToArray();
        var subTreasuryAggregators = overridePairs
            .Select(kv => kv.Key.Trim().ToLowerInvariant())
            .ToArray();
        var subTreasuryLists = overridePairs
            .Select(kv => string.Join(",", kv.Value.Select(addr => addr.Trim().ToLowerInvariant())))
            .ToArray();

        var sql = maxBlock.HasValue ? BaseRowsSqlHistorical : BaseRowsSql;
        var rows = new List<ScoreGroupMintLimitBaseRow>();
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("scoreMintPolicies", policies);
        command.Parameters.AddWithValue("subTreasuryAggregators", subTreasuryAggregators);
        command.Parameters.AddWithValue("subTreasuryLists", subTreasuryLists);
        AddMaxBlockParameter(command, maxBlock);
        AddTextFilterParameter(command, "groupAddressFilter", groupAddressFilter);
        AddTextFilterParameter(command, "collateralTokenFilter", collateralTokenFilter);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var group = reader.GetString(0).ToLowerInvariant();
            var collateralToken = reader.GetString(1).ToLowerInvariant();
            var policy = reader.GetString(2).ToLowerInvariant();
            var treasuryBalance = ParseBigInteger(reader.GetString(3));
            var currentSupply = ParseBigInteger(reader.GetString(4));
            rows.Add(new ScoreGroupMintLimitBaseRow(
                group,
                collateralToken,
                policy,
                treasuryBalance,
                currentSupply));
        }

        return rows;
    }

    private static Dictionary<(string Policy, string Group, string Collateral), (BigInteger Amount, ulong Day)> ReadHistorical(
        NpgsqlConnection connection,
        string[] policies,
        int commandTimeoutSeconds,
        long? maxBlock,
        NpgsqlTransaction? transaction)
    {
        var result = new Dictionary<(string, string, string), (BigInteger, ulong)>();
        using var command = new NpgsqlCommand(HistoricalSql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("scoreMintPolicies", policies);
        AddMaxBlockParameter(command, maxBlock);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // policy | group_address | collateral | supply | day
            result[(reader.GetString(0), reader.GetString(1).ToLowerInvariant(), reader.GetString(2))] = (
                ParseBigInteger(reader.GetString(3)),
                ParseUInt64(reader.GetString(4)));
        }

        return result;
    }

    private static Dictionary<(string Policy, string Group, string Collateral), (BigInteger Amount, ulong Day)> ReadPersonal(
        NpgsqlConnection connection,
        string[] policies,
        int commandTimeoutSeconds,
        long? maxBlock,
        NpgsqlTransaction? transaction)
    {
        var result = new Dictionary<(string, string, string), (BigInteger, ulong)>();
        using var command = new NpgsqlCommand(PersonalSql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("scoreMintPolicies", policies);
        AddMaxBlockParameter(command, maxBlock);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1).ToLowerInvariant(), reader.GetString(2))] = (
                ParseBigInteger(reader.GetString(3)),
                ParseUInt64(reader.GetString(4)));
        }

        return result;
    }

    private static BigInteger DiscountToTargetDay(BigInteger amount, ulong storedDay, ulong targetDay)
    {
        var delta = targetDay > storedDay ? targetDay - storedDay : 0;
        return delta == 0 ? amount : CirclesConverter.InflationaryToDemurrage(amount, delta);
    }

    private static string AddressToTokenId(string address)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (hex.Length != 40)
            throw new FormatException($"Invalid token address '{address}'.");

        return BigInteger.Parse("00" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static BigInteger ParseBigInteger(string value) =>
        BigInteger.Parse(value, CultureInfo.InvariantCulture);

    private static ulong ParseUInt64(string value) =>
        ulong.Parse(value, CultureInfo.InvariantCulture);

    private static void AddMaxBlockParameter(NpgsqlCommand command, long? maxBlock)
    {
        var parameter = command.Parameters.Add("maxBlock", NpgsqlDbType.Bigint);
        parameter.Value = (object?)maxBlock ?? DBNull.Value;
    }

    private static void AddTextFilterParameter(NpgsqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Text);
        parameter.Value = (object?)value ?? DBNull.Value;
    }
}
