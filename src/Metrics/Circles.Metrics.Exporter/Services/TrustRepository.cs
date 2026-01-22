using Npgsql;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Repository for querying trust score metrics from the analytics database.
/// Queries the trust_scores_current table which contains pre-computed trust scores.
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
            FROM trust_scores_current
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
            FROM trust_scores_current
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
            FROM trust_scores_current
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
                FROM trust_scores_current c
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
                FROM trust_scores_current c
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
            SELECT COUNT(*) FROM trust_scores_current t
            JOIN "CrcV2_RegisterHuman" r ON LOWER(t.avatar) = LOWER(r."avatar")
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

    public async Task<long> GetPenalizedAccountsAsync(CancellationToken ct = default)
    {
        // Accounts where penalty was applied (check JSON details)
        const string sql = """
            SELECT COUNT(*) FROM trust_scores_current
            WHERE details::jsonb->>'penalty_applied' = 'true'
            OR (details::jsonb->'penalties') IS NOT NULL
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
            FROM trust_scores_current
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
                LEFT JOIN trust_scores_current s ON LOWER(t."from") = LOWER(s.avatar)
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
            SELECT MAX(computed_at) FROM trust_scores_current
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);

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
}
