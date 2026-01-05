using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Circles.Common;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// ILoadGraph implementation that uses the test-env query proxy API
/// instead of direct PostgreSQL connections. This enables pathfinder tests
/// to run against staging.circlesubi.network/test-env without requiring
/// local database access.
/// </summary>
public class ProxyLoadGraph : ILoadGraph
{
    private readonly TestEnvironmentClient _client;
    private readonly Settings _settings;

    // SQL queries embedded as constants (same as in LoadGraph but we can't use embedded resources from test assembly)
    private const string BalanceQuery = """
        with static_token_transfers as (
            select t."blockNumber"
                 , t."timestamp"
                 , t."transactionIndex"
                 , t."logIndex"
                 , t."transactionHash"
                 , t."tokenAddress"
                 , t."from"
                 , t."to"
                 , t."amount"
            from "CrcV2_Erc20WrapperTransfer" t
                     join "CrcV2_ERC20WrapperDeployed" d on d."circlesType" = 1 and d."erc20Wrapper" = t."tokenAddress"
            order by t."blockNumber", t."transactionIndex", t."logIndex"
        ), static_from_transfers as (
            select t1."timestamp"
                 , t1."tokenAddress"
                 , t1."from" as "account"
                 , -t1."amount" as diff
            from static_token_transfers t1
                     inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."from"
        ), static_to_transfers as (
            select t1."timestamp"
                 , t1."tokenAddress"
                 , t1."to" as "account"
                 , t1."amount" as diff
            from static_token_transfers t1
                     inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."to"
        ), static_sum as (
            select sum(diff) AS static_balance
                 , account
                 , "tokenAddress"
                 , max("timestamp") AS "timestamp"
                 , true as "isWrapped"
                 , 'static' as "circlesType"
            from (
                     select *
                     from static_from_transfers
                     union all
                     select *
                     from static_to_transfers
                 ) as t
            group by account
                   , "tokenAddress"
        ),

        demurraged_wrapped_token_transfers as (
            select t."blockNumber"
                 , t."timestamp"
                 , t."transactionIndex"
                 , t."logIndex"
                 , t."transactionHash"
                 , t."tokenAddress"
                 , t."from"
                 , t."to"
                 , t."amount"
            from "CrcV2_Erc20WrapperTransfer" t
                     join "CrcV2_ERC20WrapperDeployed" d on d."circlesType" = 0 and d."erc20Wrapper" = t."tokenAddress"
            order by t."blockNumber", t."transactionIndex", t."logIndex"
        ), demurraged_wrapped_from_transfers as (
            select t1."timestamp"
                 , t1."tokenAddress"
                 , t1."from" as "account"
                 , -t1."amount" as diff
            from demurraged_wrapped_token_transfers t1
                     inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."from"
        ), demurraged_wrapped_to_transfers as (
            select t1."timestamp"
                 , t1."tokenAddress"
                 , t1."to" as "account"
                 , t1."amount" as diff
            from demurraged_wrapped_token_transfers t1
                     inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."to"
        ), demurraged_wrapped_sum as (
            select floor(crc_demurrage(1675209600::bigint, max("timestamp"), sum(diff))) AS demurraged_balance
                 , account
                 , "tokenAddress"
                 , max("timestamp") AS "timestamp"
                 , true as "isWrapped"
                 , 'demurraged' as "circlesType"
            from (
                     select *
                     from demurraged_wrapped_from_transfers
                     union all
                     select *
                     from demurraged_wrapped_to_transfers
                 ) as t
            group by account
                   , "tokenAddress"
        ),

        all_transfers as (
            select "static_balance" as balance
                 , "account"
                 , "tokenAddress"
                 , "isWrapped"
                 , "circlesType"
            from static_sum
            union all
            select "demurraged_balance" as balance
                 , "account"
                 , "tokenAddress"
                 , "isWrapped"
                 , "circlesType"
            from demurraged_wrapped_sum
            union all
            select
                "demurragedTotalBalance" as balance
                 ,"account"
                 ,"tokenAddress"
                 ,false AS "isWrapped"
                 ,'demurraged' AS "circlesType"
            from "V_CrcV2_BalancesByAccountAndToken"
        )

        select balance::text
             , account
             , "tokenAddress"
             , "isWrapped"
             , "circlesType"
        from all_transfers
        where balance > 0
        order by balance, account, "tokenAddress"
        """;

    private const string TrustQuery = """
        SELECT t1.truster, t1.trustee FROM "V_CrcV2_TrustRelations" t1
        LEFT JOIN "CrcV2_RegisterGroup" t2 on t2."group" = t1.truster
        WHERE t2."group"  IS NULL

        UNION ALL

        SELECT t1.truster, t2."erc20Wrapper" AS trustee FROM "V_CrcV2_TrustRelations" t1
        INNER JOIN "CrcV2_ERC20WrapperDeployed" t2
        ON t2.avatar = t1.trustee
        LEFT JOIN "CrcV2_RegisterGroup" t3 on t3."group" = t1.truster
        WHERE t3."group"  IS NULL
        """;

    private const string GroupQuery = """
        SELECT
            "group" as group_address
        FROM "CrcV2_RegisterGroup"
        WHERE "mint" = LOWER('0xCDFc5135AEC0aFbf102C108e7f5C8A88C6112842')
        """;

