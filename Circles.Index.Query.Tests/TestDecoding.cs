// namespace Circles.Index.Query.Tests;
//
// using Nethermind.Abi;
// using Nethermind.Core;
// using Nethermind.Int256;
//
// public static class HandleOps4773Decoder
// {
//     // The 4-byte selector for function handleOps(...) is 0x765e827f.
//     private static readonly byte[] HandleOpsSelector = { 0x76, 0x5e, 0x82, 0x7f };
//
//     /// <summary>
//     /// Quick check: is data[0..4] = 0x765e827f?
//     /// If yes, it's the 4773 handleOps (ops[], beneficiary).
//     /// </summary>
//     public static bool IsHandleOpsCall(byte[] txData)
//     {
//         if (txData.Length < 4) return false;
//         return txData[0] == HandleOpsSelector[0]
//             && txData[1] == HandleOpsSelector[1]
//             && txData[2] == HandleOpsSelector[2]
//             && txData[3] == HandleOpsSelector[3];
//     }
//
//     /// <summary>
//     /// Decode the handleOps(UserOperation[], address) call, returning the
//     /// 1) userOps array and 2) beneficiary address.
//     ///
//     /// Note: Each userOp is a 9-field tuple in this variant:
//     /// (address,uint256,bytes,bytes,bytes32,uint256,bytes32,bytes,bytes).
//     /// </summary>
//     public static (MyUserOp[] userOps, Address beneficiary) DecodeHandleOpsTx(byte[] txData)
//     {
//         // Build an ABI definition for:
//         //   handleOps((address,uint256,bytes,bytes,bytes32,uint256,bytes32,bytes,bytes)[], address)
//         var abiDefinition = new AbiDefinition();
//
//         // Our custom "MyUserOp" is a 9-field struct. The reflection-based decoder
//         // will map fields by their order in the class:
//         var userOpType = new AbiTuple<MyUserOp>();
//
//         var opsParam = new AbiParameter
//         {
//             Name = "ops",
//             Type = new AbiArray(userOpType)
//         };
//
//         var beneficiaryParam = new AbiParameter
//         {
//             Name = "beneficiary",
//             Type = AbiType.Address
//         };
//
//         var fn = new AbiFunctionDescription
//         {
//             Name = "handleOps",
//             Type = AbiDescriptionType.Function,
//             Inputs = new[] { opsParam, beneficiaryParam }
//         };
//
//         abiDefinition.Add(fn);
//
//         // We decode with "IncludeSignature" because the first 4 bytes are the method selector.
//         var handleOpsFn = abiDefinition.GetFunction("handleOps", camelCase: false);
//         
//         var callInfo = handleOpsFn.GetCallInfo(AbiEncodingStyle.IncludeSignature);
//
//         // Decode into [ userOps[], beneficiary ]
//         var decoded = AbiEncoder.Instance.Decode(callInfo.EncodingStyle, callInfo.Signature, txData);
//         var userOps = (MyUserOp[])decoded[0];
//         var beneficiary = (Address)decoded[1];
//
//         return (userOps, beneficiary);
//     }
//
//     /// <summary>
//     /// Example usage: just logs the userOps + beneficiary to console.
//     /// </summary>
//     public static void ExampleProcessUserOps(byte[] txData)
//     {
//         var (userOps, beneficiary) = DecodeHandleOpsTx(txData);
//
//         Console.WriteLine($"Beneficiary: {beneficiary}");
//         Console.WriteLine($"Number of userOps: {userOps.Length}");
//
//         for (int i = 0; i < userOps.Length; i++)
//         {
//             var uo = userOps[i];
//             Console.WriteLine($"\nUserOp #{i}:");
//             Console.WriteLine($"  Sender               = {uo.Sender}");
//             Console.WriteLine($"  Nonce                = {uo.Nonce}");
//             Console.WriteLine($"  InitCode length      = {uo.InitCode?.Length ?? 0}");
//             Console.WriteLine($"  CallData length      = {uo.CallData?.Length ?? 0}");
//             Console.WriteLine($"  AccountGasLimits     = 0x{BitConverter.ToString(uo.AccountGasLimits).Replace("-", "")}");
//             Console.WriteLine($"  PreVerificationGas   = {uo.PreVerificationGas}");
//             Console.WriteLine($"  GasFees              = 0x{BitConverter.ToString(uo.GasFees).Replace("-", "")}");
//             Console.WriteLine($"  PaymasterAndData len = {uo.PaymasterAndData?.Length ?? 0}");
//             Console.WriteLine($"  Signature length     = {uo.Signature?.Length ?? 0}");
//         }
//     }
// }
//
// /// <summary>
// /// Matches the 9-field struct: (address,uint256,bytes,bytes,bytes32,uint256,bytes32,bytes,bytes).
// /// </summary>
// public class MyUserOp
// {
//     public Address Sender { get; set; }               // address
//     public UInt256 Nonce { get; set; }                // uint256
//     public byte[] InitCode { get; set; }              // bytes
//     public byte[] CallData { get; set; }              // bytes
//     [AbiTypeMapping(typeof(AbiBytes), 32)]
//     public byte[] AccountGasLimits { get; set; }      // bytes32
//     public UInt256 PreVerificationGas { get; set; }   // uint256
//     [AbiTypeMapping(typeof(AbiBytes), 32)]
//     public byte[] GasFees { get; set; }               // bytes32
//     public byte[] PaymasterAndData { get; set; }      // bytes
//     public byte[] Signature { get; set; }             // bytes
//
//     // parameterless constructor
//     public MyUserOp() {}
// }
//
//
// public class TestDecoding
// {
//     [SetUp]
//     public void Setup()
//     {
//     }
//
//     [Test]
//     public void Decode4773()
//     {
//         var txDataHex =
//             "765e827f000000000000000000000000000000000000000000000000000000000000004000000000000000000000000079c02f38dba39da361b4a0484c40351d50d55a94000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000200000000000000000000000008dd07bde1f1a4a03fb7930c26ab223124f9964ac00000000000000000000000000000000000000000000000000000000000000290000000000000000000000000000000000000000000000000000000000000120000000000000000000000000000000000000000000000000000000000000014000000000000000000000000000065af100000000000000000000000000033e2c0000000000000000000000000000000000000000000000000000000000011ec100000000000000000000000077359410000000000000000000000000b2d05e18000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000004e000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000284541d63c800000000000000000000000038869bf66a61cf6bdb996a6ae40d5853fd43b52600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000001c48d80ff0a0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000017200c12c1e50abb450d6205ea2c3fa861b3b834d13e8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040d873a7900c12c1e50abb450d6205ea2c3fa861b3b834d13e8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c4f242432a0000000000000000000000008dd07bde1f1a4a03fb7930c26ab223124f9964ac00000000000000000000000097fd8f7829a019946329f6d2e763a727410475180000000000000000000000008dd07bde1f1a4a03fb7930c26ab223124f9964ac00000000000000000000000000000000000000000000000038a744897d3c6f8800000000000000000000000000000000000000000000000000000000000000a000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b56a6b7f6012ee5bef1cdf95df25e5045c7727c739000000000000000000000000000927c000000000000000000000000000004e200000000000000000000000000000000000000000000000000000000067c783050000000000000000000000000000000000000000000000000000000000001234331066306d59eac67bece8420943d5f04c1a4fb3056a799cbe6ab2c9d026898d1f1af30c9c439fb2c8abc9a92e2aff4b8d00c6e959ae22a4355d8a6da10d9a6a1b000000000000000000000000000000000000000000000000000000000000000000000000000000000001ad000000000000000000000000000000000000000000000000fd90fad33ee8b58f32c00aceead1358e4afc23f90000000000000000000000000000000000000000000000000000000000000041000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000e09a977a6afe3f9b5d6ce6d1d2a6cde78958b6df235038ee8fc19a48350575d69a86fb1fff39e667377102a603b95fd172830a24705398e99fff514adc2349d817000000000000000000000000000000000000000000000000000000000000002532e56ff54526026631f60275192284efbb70a1489e38d70aaa331d67827e5eb71d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000034226f726967696e223a2268747470733a2f2f6170702e6d657472692e78797a222c2263726f73734f726967696e223a66616c736500000000000000000000000000000000000000000000000000000000000000";
//         
//         byte[] txDataBytes = Enumerable.Range(0, txDataHex.Length / 2)
//             .Select(i => Convert.ToByte(txDataHex.Substring(i * 2, 2), 16))
//             .ToArray();
//
//
//         Assert.That(HandleOps4773Decoder.IsHandleOpsCall(txDataBytes), Is.True);
//         
//         var result = HandleOps4773Decoder.DecodeHandleOpsTx(txDataBytes);
//     }
// }