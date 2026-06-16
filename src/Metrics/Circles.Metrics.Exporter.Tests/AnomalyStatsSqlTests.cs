using Circles.Metrics.Exporter.Services;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace Circles.Metrics.Exporter.Tests;

/// <summary>
/// Regression guard for the <c>circles_rep_score_new_zero_24h</c> tier×cause split.
///
/// <see cref="RepScoreRepository.AnomalyStatsSql"/> partitions the old flat
/// "members whose score hit 0 in 24h (prev_score &gt; 0)" count into four buckets:
/// tier ∈ {significant, fringe} × cause ∈ {blacklist, trust_collapse}, where
/// significant means <c>prev_score*100 &gt;= @sig</c> (default @sig=0.1 i.e. prev_score
/// &gt;= 0.001) and cause comes from a LEFT JOIN on the <c>blacklist</c> table.
///
/// The load-bearing invariant is CONSERVATION: the four buckets must be disjoint and
/// exhaustive, so their sum equals the original <c>prev_score &gt; 0 AND score = 0</c>
/// count. This fixture runs the EXACT production SQL constant (no drift) against a real
/// Postgres and asserts the four counts, the conservation sum, the &gt;=/&lt; tier
/// boundary, the no-fan-out LEFT JOIN, and the @sig&lt;=0 robustness guard.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class AnomalyStatsSqlTests
{
    private const string Group = "score_group";

    private PostgreSqlContainer? _postgres;
    private NpgsqlDataSource? _dataSource;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        try
        {
            _postgres = new PostgreSqlBuilder("postgres:15-alpine").Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Docker/Postgres test container unavailable: {ex.Message}");
            return;
        }

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // Minimal shapes matching the columns AnomalyStatsSql touches. prev_score/score are
        // raw [0,1] fractions (the production scale; the query multiplies by 100), blacklist
        // keyed by a unique address. snapshot_at drives the 24h window.
        cmd.CommandText = @"
            CREATE TABLE rep_score_history (
                group_id       text,
                avatar         text,
                snapshot_at    timestamptz,
                prev_score     double precision,
                score          double precision,
                prev_is_member boolean);
            CREATE TABLE rep_score_state (group_id text, avatar text);
            CREATE TABLE blacklist (address text PRIMARY KEY);

            INSERT INTO blacklist(address) VALUES ('0xbl_sig'), ('0xbl_fringe'), ('0xnotinhistory');

            INSERT INTO rep_score_history(group_id, avatar, snapshot_at, prev_score, score, prev_is_member) VALUES
                -- significant (prev_score*100 >= 0.1) ---------------------------------------
                ('score_group','0xsig_trust',  now() - interval '1h', 0.00912, 0, true),  -- 0.912/100 -> sig_trust
                ('score_group','0xboundary',   now() - interval '1h', 0.001,   0, true),  -- exactly @sig (0.1) -> sig_trust (locks >=)
                ('score_group','0xbl_sig',     now() - interval '2h', 0.05,    0, true),  -- 5/100, blacklisted -> sig_blacklist
                -- fringe (0 < prev_score*100 < 0.1) -----------------------------------------
                ('score_group','0xfringe1',    now() - interval '3h', 8.4e-8,  0, true),  -- dust -> fringe_trust
                ('score_group','0xfringe2',    now() - interval '3h', 8.6e-4,  0, true),  -- 0.086/100 -> fringe_trust
                ('score_group','0xbl_fringe',  now() - interval '4h', 5.1e-5,  0, true),  -- dust, blacklisted -> fringe_blacklist
                -- MUST NOT count as new_zero ------------------------------------------------
                ('score_group','0xprev_zero',  now() - interval '1h', 0,       0, true),  -- prev_score not > 0
                ('score_group','0xdrop_only',  now() - interval '1h', 0.50, 0.20, true),  -- dropped, not to 0
                ('score_group','0xold',        now() - interval '30h',0.30,    0, true),  -- outside 24h window
                ('other_group','0xothergroup', now() - interval '1h', 0.30,    0, true);  -- different group_id";
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
        if (_postgres != null) await _postgres.DisposeAsync();
    }

    private async Task<(long sigBl, long sigTr, long fringeBl, long fringeTr)> RunSplitAsync(double sig)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(RepScoreRepository.AnomalyStatsSql, conn);
        cmd.Parameters.AddWithValue("groupId", Group);
        cmd.Parameters.AddWithValue("drop", 20.0);
        cmd.Parameters.AddWithValue("sig", sig);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True);
        return (
            reader.GetInt64(reader.GetOrdinal("new_zero_sig_blacklist")),
            reader.GetInt64(reader.GetOrdinal("new_zero_sig_trust")),
            reader.GetInt64(reader.GetOrdinal("new_zero_fringe_blacklist")),
            reader.GetInt64(reader.GetOrdinal("new_zero_fringe_trust")));
    }

    /// <summary>
    /// With the default @sig=0.1 the four buckets are exactly: sig_blacklist=1, sig_trust=2
    /// (the real score AND the boundary row), fringe_blacklist=1, fringe_trust=2. Their sum
    /// (6) equals the old flat <c>prev_score &gt; 0 AND score = 0</c> count — the rows that
    /// must NOT count (prev_score=0, a drop-not-to-zero, an out-of-window row, and a
    /// different group) are all excluded. The blacklist entry with no history row
    /// (0xnotinhistory) must not inflate any bucket (no LEFT JOIN fan-out).
    /// </summary>
    [Test]
    public async Task AnomalyStatsSql_PartitionsByTierAndCause_AndConserves()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var (sigBl, sigTr, fringeBl, fringeTr) = await RunSplitAsync(0.1);

        Assert.Multiple(() =>
        {
            Assert.That(sigBl, Is.EqualTo(1), "significant + blacklisted: 0xbl_sig");
            Assert.That(sigTr, Is.EqualTo(2), "significant + trust: 0xsig_trust and the boundary row 0xboundary");
            Assert.That(fringeBl, Is.EqualTo(1), "fringe + blacklisted: 0xbl_fringe");
            Assert.That(fringeTr, Is.EqualTo(2), "fringe + trust: 0xfringe1, 0xfringe2");
            // Conservation: the split must equal the original flat new-zero definition.
            Assert.That(sigBl + sigTr + fringeBl + fringeTr, Is.EqualTo(6),
                "the four buckets must be disjoint and exhaustive over prev_score>0 AND score=0");
        });
    }

    /// <summary>
    /// Robustness guard for the explicit <c>prev_score &gt; 0</c> predicate on the
    /// significant filters: even a misconfigured non-positive threshold must never pull a
    /// <c>prev_score = 0, score = 0</c> row into a bucket. With @sig=0 every positive
    /// prev_score becomes "significant", so all six real new-zeros land in the significant
    /// tier and fringe empties — but 0xprev_zero is still excluded everywhere, so
    /// conservation (sum == 6) holds.
    /// </summary>
    [Test]
    public async Task AnomalyStatsSql_WithNonPositiveThreshold_StillExcludesZeroPrevScore()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var (sigBl, sigTr, fringeBl, fringeTr) = await RunSplitAsync(0.0);

        Assert.Multiple(() =>
        {
            Assert.That(fringeBl + fringeTr, Is.EqualTo(0), "@sig=0 -> no fringe tier");
            Assert.That(sigBl, Is.EqualTo(2), "blacklisted positives: 0xbl_sig, 0xbl_fringe");
            Assert.That(sigTr, Is.EqualTo(4), "non-blacklisted positives: sig_trust, boundary, fringe1, fringe2");
            Assert.That(sigBl + sigTr + fringeBl + fringeTr, Is.EqualTo(6),
                "0xprev_zero (prev_score=0) must stay excluded even at @sig<=0");
        });
    }
}
