using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// V2 event replay for CacheWarmupService.
/// Loads V2 avatar registrations (humans, organizations, groups) and ERC20 wrapper deployments
/// and seeds the corresponding caches at the warmup target block.
/// </summary>
public partial class CacheWarmupService
{
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
        // Stopped avatars are NOT excluded — Hub.sol stop() only prevents minting,
        // it does not deregister the avatar. They can still transfer and be flow vertices.
        const string avatarSql = @"
            SELECT
                r.""avatar"" as address,
                r.""timestamp"",
                'CrcV2_RegisterHuman' as type
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                r.""organization"" as address,
                r.""timestamp"",
                'CrcV2_RegisterOrganization' as type
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

        // Key by wrapper address (not avatar) to support avatars with multiple wrappers —
        // an avatar can have both DemurrageCircles and InflationaryCircles wrappers deployed.
        var wrappers = new Dictionary<string, (string Avatar, CirclesType CirclesType)>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.CommandTimeout = 300;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0);
            var erc20Wrapper = reader.GetString(1);
            var circlesType = (CirclesType)reader.GetInt32(2);

            // Key by wrapper address for direct lookup
            var wrapperKey = erc20Wrapper.ToLowerInvariant();
            wrappers[wrapperKey] = (avatar.ToLowerInvariant(), circlesType);
        }

        _caches.Erc20WrapperAddresses.Seed(wrappers, _state.WarmupTargetBlock);
        _logger.LogInformation("Loaded {Count} V2 ERC20 wrapper deployments", wrappers.Count);
    }
}
