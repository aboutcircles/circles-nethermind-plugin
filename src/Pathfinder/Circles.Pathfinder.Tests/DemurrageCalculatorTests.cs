using System.Numerics;
using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Focused tests for <see cref="DemurrageCalculator"/> — the shared extraction
/// that replaces duplicated demurrage logic in LoadGraph and IncrementalLoadGraph.
/// </summary>
[TestFixture, Parallelizable]
public class DemurrageCalculatorTests
{
    private const uint V2_EPOCH = DemurrageCalculator.InflationDayZeroUnix;
    private const ulong SECONDS_PER_DAY = DemurrageCalculator.SecondsPerDay;

    #region CreateContext

    [Test]
    public void CreateContext_FrozenTimestamp_MarginDisabled()
    {
        var settings = new Settings
        {
            TargetDemurrageTimestamp = DateTimeOffset.FromUnixTimeSeconds(V2_EPOCH + 100 * (long)SECONDS_PER_DAY),
            DemurrageSafetyMargin = 0.9995
        };

        var ctx = DemurrageCalculator.CreateContext(settings);

        Assert.That(ctx.TargetDay, Is.EqualTo(100UL));
        Assert.That(ctx.ApplyMargin, Is.False, "Margin must not apply with frozen timestamp");
    }

    [Test]
    public void CreateContext_LiveMode_MarginEnabled()
    {
        var settings = new Settings
        {
            TargetDemurrageTimestamp = null,
            DemurrageSafetyMargin = 0.9995
        };

        var ctx = DemurrageCalculator.CreateContext(settings);

        Assert.That(ctx.TargetDay, Is.GreaterThan(0UL));
        Assert.That(ctx.ApplyMargin, Is.True, "Margin should apply in live mode");
    }

    [Test]
    public void CreateContext_MarginOne_MarginDisabledEvenInLiveMode()
    {
        var settings = new Settings
        {
            TargetDemurrageTimestamp = null,
            DemurrageSafetyMargin = 1.0
        };

        var ctx = DemurrageCalculator.CreateContext(settings);

        Assert.That(ctx.ApplyMargin, Is.False, "Margin=1.0 should disable margin");
    }

    #endregion

    #region Apply — static balances

    [Test]
    public void Apply_Static_AppliesFullDayDemurrage()
    {
        var balance = BigInteger.Parse("1000000000000000000"); // 1 CRC
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: false, SafetyMargin: 1.0);

