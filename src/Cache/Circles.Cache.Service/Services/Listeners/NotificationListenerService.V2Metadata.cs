using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Per-block incremental processing for Circles V2 metadata events:
/// UpdateMetadataDigest, RegisterShortName, SetAdvancedUsageFlag (consented flow).
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV2UpdateMetadataDigestAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 UpdateMetadataDigest events - update V2AvatarToCidMap cache
        const string sql = @"
            SELECT m.""blockNumber"", m.avatar, m.""metadataDigest""
            FROM ""CrcV2_UpdateMetadataDigest"" m
            WHERE m.""blockNumber"" >= @fromBlock AND m.""blockNumber"" <= @toBlock
            ORDER BY m.""blockNumber"", m.""transactionIndex"", m.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var avatar = reader.GetString(1);
                var metadataDigest = (byte[])reader.GetValue(2);

                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
                var key = avatar.ToLowerInvariant();

                _caches.V2AvatarToCidMap.Add(blockNumber, key, cid);

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 metadata digest updates", count);
        }
    }

    private async Task ProcessV2RegisterShortNameAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterShortName events - update V2AvatarToShortNameMap cache
        const string sql = @"
            SELECT s.""blockNumber"", s.avatar, s.""shortName""
            FROM ""CrcV2_RegisterShortName"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var avatar = reader.GetString(1);
                // shortName is stored as numeric (BigInteger) in database
                var shortNameNumeric = reader.GetFieldValue<BigInteger>(2);
                // Convert to Base58Btc format (like "zAlice")
                var shortNameBase58 = shortNameNumeric.ToBase58Btc();

                var key = avatar.ToLowerInvariant();

                _caches.V2AvatarToShortNameMap.Add(blockNumber, key, shortNameBase58);

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 short name registrations", count);
        }
    }

    private async Task ProcessV2SetAdvancedUsageFlagAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT f.""blockNumber"", f.avatar, f.flag
            FROM ""CrcV2_SetAdvancedUsageFlag"" f
            WHERE f.""blockNumber"" >= @fromBlock AND f.""blockNumber"" <= @toBlock
            ORDER BY f.""blockNumber"", f.""transactionIndex"", f.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var avatar = reader.GetString(1).ToLowerInvariant();
                var flag = (byte[])reader.GetValue(2);

                // Registration check: only store flags for registered avatars
                if (_registrations.IsRegistered(avatar))
                {
                    _caches.ConsentedFlowFlags.Add(blockNumber, avatar, flag);
                    count++;
                }
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 advanced usage flag updates", count);
        }
    }
}
