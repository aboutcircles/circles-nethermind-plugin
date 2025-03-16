using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Decoders
{
    /// <summary>
    /// Mirror of Solidity's FlowEdge struct (uint16 streamSinkId, uint192 amount).
    /// We store both as UInt256 for simplicity.
    /// </summary>
    public class FlowEdge
    {
        // 2 byte
        public UInt16 StreamSinkId { get; set; }

        // 24 byte
        [AbiTypeMapping(typeof(AbiUInt), 192)] public UInt256 Amount { get; set; }
    }

    /// <summary>
    /// Mirror of Solidity's Stream struct (uint16 sourceCoordinate, uint16[] flowEdgeIds, bytes data).
    /// Again, use UInt256 for the 16-bit field and array of 16-bit fields to keep it simple in the Nethermind ABI.
    /// </summary>
    public class Stream
    {
        public UInt16 SourceCoordinate { get; set; }
        public UInt16[] FlowEdgeIds { get; set; }
        public byte[] Data { get; set; }
    }

    public static class CirclesHubDecoder
    {
        // 4-byte selector for operateFlowMatrix(...) is 0x0d22d9b5.
        private static readonly byte[] OperateFlowMatrixSelector = { 0x0d, 0x22, 0xd9, 0xb5 };

        public static bool IsOperateFlowMatrixCall(byte[] callData)
        {
            if (callData.Length < 4) return false;
            return callData[0] == OperateFlowMatrixSelector[0]
                   && callData[1] == OperateFlowMatrixSelector[1]
                   && callData[2] == OperateFlowMatrixSelector[2]
                   && callData[3] == OperateFlowMatrixSelector[3];
        }

        /// <summary>
        /// Decodes:
        ///    operateFlowMatrix(
        ///       address[] _flowVertices,
        ///       (uint16,uint192)[] _flow,
        ///       (uint16,uint16[],bytes)[] _streams,
        ///       bytes _packedCoordinates
        ///    )
        /// into (Address[] flowVertices, FlowEdge[] flow, Stream[] streams, byte[] packedCoordinates).
        /// </summary>
        public static (
            Address[] flowVertices,
            FlowEdge[] flow,
            Stream[] streams,
            byte[] packedCoordinates
            )
            DecodeOperateFlowMatrix(byte[] callData)
        {
            // Build reflection-based structs for FlowEdge + Stream
            var flowEdgeType = new AbiTuple<FlowEdge>();
            var streamType = new AbiTuple<Stream>();

            // Define the function signature
            var fn = new AbiFunctionDescription
            {
                Name = "operateFlowMatrix",
                Type = AbiDescriptionType.Function,
                Inputs = new[]
                {
                    new AbiParameter
                    {
                        Name = "_flowVertices",
                        Type = new AbiArray(AbiType.Address)
                    },
                    new AbiParameter
                    {
                        Name = "_flow",
                        Type = new AbiArray(flowEdgeType)
                    },
                    new AbiParameter
                    {
                        Name = "_streams",
                        Type = new AbiArray(streamType)
                    },
                    new AbiParameter
                    {
                        Name = "_packedCoordinates",
                        Type = AbiType.DynamicBytes
                    }
                }
            };

            var abiDefinition = new AbiDefinition();
            abiDefinition.Add(fn);

            // Retrieve the function definition
            var function = abiDefinition.GetFunction("operateFlowMatrix");
            var callInfo = function.GetCallInfo();

            // Decode the transaction data into [Address[], FlowEdge[], Stream[], bytes]
            var decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, callData);

            return (
                (Address[])decoded[0],
                (FlowEdge[])decoded[1],
                (Stream[])decoded[2],
                (byte[])decoded[3]
            );
        }
    }
}