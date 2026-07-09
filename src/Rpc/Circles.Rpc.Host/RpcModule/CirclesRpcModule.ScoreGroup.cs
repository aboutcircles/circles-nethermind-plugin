using System.Globalization;
using System.Numerics;
using Circles.Common;
using Circles.Common.Dto;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Score-group mint-policy read endpoints exposed via JSON-RPC.
/// </summary>
public partial class CirclesRpcModule : ICirclesRpcModule
{
    public async Task<ScoreGroupMintLimitsResponse> GetScoreGroupMintLimits(
        string groupAddress,
        string? collateralToken = null)
    {
        var group = (groupAddress ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(group))
            throw new ArgumentException("groupAddress is required", nameof(groupAddress));

        var collateralFilter = string.IsNullOrWhiteSpace(collateralToken)
            ? null
            : collateralToken!.Trim().ToLowerInvariant();

        var policies = _settings.ScoreGroupMintPolicies;
        if (policies == null || policies.Length == 0)
            return new ScoreGroupMintLimitsResponse(group, Array.Empty<ScoreGroupMintLimitRpcRow>());

        await using var connection = await CreateConnectionAsync();

        // RPC pre-flight uses live time + no client-side safety margin: the on-chain policy
        // applies its own per-day demurrage in beforeMintPolicy. The SDK can subtract its own
        // safety margin if desired.
        var rows = ScoreGroupMintLimitReader.Read(
            connection,
            policies,
            targetTimestamp: null,
            safetyMargin: 1.0,
            commandTimeoutSeconds: _settings.DatabaseQueryTimeoutSeconds,
            groupAddressFilter: group,
            collateralTokenFilter: collateralFilter,
            subTreasuryOverrides: _settings.ScoreTreasurySubTreasuries);

        var rpcRows = rows
            .Select(row => new ScoreGroupMintLimitRpcRow(
                row.GroupAddress,
                row.CollateralToken,
                row.AvailableLimit))
            .ToArray();

        return new ScoreGroupMintLimitsResponse(group, rpcRows);
    }

    /// <inheritdoc />
    public async Task<MaxFlowResponse> FindScoreGroupRedeemPath(
        string group,
        string holder,
        string? amount = null)
    {
        // Validate + normalize (throws ArgumentException on malformed input → -32602, never a silent 0).
        var (groupAddress, holderAddress, requestedAmount) =
            ScoreGroupRedeemPlanner.ParseRequest(group, holder, amount);

        var empty = new MaxFlowResponse("0", new List<TransferPathStep>());

        var policies = _settings.ScoreGroupMintPolicies;
        if (policies == null || policies.Length == 0)
        {
            // Server misconfiguration, not a legitimately-empty result — make it visible rather than
            // returning a "0" that looks like a valid answer.
            _logger?.LogWarning(
                "FindScoreGroupRedeemPath: ScoreGroupMintPolicies (V2_SCORE_GROUP_MINT_POLICIES) is not configured; returning empty");
            return empty;
        }

        await using var connection = await CreateConnectionAsync();

        // Entitlement: the holder's demurraged gCRC balance (live NOW() basis), capped by `amount`.
        // Query first so a holder with nothing to redeem short-circuits before the heavier caps query.
        var holderBalance = await GetDemurragedBalanceAsync(connection, holderAddress, groupAddress);
        var entitlement = requestedAmount.HasValue
            ? BigInteger.Min(holderBalance, requestedAmount.Value)
            : holderBalance;
        if (entitlement <= BigInteger.Zero)
            return empty;

        // Per-collateral caps: demurraged ScoreTreasury.balanceOfCollateral(c) = HIGH+LOW sub-treasury
        // holdings (same NOW() demurrage basis as the entitlement, so the MIN compares like with like).
        var caps = ScoreGroupMintLimitReader.ReadTreasuryBalances(
                connection,
                policies,
                commandTimeoutSeconds: _settings.DatabaseQueryTimeoutSeconds,
                groupAddressFilter: groupAddress,
                subTreasuryOverrides: _settings.ScoreTreasurySubTreasuries)
            .Select(r => new ScoreGroupRedeemPlanner.CollateralCap(r.CollateralToken, r.TreasuryBalance))
            .ToList();

        // The treasury that returns collateral on redeem (the group's on-chain ScoreTreasury aggregator).
        // A non-empty `caps` means the group is a registered score group, so its treasury must exist;
        // if it somehow doesn't, return empty rather than fabricating a wrong `from` on the legs.
        var treasury = await GetGroupTreasuryAsync(connection, groupAddress);
        if (string.IsNullOrEmpty(treasury))
            return empty;

        return ScoreGroupRedeemPlanner.Plan(entitlement, caps, holderAddress, treasury);
    }

    /// <summary>
    /// Demurraged ERC-1155 balance of <paramref name="tokenAddress"/> held by <paramref name="account"/>
    /// (live NOW() demurrage basis via <c>V_CrcV2_BalancesByAccountAndToken</c>). Both inputs are
    /// expected pre-lowercased; the view stores lowercase addresses so the filter stays index-friendly.
    /// The view is schema-qualified as <c>public.</c> so that an <c>X-Max-Block-Number</c> header (which
    /// pins this connection's <c>search_path</c> to the <c>circles_at_block</c> twin schema) cannot
    /// silently resolve this read to a block-pinned twin while the treasury caps read live HEAD — that
    /// would make the MIN compare two different blocks. This method is intentionally HEAD-only.
    /// </summary>
    private async Task<BigInteger> GetDemurragedBalanceAsync(
        NpgsqlConnection connection,
        string account,
        string tokenAddress)
    {
        const string sql = """
            SELECT COALESCE(SUM("demurragedTotalBalance"), 0)::text
            FROM public."V_CrcV2_BalancesByAccountAndToken"
            WHERE account = @account AND "tokenAddress" = @token
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = _settings.DatabaseQueryTimeoutSeconds;
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("token", tokenAddress);
        var result = (string?)await command.ExecuteScalarAsync();
        return string.IsNullOrEmpty(result)
            ? BigInteger.Zero
            : BigInteger.Parse(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Latest-recorded treasury address (<c>CrcV2_RegisterGroup.treasury</c>) for a score group —
    /// the deployed <c>ScoreTreasury</c> aggregator. Returns null if the group is unknown.
    /// </summary>
    private async Task<string?> GetGroupTreasuryAsync(NpgsqlConnection connection, string group)
    {
        const string sql = """
            SELECT LOWER("treasury")
            FROM "CrcV2_RegisterGroup"
            WHERE LOWER("group") = @group
            ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
            LIMIT 1
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = _settings.DatabaseQueryTimeoutSeconds;
        command.Parameters.AddWithValue("group", group);
        return (string?)await command.ExecuteScalarAsync();
    }
}
