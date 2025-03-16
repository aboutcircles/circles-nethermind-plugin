using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Decoders
{
    /// <summary>
    /// Decoder for ERC-4773 handleOps((address,uint256,bytes,bytes,bytes32,uint256,bytes32,bytes,bytes)[], address).
    /// </summary>
    public static class HandleOps4773Decoder
    {
        // 4-byte selector for handleOps(...) is 0x765e827f.
        private static readonly byte[] HandleOpsSelector = { 0x76, 0x5e, 0x82, 0x7f };

        public static bool IsHandleOpsCall(byte[] txData)
        {
            if (txData.Length < 4) return false;
            return txData[0] == HandleOpsSelector[0]
                && txData[1] == HandleOpsSelector[1]
                && txData[2] == HandleOpsSelector[2]
                && txData[3] == HandleOpsSelector[3];
        }

        /// <summary>
        /// Decodes handleOps(...) data into a set of MyUserOp objects and a beneficiary address.
        /// </summary>
        public static (UserOp[] userOps, Address beneficiary) DecodeHandleOpsTx(byte[] txData)
        {
            var abiDefinition = new AbiDefinition();

            var userOpType = new AbiTuple<UserOp>();
            var opsParam = new AbiParameter { Name = "ops", Type = new AbiArray(userOpType) };
            var beneficiaryParam = new AbiParameter { Name = "beneficiary", Type = AbiType.Address };

            var fn = new AbiFunctionDescription
            {
                Name = "handleOps",
                Type = AbiDescriptionType.Function,
                Inputs = new[] { opsParam, beneficiaryParam }
            };

            abiDefinition.Add(fn);
            var handleOpsFn = abiDefinition.GetFunction("handleOps", camelCase: false);
            var callInfo = handleOpsFn.GetCallInfo();

            var decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, txData);
            return ((UserOp[])decoded[0], (Address)decoded[1]);
        }
    }

    /// <summary>
    /// Matches the 9-field struct: (address,uint256,bytes,bytes,bytes32,uint256,bytes32,bytes,bytes).
    /// </summary>
    public class UserOp
    {
        public Address Sender { get; set; }
        public UInt256 Nonce { get; set; }
        public byte[] InitCode { get; set; }
        public byte[] CallData { get; set; }

        [AbiTypeMapping(typeof(AbiBytes), 32)]
        public byte[] AccountGasLimits { get; set; }

        public UInt256 PreVerificationGas { get; set; }

        [AbiTypeMapping(typeof(AbiBytes), 32)]
        public byte[] GasFees { get; set; }

        public byte[] PaymasterAndData { get; set; }
        public byte[] Signature { get; set; }
    }
}
