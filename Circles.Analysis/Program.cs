using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Analysis.Data;

namespace Circles.Analysis;

public record Account(string Address, string? Name);

public record DataRange(long FromBlock, long ToBlock);

public sealed record HistogramRequest(
    long      FromBlock,
    long      ToBlock,
    int?      Bins,
    FilterCfg Filter);

/// Which roles an address should be compared against.
public readonly record struct ColFlags(
    bool From = true,
    bool To = true,
    bool TokenOwner = true,
    bool Operator = true)
{
    public bool Matches(string role) => role switch
    {
        "from" => From,
        "to" => To,
        "tokenOwner" => TokenOwner,
        "operator" => Operator,
        _ => false
    };
}

/// 1-to-1 port of the TS `FilterCfg`
public sealed class FilterCfg
{
    public Dictionary<string, ColFlags> Include { get; init; } = new();
    public string IncludeMode { get; init; } = "or";

    public Dictionary<string, ColFlags> Exclude { get; init; } = new();
    public string ExcludeMode { get; init; } = "or";

    public HashSet<string> TokenTypes { get; init; } = new();
    public HashSet<string> EventTypes { get; init; } = new();

    public double? MinAmount { get; init; }
    public double? MaxAmount { get; init; }
}

public sealed class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        var ok = BigInteger.TryParse(raw, out var value);
        return ok ? value : BigInteger.Zero;
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>
/// In-memory transfer cache.
/// </summary>
public sealed class TransferDataCache
{
    private readonly string _connectionString;

    public List<DataLoader.Transfer>?[] Blocks { get; private set; }

    public long MinBlock { get; private set; }

    public long MaxBlock { get; private set; }

    public TransferDataCache(string conn)
    {
        _connectionString = conn;

        LoadAllData();
    }

    // ------------------------------------------------------------------ helpers
    private void LoadAllData()
    {
        var loader = new DataLoader(_connectionString);
        var transferCount = 0L;
        var rangeInDb = loader.GetBlockRange();
        MinBlock = rangeInDb.FromBlock;
        MaxBlock = rangeInDb.ToBlock;

        Console.WriteLine($"Range in DB: {rangeInDb.FromBlock} to {rangeInDb.ToBlock}");

        // Initialize an array for all blocks
        Blocks = new List<DataLoader.Transfer>[MaxBlock - MinBlock + 1];

        foreach (var transfer in loader.LoadTransfers())
        {
            var blockIndex = transfer.BlockNumber - MinBlock;
            if (blockIndex < 0 || blockIndex >= Blocks.Length)
            {
                throw new InvalidOperationException($"Transfer block number {transfer.BlockNumber} is out of range.");
            }

            if (Blocks[blockIndex] == null)
            {
                Blocks[blockIndex] = new List<DataLoader.Transfer>();
            }

            Blocks[blockIndex].Add(transfer);
            transferCount++;
        }

        Console.WriteLine($"Loaded {transferCount} transfers");
    }

    public (long BinSize, int[] Counts) BuildFilteredHistogram(
        long from,
        long to,
        int bucketCount,
        FilterCfg cfg)
    {
        if (to < from) throw new ArgumentException("`to` must be ≥ `from`.");

        var minIdx = from - MinBlock;
        var maxIdx = to - MinBlock;

        if (minIdx < 0 || maxIdx >= Blocks.Length)
            throw new ArgumentOutOfRangeException(
                $"Valid block range is {MinBlock}–{MaxBlock}.");

        var span = to - from + 1;
        var binSize = Math.Max(1, span / Math.Max(1, bucketCount));
        var counts = new int[Math.Max(1, bucketCount)];

        for (var i = minIdx; i <= maxIdx; i++)
        {
            var bucket = (int)((i - minIdx) / binSize);
            if (bucket >= counts.Length) bucket = counts.Length - 1; // guard rounding

            if (Blocks[i] is not { Count: > 0 } transfers) continue;

            foreach (var tx in transfers)
            {
                if (PassesFilter(tx, cfg))
                {
                    counts[bucket] += 1;
                }
            }
        }

        return (binSize, counts);
    }

// ------------------------------------------------------------------ core predicate
    private static bool PassesFilter(DataLoader.Transfer tx, FilterCfg cfg)
    {
        /* ---------- helpers ---------- */
        static string Trim(string s) => s.Replace("CrcV2_", "", StringComparison.Ordinal);

        static double ToEth(BigInteger wei)
        {
            // 1 ETH = 1e18 wei
            const double EthBase = 1e18;
            return (double)wei / EthBase;
        }

        /* ---------- simple gates ---------- */
        if (cfg.TokenTypes.Count > 0 &&
            !cfg.TokenTypes.Contains(Trim(tx.TokenType)))
        {
            return false;
        }

        if (cfg.EventTypes.Count > 0 &&
            !cfg.EventTypes.Contains(Trim(tx.Type)))
        {
            return false;
        }

        var ethValue = ToEth(BigInteger.Parse(tx.Value));
        if (cfg.MinAmount is { } min && ethValue < min) return false;
        if (cfg.MaxAmount is { } max && ethValue > max) return false;

        /* ---------- party list ---------- */
        var parties = new (string Addr, string Role)[]
        {
            (tx.From.Address.ToLowerInvariant(), "from"),
            (tx.To.Address.ToLowerInvariant(), "to"),
            (tx.TokenOwner?.Address?.ToLowerInvariant() ?? "", "tokenOwner"),
            (tx.Operator?.Address?.ToLowerInvariant() ?? "", "operator")
        }.Where(p => !string.IsNullOrEmpty(p.Addr)).ToArray();

        /* ---------- exclude ---------- */
        if (cfg.Exclude.Count > 0)
        {
            var hit = cfg.ExcludeMode == "or"
                ? cfg.Exclude.Any(kv => MatchRule(parties, kv))
                : cfg.Exclude.All(kv => MatchRule(parties, kv));

            if (hit) return false;
        }

        /* ---------- include ---------- */
        if (cfg.Include.Count == 0) return true; // no whitelist → row survives

        var ok = cfg.IncludeMode == "or"
            ? cfg.Include.Any(kv => MatchRule(parties, kv))
            : cfg.Include.All(kv => MatchRule(parties, kv));

        return ok;

        /* ---------- local func ---------- */
        static bool MatchRule(
            IEnumerable<(string Addr, string Role)> parties,
            KeyValuePair<string, ColFlags> rule)
        {
            var (addr, flags) = (rule.Key.ToLowerInvariant(), rule.Value);
            return parties.Any(p => p.Addr == addr && flags.Matches(p.Role));
        }
    }
}

