using System.Numerics;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Per-block incremental processing for Circles V2 Trust events.
/// Updates both V2TrustRelations and GroupMemberships (when truster is a group).
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV2TrustAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process all V2 Trust events - update both V2TrustRelations cache and GroupMemberships (when truster is a group)
        const string sql = @"
            SELECT
                t.""blockNumber"",
                t.""truster"",
                t.""trustee"",
                t.""expiryTime""
            FROM ""CrcV2_Trust"" t
            WHERE t.""blockNumber"" >= @fromBlock AND t.""blockNumber"" <= @toBlock
            ORDER BY t.""blockNumber"", t.""transactionIndex"", t.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var trustCount = 0;
        var membershipCount = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var truster = reader.GetString(1);
                var trustee = reader.GetString(2);
                var expiryTimeBig = reader.GetFieldValue<BigInteger>(3);

                var trusterKey = truster.ToLowerInvariant();
                var trusteeKey = trustee.ToLowerInvariant();

                // Safely cast expiryTime to long
                long expiryLong = expiryTimeBig > long.MaxValue ? long.MaxValue : (long)expiryTimeBig;

                // Registration check: both truster and trustee must be registered avatars.
                // Removals (expiryTime == 0) always proceed — safe to remove non-existent entries.
                var trusterRegistered = _registrations.IsRegistered(trusterKey);
                var trusteeRegistered = _registrations.IsRegistered(trusteeKey);

                if (expiryTimeBig == 0)
                {
                    _caches.RemoveV2Trust(blockNumber, trusterKey, trusteeKey);
                }
                else if (trusterRegistered && trusteeRegistered)
                {
                    _caches.UpsertV2Trust(blockNumber, trusterKey, trusteeKey, expiryLong);
                }
                trustCount++;

                // Also update GroupMemberships if truster is a group
                if (_caches.Groups.ContainsKey(trusterKey))
                {
                    if (expiryTimeBig == 0)
                    {
                        _caches.RemoveGroupMembership(blockNumber, trusterKey, trusteeKey);
                    }
                    else if (trusteeRegistered)
                    {
                        _caches.UpsertGroupMembership(blockNumber, trusterKey, trusteeKey, expiryLong);
                    }
                    membershipCount++;
                }
            }
        }

        if (trustCount > 0)
        {
            _logger.LogDebug("Processed {TrustCount} V2 trust events ({MembershipCount} group memberships)",
                trustCount, membershipCount);
        }
    }
}
