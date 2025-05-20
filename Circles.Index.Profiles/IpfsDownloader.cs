// ./IpfsDownloader.cs
// Usings ──────────────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Dapper;
using Npgsql;

// Entry point ─────────────────────────────────────────────────────────────────
public static class Program
{
    // === Config ==============================================================
    private const int MaxParallelism      = 160;
    private const int HttpTimeoutSeconds  = 1;
    private const int MaxBackoffSeconds   = 3600 * 24; // 24 h
    private const int StatsIntervalSec    = 30;
    private const int ErrorMaxLen         = 1024;
    private const int WriterBatchSize     = 256;

    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres";

    private static readonly string[] Gateways =
    {
        "https://evident-magenta-puma.myfilebase.com",
        "https://circles-profiles.myfilebase.com",
        "https://da08cae2-8b50-45dc-80b9-48925be78ec8.myfilebase.com"
    };

    // === Globals ==============================================================

    private static readonly HttpClient[] _clients =
        Gateways.Select(gw =>
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = MaxParallelism,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            return new HttpClient(handler)
            {
                BaseAddress = new Uri(gw, UriKind.Absolute),
                Timeout     = TimeSpan.FromSeconds(HttpTimeoutSeconds)
            };
        }).ToArray();

    private static int _roundRobin;

    private static readonly int ChannelCapacity = MaxParallelism * 8;

    private static readonly Channel<PersistJob> PersistQueue =
        Channel.CreateBounded<PersistJob>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode     = BoundedChannelFullMode.Wait
            });

    private static long _okCount;
    private static long _failCount;
    private static long _bytesTotal;

    // === Main =================================================================
    public static async Task Main()
    {
        Console.WriteLine("[BOOT] starting IPFS downloader");

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await EnsureSchemaAsync(conn);

        List<string> missing = await LoadMissingCidsAsync(conn);
        await EnqueueAsync(conn, missing);
        Console.WriteLine($"[BOOT] queued {missing.Count} missing CIDs");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Task writerTask = Task.Run(
            () => WriterLoopAsync(ConnectionString, PersistQueue.Reader, cts.Token),
            cts.Token);

        Task statsTask = Task.Run(
            () => StatsLoopAsync(cts.Token),
            cts.Token);

        while (!cts.IsCancellationRequested)
        {
            List<QueueRow> batch;
            try
            {
                batch = await ReserveAsync(conn, MaxParallelism * 4, cts.Token);
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                Console.WriteLine($"[WARN] reserve failed: {ex.Message}");
                await Task.Delay(1000, cts.Token);
                continue;
            }

            bool noWork = batch.Count == 0;
            if (noWork)
            {
                await Task.Delay(500, cts.Token);
                continue;
            }

            ParallelOptions opts = new()
            {
                MaxDegreeOfParallelism = MaxParallelism,
                CancellationToken      = cts.Token
            };

            await Parallel.ForEachAsync(batch, opts,
                async (row, ct) => { await ProcessAsync(row, ct); });
        }

        Console.WriteLine("[SHUTDOWN] cancelling…");
        PersistQueue.Writer.Complete();
        await Task.WhenAll(writerTask, statsTask);
        foreach (HttpClient c in _clients) c.Dispose();
        Console.WriteLine("[SHUTDOWN] done");
    }

    // === Helper: discover missing CIDs =======================================
    private static async Task<List<string>> LoadMissingCidsAsync(NpgsqlConnection conn)
    {
        const string sql = @"
            UPDATE ipfs_queue
               SET status = 'FAILED', next_retry = NOW()
             WHERE status = 'IN_PROGRESS'
               AND updated_at < NOW() - INTERVAL '1 minutes';

            WITH cids AS (
                SELECT cid FROM ""UpdateMetadataDigest""
            )
            SELECT c.cid
              FROM cids c
              LEFT JOIN ipfs_files f USING (cid)
             WHERE f.cid IS NULL;
        ";

        IEnumerable<string> rows = await conn.QueryAsync<string>(sql);
        return rows.ToList();
    }

    // === Helper: enqueue CIDs =================================================
    private static async Task EnqueueAsync(NpgsqlConnection conn, IEnumerable<string> cids)
    {
        if (!cids.Any()) return;

        const string sql = @"
            INSERT INTO ipfs_queue (cid, status, attempt_count, next_retry, updated_at)
            SELECT cid, 'PENDING', 0, NOW(), NOW()
              FROM unnest(@cids) cid
            ON CONFLICT DO NOTHING;";

        await conn.ExecuteAsync(sql, new { cids });
    }

    // === DB bootstrap =========================================================
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        const string ddl = @"
            CREATE TABLE IF NOT EXISTS ipfs_files (
                id  SERIAL PRIMARY KEY,
                cid TEXT UNIQUE NOT NULL,
                payload JSONB NOT NULL,
                downloaded_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS ipfs_queue (
                cid TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                attempt_count INT NOT NULL,
                next_retry TIMESTAMPTZ NOT NULL,
                last_error TEXT,
                updated_at TIMESTAMPTZ NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ipfs_queue_next_retry_status_idx
                ON ipfs_queue(next_retry, status);
        ";

        await conn.ExecuteAsync(ddl);
    }

    // === Work reservation =====================================================
    private static async Task<List<QueueRow>> ReserveAsync(
        NpgsqlConnection conn,
        int limit,
        CancellationToken ct)
    {
        const string sql = @"
            WITH pick AS (
                SELECT cid, attempt_count
                  FROM ipfs_queue
                 WHERE status IN ('PENDING','FAILED')
                   AND next_retry <= NOW()
                 ORDER BY next_retry
                 LIMIT @limit
                 FOR UPDATE SKIP LOCKED
            )
            UPDATE ipfs_queue q
               SET status     = 'IN_PROGRESS',
                   updated_at = NOW()
              FROM pick
             WHERE q.cid = pick.cid
          RETURNING q.cid AS ""Cid"",
                    q.attempt_count AS ""AttemptCount"";
        ";

        IEnumerable<QueueRow> rows =
            await conn.QueryAsync<QueueRow>(sql, new { limit });
        return rows.ToList();
    }

    // === One download =========================================================
    private static async Task ProcessAsync(
        QueueRow row,
        CancellationToken ct)
    {
        bool   success;
        string? json  = null;
        string? error = null;
        Stopwatch sw  = Stopwatch.StartNew();

        try
        {
            HttpClient client = NextClient();
            json    = await client.GetStringAsync($"ipfs/{row.Cid}", ct);
            success = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            success = false;
            error   = ex.Message;
            Console.WriteLine($"[WARN] download failed for {row.Cid}: {error}");
        }
        finally
        {
            sw.Stop();
        }

        int  nextAttempt = success ? row.AttemptCount : row.AttemptCount + 1;
        long sizeBytes   = json is not null ? Encoding.UTF8.GetByteCount(json) : 0;

        if (success)
        {
            Console.WriteLine($"[OK]   {row.Cid}  {sizeBytes} B  {sw.ElapsedMilliseconds} ms");
            Interlocked.Add(ref _bytesTotal, sizeBytes);
            Interlocked.Increment(ref _okCount);
        }
        else
        {
            Interlocked.Increment(ref _failCount);
        }

        await PersistQueue.Writer.WriteAsync(
            new PersistJob(row.Cid, success, json, error, nextAttempt), ct);
    }

    private static HttpClient NextClient()
    {
        int index = Interlocked.Increment(ref _roundRobin);
        return _clients[index % _clients.Length];
    }

    // === Dedicated writer =====================================================
    private static async Task WriterLoopAsync(
        string connString,
        ChannelReader<PersistJob> reader,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);

        await foreach (PersistJob firstJob in reader.ReadAllAsync(ct))
        {
            var batch = new List<PersistJob>(WriterBatchSize) { firstJob };

            while (batch.Count < WriterBatchSize && reader.TryRead(out PersistJob? next))
            {
                batch.Add(next);
            }

            await using var tx = await conn.BeginTransactionAsync(ct);

            var completed = batch.Where(j => j.Success && j.Json is not null).ToList();
            if (completed.Count > 0)
            {
                const string insertFile = @"
                    INSERT INTO ipfs_files (cid, payload)
                    VALUES (@cid, @json::jsonb)
                    ON CONFLICT DO NOTHING;";
                await conn.ExecuteAsync(insertFile,
                    completed.Select(j => new { cid = j.Cid, json = j.Json }), tx);

                const string markDone = @"
                    UPDATE ipfs_queue
                       SET status     = 'COMPLETED',
                           updated_at = NOW()
                     WHERE cid = ANY(@cids);";
                await conn.ExecuteAsync(markDone,
                    new { cids = completed.Select(j => j.Cid).ToArray() }, tx);
            }

            var failed = batch.Where(j => !j.Success).ToList();
            foreach (PersistJob job in failed)
            {
                TimeSpan backoff = CalcBackoff(job.NextAttempt);

                string errorTrunc = string.IsNullOrEmpty(job.Error)
                    ? string.Empty
                    : job.Error!.Length > ErrorMaxLen
                        ? job.Error[..ErrorMaxLen]
                        : job.Error;

                const string markFail = @"
                    UPDATE ipfs_queue
                       SET status        = 'FAILED',
                           attempt_count = @nextAttempt,
                           last_error    = @error,
                           next_retry    = NOW() + @backoff,
                           updated_at    = NOW()
                     WHERE cid = @cid;";
                await conn.ExecuteAsync(markFail, new
                {
                    cid         = job.Cid,
                    nextAttempt = job.NextAttempt,
                    error       = errorTrunc,
                    backoff
                }, tx);
            }

            await tx.CommitAsync(ct);
        }
    }

    // === Stats loop ===========================================================
    private static async Task StatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(StatsIntervalSec), ct);

            long ok   = Interlocked.Exchange(ref _okCount,    0);
            long fail = Interlocked.Exchange(ref _failCount,  0);
            long mb   = Interlocked.Exchange(ref _bytesTotal, 0) / 1_000_000;

            Console.WriteLine(
                $"[STATS] last {StatsIntervalSec}s  ok={ok}  fail={fail}  MB={mb}  queued={PersistQueue.Reader.Count}/{ChannelCapacity}");
        }
    }

    // === Helpers ==============================================================
    private static TimeSpan CalcBackoff(int attempt)
    {
        double baseSec      = Math.Pow(2, Math.Min(attempt, 16));
        double jitterFactor = 0.5 + Random.Shared.NextDouble(); // 0.5 – 1.5
        double secs         = baseSec * jitterFactor;

        bool exceeds = secs > MaxBackoffSeconds;
        return exceeds
            ? TimeSpan.FromSeconds(MaxBackoffSeconds)
            : TimeSpan.FromSeconds(secs);
    }

    // === Record types =========================================================
    private sealed record QueueRow  (string Cid, int AttemptCount);
    private sealed record PersistJob(string Cid, bool Success,
                                     string? Json, string? Error, int NextAttempt);
}
