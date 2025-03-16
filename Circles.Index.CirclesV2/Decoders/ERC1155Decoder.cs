using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Decoders
{
    public static class Erc1155Decoder
    {
        // 4-byte selectors
        private static readonly byte[] SafeTransferFromSelector      = { 0xf2, 0x42, 0x43, 0x2a }; // safeTransferFrom
        private static readonly byte[] SafeTransferFromBatchSelector = { 0x2e, 0xb2, 0xc2, 0xd6 }; // safeBatchTransferFrom

        public static bool IsSafeTransferFrom(byte[] callData)
        {
            if (callData.Length < 4) return false;
            return callData[0] == SafeTransferFromSelector[0]
                   && callData[1] == SafeTransferFromSelector[1]
                   && callData[2] == SafeTransferFromSelector[2]
                   && callData[3] == SafeTransferFromSelector[3];
        }

        public static bool IsSafeTransferFromBatch(byte[] callData)
        {
            if (callData.Length < 4) return false;
            return callData[0] == SafeTransferFromBatchSelector[0]
                   && callData[1] == SafeTransferFromBatchSelector[1]
                   && callData[2] == SafeTransferFromBatchSelector[2]
                   && callData[3] == SafeTransferFromBatchSelector[3];
        }

        /// <summary>
        /// Decodes safeTransferFrom(address from, address to, uint256 id, uint256 amount, bytes data)
        /// Returns a tuple with all parameters, including the trailing bytes data argument.
        /// </summary>
        public static (Address from, Address to, UInt256 tokenId, UInt256 amount, byte[] dataPayload)
            DecodeSafeTransferFrom(byte[] callData)
        {
            // EIP-1155: safeTransferFrom(address,address,uint256,uint256,bytes)
            var fn = new AbiFunctionDescription
            {
                Name = "safeTransferFrom",
                Type = AbiDescriptionType.Function,
                Inputs = new[]
                {
                    new AbiParameter { Name = "from",   Type = AbiType.Address   },
                    new AbiParameter { Name = "to",     Type = AbiType.Address   },
                    new AbiParameter { Name = "id",     Type = AbiType.UInt256   },
                    new AbiParameter { Name = "amount", Type = AbiType.UInt256   },
                    new AbiParameter { Name = "data",   Type = AbiType.DynamicBytes }
                }
            };

            var abiDefinition = new AbiDefinition();
            abiDefinition.Add(fn);
            var function = abiDefinition.GetFunction("safeTransferFrom");
            var callInfo = function.GetCallInfo(AbiEncodingStyle.IncludeSignature); // matches the first 4 bytes

            var decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, callData);

            return (
                (Address)decoded[0],
                (Address)decoded[1],
                (UInt256)decoded[2],
                (UInt256)decoded[3],
                (byte[])decoded[4]
            );
        }

        /// <summary>
        /// Decodes safeBatchTransferFrom(address from, address to, uint256[] ids, uint256[] amounts, bytes data)
        /// Returns a tuple with all parameters, including the trailing bytes data argument.
        /// </summary>
        public static (Address from, Address to, UInt256[] ids, UInt256[] amounts, byte[] dataPayload)
            DecodeSafeTransferFromBatch(byte[] callData)
        {
            // EIP-1155: safeBatchTransferFrom(address,address,uint256[],uint256[],bytes)
            var fn = new AbiFunctionDescription
            {
                Name = "safeBatchTransferFrom",
                Type = AbiDescriptionType.Function,
                Inputs = new[]
                {
                    new AbiParameter { Name = "from",    Type = AbiType.Address    },
                    new AbiParameter { Name = "to",      Type = AbiType.Address    },
                    new AbiParameter { Name = "ids",     Type = new AbiArray(AbiType.UInt256) },
                    new AbiParameter { Name = "amounts", Type = new AbiArray(AbiType.UInt256) },
                    new AbiParameter { Name = "data",    Type = AbiType.DynamicBytes }
                }
            };

            var abiDefinition = new AbiDefinition();
            abiDefinition.Add(fn);
            var function = abiDefinition.GetFunction("safeBatchTransferFrom");
            var callInfo = function.GetCallInfo(AbiEncodingStyle.IncludeSignature);

            var decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, callData);

            return (
                (Address)decoded[0],
                (Address)decoded[1],
                (UInt256[])decoded[2],
                (UInt256[])decoded[3],
                (byte[])decoded[4]
            );
        }
    }
}
