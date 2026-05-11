using System.Globalization;
using System.Numerics;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Common;

public sealed record ScoreGroupMintLimitRow(
    string GroupAddress,
    string CollateralToken,
    string AvailableLimit);

public sealed record ScoreGroupMintLimitBaseRow(
    string GroupAddress,
    string CollateralToken,
    string Policy,
    BigInteger TreasuryBalance,
    BigInteger CurrentSupply);

public static class ScoreGroupMintLimitReader
{
    private const uint InflationDayZeroUnix = 1_602_720_000;

    private const string BaseRowsSql = """
        WITH latest_score_group AS (
            SELECT DISTINCT ON ("group")
                "group" AS group_address,
                LOWER("emitter") AS policy
            FROM "CrcV2_ScoreGroup_GroupInitialized"
            WHERE (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
              AND LOWER("emitter") = ANY(@scoreMintPolicies)
            ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
        ),
        score_groups AS (
            SELECT
                g."group" AS group_address,
                LOWER(g."treasury") AS treasury,
                LOWER(COALESCE(lsg.policy, g."mint")) AS policy
            FROM "CrcV2_RegisterGroup" g
            LEFT JOIN latest_score_group lsg ON lsg.group_address = g."group"
            WHERE (@maxBlock::bigint IS NULL OR g."blockNumber" <= @maxBlock)
              AND LOWER(g."mint") = ANY(@scoreMintPolicies)
              AND lsg.group_address IS NOT NULL
        ),
        group_tokens AS (
            SELECT
                sg.group_address,
                sg.treasury,
                sg.policy,
                t.trustee AS trusted_token
            FROM score_groups sg
            INNER JOIN "V_CrcV2_TrustRelations" t ON t.truster = sg.group_address
            INNER JOIN "V_CrcV2_Avatars" a ON a.avatar = t.trustee
        ),
        token_supply AS (
            SELECT
                b."tokenAddress",
                SUM(b."demurragedTotalBalance")::text AS current_supply
            FROM "V_CrcV2_BalancesByAccountAndToken" b
            INNER JOIN (SELECT DISTINCT trusted_token FROM group_tokens) gt
                ON gt.trusted_token = b."tokenAddress"
            GROUP BY b."tokenAddress"
        )
        SELECT
            gt.group_address,
            gt.trusted_token,
            gt.policy,
            COALESCE(tb."demurragedTotalBalance", 0)::text AS treasury_balance,
            COALESCE(ts.current_supply, '0') AS current_supply
        FROM group_tokens gt
        LEFT JOIN "V_CrcV2_BalancesByAccountAndToken" tb
            ON tb.account = gt.treasury
           AND tb."tokenAddress" = gt.trusted_token
        LEFT JOIN token_supply ts
            ON ts."tokenAddress" = gt.trusted_token;
        """;

    private const string HistoricalSql = """
        SELECT DISTINCT ON (LOWER("emitter"), "collateral")
            LOWER("emitter") AS policy,
            "collateral"::text AS collateral,
            "supply"::text AS supply,
            "day"::text AS day
        FROM "CrcV2_ScoreGroup_HistoricalSupply"
        WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
          AND (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
        ORDER BY LOWER("emitter"), "collateral", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;
        """;

    private const string PersonalSql = """
        SELECT DISTINCT ON (LOWER("emitter"), "group", "collateral")
            LOWER("emitter") AS policy,
            "group" AS group_address,
            "collateral"::text AS collateral,
            "mintedAmountOnToday"::text AS minted_amount,
            "day"::text AS day
        FROM "CrcV2_ScoreGroup_PersonalMinted"
        WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
          AND (@maxBlock::bigint IS NULL OR "blockNumber" <= @maxBlock)
        ORDER BY LOWER("emitter"), "group", "collateral", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;
        """;

    public static IReadOnlyList<ScoreGroupMintLimitRow> Read(
        NpgsqlConnection connection,
        string[] scoreMintPolicies,
        DateTimeOffset? targetTimestamp,
        double safetyMargin,
        int commandTimeoutSeconds,
        long? maxBlock = null,
        NpgsqlTransaction? transaction = null)
    {
        var policies = scoreMintPolicies
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        if (policies.Length == 0)
            return [];

        var baseRows = ReadBaseRows(connection, policies, commandTimeoutSeconds, maxBlock, transaction);
        return ReadFromBaseRows(
            connection,
            policies,
            baseRows,
            targetTimestamp,
            safetyMargin,
            commandTimeoutSeconds,
            maxBlock,
            transaction);
    }

    public static IReadOnlyList<ScoreGroupMintLimitRow> ReadFromBaseRows(
        NpgsqlConnection connection,
        string[] scoreMintPolicies,
        IEnumerable<ScoreGroupMintLimitBaseRow> baseRows,
        DateTimeOffset? targetTimestamp,
        double safetyMargin,
        int commandTimeoutSeconds,
        long? maxBlock = null,
        NpgsqlTransaction? transaction = null)
    {
        var policies = scoreMintPolicies
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        if (policies.Length == 0)
            return [];

        var historical = ReadHistorical(connection, policies, commandTimeoutSeconds, maxBlock, transaction);
        var personal = ReadPersonal(connection, policies, commandTimeoutSeconds, maxBlock, transaction);
        var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp ?? DateTimeOffset.UtcNow, InflationDayZeroUnix);
        var applyMargin = targetTimestamp == null && safetyMargin < 1.0;

