using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Index.Query.Dto;

namespace Circles.Rpc.Host.OpenRpc;

/// <summary>
/// Generates an OpenRPC 1.3.2 document by reflecting over ICirclesRpcModule.
/// The RPC method name → C# method mapping is maintained explicitly to match
/// the switch statement in Program.cs.
/// </summary>
public static class OpenRpcGenerator
{
    /// <summary>
    /// RPC method name → (C# method name, tag, summary).
    /// This is the single source of truth for the RPC → C# mapping.
    /// </summary>
    private static readonly (string RpcName, string CSharpName, string Tag, string Summary)[] MethodMappings =
    [
        // Balance & Token Methods
        ("circles_getTotalBalance", "GetTotalBalance", "Balances", "Get total CRC balance for an address (V1)"),
        ("circlesV2_getTotalBalance", "GetTotalBalance", "Balances", "Get total CRC balance for an address (V2)"),
        ("circles_getTokenBalances", "GetTokenBalances", "Balances", "Get all token balances for an address"),
        ("circles_getTokenInfo", "GetTokenInfo", "Tokens", "Get information about a specific token"),
        ("circles_getTokenInfoBatch", "GetTokenInfoBatch", "Tokens", "Get information about multiple tokens"),

        // Avatar & Profile Methods
        ("circles_getAvatarInfo", "GetAvatarInfo", "Avatars", "Get avatar information for an address"),
        ("circles_getAvatarInfoBatch", "GetAvatarInfoBatch", "Avatars", "Get avatar information for multiple addresses"),
        ("circles_getProfileCid", "GetProfileCid", "Profiles", "Get IPFS CID for an avatar's profile"),
        ("circles_getProfileCidBatch", "GetProfileCidBatch", "Profiles", "Get IPFS CIDs for multiple avatars"),
        ("circles_getProfileByCid", "GetProfileByCid", "Profiles", "Get profile content by IPFS CID"),
        ("circles_getProfileByCidBatch", "GetProfileByCidBatch", "Profiles", "Get multiple profile contents by CIDs"),
        ("circles_getProfileByAddress", "GetProfileByAddress", "Profiles", "Get profile by avatar address"),
        ("circles_getProfileByAddressBatch", "GetProfileByAddressBatch", "Profiles", "Get profiles for multiple addresses"),
        ("circles_searchProfiles", "SearchProfiles", "Profiles", "Full-text search across profiles"),

        // Trust & Network Methods
        ("circles_getTrustRelations", "GetTrustRelations", "Trust", "Get trust relations for an address"),
        ("circles_getCommonTrust", "GetCommonTrust", "Trust", "Find common trust connections between two addresses"),
        ("circles_getAggregatedTrustRelations", "GetAggregatedTrustRelations", "Trust", "Get trust relations grouped by type"),
        ("circles_getNetworkSnapshot", "GetNetworkSnapshot", "Network", "Get a snapshot of the full trust network"),

        // Group Methods
        ("circles_findGroups", "FindGroups", "Groups", "Find groups with optional filters"),
        ("circles_getGroupMembers", "GetGroupMembers", "Groups", "Get members of a specific group"),
        ("circles_getGroupMemberships", "GetGroupMemberships", "Groups", "Get groups an avatar is a member of"),

        // Transaction Methods
        ("circles_getTransactionHistory", "GetTransactionHistory", "Transactions", "Get transaction history for an avatar"),
        ("circles_getTransferData", "GetTransferData", "Transactions", "Get transfer calldata for an address"),
        ("circles_getTokenHolders", "GetTokenHolders", "Tokens", "Get all holders of a specific token"),

        // Pathfinder
        ("circlesV2_findPath", "FindPathV2", "Pathfinder", "Find a transitive transfer path through the trust network"),

        // Events & Query
        ("circles_events", "GetEvents", "Events", "Query indexed blockchain events with filters"),
        ("circles_query", "Query", "Query", "Execute a structured database query (non-paginated)"),
        ("circles_paginated_query", "Query", "Query", "Execute a structured database query with cursor pagination"),

        // System
        ("circles_health", "GetHealth", "System", "Check service health and sync status"),
        ("circles_tables", "GetTables", "System", "List available database tables and schemas"),

        // SDK Enablement Methods
        ("circles_getProfileView", "GetProfileView", "SDK", "Get a complete profile view (avatar + profile + trust stats + balances)"),
        ("circles_getTrustNetworkSummary", "GetTrustNetworkSummary", "SDK", "Get aggregated trust network statistics"),
        ("circles_getAggregatedTrustRelationsEnriched", "GetAggregatedTrustRelationsEnriched", "SDK", "Get trust relations with enriched avatar info"),
        ("circles_getValidInviters", "GetValidInviters", "SDK", "Get addresses with sufficient balance to invite"),
        ("circles_getTransactionHistoryEnriched", "GetTransactionHistoryEnriched", "SDK", "Get transaction history with enriched profiles"),
        ("circles_searchProfileByAddressOrName", "SearchProfileByAddressOrName", "SDK", "Unified search by address prefix or name"),
        ("circles_getInvitationOrigin", "GetInvitationOrigin", "SDK", "Get how an address was invited to Circles"),
        ("circles_getAllInvitations", "GetAllInvitations", "SDK", "Get all available invitations from all sources"),
        ("circles_getTrustInvitations", "GetTrustInvitations", "SDK", "Get trust-based invitations"),
        ("circles_getEscrowInvitations", "GetEscrowInvitations", "SDK", "Get escrow-based invitations"),
        ("circles_getAtScaleInvitations", "GetAtScaleInvitations", "SDK", "Get at-scale invitations"),
        ("circles_getInvitationsFrom", "GetInvitationsFrom", "SDK", "Get accounts invited by a specific avatar"),
    ];

