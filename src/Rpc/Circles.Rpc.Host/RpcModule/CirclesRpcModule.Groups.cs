using System.Numerics;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Group-related methods for CirclesRpcModule.
/// Handles group queries and membership operations.
/// </summary>
public partial class CirclesRpcModule
{
    public async Task<PagedResponse<GroupRow>> FindGroups(int limit = 50, GroupQueryParams? queryParams = null, string? cursor = null)
    {
        await using var connection = await CreateConnectionAsync();

        // Decode cursor if provided
        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);

        // Build SQL query with filters
        var sql = new System.Text.StringBuilder(@"
            SELECT
                r.""group"",
                r.name,
                r.symbol,
                r.mint,
                r.treasury,
                r.""blockNumber"",
                r.timestamp,
                r.""transactionIndex"",
                r.""logIndex""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE 1=1
        ");

        var parameters = new List<NpgsqlParameter>();

        // Apply filters
        if (queryParams != null)
        {
            if (!string.IsNullOrEmpty(queryParams.NameStartsWith))
            {
                sql.Append(@" AND r.name ILIKE @namePrefix ESCAPE '\'");
                parameters.Add(new NpgsqlParameter("namePrefix", EscapeLikePattern(queryParams.NameStartsWith) + "%"));
            }

            if (!string.IsNullOrEmpty(queryParams.SymbolStartsWith))
            {
                sql.Append(@" AND r.symbol ILIKE @symbolPrefix ESCAPE '\'");
                parameters.Add(new NpgsqlParameter("symbolPrefix", EscapeLikePattern(queryParams.SymbolStartsWith) + "%"));
            }

            if (queryParams.OwnerIn != null && queryParams.OwnerIn.Length > 0)
            {
                var normalizedOwners = queryParams.OwnerIn.Select(o => o.ToLower()).ToArray();
                sql.Append(" AND r.mint = ANY(@owners)");
                parameters.Add(new NpgsqlParameter("owners", normalizedOwners));
            }
        }

        // Apply cursor for pagination
        if (cursorBlock.HasValue && cursorTxIndex.HasValue && cursorLogIndex.HasValue)
        {
            sql.Append(@"
                AND (r.""blockNumber"", r.""transactionIndex"", r.""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)");
            parameters.Add(new NpgsqlParameter("cursorBlock", cursorBlock.Value));
            parameters.Add(new NpgsqlParameter("cursorTxIndex", cursorTxIndex.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex.Value));
        }

        sql.Append(@"
            ORDER BY r.""blockNumber"" DESC, r.""transactionIndex"" DESC, r.""logIndex"" DESC
            LIMIT @limit
        ");

        // Fetch one extra to determine if there are more results
        parameters.Add(new NpgsqlParameter("limit", limit + 1));

        await using var command = new NpgsqlCommand(sql.ToString(), connection);
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<GroupRow>();
        var cursorData = new List<(long blockNumber, int txIndex, int logIndex)>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new GroupRow(
                Group: reader.GetString(0),
                Name: reader.GetString(1),
                Symbol: reader.GetString(2),
                Mint: reader.GetString(3),
                Treasury: reader.GetString(4),
                BlockNumber: reader.GetInt64(5),
                Timestamp: reader.GetInt64(6)
            ));
            cursorData.Add((reader.GetInt64(5), reader.GetInt32(7), reader.GetInt32(8)));
        }

        // Check if there are more results
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
            cursorData.RemoveAt(cursorData.Count - 1);
        }

        // Generate next cursor from the data we already have
        string? nextCursor = null;
        if (hasMore && cursorData.Count > 0)
        {
            var lastCursor = cursorData[^1];
            nextCursor = CursorUtils.EncodeCursor(lastCursor.blockNumber, lastCursor.txIndex, lastCursor.logIndex);
        }

