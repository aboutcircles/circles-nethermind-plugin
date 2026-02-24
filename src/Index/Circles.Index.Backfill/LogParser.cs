using System.Numerics;
using System.Text.Json;

namespace Circles.Index.Backfill;

/// <summary>
/// Parses Ethereum logs into database rows using the EventRegistry definitions.
/// </summary>
public static class LogParser
{
    /// <summary>
    /// Parse a log entry from JSON-RPC response into a database row.
    /// </summary>
    public static ParsedEvent? ParseLog(
        JsonElement log,
        Dictionary<long, long> blockTimestamps,
        HashSet<string> targetTables)
    {
        var topics = log.GetProperty("topics");
        if (topics.GetArrayLength() == 0)
            return null;

        var topic0 = topics[0].GetString()!.ToLowerInvariant();
        var logAddress = log.GetProperty("address").GetString()!.ToLowerInvariant();

        // Find matching event definition
        EventDefinition? eventDef = null;
        string? tableName = null;

        foreach (var table in targetTables)
        {
            if (!EventRegistry.Events.TryGetValue(table, out var def))
                continue;

            if (def.TopicHex != topic0)
                continue;

            // Check contract filter
            if (EventRegistry.ContractFilters.TryGetValue(def.TopicHex, out var allowedAddresses))
            {
                if (!allowedAddresses.Contains(logAddress))
                    continue;
            }

            eventDef = def;
            tableName = table;
            break;
        }

        if (eventDef == null || tableName == null)
            return null;

        // Parse common fields
        var blockNumber = Convert.ToInt64(log.GetProperty("blockNumber").GetString(), 16);
        var transactionIndex = Convert.ToInt32(log.GetProperty("transactionIndex").GetString(), 16);
        var logIndex = Convert.ToInt32(log.GetProperty("logIndex").GetString(), 16);
        var transactionHash = log.GetProperty("transactionHash").GetString()!;
        var timestamp = blockTimestamps.GetValueOrDefault(blockNumber, 0);
        var data = log.GetProperty("data").GetString() ?? "0x";

        var fields = new Dictionary<string, object?>();

        // Parse indexed fields from topics
        int topicIndex = 1;
        int dataOffset = 0;
        var dataBytes = data.Length > 2 ? Convert.FromHexString(data[2..]) : Array.Empty<byte>();

        foreach (var field in eventDef.Fields)
        {
            object? value;

            if (field.IsIndexed)
            {
                // Indexed fields are in topics
                if (topicIndex >= topics.GetArrayLength())
                {
                    value = GetDefaultValue(field.Type);
                }
                else
                {
                    var topicHex = topics[topicIndex].GetString()!;
                    value = ParseTopicValue(topicHex, field.Type);
                    topicIndex++;
                }
            }
            else
            {
                // Non-indexed fields are in data
                value = ParseDataValue(dataBytes, ref dataOffset, field.Type);
            }

            fields[field.Name] = value;
        }

        return new ParsedEvent
        {
            Table = tableName,
            BlockNumber = blockNumber,
            Timestamp = timestamp,
            TransactionIndex = transactionIndex,
            LogIndex = logIndex,
            TransactionHash = transactionHash,
            Fields = fields
        };
    }

    private static object? ParseTopicValue(string topicHex, FieldType type)
    {
        // Topics are always 32 bytes (64 hex chars after 0x)
        var bytes = Convert.FromHexString(topicHex[2..]);

        return type switch
        {
            FieldType.Address => "0x" + Convert.ToHexString(bytes[^20..]).ToLowerInvariant(),
            FieldType.BigInt => new BigInteger(bytes, true, true),
            FieldType.Int => (long)new BigInteger(bytes, true, true),
            FieldType.Bytes32 => bytes,
            FieldType.Boolean => bytes[^1] != 0,
            _ => bytes
        };
    }

    private static object? ParseDataValue(byte[] data, ref int offset, FieldType type)
    {
        if (offset >= data.Length)
            return GetDefaultValue(type);

        switch (type)
        {
            case FieldType.Address:
                // Address is 20 bytes, right-aligned in 32-byte word
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var addrBytes = data.AsSpan(offset + 12, 20);
                offset += 32;
                return "0x" + Convert.ToHexString(addrBytes).ToLowerInvariant();

            case FieldType.BigInt:
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var bigIntBytes = data.AsSpan(offset, 32);
                offset += 32;
                return new BigInteger(bigIntBytes, true, true);

            case FieldType.Int:
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var intBytes = data.AsSpan(offset, 32);
                offset += 32;
                return (long)new BigInteger(intBytes, true, true);

            case FieldType.Bytes32:
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var bytes32 = data.AsSpan(offset, 32).ToArray();
                offset += 32;
                return bytes32;

            case FieldType.Boolean:
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var boolVal = data[offset + 31] != 0;
                offset += 32;
                return boolVal;

            case FieldType.Bytes:
                // Dynamic bytes: first word is offset, then length, then data
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var bytesOffset = (int)new BigInteger(data.AsSpan(offset, 32), true, true);
                offset += 32;

                if (bytesOffset + 32 > data.Length) return Array.Empty<byte>();
                var bytesLength = (int)new BigInteger(data.AsSpan(bytesOffset, 32), true, true);

                if (bytesOffset + 32 + bytesLength > data.Length) return Array.Empty<byte>();
                return data.AsSpan(bytesOffset + 32, bytesLength).ToArray();

            case FieldType.String:
                // Dynamic string: same encoding as bytes
                if (offset + 32 > data.Length) return GetDefaultValue(type);
                var strOffset = (int)new BigInteger(data.AsSpan(offset, 32), true, true);
                offset += 32;

                if (strOffset + 32 > data.Length) return "";
                var strLength = (int)new BigInteger(data.AsSpan(strOffset, 32), true, true);

                if (strOffset + 32 + strLength > data.Length) return "";
                return System.Text.Encoding.UTF8.GetString(data.AsSpan(strOffset + 32, strLength));

            default:
                offset += 32;
                return null;
        }
    }

    private static object? GetDefaultValue(FieldType type)
    {
        return type switch
        {
            FieldType.Address => "",
            FieldType.BigInt => BigInteger.Zero,
            FieldType.Int => 0L,
            FieldType.Bytes32 => Array.Empty<byte>(),
            FieldType.Bytes => Array.Empty<byte>(),
            FieldType.String => "",
            FieldType.Boolean => false,
            _ => null
        };
    }
}

public class ParsedEvent
{
    public string Table { get; init; } = "";
    public long BlockNumber { get; init; }
    public long Timestamp { get; init; }
    public int TransactionIndex { get; init; }
    public int LogIndex { get; init; }
    public string TransactionHash { get; init; } = "";
    public Dictionary<string, object?> Fields { get; init; } = new();
}
