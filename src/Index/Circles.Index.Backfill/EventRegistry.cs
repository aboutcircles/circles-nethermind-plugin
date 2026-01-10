using System.Numerics;
using System.Text;
using Nethereum.Util;

namespace Circles.Index.Backfill;

/// <summary>
/// Registry of all backfillable event schemas.
/// To add a new event:
/// 1. Define the event schema using EventDefinition.FromSolidity() or manually
/// 2. Add it to the Events dictionary
/// 3. Implement a ParseXxx method if the event has complex data types
/// </summary>
public static class EventRegistry
{
    /// <summary>
    /// All registered events that can be backfilled.
    /// Key: Table name (e.g., "CrcV2_PaymentGateway_GatewayCreated")
    /// </summary>
    public static readonly Dictionary<string, EventDefinition> Events = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Contract addresses to filter by for each event.
    /// Key: Topic hash, Value: Set of contract addresses (lowercase)
    /// If not specified, matches any address.
    /// </summary>
    public static readonly Dictionary<string, HashSet<string>> ContractFilters = new(StringComparer.OrdinalIgnoreCase);

    static EventRegistry()
    {
        // ============================================================
        // CrcV2 Hub Events (address: 0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8)
        // ============================================================
        var v2Hub = "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8";

        RegisterEvent("CrcV2_FlowEdgesScopeSingleStarted",
            "event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId)",
            v2Hub);

        RegisterEvent("CrcV2_FlowEdgesScopeLastEnded",
            "event FlowEdgesScopeLastEnded()",
            v2Hub);

        RegisterEvent("CrcV2_SetAdvancedUsageFlag",
            "event SetAdvancedUsageFlag(address indexed avatar, bytes32 flag)",
            v2Hub);

        // ============================================================
        // PaymentGateway Events (factory: 0x186725d8fe10a573dc73144f7a317fcae5314f19)
        // See Settings.cs PaymentGatewayFactoryAddresses for authoritative list
        // ============================================================
        var paymentGatewayFactory = "0x186725d8fe10a573dc73144f7a317fcae5314f19";

        RegisterEvent("CrcV2_PaymentGateway_GatewayCreated",
            "event GatewayCreated(address indexed owner, address indexed gateway)",
            paymentGatewayFactory);

        RegisterEvent("CrcV2_PaymentGateway_PaymentReceived",
            "event PaymentReceived(address indexed payer, address indexed payee, address indexed gateway, uint256 tokenId, uint256 amount, bytes data)",
            paymentGatewayFactory);

        // Note: TrustUpdated has a non-standard signature (uint96 instead of uint256)
        RegisterEventManual("CrcV2_PaymentGateway_TrustUpdated",
            "TrustUpdated(address,address,uint96)",
            new[]
            {
                ("gateway", FieldType.Address, true),
                ("trustReceiver", FieldType.Address, true),
                ("expiry", FieldType.BigInt, false)
            },
            paymentGatewayFactory);

        // ============================================================
        // TokenOffers Events (factory: 0x69db4C7CE0F9c9D83F1B8E5B1B8e5B1B8E5B1B8e - placeholder)
        // ============================================================
        // TODO: Add TokenOffers events when factory address is confirmed
        // RegisterEvent("CrcV2_TokenOffers_OfferCreated", "event OfferCreated(...)", tokenOffersFactory);

        // ============================================================
        // LBP Events
        // ============================================================
        // TODO: Add LBP events

        // ============================================================
        // Safe Events (Safe contract instances - matches any address)
        // ============================================================
        // Safe events don't have a fixed factory - they match any address
        // Use RegisterEvent without address filter

        // ============================================================
        // V1 Events
        // ============================================================
        // TODO: Add V1 events if needed for backfill
    }

    /// <summary>
    /// Register an event from a Solidity event signature.
    /// </summary>
    public static void RegisterEvent(string tableName, string soliditySignature, params string[] contractAddresses)
    {
        var def = EventDefinition.FromSolidity(tableName, soliditySignature);
        Events[tableName] = def;

        if (contractAddresses.Length > 0)
        {
            ContractFilters[def.TopicHex] = contractAddresses
                .Select(a => a.ToLowerInvariant())
                .ToHashSet();
        }
    }

    /// <summary>
    /// Register an event with manual field definitions (for non-standard types).
    /// </summary>
    public static void RegisterEventManual(string tableName, string eventSignature,
        (string name, FieldType type, bool indexed)[] fields, params string[] contractAddresses)
    {
        var topic = ComputeTopicHash(eventSignature);
        var fieldList = fields.Select(f => new FieldDefinition(f.name, f.type, f.indexed)).ToList();
        var def = new EventDefinition(tableName, topic, fieldList);
        Events[tableName] = def;

        if (contractAddresses.Length > 0)
        {
            ContractFilters[def.TopicHex] = contractAddresses
                .Select(a => a.ToLowerInvariant())
                .ToHashSet();
        }
    }

