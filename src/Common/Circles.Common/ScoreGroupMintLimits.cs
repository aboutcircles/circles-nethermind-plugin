using System.Data;
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
    /// Fail-fast ceiling (seconds) for the block-pinned <see cref="BaseRowsSqlHistorical"/> query,
    /// applied on top of the caller's (group/solver) timeout. After Fix A the query resolves the
    /// trusted-token set in ~60ms and the remaining cost is the block-pinned <c>token_supply</c>
    /// scan (~12s near head); this ceiling sits well above that but below the ~60s solver deadline,
    /// so a future regression that reintroduces a hang surfaces as a fast, clearly labelled timeout
    /// instead of a ~60s stall that spikes the <c>/findPath</c> p95 (the near-head incident this
    /// change fixes). The margin (~3.6x the measured cost) keeps healthy near-head traffic from
    /// false-tripping under transient DB load.
    /// </summary>
    internal const int HistoricalBaseRowsTimeoutSeconds = 45;

    /// <summary>Session-local temp table holding the materialized group_tokens set on the live
    /// path. Built + ANALYZEd by <see cref="ReadBaseRows"/> from <see cref="LiveGroupTokensSql"/>,
    /// consumed by <see cref="LiveBalancesTailSql"/>. See Fix D on <see cref="LiveGroupTokensSql"/>.</summary>
    internal const string LiveGroupTokensTempTable = "_sg_mint_group_tokens";

    /// <summary>
    /// Live path, statement 1 — the base-rows builder. Each score group's "treasury" (as recorded
    /// in <c>CrcV2_RegisterGroup.treasury</c>) is expanded into one or more effective treasury
    /// addresses via the <c>treasury_overrides</c> CTE. When the on-chain treasury is a
    /// <c>ScoreTreasury</c> router/splitter (does not custody collateral itself but forwards to
    /// score-keyed sub-treasuries), the override list contains the sub-treasuries that actually
    /// hold tokens. Groups not in the override map fall back to single-treasury behavior — the
    /// override list defaults to <c>ARRAY[treasury]</c>.
    ///
    /// Balance computation (in <see cref="LiveBalancesTailSql"/>) inlines the
    /// <c>V_CrcV2_BalancesByAccountAndToken</c> view logic (matview + post-refresh delta tail)
    /// with the tokenAddress filter applied before the FULL JOIN. Four coordinated optimizations
    /// keep this fast even for score groups that trust thousands of collateral tokens:
    ///   - <b>A</b> — trust relations are resolved by pushing the truster filter into
    ///     <c>CrcV2_Trust</c> before the <c>row_number()</c> window (the
    ///     <c>V_CrcV2_TrustRelations</c> view windows the whole table); avatar registration is
    ///     an indexed <c>EXISTS</c> mirroring <c>V_CrcV2_Avatars</c> instead of joining the view.
    ///   - <b>B</b> — treasury balances are aggregated once per (group, token) via a join
    ///     rather than a per-row correlated subquery over the full <c>balances</c> set.
    ///   - <b>C</b> — the matview balance watermark (<c>__BALANCE_WM__</c>) and avatar watermark
    ///     (<c>__AVATAR_WM__</c>) are inlined as integer literals by <see cref="ReadBaseRows"/>
    ///     so the planner uses the blockNumber indexes for the small delta tail. The fetch runs
    ///     inside a REPEATABLE READ snapshot to stay consistent with <c>filtered_mat</c>.
    ///   - <b>D</b> — <c>group_tokens</c> (this builder's output) is materialized into the
    ///     <see cref="LiveGroupTokensTempTable"/> temp table and ANALYZEd before the balance tail
    ///     runs, so the planner has real cardinalities and picks hash joins for the treasury/supply
    ///     aggregations. As an inline CTE it estimated 1 row and collapsed those joins into a
    ///     ~47M-row nested loop (~8.5s on a group trusting ~10k tokens).
    /// The live query is therefore split into two statements: this builder and
    /// <see cref="LiveBalancesTailSql"/> (which reads the temp table).
    /// </summary>
    internal const string LiveGroupTokensSql = """
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
        -- Fix A: push the truster filter into CrcV2_Trust BEFORE the row_number() window so it
        -- runs over only the score groups' trust rows (idx_CrcV2_Trust_truster) instead of
        -- windowing all ~760K rows. V_CrcV2_TrustRelations cannot push the predicate through
        -- its window function, which made this the dominant cost (~60s+ on large groups).
        group_trust AS (
            SELECT truster, trustee
            FROM (
                SELECT t.truster, t.trustee, t."expiryTime",
                       row_number() OVER (
                           PARTITION BY t.truster, t.trustee
                           ORDER BY t."blockNumber" DESC, t."transactionIndex" DESC, t."logIndex" DESC
                       ) AS rn
                FROM "CrcV2_Trust" t
                WHERE t.truster IN (SELECT DISTINCT group_address FROM effective_treasuries)
            ) d
            WHERE d.rn = 1
              AND d."expiryTime" > COALESCE((SELECT MAX("timestamp") FROM "System_Block"), 0)::numeric
        )
        -- group_tokens final projection — materialized into the temp table by ReadBaseRows.
        -- Mirror V_CrcV2_Avatars (matview + registrations since its watermark) with an indexed
        -- existence check. A semi-join against the view itself costs ~18s because the view has
        -- no index. __AVATAR_WM__ is the inlined MAX("blockNumber") of M_CrcV2_Avatars (see
        -- ReadBaseRows); the three register-table branches cover avatars registered after the
        -- last matview refresh.
        SELECT
            et.group_address,
            et.treasuries,
            et.policy,
            gt.trustee AS trusted_token
        FROM effective_treasuries et
        INNER JOIN group_trust gt ON gt.truster = et.group_address
        WHERE (@collateralTokenFilter::text IS NULL OR gt.trustee = @collateralTokenFilter)
          AND (
              EXISTS (SELECT 1 FROM "M_CrcV2_Avatars" a WHERE a.avatar = gt.trustee)
              OR EXISTS (SELECT 1 FROM "CrcV2_RegisterHuman" r WHERE r.avatar = gt.trustee AND r."blockNumber" > __AVATAR_WM__)
              OR EXISTS (SELECT 1 FROM "CrcV2_RegisterOrganization" r WHERE r.organization = gt.trustee AND r."blockNumber" > __AVATAR_WM__)
              OR EXISTS (SELECT 1 FROM "CrcV2_RegisterGroup" r WHERE r."group" = gt.trustee AND r."blockNumber" > __AVATAR_WM__)
          );
        """;

    /// <summary>
    /// Live path, statement 2: consumes the group_tokens temp table
    /// (<see cref="LiveGroupTokensTempTable"/>) and computes per-(group, collateral) treasury
    /// balance + current supply. Reads <c>M_CrcV2_BalancesByAccountAndToken</c> (matview) PLUS
    /// the post-watermark delta tail and merges them, so the result reflects HEAD state even
    /// though the matview lags. The temp table carries real ANALYZEd statistics, so the planner
    /// picks hash joins for the treasury/supply aggregations instead of the nested-loop plan an
    /// inline (estimated-1-row) CTE produced. Fix C: the matview balance watermark is inlined as
    /// a literal (__BALANCE_WM__) so the planner uses the blockNumber indexes for the small delta
    /// tail; ReadBaseRows reads it inside the same REPEATABLE READ snapshot as the matview and the
    /// temp-table build, keeping the watermark consistent with the rows read here.
    /// </summary>
    internal const string LiveBalancesTailSql = """
        WITH delta_tx AS (
            SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
            FROM "CrcV2_TransferSingle"
            WHERE "blockNumber" > __BALANCE_WM__
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM _sg_mint_group_tokens))
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
            FROM "CrcV2_TransferSingle"
            WHERE "blockNumber" > __BALANCE_WM__
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM _sg_mint_group_tokens))
            UNION ALL
            SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
            FROM "CrcV2_TransferBatch"
            WHERE "blockNumber" > __BALANCE_WM__
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM _sg_mint_group_tokens))
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
            FROM "CrcV2_TransferBatch"
            WHERE "blockNumber" > __BALANCE_WM__
              AND "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM _sg_mint_group_tokens))
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
            WHERE "tokenAddress" = ANY(ARRAY(SELECT DISTINCT trusted_token FROM _sg_mint_group_tokens))
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
            INNER JOIN (SELECT DISTINCT trusted_token FROM _sg_mint_group_tokens) gt
                ON gt.trusted_token = b."tokenAddress"
            GROUP BY b."tokenAddress"
        ),
        -- Fix B: aggregate treasury balances once per (group, token) via a join instead of a
        -- correlated subquery that rescanned `balances` for every group_tokens row
        -- (O(tokens x balances) -> the 120s+ blowup on large groups).
        treasury_pairs AS (
            SELECT DISTINCT gt.group_address, gt.trusted_token, ta.account
            FROM _sg_mint_group_tokens gt, unnest(gt.treasuries) AS ta(account)
        ),
        treasury_balances AS (
            SELECT tp.group_address, tp.trusted_token,
                   COALESCE(SUM(b."demurragedTotalBalance"), 0) AS treasury_balance
            FROM treasury_pairs tp
            LEFT JOIN balances b
                ON b.account = tp.account
               AND b."tokenAddress" = tp.trusted_token
            GROUP BY tp.group_address, tp.trusted_token
        )
        SELECT
            gt.group_address,
            gt.trusted_token,
            gt.policy,
            COALESCE(tb.treasury_balance, 0)::text AS treasury_balance,
            COALESCE(ts.current_supply, '0') AS current_supply
        FROM _sg_mint_group_tokens gt
        LEFT JOIN treasury_balances tb
            ON tb.group_address = gt.group_address
           AND tb.trusted_token = gt.trusted_token
        LEFT JOIN token_supply ts
            ON ts."tokenAddress" = gt.trusted_token;
        """;

    /// <summary>
    /// Historical variant of the live <see cref="LiveGroupTokensSql"/> + <see cref="LiveBalancesTailSql"/>
    /// pair, used when <c>maxBlock</c> is non-null.
    /// Bypasses <c>M_CrcV2_BalancesByAccountAndToken</c> (the matview, which only holds HEAD
    /// state and has no block-pinned twin in the test environment) and instead aggregates
    /// directly from raw <c>CrcV2_TransferSingle/Batch</c> tables with
    /// <c>WHERE "blockNumber" &lt;= @maxBlock</c>. Demurrage is anchored to the block's own
    /// timestamp from <c>System_Block</c>, mirroring the <c>circles_at_block</c> schema's
    /// <c>pin_timestamp()</c> function. This makes parity tests and any future historical
    /// point-in-time queries produce correct results.
    ///
    /// <b>Fix A</b> (mirrors <see cref="LiveGroupTokensSql"/>): the <c>group_tokens</c> CTE resolves
    /// the score groups' trusted collateral tokens the same fast way the live query does — a
    /// truster-pushdown over <c>CrcV2_Trust</c> plus an indexed avatar-registration <c>EXISTS</c> —
    /// instead of joining <c>V_CrcV2_TrustRelations</c> (which windows all ~760K trust rows) and the
    /// unindexed <c>V_CrcV2_Avatars</c>. Those two view joins were the dominant cost (~40s+ hang that
    /// pushed near-head block-pinned findPath past the solver/group timeout → -32603); the pushdown
    /// is ~60ms and returns the byte-identical head trust/avatar set (proven equivalent to the views;
    /// see their definitions). Everything downstream — the block-pinned <c>raw_tx</c> balances,
    /// <c>token_supply</c> and <c>treasury_balances</c> — is UNCHANGED, so results are identical to
    /// the pre-Fix-A query; only the trusted-token resolution got faster. <c>__AVATAR_WM__</c> is
    /// inlined by <see cref="ReadBaseRows"/> exactly as on the live path.
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
        -- Fix A, part 1: reproduce V_CrcV2_TrustRelations by pushing the truster filter into
        -- CrcV2_Trust BEFORE the row_number() window, so it runs over only the score groups' trust
        -- rows (idx_CrcV2_Trust_truster) instead of windowing all ~760K rows. row_number()
        -- partitions by (truster, trustee), so pre-filtering trusters does not change any kept
        -- truster's rn — the result is identical to the view. Head-expiry filter matches the view.
        group_trust AS (
            SELECT truster, trustee
            FROM (
                SELECT t.truster, t.trustee, t."expiryTime",
                       row_number() OVER (
                           PARTITION BY t.truster, t.trustee
                           ORDER BY t."blockNumber" DESC, t."transactionIndex" DESC, t."logIndex" DESC
                       ) AS rn
                FROM "CrcV2_Trust" t
                WHERE t.truster IN (SELECT DISTINCT group_address FROM effective_treasuries)
            ) d
            WHERE d.rn = 1
              AND d."expiryTime" > COALESCE((SELECT MAX("timestamp") FROM "System_Block"), 0)::numeric
        ),
        -- Fix A, part 2: reproduce V_CrcV2_Avatars membership (matview + registrations since its
        -- watermark) with an indexed EXISTS instead of joining the unindexed view (~18s). This is
        -- the exact structure of the V_CrcV2_Avatars view definition (M_CrcV2_Avatars UNION the
        -- three register tables > watermark). __AVATAR_WM__ is inlined by ReadBaseRows as
        -- MAX("blockNumber") of M_CrcV2_Avatars.
        group_tokens AS (
            SELECT
                et.group_address,
                et.treasuries,
                et.policy,
                gt.trustee AS trusted_token
            FROM effective_treasuries et
            INNER JOIN group_trust gt ON gt.truster = et.group_address
            WHERE (@collateralTokenFilter::text IS NULL OR gt.trustee = @collateralTokenFilter)
              AND (
                  EXISTS (SELECT 1 FROM "M_CrcV2_Avatars" a WHERE a.avatar = gt.trustee)
                  OR EXISTS (SELECT 1 FROM "CrcV2_RegisterHuman" r WHERE r.avatar = gt.trustee AND r."blockNumber" > __AVATAR_WM__)
                  OR EXISTS (SELECT 1 FROM "CrcV2_RegisterOrganization" r WHERE r.organization = gt.trustee AND r."blockNumber" > __AVATAR_WM__)
                  OR EXISTS (SELECT 1 FROM "CrcV2_RegisterGroup" r WHERE r."group" = gt.trustee AND r."blockNumber" > __AVATAR_WM__)
              )
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
        ),
        -- Fix B: aggregate treasury balances once per (group, token) via a join instead of a
        -- correlated subquery that rescanned `balances` for every group_tokens row
        -- (O(tokens x balances) -> the 120s+ blowup on large groups).
        treasury_pairs AS (
            SELECT DISTINCT gt.group_address, gt.trusted_token, ta.account
            FROM group_tokens gt, unnest(gt.treasuries) AS ta(account)
        ),
        treasury_balances AS (
            SELECT tp.group_address, tp.trusted_token,
                   COALESCE(SUM(b."demurragedTotalBalance"), 0) AS treasury_balance
            FROM treasury_pairs tp
            LEFT JOIN balances b
                ON b.account = tp.account
               AND b."tokenAddress" = tp.trusted_token
            GROUP BY tp.group_address, tp.trusted_token
        )
        SELECT
            gt.group_address,
            gt.trusted_token,
            gt.policy,
            COALESCE(tb.treasury_balance, 0)::text AS treasury_balance,
            COALESCE(ts.current_supply, '0') AS current_supply
        FROM group_tokens gt
        LEFT JOIN treasury_balances tb
            ON tb.group_address = gt.group_address
           AND tb.trusted_token = gt.trusted_token
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

    /// <summary>
    /// Returns the per-(group, collateral) demurraged treasury collateral balance — i.e. the on-chain
    /// <c>ScoreTreasury.balanceOfCollateral(c)</c> aggregated across the HIGH+LOW sub-treasuries (via the
    /// <c>SCORE_TREASURY_SUBTREASURIES</c> override map). This is the raw treasury-side cap used by the
    /// gCRC redeem-capacity computation: how much of each collateral the treasury can actually hand back.
    /// Unlike <see cref="Read"/> it does NOT subtract historical/personal supply — that subtraction is the
    /// remaining MINT headroom, the inverse of what a redeem can withdraw. The <see cref="ScoreGroupMintLimitBaseRow.TreasuryBalance"/>
    /// field carries the demurraged balance (demurrage basis = live NOW(), matching the holder's
    /// demurraged gCRC balance used as the redeem entitlement).
    /// </summary>
    public static IReadOnlyList<ScoreGroupMintLimitBaseRow> ReadTreasuryBalances(
        NpgsqlConnection connection,
        string[] scoreMintPolicies,
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

        return ReadBaseRows(
            connection,
            policies,
            commandTimeoutSeconds,
            maxBlock,
            transaction,
            NormalizeFilter(groupAddressFilter),
            NormalizeFilter(collateralTokenFilter),
            subTreasuryOverrides);
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
    //   maxBlock IS NULL  → LiveGroupTokensSql (materialized into an ANALYZEd temp table) +
    //                       LiveBalancesTailSql: matview + delta tail, demurrage at NOW().
    //                       Used by the live pathfinder + RPC (HEAD state queries).
    //   maxBlock IS NOT NULL → BaseRowsSqlHistorical: aggregates raw transfer tables with
    //                       blockNumber <= @maxBlock, demurrage anchored to the block's own
    //                       timestamp. Used by HistoricalLoadGraph (snapshot/historical-graph
    //                       endpoint), block-pinned findPath, and parity tests. Fix A resolves the
    //                       trusted-token set via a truster-pushdown + indexed avatar EXISTS (the
    //                       __AVATAR_WM__ watermark is inlined below, mirroring the live path),
    //                       replacing the V_CrcV2_TrustRelations/V_CrcV2_Avatars view joins that
    //                       hung ~40s+ near head. Result is byte-identical; only the token
    //                       resolution got faster. A fail-fast timeout guards against regressions.
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

        // Binds the parameters shared by the historical query and the live temp-table builder.
        void AddBaseRowParameters(NpgsqlCommand command)
        {
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.AddWithValue("scoreMintPolicies", policies);
            command.Parameters.AddWithValue("subTreasuryAggregators", subTreasuryAggregators);
            command.Parameters.AddWithValue("subTreasuryLists", subTreasuryLists);
            AddMaxBlockParameter(command, maxBlock);
            AddTextFilterParameter(command, "groupAddressFilter", groupAddressFilter);
            AddTextFilterParameter(command, "collateralTokenFilter", collateralTokenFilter);
        }

        // Historical/snapshot path: one self-contained query carrying all parameters. The
        // fail-fast CommandTimeout (see HistoricalBaseRowsTimeoutSeconds) bounds it below the
        // caller's group/solver deadline so a regression fails fast and clear instead of hanging.
        List<ScoreGroupMintLimitBaseRow> Execute(string sql, NpgsqlTransaction? tx)
        {
            using var command = new NpgsqlCommand(sql, connection, tx);
            AddBaseRowParameters(command);
            command.CommandTimeout = Math.Min(commandTimeoutSeconds, HistoricalBaseRowsTimeoutSeconds);
            return MaterializeBaseRows(command);
        }

        // Historical/snapshot path bounds the balance tables on blockNumber <= @maxBlock. Its
        // group_tokens CTE mirrors the live Fix A (truster-pushdown + avatar-registration EXISTS),
        // so it needs the same __AVATAR_WM__ (M_CrcV2_Avatars max blockNumber) inlined as a literal
        // — read here and substituted before the query runs. Head-consistent, matching the
        // V_CrcV2_TrustRelations/V_CrcV2_Avatars head views the pre-Fix-A query joined.
        if (maxBlock.HasValue)
        {
            var (_, avatarWatermark) =
                ReadMatviewWatermarks(connection, transaction, commandTimeoutSeconds);
            var historicalSql = BaseRowsSqlHistorical
                .Replace("__AVATAR_WM__", avatarWatermark.ToString(CultureInfo.InvariantCulture));
            return Execute(historicalSql, transaction);
        }

        // Live path: inline the matview watermarks as integer literals so the planner uses the
        // blockNumber indexes for the post-refresh delta tail (Fix C). The inlined balance
        // watermark must reflect the SAME matview snapshot that filtered_mat reads, otherwise a
        // concurrent REFRESH between the watermark fetch and the main query could double-count
        // the delta tail. That holds only under a snapshot-stable isolation level:
        //   - when we own the transaction, we open it REPEATABLE READ below;
        //   - when one is supplied, the caller MUST have done the same — enforced here so a
        //     future caller passing READ COMMITTED fails loud rather than silently mis-counting.
        if (transaction != null
            && transaction.IsolationLevel is not (IsolationLevel.RepeatableRead
                or IsolationLevel.Serializable
                or IsolationLevel.Snapshot))
        {
            throw new InvalidOperationException(
                "ScoreGroupMintLimits live query requires a REPEATABLE READ (or stricter) " +
                "transaction so the inlined matview watermark stays consistent with filtered_mat; " +
                $"got {transaction.IsolationLevel}.");
        }

        var ownTransaction = transaction == null;
        var liveTransaction = transaction
            ?? connection.BeginTransaction(IsolationLevel.RepeatableRead);
        try
        {
            var (balanceWatermark, avatarWatermark) =
                ReadMatviewWatermarks(connection, liveTransaction, commandTimeoutSeconds);

            // Fix D: materialize group_tokens into an ANALYZEd temp table so the planner has the
            // real row count (~10k for a large score group) for the balance/treasury joins in the
            // tail. As an inline CTE it estimated 1 row and the treasury_balances/token_supply
            // joins collapsed into a nested loop (a ~47M-row filter, ~8.5s); with real stats they
            // become hash joins (~0.6s). The temp table is ON COMMIT DROP and lives in the same
            // REPEATABLE READ snapshot as the watermark read above — consistent and self-cleaning.
            var builderSql = LiveGroupTokensSql
                .Replace("__AVATAR_WM__", avatarWatermark.ToString(CultureInfo.InvariantCulture));
            using (var dropCommand = new NpgsqlCommand(
                       $"DROP TABLE IF EXISTS {LiveGroupTokensTempTable}", connection, liveTransaction))
            {
                dropCommand.CommandTimeout = commandTimeoutSeconds;
                dropCommand.ExecuteNonQuery();
            }
            using (var buildCommand = new NpgsqlCommand(
                       $"CREATE TEMP TABLE {LiveGroupTokensTempTable} ON COMMIT DROP AS\n{builderSql}",
                       connection, liveTransaction))
            {
                AddBaseRowParameters(buildCommand);
                buildCommand.ExecuteNonQuery();
            }
            using (var analyzeCommand = new NpgsqlCommand(
                       $"ANALYZE {LiveGroupTokensTempTable}", connection, liveTransaction))
            {
                analyzeCommand.CommandTimeout = commandTimeoutSeconds;
                analyzeCommand.ExecuteNonQuery();
            }

            var tailSql = LiveBalancesTailSql
                .Replace("__BALANCE_WM__", balanceWatermark.ToString(CultureInfo.InvariantCulture));
            using var tailCommand = new NpgsqlCommand(tailSql, connection, liveTransaction);
            tailCommand.CommandTimeout = commandTimeoutSeconds;
            var rows = MaterializeBaseRows(tailCommand);

            if (ownTransaction)
                liveTransaction.Commit();
            return rows;
        }
        catch
        {
            // Preserve the original failure: a Rollback() on an already-broken connection can
            // itself throw and would otherwise mask the root-cause exception.
            if (ownTransaction)
            {
                try { liveTransaction.Rollback(); }
                catch { /* ignore — original exception below is the diagnostically useful one */ }
            }

            throw;
        }
        finally
        {
            if (ownTransaction)
                liveTransaction.Dispose();
        }
    }

    /// <summary>
    /// Reads the 5-column base-row shape (group, collateral, policy, treasury balance, supply)
    /// produced by both the historical query and the live balance tail.
    /// </summary>
    private static List<ScoreGroupMintLimitBaseRow> MaterializeBaseRows(NpgsqlCommand command)
    {
        var rows = new List<ScoreGroupMintLimitBaseRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ScoreGroupMintLimitBaseRow(
                reader.GetString(0).ToLowerInvariant(),
                reader.GetString(1).ToLowerInvariant(),
                reader.GetString(2).ToLowerInvariant(),
                ParseBigInteger(reader.GetString(3)),
                ParseBigInteger(reader.GetString(4))));
        }

        return rows;
    }

    /// <summary>
    /// Reads the current matview watermarks used to inline <c>__BALANCE_WM__</c> (max
    /// <c>_maxBlock</c> in <c>M_CrcV2_BalancesByAccountAndToken</c>, the cutoff for the balance
    /// delta tail) and <c>__AVATAR_WM__</c> (max <c>blockNumber</c> in <c>M_CrcV2_Avatars</c>,
    /// the cutoff for the avatar-registration delta) into <see cref="LiveGroupTokensSql"/>
    /// (avatar) and <see cref="LiveBalancesTailSql"/> (balance).
    /// </summary>
    private static (long BalanceWatermark, long AvatarWatermark) ReadMatviewWatermarks(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds)
    {
        const string sql = """
            SELECT
                COALESCE((SELECT MAX("_maxBlock") FROM "M_CrcV2_BalancesByAccountAndToken"), 0)::bigint,
                COALESCE((SELECT MAX("blockNumber") FROM "M_CrcV2_Avatars"), 0)::bigint
            """;
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (0L, 0L);

        var balanceWatermark = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
        var avatarWatermark = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return (balanceWatermark, avatarWatermark);
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
