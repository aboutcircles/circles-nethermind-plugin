using System.Numerics;
using System.Text;
using Circles.Common;
using Circles.Common.Dto;

namespace Circles.Pathfinder.Simulation;

/// <summary>
/// Encodes pathfinder TransferPathStep[] into Hub.sol operateFlowMatrix calldata.
/// Extracted from AnvilExecutionHelper for use in the production simulation canary.
/// </summary>
public static class FlowMatrixEncoder
{
    // Hub.sol V2 contract address on Gnosis chain
    public const string CirclesHubAddress = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8";

    // Pre-computed: keccak256("operateFlowMatrix(address[],(uint16,uint192)[],(uint16,uint16[],bytes)[],bytes)")[..4]
    private static readonly string MethodSelector = ComputeMethodSelector();

    private static string ComputeMethodSelector()
    {
        var hash = KeccakHelper.ComputeHash(
            "operateFlowMatrix(address[],(uint16,uint192)[],(uint16,uint16[],bytes)[],bytes)");
        return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Builds the ABI-encoded calldata for Hub.operateFlowMatrix from pathfinder transfer steps.
    /// </summary>
    /// <summary>
    /// Builds the ABI-encoded calldata for Hub.operateFlowMatrix from pathfinder transfer steps.
    /// The wrapperToAvatar mapping resolves ERC20 wrapper addresses to their underlying avatar,
    /// mirroring what the SDK does before submitting to Hub.sol (wrappers are not valid flow vertices).
    /// </summary>
    public static string BuildCalldata(
        string sender,
        string receiver,
        IReadOnlyList<TransferPathStep> transfers,
        IReadOnlyDictionary<string, string>? wrapperToAvatar = null)
    {
        // Resolve tokenOwner: SDK unwraps ERC20 wrappers before calling Hub.sol.
        // The pathfinder intentionally keeps wrapper addresses so the SDK knows what to unwrap.
        // For simulation we must do the same resolution.
        string ResolveToken(string tokenOwner)
        {
            var lower = tokenOwner.ToLowerInvariant();
            if (wrapperToAvatar != null && wrapperToAvatar.TryGetValue(lower, out var avatar))
                return avatar;
            return lower;
        }

        // Step 1: Build sorted vertex list (all unique addresses)
        var vertexSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        vertexSet.Add(sender.ToLowerInvariant());
        vertexSet.Add(receiver.ToLowerInvariant());
        foreach (var t in transfers)
        {
            vertexSet.Add(t.From.ToLowerInvariant());
            vertexSet.Add(t.To.ToLowerInvariant());
            vertexSet.Add(ResolveToken(t.TokenOwner));
        }

        // Sort by uint160 value for Hub.sol's _flowVertices ordering
        var flowVertices = vertexSet
            .OrderBy(v => BigInteger.Parse("0" + v.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber))
            .ToList();

        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < flowVertices.Count; i++)
            idx[flowVertices[i]] = i;

        if (flowVertices.Count > ushort.MaxValue + 1)
            throw new InvalidOperationException(
                $"operateFlowMatrix supports at most {ushort.MaxValue + 1} vertices; got {flowVertices.Count}.");
        if (transfers.Count > ushort.MaxValue + 1)
            throw new InvalidOperationException(
                $"operateFlowMatrix supports at most {ushort.MaxValue + 1} flow edges; got {transfers.Count}.");

        // Step 2: Build flow edges and coordinates
        var flowEdges = new List<(ushort streamSinkId, BigInteger amount)>();
        var coordinates = new List<ushort>();
        var receiverLower = receiver.ToLowerInvariant();
        var senderLower = sender.ToLowerInvariant();
        var terminalEdgeIndices = new List<int>();

        for (int i = 0; i < transfers.Count; i++)
        {
            var t = transfers[i];
            var amount = BigInteger.Parse(t.Value);
            var toAddr = t.To.ToLowerInvariant();

            ushort streamSinkId = toAddr == receiverLower ? (ushort)1 : (ushort)0;
            flowEdges.Add((streamSinkId, amount));

            if (streamSinkId == 1)
                terminalEdgeIndices.Add(i);

            coordinates.Add((ushort)idx[ResolveToken(t.TokenOwner)]);
            coordinates.Add((ushort)idx[t.From.ToLowerInvariant()]);
            coordinates.Add((ushort)idx[toAddr]);
        }

        if (transfers.Count > 0 && terminalEdgeIndices.Count == 0)
            throw new InvalidOperationException(
                "Cannot build operateFlowMatrix calldata: no transfer terminates at the requested receiver.");

        var senderCoordinate = (ushort)idx[senderLower];
        var terminalEdgeIds = terminalEdgeIndices.Select(i => (ushort)i).ToArray();
        var packedCoordinates = PackCoordinates(coordinates);

        return AbiEncode(flowVertices, flowEdges, senderCoordinate, terminalEdgeIds, packedCoordinates);
    }

