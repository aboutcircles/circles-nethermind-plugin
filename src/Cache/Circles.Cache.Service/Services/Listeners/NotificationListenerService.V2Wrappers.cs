using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Per-block incremental processing for Circles V2 ERC20WrapperDeployed events.
/// Keyed by wrapper address (not avatar) to support avatars with multiple wrappers.
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string wrapperSql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper"", e.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" >= @fromBlock AND e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using (var wrapperCmd = new NpgsqlCommand(wrapperSql, conn))
        {
            wrapperCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            wrapperCmd.Parameters.AddWithValue("toBlock", toBlock);

            var count = 0;
            await using (var wrapperReader = await wrapperCmd.ExecuteReaderAsync(ct))
            {
                while (await wrapperReader.ReadAsync(ct))
                {
                    var blockNumber = wrapperReader.GetInt64(0);
                    var avatar = wrapperReader.GetString(1);
                    var erc20Wrapper = wrapperReader.GetString(2);
                    var circlesType = (CirclesType)wrapperReader.GetInt32(3);

                    // Key by wrapper address (not avatar) to support avatars with multiple wrappers
                    var wrapperKey = erc20Wrapper.ToLowerInvariant();
                    var avatarKey = avatar.ToLowerInvariant();

                    // Registration check: underlying avatar must be registered
                    if (_registrations.IsRegistered(avatarKey))
                    {
                        _caches.UpsertWrapper(blockNumber, wrapperKey, avatar, circlesType);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                _logger.LogDebug("Processed {Count} V2 ERC20 wrapper deployments", count);
            }
        }
    }
}
