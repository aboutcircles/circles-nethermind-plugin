using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using DotNetEnv;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Npgsql;
using Nethereum.Contracts.ContractHandlers;

namespace Circles.TrustMissingAvatars;

public readonly record struct MissingRow(string Group, string Avatar);

[Function("enableCRCForRouting")]
public class EnableCrcForRoutingFunction : FunctionMessage
{
    [Parameter("address", "baseGroup", 1)]
    public string BaseGroup { get; set; } = string.Empty;

    [Parameter("address[]", "crcArray", 2)]
    public List<string> CrcArray { get; set; } = new();
}

public static class Program
{
    private const int DefaultBatchSize = 50;
    private const int MaxBatchSize = 200;
    private const int MaxIterations = 50;

    public static async Task Main(string[] args)
    {
        Env.Load();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var databaseUrl = RequireEnv("DATABASE_URL");
        var rpcUrl = RequireEnv("RPC_URL");
        var privateKey = NormalizePrivateKey(RequireEnv("PRIVATE_KEY"));
        var routerAddress = NormalizeAddress("ROUTER_ADDRESS", RequireEnv("ROUTER_ADDRESS"));

        var batchSize = ParseIntEnv("BATCH_SIZE", DefaultBatchSize);
        if (batchSize is <= 0 or > MaxBatchSize)
        {
            throw new Exception($"BATCH_SIZE must be in [1..{MaxBatchSize}]");
        }

        var gasLimit = ParseNullableBigIntEnv("GAS_LIMIT");
        var confirmations = ParseIntEnv("CONFIRMATIONS", 1);
        if (confirmations <= 0)
        {
            throw new Exception("CONFIRMATIONS must be >= 1");
        }

        var chainId = await GetChainIdAsync(rpcUrl, cts.Token);
        var account = new Account(privateKey, chainId);
        var web3 = new Web3(account, rpcUrl);

        var routerHandler = web3.Eth.GetContractHandler(routerAddress);

        var routerTruster = routerAddress.ToLowerInvariant();

        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            cts.Token.ThrowIfCancellationRequested();

            var missing = await FetchMissingPairsAsync(databaseUrl, routerTruster, cts.Token);
            if (missing.Count == 0)
            {
                Console.WriteLine("Done. No missing avatars.");
                return;
            }

            Console.WriteLine($"Iteration {iteration}: missing avatars = {missing.Count}");

            var byGroup = missing
                .GroupBy(m => m.Group, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(m => m.Avatar).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            var sortedGroups = byGroup.Keys.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            var failures = new List<(string Group, string Avatar, string Error)>();

            foreach (var groupRaw in sortedGroups)
            {
                cts.Token.ThrowIfCancellationRequested();

                var baseGroup = NormalizeAddress("baseGroup", groupRaw);

                var avatars = byGroup[groupRaw]
                    .Select(a => NormalizeAddress("avatar", a).ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (avatars.Count == 0)
                {
                    continue;
                }

                var batches = Chunk(avatars, batchSize).ToList();
                Console.WriteLine($"Group {baseGroup}: {avatars.Count} avatars -> {batches.Count} tx(s)");

                for (var i = 0; i < batches.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var batch = batches[i];
                    Console.WriteLine($"  sending batch {i + 1}/{batches.Count} (size={batch.Count})");

                    var batchFailures = await SendBatchWithBisectAsync(
                        web3,
                        routerHandler,
                        account.Address,
                        baseGroup,
                        batch,
                        gasLimit,
                        confirmations,
                        cts.Token
                    );

                    foreach (var kv in batchFailures)
                    {
                        failures.Add((baseGroup, kv.Key, kv.Value));
                    }
                }
            }

            if (failures.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Some avatars still caused tx failures after bisection: {failures.Count}");
                sb.AppendLine("Sample:");
                foreach (var x in failures.Take(20))
                {
                    sb.AppendLine($"{x.Group} -> {x.Avatar}");
                    sb.AppendLine(x.Error);
                    sb.AppendLine("---");
                }

                throw new Exception(sb.ToString());
            }
        }

        throw new Exception($"Too many iterations ({MaxIterations}). Something is preventing convergence.");
    }

    private static string RequireEnv(string name)
    {
        var val = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(val))
        {
            throw new Exception($"Missing required env var: {name}");
        }

        return val.Trim();
    }

