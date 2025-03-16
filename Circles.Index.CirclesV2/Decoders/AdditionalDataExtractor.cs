using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Decoders
{
    public static class AdditionalDataExtractor
    {
        public static byte[] ExtractAdditionalData(Transaction transaction)
        {
            // Main dispatch logic to see if it's a Safe call, a 4337 handleOps call, etc.
            var dataToDecode = transaction.Data?.ToArray() ?? [];

            if (SafeTransactionDecoder.IsExecTransactionCall(dataToDecode))
            {
                var callData = ExtractFromSafeTransaction(dataToDecode);
                return GetAdditionalData("Safe", callData);
            }
            else if (HandleOps4773Decoder.IsHandleOpsCall(dataToDecode))
            {
                var callData = ExtractFromHandleOps(dataToDecode);
                return GetAdditionalData("AA", callData);
            }
            else
            {
                // Possibly an EOA direct call
                return GetAdditionalData("EOA", dataToDecode);
            }
        }

        private static byte[] ExtractFromSafeTransaction(byte[] data)
        {
            var safeTx = SafeTransactionDecoder.DecodeSafeTx(data);
            return safeTx.Data ?? [];
        }

        private static byte[] ExtractFromHandleOps(byte[] data)
        {
            var (userOps, _) = HandleOps4773Decoder.DecodeHandleOpsTx(data);

            foreach (var uo in userOps)
            {
                if (uo.CallData?.Length >= 4
                    && uo.CallData[0] == 0x54
                    && uo.CallData[1] == 0x1d
                    && uo.CallData[2] == 0x63
                    && uo.CallData[3] == 0xc8)
                {
                    var decoded = DecodeExecuteUserOpWithErrorString(uo.CallData);
                    return decoded.dataPayload;
                }
            }

            return [];
        }

        /// <summary>
        /// Decodes executeUserOpWithErrorString(address,uint256,bytes,uint8).
        /// 4-byte selector: 0x541d63c8
        /// </summary>
        private static (Address to, UInt256 value, byte[] dataPayload, byte operation)
            DecodeExecuteUserOpWithErrorString(byte[] callData)
        {
            var fn = new AbiFunctionDescription
            {
                Name = "executeUserOpWithErrorString",
                Type = AbiDescriptionType.Function,
                Inputs =
                [
                    new AbiParameter { Name = "to", Type = AbiType.Address },
                    new AbiParameter { Name = "value", Type = AbiType.UInt256 },
                    new AbiParameter { Name = "data", Type = AbiType.DynamicBytes },
                    new AbiParameter { Name = "operation", Type = AbiType.UInt8 }
                ]
            };

            var abiDefinition = new AbiDefinition();
            abiDefinition.Add(fn);
            var function = abiDefinition.GetFunction("executeUserOpWithErrorString", camelCase: false);
            var callInfo = function.GetCallInfo();

            var decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, callData);

            return (
                (Address)decoded[0],
                (UInt256)decoded[1],
                (byte[])decoded[2],
                (byte)decoded[3]
            );
        }

        private static byte[] GetAdditionalData(string type, byte[] callData)
        {
            // Additional checks on the resulting callData, e.g.:
            if (CirclesHubDecoder.IsOperateFlowMatrixCall(callData))
            {
                Console.WriteLine($"Handling -> operateFlowMatrix ({type})");
                var operateFlowMatrix = CirclesHubDecoder.DecodeOperateFlowMatrix(callData);
                if (operateFlowMatrix.streams.Length > 0)
                {
                    return operateFlowMatrix.streams[0].Data;
                }
            }
            else if (Erc1155Decoder.IsSafeTransferFrom(callData))
            {
                Console.WriteLine($"Handling -> ERC1155 safeTransferFrom ({type})");
                var (from, to, tokenId, amount, dataPayload) = Erc1155Decoder.DecodeSafeTransferFrom(callData);
                return dataPayload;
            }
            else if (Erc1155Decoder.IsSafeTransferFromBatch(callData))
            {
                Console.WriteLine($"Handling -> ERC1155 safeTransferFrom ({type})");
                var (from, to, tokenIds, amounts, dataPayload) = Erc1155Decoder.DecodeSafeTransferFromBatch(callData);
                return dataPayload;
            }
            else
            {
                // Unknown
            }

            return [];
        }
    }
}