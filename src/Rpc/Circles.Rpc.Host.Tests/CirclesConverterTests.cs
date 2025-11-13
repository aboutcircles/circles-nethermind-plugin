using System.Numerics;
using Circles.Rpc.Host;

namespace Circles.Rpc.Host.Tests;

[TestFixture]
public class CirclesConverterTests
{
    private const decimal AttoDivisor = 1_000_000_000_000_000_000m; // 10^18

    #region AttoCirclesToCircles Tests

    [Test]
    public void AttoCirclesToCircles_Zero_ReturnsZero()
    {
        var result = CirclesConverter.AttoCirclesToCircles(BigInteger.Zero);
        Assert.That(result, Is.EqualTo(0m));
    }

    [Test]
    public void AttoCirclesToCircles_OneCircle_ReturnsOne()
    {
        var oneCircle = BigInteger.Parse("1000000000000000000"); // 10^18
        var result = CirclesConverter.AttoCirclesToCircles(oneCircle);
        Assert.That(result, Is.EqualTo(1m));
    }

    [Test]
    public void AttoCirclesToCircles_FractionalCircle_ReturnsCorrectDecimal()
    {
        var halfCircle = BigInteger.Parse("500000000000000000"); // 0.5 * 10^18
        var result = CirclesConverter.AttoCirclesToCircles(halfCircle);
        Assert.That(result, Is.EqualTo(0.5m));
    }

    [Test]
    public void AttoCirclesToCircles_LargeAmount_HandlesCorrectly()
    {
        var largeAmount = BigInteger.Parse("1234567890123456789012345678"); // Very large
        var result = CirclesConverter.AttoCirclesToCircles(largeAmount);
        var expected = 1234567890123456789012345678m / AttoDivisor;
        Assert.That(result, Is.EqualTo(expected));
    }

    #endregion

    #region CirclesToAttoCircles Tests

