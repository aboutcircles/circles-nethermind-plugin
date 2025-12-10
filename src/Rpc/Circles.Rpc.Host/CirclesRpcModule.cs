using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Circles.Index.Common.Dto;
using Nethermind.Int256;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host;

/// <summary>
/// Utility class for cursor-based pagination.
/// </summary>
public static class CursorUtils
{
    /// <summary>
    /// Decodes a base64-encoded cursor string into blockNumber, transactionIndex, logIndex.
    /// </summary>
    public static (long? blockNumber, int? transactionIndex, int? logIndex) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null, null);
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':');
            if (parts.Length >= 3)
            {
                return (long.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            }
        }
        catch
        {
            // Invalid cursor, ignore
        }

        return (null, null, null);
    }

    /// <summary>
    /// Decodes a base64-encoded cursor string into blockNumber, transactionIndex, logIndex, batchIndex.
    /// </summary>
    public static (long? blockNumber, int? transactionIndex, int? logIndex, int? batchIndex) DecodeCursorWithBatch(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null, null, null);
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':');
            if (parts.Length >= 4)
            {
                return (long.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            }
            else if (parts.Length >= 3)
            {
                return (long.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), 0);
            }
        }
        catch
        {
            // Invalid cursor, ignore
        }

        return (null, null, null, null);
    }

    /// <summary>
    /// Encodes blockNumber, transactionIndex, logIndex into a base64-encoded cursor string.
    /// </summary>
    public static string? EncodeCursor(long blockNumber, int transactionIndex, int logIndex)
    {
        var cursorString = $"{blockNumber}:{transactionIndex}:{logIndex}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cursorString));
    }

    /// <summary>
    /// Encodes blockNumber, transactionIndex, logIndex, batchIndex into a base64-encoded cursor string.
    /// </summary>
    public static string? EncodeCursorWithBatch(long blockNumber, int transactionIndex, int logIndex, int batchIndex)
    {
        var cursorString = $"{blockNumber}:{transactionIndex}:{logIndex}:{batchIndex}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cursorString));
    }

    /// <summary>
    /// Generates a cursor from the last result in a list, using the specified cursor-generating function.
    /// </summary>
    public static async Task<string?> GenerateCursorFromLastResult<T>(
        IReadOnlyList<T> results,
        Func<T, Task<(long blockNumber, int transactionIndex, int logIndex)?>> getCursorValues,
        NpgsqlConnection connection)
    {
        if (results.Count == 0)
        {
            return null;
        }

        var lastResult = results[^1];
        var cursorValues = await getCursorValues(lastResult);
        if (cursorValues.HasValue)
        {
            return EncodeCursor(cursorValues.Value.blockNumber, cursorValues.Value.transactionIndex, cursorValues.Value.logIndex);
        }

        return null;
    }
}

public class CirclesRpcModule : ICirclesRpcModule
{
    private readonly Settings _settings;
    private readonly string _readOnlyDbConnectionString;
    private readonly MemoryCache _profileByCidCache;
    private readonly MemoryCache _tokenExposureCache;
    private static readonly HttpClient HttpClient = new();
    private readonly NethermindRpcClient? _nethermindRpcClient;
    private readonly ILogger<CirclesRpcModule>? _logger;
    private readonly CacheServiceClient.CacheServiceClient? _cacheServiceClient;

