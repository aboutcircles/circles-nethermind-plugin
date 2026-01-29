namespace Circles.Index.CirclesV2.Tests;

/// <summary>
/// Unit tests for TransferCalldataParser - extracts 'data' bytes from ERC-1155 transfer calldata.
/// </summary>
[TestFixture]
public class TransferCalldataParserTests
{
    private const string Alice = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // Real Circles V2 Hub address on Gnosis chain
    private const string CirclesV2Hub = "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8";

    // ─────────────────────── safeTransferFrom Tests ───────────────────────

    [Test]
    public void ParseCalldata_SafeTransferFrom_WithData_ReturnsTransferData()
    {
        // safeTransferFrom(address from, address to, uint256 id, uint256 value, bytes data)
        // Selector: 0xf242432a
        var calldata = BuildSafeTransferFromCalldata(
            from: Alice,
            to: Bob,
            id: 123,
            value: 1000,
            data: new byte[] { 0x01, 0x02, 0x03, 0x04 }
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
        });
    }

    [Test]
    public void ParseCalldata_SafeTransferFrom_EmptyData_ReturnsEmpty()
    {
        var calldata = BuildSafeTransferFromCalldata(
            from: Alice,
            to: Bob,
            id: 123,
            value: 1000,
            data: Array.Empty<byte>()
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── safeBatchTransferFrom Tests ───────────────────────

    [Test]
    public void ParseCalldata_SafeBatchTransferFrom_WithData_ReturnsTransferData()
    {
        // safeBatchTransferFrom(address from, address to, uint256[] ids, uint256[] values, bytes data)
        // Selector: 0x2eb2c2d6
        var calldata = BuildSafeBatchTransferFromCalldata(
            from: Alice,
            to: Bob,
            ids: new[] { 123UL, 456UL },
            values: new[] { 100UL, 200UL },
            data: new byte[] { 0xde, 0xad, 0xbe, 0xef }
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
        });
    }

    // ─────────────────────── Edge Cases ───────────────────────

    [Test]
    public void ParseCalldata_UnknownSelector_ReturnsEmpty()
    {
        var calldata = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseCalldata_TooShort_ReturnsEmpty()
    {
        var calldata = new byte[] { 0xf2, 0x42, 0x43 }; // Too short (< 4 bytes)
        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseCalldata_EmptyCalldata_ReturnsEmpty()
    {
        var results = TransferCalldataParser.ParseCalldata(Array.Empty<byte>()).ToList();
        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── operateFlowMatrix Tests ───────────────────────

    [Test]
    public void ParseCalldata_OperateFlowMatrix_SingleStream_WithData_ReturnsTransferData()
    {
        // operateFlowMatrix(address[] _flowVertices, FlowEdge[] _flow, Stream[] _streams, bytes _packedCoordinates)
        // Stream struct: { uint16 sourceCoordinate, uint256[] flowEdgeIds, bytes data }
        // Selector: 0x0d22d9b5

        var charlie = "0xcccccccccccccccccccccccccccccccccccccccc";

        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob, charlie },  // indices: 0=Alice, 1=Bob, 2=Charlie
            streams: new[]
            {
                new StreamData(
                    sourceCoordinate: 0,  // Alice
                    flowEdgeIds: new ulong[] { 0 },  // first edge points to target
                    data: new byte[] { 0xca, 0xfe, 0xba, 0xbe }
                )
            },
            // Packed coordinates: each flow edge = 6 bytes (circlesId, sender, receiver)
            // Edge 0: circlesId=0, sender=0 (Alice), receiver=1 (Bob)
            packedCoordinates: new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xca, 0xfe, 0xba, 0xbe }));
        });
    }

    [Test]
    public void ParseCalldata_OperateFlowMatrix_MultipleStreams_ReturnsMultipleTransferData()
    {
        var charlie = "0xcccccccccccccccccccccccccccccccccccccccc";

        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob, charlie },
            streams: new[]
            {
                new StreamData(
                    sourceCoordinate: 0,  // Alice
                    flowEdgeIds: new ulong[] { 0 },
                    data: new byte[] { 0x01 }
                ),
                new StreamData(
                    sourceCoordinate: 1,  // Bob
                    flowEdgeIds: new ulong[] { 1 },
                    data: new byte[] { 0x02 }
                )
            },
            // Edge 0: Alice -> Bob, Edge 1: Bob -> Charlie (6 bytes each: circlesId, sender, receiver)
            packedCoordinates: new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x01,  // edge 0: circlesId=0, sender=0, receiver=1
                0x00, 0x00, 0x00, 0x01, 0x00, 0x02   // edge 1: circlesId=0, sender=1, receiver=2
            }
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x01 }));

            Assert.That(results[1].From, Is.EqualTo(Bob));
            Assert.That(results[1].To, Is.EqualTo(charlie));
            Assert.That(results[1].Data, Is.EqualTo(new byte[] { 0x02 }));
        });
    }

    [Test]
    public void ParseCalldata_OperateFlowMatrix_StreamWithEmptyData_IsSkipped()
    {
        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob },
            streams: new[]
            {
                new StreamData(
                    sourceCoordinate: 0,
                    flowEdgeIds: new ulong[] { 0 },
                    data: Array.Empty<byte>()  // Empty data should be skipped
                )
            },
            packedCoordinates: new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }  // 6-byte triplet
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseCalldata_OperateFlowMatrix_NoStreams_ReturnsEmpty()
    {
        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob },
            streams: Array.Empty<StreamData>(),
            packedCoordinates: Array.Empty<byte>()
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── Integration Tests with Realistic Data ───────────────────────

    [Test]
    public void ParseCalldata_SafeTransferFrom_RealisticPaymentData_ParsesCorrectly()
    {
        // Simulate a payment reference in the data field (common pattern)
        // Payment reference: "PAY-2024-001"
        var paymentRef = System.Text.Encoding.UTF8.GetBytes("PAY-2024-001");

        var calldata = BuildSafeTransferFromCalldata(
            from: "0xf6b3b2ba61c16289ec3a9dcc190103c7141e6ca2",  // real-looking address
            to: "0x97fd8f7829a019946329f6d2e763a72741047518",
            id: 12345678901234567890UL, // token ID (fits in ulong)
            value: 1_000_000_000_000_000_000, // 1 CRC (18 decimals)
            data: paymentRef
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo("0xf6b3b2ba61c16289ec3a9dcc190103c7141e6ca2"));
            Assert.That(results[0].To, Is.EqualTo("0x97fd8f7829a019946329f6d2e763a72741047518"));
            Assert.That(System.Text.Encoding.UTF8.GetString(results[0].Data), Is.EqualTo("PAY-2024-001"));
        });
    }

    [Test]
    public void ParseCalldata_SafeTransferFrom_LargeData_ParsesCorrectly()
    {
        // Test with larger data payload (e.g., JSON metadata)
        var metadata = System.Text.Encoding.UTF8.GetBytes(
            "{\"purpose\":\"donation\",\"campaign\":\"2024-winter\",\"message\":\"Happy holidays!\"}");

        var calldata = BuildSafeTransferFromCalldata(
            from: Alice,
            to: Bob,
            id: 12345,
            value: 500_000_000_000_000_000, // 0.5 CRC
            data: metadata
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data.Length, Is.EqualTo(metadata.Length));
        Assert.That(results[0].Data, Is.EqualTo(metadata));
    }

    [Test]
    public void ParseCalldata_SafeBatchTransferFrom_MultipleTokens_ParsesData()
    {
        // Batch transfer with multiple token types (common for multi-token transfers)
        var batchRef = new byte[] { 0x42, 0x41, 0x54, 0x43, 0x48, 0x31 }; // "BATCH1"

        var calldata = BuildSafeBatchTransferFromCalldata(
            from: Alice,
            to: Bob,
            ids: new ulong[] { 1, 2, 3, 4, 5 },  // 5 different tokens
            values: new ulong[] { 100, 200, 300, 400, 500 },
            data: batchRef
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(batchRef));
    }

    [Test]
    public void ParseCalldata_OperateFlowMatrix_ComplexPath_ParsesAllStreams()
    {
        // Complex flow: A -> B -> C -> D (chain of transfers)
        var dave = "0xdddddddddddddddddddddddddddddddddddddddd";
        var charlie = "0xcccccccccccccccccccccccccccccccccccccccc";

        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob, charlie, dave },
            streams: new[]
            {
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0x01 }),  // A -> B
                new StreamData(1, new ulong[] { 1 }, new byte[] { 0x02 }),  // B -> C
                new StreamData(2, new ulong[] { 2 }, new byte[] { 0x03 })   // C -> D
            },
            packedCoordinates: new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x01,  // edge 0: circlesId=0, A(0) -> B(1)
                0x00, 0x00, 0x00, 0x01, 0x00, 0x02,  // edge 1: circlesId=0, B(1) -> C(2)
                0x00, 0x00, 0x00, 0x02, 0x00, 0x03   // edge 2: circlesId=0, C(2) -> D(3)
            }
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            // First transfer: Alice -> Bob
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x01 }));

            // Second transfer: Bob -> Charlie
            Assert.That(results[1].From, Is.EqualTo(Bob));
            Assert.That(results[1].To, Is.EqualTo(charlie));
            Assert.That(results[1].Data, Is.EqualTo(new byte[] { 0x02 }));

            // Third transfer: Charlie -> Dave
            Assert.That(results[2].From, Is.EqualTo(charlie));
            Assert.That(results[2].To, Is.EqualTo(dave));
            Assert.That(results[2].Data, Is.EqualTo(new byte[] { 0x03 }));
        });
    }

    [Test]
    public void ParseCalldata_OperateFlowMatrix_MixedDataAndNoData_OnlyReturnsNonEmpty()
    {
        // Some streams have data, some don't
        var charlie = "0xcccccccccccccccccccccccccccccccccccccccc";

        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob, charlie },
            streams: new[]
            {
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0xab, 0xcd }),  // has data
                new StreamData(1, new ulong[] { 1 }, Array.Empty<byte>()),        // no data - skipped
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0xef })         // has data
            },
            packedCoordinates: new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x01,  // edge 0: circlesId=0, A -> B
                0x00, 0x00, 0x00, 0x01, 0x00, 0x02   // edge 1: circlesId=0, B -> C
            }
        );

        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(2), "Should skip stream with empty data");
    }

    // ─────────────────────── Real Calldata Tests (Hex Encoded) ───────────────────────

    /// <summary>
    /// Test with actual hex-encoded calldata format as it would appear on-chain.
    /// This format is how real transaction data looks when retrieved from an RPC node.
    /// </summary>
    [Test]
    public void ParseCalldata_RawHexCalldata_SafeTransferFrom_ParsesCorrectly()
    {
        // This is ABI-encoded safeTransferFrom with 4 bytes of data (0xdeadbeef)
        // Built manually to verify byte-for-byte correctness
        var hexCalldata =
            "f242432a" +                                                          // selector
            "000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +    // from
            "000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" +    // to
            "0000000000000000000000000000000000000000000000000000000000000001" +    // id
            "0000000000000000000000000000000000000000000000000000000000000064" +    // value (100)
            "00000000000000000000000000000000000000000000000000000000000000a0" +    // data offset (160)
            "0000000000000000000000000000000000000000000000000000000000000004" +    // data length (4)
            "deadbeef00000000000000000000000000000000000000000000000000000000";    // data + padding

        var calldata = HexToBytes(hexCalldata);
        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
        });
    }

    [Test]
    public void ParseCalldata_RawHexCalldata_SafeBatchTransferFrom_ParsesCorrectly()
    {
        // safeBatchTransferFrom with 2 tokens and 2-byte data
        var hexCalldata =
            "2eb2c2d6" +                                                          // selector
            "000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +    // from
            "000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" +    // to
            "00000000000000000000000000000000000000000000000000000000000000a0" +    // ids offset (160)
            "0000000000000000000000000000000000000000000000000000000000000100" +    // values offset (256)
            "0000000000000000000000000000000000000000000000000000000000000160" +    // data offset (352)
            "0000000000000000000000000000000000000000000000000000000000000002" +    // ids.length = 2
            "0000000000000000000000000000000000000000000000000000000000000001" +    // ids[0] = 1
            "0000000000000000000000000000000000000000000000000000000000000002" +    // ids[1] = 2
            "0000000000000000000000000000000000000000000000000000000000000002" +    // values.length = 2
            "000000000000000000000000000000000000000000000000000000000000000a" +    // values[0] = 10
            "0000000000000000000000000000000000000000000000000000000000000014" +    // values[1] = 20
            "0000000000000000000000000000000000000000000000000000000000000002" +    // data.length = 2
            "cafe000000000000000000000000000000000000000000000000000000000000";    // data = 0xcafe + padding

        var calldata = HexToBytes(hexCalldata);
        var results = TransferCalldataParser.ParseCalldata(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xca, 0xfe }));
    }

    // ─────────────────────── LogParser Integration Pattern Tests ───────────────────────
    // These tests verify the exact patterns that LogParser.ParseTransferDataFromCalldata
    // will encounter when processing real transactions.

    /// <summary>
    /// Verifies the TransferData event creation pattern that LogParser uses.
    /// LogParser creates TransferData events with negative log indices for synthetic events.
    /// </summary>
    [Test]
    public void TransferDataCreationPattern_MatchesLogParserExpectations()
    {
        // Simulate what LogParser.ParseTransferDataFromCalldata does
        var calldata = BuildSafeTransferFromCalldata(
            from: Alice,
            to: Bob,
            id: 100,
            value: 1000,
            data: new byte[] { 0x01, 0x02, 0x03 }
        );

        // Parse calldata (what ParseTransferDataFromCalldata does internally)
        var parsedData = TransferCalldataParser.ParseCalldata(calldata).ToList();

        // Verify we can create TransferData event from parsed data
        // This matches the pattern in LogParser.ParseTransferDataFromCalldata
        int syntheticLogIndex = -1;
        var transferDataEvents = new List<TransferData>();

        foreach (var (from, to, data) in parsedData)
        {
            if (data.Length == 0) continue;  // Skip empty - same as LogParser

            transferDataEvents.Add(new TransferData(
                BlockNumber: 12345678,
                Timestamp: 1704067200,  // 2024-01-01 00:00:00 UTC
                TransactionIndex: 5,
                LogIndex: syntheticLogIndex--,  // Negative index for synthetic events
                TransactionHash: "0xabcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
                Emitter: "",  // Empty for calldata-derived events
                From: from,
                To: to,
                Data: data
            ));
        }

        Assert.That(transferDataEvents, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(transferDataEvents[0].BlockNumber, Is.EqualTo(12345678));
            Assert.That(transferDataEvents[0].LogIndex, Is.EqualTo(-1), "Should have negative log index");
            Assert.That(transferDataEvents[0].From, Is.EqualTo(Alice));
            Assert.That(transferDataEvents[0].To, Is.EqualTo(Bob));
            Assert.That(transferDataEvents[0].Data, Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
            Assert.That(transferDataEvents[0].Emitter, Is.Empty, "Emitter should be empty for calldata events");
        });
    }

    /// <summary>
    /// Tests the operateFlowMatrix pattern where multiple streams create multiple TransferData events.
    /// This matches the pattern where LogParser decrements syntheticLogIndex for each event.
    /// </summary>
    [Test]
    public void TransferDataCreationPattern_MultipleEvents_HaveDescendingLogIndices()
    {
        var charlie = "0xcccccccccccccccccccccccccccccccccccccccc";

        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob, charlie },
            streams: new[]
            {
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0x01 }),
                new StreamData(1, new ulong[] { 1 }, new byte[] { 0x02 })
            },
            packedCoordinates: new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x01,  // edge 0: circlesId=0, A -> B
                0x00, 0x00, 0x00, 0x01, 0x00, 0x02   // edge 1: circlesId=0, B -> C
            }
        );

        var parsedData = TransferCalldataParser.ParseCalldata(calldata).ToList();

        // Simulate LogParser's log index assignment
        int startingLogIndex = -5;  // LogParser calculates this based on TransferSummary count
        int logIndex = startingLogIndex;

        var events = new List<TransferData>();
        foreach (var (from, to, data) in parsedData)
        {
            if (data.Length == 0) continue;

            events.Add(new TransferData(
                12345678, 1704067200, 1, logIndex--, "0x123", "", from, to, data));
        }

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(events[0].LogIndex, Is.EqualTo(-5));
            Assert.That(events[1].LogIndex, Is.EqualTo(-6), "Each event should have decreasing log index");
        });
    }

    /// <summary>
    /// Verifies that the TransferData record can be serialized and deserialized correctly
    /// via the JSON polymorphism defined in IIndexedEventV2.
    /// </summary>
    [Test]
    public void TransferData_JsonPolymorphism_SerializesCorrectly()
    {
        var transferData = new TransferData(
            BlockNumber: 12345678,
            Timestamp: 1704067200,
            TransactionIndex: 1,
            LogIndex: -1,
            TransactionHash: "0x123456",
            Emitter: "",
            From: Alice,
            To: Bob,
            Data: new byte[] { 0xca, 0xfe }
        );

        // Verify it implements IIndexedEventV2 (required for indexing)
        IIndexedEventV2 evt = transferData;
        Assert.That(evt.BlockNumber, Is.EqualTo(12345678));

        // Verify JSON serialization uses the correct type discriminator
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        var json = System.Text.Json.JsonSerializer.Serialize<IIndexedEventV2>(transferData, options);

        Assert.That(json, Does.Contain("\"$type\":\"CrcV2_TransferData\""));
    }

    // ─────────────────────── Helper Methods ───────────────────────

    /// <summary>
    /// Builds ABI-encoded calldata for safeTransferFrom(address,address,uint256,uint256,bytes)
    /// </summary>
    private static byte[] BuildSafeTransferFromCalldata(
        string from, string to, ulong id, ulong value, byte[] data)
    {
        using var ms = new MemoryStream();

        // Function selector: 0xf242432a
        ms.Write(new byte[] { 0xf2, 0x42, 0x43, 0x2a });

        // from address (32 bytes, left-padded)
        ms.Write(PadAddress(from));

        // to address (32 bytes, left-padded)
        ms.Write(PadAddress(to));

        // id (uint256, 32 bytes)
        ms.Write(PadUint256(id));

        // value (uint256, 32 bytes)
        ms.Write(PadUint256(value));

        // data offset pointer (points to position 160 = 5 * 32)
        ms.Write(PadUint256(160));

        // data: length + content
        ms.Write(PadUint256((ulong)data.Length));
        ms.Write(data);

        // Pad to 32-byte boundary
        var padding = (32 - (data.Length % 32)) % 32;
        ms.Write(new byte[padding]);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds ABI-encoded calldata for safeBatchTransferFrom(address,address,uint256[],uint256[],bytes)
    /// </summary>
    private static byte[] BuildSafeBatchTransferFromCalldata(
        string from, string to, ulong[] ids, ulong[] values, byte[] data)
    {
        using var ms = new MemoryStream();

        // Function selector: 0x2eb2c2d6
        ms.Write(new byte[] { 0x2e, 0xb2, 0xc2, 0xd6 });

        // from address (32 bytes)
        ms.Write(PadAddress(from));

        // to address (32 bytes)
        ms.Write(PadAddress(to));

        // Offsets for dynamic params (relative to start of params, not including selector)
        // ids offset: starts at 160 (5 * 32)
        ms.Write(PadUint256(160));

        // values offset: after ids array
        int idsSize = 32 + ids.Length * 32; // length + elements
        ms.Write(PadUint256((ulong)(160 + idsSize)));

        // data offset: after values array
        int valuesSize = 32 + values.Length * 32;
        ms.Write(PadUint256((ulong)(160 + idsSize + valuesSize)));

        // ids array: length + elements
        ms.Write(PadUint256((ulong)ids.Length));
        foreach (var id in ids)
            ms.Write(PadUint256(id));

        // values array: length + elements
        ms.Write(PadUint256((ulong)values.Length));
        foreach (var val in values)
            ms.Write(PadUint256(val));

        // data: length + content
        ms.Write(PadUint256((ulong)data.Length));
        ms.Write(data);

        // Pad to 32-byte boundary
        var padding = (32 - (data.Length % 32)) % 32;
        ms.Write(new byte[padding]);

        return ms.ToArray();
    }

    private static byte[] PadAddress(string address)
    {
        var bytes = HexToBytes(address);
        var result = new byte[32];
        Array.Copy(bytes, 0, result, 12, 20); // Left-pad with zeros
        return result;
    }

    private static byte[] PadUint256(ulong value)
    {
        var result = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            result[31 - i] = (byte)(value >> (i * 8));
        }
        return result;
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x"))
            hex = hex[2..];

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    /// <summary>
    /// Helper record for stream data in operateFlowMatrix tests
    /// </summary>
    private record StreamData(ushort sourceCoordinate, ulong[] flowEdgeIds, byte[] data);

    /// <summary>
    /// Builds ABI-encoded calldata for operateFlowMatrix(address[],FlowEdge[],Stream[],bytes)
    /// Stream struct: { uint16 sourceCoordinate, uint256[] flowEdgeIds, bytes data }
    /// </summary>
    private static byte[] BuildOperateFlowMatrixCalldata(
        string[] flowVertices,
        StreamData[] streams,
        byte[] packedCoordinates)
    {
        using var ms = new MemoryStream();

        // Function selector: 0x0d22d9b5
        ms.Write(new byte[] { 0x0d, 0x22, 0xd9, 0xb5 });

        // All 4 parameters are dynamic, so we write 4 offset pointers first
        // Then the actual data for each parameter

        // Calculate sizes for offset calculation
        // Header: 4 offset pointers = 128 bytes
        int headerSize = 128;

        // flowVertices array: length (32) + elements (32 each)
        int verticesDataSize = 32 + flowVertices.Length * 32;

        // flow array (FlowEdge[]): we'll use empty for simplicity
        int flowDataSize = 32; // just length = 0

        // streams array: complex - calculate later
        int streamsDataSize = CalculateStreamsDataSize(streams);

        // packedCoordinates: length (32) + data + padding
        int packedCoordsDataSize = 32 + packedCoordinates.Length + Padding32(packedCoordinates.Length);

        // Write offset pointers (relative to start of params, after selector)
        ms.Write(PadUint256((ulong)headerSize)); // flowVertices offset
        ms.Write(PadUint256((ulong)(headerSize + verticesDataSize))); // flow offset
        ms.Write(PadUint256((ulong)(headerSize + verticesDataSize + flowDataSize))); // streams offset
        ms.Write(PadUint256((ulong)(headerSize + verticesDataSize + flowDataSize + streamsDataSize))); // packedCoordinates offset

        // Write flowVertices array
        ms.Write(PadUint256((ulong)flowVertices.Length));
        foreach (var addr in flowVertices)
            ms.Write(PadAddress(addr));

        // Write empty flow array
        ms.Write(PadUint256(0));

        // Write streams array - this is an array of structs (tuples)
        WriteStreamsArray(ms, streams);

        // Write packedCoordinates
        ms.Write(PadUint256((ulong)packedCoordinates.Length));
        ms.Write(packedCoordinates);
        ms.Write(new byte[Padding32(packedCoordinates.Length)]);

        return ms.ToArray();
    }

    /// <summary>
    /// Calculates the total size of the streams array encoding.
    /// Includes: length (32) + offset pointers (N*32) + struct data
    /// </summary>
    private static int CalculateStreamsDataSize(StreamData[] streams)
    {
        if (streams.Length == 0)
            return 32; // just the length field

        // Array header: length (32) + offset pointers (32 each)
        int size = 32 + streams.Length * 32;

        // Each stream struct data
        foreach (var stream in streams)
        {
            // Struct fixed fields: sourceCoordinate (32) + flowEdgeIds offset (32) + data offset (32)
            size += 96;
            // flowEdgeIds array: length (32) + elements (32 each)
            size += 32 + stream.flowEdgeIds.Length * 32;
            // data: length (32) + data + padding
            size += 32 + stream.data.Length + Padding32(stream.data.Length);
        }

        return size;
    }

    /// <summary>
    /// Writes the streams array in ABI encoding format.
    /// Offsets are relative to array data start (after the length field), per ABI spec.
    /// </summary>
    private static void WriteStreamsArray(MemoryStream ms, StreamData[] streams)
    {
        // Array length
        ms.Write(PadUint256((ulong)streams.Length));

        if (streams.Length == 0)
            return;

        // Calculate struct offsets - relative to array data start (AFTER the length field)
        // The parser does: structAbsoluteOffset = streamsArrayDataStart + structOffsetRelative
        // Where streamsArrayDataStart = streamsOffset + 32
        // So offsets should be: N*32 (offset pointers) + accumulated struct sizes
        int[] structOffsets = new int[streams.Length];
        int currentOffset = streams.Length * 32; // offset pointers only (no length field)

        for (int i = 0; i < streams.Length; i++)
        {
            structOffsets[i] = currentOffset;
            // Each struct: 96 (fixed fields) + flowEdgeIds data + data bytes data
            int flowEdgeIdsSize = 32 + streams[i].flowEdgeIds.Length * 32;
            int dataBytesSize = 32 + streams[i].data.Length + Padding32(streams[i].data.Length);
            currentOffset += 96 + flowEdgeIdsSize + dataBytesSize;
        }

        // Write offset pointers
        foreach (var offset in structOffsets)
            ms.Write(PadUint256((ulong)offset));

        // Write each struct
        foreach (var stream in streams)
        {
            WriteStreamStruct(ms, stream);
        }
    }

    /// <summary>
    /// Writes a single Stream struct: { uint16 sourceCoordinate, uint256[] flowEdgeIds, bytes data }
    /// </summary>
    private static void WriteStreamStruct(MemoryStream ms, StreamData stream)
    {
        // sourceCoordinate (uint16 padded to 32 bytes)
        ms.Write(PadUint256(stream.sourceCoordinate));

        // flowEdgeIds offset (relative to struct start) = 96 (after the 3 fixed fields)
        ms.Write(PadUint256(96));

        // data offset = 96 + flowEdgeIds array size
        int flowEdgeIdsSize = 32 + stream.flowEdgeIds.Length * 32;
        ms.Write(PadUint256((ulong)(96 + flowEdgeIdsSize)));

        // flowEdgeIds array
        ms.Write(PadUint256((ulong)stream.flowEdgeIds.Length));
        foreach (var edgeId in stream.flowEdgeIds)
            ms.Write(PadUint256(edgeId));

        // data bytes
        ms.Write(PadUint256((ulong)stream.data.Length));
        ms.Write(stream.data);
        ms.Write(new byte[Padding32(stream.data.Length)]);
    }

    private static int Padding32(int length)
    {
        return (32 - (length % 32)) % 32;
    }
}
