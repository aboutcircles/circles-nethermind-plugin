using Circles.Common.TestUtils;
using Circles.Pathfinder.Host.Data;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Parity tests for the HistoricalLoadGraph SQL templates against the live query semantics.
///
/// Background: the live loader (LoadGraph + *.sql resources) and the block-pinned historical
/// loader (HistoricalLoadGraph inline templates) are maintained in parallel and have drifted
/// before — historical graphs were missing the ERC20-wrapper trust-edge projection, all
/// wrapped ERC20 balances, and the trustee registered-avatars filter on group trust.
///
/// The integration tests execute BOTH forms through the block-pinned test-environment query
/// proxy (search_path = circles_at_block, circles.max_block_number = N), where live views
/// (V_CrcV2_TrustRelations, V_CrcV2_Avatars) resolve to their block-pinned twins — an
/// independent oracle for the historical templates' active_trust / registered_avatars CTEs.
/// Raw tables in the reference SQL carry explicit blockNumber pins (raw tables are not
/// twinned). Result sets must be identical.
///
/// The structure tests are DB-free drift guards that run in plain CI.
///
/// Requires TEST_ENV_URL; integration tests skip when absent.
/// Block 46406058: score group active, wrapper deployments + wrapped transfers present
/// (same fixture block as ScoreGroupBaseRowsQueryParityTests).
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("RequiresTestEnv")]
public sealed class HistoricalTemplateParityTests
{
    private const long TestBlock = 46_406_058;
    private const string StandardMintPolicy = "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";
    private const string ScoreMintPolicy = "0x450d68272e43c4cab7cbc7faa37893a50fae9569";

    private static string RegisteredAvatarsCte =>
        string.Format(HistoricalLoadGraph.RegisteredAvatarsCte, TestBlock);

    // The REAL templates with production string.Format substitution — not copies.
    private static string HistoricalTrustSql =>
        string.Format(HistoricalLoadGraph.TrustQueryTemplate, TestBlock, "", RegisteredAvatarsCte);

    private static string HistoricalBalanceSql =>
        string.Format(HistoricalLoadGraph.BalanceQueryTemplate, TestBlock, "", RegisteredAvatarsCte);

    private static string HistoricalGroupTrustSql =>
        string.Format(HistoricalLoadGraph.GroupTrustScoreAwareQueryTemplate, TestBlock, "", RegisteredAvatarsCte)
            .Replace("ANY(@scoreMintPolicies)", $"ANY(ARRAY['{ScoreMintPolicy}']::text[])")
            .Replace("LOWER(@mintPolicy)", $"LOWER('{StandardMintPolicy}')");

    // Live trustQuery.sql semantics: pinned view twins for trust/avatars, explicit pins on raw tables.
    private static readonly string ReferenceTrustSql = $"""
        SELECT t1.truster, t1.trustee FROM "V_CrcV2_TrustRelations" t1
        INNER JOIN "V_CrcV2_Avatars" a1 ON a1.avatar = t1.truster
        INNER JOIN "V_CrcV2_Avatars" a2 ON a2.avatar = t1.trustee
        LEFT JOIN "CrcV2_RegisterGroup" t2
            ON t2."group" = t1.truster AND t2."blockNumber" <= {TestBlock}
        WHERE t2."group" IS NULL

        UNION ALL

        SELECT t1.truster, w."erc20Wrapper" AS trustee FROM "V_CrcV2_TrustRelations" t1
        INNER JOIN "CrcV2_ERC20WrapperDeployed" w
            ON w.avatar = t1.trustee AND w."blockNumber" <= {TestBlock}
        INNER JOIN "V_CrcV2_Avatars" a1 ON a1.avatar = t1.truster
        INNER JOIN "V_CrcV2_Avatars" a2 ON a2.avatar = w.avatar
        LEFT JOIN "CrcV2_RegisterGroup" t3
            ON t3."group" = t1.truster AND t3."blockNumber" <= {TestBlock}
        WHERE t3."group" IS NULL
        """;

