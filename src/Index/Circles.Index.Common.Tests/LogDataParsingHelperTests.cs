using Nethermind.Int256;

namespace Circles.Index.Common.Tests;

/// <summary>
/// Unit tests for LogDataParsingHelper - the core ABI decoding logic for event logs.
/// These tests validate that Solidity event data is correctly parsed.
/// </summary>
[TestFixture]
public class LogDataParsingHelperTests
{
    // ─────────────────────── ParseAddressFromTopic Tests ───────────────────────

    [Test]
    public void ParseAddressFromTopic_ValidTopic_ReturnsLowercaseAddress()
    {
        // Solidity topics are 32 bytes with address in last 20 bytes
        // Address: 0x1234567890123456789012345678901234567890
        var topicBytes = new byte[32];
        // Last 20 bytes are the address
        var addressBytes = Convert.FromHexString("1234567890123456789012345678901234567890");
        Array.Copy(addressBytes, 0, topicBytes, 12, 20);

        var result = LogDataParsingHelper.ParseAddressFromTopic(topicBytes);

        Assert.That(result, Is.EqualTo("0x1234567890123456789012345678901234567890"));
    }

    [Test]
    public void ParseAddressFromTopic_ZeroAddress_ReturnsZeros()
    {
        var topicBytes = new byte[32]; // All zeros

        var result = LogDataParsingHelper.ParseAddressFromTopic(topicBytes);

        Assert.That(result, Is.EqualTo("0x0000000000000000000000000000000000000000"));
    }

    [Test]
    public void ParseAddressFromTopic_MaxAddress_ReturnsAllFs()
    {
        var topicBytes = new byte[32];
        for (int i = 12; i < 32; i++)
            topicBytes[i] = 0xFF;

        var result = LogDataParsingHelper.ParseAddressFromTopic(topicBytes);

        Assert.That(result, Is.EqualTo("0xffffffffffffffffffffffffffffffffffffffff"));
    }

    [Test]
    public void ParseAddressFromTopic_TooShort_ThrowsArgumentException()
    {
        var shortBytes = new byte[19]; // Less than 20 bytes

        Assert.Throws<ArgumentException>(() =>
            LogDataParsingHelper.ParseAddressFromTopic(shortBytes));
    }

    [Test]
    public void ParseAddressFromTopic_Exactly20Bytes_Works()
    {
        // Edge case: exactly 20 bytes (no padding)
        var addressBytes = Convert.FromHexString("de374ece6fa50e781e81aac78e811b33d16912c7");

        var result = LogDataParsingHelper.ParseAddressFromTopic(addressBytes);

        Assert.That(result, Is.EqualTo("0xde374ece6fa50e781e81aac78e811b33d16912c7"));
    }

    // ─────────────────────── ParseSingleUInt256 Tests ───────────────────────

    [Test]
    public void ParseSingleUInt256_Zero_ReturnsZero()
    {
        var data = new byte[32]; // All zeros

        var result = LogDataParsingHelper.ParseSingleUInt256(data);

        Assert.That(result, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void ParseSingleUInt256_One_ReturnsOne()
    {
        var data = new byte[32];
        data[31] = 1; // Big-endian: 1 is in last byte

        var result = LogDataParsingHelper.ParseSingleUInt256(data);

        Assert.That(result, Is.EqualTo(UInt256.One));
    }

    [Test]
    public void ParseSingleUInt256_MaxValue_ReturnsMax()
    {
        var data = new byte[32];
        for (int i = 0; i < 32; i++)
            data[i] = 0xFF;

        var result = LogDataParsingHelper.ParseSingleUInt256(data);

        Assert.That(result, Is.EqualTo(UInt256.MaxValue));
    }

    [Test]
    public void ParseSingleUInt256_OneCrc_Parses1e18()
    {
        // 1 CRC = 10^18 = 0xDE0B6B3A7640000
        var data = new byte[32];
        var crcBytes = Convert.FromHexString("0DE0B6B3A7640000");
        Array.Copy(crcBytes, 0, data, 32 - 8, 8);

        var result = LogDataParsingHelper.ParseSingleUInt256(data);

        Assert.That(result, Is.EqualTo(new UInt256(1_000_000_000_000_000_000)));
    }

    [Test]
    public void ParseSingleUInt256_TooShort_ThrowsArgumentException()
    {
        var shortData = new byte[31];

        Assert.Throws<ArgumentException>(() =>
            LogDataParsingHelper.ParseSingleUInt256(shortData));
    }

    [Test]
    public void ParseSingleUInt256_LongerData_UsesFirst32Bytes()
    {
        var data = new byte[64];
        data[31] = 42; // Value in first 32 bytes
        data[63] = 99; // Different value in next 32 bytes (ignored)

        var result = LogDataParsingHelper.ParseSingleUInt256(data);

        Assert.That(result, Is.EqualTo(new UInt256(42)));
    }

    // ─────────────────────── ParseOffset Tests ───────────────────────

    [Test]
    public void ParseOffset_ValidOffset_ReturnsCorrectValue()
    {
        // Offset value of 64 (0x40) - common for first dynamic array
        var data = new byte[32];
        data[31] = 64;

        var result = LogDataParsingHelper.ParseOffset(data, 0);

        Assert.That(result, Is.EqualTo(64));
    }

    [Test]
    public void ParseOffset_AtDifferentPosition_ReadsCorrectly()
    {
        // Two offsets: first at position 0 (value 64), second at position 32 (value 128)
        var data = new byte[64];
        data[31] = 64;  // First offset
        data[63] = 128; // Second offset

        Assert.Multiple(() =>
        {
            Assert.That(LogDataParsingHelper.ParseOffset(data, 0), Is.EqualTo(64));
            Assert.That(LogDataParsingHelper.ParseOffset(data, 32), Is.EqualTo(128));
        });
    }

    [Test]
    public void ParseOffset_NegativeOffset_Throws()
    {
        var data = new byte[32];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LogDataParsingHelper.ParseOffset(data, -1));
    }

    [Test]
    public void ParseOffset_OffsetBeyondData_Throws()
    {
        var data = new byte[32];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LogDataParsingHelper.ParseOffset(data, 1)); // Need 32 bytes from position 1
    }

