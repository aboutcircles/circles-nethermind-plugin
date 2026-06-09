using System.Data;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Connection helpers for CacheWarmupService.
/// Provides pooled readonly connections and transactional snapshot-bound connections so the
/// parallel warmup fan-out reads from a single consistent PostgreSQL snapshot.
/// </summary>
public partial class CacheWarmupService
{
    protected virtual async Task WithReadonlyConnectionAsync(
        Func<NpgsqlConnection, CancellationToken, Task> action,
        CancellationToken ct)
    {
        await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
        await action(conn, ct);
    }

    protected virtual async Task<WarmupSnapshotContext> CreateWarmupSnapshotAsync(CancellationToken ct)
    {
        var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        await using var cmd = new NpgsqlCommand("SELECT pg_export_snapshot()", conn, tx);
        var result = await cmd.ExecuteScalarAsync(ct);
        var snapshotId = result?.ToString();

        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw new InvalidOperationException("Failed to export PostgreSQL snapshot for warmup.");
        }

        return new WarmupSnapshotContext(conn, tx, snapshotId);
    }

    protected virtual async Task WithSnapshotReadonlyConnectionAsync(
        string snapshotId,
        Func<NpgsqlConnection, CancellationToken, Task> action,
        CancellationToken ct)
    {
        await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        // snapshotId comes from PostgreSQL pg_export_snapshot() (not user input).
        // PostgreSQL does not allow parameterization for SET TRANSACTION SNAPSHOT,
        // so we keep interpolation local and escape defensively.
        var escapedSnapshotId = snapshotId.Replace("'", "''");
        await using (var setSnapshotCmd = new NpgsqlCommand($"SET TRANSACTION SNAPSHOT '{escapedSnapshotId}'", conn, tx))
        {
            await setSnapshotCmd.ExecuteNonQueryAsync(ct);
        }

        await action(conn, ct);
        await tx.CommitAsync(ct);
    }
}
