using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Profile view + profile search methods for CirclesRpcModule.
/// </summary>
public partial class CirclesRpcModule
{
    /// <summary>
    /// Gets a consolidated profile view combining avatar info, profile data, trust stats, and balances.
    /// Replaces 6-7 separate RPC calls typically needed to display a user profile.
    /// </summary>
    public async Task<ProfileViewResponse> GetProfileView(string address)
    {
        // Get avatar info
        var avatarInfo = await GetAvatarInfoBatchInternal(new[] { address });
        var avatar = avatarInfo.FirstOrDefault();

        // Get profile data (if exists)
        JsonElement? profile = null;
        try
        {
            profile = await GetProfileByAddress(address);
        }
        catch
        {
            // Profile optional
        }

        // Get trust relations
        var trustRelations = await GetTrustRelations(address);

        // Get balances
        TotalBalanceResponse? v1Balance = null;
        TotalBalanceResponse? v2Balance = null;

        if (avatar?.HasV1 == true)
        {
            try
            {
                v1Balance = await GetTotalBalance(address, 1, true);
            }
            catch
            {
                // Balance query optional
            }
        }

        if (avatar?.Version == 2)
        {
            try
            {
                v2Balance = await GetTotalBalance(address, 2, true);
            }
            catch
            {
                // Balance query optional
            }
        }

        return new ProfileViewResponse
        {
            Address = address,
            AvatarInfo = avatar,
            Profile = profile,
            TrustStats = new TrustStats
            {
                TrustsCount = trustRelations.Trusts?.Length ?? 0,
                TrustedByCount = trustRelations.TrustedBy?.Length ?? 0
            },
            V1Balance = v1Balance?.Balance,
            V2Balance = v2Balance?.Balance
        };
    }

