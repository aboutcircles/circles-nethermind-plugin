using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Metadata loaders for CacheWarmupService.
/// Group memberships, V1/V2 trust relations, consented flow flags, V1/V2 CID maps, and
/// V2 short names — each filtered to registered avatars and seeded at the warmup target block.
/// </summary>
public partial class CacheWarmupService
{
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
            INNER JOIN registered_avatars ra_member ON ra_member.avatar = lt.trustee
            INNER JOIN registered_avatars ra_group ON ra_group.avatar = lt.truster
            INNER JOIN ""CrcV2_RegisterGroup"" g ON g.""group"" = lt.truster AND g.""blockNumber"" <= @toBlock
            WHERE lt.rn = 1
              AND lt.""expiryTime"" > @targetTimestamp";

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
}
