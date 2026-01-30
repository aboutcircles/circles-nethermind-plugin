using Npgsql;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Repository for querying trust score metrics from the analytics database.
/// Queries the V_TrustScores_Current table which contains pre-computed trust scores.
/// </summary>
public class TrustRepository
{
    private readonly string _connectionString;
    private readonly ILogger<TrustRepository> _logger;

    public TrustRepository(string connectionString, ILogger<TrustRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ===========================================
    // Score Distribution
    // ===========================================

    public record ScoreDistribution(
        double Avg,
        double Median,
        double StdDev,
        double P75,
        double P90,
        double P99,
        double Min,
        double Max,
        long TotalCount
    );

    public async Task<ScoreDistribution> GetScoreDistributionAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                COALESCE(AVG(trust_score), 0) as avg,
                COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY trust_score), 0) as median,
                COALESCE(STDDEV(trust_score), 0) as stddev,
                COALESCE(PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY trust_score), 0) as p75,
                COALESCE(PERCENTILE_CONT(0.90) WITHIN GROUP (ORDER BY trust_score), 0) as p90,
                COALESCE(PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY trust_score), 0) as p99,
                COALESCE(MIN(trust_score), 0) as min,
                COALESCE(MAX(trust_score), 0) as max,
                COUNT(*) as total_count
            FROM "V_TrustScores_Current"
            WHERE trust_score IS NOT NULL
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new ScoreDistribution(
                reader.GetDouble(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetInt64(8)
            );
        }

        return new ScoreDistribution(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    // ===========================================
    // Trust Level Distribution
    // ===========================================

    public async Task<Dictionary<string, long>> GetTrustLevelCountsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT trust_level, COUNT(*) as count
            FROM "V_TrustScores_Current"
            GROUP BY trust_level
            """;

        var results = new Dictionary<string, long>
        {
            ["VERY_HIGH"] = 0,
            ["HIGH"] = 0,
            ["MEDIUM"] = 0,
            ["LOW"] = 0,
            ["VERY_LOW"] = 0,
            ["UNKNOWN"] = 0
        };

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var level = reader.GetString(0);
            var count = reader.GetInt64(1);
            if (results.ContainsKey(level))
            {
                results[level] = count;
            }
        }

        return results;
    }

    // ===========================================
    // Confidence Metrics
    // ===========================================

    public record ConfidenceMetrics(double Avg, double Median, long LowConfidenceCount);

    public async Task<ConfidenceMetrics> GetConfidenceMetricsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                COALESCE(AVG(confidence), 0) as avg,
                COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY confidence), 0) as median,
                COUNT(*) FILTER (WHERE confidence < 0.5) as low_confidence_count
            FROM "V_TrustScores_Current"
            WHERE confidence IS NOT NULL
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new ConfidenceMetrics(
                reader.GetDouble(0),
                reader.GetDouble(1),
                reader.GetInt64(2)
            );
        }

        return new ConfidenceMetrics(0, 0, 0);
    }

    // ===========================================
    // Trust Network Health
    // ===========================================

    public async Task<long> GetTrustVelocityAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_Trust"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND "expiryTime" > EXTRACT(EPOCH FROM NOW()) + 86400
            """;

        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetTrustChurnAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_Trust"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND "expiryTime" <= EXTRACT(EPOCH FROM NOW())
            """;

        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<double> GetTrustReciprocityRateAsync(CancellationToken ct = default)
    {
        // Count mutual trusts (A trusts B AND B trusts A)
        const string sql = """
            WITH trusts AS (
                SELECT "truster", "trustee" FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
            )
            SELECT
                CASE WHEN COUNT(*) = 0 THEN 0
                ELSE (COUNT(*) FILTER (WHERE EXISTS (
                    SELECT 1 FROM trusts t2 WHERE t2."truster" = t1."trustee" AND t2."trustee" = t1."truster"
                ))::float / COUNT(*)::float) * 100
                END as reciprocity_rate
            FROM trusts t1
            """;

        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetTrustGraphDensityAsync(CancellationToken ct = default)
    {
        // Density = actual edges / possible edges
        // Possible edges = n * (n-1) for directed graph without self-loops
        const string sql = """
            WITH stats AS (
                SELECT
                    COUNT(DISTINCT avatar) as node_count,
                    (SELECT COUNT(*) FROM "V_CrcV2_TrustRelations" WHERE "truster" != "trustee") as edge_count
                FROM (
                    SELECT "truster" as avatar FROM "V_CrcV2_TrustRelations"
                    UNION
                    SELECT "trustee" as avatar FROM "V_CrcV2_TrustRelations"
                ) nodes
            )
            SELECT
                CASE WHEN node_count < 2 THEN 0
                ELSE edge_count::float / (node_count * (node_count - 1))::float
                END as density
            FROM stats
            """;

        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetAvgOutDegreeAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(AVG(out_degree), 0) FROM (
                SELECT "truster", COUNT(*) as out_degree
                FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
                GROUP BY "truster"
            ) degrees
            """;

        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetAvgInDegreeAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(AVG(in_degree), 0) FROM (
                SELECT "trustee", COUNT(*) as in_degree
                FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
                GROUP BY "trustee"
            ) degrees
            """;

        return await ExecuteScalarAsync<double>(sql, ct);
    }

    // ===========================================
    // Anomaly Detection
    // ===========================================

    public async Task<long> GetScoreDropsAsync(TimeSpan window, int dropThreshold = 20, CancellationToken ct = default)
    {
        // Count accounts where score dropped significantly since last snapshot
        var sql = $"""
            SELECT COUNT(*) FROM (
                SELECT c.avatar,
                       c.trust_score as current_score,
                       h.trust_score as previous_score
                FROM "V_TrustScores_Current" c
                JOIN trust_scores_history h ON c.avatar = h.avatar
                WHERE h.snapshot_date = (
                    SELECT MAX(snapshot_date) FROM trust_scores_history
                    WHERE snapshot_date < NOW() - INTERVAL '{(int)window.TotalSeconds} seconds'
                )
                AND (h.trust_score - c.trust_score) > {dropThreshold}
            ) drops
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            // History table may not exist or have data
            return 0;
        }
    }

    public async Task<long> GetScoreSpikesAsync(TimeSpan window, int spikeThreshold = 30, CancellationToken ct = default)
    {
        // Count accounts where score increased suspiciously
        var sql = $"""
            SELECT COUNT(*) FROM (
                SELECT c.avatar,
                       c.trust_score as current_score,
                       h.trust_score as previous_score
                FROM "V_TrustScores_Current" c
                JOIN trust_scores_history h ON c.avatar = h.avatar
                WHERE h.snapshot_date = (
                    SELECT MAX(snapshot_date) FROM trust_scores_history
                    WHERE snapshot_date < NOW() - INTERVAL '{(int)window.TotalSeconds} seconds'
                )
                AND (c.trust_score - h.trust_score) > {spikeThreshold}
            ) spikes
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            // History table may not exist or have data
            return 0;
        }
    }

    public async Task<long> GetLowTrustNewAccountsAsync(TimeSpan window, CancellationToken ct = default)
    {
        // New accounts (registered in window) with LOW or VERY_LOW trust
        var sql = $"""
            SELECT COUNT(*) FROM "V_TrustScores_Current" t
            JOIN "CrcV2_RegisterHuman" r ON t.avatar = r."avatar"
            WHERE r."timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND t.trust_level IN ('LOW', 'VERY_LOW')
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    // ===========================================
    // Score Buckets
    // ===========================================

    public async Task<Dictionary<string, long>> GetScoreBucketsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                CASE
                    WHEN trust_score >= 90 THEN '90-100'
                    WHEN trust_score >= 80 THEN '80-90'
                    WHEN trust_score >= 70 THEN '70-80'
                    WHEN trust_score >= 60 THEN '60-70'
                    WHEN trust_score >= 50 THEN '50-60'
                    WHEN trust_score >= 40 THEN '40-50'
                    WHEN trust_score >= 30 THEN '30-40'
                    WHEN trust_score >= 20 THEN '20-30'
                    WHEN trust_score >= 10 THEN '10-20'
                    ELSE '0-10'
                END as bucket,
                COUNT(*) as count
            FROM "V_TrustScores_Current"
            WHERE trust_score IS NOT NULL
            GROUP BY bucket
            ORDER BY bucket
            """;

        var results = new Dictionary<string, long>
        {
            ["0-10"] = 0,
            ["10-20"] = 0,
            ["20-30"] = 0,
            ["30-40"] = 0,
            ["40-50"] = 0,
            ["50-60"] = 0,
            ["60-70"] = 0,
            ["70-80"] = 0,
            ["80-90"] = 0,
            ["90-100"] = 0
        };

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var bucket = reader.GetString(0);
            var count = reader.GetInt64(1);
            if (results.ContainsKey(bucket))
            {
                results[bucket] = count;
            }
        }

        return results;
    }

    // ===========================================
    // Economic-Trust Correlation
    // ===========================================

    public record TrustVolumeMetrics(
        double HighTrustVolume,
        double LowTrustVolume,
        double TrustWeightedVolume,
        double TotalVolume
    );

    public async Task<TrustVolumeMetrics> GetTrustVolumeMetricsAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            WITH transfers AS (
                SELECT
                    "from",
                    ("value"::float8) / 1e18 as amount
                FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            ),
            scored_transfers AS (
                SELECT
                    t.amount,
                    COALESCE(s.trust_score, 50) as score,
                    COALESCE(s.trust_level, 'MEDIUM') as level
                FROM transfers t
                LEFT JOIN "V_TrustScores_Current" s ON t."from" = s.avatar
            )
            SELECT
                COALESCE(SUM(amount) FILTER (WHERE level IN ('HIGH', 'VERY_HIGH')), 0) as high_trust_volume,
                COALESCE(SUM(amount) FILTER (WHERE level IN ('LOW', 'VERY_LOW')), 0) as low_trust_volume,
                COALESCE(SUM(amount * score / 100.0), 0) as weighted_volume,
                COALESCE(SUM(amount), 0) as total_volume
            FROM scored_transfers
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new TrustVolumeMetrics(
                reader.GetDouble(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3)
            );
        }

        return new TrustVolumeMetrics(0, 0, 0, 0);
    }

    // ===========================================
    // Timestamps
    // ===========================================

    public async Task<DateTimeOffset> GetLastComputeTimeAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT MAX(computed_at) FROM "V_TrustScores_Current"
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        // computed_at is bigint (Unix epoch seconds), not DateTime
        if (result is long epochSeconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }
        // Fallback for DateTime if schema changes
        if (result is DateTime dt)
        {
            return new DateTimeOffset(dt, TimeSpan.Zero);
        }

        return DateTimeOffset.MinValue;
    }

    public async Task<long> GetHistoryCountAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(DISTINCT avatar) FROM trust_scores_history
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    // ===========================================
    // Helper Methods
    // ===========================================

    private async Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result == null || result == DBNull.Value)
        {
            return default!;
        }

        return (T)Convert.ChangeType(result, typeof(T));
    }

    // ===========================================
    // BATCHED QUERIES (Optimized for fewer round-trips)
    // ===========================================

    /// <summary>
    /// Batched result combining score distribution, level counts, and score buckets.
    /// Replaces 3 separate queries with a single table scan.
    /// </summary>
    public record ScoreDistributionBatched(
        // Score stats
        double Avg, double Median, double StdDev, double P75, double P90, double P99,
        double Min, double Max, long TotalCount,
        // Level counts
        long LevelVeryHigh, long LevelHigh, long LevelMedium, long LevelLow, long LevelVeryLow, long LevelUnknown,
        // Score buckets
        long Bucket90_100, long Bucket80_90, long Bucket70_80, long Bucket60_70, long Bucket50_60,
        long Bucket40_50, long Bucket30_40, long Bucket20_30, long Bucket10_20, long Bucket0_10
    );

    /// <summary>
    /// Gets all score distribution metrics in a single query.
    /// Combines: GetScoreDistributionAsync + GetTrustLevelCountsAsync + GetScoreBucketsAsync
    /// </summary>
    public async Task<ScoreDistributionBatched> GetScoreDistributionBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                -- Score distribution stats
                COALESCE(AVG(trust_score), 0) as avg,
                COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY trust_score), 0) as median,
                COALESCE(STDDEV(trust_score), 0) as stddev,
                COALESCE(PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY trust_score), 0) as p75,
                COALESCE(PERCENTILE_CONT(0.90) WITHIN GROUP (ORDER BY trust_score), 0) as p90,
                COALESCE(PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY trust_score), 0) as p99,
                COALESCE(MIN(trust_score), 0) as min,
                COALESCE(MAX(trust_score), 0) as max,
                COUNT(*) as total_count,
                -- Trust level counts using FILTER
                COUNT(*) FILTER (WHERE trust_level = 'VERY_HIGH') as level_very_high,
                COUNT(*) FILTER (WHERE trust_level = 'HIGH') as level_high,
                COUNT(*) FILTER (WHERE trust_level = 'MEDIUM') as level_medium,
                COUNT(*) FILTER (WHERE trust_level = 'LOW') as level_low,
                COUNT(*) FILTER (WHERE trust_level = 'VERY_LOW') as level_very_low,
                COUNT(*) FILTER (WHERE trust_level NOT IN ('VERY_HIGH', 'HIGH', 'MEDIUM', 'LOW', 'VERY_LOW') OR trust_level IS NULL) as level_unknown,
                -- Score buckets
                COUNT(*) FILTER (WHERE trust_score >= 90) as bucket_90_100,
                COUNT(*) FILTER (WHERE trust_score >= 80 AND trust_score < 90) as bucket_80_90,
                COUNT(*) FILTER (WHERE trust_score >= 70 AND trust_score < 80) as bucket_70_80,
                COUNT(*) FILTER (WHERE trust_score >= 60 AND trust_score < 70) as bucket_60_70,
                COUNT(*) FILTER (WHERE trust_score >= 50 AND trust_score < 60) as bucket_50_60,
                COUNT(*) FILTER (WHERE trust_score >= 40 AND trust_score < 50) as bucket_40_50,
                COUNT(*) FILTER (WHERE trust_score >= 30 AND trust_score < 40) as bucket_30_40,
                COUNT(*) FILTER (WHERE trust_score >= 20 AND trust_score < 30) as bucket_20_30,
                COUNT(*) FILTER (WHERE trust_score >= 10 AND trust_score < 20) as bucket_10_20,
                COUNT(*) FILTER (WHERE trust_score >= 0 AND trust_score < 10) as bucket_0_10
            FROM "V_TrustScores_Current"
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new ScoreDistributionBatched(
                reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                reader.GetDouble(6), reader.GetDouble(7), reader.GetInt64(8),
                reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
                reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14),
                reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17),
                reader.GetInt64(18), reader.GetInt64(19), reader.GetInt64(20),
                reader.GetInt64(21), reader.GetInt64(22), reader.GetInt64(23),
                reader.GetInt64(24)
            );
        }

        return new ScoreDistributionBatched(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Batched result for network health metrics.
    /// </summary>
    public record NetworkHealthBatched(
        long Velocity24h, long Velocity7d, long Velocity30d,
        long Churn24h, long Churn7d, long Churn30d,
        double ReciprocityRate, double GraphDensity,
        double AvgOutDegree, double AvgInDegree
    );

    /// <summary>
    /// Gets all network health metrics in a single query.
    /// Combines: GetTrustVelocityAsync (x3) + GetTrustChurnAsync (x3) + reciprocity + density + degrees
    /// </summary>
    public async Task<NetworkHealthBatched> GetNetworkHealthBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            WITH
            now_ts AS (SELECT EXTRACT(EPOCH FROM NOW())::bigint as ts),
            velocity AS (
                SELECT
                    COUNT(*) FILTER (WHERE "timestamp" > (SELECT ts FROM now_ts) - 86400
                        AND "expiryTime" > (SELECT ts FROM now_ts) + 86400) as vel_24h,
                    COUNT(*) FILTER (WHERE "timestamp" > (SELECT ts FROM now_ts) - 604800
                        AND "expiryTime" > (SELECT ts FROM now_ts) + 86400) as vel_7d,
                    COUNT(*) FILTER (WHERE "timestamp" > (SELECT ts FROM now_ts) - 2592000
                        AND "expiryTime" > (SELECT ts FROM now_ts) + 86400) as vel_30d
                FROM "CrcV2_Trust"
            ),
            churn AS (
                SELECT
                    COUNT(*) FILTER (WHERE "timestamp" > (SELECT ts FROM now_ts) - 86400
                        AND "expiryTime" <= (SELECT ts FROM now_ts)) as churn_24h,
                    COUNT(*) FILTER (WHERE "timestamp" > (SELECT ts FROM now_ts) - 604800
                        AND "expiryTime" <= (SELECT ts FROM now_ts)) as churn_7d,
                    COUNT(*) FILTER (WHERE "timestamp" > (SELECT ts FROM now_ts) - 2592000
                        AND "expiryTime" <= (SELECT ts FROM now_ts)) as churn_30d
                FROM "CrcV2_Trust"
            ),
            trusts AS (
                SELECT "truster", "trustee" FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
            ),
            reciprocity AS (
                SELECT
                    CASE WHEN COUNT(*) = 0 THEN 0
                    ELSE (COUNT(*) FILTER (WHERE EXISTS (
                        SELECT 1 FROM trusts t2
                        WHERE t2."truster" = t1."trustee" AND t2."trustee" = t1."truster"
                    ))::float / COUNT(*)::float) * 100
                    END as rate
                FROM trusts t1
            ),
            graph_stats AS (
                SELECT
                    COUNT(DISTINCT avatar) as node_count,
                    (SELECT COUNT(*) FROM trusts) as edge_count
                FROM (
                    SELECT "truster" as avatar FROM trusts
                    UNION
                    SELECT "trustee" as avatar FROM trusts
                ) nodes
            ),
            density AS (
                SELECT
                    CASE WHEN node_count < 2 THEN 0
                    ELSE edge_count::float / (node_count * (node_count - 1))::float
                    END as density
                FROM graph_stats
            ),
            out_degrees AS (
                SELECT COALESCE(AVG(out_degree), 0) as avg FROM (
                    SELECT "truster", COUNT(*) as out_degree
                    FROM trusts GROUP BY "truster"
                ) d
            ),
            in_degrees AS (
                SELECT COALESCE(AVG(in_degree), 0) as avg FROM (
                    SELECT "trustee", COUNT(*) as in_degree
                    FROM trusts GROUP BY "trustee"
                ) d
            )
            SELECT
                v.vel_24h, v.vel_7d, v.vel_30d,
                c.churn_24h, c.churn_7d, c.churn_30d,
                r.rate, d.density,
                o.avg, i.avg
            FROM velocity v, churn c, reciprocity r, density d, out_degrees o, in_degrees i
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new NetworkHealthBatched(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetDouble(6), reader.GetDouble(7),
                reader.GetDouble(8), reader.GetDouble(9)
            );
        }

        return new NetworkHealthBatched(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Batched result for anomaly detection metrics.
    /// </summary>
    public record AnomalyDetectionBatched(
        long ScoreDrops24h, long ScoreDrops7d,
        long ScoreSpikes24h, long ScoreSpikes7d,
        long LowTrustNew24h, long LowTrustNew7d
    );

    /// <summary>
    /// Gets all anomaly detection metrics in a single query.
    /// Combines: GetScoreDropsAsync (x2) + GetScoreSpikesAsync (x2) + GetLowTrustNewAccountsAsync (x2)
    /// Note: History table queries are still separate due to potential table non-existence.
    /// </summary>
    public async Task<AnomalyDetectionBatched> GetAnomalyDetectionBatchedAsync(
        int dropThreshold = 20, int spikeThreshold = 30, CancellationToken ct = default)
    {
        // Check if history table exists first
        bool historyExists = await CheckTableExistsAsync("trust_scores_history", ct);

        long drops24h = 0, drops7d = 0, spikes24h = 0, spikes7d = 0;

        if (historyExists)
        {
            // Query historical comparisons in one batch
            var historySql = $"""
                WITH
                now_ts AS (SELECT NOW() as ts),
                snapshot_24h AS (
                    SELECT MAX(snapshot_date) as dt FROM trust_scores_history
                    WHERE snapshot_date < (SELECT ts FROM now_ts) - INTERVAL '1 day'
                ),
                snapshot_7d AS (
                    SELECT MAX(snapshot_date) as dt FROM trust_scores_history
                    WHERE snapshot_date < (SELECT ts FROM now_ts) - INTERVAL '7 days'
                ),
                changes_24h AS (
                    SELECT
                        COUNT(*) FILTER (WHERE (h.trust_score - c.trust_score) > {dropThreshold}) as drops,
                        COUNT(*) FILTER (WHERE (c.trust_score - h.trust_score) > {spikeThreshold}) as spikes
                    FROM "V_TrustScores_Current" c
                    JOIN trust_scores_history h ON c.avatar = h.avatar
                    WHERE h.snapshot_date = (SELECT dt FROM snapshot_24h)
                ),
                changes_7d AS (
                    SELECT
                        COUNT(*) FILTER (WHERE (h.trust_score - c.trust_score) > {dropThreshold}) as drops,
                        COUNT(*) FILTER (WHERE (c.trust_score - h.trust_score) > {spikeThreshold}) as spikes
                    FROM "V_TrustScores_Current" c
                    JOIN trust_scores_history h ON c.avatar = h.avatar
                    WHERE h.snapshot_date = (SELECT dt FROM snapshot_7d)
                )
                SELECT
                    COALESCE(c24.drops, 0), COALESCE(c7.drops, 0),
                    COALESCE(c24.spikes, 0), COALESCE(c7.spikes, 0)
                FROM changes_24h c24, changes_7d c7
                """;

            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(historySql, conn) { CommandTimeout = 60 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    drops24h = reader.GetInt64(0);
                    drops7d = reader.GetInt64(1);
                    spikes24h = reader.GetInt64(2);
                    spikes7d = reader.GetInt64(3);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query trust score history for anomaly detection");
            }
        }

        // Query low trust new accounts
        var sql = """
            WITH now_ts AS (SELECT EXTRACT(EPOCH FROM NOW())::bigint as ts)
            SELECT
                COUNT(*) FILTER (WHERE r."timestamp" > (SELECT ts FROM now_ts) - 86400) as low_new_24h,
                COUNT(*) FILTER (WHERE r."timestamp" > (SELECT ts FROM now_ts) - 604800) as low_new_7d
            FROM "V_TrustScores_Current" t
            JOIN "CrcV2_RegisterHuman" r ON t.avatar = r."avatar"
            WHERE t.trust_level IN ('LOW', 'VERY_LOW')
            """;

        long lowNew24h = 0, lowNew7d = 0;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                lowNew24h = reader.GetInt64(0);
                lowNew7d = reader.GetInt64(1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query low trust new accounts");
        }

        return new AnomalyDetectionBatched(
            drops24h, drops7d, spikes24h, spikes7d,
            lowNew24h, lowNew7d
        );
    }

    /// <summary>
    /// Batched result for economic-trust correlation metrics.
    /// </summary>
    public record EconomicCorrelationBatched(
        double HighTrustVolume24h, double LowTrustVolume24h, double WeightedVolume24h, double TotalVolume24h,
        double HighTrustVolume7d, double LowTrustVolume7d, double WeightedVolume7d, double TotalVolume7d
    );

    /// <summary>
    /// Gets all economic-trust correlation metrics in a single query.
    /// Combines: GetTrustVolumeMetricsAsync (x2 windows)
    /// </summary>
    public async Task<EconomicCorrelationBatched> GetEconomicCorrelationBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            WITH
            now_ts AS (SELECT EXTRACT(EPOCH FROM NOW())::bigint as ts),
            transfers_24h AS (
                SELECT
                    "from",
                    ("value"::float8) / 1e18 as amount
                FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > (SELECT ts FROM now_ts) - 86400
            ),
            transfers_7d AS (
                SELECT
                    "from",
                    ("value"::float8) / 1e18 as amount
                FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > (SELECT ts FROM now_ts) - 604800
            ),
            scored_24h AS (
                SELECT
                    t.amount,
                    COALESCE(s.trust_score, 50) as score,
                    COALESCE(s.trust_level, 'MEDIUM') as level
                FROM transfers_24h t
                LEFT JOIN "V_TrustScores_Current" s ON t."from" = s.avatar
            ),
            scored_7d AS (
                SELECT
                    t.amount,
                    COALESCE(s.trust_score, 50) as score,
                    COALESCE(s.trust_level, 'MEDIUM') as level
                FROM transfers_7d t
                LEFT JOIN "V_TrustScores_Current" s ON t."from" = s.avatar
            ),
            metrics_24h AS (
                SELECT
                    COALESCE(SUM(amount) FILTER (WHERE level IN ('HIGH', 'VERY_HIGH')), 0) as high_volume,
                    COALESCE(SUM(amount) FILTER (WHERE level IN ('LOW', 'VERY_LOW')), 0) as low_volume,
                    COALESCE(SUM(amount * score / 100.0), 0) as weighted_volume,
                    COALESCE(SUM(amount), 0) as total_volume
                FROM scored_24h
            ),
            metrics_7d AS (
                SELECT
                    COALESCE(SUM(amount) FILTER (WHERE level IN ('HIGH', 'VERY_HIGH')), 0) as high_volume,
                    COALESCE(SUM(amount) FILTER (WHERE level IN ('LOW', 'VERY_LOW')), 0) as low_volume,
                    COALESCE(SUM(amount * score / 100.0), 0) as weighted_volume,
                    COALESCE(SUM(amount), 0) as total_volume
                FROM scored_7d
            )
            SELECT
                m24.high_volume, m24.low_volume, m24.weighted_volume, m24.total_volume,
                m7.high_volume, m7.low_volume, m7.weighted_volume, m7.total_volume
            FROM metrics_24h m24, metrics_7d m7
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new EconomicCorrelationBatched(
                reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7)
            );
        }

        return new EconomicCorrelationBatched(0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Checks if a table exists in the database.
    /// </summary>
    private async Task<bool> CheckTableExistsAsync(string tableName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT FROM information_schema.tables
                WHERE table_name = @tableName
            )
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tableName", tableName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    // ===========================================
    // History Snapshot
    // ===========================================

    /// <summary>
    /// Creates a snapshot of current trust scores in the history table.
    /// Should be called once daily to enable anomaly detection metrics.
    /// Uses INSERT ... ON CONFLICT to handle duplicate snapshots gracefully.
    /// </summary>
    public async Task<int> CreateHistorySnapshotAsync(CancellationToken ct = default)
    {
        // Check if history table exists (created by DatabaseSchema)
        if (!await CheckTableExistsAsync("trust_scores_history", ct))
        {
            _logger.LogWarning("trust_scores_history table does not exist. Skipping snapshot.");
            return 0;
        }

        // Check if we already have a snapshot today (within last 23 hours)
        const string checkSql = """
            SELECT COUNT(*) FROM trust_scores_history
            WHERE snapshot_date > NOW() - INTERVAL '23 hours'
            """;

        var recentCount = await ExecuteScalarAsync<long>(checkSql, ct);
        if (recentCount > 0)
        {
            _logger.LogDebug("Recent snapshot already exists ({Count} records in last 23h). Skipping.", recentCount);
            return 0;
        }

        // Insert snapshot from current scores
        const string insertSql = """
            INSERT INTO trust_scores_history (avatar, trust_score, trust_level, confidence, snapshot_date)
            SELECT avatar, trust_score, trust_level, confidence, NOW()
            FROM "V_TrustScores_Current"
            WHERE avatar IS NOT NULL
            ON CONFLICT (avatar, snapshot_date) DO NOTHING
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(insertSql, conn) { CommandTimeout = 300 };
        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Created trust score history snapshot: {RowsAffected} records", rowsAffected);
        return rowsAffected;
    }

    /// <summary>
    /// Cleans up old history records beyond the retention period.
    /// Default retention: 90 days.
    /// </summary>
    public async Task<int> CleanupOldHistoryAsync(int retentionDays = 90, CancellationToken ct = default)
    {
        if (!await CheckTableExistsAsync("trust_scores_history", ct))
        {
            return 0;
        }

        var sql = $"""
            DELETE FROM trust_scores_history
            WHERE snapshot_date < NOW() - INTERVAL '{retentionDays} days'
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
        var rowsDeleted = await cmd.ExecuteNonQueryAsync(ct);

        if (rowsDeleted > 0)
        {
            _logger.LogInformation("Cleaned up {RowsDeleted} old history records (>{Days} days)", rowsDeleted, retentionDays);
        }

        return rowsDeleted;
    }
}