    // ─────────────────────── ParseUInt256Array Tests ───────────────────────

    [Test]
    public void ParseUInt256Array_EmptyArray_ReturnsEmpty()
    {
        // Empty array: just length (0) = 32 bytes of zeros
        var data = new byte[32];

        var result = LogDataParsingHelper.ParseUInt256Array(data, 0);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParseUInt256Array_SingleElement_ReturnsOneValue()
    {
        // Array with 1 element: length (1) + value
        var data = new byte[64];
        data[31] = 1;   // Length = 1
        data[63] = 42;  // Value = 42

        var result = LogDataParsingHelper.ParseUInt256Array(data, 0);

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(new UInt256(42)));
    }

    [Test]
    public void ParseUInt256Array_MultipleElements_ReturnsAll()
    {
        // Array with 3 elements
        var data = new byte[32 + 3 * 32]; // length + 3 values
        data[31] = 3;    // Length = 3
        data[63] = 10;   // First element
        data[95] = 20;   // Second element
        data[127] = 30;  // Third element

        var result = LogDataParsingHelper.ParseUInt256Array(data, 0);

        Assert.That(result, Has.Length.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(new UInt256(10)));
        Assert.That(result[1], Is.EqualTo(new UInt256(20)));
        Assert.That(result[2], Is.EqualTo(new UInt256(30)));
    }

