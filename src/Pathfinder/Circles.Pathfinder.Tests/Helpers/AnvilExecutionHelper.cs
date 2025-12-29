using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Circles.Index.Common.Dto;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// Helper for executing pathfinder transfer paths on an Anvil fork.
///
/// This enables end-to-end testing of the mint-along-path feature by:
/// 1. Taking sorted transfer paths from the pathfinder
/// 2. Building operateFlowMatrix call data
/// 3. Executing on an Anvil fork and checking success/failure
/// </summary>
public class AnvilExecutionHelper : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _anvilUrl;

    // Circles Hub V2 contract address on Gnosis
    public const string CirclesHubAddress = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8";

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
        // Poll for receipt (transaction might not be mined immediately)
        for (int i = 0; i < 10; i++)
        {
            var response = await _httpClient.PostAsJsonAsync("", new
            {
                jsonrpc = "2.0",
                method = "eth_getTransactionReceipt",
                @params = new object[] { txHash },
                id = 1
            });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Transaction receipt not found for {txHash}");
    }

    private async Task<T> CallRpcAsync<T>(string method, params object[] parameters)
    {
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

        var result = json.GetProperty("result");

        if (typeof(T) == typeof(string))
        {
            return (T)(object)result.GetString()!;
        }
        else if (typeof(T) == typeof(bool))
        {
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

    public void Dispose()
    {
        _httpClient.Dispose();
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
