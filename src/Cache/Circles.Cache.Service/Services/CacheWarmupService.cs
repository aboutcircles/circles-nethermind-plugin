using System.Numerics;
using Circles.Cache.Service.Caches;
using Circles.Index.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that performs initial cache warmup by replaying all events from PostgreSQL.
/// Runs once on startup to build the cache state from historical blockchain data.
/// </summary>
public class CacheWarmupService : BackgroundService
{
    private readonly ILogger<CacheWarmupService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;

    // Fields for periodic reminder logging
    private DateTime _lastReminderLogTime = DateTime.MinValue;
    private readonly TimeSpan _reminderInterval = TimeSpan.FromMinutes(5);

    public CacheWarmupService(
        ILogger<CacheWarmupService> logger,
        CacheServiceSettings settings,
        CacheServiceState state,
        CacheContainer caches)
    {
        _logger = logger;
        _settings = settings;
        _state = state;
        _caches = caches;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting cache warmup service");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting cache warmup...");

                // Wait for PostgreSQL to be ready
                await WaitForDatabaseAsync(stoppingToken);

                await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
                await conn.OpenAsync(stoppingToken);

                // Wait for initial sync to complete before starting warmup
                await WaitForInitialSyncAsync(conn, stoppingToken);

                // Clear caches before replay to ensure monotonic block order
                ClearCaches();

                // STEP 1: Get the current database head block and set it as warmup target
                var warmupTarget = await GetDatabaseHeadAsync(conn, stoppingToken);
                _state.WarmupTargetBlock = warmupTarget;
                _logger.LogInformation("Warmup target block set to: {Block}", warmupTarget);

                // STEP 2: Replay all events up to the warmup target
                _logger.LogInformation("Starting warmup replay up to block {Block}...", warmupTarget);

                // Replay V1 events
                await ReplayV1EventsAsync(conn, warmupTarget, stoppingToken);

                // Replay V2 events
                await ReplayV2EventsAsync(conn, warmupTarget, stoppingToken);

                // Load group memberships
                await LoadGroupMembershipsAsync(conn, warmupTarget, stoppingToken);

                // Load trust relations
                await LoadTrustRelationsAsync(conn, warmupTarget, stoppingToken);

                // Load avatar metadata (CID maps)
                await LoadAvatarMetadataAsync(conn, warmupTarget, stoppingToken);

                // Load V2 short names
                await LoadV2ShortNamesAsync(conn, warmupTarget, stoppingToken);

                // Load balances (this may take longer)
                await LoadBalancesAsync(conn, stoppingToken);

                // Rebuild secondary indexes for fast balance lookups
                _logger.LogInformation("Rebuilding secondary indexes...");
                _caches.RebuildSecondaryIndexes();
                _logger.LogInformation("Secondary indexes rebuilt");

                // Update state
                _state.LastProcessedBlock = warmupTarget;
                _logger.LogInformation("========================================");
                _logger.LogInformation("✓ Warmup replay completed at block {Block}", warmupTarget);
                _logger.LogInformation("========================================");

                // STEP 3: Initialize block ring buffer with recent blocks
                await InitializeBlockRingBufferAsync(conn, warmupTarget, stoppingToken);

                // STEP 4: Check if new blocks arrived during warmup and process them
                var currentHead = await GetDatabaseHeadAsync(conn, stoppingToken);
                if (currentHead > warmupTarget)
                {
                    _logger.LogInformation(
                        "New blocks arrived during warmup ({WarmupTarget} -> {CurrentHead}). Processing gap...",
                        warmupTarget, currentHead);

                    // Process the gap using the same notification processing logic
                    await ProcessBlockGapAsync(conn, warmupTarget + 1, currentHead, stoppingToken);

                    _state.LastProcessedBlock = currentHead;
                    _logger.LogInformation("Processed {Count} blocks that arrived during warmup",
                        currentHead - warmupTarget);
                }
                else
                {
                    _logger.LogInformation("No new blocks arrived during warmup");
                }

                // STEP 5: Mark warmup as complete
                _state.WarmupComplete = true;

                // Reset reminder time on success
                _lastReminderLogTime = DateTime.MinValue;

                _logger.LogInformation("Cache warmup completed successfully");
                break; // Exit loop on success
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache warmup failed");

