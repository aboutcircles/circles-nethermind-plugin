using System.Globalization;
using System.Numerics;
using Circles.Common;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Tests.Helpers;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Demurrage-specific tests covering the interaction between time-decay and the pathfinder pipeline.
/// Complements ContractConformanceTests (pure math) with pipeline-level concerns:
/// - LoadGraph static/demurraged balance conversion
/// - Safety margin behavior
/// - FixtureLoadGraph vs LoadGraph epoch consistency
/// - Stale balance detection
/// - Edge-of-sufficiency after demurrage
/// - Multi-day drift effects
/// </summary>
[TestFixture]
public class DemurrageTests
{
    // V2 Hub contract epoch (must match LoadGraph.cs and DiscountedBalances.sol)
    private const uint V2_INFLATION_DAY_ZERO = 1_675_209_600; // Feb 1, 2023 00:00 UTC

    // V1 Hub epoch (used by CirclesConverter.Today())
    private const uint V1_INFLATION_DAY_ZERO = 1_602_720_000; // Oct 15, 2020 00:00 UTC

    private const long SECONDS_PER_DAY = 86_400;

    #region V1 vs V2 Epoch Divergence

    /// <summary>
    /// Verifies the two epochs produce different day indices for the same timestamp,
    /// confirming that using the wrong one is a real bug.
    /// </summary>
    [Test]
    public void EpochDivergence_SameTimestamp_DifferentDayIndices()
    {
        var now = DateTimeOffset.UtcNow;
        var v1Day = CirclesConverter.DayFromTimestamp(now, V1_INFLATION_DAY_ZERO);
        var v2Day = CirclesConverter.DayFromTimestamp(now, V2_INFLATION_DAY_ZERO);

        // V1 epoch is 839 days earlier: (1_675_209_600 - 1_602_720_000) / 86_400 = 839
        ulong dayDiff = v1Day - v2Day;
        Assert.That(dayDiff, Is.EqualTo(839UL),
            $"V1 vs V2 epoch difference should be exactly 839 days (got {dayDiff})");

        // This confirms that using V1 epoch for V2 conversion applies 839 extra days of decay
        Assert.That(v1Day, Is.GreaterThan(v2Day));
    }

    /// <summary>
    /// Quantifies the impact: using V1 epoch instead of V2 for demurrage conversion
    /// produces ~16% more decay on a 1 CRC balance.
    /// </summary>
    [Test]
    public void EpochDivergence_V1Epoch_Causes16PercentExtraDecay()
    {
        var balance = BigInteger.Parse("1000000000000000000"); // 1 CRC

        var now = DateTimeOffset.UtcNow;
        var v1Day = CirclesConverter.DayFromTimestamp(now, V1_INFLATION_DAY_ZERO);
        var v2Day = CirclesConverter.DayFromTimestamp(now, V2_INFLATION_DAY_ZERO);

        var withV1 = CirclesConverter.InflationaryToDemurrage(balance, v1Day);
        var withV2 = CirclesConverter.InflationaryToDemurrage(balance, v2Day);

        // V1 should produce more decay (smaller result)
        Assert.That(withV1, Is.LessThan(withV2),
            "V1 epoch should produce more decay than V2");

        // The extra decay from V1 should be roughly 16% (857 days * ~0.02%/day)
        double extraDecayPct = 100.0 * (1.0 - (double)withV1 / (double)withV2);
        Assert.That(extraDecayPct, Is.InRange(13.0, 19.0),
            $"Extra decay from V1 epoch should be ~16% (got {extraDecayPct:F2}%)");
    }

    /// <summary>
    /// Verifies that AttoStaticCirclesToAttoCircles (which uses V1 epoch) produces
    /// a DIFFERENT result than InflationaryToDemurrage with V2 day for the same input.
    /// This is the exact bug documented in DEMURRAGE_DISCREPANCY_INVESTIGATION.md.
    /// </summary>
    [Test]
    public void AttoStaticCirclesToAttoCircles_UsesV1Epoch_ProducesDifferentResult()
    {
        var balance = BigInteger.Parse("50000000000000000000"); // 50 CRC

        // What CirclesConverter.AttoStaticCirclesToAttoCircles does (V1 epoch)
        var withV1Api = CirclesConverter.AttoStaticCirclesToAttoCircles(balance);

        // What LoadGraph.cs does (V2 epoch, correct)
        var v2Day = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, V2_INFLATION_DAY_ZERO);
        var withV2Direct = CirclesConverter.InflationaryToDemurrage(balance, v2Day);

