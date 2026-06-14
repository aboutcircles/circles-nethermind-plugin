using System.Numerics;
using Circles.Common.TestUtils;
using NUnit.Framework;

namespace Circles.Common.Tests;

/// <summary>
/// Integration tests that verify <see cref="ScoreGroupMintLimitReader.BaseRowsSqlHistorical"/>
/// produces numerically identical results to the original view-based query that used
/// V_CrcV2_BalancesByAccountAndToken directly (before the predicate-pushdown optimisation).
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
                "@collateralTokenFilter::text IS NULL OR t.trustee = @collateralTokenFilter",
                "TRUE")
            .Replace(
                "@subTreasuryAggregators::text[]",
                "ARRAY[]::text[]")
            .Replace(
                "@subTreasuryLists::text[]",
                "ARRAY[]::text[]");

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
        // The C# dispatch (maxBlock.HasValue ? BaseRowsSqlHistorical : BaseRowsSql) is not
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
    }

    [Test]
    public void ReadBaseRows_LiveSql_HasOptimizedStructure()
    {
        // The live (maxBlock IS NULL) query carries all three optimizations from the
        // ScoreGroupMintLimits perf rewrite. These structural guards prevent a future edit
        // from silently reintroducing the O(n^2) / full-table-window patterns. Functional
        // equivalence was validated directly against staging (set + value parity vs the
        // original view-based query).
        var sql = ScoreGroupMintLimitReader.BaseRowsSql;

        // Fix A: trust resolved by pushing the truster filter into CrcV2_Trust before the
        // window, and avatar registration checked via an indexed EXISTS — not the views.
        Assert.That(sql, Does.Contain("group_trust"),
            "Live SQL must resolve trust via the pushed-down group_trust CTE");
        Assert.That(sql, Does.Not.Contain("\"V_CrcV2_TrustRelations\""),
            "Live SQL must not window the whole V_CrcV2_TrustRelations view");
        Assert.That(sql, Does.Not.Contain("\"V_CrcV2_Avatars\""),
            "Live SQL must not join the un-indexed V_CrcV2_Avatars view");

        // Fix B: treasury balances aggregated once via join, not a per-row subquery.
        Assert.That(sql, Does.Contain("treasury_balances"),
            "Live SQL must aggregate treasury balances via a join");
        Assert.That(sql, Does.Not.Contain("b.account = ANY(gt.treasuries)"),
            "Live SQL must not retain the per-row correlated treasury subquery");

        // Fix C: matview watermarks inlined as literals (replaced by ReadBaseRows) so the
        // planner uses the blockNumber indexes for the delta tail.
        Assert.That(sql, Does.Contain("__BALANCE_WM__"),
            "Live SQL must carry the balance-watermark literal placeholder");
        Assert.That(sql, Does.Contain("__AVATAR_WM__"),
            "Live SQL must carry the avatar-watermark literal placeholder");
    }

    [Test]
    public void ReadBaseRows_LiveSql_WatermarkPlaceholdersFullySubstituted()
    {
        // Guard Fix C's runtime substitution: ReadBaseRows replaces both placeholders with the
        // matview watermark integers. If a future rename leaves one placeholder behind, the
        // generated SQL would carry a literal "__…__" token → a Postgres parse error (or, worse,
        // a silently-dropped EXISTS branch feeding wrong tokens into a financial sum). Mirror the
        // exact .Replace() chain ReadBaseRows performs and assert no placeholder survives.
        var substituted = ScoreGroupMintLimitReader.BaseRowsSql
            .Replace("__BALANCE_WM__", "46000000")
            .Replace("__AVATAR_WM__", "46000001");

        Assert.That(substituted, Does.Not.Contain("__"),
            "After watermark substitution the live SQL must contain no residual '__…__' placeholder");
    }

    [Test]
    public void ReadBaseRows_SharedTreasuryTail_IdenticalAcrossLiveAndHistorical()
    {
        // Fix B's treasury aggregation + final projection is duplicated verbatim in BaseRowsSql
        // and BaseRowsSqlHistorical. A correctness fix to one tail that misses the other would
        // silently diverge live vs historical (block-pinned) mint-limit math. Pin the tails as
        // string-identical so any drift fails CI.
        const string tailMarker = "treasury_pairs AS (";
        var liveTail = TailFrom(ScoreGroupMintLimitReader.BaseRowsSql, tailMarker);
        var historicalTail = TailFrom(ScoreGroupMintLimitReader.BaseRowsSqlHistorical, tailMarker);

        Assert.That(liveTail, Is.Not.Empty, "Live SQL must contain the shared treasury tail");
        Assert.That(historicalTail, Is.EqualTo(liveTail),
            "The treasury_pairs/treasury_balances/final-SELECT tail must stay identical across the live and historical queries");
    }

    private static string TailFrom(string sql, string marker)
    {
        var index = sql.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? string.Empty : sql[index..];
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
