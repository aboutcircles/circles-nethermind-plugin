using System.Numerics;
using Circles.Common;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder.Data;

/// <summary>
/// Centralised demurrage application for pathfinder balance loading.
/// Replaces the duplicated logic previously inlined in <see cref="LoadGraph"/> and
/// <see cref="IncrementalLoadGraph"/>.
/// </summary>
public static class DemurrageCalculator
{
    // V2 Hub epoch — MUST match CirclesConverter.V2_INFLATION_DAY_ZERO_UNIX
    internal const uint InflationDayZeroUnix = 1_675_209_600; // Feb 1, 2023 00:00 UTC
    internal const ulong SecondsPerDay = 86_400;

    /// <summary>
    /// Pre-compute the target day and margin flag once per graph build,
    /// then pass the result to <see cref="Apply"/> for each balance row.
    /// </summary>
    public static DemurrageContext CreateContext(Settings settings)
    {
        var targetTimestamp = settings.TargetDemurrageTimestamp ?? DateTimeOffset.UtcNow;
        var targetDay = CirclesConverter.DayFromTimestamp(targetTimestamp, InflationDayZeroUnix);
        bool applyMargin = settings.TargetDemurrageTimestamp == null
                           && settings.DemurrageSafetyMargin < 1.0;

        return new DemurrageContext(targetDay, applyMargin, settings.DemurrageSafetyMargin);
    }

    /// <summary>
    /// Apply demurrage + safety margin to a single balance row.
    /// Returns null when the result is zero (caller should skip the row).
    /// </summary>
    /// <param name="balanceValue">Raw balance from DB or in-memory state.</param>
    /// <param name="lastActivity">Unix timestamp of last balance activity (only used for demurraged type).</param>
    /// <param name="isStatic">True for inflationary ERC20 wrapper balances.</param>
    /// <param name="ctx">Pre-computed context from <see cref="CreateContext"/>.</param>
    /// <param name="logger">Optional logger for debug/warning output.</param>
    /// <param name="accountHint">Short account prefix for log messages (optional).</param>
    public static BigInteger? Apply(
        BigInteger balanceValue,
        long lastActivity,
        bool isStatic,
        DemurrageContext ctx,
        ILogger? logger = null,
        string? accountHint = null)
    {
        if (isStatic)
        {
            var demurraged = CirclesConverter.InflationaryToDemurrage(balanceValue, ctx.TargetDay);
            if (demurraged == 0) return null;

            if (logger != null && logger.IsEnabled(LogLevel.Debug) && balanceValue > 0)
            {
                var pctDelta = 100.0 * (1.0 - (double)demurraged / (double)balanceValue);
                logger.LogDebug(
                    "[Demurrage] static: acct={Account}, raw={Raw}, adj={Adjusted}, delta={Delta}%, targetDay={TargetDay}",
                    accountHint ?? "?", balanceValue, demurraged, pctDelta.ToString("F2"), ctx.TargetDay);
            }

            balanceValue = demurraged;
        }
        else
        {
            // Guard: corrupted data where lastActivity predates Circles epoch
            if (lastActivity < InflationDayZeroUnix)
            {
                logger?.LogWarning(
                    "[Demurrage] lastActivity {LastActivity} < epoch {Epoch} for acct={Account} — skipping",
                    lastActivity, InflationDayZeroUnix, accountHint ?? "?");
                return null;
            }

            var lastActivityDay = (ulong)(lastActivity - InflationDayZeroUnix) / SecondsPerDay;
            var daysDelta = ctx.TargetDay > lastActivityDay ? ctx.TargetDay - lastActivityDay : 0;

            if (daysDelta > 0)
            {
                var demurraged = CirclesConverter.InflationaryToDemurrage(balanceValue, daysDelta);

                if (logger != null && logger.IsEnabled(LogLevel.Debug) && balanceValue > 0)
                {
                    var pctDelta = 100.0 * (1.0 - (double)demurraged / (double)balanceValue);
                    logger.LogDebug(
                        "[Demurrage] flow: acct={Account}, raw={Raw}, adj={Adjusted}, delta={Delta}%, daysDiff={DaysDiff}",
                        accountHint ?? "?", balanceValue, demurraged, pctDelta.ToString("F2"), daysDelta);
                }

                balanceValue = demurraged;
            }
        }

        // Safety margin in live mode
        if (ctx.ApplyMargin && balanceValue != 0)
        {
            balanceValue = (BigInteger)((double)balanceValue * ctx.SafetyMargin);
        }

        return balanceValue == 0 ? null : balanceValue;
    }
}

/// <summary>
/// Immutable snapshot of demurrage parameters for a single graph build cycle.
/// </summary>
public readonly record struct DemurrageContext(
    ulong TargetDay,
    bool ApplyMargin,
    double SafetyMargin);
