using System.Numerics;
using Nethermind.Int256;

namespace Circles.Index.Common.Tests;

/// <summary>
/// Edge case tests for numeric precision in demurrage calculations and conversions.
/// These tests ensure the math library handles boundary conditions correctly.
/// </summary>
[TestFixture]
public class NumericPrecisionTests
{
    private static readonly BigInteger ONE_CRC = BigInteger.Pow(10, 18);
    private static readonly BigInteger MAX_UINT256 = BigInteger.Pow(2, 256) - 1;

    // ─────────────────────── Day 0 Edge Cases ───────────────────────────

    [Test]
    public void ApplyDemurrage_Day0_ReturnsOriginalBalance()
    {
        var balance = ONE_CRC * 1000;
        var (newBalance, discountCost) = Demurrage.ApplyDemurrage(balance, 0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(newBalance, Is.EqualTo(balance), "Balance should be unchanged at day 0");
            Assert.That(discountCost, Is.EqualTo(BigInteger.Zero), "No discount cost at day 0");
        });
    }

    [Test]
    public void ApplyDemurrage_TargetBeforeStored_ReturnsOriginalBalance()
    {
        var balance = ONE_CRC * 500;
        // Target day (5) < stored day (10) - should return original balance
        var (newBalance, discountCost) = Demurrage.ApplyDemurrage(balance, 10, 5);

        Assert.Multiple(() =>
        {
            Assert.That(newBalance, Is.EqualTo(balance), "Balance unchanged when target < stored");
            Assert.That(discountCost, Is.EqualTo(BigInteger.Zero), "No discount when target < stored");
        });
    }

    [Test]
    public void ApplyDemurrage_ZeroBalance_StaysZero()
    {
        var (newBalance, discountCost) = Demurrage.ApplyDemurrage(BigInteger.Zero, 0, 1000);

        Assert.Multiple(() =>
        {
            Assert.That(newBalance, Is.EqualTo(BigInteger.Zero));
            Assert.That(discountCost, Is.EqualTo(BigInteger.Zero));
        });
    }

    // ─────────────────────── Minimum Amount Tests ───────────────────────

    [Test]
    public void ApplyDemurrage_OneWei_PreservesPrecision()
    {
        var balance = BigInteger.One; // 1 wei
        var (newBalance, discountCost) = Demurrage.ApplyDemurrage(balance, 0, 1);

        // After 1 day of demurrage, 1 wei should still be >= 0
        Assert.That(newBalance, Is.GreaterThanOrEqualTo(BigInteger.Zero),
            "Demurrage on 1 wei should not go negative");

        // The sum should equal original
        Assert.That(newBalance + discountCost, Is.EqualTo(balance),
            "Balance + discount should equal original");
    }

    [Test]
    public void ApplyDemurrage_SmallAmount_MultipleYears()
    {
        var balance = BigInteger.Parse("1000"); // 1000 wei
        ulong daysIn10Years = 365 * 10;

        var (newBalance, discountCost) = Demurrage.ApplyDemurrage(balance, 0, daysIn10Years);

        Assert.Multiple(() =>
        {
            Assert.That(newBalance, Is.GreaterThanOrEqualTo(BigInteger.Zero),
                "Balance should not go negative");
            Assert.That(newBalance + discountCost, Is.EqualTo(balance),
                "Conservation: balance + discount = original");
        });
    }

    // ─────────────────────── Large Amount Tests ─────────────────────────

    [Test]
    public void ApplyDemurrage_MaxUInt256_DoesNotOverflow()
    {
        // Use a reasonable large balance (not max, but substantial)
        var largeBalance = BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935");

        // Should not throw - just test that calculation completes
        Assert.DoesNotThrow(() =>
        {
            var (newBalance, _) = Demurrage.ApplyDemurrage(largeBalance, 0, 365);
        }, "Demurrage on very large balance should not throw");
    }

    [Test]
    public void ApplyDemurrage_LargeAmount_ManyDays()
    {
        var balance = ONE_CRC * 1_000_000_000; // 1 billion CRC
        ulong days = 3650; // 10 years

        var (newBalance, discountCost) = Demurrage.ApplyDemurrage(balance, 0, days);

        Assert.Multiple(() =>
        {
            Assert.That(newBalance, Is.GreaterThan(BigInteger.Zero),
                "10 years of demurrage should not reduce to zero");
            Assert.That(newBalance, Is.LessThan(balance),
                "Balance should decrease over 10 years");
            Assert.That(newBalance + discountCost, Is.EqualTo(balance),
                "Conservation law");
        });
    }

    // ─────────────────────── Fixed64 Math Tests ─────────────────────────

    [Test]
    public void Fixed64_Pow_Zero_ReturnsOne()
    {
        var result = Fixed64.Pow(Fixed64.ONE, 0);
        Assert.That(result, Is.EqualTo(Fixed64.ONE), "x^0 should equal 1");
    }

    [Test]
    public void Fixed64_Pow_One_ReturnsBase()
    {
        var gamma = new BigInteger(18443079296116538654);
        var result = Fixed64.Pow(gamma, 1);
        Assert.That(result, Is.EqualTo(gamma), "x^1 should equal x");
    }

    [Test]
    public void Fixed64_MulU_Identity()
    {
        var amount = ONE_CRC * 123;
        var result = Fixed64.MulU(Fixed64.ONE, amount);
        Assert.That(result, Is.EqualTo(amount), "Multiplying by 1.0 should return original");
    }

    [Test]
    public void Fixed64_MulU_Zero()
    {
        var result = Fixed64.MulU(Fixed64.ONE, BigInteger.Zero);
        Assert.That(result, Is.EqualTo(BigInteger.Zero), "Anything * 0 = 0");
    }

    // ─────────────────────── CirclesConverter Tests ─────────────────────

    [Test]
    public void InflationaryToDemurrage_Day0_ReturnsOriginal()
    {
        var amount = ONE_CRC * 100;
        var result = CirclesConverter.InflationaryToDemurrage(amount, 0);
        Assert.That(result, Is.EqualTo(amount), "At day 0, no conversion needed");
    }

    [Test]
    public void DemurrageToInflationary_Day0_ReturnsOriginal()
    {
        var amount = ONE_CRC * 100;
        var result = CirclesConverter.DemurrageToInflationary(amount, 0);
        Assert.That(result, Is.EqualTo(amount), "At day 0, no conversion needed");
    }

    [Test]
    public void InflationaryToDemurrage_ZeroAmount_StaysZero()
    {
        var result = CirclesConverter.InflationaryToDemurrage(BigInteger.Zero, 1000);
        Assert.That(result, Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void DemurrageToInflationary_ZeroAmount_StaysZero()
    {
        var result = CirclesConverter.DemurrageToInflationary(BigInteger.Zero, 1000);
        Assert.That(result, Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void RoundTrip_PreservesValue_WithinTolerance()
    {
        var original = ONE_CRC * 12345;
        ulong day = 500;

        var demurraged = CirclesConverter.InflationaryToDemurrage(original, day);
        var recovered = CirclesConverter.DemurrageToInflationary(demurraged, day);

        // Allow for 2^64 rounding error (as per existing tests)
        var maxLoss = BigInteger.One << 64;
        var diff = BigInteger.Abs(original - recovered);

        Assert.That(diff, Is.LessThan(maxLoss),
            $"Round-trip loss {diff} should be < 2^64");
    }

    // ─────────────────────── DayFromTimestamp Tests ─────────────────────

    [Test]
    public void DayFromTimestamp_BeforeInflation_UnderflowsToLargeValue()
    {
        // Timestamp before inflation start - expected to underflow due to ulong arithmetic
        // This is a known edge case: the method expects ts >= inflationDayZeroUnix
        uint inflationStart = 1_602_720_000; // Oct 2020
        var beforeStart = DateTimeOffset.FromUnixTimeSeconds(inflationStart - 86400);

        var day = CirclesConverter.DayFromTimestamp(beforeStart, inflationStart);

        // Due to uint/ulong underflow, result will be a very large number (not 0)
        // This documents expected behavior - callers must ensure ts >= inflationDayZeroUnix
        Assert.That(day, Is.GreaterThan(0UL),
            "Timestamps before epoch underflow - callers must validate input");
    }

    [Test]
    public void DayFromTimestamp_ExactlyAtStart_ReturnsZero()
    {
        uint inflationStart = 1_602_720_000;
        var atStart = DateTimeOffset.FromUnixTimeSeconds(inflationStart);

        var day = CirclesConverter.DayFromTimestamp(atStart, inflationStart);
        Assert.That(day, Is.EqualTo(0UL));
    }

    [Test]
    public void DayFromTimestamp_OneSecondAfterStart_StillDayZero()
    {
        uint inflationStart = 1_602_720_000;
        var justAfter = DateTimeOffset.FromUnixTimeSeconds(inflationStart + 1);

        var day = CirclesConverter.DayFromTimestamp(justAfter, inflationStart);
        Assert.That(day, Is.EqualTo(0UL), "1 second after start is still day 0");
    }

    [Test]
    public void DayFromTimestamp_AfterOneDay_ReturnsDayOne()
    {
        uint inflationStart = 1_602_720_000;
        uint secondsPerDay = 86400;
        var nextDay = DateTimeOffset.FromUnixTimeSeconds(inflationStart + secondsPerDay);

        var day = CirclesConverter.DayFromTimestamp(nextDay, inflationStart);
        Assert.That(day, Is.EqualTo(1UL));
    }

    // ─────────────────────── Monotonicity Tests ─────────────────────────

    [Test]
    public void ApplyDemurrage_IsMonotonicDecreasing()
    {
        var balance = ONE_CRC * 1000;
        BigInteger prevBalance = balance;

        for (ulong day = 1; day <= 365; day += 30)
        {
            var (newBalance, _) = Demurrage.ApplyDemurrage(balance, 0, day);

            Assert.That(newBalance, Is.LessThanOrEqualTo(prevBalance),
                $"Balance at day {day} should be <= balance at earlier day");

            prevBalance = newBalance;
        }
    }

    [Test]
    public void DemurrageRate_ApproximatelyCorrect()
    {
        // Circles uses ~7% annual demurrage
        // After 365 days, balance should be roughly 93% of original
        var balance = ONE_CRC * 10000;
        var (afterYear, _) = Demurrage.ApplyDemurrage(balance, 0, 365);

        var ratio = (double)afterYear / (double)balance;

        // Expected ~0.93 (93% remaining after 7% demurrage)
        Assert.That(ratio, Is.InRange(0.92, 0.94),
            $"After 1 year, expected ~93% remaining, got {ratio:P2}");
    }
}
