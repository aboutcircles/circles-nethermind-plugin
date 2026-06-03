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

    // V2 Hub epoch on gnosis mainnet: Hub(0x5524...).inflationDayZero() == 1_602_720_000
    private const uint InflationDayZeroUnix = 1_602_720_000;
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

    // Group query with temporal filter. Score-aware (mirrors live groupQuery.sql): includes
    // groups whose mint policy is the STANDARD policy ({1}) OR a SCORE policy ({2}) that has an
    // indexed pathMintRouter. Without the score branch, score groups never become GroupNodes and
    // the pathfinder degrades them to non-groups (no mint edges at all).
    private const string GroupQueryTemplate = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY({2})
              AND "blockNumber" <= {0}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        supported_groups AS (
            SELECT g."group" AS group_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {0}
              AND (
                  g."mint" = LOWER('{1}')
                  OR (LOWER(g."mint") = ANY({2}) AND sg.router_address IS NOT NULL)
              )
        )
        SELECT group_address FROM supported_groups
        """;

    // Group trust query with temporal filter. Score-aware: the trusting group may use the STANDARD
    // policy ({1}) or a SCORE policy ({2}) with a router. NB: unlike the live groupTrustQuery.sql this
    // omits the trustee registered-avatars filter (acceptable for current fixtures whose collateral is
    // a registered avatar; mirrors the pre-existing ProxyLoadGraph trust query). Follow-up if a fixture
    // ever needs unregistered-trustee exclusion.
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
        ), latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY({2}) AND "blockNumber" <= {0}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ), supported_groups AS (
            SELECT g."group" AS group_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {0}
              AND (
                  g."mint" = LOWER('{1}')
                  OR (LOWER(g."mint") = ANY({2}) AND sg.router_address IS NOT NULL)
              )
        )
        SELECT t.truster AS group_address, t.trustee AS trusted_token
        FROM active_trust t
        INNER JOIN supported_groups g ON g.group_address = t.truster
        """;

    private const string RegisteredAvatarsQueryTemplate = """
        SELECT avatar FROM "V_CrcV2_Avatars"
        WHERE "blockNumber" <= {0}
        """;

    // ── Score-group loaders ───────────────────────────────────────────────
    // Ported from HistoricalLoadGraph (the direct-DB loader). The proxy executes raw SQL with
    // no parameter binding, so {1}=score-mint-policy array literal, {2}=standard mint policy,
    // {3}=standard group router are inlined. All addresses come from Settings / DB rows (trusted,
    // hex-only) — not user input — so inlining carries no injection surface. Block-pinned via {0}.

    // Group → path-mint router. Score groups use their indexed pathMintRouter; standard groups
    // fall back to the standard router. Mirrors GroupRoutersQueryTemplate.
    private const string GroupRoutersQueryTemplate = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                "pathMintRouter" AS router_address
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE LOWER("emitter") = ANY({1})
              AND "blockNumber" <= {0}
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        supported_groups AS (
            SELECT g."group" AS group_address, g."mint", sg.router_address
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
            WHERE g."blockNumber" <= {0}
              AND (
                  g."mint" = LOWER('{2}')
                  OR (LOWER(g."mint") = ANY({1}) AND sg.router_address IS NOT NULL)
              )
        )
        SELECT group_address,
               CASE WHEN router_address IS NOT NULL THEN router_address ELSE LOWER('{3}') END
        FROM supported_groups
        """;

    // Score-group path-mint routers. Mirrors ScoreRoutersQueryTemplate.
    private const string ScoreRoutersQueryTemplate = """
        SELECT DISTINCT LOWER("pathMintRouter") AS router_address
        FROM "CrcV2_ScoreGroup_GroupInitialized"
        WHERE LOWER("emitter") = ANY({1}) AND "blockNumber" <= {0}
        """;

    // Latest approved operator approvals for the given accounts. Mirrors OperatorApprovalsQueryTemplate.
    // {1} = account array literal.
    private const string OperatorApprovalsQueryTemplate = """
        SELECT account, operator FROM (
            SELECT DISTINCT ON (LOWER("account"), LOWER("operator"))
                LOWER("account") AS account,
                LOWER("operator") AS operator,
                "approved" AS approved
            FROM "CrcV2_ApprovalForAll"
            WHERE LOWER("account") = ANY({1}) AND "blockNumber" <= {0}
            ORDER BY LOWER("account"), LOWER("operator"),
                     "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ) latest
        WHERE approved = true
        """;

    private const string ConsentedFlowQueryTemplate = """
        SELECT DISTINCT ON (avatar) avatar, flag
        FROM "CrcV2_SetAdvancedUsageFlag"
        WHERE "blockNumber" <= {0}
        ORDER BY avatar, "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        """;

    // Standard-only fallbacks used when no score policies are configured. These keep the exact
    // pre-score behavior (and avoid referencing CrcV2_ScoreGroup_GroupInitialized) for the other
    // suites that share ProxyLoadGraph (ScenarioTests etc.), mirroring the live loader's
    // ScoreGroupTablesAvailable guard which falls back to the standard-only *.fallback.sql.
    private const string GroupQueryStandardTemplate = """
        SELECT "group" AS group_address
        FROM "CrcV2_RegisterGroup"
        WHERE "mint" = LOWER('{1}')
          AND "blockNumber" <= {0}
        """;

    private const string GroupTrustQueryStandardTemplate = """
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
        var scoreAware = _settings.ScoreGroupMintPolicies.Length > 0;
        var query = scoreAware
            ? string.Format(GroupQueryTemplate, _client.BlockNumber,
                _settings.StandardMintPolicyAddress.ToLowerInvariant(),
                SqlArrayLiteral(_settings.ScoreGroupMintPolicies))
            : string.Format(GroupQueryStandardTemplate, _client.BlockNumber,
                _settings.StandardMintPolicyAddress.ToLowerInvariant());
        var response = _client.ExecuteQueryAsync(query, maxRows: 10000).GetAwaiter().GetResult();
        var results = new List<string>();

        foreach (var row in response.Rows)
        {
            var groupAddress = GetString(row[0]);
            results.Add(groupAddress);
        }

        return results;
    }

    public IEnumerable<string> LoadOrganizations() => [];

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        var scoreAware = _settings.ScoreGroupMintPolicies.Length > 0;
        var query = scoreAware
            ? string.Format(GroupTrustQueryTemplate, _client.BlockNumber,
                _settings.StandardMintPolicyAddress.ToLowerInvariant(),
                SqlArrayLiteral(_settings.ScoreGroupMintPolicies))
            : string.Format(GroupTrustQueryStandardTemplate, _client.BlockNumber,
                _settings.StandardMintPolicyAddress.ToLowerInvariant());
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

    /// <summary>
    /// Explicitly NOT supported via the test-env proxy — the production
    /// <c>ScoreGroupMintLimitReader</c> needs a direct NpgsqlConnection. Returns empty so the
    /// pathfinder models the score group with an UNBOUNDED mint cap. This is correct ONLY for
    /// projection fixtures whose collateral has no mint-limit row (unbounded by definition); a
    /// mint-limit-dependent case (e.g. P14 "limit reached") must NOT rely on this loader — it
    /// would pass spuriously. Overridden explicitly (rather than inheriting the silent interface
    /// default) to make the gap visible. See coverage tracker §5.E.
    /// </summary>
    public IEnumerable<(string GroupAddress, string CollateralToken, string AvailableLimit)> LoadScoreGroupMintLimits() => [];

    public IEnumerable<(string GroupAddress, string RouterAddress)> LoadGroupRouters()
    {
        if (_settings.ScoreGroupMintPolicies.Length == 0)
            return [];

        var query = string.Format(
            GroupRoutersQueryTemplate,
            _client.BlockNumber,
            SqlArrayLiteral(_settings.ScoreGroupMintPolicies),
            _settings.StandardMintPolicyAddress.ToLowerInvariant(),
            _settings.GroupRouterAddress.ToLowerInvariant());

        var response = _client.ExecuteQueryAsync(query, maxRows: 10000).GetAwaiter().GetResult();
        var results = new List<(string GroupAddress, string RouterAddress)>();
        foreach (var row in response.Rows)
            results.Add((GetString(row[0]).ToLowerInvariant(), GetString(row[1]).ToLowerInvariant()));
        return results;
    }

    public IEnumerable<string> LoadScoreRouters()
    {
        if (_settings.ScoreGroupMintPolicies.Length == 0)
            return [];

        var query = string.Format(
            ScoreRoutersQueryTemplate,
            _client.BlockNumber,
            SqlArrayLiteral(_settings.ScoreGroupMintPolicies));

        var response = _client.ExecuteQueryAsync(query, maxRows: 1000).GetAwaiter().GetResult();
        var results = new List<string>();
        foreach (var row in response.Rows)
            results.Add(GetString(row[0]).ToLowerInvariant());
        return results;
    }

    public IEnumerable<(string Account, string Operator)> LoadOperatorApprovals(IEnumerable<string> accounts)
    {
        var accountSet = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.ToLowerInvariant())
            .Distinct()
            .ToArray();
        if (accountSet.Length == 0)
            return [];

        var query = string.Format(
            OperatorApprovalsQueryTemplate,
            _client.BlockNumber,
            SqlArrayLiteral(accountSet));

        var response = _client.ExecuteQueryAsync(query, maxRows: 100000).GetAwaiter().GetResult();
        var results = new List<(string Account, string Operator)>();
        foreach (var row in response.Rows)
            results.Add((GetString(row[0]).ToLowerInvariant(), GetString(row[1]).ToLowerInvariant()));
        return results;
    }

    /// <summary>
    /// Builds a Postgres text[] array literal — ARRAY['a','b'] — from address-like strings.
    /// Values are lowercased and single quotes are doubled defensively; inputs are hex addresses
    /// from Settings / DB rows, never user input.
    /// </summary>
    private static string SqlArrayLiteral(IEnumerable<string> values)
    {
        var quoted = values
            .Select(v => $"'{v.ToLowerInvariant().Replace("'", "''")}'")
            .ToList();
        // Empty must be typed so Postgres can resolve "= ANY(...)" — ARRAY[] alone is untyped.
        return quoted.Count == 0 ? "ARRAY[]::text[]" : $"ARRAY[{string.Join(",", quoted)}]";
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

    /// <summary>
    /// Returns empty — ProxyLoadGraph uses native-only queries (no wrappers in graph).
    /// </summary>
    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, CirclesType CirclesType)> LoadWrapperMappings()
        => Array.Empty<(string, string, CirclesType)>();

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