    // Live groupTrustQuery.sql semantics (score-aware): pinned view twins + pinned raw tables.
    private static readonly string ReferenceGroupTrustSql = $"""
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY(ARRAY['{ScoreMintPolicy}']::text[])
              AND "blockNumber" <= {TestBlock}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        supported_groups AS (
            SELECT g."group" AS group_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {TestBlock}
              AND (
                  g."mint" = LOWER('{StandardMintPolicy}')
                  OR (LOWER(g."mint") = ANY(ARRAY['{ScoreMintPolicy}']::text[]) AND sg.router_address IS NOT NULL)
              )
        )
        SELECT t.truster AS group_address, t.trustee AS trusted_token
        FROM "V_CrcV2_TrustRelations" t
        INNER JOIN supported_groups g ON g.group_address = t.truster
        INNER JOIN "V_CrcV2_Avatars" ra ON ra.avatar = t.trustee
        """;

    private static bool TestEnvAvailable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ENV_URL"));

    private static async Task<TestEnvironmentClient> CreatePinnedSessionAsync()
    {
        if (!TestEnvAvailable)
            Assert.Ignore("TEST_ENV_URL not set — skipping integration test");

        if (!await TestEnvironmentClient.BlockExistsAsync(TestBlock))
            Assert.Ignore($"Block {TestBlock} not indexed in test environment — skipping");

        return await TestEnvironmentClient.CreateSessionAsync(TestBlock);
    }

    private static async Task<long> CountAsync(TestEnvironmentClient session, string sql)
    {
        var response = await session.ExecuteQueryAsync(sql, maxRows: 10);
        Assert.That(response.Rows, Has.Count.EqualTo(1),
            $"Expected a single count row, SQL: {sql[..Math.Min(sql.Length, 120)]}…");
        return Convert.ToInt64(response.Rows[0][0]?.ToString());
    }

    private static async Task AssertSetParityAsync(
        TestEnvironmentClient session, string historicalSql, string referenceSql, string label)
    {
        var histCount = await CountAsync(session, $"SELECT count(*) FROM ({historicalSql}) h");
        var refCount = await CountAsync(session, $"SELECT count(*) FROM ({referenceSql}) r");

        Assert.That(histCount, Is.GreaterThan(0), $"{label}: historical query returned no rows at block {TestBlock}");
        Assert.That(histCount, Is.EqualTo(refCount),
            $"{label}: row count mismatch — historical={histCount} reference={refCount}");

        var histMinusRef = await CountAsync(session, $"""
            SELECT count(*) FROM (
                SELECT * FROM ({historicalSql}) h
                EXCEPT
                SELECT * FROM ({referenceSql}) r
            ) d
            """);
        Assert.That(histMinusRef, Is.EqualTo(0),
            $"{label}: {histMinusRef} rows in historical template missing from live reference");

        var refMinusHist = await CountAsync(session, $"""
            SELECT count(*) FROM (
                SELECT * FROM ({referenceSql}) r
                EXCEPT
                SELECT * FROM ({historicalSql}) h
            ) d
            """);
        Assert.That(refMinusHist, Is.EqualTo(0),
            $"{label}: {refMinusHist} rows in live reference missing from historical template");
    }

    [Test]
    public async Task TrustTemplate_MatchesLiveSemanticsAtPinnedBlock()
    {
        await using var session = await CreatePinnedSessionAsync();
        await AssertSetParityAsync(session, HistoricalTrustSql, ReferenceTrustSql, "trust");
    }

    [Test]
    public async Task TrustTemplate_ProducesWrapperTrustEdges()
    {
        await using var session = await CreatePinnedSessionAsync();
        var wrapperEdges = await CountAsync(session, $"""
            SELECT count(*) FROM ({HistoricalTrustSql}) t
            WHERE t.trustee IN (
                SELECT "erc20Wrapper" FROM "CrcV2_ERC20WrapperDeployed"
                WHERE "blockNumber" <= {TestBlock})
            """);
        Assert.That(wrapperEdges, Is.GreaterThan(0),
            $"Expected wrapper trust edges at block {TestBlock} — the wrapper UNION branch returned none");
    }