public static class Program
{
    // ---------------------------------------------------------------- constants
    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ------------------------------------------------ Services
        builder.Services.AddSingleton(
            new TransferDataCache(ConnectionString));

        builder.Services.AddCors(o =>
            o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
            o.SerializerOptions.Converters.Add(new BigIntegerJsonConverter()));

        var app = builder.Build();

        // ------------------------------------------------ Middleware
        app.UseCors();

        app.MapGet("/blocks/min-max",
            (TransferDataCache cache) => Results.Ok(new DataRange(cache.MinBlock, cache.MaxBlock)));

        app.MapGet("/blocks/histogram", (
            long from,
            long to,
            int? bins,
            TransferDataCache cache) =>
        {
            if (to < from) return Results.BadRequest("`to` must be ≥ `from`.");

            var minIdx = from - cache.MinBlock;
            var maxIdx = to - cache.MinBlock;

            if (minIdx < 0 || maxIdx >= cache.Blocks.Length)
                return Results.BadRequest($"Valid block range is {cache.MinBlock}–{cache.MaxBlock}.");

            // decide how many buckets to aggregate into
            var bucketCount = Math.Max(1, bins ?? 500); // fallback if client omits &bins=
            var span = to - from + 1;
            var binSize = Math.Max(1, span / bucketCount);

            var counts = new int[bucketCount];

            for (var b = minIdx; b <= maxIdx; b++)
            {
                var bucket = (int)((b - minIdx) / binSize);
                if (bucket >= bucketCount) bucket = bucketCount - 1; // guard rounding
                counts[bucket] += cache.Blocks[b]?.Count ?? 0;
            }

            return Results.Ok(new
            {
                from,
                binSize,
                counts
            });
        });
        
        app.MapPost("/blocks/histogram/filtered", (
            HistogramRequest body,
            TransferDataCache cache) =>
        {
            var (binSize, counts) = cache.BuildFilteredHistogram(
                body.FromBlock,
                body.ToBlock,
                body.Bins ?? 500,
                body.Filter);

            return Results.Ok(new { body.FromBlock, binSize, counts });
        });

        app.MapGet("/transfers", (long fromBlock, long toBlock, TransferDataCache cache) =>
        {
            if (toBlock < fromBlock) return Results.BadRequest("`to` must be ≥ `from`.");

            var minIdx = fromBlock - cache.MinBlock;
            var maxIdx = toBlock - cache.MinBlock;

            if (minIdx < 0 || maxIdx >= cache.Blocks.Length)
                return Results.BadRequest($"Valid block range is {cache.MinBlock}–{cache.MaxBlock}.");

            var transfers = new List<DataLoader.Transfer>();

            for (var b = minIdx; b <= maxIdx; b++)
            {
                var blockTransfers = cache.Blocks[b];
                if (blockTransfers != null)
                {
                    transfers.AddRange(blockTransfers);
                }
            }

            return Results.Ok(transfers);
        });

        app.Run();
    }
}