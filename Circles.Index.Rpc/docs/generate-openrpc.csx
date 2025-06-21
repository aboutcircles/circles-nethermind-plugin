#!/usr/bin/env dotnet-script
#r "nuget: Newtonsoft.Json, 13.0.3"
#r "nuget: Microsoft.CodeAnalysis.CSharp, 4.8.0"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;

Console.WriteLine("🚀 Starting OpenRPC Generation...");

// Find source files
var searchPaths = new[] {
    "../ICirclesRpcModule.cs",
    "../CirclesRpcModule.cs",
    "../../ICirclesRpcModule.cs", 
    "../../CirclesRpcModule.cs",
    "../../../ICirclesRpcModule.cs",
    "../../../CirclesRpcModule.cs",
    "ICirclesRpcModule.cs",
    "CirclesRpcModule.cs"
};

string interfacePath = null;
string implementationPath = null;

foreach (var path in searchPaths)
{
    if (path.Contains("ICirclesRpcModule") && File.Exists(path) && interfacePath == null)
    {
        interfacePath = path;
    }
    else if (path.Contains("CirclesRpcModule.cs") && !path.Contains("ICircles") && File.Exists(path) && implementationPath == null)
    {
        implementationPath = path;
    }
}

if (interfacePath == null || implementationPath == null)
{
    Console.WriteLine("❌ Could not find required source files");
    Console.WriteLine($"Interface found: {interfacePath != null}");
    Console.WriteLine($"Implementation found: {implementationPath != null}");
    return 1;
}

Console.WriteLine($"✓ Found interface at: {interfacePath}");
Console.WriteLine($"✓ Found implementation at: {implementationPath}");

// Parse the interface file to get method signatures and descriptions
var interfaceContent = File.ReadAllText(interfacePath);
var interfaceTree = CSharpSyntaxTree.ParseText(interfaceContent);
var interfaceRoot = interfaceTree.GetRoot();

// Extract RPC methods from interface
var methods = new List<dynamic>();
var methodDescriptions = new Dictionary<string, string>();
var methodImplementations = new Dictionary<string, bool>();

// First pass: extract all method declarations from interface
var interfaceMethods = interfaceRoot.DescendantNodes()
    .OfType<MethodDeclarationSyntax>()
    .Where(m => m.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString().Contains("JsonRpcMethod"))))
    .ToList();

Console.WriteLine($"📝 Found {interfaceMethods.Count} RPC methods in interface");

// Check implementation to see which methods are actually implemented
var implContent = File.ReadAllText(implementationPath);
var implTree = CSharpSyntaxTree.ParseText(implContent);
var implRoot = implTree.GetRoot();

var implementedMethods = implRoot.DescendantNodes()
    .OfType<MethodDeclarationSyntax>()
    .Select(m => m.Identifier.Text)
    .ToHashSet();

