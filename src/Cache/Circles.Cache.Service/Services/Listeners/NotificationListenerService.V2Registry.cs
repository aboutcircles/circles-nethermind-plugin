using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// V2 event dispatcher + per-block incremental processing for Circles V2 avatar/group registrations.
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV2EventsAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterHuman
        await ProcessV2RegisterHumanAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterOrganization
        await ProcessV2RegisterOrganizationAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterGroup
        await ProcessV2RegisterGroupAsync(conn, fromBlock, toBlock, ct);

        // Note: CrcV2_Stopped is NOT processed here — stop() only prevents minting,
        // it does not deregister the avatar. Stopped avatars remain in V2Avatars/Groups.

        // Process V2 Trust (for group memberships)
        await ProcessV2TrustAsync(conn, fromBlock, toBlock, ct);

        // Process V2 ERC20WrapperDeployed
        await ProcessV2Erc20WrapperDeployedAsync(conn, fromBlock, toBlock, ct);

        // Process V2 Transfers for balance updates — ERC1155 (TransferSingle + TransferBatch)
        // AND ERC20 wrapper transfers, merged into a single block-ordered pass so the shared
        // balance cache receives a single monotonic flush sequence (see V2Transfers.cs).
        await ProcessV2TransfersAsync(conn, fromBlock, toBlock, ct);

        // Process V2 UpdateMetadataDigest (for CID maps)
        await ProcessV2UpdateMetadataDigestAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterShortName (for short name mappings)
        await ProcessV2RegisterShortNameAsync(conn, fromBlock, toBlock, ct);

        // Process V2 SetAdvancedUsageFlag (for consented flow)
        await ProcessV2SetAdvancedUsageFlagAsync(conn, fromBlock, toBlock, ct);
    }

    private async Task ProcessV2RegisterHumanAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""avatar""
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var avatar = reader.GetString(2);

                var avatarKey = avatar.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, avatarKey, ("CrcV2_RegisterHuman", timestamp));
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 human registrations", count);
        }
    }

    private async Task ProcessV2RegisterOrganizationAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""organization""
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var organization = reader.GetString(2);

                var orgKey = organization.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, orgKey, ("CrcV2_RegisterOrganization", timestamp));
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 organization registrations", count);
        }
    }

    private async Task ProcessV2RegisterGroupAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint"", r.""symbol""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        var count = 0;
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("fromBlock", fromBlock);
            cmd.Parameters.AddWithValue("toBlock", toBlock);

            await using (var groupReader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await groupReader.ReadAsync(ct))
                {
                    var blockNumber = groupReader.GetInt64(0);
                    var group = groupReader.GetString(1);
                    var name = groupReader.GetString(2);
                    var mint = groupReader.GetString(3);
                    var symbol = groupReader.GetString(4);

                    var groupKey = group.ToLowerInvariant();

                    _caches.Groups.Add(blockNumber, groupKey, (name, mint, symbol));
                    count++;
                }
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 group registrations", count);
        }
    }
}
