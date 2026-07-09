using System.Numerics;
using Circles.Common.TestUtils;
using NUnit.Framework;

namespace Circles.Common.Tests;

/// <summary>
/// Integration tests that verify <see cref="ScoreGroupMintLimitReader.BaseRowsSqlHistorical"/>
/// produces numerically identical results to a view-based oracle query built from
/// V_CrcV2_TrustRelations / V_CrcV2_Avatars / V_CrcV2_BalancesByAccountAndToken directly. The
/// historical query resolves trusted tokens via Fix A (truster-pushdown + register EXISTS) rather
/// than the view joins; set + value parity of that resolution was validated directly against
/// staging, and the numeric treasury/supply math is what these tests pin against the oracle.
///
/// Both queries execute through the test-environment HTTP query proxy, which pins
/// search_path = circles_at_block, public and circles.max_block_number = N for every
/// request. Under that search_path:
///   - reference: V_CrcV2_BalancesByAccountAndToken → circles_at_block twin → raw tables
///     with blockNumber &lt;= pin_block() and pin_timestamp() for demurrage. Correct.
///   - historical: BaseRowsSqlHistorical → raw tables with blockNumber &lt;= @maxBlock and
///     block timestamp from System_Block. Correct by construction; must match reference.
///
/// Requires TEST_ENV_URL; tests skip automatically when absent.
/// Block 46406058 is a real Gnosis block where the OffchainScoreBasedMintPolicy score
/// group was active (confirmed by the personal-mint regression scenario).
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("RequiresTestEnv")]
public sealed class ScoreGroupBaseRowsQueryParityTests
{
    private const string ScoreGroupMintPolicy = "0x450d68272e43c4cab7cbc7faa37893a50fae9569";
    private const long TestBlock = 46_406_058;