    public static string ComputeTopicHash(string eventSignature)
    {
        var hash = Sha3Keccack.Current.CalculateHash(eventSignature);
        return "0x" + hash.ToLowerInvariant();
    }

    /// <summary>
    /// Get all topic hashes for the specified tables.
    /// </summary>
    public static List<string> GetTopicsForTables(IEnumerable<string> tableNames)
    {
        return tableNames
            .Where(t => Events.ContainsKey(t))
            .Select(t => Events[t].TopicHex)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Get contract addresses to filter for the specified tables.
    /// Returns null if no filter should be applied (match all addresses).
    /// </summary>
    public static HashSet<string>? GetContractAddressesForTables(IEnumerable<string> tableNames)
    {
        var addresses = new HashSet<string>();
        foreach (var table in tableNames)
        {
            if (Events.TryGetValue(table, out var evt) &&
                ContractFilters.TryGetValue(evt.TopicHex, out var filter))
            {
                foreach (var addr in filter)
                    addresses.Add(addr);
            }
        }
        return addresses.Count > 0 ? addresses : null;
    }
}

public enum FieldType
{
    Address,
    BigInt,
    Int,
    Bytes,
    Bytes32,
    String,
    Boolean
}

public record FieldDefinition(string Name, FieldType Type, bool IsIndexed);

public class EventDefinition
{
    public string TableName { get; }
    public string TopicHex { get; }
    public List<FieldDefinition> Fields { get; }

    public EventDefinition(string tableName, string topicHex, List<FieldDefinition> fields)
    {
        TableName = tableName;
        TopicHex = topicHex.ToLowerInvariant();
        Fields = fields;
    }

    /// <summary>
    /// Parse a Solidity event signature and create an EventDefinition.
    /// Example: "event TransferBatch(address indexed _operator, address indexed _from, uint256 amount)"
    /// </summary>
    public static EventDefinition FromSolidity(string tableName, string soliditySignature)
    {
        var sig = soliditySignature.Trim();
        if (sig.StartsWith("event "))
            sig = sig.Substring(6);

        var openParen = sig.IndexOf('(');
        var closeParen = sig.LastIndexOf(')');
        if (openParen == -1 || closeParen == -1)
            throw new ArgumentException($"Invalid event signature: {soliditySignature}");

        var eventName = sig.Substring(0, openParen).Trim();
        var paramsStr = sig.Substring(openParen + 1, closeParen - openParen - 1);

        // Build canonical signature for topic hash
        var canonicalParams = new StringBuilder();
        var fields = new List<FieldDefinition>();

        var paramParts = SplitParams(paramsStr);
        for (int i = 0; i < paramParts.Count; i++)
        {
            var param = paramParts[i].Trim();
            if (string.IsNullOrEmpty(param)) continue;

            var parts = param.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new ArgumentException($"Invalid parameter: {param}");

            var solType = parts[0];
            var isIndexed = parts.Length >= 3 && parts[1] == "indexed";
            var name = parts[^1]; // last part is always the name

            if (i > 0) canonicalParams.Append(',');
            canonicalParams.Append(solType);

            var fieldType = MapSolidityType(solType);
            fields.Add(new FieldDefinition(name, fieldType, isIndexed));
        }

        var topicSig = $"{eventName}({canonicalParams})";
        var topicHex = EventRegistry.ComputeTopicHash(topicSig);

        return new EventDefinition(tableName, topicHex, fields);
    }

    private static List<string> SplitParams(string paramsStr)
    {
        // Handle nested types like "bytes data" correctly
        var result = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var c in paramsStr)
        {
            if (c == ',' && depth == 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                if (c == '(' || c == '[') depth++;
                if (c == ')' || c == ']') depth--;
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static FieldType MapSolidityType(string solType)
    {
        return solType switch
        {
            "address" => FieldType.Address,
            "bool" => FieldType.Boolean,
            "string" => FieldType.String,
            "bytes" => FieldType.Bytes,
            "bytes32" => FieldType.Bytes32,
            "uint8" or "uint16" or "uint32" or "uint64" or "int8" or "int16" or "int32" or "int64" => FieldType.Int,
            "uint96" or "uint128" or "uint256" or "int96" or "int128" or "int256" => FieldType.BigInt,
            _ when solType.StartsWith("uint") || solType.StartsWith("int") => FieldType.BigInt,
            _ => throw new ArgumentException($"Unsupported Solidity type: {solType}")
        };
    }
}