foreach (var method in interfaceMethods)
{
    var methodName = method.Identifier.Text;
    var isImplemented = implementedMethods.Contains(methodName);
    
    if (!isImplemented)
    {
        Console.WriteLine($"  ⚠️  {methodName} - Not implemented, skipping");
        continue;
    }
    
    Console.WriteLine($"  → Processing: {methodName}");
    
    // Extract description from JsonRpcMethod attribute
    var description = "";
    var isImplementedFlag = true;
    
    var jsonRpcAttr = method.AttributeLists
        .SelectMany(al => al.Attributes)
        .FirstOrDefault(a => a.Name.ToString().Contains("JsonRpcMethod"));
        
    if (jsonRpcAttr != null && jsonRpcAttr.ArgumentList != null)
    {
        foreach (var arg in jsonRpcAttr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.ToString() == "Description")
            {
                description = arg.Expression.ToString().Trim('"');
            }
            else if (arg.NameEquals?.Name.ToString() == "IsImplemented")
            {
                isImplementedFlag = arg.Expression.ToString().ToLower() == "true";
            }
        }
    }
    
    if (!isImplementedFlag)
    {
        Console.WriteLine($"    Skipping (marked as not implemented)");
        continue;
    }
    
    // Parse parameters
    var methodParams = new List<dynamic>();
    
    foreach (var param in method.ParameterList.Parameters)
    {
        var paramName = param.Identifier.Text;
        var paramType = param.Type.ToString();
        var hasDefault = param.Default != null;
        object defaultValue = null;
        
        if (hasDefault)
        {
            var defaultStr = param.Default.Value.ToString();
            // Parse default value properly
            if (defaultStr == "null") 
                defaultValue = null;
            else if (defaultStr == "true") 
                defaultValue = true;
            else if (defaultStr == "false") 
                defaultValue = false;
            else if (int.TryParse(defaultStr, out var intVal)) 
                defaultValue = intVal;
            else 
                defaultValue = defaultStr.Trim('"');
        }
        
        methodParams.Add(new {
            name = paramName,
            description = GetParamDescription(methodName, paramName),
            required = !hasDefault,
            schema = GetSchema(paramType),
            @default = defaultValue
        });
    }
    
    // Parse return type
    var returnType = "string";
    if (method.ReturnType is GenericNameSyntax genericReturn)
    {
        if (genericReturn.Identifier.Text == "Task" && genericReturn.TypeArgumentList.Arguments.Count > 0)
        {
            var innerType = genericReturn.TypeArgumentList.Arguments[0];
            if (innerType is GenericNameSyntax innerGeneric && innerGeneric.Identifier.Text == "ResultWrapper")
            {
                returnType = innerGeneric.TypeArgumentList.Arguments[0].ToString();
            }
        }
    }
    else if (method.ReturnType is IdentifierNameSyntax identifierReturn)
    {
        if (identifierReturn.Identifier.Text.StartsWith("ResultWrapper"))
        {
            // Extract from ResultWrapper<Type>
            var match = Regex.Match(method.ReturnType.ToString(), @"ResultWrapper<(.+)>");
            if (match.Success)
            {
                returnType = match.Groups[1].Value;
            }
        }
    }
    
    methods.Add(new {
        name = methodName,
        description = string.IsNullOrEmpty(description) ? GetMethodDescription(methodName) : description,
        summary = GetMethodSummary(methodName),
        tags = new[] { new { name = GetCategory(methodName) } },
        @params = methodParams,
        result = new {
            name = "result",
            description = "The result of the operation",
            schema = GetSchema(returnType)
        }
    });
}

Console.WriteLine($"✓ Processed {methods.Count} implemented RPC methods");