    [Test]
    public void CirclesToAttoCircles_Zero_ReturnsZero()
    {
        var result = CirclesConverter.CirclesToAttoCircles(0m);
        Assert.That(result, Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void CirclesToAttoCircles_One_ReturnsOneAtto()
    {
        var result = CirclesConverter.CirclesToAttoCircles(1m);
        var expected = BigInteger.Parse("1000000000000000000");
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void CirclesToAttoCircles_Fractional_ReturnsCorrectAtto()
    {
        var result = CirclesConverter.CirclesToAttoCircles(0.5m);
        var expected = BigInteger.Parse("500000000000000000");
        Assert.That(result, Is.EqualTo(expected));
    }

    #endregion

    #region Round-Trip Tests

    [Test]
    public void RoundTrip_AttoToCirclesAndBack_PreservesPrecision()
    {
        var original = BigInteger.Parse("1234567890123456789");
        var circles = CirclesConverter.AttoCirclesToCircles(original);
        var back = CirclesConverter.CirclesToAttoCircles(circles);
        Assert.That(back, Is.EqualTo(original));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(100)]
    [TestCase(999999)]
    public void RoundTrip_CirclesToAttoAndBack_PreservesValue(decimal circles)
    {
        var atto = CirclesConverter.CirclesToAttoCircles(circles);
        var back = CirclesConverter.AttoCirclesToCircles(atto);
        Assert.That(back, Is.EqualTo(circles));
    }

    #endregion

    #region Placeholder Methods Tests (Phase 1 - Database Only)

    [Test]
    public void AttoCrcToAttoCircles_Placeholder_ReturnsInputUnchanged()
    {
        var input = BigInteger.Parse("1000000000000000000");
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = CirclesConverter.AttoCrcToAttoCircles(input, timestamp);

        // Phase 1: Should return unchanged (placeholder behavior)
        Assert.That(result, Is.EqualTo(input),
            "Phase 1 placeholder should return input unchanged");
    }

    [Test]
    public void AttoCirclesToAttoCrc_Placeholder_ReturnsInputUnchanged()
    {
        var input = BigInteger.Parse("2000000000000000000");
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = CirclesConverter.AttoCirclesToAttoCrc(input, timestamp);

        // Phase 1: Should return unchanged (placeholder behavior)
        Assert.That(result, Is.EqualTo(input),
            "Phase 1 placeholder should return input unchanged");
    }

    [Test]
    public void AttoCirclesToAttoStaticCircles_Placeholder_ReturnsInputUnchanged()
    {
        var input = BigInteger.Parse("3000000000000000000");
        var result = CirclesConverter.AttoCirclesToAttoStaticCircles(input);

        // Phase 1: Should return unchanged (placeholder behavior)
        Assert.That(result, Is.EqualTo(input),
            "Phase 1 placeholder should return input unchanged");
    }

    [Test]
    public void AttoStaticCirclesToAttoCircles_Placeholder_ReturnsInputUnchanged()
    {
        var input = BigInteger.Parse("4000000000000000000");
        var result = CirclesConverter.AttoStaticCirclesToAttoCircles(input);

        // Phase 1: Should return unchanged (placeholder behavior)
        Assert.That(result, Is.EqualTo(input),
            "Phase 1 placeholder should return input unchanged");
    }

    [Test]
    public void PlaceholderMethods_WithZero_ReturnZero()
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.That(CirclesConverter.AttoCrcToAttoCircles(BigInteger.Zero, timestamp),
            Is.EqualTo(BigInteger.Zero));
        Assert.That(CirclesConverter.AttoCirclesToAttoCrc(BigInteger.Zero, timestamp),
            Is.EqualTo(BigInteger.Zero));
        Assert.That(CirclesConverter.AttoCirclesToAttoStaticCircles(BigInteger.Zero),
            Is.EqualTo(BigInteger.Zero));
        Assert.That(CirclesConverter.AttoStaticCirclesToAttoCircles(BigInteger.Zero),
            Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void PlaceholderMethods_WithLargeValues_HandleCorrectly()
    {
        var large = BigInteger.Parse("999999999999999999999999999");
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // All placeholders should return input unchanged
        Assert.That(CirclesConverter.AttoCrcToAttoCircles(large, timestamp), Is.EqualTo(large));
        Assert.That(CirclesConverter.AttoCirclesToAttoCrc(large, timestamp), Is.EqualTo(large));
        Assert.That(CirclesConverter.AttoCirclesToAttoStaticCircles(large), Is.EqualTo(large));
        Assert.That(CirclesConverter.AttoStaticCirclesToAttoCircles(large), Is.EqualTo(large));
    }

    #endregion

    #region Documentation Tests (Verify Phase 1 Limitations)

    [Test]
    public void PlaceholderDocumentation_VerifyPhase1Limitations()
    {
        // This test documents the Phase 1 limitation:
        // Database-only calculations cannot perform time-based adjustments.
        // The placeholder methods return unchanged values as noted in the implementation.

        var input = BigInteger.Parse("1000000000000000000");
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // For reference: In Phase 3 with blockchain connector, these would differ:
        // - AttoCrcToAttoCircles: Would apply inflation (result > input for V1 tokens)
        // - AttoCirclesToAttoCrc: Would apply deflation (result < input)
        // - AttoCirclesToAttoStaticCircles: Would apply demurrage calculation
        // - AttoStaticCirclesToAttoCircles: Would reverse demurrage calculation

        // But in Phase 1, all return input unchanged:
        Assert.That(CirclesConverter.AttoCrcToAttoCircles(input, timestamp),
            Is.EqualTo(input), "Phase 1: No inflation applied");
        Assert.That(CirclesConverter.AttoCirclesToAttoCrc(input, timestamp),
            Is.EqualTo(input), "Phase 1: No deflation applied");
        Assert.That(CirclesConverter.AttoCirclesToAttoStaticCircles(input),
            Is.EqualTo(input), "Phase 1: No demurrage applied");
        Assert.That(CirclesConverter.AttoStaticCirclesToAttoCircles(input),
            Is.EqualTo(input), "Phase 1: No demurrage reversal applied");
    }

    #endregion
}
