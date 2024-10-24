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

    [JsonRpcMethod(Description = "Queries the data of one Circles index table",
        IsImplemented = true)]
    ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query);

    [JsonRpcMethod(Description = "Returns all events affecting the specified account since block N",
        IsImplemented = true)]
    ResultWrapper<CirclesEvent[]> circles_events(Address? address, long? fromBlock, long? toBlock = null,
        string[]? eventTypes = null, FilterPredicateDto[]? filters = null, bool? sortAscending = false);

    [JsonRpcMethod(
        Description = "Tries to find a transitive transfer path between two addresses in the Circles V2 graph",
        IsImplemented = true)]
    Task<ResultWrapper<MaxFlowResponse>> circlesV2_findPath(FlowRequest flowRequest);
}