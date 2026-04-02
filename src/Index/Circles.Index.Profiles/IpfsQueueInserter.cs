using Dapper;
using Npgsql;

/// <summary>
/// High-performance, thread-safe helper to bulk-insert CIDs into the ipfs_queue table.
/// </summary>
public static class IpfsQueueInserter
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? throw new Exception("Postgres connection (env var: POSTGRES_CONNECTION_STRING) string not found");

    private static readonly NpgsqlDataSource DataSource = NpgsqlDataSource.Create(ConnectionString);

    /// <summary>
    /// Fast path for a single CID.
    /// </summary>
    public static Task InsertAsync(string cid, CancellationToken ct = default) =>
        InsertAsync([cid], ct);

    /// <summary>
    /// Bulk-inserts CIDs. Uses one round-trip and lets Postgres de-dupe via
    /// <c>ON CONFLICT DO NOTHING</c>.
    /// </summary>
    public static async Task InsertAsync(IEnumerable<string> cids, CancellationToken ct = default)
    {
        string[] cidArray = (cids as string[]) ?? cids.ToArray();

        bool nothingToInsert = cidArray.Length == 0;
        if (nothingToInsert)
        {
            return;
        }

        const string sql = @"
            INSERT INTO ipfs_queue (cid, status, attempt_count, next_retry, updated_at)
            SELECT cid, 'PENDING', 0, NOW(), NOW()
              FROM unnest(@cids) cid
            ON CONFLICT DO NOTHING;";

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);

        // Dapper maps string[] → text[] automatically.
        await conn.ExecuteAsync(sql, new { cids = cidArray });
    }
}
