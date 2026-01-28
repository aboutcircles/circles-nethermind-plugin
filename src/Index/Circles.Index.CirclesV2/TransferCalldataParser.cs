using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2;

/// <summary>
/// Parses ERC-1155 transfer calldata to extract the 'data' bytes parameter
/// that is not emitted in TransferSingle/TransferBatch events.
/// </summary>
public static class TransferCalldataParser
{
    // Function selectors (first 4 bytes of keccak256 hash of signature)
    // safeTransferFrom(address,address,uint256,uint256,bytes)
    private static readonly byte[] SafeTransferFromSelector = [0xf2, 0x42, 0x43, 0x2a];

    // safeBatchTransferFrom(address,address,uint256[],uint256[],bytes)
    private static readonly byte[] SafeBatchTransferFromSelector = [0x2e, 0xb2, 0xc2, 0xd6];

    // operateFlowMatrix(address[],FlowEdge[],Stream[],bytes)
    // where Stream has: (uint16 sourceCoordinate, uint256[] flowEdgeIds, bytes data)
    private static readonly byte[] OperateFlowMatrixSelector = [0x0d, 0x22, 0xd9, 0xb5];

    /// <summary>
    /// Parses transaction calldata to extract (from, to, data) tuples for transfer data.
    /// Only returns entries where data.Length > 0.
    /// </summary>
    /// <param name="calldata">Raw transaction input data</param>
    /// <returns>Enumerable of (from address, to address, data bytes) tuples</returns>
    public static IEnumerable<(string From, string To, byte[] Data)> ParseCalldata(byte[] calldata)
    {
        if (calldata.Length < 4)
            yield break;

        var selector = calldata.AsSpan(0, 4);

        if (selector.SequenceEqual(SafeTransferFromSelector))
        {
            var result = ParseSafeTransferFrom(calldata);
            if (result.HasValue && result.Value.Data.Length > 0)
                yield return result.Value;
        }
        else if (selector.SequenceEqual(SafeBatchTransferFromSelector))
        {
            var result = ParseSafeBatchTransferFrom(calldata);
            if (result.HasValue && result.Value.Data.Length > 0)
                yield return result.Value;
        }
        else if (selector.SequenceEqual(OperateFlowMatrixSelector))
        {
            foreach (var result in ParseOperateFlowMatrix(calldata))
            {
                if (result.Data.Length > 0)
                    yield return result;
            }
        }
    }

    /// <summary>
    /// Parses safeTransferFrom(address from, address to, uint256 id, uint256 value, bytes data)
    /// ABI layout (after 4-byte selector):
    ///   offset 0:   from (address, 32 bytes padded)
    ///   offset 32:  to (address, 32 bytes padded)
    ///   offset 64:  id (uint256)
    ///   offset 96:  value (uint256)
    ///   offset 128: data offset pointer
    ///   At data offset: length (uint256) + data bytes
    /// </summary>
    private static (string From, string To, byte[] Data)? ParseSafeTransferFrom(byte[] calldata)
    {
        var data = calldata.AsSpan();

        // Minimum: 4 (selector) + 160 (5 params) = 164 bytes
        if (data.Length < 164)
            return null;

        // Skip selector
        var params_ = data.Slice(4);

        string from = ParseAddressAt(params_, 0);
        string to = ParseAddressAt(params_, 32);
        // id at 64, value at 96 - not needed
        int dataOffset = LogDataParsingHelper.ParseOffset(params_, 128);

        byte[] dataBytes = LogDataParsingHelper.ParseBytes(params_, dataOffset);

        return (from, to, dataBytes);
    }

    /// <summary>
    /// Parses safeBatchTransferFrom(address from, address to, uint256[] ids, uint256[] values, bytes data)
    /// ABI layout (after 4-byte selector):
    ///   offset 0:   from (address)
    ///   offset 32:  to (address)
    ///   offset 64:  ids offset pointer
    ///   offset 96:  values offset pointer
    ///   offset 128: data offset pointer
    /// </summary>
    private static (string From, string To, byte[] Data)? ParseSafeBatchTransferFrom(byte[] calldata)
    {
        var data = calldata.AsSpan();

        // Minimum: 4 (selector) + 160 (5 params) = 164 bytes
        if (data.Length < 164)
            return null;

        var params_ = data.Slice(4);

        string from = ParseAddressAt(params_, 0);
        string to = ParseAddressAt(params_, 32);
        // ids offset at 64, values offset at 96 - not needed
        int dataOffset = LogDataParsingHelper.ParseOffset(params_, 128);

        byte[] dataBytes = LogDataParsingHelper.ParseBytes(params_, dataOffset);

        return (from, to, dataBytes);
    }

