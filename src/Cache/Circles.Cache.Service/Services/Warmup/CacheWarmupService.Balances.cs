using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Balance loading for CacheWarmupService.
/// View-based fast path for V1, V2 (ERC1155), and ERC20 wrapper balances, plus the
/// transfer-replay fallback builders. Wrapper balances merge into the V2 cache via Seed()
/// to keep the RollbackCache invariant of one Seed per key per block.
/// </summary>
public partial class CacheWarmupService
{
    protected virtual async Task LoadBalancesAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading balances from pre-computed views (fast path)...");

        // Load V1 balances directly from the balance view
        await LoadV1BalancesFromViewAsync(conn, toBlock, ct);

        // Load V2 balances directly from the balance view
        await LoadV2BalancesFromViewAsync(conn, toBlock, ct);

        // Load ERC20 wrapper balances from view (or aggregated query)
        await LoadErc20WrapperBalancesFromViewAsync(conn, toBlock, ct);

        _logger.LogInformation("Balance loading completed");
    }

    /// <summary>
    /// Loads V1 balances directly from the pre-computed V_CrcV1_BalancesByAccountAndToken view.
    /// This is much faster than replaying all transfers since the view already aggregates them.
    /// </summary>
    private async Task LoadV1BalancesFromViewAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V1 balances from view...");

        // Build balances from transfers bounded to the warmup target block.
        const string sql = @"
            WITH account_balances AS (
                SELECT
                    account,
                    ""tokenAddress"",
                    SUM(delta) as balance
                FROM (
                    SELECT ""from"" AS account, ""tokenAddress"", -amount AS delta
                    FROM ""CrcV1_Transfer""
                    WHERE ""blockNumber"" <= @toBlock

                    UNION ALL

                    SELECT ""to"" AS account, ""tokenAddress"", amount AS delta
                    FROM ""CrcV1_Transfer""
                    WHERE ""blockNumber"" <= @toBlock
                ) t
                GROUP BY account, ""tokenAddress""
                HAVING SUM(delta) > 0
            )
            SELECT account, ""tokenAddress"", balance
            FROM account_balances
            WHERE account != '0x0000000000000000000000000000000000000000'";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300; // 5 minutes should be plenty
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var balances = new Dictionary<string, decimal>();
        var count = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 50000;

        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetString(0).ToLowerInvariant();
            var tokenAddress = reader.GetString(1).ToLowerInvariant();
            var totalBalanceBig = reader.GetFieldValue<BigInteger>(2);

            // Convert from atto (10^18) to decimal using CirclesConverter for proper precision
            decimal balance;
            try
            {
                balance = CirclesConverter.AttoCirclesToCircles(totalBalanceBig);
            }
            catch (OverflowException)
            {
                _logger.LogWarning("Skipping V1 balance with value {Value} that would overflow decimal", totalBalanceBig);
                continue;
            }
            if (balance > 0)
            {
                var key = $"{account}:{tokenAddress}";
                balances[key] = balance;
            }

            count++;
            if (count % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V1 balance loading progress: {Count} entries ({Rate:F0} entries/sec)",
                    count, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        // Seed the cache with all balances at once at the warmup target block
        _caches.V1BalancesByAccountAndToken.Seed(balances, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V1 balances from view", balances.Count);
    }

    /// <summary>
    /// Loads V2 balances directly from the pre-computed V_CrcV2_BalancesByAccountAndToken view.
    /// Uses totalBalance (not demurragedTotalBalance) for cache storage since demurrage is time-dependent.
    /// </summary>
    private async Task LoadV2BalancesFromViewAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V2 balances from view...");

        // Build balances from transfers bounded to the warmup target block.
        // Token-only filter: tokenAddress (= token owner) must be registered.
        // Account is NOT filtered — stopped avatars can still hold valid tokens.
        // (PathfinderGraphController applies the stricter IsValidBalance check at read time.)
        const string sql = @"
            WITH " + RegisteredAvatarsCte + @",
            account_balances AS (
                SELECT
                    account,
                    ""tokenAddress"",
                    SUM(delta) as balance,
                    MAX(ts) as last_activity
                FROM (
                    SELECT ""from"" AS account, ""tokenAddress"", -value AS delta, ""timestamp"" AS ts
                    FROM ""CrcV2_TransferSingle""
                    WHERE ""blockNumber"" <= @toBlock

                    UNION ALL

                    SELECT ""to"" AS account, ""tokenAddress"", value AS delta, ""timestamp"" AS ts
                    FROM ""CrcV2_TransferSingle""
                    WHERE ""blockNumber"" <= @toBlock

                    UNION ALL

                    SELECT ""from"" AS account, ""tokenAddress"", -value AS delta, ""timestamp"" AS ts
                    FROM ""CrcV2_TransferBatch""
                    WHERE ""blockNumber"" <= @toBlock

                    UNION ALL

                    SELECT ""to"" AS account, ""tokenAddress"", value AS delta, ""timestamp"" AS ts
                    FROM ""CrcV2_TransferBatch""
                    WHERE ""blockNumber"" <= @toBlock
                ) t
                GROUP BY account, ""tokenAddress""
                HAVING SUM(delta) > 0
            )
            SELECT ab.account, ab.""tokenAddress"", ab.balance, ab.last_activity
            FROM account_balances ab
            INNER JOIN registered_avatars ra_token ON ra_token.avatar = ab.""tokenAddress""
            WHERE ab.account != '0x0000000000000000000000000000000000000000'";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var balances = new Dictionary<string, decimal>();
        var lastActivities = new Dictionary<string, long>();
        var count = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 50000;

        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetString(0).ToLowerInvariant();
            var tokenAddress = reader.GetString(1).ToLowerInvariant();
            var totalBalanceBig = reader.GetFieldValue<BigInteger>(2);
            var lastActivity = reader.GetInt64(3);

            // Convert from atto (10^18) to decimal using CirclesConverter for proper precision
            decimal balance;
            try
            {
                balance = CirclesConverter.AttoCirclesToCircles(totalBalanceBig);
            }
            catch (OverflowException)
            {
                _logger.LogWarning("Skipping V2 balance with value {Value} that would overflow decimal", totalBalanceBig);
                continue;
            }
            if (balance > 0)
            {
                var key = $"{account}:{tokenAddress}";
                balances[key] = balance;
                lastActivities[key] = lastActivity;
            }

            count++;
            if (count % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V2 balance loading progress: {Count} entries ({Rate:F0} entries/sec)",
                    count, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        // Seed the cache with all balances at once at the warmup target block
        _caches.V2BalancesByAccountAndToken.Seed(balances, _state.WarmupTargetBlock);

        // Seed matching V2LastActivity snapshot at the same warmup block.
        _caches.V2LastActivity.Seed(lastActivities, _state.WarmupTargetBlock);

        _logger.LogInformation("Loaded {Count} V2 ERC1155 balances from view", balances.Count);
    }

    /// <summary>
    /// Loads ERC20 wrapper balances from aggregated transfer data.
    /// Since there's no pre-computed view for wrapper balances, we use an aggregation query.
    /// Wrapper balances are merged into the existing V2 cache using Seed() to avoid Add() issues.
    /// </summary>
    private async Task LoadErc20WrapperBalancesFromViewAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading ERC20 wrapper balances...");

        // Aggregate wrapper transfers up to the warmup target block.
        // Without the block filter, transfers arriving during warmup would be double-counted
        // when gap processing re-applies them as deltas on top of the seeded balances.
        // Registration filter: account must be registered (matches balanceQuery.sql).
        // Wrapper deployer registration is already enforced by ReplayV2Erc20WrapperDeployedAsync.
        const string sql = @"
            WITH " + RegisteredAvatarsCte + @",
            account_balances AS (
                SELECT
                    account,
                    ""tokenAddress"",
                    SUM(CASE WHEN account = t.""to"" THEN t.amount ELSE 0 END) -
                    SUM(CASE WHEN account = t.""from"" THEN t.amount ELSE 0 END) as balance,
                    MAX(t.""timestamp"") as last_activity
                FROM ""CrcV2_Erc20WrapperTransfer"" t
                CROSS JOIN LATERAL (
                    SELECT DISTINCT x FROM (VALUES (t.""to""), (t.""from"")) AS v(x)
                    WHERE x != '0x0000000000000000000000000000000000000000'
                ) AS accounts(account)
                WHERE t.""blockNumber"" <= @toBlock
                GROUP BY account, ""tokenAddress""
                HAVING SUM(CASE WHEN account = t.""to"" THEN t.amount ELSE 0 END) -
                       SUM(CASE WHEN account = t.""from"" THEN t.amount ELSE 0 END) > 0
            )
            SELECT ab.account, ab.""tokenAddress"", ab.balance, ab.last_activity
            FROM account_balances ab
            INNER JOIN registered_avatars ra ON ra.avatar = ab.account";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // Collect wrapper balances into a dictionary first
            var wrapperBalances = new Dictionary<string, decimal>();
            var wrapperLastActivities = new Dictionary<string, long>();

            while (await reader.ReadAsync(ct))
            {
                var account = reader.GetString(0).ToLowerInvariant();
                var tokenAddress = reader.GetString(1).ToLowerInvariant();
                var balanceBig = reader.GetFieldValue<BigInteger>(2);
                var lastActivity = reader.GetInt64(3);

                // Convert from atto (10^18) to decimal using CirclesConverter for proper precision
                decimal balance;
                try
                {
                    balance = CirclesConverter.AttoCirclesToCircles(balanceBig);
                }
                catch (OverflowException)
                {
                    continue;
                }

                if (balance > 0)
                {
                    var key = $"{account}:{tokenAddress}";
                    wrapperBalances[key] = balance;
                    wrapperLastActivities[key] = lastActivity;
                }
            }

            // Merge wrapper balances into existing V2 cache by re-seeding with combined data
            MergeWrapperBalancesIntoV2Cache(wrapperBalances);

            // Merge wrapper last-activity into existing V2 last-activity snapshot.
            var mergedLastActivities = new Dictionary<string, long>(_caches.V2LastActivity.ReadOnlyDictionary);
            foreach (var (key, ts) in wrapperLastActivities)
            {
                mergedLastActivities[key] = ts;
            }
            _caches.V2LastActivity.Seed(mergedLastActivities, _state.WarmupTargetBlock);

            _logger.LogInformation("Loaded {Count} ERC20 wrapper balances", wrapperBalances.Count);
        }
        catch (Exception ex)
        {
            // If the complex query fails, fall back to simpler approach
            _logger.LogWarning(ex, "Complex wrapper balance query failed, using simple aggregation");
            await LoadErc20WrapperBalancesSimpleAsync(conn, toBlock, ct);
        }
    }

    /// <summary>
    /// Merges wrapper balances into the V2 cache by getting existing balances and re-seeding.
    /// This avoids the RollbackCache.Add() issue where Add() fails after Seed() on same block.
    /// </summary>
    private void MergeWrapperBalancesIntoV2Cache(Dictionary<string, decimal> wrapperBalances)
    {
        // Get existing V2 balances
        var existingBalances = _caches.V2BalancesByAccountAndToken.ReadOnlyDictionary;

        // Create merged dictionary starting with existing balances
        var mergedBalances = new Dictionary<string, decimal>(existingBalances);

        // Add/merge wrapper balances
        foreach (var (key, balance) in wrapperBalances)
        {
            // Wrapper balances should not overlap with ERC1155 balances (different token addresses)
            // but use merge logic just in case
            mergedBalances[key] = mergedBalances.GetValueOrDefault(key, 0m) + balance;
        }

        // Re-seed the cache with merged data at the warmup target block
        _caches.V2BalancesByAccountAndToken.Seed(mergedBalances, _state.WarmupTargetBlock);
    }

    /// <summary>
    /// Simple fallback for loading ERC20 wrapper balances - aggregates in memory.
    /// </summary>
    private async Task LoadErc20WrapperBalancesSimpleAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT ""from"", ""to"", ""tokenAddress"", amount, ""timestamp""
            FROM ""CrcV2_Erc20WrapperTransfer""
            WHERE ""blockNumber"" <= @toBlock";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var balances = new Dictionary<string, BigInteger>();
        var maxTimestamps = new Dictionary<string, long>();

        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(0).ToLowerInvariant();
            var to = reader.GetString(1).ToLowerInvariant();
            var tokenAddress = reader.GetString(2).ToLowerInvariant();
            var amount = reader.GetFieldValue<BigInteger>(3);
            var timestamp = reader.GetInt64(4);

            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from}:{tokenAddress}";
                balances[fromKey] = balances.GetValueOrDefault(fromKey, BigInteger.Zero) - amount;
                maxTimestamps[fromKey] = Math.Max(maxTimestamps.GetValueOrDefault(fromKey, 0L), timestamp);
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to}:{tokenAddress}";
                balances[toKey] = balances.GetValueOrDefault(toKey, BigInteger.Zero) + amount;
                maxTimestamps[toKey] = Math.Max(maxTimestamps.GetValueOrDefault(toKey, 0L), timestamp);
            }
        }

        // Convert to decimal dictionary using CirclesConverter for proper precision
        var wrapperBalances = new Dictionary<string, decimal>();
        foreach (var (key, balanceBig) in balances)
        {
            if (balanceBig <= 0) continue;

            try
            {
                wrapperBalances[key] = CirclesConverter.AttoCirclesToCircles(balanceBig);
            }
            catch (OverflowException)
            {
                continue;
            }
        }

        // Merge wrapper balances into existing V2 cache
        MergeWrapperBalancesIntoV2Cache(wrapperBalances);

        // Merge wrapper last-activity into existing V2 last-activity snapshot.
        var mergedLastActivities = new Dictionary<string, long>(_caches.V2LastActivity.ReadOnlyDictionary);
        foreach (var key in wrapperBalances.Keys)
        {
            if (maxTimestamps.TryGetValue(key, out var ts))
            {
                mergedLastActivities[key] = ts;
            }
        }
        _caches.V2LastActivity.Seed(mergedLastActivities, _state.WarmupTargetBlock);

        _logger.LogInformation("Loaded {Count} ERC20 wrapper balances (simple method)", wrapperBalances.Count);
    }

    private async Task BuildV1BalancesFromTransfersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Process all V1 transfers and build balances incrementally
        // This is much faster than the aggregate view because it processes each transfer once
        const string sql = @"
            SELECT
                ""from"",
                ""to"",
                ""tokenAddress"",
                amount,
                ""blockNumber""
            FROM ""CrcV1_Transfer""
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 600; // 10 minutes for processing all transfers
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var transferCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 100000; // Log every 100k transfers

        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            var tokenAddress = reader.GetString(2);
            var amountBig = reader.GetFieldValue<BigInteger>(3);
            var blockNumber = reader.GetInt64(4);

            // Convert from atto (10^18) to decimal using CirclesConverter for proper precision
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
            var tokenKey = tokenAddress.ToLowerInvariant();

            if (blockNumber != currentBlock)
            {
                if (currentBlock != -1)
                {
                    // Add balances for the previous block
                    foreach (var kvp in currentBalances)
                    {
                        _caches.V1BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                    }
                }
                currentBlock = blockNumber;
            }

            // Update balances for sender
            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                currentBalances[fromKey] = currentBalances.GetValueOrDefault(fromKey, 0m) - value;
            }

            // Update balances for receiver
            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                currentBalances[toKey] = currentBalances.GetValueOrDefault(toKey, 0m) + value;
            }

            transferCount++;

            if (transferCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V1 balance building progress: {Count} transfers processed ({Rate:F0} transfers/sec)",
                    transferCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        // Add balances for the last block
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.V1BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
            }
        }

        _logger.LogInformation("Built V1 balances from {Count} transfers. Total balance entries: {BalanceCount}",
            transferCount, _caches.V1BalancesByAccountAndToken.Count);
    }

    private async Task BuildV2BalancesFromTransfersAsync(NpgsqlConnection conn, CancellationToken ct)
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
                    'Single' as type
                FROM ""CrcV2_TransferSingle""

                UNION ALL

                SELECT
                    ""from"",
                    ""to"",
                    ""tokenAddress"",
                    value,
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    'Batch' as type
                FROM ""CrcV2_TransferBatch""
            ) AS combined
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        var transferCount = 0;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 600; // 10 minutes
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 100000;

        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            var tokenAddress = reader.GetString(2).ToLowerInvariant();
            var valueBig = reader.GetFieldValue<BigInteger>(3);
            var blockNumber = reader.GetInt64(4);

            var divisor = BigInteger.Parse("1000000000000000000");
            var amountBig = valueBig / divisor;

            if (amountBig > (BigInteger)decimal.MaxValue || amountBig < (BigInteger)decimal.MinValue)
            {
                _logger.LogWarning("Skipping V2 transfer with value {Value} that would overflow decimal", valueBig);
                continue;
            }

            var amount = (decimal)amountBig;

            if (blockNumber != currentBlock)
            {
                if (currentBlock != -1)
                {
                    // Add balances for the previous block
                    foreach (var kvp in currentBalances)
                    {
                        _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                    }
                }
                currentBlock = blockNumber;
            }

            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from.ToLowerInvariant()}:{tokenAddress}";
                currentBalances[fromKey] = currentBalances.GetValueOrDefault(fromKey, 0m) - amount;
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to.ToLowerInvariant()}:{tokenAddress}";
                currentBalances[toKey] = currentBalances.GetValueOrDefault(toKey, 0m) + amount;
            }

            transferCount++;

            if (transferCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V2 Transfer progress: {Count} transfers ({Rate:F0} transfers/sec)",
                    transferCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        // Add balances for the last block
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
            }
        }

        _logger.LogInformation("Built V2 balances from {Count} transfers. Total balance entries: {BalanceCount}",
            transferCount, _caches.V2BalancesByAccountAndToken.Count);
    }

    private async Task BuildErc20WrapperBalancesFromTransfersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Process all ERC20 wrapper transfers and build balances incrementally
        const string sql = @"
            SELECT
                ""from"",
                ""to"",
                ""tokenAddress"",
                amount,
                ""blockNumber""
            FROM ""CrcV2_Erc20WrapperTransfer""
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 600; // 10 minutes for processing all transfers
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var transferCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 10000; // Log every 10k transfers (fewer than ERC1155)

        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            var tokenAddress = reader.GetString(2);
            var amountBig = reader.GetFieldValue<BigInteger>(3);
            var blockNumber = reader.GetInt64(4);

            var divisor = BigInteger.Parse("1000000000000000000");
            var valueBig = amountBig / divisor;

            if (valueBig > (BigInteger)decimal.MaxValue || valueBig < (BigInteger)decimal.MinValue)
            {
                _logger.LogWarning("Skipping ERC20 wrapper transfer with amount {Amount} that would overflow decimal", amountBig);
                continue;
            }

            var value = (decimal)valueBig;
            var tokenKey = tokenAddress.ToLowerInvariant();

            if (blockNumber != currentBlock)
            {
                if (currentBlock != -1)
                {
                    // Add balances for the previous block
                    foreach (var kvp in currentBalances)
                    {
                        _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                    }
                }
                currentBlock = blockNumber;
            }

            // Update balances for sender
            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                currentBalances[fromKey] = currentBalances.GetValueOrDefault(fromKey, 0m) - value;
            }

            // Update balances for receiver
            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                currentBalances[toKey] = currentBalances.GetValueOrDefault(toKey, 0m) + value;
            }

            transferCount++;

            if (transferCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("ERC20 wrapper balance building progress: {Count} transfers processed ({Rate:F0} transfers/sec)",
                    transferCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        // Add balances for the last block
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
            }
        }

        _logger.LogInformation("Built ERC20 wrapper balances from {Count} transfers. Total V2 balance entries: {BalanceCount}",
            transferCount, _caches.V2BalancesByAccountAndToken.Count);
    }
}
