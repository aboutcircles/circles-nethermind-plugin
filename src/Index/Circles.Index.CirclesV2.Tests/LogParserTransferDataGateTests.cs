using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Tests;

/// <summary>
/// Tests the calldata-parsing gate in <see cref="TransferDataExtractor.Extract"/>
/// (invoked from <see cref="LogParser.ParseTransaction"/> during indexing).
///
/// TransferData (the annotation blob) is extracted from transaction CALLDATA — it is not emitted
/// in the TransferSingle/TransferBatch event. Extraction is gated on a transfer event being present
/// in the transaction. The existing TransferCalldataParser/CalldataUnwrapper tests call the decoder
/// directly and bypass this gate; these tests exercise the gate itself:
///   1. A 0-value transfer carrying data IS indexed (the sanctioned annotation path for gCRC).
///   2. A transaction with no transfer event does NOT parse calldata.
///   3. A transfer event present but empty data yields nothing.
///   4. A transfer event present but non-transfer calldata (selector collision) yields nothing.
/// </summary>
[TestFixture]
public class LogParserTransferDataGateTests
{
    private const string Alice = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Hub = "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8";
    private const string TxHash = "0x11"; // placeholder — only carried through as a string field

    private const long BlockNumber = 12_345_678;
    private const long Timestamp = 1_700_000_000;

    private static TransferSingle TransferSingleEvent(ulong value = 0) =>
        new(BlockNumber, Timestamp, 0, 0, TxHash, Hub, Alice, Alice, Bob, Id: (UInt256)1, Value: (UInt256)value);

    private static TransferBatch TransferBatchEvent(ulong value = 0) =>
        new(BlockNumber, Timestamp, 0, 0, TxHash, Hub, BatchIndex: 0, Alice, Alice, Bob, Id: (UInt256)1, Value: (UInt256)value);

    private static List<TransferData> Extract(IReadOnlyList<IIndexedEventV2> events, byte[] calldata) =>
        TransferDataExtractor.Extract(events, calldata, BlockNumber, Timestamp, 0, TxHash, startingLogIndex: -1)
            .ToList();

    [Test]
    public void ExtractTransferData_ZeroValueTransferSingle_WithData_YieldsTransferData()
    {
        // A 0-value ERC-1155 safeTransferFrom is how an annotation rides alongside ERC20 (gCRC)
        // transfers. The Hub emits TransferSingle even for value=0, satisfying the gate, and the
        // parser ignores value — so the data blob must surface as a TransferData event.
        var annotation = new byte[] { 0xca, 0xfe, 0xba, 0xbe };
        var calldata = BuildSafeTransferFromCalldata(Alice, Bob, id: 1, value: 0, data: annotation);

        var results = Extract([TransferSingleEvent(value: 0)], calldata);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(annotation));
            // Emitted at the caller-supplied startingLogIndex (-1 here).
            Assert.That(results[0].LogIndex, Is.EqualTo(-1));
        });
    }

    [Test]
    public void ExtractTransferData_TransferBatchEventPresent_WithData_YieldsTransferData()
    {
        // The gate fires on TransferBatch as well as TransferSingle. The data still rides the
        // safeTransferFrom calldata; the event type is only what satisfies the gate.
        var annotation = new byte[] { 0xab, 0xcd };
        var calldata = BuildSafeTransferFromCalldata(Alice, Bob, id: 1, value: 0, data: annotation);

        var results = Extract([TransferBatchEvent()], calldata);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(annotation));
    }

    [Test]
    public void ExtractTransferData_NoTransferEvent_ReturnsEmpty()
    {
        // Same data-bearing calldata, but the transaction emits no TransferSingle/TransferBatch
        // (only a Trust event). The gate must NOT parse calldata — extraction strictly requires a
        // transfer event. This is the failure mode the annotation workaround would hit if any Hub
        // path were to skip TransferSingle.
        var calldata = BuildSafeTransferFromCalldata(Alice, Bob, id: 1, value: 0,
            data: new byte[] { 0xca, 0xfe, 0xba, 0xbe });

        var trust = new Trust(BlockNumber, Timestamp, 0, 0, TxHash, Hub, Alice, Bob, ExpiryTime: (UInt256)Timestamp);

        Assert.That(Extract([trust], calldata), Is.Empty);
    }

    [Test]
    public void ExtractTransferData_TransferEventPresent_EmptyData_ReturnsEmpty()
    {
        // Gate passes (transfer event present) but the transfer carries no data — nothing to index.
        var calldata = BuildSafeTransferFromCalldata(Alice, Bob, id: 1, value: 1000, data: []);

        Assert.That(Extract([TransferSingleEvent(value: 1000)], calldata), Is.Empty);
    }

    [Test]
    public void ExtractTransferData_TransferEventPresent_NonTransferCalldata_ReturnsEmpty()
    {
        // Gate passes, but the calldata is an unrelated selector (e.g. a colliding non-Circles call).
        // The decoder finds no transfer and the silent-skip path yields nothing.
        var calldata = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x00, 0x00, 0x00, 0x00 };

        Assert.That(Extract([TransferSingleEvent()], calldata), Is.Empty);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Builds ABI-encoded calldata for safeTransferFrom(address,address,uint256,uint256,bytes).
    /// Mirrors the layout proven in TransferCalldataParserTests.
    /// </summary>
    private static byte[] BuildSafeTransferFromCalldata(
        string from, string to, ulong id, ulong value, byte[] data)
    {
        using var ms = new MemoryStream();
        ms.Write([0xf2, 0x42, 0x43, 0x2a]);   // selector
        ms.Write(PadAddress(from));
        ms.Write(PadAddress(to));
        ms.Write(PadUint256(id));
        ms.Write(PadUint256(value));
        ms.Write(PadUint256(160));             // data offset (5 * 32)
        ms.Write(PadUint256((ulong)data.Length));
        ms.Write(data);
        ms.Write(new byte[(32 - (data.Length % 32)) % 32]); // pad to 32-byte boundary
        return ms.ToArray();
    }

    private static byte[] PadAddress(string address)
    {
        var bytes = Convert.FromHexString(address.StartsWith("0x") ? address[2..] : address);
        var result = new byte[32];
        Array.Copy(bytes, 0, result, 12, 20);
        return result;
    }

    private static byte[] PadUint256(ulong value)
    {
        var result = new byte[32];
        for (int i = 0; i < 8; i++)
            result[31 - i] = (byte)(value >> (i * 8));
        return result;
    }
}