// Create OpenRPC document
var document = new {
    openrpc = "1.2.6",
    info = new {
        title = "Circles Protocol RPC API",
        description = "JSON-RPC API for interacting with the Circles protocol indexer and blockchain data",
        version = "1.0.0",
        contact = new {
            name = "Circles UBI",
            url = "https://aboutcircles.com"
        },
        license = new {
            name = "AGPL-3.0",
            url = "https://www.gnu.org/licenses/agpl-3.0.html"
        }
    },
    servers = new[] {
        new {
            name = "Cricles Gnosis Mainnet RPC",
            url = "https://rpc.aboutcircles.com",
            summary = "Production Circles RPC endpoint",
            description = "Main production endpoint for Circles"
        },
        new {
            name = "Circles Staging RPC",
            url = "https://rpc.circlesubi.network",
            summary = "Staging Circles RPC endpoint",
            description = "Use this for local development and testing"
        }
    },
    methods = methods.OrderBy(m => m.name).ToArray(),
    components = new {
        schemas = new Dictionary<string, object> {
            ["Address"] = new {
                type = "string",
                description = "Ethereum address (0x-prefixed, 40 hex characters)",
                pattern = "^0x[a-fA-F0-9]{40}$",
                example = "0xde374ece6fa50e781e81aac78e811b33d16912c7"
            },
            ["UInt256"] = new {
                type = "string",
                description = "256-bit unsigned integer as decimal string",
                example = "1000000000000000000"
            },
            ["CirclesEvent"] = new {
                type = "object",
                description = "A Circles protocol event",
                properties = new {
                    @event = new { 
                        type = "string",
                        description = "The event name (e.g., CrcV1_Transfer)"
                    },
                    values = new { 
                        type = "object",
                        description = "Event-specific data fields"
                    }
                },
                required = new[] { "event", "values" }
            },
            ["CirclesTokenBalance"] = new {
                type = "object",
                description = "Token balance information",
                properties = new {
                    tokenAddress = new { type = "string" },
                    tokenId = new { type = "string" },
                    tokenOwner = new { type = "string" },
                    tokenType = new { type = "string" },
                    version = new { type = "integer" },
                    attoCircles = new { type = "string" },
                    circles = new { type = "number" },
                    staticAttoCircles = new { type = "string" },
                    staticCircles = new { type = "number" },
                    attoCrc = new { type = "string" },
                    crc = new { type = "number" },
                    isErc20 = new { type = "boolean" },
                    isErc1155 = new { type = "boolean" },
                    isWrapped = new { type = "boolean" },
                    isInflationary = new { type = "boolean" },
                    isGroup = new { type = "boolean" }
                }
            },
            ["CirclesTrustRelations"] = new {
                type = "object",
                description = "Trust relationships for an address",
                properties = new {
                    user = new { @ref = "#/components/schemas/Address" },
                    trusts = new { 
                        type = "array",
                        items = new { @ref = "#/components/schemas/CirclesTrustRelation" }
                    },
                    trustedBy = new { 
                        type = "array",
                        items = new { @ref = "#/components/schemas/CirclesTrustRelation" }
                    }
                }
            },
            ["CirclesTrustRelation"] = new {
                type = "object",
                properties = new {
                    user = new { @ref = "#/components/schemas/Address" },
                    limit = new { type = "integer" }
                }
            },
            ["SelectDto"] = new {
                type = "object",
                description = "Database query parameters",
                properties = new {
                    @namespace = new { type = "string", description = "Table namespace (e.g., CrcV1, CrcV2)" },
                    table = new { type = "string", description = "Table name" },
                    columns = new { 
                        type = "array", 
                        items = new { type = "string" },
                        description = "Columns to select"
                    },
                    filter = new { type = "array", description = "Filter conditions" },
                    order = new { type = "array", description = "Order by conditions" },
                    limit = new { type = "integer", description = "Maximum results" }
                }
            },
            ["FilterPredicateDto"] = new {
                type = "object",
                description = "Filter predicate for queries",
                properties = new {
                    column = new { type = "string" },
                    filterType = new { 
                        type = "string", 
                        @enum = new[] { "Equals", "NotEquals", "GreaterThan", "GreaterThanOrEquals", "LessThan", "LessThanOrEquals", "Like", "NotLike", "In", "NotIn" } 
                    },
                    value = new { }
                }
            },
            ["DatabaseQueryResult"] = new {
                type = "object",
                properties = new {
                    columns = new { type = "array", items = new { type = "string" } },
                    rows = new { type = "array", items = new { type = "array" } }
                }
            },
            ["Profile"] = new {
                type = "object",
                description = "User profile information",
                properties = new {
                    address = new { type = "string" },
                    CID = new { type = "string", nullable = true },
                    lastUpdatedAt = new { type = "integer", nullable = true },
                    name = new { type = "string" },
                    description = new { type = "string", nullable = true },
                    registeredName = new { type = "string", nullable = true },
                    location = new { type = "string", nullable = true },
                    imageUrl = new { type = "string", nullable = true },
                    previewImageUrl = new { type = "string", nullable = true },
                    geoLocation = new { type = "array", items = new { type = "number" }, nullable = true },
                    longitude = new { type = "number", nullable = true },
                    latitude = new { type = "number", nullable = true },
                    shortName = new { type = "string", nullable = true }
                }
            },
            ["AvatarRow"] = new {
                type = "object",
                description = "Avatar information",
                properties = new {
                    version = new { type = "integer" },
                    type = new { type = "string" },
                    avatar = new { type = "string" },
                    tokenId = new { type = "string" },
                    hasV1 = new { type = "boolean" },
                    v1Token = new { type = "string", nullable = true },
                    cidV0Digest = new { type = "string" },
                    cidV0 = new { type = "string" },
                    isHuman = new { type = "boolean" },
                    name = new { type = "string", nullable = true },
                    symbol = new { type = "string", nullable = true }
                }
            },
            ["TokenInfo"] = new {
                type = "object",
                description = "Token metadata",
                properties = new {
                    tokenAddress = new { @ref = "#/components/schemas/Address" },
                    tokenOwner = new { @ref = "#/components/schemas/Address" },
                    tokenType = new { type = "string" },
                    version = new { type = "integer" },
                    isErc20 = new { type = "boolean" },
                    isErc1155 = new { type = "boolean" },
                    isWrapped = new { type = "boolean" },
                    isInflationary = new { type = "boolean" },
                    isGroup = new { type = "boolean" }
                }
            },
            ["FlowRequest"] = new {
                type = "object",
                description = "Path finding request",
                properties = new {
                    from = new { @ref = "#/components/schemas/Address" },
                    to = new { @ref = "#/components/schemas/Address" },
                    amount = new { type = "string" }
                }
            },
            ["MaxFlowResponse"] = new {
                type = "object",
                description = "Path finding response",
                properties = new {
                    maxFlow = new { type = "string" },
                    paths = new { type = "array", items = new { type = "object" } }
                }
            },
            ["DatabaseNamespace"] = new {
                type = "object",
                properties = new {
                    @namespace = new { type = "string" },
                    tables = new { type = "array", items = new { @ref = "#/components/schemas/DatabaseTable" } }
                }
            },
            ["DatabaseTable"] = new {
                type = "object",
                properties = new {
                    table = new { type = "string" },
                    topic = new { type = "string" },
                    columns = new { type = "array", items = new { @ref = "#/components/schemas/DatabaseColumn" } }
                }
            },
            ["DatabaseColumn"] = new {
                type = "object",
                properties = new {
                    column = new { type = "string" },
                    type = new { type = "string" }
                }
            }
        },
        errors = new Dictionary<string, object> {
            ["ParseError"] = new {
                code = -32700,
                message = "Parse error"
            },
            ["InvalidRequest"] = new {
                code = -32600,
                message = "Invalid Request"
            },
            ["MethodNotFound"] = new {
                code = -32601,
                message = "Method not found"
            },
            ["InvalidParams"] = new {
                code = -32602,
                message = "Invalid params"
            },
            ["InternalError"] = new {
                code = -32603,
                message = "Internal error"
            }
        }
    }
};

