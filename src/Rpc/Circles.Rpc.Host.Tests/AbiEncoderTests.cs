using System.Numerics;
using Circles.Rpc.Host;

namespace Circles.Rpc.Host.Tests;

[TestFixture]
public class AbiEncoderTests
{
    [Test]
    public void EncodeBalanceOfErc20_ReturnsExpectedCalldata()
    {
        const string address = "0x1234567890abcdef1234567890abcdef12345678";

        var calldata = AbiEncoder.EncodeBalanceOfErc20(address);

        const string expected = "0x70a082310000000000000000000000001234567890abcdef1234567890abcdef12345678";
        Assert.That(calldata, Is.EqualTo(expected));
    }

    [Test]
    public void EncodeBalanceOfErc1155_ReturnsExpectedCalldata()
    {
        const string address = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
        var tokenId = BigInteger.Parse("12345678901234567890");

        var calldata = AbiEncoder.EncodeBalanceOfErc1155(address, tokenId);

        var cleanAddress = address[2..];
        var tokenIdHex = tokenId.ToString("X").PadLeft(64, '0');
        var expected = $"0x00fdd58e{cleanAddress.PadLeft(64, '0')}{tokenIdHex}";

        Assert.That(calldata, Is.EqualTo(expected));
    }

    [Test]
    public void EncodeBalanceOfBatch_ComputesCorrectOffsetsAndArrays()
    {
        var addresses = new[]
        {
            "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var tokenIds = new[] { BigInteger.One, new BigInteger(2) };

        var calldata = AbiEncoder.EncodeBalanceOfBatch(addresses, tokenIds);
        var clean = calldata[2..];

        Assert.That(clean.StartsWith("4e1273f4"), "Method id should match balanceOfBatch signature");

        var addressOffset = clean.Substring(8, 64);
        var tokenOffset = clean.Substring(72, 64);

        Assert.That(addressOffset, Is.EqualTo(FormatHex(0x40)));
        Assert.That(tokenOffset, Is.EqualTo(FormatHex(0xA0)));

        var addressesLength = clean.Substring(136, 64);
        Assert.That(addressesLength, Is.EqualTo(FormatHex(2)));

        var firstAddress = clean.Substring(200, 64);
        var secondAddress = clean.Substring(264, 64);
        Assert.That(firstAddress, Is.EqualTo(addresses[0][2..].PadLeft(64, '0')));
        Assert.That(secondAddress, Is.EqualTo(addresses[1][2..].PadLeft(64, '0')));

        var tokenLength = clean.Substring(328, 64);
        Assert.That(tokenLength, Is.EqualTo(FormatHex(2)));

        var firstToken = clean.Substring(392, 64);
        var secondToken = clean.Substring(456, 64);
        Assert.That(firstToken, Is.EqualTo(FormatHex(1)));
        Assert.That(secondToken, Is.EqualTo(FormatHex(2)));
    }

    [Test]
    public void DecodeUint256_ReturnsZeroForEmptyPayload()
    {
        var result = AbiEncoder.DecodeUint256(string.Empty);
        Assert.That(result, Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void DecodeUint256_ReturnsParsedValue()
    {
        var payload = "0x" + FormatHex(42);
        var result = AbiEncoder.DecodeUint256(payload);
        Assert.That(result, Is.EqualTo(new BigInteger(42)));
    }

    [Test]
    public void DecodeUint256Array_ReturnsParsedValues()
    {
        var payload = BuildUint256ArrayPayload(new[] { BigInteger.One, new BigInteger(2) });

        var result = AbiEncoder.DecodeUint256Array(payload);

        Assert.That(result.Length, Is.EqualTo(2));
        Assert.That(result[0], Is.EqualTo(BigInteger.One));
        Assert.That(result[1], Is.EqualTo(new BigInteger(2)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge case tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DecodeUint256_OnlyPrefix_ReturnsZero()
    {
        var result = AbiEncoder.DecodeUint256("0x");
        Assert.That(result, Is.EqualTo(BigInteger.Zero));
    }

    [Test]
    public void DecodeUint256Array_EmptyPayload_ReturnsEmptyArray()
    {
        var result = AbiEncoder.DecodeUint256Array("");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DecodeUint256Array_OnlyPrefix_ReturnsEmptyArray()
    {
        var result = AbiEncoder.DecodeUint256Array("0x");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DecodeUint256Array_TruncatedPayload_TooShortForOffset_Throws()
    {
        // offset=0x20 points to position 64, but payload has no data there
        // This is a bounds violation — the decoder throws ArgumentOutOfRangeException
        Assert.That(() => AbiEncoder.DecodeUint256Array("0x" + FormatHex(0x20)),
            Throws.InstanceOf<ArgumentOutOfRangeException>(),
            "Truncated payload should throw when array length cannot be read");
    }

    [Test]
    public void DecodeUint256Array_ZeroLengthArray_ReturnsEmptyArray()
    {
        // Valid encoding of an empty array: offset(32) + length(0)
        var payload = "0x" + FormatHex(0x20) + FormatHex(0);
        var result = AbiEncoder.DecodeUint256Array(payload);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DecodeUint256Array_SingleElement_ParsesCorrectly()
    {
        var payload = BuildUint256ArrayPayload(new[] { new BigInteger(42) });
        var result = AbiEncoder.DecodeUint256Array(payload);

        Assert.That(result.Length, Is.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(new BigInteger(42)));
    }

    [Test]
    public void DecodeUint256_MaxUint256_ParsesCorrectly()
    {
        // Max uint256: 2^256 - 1
        var maxHex = new string('f', 64);
        var result = AbiEncoder.DecodeUint256("0x" + maxHex);

        var expected = BigInteger.Pow(2, 256) - 1;
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void EncodeBalanceOfErc20_WithoutPrefix_Works()
    {
        const string address = "1234567890abcdef1234567890abcdef12345678";
        var calldata = AbiEncoder.EncodeBalanceOfErc20(address);

        // Should handle address without 0x prefix
        Assert.That(calldata, Does.StartWith("0x70a08231"));
        Assert.That(calldata, Does.Contain(address));
    }

    [Test]
    public void EncodeBalanceOfBatch_EmptyArrays_ReturnsValidEncoding()
    {
        var calldata = AbiEncoder.EncodeBalanceOfBatch(
            Array.Empty<string>(),
            Array.Empty<BigInteger>());

        Assert.That(calldata, Does.StartWith("0x4e1273f4"));
    }

    [Test]
    public void EncodeBalanceOfBatch_MismatchedLengths_ThrowsArgumentException()
    {
        Assert.That(() => AbiEncoder.EncodeBalanceOfBatch(
            new[] { "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            Array.Empty<BigInteger>()),
            Throws.InstanceOf<ArgumentException>());
    }

    private static string FormatHex(long value)
    {
        return value.ToString("X").PadLeft(64, '0');
    }

    private static string BuildUint256ArrayPayload(IReadOnlyList<BigInteger> values)
    {
        // Follows the ABI encoding the decoder expects: offset + array contents
        var offset = FormatHex(0x20);
        var length = FormatHex(values.Count);
        var dataBuilder = new System.Text.StringBuilder();
        dataBuilder.Append(length);
        foreach (var value in values)
        {
            dataBuilder.Append(value.ToString("X").PadLeft(64, '0'));
        }

        return $"0x{offset}{dataBuilder}";
    }
}
