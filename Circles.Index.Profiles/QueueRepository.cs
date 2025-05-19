using Npgsql;

namespace Circles.Index.Profiles;

internal sealed class QueueRepository : IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;

    public QueueRepository(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public NpgsqlDataSource DataSource => _db;

    private const string Ddl = @"CREATE TABLE IF NOT EXISTS ipfs_files (
                                    id SERIAL PRIMARY KEY,
                                    cid TEXT UNIQUE NOT NULL,
                                    payload JSONB NOT NULL,
                                    downloaded_at TIMESTAMPTZ NOT NULL DEFAULT NOW());

                                 CREATE TABLE IF NOT EXISTS ipfs_queue (
                                    cid TEXT PRIMARY KEY,
                                    status TEXT NOT NULL,
                                    attempt_count INT NOT NULL,
                                    next_retry TIMESTAMPTZ NOT NULL,
                                    last_error TEXT,
                                    updated_at TIMESTAMPTZ NOT NULL);

                                ALTER TABLE public.ipfs_files
                                    ADD COLUMN name        text    GENERATED ALWAYS AS (payload ->> 'name')        STORED,
                                    ADD COLUMN description text    GENERATED ALWAYS AS (payload ->> 'description') STORED;

                                CREATE INDEX ipfs_files_name_trgm_idx
                                    ON public.ipfs_files
                                        USING gin (name gin_trgm_ops);

                                ALTER TABLE public.ipfs_files
                                    ADD COLUMN search_vec tsvector
                                        GENERATED ALWAYS AS (
                                            setweight(to_tsvector('simple', coalesce(payload ->> 'name',        '')), 'A')
                                                || setweight(to_tsvector('simple', coalesce(payload ->> 'description', '')), 'B')
                                            ) STORED;

                                CREATE INDEX ipfs_files_fts_idx
                                    ON public.ipfs_files
                                        USING gin (search_vec);
";

    public async Task EnsureSchemaAsync()
    {
        await using var cmd = _db.CreateCommand(Ddl);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task BulkInsertCidsAsync(IEnumerable<string> cids, CancellationToken ct)
    {
        const string sql = @"INSERT INTO ipfs_queue (cid,status,attempt_count,next_retry,updated_at)
                             SELECT cid,'PENDING',0,NOW(),NOW()
                               FROM UNNEST(@cids) cid
                             ON CONFLICT DO NOTHING;";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("cids", cids);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Atomically grabs up to <paramref name="batchSize"/> rows ready for work and marks them IN_PROGRESS.
    /// Uses FOR UPDATE SKIP LOCKED so multiple processes cooperate safely.
    /// </summary>
    public async Task<IReadOnlyList<QueueItem>> ReserveBatchAsync(int batchSize, CancellationToken ct)
    {
        const string sql = @"WITH sel AS (
                               SELECT cid,attempt_count
                                 FROM ipfs_queue
                                WHERE status IN ('PENDING','FAILED')
                                  AND next_retry <= NOW()
                                ORDER BY next_retry
                                LIMIT @limit
                                FOR UPDATE SKIP LOCKED)
                             UPDATE ipfs_queue q
                                SET status='IN_PROGRESS', updated_at=NOW()
                               FROM sel
                              WHERE q.cid = sel.cid
                             RETURNING q.cid, q.attempt_count;";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", batchSize);

        var list = new List<QueueItem>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            list.Add(new QueueItem(rdr.GetString(0), rdr.GetInt32(1)));

        return list;
    }

    public async Task MarkCompletedAsync(string cid, CancellationToken ct)
    {
        const string sql = "UPDATE ipfs_queue SET status='COMPLETED', updated_at=NOW() WHERE cid=$1;";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(cid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(string cid, int attempts, TimeSpan backoff, string error, CancellationToken ct)
    {
        const string sql = @"UPDATE ipfs_queue
                                SET status='FAILED',
                                    attempt_count=$2,
                                    last_error=$3,
                                    next_retry=NOW()+$4,
                                    updated_at=NOW()
                              WHERE cid=$1;";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(cid);
        cmd.Parameters.AddWithValue(attempts);
        cmd.Parameters.AddWithValue(error);
        cmd.Parameters.AddWithValue(backoff);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
    }
}