// Save the document
var json = JsonConvert.SerializeObject(document, Formatting.Indented);
File.WriteAllText("circles-rpc.json", json);

Console.WriteLine($"✅ Generated OpenRPC document with {methods.Count} methods");
Console.WriteLine($"📄 Output: circles-rpc.json");

return 0;

// Helper functions
string GetCategory(string methodName)
{
    if (methodName.StartsWith("circles_")) return "Circles v1";
    if (methodName.StartsWith("circlesV2_")) return "Circles v2";
    if (methodName.StartsWith("eth_")) return "Ethereum";
    return "Other";
}

string GetMethodDescription(string methodName)
{
    var descriptions = new Dictionary<string, string> {
        ["circles_getTotalBalance"] = "Gets the V1 Circles balance of the specified address",
        ["circlesV2_getTotalBalance"] = "Gets the V2 Circles balance of the specified address",
        ["circles_getTokenBalances"] = "Gets the balance of each V1 Circles token the specified address holds",
        ["circles_query"] = "Queries the data of one Circles index table",
        ["circles_events"] = "Returns all events affecting the specified account since block N",
        ["circles_getTrustRelations"] = "This method allows you to query all (v1) trust relations of an address",
        ["circles_getCommonTrust"] = "Gets the common trust between two addresses. If version is specified, it will only return trusts with the specified version",
        ["circles_health"] = "Checks if the database is available and indexing progresses as expected",
        ["circles_tables"] = "Returns all indexed tables and columns grouped by namespace",
        ["circles_getAvatarInfo"] = "Queries essential information about an avatar",
        ["circles_getAvatarInfoBatch"] = "Queries essential information about an avatar in batch",
        ["circles_getProfileCid"] = "Queries the profile CID of a Circles avatar. Returns an error if the avatar is not found",
        ["circles_getProfileCidBatch"] = "Queries the profile CID of many Circles avatars. Returns 'null' for avatars that are not found",
        ["circles_getProfileByAddress"] = "Gets complete profile information by address",
        ["circles_getProfileByAddressBatch"] = "Gets profiles for multiple addresses",
        ["circles_getProfileByCid"] = "Gets profile data from IPFS by CID",
        ["circles_getProfileByCidBatch"] = "Gets multiple profiles from IPFS by CIDs",
        ["circles_getTokenInfo"] = "Gets detailed information about a specific token",
        ["circles_getTokenInfoBatch"] = "Gets information about multiple tokens",
        ["circles_searchProfiles"] = "Full-text search over avatar profiles (name & description)",
        ["circlesV2_findPath"] = "Tries to find a transitive transfer path between two addresses in the Circles V2 graph",
        ["circles_getNetworkSnapshot"] = "Gets a complete snapshot of the trust network",
        ["circles_getBalanceBreakdown"] = "Queries the balances of all Circles tokens an avatar has"
    };

    return descriptions.ContainsKey(methodName) 
        ? descriptions[methodName] 
        : $"Executes the {methodName} operation";
}

