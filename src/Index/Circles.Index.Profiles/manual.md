## Operator Manual — **IpfsDownloader**

### 1 What this thing does (and why)

* **Goal:** for every CID that appears in the Postgres view `UpdateMetadataDigest`, fetch its JSON payload from IPFS **exactly once** and store it in `ipfs_files`.
* **Why you care:** downstream services read those JSON blobs; gaps break user-facing features.

The worker process:

1. Diffs `UpdateMetadataDigest` against `ipfs_files` and enqueues the missing CIDs in `ipfs_queue`.
2. Reserves ready rows atomically (`FOR UPDATE SKIP LOCKED`) and downloads them in parallel from a set of HTTP IPFS gateways.
3. Streams each response, applying a **size limit** (default 164 KiB) and a strict JSON parse check.
   *Too big* or *invalid JSON* → row is **BLACKLISTED** (permanent stop).
4. Retries all other failures with exponential-and-jitter back-off (capped, default 72 h).
5. Uses **one dedicated writer connection** to commit results, guaranteeing at-least-once delivery and crash-safety.

Queue state alone is sufficient to resume after crashes, host reboots, or Postgres fail-overs.

---

### 2 File layout

```
IpfsDownloader.cs      ← single-file worker
build.sh / Dockerfile  ← optional wrappers (if present in repo)
README.md              ← developer quick-start
```

No extra config files — everything is driven by environment variables (see below).

---

### 3 Configuration knobs

Set any of the following environment variables to override the built-in defaults — they are read **once** at process start.

| Env variable                | Default                  | Purpose / effect                                               |
| --------------------------- | ------------------------ | -------------------------------------------------------------- |
| `IPFS_MAX_PARALLELISM`      | **192**                  | Max simultaneous downloads (and sockets per gateway).          |
| `IPFS_HTTP_TIMEOUT_SEC`     | 1 s                      | Per-request timeout. Keep low; retries cover slow links.       |
| `IPFS_MAX_BACKOFF_SEC`      | 259 200 s (72 h)         | Upper cap for retry delay.                                     |
| `IPFS_MAX_DOWNLOAD_BYTES`   | **167 936 B** (164 KiB)  | Payloads larger than this are **BLACKLISTED** (never retried). |
| `IPFS_WRITER_BATCH_SIZE`    | 256                      | Number of queue rows flushed in one DB transaction.            |
| `IPFS_STATS_INTERVAL_SEC`   | 30 s                     | Log window for `[STATS]` lines.                                |
| `IPFS_ERROR_MAX_LEN`        | 1 024 chars              | Truncation limit for `last_error` in `ipfs_queue`.             |
| `IPFS_GATEWAYS`             | 3 comma-separated URLs   | Download round-robin order; add/remove gateways here.          |
| `IPFS_PG_CONNECTION_STRING` | local `postgres` DSN     | Usual Npgsql connection string.                                |
| *Derived* `ChannelCapacity` | `IPFS_MAX_PARALLELISM×8` | In-memory buffer between downloaders and writer.               |

Unset variables fall back to the shown defaults.

---

### 4 Database schema (auto-created)

```sql
CREATE TABLE ipfs_files (
    id  serial PRIMARY KEY,
    cid text UNIQUE NOT NULL,
    payload jsonb NOT NULL,
    downloaded_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE ipfs_queue (
    cid text PRIMARY KEY,
    status text NOT NULL,            -- PENDING | IN_PROGRESS | FAILED | COMPLETED | BLACKLISTED
    attempt_count int NOT NULL,
    next_retry timestamptz NOT NULL,
    last_error text,
    updated_at timestamptz NOT NULL
);

CREATE INDEX ipfs_queue_next_retry_status_idx
    ON ipfs_queue(next_retry, status);
```

The worker creates any missing tables/indexes on startup.

---

### 5 How to run

#### 5.1 Prerequisites

* .NET 8 SDK (or newer).
* Postgres 13+ reachable via `IPFS_PG_CONNECTION_STRING`.
* `UpdateMetadataDigest` view/table populated upstream.
* Outbound HTTPS access to all configured gateways.

#### 5.2 Quick start

```bash
# tweak whatever you like; these are just examples
export IPFS_MAX_PARALLELISM=256
export IPFS_GATEWAYS="https://gw1.example.com,https://gw2.example.com"
export IPFS_PG_CONNECTION_STRING="Host=db;Port=5432;Username=ipfs;Password=secret;Database=ipfs"

dotnet build -c Release
dotnet run   -c Release
```