        var result = DemurrageCalculator.Apply(balance, lastActivity: 0, isStatic: true, ctx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.LessThan(balance), "Static balance should decay");
        Assert.That(result.Value, Is.GreaterThan(BigInteger.Zero));
    }

    [Test]
    public void Apply_Static_ZeroBalance_ReturnsNull()
    {
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: false, SafetyMargin: 1.0);

        var result = DemurrageCalculator.Apply(BigInteger.Zero, lastActivity: 0, isStatic: true, ctx);

        Assert.That(result, Is.Null, "Zero balance after demurrage should return null");
    }

    #endregion

    #region Apply — demurraged balances

    [Test]
    public void Apply_Demurraged_PreEpochActivity_ReturnsNull()
    {
        var balance = BigInteger.Parse("1000000000000000000");
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: false, SafetyMargin: 1.0);

        // lastActivity before V2 epoch
        var result = DemurrageCalculator.Apply(balance, lastActivity: (long)V2_EPOCH - 1, isStatic: false, ctx);

        Assert.That(result, Is.Null, "Pre-epoch lastActivity should be rejected");
    }

    [Test]
    public void Apply_Demurraged_SameDayActivity_NoDecay()
    {
        var balance = BigInteger.Parse("1000000000000000000");
        // lastActivity on day 100
        long lastActivity = (long)V2_EPOCH + 100 * (long)SECONDS_PER_DAY;
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: false, SafetyMargin: 1.0);

        var result = DemurrageCalculator.Apply(balance, lastActivity, isStatic: false, ctx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo(balance), "Same day should produce no decay");
    }

    [Test]
    public void Apply_Demurraged_MultiDayDelta_AppliesDecay()
    {
        var balance = BigInteger.Parse("1000000000000000000");
        // lastActivity on day 50, target day 100 → 50 days of decay
        long lastActivity = (long)V2_EPOCH + 50 * (long)SECONDS_PER_DAY;
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: false, SafetyMargin: 1.0);

        var result = DemurrageCalculator.Apply(balance, lastActivity, isStatic: false, ctx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.LessThan(balance), "50 days should produce decay");
        // 50 days ≈ 1% decay
        double ratio = (double)result.Value / (double)balance;
        Assert.That(ratio, Is.InRange(0.98, 1.00));
    }

    #endregion

    #region Apply — safety margin

    [Test]
    public void Apply_WithMargin_ReducesBalance()
    {
        var balance = BigInteger.Parse("1000000000000000000");
        // Same day, no demurrage decay — only margin should reduce
        long lastActivity = (long)V2_EPOCH + 100 * (long)SECONDS_PER_DAY;
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: true, SafetyMargin: 0.9995);

        var result = DemurrageCalculator.Apply(balance, lastActivity, isStatic: false, ctx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.LessThan(balance), "Margin should reduce balance");

        double reduction = 1.0 - (double)result.Value / (double)balance;
        Assert.That(reduction, Is.InRange(0.0004, 0.0006), "~0.05% reduction expected");
    }

    [Test]
    public void Apply_WithoutMargin_NoReduction()
    {
        var balance = BigInteger.Parse("1000000000000000000");
        long lastActivity = (long)V2_EPOCH + 100 * (long)SECONDS_PER_DAY;
        var ctx = new DemurrageContext(TargetDay: 100, ApplyMargin: false, SafetyMargin: 0.9995);

        var result = DemurrageCalculator.Apply(balance, lastActivity, isStatic: false, ctx);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo(balance), "No margin should mean no reduction");
    }

    #endregion

    #region Consistency: static vs demurraged equivalence

    [Test]
    public void Apply_StaticAndDemurraged_SameResult_WhenEquivalentInputs()
    {
        // A static balance of X at targetDay d is equivalent to
        // a demurraged balance of X with lastActivity at epoch (day 0)
        var balance = BigInteger.Parse("5000000000000000000"); // 5 CRC
        var ctx = new DemurrageContext(TargetDay: 200, ApplyMargin: false, SafetyMargin: 1.0);

        var staticResult = DemurrageCalculator.Apply(balance, lastActivity: 0, isStatic: true, ctx);
        var demurragedResult = DemurrageCalculator.Apply(
            balance, lastActivity: (long)V2_EPOCH, isStatic: false, ctx);

        Assert.That(staticResult, Is.Not.Null);
        Assert.That(demurragedResult, Is.Not.Null);
        Assert.That(staticResult!.Value, Is.EqualTo(demurragedResult!.Value),
            "Static(X, day=200) should equal Demurraged(X, lastActivity=epoch, day=200)");
    }

    #endregion

    #region Bit-identical regression: old inline logic vs DemurrageCalculator

    /// <summary>
    /// Replicate the EXACT old LoadGraph.LoadV2Balances() inline demurrage logic
    /// and compare against DemurrageCalculator.Apply() for many inputs.
    /// Any bit-level difference is a regression.
    /// </summary>
    [TestCase("1000000000000000000", "static", 100L, 0L)]           // 1 CRC static, day 100
    [TestCase("50000000000000000000", "static", 365L, 0L)]          // 50 CRC static, 1 year
    [TestCase("1000000000000000000", "demurraged", 200L, 50L)]      // 1 CRC demurraged, 150 days delta
    [TestCase("999999999999999999", "demurraged", 100L, 100L)]      // no delta (same day)
    [TestCase("100000000000000000000", "demurraged", 500L, 200L)]   // 100 CRC, 300 days delta
    [TestCase("1", "static", 1000L, 0L)]                            // 1 atto, static
    [TestCase("1", "demurraged", 1000L, 0L)]                        // 1 atto, demurraged
    [TestCase("999999999999999999999999999", "static", 50L, 0L)]    // huge balance
    public void BitIdenticalRegression_OldInlineLogic_vs_DemurrageCalculator(
        string balanceStr, string type, long targetDayLong, long lastActivityDayOffset)
    {
        ulong targetDay = (ulong)targetDayLong;
        var balance = BigInteger.Parse(balanceStr);
        long lastActivity = (long)V2_EPOCH + lastActivityDayOffset * (long)SECONDS_PER_DAY;

        BigInteger? oldResult = OldInlineLogic(balance, type, targetDay, lastActivity,
            applyMargin: false, safetyMargin: 1.0);

        var ctx = new DemurrageContext(TargetDay: targetDay, ApplyMargin: false, SafetyMargin: 1.0);
        BigInteger? newResult = DemurrageCalculator.Apply(
            balance, lastActivity, isStatic: type == "static", ctx);

        Assert.That(newResult, Is.EqualTo(oldResult),
            $"Regression: type={type}, balance={balanceStr}, targetDay={targetDay}, lastActivityDay={lastActivityDayOffset}. " +
            $"Old={oldResult}, New={newResult}");
    }

    [TestCase("1000000000000000000", "demurraged", 200L, 50L, 0.9995)]
    [TestCase("50000000000000000000", "static", 365L, 0L, 0.999)]
    [TestCase("1000000000000000000", "demurraged", 100L, 100L, 0.9995)]
    public void BitIdenticalRegression_WithMargin(
        string balanceStr, string type, long targetDayLong, long lastActivityDayOffset, double margin)
    {
        ulong targetDay = (ulong)targetDayLong;
        var balance = BigInteger.Parse(balanceStr);
        long lastActivity = (long)V2_EPOCH + lastActivityDayOffset * (long)SECONDS_PER_DAY;

        BigInteger? oldResult = OldInlineLogic(balance, type, targetDay, lastActivity,
            applyMargin: true, safetyMargin: margin);

        var ctx = new DemurrageContext(TargetDay: targetDay, ApplyMargin: true, SafetyMargin: margin);
        BigInteger? newResult = DemurrageCalculator.Apply(
            balance, lastActivity, isStatic: type == "static", ctx);

        Assert.That(newResult, Is.EqualTo(oldResult),
            $"Margin regression: margin={margin}. Old={oldResult}, New={newResult}");
    }

    [TestCase("1000000000000000000", 200L, 50L)]
    [TestCase("1000000000000000000", 100L, 100L)]
    [TestCase("50000000000000000000", 500L, 200L)]
    public void BitIdenticalRegression_IncrementalLoadGraph(
        string balanceStr, long targetDayLong, long lastActivityDayOffset)
    {
        ulong targetDay = (ulong)targetDayLong;
        var balance = BigInteger.Parse(balanceStr);
        long lastActivity = (long)V2_EPOCH + lastActivityDayOffset * (long)SECONDS_PER_DAY;

        BigInteger? oldResult = OldIncrementalLogic(balance, targetDay, lastActivity,
            applyMargin: false, safetyMargin: 1.0);

        var ctx = new DemurrageContext(TargetDay: targetDay, ApplyMargin: false, SafetyMargin: 1.0);
        BigInteger? newResult = DemurrageCalculator.Apply(
            balance, lastActivity, isStatic: false, ctx);

        Assert.That(newResult, Is.EqualTo(oldResult),
            $"Incremental regression: Old={oldResult}, New={newResult}");
    }

    [TestCase("1000000000000000000", 200L, 50L, 0.9995)]
    [TestCase("50000000000000000000", 100L, 100L, 0.9995)]
    public void BitIdenticalRegression_IncrementalLoadGraph_WithMargin(
        string balanceStr, long targetDayLong, long lastActivityDayOffset, double margin)
    {
        ulong targetDay = (ulong)targetDayLong;
        var balance = BigInteger.Parse(balanceStr);
        long lastActivity = (long)V2_EPOCH + lastActivityDayOffset * (long)SECONDS_PER_DAY;

        BigInteger? oldResult = OldIncrementalLogic(balance, targetDay, lastActivity,
            applyMargin: true, safetyMargin: margin);

        var ctx = new DemurrageContext(TargetDay: targetDay, ApplyMargin: true, SafetyMargin: margin);
        BigInteger? newResult = DemurrageCalculator.Apply(
            balance, lastActivity, isStatic: false, ctx);

        Assert.That(newResult, Is.EqualTo(oldResult),
            $"Incremental margin regression: Old={oldResult}, New={newResult}");
    }

    #endregion

    #region Old logic replicas (for regression comparison)

    /// <summary>
    /// Exact replica of the OLD LoadGraph.LoadV2Balances() inline demurrage code.
    /// </summary>
    private static BigInteger? OldInlineLogic(
        BigInteger balanceValue, string type, ulong targetDay, long lastActivity,
        bool applyMargin, double safetyMargin)
    {
        const uint inflationDayZeroUnix = 1_602_720_000;
        const ulong secondsPerDay = 86_400;

        if (type == "static")
        {
            var demurragedAttoCircles = Circles.Common.CirclesConverter.InflationaryToDemurrage(balanceValue, targetDay);
            if (demurragedAttoCircles == 0) return null;
            balanceValue = demurragedAttoCircles;
        }
        else if (type == "demurraged")
        {
            if (lastActivity < inflationDayZeroUnix) return null;

            var lastActivityDay = (ulong)(lastActivity - inflationDayZeroUnix) / secondsPerDay;
            var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;

            if (daysDelta > 0)
            {
                balanceValue = Circles.Common.CirclesConverter.InflationaryToDemurrage(balanceValue, daysDelta);
            }
        }

        if (applyMargin && balanceValue != 0)
        {
            balanceValue = (BigInteger)((double)balanceValue * safetyMargin);
        }

        return balanceValue == 0 ? null : balanceValue;
    }

    /// <summary>
    /// Exact replica of the OLD IncrementalLoadGraph.LoadV2Balances() logic.
    /// Note: the old code went through string roundtrip for margin; this replica
    /// follows that exact path for bit-identical comparison.
    /// </summary>
    private static BigInteger? OldIncrementalLogic(
        BigInteger inflationaryBalance, ulong targetDay, long lastActivity,
        bool applyMargin, double safetyMargin)
    {
        const uint inflationDayZeroUnix = 1_602_720_000;
        const ulong secondsPerDay = 86_400;

        if (lastActivity < inflationDayZeroUnix) return null;

        var lastActivityDay = (ulong)(lastActivity - inflationDayZeroUnix) / secondsPerDay;
        var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;

        string balance;
        if (daysDelta > 0)
        {
            var demurragedBalance = Circles.Common.CirclesConverter.InflationaryToDemurrage(inflationaryBalance, daysDelta);
            balance = demurragedBalance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            balance = inflationaryBalance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (applyMargin && balance != "0")
        {
            if (BigInteger.TryParse(balance, out var raw))
            {
                var margined = (BigInteger)((double)raw * safetyMargin);
                balance = margined.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (balance == "0") return null;
        return BigInteger.Parse(balance);
    }

    #endregion
}
