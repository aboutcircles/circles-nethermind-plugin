using System.Numerics;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Trust relation methods for CirclesRpcModule.
/// Handles V1 and V2 trust queries and common trust calculations.
/// </summary>
public partial class CirclesRpcModule
{
    public async Task<TrustRelationsResponse> GetTrustRelations(string address)
    {
        address = ValidateAndNormalizeAddress(address);

        // If cache service is enabled, try using it first (V1 only for backward compatibility)
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for trust relations query for {Address}", address);

                var cacheResult = await _cacheServiceClient.GetTrustRelationsAsync(address, version: 1);

                if (cacheResult != null)
                {
                    // Convert cache response to RPC response format
                    // V1 trust uses "limit" field (0-100 percentage), stored in ExpiryTime for simplicity
                    var cacheTrusts = cacheResult.Trusts
                        .Select(t => new TrustRelation(User: t.Trustee, Limit: (int)t.ExpiryTime))
                        .ToArray();
                    var cacheTrustedBy = cacheResult.TrustedBy
                        .Select(t => new TrustRelation(User: t.Truster, Limit: (int)t.ExpiryTime))
                        .ToArray();

                    return new TrustRelationsResponse(
                        User: address,
                        Trusts: cacheTrusts,
                        TrustedBy: cacheTrustedBy
                    );
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service trust relations query failed, falling back to database");
                // Fall through to database query below
            }
        }

        // Fallback: use traditional database approach
        _logger?.LogDebug("Using database for trust relations query for {Address}", address);

        await using var connection = await CreateConnectionAsync();
        // NOTE: This query intentionally includes limit=0 entries (untrusts) to match production behavior.
        // Semantically, limit=0 means "untrusted" and arguably shouldn't be returned as a trust relation.
        // Both this fallback and the cache warmup (CacheWarmupService.LoadTrustRelationsAsync) now include
        // limit=0 entries for production parity. TODO: Consider filtering limit=0 in both places in future.
        const string sql = @"
            select ""user"",
                   ""canSendTo"",
                   ""limit""
            from (
                     select ""blockNumber"",
                            ""transactionIndex"",
                            ""logIndex"",
                            ""user"",
                            ""canSendTo"",
                            ""limit"",
                            row_number() over (partition by ""user"", ""canSendTo"" order by ""blockNumber"" desc, ""transactionIndex"" desc, ""logIndex"" desc) as rn
                     from ""CrcV1_Trust""
                 ) t
            where rn = 1
              and (""user"" = @address
               or ""canSendTo"" = @address)
        ";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address);
        var trusts = new List<TrustRelation>();
        var trustedBy = new List<TrustRelation>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var user = reader.GetString(0);
            var canSendTo = reader.GetString(1);
            // V1 trust limits are percentages (0-100), stored as uint256 in contract but always fit in int32
            var limit = Convert.ToInt32(reader.GetValue(2));

