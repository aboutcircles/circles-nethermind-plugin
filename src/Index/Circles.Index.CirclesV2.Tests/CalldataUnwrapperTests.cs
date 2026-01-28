namespace Circles.Index.CirclesV2.Tests;

/// <summary>
/// Unit tests for CalldataUnwrapper - handles ERC-4337 and Safe wrapper unwrapping
/// to extract nested Circles Hub calls for TransferData parsing.
/// </summary>
[TestFixture]
public class CalldataUnwrapperTests
{
    private const string Alice = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Charlie = "0xcccccccccccccccccccccccccccccccccccccccc";

    // ─────────────────────── Direct Hub Calls (Passthrough) ───────────────────────

    [Test]
    public void UnwrapAndParse_DirectSafeTransferFrom_PassesThrough()
    {
        // Direct Hub call should pass through to TransferCalldataParser
        var calldata = BuildSafeTransferFromCalldata(Alice, Bob, 123, 1000, new byte[] { 0x01, 0x02 });

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x01, 0x02 }));
        });
    }

    [Test]
    public void UnwrapAndParse_DirectOperateFlowMatrix_PassesThrough()
    {
        var calldata = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob },
            streams: new[]
            {
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0xca, 0xfe })
            },
            packedCoordinates: new byte[] { 0x00, 0x00, 0x00, 0x01 }
        );

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xca, 0xfe }));
    }

    // ─────────────────────── Safe execTransaction Unwrapping ───────────────────────

    [Test]
    public void UnwrapAndParse_ExecTransaction_WithSafeTransferFrom_ExtractsData()
    {
        var innerCalldata = BuildSafeTransferFromCalldata(
            Alice, Bob, 123, 1000, new byte[] { 0xde, 0xad });

        var calldata = BuildExecTransactionCalldata(innerCalldata);

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].From, Is.EqualTo(Alice));
            Assert.That(results[0].To, Is.EqualTo(Bob));
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xde, 0xad }));
        });
    }

    [Test]
    public void UnwrapAndParse_ExecTransaction_EmptyInnerData_ReturnsEmpty()
    {
        var calldata = BuildExecTransactionCalldata(Array.Empty<byte>());

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── Safe multiSend Unwrapping ───────────────────────

    [Test]
    public void UnwrapAndParse_MultiSend_SingleTransaction_ExtractsData()
    {
        var innerCalldata = BuildSafeTransferFromCalldata(
            Alice, Bob, 123, 1000, new byte[] { 0xbe, 0xef });

        var calldata = BuildMultiSendCalldata(new[]
        {
            (Operation: (byte)0, To: Bob, Value: 0UL, Data: innerCalldata)
        });

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xbe, 0xef }));
    }

    [Test]
    public void UnwrapAndParse_MultiSend_MultipleSafeTransfers_ExtractsAllData()
    {
        var inner1 = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0x01 });
        var inner2 = BuildSafeTransferFromCalldata(Bob, Charlie, 2, 200, new byte[] { 0x02 });

        var calldata = BuildMultiSendCalldata(new[]
        {
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: inner1),
            (Operation: (byte)0, To: Bob, Value: 0UL, Data: inner2)
        });

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x01 }));
            Assert.That(results[1].Data, Is.EqualTo(new byte[] { 0x02 }));
        });
    }

    [Test]
    public void UnwrapAndParse_MultiSend_EmptyTransactions_ReturnsEmpty()
    {
        var calldata = BuildMultiSendCalldata(Array.Empty<(byte, string, ulong, byte[])>());

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── ERC-4337 handleOps Unwrapping ───────────────────────

    [Test]
    public void UnwrapAndParse_HandleOps_SingleUserOp_ExtractsData()
    {
        var innerCalldata = BuildSafeTransferFromCalldata(
            Alice, Bob, 123, 1000, new byte[] { 0x42 });

        var calldata = BuildHandleOpsCalldata(new[] { innerCalldata });

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x42 }));
    }

    [Test]
    public void UnwrapAndParse_HandleOps_MultipleUserOps_ExtractsAllData()
    {
        var inner1 = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0x01 });
        var inner2 = BuildSafeTransferFromCalldata(Bob, Charlie, 2, 200, new byte[] { 0x02 });

        var calldata = BuildHandleOpsCalldata(new[] { inner1, inner2 });

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(2));
    }

    [Test]
    public void UnwrapAndParse_HandleOps_EmptyOps_ReturnsEmpty()
    {
        var calldata = BuildHandleOpsCalldata(Array.Empty<byte[]>());

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── Nested Unwrapping (handleOps → execTransaction) ───────────────────────

    [Test]
    public void UnwrapAndParse_HandleOps_WithNestedExecTransaction_ExtractsData()
    {
        // handleOps([UserOp{callData: execTransaction(Hub, safeTransferFrom(...))}])
        var hubCall = BuildSafeTransferFromCalldata(
            Alice, Bob, 123, 1000, new byte[] { 0xca, 0xfe, 0xba, 0xbe });

        var execTransaction = BuildExecTransactionCalldata(hubCall);
        var handleOps = BuildHandleOpsCalldata(new[] { execTransaction });

        var results = CalldataUnwrapper.UnwrapAndParse(handleOps).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0xca, 0xfe, 0xba, 0xbe }));
    }

    [Test]
    public void UnwrapAndParse_HandleOps_WithNestedMultiSend_ExtractsAllData()
    {
        // handleOps([UserOp{callData: multiSend([safeTransfer1, safeTransfer2])}])
        var transfer1 = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0x01 });
        var transfer2 = BuildSafeTransferFromCalldata(Bob, Charlie, 2, 200, new byte[] { 0x02 });

        var multiSend = BuildMultiSendCalldata(new[]
        {
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: transfer1),
            (Operation: (byte)0, To: Bob, Value: 0UL, Data: transfer2)
        });

        var handleOps = BuildHandleOpsCalldata(new[] { multiSend });

        var results = CalldataUnwrapper.UnwrapAndParse(handleOps).ToList();

        Assert.That(results, Has.Count.EqualTo(2));
    }

    // ─────────────────────── Depth Limit Tests ───────────────────────

    [Test]
    public void UnwrapAndParse_DepthLimit_PreventsInfiniteRecursion()
    {
        // Build deeply nested calldata (6+ levels of execTransaction)
        // This should stop at MaxDepth (5) and not infinitely recurse
        var innerCall = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0xff });

        var nested = innerCall;
        for (int i = 0; i < 10; i++)
        {
            nested = BuildExecTransactionCalldata(nested);
        }

        // Should not throw and should stop before reaching the inner data
        var results = CalldataUnwrapper.UnwrapAndParse(nested).ToList();

        // With MaxDepth=5, we should be able to unwrap 5 levels
        // After that, the inner execTransaction selector won't be recognized as a Hub call
        Assert.That(results, Is.Empty, "Should stop at depth limit before reaching inner data");
    }

    [Test]
    public void UnwrapAndParse_ExactlyMaxDepth_StillWorks()
    {
        // 5 levels: handleOps → exec → multi → exec → safeTransfer
        var hubCall = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0x42 });
        var exec1 = BuildExecTransactionCalldata(hubCall);  // depth 4
        var multi = BuildMultiSendCalldata(new[]
        {
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: exec1)
        }); // depth 3
        var exec2 = BuildExecTransactionCalldata(multi);    // depth 2
        var handleOps = BuildHandleOpsCalldata(new[] { exec2 }); // depth 1

        var results = CalldataUnwrapper.UnwrapAndParse(handleOps).ToList();

        // Should still extract data at exactly 5 levels
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x42 }));
    }

    // ─────────────────────── Edge Cases ───────────────────────

    [Test]
    public void UnwrapAndParse_UnknownSelector_ReturnsEmpty()
    {
        var calldata = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x00, 0x00 };
        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void UnwrapAndParse_TooShort_ReturnsEmpty()
    {
        var calldata = new byte[] { 0x76, 0x5e, 0x82 }; // handleOps selector incomplete
        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void UnwrapAndParse_EmptyCalldata_ReturnsEmpty()
    {
        var results = CalldataUnwrapper.UnwrapAndParse(Array.Empty<byte>()).ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void UnwrapAndParse_MalformedExecTransaction_ReturnsEmpty()
    {
        // execTransaction selector but truncated data
        var calldata = new byte[]
        {
            0x6a, 0x76, 0x12, 0x02, // selector
            0x00, 0x00, 0x00, 0x00  // truncated params
        };

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void UnwrapAndParse_MalformedHandleOps_ReturnsEmpty()
    {
        // handleOps selector but truncated data
        var calldata = new byte[]
        {
            0x76, 0x5e, 0x82, 0x7f, // selector
            0x00, 0x00, 0x00, 0x00  // truncated params
        };

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();
        Assert.That(results, Is.Empty);
    }

    // ─────────────────────── Real-World Pattern Tests ───────────────────────

    [Test]
    public void UnwrapAndParse_TypicalERC4337Pattern_ExtractsData()
    {
        // Typical ERC-4337 flow: EntryPoint.handleOps → Safe.execTransaction → Hub.operateFlowMatrix
        var operateFlowMatrix = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob, Charlie },
            streams: new[]
            {
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0x50, 0x41, 0x59 }) // "PAY"
            },
            packedCoordinates: new byte[] { 0x00, 0x00, 0x00, 0x01 }
        );

        var execTransaction = BuildExecTransactionCalldata(operateFlowMatrix);
        var handleOps = BuildHandleOpsCalldata(new[] { execTransaction });

        var results = CalldataUnwrapper.UnwrapAndParse(handleOps).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x50, 0x41, 0x59 }));
    }

    [Test]
    public void UnwrapAndParse_BatchedTransfersViaMultiSend_ExtractsAllData()
    {
        // Pattern: handleOps → execTransaction → multiSend → [multiple Hub calls]
        var transfer1 = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0x41 }); // "A"
        var transfer2 = BuildSafeTransferFromCalldata(Alice, Charlie, 2, 200, new byte[] { 0x42 }); // "B"

        var multiSend = BuildMultiSendCalldata(new[]
        {
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: transfer1),
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: transfer2)
        });

        var execTransaction = BuildExecTransactionCalldata(multiSend);
        var handleOps = BuildHandleOpsCalldata(new[] { execTransaction });

        var results = CalldataUnwrapper.UnwrapAndParse(handleOps).ToList();

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x41 }));
        Assert.That(results[1].Data, Is.EqualTo(new byte[] { 0x42 }));
    }

    // ─────────────────────── Helper Methods ───────────────────────

    private record StreamData(ushort sourceCoordinate, ulong[] flowEdgeIds, byte[] data);

    /// <summary>
    /// Builds ABI-encoded calldata for safeTransferFrom(address,address,uint256,uint256,bytes)
    /// Selector: 0xf242432a
    /// </summary>
    private static byte[] BuildSafeTransferFromCalldata(
        string from, string to, ulong id, ulong value, byte[] data)
    {
        using var ms = new MemoryStream();

        ms.Write(new byte[] { 0xf2, 0x42, 0x43, 0x2a });
        ms.Write(PadAddress(from));
        ms.Write(PadAddress(to));
        ms.Write(PadUint256(id));
        ms.Write(PadUint256(value));
        ms.Write(PadUint256(160)); // data offset
        ms.Write(PadUint256((ulong)data.Length));
        ms.Write(data);
        ms.Write(new byte[Padding32(data.Length)]);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds ABI-encoded calldata for Safe.execTransaction
    /// Selector: 0x6a761202
    /// </summary>
    private static byte[] BuildExecTransactionCalldata(byte[] innerData)
    {
        using var ms = new MemoryStream();

        // Selector
        ms.Write(new byte[] { 0x6a, 0x76, 0x12, 0x02 });

        // execTransaction params:
        // to (address) - offset 0
        ms.Write(PadAddress(Alice)); // target address (doesn't matter for unwrapping)

        // value (uint256) - offset 32
        ms.Write(PadUint256(0));

        // data (bytes) - offset 64: pointer
        // Points to: 320 (10 params * 32 = 320)
        ms.Write(PadUint256(320));

        // operation (uint8) - offset 96
        ms.Write(PadUint256(0)); // CALL

        // safeTxGas (uint256) - offset 128
        ms.Write(PadUint256(0));

        // baseGas (uint256) - offset 160
        ms.Write(PadUint256(0));

        // gasPrice (uint256) - offset 192
        ms.Write(PadUint256(0));

        // gasToken (address) - offset 224
        ms.Write(PadAddress("0x0000000000000000000000000000000000000000"));

        // refundReceiver (address) - offset 256
        ms.Write(PadAddress("0x0000000000000000000000000000000000000000"));

        // signatures (bytes) - offset 288: pointer
        // Points to: after inner data
        int signaturesOffset = 320 + 32 + innerData.Length + Padding32(innerData.Length);
        ms.Write(PadUint256((ulong)signaturesOffset));

        // Inner data (at offset 320)
        ms.Write(PadUint256((ulong)innerData.Length));
        ms.Write(innerData);
        ms.Write(new byte[Padding32(innerData.Length)]);

        // Signatures (empty)
        ms.Write(PadUint256(0));

        return ms.ToArray();
    }

    /// <summary>
    /// Builds calldata for Safe.multiSend(bytes transactions)
    /// Selector: 0x8d80ff0a
    ///
    /// Packed format (NOT ABI): [op(1)][to(20)][value(32)][dataLen(32)][data(var)]...
    /// </summary>
    private static byte[] BuildMultiSendCalldata((byte Operation, string To, ulong Value, byte[] Data)[] transactions)
    {
        using var ms = new MemoryStream();

        // Selector
        ms.Write(new byte[] { 0x8d, 0x80, 0xff, 0x0a });

        // Build packed transactions
        using var packedMs = new MemoryStream();
        foreach (var (operation, to, value, data) in transactions)
        {
            packedMs.WriteByte(operation);
            packedMs.Write(HexToBytes(to)); // 20 bytes
            packedMs.Write(PadUint256(value)); // 32 bytes
            packedMs.Write(PadUint256((ulong)data.Length)); // 32 bytes
            packedMs.Write(data);
        }
        var packedData = packedMs.ToArray();

        // transactions offset (32)
        ms.Write(PadUint256(32));

        // transactions data
        ms.Write(PadUint256((ulong)packedData.Length));
        ms.Write(packedData);
        ms.Write(new byte[Padding32(packedData.Length)]);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds calldata for ERC-4337 EntryPoint.handleOps(PackedUserOperation[] ops, address beneficiary)
    /// Selector: 0x765e827f
    ///
    /// PackedUserOperation struct (v0.7):
    ///   address sender, uint256 nonce, bytes initCode, bytes callData,
    ///   bytes32 accountGasLimits, uint256 preVerificationGas,
    ///   bytes32 gasFees, bytes paymasterAndData, bytes signature
    /// </summary>
    private static byte[] BuildHandleOpsCalldata(byte[][] userOpCallDatas)
    {
        using var ms = new MemoryStream();

        // Selector
        ms.Write(new byte[] { 0x76, 0x5e, 0x82, 0x7f });

        // params: ops offset (32), beneficiary (32)
        ms.Write(PadUint256(64)); // ops array offset
        ms.Write(PadAddress("0x0000000000000000000000000000000000000001")); // beneficiary

        // ops array
        ms.Write(PadUint256((ulong)userOpCallDatas.Length)); // array length

        // Calculate struct offsets
        // After length + offset pointers: 32 + (N * 32)
        int baseOffset = 32 + userOpCallDatas.Length * 32;
        var structOffsets = new List<int>();
        int currentOffset = baseOffset;

        foreach (var callData in userOpCallDatas)
        {
            structOffsets.Add(currentOffset);
            // Each struct: 9 fixed fields (288 bytes) + initCode + callData + paymasterAndData + signature
            // Simplified: we only care about callData, others are empty
            int structSize = 288 + // fixed fields
                             32 + 0 + // initCode (empty)
                             32 + callData.Length + Padding32(callData.Length) + // callData
                             32 + 0 + // paymasterAndData (empty)
                             32 + 0;  // signature (empty)
            currentOffset += structSize;
        }

        // Write offset pointers
        foreach (var offset in structOffsets)
        {
            ms.Write(PadUint256((ulong)offset));
        }

        // Write each UserOp struct
        foreach (var callData in userOpCallDatas)
        {
            WritePackedUserOperation(ms, callData);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Writes a PackedUserOperation struct with minimal data (only callData is populated).
    /// </summary>
    private static void WritePackedUserOperation(MemoryStream ms, byte[] callData)
    {
        // sender (address)
        ms.Write(PadAddress(Alice));

        // nonce (uint256)
        ms.Write(PadUint256(0));

        // initCode offset (relative to struct start) = 288 (after 9 fixed 32-byte fields)
        ms.Write(PadUint256(288));

        // callData offset = 288 + 32 (initCode length) + 0 (initCode data)
        ms.Write(PadUint256(288 + 32));

        // accountGasLimits (bytes32)
        ms.Write(new byte[32]);

        // preVerificationGas (uint256)
        ms.Write(PadUint256(0));

        // gasFees (bytes32)
        ms.Write(new byte[32]);

        // paymasterAndData offset
        int paymasterOffset = 288 + 32 + 32 + callData.Length + Padding32(callData.Length);
        ms.Write(PadUint256((ulong)paymasterOffset));

        // signature offset
        int signatureOffset = paymasterOffset + 32;
        ms.Write(PadUint256((ulong)signatureOffset));

        // initCode (empty)
        ms.Write(PadUint256(0));

        // callData
        ms.Write(PadUint256((ulong)callData.Length));
        ms.Write(callData);
        ms.Write(new byte[Padding32(callData.Length)]);

        // paymasterAndData (empty)
        ms.Write(PadUint256(0));

        // signature (empty)
        ms.Write(PadUint256(0));
    }

    /// <summary>
    /// Builds ABI-encoded calldata for operateFlowMatrix
    /// </summary>
    private static byte[] BuildOperateFlowMatrixCalldata(
        string[] flowVertices,
        StreamData[] streams,
        byte[] packedCoordinates)
    {
        using var ms = new MemoryStream();

        ms.Write(new byte[] { 0x0d, 0x22, 0xd9, 0xb5 });

        int headerSize = 128;
        int verticesDataSize = 32 + flowVertices.Length * 32;
        int flowDataSize = 32;
        int streamsDataSize = CalculateStreamsDataSize(streams);

        ms.Write(PadUint256((ulong)headerSize));
        ms.Write(PadUint256((ulong)(headerSize + verticesDataSize)));
        ms.Write(PadUint256((ulong)(headerSize + verticesDataSize + flowDataSize)));
        ms.Write(PadUint256((ulong)(headerSize + verticesDataSize + flowDataSize + streamsDataSize)));

        ms.Write(PadUint256((ulong)flowVertices.Length));
        foreach (var addr in flowVertices)
            ms.Write(PadAddress(addr));

        ms.Write(PadUint256(0));

        WriteStreamsArray(ms, streams);

        ms.Write(PadUint256((ulong)packedCoordinates.Length));
        ms.Write(packedCoordinates);
        ms.Write(new byte[Padding32(packedCoordinates.Length)]);

        return ms.ToArray();
    }

    private static int CalculateStreamsDataSize(StreamData[] streams)
    {
        if (streams.Length == 0)
            return 32;

        int size = 32 + streams.Length * 32;

        foreach (var stream in streams)
        {
            size += 96;
            size += 32 + stream.flowEdgeIds.Length * 32;
            size += 32 + stream.data.Length + Padding32(stream.data.Length);
        }

        return size;
    }

    private static void WriteStreamsArray(MemoryStream ms, StreamData[] streams)
    {
        ms.Write(PadUint256((ulong)streams.Length));

        if (streams.Length == 0)
            return;

        int[] structOffsets = new int[streams.Length];
        int currentOffset = 32 + streams.Length * 32;

        for (int i = 0; i < streams.Length; i++)
        {
            structOffsets[i] = currentOffset;
            int flowEdgeIdsSize = 32 + streams[i].flowEdgeIds.Length * 32;
            int dataBytesSize = 32 + streams[i].data.Length + Padding32(streams[i].data.Length);
            currentOffset += 96 + flowEdgeIdsSize + dataBytesSize;
        }

        foreach (var offset in structOffsets)
            ms.Write(PadUint256((ulong)offset));

        foreach (var stream in streams)
            WriteStreamStruct(ms, stream);
    }

    private static void WriteStreamStruct(MemoryStream ms, StreamData stream)
    {
        ms.Write(PadUint256(stream.sourceCoordinate));
        ms.Write(PadUint256(96));

        int flowEdgeIdsSize = 32 + stream.flowEdgeIds.Length * 32;
        ms.Write(PadUint256((ulong)(96 + flowEdgeIdsSize)));

        ms.Write(PadUint256((ulong)stream.flowEdgeIds.Length));
        foreach (var edgeId in stream.flowEdgeIds)
            ms.Write(PadUint256(edgeId));

        ms.Write(PadUint256((ulong)stream.data.Length));
        ms.Write(stream.data);
        ms.Write(new byte[Padding32(stream.data.Length)]);
    }

    private static byte[] PadAddress(string address)
    {
        var bytes = HexToBytes(address);
        var result = new byte[32];
        Array.Copy(bytes, 0, result, 12, 20);
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

    private static int Padding32(int length)
    {
        return (32 - (length % 32)) % 32;
    }
}
