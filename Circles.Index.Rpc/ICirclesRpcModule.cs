using Circles.Index.Common;
using Circles.Index.Query.Dto;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Circles.Index.Rpc;

#region DTOs

public record CirclesTokenBalance(Address Token, string Balance);

public record CirclesTrustRelation(Address User, int limit);

public record CirclesTrustRelations(Address User, CirclesTrustRelation[] Trusts, CirclesTrustRelation[] TrustedBy);

#endregion

[RpcModule("Circles")]
public interface ICirclesRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Gets the Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circles_getTotalBalance(Address address);

    [JsonRpcMethod(Description = "This method allows you to query all (v1) trust relations of an address",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address);

    [JsonRpcMethod(Description = "Gets the balance of each Circles token the specified address holds",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address);

    [JsonRpcMethod(Description = "Queries the data of one Circles index table",
        IsImplemented = true)]
    ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query);

    [JsonRpcMethod(Description = "Calculates a transitive transfer path along the trust relations of a user",
        IsImplemented = true)]
    ResultWrapper<string> circles_computeTransfer(string from, string to, string amount);
}