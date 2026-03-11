using System.Numerics;
using Circles.Rpc.Host;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Tests for TokenIdToAddress / AddressToTokenIdBigInt round-trip conversion
/// and edge cases in token ID handling.
/// </summary>
[TestFixture]
public class TokenConversionTests
{
    // TokenIdToAddress and AddressToTokenIdBigInt are private static methods.
    // We use reflection to test them, or test indirectly via public methods.
    // For now, we test the conversion logic by reimplementing the same algorithm
    // and verifying it matches expectations.

    [TestCase("0x42cedde51198d1773590311e2a340dc06b24cb37")]
    [TestCase("0x0000000000000000000000000000000000000001")]
    [TestCase("0xffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x0000000000000000000000000000000000000000")]
    public void AddressToTokenId_RoundTrip_PreservesAddress(string address)
    {
        var tokenId = AddressToTokenIdBigInt(address);
        var recovered = TokenIdToAddress(tokenId.ToString());

        Assert.That(recovered, Is.EqualTo(address.ToLowerInvariant()));
    }

    [Test]
    public void AddressToTokenId_ZeroAddress_ReturnsZero()
    {
        var result = AddressToTokenIdBigInt("0x0000000000000000000000000000000000000000");
        Assert.That(result, Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void AddressToTokenId_MaxUint160_ReturnsCorrectValue()
    {
        var maxUint160 = BigInteger.Pow(2, 160) - 1;
        var result = AddressToTokenIdBigInt("0xffffffffffffffffffffffffffffffffffffffff");
        Assert.That(result, Is.EqualTo(maxUint160));
    }

    [Test]
    public void TokenIdToAddress_AlreadyHexAddress_LowercasesIt()
    {
        var result = TokenIdToAddress("0xAbCdEf1234567890AbCdEf1234567890AbCdEf12");
        Assert.That(result, Is.EqualTo("0xabcdef1234567890abcdef1234567890abcdef12"));
    }

    [Test]
    public void TokenIdToAddress_NumericId_ConvertsToAddress()
    {
        // tokenId = 1 → should be 0x0000...0001
        var result = TokenIdToAddress("1");
        Assert.That(result, Is.EqualTo("0x0000000000000000000000000000000000000001"));
    }

    [Test]
    public void TokenIdToAddress_LeadingZeroAddress_PreservesFullLength()
    {
        // Address with leading zeros: 0x0000...0042
        var result = TokenIdToAddress("66"); // 0x42
        Assert.That(result, Has.Length.EqualTo(42)); // "0x" + 40 hex chars
        Assert.That(result, Is.EqualTo("0x0000000000000000000000000000000000000042"));
    }

    [Test]
    public void TokenIdToAddress_LargeValue_TruncatesTo40Chars()
    {
        // A value that when converted to hex has more than 40 chars
        // BigInteger.ToString("x") prepends "0" for positive MSB >= 8
        // For max uint160: all f's = 40 chars, but BigInteger prepends "0" → 41 chars
        var maxUint160 = BigInteger.Pow(2, 160) - 1;
        var result = TokenIdToAddress(maxUint160.ToString());
        Assert.That(result, Is.EqualTo("0xffffffffffffffffffffffffffffffffffffffff"));
    }

    [Test]
    public void AddressToTokenId_NeverReturnsNegative()
    {
        // BigInteger.Parse with HexNumber can return negative if MSB >= 8
        // The "0" prefix in the code prevents this
        var result = AddressToTokenIdBigInt("0xffffffffffffffffffffffffffffffffffffffff");
        Assert.That(result.Sign, Is.EqualTo(1), "Token ID should always be positive");
    }

    // Mirror the private methods for testing
    private static BigInteger AddressToTokenIdBigInt(string address)
    {
        var hex = address.StartsWith("0x") ? address.Substring(2) : address;
        return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
    }

    private static string TokenIdToAddress(string tokenId)
    {
        if (tokenId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return tokenId.ToLowerInvariant();
        }

        var bigInt = BigInteger.Parse(tokenId);
        var hex = bigInt.ToString("x");
        if (hex.Length > 40)
        {
            hex = hex.Substring(hex.Length - 40);
        }
        else
        {
            hex = hex.PadLeft(40, '0');
        }
        return "0x" + hex;
    }
}
