using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Per-block incremental processing for Circles V2 transfer events.
/// Covers the 1155 hub (TransferSingle + TransferBatch) AND ERC20 wrapper transfers in a single
/// block-ordered pass.
///
/// These were previously two separate methods, each flushing per-block to the shared
/// V2BalancesByAccountAndToken cache. Because the cache (a RollbackCache) requires monotonically
/// non-decreasing block numbers across Add() calls, running the 1155 pass (advancing the cache to
/// its highest 1155 block) and then the wrapper pass (starting at its lowest wrapper block) would
/// throw "Block number must be monotonically increasing" whenever a wrapper transfer sat at a lower
/// block than the highest 1155 transfer in the same batch — a recurring fault for wrap/unwrap-heavy
/// avatars. Merging both sources into one block-ordered stream (mirroring the warmup builders)
/// produces a single monotonic flush sequence. ERC1155 balances are keyed by the avatar's token
/// address and wrapper balances by the wrapper contract address — disjoint namespaces — so unifying
/// the pass changes only the flush ordering, never the per-key arithmetic.
/// </summary>
public partial class NotificationListenerService
{
    private async Task ProcessV2TransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Combine ERC1155 (TransferSingle + TransferBatch) and ERC20 wrapper transfers into one
        // stream ordered by (blockNumber, transactionIndex, logIndex). tokenAddress is column index
        // 2 in every arm (1155 token owner / wrapper contract); the wrapper table's "amount" is
        // aliased to "value" so all three arms share the projection.
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
                    ""timestamp""
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
                    ""timestamp""
                FROM ""CrcV2_TransferBatch""
                WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock

                UNION ALL

                SELECT
                    ""from"",
                    ""to"",
                    ""tokenAddress"",
                    amount AS value,
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    ""timestamp""
                FROM ""CrcV2_Erc20WrapperTransfer""
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
                        // Flush balances for the previous block (atomic index+balance write).
                        foreach (var kvp in currentBalances)
                        {
                            _caches.UpsertBalance(currentBlock, kvp.Key, isV1: false, kvp.Value);
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

        // Flush balances for the last block (atomic index+balance write)
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.UpsertBalance(currentBlock, kvp.Key, isV1: false, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} V2 transfer events (ERC1155 + ERC20 wrapper)", transferCount);
        }
    }
}
