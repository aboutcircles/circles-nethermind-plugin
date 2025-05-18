using Circles.Index.Common;
using Circles.Index.Query.Dto;
using Circles.Pathfinder.DTOs;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Circles.Index.Rpc;

#region DTOs

public record CirclesTokenBalance(
    string TokenAddress,
    string TokenId,
    string TokenOwner,
    string TokenType,
    int Version,
    string AttoCircles,
    decimal Circles,
    string StaticAttoCircles,
    decimal StaticCircles,
    string AttoCrc,
    decimal Crc,
    bool IsErc20,
    bool IsErc1155,
    bool IsWrapped,
    bool IsInflationary,
    bool IsGroup);

public record CirclesTrustRelation(Address User, int limit);

public record CirclesTrustRelations(Address User, CirclesTrustRelation[] Trusts, CirclesTrustRelation[] TrustedBy);

public record CirclesEvent(string Event, IDictionary<string, object?> Values);

public record DatabaseColumn(string Column, string Type);

public class DatabaseTable(string table, string topic)
{
    public string Table { get; set; } = table;
    public string Topic { get; set; } = topic;
    public DatabaseColumn[] Columns { get; set; } = [];
}

public class DatabaseNamespace(string @namespace)
{
    public string Namespace { get; set; } = @namespace;
    public DatabaseTable[] Tables { get; set; } = [];
}

#endregion

[RpcModule("Circles")]
public interface ICirclesRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Gets the V1 Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circles_getTotalBalance(Address address, bool? asTimeCircles = true);

    [JsonRpcMethod(Description = "Gets the V2 Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circlesV2_getTotalBalance(Address address, bool? asTimeCircles = true);

    [JsonRpcMethod(Description = "This method allows you to query all (v1) trust relations of an address",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address);

    [JsonRpcMethod(Description = "Gets the balance of each V1 Circles token the specified address holds",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address);

    [JsonRpcMethod(
        Description =
            "Gets the common trust between two addresses. If version is specified, it will only return trusts with the specified version. If version is not specified, it will return all trusts.",
        IsImplemented = true)]
    Task<ResultWrapper<Address[]>> circles_getCommonTrust(Address address1, Address address2, int? version = null);

    [JsonRpcMethod(Description = "Queries the data of one Circles index table",
        IsImplemented = true)]
    ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query);

    [JsonRpcMethod(Description = "Queries the profile CID of a Circles avatar.",
        IsImplemented = true)]
    ResultWrapper<IEnumerable<CirclesTokenBalance>> circles_getBalanceBreakdown(Address address);

    [JsonRpcMethod(Description = "Queries the profile CID of a Circles avatar.",
        IsImplemented = true)]
    ResultWrapper<string> circles_getProfileCid(Address address);

    [JsonRpcMethod(Description = "Queries the profile CID of a Circles avatar.",
        IsImplemented = true)]
    ResultWrapper<List<string?>> circles_getProfileCidBatch(Address[] address);

    [JsonRpcMethod(Description = "Returns all events affecting the specified account since block N",
        IsImplemented = true)]
    ResultWrapper<CirclesEvent[]> circles_events(Address? address, long? fromBlock, long? toBlock = null,
        string[]? eventTypes = null, FilterPredicateDto[]? filters = null, bool? sortAscending = false);

    [JsonRpcMethod(
        Description = "Tries to find a transitive transfer path between two addresses in the Circles V2 graph",
        IsImplemented = true)]
    Task<ResultWrapper<MaxFlowResponse>> circlesV2_findPath(FlowRequest flowRequest);

    [JsonRpcMethod(
        Description = "Checks if the database is available and indexing progresses as expected",
        IsImplemented = true)]
    ResultWrapper<string> circles_health();

    [JsonRpcMethod(
        Description = "Returns all indexed tables and columns grouped by namespace",
        IsImplemented = true)]
    Task<ResultWrapper<IEnumerable<DatabaseNamespace>>> circles_tables();
}