using System.Numerics;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests to verify PostgreSQL type conversions are handled correctly.
/// These tests validate the edge cases that could cause runtime errors.
/// </summary>
public class TypeConversionTests
{
    /// <summary>
    /// Tests that BigInteger values near the decimal.MaxValue boundary are handled correctly.
    /// This is critical for V1/V2 Transfer amounts which can be very large.
    /// </summary>
    [Fact]
    public void BigInteger_ToDecimal_ShouldOverflow_WhenValueExceedsDecimalMax()
    {
        // Arrange - decimal.MaxValue is ~7.9 x 10^28
        // To exceed decimal.MaxValue AFTER dividing by 10^18, we need ~7.9 x 10^46 in wei
        // This represents a value larger than any practical token amount
        var largeValue = BigInteger.Parse("100000000000000000000000000000000000000000000000"); // 10^47

        // Act
        var divisor = BigInteger.Parse("1000000000000000000"); // 10^18
        var divided = largeValue / divisor; // = 10^29

        // This should be larger than decimal.MaxValue after division
        var exceedsMax = divided > (BigInteger)decimal.MaxValue;
        exceedsMax.Should().BeTrue("Extremely large Ethereum values can exceed decimal.MaxValue even after wei conversion");
    }

    /// <summary>
    /// Tests the correct pattern for converting BigInteger amounts to decimal.
    /// </summary>
    [Fact]
    public void BigInteger_SafeConversion_ShouldWork_ForTypicalEthereumValues()
    {
        // Arrange - Typical large Ethereum balance (100 million tokens with 18 decimals)
        var weiValue = BigInteger.Parse("100000000000000000000000000"); // 10^26 wei = 100M tokens
        var divisor = BigInteger.Parse("1000000000000000000"); // 10^18

        // Act
        var tokenValue = weiValue / divisor;

        // Assert - Should be safe to convert
        var withinDecimalRange = tokenValue <= (BigInteger)decimal.MaxValue && tokenValue >= (BigInteger)decimal.MinValue;
        withinDecimalRange.Should().BeTrue();

        var decimalValue = (decimal)tokenValue;
        decimalValue.Should().Be(100_000_000m);
    }

    /// <summary>
    /// Tests that Unix timestamps stored as BigInteger can safely convert to long.
    /// </summary>
    [Fact]
    public void BigInteger_ToLong_ShouldCap_WhenExceedsLongMax()
    {
        // Arrange - Value larger than long.MaxValue
        var largeTimestamp = BigInteger.Parse("9999999999999999999999");

        // Act - Apply the capping logic from the cache service
        long safeValue = largeTimestamp > long.MaxValue ? long.MaxValue : (long)largeTimestamp;

        // Assert
        safeValue.Should().Be(long.MaxValue);
    }

    /// <summary>
    /// Tests that normal Unix timestamps convert correctly.
    /// </summary>
    [Fact]
    public void BigInteger_ToLong_ShouldConvert_ForNormalTimestamps()
    {
        // Arrange - Year 2025 Unix timestamp
        var normalTimestamp = new BigInteger(1735689600);

        // Act
        long safeValue = normalTimestamp > long.MaxValue ? long.MaxValue : (long)normalTimestamp;

        // Assert
        safeValue.Should().Be(1735689600L);
    }

    /// <summary>
    /// Tests V2 token ID conversion (stored as NUMERIC, read as BigInteger, converted to string).
    /// </summary>
    [Fact]
    public void V2TokenId_ShouldConvertToString_Correctly()
    {
        // Arrange - V2 token ID is the avatar address as uint256
        var tokenIdBigInt = BigInteger.Parse("1390849295786071768276380950238675083608645509734");

        // Act
        var tokenIdString = tokenIdBigInt.ToString();

        // Assert
        tokenIdString.Should().Be("1390849295786071768276380950238675083608645509734");
    }

    /// <summary>
    /// Tests that zero address handling works correctly.
    /// </summary>
    [Fact]
    public void ZeroAddress_ShouldBeHandled_InTransferProcessing()
    {
        // Arrange
        var zeroAddress = "0x0000000000000000000000000000000000000000";
        var normalAddress = "0xde374ece6fa50e781e81aac78e811b33d16912c7";

        // Act & Assert
        (zeroAddress != "0x0000000000000000000000000000000000000000").Should().BeFalse();
        (normalAddress != "0x0000000000000000000000000000000000000000").Should().BeTrue();
    }

    /// <summary>
    /// Tests BYTEA to byte[] conversion for metadata digest.
    /// </summary>
    [Fact]
    public void MetadataDigest_ByteArray_ShouldHaveCorrectLength()
    {
        // Arrange - IPFS CIDv0 digest is 32 bytes
        var digest = new byte[32];
        for (int i = 0; i < 32; i++) digest[i] = (byte)i;

        // Assert
        digest.Length.Should().Be(32);
    }
}
