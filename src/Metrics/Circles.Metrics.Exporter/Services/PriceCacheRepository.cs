using Npgsql;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Manages the crc_daily_prices table in postgres-gnosis.
/// Cache-through pattern: check PG first, fetch externally on miss, store for future lookups.
/// Same pattern as TrustRepository's trust_scores_history table.
/// </summary>
public class PriceCacheRepository
{
    private readonly string _connectionString;
    private readonly ILogger<PriceCacheRepository> _logger;
    private volatile bool _tableVerified;

    public PriceCacheRepository(string connectionString, ILogger<PriceCacheRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public record DailyPrice(
        DateOnly Date,
        double ScrcXdai,
        double ConvFactor,
        double DcrcXdai,
        double XdaiEur,
        double DcrcEur,
        string Source,
        DateTimeOffset FetchedAt);

    /// <summary>
    /// Gets the cached price for a specific date. Returns null if not cached.
    /// </summary>
    public async Task<DailyPrice?> GetAsync(DateOnly date, CancellationToken ct = default)
    {
        await EnsureTableExistsAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT date, scrc_xdai, conv_factor, dcrc_xdai, xdai_eur, dcrc_eur, source, fetched_at
            FROM crc_daily_prices
            WHERE date = @date
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new DailyPrice(
            Date: DateOnly.FromDateTime(reader.GetDateTime(0)),
            ScrcXdai: reader.GetDouble(1),
            ConvFactor: reader.GetDouble(2),
            DcrcXdai: reader.GetDouble(3),
            XdaiEur: reader.GetDouble(4),
            DcrcEur: reader.GetDouble(5),
            Source: reader.GetString(6),
            FetchedAt: reader.GetDateTime(7));
    }

    /// <summary>
    /// Inserts or updates the price for a specific date.
    /// </summary>
    public async Task UpsertAsync(DateOnly date, double scrcXdai, double convFactor,
        double dcrcXdai, double xdaiEur, double dcrcEur, string source, CancellationToken ct = default)
    {
        await EnsureTableExistsAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO crc_daily_prices (date, scrc_xdai, conv_factor, dcrc_xdai, xdai_eur, dcrc_eur, source, fetched_at)
            VALUES (@date, @scrc_xdai, @conv_factor, @dcrc_xdai, @xdai_eur, @dcrc_eur, @source, NOW())
            ON CONFLICT (date) DO UPDATE SET
                scrc_xdai = EXCLUDED.scrc_xdai,
                conv_factor = EXCLUDED.conv_factor,
                dcrc_xdai = EXCLUDED.dcrc_xdai,
                xdai_eur = EXCLUDED.xdai_eur,
                dcrc_eur = EXCLUDED.dcrc_eur,
                source = EXCLUDED.source,
                fetched_at = EXCLUDED.fetched_at
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("scrc_xdai", scrcXdai);
        cmd.Parameters.AddWithValue("conv_factor", convFactor);
        cmd.Parameters.AddWithValue("dcrc_xdai", dcrcXdai);
        cmd.Parameters.AddWithValue("xdai_eur", xdaiEur);
        cmd.Parameters.AddWithValue("dcrc_eur", dcrcEur);
        cmd.Parameters.AddWithValue("source", source);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureTableExistsAsync(CancellationToken ct)
    {
        if (_tableVerified) return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            CREATE TABLE IF NOT EXISTS crc_daily_prices (
                date         DATE PRIMARY KEY,
                scrc_xdai    DOUBLE PRECISION NOT NULL,
                conv_factor  DOUBLE PRECISION NOT NULL,
                dcrc_xdai    DOUBLE PRECISION NOT NULL,
                xdai_eur     DOUBLE PRECISION NOT NULL DEFAULT 0,
                dcrc_eur     DOUBLE PRECISION NOT NULL DEFAULT 0,
                source       TEXT NOT NULL,
                fetched_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        _tableVerified = true;

        _logger.LogInformation("crc_daily_prices table verified/created");
    }
}
