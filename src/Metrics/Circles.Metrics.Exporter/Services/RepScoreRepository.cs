using Npgsql;
using NpgsqlTypes;

namespace Circles.Metrics.Exporter.Services;

public class RepScoreRepository
{
    private readonly string _repScoreConn;
    private readonly string _circlesDbConn;
    private readonly string _groupId;
    private readonly double _highScoreThreshold;
    private readonly double _scoreDropThreshold;
    // Previous-score floor (0-100 scale) separating "significant" new-zero
    // transitions from fringe dust churn. Mirrors the _scoreDropThreshold pattern.
    private readonly double _newZeroSignificantThreshold;
    public double ScoreDropThreshold => _scoreDropThreshold;
    public double NewZeroSignificantThreshold => _newZeroSignificantThreshold;
    private readonly ILogger<RepScoreRepository> _logger;

    // Dust floor (0-100 scale) for the computed blacklist-leak count. A blacklisted
    // avatar should compute to exactly 0 (AA forces it), so anything above dust is a
    // genuine leak. Matches the 0.1 used by the score-distribution / new-zero logic.
    private const double ComputedLeakDustScore100 = 0.1;

    public RepScoreRepository(
        string repScoreConn,
        string circlesDbConn,
        string groupId,
        double highScoreThreshold,
        double scoreDropThreshold,
        double newZeroSignificantThreshold,
        ILogger<RepScoreRepository> logger)
    {
        _repScoreConn = repScoreConn;
        _circlesDbConn = circlesDbConn;
        _groupId = groupId;
        _highScoreThreshold = highScoreThreshold;
        _scoreDropThreshold = scoreDropThreshold;
        _newZeroSignificantThreshold = newZeroSignificantThreshold;
        _logger = logger;
    }

    // ===========================================
    // Blacklist Stats
    // ===========================================

    public record BlacklistStats(
        long Total,
        long MembersTotal,
        long MembersNonzeroScore,
        long MembersNonzeroComputedScore,
        long Additions24h,
        long Removals24h,
        double LastRefreshAgeSeconds);

    public async Task<BlacklistStats> GetBlacklistStatsAsync(CancellationToken ct = default)
    {
        const string sql = """
            WITH member_blacklist AS (
                SELECT
                    COUNT(DISTINCT s.avatar)                                  AS members_total,
                    COUNT(DISTINCT s.avatar) FILTER (WHERE s.score_uint > 0)  AS members_nonzero_score
                FROM rep_score_emitted_state s
                JOIN blacklist b ON b.address = s.avatar
                WHERE s.group_id = @groupId
            ),
            member_blacklist_computed AS (
                -- Leading indicator: blacklisted members whose COMPUTED (pre-publish)
                -- score is still above dust. AA forces a blacklisted avatar to 0 at
                -- compute time, so any row here is a genuine leak ~2h ahead of the
                -- emitted view. score is the [0,1] float, hence the *100 dust compare.
                SELECT COUNT(DISTINCT st.avatar) AS members_nonzero_computed
                FROM rep_score_state st
                JOIN blacklist b ON b.address = st.avatar
                WHERE st.group_id = @groupId
                  AND st.score * 100 > @dust
            ),
            refresh_stats AS (
                SELECT
                    COALESCE(EXTRACT(EPOCH FROM (NOW() - MAX(completed_at) FILTER (WHERE error IS NULL))), 999999) AS last_refresh_age,
                    COALESCE(SUM(added) FILTER (WHERE completed_at > NOW() - INTERVAL '24 hours' AND error IS NULL), 0) AS additions_24h,
                    COALESCE(SUM(removed) FILTER (WHERE completed_at > NOW() - INTERVAL '24 hours' AND error IS NULL), 0) AS removals_24h
                FROM blacklist_refresh_log
            )
            SELECT
                (SELECT COUNT(*) FROM blacklist) AS total,
                mb.members_total,
                mb.members_nonzero_score,
                mbc.members_nonzero_computed,
                rs.additions_24h,
                rs.removals_24h,
                rs.last_refresh_age
            FROM member_blacklist mb, member_blacklist_computed mbc, refresh_stats rs
            """;

        await using var conn = new NpgsqlConnection(_repScoreConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("groupId", _groupId);
        cmd.Parameters.AddWithValue("dust", ComputedLeakDustScore100);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new BlacklistStats(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetDouble(6));
        }

        return new BlacklistStats(0, 0, 0, 0, 0, 0, 999999);
    }