    public CirclesRpcModule(
        Settings settings,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<CirclesRpcModule>? logger = null,
        CacheServiceClient.CacheServiceClient? cacheServiceClient = null)
    {
        _settings = settings;
        _readOnlyDbConnectionString = settings.IndexReadonlyDbConnectionString;
        _profileByCidCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        _tokenExposureCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 50_000 });
        _logger = logger;
        _cacheServiceClient = cacheServiceClient;

        // Initialize Nethermind RPC client if BalanceMode is "live"
        if (_settings.BalanceMode.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            if (httpClientFactory != null)
            {
                _nethermindRpcClient = new NethermindRpcClient(httpClientFactory, _settings.NethermindRpcUrl ?? "http://localhost:8545");
            }
            else
            {
                _nethermindRpcClient = null; // HttpClientFactory not available, cannot create client
            }
        }
    }

    private async Task<NpgsqlConnection> CreateConnectionAsync()
    {
        var connection = new NpgsqlConnection(_readOnlyDbConnectionString);
        await connection.OpenAsync();
        return connection;
    }

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

        // Fallback: use traditional database + Nethermind approach
        _logger?.LogDebug("Using database for total balance query (address={Address}, version={Version})", address, version);
        var balances = await GetTokenBalancesForAccount(address);
        var relevantBalances = balances.Where(o => o.Version == version);

        string balance;
        if (asTimeCircles == null || asTimeCircles == true)
        {
            var totalBalance = relevantBalances
                .Select(o => o.Circles)
                .Sum();

            balance = totalBalance.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            var totalBalance = relevantBalances
                .Select(o => UInt256.Parse(o.StaticAttoCircles))
                .Aggregate((UInt256)0, (acc, val) => acc + val);

            balance = totalBalance.ToString(CultureInfo.InvariantCulture);
        }

        return new TotalBalanceResponse(balance);
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

        foreach (var token in tokens.Values)
        {
            // Balance comes directly from the database - no RPC calls needed!
            // GetTokenExposureIdsAsync returns balance for all token types:
            // - V1: totalBalance (raw attoCrc)
            // - V2 ERC1155: demurragedTotalBalance (attoCircles with demurrage applied)
            // - V2 wrapped inflationary: sum of transfers (staticAttoCircles)
            // - V2 wrapped demurraged: sum of transfers with demurrage applied (attoCircles)
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

    private async Task<Dictionary<string, TokenExposureInfo>> GetTokenExposureIdsAsync(string address)
    {
        var lowerAddress = address.ToLower();
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
            -- Need to apply demurrage based on last activity timestamp
            demurraged_wrapped_balances AS (
                SELECT wt.""tokenAddress""
                     , 'CrcV2_ERC20WrapperDeployed_Demurraged' as ""type""
                     , wd.avatar as ""tokenOwner""
                     , floor(crc_demurrage(1675209600::bigint, MAX(wt.""timestamp""),
                         SUM(CASE
                             WHEN wt.""to"" = @address THEN wt.amount
                             WHEN wt.""from"" = @address THEN -wt.amount
                             ELSE 0
                         END))) as balance
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
                FROM  public.""V_CrcV2_BalancesByAccountAndToken"" v2
                LEFT JOIN ""CrcV2_RegisterHuman"" rh ON rh.avatar = v2.""tokenAddress""
                WHERE v2.account = @address
                  AND v2.""totalBalance"" > 0

                UNION ALL

                -- V2 wrapped ERC20 inflationary tokens (balance calculated from transfers)
                SELECT * FROM static_wrapped_balances

                UNION ALL

                -- V2 wrapped ERC20 demurraged tokens (balance calculated with demurrage)
                SELECT * FROM demurraged_wrapped_balances
            )
            SELECT ""tokenAddress"", ""type"", ""tokenOwner"", balance
            FROM tokens
            WHERE balance > 0
        ";

        await using var connection = new NpgsqlConnection(_readOnlyDbConnectionString);
        await connection.OpenAsync();
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

            var isWrapped = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary"
                or "CrcV2_ERC20WrapperDeployed_Demurraged";

            var isInflationary = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary" || tokenType is "CrcV1_Signup";
            var isGroup = tokenType is "CrcV2_RegisterGroup";

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
        var balances = AbiEncoder.DecodeUint256Array(resultHex);

        return balances.ToList();
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

            if (token.TokenType == "CrcV1_Signup")
            {
                // V1 CRC: rawBalance from DB view is attoCrc (raw ERC20 balance)
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

    public async Task<TokenInfo?> GetTokenInfo(string tokenAddress)
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
            SELECT wd.""erc20Wrapper"", wd.avatar, wd.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" wd
            WHERE wd.""erc20Wrapper"" = @tokenAddress LIMIT 1";
        await using (var cmd = new NpgsqlCommand(v2WrappedSql, connection))
        {
            cmd.Parameters.AddWithValue("tokenAddress", lowerTokenAddress);
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    var tokenOwner = reader.GetString(1);
                    var circlesType = reader.GetInt32(2);

                    // Determine token type based on circlesType
                    // circlesType = 0 for demurraged, 1 for inflationary
                    var isInflationary = circlesType == 1;
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
                        IsGroup: false
                    );
                }
            }
        }

        // Return null for non-existent tokens instead of throwing exception
        return null;
    }

    public async Task<TokenInfo?[]> GetTokenInfoBatch(string[] tokenAddresses)
    {
        if (tokenAddresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenAddresses), "Batch size exceeds 1000");
        }

        // Execute all lookups in parallel
        var getTokenInfoTasks = tokenAddresses.Select(async tokenAddress =>
        {
            try
            {
                return await GetTokenInfo(tokenAddress);
            }
            catch
            {
                // Return null for tokens that don't exist or fail to load
                return null;
            }
        }).ToArray();

        var results = await Task.WhenAll(getTokenInfoTasks);

        // Return array with same length as input, preserving positions
        return results;
    }

    public async Task<AvatarInfo> GetAvatarInfo(string address)
    {
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
        var results = await GetAvatarInfoBatchInternal(addresses);
        return results.Where(r => r != null).ToArray()!;
    }

    private async Task<AvatarInfo?[]> GetAvatarInfoBatchInternal(string[] addresses)
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
                            Type: cacheInfo.Type,
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
    /// Fetches V2 avatar information from the database.
    /// </summary>
    private async Task<Dictionary<string, AvatarInfo>> FetchV2AvatarsAsync(string[] addresses)
    {
        var v2AvatarMap = new Dictionary<string, AvatarInfo>();
        const string v2Sql = @"
            SELECT a.avatar, a.""timestamp"", a.name, a.type, rn.""metadataDigest"", rsn.""shortName"", a.""cidV0Digest""
            FROM ""V_CrcV2_Avatars"" a
            LEFT JOIN ""CrcV2_UpdateMetadataDigest"" rn ON rn.avatar = a.avatar
            LEFT JOIN ""CrcV2_RegisterShortName"" rsn ON rsn.avatar = a.avatar
            WHERE a.avatar = ANY(@addresses)";

        await using var connection = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(v2Sql, connection);
        cmd.Parameters.AddWithValue("addresses", addresses);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var avatar = reader.GetString(0);
            var avatarType = reader.GetString(3);
            var isHuman = avatarType == "CrcV2_RegisterHuman";

            // Convert metadataDigest bytes to proper IPFS CIDv0
            string? cid = null;
            if (!reader.IsDBNull(4))
            {
                var metadataDigest = (byte[])reader.GetValue(4);
                cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
            }

            // cidV0Digest should be empty string per remote implementation
            var cidV0Digest = "";

            v2AvatarMap[avatar] = new AvatarInfo(
                Version: 2,
                Type: avatarType,
                Avatar: avatar,
                TokenId: avatar,  // For V2, tokenId is the avatar address (for ERC1155)
                HasV1: false,
                V1Token: null,
                CidV0Digest: cidV0Digest,
                CidV0: cid,
                IsHuman: isHuman,
                Name: reader.IsDBNull(2) ? null : reader.GetString(2),
                Symbol: reader.IsDBNull(5) ? "" : reader.GetString(5)
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
        var result = new JsonElement?[addresses.Length];

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
        // We call the cache service client directly to get the full AvatarInfoResponse with ShortName
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
                var avatarType = reader.GetString(1);
                avatarTypeMap[avatar] = avatarType;
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

        // Check cache first
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

        // Fetch missing profiles from database
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
        while (await reader.ReadAsync())
        {
            var avatar = reader.GetString(0);
            var avatarName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shortName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var avatarType = reader.GetString(3);
            var payload = reader.IsDBNull(4) ? null : reader.GetString(4);
            var cid = reader.IsDBNull(5) ? null : reader.GetString(5);

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

            results.Add(new ProfileSearchResultItem(
                Avatar: avatar,
                AvatarInfo: avatarInfo,
                Profile: profile
            ));
        }

        return new ProfileSearchResult(Total: results.Count, Results: results.ToArray());
    }


    public async Task<TrustRelationsResponse> GetTrustRelations(string address)
    {
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
                        User: address.ToLower(),
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
        command.Parameters.AddWithValue("address", address.ToLower());
        var trusts = new List<TrustRelation>();
        var trustedBy = new List<TrustRelation>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var user = reader.GetString(0);
            var canSendTo = reader.GetString(1);
            // V1 trust limits are percentages (0-100), stored as uint256 in contract but always fit in int32
            var limit = Convert.ToInt32(reader.GetValue(2));
            if (user.Equals(address, StringComparison.OrdinalIgnoreCase))
            {
                trusts.Add(new TrustRelation(User: canSendTo, Limit: limit));
            }
            else
            {
                trustedBy.Add(new TrustRelation(User: user, Limit: limit));
            }
        }
        return new TrustRelationsResponse(User: address.ToLower(), Trusts: trusts.ToArray(), TrustedBy: trustedBy.ToArray());
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

    public async Task<AggregatedTrustRelation[]> GetAggregatedTrustRelations(string avatar)
    {
        var normalizedAvatar = avatar.ToLower();

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

            // Map avatar type to simple format
            string? objectAvatarType = avatarType switch
            {
                "Human" => "Human",
                "Organization" => "Organization",
                "Group" => "Group",
                _ => null
            };

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
                sql.Append(" AND r.name ILIKE @namePrefix");
                parameters.Add(new NpgsqlParameter("namePrefix", queryParams.NameStartsWith + "%"));
            }

            if (!string.IsNullOrEmpty(queryParams.SymbolStartsWith))
            {
                sql.Append(" AND r.symbol ILIKE @symbolPrefix");
                parameters.Add(new NpgsqlParameter("symbolPrefix", queryParams.SymbolStartsWith + "%"));
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
        // If cache service is enabled and no cursor (first page), try cache first
        if (_settings.UseCacheService && _cacheServiceClient != null && string.IsNullOrEmpty(cursor))
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
                    var results = allMembers.Take(limit).ToArray();

                    // Cache-based results don't support pagination via cursor
                    return new PagedResponse<GroupMembershipRow>(
                        Results: results,
                        HasMore: hasMore,
                        NextCursor: null // Cache doesn't support cursor pagination
                    );
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
        // If cache service is enabled and no cursor (first page), try cache first
        if (_settings.UseCacheService && _cacheServiceClient != null && string.IsNullOrEmpty(cursor))
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
                    var results = allGroups.Take(limit).ToArray();

                    return new PagedResponse<GroupMembershipRow>(
                        Results: results,
                        HasMore: hasMore,
                        NextCursor: null // Cache doesn't support cursor pagination
                    );
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
        var normalizedAddress = address.ToLower();
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

    public async Task<PagedResponse<TransactionHistoryRow>> GetTransactionHistory(
        string avatarAddress, 
        int limit = 50, 
        string? cursor = null,
        int? version = null,
        bool excludeIntermediary = false)
    {
        var normalizedAddress = avatarAddress.ToLower();
        await using var connection = await CreateConnectionAsync();

        // Decode cursor if provided
        var (cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex) = CursorUtils.DecodeCursorWithBatch(cursor);

        string sql;
        
        if (excludeIntermediary)
        {
            // Use TransferSummary view which contains only real user-to-user transfers
            // (intermediary hop transfers are filtered out and stored in the 'events' JSON column)
            sql = @$"
                SELECT 
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    version,
                    NULL::text as operator,
                    ""from"",
                    ""to"",
                    NULL::text as id,
                    value
                FROM ""V_Crc_TransferSummary""
                WHERE (""from"" = @address OR ""to"" = @address)
                  {(version.HasValue ? "AND version = @version" : "")}
                  {(cursorBlock.HasValue ? @"AND (
                    ""blockNumber"" < @cursorBlock OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
                  )" : "")}
                ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
                LIMIT @limit
            ";
        }
        else
        {
            // Use full Transfers view which includes all transfers (including intermediary hops)
            sql = @$"
                SELECT 
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    ""batchIndex"",
                    ""transactionHash"",
                    version,
                    operator,
                    ""from"",
                    ""to"",
                    id,
                    value
                FROM ""V_Crc_Transfers""
                WHERE (""from"" = @address OR ""to"" = @address)
                  {(version.HasValue ? "AND version = @version" : "")}
                  {(cursorBlock.HasValue ? @"AND (
                    ""blockNumber"" < @cursorBlock OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex) OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" = @cursorLogIndex AND ""batchIndex"" < @cursorBatchIndex)
                  )" : "")}
                ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC, ""batchIndex"" DESC
                LIMIT @limit
            ";
        }

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", normalizedAddress);
        cmd.Parameters.AddWithValue("limit", limit + 1); // Fetch one extra to check for more

        if (version.HasValue)
        {
            cmd.Parameters.AddWithValue("version", version.Value);
        }

        if (cursorBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("cursorBlock", cursorBlock.Value);
            cmd.Parameters.AddWithValue("cursorTxIndex", cursorTxIndex!.Value);
            cmd.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
            cmd.Parameters.AddWithValue("cursorBatchIndex", cursorBatchIndex!.Value);
        }

        var results = new List<TransactionHistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionIndex = reader.GetInt32(2);
            var logIndex = reader.GetInt32(3);
            var batchIndex = reader.GetInt32(4);
            var transactionHash = reader.GetString(5);
            var ver = reader.GetInt32(6);
            var operatorAddr = reader.IsDBNull(7) ? null : reader.GetString(7);
            var from = reader.GetString(8);
            var to = reader.GetString(9);
            var id = reader.IsDBNull(10) ? null : reader.GetString(10);
            var valueRaw = reader.GetFieldValue<System.Numerics.BigInteger>(11);

            // Calculate all circle amount formats
            // For V2: value is demurraged attoCircles from the database
            // For V1: value is raw attoCrc (inflationary V1 CRC)

            BigInteger attoCirclesDemurraged;
            BigInteger staticAttoCircles;
            BigInteger attoCrc;
            
            if (ver == 1)
            {
                // V1: value is raw attoCrc
                attoCrc = valueRaw;
                attoCirclesDemurraged = CirclesConverter.AttoCrcToAttoCircles(attoCrc, (ulong)timestamp);
                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCirclesDemurraged);
            }
            else
            {
                // V2: value is demurraged attoCircles
                attoCirclesDemurraged = valueRaw;
                
                // Calculate day from timestamp for conversions
                var timestampUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                var day = CirclesConverter.DayFromTimestamp(timestampUtc, 1_602_720_000); // INFLATION_DAY_ZERO_UNIX

                // staticAttoCircles = convert demurraged to inflationary (static)
                staticAttoCircles = CirclesConverter.DemurrageToInflationary(attoCirclesDemurraged, day);
                
                // attoCrc = convert demurraged attoCircles to V1 CRC
                attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCirclesDemurraged, (ulong)timestamp);
            }

            // circles = convert demurraged attoCircles to decimal
            var circles = CirclesConverter.AttoCirclesToCircles(attoCirclesDemurraged);

            // staticCircles = convert staticAttoCircles to decimal
            var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

            // crc = convert attoCrc to decimal
            var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

            results.Add(new TransactionHistoryRow(
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionIndex: transactionIndex,
                LogIndex: logIndex,
                TransactionHash: transactionHash,
                Version: ver,
                From: from,
                To: to,
                Operator: operatorAddr,
                Id: id,
                Value: valueRaw.ToString(),
                Circles: circles.ToString(),
                AttoCircles: attoCirclesDemurraged.ToString(),
                Crc: crc.ToString(),
                AttoCrc: attoCrc.ToString(),
                StaticCircles: staticCircles.ToString(),
                StaticAttoCircles: staticAttoCircles.ToString()
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
            // Include batchIndex in cursor for proper pagination of batch transfers
            nextCursor = CursorUtils.EncodeCursorWithBatch(lastResult.BlockNumber, lastResult.TransactionIndex, lastResult.LogIndex, 0);
        }

        return new PagedResponse<TransactionHistoryRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    public async Task<PagedResponse<TokenHolderRow>> GetTokenHolders(string tokenAddress, int limit = 100, string? cursor = null)
    {
        var normalizedToken = tokenAddress.ToLower();
        await using var connection = await CreateConnectionAsync();

        // Build query with cursor pagination - UNION both V1 and V2 views
        var sql = @$"
            SELECT
                account,
                ""totalBalance"",
                ""tokenAddress"",
                version
            FROM (
                SELECT account, ""totalBalance"", ""tokenAddress"", 1 as version
                FROM ""V_CrcV1_BalancesByAccountAndToken""
                WHERE ""tokenAddress"" = @tokenAddress AND ""totalBalance"" > 0
                UNION ALL
                SELECT account, ""totalBalance"", ""tokenAddress"", 2 as version
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

    public async Task<CommonTrustResponse> GetCommonTrust(string address1, string address2, int? version = null)
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
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address1", address1.ToLower());
        command.Parameters.AddWithValue("address2", address2.ToLower());

        var commonTrusts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            commonTrusts.Add(reader.GetString(0));
        }
        return new CommonTrustResponse(Address1: address1.ToLower(), Address2: address2.ToLower(), CommonTrusts: commonTrusts.ToArray());
    }

    public async Task<JsonElement> GetNetworkSnapshot()
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            throw new InvalidOperationException("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/snapshot";

        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        // Parse to JsonDocument and clone the root element to detach from the document
        // This matches production behavior - return the raw pathfinder response
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> FindPathV2(FlowRequest flowRequest)
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            throw new InvalidOperationException("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/findPath";

        // Configure JSON serialization with camelCase property names to match Pathfinder DTOs
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        var jsonContent = JsonSerializer.Serialize(flowRequest, jsonOptions);

        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        using var response = await HttpClient.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Pathfinder service returned {response.StatusCode}: {errorContent}");
        }

        var responseString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(responseString);
    }


    public async Task<PagedEventsResponse> GetEvents(
        string? address,
        long? fromBlock,
        long? toBlock,
        string[]? eventTypes,
        IFilterPredicateDto[]? filterPredicates = null,
        bool? sortAscending = false,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 100;
        const int maxLimit = 1000;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        // Decode cursor if provided
        var (cursorBlockNumber, cursorTransactionIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);

        // Use the schema-aware map to get all event tables and their address columns
        var eventTables = DatabaseSchemaMap.TableAddressColumns;

        if (eventTables == null)
        {
            return new PagedEventsResponse(Array.Empty<object>(), false, null);
        }

        var queries = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        // Filter to only requested event types, or use all tables if no filter specified
        var relevantTables = eventTypes == null || eventTypes.Length == 0
            ? eventTables
            : eventTables.Where(kvp => eventTypes.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Add basic filter parameters
        if (address != null) parameters.Add(new NpgsqlParameter("address", address.ToLower()));
        if (fromBlock.HasValue) parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        if (toBlock.HasValue) parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));

        // Add cursor parameters if cursor is provided
        if (cursorBlockNumber.HasValue)
        {
            parameters.Add(new NpgsqlParameter("cursorBlockNumber", cursorBlockNumber.Value));
            parameters.Add(new NpgsqlParameter("cursorTransactionIndex", cursorTransactionIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
        }

        // Determine sort order once
        var sortOrder = sortAscending == true ? "ASC" : "DESC";
        var cursorComparison = sortAscending == true ? ">" : "<";

        foreach (var table in relevantTables)
        {
            // Extract namespace from table name (format: "Namespace_TableName")
            var parts = table.Key.Split('_', 2);
            if (parts.Length < 2)
            {
                continue; // Skip malformed table names
            }

            var tableNamespace = parts[0];

            // Skip System namespace and View tables (starting with V_) to match remote behavior
            // System tables are internal (Block, EventTableHead, PathfinderRequestLog, etc.)
            // View tables are virtual tables and should not be queried as events
            if (tableNamespace == "System" || tableNamespace.StartsWith('V'))
            {
                continue;
            }

            // Skip tables that don't have the required event columns
            var tableColumns = DatabaseSchemaMap.GetTableColumns(table.Key);
            if (tableColumns == null ||
                !tableColumns.ContainsKey("blockNumber") ||
                !tableColumns.ContainsKey("transactionIndex") ||
                !tableColumns.ContainsKey("logIndex") ||
                !tableColumns.ContainsKey("transactionHash"))
            {
                continue;
            }

            var whereClauses = new List<string>();

            // Basic address filter - only add if address is specified and table has address columns
            if (address != null && table.Value.Any())
            {
                whereClauses.Add($"({string.Join(" OR ", table.Value.Select(col => $"t.\"{col}\" = @address"))})");
            }

            // Block range filters
            if (fromBlock.HasValue) whereClauses.Add("t.\"blockNumber\" >= @fromBlock");
            if (toBlock.HasValue) whereClauses.Add("t.\"blockNumber\" <= @toBlock");

            // Cursor-based pagination filter
            if (cursorBlockNumber.HasValue)
            {
                whereClauses.Add($"(t.\"blockNumber\", t.\"transactionIndex\", t.\"logIndex\") {cursorComparison} (@cursorBlockNumber, @cursorTransactionIndex, @cursorLogIndex)");
            }

            // Advanced filter predicates
            if (filterPredicates != null && filterPredicates.Length > 0)
            {
                foreach (var predicate in filterPredicates)
                {
                    var predicateClause = BuildPredicateClause(predicate, parameters, table.Key);
                    if (!string.IsNullOrEmpty(predicateClause))
                    {
                        whereClauses.Add(predicateClause);
                    }
                }
            }

            var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : "";

            var query = $@"(SELECT t.""blockNumber"", t.""transactionIndex"", t.""transactionHash"", t.""logIndex"", '{table.Key}' as event_name, to_jsonb(t) as event_payload FROM ""{table.Key}"" t{whereSql} ORDER BY t.""blockNumber"" {sortOrder}, t.""transactionIndex"" {sortOrder}, t.""logIndex"" {sortOrder})";
            queries.Add(query);
        }

        if (queries.Count == 0)
        {
            return new PagedEventsResponse(Array.Empty<object>(), false, null);
        }

        // Combine results from all tables and apply final ORDER BY with LIMIT
        // Fetch one extra row to determine if there are more results
        var finalSql = string.Join(" UNION ALL ", queries);
        finalSql = $"SELECT * FROM ({finalSql}) combined ORDER BY \"blockNumber\" {sortOrder}, \"transactionIndex\" {sortOrder}, \"logIndex\" {sortOrder} LIMIT {effectiveLimit + 1}";

        // Execute the combined query
        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.CommandTimeout = 30;

        // Add all parameters
        if (address != null) command.Parameters.AddWithValue("address", address.ToLower());
        if (fromBlock.HasValue) command.Parameters.AddWithValue("fromBlock", fromBlock.Value);
        if (toBlock.HasValue) command.Parameters.AddWithValue("toBlock", toBlock.Value);

        // Add cursor parameters
        if (cursorBlockNumber.HasValue)
        {
            command.Parameters.AddWithValue("cursorBlockNumber", cursorBlockNumber.Value);
            command.Parameters.AddWithValue("cursorTransactionIndex", cursorTransactionIndex!.Value);
            command.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
        }

        // Add filter predicate parameters
        foreach (var param in parameters)
        {
            // Skip parameters we've already added
            if (param.ParameterName == "address" || param.ParameterName == "fromBlock" ||
                param.ParameterName == "toBlock" || param.ParameterName == "cursorBlockNumber" ||
                param.ParameterName == "cursorTransactionIndex" || param.ParameterName == "cursorLogIndex")
            {
                continue;
            }
            command.Parameters.Add(param);
        }

        var events = new List<object>();
        long lastBlockNumber = 0;
        int lastTransactionIndex = 0;
        int lastLogIndex = 0;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Track cursor values from each row
            lastBlockNumber = reader.GetInt64(0);
            lastTransactionIndex = reader.GetInt32(1);
            lastLogIndex = reader.GetInt32(3);
            var eventName = reader.GetString(4);

            // Parse the event payload
            var payloadJson = reader.GetString(5);
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

            if (payloadDict != null)
            {
                // Convert numeric fields to hex format and create ordered dictionary
                var orderedValues = new Dictionary<string, object?>();

                // Add standard fields in remote server order
                var standardFieldsOrder = new[] { "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash" };

                foreach (var fieldName in standardFieldsOrder)
                {
                    if (payloadDict.TryGetValue(fieldName, out var value))
                    {
                        if (fieldName == "blockNumber" || fieldName == "timestamp" || fieldName == "transactionIndex" || fieldName == "logIndex")
                        {
                            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numValue))
                            {
                                orderedValues[fieldName] = "0x" + numValue.ToString("x");
                            }
                            else
                            {
                                orderedValues[fieldName] = value.ToString();
                            }
                        }
                        else if (value.ValueKind == JsonValueKind.String)
                        {
                            orderedValues[fieldName] = value.GetString();
                        }
                        else
                        {
                            orderedValues[fieldName] = JsonSerializer.Deserialize<object>(value.GetRawText());
                        }
                    }
                }

                // Add remaining fields in alphabetical order but with "limit" last to match remote
                var remainingFields = payloadDict
                    .Where(kvp => !orderedValues.ContainsKey(kvp.Key))
                    .OrderBy(x => x.Key == "limit" ? "zzz" : x.Key);

                foreach (var field in remainingFields)
                {
                    var key = field.Key;
                    var value = field.Value;

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        orderedValues[key] = value.GetString();
                    }
                    else if (value.ValueKind == JsonValueKind.Number)
                    {
                        orderedValues[key] = value.ToString();
                    }
                    else
                    {
                        orderedValues[key] = JsonSerializer.Deserialize<object>(value.GetRawText());
                    }
                }

                events.Add(new
                {
                    @event = eventName,
                    values = orderedValues
                });
            }
        }

        // Determine if there are more results
        var hasMore = events.Count > effectiveLimit;
        if (hasMore)
        {
            // Remove the extra row we fetched
            events.RemoveAt(events.Count - 1);
            // Get cursor from the last row we're actually returning
            var secondLastEvent = events.Count > 0 ? events[^1] : null;
            if (secondLastEvent != null)
            {
                var eventDict = (dynamic)secondLastEvent;
                var values = (Dictionary<string, object?>)eventDict.values;
                if (values.TryGetValue("blockNumber", out var bn) &&
                    values.TryGetValue("transactionIndex", out var ti) &&
                    values.TryGetValue("logIndex", out var li))
                {
                    // Parse hex values back to numbers for the cursor
                    lastBlockNumber = Convert.ToInt64(bn?.ToString()?.Replace("0x", ""), 16);
                    lastTransactionIndex = Convert.ToInt32(ti?.ToString()?.Replace("0x", ""), 16);
                    lastLogIndex = Convert.ToInt32(li?.ToString()?.Replace("0x", ""), 16);
                }
            }
        }

        var nextCursor = hasMore ? CursorUtils.EncodeCursor(lastBlockNumber, lastTransactionIndex, lastLogIndex) : null;

        return new PagedEventsResponse(events.ToArray(), hasMore, nextCursor);
    }

    /// <summary>
    /// Builds a WHERE clause from an IFilterPredicateDto.
    /// </summary>
    private string BuildPredicateClause(IFilterPredicateDto predicate, List<NpgsqlParameter> parameters, string tablePrefix)
    {
        return predicate switch
        {
            FilterPredicateDto fp => BuildFilterPredicateClause(fp, parameters, tablePrefix),
            ConjunctionDto conj => BuildConjunctionClause(conj, parameters, tablePrefix),
            _ => ""
        };
    }

    private string BuildFilterPredicateClause(FilterPredicateDto predicate, List<NpgsqlParameter> parameters, string tablePrefix)
    {
        if (predicate.Column == null)
        {
            throw new ArgumentNullException(nameof(predicate.Column), "Filter column cannot be null.");
        }
        var validatedColumn = ValidateIdentifier(predicate.Column, "Filter column");
        var column = $"t.\"{validatedColumn}\"";
        var paramName = $"@pred_{tablePrefix}_{parameters.Count}";

        switch (predicate.FilterType)
        {
            case FilterType.Equals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} = {paramName}";

            case FilterType.NotEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} != {paramName}";

            case FilterType.GreaterThan:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} > {paramName}";

            case FilterType.GreaterThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} >= {paramName}";

            case FilterType.LessThan:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} < {paramName}";

            case FilterType.LessThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} <= {paramName}";

            case FilterType.Like:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} LIKE {paramName}";

            case FilterType.ILike:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} ILIKE {paramName}";

            case FilterType.NotLike:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} NOT LIKE {paramName}";

            case FilterType.In:
                if (predicate.Value is Array arr)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arr));
                    return $"{column} = ANY({paramName})";
                }
                return "";

            case FilterType.NotIn:
                if (predicate.Value is Array arr2)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arr2));
                    return $"{column} != ALL({paramName})";
                }
                return "";

            case FilterType.IsNull:
                return $"{column} IS NULL";

            case FilterType.IsNotNull:
                return $"{column} IS NOT NULL";

            default:
                return "";
        }
    }

    private string BuildConjunctionClause(ConjunctionDto conjunction, List<NpgsqlParameter> parameters, string tablePrefix)
    {
        if (conjunction.Predicates == null || conjunction.Predicates.Length == 0)
            return "";

        var clauses = new List<string>();
        foreach (var pred in conjunction.Predicates)
        {
            var clause = BuildPredicateClause(pred, parameters, tablePrefix);
            if (!string.IsNullOrEmpty(clause))
            {
                clauses.Add(clause);
            }
        }

        if (clauses.Count == 0)
            return "";

        var joinOperator = conjunction.ConjunctionType == ConjunctionType.And ? " AND " : " OR ";
        return $"({string.Join(joinOperator, clauses)})";
    }

    /// <summary>
    /// Builds a WHERE clause for the Query method.
    /// </summary>
    private string BuildQueryPredicateClause(IFilterPredicateDto predicate, List<NpgsqlParameter> parameters)
    {
        return predicate switch
        {
            FilterPredicateDto fp => BuildQueryFilterPredicateClause(fp, parameters),
            ConjunctionDto conj => BuildQueryConjunctionClause(conj, parameters),
            _ => ""
        };
    }

    private string BuildQueryFilterPredicateClause(FilterPredicateDto predicate, List<NpgsqlParameter> parameters)
    {
        if (predicate.Column == null)
        {
            throw new ArgumentNullException(nameof(predicate.Column), "Filter column cannot be null.");
        }
        var validatedColumn = ValidateIdentifier(predicate.Column, "Filter column");
        var column = $"\"{validatedColumn}\"::text";
        var paramName = $"@p{parameters.Count}";

        switch (predicate.FilterType)
        {
            case FilterType.Equals:
                // Handle array values as IN clause (for backwards compatibility)
                // Arrays come in as JsonElement due to ObjectToInferredTypeConverter
                if (predicate.Value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
                {
                    var arrayValues = jsonElement.EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .ToArray();
                    parameters.Add(new NpgsqlParameter(paramName, arrayValues));
                    return $"{column} IN (SELECT unnest({paramName}::text[]))";
                }
                if (predicate.Value is Array arrEquals)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arrEquals));
                    return $"{column} IN (SELECT unnest({paramName}::text[]))";
                }
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} = {paramName}::text";

            case FilterType.NotEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} != {paramName}::text";

            case FilterType.GreaterThan:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} > {paramName}::text";

            case FilterType.GreaterThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} >= {paramName}::text";

            case FilterType.LessThan:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} < {paramName}::text";

            case FilterType.LessThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} <= {paramName}::text";

            case FilterType.Like:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} LIKE {paramName}::text";

            case FilterType.ILike:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} ILIKE {paramName}::text";

            case FilterType.NotLike:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} NOT LIKE {paramName}::text";

            case FilterType.In:
                if (predicate.Value is Array arr)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arr));
                    return $"{column} IN (SELECT unnest({paramName}::text[]))";
                }
                return "";

            case FilterType.NotIn:
                if (predicate.Value is Array arr2)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arr2));
                    return $"{column} NOT IN (SELECT unnest({paramName}::text[]))";
                }
                return "";

            case FilterType.IsNull:
                return $"{column} IS NULL";

            case FilterType.IsNotNull:
                return $"{column} IS NOT NULL";

            default:
                return "";
        }
    }

    private string BuildQueryConjunctionClause(ConjunctionDto conjunction, List<NpgsqlParameter> parameters)
    {
        if (conjunction.Predicates == null || conjunction.Predicates.Length == 0)
            return "";

        var clauses = new List<string>();
        foreach (var pred in conjunction.Predicates)
        {
            var clause = BuildQueryPredicateClause(pred, parameters);
            if (!string.IsNullOrEmpty(clause))
            {
                clauses.Add(clause);
            }
        }

        if (clauses.Count == 0)
            return "";

        var joinOperator = conjunction.ConjunctionType == ConjunctionType.And ? " AND " : " OR ";
        return $"({string.Join(joinOperator, clauses)})";
    }

    public async Task<HealthResponse> GetHealth()
    {
        string databaseStatus;
        string indexStatus;
        string overallStatus;

        try
        {
            await using var connection = await CreateConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
            databaseStatus = "connected";
        }
        catch (Exception)
        {
            databaseStatus = "disconnected";
            indexStatus = "unknown";
            overallStatus = "unhealthy";
            return new HealthResponse(
                Status: overallStatus,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Database: databaseStatus,
                Index: indexStatus
            );
        }

        // Check indexer synchronization
        try
        {
            // Get latest block from database
            long? lastPersisted;
            await using var connection = await CreateConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT MAX(\"blockNumber\") as block_number FROM \"System_Block\"", connection);
            var result = await command.ExecuteScalarAsync();
            lastPersisted = result is long longResult ? longResult : 0;

            // Get latest block from Nethermind
            long blockHead = 0;
            if (_nethermindRpcClient != null)
            {
                blockHead = await _nethermindRpcClient.GetLatestBlockNumber();
            }

            if (blockHead - lastPersisted >= 3)
            {
                indexStatus = "lagging";
                overallStatus = "unhealthy";
            }
            else
            {
                indexStatus = "synchronized";
                overallStatus = "healthy";
            }
        }
        catch (Exception)
        {
            indexStatus = "unknown";
            overallStatus = "unhealthy";
        }

        return new HealthResponse(
            Status: overallStatus,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Database: databaseStatus,
            Index: indexStatus
        );
    }

    public Task<TableNamespace[]> GetTables()
    {
        var namespaces = new List<TableNamespace>();

        foreach (var schema in SchemaProvider.AllSchemas)
        {
            var schemaNamespaces = schema.Tables.GroupBy(o => o.Key.Namespace);

            foreach (var @namespace in schemaNamespaces)
            {
                if (@namespace.Key == "System")
                {
                    continue;
                }

                var tableDefinitions = new List<TableDefinition>();

                foreach (var table in @namespace)
                {
                    var topic = "0x" + Convert.ToHexStringLower(table.Value.Topic);

                    var columns = new List<TableColumn>();
                    foreach (var column in table.Value.Columns)
                    {
                        var columnDto = new TableColumn(column.Column, column.Type.ToString());
                        columns.Add(columnDto);
                    }

                    var tableDto = new TableDefinition(table.Key.Table, topic, [.. columns]);
                    tableDefinitions.Add(tableDto);
                }

                var namespaceDto = new TableNamespace(@namespace.Key, [.. tableDefinitions]);
                namespaces.Add(namespaceDto);
            }
        }

        return Task.FromResult(namespaces.ToArray());
    }


    /// <summary>
    /// Removes JSON-LD fields (@type, @context) from a profile JsonElement.
    /// The remote implementation doesn't include these semantic web fields in responses.
    /// </summary>
    private static JsonElement? StripJsonLdFields(JsonElement? profile)
    {
        if (profile == null || profile.Value.ValueKind != JsonValueKind.Object)
        {
            return profile;
        }

        var dict = new Dictionary<string, JsonElement>();

        foreach (var prop in profile.Value.EnumerateObject())
        {
            // Skip JSON-LD semantic fields only
            if (prop.Name == "@type" || prop.Name == "@context")
            {
                continue;
            }

            dict[prop.Name] = prop.Value;
        }

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
    }

    /// <summary>
    /// Validates that an identifier contains only safe characters (letters, digits, underscore).
    /// </summary>
    private static string ValidateIdentifier(string identifier, string identifierType)
    {
        if (identifier == null)
        {
            throw new ArgumentNullException(nameof(identifier), $"{identifierType} cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException($"{identifierType} cannot be empty or whitespace.");
        }

        if (!Regex.IsMatch(identifier, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
        {
            throw new ArgumentException($"{identifierType} contains invalid characters. Only letters, digits, and underscores are allowed, and it must start with a letter or underscore.");
        }

        return identifier;
    }

    #region SDK Enablement Endpoints

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
    /// Gets aggregated trust network summary including trust counts, common trusts, and network depth.
    /// Server-side aggregation reduces client-side processing.
    /// </summary>
    public async Task<TrustNetworkSummaryResponse> GetTrustNetworkSummary(string address, int? maxDepth = 2)
    {
        var trustRelations = await GetTrustRelations(address);

        // Calculate network size at different depths
        var depth1Trusts = new HashSet<string>(trustRelations.Trusts?.Select(t => t.User) ?? Array.Empty<string>());
        var depth1TrustedBy = new HashSet<string>(trustRelations.TrustedBy?.Select(t => t.User) ?? Array.Empty<string>());

        // Mutual trusts (intersection)
        var mutualTrusts = depth1Trusts.Intersect(depth1TrustedBy).ToArray();

        return new TrustNetworkSummaryResponse
        {
            Address = address,
            DirectTrustsCount = depth1Trusts.Count,
            DirectTrustedByCount = depth1TrustedBy.Count,
            MutualTrustsCount = mutualTrusts.Length,
            MutualTrusts = mutualTrusts,
            NetworkReach = depth1Trusts.Count + depth1TrustedBy.Count - mutualTrusts.Length // Union count
        };
    }

    /// <summary>
    /// Gets aggregated trust relations showing mutual, one-way trusts, and trusted-by in a single call.
    /// Categorizes relationships for easier UI rendering. Enriched with avatar info.
    /// </summary>
    public async Task<PagedAggregatedTrustRelationsResponse> GetAggregatedTrustRelationsEnriched(
        string address,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 50;
        const int maxLimit = 200;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        var trustRelations = await GetTrustRelations(address);

        var trustsSet = new HashSet<string>(trustRelations.Trusts?.Select(t => t.User) ?? Array.Empty<string>());
        var trustedBySet = new HashSet<string>(trustRelations.TrustedBy?.Select(t => t.User) ?? Array.Empty<string>());

        var mutualAddresses = trustsSet.Intersect(trustedBySet).OrderBy(a => a).ToList();
        var oneWayTrustsAddresses = trustsSet.Except(trustedBySet).OrderBy(a => a).ToList();
        var oneWayTrustedByAddresses = trustedBySet.Except(trustsSet).OrderBy(a => a).ToList();

        // Build combined sorted list with relation types for stable cursor-based pagination
        var allRelations = new List<(string Address, string RelationType)>();
        allRelations.AddRange(mutualAddresses.Select(a => (a, "mutual")));
        allRelations.AddRange(oneWayTrustsAddresses.Select(a => (a, "trusts")));
        allRelations.AddRange(oneWayTrustedByAddresses.Select(a => (a, "trustedBy")));

        // Sort by address for consistent ordering
        allRelations = allRelations.OrderBy(r => r.Address).ToList();

        // Decode cursor (we use address as cursor for simplicity since addresses are unique)
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

        // Filter by cursor
        if (cursorAddress != null)
        {
            allRelations = allRelations.Where(r => string.Compare(r.Address, cursorAddress, StringComparison.Ordinal) > 0).ToList();
        }

        // Take limit + 1 to check if there are more
        var pageRelations = allRelations.Take(effectiveLimit + 1).ToList();
        var hasMore = pageRelations.Count > effectiveLimit;
        if (hasMore)
        {
            pageRelations.RemoveAt(pageRelations.Count - 1);
        }

        // Get avatar info for addresses in this page
        var pageAddresses = pageRelations.Select(r => r.Address).ToArray();
        var avatars = pageAddresses.Length > 0 ? await GetAvatarInfoBatchInternal(pageAddresses) : Array.Empty<AvatarInfo?>();
        var avatarDict = avatars.Where(a => a != null).ToDictionary(a => a!.Avatar, a => a);

        // Build results
        var results = pageRelations.Select(r => new TrustRelationInfo
        {
            Address = r.Address,
            AvatarInfo = avatarDict.TryGetValue(r.Address, out var avatar) ? avatar : null,
            RelationType = r.RelationType
        }).ToArray();

        // Generate next cursor from last address
        string? nextCursor = null;
        if (hasMore && results.Length > 0)
        {
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(results[^1].Address));
        }

        return new PagedAggregatedTrustRelationsResponse
        {
            Address = address,
            Results = results,
            Counts = new TrustRelationCounts
            {
                Mutual = mutualAddresses.Count,
                Trusts = oneWayTrustsAddresses.Count,
                TrustedBy = oneWayTrustedByAddresses.Count,
                Total = mutualAddresses.Count + oneWayTrustsAddresses.Count + oneWayTrustedByAddresses.Count
            },
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

    /// <summary>
    /// Gets list of valid inviters for an address (addresses that trust them and have sufficient balance).
    /// Useful for invitation flows and invitation escrow scenarios.
    /// </summary>
    public async Task<PagedValidInvitersResponse> GetValidInviters(
        string address,
        string? minimumBalance = null,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 50;
        const int maxLimit = 200;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        var trustRelations = await GetTrustRelations(address);
        var trustedByAddresses = trustRelations.TrustedBy?.Select(t => t.User).OrderBy(a => a).ToList() ?? new List<string>();

        if (trustedByAddresses.Count == 0)
        {
            return new PagedValidInvitersResponse
            {
                Address = address,
                Results = Array.Empty<InviterInfo>(),
                HasMore = false,
                NextCursor = null
            };
        }

        // Decode cursor (using address as cursor)
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

        // Filter by cursor
        if (cursorAddress != null)
        {
            trustedByAddresses = trustedByAddresses.Where(a => string.Compare(a, cursorAddress, StringComparison.Ordinal) > 0).ToList();
        }

        // Process addresses and collect valid inviters until we have enough
        var validInviters = new List<InviterInfo>();
        var processedCount = 0;

        foreach (var inviterAddress in trustedByAddresses)
        {
            if (validInviters.Count > effectiveLimit)
            {
                break; // We have enough (including the extra one for hasMore check)
            }

            try
            {
                // Get avatar info to determine version
                var avatarInfo = await GetAvatarInfoBatchInternal(new[] { inviterAddress });
                var avatar = avatarInfo.FirstOrDefault();

                if (avatar == null)
                {
                    processedCount++;
                    continue;
                }

                // Get balance (try both v1 and v2)
                TotalBalanceResponse? balance = null;

                if (avatar.Version == 2)
                {
                    try
                    {
                        balance = await GetTotalBalance(inviterAddress, 2, true);
                    }
                    catch { }
                }
                else if (avatar.HasV1 == true)
                {
                    try
                    {
                        balance = await GetTotalBalance(inviterAddress, 1, true);
                    }
                    catch { }
                }

                if (balance != null)
                {
                    // Check minimum balance if specified
                    if (string.IsNullOrEmpty(minimumBalance) ||
                        decimal.TryParse(balance.Balance, out var balanceValue) &&
                        decimal.TryParse(minimumBalance, out var minValue) &&
                        balanceValue >= minValue)
                    {
                        validInviters.Add(new InviterInfo
                        {
                            Address = inviterAddress,
                            Balance = balance.Balance,
                            AvatarInfo = avatar
                        });
                    }
                }
            }
            catch
            {
                // Skip inviters with errors
            }

            processedCount++;
        }

        // Determine if there are more results
        var hasMore = validInviters.Count > effectiveLimit;
        if (hasMore)
        {
            validInviters.RemoveAt(validInviters.Count - 1);
        }

        // Generate next cursor from last address
        string? nextCursor = null;
        if (hasMore && validInviters.Count > 0)
        {
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(validInviters[^1].Address));
        }

        return new PagedValidInvitersResponse
        {
            Address = address,
            Results = validInviters.ToArray(),
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

    /// <summary>
    /// Gets transaction history with enriched data including demurrage calculations and profile info.
    /// Reduces need for separate profile lookups and demurrage computations on client side.
    /// </summary>
    public async Task<PagedResponse<EnrichedTransaction>> GetTransactionHistoryEnriched(
        string address,
        long fromBlock,
        long? toBlock = null,
        int? limit = null,
        string? cursor = null)
    {
        var normalizedAddress = address.ToLower();
        await using var connection = await CreateConnectionAsync();

        // Decode cursor if provided
        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);

        // Use limit or default to 20 if not specified
        var effectiveLimit = limit ?? 20;

        // Build query to get events with cursor pagination
        var sql = @$"
            SELECT 
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                event_name,
                event_payload
            FROM (
                SELECT 
                    e.""blockNumber"",
                    e.""transactionIndex"",
                    e.""logIndex"",
                    e.""transactionHash"",
                    'transfer' as event_name,
                    to_jsonb(e) as event_payload
                FROM ""V_Crc_Transfers"" e
                WHERE e.version = 2
                  AND (e.""from"" = @address OR e.""to"" = @address)
                  AND e.""blockNumber"" >= @fromBlock
                  {(toBlock.HasValue ? "AND e.\"blockNumber\" <= @toBlock" : "")}
                  {(cursorBlock.HasValue ? @"AND (
                    e.""blockNumber"" < @cursorBlock OR
                    (e.""blockNumber"" = @cursorBlock AND e.""transactionIndex"" < @cursorTxIndex) OR
                    (e.""blockNumber"" = @cursorBlock AND e.""transactionIndex"" = @cursorTxIndex AND e.""logIndex"" < @cursorLogIndex)
                  )" : "")}
                
                UNION ALL
                
                SELECT 
                    t.""blockNumber"",
                    t.""transactionIndex"",
                    t.""logIndex"",
                    t.""transactionHash"",
                    'trust' as event_name,
                    to_jsonb(t) as event_payload
                FROM ""CrcV2_Trust"" t
                WHERE (t.truster = @address OR t.trustee = @address)
                  AND t.""blockNumber"" >= @fromBlock
                  {(toBlock.HasValue ? "AND t.\"blockNumber\" <= @toBlock" : "")}
                  {(cursorBlock.HasValue ? @"AND (
                    t.""blockNumber"" < @cursorBlock OR
                    (t.""blockNumber"" = @cursorBlock AND t.""transactionIndex"" < @cursorTxIndex) OR
                    (t.""blockNumber"" = @cursorBlock AND t.""transactionIndex"" = @cursorTxIndex AND t.""logIndex"" < @cursorLogIndex)
                  )" : "")}
            ) combined
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", normalizedAddress);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("limit", effectiveLimit + 1); // Fetch one extra to check for more

        if (toBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("toBlock", toBlock.Value);
        }

        if (cursorBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("cursorBlock", cursorBlock.Value);
            cmd.Parameters.AddWithValue("cursorTxIndex", cursorTxIndex!.Value);
            cmd.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
        }

        var events = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var eventPayloadJson = reader.GetString(5);
            var eventPayload = JsonSerializer.Deserialize<JsonElement>(eventPayloadJson);
            events.Add(eventPayload);
        }

        // Check if there are more results
        var hasMore = events.Count > effectiveLimit;
        if (hasMore)
        {
            events.RemoveAt(events.Count - 1); // Remove the extra row
        }

        // Extract all involved addresses from events
        var involvedAddresses = new HashSet<string>();
        foreach (var evt in events)
        {
            // Extract addresses from different event types
            if (evt.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(from.GetString()!);
            if (evt.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(to.GetString()!);
            if (evt.TryGetProperty("truster", out var truster) && truster.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(truster.GetString()!);
            if (evt.TryGetProperty("trustee", out var trustee) && trustee.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(trustee.GetString()!);
        }

        // Batch fetch avatar info and profiles for all involved addresses (in parallel)
        var addressArray = involvedAddresses.ToArray();
        AvatarInfo?[] avatars;
        JsonElement?[] profiles;

        if (addressArray.Length > 0)
        {
            // Run both fetches in parallel - they are independent operations
            var avatarTask = GetAvatarInfoBatchInternal(addressArray);
            var profileTask = GetProfileByAddressBatch(addressArray);
            await Task.WhenAll(avatarTask, profileTask);
            avatars = await avatarTask;
            profiles = await profileTask;
        }
        else
        {
            avatars = Array.Empty<AvatarInfo?>();
            profiles = Array.Empty<JsonElement?>();
        }

        var avatarDict = avatars.Where(a => a != null).ToDictionary(a => a!.Avatar, a => a);
        var profileDict = involvedAddresses.Zip(profiles, (addr, prof) => new { addr, prof })
            .Where(x => x.prof != null)
            .ToDictionary(x => x.addr, x => x.prof);

        // Enrich each event
        var enrichedTransactions = new List<EnrichedTransaction>();
        foreach (var evt in events)
        {
            // Extract top-level fields from the event
            var blockNumber = evt.TryGetProperty("blockNumber", out var bn) ? bn.GetInt64() : 0;
            var transactionHash = evt.TryGetProperty("transactionHash", out var th) ? th.GetString() ?? "" : "";
            var transactionIndex = evt.TryGetProperty("transactionIndex", out var ti) ? ti.GetInt32() : 0;
            var logIndex = evt.TryGetProperty("logIndex", out var li) ? li.GetInt32() : 0;

            var enriched = new EnrichedTransaction
            {
                BlockNumber = blockNumber,
                TransactionHash = transactionHash,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                Event = evt,
                Participants = new Dictionary<string, ParticipantInfo>()
            };

            // Extract addresses specific to this event
            var eventAddresses = new HashSet<string>();
            if (evt.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                eventAddresses.Add(from.GetString()!);
            if (evt.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
                eventAddresses.Add(to.GetString()!);
            if (evt.TryGetProperty("truster", out var truster) && truster.ValueKind == JsonValueKind.String)
                eventAddresses.Add(truster.GetString()!);
            if (evt.TryGetProperty("trustee", out var trustee) && trustee.ValueKind == JsonValueKind.String)
                eventAddresses.Add(trustee.GetString()!);

            // Add participant info only for addresses in this specific event
            foreach (var addr in eventAddresses)
            {
                var participantInfo = new ParticipantInfo
                {
                    AvatarInfo = avatarDict.TryGetValue(addr, out var avatar) ? avatar : null,
                    Profile = profileDict.TryGetValue(addr, out var profile) ? profile : null
                };
                enriched.Participants[addr] = participantInfo;
            }

            enrichedTransactions.Add(enriched);
        }

        // Generate next cursor
        string? nextCursor = null;
        if (hasMore && enrichedTransactions.Count > 0)
        {
            // Extract cursor from the last event
            var lastEvent = enrichedTransactions[^1].Event;
            if (lastEvent.TryGetProperty("blockNumber", out var blockNum) &&
                lastEvent.TryGetProperty("transactionIndex", out var txIdx) &&
                lastEvent.TryGetProperty("logIndex", out var logIdx))
            {
                nextCursor = CursorUtils.EncodeCursor(
                    blockNum.GetInt64(),
                    txIdx.GetInt32(),
                    logIdx.GetInt32());
            }
        }

        return new PagedResponse<EnrichedTransaction>(
            Results: enrichedTransactions.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
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
        SELECT  p.avatar, p.avatar_name, p.short_name::text as short_name, p.avatar_type, p.payload, p.cid,
                COALESCE(r.receive_count, 0) as receive_count, p.rank
        FROM   (SELECT * FROM w_profile
                UNION ALL
                SELECT * FROM wo_profile) p
        LEFT JOIN recv r USING (avatar)
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

    #endregion

    public async Task<PagedQueryResponse> Query(SelectDto query, string? cursor = null)
    {
        if (string.IsNullOrEmpty(query.Table) || string.IsNullOrEmpty(query.Namespace))
        {
            throw new ArgumentException("Namespace and Table must be provided.");
        }

        // Validate and safely construct table name
        var validatedNamespace = ValidateIdentifier(query.Namespace, "Namespace");
        var validatedTable = ValidateIdentifier(query.Table, "Table");
        var fullTableName = $"{validatedNamespace}_{validatedTable}";
        var tableName = $"\"{fullTableName}\"";

        // Check if the table has event columns for cursor-based pagination
        var tableColumns = DatabaseSchemaMap.GetTableColumns(fullTableName);
        var hasEventColumns = tableColumns != null &&
            tableColumns.ContainsKey("blockNumber") &&
            tableColumns.ContainsKey("transactionIndex") &&
            tableColumns.ContainsKey("logIndex");

        // Decode cursor if provided and table supports cursor-based pagination
        var (cursorBlockNumber, cursorTransactionIndex, cursorLogIndex) = hasEventColumns
            ? CursorUtils.DecodeCursor(cursor)
            : (null, null, null);

        // Validate and quote columns - always include event columns for cursor if table supports it
        var columns = "*";
        var requestedColumns = query.Columns?.ToList() ?? new List<string>();

        // Ensure event columns are included if we need them for pagination
        if (hasEventColumns && requestedColumns.Any() && !requestedColumns.Contains("*"))
        {
            var eventColumns = new[] { "blockNumber", "transactionIndex", "logIndex" };
            foreach (var eventCol in eventColumns)
            {
                if (!requestedColumns.Contains(eventCol))
                {
                    requestedColumns.Add(eventCol);
                }
            }
        }

        if (requestedColumns.Any())
        {
            var validatedColumns = requestedColumns.Select(c => ValidateIdentifier(c, "Column")).ToArray();
            var quotedColumns = validatedColumns.Select(c => $"\"{c}\"").ToArray();
            columns = string.Join(", ", quotedColumns);
        }

        var parameters = new List<NpgsqlParameter>();
        var whereClauses = new List<string>();
        if (query.Filter != null)
        {
            foreach (var filter in query.Filter)
            {
                var clause = BuildQueryPredicateClause(filter, parameters);
                if (!string.IsNullOrEmpty(clause))
                {
                    whereClauses.Add(clause);
                }
            }
        }

        // Determine sort order from query.Order for cursor comparison
        var sortAscending = true; // Default ASC
        if (query.Order != null && query.Order.Any())
        {
            var firstOrder = query.Order.First();
            sortAscending = firstOrder.SortOrder?.ToUpper() != "DESC";
        }
        var cursorComparison = sortAscending ? ">" : "<";

        // Add cursor-based pagination filter if table supports it and cursor is provided
        if (hasEventColumns && cursorBlockNumber.HasValue)
        {
            parameters.Add(new NpgsqlParameter("cursorBlockNumber", cursorBlockNumber.Value));
            parameters.Add(new NpgsqlParameter("cursorTransactionIndex", cursorTransactionIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
            whereClauses.Add($"(\"blockNumber\", \"transactionIndex\", \"logIndex\") {cursorComparison} (@cursorBlockNumber, @cursorTransactionIndex, @cursorLogIndex)");
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        // Validate and quote ORDER BY columns
        var orderBySql = "";
        if (query.Order != null && query.Order.Any())
        {
            var orderByClauses = query.Order.Select(o =>
            {
                if (o.Column == null)
                {
                    throw new ArgumentNullException("Order column", "Order column cannot be null.");
                }
                var validatedColumn = ValidateIdentifier(o.Column, "Order column");
                var quotedColumn = $"\"{validatedColumn}\"";
                var sortOrder = o.SortOrder?.ToUpper() == "DESC" ? "DESC" : "ASC";
                return $"{quotedColumn} {sortOrder}";
            });
            orderBySql = "ORDER BY " + string.Join(", ", orderByClauses);
        }
        else if (hasEventColumns)
        {
            // Default ordering by event columns if table supports them and no order specified
            orderBySql = "ORDER BY \"blockNumber\" ASC, \"transactionIndex\" ASC, \"logIndex\" ASC";
        }

        // Validate LIMIT parameters
        const int defaultLimit = 100;
        const int maxLimit = 10000; // Reasonable safety limit
        var effectiveLimit = query.Limit.HasValue
            ? Math.Min(Math.Max(query.Limit.Value, 1), maxLimit)
            : defaultLimit;

        // Fetch one extra row to determine if there are more results
        var limitSql = $"LIMIT {effectiveLimit + 1}";

        var finalSql = $"SELECT {columns} FROM {tableName} {whereSql} {orderBySql} {limitSql}";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<object?[]>();
        var columnNames = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        // Find column indices for cursor generation
        var blockNumberIdx = columnNames.IndexOf("blockNumber");
        var transactionIndexIdx = columnNames.IndexOf("transactionIndex");
        var logIndexIdx = columnNames.IndexOf("logIndex");

        long lastBlockNumber = 0;
        int lastTransactionIndex = 0;
        int lastLogIndex = 0;

        while (await reader.ReadAsync())
        {
            var row = new object?[columnNames.Count];
            for (int i = 0; i < columnNames.Count; i++)
            {
                var value = reader.GetValue(i);
                row[i] = value is DBNull ? null : value;
            }

            // Track cursor values if available
            if (hasEventColumns && blockNumberIdx >= 0)
            {
                if (row[blockNumberIdx] is long bn) lastBlockNumber = bn;
                else if (row[blockNumberIdx] != null) long.TryParse(row[blockNumberIdx]?.ToString(), out lastBlockNumber);

                if (row[transactionIndexIdx] is int ti) lastTransactionIndex = ti;
                else if (row[transactionIndexIdx] is long tiLong) lastTransactionIndex = (int)tiLong;
                else if (row[transactionIndexIdx] != null) int.TryParse(row[transactionIndexIdx]?.ToString(), out lastTransactionIndex);

                if (row[logIndexIdx] is int li) lastLogIndex = li;
                else if (row[logIndexIdx] is long liLong) lastLogIndex = (int)liLong;
                else if (row[logIndexIdx] != null) int.TryParse(row[logIndexIdx]?.ToString(), out lastLogIndex);
            }

            results.Add(row);
        }

        // Determine if there are more results
        var hasMore = results.Count > effectiveLimit;
        string? nextCursor = null;

        if (hasMore)
        {
            // Remove the extra row we fetched
            results.RemoveAt(results.Count - 1);

            // Get cursor from the last row we're actually returning
            if (hasEventColumns && results.Count > 0 && blockNumberIdx >= 0)
            {
                var lastRow = results[^1];
                if (lastRow[blockNumberIdx] is long bn) lastBlockNumber = bn;
                else if (lastRow[blockNumberIdx] != null) long.TryParse(lastRow[blockNumberIdx]?.ToString(), out lastBlockNumber);

                if (lastRow[transactionIndexIdx] is int ti) lastTransactionIndex = ti;
                else if (lastRow[transactionIndexIdx] is long tiLong) lastTransactionIndex = (int)tiLong;
                else if (lastRow[transactionIndexIdx] != null) int.TryParse(lastRow[transactionIndexIdx]?.ToString(), out lastTransactionIndex);

                if (lastRow[logIndexIdx] is int li) lastLogIndex = li;
                else if (lastRow[logIndexIdx] is long liLong) lastLogIndex = (int)liLong;
                else if (lastRow[logIndexIdx] != null) int.TryParse(lastRow[logIndexIdx]?.ToString(), out lastLogIndex);

                nextCursor = CursorUtils.EncodeCursor(lastBlockNumber, lastTransactionIndex, lastLogIndex);
            }
        }

        return new PagedQueryResponse(Columns: columnNames, Rows: results, HasMore: hasMore, NextCursor: nextCursor);
    }
}
