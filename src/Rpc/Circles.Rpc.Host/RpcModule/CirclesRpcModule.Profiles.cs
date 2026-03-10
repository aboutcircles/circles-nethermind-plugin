using System.Text.Json;
using Circles.Common;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Profile-related methods for CirclesRpcModule.
/// Handles profile CID queries, profile content fetching, and profile search.
/// </summary>
public partial class CirclesRpcModule
{
    public async Task<ProfileCidResponse> GetProfileCid(string address)
    {
        var results = await GetProfileCidBatchInternal(new[] { address });
        return new ProfileCidResponse(results[0]);
    }

    public async Task<Dictionary<string, string?>> GetProfileCidBatch(string[] addresses)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return new Dictionary<string, string?>();
        }

        var results = await GetProfileCidBatchInternal(addresses);
        var dict = new Dictionary<string, string?>();
        for (int i = 0; i < addresses.Length; i++)
        {
            if (addresses[i] != null)
            {
                dict[addresses[i].ToLower()] = results[i];
            }
        }
        return dict;
    }

    private async Task<string?[]> GetProfileCidBatchInternal(string[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        // If cache service is enabled, try using it first
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for profile CID batch query ({Count} addresses)", addresses.Length);

                var cacheResults = await _cacheServiceClient.GetProfileCidBatchAsync(addresses);

                // Convert to string?[] array
                var cacheResult = new string?[addresses.Length];
                for (int i = 0; i < cacheResults.Length && i < addresses.Length; i++)
                {
                    cacheResult[i] = cacheResults[i].Cid;
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
        _logger?.LogDebug("Using database for profile CID batch query ({Count} addresses)", addresses.Length);
        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();
        var result = new string?[addresses.Length];

        await using var connection = await CreateConnectionAsync();

        // First, check V2 CIDs
        var v2CidMap = new Dictionary<string, string>();
        const string v2Sql = @"SELECT avatar, ""metadataDigest"" FROM ""CrcV2_UpdateMetadataDigest"" WHERE avatar = ANY(@addresses)";

        await using (var cmd = new NpgsqlCommand(v2Sql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var avatar = reader.GetString(0);
                var metadataDigest = (byte[])reader.GetValue(1);
                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
                v2CidMap[avatar] = cid;
            }
        }

        // Then, check V1 CIDs (for those not found in V2)
        var v1CidMap = new Dictionary<string, string>();
        try
        {
            const string v1Sql = @"SELECT avatar, ""metadataDigest"" FROM ""CrcV1_UpdateMetadataDigest"" WHERE avatar = ANY(@addresses)";

            await using (var cmd = new NpgsqlCommand(v1Sql, connection))
            {
                cmd.Parameters.AddWithValue("addresses", lowerAddresses);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var avatar = reader.GetString(0);
                    var metadataDigest = (byte[])reader.GetValue(1);
                    var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
                    v1CidMap[avatar] = cid;
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table does not exist, skip V1
        }

        // Populate results (V2 takes priority)
        for (int i = 0; i < lowerAddresses.Length; i++)
        {
            var addr = lowerAddresses[i];
            if (v2CidMap.TryGetValue(addr, out var v2Cid))
            {
                result[i] = v2Cid;
            }
            else if (v1CidMap.TryGetValue(addr, out var v1Cid))
            {
                result[i] = v1Cid;
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    public async Task<JsonElement?> GetProfileByAddress(string address)
    {
        var results = await GetProfileByAddressBatchInternal(new[] { address });
        return results[0];
    }

    public async Task<JsonElement?[]> GetProfileByAddressBatch(string[] addresses)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return Array.Empty<JsonElement?>();
        }

        return await GetProfileByAddressBatchInternal(addresses);
    }

    private async Task<JsonElement?[]> GetProfileByAddressBatchInternal(string[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();

        // Try cache service path first - gets CIDs, short names, and avatar types in one call
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                return await GetProfileByAddressBatchViaCacheService(lowerAddresses);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service profile batch failed, falling back to database");
                // Fall through to database path
            }
        }

        // Fallback: Database path
        return await GetProfileByAddressBatchViaDatabase(lowerAddresses);
    }

    /// <summary>
    /// Optimized profile fetch using cache service.
    /// Gets avatar info (including CID, shortName, type) in a single batch call,
    /// avoiding 3 separate DB queries.
    /// </summary>
    private async Task<JsonElement?[]> GetProfileByAddressBatchViaCacheService(string[] lowerAddresses)
    {
        var result = new JsonElement?[lowerAddresses.Length];

        // Get avatar info batch directly from cache service - includes CID, shortName, and type
        var cacheAvatarInfos = await _cacheServiceClient!.GetAvatarInfoBatchAsync(lowerAddresses);

        // Build maps from cache response
        var cidMap = new Dictionary<string, string>();
        var shortNameMap = new Dictionary<string, string>();
        var avatarTypeMap = new Dictionary<string, string>();

        for (int i = 0; i < lowerAddresses.Length; i++)
        {
            var addr = lowerAddresses[i];
            var info = cacheAvatarInfos[i];
            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.CidV0))
                    cidMap[addr] = info.CidV0;
                if (!string.IsNullOrEmpty(info.ShortName))
                    shortNameMap[addr] = info.ShortName;
                if (!string.IsNullOrEmpty(info.Type))
                    avatarTypeMap[addr] = info.Type;
            }
        }

        // Fetch IPFS profiles by CID
        var validCids = cidMap.Values.Distinct().ToArray();
        var profileByCidMap = new Dictionary<string, JsonElement?>();

        if (validCids.Length > 0)
        {
            var profiles = await GetProfileByCidBatchInternal(validCids);
            for (int i = 0; i < validCids.Length; i++)
            {
                profileByCidMap[validCids[i]] = profiles[i];
            }
        }

        // Assemble enriched profiles
        for (int i = 0; i < lowerAddresses.Length; i++)
        {
            var addr = lowerAddresses[i];
            var hasCid = cidMap.TryGetValue(addr, out var cid);

            JsonElement? baseProfile = null;
            if (hasCid && cid != null && profileByCidMap.TryGetValue(cid, out var profile))
            {
                baseProfile = profile;
            }

            var hasShortName = shortNameMap.TryGetValue(addr, out var shortName);
            var hasAvatarType = avatarTypeMap.TryGetValue(addr, out var avatarType);

            if (baseProfile != null)
            {
                var profileDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseProfile.Value.GetRawText());

                if (profileDict != null)
                {
                    var enrichedProfile = new Dictionary<string, JsonElement>
                    {
                        ["address"] = JsonSerializer.SerializeToElement(addr)
                    };

                    foreach (var kvp in profileDict)
                    {
                        if (kvp.Key != "namespaces" && kvp.Key != "signingKeys")
                        {
                            enrichedProfile[kvp.Key] = kvp.Value;
                        }
                    }

                    if (hasShortName)
                        enrichedProfile["shortName"] = JsonSerializer.SerializeToElement(shortName);
                    if (hasAvatarType)
                        enrichedProfile["avatarType"] = JsonSerializer.SerializeToElement(avatarType);

                    result[i] = JsonSerializer.SerializeToElement(enrichedProfile);
                }
                else
                {
                    result[i] = baseProfile;
                }
            }
            else if (hasAvatarType || hasShortName)
            {
                var minimalProfile = new Dictionary<string, object?>
                {
                    ["address"] = addr,
                    ["shortName"] = hasShortName ? shortName : null,
                    ["name"] = null,
                    ["description"] = null,
                    ["avatarType"] = hasAvatarType ? avatarType : null
                };
                result[i] = JsonSerializer.SerializeToElement(minimalProfile);
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Database fallback for profile fetch.
    /// Makes separate queries for CIDs, short names, and avatar types.
    /// </summary>
    private async Task<JsonElement?[]> GetProfileByAddressBatchViaDatabase(string[] lowerAddresses)
    {
        var result = new JsonElement?[lowerAddresses.Length];

        // Get CIDs for all addresses
        var cids = await GetProfileCidBatchInternal(lowerAddresses);

        // Get short names and avatar types for enrichment
        await using var connection = await CreateConnectionAsync();

        var shortNameMap = new Dictionary<string, string>();
        var avatarTypeMap = new Dictionary<string, string>();

        // Get V2 short names
        const string shortNameSql = @"SELECT avatar, ""shortName"" FROM ""CrcV2_RegisterShortName"" WHERE avatar = ANY(@addresses)";
        await using (var cmd = new NpgsqlCommand(shortNameSql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var avatar = reader.GetString(0);
                var shortName = reader.GetString(1);
                shortNameMap[avatar] = shortName;
            }
        }

        // Get avatar types from V2
        const string v2TypeSql = @"SELECT avatar, type FROM ""V_CrcV2_Avatars"" WHERE avatar = ANY(@addresses)";
        await using (var cmd = new NpgsqlCommand(v2TypeSql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var avatar = reader.GetString(0);
                var avatarTypeName = reader.GetString(1);
                avatarTypeMap[avatar] = avatarTypeName;
            }
        }

        // Get avatar types from V1 (for those not in V2)
        const string v1TypeSql = @"SELECT ""user"", 'CrcV1_Signup' as type FROM ""CrcV1_Signup"" WHERE ""user"" = ANY(@addresses)";
        await using (var cmd = new NpgsqlCommand(v1TypeSql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var avatar = reader.GetString(0);
                // Only set if not already set by V2
                if (!avatarTypeMap.ContainsKey(avatar))
                {
                    avatarTypeMap[avatar] = "CrcV1_Signup";
                }
            }
        }

        // Fetch profiles by CID
        var validCids = cids.Where(c => c != null).Distinct().ToArray();
        var profileByCidMap = new Dictionary<string, JsonElement?>();

        if (validCids.Length > 0)
        {
            var profiles = await GetProfileByCidBatchInternal(validCids!);
            for (int i = 0; i < validCids.Length; i++)
            {
                profileByCidMap[validCids[i]!] = profiles[i];
            }
        }

        // Assemble enriched profiles
        for (int i = 0; i < lowerAddresses.Length; i++)
        {
            var addr = lowerAddresses[i];
            var cid = cids[i];

            JsonElement? baseProfile = null;
            if (cid != null && profileByCidMap.TryGetValue(cid, out var profile))
            {
                baseProfile = profile;
            }

            // Get enrichment data
            var hasShortName = shortNameMap.TryGetValue(addr, out var shortName);
            var hasAvatarType = avatarTypeMap.TryGetValue(addr, out var avatarType);

            // If we have a profile, enrich it
            if (baseProfile != null)
            {
                // Deserialize to dictionary for enrichment
                var profileDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseProfile.Value.GetRawText());

                if (profileDict != null)
                {
                    // Create new dictionary with address first to match remote field order
                    var enrichedProfile = new Dictionary<string, JsonElement>
                    {
                        ["address"] = JsonSerializer.SerializeToElement(addr)
                    };

                    // Add all other fields from the original profile
                    foreach (var kvp in profileDict)
                    {
                        // Exclude namespaces and signingKeys to match remote
                        if (kvp.Key != "namespaces" && kvp.Key != "signingKeys")
                        {
                            enrichedProfile[kvp.Key] = kvp.Value;
                        }
                    }

                    // Add shortName if available
                    if (hasShortName)
                        enrichedProfile["shortName"] = JsonSerializer.SerializeToElement(shortName);

                    // Add avatarType if available
                    if (hasAvatarType)
                        enrichedProfile["avatarType"] = JsonSerializer.SerializeToElement(avatarType);

                    result[i] = JsonSerializer.SerializeToElement(enrichedProfile);
                }
                else
                {
                    result[i] = baseProfile;
                }
            }
            // If no profile but we have metadata, create a minimal profile
            else if (hasAvatarType || hasShortName)
            {
                var minimalProfile = new Dictionary<string, object?>
                {
                    ["address"] = addr,
                    ["shortName"] = hasShortName ? shortName : null,
                    ["name"] = null,
                    ["description"] = null,
                    ["avatarType"] = hasAvatarType ? avatarType : null
                };
                result[i] = JsonSerializer.SerializeToElement(minimalProfile);
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    private async Task<JsonElement?[]> GetProfileByCidBatchInternal(string[] cids)
    {
        if (cids.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(cids), "Batch size exceeds 1000");
        }

        var result = new JsonElement?[cids.Length];
        var missingCidIndexes = new List<int>();
        var missingCids = new List<string>();

        // Check local cache first
        for (int i = 0; i < cids.Length; i++)
        {
            var currentCid = cids[i];
            if (string.IsNullOrWhiteSpace(currentCid))
            {
                result[i] = null;
                continue;
            }

            if (_profileByCidCache.TryGetValue(currentCid, out JsonElement? cached) && cached != null)
            {
                result[i] = (JsonElement)cached;
            }
            else
            {
                missingCidIndexes.Add(i);
                missingCids.Add(currentCid);
            }
        }

        if (missingCids.Count == 0)
        {
            return result;
        }

        // Try cache service first, then fall back to database
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                var cacheResults = await _cacheServiceClient.GetProfileContentBatchAsync(missingCids.ToArray());
                for (int i = 0; i < missingCids.Count; i++)
                {
                    int targetIndex = missingCidIndexes[i];
                    string targetCid = missingCids[i];
                    var content = cacheResults[i]?.Content;

                    if (!string.IsNullOrEmpty(content))
                    {
                        var profile = JsonSerializer.Deserialize<JsonElement>(content);
                        result[targetIndex] = profile;
                        _profileByCidCache.Set(targetCid, profile, new MemoryCacheEntryOptions { Size = 1 });
                    }
                    else
                    {
                        result[targetIndex] = null;
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache service profile content fetch failed, falling back to database");
                // Fall through to database
            }
        }

        // Fallback: Fetch missing profiles from database
        const string query = @"
            SELECT f.payload
            FROM unnest(@cids) WITH ORDINALITY as u(_cid, _index)
            LEFT JOIN ipfs_files f ON f.cid = u._cid
            ORDER BY u._index";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("cids", missingCids.ToArray());

        await using var reader = await command.ExecuteReaderAsync();
        int readCount = 0;
        while (await reader.ReadAsync())
        {
            int targetIndex = missingCidIndexes[readCount];
            string targetCid = cids[targetIndex];

            if (!reader.IsDBNull(0))
            {
                var payloadStr = reader.GetString(0);
                var profile = JsonSerializer.Deserialize<JsonElement>(payloadStr);
                var cleanedProfile = StripJsonLdFields(profile);
                result[targetIndex] = cleanedProfile;
                _profileByCidCache.Set(targetCid, cleanedProfile, new MemoryCacheEntryOptions { Size = 1 });
            }
            else
            {
                result[targetIndex] = null;
            }

            readCount++;
        }

        return result;
    }

    public async Task<JsonElement?> GetProfileByCid(string cid)
    {
        if (string.IsNullOrWhiteSpace(cid))
        {
            throw new ArgumentException("CID must not be empty.", nameof(cid));
        }

        var results = await GetProfileByCidBatchInternal(new[] { cid });
        return results[0];
    }

    public async Task<JsonElement?[]> GetProfileByCidBatch(string[] cids)
    {
        if (cids == null || cids.Length == 0)
        {
            return Array.Empty<JsonElement?>();
        }

        return await GetProfileByCidBatchInternal(cids);
    }

    public async Task<ProfileSearchResult> SearchProfiles(string text, int limit = 20, int offset = 0, string[]? types = null)
    {
        const int hardLimit = 100;
        if (limit > hardLimit)
        {
            throw new ArgumentException($"limit must not exceed {hardLimit} (got {limit}).");
        }

        string qText = text.Trim();
        string[] tokens = qText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!tokens.Any(o => o.Length > 1))
        {
            return new ProfileSearchResult(Total: 0, Results: Array.Empty<ProfileSearchResultItem>());
        }

        if (tokens.Length > 3)
        {
            throw new ArgumentException("Too many search terms. Maximum is 3.");
        }

        qText = string.Join(' ', tokens);

        string[]? typeFilter = types?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Try profile pinning service first (fast path)
        if (!string.IsNullOrEmpty(_settings.ProfilePinningServiceUrl))
        {
            try
            {
                var proxyResult = await SearchProfilesViaProxy(qText, limit, offset, typeFilter);
                if (proxyResult != null)
                {
                    return proxyResult;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Profile pinning service proxy failed, falling back to SQL");
            }
        }

        // Fallback: direct SQL search
        return await SearchProfilesViaSql(qText, limit, offset, typeFilter);
    }

    /// <summary>
    /// Fast path: proxy search to the profile-pinning service REST API.
    /// Returns null if the service is unavailable or returns an error.
    /// </summary>
    private async Task<ProfileSearchResult?> SearchProfilesViaProxy(
        string qText, int limit, int offset, string[]? typeFilter)
    {
        var url = $"{_settings.ProfilePinningServiceUrl!.TrimEnd('/')}/search/text?q={Uri.EscapeDataString(qText)}&limit={limit}&offset={offset}";

        if (typeFilter is { Length: > 0 })
        {
            // Profile pinning service currently supports single type filter
            url += $"&type={Uri.EscapeDataString(typeFilter[0])}";
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await HttpClient.GetAsync(url, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("Profile pinning service returned {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var proxyResults = JsonSerializer.Deserialize<JsonElement[]>(json);

        if (proxyResults == null || proxyResults.Length == 0)
        {
            return new ProfileSearchResult(Total: 0, Results: Array.Empty<ProfileSearchResultItem>());
        }

        // Safety net: server handles offset, but cap to requested limit
        var paged = proxyResults.Take(limit).ToArray();

        // Collect addresses for batch avatar info lookup
        var addresses = paged
            .Select(r => r.TryGetProperty("address", out var addr) ? addr.GetString() : null)
            .Where(a => a != null)
            .ToArray();

        if (addresses.Length == 0)
        {
            return new ProfileSearchResult(Total: 0, Results: Array.Empty<ProfileSearchResultItem>());
        }

        // Batch avatar info lookup (single DB call instead of N+1)
        var avatarInfos = await GetAvatarInfoBatchInternal(addresses!);

        var results = new List<ProfileSearchResultItem>();
        for (int i = 0; i < paged.Length && i < addresses.Length; i++)
        {
            var avatarInfo = avatarInfos[i];
            if (avatarInfo == null) continue;

            var proxyEntry = paged[i];
            JsonElement? profile = null;

            // Build profile from proxy response fields
            var profileDict = new Dictionary<string, object?>();
            if (proxyEntry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind != JsonValueKind.Null)
                profileDict["name"] = nameEl.GetString();
            if (proxyEntry.TryGetProperty("description", out var descEl) && descEl.ValueKind != JsonValueKind.Null)
                profileDict["description"] = descEl.GetString();
            if (proxyEntry.TryGetProperty("location", out var locEl) && locEl.ValueKind != JsonValueKind.Null)
                profileDict["location"] = locEl.GetString();

            if (profileDict.Count > 0)
            {
                profile = JsonSerializer.SerializeToElement(profileDict);
            }

            results.Add(new ProfileSearchResultItem(
                Avatar: addresses[i]!,
                AvatarInfo: avatarInfo,
                Profile: profile
            ));
        }

        return new ProfileSearchResult(Total: results.Count, Results: results.ToArray());
    }

    /// <summary>
    /// Slow path: direct SQL search with full-table aggregation.
    /// Used as fallback when profile-pinning service is unavailable.
    /// </summary>
    private async Task<ProfileSearchResult> SearchProfilesViaSql(
        string qText, int limit, int offset, string[]? typeFilter)
    {
        bool hasTypeFilter = typeFilter is { Length: > 0 };
        string typeFilterClause = hasTypeFilter ? " AND a.type = ANY(@types)" : string.Empty;

        // Uses delta-enhanced views for fresh data with materialized view performance:
        // - V_CrcV2_Avatars: matview + delta for new registrations/CID updates
        // - V_CrcV2_ReceiveCount: matview + delta for recent transfers
        // - idx_ipfs_files_payload_profile_fts: GIN index on profile tsvectors
        string sql = $@"
        WITH
            input(txt) AS (VALUES (@search)),
            q AS (
                SELECT to_tsquery(
                         'simple',
                         (
                           SELECT string_agg(quote_literal(tok) || ':*', ' & ')
                           FROM   unnest(string_to_array(txt, ' ')) AS tok
                         )
                       ) AS query
                FROM input
            ),
            w_profile AS (
                SELECT  a.avatar, a.""timestamp"", a.name AS avatar_name, rs.""shortName"" AS short_name,
                        a.type AS avatar_type, f.cid AS cid, f.metadata_digest, f.payload,
                        ts_rank_cd(
                          ARRAY[1.0, 0.4, 0.2, 0.05],
                          (
                            setweight(to_tsvector('simple', coalesce(f.payload ->> 'name', '')), 'A') ||
                            setweight(to_tsvector('simple', coalesce(f.payload ->> 'description', '')), 'B') ||
                            setweight(to_tsvector('simple', a.avatar), 'C')
                          ),
                          q.query
                        ) AS rank
                FROM   ""V_CrcV2_Avatars"" a
                LEFT JOIN ""CrcV2_RegisterShortName"" rs ON rs.avatar = a.avatar
                JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
                CROSS JOIN q
                WHERE (
                        setweight(to_tsvector('simple', coalesce(f.payload ->> 'name', '')), 'A') ||
                        setweight(to_tsvector('simple', coalesce(f.payload ->> 'description', '')), 'B') ||
                        setweight(to_tsvector('simple', a.avatar), 'C')
                      ) @@ q.query
                  {typeFilterClause}
            ),
            wo_profile AS (
                SELECT  a.avatar, a.""timestamp"", a.name AS avatar_name, rs.""shortName"" AS short_name,
                        a.type AS avatar_type, NULL::text AS cid, NULL::bytea AS metadata_digest, NULL::jsonb AS payload,
                        ts_rank_cd(
                          ARRAY[1.0, 0.4, 0.2, 0.05],
                          (
                            setweight(to_tsvector('simple', a.name), 'A') ||
                            setweight(to_tsvector('simple', a.avatar), 'C')
                          ),
                          q.query
                        ) AS rank
                FROM   ""V_CrcV2_Avatars"" a
                LEFT JOIN ""CrcV2_RegisterShortName"" rs ON rs.avatar = a.avatar
                LEFT JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
                CROSS JOIN q
                WHERE f.metadata_digest IS NULL
                  AND (
                        setweight(to_tsvector('simple', a.name), 'A') ||
                        setweight(to_tsvector('simple', a.avatar), 'C')
                      ) @@ q.query
                  {typeFilterClause}
            )
        SELECT  p.avatar, p.avatar_name, p.short_name::text as short_name, p.avatar_type, p.payload, p.cid
        FROM   (SELECT * FROM w_profile
                UNION ALL
                SELECT * FROM wo_profile) p
        LEFT JOIN ""V_CrcV2_ReceiveCount"" r USING (avatar)
        ORDER BY COALESCE(r.receive_count, 0) DESC, p.rank DESC
        LIMIT  @limit
        OFFSET @offset;";

        await using var conn = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.ProfileSearchTimeoutSeconds;
        cmd.Parameters.AddWithValue("search", qText);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);
        if (hasTypeFilter)
        {
            cmd.Parameters.AddWithValue("types", typeFilter!);
        }

        var results = new List<ProfileSearchResultItem>();
        await using var reader = await cmd.ExecuteReaderAsync();

        // Collect all avatars first, then batch lookup (eliminates N+1)
        var rows = new List<(string avatar, string? payload)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        if (rows.Count == 0)
        {
            return new ProfileSearchResult(Total: 0, Results: Array.Empty<ProfileSearchResultItem>());
        }

        // Batch avatar info lookup
        var avatarAddresses = rows.Select(r => r.avatar).ToArray();
        var avatarInfos = await GetAvatarInfoBatchInternal(avatarAddresses);

        for (int i = 0; i < rows.Count; i++)
        {
            var avatarInfo = avatarInfos[i];
            if (avatarInfo == null) continue;

            JsonElement? profile = null;
            if (rows[i].payload != null)
            {
                profile = JsonSerializer.Deserialize<JsonElement>(rows[i].payload!);
                profile = StripJsonLdFields(profile);
            }

            results.Add(new ProfileSearchResultItem(
                Avatar: rows[i].avatar,
                AvatarInfo: avatarInfo,
                Profile: profile
            ));
        }

        return new ProfileSearchResult(Total: results.Count, Results: results.ToArray());
    }
}