    [Test]
    public void ParseUInt256Array_WithOffset_StartsAtCorrectPosition()
    {
        // Data at offset 64
        var data = new byte[64 + 64]; // 64 bytes padding + length + 1 value
        data[64 + 31] = 1;   // Length at offset 64
        data[64 + 63] = 99;  // Value at offset 96

        var result = LogDataParsingHelper.ParseUInt256Array(data, 64);

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(new UInt256(99)));
    }

    [Test]
    public void ParseUInt256Array_InsufficientDataForLength_Throws()
    {
        var data = new byte[31]; // Not enough for length field

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LogDataParsingHelper.ParseUInt256Array(data, 0));
    }

    [Test]
    public void ParseUInt256Array_InsufficientDataForElements_Throws()
    {
        // Claims 2 elements but only has space for 1
        var data = new byte[64]; // length + 1 element (need 96 for 2)
        data[31] = 2; // Length = 2

        Assert.Throws<ArgumentException>(() =>
            LogDataParsingHelper.ParseUInt256Array(data, 0));
    }

    // ─────────────────────── ParseBytes Tests ───────────────────────

    [Test]
    public void ParseBytes_EmptyBytes_ReturnsEmpty()
    {
        var data = new byte[32]; // Length = 0

        var result = LogDataParsingHelper.ParseBytes(data, 0);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParseBytes_SimpleData_ReturnsCorrectBytes()
    {
        // "hello" = 5 bytes
        var data = new byte[64]; // length + padded data
        data[31] = 5; // Length = 5
        data[32] = (byte)'h';
        data[33] = (byte)'e';
        data[34] = (byte)'l';
        data[35] = (byte)'l';
        data[36] = (byte)'o';

        var result = LogDataParsingHelper.ParseBytes(data, 0);

        Assert.That(result, Is.EqualTo(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }));
    }

    [Test]
    public void ParseBytes_InsufficientData_Throws()
    {
        var data = new byte[33]; // Length says 5, but only 1 byte available
        data[31] = 5;

        Assert.Throws<ArgumentException>(() =>
            LogDataParsingHelper.ParseBytes(data, 0));
    }

    // ─────────────────────── ParseString Tests ───────────────────────

    [Test]
    public void ParseString_ValidUtf8_ReturnsString()
    {
        // "test" = 4 bytes
        var data = new byte[64];
        data[31] = 4;
        data[32] = (byte)'t';
        data[33] = (byte)'e';
        data[34] = (byte)'s';
        data[35] = (byte)'t';

        var result = LogDataParsingHelper.ParseString(data, 0);

        Assert.That(result, Is.EqualTo("test"));
    }

    [Test]
    public void ParseString_EmptyString_ReturnsEmpty()
    {
        var data = new byte[32]; // Length = 0

        var result = LogDataParsingHelper.ParseString(data, 0);

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void ParseString_Unicode_ReturnsCorrectly()
    {
        // "€" = 3 UTF-8 bytes (E2 82 AC)
        var data = new byte[64];
        data[31] = 3;
        data[32] = 0xE2;
        data[33] = 0x82;
        data[34] = 0xAC;

        var result = LogDataParsingHelper.ParseString(data, 0);

        Assert.That(result, Is.EqualTo("€"));
    }

    // ─────────────────────── ParseAddressArray Tests ───────────────────────
    // NOTE: ParseAddressArray tests require Nethermind.Core.Address at runtime.
    // These tests are skipped in favor of integration tests that have access
    // to the Nethermind runtime. The method is tested indirectly through
    // LogParser integration tests.

    // ─────────────────────── Real Event Data Tests ───────────────────────

    [Test]
    public void ParseTransferSingleData_RealFormat_ParsesCorrectly()
    {
        // TransferSingle event data: uint256 id + uint256 value
        // Example: id=0x123..., value=1000000000000000000 (1 CRC)
        var data = new byte[64];

        // Token ID (let's say it's an address as UInt256)
        var tokenId = Convert.FromHexString("de374ece6fa50e781e81aac78e811b33d16912c7");
        Array.Copy(tokenId, 0, data, 32 - 20, 20);

        // Value: 1 CRC = 10^18
        var valueBytes = Convert.FromHexString("0DE0B6B3A7640000");
        Array.Copy(valueBytes, 0, data, 64 - 8, 8);

        var dataSpan = data.AsSpan();
        var id = new UInt256(dataSpan.Slice(0, 32), true);
        var value = new UInt256(dataSpan.Slice(32, 32), true);

        Assert.Multiple(() =>
        {
            Assert.That(id.IsZero, Is.False, "Token ID should not be zero");
            Assert.That(value, Is.EqualTo(new UInt256(1_000_000_000_000_000_000)), "Value should be 1 CRC");
        });
    }

    [Test]
    public void ParseTransferBatchData_ArrayStructure_ParsesCorrectly()
    {
        // TransferBatch: offset to ids + offset to values + ids array + values array
        // Offsets: 64 (0x40) and 128 (0x80)
        var data = new byte[192]; // 2 offsets + 2 arrays (1 element each)

        // First offset: 64 (points to ids array)
        data[31] = 64;

        // Second offset: 128 (points to values array)
        data[63] = 128;

        // Ids array at offset 64: length=1, id=12345
        data[64 + 31] = 1;  // Length
        data[64 + 63] = 0x39; // 12345 = 0x3039
        data[64 + 62] = 0x30;

        // Values array at offset 128: length=1, value=100
        data[128 + 31] = 1;  // Length
        data[128 + 63] = 100;

        var dataSpan = data.AsSpan();
        int idsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        var ids = LogDataParsingHelper.ParseUInt256Array(dataSpan, idsOffset);
        var values = LogDataParsingHelper.ParseUInt256Array(dataSpan, valuesOffset);

        Assert.Multiple(() =>
        {
            Assert.That(idsOffset, Is.EqualTo(64));
            Assert.That(valuesOffset, Is.EqualTo(128));
            Assert.That(ids, Has.Length.EqualTo(1));
            Assert.That(values, Has.Length.EqualTo(1));
            Assert.That(ids[0], Is.EqualTo(new UInt256(12345)));
            Assert.That(values[0], Is.EqualTo(new UInt256(100)));
        });
    }
}
