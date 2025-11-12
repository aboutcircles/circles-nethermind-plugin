using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using static Circles.Rpc.Host.JsonRpcHelpers;

namespace Circles.Rpc.Host;

public class CirclesRpcModule
{
    private readonly Settings _settings;
    private readonly string _readOnlyDbConnectionString;
    private readonly MemoryCache _profileByCidCache;
    private static readonly HttpClient HttpClient = new();

    public CirclesRpcModule(Settings settings)
    {
        _settings = settings;
        _readOnlyDbConnectionString = settings.IndexReadonlyDbConnectionString;
        _profileByCidCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
    }

    private async Task<NpgsqlConnection> CreateConnectionAsync()
    {
        var connection = new NpgsqlConnection(_readOnlyDbConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<object> GetTotalBalanceV1(string address)
    {
        // NOTE: This balance is a raw sum of historical transfers and does not account for
        // time-based inflation. The actual balance may be higher.
        using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT COALESCE(SUM(CASE WHEN t.""to"" = @address THEN t.amount WHEN t.""from"" = @address THEN -t.amount ELSE 0 END), 0) as balance FROM ""CrcV1_Transfer"" t WHERE t.""to"" = @address OR t.""from"" = @address";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var result = await command.ExecuteScalarAsync();
        return result as string ?? "0";
    }

    public async Task<object> GetTotalBalanceV2(string address)
    {
        // NOTE: This balance is a raw sum of historical transfers and does not account for
        // time-based demurrage. The actual balance may be lower.
        using var connection = await CreateConnectionAsync();
        const string sql = @"
            SELECT COALESCE(SUM(value), 0)
            FROM (
                -- ERC1155 Single Transfers
                SELECT value FROM ""CrcV2_TransferSingle"" WHERE ""to"" = @address
                UNION ALL
                SELECT -value FROM ""CrcV2_TransferSingle"" WHERE ""from"" = @address

                UNION ALL
                
                -- ERC1155 Batch Transfers
                SELECT value FROM ""CrcV2_TransferBatch"" WHERE ""to"" = @address
                UNION ALL
                SELECT -value FROM ""CrcV2_TransferBatch"" WHERE ""from"" = @address

                UNION ALL

                -- ERC20 Wrapper Transfers
                SELECT amount AS value FROM ""CrcV2_Erc20WrapperTransfer"" WHERE ""to"" = @address
                UNION ALL
                SELECT -amount AS value FROM ""CrcV2_Erc20WrapperTransfer"" WHERE ""from"" = @address
            ) as all_transfers;
        ";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var result = await command.ExecuteScalarAsync();
        return result?.ToString() ?? "0";
    }

    public async Task<object> GetTokenBalances(string address)
    {
        // NOTE: This method currently only returns V1 token balances. The returned balances are
        // raw sums of historical transfers and do not account for time-based inflation.
        using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT t.""tokenAddress"", COALESCE(SUM(CASE WHEN t.""to"" = @address THEN t.amount WHEN t.""from"" = @address THEN -t.amount ELSE 0 END), 0) as balance FROM ""CrcV1_Transfer"" t WHERE t.""to"" = @address OR t.""from"" = @address GROUP BY t.""tokenAddress"" HAVING SUM(CASE WHEN t.""to"" = @address THEN t.amount WHEN t.""from"" = @address THEN -t.amount ELSE 0 END) > 0 ORDER BY balance DESC";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var results = new List<object>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new { token = reader.GetString(0), balance = reader.GetString(1) });
        }
        return results;
    }

    public async Task<object> GetTokenInfo(string tokenAddress)
    {
        await using var connection = await CreateConnectionAsync();
        var lowerTokenAddress = tokenAddress.ToLower();

        // 1. Check for V1 token
        const string v1Sql = @"SELECT token, ""user"" as owner FROM ""CrcV1_Signup"" WHERE token = @tokenAddress LIMIT 1";
        await using (var cmd = new NpgsqlCommand(v1Sql, connection))
        {
            cmd.Parameters.AddWithValue("tokenAddress", lowerTokenAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new
                {
                    token = reader.GetString(0),
                    tokenOwner = reader.GetString(1),
                    version = 1,
                    type = "Avatar",
                    isErc20 = true,
                    isErc1155 = false,
                    isWrapped = false,
                    isInflationary = true,
                    isGroup = false
                };
            }
        }

        // 2. Check for V2 Avatar/Group token
        const string v2AvatarSql = @"SELECT avatar, type FROM ""V_CrcV2_Avatars"" WHERE avatar = @tokenAddress LIMIT 1";
        await using (var cmd = new NpgsqlCommand(v2AvatarSql, connection))
        {
            cmd.Parameters.AddWithValue("tokenAddress", lowerTokenAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var type = reader.GetString(1);
                var isGroup = type == "CrcV2_RegisterGroup";
                return new
                {
                    token = reader.GetString(0),
                    tokenOwner = reader.GetString(0), // For V2 avatars, the token and owner are the same
                    version = 2,
                    type = "Avatar",
                    isErc20 = false,
                    isErc1155 = true,
                    isWrapped = false,
                    isInflationary = false,
                    isGroup
                };
            }
        }

        // 3. Check for V2 Wrapped ERC20 token
        const string v2WrappedSql = @"SELECT ""erc20Wrapper"", avatar FROM ""CrcV2_ERC20WrapperDeployed"" WHERE ""erc20Wrapper"" = @tokenAddress LIMIT 1";
        await using (var cmd = new NpgsqlCommand(v2WrappedSql, connection))
        {
            cmd.Parameters.AddWithValue("tokenAddress", lowerTokenAddress);
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    return new
                    {
                        token = reader.GetString(0),
                        tokenOwner = reader.GetString(1),
                        version = 2,
                        type = "ERC20",
                        isErc20 = true,
                        isErc1155 = false,
                        isWrapped = true,
                        isInflationary = false,
                        isGroup = false
                    };
                }
            }
        }

