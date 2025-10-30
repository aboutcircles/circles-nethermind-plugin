using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Npgsql;

// Entry point ─────────────────────────────────────────────────────────────────
public static class IpfsDownloader
{
    // === Config ==============================================================
    private static readonly int MaxParallelism = GetEnvInt("IPFS_MAX_PARALLELISM", 192);
    private static readonly int HttpTimeoutSeconds = GetEnvInt("IPFS_HTTP_TIMEOUT_SEC", 1);
    private static readonly int MaxBackoffSeconds = GetEnvInt("IPFS_MAX_BACKOFF_SEC", 3600 * 72);
    private static readonly int StatsIntervalSec = GetEnvInt("IPFS_STATS_INTERVAL_SEC", 30);
    private static readonly int ErrorMaxLen = GetEnvInt("IPFS_ERROR_MAX_LEN", 1024);
    private static readonly int WriterBatchSize = GetEnvInt("IPFS_WRITER_BATCH_SIZE", 256);
    private static readonly long MaxDownloadBytes = GetEnvLong("IPFS_MAX_DOWNLOAD_BYTES", 164L * 1024);
    private static readonly int ChannelCapacity = MaxParallelism * 8;

    private static readonly string[] Gateways =
        Environment.GetEnvironmentVariable("IPFS_GATEWAYS") is { Length: > 0 } gwEnv
            ? gwEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["https://circles-profiles.myfilebase.com"];

