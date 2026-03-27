using System.Data;
using System.Numerics;
using Circles.Cache.Service.Caches;
using Circles.Common;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly NpgsqlDataSource _readonlyDataSource;
    private readonly GapReplayNotificationListener _gapReplayProcessor;

    protected CacheServiceSettings Settings => _settings;
    protected CacheServiceState State => _state;
    protected CacheContainer Caches => _caches;

    // Fields for periodic reminder logging
    private DateTime _lastReminderLogTime = DateTime.MinValue;
    private readonly TimeSpan _reminderInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// SQL CTE that materializes all registered V2 avatars (humans, orgs, groups) up to @toBlock.
    /// Used as a filter in warmup queries to ensure only registered addresses enter the cache.
    /// Single source of truth — change here to update all warmup queries.
    /// </summary>
    private const string RegisteredAvatarsCte = @"
            registered_avatars AS MATERIALIZED (
                SELECT organization AS avatar FROM ""CrcV2_RegisterOrganization"" WHERE ""blockNumber"" <= @toBlock
                UNION ALL
                SELECT ""group"" AS avatar FROM ""CrcV2_RegisterGroup"" WHERE ""blockNumber"" <= @toBlock
                UNION ALL
                SELECT avatar FROM ""CrcV2_RegisterHuman"" WHERE ""blockNumber"" <= @toBlock
            )";

    public CacheWarmupService(
        ILogger<CacheWarmupService> logger,
        CacheServiceSettings settings,
        CacheServiceState state,
        CacheContainer caches,
        NpgsqlDataSource readonlyDataSource)
    {
        _logger = logger;
        _settings = settings;
        _state = state;
        _caches = caches;
        _readonlyDataSource = readonlyDataSource;
        _gapReplayProcessor = new GapReplayNotificationListener(settings, state, caches, readonlyDataSource);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting cache warmup service");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for warmup to be needed (either initially or after a recovery reset)
                while (_state.WarmupComplete && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    await RunWarmupIterationAsync(stoppingToken);
                    // Don't break - keep monitoring for re-warmup requests
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cache warmup failed");

                    var now = DateTime.UtcNow;
                    if (now - _lastReminderLogTime >= _reminderInterval)
                    {
                        _logger.LogWarning("Cache warmup failed. Service will remain unhealthy until manual restart or DB issue is resolved.");
                        _lastReminderLogTime = now;
                    }

                    try
                    {
                        await DelayAfterFailureAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Cache warmup service stopped");
    }

    protected virtual async Task RunWarmupIterationAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting cache warmup...");

        await WaitForDatabaseAsync(stoppingToken);

        long warmupTarget = 0;

        // Phase 1: Wait for database and initial sync on a shared connection
        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            await WaitForInitialSyncAsync(conn, token);
            ClearCaches();
            warmupTarget = await GetDatabaseHeadAsync(conn, token);
        }, stoppingToken);

        _state.WarmupTargetBlock = warmupTarget;
        _logger.LogInformation("Starting warmup replay up to block {Block}...", warmupTarget);

        // Phase 2: Load all data in parallel — each task opens its own pooled connection
        var warmupSw = System.Diagnostics.Stopwatch.StartNew();

        long warmupTargetTimestamp = 0;

        await using (var snapshot = await CreateWarmupSnapshotAsync(stoppingToken))
        {
            await Task.WhenAll(
                TimedLoadAsync("V1 events", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => ReplayV1EventsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("V2 events", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => ReplayV2EventsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("group memberships", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadGroupMembershipsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("trust relations", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadTrustRelationsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("consented flow flags", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadConsentedFlowFlagsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("avatar metadata", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadAvatarMetadataAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("short names", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadV2ShortNamesAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("balances", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadBalancesAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("warmup target timestamp", async ct =>
                {
                    await WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId, async (c, t) =>
                    {
                        warmupTargetTimestamp = await GetMaxTimestampUpToBlockAsync(c, warmupTarget, t);
                    }, ct);
                }, stoppingToken)
            );
        }

        warmupSw.Stop();
        _logger.LogInformation("All data loaded in {Elapsed:n1}s (parallel)", warmupSw.Elapsed.TotalSeconds);

        _logger.LogInformation("Rebuilding secondary indexes...");
        _caches.RebuildSecondaryIndexes();
        _logger.LogInformation("Secondary indexes rebuilt");

        _state.LastProcessedBlock = warmupTarget;
        _logger.LogInformation("========================================");
        _logger.LogInformation("✓ Warmup replay completed at block {Block}", warmupTarget);
        _logger.LogInformation("========================================");

        // Phase 3: Initialize ring buffer and catch up on a shared connection
        var finalHeadTimestamp = warmupTargetTimestamp;
        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            await InitializeBlockRingBufferAsync(conn, warmupTarget, token);

            var currentHead = await GetDatabaseHeadAsync(conn, token);
            if (currentHead > warmupTarget)
            {
                _logger.LogInformation(
                    "New blocks arrived during warmup ({WarmupTarget} -> {CurrentHead}). Processing gap...",
                    warmupTarget, currentHead);

                await ProcessBlockGapAsync(conn, warmupTarget + 1, currentHead, token);

                _state.LastProcessedBlock = currentHead;
                finalHeadTimestamp = await GetMaxTimestampUpToBlockAsync(conn, currentHead, token);
                _logger.LogInformation("Processed {Count} blocks that arrived during warmup",
                    currentHead - warmupTarget);
            }
            else
            {
                _logger.LogInformation("No new blocks arrived during warmup");
            }
        }, stoppingToken);

        _state.CurrentBlockTimestamp = finalHeadTimestamp;

        _state.WarmupComplete = true;
        _lastReminderLogTime = DateTime.MinValue;

        _logger.LogInformation("Cache warmup completed successfully");
    }

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

    private async Task TimedLoadAsync(string name, Func<CancellationToken, Task> load, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Loading {Name}...", name);
        await load(ct);
        sw.Stop();
        _logger.LogInformation("Loaded {Name} in {Elapsed:n1}s", name, sw.Elapsed.TotalSeconds);
    }

    protected virtual Task DelayAfterFailureAsync(CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(30), ct);

    protected virtual async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for PostgreSQL to be ready...");

        const int maxRetries = 60;
        const int delayMs = 5000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
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
    protected virtual async Task WaitForInitialSyncAsync(NpgsqlConnection conn, CancellationToken ct)
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

        // Create a cancellation source that we can use to cancel the WaitAsync when needed
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        const int waitLogIntervalSeconds = 300;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for notification with periodic logging
                // Use a fresh cancellation token for each wait iteration
                using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                iterationCts.CancelAfter(TimeSpan.FromSeconds(waitLogIntervalSeconds));

                try
                {
                    // WaitAsync will return when a notification is received or when cancelled
                    await conn.WaitAsync(iterationCts.Token);

                    // If we get here without cancellation, check if notification was received
                    if (notificationReceived.Task.IsCompleted)
                    {
                        _logger.LogInformation("Initial sync confirmed complete via NOTIFY event");

                        // Unlisten to free the connection for subsequent operations
                        await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN {_settings.PgNotifyChannel}", conn);
                        await unlistenCmd.ExecuteNonQueryAsync(ct);

                        return;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // This is expected - periodic timeout for logging
                    // The iteration CTS timed out, but the main CT is still valid
                    _logger.LogInformation("Still waiting for first NOTIFY event on channel '{Channel}'...",
                        _settings.PgNotifyChannel);
                }
            }
        }
        finally
        {
            // Cancel any in-progress WaitAsync before trying to UNLISTEN
            // This is important because UNLISTEN cannot execute while connection is in 'Waiting' state
            await waitCts.CancelAsync();

            // Give a small delay for the connection to exit the waiting state
            await Task.Delay(100, CancellationToken.None);

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

    protected virtual async Task<long> GetDatabaseHeadAsync(NpgsqlConnection conn, CancellationToken ct)
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

    protected virtual async Task ReplayV1EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
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
                'CrcV1_Signup' as type
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                o.""organization"" as address,
                NULL as token,
                'CrcV1_OrganizationSignup' as type
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

            if (type == "CrcV1_Signup")
            {
                var tokenKey = token!.ToLowerInvariant();
                avatars[addressKey] = ("CrcV1_Signup", token!);
                tokenOwners[tokenKey] = address;
                humanCount++;
            }
            else
            {
                avatars[addressKey] = ("CrcV1_OrganizationSignup", null);
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

    protected virtual async Task ReplayV2EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
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

        // Load V2 avatars (humans and organizations) using Seed() for efficiency.
        // Excludes stopped avatars so downstream registration checks auto-exclude their data.
        const string avatarSql = @"
            SELECT
                r.""avatar"" as address,
                r.""timestamp"",
                'CrcV2_RegisterHuman' as type
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" <= @toBlock
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_Stopped"" s
                  WHERE s.""avatar"" = r.""avatar""
                    AND s.""blockNumber"" <= @toBlock
              )

            UNION ALL

            SELECT
                r.""organization"" as address,
                r.""timestamp"",
                'CrcV2_RegisterOrganization' as type
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" <= @toBlock
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_Stopped"" s
                  WHERE s.""avatar"" = r.""organization""
                    AND s.""blockNumber"" <= @toBlock
              )";

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

                if (type == "CrcV2_RegisterHuman")
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

        // Only load wrappers whose underlying avatar is registered (matches wrapperMappingQuery.sql)
        const string sql = @"
            WITH " + RegisteredAvatarsCte + @"
            SELECT
                e.""avatar"",
                e.""erc20Wrapper"",
                e.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            INNER JOIN registered_avatars ra ON ra.avatar = e.""avatar""
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
        // Registration filter matches balanceQuery.sql: both account AND tokenAddress must be registered.
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
            INNER JOIN registered_avatars ra_account ON ra_account.avatar = ab.account
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

    protected virtual async Task LoadGroupMembershipsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading group memberships...");

        var targetTimestamp = await GetMaxTimestampUpToBlockAsync(conn, toBlock, ct);

        // Load group memberships using block-bounded latest trust state.
        // Registration filter: member (trustee) must be a registered avatar (matches groupTrustQuery.sql).
        const string sql = @"
            WITH " + RegisteredAvatarsCte + @",
            latest_trust AS (
                SELECT
                    ct.truster,
                    ct.trustee,
                    ct.""expiryTime"",
                    ROW_NUMBER() OVER (
                        PARTITION BY ct.truster, ct.trustee
                        ORDER BY ct.""blockNumber"" DESC, ct.""transactionIndex"" DESC, ct.""logIndex"" DESC
                    ) AS rn
                FROM ""CrcV2_Trust"" ct
                WHERE ct.""blockNumber"" <= @toBlock
            )
            SELECT
                lt.truster AS ""group"",
                lt.trustee AS ""member"",
                lt.""expiryTime""
            FROM latest_trust lt
            INNER JOIN registered_avatars ra ON ra.avatar = lt.trustee
            WHERE lt.rn = 1
              AND lt.""expiryTime"" > @targetTimestamp
              AND EXISTS (
                  SELECT 1
                  FROM ""CrcV2_RegisterGroup"" g
                  WHERE g.""group"" = lt.truster
                    AND g.""blockNumber"" <= @toBlock
              )";

        var memberships = new Dictionary<string, (string Member, long ExpiryTime)>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.Parameters.AddWithValue("targetTimestamp", targetTimestamp);
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

    protected virtual async Task LoadTrustRelationsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading trust relations...");

        var targetTimestamp = await GetMaxTimestampUpToBlockAsync(conn, toBlock, ct);

        // Load V1 trust relations using Seed() for efficiency
        // V1 trust has a "limit" field (0-100 percentage) that we store as the value
        // Filter out limit=0 entries (untrusts) to match incremental path behavior
        // (NotificationListenerService.ProcessV1TrustAsync removes limit=0 entries)
        const string v1Sql = @"
            SELECT ""canSendTo"" as truster, ""user"" as trustee, ""limit""
            FROM (
                SELECT ""user"", ""canSendTo"", ""limit"",
                       ROW_NUMBER() OVER (PARTITION BY ""user"", ""canSendTo""
                                          ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV1_Trust""
                WHERE ""blockNumber"" <= @toBlock
            ) t
            WHERE rn = 1 AND ""limit"" > 0";

        var v1TrustData = new Dictionary<string, long>();

        await using (var v1Cmd = new NpgsqlCommand(v1Sql, conn))
        {
            v1Cmd.Parameters.AddWithValue("toBlock", toBlock);
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

        // Load V2 trust relations using block-bounded latest trust state.
        // Registration filter: both truster and trustee must be registered avatars (matches trustQuery.sql).
        const string v2Sql = @"
            WITH " + RegisteredAvatarsCte + @",
            latest_trust AS (
                SELECT
                    ct.truster,
                    ct.trustee,
                    ct.""expiryTime"",
                    ROW_NUMBER() OVER (
                        PARTITION BY ct.truster, ct.trustee
                        ORDER BY ct.""blockNumber"" DESC, ct.""transactionIndex"" DESC, ct.""logIndex"" DESC
                    ) AS rn
                FROM ""CrcV2_Trust"" ct
                WHERE ct.""blockNumber"" <= @toBlock
            )
            SELECT lt.truster, lt.trustee, lt.""expiryTime""
            FROM latest_trust lt
            INNER JOIN registered_avatars ra1 ON ra1.avatar = lt.truster
            INNER JOIN registered_avatars ra2 ON ra2.avatar = lt.trustee
            WHERE lt.rn = 1
              AND lt.""expiryTime"" > @targetTimestamp";

        var v2TrustData = new Dictionary<string, long>();

        await using (var v2Cmd = new NpgsqlCommand(v2Sql, conn))
        {
            v2Cmd.Parameters.AddWithValue("toBlock", toBlock);
            v2Cmd.Parameters.AddWithValue("targetTimestamp", targetTimestamp);
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

    private static async Task<long> GetMaxTimestampUpToBlockAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT COALESCE(MAX(""timestamp""), 0)
            FROM ""System_Block""
            WHERE ""blockNumber"" <= @toBlock";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };
    }

    protected virtual async Task LoadConsentedFlowFlagsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading consented flow flags...");

        // Registration filter: only load flags for registered avatars (matches consentedFlowQuery.sql).
        const string sql = @"
            WITH " + RegisteredAvatarsCte + @"
            SELECT DISTINCT ON (f.avatar) f.avatar, f.flag
            FROM ""CrcV2_SetAdvancedUsageFlag"" f
            INNER JOIN registered_avatars ra ON ra.avatar = f.avatar
            WHERE f.""blockNumber"" <= @toBlock
            ORDER BY f.avatar, f.""blockNumber"" DESC, f.""transactionIndex"" DESC, f.""logIndex"" DESC";

        var flags = new Dictionary<string, byte[]>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0).ToLowerInvariant();
            var flag = (byte[])reader.GetValue(1);
            flags[avatar] = flag;
        }

        _caches.ConsentedFlowFlags.Seed(flags, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} consented flow flags", flags.Count);
    }

    protected virtual async Task LoadAvatarMetadataAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
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

    protected virtual async Task LoadV2ShortNamesAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
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
    protected virtual async Task InitializeBlockRingBufferAsync(NpgsqlConnection conn, long fromBlock, CancellationToken ct)
    {
        // Clear the buffer before initialization to prevent race conditions.
        // After TriggerFullRewarmup() clears the buffer, the notification listener's
        // async callback can still fire and add newer blocks via UpdateFromBlocks()
        // before we reach this point. Without this clear, Add() would throw
        // "Block number must be greater than current latest" when we try to add
        // blocks from the warmup target range (which are older than what the
        // listener just added).
        _state.BlockRingBuffer.Clear();

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
    protected virtual async Task ProcessBlockGapAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
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
            if (reorgPoint.Value <= _state.WarmupTargetBlock)
            {
                _logger.LogWarning(
                    "Reorg at block {ReorgBlock} during gap processing crossed warmup seed boundary {WarmupTarget}; restarting warmup.",
                    reorgPoint.Value,
                    _state.WarmupTargetBlock);

                RewarmupReset.Trigger(_state, ClearCaches);
                throw new InvalidOperationException("Warmup gap replay crossed warmup seed boundary due to reorg.");
            }

            _logger.LogWarning("Reorg detected at block {ReorgBlock} during gap processing! Rolling back caches...",
                reorgPoint.Value);

            // Rollback all caches
            _caches.RollbackAll(reorgPoint.Value);

            // Rebuild secondary indexes after rollback
            _logger.LogInformation("Rebuilding secondary indexes after rollback...");
            _caches.RebuildSecondaryIndexes();

            // Update state to reflect rolled-back position BEFORE replay.
            // Without this, a failure in ReplayRangeAsync would leave LastProcessedBlock
            // at warmupTarget while caches are actually rolled back to reorgPoint - 1.
            _state.LastProcessedBlock = reorgPoint.Value - 1;

            // Adjust the fromBlock to start processing from the reorg point
            fromBlock = reorgPoint.Value;
        }

        // Replay all domains via the same processor used by the live notification listener.
        await _gapReplayProcessor.ReplayRangeAsync(fromBlock, toBlock, ct);

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

                _caches.V1Avatars.Add(blockNumber, userKey, ("CrcV1_Signup", token));
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

                _caches.V1Avatars.Add(blockNumber, orgKey, ("CrcV1_OrganizationSignup", null));
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

                _caches.V2Avatars.Add(blockNumber, avatarKey, ("CrcV2_RegisterHuman", timestamp));
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

                _caches.V2Avatars.Add(blockNumber, orgKey, ("CrcV2_RegisterOrganization", timestamp));
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

                _caches.UpsertWrapper(blockNumber, wrapperKey, avatar, circlesType);
            }
        }
    }

    /// <summary>
    /// Clears all caches to ensure a clean state for warmup replay.
    /// </summary>
    protected virtual void ClearCaches()
    {
        // Note: BlockRingBuffer is cleared by RewarmupReset.Trigger before this callback.
        // Do not clear it here to avoid confusing double-clear.

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
        _caches.V2LastActivity.Seed(new Dictionary<string, long>());
        _caches.V1TrustRelations.Seed(new Dictionary<string, long>());
        _caches.V2TrustRelations.Seed(new Dictionary<string, long>());
        _caches.ConsentedFlowFlags.Seed(new Dictionary<string, byte[]>());

        // Clear secondary indexes (plain Dictionaries, not RollbackCaches) to prevent
        // stale phantom entries from being served during warmup Phase 2.
        _caches.RebuildSecondaryIndexes();
    }

    /// <summary>
    /// Adapter that exposes NotificationListenerService block-range processing
    /// so warmup gap replay can reuse the exact same per-domain logic.
    /// </summary>
    private sealed class GapReplayNotificationListener : NotificationListenerService
    {
        public GapReplayNotificationListener(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            NpgsqlDataSource readonlyDataSource)
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches, readonlyDataSource)
        {
        }

        public Task ReplayRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
            => ProcessBlockRangeAsync(fromBlock, toBlock, ct);
    }

    protected sealed class WarmupSnapshotContext : IAsyncDisposable
    {
        public NpgsqlConnection ExportConnection { get; }
        public NpgsqlTransaction ExportTransaction { get; }
        public string SnapshotId { get; }

        public WarmupSnapshotContext(NpgsqlConnection exportConnection, NpgsqlTransaction exportTransaction, string snapshotId)
        {
            ExportConnection = exportConnection;
            ExportTransaction = exportTransaction;
            SnapshotId = snapshotId;
        }

        public async ValueTask DisposeAsync()
        {
            await ExportTransaction.DisposeAsync();
            await ExportConnection.DisposeAsync();
        }
    }
}
