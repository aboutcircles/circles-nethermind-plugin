// File: IpfsQueueDownloader.cs
// .NET 8 project – single‑file full implementation
// Dependencies: Npgsql >= 8.x  (dotnet add package Npgsql)
// Build:
//   dotnet build -c Release
// Run:
//   dotnet run -- <comma‑separated‑gateways> <pg‑connection> [cidListFile]
//
// Tables (automatically created):
// ┌────────────┐      ┌────────────┐
// │ ipfs_queue │←──┐ │ ipfs_files │
// └────────────┘   │ └────────────┘
// cid  PK           │ cid  UNIQUE
// status            │ payload jsonb
// attempt_count     └─ downloaded_at
// next_retry           (plus usual id serial)
// last_error
// updated_at
//
// Worker algorithm (safe for multi‑process):
// 1.  ReserveBatchAsync() – atomic UPDATE … RETURNING with FOR UPDATE SKIP LOCKED.
// 2.  Download & insert payload.
// 3a. On success → status COMPLETED.
// 3b. On fail    → status FAILED, attempt_count++, next_retry=now+backoff.
//
// A cron job (optional) can reset rows stuck IN_PROGRESS too long:
//   UPDATE ipfs_queue SET status='FAILED', next_retry=NOW()
//   WHERE status='IN_PROGRESS' AND updated_at < NOW() - INTERVAL '30 minutes';