    private static int GetEnvInt(string name, int @default) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var val) && val > 0
            ? val
            : @default;

    private static long GetEnvLong(string name, long @default) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var val) && val > 0
            ? val
            : @default;

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
                Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds)
            };
        }).ToArray();

    private static int _roundRobin;

    private static readonly Channel<PersistJob> PersistQueue =
        Channel.CreateBounded<PersistJob>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    private static long _okCount;
    private static long _failCount;
    private static long _bytesTotal;

    // === Main =================================================================
    public static async Task Main(CancellationToken ct, string connectionString)
    {
        Console.WriteLine("[BOOT] starting IPFS downloader");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        Task writerTask = Task.Run(
            () => WriterLoopAsync(PersistQueue.Reader, ct, connectionString),
            ct);

        Task reaperTask = Task.Run(
            () => ReaperLoopAsync(ct, connectionString),
            ct);

        Task statsTask = Task.Run(
            () => StatsLoopAsync(ct),
            ct);

        while (!ct.IsCancellationRequested)
        {
            List<QueueRow> batch;
            try
            {
                batch = await ReserveAsync(conn, MaxParallelism * 4);
            }
            catch (PostgresException pgEx) when (IsTransient(pgEx))
            {
                Console.WriteLine($"[WARN] transient DB error during reserve: {pgEx.Message}");
                await ReopenAsync(conn, ct);
                await Task.Delay(1000, ct);
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[WARN] reserve failed: {ex.Message}");
                await Task.Delay(1000, ct);
                continue;
            }

            if (batch.Count == 0)
            {
                await Task.Delay(100, ct);
                continue;
            }

            ParallelOptions opts = new()
            {
                MaxDegreeOfParallelism = MaxParallelism,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(batch, opts,
                async (row, ct) => { await ProcessAsync(row, ct); });
        }

        Console.WriteLine("[SHUTDOWN] cancelling…");
        PersistQueue.Writer.Complete();
        await Task.WhenAll(writerTask, statsTask, reaperTask);
        foreach (HttpClient c in _clients) c.Dispose();
        Console.WriteLine("[SHUTDOWN] done");
    }

    private static async Task ReaperLoopAsync(CancellationToken ct, string connectionString)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                const string reset = @"
                UPDATE ipfs_queue
                   SET status     = 'FAILED',
                       next_retry = NOW(),
                       updated_at = NOW()
                 WHERE status = 'IN_PROGRESS'
                   AND updated_at < NOW() - INTERVAL '1 minute';";
                await conn.ExecuteAsync(reset, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[REAPER] failed: {ex.Message}");
            }
        }
    }

    // === Work reservation =====================================================
    private static async Task<List<QueueRow>> ReserveAsync(
        NpgsqlConnection conn,
        int limit)
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
                    q.metadata_digest AS ""MetadataDigest"",
                    q.attempt_count AS ""AttemptCount"";
        ";

        IEnumerable<QueueRow> rows = await conn.QueryAsync<QueueRow>(sql, new { limit });
        return rows.ToList();
    }

    // === One download =========================================================
    private static async Task ProcessAsync(
        QueueRow row,
        CancellationToken ct)
    {
        bool success;
        bool blacklisted = false;
        string? json = null;
        string? error = null;
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            HttpClient client = NextClient();

            using HttpRequestMessage req = new(HttpMethod.Get, $"ipfs/{row.Cid}");
            using HttpResponseMessage resp =
                await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            resp.EnsureSuccessStatusCode();

            long? contentLength = resp.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > MaxDownloadBytes)
            {
                blacklisted = true;
                throw new InvalidOperationException(
                    $"Payload too large: {contentLength.Value} B > {MaxDownloadBytes} B");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();

            byte[] buffer = new byte[8192];
            int read;
            long total = 0;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    blacklisted = true;
                    throw new InvalidOperationException(
                        $"Payload exceeded limit {MaxDownloadBytes} B while streaming");
                }

                ms.Write(buffer, 0, read);
            }

            json = Encoding.UTF8.GetString(ms.ToArray());

            // -------- JSON validation --------
            try
            {
                JsonDocument.Parse(json);
                success = true;
            }
            catch (JsonException)
            {
                success = false;
                blacklisted = true;
                error = "invalid JSON payload";
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            success = false;
            error = ex.Message;
            Console.WriteLine($"[WARN] download failed for {row.Cid}: {error}");
        }
        finally
        {
            sw.Stop();
        }

        int nextAttempt = success ? row.AttemptCount : row.AttemptCount + 1;
        long sizeBytes = json is not null ? Encoding.UTF8.GetByteCount(json) : 0;

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
            new PersistJob(row.Cid, row.MetadataDigest, success, blacklisted, json, error, nextAttempt), ct);
    }

    private static HttpClient NextClient()
    {
        int index = Interlocked.Increment(ref _roundRobin);
        return _clients[index % _clients.Length];
    }

    // === Dedicated writer =====================================================
    private static async Task WriterLoopAsync(
        ChannelReader<PersistJob> reader,
        CancellationToken ct,
        string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        while (!ct.IsCancellationRequested && await reader.WaitToReadAsync(ct))
        {
            var batch = new List<PersistJob>(WriterBatchSize);
            while (batch.Count < WriterBatchSize && reader.TryRead(out var job))
            {
                batch.Add(job);
            }

            if (conn.FullState != System.Data.ConnectionState.Open)
                await ReopenAsync(conn, ct);

            try
            {
                await using var tx = await conn.BeginTransactionAsync(ct);

                // successes
                var completed = batch.Where(j => j.Success).ToList();
                if (completed.Count > 0)
                {
                    const string insertFile = @"
                        INSERT INTO ipfs_files (cid, metadata_digest, payload)
                        VALUES (@cid, @metadata_digest, @json::jsonb)
                        ON CONFLICT DO NOTHING;";
                    await conn.ExecuteAsync(insertFile,
                        completed.Select(j => new
                        {
                            cid = j.Cid,
                            metadata_digest = j.MetadataDigest,
                            json = j.Json
                        }), tx);

                    const string markDone = @"
                        UPDATE ipfs_queue
                           SET status     = 'COMPLETED',
                               updated_at = NOW()
                         WHERE cid = ANY(@cids);"; 
                    await conn.ExecuteAsync(markDone,
                        new { cids = completed.Select(j => j.Cid).ToArray() }, tx);
                }

                // blacklisted
                var blacklisted = batch.Where(j => j.Blacklisted).ToList();
                foreach (var job in blacklisted)
                {
                    string errorTrunc = Truncate(job.Error);
                    const string markBlack = @"
                        UPDATE ipfs_queue
                           SET status        = 'BLACKLISTED',
                               attempt_count = @nextAttempt,
                               last_error    = @error,
                               updated_at    = NOW()
                         WHERE cid = @cid;";
                    await conn.ExecuteAsync(markBlack, new
                    {
                        cid = job.Cid,
                        nextAttempt = job.NextAttempt,
                        error = errorTrunc
                    }, tx);
                }

                // regular failures
                var failed = batch.Where(j => !j.Success && !j.Blacklisted).ToList();
                foreach (var job in failed)
                {
                    TimeSpan backoff = CalcBackoff(job.NextAttempt);

                    string errorTrunc = Truncate(job.Error);
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
                        cid = job.Cid,
                        nextAttempt = job.NextAttempt,
                        error = errorTrunc,
                        backoff
                    }, tx);
                }

                await tx.CommitAsync(ct);
            }
            catch (PostgresException pgEx) when (IsTransient(pgEx))
            {
                Console.WriteLine($"[WARN] writer transient DB error: {pgEx.SqlState} – {pgEx.Message}");
                await ReopenAsync(conn, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[ERROR] writer crashed while persisting: {ex}");
            }
        }
    }

    // === Stats loop ===========================================================
    private static async Task StatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(StatsIntervalSec), ct);

            long ok = Interlocked.Exchange(ref _okCount, 0);
            long fail = Interlocked.Exchange(ref _failCount, 0);
            long mb = Interlocked.Exchange(ref _bytesTotal, 0) / 1_000_000;

            Console.WriteLine(
                $"[STATS] last {StatsIntervalSec}s  ok={ok}  fail={fail}  MB={mb}  queued={PersistQueue.Reader.Count}/{ChannelCapacity}");
        }
    }

    // === Helpers ==============================================================
    private static TimeSpan CalcBackoff(int attempt)
    {
        double baseSec = Math.Pow(2, Math.Min(attempt, 16));
        double jitterFactor = 0.5 + Random.Shared.NextDouble();
        double secs = baseSec * jitterFactor;

        return secs > MaxBackoffSeconds
            ? TimeSpan.FromSeconds(MaxBackoffSeconds)
            : TimeSpan.FromSeconds(secs);
    }

    private static bool IsTransient(PostgresException ex) =>
        ex.IsTransient ||
        (ex.SqlState is { Length: 5 } state && state.StartsWith("08", StringComparison.Ordinal));

    private static async Task ReopenAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            await conn.CloseAsync();
        }
        catch
        {
            /* ignore */
        }

        await conn.OpenAsync(ct);
    }

    private static string Truncate(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text!.Length > ErrorMaxLen
                ? text[..ErrorMaxLen]
                : text;

    // === Record types =========================================================
    private sealed record QueueRow(string Cid, byte[] MetadataDigest, int AttemptCount);

    private sealed record PersistJob(
        string Cid,
        byte[] MetadataDigest,
        bool Success,
        bool Blacklisted,
        string? Json,
        string? Error,
        int NextAttempt);
}