string GetMethodSummary(string methodName)
{
    var summaries = new Dictionary<string, string> {
        ["circles_getTotalBalance"] = "getTotalBalance",
        ["circlesV2_getTotalBalance"] = "getTotalBalance",
        ["circles_getTokenBalances"] = "getTokenBalances",
        ["circles_query"] = "Query database",
        ["circles_events"] = "events",
        ["circles_getTrustRelations"] = "getTrustRelations",
        ["circles_getCommonTrust"] = "getCommonTrust",
        ["circles_health"] = "health",
        ["circles_tables"] = "tables",
        ["circles_getAvatarInfo"] = "getAvatarInfo",
        ["circles_getAvatarInfoBatch"] = "getAvatarInfoBatch",
        ["circles_getProfileCid"] = "getProfileCid",
        ["circles_getProfileCidBatch"] = "getProfileCidBatch",
        ["circles_getProfileByAddress"] = "getProfileByAddress",
        ["circles_getProfileByAddressBatch"] = "getProfileByAddressBatch",
        ["circles_getProfileByCid"] = "getProfileByCid",
        ["circles_getProfileByCidBatch"] = "getProfileByCidBatch",
        ["circles_getTokenInfo"] = "getTokenInfo",
        ["circles_getTokenInfoBatch"] = "getTokenInfoBatch",
        ["circles_searchProfiles"] = "searchProfiles",
        ["circlesV2_findPath"] = "findPath",
        ["circles_getNetworkSnapshot"] = "getNetworkSnapshot",
        ["circles_getBalanceBreakdown"] = "getBalanceBreakdown"
    };
    
    return summaries.ContainsKey(methodName) ? summaries[methodName] : methodName;
}

string GetParamDescription(string methodName, string paramName)
{
    var descriptions = new Dictionary<string, string> {
        ["address"] = "The Ethereum address to query",
        ["avatar"] = "The avatar address",
        ["address1"] = "The first address to compare",
        ["address2"] = "The second address to compare",
        ["addresses"] = "Array of Ethereum addresses",
        ["avatars"] = "Array of avatar addresses",
        ["fromBlock"] = "Starting block number (inclusive)",
        ["toBlock"] = "Ending block number (inclusive)",
        ["query"] = "Database query parameters",
        ["eventTypes"] = "Array of event types to filter",
        ["filterPredicates"] = "Additional filter conditions",
        ["filters"] = "Additional filter conditions",
        ["sortAscending"] = "Sort results in ascending order",
        ["asTimeCircles"] = "Return balance in time circles (true) or static circles (false)",
        ["version"] = "Circles protocol version (1 or 2)",
        ["cid"] = "IPFS Content Identifier",
        ["cids"] = "Array of IPFS Content Identifiers",
        ["tokenAddress"] = "The token contract address",
        ["tokenAddresses"] = "Array of token contract addresses",
        ["text"] = "Search query text",
        ["limit"] = "Maximum number of results",
        ["offset"] = "Number of results to skip",
        ["flowRequest"] = "Path finding request parameters"
    };

    return descriptions.ContainsKey(paramName) 
        ? descriptions[paramName] 
        : $"The {paramName} parameter";
}

