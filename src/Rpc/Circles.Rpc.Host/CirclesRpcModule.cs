using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Circles.Index;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Circles.Pathfinder.DTOs;
using Nethermind.Int256;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;


namespace Circles.Rpc.Host;

public class CirclesRpcModule : ICirclesRpcModule
{
    private readonly Settings _settings;
    private readonly string _readOnlyDbConnectionString;
    private readonly MemoryCache _profileByCidCache;
    private static readonly HttpClient HttpClient = new();
    private readonly NethermindRpcClient? _nethermindRpcClient;
    private readonly ILogger<CirclesRpcModule>? _logger;

    public CirclesRpcModule(Settings settings, IHttpClientFactory? httpClientFactory = null, ILogger<CirclesRpcModule>? logger = null)
    {
        _settings = settings;
        _readOnlyDbConnectionString = settings.IndexReadonlyDbConnectionString;
        _profileByCidCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        _logger = logger;

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

    public async Task<string> GetTotalBalance(string address, int version, bool? asTimeCircles = true)
    {
        var balances = await GetTokenBalancesForAccount(address);
        var relevantBalances = balances.Where(o => o.Version == version);

        if (asTimeCircles == null || asTimeCircles == true)
        {
            var totalBalance = relevantBalances
                .Select(o => o.Circles)
                .Sum();

            return totalBalance.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            var totalBalance = relevantBalances
                .Select(o => UInt256.Parse(o.StaticAttoCircles))
                .Aggregate((UInt256)0, (acc, val) => acc + val);

            return totalBalance.ToString(CultureInfo.InvariantCulture);
        }
    }

    public async Task<CirclesTokenBalance[]> GetTokenBalances(string address)
    {
        var tokens = GetTokenExposureIds(address);

        if (tokens.Count == 0)
        {
            return Array.Empty<CirclesTokenBalance>();
        }

        var hubAddress = _settings.CirclesV2HubAddress;
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        // Batch fetch balances for efficiency
        var tokenBalances = new List<CirclesTokenBalance>();
        
        // Pre-fetch movement timestamps for demurraged tokens
        var demurragedTokens = tokens.Values.Where(t => !t.IsInflationary && t.TokenType != "CrcV1_Signup").ToArray();
        var movementTimestamps = await GetBatchMovementTimestamps(address, demurragedTokens.Select(t => t.TokenAddress).ToArray());
        
        foreach (var token in tokens.Values)
        {
            var rawBalance = await FetchBalance(
                token.TokenAddress,
                address,
                token.IsErc20,
                hubAddress);

            BigInteger attoCircles;
            decimal circles;
            BigInteger attoCrc;
            decimal crc;
            BigInteger staticAttoCircles;
            decimal staticCircles;

            if (token.TokenType == "CrcV1_Signup")
            {
                // OG CRC
                attoCrc = rawBalance;
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
            }
            else
            {
                if (token.IsInflationary)
                {
                    staticAttoCircles = rawBalance;
                    staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                    attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                    circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                    attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                    crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                }
                else
                {
                    // Demurraged Circles - get movement timestamp from batch
                    if (!movementTimestamps.TryGetValue(token.TokenAddress, out var lastMovementTs) || lastMovementTs == null)
                    {
                        // Log warning and skip token - token was never moved
                        _logger?.LogWarning(
                            "Account {Address} has a token {TokenAddress} that was never moved, skipping",
                            address, token.TokenAddress);
                        continue;
                    }

                    const uint DAY_ZERO = 1_602_720_000; // 2020-10-31 00:00:00 UTC
                    
                    var storedDay = CirclesConverter.DayFromTimestamp(
                        DateTimeOffset.FromUnixTimeSeconds(lastMovementTs.Value),
                        DAY_ZERO);
                    
                    var todayDay = CirclesConverter.DayFromTimestamp(
                        DateTimeOffset.UtcNow,
                        DAY_ZERO);
                    
                    var (demurragedAttoCircles, _) = Demurrage.ApplyDemurrage(
                        storedBalance: rawBalance,
                        storedDay: storedDay,
                        targetDay: todayDay);

                    attoCircles = demurragedAttoCircles;
                    circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                    attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                    crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                    staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                    staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                }
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
            .OrderByDescending(o => o.Circles)  // Match reference: sort by Circles, not StaticCircles
            .ToArray();

        return orderedResult;
    }
    /// <summary>
    /// Batch fetches movement timestamps for multiple tokens to avoid connection pool exhaustion.
    /// </summary>
    private async Task<Dictionary<string, long?>> GetBatchMovementTimestamps(string address, string[] tokenAddresses)
    {
        var result = new Dictionary<string, long?>();
        
        if (tokenAddresses.Length == 0)
        {
            return result;
        }

        var lowerAddress = address.ToLower();
        var lowerTokenAddresses = tokenAddresses.Select(t => t.ToLower()).ToArray();

        const string sql = @"
            SELECT DISTINCT ON (""tokenAddress"") ""tokenAddress"", ""timestamp""
            FROM (
                SELECT ""tokenAddress"", ""timestamp""
                FROM ""CrcV2_TransferSingle""
                WHERE ""to"" = @address AND ""tokenAddress"" = ANY(@tokenAddresses)
                UNION ALL
                SELECT ""tokenAddress"", ""timestamp""
                FROM ""CrcV2_TransferBatch""
                WHERE ""to"" = @address AND ""tokenAddress"" = ANY(@tokenAddresses)
                UNION ALL
                SELECT ""tokenAddress"", ""timestamp""
                FROM ""CrcV1_Transfer""
                WHERE ""to"" = @address AND ""tokenAddress"" = ANY(@tokenAddresses)
                UNION ALL
                SELECT ""tokenAddress"", ""timestamp""
                FROM ""CrcV2_Erc20WrapperTransfer""
                WHERE ""to"" = @address AND ""tokenAddress"" = ANY(@tokenAddresses)
            ) combined
            ORDER BY ""tokenAddress"", ""timestamp"" DESC";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", lowerAddress);
        command.Parameters.AddWithValue("tokenAddresses", lowerTokenAddresses);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tokenAddress = reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                var timestamp = reader.GetInt64(1);
                result[tokenAddress] = timestamp;
            }
        }

        // Initialize missing tokens with null
        foreach (var tokenAddress in lowerTokenAddresses)
        {
            if (!result.ContainsKey(tokenAddress))
            {
                result[tokenAddress] = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts an Ethereum address to a BigInteger token ID for ERC-1155.
    /// </summary>
    private static BigInteger AddressToTokenIdBigInt(string address)
    {
        var hex = address.StartsWith("0x") ? address.Substring(2) : address;
        return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
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

    public TokenExposureInfo(string tokenAddress, string tokenOwner, string tokenType, int version,
        bool isErc20, bool isErc1155, bool isWrapped, bool isInflationary, bool isGroup)
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
    }
    }

    private Dictionary<string, TokenExposureInfo> GetTokenExposureIds(string address)
    {
        var lowerAddress = address.ToLower();

        const string sql = @"
            WITH tokens AS (
                -- V1 avatar tokens from V1 transfers
                SELECT t.""tokenAddress""
                     , 'CrcV1_Signup' as ""type""
                     , s.""user"" as ""tokenOwner""
                FROM  public.""CrcV1_Transfer"" t
                join ""CrcV1_Signup"" s on s.token = t.""tokenAddress""
                WHERE t.""to"" = @address

                UNION ALL

                -- V2 avatar tokens from TransferSingle (ERC1155)
                SELECT DISTINCT ts.""tokenAddress""
                     , case when rh.avatar is not null then 'CrcV2_RegisterHuman' else 'CrcV2_RegisterGroup' end as ""type""
                     , ts.""tokenAddress"" as ""tokenOwner""
                FROM  public.""CrcV2_TransferSingle"" ts
                left join ""CrcV2_RegisterHuman"" rh on rh.avatar = ts.""tokenAddress""
                WHERE ts.""to"" = @address

                UNION ALL

                -- V2 avatar tokens from TransferBatch (ERC1155)
                SELECT DISTINCT tb.""tokenAddress""
                     , case when rh.avatar is not null then 'CrcV2_RegisterHuman' else 'CrcV2_RegisterGroup' end as ""type""
                     , tb.""tokenAddress"" as ""tokenOwner""
                FROM  public.""CrcV2_TransferBatch"" tb
                left join ""CrcV2_RegisterHuman"" rh on rh.avatar = tb.""tokenAddress""
                WHERE tb.""to"" = @address

                UNION ALL

                -- V2 wrapped ERC20 tokens (both inflationary and demurraged)
                SELECT DISTINCT wt.""tokenAddress""
                     , case when wd.""circlesType"" = 0 then 'CrcV2_ERC20WrapperDeployed_Demurraged' else 'CrcV2_ERC20WrapperDeployed_Inflationary' end as type
                     , wd.avatar as tokenOwner
                FROM  public.""CrcV2_Erc20WrapperTransfer"" wt
                join ""CrcV2_ERC20WrapperDeployed"" wd on wd.""erc20Wrapper"" = wt.""tokenAddress""
                WHERE wt.""to"" = @address
            )
            SELECT DISTINCT ""tokenAddress"", ""type"", ""tokenOwner""
            FROM tokens
        ";

        using var connection = new NpgsqlConnection(_readOnlyDbConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", lowerAddress);

        var tokenExposureIds = new Dictionary<string, TokenExposureInfo>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var token = reader.GetString(0);
            var tokenType = reader.GetString(1);
            var tokenOwner = reader.GetString(2);

            var isWrapped = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary"
                or "CrcV2_ERC20WrapperDeployed_Demurraged";

            var isInflationary = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary";
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
                isGroup);

            tokenExposureIds.Add(token, tokenInfo);
        }

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
        var tokens = GetTokenExposureIds(address);
        var hubAddress = _settings.CirclesV2HubAddress;

        var erc20Tokens = tokens.Values.Where(o => o.IsErc20).ToArray();
        var erc1155Tokens = tokens.Values.Where(o => o.IsErc1155).ToArray();

        var balances = new Dictionary<string, BigInteger>();

        // Fetch ERC1155 balances in batch
        if (erc1155Tokens.Length > 0)
        {
            var accounts = Enumerable.Repeat(address, erc1155Tokens.Length).ToArray();
            var tokenIds = erc1155Tokens.Select(o => AddressToTokenIdBigInt(o.TokenAddress)).ToArray();

            var erc1155Balances = await GetBatchBalances(hubAddress, accounts, tokenIds);

            for (int i = 0; i < erc1155Tokens.Length; i++)
            {
                balances.Add(erc1155Tokens[i].TokenAddress, erc1155Balances[i]);
            }
        }

        // Fetch ERC20 balances individually
        foreach (var tokenInfo in erc20Tokens)
        {
            var balance = await FetchBalance(
                tokenInfo.TokenAddress,
                address,
                tokenInfo.IsErc20,
                hubAddress);

            balances.Add(tokenInfo.TokenAddress, balance);
        }

        // Convert to CirclesTokenBalance
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tokenBalances = tokens.Values.Select(token =>
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
                // OG CRC
                attoCrc = rawBalance;
                crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
            }
            else
            {
                if (token.IsInflationary)
                {
                    staticAttoCircles = rawBalance;
                    staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                    attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                    circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                    attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                    crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                }
                else
                {
                    attoCircles = rawBalance;
                    circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                    attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                    crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                    staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                    staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                }
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
        .OrderByDescending(o => o.StaticCircles)
        .ToList();

        return tokenBalances.ToArray();
    }

    public async Task<TokenInfo> GetTokenInfo(string tokenAddress)
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

        throw new InvalidOperationException($"No token info found for address {tokenAddress}");
    }

    public async Task<TokenInfo[]> GetTokenInfoBatch(string[] tokenAddresses)
    {
        var results = new List<TokenInfo>();
        foreach (var tokenAddress in tokenAddresses)
        {
            try
            {
                var tokenInfo = await GetTokenInfo(tokenAddress);
                results.Add(tokenInfo);
            }
            catch
            {
                // Skip tokens that don't exist
            }
        }
        return results.ToArray();
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

        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();
        var result = new AvatarInfo?[addresses.Length];

        await using var connection = await CreateConnectionAsync();

        // First, check for V2 avatars
        var v2AvatarMap = new Dictionary<string, AvatarInfo>();
        const string v2Sql = @"
            SELECT a.avatar, a.""timestamp"", a.name, a.type, rn.""metadataDigest"", rsn.""shortName"", a.""cidV0Digest""
            FROM ""V_CrcV2_Avatars"" a
            LEFT JOIN ""CrcV2_UpdateMetadataDigest"" rn ON rn.avatar = a.avatar
            LEFT JOIN ""CrcV2_RegisterShortName"" rsn ON rsn.avatar = a.avatar
            WHERE a.avatar = ANY(@addresses)";

        await using (var cmd = new NpgsqlCommand(v2Sql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
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
        }

        // Then, check for V1 avatars (those not found in V2)
        var v1AvatarMap = new Dictionary<string, AvatarInfo>();
        const string v1Sql = @"
            SELECT s.""user"", s.token
            FROM ""CrcV1_Signup"" s
            WHERE s.""user"" = ANY(@addresses)";

        await using (var cmd = new NpgsqlCommand(v1Sql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
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

        // Get V1 CIDs for V1 avatars
        var v1CidSql = @"
            SELECT avatar, ""metadataDigest""
            FROM ""CrcV1_UpdateMetadataDigest""
            WHERE avatar = ANY(@addresses)";

        var v1CidMap = new Dictionary<string, string>();
        try
        {
            await using (var cmd = new NpgsqlCommand(v1CidSql, connection))
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

    public async Task<string?> GetProfileCid(string address)
    {
        var results = await GetProfileCidBatchInternal(new[] { address });
        return results[0];
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

    public async Task<Dictionary<string, JsonElement?>> GetProfileByAddressBatch(string[] addresses)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return new Dictionary<string, JsonElement?>();
        }

        var results = await GetProfileByAddressBatchInternal(addresses);
        var dict = new Dictionary<string, JsonElement?>();
        for (int i = 0; i < addresses.Length; i++)
        {
            if (addresses[i] != null)
            {
                dict[addresses[i].ToLower()] = results[i];
            }
        }
        return dict;
    }

    private async Task<JsonElement?[]> GetProfileByAddressBatchInternal(string[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();
        var result = new JsonElement?[addresses.Length];

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
                        enrichedProfile[kvp.Key] = kvp.Value;
                    }

                    // Add shortName if available
                    if (hasShortName)
                        enrichedProfile["shortName"] = JsonSerializer.SerializeToElement(shortName);

                    // Note: avatarType and CID are NOT included to match remote implementation

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
                    ["description"] = null
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

            if (_profileByCidCache.TryGetValue(currentCid, out var cached) && cached != null)
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
            var payloadCellValue = reader.GetValue(0);
            int targetIndex = missingCidIndexes[readCount];
            string targetCid = cids[targetIndex];

            if (payloadCellValue is string payloadStr)
            {
                var profile = JsonSerializer.Deserialize<JsonElement>(payloadStr);
                result[targetIndex] = profile;
                _profileByCidCache.Set(targetCid, profile, new MemoryCacheEntryOptions { Size = 1 });
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

    public async Task<Dictionary<string, JsonElement?>> GetProfileByCidBatch(string[] cids)
    {
        var results = await GetProfileByCidBatchInternal(cids);
        var dict = new Dictionary<string, JsonElement?>();
        for (int i = 0; i < cids.Length; i++)
        {
            dict[cids[i]] = results[i];
        }
        return dict;
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
        using var connection = await CreateConnectionAsync();
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
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var trusts = new List<TrustRelation>();
        var trustedBy = new List<TrustRelation>();
        using var reader = await command.ExecuteReaderAsync();
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
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address1", address1.ToLower());
        command.Parameters.AddWithValue("address2", address2.ToLower());

        var commonTrusts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            commonTrusts.Add(reader.GetString(0));
        }
        return new CommonTrustResponse(Address1: address1.ToLower(), Address2: address2.ToLower(), CommonTrusts: commonTrusts);
    }

    public async Task<NetworkSnapshotResponse> GetNetworkSnapshot()
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
        var snapshot = await JsonSerializer.DeserializeAsync<JsonElement>(stream);

        // Extract BlockNumber and Addresses directly to match remote response structure
        var blockNumber = snapshot.GetProperty("BlockNumber");
        var addresses = snapshot.GetProperty("Addresses");

        return new NetworkSnapshotResponse(BlockNumber: blockNumber, Addresses: addresses);
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


    public async Task<EventsResponse> GetEvents(
        string? address,
        long? fromBlock,
        long? toBlock,
        string[]? eventTypes,
        IFilterPredicateDto[]? filterPredicates = null,
        bool? sortAscending = false)
    {
        // Use the schema-aware map to get all event tables and their address columns
        var eventTables = DatabaseSchemaMap.TableAddressColumns;

        if (eventTables == null)
        {
            return new EventsResponse(Array.Empty<object>());
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

        // Determine sort order once
        var sortOrder = sortAscending == true ? "ASC" : "DESC";

        foreach (var table in relevantTables)
        {
            // Skip tables that don't have the required event columns (like System tables)
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

            // Wrap each query in a subquery to apply ORDER BY and LIMIT before UNION
            // This pushes the sorting and limit down to each table scan instead of sorting the entire UNION result
            var query = $@"(SELECT t.""blockNumber"", t.""transactionIndex"", t.""transactionHash"", t.""logIndex"", '{table.Key}' as event_name, to_jsonb(t) as event_payload FROM ""{table.Key}"" t{whereSql} ORDER BY t.""blockNumber"" {sortOrder}, t.""transactionIndex"" {sortOrder}, t.""logIndex"" {sortOrder} LIMIT 1000)";
            queries.Add(query);
        }

        if (queries.Count == 0)
        {
            return new EventsResponse(Array.Empty<object>());
        }

        // Combine results from all tables and apply final ORDER BY and LIMIT
        var finalSql = string.Join(" UNION ALL ", queries);
        finalSql = $"SELECT * FROM ({finalSql}) combined ORDER BY \"blockNumber\" {sortOrder}, \"transactionIndex\" {sortOrder}, \"logIndex\" {sortOrder} LIMIT 1000";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.CommandTimeout = 30; // 30 second timeout to prevent hanging
        command.Parameters.AddRange(parameters.ToArray());

        var events = new List<object>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Parse the event payload
            var payloadJson = reader.GetString(5);
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

            if (payloadDict != null)
            {
                // Convert numeric fields to hex format and create ordered dictionary
                // Order: blockNumber, timestamp, transactionIndex, logIndex, transactionHash, then other fields
                var orderedValues = new Dictionary<string, object?>();

                // Add standard fields in remote server order
                var standardFieldsOrder = new[] { "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash" };

                foreach (var fieldName in standardFieldsOrder)
                {
                    if (payloadDict.TryGetValue(fieldName, out var value))
                    {
                        // Convert numeric types to hex strings
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

                // Add remaining fields
                foreach (var kvp in payloadDict)
                {
                    var key = kvp.Key;
                    var value = kvp.Value;

                    // Skip if already added
                    if (orderedValues.ContainsKey(key))
                        continue;

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        orderedValues[key] = value.GetString();
                    }
                    else if (value.ValueKind == JsonValueKind.Number)
                    {
                        // Keep other numbers as strings
                        orderedValues[key] = value.ToString();
                    }
                    else
                    {
                        orderedValues[key] = JsonSerializer.Deserialize<object>(value.GetRawText());
                    }
                }

                events.Add(new
                {
                    @event = reader.GetString(4),
                    values = orderedValues
                });
            }
        }
        return new EventsResponse(events.ToArray());
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
        var column = $"t.\"{predicate.Column}\"";
        var paramName = $"@pred_{tablePrefix}_{predicate.Column}_{parameters.Count}";

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
        var column = $"\"{predicate.Column}\"::text";
        var paramName = $"@p{parameters.Count}";

        switch (predicate.FilterType)
        {
            case FilterType.Equals:
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
            using var connection = await CreateConnectionAsync();
            using var command = new NpgsqlCommand("SELECT 1", connection);
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
            using var connection = await CreateConnectionAsync();
            using var command = new NpgsqlCommand("SELECT MAX(\"blockNumber\") as block_number FROM \"System_Block\"", connection);
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

        foreach (var schema in DatabaseSchemaProvider.AllSchemas)
        {
            var schemaNamespaces = schema.Tables.GroupBy(o => o.Key.Namespace);

            foreach (var @namespace in schemaNamespaces)
            {
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

    public async Task<QueryResponse> Query(SelectDto query)
    {
        if (string.IsNullOrEmpty(query.Table) || string.IsNullOrEmpty(query.Namespace))
        {
            throw new ArgumentException("Namespace and Table must be provided.");
        }

        // Validate and safely construct table name
        var validatedNamespace = ValidateIdentifier(query.Namespace, "Namespace");
        var validatedTable = ValidateIdentifier(query.Table, "Table");
        var tableName = $"\"{validatedNamespace}_{validatedTable}\"";

        // Validate and quote columns
        var columns = "*";
        if (query.Columns != null && query.Columns.Any())
        {
            var validatedColumns = query.Columns.Select(c => ValidateIdentifier(c, "Column")).ToArray();
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

        // Validate LIMIT and OFFSET parameters
        const int maxLimit = 10000; // Reasonable safety limit
        var limitSql = "";
        if (query.Limit.HasValue)
        {
            if (query.Limit.Value <= 0)
            {
                throw new ArgumentException("Limit must be greater than 0.");
            }
            if (query.Limit.Value > maxLimit)
            {
                throw new ArgumentException($"Limit cannot exceed {maxLimit}.");
            }
            limitSql = $"LIMIT {query.Limit.Value}";
        }

        // Note: OFFSET would also need validation if used
        // const int maxOffset = 1000000;
        // if (query.Offset.HasValue && query.Offset.Value < 0)
        // {
        //     throw new ArgumentException("Offset cannot be negative.");
        // }

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

        return new QueryResponse(Columns: columnNames, Rows: results);
    }
}