                // Log periodic reminder if enough time has passed since last reminder
                var now = DateTime.UtcNow;
                if (now - _lastReminderLogTime >= _reminderInterval)
                {
                    _logger.LogWarning("Cache warmup failed. Service will remain unhealthy until manual restart or DB issue is resolved.");
                    _lastReminderLogTime = now;
                }

                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Cache warmup service stopped");
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for PostgreSQL to be ready...");

        const int maxRetries = 60;
        const int delayMs = 5000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                _logger.LogInformation("PostgreSQL is ready");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PostgreSQL not ready yet (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s...", i + 1, maxRetries, delayMs / 1000);
                await Task.Delay(delayMs, ct);
            }
        }

        throw new Exception($"PostgreSQL did not become ready after {maxRetries} attempts");
    }

    /// <summary>
    /// Waits for the Nethermind plugin to complete initial sync by listening for the first NOTIFY event.
    /// The plugin only sends NOTIFY events once initial sync is complete and blocks are being streamed live.
    /// </summary>
    private async Task WaitForInitialSyncAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Waiting for Nethermind plugin to complete initial sync...");
        _logger.LogInformation("Listening for first pg_notify event on channel '{Channel}' to detect sync completion...",
            _settings.PgNotifyChannel);

        // Set up NOTIFY listener
        var notificationReceived = new TaskCompletionSource<bool>();

        conn.Notification += (sender, args) =>
        {
            _logger.LogInformation("Received first NOTIFY event - initial sync is complete!");
            notificationReceived.TrySetResult(true);
        };

        await using var cmd = new NpgsqlCommand($"LISTEN {_settings.PgNotifyChannel}", conn);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Subscribed to NOTIFY channel. Waiting for first event...");

        // Wait for first notification or cancellation
        using var ctRegistration = ct.Register(() => notificationReceived.TrySetCanceled(ct));

        var lastWaitLogTime = DateTime.UtcNow;
        const int waitLogIntervalSeconds = 300;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for notification with periodic logging
                var waitTask = conn.WaitAsync(ct);
                var delayTask = Task.Delay(TimeSpan.FromSeconds(waitLogIntervalSeconds), ct);
                var completedTask = await Task.WhenAny(notificationReceived.Task, waitTask, delayTask);

                if (completedTask == notificationReceived.Task)
                {
                    // Notification received, sync is complete
                    _logger.LogInformation("Initial sync confirmed complete via NOTIFY event");

                    // Wait for the WaitAsync to complete if it hasn't already, to exit the waiting state
                    if (!waitTask.IsCompleted)
                    {
                        await waitTask;
                    }

                    // Unlisten to free the connection for subsequent operations
                    await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN {_settings.PgNotifyChannel}", conn);
                    await unlistenCmd.ExecuteNonQueryAsync(ct);

                    return;
                }
                else if (completedTask == delayTask)
                {
                    // Periodic log to show we're still waiting
                    _logger.LogInformation("Still waiting for first NOTIFY event on channel '{Channel}'...",
                        _settings.PgNotifyChannel);
                }
                // If waitTask completed, it means there's data but not our notification, continue loop
            }
        }
        finally
        {
            // Ensure we always unlisten, even if cancelled or exception occurs
            try
            {
                await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN {_settings.PgNotifyChannel}", conn);
                await unlistenCmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to UNLISTEN from channel '{Channel}' during cleanup", _settings.PgNotifyChannel);
            }
        }
    }

    private async Task<long> GetDatabaseHeadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT COALESCE(MAX(""blockNumber""), 0)
            FROM ""System_Block""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        // blockNumber is BIGINT, can be returned as int or long depending on value
        return result switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };
    }

    private async Task ReplayV1EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Replaying V1 events...");

        // Replay V1 Signups (both human and organization)
        await ReplayV1SignupsAsync(conn, toBlock, ct);

        _logger.LogInformation("V1 event replay completed");
    }

    private async Task ReplayV1SignupsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V1 avatars...");

        // Load both human and organization signups, using Seed() for efficiency
        const string sql = @"
            SELECT
                s.""user"" as address,
                s.""token"",
                'Human' as type
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                o.""organization"" as address,
                NULL as token,
                'Organization' as type
            FROM ""CrcV1_OrganizationSignup"" o
            WHERE o.""blockNumber"" <= @toBlock";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;

        var avatars = new Dictionary<string, (string Type, string? Token)>();
        var tokenOwners = new Dictionary<string, string>();
        var humanCount = 0;
        var orgCount = 0;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var address = reader.GetString(0);
            var token = reader.IsDBNull(1) ? null : reader.GetString(1);
            var type = reader.GetString(2);

            var addressKey = address.ToLowerInvariant();

            if (type == "Human")
            {
                var tokenKey = token!.ToLowerInvariant();
                avatars[addressKey] = ("Human", token!);
                tokenOwners[tokenKey] = address;
                humanCount++;
            }
            else
            {
                avatars[addressKey] = ("Organization", null);
                orgCount++;
            }
        }

        // Seed caches with bulk data at the warmup target block
        var warmupBlock = _state.WarmupTargetBlock;
        _caches.V1Avatars.Seed(avatars, warmupBlock);
        _caches.V1TokenOwnerByToken.Seed(tokenOwners, warmupBlock);

        _logger.LogInformation("Loaded {HumanCount} V1 human signups and {OrgCount} organization signups",
            humanCount, orgCount);
    }

    private async Task ReplayV2EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Replaying V2 events...");

        // Replay V2 avatar registrations (all types in block order)
        await ReplayV2AvatarRegistrationsAsync(conn, toBlock, ct);

        // Replay V2 ERC20WrapperDeployed
        await ReplayV2Erc20WrapperDeployedAsync(conn, toBlock, ct);

        _logger.LogInformation("V2 event replay completed");
    }

    private async Task ReplayV2AvatarRegistrationsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V2 avatars...");

        // Load V2 avatars (humans and organizations) using Seed() for efficiency
        const string avatarSql = @"
            SELECT
                r.""avatar"" as address,
                r.""timestamp"",
                'Human' as type
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                r.""organization"" as address,
                r.""timestamp"",
                'Organization' as type
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" <= @toBlock";

        var v2Avatars = new Dictionary<string, (string Type, long Timestamp)>();
        var humanCount = 0;
        var orgCount = 0;

        await using (var cmd = new NpgsqlCommand(avatarSql, conn))
        {
            cmd.Parameters.AddWithValue("toBlock", toBlock);
            cmd.CommandTimeout = 300;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var address = reader.GetString(0);
                var timestamp = reader.GetInt64(1);
                var type = reader.GetString(2);

                var addressKey = address.ToLowerInvariant();
                v2Avatars[addressKey] = (type, timestamp);

                if (type == "Human")
                    humanCount++;
                else
                    orgCount++;
            }
        }

        _caches.V2Avatars.Seed(v2Avatars, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {HumanCount} V2 humans and {OrgCount} organizations", humanCount, orgCount);

        // Load groups separately (they have different data structure)
        _logger.LogInformation("Loading V2 groups...");

        const string groupSql = @"
            SELECT
                r.""group"" as address,
                r.""name"",
                r.""mint"",
                r.""symbol""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" <= @toBlock";

        var groups = new Dictionary<string, (string Name, string Mint, string Symbol)>();

        await using (var groupCmd = new NpgsqlCommand(groupSql, conn))
        {
            groupCmd.Parameters.AddWithValue("toBlock", toBlock);
            groupCmd.CommandTimeout = 300;

            await using var groupReader = await groupCmd.ExecuteReaderAsync(ct);
            while (await groupReader.ReadAsync(ct))
            {
                var address = groupReader.GetString(0);
                var name = groupReader.GetString(1);
                var mint = groupReader.GetString(2);
                var symbol = groupReader.GetString(3);

                var addressKey = address.ToLowerInvariant();
                groups[addressKey] = (name, mint, symbol);
            }
        }

        _caches.Groups.Seed(groups, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {GroupCount} V2 groups", groups.Count);
    }

    private async Task ReplayV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V2 ERC20 wrappers...");

        const string sql = @"
            SELECT
                e.""avatar"",
                e.""erc20Wrapper"",
                e.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" <= @toBlock";

        // Key by wrapper address (not avatar) to support avatars with multiple wrappers
        // An avatar can have both demurraged (circlesType=0) and inflationary (circlesType=1) wrappers
        var wrappers = new Dictionary<string, (string Avatar, int CirclesType)>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0);
            var erc20Wrapper = reader.GetString(1);
            var circlesType = reader.GetInt32(2);

            // Key by wrapper address for direct lookup
            var wrapperKey = erc20Wrapper.ToLowerInvariant();
            wrappers[wrapperKey] = (avatar.ToLowerInvariant(), circlesType);
        }

        _caches.Erc20WrapperAddresses.Seed(wrappers, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V2 ERC20 wrapper deployments", wrappers.Count);
    }

    private async Task LoadBalancesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Loading balances from pre-computed views (fast path)...");

        // Load V1 balances directly from the balance view
        await LoadV1BalancesFromViewAsync(conn, ct);

        // Load V2 balances directly from the balance view
        await LoadV2BalancesFromViewAsync(conn, ct);

        // Load ERC20 wrapper balances from view (or aggregated query)
        await LoadErc20WrapperBalancesFromViewAsync(conn, ct);

        _logger.LogInformation("Balance loading completed");
    }

    /// <summary>
    /// Loads V1 balances directly from the pre-computed V_CrcV1_BalancesByAccountAndToken view.
    /// This is much faster than replaying all transfers since the view already aggregates them.
    /// </summary>
    private async Task LoadV1BalancesFromViewAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Loading V1 balances from view...");

        // The view returns pre-aggregated balances - just load them directly
        // Note: We use totalBalance (raw attoCrc) divided by 10^18 to get decimal
        const string sql = @"
            SELECT
                account,
                ""tokenAddress"",
                ""totalBalance""
            FROM ""V_CrcV1_BalancesByAccountAndToken""
            WHERE ""totalBalance"" > 0";

        await using var cmd = new NpgsqlCommand(sql, conn);
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
    private async Task LoadV2BalancesFromViewAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Loading V2 balances from view...");

        // Load ERC1155 balances from the view
        // Use totalBalance for storage - demurrage will be calculated at query time
        const string sql = @"
            SELECT
                account,
                ""tokenId"",
                ""totalBalance""
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE ""totalBalance"" > 0";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var balances = new Dictionary<string, decimal>();
        var count = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 50000;

        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetString(0).ToLowerInvariant();
            var tokenId = reader.GetString(1); // tokenId is already a string in the view
            var totalBalanceBig = reader.GetFieldValue<BigInteger>(2);

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
                var key = $"{account}:{tokenId}";
                balances[key] = balance;
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
        _logger.LogInformation("Loaded {Count} V2 ERC1155 balances from view", balances.Count);
    }

    /// <summary>
    /// Loads ERC20 wrapper balances from aggregated transfer data.
    /// Since there's no pre-computed view for wrapper balances, we use an aggregation query.
    /// Wrapper balances are merged into the existing V2 cache using Seed() to avoid Add() issues.
    /// </summary>
    private async Task LoadErc20WrapperBalancesFromViewAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Loading ERC20 wrapper balances...");

        // Aggregate wrapper transfers to get current balances
        // This is faster than replaying transfers one by one because it's a single SQL aggregation
        const string sql = @"
            WITH wrapper_balances AS (
                SELECT
                    ""tokenAddress"",
                    SUM(CASE WHEN ""to"" != '0x0000000000000000000000000000000000000000'
                        THEN amount ELSE 0 END) -
                    SUM(CASE WHEN ""from"" != '0x0000000000000000000000000000000000000000'
                        THEN amount ELSE 0 END) as net_amount,
                    CASE
                        WHEN ""to"" != '0x0000000000000000000000000000000000000000' THEN ""to""
                        ELSE ""from""
                    END as account
                FROM ""CrcV2_Erc20WrapperTransfer""
                GROUP BY ""tokenAddress"",
                    CASE
                        WHEN ""to"" != '0x0000000000000000000000000000000000000000' THEN ""to""
                        ELSE ""from""
                    END
            ),
            account_balances AS (
                SELECT
                    account,
                    ""tokenAddress"",
                    SUM(CASE WHEN account = t.""to"" THEN t.amount ELSE 0 END) -
                    SUM(CASE WHEN account = t.""from"" THEN t.amount ELSE 0 END) as balance
                FROM ""CrcV2_Erc20WrapperTransfer"" t
                CROSS JOIN LATERAL (
                    SELECT DISTINCT x FROM (VALUES (t.""to""), (t.""from"")) AS v(x)
                    WHERE x != '0x0000000000000000000000000000000000000000'
                ) AS accounts(account)
                GROUP BY account, ""tokenAddress""
                HAVING SUM(CASE WHEN account = t.""to"" THEN t.amount ELSE 0 END) -
                       SUM(CASE WHEN account = t.""from"" THEN t.amount ELSE 0 END) > 0
            )
            SELECT account, ""tokenAddress"", balance
            FROM account_balances";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // Collect wrapper balances into a dictionary first
            var wrapperBalances = new Dictionary<string, decimal>();

            while (await reader.ReadAsync(ct))
            {
                var account = reader.GetString(0).ToLowerInvariant();
                var tokenAddress = reader.GetString(1).ToLowerInvariant();
                var balanceBig = reader.GetFieldValue<BigInteger>(2);

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
                }
            }

            // Merge wrapper balances into existing V2 cache by re-seeding with combined data
            MergeWrapperBalancesIntoV2Cache(wrapperBalances);
            _logger.LogInformation("Loaded {Count} ERC20 wrapper balances", wrapperBalances.Count);
        }
        catch (Exception ex)
        {
            // If the complex query fails, fall back to simpler approach
            _logger.LogWarning(ex, "Complex wrapper balance query failed, using simple aggregation");
            await LoadErc20WrapperBalancesSimpleAsync(conn, ct);
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
    private async Task LoadErc20WrapperBalancesSimpleAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT ""from"", ""to"", ""tokenAddress"", amount
            FROM ""CrcV2_Erc20WrapperTransfer""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var balances = new Dictionary<string, BigInteger>();

        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(0).ToLowerInvariant();
            var to = reader.GetString(1).ToLowerInvariant();
            var tokenAddress = reader.GetString(2).ToLowerInvariant();
            var amount = reader.GetFieldValue<BigInteger>(3);

            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from}:{tokenAddress}";
                balances[fromKey] = balances.GetValueOrDefault(fromKey, BigInteger.Zero) - amount;
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to}:{tokenAddress}";
                balances[toKey] = balances.GetValueOrDefault(toKey, BigInteger.Zero) + amount;
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
                    id,
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
                    id,
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
            var tokenId = reader.GetFieldValue<BigInteger>(2).ToString();
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
                var fromKey = $"{from.ToLowerInvariant()}:{tokenId}";
                currentBalances[fromKey] = currentBalances.GetValueOrDefault(fromKey, 0m) - amount;
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to.ToLowerInvariant()}:{tokenId}";
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

    private async Task LoadGroupMembershipsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading group memberships...");

        // Load group memberships using Seed() for efficiency
        const string sql = @"
            SELECT
                ""group"",
                ""member"",
                ""expiryTime""
            FROM ""V_CrcV2_GroupMemberships""";

        var memberships = new Dictionary<string, (string Member, long ExpiryTime)>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var group = reader.GetString(0);
            var member = reader.GetString(1);
            var expiryTimeBig = reader.GetFieldValue<BigInteger>(2);

            // Safely cast expiryTime to long, capping at long.MaxValue for overflow
            long expiryLong = expiryTimeBig > long.MaxValue ? long.MaxValue : (long)expiryTimeBig;

            // Composite key: group:member
            var key = $"{group.ToLowerInvariant()}:{member.ToLowerInvariant()}";
            memberships[key] = (member, expiryLong);
        }

        _caches.GroupMemberships.Seed(memberships, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} group memberships", memberships.Count);
    }

    private async Task LoadTrustRelationsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading trust relations...");

        // Load V1 trust relations using Seed() for efficiency
        // V1 trust has a "limit" field (0-100 percentage) that we store as the value
        // NOTE: We intentionally include limit=0 entries (untrusts) to match production behavior.
        // The V_CrcV1_TrustRelations view filters "limit > 0", but production RPC doesn't use it.
        // Semantically, limit=0 means "untrusted" and arguably shouldn't be returned, but we need
        // production parity for now. TODO: Consider filtering limit=0 in both places in future.
        const string v1Sql = @"
            SELECT ""user"" as truster, ""canSendTo"" as trustee, ""limit""
            FROM (
                SELECT ""user"", ""canSendTo"", ""limit"",
                       ROW_NUMBER() OVER (PARTITION BY ""user"", ""canSendTo""
                                          ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV1_Trust""
            ) t
            WHERE rn = 1";

        var v1TrustData = new Dictionary<string, long>();

        await using (var v1Cmd = new NpgsqlCommand(v1Sql, conn))
        {
            v1Cmd.CommandTimeout = 300;
            await using var v1Reader = await v1Cmd.ExecuteReaderAsync(ct);

            while (await v1Reader.ReadAsync(ct))
            {
                var truster = v1Reader.GetString(0).ToLowerInvariant();
                var trustee = v1Reader.GetString(1).ToLowerInvariant();
                var limitBig = v1Reader.GetFieldValue<BigInteger>(2);

                // Store the trust limit (0-100) as the value
                long limitLong = limitBig > long.MaxValue ? long.MaxValue : (long)limitBig;
                var key = $"{truster}:{trustee}";
                v1TrustData[key] = limitLong;
            }
        }

        _caches.V1TrustRelations.Seed(v1TrustData, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V1 trust relations", v1TrustData.Count);

        // Load V2 trust relations using Seed() for efficiency
        const string v2Sql = @"
            SELECT truster, trustee, ""expiryTime""
            FROM ""V_CrcV2_TrustRelations""";

        var v2TrustData = new Dictionary<string, long>();

        await using (var v2Cmd = new NpgsqlCommand(v2Sql, conn))
        {
            v2Cmd.CommandTimeout = 300;
            await using var v2Reader = await v2Cmd.ExecuteReaderAsync(ct);

            while (await v2Reader.ReadAsync(ct))
            {
                var truster = v2Reader.GetString(0).ToLowerInvariant();
                var trustee = v2Reader.GetString(1).ToLowerInvariant();
                var expiryTimeBig = v2Reader.GetFieldValue<BigInteger>(2);

                long expiryLong = expiryTimeBig > long.MaxValue ? long.MaxValue : (long)expiryTimeBig;

                var key = $"{truster}:{trustee}";
                v2TrustData[key] = expiryLong;
            }
        }

        _caches.V2TrustRelations.Seed(v2TrustData, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V2 trust relations", v2TrustData.Count);
    }

    private async Task LoadAvatarMetadataAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading avatar metadata (CID maps)...");

        // Load V1 avatar CIDs using Seed() for efficiency
        const string v1CidSql = @"
            SELECT m.avatar, m.""metadataDigest""
            FROM (
                SELECT avatar, ""metadataDigest"",
                    ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV1_UpdateMetadataDigest""
                WHERE ""blockNumber"" <= @toBlock
            ) m
            WHERE m.rn = 1";

        var v1CidMap = new Dictionary<string, string>();

        await using (var v1CidCmd = new NpgsqlCommand(v1CidSql, conn))
        {
            v1CidCmd.Parameters.AddWithValue("toBlock", toBlock);
            v1CidCmd.CommandTimeout = 300;
            await using var v1CidReader = await v1CidCmd.ExecuteReaderAsync(ct);

            while (await v1CidReader.ReadAsync(ct))
            {
                var avatar = v1CidReader.GetString(0);
                var metadataDigest = (byte[])v1CidReader.GetValue(1);
                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);

                var key = avatar.ToLowerInvariant();
                v1CidMap[key] = cid;
            }
        }

        _caches.V1AvatarToCidMap.Seed(v1CidMap, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V1 avatar CIDs", v1CidMap.Count);

        // Load V2 avatar CIDs using Seed() for efficiency
        const string v2CidSql = @"
            SELECT m.avatar, m.""metadataDigest""
            FROM (
                SELECT avatar, ""metadataDigest"",
                    ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV2_UpdateMetadataDigest""
                WHERE ""blockNumber"" <= @toBlock
            ) m
            WHERE m.rn = 1";

        var v2CidMap = new Dictionary<string, string>();

        await using (var v2CidCmd = new NpgsqlCommand(v2CidSql, conn))
        {
            v2CidCmd.Parameters.AddWithValue("toBlock", toBlock);
            v2CidCmd.CommandTimeout = 300;
            await using var v2CidReader = await v2CidCmd.ExecuteReaderAsync(ct);

            while (await v2CidReader.ReadAsync(ct))
            {
                var avatar = v2CidReader.GetString(0);
                var metadataDigest = (byte[])v2CidReader.GetValue(1);
                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);

                var key = avatar.ToLowerInvariant();
                v2CidMap[key] = cid;
            }
        }

        _caches.V2AvatarToCidMap.Seed(v2CidMap, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V2 avatar CIDs", v2CidMap.Count);
    }

    private async Task LoadV2ShortNamesAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V2 short names...");

        // Load V2 short names using Seed() for efficiency
        const string shortNameSql = @"
            SELECT s.avatar, s.""shortName""
            FROM (
                SELECT avatar, ""shortName"",
                    ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV2_RegisterShortName""
                WHERE ""blockNumber"" <= @toBlock
            ) s
            WHERE s.rn = 1";

        var shortNames = new Dictionary<string, string>();

        await using var cmd = new NpgsqlCommand(shortNameSql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0);
            // shortName is stored as numeric (BigInteger) in database
            var shortNameNumeric = reader.GetFieldValue<BigInteger>(1);
            // Convert to Base58Btc format (like "zAlice")
            var shortNameBase58 = shortNameNumeric.ToBase58Btc();

            var key = avatar.ToLowerInvariant();
            shortNames[key] = shortNameBase58;
        }

        _caches.V2AvatarToShortNameMap.Seed(shortNames, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V2 short names", shortNames.Count);
    }

    /// <summary>
    /// Initializes the block ring buffer with the most recent blocks from the database.
    /// </summary>
    private async Task InitializeBlockRingBufferAsync(NpgsqlConnection conn, long fromBlock, CancellationToken ct)
    {
        var capacity = _settings.RollbackCapacity;
        var startBlock = Math.Max(0, fromBlock - capacity + 1);

        _logger.LogInformation("Initializing block ring buffer with blocks {StartBlock} to {EndBlock}...",
            startBlock, fromBlock);

        const string sql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            WHERE ""blockNumber"" >= @startBlock AND ""blockNumber"" <= @endBlock
            ORDER BY ""blockNumber"" ASC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("startBlock", startBlock);
        cmd.Parameters.AddWithValue("endBlock", fromBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var blockHashStr = reader.GetString(1);

            _state.BlockRingBuffer.Add(blockNumber, blockHashStr);
            count++;
        }

        _logger.LogInformation("Block ring buffer initialized with {Count} blocks", count);
    }

    /// <summary>
    /// Processes blocks that arrived during the warmup phase.
    /// Reuses the same logic as the notification listener.
    /// </summary>
    private async Task ProcessBlockGapAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Processing block gap: {FromBlock} to {ToBlock}", fromBlock, toBlock);

        // Update block ring buffer with new blocks
        const string blocksSql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"" ASC";

        var newBlocks = new List<(long BlockNumber, string BlockHash)>();

        await using (var blocksCmd = new NpgsqlCommand(blocksSql, conn))
        {
            blocksCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            blocksCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var blocksReader = await blocksCmd.ExecuteReaderAsync(ct);
            while (await blocksReader.ReadAsync(ct))
            {
                var blockNumber = blocksReader.GetInt64(0);
                var blockHashStr = blocksReader.GetString(1);
                newBlocks.Add((blockNumber, blockHashStr));
            }
        }

        // Check for reorgs when updating the ring buffer
        var reorgPoint = _state.BlockRingBuffer.UpdateFromBlocks(newBlocks);

        if (reorgPoint.HasValue)
        {
            _logger.LogWarning("Reorg detected at block {ReorgBlock} during gap processing! Rolling back caches...",
                reorgPoint.Value);

            // Rollback all caches
            _caches.RollbackAll(reorgPoint.Value);

            // Rebuild secondary indexes after rollback
            _logger.LogInformation("Rebuilding secondary indexes after rollback...");
            _caches.RebuildSecondaryIndexes();

            // Adjust the fromBlock to start processing from the reorg point
            fromBlock = reorgPoint.Value;
        }

        // Process V1 events in this range
        await ProcessV1EventsInRangeAsync(conn, fromBlock, toBlock, ct);

        // Process V2 events in this range
        await ProcessV2EventsInRangeAsync(conn, fromBlock, toBlock, ct);

        _logger.LogInformation("Successfully processed block gap {FromBlock} to {ToBlock}", fromBlock, toBlock);
    }

    /// <summary>
    /// Process V1 events in a specific block range (used for gap processing).
    /// </summary>
    private async Task ProcessV1EventsInRangeAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Signups (humans)
        const string humanSignupSql = @"
            SELECT s.""blockNumber"", s.""user"", s.""token""
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var user = reader.GetString(1);
                var token = reader.GetString(2);

                var userKey = user.ToLowerInvariant();
                var tokenKey = token.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, userKey, ("Human", token));
                _caches.V1TokenOwnerByToken.Add(blockNumber, tokenKey, user);
            }
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

        await using (var orgReader = await orgCmd.ExecuteReaderAsync(ct))
        {
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var organization = orgReader.GetString(1);

                var orgKey = organization.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, orgKey, ("Organization", null));
            }
        }
    }

    /// <summary>
    /// Process V2 events in a specific block range (used for gap processing).
    /// </summary>
    private async Task ProcessV2EventsInRangeAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterHuman
        const string humanSql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""avatar""
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var cmd = new NpgsqlCommand(humanSql, conn))
        {
            cmd.Parameters.AddWithValue("fromBlock", fromBlock);
            cmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var avatar = reader.GetString(2);

                var avatarKey = avatar.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, avatarKey, ("Human", timestamp));
            }
        }

        // Process V2 RegisterOrganization
        const string orgSql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""organization""
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var orgCmd = new NpgsqlCommand(orgSql, conn))
        {
            orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            orgCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var orgReader = await orgCmd.ExecuteReaderAsync(ct);
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var timestamp = orgReader.GetInt64(1);
                var organization = orgReader.GetString(2);

                var orgKey = organization.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, orgKey, ("Organization", timestamp));
            }
        }

        // Process V2 RegisterGroup
        const string groupSql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint"", r.""symbol""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var groupCmd = new NpgsqlCommand(groupSql, conn))
        {
            groupCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            groupCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var groupReader = await groupCmd.ExecuteReaderAsync(ct);
            while (await groupReader.ReadAsync(ct))
            {
                var blockNumber = groupReader.GetInt64(0);
                var group = groupReader.GetString(1);
                var name = groupReader.GetString(2);
                var mint = groupReader.GetString(3);
                var symbol = groupReader.GetString(4);

                var groupKey = group.ToLowerInvariant();

                _caches.Groups.Add(blockNumber, groupKey, (name, mint, symbol));
            }
        }

        // Process V2 ERC20WrapperDeployed
        const string wrapperSql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper"", e.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" >= @fromBlock AND e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using (var wrapperCmd = new NpgsqlCommand(wrapperSql, conn))
        {
            wrapperCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            wrapperCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var wrapperReader = await wrapperCmd.ExecuteReaderAsync(ct);
            while (await wrapperReader.ReadAsync(ct))
            {
                var blockNumber = wrapperReader.GetInt64(0);
                var avatar = wrapperReader.GetString(1);
                var erc20Wrapper = wrapperReader.GetString(2);
                var circlesType = wrapperReader.GetInt32(3);

                // Key by wrapper address (not avatar) to support avatars with multiple wrappers
                var wrapperKey = erc20Wrapper.ToLowerInvariant();

                _caches.Erc20WrapperAddresses.Add(blockNumber, wrapperKey, (avatar.ToLowerInvariant(), circlesType));
            }
        }
    }

    /// <summary>
    /// Clears all caches to ensure a clean state for warmup replay.
    /// </summary>
    private void ClearCaches()
    {
        _caches.V1Avatars.Seed(new Dictionary<string, (string, string?)>());
        _caches.V1TokenOwnerByToken.Seed(new Dictionary<string, string>());
        _caches.V1AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2Avatars.Seed(new Dictionary<string, (string, long)>());
        _caches.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, int)>());
        _caches.Groups.Seed(new Dictionary<string, (string, string, string)>());
        _caches.GroupMemberships.Seed(new Dictionary<string, (string, long)>());
        _caches.V2AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>());
        _caches.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V1TrustRelations.Seed(new Dictionary<string, long>());
        _caches.V2TrustRelations.Seed(new Dictionary<string, long>());
    }
}
