using System.Numerics;
using Circles.Common;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Token information and exposure methods for CirclesRpcModule.
/// Handles token metadata queries and token exposure tracking.
/// </summary>
public partial class CirclesRpcModule
{
    // Helper class to represent token information from database
    private class TokenExposureInfo
    {
        public string TokenAddress { get; set; }
        public string TokenOwner { get; set; }
        public string TokenType { get; set; }
        public int Version { get; set; }
        public bool IsErc20 { get; set; }
        public bool IsErc1155 { get; set; }
        public bool IsWrapped { get; set; }
        public bool IsInflationary { get; set; }
        public bool IsGroup { get; set; }
        /// <summary>
        /// Balance from the database view. For V1 tokens this is attoCrc (raw ERC20 balance).
        /// For V2 ERC1155 tokens this is the demurraged attoCircles.
        /// For wrapped ERC20 tokens this may be null (requires RPC fetch).
        /// </summary>
        public BigInteger? Balance { get; set; }

        public TokenExposureInfo(string tokenAddress, string tokenOwner, string tokenType, int version,
            bool isErc20, bool isErc1155, bool isWrapped, bool isInflationary, bool isGroup, BigInteger? balance = null)
        {
            TokenAddress = tokenAddress;
            TokenOwner = tokenOwner;
            TokenType = tokenType;
            Version = version;
            IsErc20 = isErc20;
            IsErc1155 = isErc1155;
            IsWrapped = isWrapped;
            IsInflationary = isInflationary;
            IsGroup = isGroup;
            Balance = balance;
        }
    }

