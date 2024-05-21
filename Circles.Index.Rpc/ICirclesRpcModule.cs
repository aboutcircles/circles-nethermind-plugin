using Circles.Index.Common;
using Circles.Index.Query.Dto;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Circles.Index.Rpc;

#region DTOs

public record CirclesTokenBalance(Address Token, string Balance, string TokenOwner);
public record CirclesTokenBalanceV2(UInt256 TokenId, string Balance, string TokenOwner);

public record CirclesTrustRelation(Address User, int limit);

public record CirclesTrustRelations(Address User, CirclesTrustRelation[] Trusts, CirclesTrustRelation[] TrustedBy);

#endregion

[RpcModule("Circles")]
public interface ICirclesRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Gets the V1 Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circles_getTotalBalance(Address address, bool asTimeCircles = false);

    [JsonRpcMethod(Description = "This method allows you to query all (v1) trust relations of an address",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address);

    [JsonRpcMethod(Description = "Gets the balance of each V1 Circles token the specified address holds",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address, bool asTimeCircles = false);
    
    [JsonRpcMethod(Description = "Gets the V2 Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circlesV2_getTotalBalance(Address address, bool asTimeCircles = false);
    
    [JsonRpcMethod(Description = "Gets the balance of each V2 Circles token the specified address holds",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTokenBalanceV2[]>> circlesV2_getTokenBalances(Address address, bool asTimeCircles = false);

    [JsonRpcMethod(Description = "Queries the data of one Circles index table",
        IsImplemented = true)]
    ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query);
}