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

public class CirclesRpcModule : ICirclesRpcModule
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
        return result?.ToString() ?? "0";
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
        // NOTE: The returned balances are raw sums of historical transfers.
        // They do NOT account for time-based adjustments:
        // - V1 tokens: No inflation adjustment applied
        // - V2 demurraged tokens: No demurrage decay applied
        // - V2 inflationary tokens: No inflation adjustment applied
        // For accurate balances, a blockchain connector service is required (Phase 3).

        var lowerAddress = address.ToLower();
        await using var connection = await CreateConnectionAsync();

        var tokenBalances = new List<CirclesTokenBalance>();

        // Current timestamp for conversion placeholders
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ===== V1 Token Balances =====
        const string v1Sql = @"
            SELECT
                t.""tokenAddress"",
                COALESCE(SUM(CASE
                    WHEN t.""to"" = @address THEN t.amount::numeric
                    WHEN t.""from"" = @address THEN -t.amount::numeric
                    ELSE 0
                END), 0) as balance,
                s.""user"" as owner
            FROM ""CrcV1_Transfer"" t
            JOIN ""CrcV1_Signup"" s ON s.token = t.""tokenAddress""
            WHERE t.""to"" = @address OR t.""from"" = @address
            GROUP BY t.""tokenAddress"", s.""user""
            HAVING SUM(CASE
                WHEN t.""to"" = @address THEN t.amount::numeric
                WHEN t.""from"" = @address THEN -t.amount::numeric
                ELSE 0
            END) > 0";

        await using (var cmd = new NpgsqlCommand(v1Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", lowerAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var tokenAddress = reader.GetString(0);
                var balanceStr = reader.GetValue(1).ToString();
                var owner = reader.GetString(2);

                if (!BigInteger.TryParse(balanceStr, out var rawBalance) || rawBalance <= 0)
                    continue;

                // V1 tokens are inflationary CRC (demurraged)
                var attoCrc = rawBalance;
                var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                var attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                var circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                var staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                tokenBalances.Add(new CirclesTokenBalance(
                    TokenAddress: tokenAddress,
                    TokenId: tokenAddress,
                    TokenOwner: owner,
                    TokenType: "CrcV1_Signup",
                    Version: 1,
                    AttoCircles: attoCircles.ToString(),
                    Circles: circles,
                    StaticAttoCircles: staticAttoCircles.ToString(),
                    StaticCircles: staticCircles,
                    AttoCrc: attoCrc.ToString(),
                    Crc: crc,
                    IsErc20: true,
                    IsErc1155: false,
                    IsWrapped: false,
                    IsInflationary: true,
                    IsGroup: false
                ));
            }
        }

        // ===== V2 ERC-1155 Token Balances (Demurraged) =====
        const string v2Erc1155Sql = @"
            WITH transfers AS (
                SELECT
                    ""tokenAddress"",
                    SUM(CASE
                        WHEN ""to"" = @address THEN value::numeric
                        WHEN ""from"" = @address THEN -value::numeric
                        ELSE 0
                    END) as balance
                FROM ""CrcV2_TransferSingle""
                WHERE ""to"" = @address OR ""from"" = @address
                GROUP BY ""tokenAddress""

                UNION ALL

                SELECT
                    ""tokenAddress"",
                    SUM(CASE
                        WHEN ""to"" = @address THEN value::numeric
                        WHEN ""from"" = @address THEN -value::numeric
                        ELSE 0
                    END) as balance
                FROM ""CrcV2_TransferBatch""
                WHERE ""to"" = @address OR ""from"" = @address
                GROUP BY ""tokenAddress""
            ),
            balances AS (
                SELECT
                    ""tokenAddress"",
                    SUM(balance) as total_balance
                FROM transfers
                GROUP BY ""tokenAddress""
                HAVING SUM(balance) > 0
            )
            SELECT
                b.""tokenAddress"",
                b.total_balance,
                COALESCE(rh.avatar, rg.""group"") as owner,
                CASE
                    WHEN rh.avatar IS NOT NULL THEN 'CrcV2_RegisterHuman'
                    WHEN rg.""group"" IS NOT NULL THEN 'CrcV2_RegisterGroup'
                    ELSE 'Unknown'
                END as token_type,
                CASE WHEN rg.""group"" IS NOT NULL THEN true ELSE false END as is_group
            FROM balances b
            LEFT JOIN ""CrcV2_RegisterHuman"" rh ON rh.avatar = b.""tokenAddress""
            LEFT JOIN ""CrcV2_RegisterGroup"" rg ON rg.""group"" = b.""tokenAddress""";

        await using (var cmd = new NpgsqlCommand(v2Erc1155Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", lowerAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var tokenAddress = reader.GetString(0);
                var balanceStr = reader.GetValue(1).ToString();
                var owner = reader.IsDBNull(2) ? tokenAddress : reader.GetString(2);
                var tokenType = reader.GetString(3);
                var isGroup = reader.GetBoolean(4);

                if (!BigInteger.TryParse(balanceStr, out var rawBalance) || rawBalance <= 0)
                    continue;

                // V2 ERC-1155 tokens are demurraged (stored in attoCircles)
                var attoCircles = rawBalance;
                var circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                var staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                var attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                // For V2 ERC-1155, tokenId is derived from the address
                var tokenId = AddressToTokenId(tokenAddress);

                tokenBalances.Add(new CirclesTokenBalance(
                    TokenAddress: tokenAddress,
                    TokenId: tokenId,
                    TokenOwner: owner,
                    TokenType: tokenType,
                    Version: 2,
                    AttoCircles: attoCircles.ToString(),
                    Circles: circles,
                    StaticAttoCircles: staticAttoCircles.ToString(),
                    StaticCircles: staticCircles,
                    AttoCrc: attoCrc.ToString(),
                    Crc: crc,
                    IsErc20: false,
                    IsErc1155: true,
                    IsWrapped: false,
                    IsInflationary: false,
                    IsGroup: isGroup
                ));
            }
        }

        // ===== V2 ERC-20 Wrapped Token Balances =====
        const string v2Erc20Sql = @"
            SELECT
                t.""tokenAddress"",
                COALESCE(SUM(CASE
                    WHEN t.""to"" = @address THEN t.amount::numeric
                    WHEN t.""from"" = @address THEN -t.amount::numeric
                    ELSE 0
                END), 0) as balance,
                wd.avatar as owner,
                wd.""circlesType""
            FROM ""CrcV2_Erc20WrapperTransfer"" t
            JOIN ""CrcV2_ERC20WrapperDeployed"" wd ON wd.""erc20Wrapper"" = t.""tokenAddress""
            WHERE t.""to"" = @address OR t.""from"" = @address
            GROUP BY t.""tokenAddress"", wd.avatar, wd.""circlesType""
            HAVING SUM(CASE
                WHEN t.""to"" = @address THEN t.amount::numeric
                WHEN t.""from"" = @address THEN -t.amount::numeric
                ELSE 0
            END) > 0";

        await using (var cmd = new NpgsqlCommand(v2Erc20Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", lowerAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var tokenAddress = reader.GetString(0);
                var balanceStr = reader.GetValue(1).ToString();
                var owner = reader.GetString(2);
                var circlesType = reader.GetInt32(3); // 0 = demurraged, 1 = inflationary

                if (!BigInteger.TryParse(balanceStr, out var rawBalance) || rawBalance <= 0)
                    continue;

                bool isInflationary = circlesType == 1;
                string tokenType = isInflationary
                    ? "CrcV2_ERC20WrapperDeployed_Inflationary"
                    : "CrcV2_ERC20WrapperDeployed_Demurraged";

                BigInteger attoCircles;
                BigInteger staticAttoCircles;
                BigInteger attoCrc;

                if (isInflationary)
                {
                    // Inflationary wrapped tokens store staticAttoCircles
                    staticAttoCircles = rawBalance;
                    attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                    attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                }
                else
                {
                    // Demurraged wrapped tokens store attoCircles
                    attoCircles = rawBalance;
                    staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                    attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                }

                var circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                tokenBalances.Add(new CirclesTokenBalance(
                    TokenAddress: tokenAddress,
                    TokenId: tokenAddress,
                    TokenOwner: owner,
                    TokenType: tokenType,
                    Version: 2,
                    AttoCircles: attoCircles.ToString(),
                    Circles: circles,
                    StaticAttoCircles: staticAttoCircles.ToString(),
                    StaticCircles: staticCircles,
                    AttoCrc: attoCrc.ToString(),
                    Crc: crc,
                    IsErc20: true,
                    IsErc1155: false,
                    IsWrapped: true,
                    IsInflationary: isInflationary,
                    IsGroup: false
                ));
            }
        }

        // Order by circles value (descending)
        var orderedBalances = tokenBalances
            .OrderByDescending(b => b.Circles)
            .ToList();

        return orderedBalances;
    }

    /// <summary>
    /// Converts an Ethereum address to a uint256 token ID.
    /// This mimics the conversion used in ERC-1155 for Circles V2.
    /// </summary>
    private static string AddressToTokenId(string address)
    {
        // Remove 0x prefix if present
        var hex = address.StartsWith("0x") ? address.Substring(2) : address;

        // Parse as BigInteger (treating as big-endian hex)
        if (BigInteger.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var tokenId))
        {
            return tokenId.ToString();
        }

        return "0";
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
        var results = await GetAvatarInfoBatchInternal(new[] { address });
        var result = results[0];

        if (result == null)
        {
            return CreateError($"No avatar found for address {address}");
        }

        return result;
    }

    public async Task<object> GetAvatarInfoBatch(string[] addresses)
    {
        return await GetAvatarInfoBatchInternal(addresses);
    }

    private async Task<object?[]> GetAvatarInfoBatchInternal(string[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();
        var result = new object?[addresses.Length];

        await using var connection = await CreateConnectionAsync();

        // First, check for V2 avatars
        var v2AvatarMap = new Dictionary<string, object>();
        const string v2Sql = @"
            SELECT a.avatar, a.""timestamp"", a.name, a.type, rn.cid, rsn.""shortName""
            FROM ""V_CrcV2_Avatars"" a
            LEFT JOIN (SELECT avatar, 'bafy' || encode(""metadataDigest"", 'base64') as cid FROM ""CrcV2_UpdateMetadataDigest"") rn ON rn.avatar = a.avatar
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
                var cid = reader.IsDBNull(4) ? null : reader.GetString(4);
                var shortName = reader.IsDBNull(5) ? null : reader.GetString(5);

                v2AvatarMap[avatar] = new
                {
                    version = 2,
                    type = avatarType,
                    avatar,
                    tokenId = avatar,  // For V2, tokenId is the avatar address (for ERC1155)
                    hasV1 = false,
                    v1Token = (string?)null,
                    cidV0Digest = "",
                    cidV0 = cid,
                    isHuman,
                    name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    symbol = "",
                    shortName
                };
            }
        }

        // Then, check for V1 avatars (those not found in V2)
        var v1AvatarMap = new Dictionary<string, object>();
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

                v1AvatarMap[userAddress] = new
                {
                    version = 1,
                    type = "CrcV1_Signup",
                    avatar = userAddress,
                    tokenId = tokenAddress,
                    hasV1 = true,
                    v1Token = tokenAddress,
                    cidV0Digest = "",
                    cidV0 = (string?)null,
                    isHuman = true,  // V1 signups are always human
                    name = (string?)null,
                    symbol = "",
                    shortName = (string?)null
                };
            }
        }

        // Get V1 CIDs for V1 avatars
        var v1CidSql = @"
            SELECT avatar, 'bafy' || encode(""metadataDigest"", 'base64') as cid
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
                    var cid = reader.GetString(1);
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
                    var v2Dict = (dynamic)v2Avatar;
                    result[i] = new
                    {
                        version = v2Dict.version,
                        type = v2Dict.type,
                        avatar = v2Dict.avatar,
                        tokenId = v2Dict.tokenId,
                        hasV1 = true,
                        v1Token = ((dynamic)v1Avatar).v1Token,
                        cidV0Digest = v2Dict.cidV0Digest,
                        cidV0 = v2Dict.cidV0 ?? (v1CidMap.TryGetValue(addr, out var v1Cid) ? v1Cid : null),
                        isHuman = v2Dict.isHuman,
                        name = v2Dict.name,
                        symbol = v2Dict.symbol,
                        shortName = v2Dict.shortName
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
                var v1Dict = (dynamic)v1Avatar;
                result[i] = new
                {
                    version = v1Dict.version,
                    type = v1Dict.type,
                    avatar = v1Dict.avatar,
                    tokenId = v1Dict.tokenId,
                    hasV1 = v1Dict.hasV1,
                    v1Token = v1Dict.v1Token,
                    cidV0Digest = v1Dict.cidV0Digest,
                    cidV0 = v1CidMap.TryGetValue(addr, out var v1Cid) ? v1Cid : null,
                    isHuman = v1Dict.isHuman,
                    name = v1Dict.name,
                    symbol = v1Dict.symbol,
                    shortName = v1Dict.shortName
                };
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    public async Task<object> GetProfileCid(string address)
    {
        var results = await GetProfileCidBatchInternal(new[] { address });
        var cid = results[0];
        return cid != null ? cid : CreateError("No profile found");
    }

    public async Task<object> GetProfileCidBatch(string[] addresses)
    {
        return await GetProfileCidBatchInternal(addresses);
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
        const string v2Sql = @"SELECT avatar, 'bafy' || encode(""metadataDigest"", 'base64') as cid FROM ""CrcV2_UpdateMetadataDigest"" WHERE avatar = ANY(@addresses)";

        await using (var cmd = new NpgsqlCommand(v2Sql, connection))
        {
            cmd.Parameters.AddWithValue("addresses", lowerAddresses);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var avatar = reader.GetString(0);
                var cid = reader.GetString(1);
                v2CidMap[avatar] = cid;
            }
        }

        // Then, check V1 CIDs (for those not found in V2)
        var v1CidMap = new Dictionary<string, string>();
        try
        {
            const string v1Sql = @"SELECT avatar, 'bafy' || encode(""metadataDigest"", 'base64') as cid FROM ""CrcV1_UpdateMetadataDigest"" WHERE avatar = ANY(@addresses)";

            await using (var cmd = new NpgsqlCommand(v1Sql, connection))
            {
                cmd.Parameters.AddWithValue("addresses", lowerAddresses);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var avatar = reader.GetString(0);
                    var cid = reader.GetString(1);
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

    public async Task<object> GetProfileByAddress(string address)
    {
        var results = await GetProfileByAddressBatchInternal(new[] { address });
        var profile = results[0];
        return profile ?? CreateError($"No profile found for address {address}");
    }

    public async Task<object> GetProfileByAddressBatch(string[] addresses)
    {
        return await GetProfileByAddressBatchInternal(addresses);
    }

    private async Task<object?[]> GetProfileByAddressBatchInternal(string[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        var lowerAddresses = addresses.Where(a => a != null).Select(a => a.ToLower()).ToArray();
        var result = new object?[addresses.Length];

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
        var profileByCidMap = new Dictionary<string, object?>();

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

            object? baseProfile = null;
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
                var profileJson = JsonSerializer.Serialize(baseProfile);
                var profileDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(profileJson);

                if (profileDict != null)
                {
                    profileDict["address"] = addr;
                    if (hasShortName) profileDict["shortName"] = shortName;
                    if (hasAvatarType) profileDict["avatarType"] = avatarType;
                    if (cid != null) profileDict["CID"] = cid;

                    result[i] = profileDict;
                }
                else
                {
                    result[i] = baseProfile;
                }
            }
            // If no profile but we have metadata, create a minimal profile
            else if (hasAvatarType || hasShortName)
            {
                result[i] = new
                {
                    address = addr,
                    avatarType = hasAvatarType ? avatarType : null,
                    shortName = hasShortName ? shortName : null,
                    CID = cid,
                    name = (string?)null,
                    description = (string?)null
                };
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    private async Task<object?[]> GetProfileByCidBatchInternal(string[] cids)
    {
        if (cids.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(cids), "Batch size exceeds 1000");
        }

        var result = new object?[cids.Length];
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
                result[i] = cached;
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
                var profile = JsonSerializer.Deserialize<object>(payloadStr);
                if (profile != null)
                {
                    result[targetIndex] = profile;
                    _profileByCidCache.Set(targetCid, profile, new MemoryCacheEntryOptions { Size = 1 });
                }
                else
                {
                    result[targetIndex] = null;
                }
            }
            else
            {
                result[targetIndex] = null;
            }

            readCount++;
        }

        return result;
    }

    public async Task<object> GetProfileByCid(string cid)
    {
        if (string.IsNullOrWhiteSpace(cid))
        {
            return CreateError("CID must not be empty.");
        }

        var results = await GetProfileByCidBatchInternal(new[] { cid });
        var profile = results[0];
        return profile ?? CreateError($"No profile found for cid {cid}");
    }

    public async Task<object> GetProfileByCidBatch(string[] cids)
    {
        return await GetProfileByCidBatchInternal(cids);
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
        var trusts = new Dictionary<string, int>();
        var trustedBy = new Dictionary<string, int>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var user = reader.GetString(0);
            var canSendTo = reader.GetString(1);
            // V1 trust limits are percentages (0-100), stored as uint256 in contract but always fit in int32
            var limit = Convert.ToInt32(reader.GetValue(2));
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

    public async Task<object> GetEvents(
        string? address,
        long? fromBlock,
        long? toBlock,
        string[]? eventTypes,
        FilterPredicateDto[]? filterPredicates = null,
        bool? sortAscending = false)
    {
        // Use the schema-aware map to get all event tables and their address columns
        var eventTables = DatabaseSchemaMap.TableAddressColumns;

        var queries = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        // Filter to only requested event types, or use all tables if no filter specified
        var relevantTables = eventTypes == null || !eventTypes.Any()
            ? eventTables
            : eventTables.Where(kvp => eventTypes.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Add basic filter parameters
        if (address != null) parameters.Add(new NpgsqlParameter("address", address.ToLower()));
        if (fromBlock.HasValue) parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        if (toBlock.HasValue) parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));

        foreach (var table in relevantTables)
        {
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
                    if (string.IsNullOrWhiteSpace(predicate.Column))
                        continue;

                    var predicateClause = BuildPredicateClause(predicate, parameters, table.Key);
                    if (!string.IsNullOrEmpty(predicateClause))
                    {
                        whereClauses.Add(predicateClause);
                    }
                }
            }

            var whereSql = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";

            // Include transactionIndex in the SELECT to support ORDER BY
            queries.Add($@"SELECT t.""blockNumber"", t.""transactionIndex"", t.""transactionHash"", t.""logIndex"", '{table.Key}' as event_name, to_jsonb(t) as event_payload FROM ""{table.Key}"" t {whereSql}");
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
                transactionIndex = reader.GetInt32(1),
                transactionHash = reader.GetString(2),
                logIndex = reader.GetInt32(3),
                @event = reader.GetString(4),
                payload = JsonSerializer.Deserialize<object>(reader.GetString(5))
            });
        }
        return new { events };
    }

    /// <summary>
    /// Builds a WHERE clause from a FilterPredicateDto.
    /// </summary>
    private string BuildPredicateClause(FilterPredicateDto predicate, List<NpgsqlParameter> parameters, string tablePrefix)
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
        const string sql = @"SELECT DISTINCT table_schema, table_name FROM information_schema.tables WHERE table_schema NOT IN ('information_schema', 'pg_catalog') ORDER BY table_schema, table_name";
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
                whereClauses.Add($"\"{filter.Column}\"::text {op} {paramName}::text");
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