    private static int ParseIntEnv(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw.Trim(), out var value))
        {
            throw new Exception($"{name} must be an integer");
        }

        return value;
    }

    private static BigInteger? ParseNullableBigIntEnv(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!BigInteger.TryParse(raw.Trim(), out var value))
        {
            throw new Exception($"{name} must be an integer (fits in BigInteger)");
        }

        if (value <= 0)
        {
            throw new Exception($"{name} must be > 0");
        }

        return value;
    }

    private static string NormalizePrivateKey(string pk)
    {
        var trimmed = pk.Trim();
        if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "0x" + trimmed;
        }

        if (trimmed.Length != 66)
        {
            throw new Exception($"PRIVATE_KEY looks wrong length (got {trimmed.Length}, expected 66)");
        }

        if (!Regex.IsMatch(trimmed, "^0x[0-9a-fA-F]{64}$"))
        {
            throw new Exception("PRIVATE_KEY must be hex (0x + 64 hex chars)");
        }

        return trimmed;
    }

    private static string NormalizeAddress(string label, string address)
    {
        var trimmed = address.Trim();
        if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "0x" + trimmed;
        }

        if (!Regex.IsMatch(trimmed, "^0x[0-9a-fA-F]{40}$"))
        {
            throw new Exception($"{label} must be a valid 20-byte hex address (0x + 40 hex chars): {address}");
        }

        return trimmed;
    }

    private static async Task<BigInteger> GetChainIdAsync(string rpcUrl, CancellationToken ct)
    {
        var web3 = new Web3(rpcUrl);
        var chainId = await web3.Eth.ChainId.SendRequestAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return chainId.Value;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        if (size <= 0)
        {
            throw new Exception("chunk size must be > 0");
        }

        for (var i = 0; i < items.Count; i += size)
        {
            var count = Math.Min(size, items.Count - i);
            var batch = new List<T>(count);
            for (var j = 0; j < count; j++)
            {
                batch.Add(items[i + j]);
            }
            yield return batch;
        }
    }

    private static async Task<List<MissingRow>> FetchMissingPairsAsync(
        string connectionString,
        string routerTruster,
        CancellationToken ct)
    {
        const string sql = @"
WITH
  now_ts AS (
    SELECT max(""timestamp"")::numeric AS ts
    FROM ""System_Block""
    WHERE ""timestamp"" IS NOT NULL
  ),
  router_trusted AS MATERIALIZED (
    SELECT DISTINCT tr.trustee
    FROM ""V_CrcV2_TrustRelations"" tr
    JOIN now_ts n ON true
    WHERE tr.truster = @routerTruster
      AND tr.trustee IS NOT NULL
      AND tr.""expiryTime"" > n.ts
  ),
  needed AS MATERIALIZED (
    SELECT
      tr.trustee AS avatar,
      MIN(bgc.""group"") AS ""group""
    FROM ""CrcV2_BaseGroupCreated"" bgc
    JOIN ""V_CrcV2_TrustRelations"" tr
      ON tr.truster = bgc.""group""
    JOIN ""CrcV2_RegisterHuman"" a
      ON a.avatar = tr.trustee
    JOIN now_ts n ON true
    WHERE tr.trustee IS NOT NULL
      AND tr.""expiryTime"" > n.ts
    GROUP BY tr.trustee
  )
SELECT n.""group"", n.avatar
FROM needed n
LEFT JOIN router_trusted rt
  ON rt.trustee = n.avatar
WHERE rt.trustee IS NULL;";

        var results = new List<MissingRow>();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("routerTruster", routerTruster);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var group = reader.GetString(0);
            var avatar = reader.GetString(1);
            results.Add(new MissingRow(group, avatar));
        }

        return results;
    }

    private static async Task<TransactionReceipt> SendEnableAsync(
        ContractHandler routerHandler,
        string fromAddress,
        string baseGroup,
        List<string> avatars,
        BigInteger? gasLimit,
        CancellationToken ct)
    {
        var function = new EnableCrcForRoutingFunction
        {
            FromAddress = fromAddress,
            BaseGroup = baseGroup,
            CrcArray = avatars,
            Gas = gasLimit.HasValue ? new HexBigInteger(gasLimit.Value) : null
        };

        var receipt = await routerHandler.SendRequestAndWaitForReceiptAsync(function, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (receipt.Status != null && receipt.Status.Value == BigInteger.Zero)
        {
            throw new Exception($"Transaction reverted (status=0). tx={receipt.TransactionHash}");
        }

        return receipt;
    }

    private static async Task WaitForConfirmationsAsync(IWeb3 web3, string txHash, int confirmations, CancellationToken ct)
    {
        var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (receipt == null)
        {
            throw new Exception($"Receipt not found for tx {txHash}");
        }

        if (receipt.BlockNumber?.Value == null)
        {
            throw new Exception($"Receipt missing block number for tx {txHash}");
        }

        var receiptBlock = receipt.BlockNumber.Value;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var head = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().ConfigureAwait(false);
            var required = receiptBlock + (confirmations - 1);

            if (head.Value >= required)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> SendBatchWithBisectAsync(
        IWeb3 web3,
        ContractHandler routerHandler,
        string fromAddress,
        string baseGroup,
        List<string> avatars,
        BigInteger? gasLimit,
        int confirmations,
        CancellationToken ct)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        async Task WalkAsync(List<string> batch)
        {
            ct.ThrowIfCancellationRequested();

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                var receipt = await SendEnableAsync(
                    routerHandler,
                    fromAddress,
                    baseGroup,
                    batch,
                    gasLimit,
                    ct
                ).ConfigureAwait(false);

                Console.WriteLine($"    ok tx={receipt.TransactionHash}");

                if (confirmations > 1)
                {
                    await WaitForConfirmationsAsync(web3, receipt.TransactionHash, confirmations, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (batch.Count == 1)
                {
                    failures[batch[0]] = ex.ToString();
                    Console.WriteLine($"    failed avatar={batch[0]}");
                    return;
                }

                var mid = batch.Count / 2;
                var left = batch.Take(mid).ToList();
                var right = batch.Skip(mid).ToList();

                await WalkAsync(left).ConfigureAwait(false);
                await WalkAsync(right).ConfigureAwait(false);
            }
        }

        await WalkAsync(avatars).ConfigureAwait(false);
        return failures;
    }
}