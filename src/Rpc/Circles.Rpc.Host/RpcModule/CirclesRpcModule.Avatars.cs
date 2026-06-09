using Circles.Common;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Avatar information methods for CirclesRpcModule.
/// Handles avatar info queries for both V1 and V2 avatars.
/// </summary>
public partial class CirclesRpcModule
{
    public async Task<AvatarInfo> GetAvatarInfo(string address)
    {
        address = ValidateAndNormalizeAddress(address);

        var results = await GetAvatarInfoBatchInternal(new[] { address });
        var result = results[0];

        if (result == null)
        {
            throw new InvalidOperationException($"No avatar found for address {address}");
        }

        return result;
    }

    public async Task<AvatarInfo[]> GetAvatarInfoBatch(string[] addresses)
    {
        for (int i = 0; i < addresses.Length; i++)
            addresses[i] = ValidateAndNormalizeAddress(addresses[i], $"addresses[{i}]");

        var results = await GetAvatarInfoBatchInternal(addresses);
        return results.Where(r => r != null).ToArray()!;
    }

    private async Task<AvatarInfo?[]> GetAvatarInfoBatchInternal(string[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        // If cache service is enabled, try using it first.
        // Block-pinned requests (X-Max-Block-Number present) bypass the head-only cache and fall
        // through to the DB path. PARTIAL PIN: avatar existence/type/name AND the metadata CID now
        // pin via the V_CrcV2_Avatars twin (FetchV2AvatarsAsync reads the view's latest-wins
        // "cidV0Digest" column). The short name is still computed from an unfiltered base-table join
        // (CrcV2_RegisterShortName) — it has no twin column yet, so that single secondary field
        // reflects head. This is a strict improvement over the previous head-only cache result and
        // is inert without the header.
        if (_settings.UseCacheService && _cacheServiceClient != null && GetMaxBlockNumberFromHeader() is null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for avatar info batch query ({Count} addresses)", addresses.Length);

                var cacheResults = await _cacheServiceClient.GetAvatarInfoBatchAsync(addresses);

                // Convert cache results to AvatarInfo
                var cacheResult = new AvatarInfo?[addresses.Length];
                for (int i = 0; i < cacheResults.Length; i++)
                {
                    var cacheInfo = cacheResults[i];
                    if (cacheInfo != null)
                    {
                        cacheResult[i] = new AvatarInfo(
                            Version: cacheInfo.Version,
                            Type: NormalizeAvatarType(cacheInfo.Type),
                            Avatar: cacheInfo.Avatar,
                            TokenId: cacheInfo.TokenId ?? cacheInfo.Avatar,
                            HasV1: cacheInfo.HasV1,
                            V1Token: cacheInfo.V1Token,
                            CidV0Digest: "",
                            CidV0: cacheInfo.CidV0,
                            IsHuman: cacheInfo.IsHuman,
                            Name: cacheInfo.Name,
                            Symbol: cacheInfo.Symbol ?? ""
                        );
                    }
                }

                return cacheResult;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service query failed, falling back to database");
                // Fall through to database query below
            }
        }

        // Fallback: use traditional database approach
        _logger?.LogDebug("Using database for avatar info batch query ({Count} addresses)", addresses.Length);
        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();
        var result = new AvatarInfo?[addresses.Length];

        // Run V1 and V2 queries in parallel using separate connections
        var v2Task = FetchV2AvatarsAsync(lowerAddresses);
        var v1Task = FetchV1AvatarsAsync(lowerAddresses);

        await Task.WhenAll(v2Task, v1Task);

        var v2AvatarMap = await v2Task;
        var (v1AvatarMap, v1CidMap) = await v1Task;

        // Populate results
        for (int i = 0; i < lowerAddresses.Length; i++)
        {
            var addr = lowerAddresses[i];

            // Check V2 first (takes priority)
            if (v2AvatarMap.TryGetValue(addr, out var v2Avatar))
            {
                // If this address also has V1, merge the info
                if (v1AvatarMap.TryGetValue(addr, out var v1Avatar))
                {
                    result[i] = v2Avatar with
                    {
                        HasV1 = true,
                        V1Token = v1Avatar.V1Token,
                        CidV0 = v2Avatar.CidV0 ?? (v1CidMap.TryGetValue(addr, out var v1Cid) ? v1Cid : null)
                    };
                }
                else
                {
                    result[i] = v2Avatar;
                }
            }
            // If no V2, check V1
            else if (v1AvatarMap.TryGetValue(addr, out var v1Avatar))
            {
                result[i] = v1Avatar with
                {
                    CidV0 = v1CidMap.TryGetValue(addr, out var v1Cid) ? v1Cid : null
                };
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    /// <summary>
    /// V2 avatar info query used by the DB-fallback path (cache miss / cache failure / block-pinned).
    ///
    /// CID source is the view's own latest-wins "cidV0Digest" column. V_CrcV2_Avatars derives it
    /// via DISTINCT ON (avatar) / LATERAL ORDER BY blockNumber DESC LIMIT 1, and the circles_at_block
    /// twin adds "blockNumber &lt;= pin_block()". Reading it here — instead of a separate, unfiltered,
    /// un-ordered LEFT JOIN on CrcV2_UpdateMetadataDigest — (1) pins the CID under X-Max-Block-Number,
    /// (2) removes a nondeterministic multi-row join (the old join picked an arbitrary metadata row
    /// when an avatar had several updates), and (3) aligns the DB fallback with the cache path, which
    /// also returns the latest CID.
    ///
    /// Symbol source is the group's "CrcV2_RegisterGroup".symbol (a text column), matching the cache
    /// path which serves the group's registration symbol (e.g. "gCRC") for groups and null/"" for
    /// non-groups. The earlier code mis-sourced Symbol from "CrcV2_RegisterShortName".shortName — a
    /// numeric column that is (a) the wrong field (the short name is not exposed by this RPC; AvatarInfo
    /// has no ShortName) and (b) a hard crash, since reader.GetString on a numeric throws
    /// InvalidCastException. That latent defect was dormant only because head traffic is served by the
    /// cache; block-pinned reads (and cache-failure fallbacks) hit this DB path and 500'd for every
    /// avatar that had a registered short name. symbol is immutable post-registration and existence is
    /// gated by the pinned V_CrcV2_Avatars row, so the unpinned base-table join is correct. The join is
    /// 1:1 — Hub.registerGroup reverts if the avatar is already registered, so there is exactly one
    /// CrcV2_RegisterGroup row per group and the LEFT JOIN cannot fan out (and matches null → "" for
    /// non-groups).
    ///
    /// Column order is contractual: avatar(0), name(1), type(2), symbol(3), cidV0Digest(4).
    /// Exposed as <c>internal const</c> so a Testcontainers regression test runs the exact production
    /// SQL (no drift) and proves the numeric-shortName crash class can never return.
    /// </summary>
    internal const string V2AvatarInfoSql = @"
            SELECT a.avatar, a.name, a.type, g.symbol, a.""cidV0Digest""
            FROM ""V_CrcV2_Avatars"" a
            LEFT JOIN ""CrcV2_RegisterGroup"" g ON g.""group"" = a.avatar
            WHERE a.avatar = ANY(@addresses)";

    /// <summary>
    /// Fetches V2 avatar information from the database.
    /// </summary>
    private async Task<Dictionary<string, AvatarInfo>> FetchV2AvatarsAsync(string[] addresses)
    {
        var v2AvatarMap = new Dictionary<string, AvatarInfo>();

        await using var connection = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(V2AvatarInfoSql, connection);
        cmd.Parameters.AddWithValue("addresses", addresses);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var avatar = reader.GetString(0);
            var avatarType = reader.GetString(2);
            var isHuman = avatarType == "CrcV2_RegisterHuman";

            // Convert the view's latest-wins metadataDigest bytes to a proper IPFS CIDv0
            string? cid = null;
            if (!reader.IsDBNull(4))
            {
                var metadataDigest = (byte[])reader.GetValue(4);
                cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
            }

            v2AvatarMap[avatar] = new AvatarInfo(
                Version: 2,
                Type: avatarType,
                Avatar: avatar,
                TokenId: avatar,  // For V2, tokenId is the avatar address (for ERC1155)
                HasV1: false,
                V1Token: null,
                CidV0Digest: "",  // empty string per remote implementation
                CidV0: cid,
                IsHuman: isHuman,
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                Symbol: reader.IsDBNull(3) ? "" : reader.GetString(3)
            );
        }

        return v2AvatarMap;
    }

    /// <summary>
    /// Fetches V1 avatar information and CIDs from the database.
    /// </summary>
    private async Task<(Dictionary<string, AvatarInfo> avatars, Dictionary<string, string> cids)> FetchV1AvatarsAsync(string[] addresses)
    {
        var v1AvatarMap = new Dictionary<string, AvatarInfo>();
        var v1CidMap = new Dictionary<string, string>();

        await using var connection = await CreateConnectionAsync();

        // Fetch V1 avatars
        const string v1Sql = @"
            SELECT s.""user"", s.token
            FROM ""CrcV1_Signup"" s
            WHERE s.""user"" = ANY(@addresses)";

        await using (var cmd = new NpgsqlCommand(v1Sql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", addresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var userAddress = reader.GetString(0);
                var tokenAddress = reader.GetString(1);

                v1AvatarMap[userAddress] = new AvatarInfo(
                    Version: 1,
                    Type: "CrcV1_Signup",
                    Avatar: userAddress,
                    TokenId: tokenAddress,
                    HasV1: true,
                    V1Token: tokenAddress,
                    CidV0Digest: "",
                    CidV0: null,
                    IsHuman: true,  // V1 signups are always human
                    Name: null,
                    Symbol: ""
                );
            }
        }

        // Fetch V1 CIDs
        const string v1CidSql = @"
            SELECT avatar, ""metadataDigest""
            FROM ""CrcV1_UpdateMetadataDigest""
            WHERE avatar = ANY(@addresses)";

        try
        {
            await using var cmd = new NpgsqlCommand(v1CidSql, connection);
            cmd.Parameters.AddWithValue("addresses", addresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var avatar = reader.GetString(0);
                var metadataDigest = (byte[])reader.GetValue(1);
                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
                v1CidMap[avatar] = cid;
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table does not exist, skip V1 CIDs
        }

        return (v1AvatarMap, v1CidMap);
    }
}