    /// <summary>
    /// Parses operateFlowMatrix(address[] _flowVertices, FlowEdge[] _flow, Stream[] _streams, bytes _packedCoordinates)
    ///
    /// Stream struct: { uint16 sourceCoordinate, uint256[] flowEdgeIds, bytes data }
    ///
    /// The 'from' address is derived from _flowVertices[stream.sourceCoordinate]
    /// The 'to' address is derived by following the flow edge chain through _packedCoordinates to _flowVertices
    /// </summary>
    private static List<(string From, string To, byte[] Data)> ParseOperateFlowMatrix(byte[] calldata)
    {
        var results = new List<(string From, string To, byte[] Data)>();
        var data = calldata.AsSpan();

        // Minimum: 4 (selector) + 128 (4 offset pointers) = 132 bytes
        if (data.Length < 132)
            return results;

        var params_ = data.Slice(4);

        // Parse offsets to each parameter
        int verticesOffset = LogDataParsingHelper.ParseOffset(params_, 0);
        int flowOffset = LogDataParsingHelper.ParseOffset(params_, 32);
        int streamsOffset = LogDataParsingHelper.ParseOffset(params_, 64);
        int packedCoordinatesOffset = LogDataParsingHelper.ParseOffset(params_, 96);

        // Parse _flowVertices array (addresses)
        string[] flowVertices = LogDataParsingHelper.ParseAddressArray(params_, verticesOffset);

        // Parse _packedCoordinates bytes (used to derive 'to' address)
        byte[] packedCoordinates = LogDataParsingHelper.ParseBytes(params_, packedCoordinatesOffset);

        // Parse _streams array - this is an array of structs, each encoded as a tuple
        // Array format: length (32 bytes), then for each element: offset to struct data
        int streamsArrayLength = (int)new UInt256(params_.Slice(streamsOffset, 32), true);
        int streamsArrayDataStart = streamsOffset + 32;

        for (int i = 0; i < streamsArrayLength; i++)
        {
            // Each array element is an offset to the struct data (relative to array start)
            int structOffsetRelative = LogDataParsingHelper.ParseOffset(params_, streamsArrayDataStart + i * 32);
            int structAbsoluteOffset = streamsOffset + structOffsetRelative;

            var streamResult = ParseStream(params_, structAbsoluteOffset, flowVertices, packedCoordinates);
            if (streamResult.HasValue)
                results.Add(streamResult.Value);
        }

        return results;
    }

    /// <summary>
    /// Parses a single Stream struct: { uint16 sourceCoordinate, uint256[] flowEdgeIds, bytes data }
    ///
    /// ABI encoding of struct:
    ///   offset 0: sourceCoordinate (uint16, padded to 32 bytes)
    ///   offset 32: flowEdgeIds offset (pointer to dynamic array)
    ///   offset 64: data offset (pointer to dynamic bytes)
    /// </summary>
    private static (string From, string To, byte[] Data)? ParseStream(
        ReadOnlySpan<byte> params_,
        int structOffset,
        string[] flowVertices,
        byte[] packedCoordinates)
    {
        if (structOffset + 96 > params_.Length)
            return null;

        // sourceCoordinate is uint16 stored in 32 bytes (big-endian, value in last 2 bytes)
        var sourceCoordWord = params_.Slice(structOffset, 32);
        ushort sourceCoordinate = (ushort)new UInt256(sourceCoordWord, true);

        if (sourceCoordinate >= flowVertices.Length)
            return null;

        string from = flowVertices[sourceCoordinate];

        // Parse flowEdgeIds offset (relative to struct start)
        int flowEdgeIdsOffset = LogDataParsingHelper.ParseOffset(params_, structOffset + 32);
        int flowEdgeIdsAbsolute = structOffset + flowEdgeIdsOffset;

        // Parse data offset (relative to struct start)
        int dataOffset = LogDataParsingHelper.ParseOffset(params_, structOffset + 64);
        int dataAbsolute = structOffset + dataOffset;

        byte[] dataBytes = LogDataParsingHelper.ParseBytes(params_, dataAbsolute);

        // Only proceed if there's actual data
        if (dataBytes.Length == 0)
            return null;

        // Parse flowEdgeIds to derive 'to' address
        // The first flowEdgeId points to an entry in _packedCoordinates which contains the target coordinate
        UInt256[] flowEdgeIds = LogDataParsingHelper.ParseUInt256Array(params_, flowEdgeIdsAbsolute);

        if (flowEdgeIds.Length == 0)
            return null;

        // Derive 'to' from the first flow edge
        // Each packed coordinate is 2 bytes (uint16), so flowEdgeId * 2 is the byte offset
        // The coordinate at that position indexes into flowVertices
        string to = DeriveToAddress(flowEdgeIds[0], packedCoordinates, flowVertices);

        return (from, to, dataBytes);
    }

    /// <summary>
    /// Derives the 'to' address from a flow edge ID by looking up the packed coordinate
    /// and then indexing into flow vertices.
    ///
    /// Flow edges encode: sourceCoord (2 bytes) + targetCoord (2 bytes) = 4 bytes per edge
    /// The flowEdgeId is the index into the packed coordinates array
    /// We want the target coordinate (second 2 bytes of the 4-byte entry)
    /// </summary>
    private static string DeriveToAddress(UInt256 flowEdgeId, byte[] packedCoordinates, string[] flowVertices)
    {
        // Each flow edge entry is 4 bytes: 2 bytes source coord + 2 bytes target coord
        // flowEdgeId indexes these 4-byte entries
        int byteOffset = (int)flowEdgeId * 4;

        if (byteOffset + 4 > packedCoordinates.Length)
        {
            // Fall back to first vertex if we can't derive
            return flowVertices.Length > 0 ? flowVertices[0] : "";
        }

        // Target coordinate is at bytes [byteOffset + 2, byteOffset + 4) (big-endian uint16)
        ushort targetCoord = (ushort)((packedCoordinates[byteOffset + 2] << 8) | packedCoordinates[byteOffset + 3]);

        if (targetCoord >= flowVertices.Length)
        {
            return flowVertices.Length > 0 ? flowVertices[0] : "";
        }

        return flowVertices[targetCoord];
    }

    /// <summary>
    /// Parses an address from a 32-byte padded slot at the given offset.
    /// Address is in the last 20 bytes.
    /// </summary>
    private static string ParseAddressAt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 32 > data.Length)
            throw new ArgumentException($"Not enough data to parse address at offset {offset}");

        return LogDataParsingHelper.ParseAddressFromTopic(data.Slice(offset, 32));
    }
}
