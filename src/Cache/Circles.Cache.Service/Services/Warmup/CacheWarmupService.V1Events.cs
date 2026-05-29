using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// V1 event replay for CacheWarmupService.
/// Loads V1 signups (human and organization) and seeds the V1Avatars + V1TokenOwnerByToken
/// caches at the warmup target block.
/// </summary>
public partial class CacheWarmupService
{
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
}