        return CreateError("No token info found");
    }

    public async Task<object> GetTokenInfoBatch(string[] tokenAddresses)
    {
        var results = new List<object?>();
        foreach (var tokenAddress in tokenAddresses)
        {
            try
            {
                var tokenInfo = await GetTokenInfo(tokenAddress);
                results.Add(tokenInfo);
            }
            catch { results.Add(null); }
        }
        return results;
    }

    public async Task<object> GetAvatarInfo(string address)
    {
        using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT t.avatar, t.""timestamp"", t.name, t.type FROM ""V_CrcV2_Avatars"" t WHERE t.avatar = @address LIMIT 1";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new { version = 2, type = reader.GetString(3), avatar = reader.GetString(0), tokenId = "1", hasV1 = false, v1Token = (string?)null, cidV0Digest = "", cidV0 = (string?)null, isHuman = reader.GetString(3) == "CrcV2_RegisterHuman", name = reader.IsDBNull(2) ? null : reader.GetString(2), symbol = "" };
        }
        return CreateError($"No avatar found for address {address}");
    }

    public async Task<object> GetAvatarInfoBatch(string[] addresses)
    {
        var results = new List<object?>();
        foreach (var address in addresses)
        {
            try { results.Add(await GetAvatarInfo(address)); }
            catch { results.Add(null); }
        }
        return results.ToArray();
    }

    public async Task<object> GetProfileCid(string address)
    {
        using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT cid FROM ""CrcV2_RegisterName"" WHERE avatar = @address LIMIT 1";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var result = await command.ExecuteScalarAsync();
        return result != null ? result.ToString()! : CreateError("No profile found");
    }

    public async Task<object> GetProfileCidBatch(string[] addresses)
    {
        var results = new List<string?>();
        foreach (var address in addresses)
        {
            try
            {
                var cid = await GetProfileCid(address);
                results.Add(cid is string s ? s : null);
            }
            catch { results.Add(null); }
        }
        return results.ToArray();
    }

    public async Task<object> GetProfileByAddress(string address)
    {
        var cid = await GetProfileCid(address);
        return cid is string cidString ? await GetProfileByCid(cidString) : CreateError($"No profile found for address {address}");
    }

    public async Task<object> GetProfileByAddressBatch(string[] addresses)
    {
        var results = new List<object?>();
        foreach (var address in addresses)
        {
            try { results.Add(await GetProfileByAddress(address)); }
            catch { results.Add(null); }
        }
        return results.ToArray();
    }

    public async Task<object> GetProfileByCid(string cid)
    {
        if (_profileByCidCache.TryGetValue(cid, out object? cached) && cached != null) return cached;
        using var connection = await CreateConnectionAsync();
        const string query = "select payload from ipfs_files where cid = @cid";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("cid", cid);
        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            string payload = reader.GetString(0);
            var profile = JsonSerializer.Deserialize<object>(payload);
            if (profile != null)
            {
                _profileByCidCache.Set(cid, profile, new MemoryCacheEntryOptions { Size = 1 });
                return profile;
            }
        }
        return CreateError($"No profile found for cid {cid}");
    }

    public async Task<object> GetProfileByCidBatch(string[] cids)
    {
        var results = new List<object?>();
        foreach (var cid in cids)
        {
            try { results.Add(await GetProfileByCid(cid)); }
            catch { results.Add(null); }
        }
        return results.ToArray();
    }

    public async Task<object> SearchProfiles(string text, int limit = 20, int offset = 0, string[]? types = null)
    {
        const int hardLimit = 100;
        if (limit > hardLimit)
        {
            return CreateError($"limit must not exceed {hardLimit} (got {limit}).");
        }

        string qText = text.Trim();
        string[] tokens = qText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!tokens.Any(o => o.Length > 1))
        {
            return Array.Empty<object>();
        }

        if (tokens.Length > 3)
        {
            return CreateError("Too many search terms. Maximum is 3.");
        }

        qText = string.Join(' ', tokens);

        string[]? typeFilter = types?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool hasTypeFilter = typeFilter is { Length: > 0 };
        string typeFilterClause = hasTypeFilter ? " AND a.type = ANY(@types)" : string.Empty;

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
            recv AS (
                SELECT ""to""::text AS avatar, COUNT(*) AS receive_count
                FROM   ""CrcV2_TransferSummary""
                GROUP  BY ""to""
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
        LEFT JOIN recv r USING (avatar)
        ORDER BY COALESCE(r.receive_count, 0) DESC, p.rank DESC
        LIMIT  @limit
        OFFSET @offset;";

        await using var conn = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("search", qText);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);
        if (hasTypeFilter)
        {
            cmd.Parameters.AddWithValue("types", typeFilter!);
        }

        var profiles = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var avatar = reader.GetString(0);
            var avatarName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shortName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var avatarType = reader.GetString(3);
            var payload = reader.IsDBNull(4) ? null : reader.GetString(4);
            var cid = reader.IsDBNull(5) ? null : reader.GetString(5);

            if (payload != null)
            {
                var profile = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload);
                if (profile != null)
                {
                    profile["address"] = avatar;
                    profile["avatarType"] = avatarType;
                    profile["CID"] = cid;
                    profile["shortName"] = shortName;
                    profiles.Add(profile);
                }
            }
            else
            {
                profiles.Add(new
                {
                    address = avatar,
                    name = avatarName,
                    avatarType,
                    CID = cid,
                    shortName,
                    description = (string?)null,
                    imageUrl = (string?)null,
                    location = (string?)null
                });
            }
        }

        return profiles;
    }

    public async Task<object> GetTrustRelations(string address)
    {
        using var connection = await CreateConnectionAsync();
        const string sql = @"
            SELECT ""user"", ""canSendTo"", ""limit""
            FROM (
                SELECT ""user"", ""canSendTo"", ""limit"",
                       ROW_NUMBER() OVER (PARTITION BY ""user"", ""canSendTo"" ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV1_Trust""
                WHERE ""user"" = @address OR ""canSendTo"" = @address
            ) t
            WHERE rn = 1";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var trusts = new Dictionary<string, int>();
        var trustedBy = new Dictionary<string, int>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var user = reader.GetString(0);
            var canSendTo = reader.GetString(1);
            var limit = reader.GetInt32(2);
            if (user.Equals(address, StringComparison.OrdinalIgnoreCase))
            {
                trusts[canSendTo] = limit;
            }
            else
            {
                trustedBy[user] = limit;
            }
        }
        return new { user = address.ToLower(), trusts, trustedBy };
    }

    private async Task<bool> IsV2Human(string address)
    {
        await using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT 1 FROM ""CrcV2_RegisterHuman"" WHERE avatar = @address";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var result = await command.ExecuteScalarAsync();
        return result != null;
    }

    public async Task<object> GetCommonTrust(string address1, string address2, int? version = null)
    {
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
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address1", address1.ToLower());
        command.Parameters.AddWithValue("address2", address2.ToLower());

        var commonTrusts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            commonTrusts.Add(reader.GetString(0));
        }
        return new { address1 = address1.ToLower(), address2 = address2.ToLower(), commonTrusts };
    }

    public async Task<object> GetNetworkSnapshot()
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            return CreateError("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/snapshot";

        try
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var snapshot = await JsonSerializer.DeserializeAsync<JsonElement>(stream);
            return snapshot;
        }
        catch (Exception ex)
        {
            return CreateError($"Failed to get network snapshot from pathfinder: {ex.Message}");
        }
    }

    public async Task<object> FindPathV2(FlowRequest flowRequest)
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            return CreateError("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/findPath";

        try
        {
            var jsonContent = JsonSerializer.Serialize(flowRequest);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var response = await HttpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            var maxFlowResponse = JsonSerializer.Deserialize<MaxFlowResponse>(responseString);

            return maxFlowResponse ?? CreateError("Failed to deserialize MaxFlowResponse from pathfinder.");
        }
        catch (Exception ex)
        {
            return CreateError($"Failed to find path from pathfinder: {ex.Message}");
        }
    }

    public async Task<object> GetEvents(string? address, long? fromBlock, long? toBlock, string[]? eventTypes, bool? sortAscending = false)
    {
        // A predefined map of event tables and their columns that contain addresses.
        var eventTables = new Dictionary<string, string[]>
        {
            { "CrcV1_HubSignup", new[] { "user", "token" } },
            { "CrcV1_Signup", new[] { "user", "token" } },
            { "CrcV1_OrganizationSignup", new[] { "user" } },
            { "CrcV1_Trust", new[] { "user", "canSendTo" } },
            { "CrcV1_Transfer", new[] { "from", "to", "tokenAddress" } },
            { "CrcV2_RegisterHuman", new[] { "avatar" } },
            { "CrcV2_RegisterGroup", new[] { "avatar", "owner" } },
            { "CrcV2_RegisterOrganization", new[] { "avatar" } },
            { "CrcV2_TransferSingle", new[] { "operator", "from", "to" } },
            { "CrcV2_TransferBatch", new[] { "operator", "from", "to" } },
            { "CrcV2_RegisterName", new[] { "avatar" } },
            { "CrcV2_ERC20WrapperDeployed", new[] { "erc20Wrapper", "avatar" } },
            { "CrcV2_Erc20WrapperTransfer", new[] { "from", "to", "tokenAddress" } }
        };

        var queries = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        var relevantTables = eventTypes == null || !eventTypes.Any()
            ? eventTables
            : eventTables.Where(kvp => eventTypes.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        if (address != null) parameters.Add(new NpgsqlParameter("address", address.ToLower()));
        if (fromBlock.HasValue) parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        if (toBlock.HasValue) parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));

        foreach (var table in relevantTables)
        {
            var whereClauses = new List<string>();
            if (address != null && table.Value.Any())
            {
                whereClauses.Add($"({string.Join(" OR ", table.Value.Select(col => $"t.\"{col}\" = @address"))})");
            }
            if (fromBlock.HasValue) whereClauses.Add("t.\"blockNumber\" >= @fromBlock");
            if (toBlock.HasValue) whereClauses.Add("t.\"blockNumber\" <= @toBlock");

            var whereSql = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";
            queries.Add($@"SELECT t.""blockNumber"", t.""transactionHash"", t.""logIndex"", '{table.Key}' as event_name, to_jsonb(t) as event_payload FROM ""{table.Key}"" t {whereSql}");
        }

        if (queries.Count == 0)
        {
            return new { events = Array.Empty<object>() };
        }

        var finalSql = string.Join(" UNION ALL ", queries);
        var sortOrder = sortAscending == true ? "ASC" : "DESC";
        finalSql += $" ORDER BY \"blockNumber\" {sortOrder}, \"transactionIndex\" {sortOrder}, \"logIndex\" {sortOrder} LIMIT 1000";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        var events = new List<object>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new
            {
                blockNumber = reader.GetInt64(0),
                transactionHash = reader.GetString(1),
                logIndex = reader.GetInt32(2),
                @event = reader.GetString(3),
                payload = JsonSerializer.Deserialize<object>(reader.GetString(4))
            });
        }
        return new { events };
    }

    public async Task<object> GetHealth()
    {
        try
        {
            using var connection = await CreateConnectionAsync();
            using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
            return new { status = "healthy", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), database = "connected", index = "synchronized" };
        }
        catch (Exception)
        {
            return new { status = "unhealthy", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), database = "disconnected", index = "unknown" };
        }
    }

    public async Task<object> GetTables()
    {
        using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT DISTINCT table_schema, table_name FROM information_schema.tables WHERE table_schema NOT IN (information_schema, pg_catalog) ORDER BY table_schema, table_name";
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync();
        var namespaces = new Dictionary<string, List<string>>();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            if (!namespaces.ContainsKey(schema)) namespaces[schema] = new List<string>();
            namespaces[schema].Add(table);
        }
        return new { namespaces = namespaces.Select(kvp => new { name = kvp.Key, tables = kvp.Value.ToArray() }).ToArray() };
    }

    public async Task<object> Query(SelectDto query)
    {
        if (string.IsNullOrEmpty(query.Table) || string.IsNullOrEmpty(query.Namespace))
        {
            return CreateError("Namespace and Table must be provided.");
        }

        var tableName = $"\"{query.Namespace}_{query.Table}\"";
        var columns = (query.Columns == null || !query.Columns.Any())
            ? "*"
            : string.Join(", ", query.Columns.Select(c => $"\"{c}\""));

        var parameters = new List<NpgsqlParameter>();
        var whereClauses = new List<string>();
        if (query.Filter != null)
        {
            int paramIndex = 0;
            foreach (var filter in query.Filter.OfType<FilterPredicateDto>())
            {
                var paramName = $"@p{paramIndex++}";
                var op = filter.FilterType switch
                {
                    FilterType.Equals => "=",
                    FilterType.NotEquals => "!=",
                    FilterType.GreaterThan => ">",
                    FilterType.GreaterThanOrEquals => ">=",
                    FilterType.LessThan => "<",
                    FilterType.LessThanOrEquals => "<=",
                    FilterType.In => "IN",
                    _ => "="
                };
                whereClauses.Add($"\"{filter.Column}\" {op} {paramName}");
                parameters.Add(new NpgsqlParameter(paramName, filter.Value));
            }
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        var orderBySql = "";
        if (query.Order != null && query.Order.Any())
        {
            var orderByClauses = query.Order.Select(o => $"\"{o.Column}\" {(o.SortOrder?.ToUpper() == "DESC" ? "DESC" : "ASC")}");
            orderBySql = "ORDER BY " + string.Join(", ", orderByClauses);
        }

        var limitSql = query.Limit.HasValue ? $"LIMIT {query.Limit.Value}" : "";

        var finalSql = $"SELECT {columns} FROM {tableName} {whereSql} {orderBySql} {limitSql}";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<Dictionary<string, object?>>();
        var columnNames = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < columnNames.Count; i++)
            {
                var value = reader.GetValue(i);
                row[columnNames[i]] = value is DBNull ? null : value;
            }
            results.Add(row);
        }

        return new { columns = columnNames, rows = results };
    }

    private static object CreateError(string message) => new { error = message };
}