            // V1 Trust semantics: canSendTo = truster, user = trustee
            if (canSendTo.Equals(address, StringComparison.OrdinalIgnoreCase))
            {
                trusts.Add(new TrustRelation(User: user, Limit: limit));
            }
            else
            {
                trustedBy.Add(new TrustRelation(User: canSendTo, Limit: limit));
            }
        }
        return new TrustRelationsResponse(User: address, Trusts: trusts.ToArray(), TrustedBy: trustedBy.ToArray());
    }

    private async Task<bool> IsV2Human(string address)
    {
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                var avatarInfo = await _cacheServiceClient.GetAvatarInfoAsync(address);
                return avatarInfo is { Version: 2, Type: "CrcV2_RegisterHuman" };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service IsV2Human failed, falling back to database");
            }
        }

        await using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT 1 FROM ""CrcV2_RegisterHuman"" WHERE avatar = @address";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address);
        var result = await command.ExecuteScalarAsync();
        return result != null;
    }

    public async Task<AggregatedTrustRelation[]> GetAggregatedTrustRelations(string avatar)
    {
        var normalizedAvatar = ValidateAndNormalizeAddress(avatar, nameof(avatar));

        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for aggregated trust relations for {Avatar}", normalizedAvatar);

                var cacheResult = await _cacheServiceClient.GetTrustRelationsAsync(normalizedAvatar, version: 2);
                if (cacheResult != null)
                {
                    // Collect all unique counterpart addresses for batch avatar info lookup
                    var counterparts = new Dictionary<string, (bool trusts, bool trustedBy, long expiryTime, long timestamp)>(StringComparer.OrdinalIgnoreCase);

                    foreach (var t in cacheResult.Trusts)
                    {
                        if (t.Trustee.Equals(normalizedAvatar, StringComparison.OrdinalIgnoreCase)) continue;
                        if (counterparts.TryGetValue(t.Trustee, out var existing))
                            counterparts[t.Trustee] = (true, existing.trustedBy, Math.Max(t.ExpiryTime, existing.expiryTime), Math.Max(t.Timestamp, existing.timestamp));
                        else
                            counterparts[t.Trustee] = (true, false, t.ExpiryTime, t.Timestamp);
                    }

                    foreach (var t in cacheResult.TrustedBy)
                    {
                        if (t.Truster.Equals(normalizedAvatar, StringComparison.OrdinalIgnoreCase)) continue;
                        if (counterparts.TryGetValue(t.Truster, out var existing))
                            counterparts[t.Truster] = (existing.trusts, true, Math.Max(t.ExpiryTime, existing.expiryTime), Math.Max(t.Timestamp, existing.timestamp));
                        else
                            counterparts[t.Truster] = (false, true, t.ExpiryTime, t.Timestamp);
                    }

                    // Batch-fetch avatar types for all counterparts
                    var counterpartAddresses = counterparts.Keys.ToArray();
                    var avatarInfos = counterpartAddresses.Length > 0
                        ? await _cacheServiceClient.GetAvatarInfoBatchAsync(counterpartAddresses)
                        : Array.Empty<CacheServiceClient.Models.AvatarInfoResponse?>();

                    var avatarTypeMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < counterpartAddresses.Length; i++)
                    {
                        var info = avatarInfos[i];
                        // Normalize cache short names to full canonical format
                        avatarTypeMap[counterpartAddresses[i]] = info?.Type != null
                            ? NormalizeAvatarType(info.Type)
                            : null;
                    }

                    // Build aggregated relations
                    var cacheAggregated = new List<AggregatedTrustRelation>(counterparts.Count);
                    foreach (var (counterpart, (trusts, trustedBy, expiryTime, timestamp)) in counterparts)
                    {
                        var relation = (trusts, trustedBy) switch
                        {
                            (true, true) => "mutuallyTrusts",
                            (true, false) => "trusts",
                            (false, true) => "trustedBy",
                            _ => "trustedBy" // shouldn't happen
                        };

                        avatarTypeMap.TryGetValue(counterpart, out var objectAvatarType);

                        cacheAggregated.Add(new AggregatedTrustRelation(
                            SubjectAvatar: normalizedAvatar,
                            Relation: relation,
                            ObjectAvatar: counterpart,
                            Timestamp: timestamp,
                            ExpiryTime: expiryTime,
                            ObjectAvatarType: objectAvatarType
                        ));
                    }

                    return cacheAggregated.ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service aggregated trust relations failed, falling back to database");
            }
        }

        await using var connection = await CreateConnectionAsync();

        // Query V2 trust relations for this avatar
        const string sql = @"
            SELECT
                t.truster,
                t.trustee,
                t.""expiryTime"",
                t.timestamp,
                a.type as avatar_type
            FROM ""V_CrcV2_TrustRelations"" t
            LEFT JOIN ""V_CrcV2_Avatars"" a
                ON a.avatar = CASE
                    WHEN t.truster = @avatar THEN t.trustee
                    ELSE t.truster
                END
            WHERE t.truster = @avatar OR t.trustee = @avatar
            ORDER BY t.timestamp DESC";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("avatar", normalizedAvatar);

        // Group by counterpart
        var trustBucket = new Dictionary<string, List<(string truster, string trustee, long expiryTime, long timestamp, string? avatarType)>>();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var truster = reader.GetString(0);
            var trustee = reader.GetString(1);
            var expiryTimeBig = reader.GetFieldValue<BigInteger>(2);
            var expiryTime = expiryTimeBig > long.MaxValue ? long.MaxValue : (long)expiryTimeBig;
            var timestamp = reader.GetInt64(3);
            var avatarType = reader.IsDBNull(4) ? null : reader.GetString(4);

            // Determine counterpart (not the avatar itself)
            var counterpart = truster.Equals(normalizedAvatar, StringComparison.OrdinalIgnoreCase)
                ? trustee
                : truster;

            if (counterpart.Equals(normalizedAvatar, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Skip self-trust
            }

            if (!trustBucket.ContainsKey(counterpart))
            {
                trustBucket[counterpart] = new List<(string, string, long, long, string?)>();
            }

            trustBucket[counterpart].Add((truster, trustee, expiryTime, timestamp, avatarType));
        }

        // Determine relation type and create aggregated response
        var result = new List<AggregatedTrustRelation>();

        foreach (var (counterpart, rows) in trustBucket)
        {
            if (rows.Count == 0) continue;

            // Get max timestamp and expiryTime for this counterpart
            var maxTimestamp = rows.Max(r => r.timestamp);
            var maxExpiryTime = rows.Max(r => r.expiryTime);
            var avatarType = rows.FirstOrDefault(r => r.avatarType != null).avatarType;

            // Determine relation type based on number of rows and direction
            string relationType;
            if (rows.Count == 2)
            {
                // Bidirectional trust = mutual
                relationType = "mutuallyTrusts";
            }
            else if (rows.Count == 1)
            {
                var row = rows[0];
                if (row.trustee.Equals(normalizedAvatar, StringComparison.OrdinalIgnoreCase))
                {
                    // Someone trusts this avatar
                    relationType = "trustedBy";
                }
                else if (row.truster.Equals(normalizedAvatar, StringComparison.OrdinalIgnoreCase))
                {
                    // This avatar trusts someone
                    relationType = "trusts";
                }
                else
                {
                    throw new InvalidOperationException("Unexpected trust relation - couldn't determine direction");
                }
            }
            else
            {
                throw new InvalidOperationException($"Unexpected number of trust rows for counterpart: {rows.Count}");
            }

            // Normalize avatar type to full canonical format (CrcV2_RegisterHuman etc.)
            // DB already stores full names. Cache stores short names → normalize.
            string? objectAvatarType = avatarType != null
                ? NormalizeAvatarType(avatarType)
                : null;

            result.Add(new AggregatedTrustRelation(
                SubjectAvatar: normalizedAvatar,
                Relation: relationType,
                ObjectAvatar: counterpart,
                Timestamp: maxTimestamp,
                ExpiryTime: maxExpiryTime,
                ObjectAvatarType: objectAvatarType
            ));
        }

        return result.ToArray();
    }

    public async Task<CommonTrustResponse> GetCommonTrust(string address1, string address2, int? version = null)
    {
        address1 = ValidateAndNormalizeAddress(address1, nameof(address1));
        address2 = ValidateAndNormalizeAddress(address2, nameof(address2));

        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for common trust between {A1} and {A2}", address1, address2);
                var addr1 = address1;
                var addr2 = address2;

                // Determine if address2 is V2 human (uses cache internally)
                var isAddr2V2Human = await IsV2Human(addr2);

                var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (version == 1 || version == null)
                {
                    // V1 shared outgoing: address1.Trusts ∩ address2.Trusts
                    var (trust1, trust2) = await FetchTrustPairAsync(addr1, addr2, 1);
                    if (trust1 != null && trust2 != null)
                    {
                        var set1 = new HashSet<string>(trust1.Trusts.Select(t => t.Trustee), StringComparer.OrdinalIgnoreCase);
                        set1.ExceptWith(new[] { addr1, addr2 });
                        var set2 = new HashSet<string>(trust2.Trusts.Select(t => t.Trustee), StringComparer.OrdinalIgnoreCase);
                        set1.IntersectWith(set2);
                        common.UnionWith(set1);
                    }
                }

                if (version == 2 || version == null)
                {
                    if (isAddr2V2Human)
                    {
                        // Safer V2: address1.Trusts ∩ address2.TrustedBy (find intermediaries)
                        var (trust1, trust2) = await FetchTrustPairAsync(addr1, addr2, 2);
                        if (trust1 != null && trust2 != null)
                        {
                            var set1 = new HashSet<string>(trust1.Trusts.Select(t => t.Trustee), StringComparer.OrdinalIgnoreCase);
                            set1.ExceptWith(new[] { addr1, addr2 });
                            var set2 = new HashSet<string>(trust2.TrustedBy.Select(t => t.Truster), StringComparer.OrdinalIgnoreCase);
                            set1.IntersectWith(set2);
                            common.UnionWith(set1);
                        }
                    }
                    else
                    {
                        // V2 shared outgoing: address1.Trusts ∩ address2.Trusts
                        var (trust1, trust2) = await FetchTrustPairAsync(addr1, addr2, 2);
                        if (trust1 != null && trust2 != null)
                        {
                            var set1 = new HashSet<string>(trust1.Trusts.Select(t => t.Trustee), StringComparer.OrdinalIgnoreCase);
                            set1.ExceptWith(new[] { addr1, addr2 });
                            var set2 = new HashSet<string>(trust2.Trusts.Select(t => t.Trustee), StringComparer.OrdinalIgnoreCase);
                            set1.IntersectWith(set2);
                            common.UnionWith(set1);
                        }
                    }
                }

                return new CommonTrustResponse(Address1: addr1, Address2: addr2, CommonTrusts: common.ToArray());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service common trust query failed, falling back to database");
            }
        }

        var address2IsV2Human = await IsV2Human(address2);

        const string saferV2 = @"
            select distinct a.trustee as mid
            from ""V_Crc_TrustRelations"" a
            join ""V_Crc_TrustRelations"" b
              on a.trustee = b.truster
            where a.truster = @address1
              and b.trustee = @address2
              and a.trustee not in (@address1, @address2)
              and a.version = 2
              and b.version = 2
        ";

        const string sharedOutV1 = @"
            select distinct a.trustee as mid
            from ""V_Crc_TrustRelations"" a
            join ""V_Crc_TrustRelations"" b
              on a.trustee = b.trustee
            where a.truster = @address1
              and b.truster = @address2
              and a.trustee not in (@address1, @address2)
              and a.version = 1
              and b.version = 1
        ";

        const string sharedOutV2 = @"
            select distinct a.trustee as mid
            from ""V_Crc_TrustRelations"" a
            join ""V_Crc_TrustRelations"" b
              on a.trustee = b.trustee
            where a.truster = @address1
              and b.truster = @address2
              and a.trustee not in (@address1, @address2)
              and a.version = 2
              and b.version = 2
        ";

        string sql;
        if (version == 1)
        {
            sql = sharedOutV1;
        }
        else if (version == 2)
        {
            sql = address2IsV2Human ? saferV2 : sharedOutV2;
        }
        else
        {
            sql = address2IsV2Human
                ? $"{sharedOutV1} union {saferV2}"
                : $"{sharedOutV1} union {sharedOutV2}";
        }

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address1", address1);
        command.Parameters.AddWithValue("address2", address2);

        var commonTrusts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            commonTrusts.Add(reader.GetString(0));
        }
        return new CommonTrustResponse(Address1: address1, Address2: address2, CommonTrusts: commonTrusts.ToArray());
    }

    /// <summary>
    /// Fetches trust relations for two addresses in parallel from the cache service.
    /// </summary>
    private async Task<(CacheServiceClient.Models.TrustRelationsResponse? trust1, CacheServiceClient.Models.TrustRelationsResponse? trust2)>
        FetchTrustPairAsync(string address1, string address2, int version)
    {
        var task1 = _cacheServiceClient!.GetTrustRelationsAsync(address1, version);
        var task2 = _cacheServiceClient!.GetTrustRelationsAsync(address2, version);
        await Task.WhenAll(task1, task2);
        return (task1.Result, task2.Result);
    }
}
