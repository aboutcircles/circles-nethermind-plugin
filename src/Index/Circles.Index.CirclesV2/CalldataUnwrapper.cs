using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2;

/// <summary>
/// Recursively unwraps calldata from ERC-4337 and Safe wrapper contracts
/// until reaching actual Circles Hub calls that can be parsed for transfer data.
///
/// Supported wrappers:
/// - ERC-4337 handleOps (0x765e827f) - Account Abstraction entry point
/// - Safe execTransaction (0x6a761202) - Gnosis Safe transaction execution
/// - Safe multiSend (0x8d80ff0a) - Batched Safe transactions
/// </summary>
public static class CalldataUnwrapper
{
    // ERC-4337 EntryPoint.handleOps selector
    // handleOps(PackedUserOperation[] ops, address beneficiary)
    private static readonly byte[] HandleOpsSelector = [0x76, 0x5e, 0x82, 0x7f];

    // Safe.execTransaction selector
    // execTransaction(address to, uint256 value, bytes data, uint8 operation,
    //                 uint256 safeTxGas, uint256 baseGas, uint256 gasPrice,
    //                 address gasToken, address refundReceiver, bytes signatures)
    private static readonly byte[] ExecTransactionSelector = [0x6a, 0x76, 0x12, 0x02];

    // MultiSend.multiSend selector
    // multiSend(bytes transactions)
    private static readonly byte[] MultiSendSelector = [0x8d, 0x80, 0xff, 0x0a];

    /// <summary>
    /// Maximum recursion depth to prevent infinite loops from malformed data.
    /// 5 levels handles: handleOps → execTransaction → multiSend → execTransaction → Hub call
    /// </summary>
    private const int MaxDepth = 5;

    /// <summary>
    /// Recursively unwraps calldata from wrapper contracts (ERC-4337, Safe)
    /// until reaching actual Hub calls, then parses for transfer data.
    /// </summary>
    /// <param name="calldata">Raw transaction input data</param>
    /// <param name="depth">Current recursion depth (internal use)</param>
    /// <returns>Enumerable of (from address, to address, data bytes) tuples</returns>
    public static IEnumerable<(string From, string To, byte[] Data)> UnwrapAndParse(
        byte[] calldata,
        int depth = 0)
    {
        if (depth > MaxDepth || calldata.Length < 4)
            yield break;

        var selector = calldata.AsSpan(0, 4);

        // Check for wrapper contracts first
        if (selector.SequenceEqual(HandleOpsSelector))
        {
            foreach (var inner in UnwrapHandleOps(calldata))
            {
                foreach (var result in UnwrapAndParse(inner, depth + 1))
                    yield return result;
            }
        }
        else if (selector.SequenceEqual(ExecTransactionSelector))
        {
            var inner = UnwrapExecTransaction(calldata);
            if (inner != null && inner.Length > 0)
            {
                foreach (var result in UnwrapAndParse(inner, depth + 1))
                    yield return result;
            }
        }
        else if (selector.SequenceEqual(MultiSendSelector))
        {
            foreach (var inner in UnwrapMultiSend(calldata))
            {
                foreach (var result in UnwrapAndParse(inner, depth + 1))
                    yield return result;
            }
        }
        else
        {
            // Not a wrapper - try parsing as direct Hub call
            foreach (var result in TransferCalldataParser.ParseCalldata(calldata))
                yield return result;
        }
    }

    /// <summary>
    /// Unwraps ERC-4337 handleOps calldata to extract inner callData from each UserOperation.
    ///
    /// handleOps(PackedUserOperation[] ops, address beneficiary)
    ///
    /// PackedUserOperation struct (ERC-4337 v0.7):
    ///   address sender
    ///   uint256 nonce
    ///   bytes initCode
    ///   bytes callData         ← This is what we want
    ///   bytes32 accountGasLimits
    ///   uint256 preVerificationGas
    ///   bytes32 gasFees
    ///   bytes paymasterAndData
    ///   bytes signature
    /// </summary>
    private static IEnumerable<byte[]> UnwrapHandleOps(byte[] calldata)
    {
        var data = calldata.AsSpan();
        if (data.Length < 68) // selector(4) + ops offset(32) + beneficiary(32)
            return [];

        var params_ = data.Slice(4);

        // Collect results in a list to avoid yield in try-catch
        var results = new List<byte[]>();

        try
        {
            // First param is offset to ops array
            int opsOffset = LogDataParsingHelper.ParseOffset(params_, 0);

            if (opsOffset + 32 > params_.Length)
                return [];

            // Parse array length with overflow protection
            var opsLengthVal = new UInt256(params_.Slice(opsOffset, 32), true);
            if (opsLengthVal > int.MaxValue)
                return [];

            int opsLength = (int)opsLengthVal;

            // Sanity check - don't process unreasonably large arrays
            if (opsLength > 100 || opsLength < 0)
                return [];

            int arrayDataStart = opsOffset + 32;

            for (int i = 0; i < opsLength; i++)
            {
                // Each element is an offset to the struct (relative to array start)
                int elementOffsetPosition = arrayDataStart + i * 32;
                if (elementOffsetPosition + 32 > params_.Length)
                    break;

                int structOffset = LogDataParsingHelper.ParseOffset(params_, elementOffsetPosition);
                int absoluteStructOffset = opsOffset + structOffset;

                var innerCalldata = ExtractCallDataFromUserOp(params_, absoluteStructOffset);
                if (innerCalldata != null && innerCalldata.Length > 0)
                    results.Add(innerCalldata);
            }
        }
        catch (Exception)
        {
            // Malformed data - return empty
            return [];
        }

        return results;
    }