    [Test]
    public async Task BalanceTemplate_MatchesLiveSemanticsAtPinnedBlock()
    {
        await using var session = await CreatePinnedSessionAsync();

        // The native branch predates this fix and is compared as part of the full set; the
        // wrapped branches are the new surface. Both reference and historical aggregate from
        // pinned raw tables (no balance view twin exists), so this guards the port, not the
        // pinning principle — which the trust/groupTrust tests cover via the view twins.
        var referenceBalanceSql = $"""
            WITH {RegisteredAvatarsCte},
            static_wrapper_transfers AS (
                SELECT t."timestamp", t."tokenAddress", t."from", t."to", t."amount"
                FROM "CrcV2_Erc20WrapperTransfer" t
                JOIN "CrcV2_ERC20WrapperDeployed" d
                    ON d."circlesType" = 1 AND d."erc20Wrapper" = t."tokenAddress"
                   AND d."blockNumber" <= {TestBlock}
                JOIN registered_avatars a ON a.avatar = d.avatar
                WHERE t."blockNumber" <= {TestBlock}
            ), static_sum AS (
                SELECT sum(diff) AS balance, account, "tokenAddress", max("timestamp") AS last_ts,
                       true AS "isWrapped", 'static' AS "circlesType"
                FROM (
                    SELECT t."timestamp", t."tokenAddress", t."from" AS account, -t."amount" AS diff
                    FROM static_wrapper_transfers t
                    JOIN registered_avatars ra ON ra.avatar = t."from"
                    UNION ALL
                    SELECT t."timestamp", t."tokenAddress", t."to" AS account, t."amount" AS diff
                    FROM static_wrapper_transfers t
                    JOIN registered_avatars ra ON ra.avatar = t."to"
                ) AS t
                GROUP BY account, "tokenAddress"
            ), demurraged_wrapper_transfers AS (
                SELECT t."timestamp", t."tokenAddress", t."from", t."to", t."amount"
                FROM "CrcV2_Erc20WrapperTransfer" t
                JOIN "CrcV2_ERC20WrapperDeployed" d
                    ON d."circlesType" = 0 AND d."erc20Wrapper" = t."tokenAddress"
                   AND d."blockNumber" <= {TestBlock}
                JOIN registered_avatars a ON a.avatar = d.avatar
                WHERE t."blockNumber" <= {TestBlock}
            ), demurraged_sum AS (
                SELECT sum(diff) AS balance, account, "tokenAddress", max("timestamp") AS last_ts,
                       true AS "isWrapped", 'demurraged' AS "circlesType"
                FROM (
                    SELECT t."timestamp", t."tokenAddress", t."from" AS account, -t."amount" AS diff
                    FROM demurraged_wrapper_transfers t
                    JOIN registered_avatars ra ON ra.avatar = t."from"
                    UNION ALL
                    SELECT t."timestamp", t."tokenAddress", t."to" AS account, t."amount" AS diff
                    FROM demurraged_wrapper_transfers t
                    JOIN registered_avatars ra ON ra.avatar = t."to"
                ) AS t
                GROUP BY account, "tokenAddress"
            ), native_tx AS (
                SELECT "timestamp", "from" AS account, "tokenAddress", -value AS delta
                FROM "CrcV2_TransferSingle"
                WHERE "from" <> '0x0000000000000000000000000000000000000000' AND "blockNumber" <= {TestBlock}
                UNION ALL
                SELECT "timestamp", "to" AS account, "tokenAddress", value AS delta
                FROM "CrcV2_TransferSingle"
                WHERE "to" <> '0x0000000000000000000000000000000000000000' AND "blockNumber" <= {TestBlock}
                UNION ALL
                SELECT "timestamp", "from" AS account, "tokenAddress", -value AS delta
                FROM "CrcV2_TransferBatch"
                WHERE "from" <> '0x0000000000000000000000000000000000000000' AND "blockNumber" <= {TestBlock}
                UNION ALL
                SELECT "timestamp", "to" AS account, "tokenAddress", value AS delta
                FROM "CrcV2_TransferBatch"
                WHERE "to" <> '0x0000000000000000000000000000000000000000' AND "blockNumber" <= {TestBlock}
            ), native_agg AS (
                SELECT account, "tokenAddress", sum(delta) AS balance, max("timestamp") AS last_ts
                FROM native_tx
                GROUP BY account, "tokenAddress"
                HAVING sum(delta) > 0
            ), native_sum AS (
                SELECT balance, n.account, n."tokenAddress", n.last_ts,
                       false AS "isWrapped", 'demurraged' AS "circlesType"
                FROM native_agg n
                JOIN registered_avatars ra ON ra.avatar = n.account
                JOIN registered_avatars ra_token ON ra_token.avatar = n."tokenAddress"
            )
            SELECT balance::text, account, "tokenAddress", last_ts AS "lastActivity",
                   "isWrapped", "circlesType"
            FROM (
                SELECT * FROM static_sum
                UNION ALL
                SELECT * FROM demurraged_sum
                UNION ALL
                SELECT * FROM native_sum
            ) all_balances
            WHERE balance > 0
            """;

        await AssertSetParityAsync(session, HistoricalBalanceSql, referenceBalanceSql, "balance");
    }

