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

    // Demurrage constants (same as CirclesConverter)
    private const uint InflationDayZeroUnix = 1_675_209_600; // Feb 1, 2023 00:00 UTC
    private const ulong SecondsPerDay = 86_400;

    /// <summary>
    /// Maximum rows to fetch for balance queries.
    /// Configurable via PATHFINDER_MAX_BALANCE_ROWS environment variable.
    /// Default: 1,000,000 rows.
    /// </summary>
    private static readonly int MaxBalanceRows =
        int.TryParse(Environment.GetEnvironmentVariable("PATHFINDER_MAX_BALANCE_ROWS"), out var balRows)
            ? balRows
            : 1_000_000;

    /// <summary>
    /// Maximum rows to fetch for trust queries.
    /// Configurable via PATHFINDER_MAX_TRUST_ROWS environment variable.
    /// Default: 2,000,000 rows.
    /// </summary>
    private static readonly int MaxTrustRows =
        int.TryParse(Environment.GetEnvironmentVariable("PATHFINDER_MAX_TRUST_ROWS"), out var trustRows)
            ? trustRows
            : 2_000_000;

    // All queries use {0} placeholder for block number to ensure temporal consistency with Anvil forks.
    // This prevents post-fork data from leaking into the graph (see anvil-failures-2026-02-14.md).

    // Native-only balance query: uses CrcV2_TransferSingle/Batch (no ERC20 wrappers).
    // Wrapper addresses are excluded because Hub.sol operateFlowMatrix requires registered
    // avatars as flow vertices — wrapper contracts are NOT registered avatars.
    // The underlying avatar's tokens are already covered via native transfers.
    private const string BalanceQueryTemplate = """
        WITH tx AS (
            SELECT "timestamp", "from" AS account, "tokenAddress", -value AS delta
            FROM "CrcV2_TransferSingle" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", value AS delta
            FROM "CrcV2_TransferSingle" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "timestamp", "from" AS account, "tokenAddress", -value AS delta
            FROM "CrcV2_TransferBatch" WHERE "blockNumber" <= {0}
            UNION ALL
            SELECT "timestamp", "to" AS account, "tokenAddress", value AS delta
            FROM "CrcV2_TransferBatch" WHERE "blockNumber" <= {0}
        ), agg AS (
            SELECT account, "tokenAddress", sum(delta) AS balance, max("timestamp") AS last_ts
            FROM tx
            GROUP BY account, "tokenAddress"
        )
        SELECT balance::text, account, "tokenAddress", last_ts AS "lastActivity",
               false AS "isWrapped", 'demurraged' AS "circlesType"
        FROM agg
        INNER JOIN "V_CrcV2_Avatars" a ON a.avatar = agg.account
        WHERE account <> '0x0000000000000000000000000000000000000000' AND balance > 0
        ORDER BY balance, account, "tokenAddress"
        """;

    // Inlined trust view with temporal filter on trust events.
    // Uses row_number() to get latest trust event per (truster, trustee) pair at the target height.
    // Excludes ERC20 wrapper trust edges — wrapper addresses are not registered avatars
    // and Hub.sol operateFlowMatrix rejects them as flow vertices.
    private const string TrustQueryTemplate = """
        WITH trust_state AS (
            SELECT truster, trustee, "expiryTime",
                   row_number() OVER (PARTITION BY truster, trustee
                       ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC) AS rn
            FROM "CrcV2_Trust"
            WHERE "blockNumber" <= {0}
        ), active_trust AS (
            SELECT truster, trustee FROM trust_state
            WHERE rn = 1 AND "expiryTime" > (SELECT max("timestamp") FROM "System_Block" WHERE "blockNumber" <= {0})::numeric
        )
        SELECT t1.truster, t1.trustee FROM active_trust t1
        INNER JOIN "V_CrcV2_Avatars" a1 ON a1.avatar = t1.truster
        INNER JOIN "V_CrcV2_Avatars" a2 ON a2.avatar = t1.trustee
        LEFT JOIN "CrcV2_RegisterGroup" t2 ON t2."group" = t1.truster
        WHERE t2."group" IS NULL
        """;

    // Group query with temporal filter — {1} = router address from Settings.GroupRouterAddress
    private const string GroupQueryTemplate = """
        SELECT "group" AS group_address
        FROM "CrcV2_RegisterGroup"
        WHERE "mint" = LOWER('{1}')
          AND "blockNumber" <= {0}
        """;

    // Group trust query with temporal filter: uses same trust_state CTE — {1} = router address
    private const string GroupTrustQueryTemplate = """
        WITH trust_state AS (
            SELECT truster, trustee, "expiryTime",
                   row_number() OVER (PARTITION BY truster, trustee
                       ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC) AS rn
            FROM "CrcV2_Trust"
            WHERE "blockNumber" <= {0}
        ), active_trust AS (
            SELECT truster, trustee FROM trust_state
            WHERE rn = 1 AND "expiryTime" > (SELECT max("timestamp") FROM "System_Block" WHERE "blockNumber" <= {0})::numeric
        )
        SELECT t.truster AS group_address, t.trustee AS trusted_token
        FROM active_trust t
        INNER JOIN "CrcV2_RegisterGroup" g ON g."group" = t.truster
        WHERE g."mint" = LOWER('{1}')
          AND g."blockNumber" <= {0}
        """;

    private const string RegisteredAvatarsQueryTemplate = """
        SELECT avatar FROM "V_CrcV2_Avatars"
        WHERE "blockNumber" <= {0}
        """;

    private const string ConsentedFlowQueryTemplate = """
        SELECT DISTINCT ON (avatar) avatar, flag
        FROM "CrcV2_SetAdvancedUsageFlag"
        WHERE "blockNumber" <= {0}
        ORDER BY avatar, "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        """;

    public ProxyLoadGraph(TestEnvironmentClient client, Settings settings)
    {
        _client = client;
        _settings = settings;
    }

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        var query = string.Format(BalanceQueryTemplate, _client.BlockNumber);
        var response = _client.ExecuteQueryAsync(query, maxRows: MaxBalanceRows).GetAwaiter().GetResult();
        var results = new List<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>();

        Console.WriteLine($"ProxyLoadGraph: Balance query returned {response.RowCount} rows (max: {MaxBalanceRows}){(response.Truncated ? " (TRUNCATED! Consider increasing PATHFINDER_MAX_BALANCE_ROWS)" : "")}");

        // Calculate target day for demurrage (configurable for testing, defaults to NOW)
        var targetTimestamp = _settings.TargetDemurrageTimestamp ?? DateTimeOffset.UtcNow;
        var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp, InflationDayZeroUnix);

        foreach (var row in response.Rows)
        {
            var balance = GetString(row[0]);
            var account = GetString(row[1]);
            var tokenAddress = GetString(row[2]);
            var lastActivity = GetInt64(row[3]);
            var isWrapped = GetBool(row[4]);
            var type = GetString(row[5]);

            if (type == "static")
            {
                // Convert static (inflationary) Circles to demurraged Circles at target day
                var staticAttoCircles = BigInteger.Parse(balance);
                var demurragedAttoCircles = CirclesConverter.InflationaryToDemurrage(staticAttoCircles, targetDay);
                if (demurragedAttoCircles == 0)
                {
                    continue;
                }

                balance = demurragedAttoCircles.ToString(CultureInfo.InvariantCulture);
            }
            else if (type == "demurraged")
            {
                // Apply demurrage from lastActivity to target timestamp
                var inflationaryBalance = BigInteger.Parse(balance);
                var lastActivityDay = (ulong)(lastActivity - InflationDayZeroUnix) / SecondsPerDay;
                var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;

                if (daysDelta > 0)
                {
                    // Apply demurrage: balance * gamma^daysDelta
                    var demurragedBalance = CirclesConverter.InflationaryToDemurrage(inflationaryBalance, daysDelta);
                    balance = demurragedBalance.ToString(CultureInfo.InvariantCulture);
                }
                // If no delta, balance stays as-is (already in correct form)
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
        var query = string.Format(TrustQueryTemplate, _client.BlockNumber);
        var response = _client.ExecuteQueryAsync(query, maxRows: MaxTrustRows).GetAwaiter().GetResult();
        var results = new List<(string Truster, string Trustee, int Limit)>();

        Console.WriteLine($"ProxyLoadGraph: Trust query returned {response.RowCount} rows (max: {MaxTrustRows}){(response.Truncated ? " (TRUNCATED! Consider increasing PATHFINDER_MAX_TRUST_ROWS)" : "")}");

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
        var query = string.Format(GroupQueryTemplate, _client.BlockNumber, _settings.GroupRouterAddress);
        var response = _client.ExecuteQueryAsync(query, maxRows: 10000).GetAwaiter().GetResult();
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
        var query = string.Format(GroupTrustQueryTemplate, _client.BlockNumber, _settings.GroupRouterAddress);
        var response = _client.ExecuteQueryAsync(query, maxRows: 100000).GetAwaiter().GetResult();
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
        // Use block filter to get consented flow state at the session's block
        var query = string.Format(ConsentedFlowQueryTemplate, _client.BlockNumber);
        var response = _client.ExecuteQueryAsync(query, maxRows: 10000).GetAwaiter().GetResult();
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

    public IEnumerable<string> LoadRegisteredAvatars()
    {
        var query = string.Format(RegisteredAvatarsQueryTemplate, _client.BlockNumber);
        var response = _client.ExecuteQueryAsync(query, maxRows: 100000).GetAwaiter().GetResult();
        var results = new List<string>();

        foreach (var row in response.Rows)
        {
            var avatar = GetString(row[0]);
            results.Add(avatar.ToLowerInvariant());
        }

        Console.WriteLine($"ProxyLoadGraph: RegisteredAvatars query returned {results.Count} rows");
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

    private static long GetInt64(object? value)
    {
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
            {
                return je.GetInt64();
            }

            if (je.ValueKind == JsonValueKind.String && long.TryParse(je.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        if (value is long l) return l;
        if (value is int i) return i;
        return long.TryParse(value?.ToString(), out var result) ? result : 0;
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
