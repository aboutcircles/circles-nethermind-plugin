using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Per-block incremental processing for Circles V1 events: signups, transfers, trust, metadata.
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV1EventsAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Signups
        const string humanSignupSql = @"
            SELECT s.""blockNumber"", s.""user"", s.""token""
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var user = reader.GetString(1);
                var token = reader.GetString(2);

                var userKey = user.ToLowerInvariant();
                var tokenKey = token.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, userKey, ("CrcV1_Signup", token));
                _caches.V1TokenOwnerByToken.Add(blockNumber, tokenKey, user);
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 human signups", count);
        }

        // Process V1 Organization Signups
        const string orgSignupSql = @"
            SELECT o.""blockNumber"", o.""organization""
            FROM ""CrcV1_OrganizationSignup"" o
            WHERE o.""blockNumber"" >= @fromBlock AND o.""blockNumber"" <= @toBlock
            ORDER BY o.""blockNumber"", o.""transactionIndex"", o.""logIndex""";

        await using var orgCmd = new NpgsqlCommand(orgSignupSql, conn);
        orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
        orgCmd.Parameters.AddWithValue("toBlock", toBlock);

        var orgCount = 0;

        await using (var orgReader = await orgCmd.ExecuteReaderAsync(ct))
        {
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var organization = orgReader.GetString(1);

                var orgKey = organization.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, orgKey, ("CrcV1_OrganizationSignup", null));
                orgCount++;
            }
        }

        if (orgCount > 0)
        {
            _logger.LogDebug("Processed {Count} V1 organization signups", orgCount);
        }

        // Process V1 Transfers (for balance updates)
        await ProcessV1TransfersAsync(conn, fromBlock, toBlock, ct);

        // Process V1 Trust events
        await ProcessV1TrustAsync(conn, fromBlock, toBlock, ct);

        // Process V1 UpdateMetadataDigest (for CID maps)
        await ProcessV1UpdateMetadataDigestAsync(conn, fromBlock, toBlock, ct);
    }

    private async Task ProcessV1TransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 transfers incrementally
        const string sql = @"
            SELECT ""from"", ""to"", ""tokenAddress"", amount, ""blockNumber""
            FROM ""CrcV1_Transfer""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var transferCount = 0;
        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var amountBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);

                var tokenKey = tokenAddress.ToLowerInvariant();
                // Convert from wei (18 decimals) to token units using CirclesConverter for proper precision
                decimal value;
                try
                {
                    value = CirclesConverter.AttoCirclesToCircles(amountBig);
                }
                catch (OverflowException)
                {
                    _logger.LogWarning("Skipping V1 transfer with amount {Amount} that would overflow decimal", amountBig);
                    continue;
                }

                if (blockNumber != currentBlock)
                {
                    if (currentBlock != -1)
                    {
                        // Flush balances for the previous block (index-before-balance ordering)
                        foreach (var kvp in currentBalances)
                        {
                            _caches.UpdateBalanceIndex(kvp.Key, isV1: true, kvp.Value);
                            _caches.V1BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                        }
                    }
                    currentBlock = blockNumber;
                }

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(fromKey))
                    {
                        _caches.V1BalancesByAccountAndToken.TryGetValue(fromKey, out var existingBalance);
                        currentBalances[fromKey] = existingBalance;
                    }
                    currentBalances[fromKey] -= value;
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(toKey))
                    {
                        _caches.V1BalancesByAccountAndToken.TryGetValue(toKey, out var existingBalance);
                        currentBalances[toKey] = existingBalance;
                    }
                    currentBalances[toKey] += value;
                }

                transferCount++;
            }
        }

        // Flush balances for the last block (index-before-balance ordering)
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.UpdateBalanceIndex(kvp.Key, isV1: true, kvp.Value);
                _caches.V1BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} V1 transfer events", transferCount);
        }
    }

    private async Task ProcessV1TrustAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Trust events - update V1TrustRelations cache
        const string sql = @"
            SELECT t.""blockNumber"", t.""canSendTo"" as truster, t.""user"" as trustee, t.""limit""
            FROM ""CrcV1_Trust"" t
            WHERE t.""blockNumber"" >= @fromBlock AND t.""blockNumber"" <= @toBlock
            ORDER BY t.""blockNumber"", t.""transactionIndex"", t.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var truster = reader.GetString(1);
                var trustee = reader.GetString(2);
                var limitBig = reader.GetFieldValue<BigInteger>(3);
                long limitLong = limitBig > long.MaxValue ? long.MaxValue : (long)limitBig;

                var key = $"{truster.ToLowerInvariant()}:{trustee.ToLowerInvariant()}";

                // If limit is 0, remove the trust relation; otherwise add/update it
                if (limitBig == 0)
                {
                    _caches.RemoveV1Trust(blockNumber, truster, trustee);
                }
                else
                {
                    // V1 trust stores the trust limit (0-100)
                    _caches.UpsertV1Trust(blockNumber, truster, trustee, limitLong);
                }

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 trust events", count);
        }
    }

    private async Task ProcessV1UpdateMetadataDigestAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 UpdateMetadataDigest events - update V1AvatarToCidMap cache
        const string sql = @"
            SELECT m.""blockNumber"", m.avatar, m.""metadataDigest""
            FROM ""CrcV1_UpdateMetadataDigest"" m
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

                _caches.V1AvatarToCidMap.Add(blockNumber, key, cid);

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 metadata digest updates", count);
        }
    }
}