    [Test]
    public async Task BalanceTemplate_ProducesWrappedBalances()
    {
        await using var session = await CreatePinnedSessionAsync();
        var wrappedRows = await CountAsync(session,
            $"""SELECT count(*) FROM ({HistoricalBalanceSql}) b WHERE b."isWrapped" """);
        Assert.That(wrappedRows, Is.GreaterThan(0),
            $"Expected wrapped ERC20 balance rows at block {TestBlock} — the wrapper branches returned none");
    }

    [Test]
    public async Task GroupTrustTemplate_MatchesLiveSemanticsAtPinnedBlock()
    {
        await using var session = await CreatePinnedSessionAsync();
        Assert.That(HistoricalGroupTrustSql, Does.Not.Contain("@"),
            "SQL parameter substitution incomplete — a .Replace() pattern no longer matches the template");
        await AssertSetParityAsync(session, HistoricalGroupTrustSql, ReferenceGroupTrustSql, "groupTrust");
    }

    // ── DB-free structure guards (run in plain CI) ───────────────────────────

    [Test]
    public void TrustTemplate_ContainsWrapperUnionBranch()
    {
        Assert.That(HistoricalLoadGraph.TrustQueryTemplate, Does.Contain("CrcV2_ERC20WrapperDeployed"),
            "Trust template must project trust edges onto trustee ERC20 wrappers (live trustQuery.sql parity)");
        Assert.That(HistoricalLoadGraph.TrustQueryTemplate, Does.Contain("UNION ALL"));
    }

    [Test]
    public void BalanceTemplate_ContainsWrapperBranches()
    {
        Assert.That(HistoricalLoadGraph.BalanceQueryTemplate, Does.Contain("CrcV2_Erc20WrapperTransfer"),
            "Balance template must load wrapped ERC20 balances (live balanceQuery.sql parity)");
        Assert.That(HistoricalLoadGraph.BalanceQueryTemplate, Does.Contain("'static' AS \"circlesType\""),
            "Balance template must label static (circlesType=1) wrapper balances for C# inflationary conversion");
    }

    [Test]
    public void GroupTrustTemplates_FilterTrusteeRegistration()
    {
        Assert.That(HistoricalLoadGraph.GroupTrustQueryTemplate,
            Does.Contain("registered_avatars ra ON ra.avatar = t.trustee"),
            "Standard group-trust template must filter trustees to registered avatars (groupTrustQuery.fallback.sql parity)");
        Assert.That(HistoricalLoadGraph.GroupTrustScoreAwareQueryTemplate,
            Does.Contain("registered_avatars ra ON ra.avatar = t.trustee"),
            "Score-aware group-trust template must filter trustees to registered avatars (groupTrustQuery.sql parity)");
    }

    [Test]
    public void Templates_FormatCleanly_NoUnsubstitutedPlaceholders()
    {
        foreach (var (sql, label) in new[]
                 {
                     (HistoricalTrustSql, "trust"),
                     (HistoricalBalanceSql, "balance"),
                     (HistoricalGroupTrustSql, "groupTrust"),
                 })
        {
            Assert.That(sql, Does.Not.Contain("{0}"), $"{label}: unsubstituted block placeholder");
            Assert.That(sql, Does.Not.Contain("{2}"), $"{label}: unsubstituted CTE placeholder");
        }
    }
}