    /// <summary>
    /// Converts an Ethereum address to a BigInteger token ID for ERC-1155.
    /// </summary>
    private static BigInteger AddressToTokenIdBigInt(string address)
    {
        var hex = address.StartsWith("0x") ? address.Substring(2) : address;
        return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// Converts a numeric ERC1155 token ID to a lowercase hex address.
    /// This is the inverse of AddressToTokenIdBigInt.
    /// </summary>
    private static string TokenIdToAddress(string tokenId)
    {
        // If it's already a hex address, just lowercase it
        if (tokenId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return tokenId.ToLowerInvariant();
        }

        // Parse as BigInteger and convert to hex address
        // Note: BigInteger.ToString("x") may add a leading '0' when the MSB nibble >= 8
        // to avoid interpreting the number as negative. We need exactly 40 hex chars
        // for a valid Ethereum address, so we take the rightmost 40 characters.
        var bigInt = BigInteger.Parse(tokenId);
        var hex = bigInt.ToString("x");
        if (hex.Length > 40)
        {
            hex = hex.Substring(hex.Length - 40);
        }
        else
        {
            hex = hex.PadLeft(40, '0');
        }
        return "0x" + hex;
    }

    private async Task<Dictionary<string, TokenExposureInfo>> GetTokenExposureIdsAsync(string address)
    {
        var lowerAddress = address; // already validated and lowered by caller
        var cacheKey = $"tokenExposure:{lowerAddress}";

        // Check cache first (5 minute TTL for token exposure data)
        if (_tokenExposureCache.TryGetValue(cacheKey, out Dictionary<string, TokenExposureInfo>? cached) && cached != null)
        {
            return cached;
        }

        // Use the balance views which correctly aggregate transfers and compute balances > 0
        // This matches the production behavior which uses in-memory caches seeded from these views
        // Also returns the balance directly from the views to avoid separate RPC calls
        // NOTE: No eth_calls needed - all balances come from DB views or transfer aggregation
        const string sql = @"
            WITH
            -- V2 wrapped ERC20 token balances (inflationary/static)
            -- These are calculated from transfer events, not from on-chain balanceOf()
            static_wrapped_balances AS (
                SELECT wt.""tokenAddress""
                     , 'CrcV2_ERC20WrapperDeployed_Inflationary' as ""type""
                     , wd.avatar as ""tokenOwner""
                     , SUM(CASE
                         WHEN wt.""to"" = @address THEN wt.amount
                         WHEN wt.""from"" = @address THEN -wt.amount
                         ELSE 0
                       END) as balance
                FROM public.""CrcV2_Erc20WrapperTransfer"" wt
                JOIN public.""CrcV2_ERC20WrapperDeployed"" wd
                  ON wd.""erc20Wrapper"" = wt.""tokenAddress"" AND wd.""circlesType"" = 1
                WHERE wt.""to"" = @address OR wt.""from"" = @address
                GROUP BY wt.""tokenAddress"", wd.avatar
                HAVING SUM(CASE
                    WHEN wt.""to"" = @address THEN wt.amount
                    WHEN wt.""from"" = @address THEN -wt.amount
                    ELSE 0
                END) > 0
            ),
            -- V2 wrapped ERC20 token balances (demurraged)
            -- Inline demurrage computation avoids PL/pgSQL function call overhead
            demurraged_wrapped_balances AS (
                SELECT wt.""tokenAddress""
                     , 'CrcV2_ERC20WrapperDeployed_Demurraged' as ""type""
                     , wd.avatar as ""tokenOwner""
                     , floor(
                         SUM(CASE
                             WHEN wt.""to"" = @address THEN wt.amount
                             WHEN wt.""from"" = @address THEN -wt.amount
                             ELSE 0
                         END)
                         * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
                             (EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
                             - (MAX(wt.""timestamp"") - 1602720000) / 86400
                         )
                       ) as balance
                FROM public.""CrcV2_Erc20WrapperTransfer"" wt
                JOIN public.""CrcV2_ERC20WrapperDeployed"" wd
                  ON wd.""erc20Wrapper"" = wt.""tokenAddress"" AND wd.""circlesType"" = 0
                WHERE wt.""to"" = @address OR wt.""from"" = @address
                GROUP BY wt.""tokenAddress"", wd.avatar
                HAVING SUM(CASE
                    WHEN wt.""to"" = @address THEN wt.amount
                    WHEN wt.""from"" = @address THEN -wt.amount
                    ELSE 0
                END) > 0
            ),
            tokens AS (
                -- V1 token balances from the materialized balance view
                -- For V1: totalBalance is the raw attoCrc (ERC20 balance)
                SELECT v1.""tokenAddress""
                     , 'CrcV1_Signup' as ""type""
                     , s.""user"" as ""tokenOwner""
                     , v1.""totalBalance"" as balance
                     , false as ""isGroup""
                FROM  public.""V_CrcV1_BalancesByAccountAndToken"" v1
                JOIN  public.""CrcV1_Signup"" s ON s.token = v1.""tokenAddress""
                WHERE v1.account = @address
                  AND v1.""totalBalance"" > 0

                UNION ALL

                -- V2 ERC1155 token balances (human/group tokens)
                -- For V2 ERC1155: use demurragedTotalBalance (demurrage already applied)
                -- This is the equivalent of what production does with LastTokenMovement + Demurrage.ApplyDemurrage()
                -- NOTE: tokenAddress in the view is already the avatar address (computed from token ID in LogParser)
                SELECT v2.""tokenAddress""
                     , CASE
                         WHEN rh.avatar IS NOT NULL THEN 'CrcV2_RegisterHuman'
                         ELSE 'CrcV2_RegisterGroup'
                       END as ""type""
                     , v2.""tokenAddress"" as ""tokenOwner""
                     , v2.""demurragedTotalBalance"" as balance
                     , rh.avatar IS NULL as ""isGroup""
                FROM  public.""V_CrcV2_BalancesByAccountAndToken"" v2
                LEFT JOIN ""CrcV2_RegisterHuman"" rh ON rh.avatar = v2.""tokenAddress""
                WHERE v2.account = @address
                  AND v2.""totalBalance"" > 0

                UNION ALL

                -- V2 wrapped ERC20 inflationary tokens (balance calculated from transfers)
                SELECT swb.*, rg.""group"" IS NOT NULL as ""isGroup""
                FROM static_wrapped_balances swb
                LEFT JOIN ""CrcV2_RegisterGroup"" rg ON rg.""group"" = swb.""tokenOwner""

                UNION ALL

                -- V2 wrapped ERC20 demurraged tokens (balance calculated with demurrage)
                SELECT dwb.*, rg.""group"" IS NOT NULL as ""isGroup""
                FROM demurraged_wrapped_balances dwb
                LEFT JOIN ""CrcV2_RegisterGroup"" rg ON rg.""group"" = dwb.""tokenOwner""
            )
            SELECT ""tokenAddress"", ""type"", ""tokenOwner"", balance, ""isGroup""
            FROM tokens
            WHERE balance > 0
        ";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", lowerAddress);

        var tokenExposureIds = new Dictionary<string, TokenExposureInfo>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var token = reader.GetString(0);
            var tokenType = reader.GetString(1);
            var tokenOwner = reader.GetString(2);

            // Read balance from column 3 (all token types now have balance from DB)
            var balanceDecimal = reader.GetDecimal(3);
            BigInteger? balance = (BigInteger)balanceDecimal;

            // Read isGroup from column 4 (computed in SQL via LEFT JOIN on CrcV2_RegisterGroup)
            var isGroup = reader.GetBoolean(4);

            var isWrapped = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary"
                or "CrcV2_ERC20WrapperDeployed_Demurraged";

            var isInflationary = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary" || tokenType is "CrcV1_Signup";

            var isErc20 = tokenType == "CrcV1_Signup" || isWrapped;
            var isErc1155 = tokenType is "CrcV2_RegisterHuman" or "CrcV2_RegisterGroup";

            var version = isWrapped || isErc1155 ? 2 : 1;

            var tokenInfo = new TokenExposureInfo(
                token,
                tokenOwner,
                tokenType,
                version,
                isErc20,
                isErc1155,
                isWrapped,
                isInflationary,
                isGroup,
                balance);

            tokenExposureIds.Add(token, tokenInfo);
        }