        var rows = new List<ScoreGroupMintLimitRow>();
        foreach (var row in baseRows)
        {
            var group = row.GroupAddress.ToLowerInvariant();
            var collateralToken = row.CollateralToken.ToLowerInvariant();
            var policy = row.Policy.ToLowerInvariant();
            var treasuryBalance = row.TreasuryBalance;
            var currentSupply = row.CurrentSupply;
            var collateralId = AddressToTokenId(collateralToken);

            var historicalSupply = historical.TryGetValue((policy, collateralId), out var historicalState)
                ? DiscountToTargetDay(historicalState.Amount, historicalState.Day, targetDay)
                : currentSupply;

            var personalMinted = personal.TryGetValue((policy, group, collateralId), out var personalState)
                ? DiscountToTargetDay(personalState.Amount, personalState.Day, targetDay)
                : BigInteger.Zero;

            var available = historicalSupply + personalMinted - treasuryBalance;
            if (available < BigInteger.Zero)
                available = BigInteger.Zero;

            if (applyMargin && available > BigInteger.Zero)
                available = (BigInteger)((double)available * safetyMargin);

            rows.Add(new ScoreGroupMintLimitRow(
                group,
                collateralToken,
                available.ToString(CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private static IReadOnlyList<ScoreGroupMintLimitBaseRow> ReadBaseRows(
        NpgsqlConnection connection,
        string[] policies,
        int commandTimeoutSeconds,
        long? maxBlock,
        NpgsqlTransaction? transaction)
    {
        var rows = new List<ScoreGroupMintLimitBaseRow>();
        using var command = new NpgsqlCommand(BaseRowsSql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("scoreMintPolicies", policies);
        AddMaxBlockParameter(command, maxBlock);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var group = reader.GetString(0).ToLowerInvariant();
            var collateralToken = reader.GetString(1).ToLowerInvariant();
            var policy = reader.GetString(2).ToLowerInvariant();
            var treasuryBalance = ParseBigInteger(reader.GetString(3));
            var currentSupply = ParseBigInteger(reader.GetString(4));
            rows.Add(new ScoreGroupMintLimitBaseRow(
                group,
                collateralToken,
                policy,
                treasuryBalance,
                currentSupply));
        }

        return rows;
    }

    private static Dictionary<(string Policy, string Collateral), (BigInteger Amount, ulong Day)> ReadHistorical(
        NpgsqlConnection connection,
        string[] policies,
        int commandTimeoutSeconds,
        long? maxBlock,
        NpgsqlTransaction? transaction)
    {
        var result = new Dictionary<(string, string), (BigInteger, ulong)>();
        using var command = new NpgsqlCommand(HistoricalSql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("scoreMintPolicies", policies);
        AddMaxBlockParameter(command, maxBlock);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1))] = (
                ParseBigInteger(reader.GetString(2)),
                ParseUInt64(reader.GetString(3)));
        }

        return result;
    }

    private static Dictionary<(string Policy, string Group, string Collateral), (BigInteger Amount, ulong Day)> ReadPersonal(
        NpgsqlConnection connection,
        string[] policies,
        int commandTimeoutSeconds,
        long? maxBlock,
        NpgsqlTransaction? transaction)
    {
        var result = new Dictionary<(string, string, string), (BigInteger, ulong)>();
        using var command = new NpgsqlCommand(PersonalSql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("scoreMintPolicies", policies);
        AddMaxBlockParameter(command, maxBlock);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1).ToLowerInvariant(), reader.GetString(2))] = (
                ParseBigInteger(reader.GetString(3)),
                ParseUInt64(reader.GetString(4)));
        }

        return result;
    }

    private static BigInteger DiscountToTargetDay(BigInteger amount, ulong storedDay, ulong targetDay)
    {
        var delta = targetDay > storedDay ? targetDay - storedDay : 0;
        return delta == 0 ? amount : CirclesConverter.InflationaryToDemurrage(amount, delta);
    }

    private static string AddressToTokenId(string address)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (hex.Length != 40)
            throw new FormatException($"Invalid token address '{address}'.");

        return BigInteger.Parse("00" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static BigInteger ParseBigInteger(string value) =>
        BigInteger.Parse(value, CultureInfo.InvariantCulture);

    private static ulong ParseUInt64(string value) =>
        ulong.Parse(value, CultureInfo.InvariantCulture);

    private static void AddMaxBlockParameter(NpgsqlCommand command, long? maxBlock)
    {
        var parameter = command.Parameters.Add("maxBlock", NpgsqlDbType.Bigint);
        parameter.Value = (object?)maxBlock ?? DBNull.Value;
    }
}
