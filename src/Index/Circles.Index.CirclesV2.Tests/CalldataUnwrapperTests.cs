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

    // ─────────────────────── Real Transaction Test ───────────────────────

    [Test]
    public void UnwrapAndParse_RealTx_0x0dd3e318_ManualDebug()
    {
        // Manually trace through the handleOps unwrapping
        var fullCalldata = HexToBytes("0x765e827f000000000000000000000000000000000000000000000000000000000000004000000000000000000000000079c02f38dba39da361b4a0484c40351d50d55a9400000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000020000000000000000000000000f48554937f18885c7f15c432c596b5843648231d000000000000000000000000000000000000019c09d66edb000000000000000000000000000000000000000000000000000000000000000000000000000001200000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000662f20000000000000000000000000004b40300000000000000000000000000000000000000000000000000000000000144300000000000000000000000000000010e0000000000000000000000000000010e00000000000000000000000000000000000000000000000000000000000006200000000000000000000000000000000000000000000000000000000000000700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004a4541d63c800000000000000000000000038869bf66a61cf6bdb996a6ae40d5853fd43b52600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000003e48d80ff0a0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000039200548c20e6c24e4876e20dadbeab75362e2f5a4bc100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000024de0e9a3e0000000000000000000000000000000000000000000000000de0b6b3a764000000c12c1e50abb450d6205ea2c3fa861b3b834d13e8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002c40d22d9b5000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000160000000000000000000000000000000000000000000000000000000000000028000000000000000000000000000000000000000000000000000000000000000030000000000000000000000007b8a5a4673fcd082b742304032ea49d6bc6e01f5000000000000000000000000c19bc204eb1c1d5b3fe500e5e5dfabab625f286c000000000000000000000000f48554937f18885c7f15c432c596b5843648231d000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000de0b6b3a7640000000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020743fbf4da2637e923af08e3e9f67248c1b09be381fa4873455e085a271cfb97c000000000000000000000000000000000000000000000000000000000000000600010002000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b56a6b7f6012ee5bef1cdf95df25e5045c7727c739000000000000000000000000000927c000000000000000000000000000004e200000000000000000000000000000000000000000000000000000000069b248360000000000000000000000000000000000000000000000000000000000001234da7739798409c9eaa25c6b950882b177adf1863377fe66fa48f0b9569080e4a92532ee41a55608be95728320a9646508c3af7bad120223d6b15ed7d59bb492901c000000000000000000000000000000000000000000000000000000000000000000000000000001ad0000000000000000000000000000000000000000000000007ccff4a0d4e537ed2c595134219f83a73e49e65d0000000000000000000000000000000000000000000000000000000000000041000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000e0a885c9a1ba5c55f80113117b82d56283e8b3bb91188b88ab2bffe7f8025a381d583ad8cdcab577f9f34b457af97aa3a59a047834aecfc57bfcfc0bbd35085f450000000000000000000000000000000000000000000000000000000000000025a830944f5d0adea2ab734f152e86146a46a22ed25b801b8f74b292be4b7cb9821d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000034226f726967696e223a2268747470733a2f2f6170702e676e6f7369732e696f222c2263726f73734f726967696e223a66616c736500000000000000000000000000000000000000000000000000000000000000");

        var params_ = fullCalldata.AsSpan().Slice(4);
        Console.WriteLine($"params_.Length = {params_.Length}");

        // First param: offset to ops array (should be 0x40 = 64)
        var opsOffsetBytes = params_.Slice(0, 32).ToArray();
        int opsOffset = (int)new Nethermind.Int256.UInt256(opsOffsetBytes, true);
        Console.WriteLine($"opsOffset = {opsOffset} (0x{opsOffset:X})");

        // Array length at opsOffset
        var opsLengthBytes = params_.Slice(opsOffset, 32).ToArray();
        int opsLength = (int)new Nethermind.Int256.UInt256(opsLengthBytes, true);
        Console.WriteLine($"opsLength = {opsLength}");

        int arrayDataStart = opsOffset + 32;
        Console.WriteLine($"arrayDataStart = {arrayDataStart}");

        // First element offset (relative to array data start)
        var elementOffsetBytes = params_.Slice(arrayDataStart, 32).ToArray();
        int structOffset = (int)new Nethermind.Int256.UInt256(elementOffsetBytes, true);
        Console.WriteLine($"structOffset (relative) = {structOffset} (0x{structOffset:X})");

        int absoluteStructOffset = arrayDataStart + structOffset;
        Console.WriteLine($"absoluteStructOffset = {absoluteStructOffset}");

        // Now look at the UserOp struct at absoluteStructOffset
        // Offset 96 should have callData offset
        var callDataOffsetBytes = params_.Slice(absoluteStructOffset + 96, 32).ToArray();
        int callDataOffsetInStruct = (int)new Nethermind.Int256.UInt256(callDataOffsetBytes, true);
        Console.WriteLine($"callDataOffsetInStruct = {callDataOffsetInStruct} (0x{callDataOffsetInStruct:X})");

        int callDataAbsolute = absoluteStructOffset + callDataOffsetInStruct;
        Console.WriteLine($"callDataAbsolute = {callDataAbsolute}");

        // Read callData length
        var callDataLengthBytes = params_.Slice(callDataAbsolute, 32).ToArray();
        int callDataLength = (int)new Nethermind.Int256.UInt256(callDataLengthBytes, true);
        Console.WriteLine($"callDataLength = {callDataLength}");

        // Read callData
        if (callDataAbsolute + 32 + callDataLength <= params_.Length)
        {
            var innerCalldata = params_.Slice(callDataAbsolute + 32, callDataLength).ToArray();
            Console.WriteLine($"innerCalldata selector = {BitConverter.ToString(innerCalldata[..4])}");
            // Should be 541d63c8 (executeUserOpWithErrorString)
        }
        else
        {
            Console.WriteLine($"ERROR: callData would exceed bounds! callDataAbsolute={callDataAbsolute}, callDataLength={callDataLength}, params_.Length={params_.Length}");
        }

        // Now manually call ExtractCallDataFromUserOp like the code does
        var extractedCalldata = ExtractCallDataFromUserOpManual(params_, absoluteStructOffset);
        if (extractedCalldata != null)
        {
            Console.WriteLine($"ExtractCallDataFromUserOp returned {extractedCalldata.Length} bytes");
            Console.WriteLine($"Extracted selector: {BitConverter.ToString(extractedCalldata[..4])}");
        }
        else
        {
            Console.WriteLine("ExtractCallDataFromUserOp returned null!");
        }

        // Now let's verify what LogDataParsingHelper.ParseOffset returns
        Console.WriteLine($"\n--- Testing LogDataParsingHelper.ParseOffset ---");
        int testOffset = Circles.Common.LogDataParsingHelper.ParseOffset(params_, absoluteStructOffset + 96);
        Console.WriteLine($"ParseOffset(params_, {absoluteStructOffset + 96}) = {testOffset}");

        // And what ParseBytes returns
        int callDataPos = absoluteStructOffset + testOffset;
        Console.WriteLine($"callDataPos = absoluteStructOffset + testOffset = {absoluteStructOffset} + {testOffset} = {callDataPos}");

        try
        {
            var parsedBytes = Circles.Common.LogDataParsingHelper.ParseBytes(params_, callDataPos);
            Console.WriteLine($"ParseBytes returned {parsedBytes.Length} bytes, selector: {BitConverter.ToString(parsedBytes[..Math.Min(4, parsedBytes.Length)])}");

            // Debug the executeUserOpWithErrorString calldata
            Console.WriteLine($"\n--- Debugging executeUserOpWithErrorString calldata ---");
            Console.WriteLine($"parsedBytes.Length = {parsedBytes.Length}");
            Console.WriteLine($"Selector: {BitConverter.ToString(parsedBytes[..4])}");

            // executeUserOpWithErrorString(address to, uint256 value, bytes data, uint8 operation)
            // Minimum: selector(4) + to(32) + value(32) + dataOffset(32) + operation(32) = 132 bytes
            var execParams = parsedBytes.AsSpan().Slice(4);
            Console.WriteLine($"execParams.Length = {execParams.Length}");

            // data is the 3rd parameter (offset at position 64)
            var dataOffsetBytes = execParams.Slice(64, 32).ToArray();
            int dataOffset = (int)new Nethermind.Int256.UInt256(dataOffsetBytes, true);
            Console.WriteLine($"dataOffset = {dataOffset} (0x{dataOffset:X})");

            if (dataOffset + 32 <= execParams.Length)
            {
                var dataLengthBytes = execParams.Slice(dataOffset, 32).ToArray();
                int dataLength = (int)new Nethermind.Int256.UInt256(dataLengthBytes, true);
                Console.WriteLine($"dataLength = {dataLength}");

                if (dataOffset + 32 + dataLength <= execParams.Length)
                {
                    var innerData = execParams.Slice(dataOffset + 32, dataLength).ToArray();
                    Console.WriteLine($"innerData.Length = {innerData.Length}");
                    Console.WriteLine($"innerData selector = {BitConverter.ToString(innerData[..4])}");

                    // This should be multiSend (8d80ff0a)
                    Console.WriteLine($"\n--- Debugging multiSend calldata ---");

                    // multiSend(bytes transactions)
                    var multiSendParams = innerData.AsSpan().Slice(4);
                    Console.WriteLine($"multiSendParams.Length = {multiSendParams.Length}");

                    // First param: offset to transactions bytes
                    var txOffsetBytes = multiSendParams.Slice(0, 32).ToArray();
                    int txOffset = (int)new Nethermind.Int256.UInt256(txOffsetBytes, true);
                    Console.WriteLine($"txOffset = {txOffset}");

                    // transactions length
                    var txLengthBytes = multiSendParams.Slice(txOffset, 32).ToArray();
                    int txLength = (int)new Nethermind.Int256.UInt256(txLengthBytes, true);
                    Console.WriteLine($"txLength = {txLength}");

                    // transactions data
                    var transactions = multiSendParams.Slice(txOffset + 32, txLength).ToArray();
                    Console.WriteLine($"transactions.Length = {transactions.Length}");

                    // Parse packed transactions
                    // Format: [op(1)][to(20)][value(32)][dataLen(32)][data(var)]...
                    int pos = 0;
                    int txCount = 0;
                    while (pos + 85 <= transactions.Length)
                    {
                        byte op = transactions[pos];
                        var to = BitConverter.ToString(transactions[(pos + 1)..(pos + 21)]).Replace("-", "").ToLower();
                        var dataLenBytes = transactions.AsSpan().Slice(pos + 53, 32).ToArray();
                        int dataLen = (int)new Nethermind.Int256.UInt256(dataLenBytes, true);

                        Console.WriteLine($"  TX {txCount}: op={op}, to=0x{to}, dataLen={dataLen}");

                        if (pos + 85 + dataLen <= transactions.Length)
                        {
                            var txData = transactions.AsSpan().Slice(pos + 85, dataLen).ToArray();
                            if (txData.Length >= 4)
                            {
                                Console.WriteLine($"    selector: {BitConverter.ToString(txData[..4])}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"    ERROR: txData would exceed bounds");
                            break;
                        }

                        pos += 85 + dataLen;
                        txCount++;
                        if (txCount > 10) break; // Safety limit
                    }
                    Console.WriteLine($"Found {txCount} transactions in multiSend");

                    // Test LogDataParsingHelper on multiSend
                    Console.WriteLine($"\n--- Testing LogDataParsingHelper on multiSend ---");
                    try
                    {
                        int parsedTxOffset = Circles.Common.LogDataParsingHelper.ParseOffset(multiSendParams, 0);
                        Console.WriteLine($"LogDataParsingHelper.ParseOffset = {parsedTxOffset}");

                        var parsedTransactions = Circles.Common.LogDataParsingHelper.ParseBytes(multiSendParams, parsedTxOffset);
                        Console.WriteLine($"LogDataParsingHelper.ParseBytes returned {parsedTransactions.Length} bytes");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"LogDataParsingHelper exception: {ex.Message}");
                    }

                    // Test operateFlowMatrix directly - TX1 starts at pos 85+36 = 121
                    Console.WriteLine($"\n--- Testing operateFlowMatrix parsing ---");
                    int tx1Start = 85 + 36; // TX0 header (85) + TX0 data (36)
                    int tx1DataStart = tx1Start + 85; // TX1 header (85)
                    var opFlowTx = transactions.AsSpan().Slice(tx1DataStart, 708).ToArray();
                    Console.WriteLine($"operateFlowMatrix calldata length: {opFlowTx.Length}");
                    Console.WriteLine($"operateFlowMatrix selector: {BitConverter.ToString(opFlowTx[..4])}");

                    // Manual parse of operateFlowMatrix
                    var opParams = opFlowTx.AsSpan().Slice(4);
                    Console.WriteLine($"opParams.Length = {opParams.Length}");

                    try
                    {
                        int verticesOffset = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, 0);
                        int flowOffset = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, 32);
                        int streamsOffset = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, 64);
                        int packedCoordOffset = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, 96);
                        Console.WriteLine($"verticesOffset={verticesOffset}, flowOffset={flowOffset}, streamsOffset={streamsOffset}, packedCoordOffset={packedCoordOffset}");

                        var flowVertices = Circles.Common.LogDataParsingHelper.ParseAddressArray(opParams, verticesOffset);
                        Console.WriteLine($"flowVertices.Length = {flowVertices.Length}");
                        foreach (var v in flowVertices)
                            Console.WriteLine($"  vertex: {v}");

                        var packedCoords = Circles.Common.LogDataParsingHelper.ParseBytes(opParams, packedCoordOffset);
                        Console.WriteLine($"packedCoords.Length = {packedCoords.Length}");

                        var streamsLen = (int)new Nethermind.Int256.UInt256(opParams.Slice(streamsOffset, 32), true);
                        Console.WriteLine($"streamsLen = {streamsLen}");

                        int streamsArrayDataStart = streamsOffset + 32;
                        for (int si = 0; si < streamsLen; si++)
                        {
                            int structOffsetRel = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, streamsArrayDataStart + si * 32);
                            int structAbsOffset = streamsOffset + structOffsetRel;
                            Console.WriteLine($"  Stream {si}: structOffsetRel={structOffsetRel}, structAbsOffset={structAbsOffset}");

                            // Parse stream
                            var sourceCoordWord = opParams.Slice(structAbsOffset, 32).ToArray();
                            ushort sourceCoord = (ushort)new Nethermind.Int256.UInt256(sourceCoordWord, true);
                            Console.WriteLine($"    sourceCoord = {sourceCoord}");

                            int flowEdgeIdsOff = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, structAbsOffset + 32);
                            int dataOff = Circles.Common.LogDataParsingHelper.ParseOffset(opParams, structAbsOffset + 64);
                            Console.WriteLine($"    flowEdgeIdsOff={flowEdgeIdsOff}, dataOff={dataOff}");

                            var streamData = Circles.Common.LogDataParsingHelper.ParseBytes(opParams, structAbsOffset + dataOff);
                            Console.WriteLine($"    streamData.Length = {streamData.Length}");
                            if (streamData.Length > 0)
                                Console.WriteLine($"    streamData = {BitConverter.ToString(streamData).Replace("-", "").ToLower()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception during operateFlowMatrix parsing: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }

                    var opFlowResults = CalldataUnwrapper.UnwrapAndParse(opFlowTx).ToList();
                    Console.WriteLine($"operateFlowMatrix UnwrapAndParse returned {opFlowResults.Count} results");
                    foreach (var r in opFlowResults)
                    {
                        Console.WriteLine($"  From: {r.From}");
                        Console.WriteLine($"  To: {r.To}");
                        Console.WriteLine($"  Data: {BitConverter.ToString(r.Data).Replace("-", "").ToLower()}");
                    }

                    // Now test UnwrapAndParse on multiSend
                    Console.WriteLine($"\n--- Testing UnwrapAndParse on multiSend ---");
                    var multiSendResults = CalldataUnwrapper.UnwrapAndParse(innerData).ToList();
                    Console.WriteLine($"multiSend UnwrapAndParse returned {multiSendResults.Count} results");
                }
                else
                {
                    Console.WriteLine($"ERROR: innerData would exceed bounds");
                }
            }
            else
            {
                Console.WriteLine($"ERROR: dataOffset exceeds bounds");
            }

            // Now test if UnwrapAndParse works on this inner calldata
            Console.WriteLine($"\n--- Testing CalldataUnwrapper.UnwrapAndParse on inner calldata ---");
            var innerResults = CalldataUnwrapper.UnwrapAndParse(parsedBytes).ToList();
            Console.WriteLine($"UnwrapAndParse on inner (executeUserOpWithErrorString) returned {innerResults.Count} results");
            foreach (var r in innerResults)
            {
                Console.WriteLine($"  Data: {BitConverter.ToString(r.Data).Replace("-", "").ToLower()[..Math.Min(40, r.Data.Length * 2)]}...");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ParseBytes/UnwrapAndParse threw: {ex.Message}");
        }

        // Finally test the full path
        Console.WriteLine($"\n--- Testing full CalldataUnwrapper.UnwrapAndParse ---");
        var fullResults = CalldataUnwrapper.UnwrapAndParse(fullCalldata).ToList();
        Console.WriteLine($"Full UnwrapAndParse returned {fullResults.Count} results");

        Assert.Pass("Debug output complete");
    }

    private static byte[]? ExtractCallDataFromUserOpManual(ReadOnlySpan<byte> params_, int structOffset)
    {
        // Need at least: sender(32) + nonce(32) + initCodeOff(32) + callDataOff(32) = 128 bytes
        if (structOffset + 128 > params_.Length)
        {
            Console.WriteLine($"ExtractCallData: structOffset + 128 = {structOffset + 128} > params_.Length = {params_.Length}");
            return null;
        }

        // callData offset is at position 96 within the struct (4th field)
        var callDataOffsetBytes = params_.Slice(structOffset + 96, 32).ToArray();
        int callDataOffsetInStruct = (int)new Nethermind.Int256.UInt256(callDataOffsetBytes, true);
        Console.WriteLine($"ExtractCallData: callDataOffsetInStruct = {callDataOffsetInStruct}");

        int callDataAbsolute = structOffset + callDataOffsetInStruct;
        Console.WriteLine($"ExtractCallData: callDataAbsolute = {callDataAbsolute}");

        if (callDataAbsolute < 0 || callDataAbsolute + 32 > params_.Length)
        {
            Console.WriteLine($"ExtractCallData: bounds check failed - callDataAbsolute={callDataAbsolute}, params_.Length={params_.Length}");
            return null;
        }

        // Read length
        var lengthBytes = params_.Slice(callDataAbsolute, 32).ToArray();
        int length = (int)new Nethermind.Int256.UInt256(lengthBytes, true);
        Console.WriteLine($"ExtractCallData: length = {length}");

        if (callDataAbsolute + 32 + length > params_.Length)
        {
            Console.WriteLine($"ExtractCallData: data bounds check failed");
            return null;
        }

        return params_.Slice(callDataAbsolute + 32, length).ToArray();
    }

    [Test]
    public void UnwrapAndParse_RealTx_0x0dd3e318_Debug()
    {
        // Test each layer separately to find where it breaks

        // Full calldata
        var fullCalldata = HexToBytes("0x765e827f000000000000000000000000000000000000000000000000000000000000004000000000000000000000000079c02f38dba39da361b4a0484c40351d50d55a9400000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000020000000000000000000000000f48554937f18885c7f15c432c596b5843648231d000000000000000000000000000000000000019c09d66edb000000000000000000000000000000000000000000000000000000000000000000000000000001200000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000662f20000000000000000000000000004b40300000000000000000000000000000000000000000000000000000000000144300000000000000000000000000000010e0000000000000000000000000000010e00000000000000000000000000000000000000000000000000000000000006200000000000000000000000000000000000000000000000000000000000000700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004a4541d63c800000000000000000000000038869bf66a61cf6bdb996a6ae40d5853fd43b52600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000003e48d80ff0a0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000039200548c20e6c24e4876e20dadbeab75362e2f5a4bc100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000024de0e9a3e0000000000000000000000000000000000000000000000000de0b6b3a764000000c12c1e50abb450d6205ea2c3fa861b3b834d13e8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002c40d22d9b5000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000160000000000000000000000000000000000000000000000000000000000000028000000000000000000000000000000000000000000000000000000000000000030000000000000000000000007b8a5a4673fcd082b742304032ea49d6bc6e01f5000000000000000000000000c19bc204eb1c1d5b3fe500e5e5dfabab625f286c000000000000000000000000f48554937f18885c7f15c432c596b5843648231d000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000de0b6b3a7640000000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020743fbf4da2637e923af08e3e9f67248c1b09be381fa4873455e085a271cfb97c000000000000000000000000000000000000000000000000000000000000000600010002000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b56a6b7f6012ee5bef1cdf95df25e5045c7727c739000000000000000000000000000927c000000000000000000000000000004e200000000000000000000000000000000000000000000000000000000069b248360000000000000000000000000000000000000000000000000000000000001234da7739798409c9eaa25c6b950882b177adf1863377fe66fa48f0b9569080e4a92532ee41a55608be95728320a9646508c3af7bad120223d6b15ed7d59bb492901c000000000000000000000000000000000000000000000000000000000000000000000000000001ad0000000000000000000000000000000000000000000000007ccff4a0d4e537ed2c595134219f83a73e49e65d0000000000000000000000000000000000000000000000000000000000000041000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000e0a885c9a1ba5c55f80113117b82d56283e8b3bb91188b88ab2bffe7f8025a381d583ad8cdcab577f9f34b457af97aa3a59a047834aecfc57bfcfc0bbd35085f450000000000000000000000000000000000000000000000000000000000000025a830944f5d0adea2ab734f152e86146a46a22ed25b801b8f74b292be4b7cb9821d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000034226f726967696e223a2268747470733a2f2f6170702e676e6f7369732e696f222c2263726f73734f726967696e223a66616c736500000000000000000000000000000000000000000000000000000000000000");

        // Check selector
        Console.WriteLine($"Full calldata length: {fullCalldata.Length}");
        Console.WriteLine($"Selector: {BitConverter.ToString(fullCalldata[..4])}");
        Assert.That(fullCalldata[..4], Is.EqualTo(new byte[] { 0x76, 0x5e, 0x82, 0x7f }), "Should be handleOps selector");

        // Try to unwrap - this should give us executeUserOpWithErrorString calldata
        var results = CalldataUnwrapper.UnwrapAndParse(fullCalldata).ToList();
        Console.WriteLine($"UnwrapAndParse returned {results.Count} results");
        foreach (var r in results)
        {
            Console.WriteLine($"  From: {r.From}, To: {r.To}, Data: {BitConverter.ToString(r.Data).Replace("-", "").ToLower()}");
        }

        Assert.That(results.Count, Is.GreaterThan(0), "Should find TransferData");
    }

    [Test]
    public void UnwrapAndParse_RealTx_0x0dd3e318_ExtractsData()
    {
        // Real tx: https://gnosis.blockscout.com/tx/0x0dd3e3185882e95e71b659c2b5124a0fdd4cf7016ecc5ad4a6288315f0eed049
        // handleOps → executeUserOpWithErrorString → multiSend → operateFlowMatrix
        // Expected data: 743fbf4da2637e923af08e3e9f67248c1b09be381fa4873455e085a271cfb97c
        var calldata = HexToBytes("0x765e827f000000000000000000000000000000000000000000000000000000000000004000000000000000000000000079c02f38dba39da361b4a0484c40351d50d55a9400000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000020000000000000000000000000f48554937f18885c7f15c432c596b5843648231d000000000000000000000000000000000000019c09d66edb000000000000000000000000000000000000000000000000000000000000000000000000000001200000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000662f20000000000000000000000000004b40300000000000000000000000000000000000000000000000000000000000144300000000000000000000000000000010e0000000000000000000000000000010e00000000000000000000000000000000000000000000000000000000000006200000000000000000000000000000000000000000000000000000000000000700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004a4541d63c800000000000000000000000038869bf66a61cf6bdb996a6ae40d5853fd43b52600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000003e48d80ff0a0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000039200548c20e6c24e4876e20dadbeab75362e2f5a4bc100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000024de0e9a3e0000000000000000000000000000000000000000000000000de0b6b3a764000000c12c1e50abb450d6205ea2c3fa861b3b834d13e8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002c40d22d9b5000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000160000000000000000000000000000000000000000000000000000000000000028000000000000000000000000000000000000000000000000000000000000000030000000000000000000000007b8a5a4673fcd082b742304032ea49d6bc6e01f5000000000000000000000000c19bc204eb1c1d5b3fe500e5e5dfabab625f286c000000000000000000000000f48554937f18885c7f15c432c596b5843648231d000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000de0b6b3a7640000000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020743fbf4da2637e923af08e3e9f67248c1b09be381fa4873455e085a271cfb97c000000000000000000000000000000000000000000000000000000000000000600010002000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b56a6b7f6012ee5bef1cdf95df25e5045c7727c739000000000000000000000000000927c000000000000000000000000000004e200000000000000000000000000000000000000000000000000000000069b248360000000000000000000000000000000000000000000000000000000000001234da7739798409c9eaa25c6b950882b177adf1863377fe66fa48f0b9569080e4a92532ee41a55608be95728320a9646508c3af7bad120223d6b15ed7d59bb492901c000000000000000000000000000000000000000000000000000000000000000000000000000001ad0000000000000000000000000000000000000000000000007ccff4a0d4e537ed2c595134219f83a73e49e65d0000000000000000000000000000000000000000000000000000000000000041000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000e0a885c9a1ba5c55f80113117b82d56283e8b3bb91188b88ab2bffe7f8025a381d583ad8cdcab577f9f34b457af97aa3a59a047834aecfc57bfcfc0bbd35085f450000000000000000000000000000000000000000000000000000000000000025a830944f5d0adea2ab734f152e86146a46a22ed25b801b8f74b292be4b7cb9821d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000034226f726967696e223a2268747470733a2f2f6170702e676e6f7369732e696f222c2263726f73734f726967696e223a66616c736500000000000000000000000000000000000000000000000000000000000000");

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        // Should find operateFlowMatrix with data
        Assert.That(results, Has.Count.GreaterThanOrEqualTo(1), "Should extract at least one TransferData");

        // Check for expected data bytes
        var expectedData = HexToBytes("743fbf4da2637e923af08e3e9f67248c1b09be381fa4873455e085a271cfb97c");
        var hasExpectedData = results.Any(r => r.Data.SequenceEqual(expectedData));
        Assert.That(hasExpectedData, Is.True, $"Should contain expected data. Found {results.Count} results: {string.Join(", ", results.Select(r => BitConverter.ToString(r.Data).Replace("-", "").ToLower()))}");
    }

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

    // ─────────────────────── Safe4337Module executeUserOpWithErrorString Unwrapping ───────────────────────

    [Test]
    public void UnwrapAndParse_ExecuteUserOpWithErrorString_ExtractsData()
    {
        // executeUserOpWithErrorString(to, value, data, operation) wrapping a safeTransferFrom
        var innerCalldata = BuildSafeTransferFromCalldata(
            Alice, Bob, 123, 1000, new byte[] { 0xde, 0xad });

        var calldata = BuildExecuteUserOpWithErrorStringCalldata(innerCalldata);

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
    public void UnwrapAndParse_ExecuteUserOpWithErrorString_EmptyInnerData_ReturnsEmpty()
    {
        var calldata = BuildExecuteUserOpWithErrorStringCalldata(Array.Empty<byte>());

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void UnwrapAndParse_ExecuteUserOpWithErrorString_WithNestedMultiSend_ExtractsAllData()
    {
        // executeUserOpWithErrorString → multiSend → [safeTransfer1, safeTransfer2]
        var transfer1 = BuildSafeTransferFromCalldata(Alice, Bob, 1, 100, new byte[] { 0x01 });
        var transfer2 = BuildSafeTransferFromCalldata(Bob, Charlie, 2, 200, new byte[] { 0x02 });

        var multiSend = BuildMultiSendCalldata(new[]
        {
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: transfer1),
            (Operation: (byte)0, To: Bob, Value: 0UL, Data: transfer2)
        });

        var calldata = BuildExecuteUserOpWithErrorStringCalldata(multiSend);

        var results = CalldataUnwrapper.UnwrapAndParse(calldata).ToList();

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x01 }));
        Assert.That(results[1].Data, Is.EqualTo(new byte[] { 0x02 }));
    }

    // ─────────────────────── Full ERC-4337 + Safe4337Module Chain ───────────────────────

    [Test]
    public void UnwrapAndParse_HandleOps_WithExecuteUserOpWithErrorString_ExtractsData()
    {
        // Full chain: handleOps → UserOp.callData [executeUserOpWithErrorString] → multiSend → [operateFlowMatrix]
        // This is the actual call path for Safe accounts using 4337 module

        var operateFlowMatrix = BuildOperateFlowMatrixCalldata(
            flowVertices: new[] { Alice, Bob },
            streams: new[]
            {
                new StreamData(0, new ulong[] { 0 }, new byte[] { 0x74, 0x78, 0x64, 0x61, 0x74, 0x61 }) // "txdata"
            },
            packedCoordinates: new byte[] { 0x00, 0x00, 0x00, 0x01 }
        );

        var multiSend = BuildMultiSendCalldata(new[]
        {
            // First TX: some other call (e.g., withdraw)
            (Operation: (byte)0, To: Alice, Value: 0UL, Data: new byte[] { 0xde, 0x0e, 0x9a, 0x3e, 0x00 }),
            // Second TX: operateFlowMatrix with data
            (Operation: (byte)0, To: Bob, Value: 0UL, Data: operateFlowMatrix)
        });

        var executeUserOp = BuildExecuteUserOpWithErrorStringCalldata(multiSend);
        var handleOps = BuildHandleOpsCalldata(new[] { executeUserOp });

        var results = CalldataUnwrapper.UnwrapAndParse(handleOps).ToList();

        // Should find the operateFlowMatrix data
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Data, Is.EqualTo(new byte[] { 0x74, 0x78, 0x64, 0x61, 0x74, 0x61 }));
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
    /// Builds ABI-encoded calldata for Safe4337Module.executeUserOpWithErrorString
    /// Selector: 0x541d63c8
    /// executeUserOpWithErrorString(address to, uint256 value, bytes data, uint8 operation)
    /// </summary>
    private static byte[] BuildExecuteUserOpWithErrorStringCalldata(byte[] innerData)
    {
        using var ms = new MemoryStream();

        // Selector
        ms.Write(new byte[] { 0x54, 0x1d, 0x63, 0xc8 });

        // to (address) - offset 0
        ms.Write(PadAddress(Alice)); // target address (doesn't matter for unwrapping)

        // value (uint256) - offset 32
        ms.Write(PadUint256(0));

        // data (bytes) - offset 64: pointer to data location
        // Points to: 128 (4 params * 32 = 128)
        ms.Write(PadUint256(128));

        // operation (uint8) - offset 96
        ms.Write(PadUint256(0)); // CALL

        // Inner data (at offset 128)
        ms.Write(PadUint256((ulong)innerData.Length));
        ms.Write(innerData);
        ms.Write(new byte[Padding32(innerData.Length)]);

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
    ///
    /// ABI encoding of dynamic struct arrays:
    /// - Array offset points to: [length][offset0][offset1]...[struct0][struct1]...
    /// - Each offset is RELATIVE to the array data start (after the length field)
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

        // Calculate struct offsets - these are RELATIVE to array data start (after length field)
        // After offset pointers: N * 32 bytes
        int offsetPointersSize = userOpCallDatas.Length * 32;
        var structOffsets = new List<int>();
        int currentOffset = offsetPointersSize; // Start after all offset pointers

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

        // Write offset pointers (relative to array data start, NOT including length)
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
        // Offsets are relative to array data start (after length field), NOT including the length
        int currentOffset = streams.Length * 32;

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