    // ===========================================
    // Score Distribution
    // ===========================================

    public record ScoreDistribution(
        long MemberCount,
        double Avg,
        double Median,
        double StdDev,
        double P25,
        double P75,
        double P90,
        long ZeroCount,
        double HighShare,
        double LastRefreshAgeSeconds,
        long Bucket0_10,
        long Bucket10_20,
        long Bucket20_30,
        long Bucket30_40,
        long Bucket40_50,
        long Bucket50_60,
        long Bucket60_70,
        long Bucket70_80,
        long Bucket80_90,
        long Bucket90_100);

    public async Task<ScoreDistribution> GetScoreDistributionAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                COUNT(*)                                                                      AS member_count,
                COALESCE(AVG(score) * 100, 0)                                                 AS avg,
                COALESCE(PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY score) * 100, 0)        AS median,
                COALESCE(STDDEV(score) * 100, 0)                                              AS stddev,
                COALESCE(PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY score) * 100, 0)        AS p25,
                COALESCE(PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY score) * 100, 0)        AS p75,
                COALESCE(PERCENTILE_CONT(0.90) WITHIN GROUP (ORDER BY score) * 100, 0)        AS p90,
                COUNT(*) FILTER (WHERE score = 0)                                              AS zero_count,
                COALESCE(COUNT(*) FILTER (WHERE score * 100 >= @high)::double precision
                    / NULLIF(COUNT(*), 0), 0)                                                  AS high_share,
                COALESCE(EXTRACT(EPOCH FROM (NOW() - MAX(computed_at))), 999999)               AS refresh_age,
                COUNT(*) FILTER (WHERE score * 100 >= 0   AND score * 100 < 10)   AS b0,
                COUNT(*) FILTER (WHERE score * 100 >= 10  AND score * 100 < 20)   AS b10,
                COUNT(*) FILTER (WHERE score * 100 >= 20  AND score * 100 < 30)   AS b20,
                COUNT(*) FILTER (WHERE score * 100 >= 30  AND score * 100 < 40)   AS b30,
                COUNT(*) FILTER (WHERE score * 100 >= 40  AND score * 100 < 50)   AS b40,
                COUNT(*) FILTER (WHERE score * 100 >= 50  AND score * 100 < 60)   AS b50,
                COUNT(*) FILTER (WHERE score * 100 >= 60  AND score * 100 < 70)   AS b60,
                COUNT(*) FILTER (WHERE score * 100 >= 70  AND score * 100 < 80)   AS b70,
                COUNT(*) FILTER (WHERE score * 100 >= 80  AND score * 100 < 90)   AS b80,
                COUNT(*) FILTER (WHERE score * 100 >= 90  AND score * 100 <= 100) AS b90
            FROM rep_score_state
            WHERE group_id = @groupId
            """;

        await using var conn = new NpgsqlConnection(_repScoreConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("groupId", _groupId);
        cmd.Parameters.AddWithValue("high", _highScoreThreshold);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new ScoreDistribution(
                reader.GetInt64(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetInt64(7),
                reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                reader.GetInt64(16),
                reader.GetInt64(17),
                reader.GetInt64(18),
                reader.GetInt64(19));
        }

        return new ScoreDistribution(0, 0, 0, 0, 0, 0, 0, 0, 0, 999999, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    // ===========================================
    // Anomaly Detection
    // ===========================================

    public record AnomalyStats(
        long Drops24h,
        long NewZeroSignificantBlacklist24h,
        long NewZeroSignificantTrust24h,
        long NewZeroFringeBlacklist24h,
        long NewZeroFringeTrust24h,
        long NewMembers24h,
        long LostMembers24h);

    // new_zero is split by previous-score tier (significant vs fringe dust) and by
    // cause (blacklisted vs not). The LEFT JOIN blacklist mirrors the direct
    // address match in GetBlacklistStatsAsync (no case normalisation — AA writes
    // blacklist.address and rep_score_history.avatar in the same representation).
    // blacklist.address is unique, so the join cannot fan out history rows.
    //
    // NOTE: is_blacklisted reflects CURRENT blacklist membership at scrape time, not
    // membership at the instant the score hit zero — so the `cause` split is a triage
    // hint, not an exact transition cause. It never changes the new-zero TOTAL (the
    // sum over cause that the alert fires on), only its attribution. If the blacklist
    // table is empty/unpopulated, every new-zero is attributed to trust_collapse.
    //
    // The `prev_score > 0` guard is on ALL FOUR new_zero filters (not just fringe) so
    // the significant+fringe partition stays disjoint AND exhaustive over the original
    // `prev_score > 0 AND score = 0` domain for ANY @sig — including a misconfigured
    // @sig <= 0, which would otherwise pull `prev_score = 0` rows into significant.
    //
    // Exposed as a const so Circles.Metrics.Exporter.Tests can run the EXACT production
    // SQL (no drift). Bind @groupId, @drop, @sig before executing.
    public const string AnomalyStatsSql = """
        WITH window_24h AS (
            SELECT h.avatar, h.prev_score, h.score, h.prev_is_member,
                   (b.address IS NOT NULL) AS is_blacklisted
            FROM rep_score_history h
            LEFT JOIN blacklist b ON b.address = h.avatar
            WHERE h.group_id = @groupId
              AND h.snapshot_at > NOW() - INTERVAL '24 hours'
        ),
        lost AS (
            SELECT COUNT(DISTINCT h.avatar) AS cnt
            FROM rep_score_history h
            LEFT JOIN rep_score_state s ON s.group_id = @groupId AND s.avatar = h.avatar
            WHERE h.group_id = @groupId
              AND h.snapshot_at > NOW() - INTERVAL '24 hours'
              AND h.prev_is_member = true
              AND s.avatar IS NULL
        )
        SELECT
            COUNT(*) FILTER (WHERE (prev_score - score) * 100 >= @drop AND prev_score > score)                                 AS drops_24h,
            COUNT(*) FILTER (WHERE prev_score > 0 AND prev_score * 100 >= @sig AND score = 0 AND is_blacklisted)               AS new_zero_sig_blacklist,
            COUNT(*) FILTER (WHERE prev_score > 0 AND prev_score * 100 >= @sig AND score = 0 AND NOT is_blacklisted)           AS new_zero_sig_trust,
            COUNT(*) FILTER (WHERE prev_score > 0 AND prev_score * 100 < @sig AND score = 0 AND is_blacklisted)                AS new_zero_fringe_blacklist,
            COUNT(*) FILTER (WHERE prev_score > 0 AND prev_score * 100 < @sig AND score = 0 AND NOT is_blacklisted)            AS new_zero_fringe_trust,
            COUNT(*) FILTER (WHERE prev_is_member = false OR prev_is_member IS NULL)                                           AS new_members_24h,
            (SELECT cnt FROM lost)                                                                                             AS lost_members_24h
        FROM window_24h
        """;

    public async Task<AnomalyStats> GetAnomalyStatsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_repScoreConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(AnomalyStatsSql, conn);
        cmd.Parameters.AddWithValue("groupId", _groupId);
        cmd.Parameters.AddWithValue("drop", _scoreDropThreshold);
        cmd.Parameters.AddWithValue("sig", _newZeroSignificantThreshold);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            // Named arguments: the four same-typed `long` NewZero* fields map positionally
            // to the SQL column order, so name them to make a transposition a compile error.
            return new AnomalyStats(
                Drops24h: reader.GetInt64(0),
                NewZeroSignificantBlacklist24h: reader.GetInt64(1),
                NewZeroSignificantTrust24h: reader.GetInt64(2),
                NewZeroFringeBlacklist24h: reader.GetInt64(3),
                NewZeroFringeTrust24h: reader.GetInt64(4),
                NewMembers24h: reader.GetInt64(5),
                LostMembers24h: reader.IsDBNull(6) ? 0 : reader.GetInt64(6));
        }

        return new AnomalyStats(0, 0, 0, 0, 0, 0, 0);
    }

    // ===========================================
    // Cross-DB Transfer Share
    // ===========================================

    public record TransferShareMetrics(
        double HighRepShare24h,
        double ZeroRepShare24h,
        double HighRepShare7d,
        double ZeroRepShare7d);

    public async Task<TransferShareMetrics> GetTransferSharesAsync(CancellationToken ct = default)
    {
        var (highRep, zeroRep) = await LoadMemberAddressBuckets(ct);

        if (highRep.Length == 0 && zeroRep.Length == 0)
            return new TransferShareMetrics(0, 0, 0, 0);

        const string sql = """
            SELECT
                -- 24h — timestamp is Unix epoch (bigint), not timestamptz
                COALESCE(SUM(CAST(value AS numeric)) FILTER (WHERE "from" = ANY(@high) AND "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400), 0)  AS high_24h,
                COALESCE(SUM(CAST(value AS numeric)) FILTER (WHERE "from" = ANY(@zero) AND "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400), 0)  AS zero_24h,
                COALESCE(SUM(CAST(value AS numeric)) FILTER (WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400), 0)                           AS total_24h,
                -- 7d
                COALESCE(SUM(CAST(value AS numeric)) FILTER (WHERE "from" = ANY(@high) AND "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800), 0) AS high_7d,
                COALESCE(SUM(CAST(value AS numeric)) FILTER (WHERE "from" = ANY(@zero) AND "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800), 0) AS zero_7d,
                COALESCE(SUM(CAST(value AS numeric)) FILTER (WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800), 0)                         AS total_7d
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800
            """;

        await using var conn = new NpgsqlConnection(_circlesDbConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("high", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = highRep });
        cmd.Parameters.Add(new NpgsqlParameter("zero", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = zeroRep });
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var high24h = reader.GetDecimal(0);
            var zero24h = reader.GetDecimal(1);
            var total24h = reader.GetDecimal(2);
            var high7d = reader.GetDecimal(3);
            var zero7d = reader.GetDecimal(4);
            var total7d = reader.GetDecimal(5);

            return new TransferShareMetrics(
                total24h > 0 ? (double)(high24h / total24h) : 0,
                total24h > 0 ? (double)(zero24h / total24h) : 0,
                total7d > 0 ? (double)(high7d / total7d) : 0,
                total7d > 0 ? (double)(zero7d / total7d) : 0);
        }

        return new TransferShareMetrics(0, 0, 0, 0);
    }

    private async Task<(string[] HighRep, string[] ZeroRep)> LoadMemberAddressBuckets(CancellationToken ct)
    {
        const string sql = """
            SELECT avatar, score FROM rep_score_state WHERE group_id = @groupId
            """;

        var highRep = new List<string>();
        var zeroRep = new List<string>();

        await using var conn = new NpgsqlConnection(_repScoreConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("groupId", _groupId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0).ToLowerInvariant();
            var score100 = (reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1)) * 100.0;
            if (score100 >= _highScoreThreshold)
                highRep.Add(avatar);
            else if (score100 < 0.1)
                zeroRep.Add(avatar);
        }

        return (highRep.ToArray(), zeroRep.ToArray());
    }
}
