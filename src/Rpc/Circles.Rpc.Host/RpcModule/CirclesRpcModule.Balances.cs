using System.Globalization;
using System.Numerics;
using Circles.Common;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Balance-related methods for CirclesRpcModule.
/// Handles token balance queries and conversions.
/// </summary>
public partial class CirclesRpcModule
{
    public async Task<TotalBalanceResponse> GetTotalBalance(string address, int version, bool? asTimeCircles = true)
    {
        // If cache service is enabled and asTimeCircles is true (or null), use cache for performance
        if (_settings.UseCacheService && _cacheServiceClient != null && (asTimeCircles == null || asTimeCircles == true))
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for total balance query (address={Address}, version={Version})", address, version);

                string cacheBalance;
                if (version == 1)
                {
                    cacheBalance = await _cacheServiceClient.GetTotalBalanceV1Async(address);
                }
                else if (version == 2)
                {
                    cacheBalance = await _cacheServiceClient.GetTotalBalanceV2Async(address);
                }
                else
                {
                    cacheBalance = await _cacheServiceClient.GetTotalBalanceAsync(address);
                }

                return new TotalBalanceResponse(cacheBalance);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service query failed, falling back to database (address={Address}, version={Version})", address, version);
                // Fall through to database query below
            }
        }

        // For raw balance (asTimeCircles=false), use optimized direct SQL query
        // This avoids the expensive GetTokenBalancesForAccount which fetches all token details
        if (asTimeCircles == false)
        {
            _logger?.LogDebug("Using optimized SQL for raw total balance (address={Address}, version={Version})", address, version);
            var rawBalance = await GetRawTotalBalanceAsync(address, version);
            return new TotalBalanceResponse(rawBalance);
        }

        // Fallback for demurraged balance: use traditional database approach
        _logger?.LogDebug("Using database for total balance query (address={Address}, version={Version})", address, version);
        var balances = await GetTokenBalancesForAccount(address);
        var relevantBalances = balances.Where(o => o.Version == version);

        var totalBalance = relevantBalances
            .Select(o => o.Circles)
            .Sum();

        return new TotalBalanceResponse(totalBalance.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Optimized query for raw total balance (asTimeCircles=false path).
    /// Uses direct SQL aggregation instead of fetching all token details.
    /// WARNING: The V2 result mixes unit systems and is an APPROXIMATION:
    /// - V2 ERC1155: totalBalance = static transfer aggregate (demurrage not applied)
    /// - V2 wrapped inflationary: static/inflationary transfer aggregate
    /// - V2 wrapped demurraged: raw demurraged transfer sums (each transfer in its own time-specific denomination)
    /// Summing these values is not mathematically precise. For accurate balances, use asTimeCircles=true
    /// which converts all values to a common unit via GetTokenBalancesForAccount.
    /// </summary>
    private async Task<string> GetRawTotalBalanceAsync(string address, int version)
    {
        var lowerAddress = address.ToLower();
        await using var connection = await CreateConnectionAsync();

        string sql;
        if (version == 1)
        {
            // V1: Sum raw attoCrc balances directly from CrcV1_Transfer table
            // FIX-010: Bypass V_CrcV1_BalancesByAccountAndToken view which uses CTE that prevents index usage
            // Direct query uses indexes on "from" and "to" columns for O(log n) lookup instead of full table scan
            sql = @"
                WITH account_transfers AS (
                    SELECT ""tokenAddress"", -amount AS delta
                    FROM ""CrcV1_Transfer""
                    WHERE ""from"" = @address
                    UNION ALL
                    SELECT ""tokenAddress"", amount AS delta
                    FROM ""CrcV1_Transfer""
                    WHERE ""to"" = @address
                ),
                token_balances AS (
                    SELECT ""tokenAddress"", SUM(delta) as balance
                    FROM account_transfers
                    GROUP BY ""tokenAddress""
                    HAVING SUM(delta) > 0
                )
                SELECT COALESCE(SUM(balance), 0)::text FROM token_balances
            ";
        }
        else if (version == 2)
        {
            // V2: Sum raw static balances from ERC1155 tokens
            // Note: totalBalance in V2 view is the raw (non-demurraged) balance
            // Also include wrapped ERC20 token balances
            sql = @"
                WITH v2_erc1155 AS (
                    SELECT COALESCE(SUM(""totalBalance""), 0) as balance
                    FROM ""V_CrcV2_BalancesByAccountAndToken""
                    WHERE account = @address
                ),
                v2_wrapped_static AS (
                    -- Inflationary (static) wrapped tokens
                    SELECT COALESCE(SUM(
                        CASE
                            WHEN wt.""to"" = @address THEN wt.amount
                            WHEN wt.""from"" = @address THEN -wt.amount
                            ELSE 0
                        END
                    ), 0) as balance
                    FROM ""CrcV2_Erc20WrapperTransfer"" wt
                    JOIN ""CrcV2_ERC20WrapperDeployed"" wd
                        ON wd.""erc20Wrapper"" = wt.""tokenAddress"" AND wd.""circlesType"" = 1
                    WHERE wt.""to"" = @address OR wt.""from"" = @address
                ),
                v2_wrapped_demurraged AS (
                    -- Demurraged wrapped tokens (need to sum raw, not apply demurrage for static)
                    SELECT COALESCE(SUM(
                        CASE
                            WHEN wt.""to"" = @address THEN wt.amount
                            WHEN wt.""from"" = @address THEN -wt.amount
                            ELSE 0
                        END
                    ), 0) as balance
                    FROM ""CrcV2_Erc20WrapperTransfer"" wt
                    JOIN ""CrcV2_ERC20WrapperDeployed"" wd
                        ON wd.""erc20Wrapper"" = wt.""tokenAddress"" AND wd.""circlesType"" = 0
                    WHERE wt.""to"" = @address OR wt.""from"" = @address
                )
                SELECT (
                    (SELECT balance FROM v2_erc1155) +
                    (SELECT balance FROM v2_wrapped_static) +
                    (SELECT balance FROM v2_wrapped_demurraged)
                )::text
            ";
        }
        else
        {
            // Combined V1 + V2
            sql = @"
                WITH v1_total AS (
                    SELECT COALESCE(SUM(""totalBalance""), 0) as balance
                    FROM ""V_CrcV1_BalancesByAccountAndToken""
                    WHERE account = @address
                ),
                v2_erc1155 AS (
                    SELECT COALESCE(SUM(""totalBalance""), 0) as balance
                    FROM ""V_CrcV2_BalancesByAccountAndToken""
                    WHERE account = @address
                ),
                v2_wrapped_static AS (
                    SELECT COALESCE(SUM(
                        CASE
                            WHEN wt.""to"" = @address THEN wt.amount
                            WHEN wt.""from"" = @address THEN -wt.amount
                            ELSE 0
                        END
                    ), 0) as balance
                    FROM ""CrcV2_Erc20WrapperTransfer"" wt
                    JOIN ""CrcV2_ERC20WrapperDeployed"" wd
                        ON wd.""erc20Wrapper"" = wt.""tokenAddress"" AND wd.""circlesType"" = 1
                    WHERE wt.""to"" = @address OR wt.""from"" = @address
                ),
                v2_wrapped_demurraged AS (
                    SELECT COALESCE(SUM(
                        CASE
                            WHEN wt.""to"" = @address THEN wt.amount
                            WHEN wt.""from"" = @address THEN -wt.amount
                            ELSE 0
                        END
                    ), 0) as balance
                    FROM ""CrcV2_Erc20WrapperTransfer"" wt
                    JOIN ""CrcV2_ERC20WrapperDeployed"" wd
                        ON wd.""erc20Wrapper"" = wt.""tokenAddress"" AND wd.""circlesType"" = 0
                    WHERE wt.""to"" = @address OR wt.""from"" = @address
                )
                SELECT (
                    (SELECT balance FROM v1_total) +
                    (SELECT balance FROM v2_erc1155) +
                    (SELECT balance FROM v2_wrapped_static) +
                    (SELECT balance FROM v2_wrapped_demurraged)
                )::text
            ";
        }

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", lowerAddress);

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? "0";
    }

    public async Task<CirclesTokenBalance[]> GetTokenBalances(string address)
    {
        // If cache service is enabled, use it (no DB fallback needed)
        if (_settings.UseCacheService && _cacheServiceClient != null)
        {
            try
            {
                _logger?.LogDebug("Using Cache Service for token balances query (address={Address})", address);

                var cacheBalances = await _cacheServiceClient.GetTokenBalancesAsync(address);
                var cachedNow = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                _logger?.LogDebug("Cache service returned {CacheCount} balances (address={Address})",
                    cacheBalances.Length, address);

                var cachedTokenBalances = new List<CirclesTokenBalance>();

                foreach (var cacheBalance in cacheBalances)
                {
                    var lookupKey = TokenIdToAddress(cacheBalance.TokenId);

                    // Parse cached balance (raw value in Circles/decimal form)
                    var rawCachedBalance = decimal.Parse(cacheBalance.Balance);
                    var rawAttoBalance = CirclesConverter.CirclesToAttoCircles(rawCachedBalance);

                    BigInteger attoCircles;
                    decimal circles;
                    BigInteger attoCrc;
                    decimal crc;
                    BigInteger staticAttoCircles;
                    decimal staticCircles;

                    // Use token metadata directly from cache
                    var tokenAddress = lookupKey;
                    var tokenOwner = cacheBalance.TokenOwner ?? lookupKey;
                    var tokenType = cacheBalance.TokenType ?? "Unknown";
                    var isErc20 = cacheBalance.IsErc20 ?? false;
                    var isErc1155 = cacheBalance.IsErc1155 ?? false;
                    var isWrapped = cacheBalance.IsWrapped ?? false;
                    var isInflationary = cacheBalance.IsInflationary ?? false;
                    var isGroup = cacheBalance.IsGroup ?? false;

                    var tokenId = isErc1155
                        ? cacheBalance.TokenId
                        : tokenAddress;

                    // Convert balance based on token type
                    // Cache stores different balance types depending on token:
                    // - V1: raw CRC balance (convert to time-circles)
                    // - V2 ERC1155: demurraged balance (totalBalance from view)
                    // - V2 Inflationary wrapper: static/inflationary balance (raw ERC20)
                    // - V2 Demurraged wrapper: demurraged balance
                    if (tokenType == "CrcV1_Signup")
                    {
                        // V1 CRC - cached value is raw CRC, need to convert to time-circles
                        attoCrc = rawAttoBalance;
                        crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                        attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, cachedNow);
                        circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                        staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                        staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                    }
                    else if (isInflationary)
                    {
                        // V2 Inflationary (wrapped ERC20): cached value is static/inflationary
                        staticAttoCircles = rawAttoBalance;
                        staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                        attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                        circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                        attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, cachedNow);
                        crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                    }
                    else
                    {
                        // V2 Demurraged (ERC1155 or demurraged wrapper): cached value is demurraged
                        attoCircles = rawAttoBalance;
                        circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                        attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, cachedNow);
                        crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                        staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                        staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                    }

                    cachedTokenBalances.Add(new CirclesTokenBalance(
                        TokenAddress: tokenAddress,
                        TokenId: tokenId,
                        TokenOwner: tokenOwner,
                        TokenType: tokenType,
                        Version: cacheBalance.Version,
                        AttoCircles: attoCircles.ToString(CultureInfo.InvariantCulture),
                        Circles: circles,
                        StaticAttoCircles: staticAttoCircles.ToString(CultureInfo.InvariantCulture),
                        StaticCircles: staticCircles,
                        AttoCrc: attoCrc.ToString(CultureInfo.InvariantCulture),
                        Crc: crc,
                        IsErc20: isErc20,
                        IsErc1155: isErc1155,
                        IsWrapped: isWrapped,
                        IsInflationary: isInflationary,
                        IsGroup: isGroup
                    ));
                }

                return cachedTokenBalances
                    .Where(o => o.Circles > 0)
                    .OrderByDescending(o => o.Circles)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache Service query failed, falling back to database (address={Address})", address);
                // Fall through to database query below
            }
        }

        // Fallback: use database query (no RPC calls needed - all balances come from DB)
        _logger?.LogDebug("Using database for token balances query (address={Address})", address);
        var tokens = await GetTokenExposureIdsAsync(address);

        if (tokens.Count == 0)
        {
            return Array.Empty<CirclesTokenBalance>();
        }

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tokenBalances = new List<CirclesTokenBalance>();

        // ═══════════════════════════════════════════════════════════════════════════════
        // BALANCE REPRESENTATION BY TOKEN TYPE
        // ═══════════════════════════════════════════════════════════════════════════════
        //
        // Different token types store balances in different representations. This is
        // intentional and reflects how each token type works on-chain:
        //
        // ┌─────────────────────────────┬─────────────────────────┬─────────────────────────────────┐
        // │ Token Type                  │ Raw Balance From DB     │ Reason                          │
        // ├─────────────────────────────┼─────────────────────────┼─────────────────────────────────┤
        // │ V1 CRC (ERC20)              │ attoCrc (inflationary)  │ Matches on-chain balanceOf()    │
        // │ V2 Inflationary Wrapper     │ staticAttoCircles       │ Matches on-chain balanceOf()    │
        // │ V2 Demurraged (ERC1155)     │ attoCircles (demurraged)│ No simple balanceOf(); computed │
        // │ V2 Demurraged Wrapper       │ attoCircles (demurraged)│ crc_demurrage() applied in SQL  │
        // └─────────────────────────────┴────────────────────────┴─────────────────────────────────┘
        //
        // WHY THE INCONSISTENCY?
        // - V1 and V2 inflationary tokens: The raw balance IS the on-chain canonical value.
        //   Storing it allows verification against balanceOf() and preserves the original form.
        // - V2 demurraged tokens: The Hub contract handles demurrage internally. There's no
        //   simple on-chain value to compare against. The DB view applies crc_demurrage()
        //   based on lastActivity timestamp to compute the current demurraged balance.
        //
        // DEMURRAGE CALCULATION:
        // - Uses day-level granularity (not seconds): γ^(today - lastActivityDay)
        // - γ = 0.9998... ≈ (1 - 0.07)^(1/365.25) - daily decay factor for 7% annual demurrage
        // - All conversions happen at query time using current timestamp for consistency
        //
        // ═══════════════════════════════════════════════════════════════════════════════

        foreach (var token in tokens.Values)
        {
            // Balance from database - representation depends on token type (see table above)
            if (!token.Balance.HasValue)
            {
                continue; // Skip tokens without balance (shouldn't happen with current SQL)
            }

            var rawBalance = token.Balance.Value;

            BigInteger attoCircles;
            decimal circles;
            BigInteger attoCrc;
            decimal crc;
            BigInteger staticAttoCircles;
            decimal staticCircles;

            if (token.TokenType == "CrcV1_Signup")
            {
                // V1 tokens: rawBalance is attoCrc (raw ERC20 balance)
                attoCrc = rawBalance;
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
            }
            else if (token.IsInflationary)
            {
                // V2 inflationary (wrapped ERC20): rawBalance is staticAttoCircles
                staticAttoCircles = rawBalance;
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
            }
            else
            {
                // V2 demurraged tokens (ERC1155 or wrapped demurraged ERC20):
                // rawBalance is already demurraged attoCircles from the DB view
                attoCircles = rawBalance;
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
            }

            var tokenId = token.IsErc1155
                ? AddressToTokenIdBigInt(token.TokenAddress).ToString(CultureInfo.InvariantCulture)
                : token.TokenAddress;

            tokenBalances.Add(new CirclesTokenBalance(
                TokenAddress: token.TokenAddress,
                TokenId: tokenId,
                TokenOwner: token.TokenOwner,
                TokenType: token.TokenType,
                Version: token.Version,
                AttoCircles: attoCircles.ToString(CultureInfo.InvariantCulture),
                Circles: circles,
                StaticAttoCircles: staticAttoCircles.ToString(CultureInfo.InvariantCulture),
                StaticCircles: staticCircles,
                AttoCrc: attoCrc.ToString(CultureInfo.InvariantCulture),
                Crc: crc,
                IsErc20: token.IsErc20,
                IsErc1155: token.IsErc1155,
                IsWrapped: token.IsWrapped,
                IsInflationary: token.IsInflationary,
                IsGroup: token.IsGroup
            ));
        }

        var orderedResult = tokenBalances
            .Where(o => o.Circles > 0)
            .OrderByDescending(o => o.Circles)
            .ToArray();

        return orderedResult;
    }

    private async Task<CirclesTokenBalance[]> GetTokenBalancesForAccount(string address)
    {
        var tokens = await GetTokenExposureIdsAsync(address);
        var hubAddress = _settings.CirclesV2HubAddress;

        // For tokens without balance from DB (wrapped ERC20), fetch via RPC
        var tokensNeedingRpc = tokens.Values.Where(o => o.Balance == null).ToList();

        var balances = new Dictionary<string, BigInteger>();

        // Use balances from DB views where available
        foreach (var token in tokens.Values.Where(o => o.Balance != null))
        {
            balances[token.TokenAddress] = token.Balance!.Value;
        }

        // Fetch missing balances via RPC (only for wrapped ERC20 tokens)
        if (tokensNeedingRpc.Count > 0 && _nethermindRpcClient != null)
        {
            foreach (var tokenInfo in tokensNeedingRpc)
            {
                var balance = await FetchBalance(
                    tokenInfo.TokenAddress,
                    address,
                    tokenInfo.IsErc20,
                    hubAddress);

                balances[tokenInfo.TokenAddress] = balance;
            }
        }

        // Convert to CirclesTokenBalance
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tokenBalances = tokens.Values
            .Where(token => balances.ContainsKey(token.TokenAddress))
            .Select(token =>
        {
            var rawBalance = balances[token.TokenAddress];

            BigInteger attoCircles;
            decimal circles;
            BigInteger attoCrc;
            decimal crc;
            BigInteger staticAttoCircles;
            decimal staticCircles;

            // ───────────────────────────────────────────────────────────────────────
            // CONVERSION FLOW BY TOKEN TYPE
            // ───────────────────────────────────────────────────────────────────────
            // V1 CRC:      attoCrc → attoCircles → staticAttoCircles
            // V2 Inflate:  staticAttoCircles → attoCircles → attoCrc
            // V2 Demurr:   attoCircles (already demurraged) → attoCrc, staticAttoCircles
            // ───────────────────────────────────────────────────────────────────────

            if (token.TokenType == "CrcV1_Signup")
            {
                // V1 CRC: rawBalance is attoCrc (inflationary ERC20 balance)
                // Convert: attoCrc → attoCircles (apply V1 inflation factor based on period)
                attoCrc = rawBalance;
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
            }
            else if (token.IsInflationary)
            {
                // V2 Inflationary (wrapped ERC20): rawBalance is staticAttoCircles
                staticAttoCircles = rawBalance;
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
            }
            else
            {
                // V2 Demurraged (ERC1155 human/group tokens): rawBalance from DB view
                // is already demurragedTotalBalance (attoCircles with demurrage applied)
                attoCircles = rawBalance;
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
            }

            var tokenId = token.IsErc1155
                ? AddressToTokenIdBigInt(token.TokenAddress).ToString(CultureInfo.InvariantCulture)
                : token.TokenAddress;

            return new CirclesTokenBalance(
                TokenAddress: token.TokenAddress,
                TokenId: tokenId,
                TokenOwner: token.TokenOwner,
                TokenType: token.TokenType,
                Version: token.Version,
                AttoCircles: attoCircles.ToString(CultureInfo.InvariantCulture),
                Circles: circles,
                StaticAttoCircles: staticAttoCircles.ToString(CultureInfo.InvariantCulture),
                StaticCircles: staticCircles,
                AttoCrc: attoCrc.ToString(CultureInfo.InvariantCulture),
                Crc: crc,
                IsErc20: token.IsErc20,
                IsErc1155: token.IsErc1155,
                IsWrapped: token.IsWrapped,
                IsInflationary: token.IsInflationary,
                IsGroup: token.IsGroup
            );
        })
        .Where(o => o.Circles > 0)
        .OrderByDescending(o => o.Circles)
        .ToList();

        return tokenBalances.ToArray();
    }

    private async Task<BigInteger> FetchBalance(string tokenAddress, string accountAddress, bool isErc20, string hubAddress)
    {
        if (_nethermindRpcClient == null)
        {
            throw new InvalidOperationException("NethermindRpcClient is not available. Make sure BalanceMode is set to 'live'.");
        }

        if (isErc20)
        {
            // ERC20: balanceOf(address)
            var data = AbiEncoder.EncodeBalanceOfErc20(accountAddress);
            var resultHex = await _nethermindRpcClient.EthCall(tokenAddress, data);
            return AbiEncoder.DecodeUint256(resultHex);
        }
        else
        {
            // ERC1155: balanceOf(address account, uint256 tokenId)
            var tokenId = AddressToTokenIdBigInt(tokenAddress);
            var data = AbiEncoder.EncodeBalanceOfErc1155(accountAddress, tokenId);
            var resultHex = await _nethermindRpcClient.EthCall(hubAddress, data);
            return AbiEncoder.DecodeUint256(resultHex);
        }
    }

    private async Task<List<BigInteger>> GetBatchBalances(string hubAddress, string[] accounts, BigInteger[] tokenIds)
    {
        if (_nethermindRpcClient == null)
        {
            throw new InvalidOperationException("NethermindRpcClient is not available. Make sure BalanceMode is set to 'live'.");
        }

        if (accounts.Length != tokenIds.Length)
        {
            throw new ArgumentException("accounts and tokenIds length mismatch");
        }

        var data = AbiEncoder.EncodeBalanceOfBatch(accounts, tokenIds);
        var resultHex = await _nethermindRpcClient.EthCall(hubAddress, data);
        var balanceResults = AbiEncoder.DecodeUint256Array(resultHex);

        return balanceResults.ToList();
    }
}