    // Original view-based SQL with test values inlined — the oracle reference.
    // The proxy resolves V_CrcV2_BalancesByAccountAndToken to the circles_at_block
    // block-pinned twin so this gives correctly block-pinned, pin_timestamp()-demurraged
    // results. Treasury overrides omitted (no override map needed for this fixture).
    private const string ReferenceBaseRowsSql = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                LOWER("emitter") AS policy
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE "blockNumber" <= 46406058
              AND LOWER("emitter") = ANY(ARRAY['0x450d68272e43c4cab7cbc7faa37893a50fae9569']::text[])
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        score_groups AS (
            SELECT
                LOWER(g."group") AS group_address,
                LOWER(g."treasury") AS treasury,
                LOWER(COALESCE(lsg.policy, g."mint")) AS policy
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group lsg ON LOWER(lsg.group_address) = LOWER(g."group")
            WHERE g."blockNumber" <= 46406058
              AND LOWER(g."mint") = ANY(ARRAY['0x450d68272e43c4cab7cbc7faa37893a50fae9569']::text[])
              AND lsg.group_address IS NOT NULL
        ),
        effective_treasuries AS (
            SELECT group_address, policy, ARRAY[treasury]::text[] AS treasuries
            FROM score_groups
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
        ),
        token_supply AS (
            SELECT
                b."tokenAddress",
                SUM(b."demurragedTotalBalance")::text AS current_supply
            FROM "V_CrcV2_BalancesByAccountAndToken" b
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
                FROM "V_CrcV2_BalancesByAccountAndToken" b
                WHERE b.account = ANY(gt.treasuries)
                  AND b."tokenAddress" = gt.trusted_token
            ), 0)::text AS treasury_balance,
            COALESCE(ts.current_supply, '0') AS current_supply
        FROM group_tokens gt
        LEFT JOIN token_supply ts
            ON ts."tokenAddress" = gt.trusted_token
        """;

    // BaseRowsSqlHistorical with the test values inlined for proxy execution.
    // @maxBlock → literal 46406058
    // @scoreMintPolicies → ARRAY literal
    // __AVATAR_WM__ → the avatar-watermark subquery ReadBaseRows inlines as a literal (Fix A's
    //   register-tail EXISTS boundary). A subquery gives the same value the runtime computes.
    // Optional filters cleared to NULL / empty-array defaults
    private static readonly string HistoricalBaseRowsSql =
        ScoreGroupMintLimitReader.BaseRowsSqlHistorical
            .Replace("@maxBlock", "46406058")
            .Replace(
                "ANY(@scoreMintPolicies)",
                "ANY(ARRAY['0x450d68272e43c4cab7cbc7faa37893a50fae9569']::text[])")
            .Replace(
                "@groupAddressFilter::text IS NULL OR LOWER(g.\"group\") = @groupAddressFilter",
                "TRUE")
            .Replace(
                "@collateralTokenFilter::text IS NULL OR gt.trustee = @collateralTokenFilter",
                "TRUE")
            .Replace(
                "@subTreasuryAggregators::text[]",
                "ARRAY[]::text[]")
            .Replace(
                "@subTreasuryLists::text[]",
                "ARRAY[]::text[]")
            .Replace(
                "__AVATAR_WM__",
                "(SELECT COALESCE(MAX(\"blockNumber\"),0) FROM \"M_CrcV2_Avatars\")");

    private static bool TestEnvAvailable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ENV_URL"));

    [Test]
    public async Task HistoricalBaseRowsSql_MatchesViewBasedReference()
    {
        if (!TestEnvAvailable)
            Assert.Ignore("TEST_ENV_URL not set — skipping integration test");

        Assert.That(HistoricalBaseRowsSql, Does.Not.Contain("@"),
            "SQL parameter substitution incomplete — a .Replace() pattern no longer matches BaseRowsSqlHistorical. " +
            "Update the .Replace() calls to match the current parameter names in BaseRowsSqlHistorical.");

        if (!await TestEnvironmentClient.BlockExistsAsync(TestBlock))
            Assert.Ignore($"Block {TestBlock} not indexed in test environment — skipping");

        await using var session = await TestEnvironmentClient.CreateSessionAsync(TestBlock);

        // Both queries go through the block-pinned proxy (search_path = circles_at_block, public).
        // Reference: V_CrcV2_BalancesByAccountAndToken → circles_at_block twin → raw tables + pin_timestamp().
        // Historical: raw tables + block timestamp from System_Block.
        // Both anchored to the same block; results must match within ±1 for integer rounding.
        var referenceResponse = await session.ExecuteQueryAsync(ReferenceBaseRowsSql, maxRows: 1000);
        var historicalResponse = await session.ExecuteQueryAsync(HistoricalBaseRowsSql, maxRows: 1000);
        Assert.That(referenceResponse.Truncated, Is.False,
            "Reference query truncated at 1000 rows — raise maxRows limit");
        Assert.That(historicalResponse.Truncated, Is.False,
            "Historical query truncated at 1000 rows — raise maxRows limit");
        var referenceRows = ParseBaseRows(referenceResponse);
        var historicalRows = ParseBaseRows(historicalResponse);

        Assert.That(historicalRows, Is.Not.Empty,
            $"Expected at least one score-group row at block {TestBlock}");
        Assert.That(historicalRows.Count, Is.EqualTo(referenceRows.Count),
            $"Row count mismatch: historical={historicalRows.Count} reference={referenceRows.Count}");

        var refByKey = referenceRows.ToDictionary(r => (r.GroupAddress, r.CollateralToken));

        foreach (var opt in historicalRows)
        {
            var key = (opt.GroupAddress, opt.CollateralToken);
            Assert.That(refByKey.ContainsKey(key), Is.True,
                $"Historical row ({opt.GroupAddress}, {opt.CollateralToken}) missing from reference");

            var @ref = refByKey[key];

            Assert.That(opt.TreasuryBalance, Is.EqualTo(@ref.TreasuryBalance),
                $"TreasuryBalance mismatch for ({opt.GroupAddress}, {opt.CollateralToken}): " +
                $"historical={opt.TreasuryBalance} ref={@ref.TreasuryBalance}");

            // ±1 tolerance: both paths use the same block timestamp for demurrage, but the
            // POWER(gamma, days) floating-point result may differ by one integer after floor().
            var delta = BigInteger.Abs(opt.CurrentSupply - @ref.CurrentSupply);
            Assert.That(delta <= BigInteger.One,
                $"CurrentSupply differs by {delta} for ({opt.GroupAddress}, {opt.CollateralToken}): " +
                $"historical={opt.CurrentSupply} ref={@ref.CurrentSupply}");
        }
    }

    [Test]
    public void ReadBaseRows_HistoricalSql_HasCorrectStructure()
    {
        // Verify BaseRowsSqlHistorical bypasses the matview and uses raw tables + block timestamp.
        // The C# dispatch (maxBlock.HasValue ? BaseRowsSqlHistorical : live builder+tail) is not
        // exercisable without a direct DB connection; this test validates the SQL constant used
        // by that dispatch path has the expected shape.
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("block_ts"),
            "Historical SQL must anchor demurrage to block timestamp via block_ts CTE");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("raw_tx"),
            "Historical SQL must aggregate from raw transfer tables via raw_tx CTE");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Not.Contain("M_CrcV2_BalancesByAccountAndToken"),
            "Historical SQL must NOT reference the matview (which only holds HEAD state)");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("\"blockNumber\" <= @maxBlock"),
            "Historical SQL must filter all raw table reads by blockNumber <= @maxBlock");

        // Fix B (treasury pre-aggregation) is applied to both the live and historical queries:
        // the per-row correlated subquery over `balances` is replaced by a treasury_balances join.
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("treasury_balances"),
            "Historical SQL must aggregate treasury balances via a join, not a correlated subquery");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Not.Contain("b.account = ANY(gt.treasuries)"),
            "Historical SQL must not retain the per-row correlated treasury subquery");

        // Fix A: the historical query resolves trusted tokens the same fast way the live query does
        // — a truster-pushdown group_trust CTE + indexed avatar-registration EXISTS — instead of
        // joining V_CrcV2_TrustRelations (windows all ~760K trust rows) and the un-indexed
        // V_CrcV2_Avatars. Those view joins were the ~40s+ near-head hang. Set + value parity of the
        // pushdown vs the views was validated directly against staging (11452 rows, symmetric diff
        // 0). These guards prevent a future edit from silently reintroducing the view joins.
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("group_trust"),
            "Historical SQL must resolve trust via the pushed-down group_trust CTE (Fix A)");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Not.Contain("\"V_CrcV2_TrustRelations\""),
            "Historical SQL must not window the whole V_CrcV2_TrustRelations view");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Not.Contain("\"V_CrcV2_Avatars\""),
            "Historical SQL must not join the un-indexed V_CrcV2_Avatars view");
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("__AVATAR_WM__"),
            "Historical SQL must carry the avatar-watermark literal placeholder (inlined by ReadBaseRows)");

        // token_supply is DELIBERATELY retained on the historical path. It is the all-holders
        // supply fallback ReadFromBaseRows uses for every (group, collateral) pair lacking a
        // HistoricalSupply event — the majority of pairs (staging: 11309 of 11452 have nonzero
        // current_supply). Dropping it would zero those pairs' mint headroom = a routing-capacity
        // regression. Fix A only sped up trusted-token resolution; supply math is untouched.
        Assert.That(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, Does.Contain("token_supply"),
            "Historical SQL must retain the all-holders token_supply fallback (no supply regression)");
    }

    [Test]
    public void ReadBaseRows_HistoricalSql_AvatarWatermarkFullySubstituted()
    {
        // Guard Fix A's runtime substitution: ReadBaseRows replaces __AVATAR_WM__ in the historical
        // SQL before executing (mirroring the live builder). If a future rename leaves the
        // placeholder behind, the generated SQL carries a literal "__…__" token → a Postgres parse
        // error, or a silently-dropped register-EXISTS branch that would feed a wrong trusted-token
        // set into the treasury/supply sums. Mirror the exact .Replace() ReadBaseRows performs and
        // assert no placeholder survives.
        var historical = ScoreGroupMintLimitReader.BaseRowsSqlHistorical.Replace("__AVATAR_WM__", "46000001");
        Assert.That(historical, Does.Not.Contain("__"),
            "After avatar-watermark substitution the historical SQL must contain no residual '__…__' placeholder");
    }

    [Test]
    public void ReadBaseRows_LiveSql_HasOptimizedStructure()
    {
        // The live (maxBlock IS NULL) query carries all optimizations from the
        // ScoreGroupMintLimits perf rewrite, split across the group_tokens builder and the
        // balance tail. These structural guards prevent a future edit from silently
        // reintroducing the O(n^2) / full-table-window / inline-CTE patterns. Functional
        // equivalence was validated directly against staging (set + value parity vs the
        // original view-based query).
        var builder = ScoreGroupMintLimitReader.LiveGroupTokensSql;
        var tail = ScoreGroupMintLimitReader.LiveBalancesTailSql;

        // Fix A: trust resolved by pushing the truster filter into CrcV2_Trust before the
        // window, and avatar registration checked via an indexed EXISTS — not the views.
        Assert.That(builder, Does.Contain("group_trust"),
            "Live builder must resolve trust via the pushed-down group_trust CTE");
        Assert.That(builder, Does.Not.Contain("\"V_CrcV2_TrustRelations\""),
            "Live builder must not window the whole V_CrcV2_TrustRelations view");
        Assert.That(builder, Does.Not.Contain("\"V_CrcV2_Avatars\""),
            "Live builder must not join the un-indexed V_CrcV2_Avatars view");

        // Fix B: treasury balances aggregated once via join, not a per-row subquery.
        Assert.That(tail, Does.Contain("treasury_balances"),
            "Live tail must aggregate treasury balances via a join");
        Assert.That(tail, Does.Not.Contain("b.account = ANY(gt.treasuries)"),
            "Live tail must not retain the per-row correlated treasury subquery");

        // Fix C: matview watermarks inlined as literals (replaced by ReadBaseRows). The avatar
        // watermark gates the builder's registration EXISTS; the balance watermark bounds the
        // tail's delta tail.
        Assert.That(builder, Does.Contain("__AVATAR_WM__"),
            "Live builder must carry the avatar-watermark literal placeholder");
        Assert.That(tail, Does.Contain("__BALANCE_WM__"),
            "Live tail must carry the balance-watermark literal placeholder");

        // Fix D: the tail must read the ANALYZEd temp table (real cardinalities → hash joins),
        // NOT an inline group_tokens CTE (estimated 1 row → nested-loop blowup, ~8.5s).
        Assert.That(tail, Does.Contain(ScoreGroupMintLimitReader.LiveGroupTokensTempTable),
            "Live tail must read the materialized group_tokens temp table");
        Assert.That(tail, Does.Not.Contain("group_tokens AS ("),
            "Live tail must not reintroduce an inline group_tokens CTE (defeats the temp-table plan)");
        Assert.That(builder.TrimEnd(), Does.EndWith(";"),
            "Live builder must be a single self-contained statement for CREATE TEMP TABLE AS");
    }

    [Test]
    public void ReadBaseRows_LiveSql_WatermarkPlaceholdersFullySubstituted()
    {
        // Guard Fix C's runtime substitution: ReadBaseRows replaces __AVATAR_WM__ in the builder
        // and __BALANCE_WM__ in the tail. If a future rename leaves one placeholder behind, the
        // generated SQL would carry a literal "__…__" token → a Postgres parse error (or, worse,
        // a silently-dropped EXISTS branch feeding wrong tokens into a financial sum). Mirror the
        // exact .Replace() calls ReadBaseRows performs and assert no placeholder survives.
        var builder = ScoreGroupMintLimitReader.LiveGroupTokensSql.Replace("__AVATAR_WM__", "46000001");
        var tail = ScoreGroupMintLimitReader.LiveBalancesTailSql.Replace("__BALANCE_WM__", "46000000");

        Assert.That(builder, Does.Not.Contain("__"),
            "After avatar-watermark substitution the live builder must contain no residual '__…__' placeholder");
        Assert.That(tail, Does.Not.Contain("__"),
            "After balance-watermark substitution the live tail must contain no residual '__…__' placeholder");
    }

    [Test]
    public void ReadBaseRows_SharedTreasuryTail_IdenticalAcrossLiveAndHistorical()
    {
        // Fix B's treasury aggregation + final projection is duplicated in the live tail and
        // BaseRowsSqlHistorical. A correctness fix to one that misses the other would silently
        // diverge live vs historical (block-pinned) mint-limit math. The only intended difference
        // is the source relation (live reads the temp table, historical the inline group_tokens
        // CTE), so normalize that away and pin the rest string-identical.
        const string tailMarker = "treasury_pairs AS (";
        var liveTail = TailFrom(ScoreGroupMintLimitReader.LiveBalancesTailSql, tailMarker)
            .Replace(ScoreGroupMintLimitReader.LiveGroupTokensTempTable, "group_tokens");
        var historicalTail = TailFrom(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, tailMarker);

        Assert.That(liveTail, Is.Not.Empty, "Live tail must contain the shared treasury tail");
        Assert.That(historicalTail, Is.EqualTo(liveTail),
            "The treasury_pairs/treasury_balances/final-SELECT tail must stay identical (modulo source relation) across the live and historical queries");
    }

    [Test]
    public void ReadBaseRows_SharedHeadCte_IdenticalAcrossLiveAndHistorical()
    {
        // The score-group resolution head (latest_score_group → score_groups → treasury_overrides
        // → effective_treasuries) is duplicated verbatim in the live builder and the historical
        // query; the two paths only diverge AFTER it (live: group_trust pushdown; historical:
        // V_CrcV2_TrustRelations join). A correctness fix to score-group selection or treasury-
        // override expansion applied to one and missed in the other would silently diverge live vs
        // historical (block-pinned) mint-limit math on the money path. Pin the head identical.
        const string headStart = "WITH latest_score_group AS (";
        const string headEnd = "LEFT JOIN treasury_overrides o ON o.aggregator = sg.treasury";
        var liveHead = Slice(ScoreGroupMintLimitReader.LiveGroupTokensSql, headStart, headEnd);
        var historicalHead = Slice(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, headStart, headEnd);

        Assert.That(liveHead, Is.Not.Empty, "Live builder must contain the shared head CTE block");
        Assert.That(historicalHead, Is.EqualTo(liveHead),
            "The latest_score_group/score_groups/treasury_overrides/effective_treasuries head must stay identical across the live builder and the historical query");
    }

    private static string TailFrom(string sql, string marker)
    {
        var index = sql.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? string.Empty : sql[index..];
    }

    // Substring from startMarker through the end of endMarker (inclusive); empty if either absent.
    private static string Slice(string sql, string startMarker, string endMarker)
    {
        var start = sql.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = sql.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : sql[start..(end + endMarker.Length)];
    }

    private static List<ScoreGroupMintLimitBaseRow> ParseBaseRows(QueryResponse response)
    {
        var rows = new List<ScoreGroupMintLimitBaseRow>();
        foreach (var row in response.Rows)
        {
            rows.Add(new ScoreGroupMintLimitBaseRow(
                Str(row[0]).ToLowerInvariant(),
                Str(row[1]).ToLowerInvariant(),
                Str(row[2]).ToLowerInvariant(),
                BigInteger.Parse(Str(row[3])),
                BigInteger.Parse(Str(row[4]))));
        }

        return rows;
    }

    private static string Str(object? v) => v?.ToString() ?? "0";
}
