using Circles.Cache.Service.Caches;
using Npgsql;
using System.Text.Json;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that listens to PostgreSQL NOTIFY events from the Indexer.
/// Processes block range notifications and updates caches in real-time.
/// </summary>
public class NotificationListenerService : BackgroundService
{
    private readonly ILogger<NotificationListenerService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;

    public NotificationListenerService(
        ILogger<NotificationListenerService> logger,
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
        // Wait for warmup to complete before starting listener
        while (!_state.WarmupComplete && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Waiting for cache warmup to complete before starting listener...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("Starting PostgreSQL LISTEN/NOTIFY listener on channel: {Channel}",
            _settings.PgNotifyChannel);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenForNotificationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Notification listener shutting down...");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in notification listener. Reconnecting in 5 seconds...");
                _state.ListenerConnected = false;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ListenForNotificationsAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_settings.PostgresConnectionString);
        await conn.OpenAsync(ct);

        conn.Notification += async (sender, args) =>
        {
            try
            {
                await HandleNotificationAsync(args.Payload, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling notification: {Payload}", args.Payload);
            }
        };

        await using var cmd = new NpgsqlCommand($"LISTEN {_settings.PgNotifyChannel}", conn);
        await cmd.ExecuteNonQueryAsync(ct);

        _state.ListenerConnected = true;
        _logger.LogInformation("Successfully connected to PostgreSQL LISTEN channel: {Channel}",
            _settings.PgNotifyChannel);

        // Keep connection alive and wait for notifications
        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct);
        }
    }

    private async Task HandleNotificationAsync(string payload, CancellationToken ct)
    {
        _logger.LogDebug("Received notification: {Payload}", payload);

        BlockRangeNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<BlockRangeNotification>(payload);
            if (notification == null)
            {
                _logger.LogWarning("Failed to deserialize notification payload: {Payload}", payload);
                return;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in notification payload: {Payload}", payload);
            return;
        }

        var fromBlock = notification.FromBlock;
        var toBlock = notification.ToBlock;

        _logger.LogInformation("Processing block range {FromBlock} to {ToBlock}", fromBlock, toBlock);

        // Check if this is a reorg (block range is before our last processed block)
        if (toBlock < _state.LastProcessedBlock)
        {
            _logger.LogWarning("Detected reorg: toBlock {ToBlock} < lastProcessedBlock {LastProcessedBlock}. Rolling back caches...",
                toBlock, _state.LastProcessedBlock);

            // Rollback all caches to the reorg point
            _caches.RollbackAll(toBlock + 1);
            _state.LastProcessedBlock = toBlock;
        }

        // Process new blocks
        await ProcessBlockRangeAsync(fromBlock, toBlock, ct);

        _state.LastProcessedBlock = toBlock;
        _logger.LogDebug("Updated LastProcessedBlock to {Block}", toBlock);
    }

    private async Task ProcessBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
        await conn.OpenAsync(ct);

        // Process V1 events in this range
        await ProcessV1EventsAsync(conn, fromBlock, toBlock, ct);

        // Process V2 events in this range
        await ProcessV2EventsAsync(conn, fromBlock, toBlock, ct);

        _logger.LogInformation("Successfully processed blocks {FromBlock} to {ToBlock}", fromBlock, toBlock);
    }

    private async Task ProcessV1EventsAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Signups
        const string humanSignupSql = @"
            SELECT s.""blockNumber"", s.""user"", s.""token""
            FROM ""CrcV1"".""Signup"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var user = reader.GetString(1);
            var token = reader.GetString(2);

            _caches.V1Avatars.Add(blockNumber, user, ("Human", token));
            _caches.V1TokenOwnerByToken.Add(blockNumber, token, user);
            count++;
        }

        await reader.CloseAsync();

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 human signups", count);
        }

        // Process V1 Organization Signups
        const string orgSignupSql = @"
            SELECT o.""blockNumber"", o.""organization""
            FROM ""CrcV1"".""OrganizationSignup"" o
            WHERE o.""blockNumber"" >= @fromBlock AND o.""blockNumber"" <= @toBlock
            ORDER BY o.""blockNumber"", o.""transactionIndex"", o.""logIndex""";

        await using var orgCmd = new NpgsqlCommand(orgSignupSql, conn);
        orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
        orgCmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var orgReader = await orgCmd.ExecuteReaderAsync(ct);
        var orgCount = 0;

        while (await orgReader.ReadAsync(ct))
        {
            var blockNumber = orgReader.GetInt64(0);
            var organization = orgReader.GetString(1);

            _caches.V1Avatars.Add(blockNumber, organization, ("Organization", null));
            orgCount++;
        }

        if (orgCount > 0)
        {
            _logger.LogDebug("Processed {Count} V1 organization signups", orgCount);
        }
    }

    private async Task ProcessV2EventsAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterHuman
        await ProcessV2RegisterHumanAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterOrganization
        await ProcessV2RegisterOrganizationAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterGroup
        await ProcessV2RegisterGroupAsync(conn, fromBlock, toBlock, ct);

        // Process V2 ERC20WrapperDeployed
        await ProcessV2Erc20WrapperDeployedAsync(conn, fromBlock, toBlock, ct);
    }

    private async Task ProcessV2RegisterHumanAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""avatar""
            FROM ""CrcV2"".""RegisterHuman"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var avatar = reader.GetString(2);

            _caches.V2Avatars.Add(blockNumber, avatar, ("Human", timestamp));
            count++;
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
            FROM ""CrcV2"".""RegisterOrganization"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var organization = reader.GetString(2);

            _caches.V2Avatars.Add(blockNumber, organization, ("Organization", timestamp));
            count++;
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 organization registrations", count);
        }
    }

    private async Task ProcessV2RegisterGroupAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint""
            FROM ""CrcV2"".""RegisterGroup"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var group = reader.GetString(1);
            var name = reader.GetString(2);
            var mint = reader.GetString(3);

            _caches.Groups.Add(blockNumber, group, (name, mint));
            count++;
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 group registrations", count);
        }
    }

    private async Task ProcessV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper""
            FROM ""CrcV2"".""ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" >= @fromBlock AND e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var avatar = reader.GetString(1);
            var erc20Wrapper = reader.GetString(2);

            _caches.Erc20WrapperAddresses.Add(blockNumber, avatar, erc20Wrapper);
            count++;
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 ERC20 wrapper deployments", count);
        }
    }

    private record BlockRangeNotification(long FromBlock, long ToBlock, long Timestamp);
}
