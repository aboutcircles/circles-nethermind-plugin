using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Circles.Pathfinder.DTOs;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using static Circles.Rpc.Host.JsonRpcHelpers;

namespace Circles.Rpc.Host;

public class CirclesRpcModule : ICirclesRpcModule
{
    private readonly Settings _settings;
    private readonly string _readOnlyDbConnectionString;
    private readonly MemoryCache _profileByCidCache;
    private static readonly HttpClient HttpClient = new();
    private readonly NethermindRpcClient? _nethermindRpcClient;

    public CirclesRpcModule(Settings settings, IHttpClientFactory? httpClientFactory = null)
    {
        _settings = settings;
        _readOnlyDbConnectionString = settings.IndexReadonlyDbConnectionString;
        _profileByCidCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });

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

    public async Task<string> GetTotalBalanceV1(string address)
    {
        if (_settings.BalanceMode.Equals("live", StringComparison.OrdinalIgnoreCase) && _nethermindRpcClient != null)
        {
            return await GetTotalBalanceV1Live(address);
        }
        else
        {
            return await GetTotalBalanceV1Database(address);
        }
    }

    private async Task<string> GetTotalBalanceV1Database(string address)
    {
        // NOTE: This balance is a raw sum of historical transfers and does not account for
        // time-based inflation. The actual balance may be higher.
        using var connection = await CreateConnectionAsync();
        const string sql = @"SELECT COALESCE(SUM(CASE WHEN t.""to"" = @address THEN t.amount WHEN t.""from"" = @address THEN -t.amount ELSE 0 END), 0) as balance FROM ""CrcV1_Transfer"" t WHERE t.""to"" = @address OR t.""from"" = @address";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());
        var result = await command.ExecuteScalarAsync();
        var sumStr = result?.ToString() ?? "0";
        var sum = BigInteger.Parse(sumStr);
        return CirclesConverter.AttoCirclesToCircles(sum).ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> GetTotalBalanceV1Live(string address)
    {
        // Get all V1 token addresses for this user
        using var connection = await CreateConnectionAsync();
        const string sql = @"
            SELECT DISTINCT t.""tokenAddress""
            FROM ""CrcV1_Transfer"" t
            WHERE t.""to"" = @address OR t.""from"" = @address
        ";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("address", address.ToLower());

        var tokenAddresses = new List<string>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tokenAddresses.Add(reader.GetString(0));
        }

        if (tokenAddresses.Count == 0)
        {
            return "0";
        }

        // Fetch live balances for all V1 tokens
        BigInteger totalBalance = BigInteger.Zero;
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var tokenAddress in tokenAddresses)
        {
            try
            {
                // Call balanceOf(address) on the V1 token contract
                var data = AbiEncoder.EncodeBalanceOfErc20(address);
                var resultHex = await _nethermindRpcClient!.EthCall(tokenAddress, data);
                var attoCrc = AbiEncoder.DecodeUint256(resultHex);

                if (attoCrc > 0)
                {
                    // Convert V1 CRC to demurraged Circles with inflation
                    var attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                    totalBalance += attoCircles;
                }
            }
            catch (Exception ex)
            {
                // Log and continue - don't fail the entire request if one token fails
                Console.WriteLine($"Warning: Failed to fetch balance for V1 token {tokenAddress}: {ex.Message}");
            }
        }

        return CirclesConverter.AttoCirclesToCircles(totalBalance).ToString(CultureInfo.InvariantCulture);
    }

    public async Task<string> GetTotalBalanceV2(string address)
    {
        if (_settings.BalanceMode.Equals("live", StringComparison.OrdinalIgnoreCase) && _nethermindRpcClient != null)
        {
            return await GetTotalBalanceV2Live(address);
        }
        else
        {
            return await GetTotalBalanceV2Database(address);
        }
    }

    private async Task<string> GetTotalBalanceV2Database(string address)
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
        var sumStr = result?.ToString() ?? "0";
        var sum = BigInteger.Parse(sumStr);
        return CirclesConverter.AttoCirclesToCircles(sum).ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> GetTotalBalanceV2Live(string address)
    {
        // Get all V2 token addresses and their types for this user
        using var connection = await CreateConnectionAsync();

        // Query for ERC-1155 tokens (demurraged)
        const string erc1155Sql = @"
            SELECT DISTINCT t.""tokenAddress""
            FROM (
                SELECT ""tokenAddress"" FROM ""CrcV2_TransferSingle"" WHERE ""to"" = @address OR ""from"" = @address
                UNION
                SELECT ""tokenAddress"" FROM ""CrcV2_TransferBatch"" WHERE ""to"" = @address OR ""from"" = @address
            ) t
        ";

        // Query for ERC-20 wrapped tokens (can be demurraged or inflationary)
        const string erc20Sql = @"
            SELECT DISTINCT t.""tokenAddress"", wd.""circlesType""
            FROM ""CrcV2_Erc20WrapperTransfer"" t
            JOIN ""CrcV2_ERC20WrapperDeployed"" wd ON wd.""erc20Wrapper"" = t.""tokenAddress""
            WHERE t.""to"" = @address OR t.""from"" = @address
        ";

        // Get ERC-1155 token addresses
        var erc1155Tokens = new List<string>();
        using (var cmd = new NpgsqlCommand(erc1155Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", address.ToLower());
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                erc1155Tokens.Add(reader.GetString(0));
            }
        }

        // Get ERC-20 wrapped token addresses and types
        var erc20Tokens = new List<(string address, int circlesType)>();
        using (var cmd = new NpgsqlCommand(erc20Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", address.ToLower());
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                erc20Tokens.Add((reader.GetString(0), reader.GetInt32(1)));
            }
        }

        if (erc1155Tokens.Count == 0 && erc20Tokens.Count == 0)
        {
            return "0";
        }

        BigInteger totalBalance = BigInteger.Zero;
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Fetch ERC-1155 balances from the Hub contract
        // V2 uses a single Hub contract at a known address
        const string hubAddress = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8"; // Gnosis Chain V2 Hub

        if (erc1155Tokens.Count > 0)
        {
            // Convert token addresses to token IDs for ERC-1155
            var tokenIds = erc1155Tokens
                .Select(addr => AddressToTokenIdBigInt(addr))
                .ToArray();

            // Create arrays for balanceOfBatch call
            var owners = Enumerable.Repeat(address, erc1155Tokens.Count).ToArray();

            try
            {
                var data = AbiEncoder.EncodeBalanceOfBatch(owners, tokenIds);
                var resultHex = await _nethermindRpcClient!.EthCall(hubAddress, data);
                var balances = AbiEncoder.DecodeUint256Array(resultHex);

                for (int i = 0; i < balances.Length; i++)
                {
                    if (balances[i] > 0)
                    {
                        // V2 ERC-1155 tokens are already in demurraged attoCircles
                        totalBalance += balances[i];
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to fetch ERC-1155 balances: {ex.Message}");
            }
        }

        // Fetch ERC-20 wrapped token balances
        foreach (var (tokenAddress, circlesType) in erc20Tokens)
        {
            try
            {
                var data = AbiEncoder.EncodeBalanceOfErc20(address);
                var resultHex = await _nethermindRpcClient!.EthCall(tokenAddress, data);
                var balance = AbiEncoder.DecodeUint256(resultHex);

                if (balance > 0)
                {
                    // circlesType: 0 = demurraged, 1 = inflationary
                    if (circlesType == 1)
                    {
                        // Inflationary wrapped tokens store staticAttoCircles
                        var attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(balance);
                        totalBalance += attoCircles;
                    }
                    else
                    {
                        // Demurraged wrapped tokens store attoCircles directly
                        totalBalance += balance;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to fetch balance for ERC-20 token {tokenAddress}: {ex.Message}");
            }
        }

        return CirclesConverter.AttoCirclesToCircles(totalBalance).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts an Ethereum address to a BigInteger token ID for ERC-1155.
    /// </summary>
    private static BigInteger AddressToTokenIdBigInt(string address)
    {
        var hex = address.StartsWith("0x") ? address.Substring(2) : address;
        return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
    }

    public async Task<CirclesTokenBalance[]> GetTokenBalances(string address)
    {
        if (_settings.BalanceMode.Equals("live", StringComparison.OrdinalIgnoreCase) && _nethermindRpcClient != null)
        {
            return await GetTokenBalancesLive(address);
        }
        else
        {
            return await GetTokenBalancesDatabase(address);
        }
    }

    private async Task<CirclesTokenBalance[]> GetTokenBalancesDatabase(string address)
    {
        // NOTE: The returned balances are raw sums of historical transfers.
        // They do NOT account for time-based adjustments:
        // - V1 tokens: No inflation adjustment applied
        // - V2 demurraged tokens: No demurrage decay applied
        // - V2 inflationary tokens: No inflation adjustment applied

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
                var balanceValue = reader.GetFieldValue<decimal>(1);
                var owner = reader.GetString(2);

                var rawBalance = new BigInteger(balanceValue);
                if (rawBalance <= 0)
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
                var balanceValue = reader.GetFieldValue<decimal>(1);
                var owner = reader.IsDBNull(2) ? tokenAddress : reader.GetString(2);
                var tokenType = reader.GetString(3);
                var isGroup = reader.GetBoolean(4);

                var rawBalance = new BigInteger(balanceValue);
                if (rawBalance <= 0)
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
                var balanceValue = reader.GetFieldValue<decimal>(1);
                var owner = reader.GetString(2);
                var circlesType = reader.GetInt32(3); // 0 = demurraged, 1 = inflationary

                var rawBalance = new BigInteger(balanceValue);
                if (rawBalance <= 0)
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
            .ToArray();

        return orderedBalances;
    }

    private async Task<CirclesTokenBalance[]> GetTokenBalancesLive(string address)
    {
        var lowerAddress = address.ToLower();
        await using var connection = await CreateConnectionAsync();

        var tokenBalances = new List<CirclesTokenBalance>();
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ===== V1 Token Balances (Live) =====
        // Get all V1 token addresses and their owners
        const string v1Sql = @"
            SELECT DISTINCT t.""tokenAddress"", s.""user"" as owner
            FROM ""CrcV1_Transfer"" t
            JOIN ""CrcV1_Signup"" s ON s.token = t.""tokenAddress""
            WHERE t.""to"" = @address OR t.""from"" = @address
        ";

        var v1Tokens = new List<(string tokenAddress, string owner)>();
        await using (var cmd = new NpgsqlCommand(v1Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", lowerAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                v1Tokens.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // Fetch live balances for V1 tokens via eth_call
        foreach (var (tokenAddress, owner) in v1Tokens)
        {
            try
            {
                var data = AbiEncoder.EncodeBalanceOfErc20(lowerAddress);
                var resultHex = await _nethermindRpcClient!.EthCall(tokenAddress, data);
                var attoCrc = AbiEncoder.DecodeUint256(resultHex);

                if (attoCrc > 0)
                {
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
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to fetch balance for V1 token {tokenAddress}: {ex.Message}");
            }
        }

        // ===== V2 ERC-1155 Token Balances (Live) =====
        // Get all V2 ERC-1155 token addresses
        const string v2Erc1155Sql = @"
            WITH tokens AS (
                SELECT DISTINCT ""tokenAddress"" FROM ""CrcV2_TransferSingle"" WHERE ""to"" = @address OR ""from"" = @address
                UNION
                SELECT DISTINCT ""tokenAddress"" FROM ""CrcV2_TransferBatch"" WHERE ""to"" = @address OR ""from"" = @address
            )
            SELECT
                t.""tokenAddress"",
                COALESCE(rh.avatar, rg.""group"") as owner,
                CASE
                    WHEN rh.avatar IS NOT NULL THEN 'CrcV2_RegisterHuman'
                    WHEN rg.""group"" IS NOT NULL THEN 'CrcV2_RegisterGroup'
                    ELSE 'Unknown'
                END as token_type,
                CASE WHEN rg.""group"" IS NOT NULL THEN true ELSE false END as is_group
            FROM tokens t
            LEFT JOIN ""CrcV2_RegisterHuman"" rh ON rh.avatar = t.""tokenAddress""
            LEFT JOIN ""CrcV2_RegisterGroup"" rg ON rg.""group"" = t.""tokenAddress""
        ";

        var erc1155Tokens = new List<(string address, string owner, string type, bool isGroup)>();
        await using (var cmd = new NpgsqlCommand(v2Erc1155Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", lowerAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var tokenAddr = reader.GetString(0);
                var owner = reader.IsDBNull(1) ? tokenAddr : reader.GetString(1);
                var tokenType = reader.GetString(2);
                var isGroup = reader.GetBoolean(3);
                erc1155Tokens.Add((tokenAddr, owner, tokenType, isGroup));
            }
        }

        // Fetch live balances via balanceOfBatch for ERC-1155 tokens
        if (erc1155Tokens.Count > 0)
        {
            const string hubAddress = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8"; // Gnosis Chain V2 Hub

            try
            {
                var tokenIds = erc1155Tokens.Select(t => AddressToTokenIdBigInt(t.address)).ToArray();
                var owners = Enumerable.Repeat(lowerAddress, erc1155Tokens.Count).ToArray();

                var data = AbiEncoder.EncodeBalanceOfBatch(owners, tokenIds);
                var resultHex = await _nethermindRpcClient!.EthCall(hubAddress, data);
                var balances = AbiEncoder.DecodeUint256Array(resultHex);

                for (int i = 0; i < balances.Length; i++)
                {
                    if (balances[i] > 0)
                    {
                        var (tokenAddress, owner, tokenType, isGroup) = erc1155Tokens[i];

                        // V2 ERC-1155 tokens are demurraged (attoCircles)
                        var attoCircles = balances[i];
                        var circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                        var staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                        var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                        var attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                        var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to fetch ERC-1155 balances: {ex.Message}");
            }
        }

        // ===== V2 ERC-20 Wrapped Token Balances (Live) =====
        const string v2Erc20Sql = @"
            SELECT DISTINCT
                t.""tokenAddress"",
                wd.avatar as owner,
                wd.""circlesType""
            FROM ""CrcV2_Erc20WrapperTransfer"" t
            JOIN ""CrcV2_ERC20WrapperDeployed"" wd ON wd.""erc20Wrapper"" = t.""tokenAddress""
            WHERE t.""to"" = @address OR t.""from"" = @address
        ";

        var erc20Tokens = new List<(string address, string owner, int circlesType)>();
        await using (var cmd = new NpgsqlCommand(v2Erc20Sql, connection))
        {
            cmd.Parameters.AddWithValue("address", lowerAddress);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                erc20Tokens.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        // Fetch live balances for ERC-20 wrapped tokens
        foreach (var (tokenAddress, owner, circlesType) in erc20Tokens)
        {
            try
            {
                var data = AbiEncoder.EncodeBalanceOfErc20(lowerAddress);
                var resultHex = await _nethermindRpcClient!.EthCall(tokenAddress, data);
                var balance = AbiEncoder.DecodeUint256(resultHex);

                if (balance > 0)
                {
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
                        staticAttoCircles = balance;
                        attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                        attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                    }
                    else
                    {
                        // Demurraged wrapped tokens store attoCircles
                        attoCircles = balance;
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
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to fetch balance for ERC-20 token {tokenAddress}: {ex.Message}");
            }
        }

        // Order by circles value (descending)
        return tokenBalances
            .OrderByDescending(b => b.Circles)
            .ToArray();
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
                    Token: reader.GetString(0),
                    TokenOwner: reader.GetString(1),
                    Version: 1,
                    Type: "Avatar",
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
                    Token: reader.GetString(0),
                    TokenOwner: reader.GetString(0), // For V2 avatars, the token and owner are the same
                    Version: 2,
                    Type: "Avatar",
                    IsErc20: false,
                    IsErc1155: true,
                    IsWrapped: false,
                    IsInflationary: false,
                    IsGroup: isGroup
                );
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
                    return new TokenInfo(
                        Token: reader.GetString(0),
                        TokenOwner: reader.GetString(1),
                        Version: 2,
                        Type: "ERC20",
                        IsErc20: true,
                        IsErc1155: false,
                        IsWrapped: true,
                        IsInflationary: false,
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
                    profileDict["address"] = JsonSerializer.SerializeToElement(addr);
                    if (hasShortName) profileDict["shortName"] = JsonSerializer.SerializeToElement(shortName);
                    // Note: avatarType and CID are NOT included to match remote implementation

                    result[i] = JsonSerializer.SerializeToElement(profileDict);
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
            return new EventsResponse(Events: Array.Empty<object>());
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
            return new EventsResponse(Events: Array.Empty<object>());
        }

        var finalSql = string.Join(" UNION ALL ", queries);
        var sortOrder = sortAscending == true ? "ASC" : "DESC";
        finalSql += $" ORDER BY \"blockNumber\" {sortOrder}, \"transactionIndex\" {sortOrder}, \"logIndex\" {sortOrder} LIMIT 1000";

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
                // Convert numeric fields to hex format for compatibility with remote
                var values = new Dictionary<string, object?>();
                foreach (var kvp in payloadDict)
                {
                    var key = kvp.Key;
                    var value = kvp.Value;

                    // Convert numeric types to hex strings
                    if (key == "blockNumber" || key == "timestamp" || key == "transactionIndex" || key == "logIndex")
                    {
                        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numValue))
                        {
                            values[key] = "0x" + numValue.ToString("x");
                        }
                        else
                        {
                            values[key] = value.ToString();
                        }
                    }
                    else if (value.ValueKind == JsonValueKind.String)
                    {
                        values[key] = value.GetString();
                    }
                    else if (value.ValueKind == JsonValueKind.Number)
                    {
                        // Keep other numbers as strings
                        values[key] = value.ToString();
                    }
                    else
                    {
                        values[key] = JsonSerializer.Deserialize<object>(value.GetRawText());
                    }
                }

                events.Add(new
                {
                    @event = reader.GetString(4),
                    values = values
                });
            }
        }
        return new EventsResponse(Events: events.ToArray());
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

    public async Task<TablesResponse> GetTables()
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
        var schemas = namespaces.Select(kvp => new TableSchema(Name: kvp.Key, Tables: kvp.Value.ToArray())).ToArray();
        return new TablesResponse(Namespaces: schemas);
    }

    /// <summary>
    /// Validates that an identifier contains only safe characters (letters, digits, underscore).
    /// </summary>
    private static string ValidateIdentifier(string identifier, string identifierType)
    {
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