    /// <summary>
    /// Parameter overrides for methods where the RPC parameter extraction
    /// differs from the C# method signature (e.g., custom parsing in handlers).
    /// Key: RPC method name → explicit param list.
    /// </summary>
    private static readonly Dictionary<string, OpenRpcParam[]> ParameterOverrides = new()
    {
        ["circles_getTotalBalance"] = [
            Param("address", true, "string", "Ethereum address (0x-prefixed)", pattern: "^0x[0-9a-fA-F]{40}$"),
            Param("asTimeCircles", false, "boolean", "Return balance in TimeCircles format (default: true)")
        ],
        ["circlesV2_getTotalBalance"] = [
            Param("address", true, "string", "Ethereum address (0x-prefixed)", pattern: "^0x[0-9a-fA-F]{40}$"),
            Param("asTimeCircles", false, "boolean", "Return balance in TimeCircles format (default: true)")
        ],
        ["circles_getTokenBalances"] = [
            Param("address", true, "string", "Ethereum address", pattern: "^0x[0-9a-fA-F]{40}$")
        ],
        ["circles_getTokenInfo"] = [
            Param("tokenAddress", true, "string", "Token contract address", pattern: "^0x[0-9a-fA-F]{40}$")
        ],
        ["circles_getTokenInfoBatch"] = [
            ParamArray("tokenAddresses", true, "string", "Array of token contract addresses")
        ],
        ["circles_getAvatarInfo"] = [
            Param("address", true, "string", "Avatar address", pattern: "^0x[0-9a-fA-F]{40}$")
        ],
        ["circles_getAvatarInfoBatch"] = [
            ParamArray("addresses", true, "string", "Array of avatar addresses")
        ],
        ["circles_getProfileCid"] = [
            Param("address", true, "string", "Avatar address", pattern: "^0x[0-9a-fA-F]{40}$")
        ],
        ["circles_getProfileCidBatch"] = [
            ParamArray("addresses", true, "string", "Array of avatar addresses")
        ],
        ["circles_getProfileByCid"] = [
            Param("cid", true, "string", "IPFS Content Identifier")
        ],
        ["circles_getProfileByCidBatch"] = [
            ParamArray("cids", true, "string", "Array of IPFS Content Identifiers")
        ],
        ["circles_getProfileByAddress"] = [
            Param("address", true, "string", "Avatar address", pattern: "^0x[0-9a-fA-F]{40}$")
        ],
        ["circles_getProfileByAddressBatch"] = [
            ParamArray("addresses", true, "string", "Array of avatar addresses")
        ],
        ["circles_searchProfiles"] = [
            Param("text", true, "string", "Search text (max 3 tokens, each > 1 character)"),
            Param("limit", false, "integer", "Maximum results (default: 20, max: 100)"),
            Param("offset", false, "integer", "Number of results to skip"),
            ParamArray("types", false, "string", "Filter by avatar types")
        ],
        ["circles_getTrustRelations"] = [
            Param("address", true, "string", "Avatar address", pattern: "^0x[0-9a-fA-F]{40}$")
        ],
        ["circles_getCommonTrust"] = [
            Param("address1", true, "string", "First address", pattern: "^0x[0-9a-fA-F]{40}$"),
            Param("address2", true, "string", "Second address", pattern: "^0x[0-9a-fA-F]{40}$"),
            Param("version", false, "integer", "Filter by version (1 or 2, null for both)")
        ],
        ["circles_events"] = [
            Param("address", false, "string", "Filter by address (null for all)", pattern: "^0x[0-9a-fA-F]{40}$"),
            Param("fromBlock", false, "integer", "Starting block number (inclusive)"),
            Param("toBlock", false, "integer", "Ending block number (inclusive)"),
            ParamArray("eventTypes", false, "string", "Filter by event types"),
            ParamRef("filterPredicates", false, "FilterPredicateDto", "Advanced filter predicates"),
            Param("sortAscending", false, "boolean", "Sort ascending (default: false)"),
            Param("limit", false, "integer", "Maximum events (default: 100, max: 1000)"),
            Param("cursor", false, "string", "Cursor for pagination")
        ],
        ["circles_query"] = [
            ParamRef("query", true, "SelectDto", "Structured query definition"),
            Param("cursor", false, "string", "Cursor for pagination")
        ],
        ["circles_paginated_query"] = [
            ParamRef("query", true, "SelectDto", "Structured query definition"),
            Param("cursor", false, "string", "Cursor for pagination")
        ],
        ["circlesV2_findPath"] = [
            ParamRef("flowRequest", true, "FlowRequest", "Path computation request")
        ],
    };