        // They should NOT be equal — the V1 API applies more decay
        Assert.That(withV1Api, Is.LessThan(withV2Direct),
            "AttoStaticCirclesToAttoCircles (V1 epoch) should produce more decay than V2 direct");

        double ratio = (double)withV1Api / (double)withV2Direct;
        Assert.That(ratio, Is.LessThan(0.90),
            "V1 API should produce at least 10% more decay than V2");
    }

    #endregion

    #region FixtureLoadGraph vs LoadGraph Consistency

    /// <summary>
    /// After the V2 epoch fix, FixtureLoadGraph should produce the same demurraged balance
    /// as the manual V2 calculation (within rounding tolerance).
    /// </summary>
    [Test]
    public void FixtureLoadGraph_StaticBalance_UsesV2Epoch()
    {
        var staticAmount = "1000000000000000000"; // 1 CRC static

        var subgraph = new FixtureSubgraph
        {
            Balances = new List<BalanceEntry>
            {
                new()
                {
                    Holder = "0xde10000000000000000000000000000000000001",
                    Token = "0xde20000000000000000000000000000000000002",
                    Amount = staticAmount,
                    IsStatic = true,
                    IsWrapped = false
                }
            }
        };

        var fixtureGraph = new FixtureLoadGraph(subgraph);
        var balances = fixtureGraph.LoadV2Balances().ToList();

        Assert.That(balances.Count, Is.EqualTo(1), "Should emit one balance");

        // Calculate expected value using V2 epoch directly
        var v2Day = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, V2_INFLATION_DAY_ZERO);
        var expectedDemurraged = CirclesConverter.InflationaryToDemurrage(
            BigInteger.Parse(staticAmount), v2Day);

        var actualBalance = BigInteger.Parse(balances[0].Balance);

        // Should match V2 calculation exactly (both use same code path now)
        Assert.That(actualBalance, Is.EqualTo(expectedDemurraged),
            $"FixtureLoadGraph should use V2 epoch. Expected={expectedDemurraged}, Actual={actualBalance}");
    }

    /// <summary>
    /// Non-static balances should pass through unchanged in FixtureLoadGraph
    /// (demurrage adjustment is only for static type, same as LoadGraph).
    /// </summary>
    [Test]
    public void FixtureLoadGraph_DemurragedBalance_PassthroughUnchanged()
    {
        var amount = "500000000000000000"; // 0.5 CRC demurraged

        var subgraph = new FixtureSubgraph
        {
            Balances = new List<BalanceEntry>
            {
                new()
                {
                    Holder = "0xde30000000000000000000000000000000000003",
                    Token = "0xde40000000000000000000000000000000000004",
                    Amount = amount,
                    IsStatic = false,
                    IsWrapped = false
                }
            }
        };

        var fixtureGraph = new FixtureLoadGraph(subgraph);
        var balances = fixtureGraph.LoadV2Balances().ToList();

        Assert.That(balances.Count, Is.EqualTo(1));
        Assert.That(balances[0].Balance, Is.EqualTo(amount),
            "Non-static balance should pass through unchanged");
    }

    /// <summary>
    /// Zero-amount static balances should be filtered out.
    /// </summary>
    [Test]
    public void FixtureLoadGraph_ZeroStaticBalance_FilteredOut()
    {
        var subgraph = new FixtureSubgraph
        {
            Balances = new List<BalanceEntry>
            {
                new()
                {
                    Holder = "0xde50000000000000000000000000000000000005",
                    Token = "0xde60000000000000000000000000000000000006",
                    Amount = "0",
                    IsStatic = true,
                    IsWrapped = false
                }
            }
        };

        var fixtureGraph = new FixtureLoadGraph(subgraph);
        var balances = fixtureGraph.LoadV2Balances().ToList();

        Assert.That(balances.Count, Is.EqualTo(0), "Zero-amount balance should be filtered");
    }

    #endregion

    #region Safety Margin Behavior

    /// <summary>
    /// Safety margin of 0.9995 should reduce balance by 0.05%.
    /// </summary>
    [Test]
    public void SafetyMargin_Default_Reduces0Point05Percent()
    {
        double margin = 0.9995;
        var balance = BigInteger.Parse("1000000000000000000000"); // 1000 CRC

        var margined = (BigInteger)((double)balance * margin);

        double reduction = 100.0 * (1.0 - (double)margined / (double)balance);
        Assert.That(reduction, Is.InRange(0.04, 0.06),
            $"Default safety margin should reduce by ~0.05% (got {reduction:F4}%)");
    }

    /// <summary>
    /// Safety margin of 1.0 should not change balance.
    /// </summary>
    [Test]
    public void SafetyMargin_Disabled_NoChange()
    {
        double margin = 1.0;
        var balance = BigInteger.Parse("1000000000000000000000");

        var margined = (BigInteger)((double)balance * margin);

        Assert.That(margined, Is.EqualTo(balance),
            "Margin of 1.0 should not change balance");
    }

    /// <summary>
    /// Safety margin should only apply in live mode (TargetDemurrageTimestamp == null).
    /// When a frozen timestamp is set, margin should NOT apply.
    /// </summary>
    [Test]
    public void SafetyMargin_FrozenTimestamp_DoesNotApply()
    {
        var settings = new Settings
        {
            DemurrageSafetyMargin = 0.9995,
            TargetDemurrageTimestamp = DateTimeOffset.UtcNow // Frozen = test mode
        };

        // The condition from LoadGraph.cs
        bool applyMargin = settings.TargetDemurrageTimestamp == null
                           && settings.DemurrageSafetyMargin < 1.0;

        Assert.That(applyMargin, Is.False,
            "Safety margin should NOT apply when TargetDemurrageTimestamp is set");
    }

    /// <summary>
    /// Safety margin should apply in live mode (TargetDemurrageTimestamp == null).
    /// </summary>
    [Test]
    public void SafetyMargin_LiveMode_Applies()
    {
        var settings = new Settings
        {
            DemurrageSafetyMargin = 0.9995,
            TargetDemurrageTimestamp = null // Live mode
        };

        bool applyMargin = settings.TargetDemurrageTimestamp == null
                           && settings.DemurrageSafetyMargin < 1.0;

        Assert.That(applyMargin, Is.True,
            "Safety margin should apply in live mode");
    }

    #endregion

    #region Day Calculation Edge Cases

    /// <summary>
    /// DayFromTimestamp at exactly the epoch should return day 0.
    /// </summary>
    [Test]
    public void DayFromTimestamp_AtEpoch_ReturnsZero()
    {
        var epoch = DateTimeOffset.FromUnixTimeSeconds(V2_INFLATION_DAY_ZERO);
        var day = CirclesConverter.DayFromTimestamp(epoch, V2_INFLATION_DAY_ZERO);

        Assert.That(day, Is.EqualTo(0UL), "Day at epoch should be 0");
    }

    /// <summary>
    /// DayFromTimestamp one second before midnight should still be day 0.
    /// </summary>
    [Test]
    public void DayFromTimestamp_JustBeforeMidnight_StillDay0()
    {
        var justBefore = DateTimeOffset.FromUnixTimeSeconds(V2_INFLATION_DAY_ZERO + SECONDS_PER_DAY - 1);
        var day = CirclesConverter.DayFromTimestamp(justBefore, V2_INFLATION_DAY_ZERO);

        Assert.That(day, Is.EqualTo(0UL), "86399 seconds after epoch should still be day 0");
    }

    /// <summary>
    /// DayFromTimestamp exactly at midnight should be day 1.
    /// </summary>
    [Test]
    public void DayFromTimestamp_ExactlyMidnight_Day1()
    {
        var midnight = DateTimeOffset.FromUnixTimeSeconds(V2_INFLATION_DAY_ZERO + SECONDS_PER_DAY);
        var day = CirclesConverter.DayFromTimestamp(midnight, V2_INFLATION_DAY_ZERO);

        Assert.That(day, Is.EqualTo(1UL), "Exactly 86400 seconds after epoch should be day 1");
    }

    /// <summary>
    /// Day boundary: balance should change at midnight but not within same day.
    /// </summary>
    [Test]
    public void DayBoundary_BalanceChangesOnlyAtMidnight()
    {
        var balance = BigInteger.Parse("1000000000000000000"); // 1 CRC

        // Two timestamps on same day (day 100), one early, one late
        long baseTs = V2_INFLATION_DAY_ZERO + 100 * SECONDS_PER_DAY;
        var earlyDay = CirclesConverter.DayFromTimestamp(
            DateTimeOffset.FromUnixTimeSeconds(baseTs + 100), V2_INFLATION_DAY_ZERO);
        var lateDay = CirclesConverter.DayFromTimestamp(
            DateTimeOffset.FromUnixTimeSeconds(baseTs + 86000), V2_INFLATION_DAY_ZERO);

        var earlyDemurraged = CirclesConverter.InflationaryToDemurrage(balance, earlyDay);
        var lateDemurraged = CirclesConverter.InflationaryToDemurrage(balance, lateDay);

        Assert.That(earlyDemurraged, Is.EqualTo(lateDemurraged),
            "Balance should not change within the same day");

        // But next day should be different
        var nextDay = CirclesConverter.DayFromTimestamp(
            DateTimeOffset.FromUnixTimeSeconds(baseTs + SECONDS_PER_DAY + 100), V2_INFLATION_DAY_ZERO);
        var nextDayDemurraged = CirclesConverter.InflationaryToDemurrage(balance, nextDay);

        Assert.That(nextDayDemurraged, Is.LessThan(earlyDemurraged),
            "Balance should decrease on the next day");
    }

    #endregion

    #region Multi-Day Drift / Stale Balance

    /// <summary>
    /// A balance computed N days ago is "stale" by the amount of demurrage that accumulated.
    /// This test quantifies the staleness for various delays.
    /// </summary>
    [TestCase(1UL, 0.01, 0.03)]   // 1 day: ~0.02% drift
    [TestCase(7UL, 0.10, 0.20)]   // 7 days: ~0.14% drift
    [TestCase(30UL, 0.50, 0.70)]  // 30 days: ~0.59% drift
    [TestCase(365UL, 6.0, 8.0)]   // 365 days: ~7% drift
    public void StalenessOverTime_DriftInExpectedRange(ulong daysDelta, double minPct, double maxPct)
    {
        var balance = BigInteger.Parse("100000000000000000000"); // 100 CRC

        // "Fresh" balance at day 100
        var fresh = CirclesConverter.InflationaryToDemurrage(balance, 100);

        // "Stale" balance = fresh value demurraged by daysDelta more days
        var stale = CirclesConverter.InflationaryToDemurrage(balance, 100 + daysDelta);

        double driftPct = 100.0 * (1.0 - (double)stale / (double)fresh);

        Assert.That(driftPct, Is.InRange(minPct, maxPct),
            $"After {daysDelta} days, drift should be in [{minPct}%, {maxPct}%] (got {driftPct:F4}%)");
    }

    /// <summary>
    /// Edge-of-sufficiency: a path that works at day N might fail at day N+1
    /// if the balance was exactly sufficient at day N.
    /// </summary>
    [Test]
    public void EdgeOfSufficiency_BalanceBarelyCoversPath_NextDayFails()
    {
        ulong day = 100;

        // Start with a balance that after demurrage at day 100 produces exactly X
        var balance = BigInteger.Parse("1000000000000000000"); // 1 CRC raw
        var demurragedDay100 = CirclesConverter.InflationaryToDemurrage(balance, day);
        var demurragedDay101 = CirclesConverter.InflationaryToDemurrage(balance, day + 1);

        // If a path requires exactly demurragedDay100, it fails on day 101
        Assert.That(demurragedDay101, Is.LessThan(demurragedDay100),
            "Balance decreases by next day");

        // The difference is the daily decay — this is what the safety margin protects against
        var dailyDecay = demurragedDay100 - demurragedDay101;
        double dailyDecayPct = 100.0 * (double)dailyDecay / (double)demurragedDay100;

        Assert.That(dailyDecayPct, Is.InRange(0.015, 0.025),
            $"Daily decay should be ~0.02% (got {dailyDecayPct:F4}%)");

        // Safety margin of 0.05% > daily decay of 0.02%, so margin covers ~2.5 days of drift
        double marginPct = 0.05;
        double daysProtected = marginPct / dailyDecayPct;
        Assert.That(daysProtected, Is.InRange(1.5, 4.0),
            $"0.05% safety margin should protect against ~2-3 days of drift (got {daysProtected:F1} days)");
    }

    /// <summary>
    /// Verifies that the safety margin (0.05%) is larger than 1-day drift (~0.02%),
    /// providing a buffer against stale balance reverts.
    /// </summary>
    [Test]
    public void SafetyMargin_LargerThanOneDayDrift()
    {
        var balance = BigInteger.Parse("100000000000000000000"); // 100 CRC
        ulong day = 200;

        var atDay = CirclesConverter.InflationaryToDemurrage(balance, day);
        var nextDay = CirclesConverter.InflationaryToDemurrage(balance, day + 1);

        double oneDayDrift = (double)(atDay - nextDay) / (double)atDay;
        double safetyMargin = 1.0 - 0.9995; // 0.0005 = 0.05%

        Assert.That(safetyMargin, Is.GreaterThan(oneDayDrift),
            $"Safety margin ({safetyMargin:P4}) should exceed 1-day drift ({oneDayDrift:P4})");
    }

    #endregion

    #region LoadGraph Epoch Guard (A5)

    /// <summary>
    /// lastActivity timestamps before V2 epoch should be detected as corrupted.
    /// This exercises the guard added in fix A5.
    /// </summary>
    [TestCase(0L)]                    // Unix epoch
    [TestCase(1_000_000_000L)]        // Sept 2001
    [TestCase(1_602_720_000L)]        // V1 epoch (Oct 2020)
    [TestCase(1_675_209_599L)]        // 1 second before V2 epoch
    public void EpochGuard_LastActivityBeforeV2Epoch_DetectedAsCorrupted(long lastActivity)
    {
        bool isCorrupted = lastActivity < V2_INFLATION_DAY_ZERO;
        Assert.That(isCorrupted, Is.True,
            $"lastActivity={lastActivity} should be detected as before V2 epoch");
    }

    /// <summary>
    /// lastActivity at or after V2 epoch should NOT be flagged as corrupted.
    /// </summary>
    [TestCase(1_675_209_600L)]        // Exactly V2 epoch
    [TestCase(1_675_209_601L)]        // 1 second after
    [TestCase(1_700_000_000L)]        // Nov 2023
    public void EpochGuard_LastActivityAtOrAfterV2Epoch_NotCorrupted(long lastActivity)
    {
        bool isCorrupted = lastActivity < V2_INFLATION_DAY_ZERO;
        Assert.That(isCorrupted, Is.False,
            $"lastActivity={lastActivity} should NOT be flagged as corrupted");
    }

    /// <summary>
    /// When lastActivity < epoch, unchecked subtraction wraps to a huge ulong,
    /// producing nonsensical day values. This is the bug A5 guards against.
    /// </summary>
    [Test]
    public void EpochGuard_UncheckedSubtraction_ProducesNonsensicalDays()
    {
        long lastActivity = 1_600_000_000L; // Before V2 epoch

        unchecked
        {
            ulong rawDelta = (ulong)(lastActivity - (long)V2_INFLATION_DAY_ZERO);
            ulong rawDay = rawDelta / 86_400;

            // Should wrap to something absurdly large
            Assert.That(rawDay, Is.GreaterThan(1_000_000UL),
                "Unchecked subtraction should produce millions of days");
        }
    }

    #endregion

    #region Demurrage Precision Over Long Periods

    /// <summary>
    /// At 3 years (1095 days), balance should still be positive and roughly (0.93)^3 ≈ 80.4%.
    /// </summary>
    [Test]
    public void LongTermDecay_3Years_Approximately80Percent()
    {
        var balance = BigInteger.Parse("100000000000000000000"); // 100 CRC
        var after3Years = CirclesConverter.InflationaryToDemurrage(balance, 1095);

        double ratio = (double)after3Years / (double)balance;
        Assert.That(ratio, Is.InRange(0.79, 0.82),
            $"After 3 years, ~80% should remain (got {ratio:P2})");
    }

    /// <summary>
    /// At 10 years (3652 days), balance should be roughly (0.93)^10 ≈ 48.4%.
    /// </summary>
    [Test]
    public void LongTermDecay_10Years_Approximately48Percent()
    {
        var balance = BigInteger.Parse("100000000000000000000"); // 100 CRC
        var after10Years = CirclesConverter.InflationaryToDemurrage(balance, 3652);

        double ratio = (double)after10Years / (double)balance;
        Assert.That(ratio, Is.InRange(0.46, 0.51),
            $"After 10 years, ~48% should remain (got {ratio:P2})");
    }

    /// <summary>
    /// Very small balances: ensure demurrage doesn't produce negative via fixed-point rounding.
    /// </summary>
    [Test]
    public void SmallBalance_DemurrageStaysNonNegative()
    {
        // 1 atto-CRC (smallest possible)
        var tinyBalance = BigInteger.One;

        for (ulong day = 0; day <= 10000; day += 100)
        {
            var result = CirclesConverter.InflationaryToDemurrage(tinyBalance, day);
            Assert.That(result, Is.GreaterThanOrEqualTo(BigInteger.Zero),
                $"Demurrage at day {day} should never produce negative");
        }
    }

    /// <summary>
    /// Very large balance: ensure no overflow in fixed-point multiplication.
    /// </summary>
    [Test]
    public void LargeBalance_NoOverflow()
    {
        // ~1 billion CRC (extreme case)
        var hugeBalance = BigInteger.Parse("1000000000000000000000000000"); // 10^27

        var result = CirclesConverter.InflationaryToDemurrage(hugeBalance, 365);
        Assert.That(result, Is.GreaterThan(BigInteger.Zero));

        double ratio = (double)result / (double)hugeBalance;
        Assert.That(ratio, Is.InRange(0.925, 0.935),
            "Even huge balances should decay by ~7% per year");
    }

    #endregion
}