    /// <summary>
    /// Extracts callData from a PackedUserOperation struct.
    ///
    /// Struct layout (v0.7 packed format):
    ///   offset 0:   sender (address, 32 bytes padded)
    ///   offset 32:  nonce (uint256)
    ///   offset 64:  initCode offset
    ///   offset 96:  callData offset    ← We need this
    ///   offset 128: accountGasLimits (bytes32)
    ///   offset 160: preVerificationGas (uint256)
    ///   offset 192: gasFees (bytes32)
    ///   offset 224: paymasterAndData offset
    ///   offset 256: signature offset
    /// </summary>
    private static byte[]? ExtractCallDataFromUserOp(ReadOnlySpan<byte> params_, int structOffset)
    {
        // Need at least: sender(32) + nonce(32) + initCodeOff(32) + callDataOff(32) = 128 bytes
        if (structOffset + 128 > params_.Length)
            return null;

        try
        {
            // callData offset is at position 96 within the struct (4th field)
            int callDataOffsetInStruct = LogDataParsingHelper.ParseOffset(params_, structOffset + 96);
            int callDataAbsolute = structOffset + callDataOffsetInStruct;

            if (callDataAbsolute < 0 || callDataAbsolute + 32 > params_.Length)
                return null;

            return LogDataParsingHelper.ParseBytes(params_, callDataAbsolute);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Unwraps Safe execTransaction calldata to extract the inner call data.
    ///
    /// execTransaction(
    ///   address to,           // offset 0
    ///   uint256 value,        // offset 32
    ///   bytes data,           // offset 64 (pointer)  ← This is what we want
    ///   uint8 operation,      // offset 96
    ///   uint256 safeTxGas,    // offset 128
    ///   uint256 baseGas,      // offset 160
    ///   uint256 gasPrice,     // offset 192
    ///   address gasToken,     // offset 224
    ///   address refundReceiver, // offset 256
    ///   bytes signatures      // offset 288 (pointer)
    /// )
    /// </summary>
    private static byte[]? UnwrapExecTransaction(byte[] calldata)
    {
        var data = calldata.AsSpan();

        // Minimum: selector(4) + 10 params * 32 = 324 bytes
        if (data.Length < 324)
            return null;

        var params_ = data.Slice(4);

        try
        {
            // data is the 3rd parameter (offset at position 64)
            int dataOffset = LogDataParsingHelper.ParseOffset(params_, 64);

            if (dataOffset < 0 || dataOffset + 32 > params_.Length)
                return null;

            return LogDataParsingHelper.ParseBytes(params_, dataOffset);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Unwraps Safe multiSend calldata to extract individual transaction calls.
    ///
    /// multiSend(bytes transactions)
    ///
    /// The transactions parameter uses PACKED encoding (NOT standard ABI):
    /// Each transaction is: [operation(1)][to(20)][value(32)][dataLength(32)][data(variable)]
    ///
    /// operation: 0 = CALL, 1 = DELEGATECALL
    /// </summary>
    private static IEnumerable<byte[]> UnwrapMultiSend(byte[] calldata)
    {
        var data = calldata.AsSpan();

        // Minimum: selector(4) + offset(32) = 36 bytes
        if (data.Length < 36)
            yield break;

        var params_ = data.Slice(4);

        byte[] transactions;
        try
        {
            // transactions is bytes at offset 0
            int txOffset = LogDataParsingHelper.ParseOffset(params_, 0);
            transactions = LogDataParsingHelper.ParseBytes(params_, txOffset);
        }
        catch (Exception)
        {
            yield break;
        }

        if (transactions.Length == 0)
            yield break;

        // Parse packed transactions
        // Format: [op(1)][to(20)][value(32)][dataLen(32)][data(var)]...
        int pos = 0;
        int maxIterations = 1000; // Safety limit
        int iterations = 0;

        while (pos + 85 <= transactions.Length && iterations++ < maxIterations)
        {
            // Minimum per tx: operation(1) + to(20) + value(32) + dataLen(32) = 85 bytes

            // byte operation = transactions[pos]; // 0=CALL, 1=DELEGATECALL
            // We don't need to check operation for our purposes

            // address to = transactions[pos+1..pos+21];
            // We don't need to address for inner parsing

            // uint256 value = transactions[pos+21..pos+53];
            // We don't need value

            // uint256 dataLen = transactions[pos+53..pos+85];
            if (pos + 85 > transactions.Length)
                break;

            var dataLenSpan = transactions.AsSpan(pos + 53, 32);
            var dataLenVal = new UInt256(dataLenSpan, true);

            // Guard against overflow
            if (dataLenVal > int.MaxValue)
                break;

            int dataLen = (int)dataLenVal;

            // Sanity check
            if (dataLen < 0 || dataLen > transactions.Length)
                break;

            // data = transactions[pos+85..pos+85+dataLen]
            if (pos + 85 + dataLen > transactions.Length)
                break;

            byte[] innerData = transactions.AsSpan(pos + 85, dataLen).ToArray();
            if (innerData.Length > 0)
                yield return innerData;

            pos += 85 + dataLen;
        }
    }
}
