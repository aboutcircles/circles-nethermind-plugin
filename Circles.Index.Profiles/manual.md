## Operator Manual — **IpfsDownloader**

*(for commit with `MaxParallelism = 192`, `MaxDownloadBytes = 164 KiB`)*

---

### 1 What this thing does (and why)

* **Goal:** for every CID that appears in the Postgres view `UpdateMetadataDigest`, fetch its JSON payload from IPFS **exactly once** and store it in `ipfs_files`.
* **Why you care:** downstream services read those JSON blobs; gaps break user-facing features.

The worker process:

1. Diffs `UpdateMetadataDigest` against `ipfs_files` and enqueues the missing CIDs in `ipfs_queue`.
2. Reserves ready rows atomically (`FOR UPDATE SKIP LOCKED`) and downloads them in parallel from a set of HTTP IPFS gateways.
3. Streams each response, applying a **164 KiB size limit** and a strict JSON parse check.
   *Too big* or *invalid JSON* → row is **BLACKLISTED** (permanent stop).
4. Retries all other failures with exponential-and-jitter back-off (≤ 24 h).
5. Uses **one dedicated writer connection** to commit results, guaranteeing at-least-once delivery and crash-safety.

Queue state alone is sufficient to resume after crashes, host reboots, or Postgres fail-overs.

---

### 2 File layout

```
IpfsDownloader.cs      ← single-file worker
build.sh / Dockerfile  ← optional wrappers (if present in repo)
README.md              ← developer quick-start
```

No separate config files—the constants live at the top of `IpfsDownloader.cs`.

---

### 3 Configuration knobs

| Constant (in code)          | Default value                  | Purpose / effect                                                       |
| --------------------------- | ------------------------------ | ---------------------------------------------------------------------- |
| `MaxParallelism`            | **192**                        | Max simultaneous downloads (and sockets per gateway).                  |
| `HttpTimeoutSeconds`        | 1 s                            | Per-request timeout. Keep low; retries cover slow links.               |
| `MaxBackoffSeconds`         | 86 400 s (24 h)                | Upper cap for retry delay.                                             |
| `MaxDownloadBytes`          | **164 KiB**                    | Payloads larger than this are flagged **BLACKLISTED** (never retried). |
| `WriterBatchSize`           | 256 rows                       | Number of queue rows flushed in one DB transaction.                    |
| `StatsIntervalSec`          | 30 s                           | Log-window for `[STATS]` lines.                                        |
| `ErrorMaxLen`               | 1 024 chars                    | Truncation limit for `last_error` in `ipfs_queue`.                     |
| `Gateways[]`                | 3 URLs                         | Round-robin order; add/remove as needed.                               |
| `ConnectionString`          | see file                       | Usual Npgsql string.                                                   |
| *Derived* `ChannelCapacity` | `MaxParallelism × 8` (≈ 1 500) | In-memory buffer between downloaders and writer.                       |

To change anything at runtime, recompile or replace the constants with environment-variable look-ups.

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
* Postgres 13+ reachable via the `ConnectionString`.
* `UpdateMetadataDigest` view/table populated upstream.
* Outbound HTTPS access to all configured gateways.

#### 5.2 Quick start

```bash
dotnet build -c Release
dotnet run   -c Release
```

Or bake into a container and launch under your orchestrator.

#### 5.3 Shutdown

Send **SIGTERM** or press **CTRL-C**:

1. Reservation loop stops.
2. Writer drains the channel and commits remaining rows.
3. HttpClients are disposed; process exits.

No work is lost—`IN_PROGRESS` rows still in flight will be reset to `FAILED` after 1 minute by the *reaper* loop on the next start.

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

| Symptom / log fragment                              | Root cause / hint                             | Operator action                                                                          |
| --------------------------------------------------- | --------------------------------------------- | ---------------------------------------------------------------------------------------- |
| Continuous `Payload too large` warnings             | Gateway serving > 164 KiB payloads            | Raise `MaxDownloadBytes` *if* payloads are legitimate; otherwise leave them BLACKLISTED. |
| `invalid JSON payload` & BLACKLISTED spikes         | Gateway returns HTML/garbled data             | Investigate the gateway, keep payloads blacklisted.                                      |
| Channel stuck full (`queued=capacity`, `ok=fail=0`) | Writer can’t talk to Postgres                 | Check DB connectivity; the writer auto-reconnects, so recovery should be automatic.      |
| Lots of rows **FAILED** quickly, same timeout error | `HttpTimeoutSeconds` too aggressive           | Bump timeout (and maybe back-off cap) or investigate gateway latency.                    |
| Table filling with stale `IN_PROGRESS` rows         | Process crashed mid-download, reaper disabled | Reaper loop runs every 30 s; if disabled, run the manual SQL reset (see below).          |

**Manual reset for stale rows**

```sql
UPDATE ipfs_queue
   SET status='FAILED', next_retry=now(), updated_at=now()
 WHERE status='IN_PROGRESS'
   AND updated_at < now() - interval '1 minute';
```

---

### 8 Operational tips

* **Horizontal scaling** – run multiple instances; the reservation query prevents double work.
* **Back-pressure tuning** – if writer saturates the channel, increase `WriterBatchSize` or give Postgres more I/O.
* **Purging history** – it’s safe to delete old `COMPLETED` rows after N days; never delete `FAILED / BLACKLISTED` unless you explicitly want to retry them.
* **Monitoring** – alert on:

  * writer exceptions > 0/min
  * queue length > `ChannelCapacity - 100` for more than 5 min
  * share of `BLACKLISTED` rows unexpectedly rising.

---

### 9 Extending / customising

| Desired change                           | Where to patch                                                       |
| ---------------------------------------- | -------------------------------------------------------------------- |
| Runtime config via ENV                   | Replace `const` values with `Environment.GetEnvironmentVariable`.    |
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