    private const string GroupTrustQuery = """
        SELECT
            t.truster as group_address,
            t.trustee as trusted_token
        FROM "V_CrcV2_TrustRelations" t
        INNER JOIN "CrcV2_RegisterGroup" g ON g."group" = t.truster
        WHERE g."mint" = LOWER('0xCDFc5135AEC0aFbf102C108e7f5C8A88C6112842')
        """;

    private const string ConsentedFlowQuery = """
        SELECT DISTINCT ON (avatar) avatar, flag
        FROM "CrcV2_SetAdvancedUsageFlag"
        ORDER BY avatar, "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        """;

    public ProxyLoadGraph(TestEnvironmentClient client, Settings settings)
    {
        _client = client;
        _settings = settings;
    }

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        var response = _client.ExecuteQueryAsync(BalanceQuery, maxRows: 100000).GetAwaiter().GetResult();
        var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>();

        Console.WriteLine($"ProxyLoadGraph: Balance query returned {response.RowCount} rows");

        foreach (var row in response.Rows)
        {
            var balance = GetString(row[0]);
            var account = GetString(row[1]);
            var tokenAddress = GetString(row[2]);
            var isWrapped = GetBool(row[3]);
            var type = GetString(row[4]);

            if (type == "static")
            {
                // Convert to Circles (same logic as LoadGraph)
                var staticAttoCircles = BigInteger.Parse(balance);
                var demurragedAttoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                if (demurragedAttoCircles == 0)
                {
                    continue;
                }

                balance = demurragedAttoCircles.ToString(CultureInfo.InvariantCulture);
            }

            if (balance == "0")
            {
                continue;
            }

            results.Add((balance,
                AddressIdPool.IdOf(account.ToLowerInvariant()),
                AddressIdPool.IdOf(tokenAddress.ToLowerInvariant()),
                isWrapped,
                type == "static"));
        }

        return results;
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        var response = _client.ExecuteQueryAsync(TrustQuery, maxRows: 500000).GetAwaiter().GetResult();
        var results = new List<(string Truster, string Trustee, int Limit)>();

        Console.WriteLine($"ProxyLoadGraph: Trust query returned {response.RowCount} rows");

        foreach (var row in response.Rows)
        {
            var truster = GetString(row[0]);
            var trustee = GetString(row[1]);
            results.Add((truster, trustee, 100));
        }

        return results;
    }

    public IEnumerable<string> LoadGroups()
    {
        var response = _client.ExecuteQueryAsync(GroupQuery, maxRows: 10000).GetAwaiter().GetResult();
        var results = new List<string>();

        foreach (var row in response.Rows)
        {
            var groupAddress = GetString(row[0]);
            results.Add(groupAddress);
        }

        return results;
    }

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        var response = _client.ExecuteQueryAsync(GroupTrustQuery, maxRows: 100000).GetAwaiter().GetResult();
        var results = new List<(string GroupAddress, string TrustedToken)>();

        foreach (var row in response.Rows)
        {
            var groupAddress = GetString(row[0]);
            var trustedToken = GetString(row[1]);
            results.Add((groupAddress, trustedToken));
        }

        return results;
    }

    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
    {
        var response = _client.ExecuteQueryAsync(ConsentedFlowQuery, maxRows: 10000).GetAwaiter().GetResult();
        var results = new List<(string Avatar, bool HasConsentedFlow)>();

        foreach (var row in response.Rows)
        {
            var avatar = GetString(row[0]);
            var flag = GetBytes(row[1]);

            // Decode the consented flow flag from Hub.sol (same logic as LoadGraph):
            // bytes32(uint256(1)) creates a 32-byte array where byte[31] & 0x01 indicates consent
            bool hasConsented = flag.Length >= 32 && (flag[31] & 0x01) != 0;
            results.Add((avatar, hasConsented));
        }

        return results;
    }

    // Helper methods to extract values from JsonElement or raw objects
    private static string GetString(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString();
        }

        return value?.ToString() ?? "";
    }

    private static bool GetBool(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.True ||
                   (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var b) && b);
        }

        if (value is bool b2) return b2;
        return bool.TryParse(value?.ToString(), out var result) && result;
    }

    private static byte[] GetBytes(object? value)
    {
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                var hex = je.GetString();
                if (!string.IsNullOrEmpty(hex))
                {
                    // Handle hex string like "0x..." or "\x..."
                    if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        hex = hex[2..];
                    }
                    else if (hex.StartsWith("\\x"))
                    {
                        // PostgreSQL bytea format: \x followed by hex
                        hex = hex[2..];
                    }

                    // Try to parse as hex, return empty array if it fails
                    try
                    {
                        return Convert.FromHexString(hex);
                    }
                    catch (FormatException)
                    {
                        // Maybe it's base64 encoded?
                        try
                        {
                            return Convert.FromBase64String(hex);
                        }
                        catch
                        {
                            // Unknown format
                            Console.WriteLine($"Warning: Unknown bytes format: {hex[..Math.Min(100, hex.Length)]}");
                            return [];
                        }
                    }
                }
            }

            // Handle JSON array of bytes
            if (je.ValueKind == JsonValueKind.Array)
            {
                var bytes = new byte[je.GetArrayLength()];
                var i = 0;
                foreach (var b in je.EnumerateArray())
                {
                    bytes[i++] = b.GetByte();
                }

                return bytes;
            }
        }

        if (value is byte[] ba) return ba;
        return [];
    }
}
