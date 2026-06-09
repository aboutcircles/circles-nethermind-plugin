using System.Numerics;
using Circles.Common.TestUtils;
using Npgsql;

namespace Circles.Common.Tests;

/// <summary>
/// Integration tests that verify the optimized BaseRowsSql in
/// <see cref="ScoreGroupMintLimitReader"/> produces numerically identical results
/// to the original view-based query that used V_CrcV2_BalancesByAccountAndToken
/// directly (before predicate-pushdown optimization).
///
/// Requires TEST_ENV_URL to be set; tests skip automatically when absent.
/// Block 46406058 is a real Gnosis block where the OffchainScoreBasedMintPolicy
/// score group was active (confirmed by the personal-mint regression scenario).
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class ScoreGroupBaseRowsQueryParityTests
{
    private const string ScoreGroupMintPolicy = "0x450d68272e43c4cab7cbc7faa37893a50fae9569";
    private const long TestBlock = 46_406_058;

    /// <summary>
    /// Original view-based SQL kept as the oracle reference. The optimized
    /// version inlines the view's logic; this constant lets us run both and
    /// compare results row-by-row.
    /// </summary>
    private const string ReferenceBaseRowsSql = """
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
            ON ts."tokenAddress" = gt.trusted_token;
        """;

    private static bool TestEnvAvailable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ENV_URL"));

    [Test]
    public async Task BaseRows_OptimizedQuery_MatchesViewBasedReference()
    {
        if (!TestEnvAvailable)
            Assert.Ignore("TEST_ENV_URL not set — skipping integration test");

        await using var session = await TestEnvironmentClient.CreateSessionAsync(TestBlock);
        var connStr = session.PostgresConnectionString
            ?? throw new InvalidOperationException("No postgres connection string from test environment");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Use a read-only transaction so both queries share the same snapshot and
        // NOW() is frozen to a single point in time.  Without this, a UTC day
        // boundary between the two statements would cause demurrage day indices
        // to differ, producing CurrentSupply divergences of more than ±1 per token.
        await using var tx = await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead);

        string[] policies = [ScoreGroupMintPolicy];
        var referenceRows = await RunReferenceQueryAsync(conn, tx, policies, TestBlock);
        var optimizedRows = ScoreGroupMintLimitReader.ReadBaseRows(
            conn, policies, commandTimeoutSeconds: 60, maxBlock: TestBlock,
            transaction: tx, groupAddressFilter: null, collateralTokenFilter: null,
            subTreasuryOverrides: null);

        await tx.RollbackAsync();

        // Build lookup keyed by (group, collateral)
        var refByKey = referenceRows.ToDictionary(
            r => (r.GroupAddress, r.CollateralToken),
            r => r);

        Assert.That(optimizedRows, Is.Not.Empty,
            $"Expected at least one score-group row at block {TestBlock}");
        Assert.That(optimizedRows.Count, Is.EqualTo(refByKey.Count),
            $"Row count mismatch: optimized={optimizedRows.Count} reference={refByKey.Count}");

        foreach (var opt in optimizedRows)
        {
            var key = (opt.GroupAddress, opt.CollateralToken);
            Assert.That(refByKey.ContainsKey(key), Is.True,
                $"Optimized row ({opt.GroupAddress}, {opt.CollateralToken}) missing from reference results");

            var @ref = refByKey[key];

            // Treasury balance: exact BigInteger match
            Assert.That(opt.TreasuryBalance, Is.EqualTo(@ref.TreasuryBalance),
                $"TreasuryBalance mismatch for ({opt.GroupAddress}, {opt.CollateralToken}): " +
                $"optimized={opt.TreasuryBalance} ref={@ref.TreasuryBalance}");

            // Current supply: demurrage uses NOW() which is snapshot-frozen within the
            // shared transaction, so both queries see the same day index.  Tolerate ±1
            // for any remaining integer-rounding differences in demurrage arithmetic.
            var supplyDelta = BigInteger.Abs(opt.CurrentSupply - @ref.CurrentSupply);
            Assert.That(supplyDelta <= BigInteger.One,
                $"CurrentSupply differs by {supplyDelta} for ({opt.GroupAddress}, {opt.CollateralToken}): " +
                $"optimized={opt.CurrentSupply} ref={@ref.CurrentSupply}");
        }
    }

    private static async Task<List<ScoreGroupMintLimitBaseRow>> RunReferenceQueryAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        string[] policies,
        long maxBlock)
    {
        var rows = new List<ScoreGroupMintLimitBaseRow>();
        await using var cmd = new NpgsqlCommand(ReferenceBaseRowsSql, conn, transaction);
        cmd.CommandTimeout = 120;
        cmd.Parameters.AddWithValue("scoreMintPolicies", policies);
        cmd.Parameters.AddWithValue("subTreasuryAggregators", Array.Empty<string>());
        cmd.Parameters.AddWithValue("subTreasuryLists", Array.Empty<string>());

        var maxBlockParam = cmd.Parameters.Add("maxBlock", NpgsqlTypes.NpgsqlDbType.Bigint);
        maxBlockParam.Value = maxBlock;

        var groupParam = cmd.Parameters.Add("groupAddressFilter", NpgsqlTypes.NpgsqlDbType.Text);
        groupParam.Value = DBNull.Value;

        var collateralParam = cmd.Parameters.Add("collateralTokenFilter", NpgsqlTypes.NpgsqlDbType.Text);
        collateralParam.Value = DBNull.Value;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ScoreGroupMintLimitBaseRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                BigInteger.Parse(reader.GetString(3)),
                BigInteger.Parse(reader.GetString(4))));
        }

        return rows;
    }
}