    // ─── Schema cache ────────────────────────────────────────────────────────
    private static readonly Dictionary<string, JsonSchemaObject> SchemaCache = new();

    public static OpenRpcDocument Generate()
    {
        SchemaCache.Clear();

        var doc = new OpenRpcDocument
        {
            Info = new OpenRpcInfo
            {
                Title = "Circles RPC API",
                Version = "1.0.0",
                Description = "JSON-RPC 2.0 API for querying Circles protocol data — balances, avatars, profiles, trust relations, events, and transitive transfer paths."
            }
        };

        var interfaceType = typeof(ICirclesRpcModule);

        foreach (var (rpcName, csharpName, tag, summary) in MethodMappings)
        {
            var method = interfaceType.GetMethod(csharpName);
            if (method == null) continue;

            var rpcMethod = new OpenRpcMethod
            {
                Name = rpcName,
                Summary = summary,
                Tags = [new OpenRpcTag { Name = tag }]
            };

            // Use parameter overrides if available, otherwise reflect
            if (ParameterOverrides.TryGetValue(rpcName, out var overrides))
            {
                rpcMethod.Params.AddRange(overrides);
            }
            else
            {
                rpcMethod.Params.AddRange(ReflectParams(method));
            }

            // Build result schema from return type
            var returnType = UnwrapTaskType(method.ReturnType);
            rpcMethod.Result = new OpenRpcResult
            {
                Name = $"{rpcName}Result",
                Schema = BuildSchema(returnType)
            };

            // Extract XML doc summary if available
            var xmlSummary = GetXmlDocSummary(method);
            if (xmlSummary != null)
                rpcMethod.Description = xmlSummary;

            doc.Methods.Add(rpcMethod);
        }

        // Add component schemas
        if (SchemaCache.Count > 0)
        {
            doc.Components = new OpenRpcComponents { Schemas = new(SchemaCache) };
        }

        return doc;
    }

    // ─── Param helpers ───────────────────────────────────────────────────────

    private static OpenRpcParam Param(string name, bool required, string type, string? desc = null, string? pattern = null) =>
        new()
        {
            Name = name,
            Required = required,
            Description = desc,
            Schema = new JsonSchemaObject { Type = type, Pattern = pattern }
        };

    private static OpenRpcParam ParamArray(string name, bool required, string itemType, string? desc = null) =>
        new()
        {
            Name = name,
            Required = required,
            Description = desc,
            Schema = new JsonSchemaObject
            {
                Type = "array",
                Items = new JsonSchemaObject { Type = itemType }
            }
        };

    private static OpenRpcParam ParamRef(string name, bool required, string schemaName, string? desc = null)
    {
        // Ensure the referenced schema exists in the cache
        EnsureSchemaByName(schemaName);
        return new OpenRpcParam
        {
            Name = name,
            Required = required,
            Description = desc,
            Schema = new JsonSchemaObject { Ref = $"#/components/schemas/{schemaName}" }
        };
    }

    // ─── Reflection-based param extraction ───────────────────────────────────

    private static List<OpenRpcParam> ReflectParams(MethodInfo method)
    {
        var result = new List<OpenRpcParam>();
        foreach (var p in method.GetParameters())
        {
            var paramType = p.ParameterType;
            var isNullable = Nullable.GetUnderlyingType(paramType) != null
                             || (paramType.IsClass && p.HasDefaultValue);
            var required = !p.HasDefaultValue && !isNullable;

            result.Add(new OpenRpcParam
            {
                Name = p.Name ?? "param",
                Required = required,
                Schema = BuildSchema(Nullable.GetUnderlyingType(paramType) ?? paramType),
                Description = IsAddressParam(p.Name) ? "Ethereum address (0x-prefixed, 40 hex chars)" : null
            });
        }
        return result;
    }