        return new PagedResponse<GroupRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    public async Task<PagedResponse<GroupMembershipRow>> GetGroupMembers(string groupAddress, int limit = 100, string? cursor = null)
    {
        groupAddress = ValidateAndNormalizeAddress(groupAddress, nameof(groupAddress));

        // If cache service is enabled and no cursor (first page), try cache first.
        // Block-pinned requests (X-Max-Block-Number present) bypass the head-only cache and fall
        // through to GetGroupMembershipInternal, which fully pins: it reads V_CrcV2_GroupMemberships,
        // which has a circles_at_block twin.
        if (_settings.UseCacheService && _cacheServiceClient != null && string.IsNullOrEmpty(cursor)
            && GetMaxBlockNumberFromHeader() is null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for group members query for {GroupAddress}", groupAddress);

                var cacheResult = await _cacheServiceClient.GetGroupMembersAsync(groupAddress);

                if (cacheResult != null)
                {
                    // Convert cache response to RPC response format
                    // Note: Cache doesn't have block/tx info, so we use 0 for those fields
                    var allMembers = cacheResult.Members
                        .Select(m => new GroupMembershipRow(
                            BlockNumber: 0,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            TransactionIndex: 0,
                            LogIndex: 0,
                            TransactionHash: "",
                            Group: m.Group,
                            Member: m.Member,
                            ExpiryTime: m.ExpiryTime
                        ))
                        .ToList();

                    var hasMore = allMembers.Count > limit;

                    // If cache has more results than requested, fall through to DB
                    // which supports proper cursor pagination. Otherwise clients get
                    // HasMore:true but NextCursor:null and can't page further.
                    if (hasMore)
                    {
                        _logger?.LogDebug("Cache has more group members than limit ({Limit}), falling through to DB for pagination support", limit);
                        // Fall through to DB path below
                    }
                    else
                    {
                        return new PagedResponse<GroupMembershipRow>(
                            Results: allMembers.ToArray(),
                            HasMore: false,
                            NextCursor: null
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service group members query failed, falling back to database");
                // Fall through to database query below
            }
        }

        return await GetGroupMembershipInternal(groupAddress, limit, cursor, filterByGroup: true);
    }

    public async Task<PagedResponse<GroupMembershipRow>> GetGroupMemberships(string memberAddress, int limit = 50, string? cursor = null)
    {
        memberAddress = ValidateAndNormalizeAddress(memberAddress, nameof(memberAddress));

        // If cache service is enabled and no cursor (first page), try cache first.
        // Block-pinned requests (X-Max-Block-Number present) bypass the head-only cache and fall
        // through to GetGroupMembershipInternal, which fully pins: it reads V_CrcV2_GroupMemberships,
        // which has a circles_at_block twin.
        if (_settings.UseCacheService && _cacheServiceClient != null && string.IsNullOrEmpty(cursor)
            && GetMaxBlockNumberFromHeader() is null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for member groups query for {MemberAddress}", memberAddress);

                var cacheResult = await _cacheServiceClient.GetMemberGroupsAsync(memberAddress);

                if (cacheResult != null)
                {
                    // Convert cache response to RPC response format
                    var allGroups = cacheResult.Groups
                        .Select(g => new GroupMembershipRow(
                            BlockNumber: 0,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            TransactionIndex: 0,
                            LogIndex: 0,
                            TransactionHash: "",
                            Group: g.Group,
                            Member: g.Member,
                            ExpiryTime: g.ExpiryTime
                        ))
                        .ToList();

                    var hasMore = allGroups.Count > limit;

                    // If cache has more results than requested, fall through to DB
                    // which supports proper cursor pagination
                    if (hasMore)
                    {
                        _logger?.LogDebug("Cache has more group memberships than limit ({Limit}), falling through to DB for pagination support", limit);
                        // Fall through to DB path below
                    }
                    else
                    {
                        return new PagedResponse<GroupMembershipRow>(
                            Results: allGroups.ToArray(),
                            HasMore: false,
                            NextCursor: null
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service member groups query failed, falling back to database");
                // Fall through to database query below
            }
        }

        return await GetGroupMembershipInternal(memberAddress, limit, cursor, filterByGroup: false);
    }

    private async Task<PagedResponse<GroupMembershipRow>> GetGroupMembershipInternal(
        string address,
        int limit,
        string? cursor,
        bool filterByGroup)
    {
        var normalizedAddress = address; // already validated and lowered by caller
        await using var connection = await CreateConnectionAsync();

        // Decode cursor if provided
        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);

        // Build SQL query
        var filterColumn = filterByGroup ? "\"group\"" : "member";
        var sql = $@"
            SELECT
                ""blockNumber"",
                timestamp,
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                ""group"",
                member,
                ""expiryTime""
            FROM ""V_CrcV2_GroupMemberships""
            WHERE {filterColumn} = @address
        ";

        var parameters = new List<NpgsqlParameter>
        {
            new("address", normalizedAddress)
        };

        // Apply cursor for pagination
        if (cursorBlock.HasValue && cursorTxIndex.HasValue && cursorLogIndex.HasValue)
        {
            sql += @"
                AND (""blockNumber"", ""transactionIndex"", ""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)";
            parameters.Add(new NpgsqlParameter("cursorBlock", cursorBlock.Value));
            parameters.Add(new NpgsqlParameter("cursorTxIndex", cursorTxIndex.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex.Value));
        }

        sql += @"
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";

        // Fetch one extra to determine if there are more results
        parameters.Add(new NpgsqlParameter("limit", limit + 1));

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<GroupMembershipRow>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            // Handle potentially large numeric values - use BigInteger and cap at long.MaxValue
            var expiryTimeBig = reader.GetFieldValue<BigInteger>(7);
            var expiryTime = expiryTimeBig > long.MaxValue ? long.MaxValue : (long)expiryTimeBig;

            results.Add(new GroupMembershipRow(
                BlockNumber: reader.GetInt64(0),
                Timestamp: reader.GetInt64(1),
                TransactionIndex: reader.GetInt32(2),
                LogIndex: reader.GetInt32(3),
                TransactionHash: reader.GetString(4),
                Group: reader.GetString(5),
                Member: reader.GetString(6),
                ExpiryTime: expiryTime
            ));
        }

        // Check if there are more results
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1); // Remove the extra row
        }

        // Generate next cursor
        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var lastResult = results[^1];
            nextCursor = CursorUtils.EncodeCursor(lastResult.BlockNumber, lastResult.TransactionIndex, lastResult.LogIndex);
        }

        return new PagedResponse<GroupMembershipRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }
}
