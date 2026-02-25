using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Common.TestUtils;
using Nethereum.Util;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Helper for executing pathfinder transfer paths on an Anvil fork.
///
/// This enables end-to-end testing of the mint-along-path feature by:
/// 1. Taking sorted transfer paths from the pathfinder
/// 2. Building operateFlowMatrix call data
/// 3. Executing on an Anvil fork and checking success/failure
///
/// Supports two modes:
/// - Proxied: Uses TestEnvironmentClient.ExecuteAnvilRpcAsync (works from anywhere)
/// - Direct: Uses direct HTTP to Anvil URL (only works from staging CI)
/// </summary>
public class AnvilExecutionHelper : IDisposable
{
    private readonly HttpClient? _httpClient;
    private readonly string? _anvilUrl;
    private readonly TestEnvironmentClient? _session;

    // Circles Hub V2 contract address on Gnosis
    public const string CirclesHubAddress = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8";

    /// <summary>
    /// Creates an AnvilExecutionHelper that uses proxied RPC calls through the test environment.
    /// This is the recommended mode for external clients.
    /// </summary>
    /// <param name="session">The test environment session with Anvil enabled.</param>
    public AnvilExecutionHelper(TestEnvironmentClient session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));

        if (!session.HasAnvil)
        {
            throw new InvalidOperationException(
                "Session does not have Anvil enabled. Create session with features: [\"anvil\"]");
        }
    }

    /// <summary>
    /// Creates an AnvilExecutionHelper with direct HTTP access to an Anvil URL.
    /// This only works when the Anvil URL is reachable (e.g., staging CI).
    /// </summary>
    /// <param name="anvilUrl">The Anvil RPC URL.</param>
    [Obsolete("Use the TestEnvironmentClient constructor for external access. Direct URL only works internally.")]
    public AnvilExecutionHelper(string anvilUrl)
    {
        _anvilUrl = anvilUrl.TrimEnd('/');
        _httpClient = new HttpClient { BaseAddress = new Uri(_anvilUrl) };
    }

    /// <summary>
    /// Gets the current block number from Anvil.
    /// </summary>
    public async Task<long> GetBlockNumberAsync()
    {
        var response = await CallRpcAsync<string>("eth_blockNumber");
        return Convert.ToInt64(response, 16);
    }

    /// <summary>
    /// Gets the timestamp of the current block from Anvil.
    /// </summary>
    public async Task<DateTimeOffset> GetBlockTimestampAsync()
    {
        var blockNumber = await CallRpcAsync<string>("eth_blockNumber");
        var block = await CallRpcAsync<JsonElement>("eth_getBlockByNumber", blockNumber, false);
        var timestampHex = block.GetProperty("timestamp").GetString();
        var timestamp = Convert.ToInt64(timestampHex, 16);
        return DateTimeOffset.FromUnixTimeSeconds(timestamp);
    }

    /// <summary>
    /// Takes a snapshot of Anvil state. Returns a snapshot ID for RevertAsync.
    /// Use this before stateful operations (eth_sendTransaction) on shared sessions.
    /// </summary>
    public async Task<string> SnapshotAsync()
    {
        return await CallRpcAsync<string>("evm_snapshot");
    }

    /// <summary>
    /// Reverts Anvil state to a previous snapshot. The snapshot is consumed (single-use).
    /// </summary>
    public async Task RevertAsync(string snapshotId)
    {
        await CallRpcAsync<bool>("evm_revert", snapshotId);
    }

    /// <summary>
    /// Gets the balance of an ERC1155 token for an account.
    /// </summary>
    public async Task<BigInteger> GetErc1155BalanceAsync(string tokenContract, string account, BigInteger tokenId)
    {
        // Build balanceOf(address,uint256) call
        var selector = "0x00fdd58e"; // balanceOf(address,uint256)
        var data = selector + account.Substring(2).PadLeft(64, '0') + tokenId.ToString("x").PadLeft(64, '0');

        var result = await CallRpcAsync<string>("eth_call", new
        {
            to = tokenContract,
            data
        }, "latest");

        return BigInteger.Parse("0" + result.Substring(2), System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// Impersonates an account on Anvil for testing.
    /// </summary>
    public async Task ImpersonateAccountAsync(string address)
    {
        await CallRpcAsync<bool>("anvil_impersonateAccount", address);
    }

    /// <summary>
    /// Stops impersonating an account.
    /// </summary>
    public async Task StopImpersonatingAccountAsync(string address)
    {
        await CallRpcAsync<bool>("anvil_stopImpersonatingAccount", address);
    }

    /// <summary>
    /// Sets the ETH balance of an account (for gas).
    /// </summary>
    public async Task SetBalanceAsync(string address, BigInteger weiAmount)
    {
        var hexAmount = "0x" + weiAmount.ToString("x");
        await CallRpcAsync<bool>("anvil_setBalance", address, hexAmount);
    }

    /// <summary>
    /// Executes a raw transaction and returns the result.
    /// </summary>
    public async Task<ExecutionResult> ExecuteTransactionAsync(
        string from,
        string to,
        string data,
        BigInteger? value = null,
        BigInteger? gas = null)
    {
        try
        {
            // Impersonate the sender
            await ImpersonateAccountAsync(from);

            // Ensure sender has ETH for gas
            await SetBalanceAsync(from, BigInteger.Parse("10000000000000000000")); // 10 ETH

            var txParams = new Dictionary<string, object>
            {
                { "from", from },
                { "to", to },
                { "data", data }
            };

            if (value.HasValue)
            {
                txParams["value"] = "0x" + value.Value.ToString("x");
            }

            if (gas.HasValue)
            {
                txParams["gas"] = "0x" + gas.Value.ToString("x");
            }

            // Send transaction
            var txHash = await CallRpcAsync<string>("eth_sendTransaction", txParams);

            // Get receipt
            var receipt = await GetTransactionReceiptAsync(txHash);

            await StopImpersonatingAccountAsync(from);

            var status = receipt.GetProperty("status").GetString();
            var gasUsed = Convert.ToInt64(receipt.GetProperty("gasUsed").GetString(), 16);

            if (status == "0x1")
            {
                return new ExecutionResult
                {
                    Success = true,
                    TxHash = txHash,
                    GasUsed = gasUsed
                };
            }
            else
            {
                // Transaction reverted - try to get revert reason
                var revertReason = await TryGetRevertReasonAsync(from, to, data);
                return new ExecutionResult
                {
                    Success = false,
                    TxHash = txHash,
                    GasUsed = gasUsed,
                    Error = revertReason ?? "Transaction reverted"
                };
            }
        }
        catch (Exception ex)
        {
            return new ExecutionResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Tries to get the revert reason by simulating the call.
    /// </summary>
    private async Task<string?> TryGetRevertReasonAsync(string from, string to, string data)
    {
        try
        {
            await CallRpcAsync<string>("eth_call", new
            {
                from,
                to,
                data
            }, "latest");
            return null;
        }
        catch (Exception ex)
        {
            // Parse revert reason from error message
            var message = ex.Message;
            if (message.Contains("revert"))
            {
                return message;
            }
            return message;
        }
    }

    private async Task<JsonElement> GetTransactionReceiptAsync(string txHash)
    {
        // Poll for receipt with exponential backoff.
        // Anvil proxied through test-env can be slow under load.
        const int maxAttempts = 20;
        var delay = 200;

        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                var result = await CallRpcAsync<JsonElement>("eth_getTransactionReceipt", txHash);
                if (result.ValueKind != JsonValueKind.Null)
                {
                    return result;
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429") || ex.Message.Contains("TooManyRequests"))
            {
                // Rate limited — back off more aggressively
                delay = Math.Min(delay * 2, 5000);
            }
            catch
            {
                // Receipt not ready yet
            }

            await Task.Delay(delay);
            delay = Math.Min(delay + 200, 3000);
        }

        throw new TimeoutException($"Transaction receipt not found for {txHash} after {maxAttempts} attempts");
    }

    private async Task<T> CallRpcAsync<T>(string method, params object[] parameters)
    {
        JsonElement result;
        const int maxRetries = 3;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (_session != null)
                {
                    // Use proxied RPC through test environment
                    result = await _session.ExecuteAnvilRpcAsync(method, parameters);
                }
                else if (_httpClient != null)
                {
                    // Use direct HTTP (legacy mode for internal access)
                    var response = await _httpClient.PostAsJsonAsync("", new
                    {
                        jsonrpc = "2.0",
                        method,
                        @params = parameters,
                        id = 1
                    });

                    var json = await response.Content.ReadFromJsonAsync<JsonElement>();

                    if (json.TryGetProperty("error", out var error))
                    {
                        var message = error.TryGetProperty("message", out var msg)
                            ? msg.GetString()
                            : "RPC error";
                        throw new Exception(message);
                    }

                    result = json.GetProperty("result");
                }
                else
                {
                    throw new InvalidOperationException("No RPC connection available");
                }

                break; // success
            }
            catch (HttpRequestException ex) when (
                attempt < maxRetries &&
                (ex.Message.Contains("429") || ex.Message.Contains("TooManyRequests")))
            {
                await Task.Delay(1000 * attempt); // exponential backoff
            }
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)result.GetString()!;
        }
        else if (typeof(T) == typeof(bool))
        {
            // Some Anvil methods return null for success (e.g., anvil_impersonateAccount, anvil_setBalance)
            if (result.ValueKind == JsonValueKind.Null)
            {
                return (T)(object)true;
            }
            return (T)(object)result.GetBoolean();
        }
        else if (typeof(T) == typeof(JsonElement))
        {
            return (T)(object)result;
        }
        else
        {
            return JsonSerializer.Deserialize<T>(result.GetRawText())!;
        }
    }

    /// <summary>
    /// Executes a pathfinder-computed transfer path on the Circles Hub V2 contract.
    /// </summary>
    public async Task<ExecutionResult> ExecuteTransferPathAsync(
        string sender,
        string receiver,
        List<TransferPathStep> transfers)
    {
        var callData = BuildOperateFlowMatrixCall(sender, receiver, transfers);
        return await ExecuteTransactionAsync(sender, CirclesHubAddress, callData);
    }

    /// <summary>
    /// Simulates a transfer path using eth_call (no state change).
    /// Returns whether the contract would accept or reject the calldata.
    /// </summary>
    public async Task<(bool Success, string? RevertReason)> SimulateTransferPathAsync(
        string sender,
        string receiver,
        List<TransferPathStep> transfers)
    {
        var callData = BuildOperateFlowMatrixCall(sender, receiver, transfers);

        try
        {
            await CallRpcAsync<string>("eth_call", new
            {
                from = sender,
                to = CirclesHubAddress,
                data = callData
            }, "latest");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Builds the calldata for Hub.operateFlowMatrix from pathfinder transfer steps.
    ///
    /// The Hub.operateFlowMatrix function signature is:
    /// operateFlowMatrix(address[] flowVertices, FlowEdge[] flow, Stream[] streams, bytes packedCoordinates)
    ///
    /// Where:
    /// - FlowEdge = (uint16 streamSinkId, uint192 amount)
    /// - Stream = (uint16 sourceCoordinate, uint16[] flowEdgeIds, bytes data)
    /// </summary>
    public static string BuildOperateFlowMatrixCall(
        string sender,
        string receiver,
        List<TransferPathStep> transfers)
    {
        // Step 1: Build sorted vertex list (all unique addresses)
        var vertexSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        vertexSet.Add(sender.ToLowerInvariant());
        vertexSet.Add(receiver.ToLowerInvariant());
        foreach (var t in transfers)
        {
            vertexSet.Add(t.From.ToLowerInvariant());
            vertexSet.Add(t.To.ToLowerInvariant());
            vertexSet.Add(t.TokenOwner.ToLowerInvariant());
        }

        // Sort by numeric value (as BigInteger) for deterministic ordering
        var flowVertices = vertexSet
            .OrderBy(v => BigInteger.Parse("0" + v.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber))
            .ToList();

        // Build index lookup
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < flowVertices.Count; i++)
        {
            idx[flowVertices[i]] = i;
        }

        // Step 2: Build flow edges and coordinates
        var flowEdges = new List<(ushort streamSinkId, BigInteger amount)>();
        var coordinates = new List<ushort>();
        var receiverLower = receiver.ToLowerInvariant();
        var senderLower = sender.ToLowerInvariant();

        // Find terminal edge indices (edges that flow to receiver)
        var terminalEdgeIndices = new List<int>();

        for (int i = 0; i < transfers.Count; i++)
        {
            var t = transfers[i];
            var amount = BigInteger.Parse(t.Value);
            var toAddr = t.To.ToLowerInvariant();

            // streamSinkId = 1 if this edge goes to receiver, 0 otherwise
            ushort streamSinkId = toAddr == receiverLower ? (ushort)1 : (ushort)0;

            flowEdges.Add((streamSinkId, amount));

            if (streamSinkId == 1)
            {
                terminalEdgeIndices.Add(i);
            }

            // Pack coordinates: tokenOwner, from, to
            coordinates.Add((ushort)idx[t.TokenOwner.ToLowerInvariant()]);
            coordinates.Add((ushort)idx[t.From.ToLowerInvariant()]);
            coordinates.Add((ushort)idx[toAddr]);
        }

        // Ensure at least one terminal edge exists
        if (terminalEdgeIndices.Count == 0 && transfers.Count > 0)
        {
            // Use last edge as terminal
            flowEdges[transfers.Count - 1] = (1, flowEdges[transfers.Count - 1].amount);
            terminalEdgeIndices.Add(transfers.Count - 1);
        }

        // Step 3: Build stream (single stream from sender to all terminal edges)
        var senderCoordinate = (ushort)idx[senderLower];
        var terminalEdgeIds = terminalEdgeIndices.Select(i => (ushort)i).ToArray();

        // Step 4: Pack coordinates into bytes
        var packedCoordinates = PackCoordinates(coordinates);

        // Step 5: Encode the ABI call
        return EncodeOperateFlowMatrix(flowVertices, flowEdges, senderCoordinate, terminalEdgeIds, packedCoordinates);
    }

    /// <summary>
    /// Packs uint16 coordinates into a hex string (big-endian).
    /// </summary>
    private static string PackCoordinates(List<ushort> coordinates)
    {
        var bytes = new byte[coordinates.Count * 2];
        for (int i = 0; i < coordinates.Count; i++)
        {
            bytes[i * 2] = (byte)(coordinates[i] >> 8);     // high byte
            bytes[i * 2 + 1] = (byte)(coordinates[i] & 0xFF); // low byte
        }
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// ABI-encodes the operateFlowMatrix call.
    /// operateFlowMatrix(address[] flowVertices, FlowEdge[] flow, Stream[] streams, bytes packedCoordinates)
    /// </summary>
    private static string EncodeOperateFlowMatrix(
        List<string> flowVertices,
        List<(ushort streamSinkId, BigInteger amount)> flowEdges,
        ushort senderCoordinate,
        ushort[] terminalEdgeIds,
        string packedCoordinates)
    {
        var keccak = new Sha3Keccack();
        var methodSig = "operateFlowMatrix(address[],(uint16,uint192)[],(uint16,uint16[],bytes)[],bytes)";
        var methodId = keccak.CalculateHash(methodSig).Substring(0, 8);

        var sb = new StringBuilder();
        sb.Append("0x");
        sb.Append(methodId);

        // Dynamic type offsets (4 arrays = 4 offsets, each 32 bytes)
        // We'll calculate actual positions as we go
        int currentOffset = 4 * 32; // Start after the 4 offset slots

        // Calculate sizes for offset computation
        int flowVerticesSize = 32 + flowVertices.Count * 32; // length + data
        int flowEdgesSize = 32 + flowEdges.Count * 64;       // length + (32+32 per edge)
        int streamsSize = 32 + CalculateStreamArraySize(new[] { (senderCoordinate, terminalEdgeIds, "") });
        int packedCoordinatesSize = 32 + ((packedCoordinates.Length / 2 + 31) / 32) * 32; // length + padded data

        // Offset to flowVertices array
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));
        int offset1 = currentOffset;
        currentOffset += flowVerticesSize;

        // Offset to flowEdges array
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));
        currentOffset += flowEdgesSize;

        // Offset to streams array
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));
        currentOffset += streamsSize;

        // Offset to packedCoordinates
        sb.Append(currentOffset.ToString("x").PadLeft(64, '0'));

        // Encode flowVertices array
        sb.Append(flowVertices.Count.ToString("x").PadLeft(64, '0'));
        foreach (var v in flowVertices)
        {
            sb.Append(v.Replace("0x", "").PadLeft(64, '0'));
        }

        // Encode flowEdges array (each element is a tuple: (uint16, uint192))
        sb.Append(flowEdges.Count.ToString("x").PadLeft(64, '0'));
        foreach (var (streamSinkId, amount) in flowEdges)
        {
            // uint16 streamSinkId (padded to 32 bytes)
            sb.Append(streamSinkId.ToString("x").PadLeft(64, '0'));
            // uint192 amount (padded to 32 bytes)
            sb.Append(amount.ToString("x").PadLeft(64, '0'));
        }

        // Encode streams array (single stream)
        sb.Append("1".PadLeft(64, '0')); // array length = 1

        // Stream is a dynamic struct, so we need offset
        sb.Append("20".PadLeft(64, '0')); // offset to first stream (32 bytes = 0x20)

        // Encode the stream struct: (uint16 sourceCoordinate, uint16[] flowEdgeIds, bytes data)
        // For dynamic struct, we encode offsets then data
        int streamDataOffset = 3 * 32; // 3 slots: sourceCoordinate, offset to flowEdgeIds, offset to data

        // sourceCoordinate
        sb.Append(senderCoordinate.ToString("x").PadLeft(64, '0'));

        // offset to flowEdgeIds (relative to stream start)
        sb.Append(streamDataOffset.ToString("x").PadLeft(64, '0'));

        int flowEdgeIdsSize = 32 + terminalEdgeIds.Length * 32;
        // offset to data (relative to stream start)
        sb.Append((streamDataOffset + flowEdgeIdsSize).ToString("x").PadLeft(64, '0'));

        // Encode flowEdgeIds array
        sb.Append(terminalEdgeIds.Length.ToString("x").PadLeft(64, '0'));
        foreach (var id in terminalEdgeIds)
        {
            sb.Append(id.ToString("x").PadLeft(64, '0'));
        }

        // Encode data (empty bytes)
        sb.Append("0".PadLeft(64, '0')); // empty bytes length

        // Encode packedCoordinates
        var coordBytes = HexToBytes(packedCoordinates);
        sb.Append(coordBytes.Length.ToString("x").PadLeft(64, '0'));
        sb.Append(packedCoordinates.PadRight(((packedCoordinates.Length + 63) / 64) * 64, '0'));

        return sb.ToString();
    }

    private static int CalculateStreamArraySize((ushort, ushort[], string)[] streams)
    {
        // For a single stream with empty data
        // 32 (array offset) + 32 (sourceCoord) + 32 (offset to edgeIds) + 32 (offset to data)
        // + 32 (edgeIds length) + N*32 (edgeIds) + 32 (data length)
        var stream = streams[0];
        return 32 + 32 + 32 + 32 + 32 + stream.Item2.Length * 32 + 32;
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x")) hex = hex.Substring(2);
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Result of executing a transaction on Anvil.
/// </summary>
public class ExecutionResult
{
    public bool Success { get; init; }
    public string? TxHash { get; init; }
    public long GasUsed { get; init; }
    public string? Error { get; init; }
    public string? RevertData { get; init; }
}