    private static bool IsAddressParam(string? name) =>
        name != null && (name.Contains("address", StringComparison.OrdinalIgnoreCase)
                      || name.Contains("avatar", StringComparison.OrdinalIgnoreCase));

    // ─── Schema builder ──────────────────────────────────────────────────────

    private static JsonSchemaObject BuildSchema(Type type)
    {
        // Unwrap nullable
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            var inner = BuildSchema(underlying);
            inner.Nullable = true;
            return inner;
        }

        // Primitives
        if (type == typeof(string)) return new JsonSchemaObject { Type = "string" };
        if (type == typeof(bool)) return new JsonSchemaObject { Type = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return new JsonSchemaObject { Type = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JsonSchemaObject { Type = "number" };

        // JsonElement — opaque object
        if (type == typeof(JsonElement))
            return new JsonSchemaObject { Type = "object", Description = "Arbitrary JSON value" };

        // Arrays / Lists
        if (type.IsArray)
        {
            var elemType = type.GetElementType()!;
            return new JsonSchemaObject { Type = "array", Items = BuildSchema(elemType) };
        }
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>)
                                || type.GetGenericTypeDefinition() == typeof(IList<>)
                                || type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            return new JsonSchemaObject { Type = "array", Items = BuildSchema(type.GetGenericArguments()[0]) };
        }

        // Dictionary<string, T>
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && type.GetGenericArguments()[0] == typeof(string))
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = BuildSchema(type.GetGenericArguments()[1])
            };
        }

        // Complex type → $ref
        var schemaName = GetSchemaName(type);
        if (!SchemaCache.ContainsKey(schemaName))
        {
            // Add placeholder to prevent infinite recursion
            SchemaCache[schemaName] = new JsonSchemaObject { Type = "object" };
            SchemaCache[schemaName] = BuildObjectSchema(type);
        }

        return new JsonSchemaObject { Ref = $"#/components/schemas/{schemaName}" };
    }

    private static JsonSchemaObject BuildObjectSchema(Type type)
    {
        var schema = new JsonSchemaObject
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaObject>(),
            Required = new List<string>()
        };

        // Handle records/classes with constructor parameters (positional records)
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var primaryCtor = constructors.MaxBy(c => c.GetParameters().Length);
        var ctorParams = primaryCtor?.GetParameters() ?? [];

        // Use public properties for schema generation
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead);

        foreach (var prop in props)
        {
            // Use JsonPropertyName attribute if present
            var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                        ?? CamelCase(prop.Name);

            // Skip JsonIgnore properties
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;

            var propSchema = BuildSchema(prop.PropertyType);
            schema.Properties[jsonName] = propSchema;

            // Check if required (non-nullable, no default in ctor)
            var ctorParam = ctorParams.FirstOrDefault(p =>
                string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

            var isNullable = Nullable.GetUnderlyingType(prop.PropertyType) != null
                          || (prop.PropertyType.IsClass && ctorParam?.HasDefaultValue == true);

            if (!isNullable && ctorParam is { HasDefaultValue: false })
            {
                schema.Required.Add(jsonName);
            }
        }

        if (schema.Required.Count == 0) schema.Required = null;
        if (schema.Properties.Count == 0) schema.Properties = null;

        return schema;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Type UnwrapTaskType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return type.GetGenericArguments()[0];
        return type;
    }

    private static string GetSchemaName(Type type)
    {
        if (type.IsGenericType)
        {
            var baseName = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join("", type.GetGenericArguments().Select(a => a.Name));
            return $"{baseName}_{args}";
        }
        return type.Name;
    }

    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void EnsureSchemaByName(string schemaName)
    {
        if (SchemaCache.ContainsKey(schemaName)) return;

        // Map known schema names to types
        var typeMap = new Dictionary<string, Type>
        {
            ["FlowRequest"] = typeof(Circles.Common.Dto.FlowRequest),
            ["SelectDto"] = typeof(SelectDto),
            ["FilterPredicateDto"] = typeof(IFilterPredicateDto),
        };

        if (typeMap.TryGetValue(schemaName, out var type))
        {
            SchemaCache[schemaName] = type.IsInterface
                ? new JsonSchemaObject { Type = "object", Description = $"See {type.Name} for structure" }
                : BuildObjectSchema(type);
        }
        else
        {
            SchemaCache[schemaName] = new JsonSchemaObject { Type = "object" };
        }
    }

    private static string? GetXmlDocSummary(MethodInfo method)
    {
        // XML doc extraction requires the XML file to be present at runtime.
        // For now, we rely on the Summary field from MethodMappings.
        // Can be enhanced with <GenerateDocumentationFile> + XML parsing.
        return null;
    }
}