    /// <summary>
    /// Unified search across profiles by address prefix or name/description text.
    /// Combines address lookup and full-text search in a single endpoint.
    /// Returns paginated results with cursor-based navigation.
    /// </summary>
    public async Task<PagedProfileSearchResponse> SearchProfileByAddressOrName(
        string query,
        int? limit = null,
        string? cursor = null,
        string[]? types = null)
    {
        // Apply pagination limits
        const int defaultLimit = 20;
        const int maxLimit = 100;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        // Check if query looks like an address (starts with 0x and is hex)
        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            query.Length >= 10 &&
            Regex.IsMatch(query, @"^0x[0-9a-fA-F]+$"))
        {
            // Address search - find avatars with matching address prefix
            // For address search, we use the avatar address as cursor

            // Decode cursor (avatar address)
            string? cursorAddress = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    cursorAddress = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                }
                catch
                {
                    // Invalid cursor, ignore
                }
            }

            // Build filters
            var filters = new List<IFilterPredicateDto>
            {
                new FilterPredicateDto
                {
                    Column = "avatar",
                    FilterType = FilterType.Like,
                    Value = $"{query.ToLowerInvariant()}%"
                }
            };

            // Add cursor filter for pagination
            if (cursorAddress != null)
            {
                filters.Add(new FilterPredicateDto
                {
                    Column = "avatar",
                    FilterType = FilterType.GreaterThan,
                    Value = cursorAddress
                });
            }

            // Add type filter if specified
            if (types != null && types.Length > 0)
            {
                filters.Add(new FilterPredicateDto
                {
                    Column = "type",
                    FilterType = FilterType.In,
                    Value = types
                });
            }

            var selectQuery = new SelectDto
            {
                Namespace = "V_Crc",
                Table = "Avatars",
                Columns = Array.Empty<string>(),
                Filter = filters,
                Order = new[]
                {
                    new OrderByDto { Column = "avatar", SortOrder = "ASC" }
                },
                Limit = effectiveLimit + 1 // Fetch one extra for hasMore check
            };

            var results = await Query(selectQuery);

            // Get full profiles for matching addresses
            var addresses = new List<string>();
            int avatarIndex = results.Columns.IndexOf("avatar");
            if (avatarIndex >= 0)
            {
                foreach (var row in results.Rows)
                {
                    var avatarValue = row[avatarIndex];
                    if (avatarValue is string avatarStr)
                    {
                        addresses.Add(avatarStr);
                    }
                }
            }

            // Check if there are more results
            var hasMore = addresses.Count > effectiveLimit;
            if (hasMore)
            {
                addresses.RemoveAt(addresses.Count - 1);
            }

            var profiles = addresses.Count > 0
                ? await GetProfileByAddressBatch(addresses.ToArray())
                : Array.Empty<JsonElement?>();

            var profileResults = profiles.Where(p => p != null).Cast<JsonElement>().ToArray();

            // Generate next cursor from last address
            string? nextCursor = null;
            if (hasMore && addresses.Count > 0)
            {
                nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(addresses[^1]));
            }

            return new PagedProfileSearchResponse
            {
                Query = query,
                SearchType = "address",
                Results = profileResults,
                HasMore = hasMore,
                NextCursor = nextCursor
            };
        }
        else
        {
            // Text search - use cursor-based pagination with rank+avatar composite cursor
            // Cursor format: "rank:avatar" base64 encoded

            double? cursorRank = null;
            string? cursorAvatar = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                    var parts = decoded.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        cursorRank = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        cursorAvatar = parts[1];
                    }
                }
                catch
                {
                    // Invalid cursor, ignore
                }
            }

            // Use the SearchProfilesWithCursor helper
            var searchResults = await SearchProfilesWithCursor(query, effectiveLimit, cursorRank, cursorAvatar, types);

            return new PagedProfileSearchResponse
            {
                Query = query,
                SearchType = "text",
                Results = searchResults.Results.Select(r => r.Profile).Where(p => p != null).Cast<JsonElement>().ToArray(),
                HasMore = searchResults.HasMore,
                NextCursor = searchResults.NextCursor
            };
        }
    }

    /// <summary>
    /// Internal helper for cursor-based profile search with ranking.
    /// </summary>
    private async Task<(ProfileSearchResultItem[] Results, bool HasMore, string? NextCursor)> SearchProfilesWithCursor(
        string text,
        int limit,
        double? cursorRank,
        string? cursorAvatar,
        string[]? types = null)
    {
        const int hardLimit = 100;
        if (limit > hardLimit)
        {
            limit = hardLimit;
        }

        string qText = text.Trim();
        string[] tokens = qText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!tokens.Any(o => o.Length > 1))
        {
            return (Array.Empty<ProfileSearchResultItem>(), false, null);
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

        bool hasTypeFilter = typeFilter is { Length: > 0 };
        string typeFilterClause = hasTypeFilter ? " AND a.type = ANY(@types)" : string.Empty;

        // Build cursor filter clause
        string cursorFilterClause = "";
        if (cursorRank.HasValue && cursorAvatar != null)
        {
            // For descending order, we want items with lower rank OR same rank but higher avatar
            cursorFilterClause = " AND (COALESCE(r.receive_count, 0), p.rank, p.avatar) < (@cursorReceiveCount, @cursorRank, @cursorAvatar)";
        }

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
        SELECT  p.avatar, p.avatar_name, p.short_name::text as short_name, p.avatar_type, p.payload, p.cid,
                COALESCE(r.receive_count, 0) as receive_count, p.rank
        FROM   (SELECT * FROM w_profile
                UNION ALL
                SELECT * FROM wo_profile) p
        LEFT JOIN ""V_CrcV2_ReceiveCount"" r USING (avatar)
        WHERE 1=1 {cursorFilterClause}
        ORDER BY COALESCE(r.receive_count, 0) DESC, p.rank DESC, p.avatar ASC
        LIMIT  @limit;";

        await using var conn = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.ProfileSearchTimeoutSeconds;
        cmd.Parameters.AddWithValue("search", qText);
        cmd.Parameters.AddWithValue("limit", limit + 1); // Fetch one extra for hasMore check
        if (hasTypeFilter)
        {
            cmd.Parameters.AddWithValue("types", typeFilter!);
        }
        if (cursorRank.HasValue && cursorAvatar != null)
        {
            cmd.Parameters.AddWithValue("cursorReceiveCount", 0L); // We'll use rank primarily
            cmd.Parameters.AddWithValue("cursorRank", cursorRank.Value);
            cmd.Parameters.AddWithValue("cursorAvatar", cursorAvatar);
        }

        var results = new List<(ProfileSearchResultItem Item, long ReceiveCount, double Rank)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var avatar = reader.GetString(0);
            var avatarName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shortName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var avatarType = reader.GetString(3);
            var payload = reader.IsDBNull(4) ? null : reader.GetString(4);
            var cid = reader.IsDBNull(5) ? null : reader.GetString(5);
            var receiveCount = reader.GetInt64(6);
            var rank = reader.GetDouble(7);

            // Get full avatar info for this result
            var avatarInfos = await GetAvatarInfoBatchInternal(new[] { avatar });
            var avatarInfo = avatarInfos[0];

            if (avatarInfo == null)
            {
                // Skip if no avatar info available
                continue;
            }

            JsonElement? profile = null;
            if (payload != null)
            {
                profile = JsonSerializer.Deserialize<JsonElement>(payload);
                profile = StripJsonLdFields(profile);
            }

            results.Add((new ProfileSearchResultItem(
                Avatar: avatar,
                AvatarInfo: avatarInfo,
                Profile: profile
            ), receiveCount, rank));
        }

        // Check if there are more results
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }

        // Generate next cursor from last result
        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var last = results[^1];
            var cursorStr = $"{last.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{last.Item.Avatar}";
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(cursorStr));
        }

        return (results.Select(r => r.Item).ToArray(), hasMore, nextCursor);
    }
}
