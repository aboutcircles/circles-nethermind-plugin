using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Decoders
{
    /// <summary>
    /// Represents a Safe execTransaction(...) call.
    /// </summary>
    public class SafeTransaction
    {
        public Address To { get; set; }
        public UInt256 Value { get; set; }
        public byte[] Data { get; set; }
        public byte Operation { get; set; }
        public UInt256 SafeTxGas { get; set; }
        public UInt256 BaseGas { get; set; }
        public UInt256 GasPrice { get; set; }
        public Address GasToken { get; set; }
        public Address RefundReceiver { get; set; }
        public byte[] Signatures { get; set; }
    }

    public static class SafeTransactionDecoder
    {
        // 4-byte selector for Gnosis Safe execTransaction(...) is commonly 0x6a761202.
        private static readonly byte[] ExecTransactionSelector = { 0x6a, 0x76, 0x12, 0x02 };

        public static bool IsExecTransactionCall(byte[] txData)
        {
            if (txData.Length < 4) return false;
            return txData[0] == ExecTransactionSelector[0]
                && txData[1] == ExecTransactionSelector[1]
                && txData[2] == ExecTransactionSelector[2]
                && txData[3] == ExecTransactionSelector[3];
        }

        /// <summary>
        /// Decodes execTransaction(...) arguments into a MySafeTransaction object.
        /// </summary>
        public static SafeTransaction DecodeSafeTx(byte[] txData)
        {
            var abiDefinition = new AbiDefinition();

            var fn = new AbiFunctionDescription
            {
                Name = "execTransaction",
                Type = AbiDescriptionType.Function,
                Inputs = new[]
                {
                    new AbiParameter { Name = "to",             Type = AbiType.Address   },
                    new AbiParameter { Name = "value",          Type = AbiType.UInt256  },
                    new AbiParameter { Name = "data",           Type = AbiType.DynamicBytes },
                    new AbiParameter { Name = "operation",      Type = AbiType.UInt8    },
                    new AbiParameter { Name = "safeTxGas",      Type = AbiType.UInt256  },
                    new AbiParameter { Name = "baseGas",        Type = AbiType.UInt256  },
                    new AbiParameter { Name = "gasPrice",       Type = AbiType.UInt256  },
                    new AbiParameter { Name = "gasToken",       Type = AbiType.Address  },
                    new AbiParameter { Name = "refundReceiver", Type = AbiType.Address  },
                    new AbiParameter { Name = "signatures",     Type = AbiType.DynamicBytes }
                }
            };

            abiDefinition.Add(fn);
            var execTxFn = abiDefinition.GetFunction("execTransaction");
            var callInfo = execTxFn.GetCallInfo();
            object[] decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, txData);

            return new SafeTransaction
            {
                To             = (Address)decoded[0],
                Value          = (UInt256)decoded[1],
                Data           = (byte[])decoded[2],
                Operation      = (byte)decoded[3],
                SafeTxGas      = (UInt256)decoded[4],
                BaseGas        = (UInt256)decoded[5],
                GasPrice       = (UInt256)decoded[6],
                GasToken       = (Address)decoded[7],
                RefundReceiver = (Address)decoded[8],
                Signatures     = (byte[])decoded[9]
            };
        }
    }
}
