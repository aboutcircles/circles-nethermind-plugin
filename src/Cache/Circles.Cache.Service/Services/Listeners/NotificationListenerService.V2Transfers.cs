using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Per-block incremental processing for Circles V2 transfer events.
/// Covers both the 1155 hub (TransferSingle + TransferBatch) and ERC20 wrapper transfers.
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV2TransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 TransferSingle and TransferBatch events together in block order
        const string sql = @"
            SELECT * FROM (
                SELECT
                    ""from"",
                    ""to"",
                    ""tokenAddress"",
                    value,
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    ""timestamp"",
                    'Single' as type
                FROM ""CrcV2_TransferSingle""
                WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock

                UNION ALL

                SELECT
                    ""from"",
                    ""to"",
                    ""tokenAddress"",
                    value,
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    ""timestamp"",
                    'Batch' as type
                FROM ""CrcV2_TransferBatch""
                WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ) AS combined
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
                var tokenAddress = reader.GetString(2).ToLowerInvariant();
                var valueBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);
                var timestamp = reader.GetInt64(7);

                // Convert from wei (18 decimals) to token units using CirclesConverter for proper precision
                decimal amount;
                try
                {
                    amount = CirclesConverter.AttoCirclesToCircles(valueBig);
                }
                catch (OverflowException)
                {
                    _logger.LogWarning("Skipping V2 transfer with value {Value} that would overflow decimal", valueBig);
                    continue;
                }

                if (blockNumber != currentBlock)
                {
                    if (currentBlock != -1)
                    {
                        // Flush balances for the previous block.
                        // Update secondary index BEFORE writing balance so concurrent reads
                        // see the token in the index before the balance appears (worst case:
                        // momentary 0-balance entry, not an invisible balance).
                        foreach (var kvp in currentBalances)
                        {
                            _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
                            _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                        }
                    }
                    currentBlock = blockNumber;
                }

                var fromLower = from.ToLowerInvariant();
                var toLower = to.ToLowerInvariant();

                // Update sender balance — token must be valid (account may be stopped but still holds tokens)
                if (from != "0x0000000000000000000000000000000000000000"
                    && CirclesInvariants.IsValidToken(tokenAddress, _registrations, _wrapperLookup))
                {
                    var fromKey = $"{fromLower}:{tokenAddress}";
                    if (!currentBalances.ContainsKey(fromKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var existingBalance);
                        currentBalances[fromKey] = existingBalance;
                    }
                    currentBalances[fromKey] -= amount;
                    _caches.V2LastActivity.Add(blockNumber, fromKey, timestamp);
                }

                // Update receiver balance — token must be valid
                if (to != "0x0000000000000000000000000000000000000000"
                    && CirclesInvariants.IsValidToken(tokenAddress, _registrations, _wrapperLookup))
                {
                    var toKey = $"{toLower}:{tokenAddress}";
                    if (!currentBalances.ContainsKey(toKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var existingBalance);
                        currentBalances[toKey] = existingBalance;
                    }
                    currentBalances[toKey] += amount;
                    _caches.V2LastActivity.Add(blockNumber, toKey, timestamp);
                }

                transferCount++;
            }
        }

        // Flush balances for the last block (index-before-balance ordering)
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
                _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} V2 transfer events", transferCount);
        }
    }

    private async Task ProcessV2Erc20WrapperTransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 ERC20 Wrapper Transfer events incrementally
        const string sql = @"
            SELECT ""from"", ""to"", ""tokenAddress"", amount, ""blockNumber"", ""timestamp""
            FROM ""CrcV2_Erc20WrapperTransfer""
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
                var timestamp = reader.GetInt64(5);

                // Convert from wei (18 decimals) to token units using CirclesConverter for proper precision
                decimal amount;
                try
                {
                    amount = CirclesConverter.AttoCirclesToCircles(amountBig);
                }
                catch (OverflowException)
                {
                    _logger.LogWarning("Skipping ERC20 wrapper transfer with amount {Amount} that would overflow decimal", amountBig);
                    continue;
                }

                var tokenKey = tokenAddress.ToLowerInvariant();

                if (blockNumber != currentBlock)
                {
                    if (currentBlock != -1)
                    {
                        // Flush balances for the previous block (index-before-balance ordering)
                        foreach (var kvp in currentBalances)
                        {
                            _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
                            _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                        }
                    }
                    currentBlock = blockNumber;
                }

                var fromLower = from.ToLowerInvariant();
                var toLower = to.ToLowerInvariant();

                // Update sender balance — token must be valid (account may be stopped)
                if (from != "0x0000000000000000000000000000000000000000"
                    && CirclesInvariants.IsValidToken(tokenKey, _registrations, _wrapperLookup))
                {
                    var fromKey = $"{fromLower}:{tokenKey}";
                    if (!currentBalances.ContainsKey(fromKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var existingBalance);
                        currentBalances[fromKey] = existingBalance;
                    }
                    currentBalances[fromKey] -= amount;
                    _caches.V2LastActivity.Add(blockNumber, fromKey, timestamp);
                }

                // Update receiver balance — token must be valid
                if (to != "0x0000000000000000000000000000000000000000"
                    && CirclesInvariants.IsValidToken(tokenKey, _registrations, _wrapperLookup))
                {
                    var toKey = $"{toLower}:{tokenKey}";
                    if (!currentBalances.ContainsKey(toKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var existingBalance);
                        currentBalances[toKey] = existingBalance;
                    }
                    currentBalances[toKey] += amount;
                    _caches.V2LastActivity.Add(blockNumber, toKey, timestamp);
                }

                transferCount++;
            }
        }

        // Flush balances for the last block (index-before-balance ordering)
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
                _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} ERC20 wrapper transfer events", transferCount);
        }
    }
}