    private static string PackCoordinates(List<ushort> coordinates)
    {
        var bytes = new byte[coordinates.Count * 2];
        for (int i = 0; i < coordinates.Count; i++)
        {
            bytes[i * 2] = (byte)(coordinates[i] >> 8);
            bytes[i * 2 + 1] = (byte)(coordinates[i] & 0xFF);
        }
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    private static string AbiEncode(
        List<string> flowVertices,
        List<(ushort streamSinkId, BigInteger amount)> flowEdges,
        ushort senderCoordinate,
        ushort[] terminalEdgeIds,
        string packedCoordinates)
    {
        var sb = new StringBuilder();
        sb.Append("0x");
        sb.Append(MethodSelector);

        int currentOffset = 4 * 32;
        int flowVerticesSize = 32 + flowVertices.Count * 32;
        int flowEdgesSize = 32 + flowEdges.Count * 64;
        int streamsSize = 32 + 32 + 32 + 32 + 32 + 32 + terminalEdgeIds.Length * 32 + 32;
        int packedCoordinatesSize = 32 + ((packedCoordinates.Length / 2 + 31) / 32) * 32;

        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));
        currentOffset += flowVerticesSize;
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));
        currentOffset += flowEdgesSize;
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));
        currentOffset += streamsSize;
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));

        // flowVertices
        sb.Append(flowVertices.Count.ToString("x").PadLeft(64, '0'));
        foreach (var v in flowVertices)
            sb.Append(v.Replace("0x", "").PadLeft(64, '0'));

        // flowEdges
        sb.Append(flowEdges.Count.ToString("x").PadLeft(64, '0'));
        foreach (var (streamSinkId, amount) in flowEdges)
        {
            sb.Append(streamSinkId.ToString("x").PadLeft(64, '0'));
            sb.Append(amount.ToString("x").PadLeft(64, '0'));
        }

        // streams (single stream)
        sb.Append("1".PadLeft(64, '0'));
        sb.Append("20".PadLeft(64, '0'));
        int streamDataOffset = 3 * 32;
        sb.Append(senderCoordinate.ToString("x").PadLeft(64, '0'));
        sb.Append(streamDataOffset.ToString("x").PadLeft(64, '0'));
        int flowEdgeIdsSize = 32 + terminalEdgeIds.Length * 32;
        sb.Append((streamDataOffset + flowEdgeIdsSize).ToString("x").PadLeft(64, '0'));
        sb.Append(terminalEdgeIds.Length.ToString("x").PadLeft(64, '0'));
        foreach (var id in terminalEdgeIds)
            sb.Append(id.ToString("x").PadLeft(64, '0'));
        sb.Append("0".PadLeft(64, '0'));

        // packedCoordinates
        var coordBytes = new byte[packedCoordinates.Length / 2];
        for (int i = 0; i < coordBytes.Length; i++)
            coordBytes[i] = Convert.ToByte(packedCoordinates.Substring(i * 2, 2), 16);
        sb.Append(coordBytes.Length.ToString("x").PadLeft(64, '0'));
        sb.Append(packedCoordinates.PadRight(((packedCoordinates.Length + 63) / 64) * 64, '0'));

        return sb.ToString();
    }
}