        // Cache for 5 minutes - token exposure data doesn't change frequently
        _tokenExposureCache.Set(cacheKey, tokenExposureIds, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            Size = 1
        });

        return tokenExposureIds;
    }

    public async Task<TokenInfo?> GetTokenInfo(string tokenAddress)
    {
        var lowerTokenAddress = ValidateAndNormalizeAddress(tokenAddress, nameof(tokenAddress));

        // If cache service is enabled, try using it first
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for token info query ({TokenAddress})", tokenAddress);

                var cacheResult = await _cacheServiceClient.GetTokenInfoAsync(lowerTokenAddress);
                if (cacheResult != null)
                {
                    return new TokenInfo(
                        TokenAddress: cacheResult.TokenAddress,
                        TokenOwner: cacheResult.TokenOwner,
                        TokenType: cacheResult.TokenType,
                        Version: cacheResult.Version,
                        IsErc20: cacheResult.IsErc20,
                        IsErc1155: cacheResult.IsErc1155,
                        IsWrapped: cacheResult.IsWrapped,
                        IsInflationary: cacheResult.IsInflationary,
                        IsGroup: cacheResult.IsGroup
                    );
                }

                // Cache returned null = token not found
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service query failed, falling back to database for token info ({TokenAddress})", tokenAddress);
                // Fall through to database query below
            }
        }

        // Fallback: use traditional database approach
        _logger?.LogDebug("Using database for token info query ({TokenAddress})", tokenAddress);

        await using var connection = await CreateConnectionAsync();

        // 1. Check for V1 token
        const string v1Sql = @"SELECT token, ""user"" as owner FROM ""CrcV1_Signup"" WHERE token = @tokenAddress LIMIT 1";
        await using (var cmd = new NpgsqlCommand(v1Sql, connection))
        {
            cmd.Parameters.AddWithValue("tokenAddress", lowerTokenAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new TokenInfo(
                    TokenAddress: reader.GetString(0),
                    TokenOwner: reader.GetString(1),
                    TokenType: "CrcV1_Signup",
                    Version: 1,
                    IsErc20: true,
                    IsErc1155: false,
                    IsWrapped: false,
                    IsInflationary: true,
                    IsGroup: false
                );
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
                return new TokenInfo(
                    TokenAddress: reader.GetString(0),
                    TokenOwner: reader.GetString(0), // For V2 avatars, the token and owner are the same
                    TokenType: type,
                    Version: 2,
                    IsErc20: false,
                    IsErc1155: true,
                    IsWrapped: false,
                    IsInflationary: false,
                    IsGroup: isGroup
                );
            }
        }

        // 3. Check for V2 Wrapped ERC20 token
        const string v2WrappedSql = @"
            SELECT wd.""erc20Wrapper"", wd.avatar, wd.""circlesType"",
                   (rg.""group"" IS NOT NULL) as ""isGroup""
            FROM ""CrcV2_ERC20WrapperDeployed"" wd
            LEFT JOIN ""CrcV2_RegisterGroup"" rg ON rg.""group"" = wd.avatar
            WHERE wd.""erc20Wrapper"" = @tokenAddress LIMIT 1";
        await using (var cmd = new NpgsqlCommand(v2WrappedSql, connection))
        {
            cmd.Parameters.AddWithValue("tokenAddress", lowerTokenAddress);
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    var tokenOwner = reader.GetString(1);
                    var circlesType = (CirclesType)reader.GetInt32(2);
                    var isGroupToken = reader.GetBoolean(3);

                    var isInflationary = circlesType == CirclesType.InflationaryCircles;
                    var tokenType = isInflationary
                        ? "CrcV2_ERC20WrapperDeployed_Inflationary"
                        : "CrcV2_ERC20WrapperDeployed_Demurraged";

                    return new TokenInfo(
                        TokenAddress: reader.GetString(0),
                        TokenOwner: tokenOwner,
                        TokenType: tokenType,
                        Version: 2,
                        IsErc20: true,
                        IsErc1155: false,
                        IsWrapped: true,
                        IsInflationary: isInflationary,
                        IsGroup: isGroupToken
                    );
                }
            }
        }

        // Return null for non-existent tokens instead of throwing exception
        return null;
    }

    public async Task<TokenInfo?[]> GetTokenInfoBatch(string[] tokenAddresses)
    {
        for (int i = 0; i < tokenAddresses.Length; i++)
            tokenAddresses[i] = ValidateAndNormalizeAddress(tokenAddresses[i], $"tokenAddresses[{i}]");

        if (tokenAddresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenAddresses), "Batch size exceeds 1000");
        }

        // If cache service is enabled, try batch API for efficiency
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for token info batch query ({Count} addresses)", tokenAddresses.Length);

                var lowerAddresses = tokenAddresses.Select(a => a.ToLowerInvariant()).ToArray();
                var cacheResults = await _cacheServiceClient.GetTokenInfoBatchAsync(lowerAddresses);

                var results = new TokenInfo?[tokenAddresses.Length];
                for (int i = 0; i < cacheResults.Length && i < tokenAddresses.Length; i++)
                {
                    var cacheResult = cacheResults[i];
                    if (cacheResult != null)
                    {
                        results[i] = new TokenInfo(
                            TokenAddress: cacheResult.TokenAddress,
                            TokenOwner: cacheResult.TokenOwner,
                            TokenType: cacheResult.TokenType,
                            Version: cacheResult.Version,
                            IsErc20: cacheResult.IsErc20,
                            IsErc1155: cacheResult.IsErc1155,
                            IsWrapped: cacheResult.IsWrapped,
                            IsInflationary: cacheResult.IsInflationary,
                            IsGroup: cacheResult.IsGroup
                        );
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service batch query failed, falling back to individual lookups");
                // Fall through to individual lookups below
            }
        }

        // Fallback: Execute lookups with bounded concurrency to prevent connection pool exhaustion
        // Each GetTokenInfo opens up to 3 sequential DB connections (V1, V2 avatar, V2 wrapper)
        _logger?.LogDebug("Using database for token info batch query ({Count} addresses)", tokenAddresses.Length);

        const int maxConcurrency = 10;
        var dbResults = new TokenInfo?[tokenAddresses.Length];
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = tokenAddresses.Select(async (tokenAddress, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                dbResults[index] = await GetTokenInfo(tokenAddress);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load token info for {TokenAddress}", tokenAddress);
                dbResults[index] = null;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        // Return array with same length as input, preserving positions
        return dbResults;
    }

    public async Task<PagedResponse<TokenHolderRow>> GetTokenHolders(string tokenAddress, int limit = 100, string? cursor = null)
    {
        var normalizedToken = ValidateAndNormalizeAddress(tokenAddress, nameof(tokenAddress));
        await using var connection = await CreateConnectionAsync();

        // Build query with cursor pagination - UNION both V1 and V2 views
        // V1: totalBalance is the raw ERC20 balance (matches on-chain balanceOf)
        // V2: demurragedTotalBalance matches Hub.sol balanceOf() (totalBalance is static/raw)
        var sql = @$"
            SELECT
                account,
                balance,
                ""tokenAddress"",
                version
            FROM (
                SELECT account, ""totalBalance"" as balance, ""tokenAddress"", 1 as version
                FROM ""V_CrcV1_BalancesByAccountAndToken""
                WHERE ""tokenAddress"" = @tokenAddress AND ""totalBalance"" > 0
                UNION ALL
                SELECT account, ""demurragedTotalBalance"" as balance, ""tokenAddress"", 2 as version
                FROM ""V_CrcV2_BalancesByAccountAndToken""
                WHERE ""tokenAddress"" = @tokenAddress AND ""totalBalance"" > 0
            ) AS combined_balances
            WHERE 1=1
              {(!string.IsNullOrEmpty(cursor) ? "AND account > @cursor" : "")}
            ORDER BY account ASC
            LIMIT @limit
        ";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("tokenAddress", normalizedToken);
        cmd.Parameters.AddWithValue("limit", limit + 1); // Fetch one extra to check for more

        if (!string.IsNullOrEmpty(cursor))
        {
            cmd.Parameters.AddWithValue("cursor", cursor.ToLower());
        }

        var results = new List<TokenHolderRow>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var account = reader.GetString(0);
            var balance = reader.GetFieldValue<System.Numerics.BigInteger>(1);
            var tokenAddr = reader.GetString(2);
            var version = reader.GetInt32(3);

            results.Add(new TokenHolderRow(
                Account: account,
                Balance: balance.ToString(),
                TokenAddress: tokenAddr,
                Version: version
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
            nextCursor = results[^1].Account;
        }

        return new PagedResponse<TokenHolderRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }
}