object GetSchema(string type)
{
    type = type.Replace("?", "").Trim();
    
    var schemas = new Dictionary<string, object> {
        ["string"] = new { type = "string" },
        ["bool"] = new { type = "boolean" },
        ["boolean"] = new { type = "boolean" },
        ["int"] = new { type = "integer" },
        ["int?"] = new { type = "integer", nullable = true },
        ["long"] = new { type = "integer", format = "int64" },
        ["long?"] = new { type = "integer", format = "int64", nullable = true },
        ["Address"] = new { @ref = "#/components/schemas/Address" },
        ["Address?"] = new { @ref = "#/components/schemas/Address", nullable = true },
        ["Address[]"] = new { type = "array", items = new { @ref = "#/components/schemas/Address" } },
        ["Address?[]"] = new { type = "array", items = new { @ref = "#/components/schemas/Address", nullable = true } },
        ["string[]"] = new { type = "array", items = new { type = "string" } },
        ["CirclesEvent[]"] = new { type = "array", items = new { @ref = "#/components/schemas/CirclesEvent" } },
        ["CirclesTokenBalance[]"] = new { type = "array", items = new { @ref = "#/components/schemas/CirclesTokenBalance" } },
        ["SelectDto"] = new { @ref = "#/components/schemas/SelectDto" },
        ["DatabaseQueryResult"] = new { @ref = "#/components/schemas/DatabaseQueryResult" },
        ["FlowRequest"] = new { @ref = "#/components/schemas/FlowRequest" },
        ["JsonElement"] = new { type = "object" },
        ["Profile"] = new { @ref = "#/components/schemas/Profile" },
        ["Profile[]"] = new { type = "array", items = new { @ref = "#/components/schemas/Profile" } },
        ["Profile?[]"] = new { type = "array", items = new { @ref = "#/components/schemas/Profile", nullable = true } },
        ["TokenInfo"] = new { @ref = "#/components/schemas/TokenInfo" },
        ["TokenInfo[]"] = new { type = "array", items = new { @ref = "#/components/schemas/TokenInfo" } },
        ["TokenInfo?[]"] = new { type = "array", items = new { @ref = "#/components/schemas/TokenInfo", nullable = true } },
        ["CirclesTrustRelations"] = new { @ref = "#/components/schemas/CirclesTrustRelations" },
        ["IEnumerable<DatabaseNamespace>"] = new { type = "array", items = new { @ref = "#/components/schemas/DatabaseNamespace" } },
        ["IEnumerable<CirclesTokenBalance>"] = new { type = "array", items = new { @ref = "#/components/schemas/CirclesTokenBalance" } },
        ["List<string?>"] = new { type = "array", items = new { type = "string", nullable = true } },
        ["List<string>"] = new { type = "array", items = new { type = "string" } },
        ["AvatarRow"] = new { @ref = "#/components/schemas/AvatarRow" },
        ["AvatarRow[]"] = new { type = "array", items = new { @ref = "#/components/schemas/AvatarRow" } },
        ["AvatarRow?[]"] = new { type = "array", items = new { @ref = "#/components/schemas/AvatarRow", nullable = true } },
        ["MaxFlowResponse"] = new { @ref = "#/components/schemas/MaxFlowResponse" },
        ["FilterPredicateDto[]"] = new { type = "array", items = new { @ref = "#/components/schemas/FilterPredicateDto" } }
    };
    
    return schemas.ContainsKey(type) ? schemas[type] : new { type = "object", description = type };
}