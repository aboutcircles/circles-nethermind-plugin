using Circles.Cache.Service.Caches;
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
        try
        {
            _logger.LogInformation("Starting cache warmup...");

            await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
            await conn.OpenAsync(stoppingToken);

            // Get the current database head block
            var dbHead = await GetDatabaseHeadAsync(conn, stoppingToken);
            _logger.LogInformation("Database head block: {Block}", dbHead);

            // Replay V1 events
            await ReplayV1EventsAsync(conn, dbHead, stoppingToken);

            // Replay V2 events
            await ReplayV2EventsAsync(conn, dbHead, stoppingToken);

            // Mark warmup as complete
            _state.WarmupComplete = true;
            _state.LastProcessedBlock = dbHead;

            _logger.LogInformation("Cache warmup completed successfully. Processed up to block {Block}", dbHead);
            _logger.LogInformation("Cache statistics: {Stats}",
                System.Text.Json.JsonSerializer.Serialize(_caches.GetStatistics()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache warmup failed");
            throw;
        }
    }

    private async Task<long> GetDatabaseHeadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT COALESCE(MAX(""blockNumber""), 0)
            FROM ""System"".""Block""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long blockNumber ? blockNumber : 0L;
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
        // Replay human signups
        const string humanSignupSql = @"
            SELECT
                s.""blockNumber"",
                s.""user"",
                s.""token""
            FROM ""CrcV1"".""Signup"" s
            WHERE s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var user = reader.GetString(1);
            var token = reader.GetString(2);

            // Add to V1Avatars cache
            _caches.V1Avatars.Add(blockNumber, user, ("Human", token));

            // Add to V1TokenOwnerByToken cache (reverse mapping)
            _caches.V1TokenOwnerByToken.Add(blockNumber, token, user);

            count++;
        }

        await reader.CloseAsync();

        _logger.LogInformation("Replayed {Count} V1 human signups", count);

        // Replay organization signups
        const string orgSignupSql = @"
            SELECT
                o.""blockNumber"",
                o.""organization""
            FROM ""CrcV1"".""OrganizationSignup"" o
            WHERE o.""blockNumber"" <= @toBlock
            ORDER BY o.""blockNumber"", o.""transactionIndex"", o.""logIndex""";

        await using var orgCmd = new NpgsqlCommand(orgSignupSql, conn);
        orgCmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var orgReader = await orgCmd.ExecuteReaderAsync(ct);
        var orgCount = 0;

        while (await orgReader.ReadAsync(ct))
        {
            var blockNumber = orgReader.GetInt64(0);
            var organization = orgReader.GetString(1);

            // Add to V1Avatars cache (organizations don't have tokens)
            _caches.V1Avatars.Add(blockNumber, organization, ("Organization", null));

            orgCount++;
        }

        _logger.LogInformation("Replayed {Count} V1 organization signups", orgCount);
    }

    private async Task ReplayV2EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Replaying V2 events...");

        // Replay V2 RegisterHuman
        await ReplayV2RegisterHumanAsync(conn, toBlock, ct);

        // Replay V2 RegisterOrganization
        await ReplayV2RegisterOrganizationAsync(conn, toBlock, ct);

        // Replay V2 RegisterGroup
        await ReplayV2RegisterGroupAsync(conn, toBlock, ct);

        // Replay V2 ERC20WrapperDeployed
        await ReplayV2Erc20WrapperDeployedAsync(conn, toBlock, ct);

        _logger.LogInformation("V2 event replay completed");
    }

    private async Task ReplayV2RegisterHumanAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                r.""blockNumber"",
                r.""timestamp"",
                r.""avatar""
            FROM ""CrcV2"".""RegisterHuman"" r
            WHERE r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var avatar = reader.GetString(2);

            // Add to V2Avatars cache
            _caches.V2Avatars.Add(blockNumber, avatar, ("Human", timestamp));

            count++;
        }

        _logger.LogInformation("Replayed {Count} V2 human registrations", count);
    }

    private async Task ReplayV2RegisterOrganizationAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                r.""blockNumber"",
                r.""timestamp"",
                r.""organization""
            FROM ""CrcV2"".""RegisterOrganization"" r
            WHERE r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var organization = reader.GetString(2);

            // Add to V2Avatars cache
            _caches.V2Avatars.Add(blockNumber, organization, ("Organization", timestamp));

            count++;
        }

        _logger.LogInformation("Replayed {Count} V2 organization registrations", count);
    }

    private async Task ReplayV2RegisterGroupAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                r.""blockNumber"",
                r.""group"",
                r.""name"",
                r.""mint""
            FROM ""CrcV2"".""RegisterGroup"" r
            WHERE r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var group = reader.GetString(1);
            var name = reader.GetString(2);
            var mint = reader.GetString(3);

            // Add to Groups cache
            _caches.Groups.Add(blockNumber, group, (name, mint));

            count++;
        }

        _logger.LogInformation("Replayed {Count} V2 group registrations", count);
    }

    private async Task ReplayV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                e.""blockNumber"",
                e.""avatar"",
                e.""erc20Wrapper""
            FROM ""CrcV2"".""ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var avatar = reader.GetString(1);
            var erc20Wrapper = reader.GetString(2);

            // Add to Erc20WrapperAddresses cache
            _caches.Erc20WrapperAddresses.Add(blockNumber, avatar, erc20Wrapper);

            count++;
        }

        _logger.LogInformation("Replayed {Count} V2 ERC20 wrapper deployments", count);
    }
}
