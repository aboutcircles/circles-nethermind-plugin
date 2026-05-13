using Circles.Common;

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
            commandTimeoutSeconds: _settings.DatabaseQueryTimeoutSeconds);

        var filtered = rows
            .Where(row => string.Equals(row.GroupAddress, group, StringComparison.OrdinalIgnoreCase))
            .Where(row => collateralFilter == null
                          || string.Equals(row.CollateralToken, collateralFilter, StringComparison.OrdinalIgnoreCase))
            .Select(row => new ScoreGroupMintLimitRpcRow(
                row.GroupAddress,
                row.CollateralToken,
                row.AvailableLimit))
            .ToArray();

        return new ScoreGroupMintLimitsResponse(group, filtered);
    }
}