Or bake into a container and launch under your orchestrator.

#### 5.3 Shutdown

Send **SIGTERM** or press **CTRL-C**:

1. Reservation loop stops.
2. Writer drains the channel and commits remaining rows.
3. `HttpClient`s are disposed; process exits.

No work is lost—`IN_PROGRESS` rows still in flight will be reset to `FAILED` after one minute by the *reaper* loop on the next start.

---

### 6 Reading the logs

Typical output:

```
[BOOT] queued 452 missing CIDs
[OK]   bafy…  2 488 B  32 ms
[WARN] download failed for bafy…: invalid JSON payload
[STATS] last 30s  ok=620  fail=11  MB=1  queued=90/1536
```

* **`[OK]`** – success, shows CID, bytes, wall-time.
* **`[WARN]`** – transient or blacklistable failure, recorded in `last_error`.
* **`[STATS]`** – window counters: `ok`, `fail`, megabytes downloaded, and channel fill (`queued=current/capacity`).
  If `queued ≈ capacity` the writer is the bottleneck; otherwise the gateways/DB are.

---

### 7 Failure modes & remedies

| Symptom / log fragment                              | Root cause / hint                             | Operator action                                                                     |
| --------------------------------------------------- | --------------------------------------------- | ----------------------------------------------------------------------------------- |
| Continuous `Payload too large` warnings             | Gateway serving > `IPFS_MAX_DOWNLOAD_BYTES`   | Raise the limit *if* payloads are legitimate; otherwise leave them BLACKLISTED.     |
| `invalid JSON payload` & BLACKLISTED spikes         | Gateway returns HTML/garbled data             | Investigate the gateway, keep payloads blacklisted.                                 |
| Channel stuck full (`queued=capacity`, `ok=fail=0`) | Writer can’t talk to Postgres                 | Check DB connectivity; the writer auto-reconnects, so recovery should be automatic. |
| Lots of rows **FAILED** quickly, same timeout error | `IPFS_HTTP_TIMEOUT_SEC` too aggressive        | Bump timeout (and maybe back-off cap) or investigate gateway latency.               |
| Table filling with stale `IN_PROGRESS` rows         | Process crashed mid-download, reaper disabled | Reaper loop runs every 30 s; if disabled, run the manual SQL reset (see below).     |

**Manual reset for stale rows**

```sql
UPDATE ipfs_queue
   SET status='FAILED', next_retry=now(), updated_at=now()
 WHERE status='IN_PROGRESS'
   AND updated_at < now() - interval '1 minute';
```

---

### 8 Operational tips

* **Horizontal scaling** — run multiple instances; the reservation query prevents double work.
* **Back-pressure tuning** — if writer saturates the channel, increase `IPFS_WRITER_BATCH_SIZE` or give Postgres more I/O.
* **Purging history** — it’s safe to delete old `COMPLETED` rows after N days; never delete `FAILED / BLACKLISTED` unless you explicitly want to retry them.
* **Monitoring** — alert on:

    * writer exceptions > 0 / min
    * queue length > `ChannelCapacity − 100` for more than 5 min
    * share of `BLACKLISTED` rows unexpectedly rising.

---

### 9 Extending / customising

| Desired change                           | Where to patch                                                       |
| ---------------------------------------- | -------------------------------------------------------------------- |
| Custom ENV names or types                | Top of `IpfsDownloader.cs` — the `GetEnv*` helpers.                  |
| Prometheus / OTLP metrics                | Add counters in `ProcessAsync`, `WriterLoopAsync`, `StatsLoopAsync`. |
| Store payloads in S3 instead of Postgres | Swap the `INSERT ipfs_files` block in `WriterLoopAsync`.             |
| Alternate back-off algorithm             | Edit `CalcBackoff()`.                                                |
| Different JSON validation rules          | Replace `JsonDocument.Parse` with your schema validator.             |

---

### 10 Quick reference

```
dotnet run -c Release                    # start
tail -f logs | grep STATS                # throughput snapshot
psql -c "SELECT status, COUNT(*) FROM ipfs_queue GROUP BY 1;"  # queue overview
kill -TERM <pid>                         # graceful stop
```

If the worker stops processing, check Postgres first, then the gateways; restart the process only if those are healthy.
